// ============================================================================
// Cartograph
// File: ViewLeaseTests.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Tests that verify ViewLease reference-counting semantics: use-after-dispose,
// double-dispose safety, and owner disposal while a lease is still outstanding.
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

using System.IO.MemoryMappedFiles;
using Cartograph;
using Xunit;

namespace Cartograph.Tests;

/// <summary>
/// Tests that verify <see cref="ViewLease"/> reference-counting and lifetime semantics.
/// </summary>
public class ViewLeaseTests {
    /// <summary>
    /// Verifies that accessing <see cref="ViewLease.Memory"/> or <see cref="ViewLease.Span"/>
    /// after the lease has been disposed throws <see cref="ObjectDisposedException"/>.
    /// </summary>
    [Fact]
    public void UseAfterDispose_Throws() {
        using LeaseFixture fixture = new(256);
        ViewLease lease = fixture.Segment.Lease();
        Assert.Equal(fixture.Expected[0], lease.Span[0]);

        lease.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = lease.Memory);
        Assert.Throws<ObjectDisposedException>(() => _ = lease.Span.Length);
    }

    /// <summary>
    /// Verifies that calling <see cref="ViewLease.Dispose"/> twice does not throw.
    /// </summary>
    [Fact]
    public void DoubleDispose_IsSafe() {
        using LeaseFixture fixture = new(128);
        ViewLease lease = fixture.Segment.Lease();
        lease.Dispose();
        lease.Dispose(); // must not throw
    }

    /// <summary>
    /// Verifies that disposing the owning <see cref="MappedSegment"/> while a <see cref="ViewLease"/>
    /// is still held keeps the mapped memory alive until the lease is released, and that new leases
    /// are refused once the owner is gone.
    /// </summary>
    [Fact]
    public void OwnerDisposeWhileLeaseHeld_KeepsMemoryAlive() {
        LeaseFixture fixture = new(512);
        ViewLease lease = fixture.Segment.Lease();

        // Retire the owner reference while a lease is still outstanding.
        fixture.Segment.Dispose();

        // Memory must remain valid (refcount > 0), not a freed-page read.
        Assert.Equal(fixture.Expected, lease.Span.ToArray());

        // New leases are refused once the owner is gone.
        Assert.Throws<ObjectDisposedException>(() => fixture.Segment.Lease());

        // Releasing the last lease unmaps; further leasing still throws.
        lease.Dispose();
        Assert.Throws<ObjectDisposedException>(() => fixture.Segment.Lease());

        fixture.DisposeFileOnly();
    }

    /// <summary>
    /// A test fixture that creates a temporary memory-mapped file and a <see cref="MappedSegment"/>
    /// over it, cleaning up both on dispose.
    /// </summary>
    private sealed class LeaseFixture : IDisposable {
        /// <summary>Path to the temporary file backing the mapping, deleted on disposal.</summary>
        private readonly string _path;

        /// <summary>The mapping opened over <see cref="_path"/>.</summary>
        private readonly MemoryMappedFile _mmf;

        /// <summary>
        /// Initializes a new instance of the <see cref="LeaseFixture" /> class.
        /// </summary>
        /// <param name="length">Number of bytes to write and map.</param>
        public LeaseFixture(int length) {
            Expected = TestArtifacts.Pattern(length, 5);
            _path = TestArtifacts.NewTempPath();
            File.WriteAllBytes(_path, Expected);
            _mmf = MemoryMappedFile.CreateFromFile(_path, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
            Segment = new MappedSegment(_mmf, 0, length, MemoryMappedFileAccess.Read);
        }

        /// <summary>Gets the expected byte content written to the mapped file.</summary>
        /// <value>The expected byte content written to the mapped file.</value>
        public byte[] Expected {
            get;
        }

        /// <summary>Gets the <see cref="MappedSegment"/> wrapping the mapped file.</summary>
        /// <value>The <see cref="MappedSegment"/> wrapping the mapped file.</value>
        public MappedSegment Segment {
            get;
        }

        /// <summary>
        /// Disposes only the underlying <see cref="MemoryMappedFile"/> and deletes the temp file,
        /// leaving <see cref="Segment"/> in its current state.
        /// </summary>
        public void DisposeFileOnly() {
            _mmf.Dispose();
            SafeDelete();
        }

        /// <summary>
        /// Disposes the <see cref="Segment"/>, the mapped file, and deletes the temp file.
        /// </summary>
        public void Dispose() {
            Segment.Dispose();
            _mmf.Dispose();
            SafeDelete();
        }

        /// <summary>
        /// Attempts to delete the temp file, swallowing any <see cref="IOException"/>.
        /// </summary>
        private void SafeDelete() {
            try {
                File.Delete(_path);
            } catch (IOException) {
            }
        }
    }
}
