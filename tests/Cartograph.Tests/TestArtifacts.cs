using Cartograph.Format;

namespace Cartograph.Tests;

/// <summary>Shared helpers for building temporary artifacts on disk.</summary>
internal static class TestArtifacts
{
    public static string NewTempPath() => Path.Combine(Path.GetTempPath(), $"cartograph-{Guid.NewGuid():N}.ctg");

    /// <summary>Writes a single-segment artifact with the given records and returns its path.</summary>
    public static string WriteSingleSegment(IReadOnlyList<byte[]> records)
    {
        string path = NewTempPath();
        SegmentedArtifactWriter writer = new();
        SegmentBuilder segment = writer.AddSegment();
        foreach (byte[] record in records)
        {
            segment.AddRecord(record);
        }

        writer.Save(path);
        return path;
    }

    public static byte[] Pattern(int length, byte seed)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            data[i] = (byte)(seed + i);
        }

        return data;
    }
}

/// <summary>A temp file that deletes itself on dispose.</summary>
internal sealed class TempFile : IDisposable
{
    public TempFile(string path) => Path = path;

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}
