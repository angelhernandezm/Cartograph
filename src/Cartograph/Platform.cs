using System.Runtime.InteropServices;

namespace Cartograph;

/// <summary>
/// Platform facts that affect memory mapping: the page size and the OS allocation granularity.
/// </summary>
/// <remarks>
/// On Windows the allocation granularity (64 KiB) differs from the page size (4 KiB), and mapped
/// view offsets must be aligned to the granularity. Window tiling in <see cref="MappedFile"/> uses
/// <see cref="AllocationGranularity"/> so that every window begins on a legal, aligned boundary.
/// </remarks>
public static partial class Platform
{
    /// <summary>The system memory page size in bytes.</summary>
    public static int PageSize => Environment.SystemPageSize;

    /// <summary>The OS allocation granularity in bytes (64 KiB on Windows; page size elsewhere).</summary>
    public static long AllocationGranularity { get; } = QueryAllocationGranularity();

    /// <summary>Rounds <paramref name="value"/> up to a multiple of <paramref name="alignment"/>.</summary>
    public static long AlignUp(long value, long alignment)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(alignment);
        return (value + (alignment - 1)) / alignment * alignment;
    }

    /// <summary>Rounds <paramref name="value"/> down to a multiple of <paramref name="alignment"/>.</summary>
    public static long AlignDown(long value, long alignment)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(alignment);
        return value / alignment * alignment;
    }

    private static long QueryAllocationGranularity()
    {
        if (OperatingSystem.IsWindows())
        {
            GetSystemInfo(out SYSTEM_INFO info);
            return info.dwAllocationGranularity;
        }

        return Environment.SystemPageSize;
    }

    [LibraryImport("kernel32.dll")]
    private static partial void GetSystemInfo(out SYSTEM_INFO lpSystemInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_INFO
    {
        public ushort wProcessorArchitecture;
        public ushort wReserved;
        public uint dwPageSize;
        public IntPtr lpMinimumApplicationAddress;
        public IntPtr lpMaximumApplicationAddress;
        public IntPtr dwActiveProcessorMask;
        public uint dwNumberOfProcessors;
        public uint dwProcessorType;
        public uint dwAllocationGranularity;
        public ushort wProcessorLevel;
        public ushort wProcessorRevision;
    }
}
