// ============================================================================
// Cartograph
// File: DemoTree.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Generates a synthetic folder tree of varied file types and sizes so the
// harness can be exercised end to end without pointing it at real data.
// Deliberately kept byte-for-byte identical to the copy in Cartograph.Harness,
// including its seed, so both apps profile against exactly the same input
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

using System.Globalization;
using System.Text;

namespace Cartograph.Baseline;

/// <summary>
/// Creates a disposable folder tree populated with synthetic content
/// </summary>
/// <remarks>
/// The generated tree deliberately mixes text and binary payloads, several nesting depths, a wide
/// spread of file sizes and a few awkward cases such as an empty file and a file without an
/// extension. That variety is what makes the round-trip meaningful: it exercises record alignment,
/// multi-page records and the zero-length edge case rather than just the happy path.
/// </remarks>
internal sealed class DemoTree : IDisposable
{
    /// <summary>
    /// Deterministic seed so that repeated demo runs produce identical trees
    /// </summary>
    private const int Seed = 20260830;

    /// <summary>
    /// Nested directories that the generated files are distributed across
    /// </summary>
    private static readonly string[] Folders =
    [
        "docs",
        "docs/reference",
        "src/core",
        "src/core/internal",
        "src/adapters",
        "data/vectors",
        "data/raw",
        "config",
    ];

    /// <summary>
    /// Extensions assigned to the generated files, driving the extension grouping mode
    /// </summary>
    private static readonly string[] Extensions = [".txt", ".md", ".json", ".csv", ".bin", ".log", ".cs"];

    /// <summary>
    /// Whether the generated tree is deleted when this instance is disposed
    /// </summary>
    private readonly bool _cleanup;

    /// <summary>
    /// Whether <see cref="Dispose" /> has already run
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="DemoTree" /> class
    /// </summary>
    /// <param name="root">Fully qualified path of the generated tree</param>
    /// <param name="cleanup">Whether the tree is deleted on disposal</param>
    private DemoTree(string root, bool cleanup)
    {
        Root = root;
        _cleanup = cleanup;
    }

    /// <summary>
    /// Gets the fully qualified path of the generated tree
    /// </summary>
    /// <value>A directory beneath the system temporary folder.</value>
    public string Root { get; }

    /// <summary>
    /// Gets the number of files that were generated
    /// </summary>
    /// <value>Matches the requested file count.</value>
    public int FileCount { get; private init; }

    /// <summary>
    /// Gets the total number of bytes that were written
    /// </summary>
    /// <value>The sum of every generated file's length.</value>
    public long TotalBytes { get; private init; }

    /// <summary>
    /// Generates a new demo tree beneath the system temporary folder
    /// </summary>
    /// <param name="fileCount">Number of files to generate</param>
    /// <param name="cleanup">Whether the tree is deleted when the returned instance is disposed</param>
    /// <returns>A handle to the generated tree</returns>
    /// <exception cref="System.ArgumentOutOfRangeException"><paramref name="fileCount" /> is less than one.</exception>
    public static DemoTree Create(int fileCount, bool cleanup)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fileCount, 1);

        string root = Path.Combine(
            Path.GetTempPath(),
            "cartograph-harness-demo-" + Guid.NewGuid().ToString("N")[..12]);

        Directory.CreateDirectory(root);

        foreach (string folder in Folders)
        {
            Directory.CreateDirectory(Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar)));
        }

        Random random = new(Seed);
        long total = 0;

        for (int i = 0; i < fileCount; i++)
        {
            string folder = Folders[i % Folders.Length];
            string extension = Extensions[i % Extensions.Length];
            string name = string.Format(CultureInfo.InvariantCulture, "item-{0:D4}{1}", i, extension);
            string path = Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar), name);

            byte[] content = GenerateContent(random, extension, i);
            File.WriteAllBytes(path, content);
            total += content.Length;
        }

        // A handful of deliberately awkward cases the happy path would never produce.
        total += WriteExtra(root, "README", BuildReadme(fileCount));
        total += WriteExtra(root, Path.Combine("config", "empty.txt"), []);
        total += WriteExtra(root, Path.Combine("data", "raw", "large.bin"), GenerateBinary(new Random(Seed + 1), 1_500_000));

        return new DemoTree(root, cleanup)
        {
            FileCount = fileCount + 3,
            TotalBytes = total,
        };
    }

    /// <summary>
    /// Deletes the generated tree unless it was created with cleanup disabled
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (!_cleanup)
        {
            return;
        }

        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ConsoleReport.Warn($"could not delete the demo tree at '{Root}': {ex.Message}");
        }
    }

    /// <summary>
    /// Writes one of the fixed extra files and returns its length
    /// </summary>
    /// <param name="root">Root of the generated tree</param>
    /// <param name="relativePath">Path of the file relative to <paramref name="root" /></param>
    /// <param name="content">Bytes to write</param>
    /// <returns>The number of bytes written</returns>
    private static long WriteExtra(string root, string relativePath, byte[] content)
    {
        string path = Path.Combine(root, relativePath);
        string? directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(path, content);
        return content.Length;
    }

    /// <summary>
    /// Produces content appropriate to a file's extension
    /// </summary>
    /// <param name="random">Source of randomness, seeded for reproducibility</param>
    /// <param name="extension">Extension of the file being generated</param>
    /// <param name="index">Ordinal of the file, embedded in the generated content</param>
    /// <returns>The generated file contents</returns>
    private static byte[] GenerateContent(Random random, string extension, int index) => extension switch
    {
        ".bin" => GenerateBinary(random, random.Next(512, 96 * 1024)),
        ".json" => Encoding.UTF8.GetBytes(GenerateJson(random, index)),
        ".csv" => Encoding.UTF8.GetBytes(GenerateCsv(random, index)),
        ".md" => Encoding.UTF8.GetBytes(GenerateMarkdown(index)),
        ".cs" => Encoding.UTF8.GetBytes(GenerateSource(index)),
        _ => Encoding.UTF8.GetBytes(GenerateText(random, index)),
    };

    /// <summary>
    /// Produces a buffer of pseudo-random bytes
    /// </summary>
    /// <param name="random">Source of randomness, seeded for reproducibility</param>
    /// <param name="length">Number of bytes to produce</param>
    /// <returns>The generated buffer</returns>
    private static byte[] GenerateBinary(Random random, int length)
    {
        byte[] buffer = new byte[length];
        random.NextBytes(buffer);
        return buffer;
    }

    /// <summary>
    /// Produces a small JSON document
    /// </summary>
    /// <param name="random">Source of randomness, seeded for reproducibility</param>
    /// <param name="index">Ordinal of the file, used as the document identifier</param>
    /// <returns>The generated JSON text</returns>
    private static string GenerateJson(Random random, int index)
    {
        StringBuilder builder = new();

        builder.Append(CultureInfo.InvariantCulture, $"{{\n  \"id\": {index},\n  \"vector\": [");

        int dimensions = random.Next(8, 64);

        for (int i = 0; i < dimensions; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(CultureInfo.InvariantCulture, $"{random.NextDouble():0.####}");
        }

        builder.Append("],\n  \"tags\": [\"demo\", \"cartograph\", \"harness\"]\n}\n");
        return builder.ToString();
    }

    /// <summary>
    /// Produces a small comma separated table
    /// </summary>
    /// <param name="random">Source of randomness, seeded for reproducibility</param>
    /// <param name="index">Ordinal of the file, embedded in each row</param>
    /// <returns>The generated CSV text</returns>
    private static string GenerateCsv(Random random, int index)
    {
        StringBuilder builder = new();
        builder.Append("id,name,score,timestamp\n");

        int rows = random.Next(4, 200);

        for (int i = 0; i < rows; i++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{index}-{i},row-{i},{random.NextDouble():0.#####},2026-08-30T12:00:00Z\n");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Produces a short markdown document
    /// </summary>
    /// <param name="index">Ordinal of the file, used in the document title</param>
    /// <returns>The generated markdown text</returns>
    private static string GenerateMarkdown(int index) =>
        $"""
        # Demo document {index}

        This file exists so the Cartograph harness has something to pack.

        - Records are opaque bytes to Cartograph.
        - Their meaning lives in the harness catalog.
        - Every record is padded to a 64-byte boundary.

        Generated for demonstration purposes only.

        """;

    /// <summary>
    /// Produces a short, syntactically plausible C# source file
    /// </summary>
    /// <param name="index">Ordinal of the file, used in the generated type name</param>
    /// <returns>The generated source text</returns>
    private static string GenerateSource(int index) =>
        $$"""
        namespace Demo.Generated;

        /// <summary>
        /// Placeholder type number {{index}} generated by the Cartograph harness
        /// </summary>
        internal sealed class Placeholder{{index}}
        {
            /// <summary>
            /// Gets the ordinal of this placeholder
            /// </summary>
            /// <value>The value {{index}}.</value>
            public int Ordinal => {{index}};
        }

        """;

    /// <summary>
    /// Produces a block of plain text of random length
    /// </summary>
    /// <param name="random">Source of randomness, seeded for reproducibility</param>
    /// <param name="index">Ordinal of the file, embedded in the first line</param>
    /// <returns>The generated text</returns>
    private static string GenerateText(Random random, int index)
    {
        StringBuilder builder = new();
        builder.Append(CultureInfo.InvariantCulture, $"Cartograph harness sample {index}\n\n");

        int lines = random.Next(3, 400);

        for (int i = 0; i < lines; i++)
        {
            builder.Append(CultureInfo.InvariantCulture,
                $"line {i:D4}: the artifact is immutable once written, so readers never coordinate.\n");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Produces the extensionless README placed at the root of the generated tree
    /// </summary>
    /// <param name="fileCount">Number of generated files, mentioned in the text</param>
    /// <returns>The README contents</returns>
    private static byte[] BuildReadme(int fileCount) => Encoding.UTF8.GetBytes(
        $"""
        Cartograph harness demo tree
        ============================

        {fileCount} generated files plus this README, an empty file and one large
        binary file, spread across nested folders and several extensions.

        Everything here is synthetic and safe to delete.

        """);
}
