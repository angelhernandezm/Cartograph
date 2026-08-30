// ============================================================================
// Cartograph
// File: CorruptionTests.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Tests that verify the artifact reader raises clean exceptions when encountering
// corrupted files, including bad magic bytes, truncation, and payload checksum failures.
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
using Xunit;

namespace Cartograph.Tests;

/// <summary>
/// Tests that verify the artifact reader raises clean exceptions for corrupted files.
/// </summary>
public class CorruptionTests
{
    /// <summary>
    /// Verifies that opening an artifact with a flipped magic byte throws a
    /// <see cref="CartographFormatException"/> cleanly.
    /// </summary>
    [Fact]
    public void BadMagic_OnOpen_ThrowsCleanly()
    {
        string path = TestArtifacts.WriteSingleSegment([TestArtifacts.Pattern(100, 1)]);
        using TempFile temp = new(path);

        byte[] bytes = File.ReadAllBytes(path);
        bytes[0] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        Assert.Throws<CartographFormatException>(() => Artifact.Open(path));
    }

    /// <summary>
    /// Verifies that opening an artifact truncated at the tail (missing the manifest)
    /// throws a <see cref="CartographFormatException"/> cleanly.
    /// </summary>
    [Fact]
    public void TruncatedFile_ThrowsCleanly()
    {
        string path = TestArtifacts.WriteSingleSegment([TestArtifacts.Pattern(4096, 2)]);
        using TempFile temp = new(path);

        byte[] bytes = File.ReadAllBytes(path);
        // Drop the tail (including the manifest) to simulate a truncated write.
        File.WriteAllBytes(path, bytes[..(bytes.Length - 128)]);

        Assert.Throws<CartographFormatException>(() => Artifact.Open(path));
    }

    /// <summary>
    /// Verifies that a flipped byte in the record payload region causes
    /// <see cref="Artifact.ReadRecord"/> to throw a <see cref="CartographFormatException"/>
    /// with a message mentioning checksum.
    /// </summary>
    [Fact]
    public void CorruptRecordPayload_FailsChecksum()
    {
        // A single-record, single-segment artifact places the first payload at offset 128.
        byte[] record = TestArtifacts.Pattern(4096, 0xAB);
        string path = TestArtifacts.WriteSingleSegment([record]);
        using TempFile temp = new(path);

        byte[] bytes = File.ReadAllBytes(path);
        bytes[150] ^= 0x01; // inside the payload region
        File.WriteAllBytes(path, bytes);

        using Artifact artifact = Artifact.Open(path);
        CartographFormatException ex = Assert.Throws<CartographFormatException>(() => artifact.ReadRecord(0));
        Assert.Contains("checksum", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies that reading a corrupted record does not throw when checksum
    /// verification is disabled via <see cref="ArtifactOpenOptions.VerifyChecksums"/>.
    /// </summary>
    [Fact]
    public void ChecksumVerificationDisabled_DoesNotThrow()
    {
        byte[] record = TestArtifacts.Pattern(4096, 0xAB);
        string path = TestArtifacts.WriteSingleSegment([record]);
        using TempFile temp = new(path);

        byte[] bytes = File.ReadAllBytes(path);
        bytes[150] ^= 0x01;
        File.WriteAllBytes(path, bytes);

        using Artifact artifact = Artifact.Open(path, new ArtifactOpenOptions { VerifyChecksums = false });
        using RecordLease lease = artifact.ReadRecord(0);
        Assert.Equal(4096, lease.Length); // reads the (corrupt) bytes without validation
    }
}
