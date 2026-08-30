// ============================================================================
// Cartograph
// File: Platform.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Exposes platform-specific memory facts (page size, allocation granularity) and
// alignment helpers used by MappedFile to tile windows on legal OS boundaries.
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
    /// <param name="value">The value to align.</param>
    /// <param name="alignment">The alignment boundary; must be positive.</param>
    /// <returns>The smallest multiple of <paramref name="alignment"/> that is greater than or equal to <paramref name="value"/>.</returns>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="alignment" /> is negative or zero.</exception>
    public static long AlignUp(long value, long alignment)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(alignment);
        return (value + (alignment - 1)) / alignment * alignment;
    }

    /// <summary>Rounds <paramref name="value"/> down to a multiple of <paramref name="alignment"/>.</summary>
    /// <param name="value">The value to align.</param>
    /// <param name="alignment">The alignment boundary; must be positive.</param>
    /// <returns>The largest multiple of <paramref name="alignment"/> that is less than or equal to <paramref name="value"/>.</returns>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="alignment" /> is negative or zero.</exception>
    public static long AlignDown(long value, long alignment)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(alignment);
        return value / alignment * alignment;
    }

    /// <summary>
    /// Queries the OS allocation granularity: calls <c>GetSystemInfo</c> on Windows,
    /// falls back to <see cref="Environment.SystemPageSize"/> elsewhere.
    /// </summary>
    /// <returns>The allocation granularity in bytes.</returns>
    private static long QueryAllocationGranularity()
    {
        if (OperatingSystem.IsWindows())
        {
            GetSystemInfo(out SYSTEM_INFO info);
            return info.dwAllocationGranularity;
        }

        return Environment.SystemPageSize;
    }

    /// <summary>P/Invoke declaration for the Windows <c>GetSystemInfo</c> API.</summary>
    /// <param name="lpSystemInfo">Receives the system information populated by the OS.</param>
    [LibraryImport("kernel32.dll")]
    private static partial void GetSystemInfo(out SYSTEM_INFO lpSystemInfo);

    /// <summary>Subset of the Windows <c>SYSTEM_INFO</c> structure used to retrieve the allocation granularity.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_INFO
    {
        /// <summary>The processor architecture of the installed operating system.</summary>
        public ushort wProcessorArchitecture;
        /// <summary>Reserved; must be zero.</summary>
        public ushort wReserved;
        /// <summary>The page size and the granularity of page protection and commitment.</summary>
        public uint dwPageSize;
        /// <summary>A pointer to the lowest memory address accessible to applications and dynamic-link libraries (DLLs).</summary>
        public IntPtr lpMinimumApplicationAddress;
        /// <summary>A pointer to the highest memory address accessible to applications and DLLs.</summary>
        public IntPtr lpMaximumApplicationAddress;
        /// <summary>A mask representing the set of processors configured into the system.</summary>
        public IntPtr dwActiveProcessorMask;
        /// <summary>The number of logical processors in the current group.</summary>
        public uint dwNumberOfProcessors;
        /// <summary>An obsolete member; use <see cref="wProcessorArchitecture"/> instead.</summary>
        public uint dwProcessorType;
        /// <summary>The granularity for the starting address at which virtual memory can be allocated.</summary>
        public uint dwAllocationGranularity;
        /// <summary>The architecture-dependent processor level.</summary>
        public ushort wProcessorLevel;
        /// <summary>The architecture-dependent processor revision.</summary>
        public ushort wProcessorRevision;
    }
}
