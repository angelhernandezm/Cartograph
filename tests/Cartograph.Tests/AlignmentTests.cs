// ============================================================================
// Cartograph
// File: AlignmentTests.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Tests that verify memory-alignment guarantees for record payloads, including
// MemoryMarshal float round-trips and minimum 4-byte address alignment.
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

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Cartograph.Format;
using Xunit;

namespace Cartograph.Tests;

/// <summary>
/// Tests that verify memory-alignment guarantees for record payloads.
/// </summary>
public class AlignmentTests
{
    /// <summary>
    /// Verifies that a float array written to an artifact as raw bytes round-trips
    /// correctly when read back and cast via <c>MemoryMarshal.Cast&lt;byte, float&gt;</c>.
    /// </summary>
    [Fact]
    public void FloatRecord_RoundTripsThroughMemoryMarshalCast()
    {
        float[] vector = new float[512];
        for (int i = 0; i < vector.Length; i++)
        {
            vector[i] = MathF.Sin(i) * 3.5f + i;
        }

        byte[] payload = MemoryMarshal.AsBytes<float>(vector).ToArray();
        string path = TestArtifacts.WriteSingleSegment([payload]);
        using TempFile temp = new(path);

        using Artifact artifact = Artifact.Open(path);
        using RecordLease lease = artifact.ReadRecord(0);

        Assert.True(lease.IsSingleSegment);
        ReadOnlySpan<float> readBack = MemoryMarshal.Cast<byte, float>(lease.FirstSpan);
        Assert.Equal(vector.Length, readBack.Length);
        for (int i = 0; i < vector.Length; i++)
        {
            Assert.Equal(vector[i], readBack[i]);
        }
    }

    /// <summary>
    /// Verifies that the first record payload's address satisfies at least 4-byte alignment,
    /// which is required for safe <c>MemoryMarshal.Cast&lt;byte, float&gt;</c> to <see cref="float"/>.
    /// </summary>
    [Fact]
    public unsafe void FirstRecordPayload_IsAtLeast4ByteAligned()
    {
        byte[] payload = TestArtifacts.Pattern(2048, 11);
        string path = TestArtifacts.WriteSingleSegment([payload]);
        using TempFile temp = new(path);

        using Artifact artifact = Artifact.Open(path);
        using RecordLease lease = artifact.ReadRecord(0);

        ref readonly byte first = ref MemoryMarshal.GetReference(lease.FirstSpan);
        nint address = (nint)Unsafe.AsPointer(ref Unsafe.AsRef(in first));

        // Explicit layout aligns record payloads to 64 bytes; the mapped page base is page-aligned,
        // so the observed address must satisfy the 4-byte requirement of MemoryMarshal.Cast<byte,float>.
        Assert.Equal(0, (int)(address % 4));
        Assert.Equal(0, (int)(address % ArtifactFormat.Alignment));
    }
}
