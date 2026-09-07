// ============================================================================
// Cartograph
// File: BenchmarkData.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Helpers that generate on-disk artifacts of float-vector records and a random
// query vector for use by the BenchmarkDotNet benchmark classes.
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

using Cartograph.Format;

namespace Cartograph.Benchmarks;

/// <summary>
/// Helpers to generate on-disk artifacts of float-vector records for the benchmarks.
/// </summary>
internal static class BenchmarkData {
    /// <summary>
    /// Writes an artifact of <paramref name="recordCount" /> vectors of <paramref name="dimensions" /> floats.
    /// </summary>
    /// <param name="recordCount">Number of vector records to write.</param>
    /// <param name="dimensions">Number of float dimensions per vector.</param>
    /// <returns>The absolute path of the written artifact file.</returns>
    public static string WriteVectorArtifact(int recordCount, int dimensions) {
        string path = Path.Combine(Path.GetTempPath(), $"cartograph-bench-{Guid.NewGuid():N}.ctg");
        SegmentedArtifactWriter writer = new();
        SegmentBuilder segment = writer.AddSegment();

        float[] vector = new float[dimensions];
        byte[] bytes = new byte[dimensions * sizeof(float)];
        Random random = new(1234);
        for (int r = 0; r < recordCount; r++) {
            for (int d = 0; d < dimensions; d++) {
                vector[d] = (float)(random.NextDouble() * 2.0 - 1.0);
            }

            Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
            segment.AddRecord(bytes);
        }

        writer.Save(path);
        return path;
    }

    /// <summary>
    /// A random unit-ish query vector used by the cosine-similarity benchmark.
    /// </summary>
    /// <param name="dimensions">Number of float dimensions in the returned vector.</param>
    /// <returns>A float array of length <paramref name="dimensions" /> filled with random values in [-1, 1].</returns>
    public static float[] QueryVector(int dimensions) {
        float[] vector = new float[dimensions];
        Random random = new(42);
        for (int d = 0; d < dimensions; d++) {
            vector[d] = (float)(random.NextDouble() * 2.0 - 1.0);
        }

        return vector;
    }

    /// <summary>
    /// Attempts to delete the file at <paramref name="path" />, swallowing any <see cref="IOException" />.
    /// </summary>
    /// <param name="path">Absolute path of the file to delete.</param>
    public static void TryDelete(string path) {
        try {
            File.Delete(path);
        } catch (IOException) {
        }
    }
}