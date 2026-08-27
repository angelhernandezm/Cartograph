using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Cartograph.Format;

namespace Cartograph.Benchmarks;

/// <summary>
/// A realistic vector-search inner loop: <see cref="TensorPrimitives.CosineSimilarity(ReadOnlySpan{float}, ReadOnlySpan{float})"/>
/// over <c>MemoryMarshal.Cast&lt;byte, float&gt;</c> of mapped pages.
/// </summary>
/// <remarks>
/// This is NOT an ANN index (Cartograph deliberately does not build one). It measures the cost of a
/// brute-force scan directly over the artifact: mapped records are cast to <see cref="float"/> spans
/// in place with no copy into the managed heap, so the scan runs with zero steady-state managed
/// allocation proportional to the corpus. The managed baseline holds every vector as a
/// <see cref="float"/> array in memory, showing the working-set difference.
/// </remarks>
[MemoryDiagnoser]
public class CosineSimilarityBenchmarks
{
    private const int Dimensions = 768;

    private string _path = string.Empty;
    private float[] _query = [];
    private float[][] _managed = [];

    [Params(50_000)]
    public int RecordCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _path = BenchmarkData.WriteVectorArtifact(RecordCount, Dimensions);
        _query = BenchmarkData.QueryVector(Dimensions);

        // Managed baseline: fully materialize every vector on the heap.
        _managed = new float[RecordCount][];
        using Artifact artifact = Artifact.Open(_path, new ArtifactOpenOptions { VerifyChecksums = false });
        ArtifactSegment segment = artifact.Segments[0];
        for (int i = 0; i < RecordCount; i++)
        {
            using RecordLease lease = segment.ReadRecord(i);
            _managed[i] = MemoryMarshal.Cast<byte, float>(lease.FirstSpan).ToArray();
        }
    }

    [GlobalCleanup]
    public void Cleanup() => BenchmarkData.TryDelete(_path);

    [Benchmark(Baseline = true)]
    public float Managed_HeapVectors()
    {
        float best = float.MinValue;
        foreach (float[] vector in _managed)
        {
            float score = TensorPrimitives.CosineSimilarity(vector, _query);
            if (score > best)
            {
                best = score;
            }
        }

        return best;
    }

    [Benchmark]
    public float Mapped_CastInPlace()
    {
        using Artifact artifact = Artifact.Open(_path, new ArtifactOpenOptions { ChunkSource = ChunkSourceKind.Mapped, VerifyChecksums = false });
        ArtifactSegment segment = artifact.Segments[0];
        float best = float.MinValue;
        for (int i = 0; i < RecordCount; i++)
        {
            using RecordLease lease = segment.ReadRecord(i);
            ReadOnlySpan<float> vector = MemoryMarshal.Cast<byte, float>(lease.FirstSpan);
            float score = TensorPrimitives.CosineSimilarity(vector, _query);
            if (score > best)
            {
                best = score;
            }
        }

        return best;
    }
}
