// ============================================================================
// Cartograph
// File: TestArtifacts.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Shared test helpers for writing temporary artifacts to disk and generating
// deterministic byte patterns; includes a TempFile RAII wrapper for cleanup.
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

namespace Cartograph.Tests;

/// <summary>Shared helpers for building temporary artifacts on disk.</summary>
internal static class TestArtifacts
{
    /// <summary>
    /// Returns a new unique temporary file path with the <c>.ctg</c> extension.
    /// </summary>
    /// <returns>An absolute path to a non-existent temporary file.</returns>
    public static string NewTempPath() => Path.Combine(Path.GetTempPath(), $"cartograph-{Guid.NewGuid():N}.ctg");

    /// <summary>Writes a single-segment artifact with the given records and returns its path.</summary>
    /// <param name="records">The records to place, in order, into the artifact's only segment.</param>
    /// <returns>An absolute path to the newly written artifact.</returns>
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

    /// <summary>
    /// Generates a deterministic byte array of <paramref name="length"/> bytes
    /// where each byte equals <c>(byte)(seed + index)</c>.
    /// </summary>
    /// <param name="length">Number of bytes to generate.</param>
    /// <param name="seed">Starting byte value added to each index.</param>
    /// <returns>A new byte array filled with the deterministic pattern.</returns>
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
    /// <summary>
    /// Initializes a new instance of the <see cref="TempFile" /> class.
    /// </summary>
    /// <param name="path">Absolute path of the temporary file to track.</param>
    public TempFile(string path) => Path = path;

    /// <summary>Gets the absolute path of the tracked temporary file.</summary>
    /// <value>The absolute path of the tracked temporary file.</value>
    public string Path { get; }

    /// <summary>
    /// Deletes the tracked file if it exists. Swallows <see cref="IOException"/>.
    /// </summary>
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
