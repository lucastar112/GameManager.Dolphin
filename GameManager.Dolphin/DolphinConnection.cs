using Dolphin.Memory.Access;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DolphinMemory = Dolphin.Memory.Access;

namespace GameManager.Dolphin;

/// <summary>
/// Provides access to Dolphin's emulated memory. Emulated addresses are 32-bit
/// big-endian PowerPC addresses; host addresses are native process addresses.
/// </summary>
public sealed class DolphinConnection : IDisposable
{
    private const int ProcessVmRead = 0x0010;
    private const int ProcessVmWrite = 0x0020;
    private const int ProcessVmOperation = 0x0008;
    private const int ProcessQueryInformation = 0x0400;

    private const uint Mem1Base = 0x80000000;
    private const uint Mem2Base = 0x90000000;
    private const uint AramBase = 0x7E000000;

    private Process? _process;
    private DolphinMemory.Dolphin? _dolphin;
    private IntPtr _readHandle;
    private IntPtr _writeHandle;
    private MEM2Extension? _mem2Extension;
    private bool _disposed;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        int desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    // SIZE_T is pointer-sized on Windows. Keep the native declarations correct
    // on x64 rather than passing an int* to APIs that write SIZE_T.
    [DllImport("kernel32.dll", EntryPoint = "ReadProcessMemory", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemoryNative(
        IntPtr hProcess,
        IntPtr lpBaseAddress,
        [Out] byte[] lpBuffer,
        nuint nSize,
        out nuint lpNumberOfBytesRead);

    [DllImport("kernel32.dll", EntryPoint = "WriteProcessMemory", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemoryNative(
        IntPtr hProcess,
        IntPtr lpBaseAddress,
        byte[] lpBuffer,
        nuint nSize,
        out nuint lpNumberOfBytesWritten);

    public DolphinConnection(Process process)
    {
        Connect(process);
    }

    private void Connect(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Dolphin process memory access is supported only on Windows.");

        _process = process;

        const int maxAttempts = 20;
        const int retryDelayMs = 250;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (process.HasExited)
                    throw new InvalidOperationException("The Dolphin process has already exited.");

                _dolphin = new DolphinMemory.Dolphin(process);

                // Query information is required by VirtualQueryEx and QueryWorkingSetEx.
                _readHandle = OpenProcess(
                    ProcessQueryInformation | ProcessVmRead | ProcessVmOperation,
                    false,
                    process.Id);

                _writeHandle = OpenProcess(
                    ProcessVmWrite | ProcessVmOperation,
                    false,
                    process.Id);

                if (_readHandle == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to open Dolphin for reading.");

                if (_writeHandle == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to open Dolphin for writing.");

                _mem2Extension = new MEM2Extension(process, _readHandle);

                Console.WriteLine($"Connected to Dolphin (PID {process.Id}).");
                return;
            }
            catch (Win32Exception exception) when (
                exception.NativeErrorCode == 299 && attempt < maxAttempts)
            {
                CloseHandles();
                Console.WriteLine(
                    $"Dolphin process is still initializing (attempt {attempt}/{maxAttempts}).");
                Thread.Sleep(retryDelayMs);
            }
            catch
            {
                CloseHandles();
                throw;
            }
        }

        throw new InvalidOperationException(
            $"Could not connect to Dolphin after {maxAttempts} attempts.");
    }

    /// <summary>
    /// Discover the emulated RAM mappings. Call after the game has initialized.
    /// MEM1 can still use the package's existing translator if discovery fails;
    /// MEM2 requires a successfully discovered mapping.
    /// </summary>
    public bool InitializeMemoryMap()
    {
        ThrowIfDisposed();

        if (_process is null || _process.HasExited || _mem2Extension is null)
            return false;

        bool success = _mem2Extension.TryInitialize(out string diagnostic);
        Console.WriteLine($"[DolphinMemory] {diagnostic}");
        return success;
    }

    public bool IsGameRunning()
    {
        if (_process is null || _dolphin is null || _process.HasExited)
            return false;

        // This is only a readiness check; actual reads and writes use the
        // region-aware translator below.
        return _dolphin.TryGetAddress(new IntPtr(Mem1Base), out _);
    }

    public bool TryRead(IntPtr address, byte[] buffer, out int bytesRead)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        bytesRead = 0;

        if (!CanAccessProcess() || buffer.Length == 0)
            return false;

        if (!TryTranslateAddress(address, buffer.Length, out IntPtr hostAddress))
            return false;

        bool success = ReadProcessMemory(
            _readHandle, hostAddress, buffer, buffer.Length, ref bytesRead);

        return success && bytesRead == buffer.Length;
    }

    public bool TryWrite(IntPtr address, byte[] data, out int bytesWritten)
    {
        ArgumentNullException.ThrowIfNull(data);
        bytesWritten = 0;

        if (!CanAccessProcess() || data.Length == 0)
            return false;

        if (!TryTranslateAddress(address, data.Length, out IntPtr hostAddress))
            return false;

        bool success = WriteProcessMemory(
            _writeHandle, hostAddress, data, data.Length, ref bytesWritten);

        return success && bytesWritten == data.Length;
    }

    public bool TryReadPointer(IntPtr address, int offset, out IntPtr pointer)
    {
        pointer = IntPtr.Zero;

        if (!TryAddAddress(address, offset, out IntPtr targetAddress))
            return false;

        byte[] buffer = new byte[sizeof(uint)];
        if (!TryRead(targetAddress, buffer, out int bytesRead) || bytesRead != buffer.Length)
        {
            Console.WriteLine(
                $"Failed to read pointer at 0x{targetAddress.ToInt64():X8}.");
            return false;
        }

        uint value = BinaryPrimitives.ReadUInt32BigEndian(buffer);
        pointer = new IntPtr(value);

        Console.WriteLine(
            $"Read pointer 0x{targetAddress.ToInt64():X8} -> 0x{pointer.ToInt64():X8}");

        return value != 0;
    }

    /// <summary>
    /// Resolve a standard pointer chain. Each offset is added to the current
    /// emulated address; every offset except the final one is dereferenced.
    /// </summary>
    public bool TryReadPointerChain(
        IntPtr address,
        byte[] buffer,
        IReadOnlyList<nint> offsets,
        out int bytesRead)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(offsets);
        bytesRead = 0;

        if (!CanAccessProcess() || buffer.Length == 0)
            return false;

        // Preserve this method's original convention: dereference the supplied
        // absolute address first, then add each offset to the pointer obtained.
        IntPtr currentAddress = address;

        foreach (nint offset in offsets)
        {
            if (!TryReadPointerAtAddress(currentAddress, out IntPtr pointer))
            {
                Console.WriteLine(
                    $"[PointerChain] Failed to dereference 0x{currentAddress.ToInt64():X8}.");
                return false;
            }

            if (!TryAddAddress(pointer, offset.ToInt64(), out currentAddress))
            {
                Console.WriteLine("[PointerChain] Pointer plus offset overflowed the 32-bit address space.");
                return false;
            }
        }

        if (!TryRead(currentAddress, buffer, out bytesRead))
            return false;

        return bytesRead == buffer.Length;
    }

    public bool TryReadPointerChainFromBase(
        IntPtr baseAddress,
        byte[] buffer,
        IReadOnlyList<nint> offsets,
        out int bytesRead)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(offsets);
        bytesRead = 0;

        if (!CanAccessProcess() || buffer.Length == 0)
            return false;

        IntPtr currentAddress = baseAddress;

        for (int i = 0; i < offsets.Count; i++)
        {
            if (!TryAddAddress(currentAddress, offsets[i].ToInt64(), out currentAddress))
            {
                Console.WriteLine("Pointer chain address overflowed the 32-bit emulated address space.");
                return false;
            }

            Console.WriteLine(
                $"[PointerChain] Offset {offsets[i]} -> 0x{currentAddress.ToInt64():X8}");

            if (i == offsets.Count - 1)
                break;

            if (!TryReadPointerAtAddress(currentAddress, out IntPtr nextAddress))
            {
                Console.WriteLine(
                    $"[PointerChain] Failed to read pointer at 0x{currentAddress.ToInt64():X8}.");
                return false;
            }

            currentAddress = nextAddress;
            Console.WriteLine(
                $"[PointerChain] Dereferenced -> 0x{currentAddress.ToInt64():X8}");
        }

        if (!TryRead(currentAddress, buffer, out bytesRead))
        {
            Console.WriteLine(
                $"[PointerChain] Final read failed at 0x{currentAddress.ToInt64():X8}.");
            return false;
        }

        return bytesRead == buffer.Length;
    }

    public bool TryReadByte(IntPtr address, out byte value)
    {
        value = 0;
        byte[] buffer = new byte[1];

        if (!TryRead(address, buffer, out int bytesRead) || bytesRead != 1)
            return false;

        value = buffer[0];
        return true;
    }

    public bool TryWriteByte(IntPtr address, byte value)
    {
        return TryWrite(address, new[] { value }, out int bytesWritten) &&
               bytesWritten == 1;
    }

    public bool TryReadDebug(IntPtr address, int length, out byte[] data)
    {
        data = Array.Empty<byte>();

        if (length <= 0 || !CanAccessProcess())
            return false;

        data = new byte[length];

        if (!TryRead(address, data, out int bytesRead))
        {
            Console.WriteLine(
                $"[MemoryDebug] Read failed. Address=0x{address.ToInt64():X8}, " +
                $"BytesRead={bytesRead}, Win32Error={Marshal.GetLastWin32Error()}");
            return false;
        }

        Console.WriteLine(
            $"[MemoryDebug] Address=0x{address.ToInt64():X8}, " +
            $"BytesRead={bytesRead}, Bytes={Convert.ToHexString(data)}");

        return bytesRead == length;
    }

    public void DebugReadAddress(IntPtr address)
    {
        if (!CanAccessProcess())
            return;

        Console.WriteLine($"[MemoryCheck] Dolphin PID: {_process!.Id}");
        Console.WriteLine($"[MemoryCheck] Emulated address: 0x{address.ToInt64():X8}");

        if (!TryTranslateAddress(address, sizeof(uint), out IntPtr hostAddress))
        {
            Console.WriteLine("[MemoryCheck] Address translation failed.");
            return;
        }

        byte[] buffer = new byte[sizeof(uint)];
        int bytesRead = 0;
        bool success = ReadProcessMemory(
            _readHandle, hostAddress, buffer, buffer.Length, ref bytesRead);

        Console.WriteLine($"[MemoryCheck] Translated host address: 0x{hostAddress.ToInt64():X}");
        Console.WriteLine($"[MemoryCheck] Read succeeded: {success}");
        Console.WriteLine($"[MemoryCheck] Bytes read: {bytesRead}");
        Console.WriteLine($"[MemoryCheck] Raw bytes: {Convert.ToHexString(buffer)}");

        if (!success)
            Console.WriteLine($"[MemoryCheck] Win32 error: {Marshal.GetLastWin32Error()}");
    }

    private bool TryReadPointerAtAddress(IntPtr address, out IntPtr pointer)
    {
        pointer = IntPtr.Zero;
        byte[] buffer = new byte[sizeof(uint)];

        if (!TryRead(address, buffer, out int bytesRead) || bytesRead != buffer.Length)
            return false;

        uint value = BinaryPrimitives.ReadUInt32BigEndian(buffer);
        if (value == 0)
            return false;

        pointer = new IntPtr(value);
        return true;
    }

    private bool TryTranslateAddress(
        IntPtr emulatedAddress,
        int length,
        out IntPtr hostAddress)
    {
        hostAddress = IntPtr.Zero;

        if (length <= 0 || _dolphin is null)
            return false;

        uint address = unchecked((uint)emulatedAddress.ToInt64());

        // MEM2 is never sent through the third-party translator: it uses a
        // fixed MEM1-style mapping and cannot discover DME's separate MEM2 map.
        if (address >= Mem2Base)
        {
            return _mem2Extension is not null &&
                   _mem2Extension.TryTranslateAddress(emulatedAddress, length, out hostAddress);
        }

        // Prefer DME-compatible discovered mappings for MEM1.
        if (address >= Mem1Base && address < Mem2Base)
        {
            if (_mem2Extension?.IsInitialized == true)
            {
                // Once the actual MEM1 size is known, do not fall back for an
                // address outside that range.
                return _mem2Extension.TryTranslateAddress(
                    emulatedAddress, length, out hostAddress);
            }

            // Retain the package as a MEM1 fallback before discovery succeeds.
            return _dolphin.TryGetAddress(emulatedAddress, out hostAddress);
        }

        // DME maps ARAM separately for supported GameCube/Triforce sessions.
        if (address >= AramBase && address < Mem1Base)
        {
            if (_mem2Extension?.IsInitialized == true)
                return _mem2Extension.TryTranslateAddress(
                    emulatedAddress, length, out hostAddress);

            return _dolphin.TryGetAddress(emulatedAddress, out hostAddress);
        }

        return false;
    }

    private bool CanAccessProcess() =>
        !_disposed &&
        _process is not null &&
        !_process.HasExited &&
        _readHandle != IntPtr.Zero &&
        _writeHandle != IntPtr.Zero;

    private static bool TryAddAddress(IntPtr address, long offset, out IntPtr result)
    {
        result = IntPtr.Zero;
        long current = unchecked((uint)address.ToInt64());

        long sum;
        try
        {
            sum = checked(current + offset);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (sum < 0 || sum > uint.MaxValue)
            return false;

        result = new IntPtr(sum);
        return true;
    }

    // Safe wrappers keep the public API's int byte counts while using the
    // correct pointer-sized native SIZE_T declarations.
    private static bool ReadProcessMemory(
        IntPtr process,
        IntPtr address,
        byte[] buffer,
        int size,
        ref int bytesRead)
    {
        bytesRead = 0;
        bool success = ReadProcessMemoryNative(
            process, address, buffer, (nuint)size, out nuint nativeBytesRead);

        bytesRead = nativeBytesRead.ToUInt64() > int.MaxValue
            ? int.MaxValue
            : (int)nativeBytesRead.ToUInt64();

        return success;
    }

    private static bool WriteProcessMemory(
        IntPtr process,
        IntPtr address,
        byte[] buffer,
        int size,
        ref int bytesWritten)
    {
        bytesWritten = 0;
        bool success = WriteProcessMemoryNative(
            process, address, buffer, (nuint)size, out nuint nativeBytesWritten);

        bytesWritten = nativeBytesWritten.ToUInt64() > int.MaxValue
            ? int.MaxValue
            : (int)nativeBytesWritten.ToUInt64();

        return success;
    }

    private void CloseHandles()
    {
        if (_readHandle != IntPtr.Zero)
        {
            CloseHandle(_readHandle);
            _readHandle = IntPtr.Zero;
        }

        if (_writeHandle != IntPtr.Zero)
        {
            CloseHandle(_writeHandle);
            _writeHandle = IntPtr.Zero;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CloseHandles();
        GC.SuppressFinalize(this);
    }
}
