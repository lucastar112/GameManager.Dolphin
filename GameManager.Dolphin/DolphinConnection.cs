using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DolphinMemory = Dolphin.Memory.Access;
using Dolphin.Memory.Access;

namespace GameManager.Dolphin
{
    public sealed class DolphinConnection
    {
        private Process? _process;
        private DolphinMemory.Dolphin? _dolphin;
        private IntPtr _readHandle;
        private IntPtr _writeHandle;
        private const int ProcessVmRead = 0x0010;
        private const int ProcessVmWrite = 0x0020;
        private const int ProcessVmOperation = 0x0008;

        // Windows process memory APIs
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr process, IntPtr baseAddress, byte[] buffer, int size, ref int bytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr process, IntPtr baseAddress, byte[] buffer, int size, ref int bytesWritten);

        /// <summary>
        /// Connects to Dolphin's process
        /// </summary>
        public DolphinConnection(Process process)
        {
            Connect(process);
        }

        /// <summary>
        /// Connects to Dolphin's process
        /// </summary>
        private void Connect(Process process)
        {
            const int maxAttempts = 20;
            const int retryDelayMs = 250;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    _process = process;
                    _dolphin = new DolphinMemory.Dolphin(process);
                    _readHandle = OpenProcess(ProcessVmRead | ProcessVmOperation, false, process.Id);
                    _writeHandle = OpenProcess(ProcessVmWrite | ProcessVmOperation, false, process.Id);

                    if (_readHandle == IntPtr.Zero)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to open Dolphin for reading.");

                    if (_writeHandle == IntPtr.Zero)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to open Dolphin for writing.");

                    Console.WriteLine($"Connected to Dolphin (PID {process.Id}).");
                    return;
                }
                catch (Win32Exception exception) when (exception.NativeErrorCode == 299)
                {
                    Console.WriteLine($"Dolphin process is still initializing " + $"(attempt {attempt}/{maxAttempts}).");
                    Thread.Sleep(retryDelayMs);
                }
            }
            throw new InvalidOperationException($"Could not connect to Dolphin after {maxAttempts} attempts.");
        }

        /// <summary>
        /// Check's if the game is running in Dolphin
        /// </summary>
        public bool IsGameRunning()
        {
            if (_process is null || _dolphin is null) 
                return false;

            if (_process.HasExited)
                return false;

            return _dolphin.TryGetAddress(0x80000000, out _);
        }

        /// <summary>
        /// Try to read the game's memory
        /// </summary>
        public bool TryRead(IntPtr address, byte[] buffer, out int bytesRead)
        {
            bytesRead = 0;

            if (_process is null)
            {
                Console.WriteLine("TryRead: process is null.");
                return false;
            }

            if (_process.HasExited)
            {
                Console.WriteLine("TryRead: process has exited.");
                return false;
            }

            if (_dolphin is null)
            {
                Console.WriteLine("TryRead: Dolphin object is null.");
                return false;
            }

            if (!_dolphin.TryGetAddress(address, out IntPtr hostAddress))
            {
                Console.WriteLine($"TryRead: failed to translate 0x{address.ToInt64():X}.");
                return false;
            }

            bool result = ReadProcessMemory(_readHandle, hostAddress, buffer, buffer.Length, ref bytesRead);

            if (!result)
            {
                Console.WriteLine($"TryRead: ReadProcessMemory failed. Host address = 0x{hostAddress.ToInt64():X}, " +
                    $"size = {buffer.Length}, Win32 error = {Marshal.GetLastWin32Error()}");
                return false;
            }

            Console.WriteLine($"TryRead: successfully read {bytesRead} bytes from 0x{address.ToInt64():X}.");
            return true;
        }

        /// <summary>
        /// Try to read the game's memory
        /// </summary>
        public bool TryReadPointerChain(IntPtr address, byte[] buffer, IReadOnlyList<nint> offsets, out int bytesRead)
        {
            bytesRead = 0;

            if (_process is null)
            {
                Console.WriteLine("TryReadPointerChain: process is null.");
                return false;
            }

            if (_process.HasExited)
            {
                Console.WriteLine("TryReadPointerChain: process has exited.");
                return false;
            }

            if (_dolphin is null)
            {
                Console.WriteLine("TryReadPointerChain: Dolphin object is null.");
                return false;
            }

            // Translate the initial Dolphin address
            // into a host process address.
            if (!_dolphin.TryGetAddress(address, out IntPtr currentHost))
            {
                Console.WriteLine("TryReadPointerChain: failed to translate initial address.");
                return false;
            }

            int pointerSize = IntPtr.Size;
            byte[] pointerBuffer = new byte[pointerSize];

            foreach (nint offset in offsets)
            {
                int pointerBytesRead = 0;

                if (!ReadProcessMemory(_readHandle, currentHost, pointerBuffer, pointerBuffer.Length, ref pointerBytesRead) || pointerBytesRead != pointerBuffer.Length)
                {
                    Console.WriteLine($"TryReadPointerChain: failed to read pointer at 0x{currentHost.ToInt64():X}. Win32 error = {Marshal.GetLastWin32Error()}");
                    return false;
                }

                long pointerValue = pointerSize == 8
                        ? BitConverter.ToInt64(pointerBuffer, 0)
                        : BitConverter.ToInt32(pointerBuffer, 0);

                IntPtr nextDolphinAddress = new IntPtr(pointerValue + (long)offset);

                // The pointer we just read is an address in
                // Dolphin's address space, so translate it again.
                if (!_dolphin.TryGetAddress(nextDolphinAddress, out currentHost))
                {
                    Console.WriteLine($"TryReadPointerChain: failed to translate pointer 0x{nextDolphinAddress.ToInt64():X}.");
                    return false;
                }
            }

            if (!ReadProcessMemory(_readHandle, currentHost, buffer, buffer.Length, ref bytesRead))
            {
                Console.WriteLine($"TryReadPointerChain: ReadProcessMemory failed. Win32 error = {Marshal.GetLastWin32Error()}");
                return false;
            }
            return true;
        }

        public bool TryReadPointerChainFromBase(IntPtr baseAddress, byte[] buffer, IReadOnlyList<nint> offsets, out int bytesRead)
        {
            bytesRead = 0;

            if (_process is null || _process.HasExited || _dolphin is null)
                return false;

            IntPtr currentAddress = baseAddress;

            for (int i = 0; i < offsets.Count; i++)
            {
                currentAddress = new IntPtr(currentAddress.ToInt64() + offsets[i]);

                Console.WriteLine($"TryReadPointerChainFromBase: offset {offsets[i]} -> 0x{currentAddress.ToInt64():X8}");

                if (i < offsets.Count - 1)
                {
                    if (!_dolphin.TryGetAddress(currentAddress, out IntPtr currentHost))
                    {
                        Console.WriteLine($"TryReadPointerChainFromBase: failed to translate 0x{currentAddress.ToInt64():X8}.");
                        return false;
                    }

                    int ptrSize = IntPtr.Size;
                    byte[] ptrBuf = new byte[ptrSize];
                    int bytesReadPointer = 0;

                    if (!ReadProcessMemory(_readHandle, currentHost, ptrBuf, ptrBuf.Length, ref bytesReadPointer) || bytesReadPointer != ptrBuf.Length)
                    {
                        Console.WriteLine($"TryReadPointerChainFromBase: pointer read failed. Win32 error = {Marshal.GetLastWin32Error()}");
                        return false;
                    }

                    long pointerValue = ptrSize == 8 ? BitConverter.ToInt64(ptrBuf, 0) : BitConverter.ToInt32(ptrBuf, 0);

                    currentAddress = new IntPtr(pointerValue);

                    Console.WriteLine($"TryReadPointerChainFromBase: dereferenced -> 0x{currentAddress.ToInt64():X8}");
                }
            }

            if (!_dolphin.TryGetAddress(currentAddress, out IntPtr finalHost))
            {
                Console.WriteLine($"TryReadPointerChainFromBase: failed to translate final address 0x{currentAddress.ToInt64():X8}.");
                return false;
            }

            if (!ReadProcessMemory(_readHandle, finalHost, buffer, buffer.Length, ref bytesRead))
            {
                Console.WriteLine($"TryReadPointerChainFromBase: final read failed. Win32 error = {Marshal.GetLastWin32Error()}");
                return false;
            }

            Console.WriteLine($"TryReadPointerChainFromBase: successfully read {bytesRead} bytes from 0x{currentAddress.ToInt64():X8}.");

            return bytesRead == buffer.Length;
        }

        public bool TryReadPointer(IntPtr address, int offset, out IntPtr pointer)
        {
            pointer = IntPtr.Zero;
            IntPtr targetAddress = new IntPtr(address.ToInt64() + offset);

            if (!_dolphin.TryGetAddress(targetAddress, out IntPtr hostAddress))
            {
                Console.WriteLine($"Failed to translate Dolphin address 0x{targetAddress.ToInt64():X8}.");
                return false;
            }

            byte[] buffer = new byte[4];
            int bytesRead = 0;

            if (!ReadProcessMemory(_readHandle, hostAddress, buffer, buffer.Length, ref bytesRead))
            {
                Console.WriteLine($"ReadProcessMemory failed. Win32 error = {Marshal.GetLastWin32Error()}");
                return false;
            }

            if (bytesRead != 4)
            {
                Console.WriteLine($"Pointer read returned {bytesRead} bytes.");
                return false;
            }

            uint value = BinaryPrimitives.ReadUInt32BigEndian(buffer);

            pointer = new IntPtr(unchecked((long)value));

            Console.WriteLine($"Read pointer 0x{targetAddress.ToInt64():X8} -> 0x{pointer.ToInt64():X8}");

            return pointer != IntPtr.Zero;
        }

        public bool TryReadByte(IntPtr address, out byte value)
        {
            value = 0;

            if (!_dolphin.TryGetAddress(address, out IntPtr hostAddress))
                return false;

            byte[] buffer = new byte[1];
            int bytesRead = 0;

            if (!ReadProcessMemory( _readHandle, hostAddress, buffer, 1, ref bytesRead))
                return false;

            if (bytesRead != 1)
                return false;

            value = buffer[0];

            return true;
        }

        public bool TryWriteByte(IntPtr address, byte value)
        {
            if (!_dolphin.TryGetAddress(address, out IntPtr hostAddress))
                return false;

            byte[] buffer = { value };
            int bytesWritten = 0;

            return WriteProcessMemory( _writeHandle, hostAddress, buffer, 1, ref bytesWritten) && bytesWritten == 1;
        }

        public void Dispose()
        {
            if (_readHandle != IntPtr.Zero)
                CloseHandle(_readHandle);
        }

        /// <summary>
        /// Try to write in the game's memory
        /// </summary>
        public bool TryWrite(IntPtr address, byte[] data, out int bytesWritten)
        {
            bytesWritten = 0;

            if (_process is null || _process.HasExited)
                return false;

            if (_dolphin is null)
                return false;

            if (!_dolphin.TryGetAddress(address, out IntPtr hostAddress))
                return false;

            return WriteProcessMemory(_writeHandle, hostAddress, data, data.Length, ref bytesWritten);
        }
    }
}