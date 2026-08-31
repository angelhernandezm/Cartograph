// ============================================================================
// Cartograph
// File: RunMetrics.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Captures allocation, garbage collection and working set deltas around a
// phase of work. Duplicated verbatim from Cartograph.Baseline (only the
// namespace differs) on purpose: the baseline must share no assembly, JIT
// behaviour or allocation profile with the library under test, so each program
// carries its own copy rather than referencing a common one
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

using System.Diagnostics;
using System.Globalization;

namespace Cartograph.Harness;

/// <summary>
/// Holds the resource cost of one measured phase of work
/// </summary>
/// <remarks>
/// Wall-clock time alone hides the difference between these two programs, because the interesting
/// distinction is not how long a read took but how much memory it had to touch to do it. Allocated
/// bytes and collection counts are the columns that make that visible.
/// </remarks>
internal sealed class RunMetrics
{
    /// <summary>
    /// Gets the name of the measured phase
    /// </summary>
    /// <value>A short label such as <c>open</c> or <c>read all records</c>.</value>
    public required string Phase { get; init; }

    /// <summary>
    /// Gets the wall-clock duration of the phase
    /// </summary>
    /// <value>Time elapsed between the start and the stop of the measurement.</value>
    public required TimeSpan Elapsed { get; init; }

    /// <summary>
    /// Gets the number of bytes allocated on the managed heap during the phase
    /// </summary>
    /// <value>
    /// Cumulative allocation, not peak occupancy: memory that was allocated and immediately
    /// collected still counts, which is exactly what makes garbage collection pressure visible.
    /// </value>
    public required long AllocatedBytes { get; init; }

    /// <summary>
    /// Gets the number of generation zero collections that occurred during the phase
    /// </summary>
    /// <value>Zero when the phase produced no short-lived garbage.</value>
    public required int Gen0 { get; init; }

    /// <summary>
    /// Gets the number of generation one collections that occurred during the phase
    /// </summary>
    /// <value>Zero when nothing survived a generation zero collection.</value>
    public required int Gen1 { get; init; }

    /// <summary>
    /// Gets the number of generation two collections that occurred during the phase
    /// </summary>
    /// <value>
    /// Non-zero values usually indicate large object heap traffic, which is what buffering whole
    /// files on the heap produces.
    /// </value>
    public required int Gen2 { get; init; }

    /// <summary>
    /// Gets the growth in the process working set across the phase, in bytes
    /// </summary>
    /// <value>
    /// May be negative when the operating system trimmed the process. For memory-mapped reads this
    /// reflects page cache pages mapped into the process rather than private managed memory.
    /// </value>
    public required long WorkingSetDelta { get; init; }

    /// <summary>
    /// Gets the peak working set of the process observed at the end of the phase, in bytes
    /// </summary>
    /// <value>The high-water mark reported by the operating system for the whole process lifetime.</value>
    public required long PeakWorkingSet { get; init; }

    /// <summary>
    /// Gets the number of payload bytes processed during the phase
    /// </summary>
    /// <value>Zero when the phase did not move a meaningful amount of payload.</value>
    public long PayloadBytes { get; init; }

    /// <summary>
    /// Gets the ratio of bytes allocated to payload bytes processed
    /// </summary>
    /// <value>
    /// A value near zero means the phase avoided copying payload onto the managed heap; a value
    /// near or above one means it copied the payload at least once.
    /// </value>
    public double AllocationRatio => PayloadBytes > 0 ? AllocatedBytes / (double)PayloadBytes : 0d;

    /// <summary>
    /// Begins a measurement
    /// </summary>
    /// <param name="phase">Short label describing the work about to be performed</param>
    /// <returns>A scope that produces a <see cref="RunMetrics" /> when stopped</returns>
    /// <exception cref="System.ArgumentException"><paramref name="phase" /> is <see langword="null" /> or empty.</exception>
    public static Scope Measure(string phase)
    {
        ArgumentException.ThrowIfNullOrEmpty(phase);
        return new Scope(phase);
    }

    /// <summary>
    /// Prints the metrics as an aligned block
    /// </summary>
    public void Report()
    {
        ConsoleReport.Field($"[{Phase}] elapsed", ConsoleReport.Duration(Elapsed));
        ConsoleReport.Field($"[{Phase}] allocated", ConsoleReport.Bytes(AllocatedBytes));

        if (PayloadBytes > 0)
        {
            ConsoleReport.Field(
                $"[{Phase}] alloc / payload",
                string.Format(CultureInfo.InvariantCulture, "{0:0.###}x", AllocationRatio));
        }

        ConsoleReport.Field($"[{Phase}] GC gen0/1/2", $"{Gen0} / {Gen1} / {Gen2}");
        ConsoleReport.Field($"[{Phase}] working set delta", ConsoleReport.Bytes(WorkingSetDelta));
        ConsoleReport.Field($"[{Phase}] peak working set", ConsoleReport.Bytes(PeakWorkingSet));

        if (PayloadBytes > 0 && Elapsed.TotalSeconds > 0)
        {
            double throughput = PayloadBytes / (1024d * 1024d) / Elapsed.TotalSeconds;
            ConsoleReport.Field(
                $"[{Phase}] throughput",
                string.Format(CultureInfo.InvariantCulture, "{0:0.##} MiB/s", throughput));
        }
    }

    /// <summary>
    /// Emits a single machine readable line describing the metrics
    /// </summary>
    /// <param name="label">Label identifying the program and mode that produced the metrics</param>
    /// <remarks>
    /// The line is printed even in quiet mode so that repeated runs can be collected into a table
    /// without parsing the human readable report.
    /// </remarks>
    public void ReportCsv(string label)
    {
        ConsoleReport.Always(string.Format(
            CultureInfo.InvariantCulture,
            "CSV,{0},{1},{2:0.###},{3},{4},{5},{6},{7},{8}",
            label,
            Phase,
            Elapsed.TotalMilliseconds,
            AllocatedBytes,
            PayloadBytes,
            Gen0,
            Gen1,
            Gen2,
            PeakWorkingSet));
    }

    /// <summary>
    /// Writes the header row matching the format produced by <see cref="ReportCsv(string)" />
    /// </summary>
    public static void ReportCsvHeader() =>
        ConsoleReport.Always("CSV,label,phase,elapsed_ms,allocated_bytes,payload_bytes,gen0,gen1,gen2,peak_ws_bytes");

    /// <summary>
    /// Represents an in-flight measurement
    /// </summary>
    /// <remarks>
    /// A forced collection is performed before the baseline is taken so that garbage produced by
    /// earlier phases is not attributed to this one.
    /// </remarks>
    internal sealed class Scope
    {
        /// <summary>
        /// Label describing the work being measured
        /// </summary>
        private readonly string _phase;

        /// <summary>
        /// Allocated byte count captured at the start of the phase
        /// </summary>
        private readonly long _allocated;

        /// <summary>
        /// Working set captured at the start of the phase
        /// </summary>
        private readonly long _workingSet;

        /// <summary>
        /// Generation zero collection count captured at the start of the phase
        /// </summary>
        private readonly int _gen0;

        /// <summary>
        /// Generation one collection count captured at the start of the phase
        /// </summary>
        private readonly int _gen1;

        /// <summary>
        /// Generation two collection count captured at the start of the phase
        /// </summary>
        private readonly int _gen2;

        /// <summary>
        /// Stopwatch measuring the wall-clock duration of the phase
        /// </summary>
        private readonly Stopwatch _watch;

        /// <summary>
        /// Initializes a new instance of the <see cref="Scope" /> class
        /// </summary>
        /// <param name="phase">Short label describing the work about to be performed</param>
        internal Scope(string phase)
        {
            _phase = phase;

            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);

            _allocated = GC.GetTotalAllocatedBytes(precise: true);
            _gen0 = GC.CollectionCount(0);
            _gen1 = GC.CollectionCount(1);
            _gen2 = GC.CollectionCount(2);
            _workingSet = CurrentWorkingSet();
            _watch = Stopwatch.StartNew();
        }

        /// <summary>
        /// Ends the measurement and produces the captured metrics
        /// </summary>
        /// <param name="payloadBytes">Number of payload bytes processed during the phase</param>
        /// <returns>The metrics describing the phase</returns>
        public RunMetrics Stop(long payloadBytes = 0)
        {
            _watch.Stop();

            long allocated = GC.GetTotalAllocatedBytes(precise: true) - _allocated;
            long workingSet = CurrentWorkingSet();

            return new RunMetrics
            {
                Phase = _phase,
                Elapsed = _watch.Elapsed,
                AllocatedBytes = allocated,
                Gen0 = GC.CollectionCount(0) - _gen0,
                Gen1 = GC.CollectionCount(1) - _gen1,
                Gen2 = GC.CollectionCount(2) - _gen2,
                WorkingSetDelta = workingSet - _workingSet,
                PeakWorkingSet = PeakWorkingSetBytes(),
                PayloadBytes = payloadBytes,
            };
        }

        /// <summary>
        /// Reads the current working set of this process
        /// </summary>
        /// <returns>The working set in bytes, or zero when it cannot be read</returns>
        private static long CurrentWorkingSet()
        {
            try
            {
                using Process process = Process.GetCurrentProcess();
                process.Refresh();
                return process.WorkingSet64;
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                return 0;
            }
        }

        /// <summary>
        /// Reads the peak working set of this process
        /// </summary>
        /// <returns>The peak working set in bytes, or zero when it cannot be read</returns>
        private static long PeakWorkingSetBytes()
        {
            try
            {
                using Process process = Process.GetCurrentProcess();
                process.Refresh();
                return process.PeakWorkingSet64;
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                return 0;
            }
        }
    }
}
