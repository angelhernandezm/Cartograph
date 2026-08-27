using BenchmarkDotNet.Attributes;
using Cartograph.Format;

namespace Cartograph.Benchmarks;

/// <summary>
/// The headline benchmark: artifact OPEN TIME as a function of file size.
/// </summary>
/// <remarks>
/// The claim under test is "build once, map instantly": mapped open should be flat (O(1)) across
/// file sizes because it only reads and validates the header and manifest, while the naive
/// <see cref="File.ReadAllBytes(string)"/> baseline is linear in file size and allocates a buffer
/// proportional to the whole file. Random-access open is also flat (it reads only metadata) but
/// pays no mapping cost.
/// </remarks>
[MemoryDiagnoser]
public class OpenTimeBenchmarks
{
    private string _path = string.Empty;

    /// <summary>Approximate artifact size, swept to show flat-vs-linear open cost.</summary>
    [Params(8, 64, 256)]
    public int SizeMiB { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        const int dimensions = 768;
        int bytesPerRecord = dimensions * sizeof(float);
        int recordCount = (int)((long)SizeMiB * 1024 * 1024 / bytesPerRecord);
        _path = BenchmarkData.WriteVectorArtifact(recordCount, dimensions);
    }

    [GlobalCleanup]
    public void Cleanup() => BenchmarkData.TryDelete(_path);

    [Benchmark(Baseline = true)]
    public long Baseline_ReadAllBytes()
    {
        byte[] bytes = File.ReadAllBytes(_path);
        return bytes.Length;
    }

    [Benchmark]
    public long Open_Mapped()
    {
        using Artifact artifact = Artifact.Open(_path, new ArtifactOpenOptions { ChunkSource = ChunkSourceKind.Mapped });
        return artifact.RecordCount;
    }

    [Benchmark]
    public long Open_RandomAccess()
    {
        using Artifact artifact = Artifact.Open(_path, new ArtifactOpenOptions { ChunkSource = ChunkSourceKind.RandomAccess });
        return artifact.RecordCount;
    }
}
