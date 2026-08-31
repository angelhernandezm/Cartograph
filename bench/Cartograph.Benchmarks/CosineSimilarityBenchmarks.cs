// ============================================================================
// Cartograph
// File: CosineSimilarityBenchmarks.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// BenchmarkDotNet benchmarks measuring cosine-similarity scan throughput over
// memory-mapped artifact records versus a managed heap-array baseline.
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
    /// <summary>Number of float dimensions per vector record.</summary>
    private const int Dimensions = 768;

    private string _path = string.Empty;
    private float[] _query = [];
    private float[][] _managed = [];

    /// <summary>Gets or sets the number of vector records in the artifact.</summary>
    [Params(50_000)]
    public int RecordCount { get; set; }

    /// <summary>
    /// Writes the artifact to disk and materializes the managed baseline array.
    /// </summary>
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

    /// <summary>Deletes the temporary artifact file created by <see cref="Setup"/>.</summary>
    [GlobalCleanup]
    public void Cleanup() => BenchmarkData.TryDelete(_path);

    /// <summary>
    /// Baseline benchmark: iterates all vectors held as managed <see cref="float"/> arrays
    /// and returns the highest cosine-similarity score against the query vector.
    /// </summary>
    /// <returns>The best cosine-similarity score found in the corpus.</returns>
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

    /// <summary>
    /// Benchmark: opens the artifact with mapped I/O, casts each record's bytes to
    /// <see cref="float"/> in-place, and returns the best cosine-similarity score.
    /// </summary>
    /// <returns>The best cosine-similarity score found in the corpus.</returns>
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
