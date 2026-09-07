// ============================================================================
// Cartograph
// File: CatalogTests.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Tests covering the file-catalog convention: binary round-trip of the
// manifest, recovery from record zero of a real artifact, and the extract and
// verify paths the explorer front ends depend on
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

using System.Buffers;
using System.IO.Hashing;
using Cartograph.Catalog;
using Cartograph.Format;
using Xunit;

namespace Cartograph.Tests;

/// <summary>Tests for <see cref="FileCatalog"/> and <see cref="CatalogedArtifact"/>.</summary>
public sealed class CatalogTests
{
    /// <summary>Payload of the single small file packed by the fixture artifact.</summary>
    private static readonly byte[] SmallPayload = "the quick brown fox\n"u8.ToArray();

    /// <summary>Length of the multi-record file packed by the fixture artifact.</summary>
    private const int LargeLength = 5000;

    /// <summary>Bytes per record used when splitting the multi-record file.</summary>
    private const int PieceSize = 2048;

    /// <summary>A serialized catalog survives a round-trip through its binary form.</summary>
    [Fact]
    public void SerializeRoundTripsEveryField()
    {
        FileCatalog original = new()
        {
            SourceRoot = @"C:\some\root",
            CreatedUtc = new DateTime(2026, 9, 6, 11, 19, 24, DateTimeKind.Utc),
            GroupingMode = "extension",
            GroupNames = ["one", "two"],
            Entries =
            [
                new CatalogEntry
                {
                    RelativePath = "a/b/c.txt",
                    Length = 1234,
                    RecordCount = 2,
                    LastWriteUtcTicks = 638_000_000_000_000_000,
                    Checksum = 0xDEADBEEFCAFEF00D,
                    SegmentIndex = 1,
                    RecordIndex = 3,
                    GlobalIndex = 9,
                },
            ],
        };

        FileCatalog restored = FileCatalog.Deserialize(new ReadOnlySequence<byte>(original.Serialize()));

        Assert.Equal(original.SourceRoot, restored.SourceRoot);
        Assert.Equal(original.CreatedUtc, restored.CreatedUtc);
        Assert.Equal(original.GroupingMode, restored.GroupingMode);
        Assert.Equal(original.GroupNames, restored.GroupNames);
        Assert.Equal(original.TotalBytes, restored.TotalBytes);

        CatalogEntry expected = original.Entries[0];
        CatalogEntry actual = restored.Entries[0];

        Assert.Equal(expected.RelativePath, actual.RelativePath);
        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(expected.RecordCount, actual.RecordCount);
        Assert.Equal(expected.LastWriteUtcTicks, actual.LastWriteUtcTicks);
        Assert.Equal(expected.Checksum, actual.Checksum);
        Assert.Equal(expected.SegmentIndex, actual.SegmentIndex);
        Assert.Equal(expected.RecordIndex, actual.RecordIndex);
        Assert.Equal(expected.GlobalIndex, actual.GlobalIndex);
    }

    /// <summary>A record that is not a catalog is rejected rather than misread.</summary>
    [Fact]
    public void DeserializeRejectsForeignRecords()
    {
        ReadOnlySequence<byte> notACatalog = new("this is not a catalog"u8.ToArray());

        Assert.Throws<InvalidDataException>(() => FileCatalog.Deserialize(notACatalog));
    }

    /// <summary>Path decomposition of an entry matches the forward-slash convention.</summary>
    [Fact]
    public void EntryDecomposesItsRelativePath()
    {
        CatalogEntry nested = NewEntry("src/Cartograph/MappedFile.cs");
        CatalogEntry root = NewEntry("LICENSE");

        Assert.Equal("MappedFile.cs", nested.Name);
        Assert.Equal("src/Cartograph", nested.Directory);
        Assert.Equal(".cs", nested.Extension);

        Assert.Equal("LICENSE", root.Name);
        Assert.Equal(string.Empty, root.Directory);
        Assert.Equal(string.Empty, root.Extension);
    }

    /// <summary>Opening an artifact recovers its catalog from record zero.</summary>
    [Fact]
    public void OpenRecoversTheCatalog()
    {
        using TempFile artifact = new(WriteFixture());
        using CatalogedArtifact cataloged = CatalogedArtifact.Open(artifact.Path);

        Assert.Equal(2, cataloged.Entries.Count);
        Assert.Equal(2, cataloged.SegmentCount);
        Assert.Equal("flat", cataloged.Catalog.GroupingMode);
        Assert.NotNull(cataloged.Find("small.txt"));
        Assert.Null(cataloged.Find("missing.txt"));
    }

    /// <summary>An artifact without a catalog record fails to open with a clear error.</summary>
    [Fact]
    public void OpenRejectsAnArtifactWithoutACatalog()
    {
        using TempFile artifact = new(TestArtifacts.WriteSingleSegment([TestArtifacts.Pattern(64, 1)]));

        Assert.Throws<InvalidDataException>(() => CatalogedArtifact.Open(artifact.Path));
    }

    /// <summary>Extracting a file that spans several records reproduces it byte for byte.</summary>
    [Fact]
    public void ExtractReassemblesAMultiRecordFile()
    {
        using TempFile artifact = new(WriteFixture());
        using CatalogedArtifact cataloged = CatalogedArtifact.Open(artifact.Path);

        CatalogEntry entry = cataloged.Find("large.bin")!;

        Assert.Equal(3, entry.RecordCount);

        string destination = Path.Combine(Path.GetTempPath(), $"cartograph-{Guid.NewGuid():N}.bin");
        using TempFile extracted = new(destination);

        long written = cataloged.ExtractTo(entry, destination);

        Assert.Equal(LargeLength, written);
        Assert.Equal(TestArtifacts.Pattern(LargeLength, 7), File.ReadAllBytes(destination));
    }

    /// <summary>A bounded preview never reads more than it was asked for.</summary>
    [Fact]
    public void ReadPrefixIsBounded()
    {
        using TempFile artifact = new(WriteFixture());
        using CatalogedArtifact cataloged = CatalogedArtifact.Open(artifact.Path);

        CatalogEntry entry = cataloged.Find("large.bin")!;
        byte[] prefix = cataloged.ReadPrefix(entry, 100);

        Assert.Equal(100, prefix.Length);
        Assert.Equal(TestArtifacts.Pattern(LargeLength, 7).AsSpan(0, 100).ToArray(), prefix);
    }

    /// <summary>Verification recomputes the whole-file hash recorded at pack time.</summary>
    [Fact]
    public void VerifyMatchesTheRecordedChecksum()
    {
        using TempFile artifact = new(WriteFixture());
        using CatalogedArtifact cataloged = CatalogedArtifact.Open(artifact.Path);

        CatalogVerification result = cataloged.Verify(cataloged.Find("large.bin")!);

        Assert.True(result.HasExpectedChecksum);
        Assert.True(result.Matches);
        Assert.Equal(LargeLength, result.BytesRead);
    }

    /// <summary>Builds a small artifact carrying a catalog, mirroring what the packer writes.</summary>
    /// <returns>The path of the artifact that was written.</returns>
    private static string WriteFixture()
    {
        byte[] large = TestArtifacts.Pattern(LargeLength, 7);

        List<CatalogEntry> entries =
        [
            new CatalogEntry
            {
                RelativePath = "small.txt",
                Length = SmallPayload.Length,
                RecordCount = 1,
                LastWriteUtcTicks = DateTime.UtcNow.Ticks,
                Checksum = XxHash3.HashToUInt64(SmallPayload),
                SegmentIndex = 1,
                RecordIndex = 0,
                GlobalIndex = 1,
            },
            new CatalogEntry
            {
                RelativePath = "large.bin",
                Length = large.Length,
                RecordCount = 3,
                LastWriteUtcTicks = DateTime.UtcNow.Ticks,
                Checksum = XxHash3.HashToUInt64(large),
                SegmentIndex = 1,
                RecordIndex = 1,
                GlobalIndex = 2,
            },
        ];

        FileCatalog catalog = new()
        {
            SourceRoot = Path.GetTempPath(),
            CreatedUtc = DateTime.UtcNow,
            GroupingMode = "flat",
            GroupNames = ["(all files)"],
            Entries = entries,
        };

        SegmentedArtifactWriter writer = new();

        writer.AddSegment(0u).AddRecord(catalog.Serialize());

        SegmentBuilder content = writer.AddSegment(1u);
        content.AddRecord(SmallPayload);

        for (int offset = 0; offset < large.Length; offset += PieceSize)
        {
            content.AddRecord(large.AsSpan(offset, Math.Min(PieceSize, large.Length - offset)));
        }

        string path = TestArtifacts.NewTempPath();
        writer.Save(path);

        return path;
    }

    /// <summary>Builds an entry that carries nothing but a relative path.</summary>
    /// <param name="relativePath">Path to place on the entry.</param>
    /// <returns>A catalog entry whose other fields are zero.</returns>
    private static CatalogEntry NewEntry(string relativePath) => new()
    {
        RelativePath = relativePath,
        Length = 0,
        RecordCount = 1,
        LastWriteUtcTicks = 0,
        Checksum = 0,
        SegmentIndex = 1,
        RecordIndex = 0,
        GlobalIndex = 1,
    };
}
