// ============================================================================
// Cartograph
// File: OpenTimeBenchmarks.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// BenchmarkDotNet benchmarks measuring artifact open time as a function of file
// size, comparing mapped and random-access opens against a ReadAllBytes baseline.
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

    /// <summary>
    /// Writes an artifact of the configured <see cref="SizeMiB"/> size to a temporary file.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        const int dimensions = 768;
        int bytesPerRecord = dimensions * sizeof(float);
        int recordCount = (int)((long)SizeMiB * 1024 * 1024 / bytesPerRecord);
        _path = BenchmarkData.WriteVectorArtifact(recordCount, dimensions);
    }

    /// <summary>Deletes the temporary artifact file created by <see cref="Setup"/>.</summary>
    [GlobalCleanup]
    public void Cleanup() => BenchmarkData.TryDelete(_path);

    /// <summary>
    /// Baseline benchmark: reads the entire artifact file into a managed byte array.
    /// </summary>
    /// <returns>The total number of bytes read.</returns>
    [Benchmark(Baseline = true)]
    public long Baseline_ReadAllBytes()
    {
        byte[] bytes = File.ReadAllBytes(_path);
        return bytes.Length;
    }

    /// <summary>
    /// Benchmark: opens the artifact with memory-mapped I/O and returns the record count.
    /// </summary>
    /// <returns>The number of records in the artifact.</returns>
    [Benchmark]
    public long Open_Mapped()
    {
        using Artifact artifact = Artifact.Open(_path, new ArtifactOpenOptions { ChunkSource = ChunkSourceKind.Mapped });
        return artifact.RecordCount;
    }

    /// <summary>
    /// Benchmark: opens the artifact with random-access I/O and returns the record count.
    /// </summary>
    /// <returns>The number of records in the artifact.</returns>
    [Benchmark]
    public long Open_RandomAccess()
    {
        using Artifact artifact = Artifact.Open(_path, new ArtifactOpenOptions { ChunkSource = ChunkSourceKind.RandomAccess });
        return artifact.RecordCount;
    }
}
