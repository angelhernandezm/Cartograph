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
    private const int Dimensions = 768;

    private string _path = string.Empty;
    private int _recordCount;
    private int[] _randomOrder = [];

    [Params(50_000)]
    public int RecordCount { get; set; }

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

    [GlobalCleanup]
    public void Cleanup() => BenchmarkData.TryDelete(_path);

    [Benchmark]
    public long SequentialScan_Mapped() => SequentialScan(ChunkSourceKind.Mapped);

    [Benchmark]
    public long SequentialScan_RandomAccess() => SequentialScan(ChunkSourceKind.RandomAccess);

    [Benchmark]
    public long RandomAccess_Mapped() => RandomScan(ChunkSourceKind.Mapped);

    [Benchmark]
    public long RandomAccess_RandomAccess() => RandomScan(ChunkSourceKind.RandomAccess);

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
