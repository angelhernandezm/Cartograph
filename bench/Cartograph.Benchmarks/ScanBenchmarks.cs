// ============================================================================
// Cartograph
// File: ScanBenchmarks.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// BenchmarkDotNet benchmarks for sequential full-scan and random record-access
// throughput, comparing mapped and random-access chunk sources.
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

using System.Buffers;
using BenchmarkDotNet.Attributes;
using Cartograph.Format;

namespace Cartograph.Benchmarks;

/// <summary>
/// Sequential full-scan and random record-access throughput for the two chunk sources versus a
/// managed baseline.
/// </summary>
/// <remarks>
/// mmap tends to win on random reads over a hot page cache (no per-read syscall, no copy), while
/// pooled <see cref="System.IO.RandomAccess"/> is competitive or better on large sequential scans and
/// is async-friendly. The managed baseline reads the whole file into an array up front, which is fast
/// to walk but allocates memory proportional to the entire file.
/// </remarks>
[MemoryDiagnoser]
public class ScanBenchmarks
{
    /// <summary>Number of float dimensions per vector record.</summary>
    private const int Dimensions = 768;

    private string _path = string.Empty;
    private int _recordCount;
    private int[] _randomOrder = [];

    /// <summary>Gets or sets the number of vector records in the artifact.</summary>
    [Params(50_000)]
    public int RecordCount { get; set; }

    /// <summary>
    /// Writes the artifact and builds a Fisher-Yates-shuffled access order for random-access benchmarks.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _recordCount = RecordCount;
        _path = BenchmarkData.WriteVectorArtifact(RecordCount, Dimensions);

        _randomOrder = new int[RecordCount];
        for (int i = 0; i < RecordCount; i++)
        {
            _randomOrder[i] = i;
        }

        Random random = new(7);
        for (int i = RecordCount - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (_randomOrder[i], _randomOrder[j]) = (_randomOrder[j], _randomOrder[i]);
        }
    }

    /// <summary>Deletes the temporary artifact file created by <see cref="Setup"/>.</summary>
    [GlobalCleanup]
    public void Cleanup() => BenchmarkData.TryDelete(_path);

    /// <summary>
    /// Benchmark: performs a sequential full scan using the <see cref="ChunkSourceKind.Mapped"/> source.
    /// </summary>
    /// <returns>An accumulated checksum value over sampled bytes to prevent dead-code elimination.</returns>
    [Benchmark]
    public long SequentialScan_Mapped() => SequentialScan(ChunkSourceKind.Mapped);

    /// <summary>
    /// Benchmark: performs a sequential full scan using the <see cref="ChunkSourceKind.RandomAccess"/> source.
    /// </summary>
    /// <returns>An accumulated checksum value over sampled bytes to prevent dead-code elimination.</returns>
    [Benchmark]
    public long SequentialScan_RandomAccess() => SequentialScan(ChunkSourceKind.RandomAccess);

    /// <summary>
    /// Benchmark: performs a random-order record access using the <see cref="ChunkSourceKind.Mapped"/> source.
    /// </summary>
    /// <returns>An accumulated checksum value over first bytes to prevent dead-code elimination.</returns>
    [Benchmark]
    public long RandomAccess_Mapped() => RandomScan(ChunkSourceKind.Mapped);

    /// <summary>
    /// Benchmark: performs a random-order record access using the <see cref="ChunkSourceKind.RandomAccess"/> source.
    /// </summary>
    /// <returns>An accumulated checksum value over first bytes to prevent dead-code elimination.</returns>
    [Benchmark]
    public long RandomAccess_RandomAccess() => RandomScan(ChunkSourceKind.RandomAccess);

    /// <summary>
    /// Reads all records sequentially using the specified <paramref name="kind"/> and accumulates
    /// a checksum by sampling every 64th byte of each record.
    /// </summary>
    /// <param name="kind">The chunk source kind to use when opening the artifact.</param>
    /// <returns>The accumulated checksum.</returns>
    private long SequentialScan(ChunkSourceKind kind)
    {
        using Artifact artifact = Artifact.Open(_path, new ArtifactOpenOptions { ChunkSource = kind, VerifyChecksums = false });
        ArtifactSegment segment = artifact.Segments[0];
        long acc = 0;
        for (int i = 0; i < _recordCount; i++)
        {
            using RecordLease lease = segment.ReadRecord(i);
            acc += Fold(lease.Sequence);
        }

        return acc;
    }

    /// <summary>
    /// Reads records in the pre-shuffled random order using the specified <paramref name="kind"/>
    /// and accumulates the first byte of each record's first span.
    /// </summary>
    /// <param name="kind">The chunk source kind to use when opening the artifact.</param>
    /// <returns>The accumulated checksum.</returns>
    private long RandomScan(ChunkSourceKind kind)
    {
        using Artifact artifact = Artifact.Open(_path, new ArtifactOpenOptions { ChunkSource = kind, VerifyChecksums = false });
        ArtifactSegment segment = artifact.Segments[0];
        long acc = 0;
        for (int i = 0; i < _randomOrder.Length; i++)
        {
            using RecordLease lease = segment.ReadRecord(_randomOrder[i]);
            acc += lease.FirstSpan.Length > 0 ? lease.FirstSpan[0] : 0;
        }

        return acc;
    }

    /// <summary>
    /// Accumulates every 64th byte of each segment in <paramref name="sequence"/> into a long checksum.
    /// </summary>
    /// <param name="sequence">The byte sequence to fold.</param>
    /// <returns>The accumulated checksum.</returns>
    private static long Fold(ReadOnlySequence<byte> sequence)
    {
        long acc = 0;
        foreach (ReadOnlyMemory<byte> segment in sequence)
        {
            ReadOnlySpan<byte> span = segment.Span;
            for (int i = 0; i < span.Length; i += 64)
            {
                acc += span[i];
            }
        }

        return acc;
    }
}
