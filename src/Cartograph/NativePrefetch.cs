// ============================================================================
// Cartograph
// File: NativePrefetch.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Optional OS-level prefaulting helpers (PrefetchVirtualMemory on Windows,
// madvise(MADV_WILLNEED) on Linux) to bring mapped pages into memory ahead of access.
//
// License: MIT
// ============================================================================
//
// MIT License
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
// ============================================================================

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
    /// <summary>The <c>madvise</c> advice value that hints pages will be needed soon.</summary>
    private const int MADV_WILLNEED = 3;

    /// <summary>Hints the OS to make the given region resident. Safe no-op on unsupported platforms.</summary>
    /// <param name="address">Pointer to the start of the virtual memory region to prefetch.</param>
    /// <param name="length">The number of bytes to prefetch.</param>
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
    /// <param name="segment">The mapped segment whose pages should be prefetched.</param>
    /// <exception cref="System.ArgumentNullException"><paramref name="segment" /> is <c>null</c>.</exception>
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

    /// <summary>P/Invoke declaration for the Windows <c>PrefetchVirtualMemory</c> API.</summary>
    /// <param name="hProcess">Handle to the process whose memory is to be prefetched; use <c>(IntPtr)(-1)</c> for the current process.</param>
    /// <param name="numberOfEntries">The number of entries in <paramref name="virtualAddresses"/>.</param>
    /// <param name="virtualAddresses">Reference to the first <see cref="WIN32_MEMORY_RANGE_ENTRY"/> structure describing the regions to prefetch.</param>
    /// <param name="flags">Reserved; must be zero.</param>
    /// <returns><see langword="true"/> if the call succeeded; otherwise <see langword="false"/>.</returns>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrefetchVirtualMemory(
        IntPtr hProcess,
        UIntPtr numberOfEntries,
        ref WIN32_MEMORY_RANGE_ENTRY virtualAddresses,
        uint flags);

    /// <summary>P/Invoke declaration for the Linux <c>madvise</c> system call.</summary>
    /// <param name="addr">The start address of the memory range.</param>
    /// <param name="length">The length of the memory range in bytes.</param>
    /// <param name="advice">The advice value (e.g. <c>MADV_WILLNEED</c>).</param>
    /// <returns>Zero on success; -1 on error with <c>errno</c> set.</returns>
    [LibraryImport("libc", SetLastError = true)]
    private static partial int madvise(IntPtr addr, nuint length, int advice);

    /// <summary>Describes a virtual memory range for use with <c>PrefetchVirtualMemory</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WIN32_MEMORY_RANGE_ENTRY
    {
        /// <summary>The base address of the virtual memory range.</summary>
        public IntPtr VirtualAddress;
        /// <summary>The size of the virtual memory range in bytes.</summary>
        public nuint NumberOfBytes;
    }
}
