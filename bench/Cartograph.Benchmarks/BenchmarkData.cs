using Cartograph.Format;

namespace Cartograph.Benchmarks;

/// <summary>Helpers to generate on-disk artifacts of float-vector records for the benchmarks.</summary>
internal static class BenchmarkData
{
    /// <summary>Writes an artifact of <paramref name="recordCount"/> vectors of <paramref name="dimensions"/> floats.</summary>
    public static string WriteVectorArtifact(int recordCount, int dimensions)
    {
        string path = Path.Combine(Path.GetTempPath(), $"cartograph-bench-{Guid.NewGuid():N}.ctg");
        SegmentedArtifactWriter writer = new();
        SegmentBuilder segment = writer.AddSegment();

        float[] vector = new float[dimensions];
        byte[] bytes = new byte[dimensions * sizeof(float)];
        Random random = new(1234);
        for (int r = 0; r < recordCount; r++)
        {
            for (int d = 0; d < dimensions; d++)
            {
                vector[d] = (float)(random.NextDouble() * 2.0 - 1.0);
            }

            Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
            segment.AddRecord(bytes);
        }

        writer.Save(path);
        return path;
    }

    /// <summary>A random unit-ish query vector used by the cosine-similarity benchmark.</summary>
    public static float[] QueryVector(int dimensions)
    {
        float[] vector = new float[dimensions];
        Random random = new(42);
        for (int d = 0; d < dimensions; d++)
        {
            vector[d] = (float)(random.NextDouble() * 2.0 - 1.0);
        }

        return vector;
    }

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
