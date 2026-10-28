using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameManager.Dolphin;

/// <summary>
/// Discovers Dolphin's emulated RAM mappings using the same signatures and
/// mapping heuristics as Dolphin Memory Engine's Windows implementation.
/// The supplied process handle must have PROCESS_QUERY_INFORMATION and
/// PROCESS_VM_READ access.
/// </summary>
internal sealed class MEM2Extension
{
    private const uint Mem1Start = 0x80000000;
    private const uint Mem2Start = 0x90000000;
    private const uint AramStart = 0x7E000000;
    private const uint AramEnd = 0x7F000000;
    private const uint AramFakeSize = 0x02000000;

    private const uint SystemInfoOffset = 0x18;
    private const uint DolphinOsGlobalsOffset = 0x84;
    private const uint WiiSpecificInfoOffset = 0x3100;

    private const int SystemInfoSize = 20;
    private const int DolphinOsGlobalsSize = 124;
    private const int WiiSpecificInfoSize = 32;

    private const uint MinimumPlausibleMemorySize = 0x01800000;
    private const uint MaximumPlausibleMemorySize = 0x08000000;
    private const ulong MaximumMappingDistance = 0x10000000;

    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);

    private readonly Process _process;
    private readonly IntPtr _processHandle;
    private readonly object _sync = new();

    private ulong _emuRamAddressStart;
    private ulong _aramAddressStart;
    private ulong _mem2AddressStart;
    private uint _mem1Size;
    private uint _aramSize;
    private uint _mem2Size;
    private bool _aramAccessible;
    private bool _mem2Present;
    private DateTime _lastAttemptUtc = DateTime.MinValue;

    public bool IsInitialized { get; private set; }
    public bool IsMem2Present => IsInitialized && _mem2Present;
    public uint Mem1Size => _mem1Size;
    public uint Mem2Size => _mem2Size;

    public MEM2Extension(Process process, IntPtr processHandle)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        if (processHandle == IntPtr.Zero)
            throw new ArgumentException("A valid Dolphin process handle is required.", nameof(processHandle));

        _processHandle = processHandle;
    }

    /// <summary>
    /// Forces a fresh scan. Call after the game has initialized in Dolphin.
    /// </summary>
    public bool TryInitialize(out string diagnostic)
    {
        lock (_sync)
        {
            _lastAttemptUtc = DateTime.UtcNow;
            ResetDiscovery();

            if (_process.HasExited)
            {
                diagnostic = "Dolphin has exited.";
                return false;
            }

            try
            {
                List<MemoryMapLine> memoryMap = EnumerateValidMemoryRegions();
                if (memoryMap.Count == 0)
                {
                    diagnostic = "No committed, resident Dolphin memory regions were found.";
                    return false;
                }

                if (!TryFindEmulatedRam(memoryMap))
                {
                    diagnostic = "Could not identify Dolphin's emulated RAM using DME's memory signatures.";
                    return false;
                }

                FindAuxiliaryMappings(memoryMap);
                IsInitialized = _emuRamAddressStart != 0;

                diagnostic = IsInitialized
                    ? $"RAM mapping found. MEM1 host=0x{_emuRamAddressStart:X}, size=0x{_mem1Size:X}; " +
                      (_mem2Present
                          ? $"MEM2 host=0x{_mem2AddressStart:X}, size=0x{_mem2Size:X}."
                          : $"MEM2 mapping not found (reported size=0x{_mem2Size:X}).")
                    : "Dolphin's emulated RAM mapping was not found.";

                return IsInitialized;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ResetDiscovery();
                diagnostic = $"Dolphin RAM discovery failed: {ex.Message}";
                return false;
            }
        }
    }

    /// <summary>
    /// Translate an emulated Wii/GC address to its host-process address.
    /// The complete requested byte range must fit inside a discovered region.
    /// </summary>
    public bool TryTranslateAddress(IntPtr emulatedAddress, int length, out IntPtr processAddress)
    {
        lock (_sync)
        {
            return TryTranslateAddressLocked(emulatedAddress, length, out processAddress);
        }
    }

    private bool TryTranslateAddressLocked(
        IntPtr emulatedAddress,
        int length,
        out IntPtr processAddress)
    {
        processAddress = IntPtr.Zero;

        if (length <= 0)
            return false;

        ulong address = unchecked((uint)emulatedAddress.ToInt64());
        ulong end = address + (uint)length;
        if (end > 0x1_0000_0000UL)
            return false;

        if (!IsInitialized || (address >= Mem2Start && !_mem2Present))
        {
            bool shouldRetry = DateTime.UtcNow - _lastAttemptUtc >= RetryInterval;

            if (shouldRetry)
            {
                _lastAttemptUtc = DateTime.UtcNow;
                ResetDiscovery();

                if (!_process.HasExited)
                {
                    try
                    {
                        List<MemoryMapLine> memoryMap = EnumerateValidMemoryRegions();
                        if (memoryMap.Count > 0 && TryFindEmulatedRam(memoryMap))
                        {
                            FindAuxiliaryMappings(memoryMap);
                            IsInitialized = _emuRamAddressStart != 0;
                        }
                    }
                    catch
                    {
                        ResetDiscovery();
                    }
                }
            }
        }

        if (!IsInitialized)
            return false;

        if (address >= Mem2Start)
        {
            ulong offset = address - Mem2Start;
            if (!_mem2Present || end - Mem2Start > _mem2Size)
                return false;

            processAddress = ToIntPtr(_mem2AddressStart + offset);
            return true;
        }

        if (address >= Mem1Start)
        {
            ulong offset = address - Mem1Start;
            if (end - Mem1Start > _mem1Size)
                return false;

            processAddress = ToIntPtr(_emuRamAddressStart + offset);
            return true;
        }

        // DME's ARAM mapping is relevant to GameCube/Triforce games only.
        if (address >= AramStart && end <= AramEnd && _aramAccessible)
        {
            ulong offset = address - AramStart;
            if (offset >= _aramSize)
                return false;

            processAddress = ToIntPtr(_aramAddressStart + offset);
            return true;
        }

        return false;
    }

    private void ResetDiscovery()
    {
        IsInitialized = false;
        _emuRamAddressStart = 0;
        _aramAddressStart = 0;
        _mem2AddressStart = 0;
        _mem1Size = 0;
        _aramSize = 0;
        _mem2Size = 0;
        _aramAccessible = false;
        _mem2Present = false;
    }

    private List<MemoryMapLine> EnumerateValidMemoryRegions()
    {
        var result = new List<MemoryMapLine>();
        nuint mbiSize = (nuint)Marshal.SizeOf<MemoryBasicInformation>();
        ulong currentAddress = 0;

        while (true)
        {
            IntPtr queryAddress = ToIntPtr(currentAddress);
            nuint queryResult = VirtualQueryEx(
                _processHandle,
                queryAddress,
                out MemoryBasicInformation info,
                mbiSize);

            if (queryResult == 0 || queryResult.ToUInt64() != mbiSize.ToUInt64())
                break;

            ulong baseAddress = unchecked((ulong)info.BaseAddress.ToInt64());
            ulong regionSize = info.RegionSize.ToUInt64();
            if (regionSize == 0 || baseAddress > ulong.MaxValue - regionSize)
                break;

            // Match DME: only keep regions whose first page is currently resident.
            var workingSetInfo = new WorkingSetExInformation
            {
                VirtualAddress = info.BaseAddress
            };

            if (QueryWorkingSetEx(
                    _processHandle,
                    ref workingSetInfo,
                    (uint)Marshal.SizeOf<WorkingSetExInformation>()) &&
                (workingSetInfo.VirtualAttributes.ToUInt64() & 1UL) != 0)
            {
                result.Add(new MemoryMapLine(baseAddress, regionSize));
            }

            ulong nextAddress = baseAddress + regionSize;
            if (nextAddress <= currentAddress)
                break;

            currentAddress = nextAddress;
        }

        return result;
    }

    private bool TryFindEmulatedRam(IReadOnlyList<MemoryMapLine> memoryMap)
    {
        byte[] systemInfo = new byte[SystemInfoSize];
        byte[] dolphinOsGlobals = new byte[DolphinOsGlobalsSize];
        byte[] wiiSpecificInfo = new byte[WiiSpecificInfoSize];

        foreach (MemoryMapLine line in memoryMap)
        {
            if (!TryReadExact(AddAddress(line.BaseAddress, SystemInfoOffset), systemInfo) ||
                !TryReadExact(AddAddress(line.BaseAddress, DolphinOsGlobalsOffset), dolphinOsGlobals) ||
                !TryReadExact(AddAddress(line.BaseAddress, WiiSpecificInfoOffset), wiiSpecificInfo))
            {
                continue;
            }

            bool isWiiDisc = systemInfo[0] == 0x5D &&
                             systemInfo[1] == 0x1C &&
                             systemInfo[2] == 0x9E &&
                             systemInfo[3] == 0xA3;

            bool isGameCubeDisc = systemInfo[4] == 0xC2 &&
                                  systemInfo[5] == 0x33 &&
                                  systemInfo[6] == 0x9F &&
                                  systemInfo[7] == 0x3D;

            bool isKnownBootCode =
                (systemInfo[8] == 0x0D && systemInfo[9] == 0x15 &&
                 systemInfo[10] == 0xEA && systemInfo[11] == 0x5E) ||
                (systemInfo[8] == 0xE5 && systemInfo[9] == 0x20 &&
                 systemInfo[10] == 0x7C && systemInfo[11] == 0x22);

            uint physicalMem1Size = ReadBigEndianUInt32(systemInfo, 16);
            uint simulatedMem1FromGlobals = ReadBigEndianUInt32(dolphinOsGlobals, 108);
            uint aramSize = ReadBigEndianUInt32(dolphinOsGlobals, 76);
            uint physicalMem1FromWiiInfo = ReadBigEndianUInt32(wiiSpecificInfo, 0);
            uint simulatedMem1FromWiiInfo = ReadBigEndianUInt32(wiiSpecificInfo, 4);
            uint simulatedMem2FromWiiInfo = ReadBigEndianUInt32(wiiSpecificInfo, 28);

            if ((isGameCubeDisc || isWiiDisc) && isKnownBootCode)
            {
                _emuRamAddressStart = line.BaseAddress;

                if (isGameCubeDisc)
                {
                    _mem1Size = simulatedMem1FromGlobals;
                    _aramSize = aramSize;
                }
                else
                {
                    _mem1Size = simulatedMem1FromWiiInfo;
                    _mem2Size = simulatedMem2FromWiiInfo;
                }

                if (IsPlausibleMemorySize(_mem1Size))
                    return true;

                ResetDiscovery();
                continue;
            }

            // DME's WAD fallback: all magic words are null, and physical/simulated
            // memory sizes must agree between SystemInfo, OSGlobals and WiiSpecificInfo.
            bool systemInfoIsNull = IsAllZero(systemInfo, 0, 16);
            if (systemInfoIsNull &&
                IsPlausibleMemorySize(physicalMem1Size) &&
                physicalMem1Size == physicalMem1FromWiiInfo &&
                IsPlausibleMemorySize(simulatedMem1FromGlobals) &&
                simulatedMem1FromGlobals == simulatedMem1FromWiiInfo)
            {
                _emuRamAddressStart = line.BaseAddress;
                _mem1Size = simulatedMem1FromWiiInfo;
                _mem2Size = simulatedMem2FromWiiInfo;
                if (IsPlausibleMemorySize(_mem1Size))
                    return true;

                ResetDiscovery();
            }
        }

        return false;
    }

    private void FindAuxiliaryMappings(IReadOnlyList<MemoryMapLine> memoryMap)
    {
        foreach (MemoryMapLine line in memoryMap)
        {
            if (_aramSize != 0 &&
                line.Size == NextPowerOfTwo(_mem1Size) &&
                line.BaseAddress == _emuRamAddressStart + NextPowerOfTwo(_mem1Size))
            {
                _aramAddressStart = line.BaseAddress;
                _aramAccessible = true;
                break;
            }

            if (_mem2Size == 0 || line.Size != NextPowerOfTwo(_mem2Size))
                continue;

            // Match DME's heuristic: MEM2 may precede MEM1, but a mapping at
            // least 256 MiB after MEM1 is treated as an unrelated region.
            if (line.BaseAddress >= _emuRamAddressStart + MaximumMappingDistance)
                continue;

            _mem2AddressStart = line.BaseAddress;
            _mem2Present = true;
            break;
        }
    }

    private bool TryReadExact(ulong address, byte[] buffer)
    {
        if (_process.HasExited)
            return false;

        bool success = ReadProcessMemoryNative(
            _processHandle,
            ToIntPtr(address),
            buffer,
            (nuint)buffer.Length,
            out nuint bytesRead);

        return success && bytesRead.ToUInt64() == (ulong)buffer.Length;
    }

    private static bool IsPlausibleMemorySize(uint size) =>
        size >= MinimumPlausibleMemorySize && size <= MaximumPlausibleMemorySize;

    private static bool IsAllZero(byte[] bytes, int start, int length)
    {
        for (int i = start; i < start + length; i++)
        {
            if (bytes[i] != 0)
                return false;
        }

        return true;
    }

    private static uint ReadBigEndianUInt32(byte[] buffer, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset, sizeof(uint)));

    private static ulong NextPowerOfTwo(uint value)
    {
        if (value == 0)
            return 0;

        ulong result = 1;
        while (result < value)
            result <<= 1;

        return result;
    }

    private static ulong AddAddress(ulong address, uint offset)
    {
        if (address > ulong.MaxValue - offset)
            throw new OverflowException("Dolphin memory address overflow.");

        return address + offset;
    }

    private static IntPtr ToIntPtr(ulong address) =>
        new(unchecked((long)address));

    private readonly record struct MemoryMapLine(ulong BaseAddress, ulong Size);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WorkingSetExInformation
    {
        public IntPtr VirtualAddress;
        public UIntPtr VirtualAttributes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQueryEx(
        IntPtr hProcess,
        IntPtr lpAddress,
        out MemoryBasicInformation lpBuffer,
        nuint dwLength);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryWorkingSetEx(
        IntPtr hProcess,
        ref WorkingSetExInformation pv,
        uint cb);

    [DllImport("kernel32.dll", EntryPoint = "ReadProcessMemory", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemoryNative(
        IntPtr hProcess,
        IntPtr lpBaseAddress,
        [Out] byte[] lpBuffer,
        nuint nSize,
        out nuint lpNumberOfBytesRead);
}
