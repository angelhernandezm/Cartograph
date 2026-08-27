using System.Runtime.InteropServices;

namespace Cartograph;

/// <summary>
/// Optional prefaulting helpers that ask the OS to bring pages into memory ahead of access.
/// </summary>
/// <remarks>
/// Page faults are synchronous and blocking: the first touch of a not-yet-resident mapped page
/// stalls the thread until the OS reads it in. Deliberate prefaulting (<c>PrefetchVirtualMemory</c>
/// on Windows, <c>madvise(MADV_WILLNEED)</c> on Linux) lets callers pay that cost up front and avoid
/// unpredictable mid-query stalls. On unsupported platforms these methods are safe no-ops.
/// </remarks>
public static partial class NativePrefetch
{
    private const int MADV_WILLNEED = 3;

    /// <summary>Hints the OS to make the given region resident. Safe no-op on unsupported platforms.</summary>
    public static unsafe void WillNeed(void* address, nuint length)
    {
        if (address is null || length == 0)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            WIN32_MEMORY_RANGE_ENTRY entry;
            entry.VirtualAddress = (IntPtr)address;
            entry.NumberOfBytes = length;
            // Pseudo handle (HANDLE)-1 == current process; avoids allocating a real handle.
            _ = PrefetchVirtualMemory(new IntPtr(-1), (UIntPtr)1, ref entry, 0);
        }
        else if (OperatingSystem.IsLinux())
        {
            _ = madvise((IntPtr)address, length, MADV_WILLNEED);
        }
        // Other platforms: no-op.
    }

    /// <summary>Hints the OS to make the memory backing <paramref name="segment"/> resident.</summary>
    public static unsafe void WillNeed(MappedSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        using ViewLease lease = segment.Lease();
        ReadOnlyMemory<byte> memory = lease.Memory;
        if (memory.IsEmpty)
        {
            return;
        }

        using System.Buffers.MemoryHandle handle = memory.Pin();
        WillNeed(handle.Pointer, (nuint)memory.Length);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrefetchVirtualMemory(
        IntPtr hProcess,
        UIntPtr numberOfEntries,
        ref WIN32_MEMORY_RANGE_ENTRY virtualAddresses,
        uint flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int madvise(IntPtr addr, nuint length, int advice);

    [StructLayout(LayoutKind.Sequential)]
    private struct WIN32_MEMORY_RANGE_ENTRY
    {
        public IntPtr VirtualAddress;
        public nuint NumberOfBytes;
    }
}
