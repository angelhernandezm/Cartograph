using System.IO.MemoryMappedFiles;
using Cartograph;
using Xunit;

namespace Cartograph.Tests;

public class ViewLeaseTests
{
    [Fact]
    public void UseAfterDispose_Throws()
    {
        using LeaseFixture fixture = new(256);
        ViewLease lease = fixture.Segment.Lease();
        Assert.Equal(fixture.Expected[0], lease.Span[0]);

        lease.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = lease.Memory);
        Assert.Throws<ObjectDisposedException>(() => _ = lease.Span.Length);
    }

    [Fact]
    public void DoubleDispose_IsSafe()
    {
        using LeaseFixture fixture = new(128);
        ViewLease lease = fixture.Segment.Lease();
        lease.Dispose();
        lease.Dispose(); // must not throw
    }

    [Fact]
    public void OwnerDisposeWhileLeaseHeld_KeepsMemoryAlive()
    {
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

    private sealed class LeaseFixture : IDisposable
    {
        private readonly string _path;
        private readonly MemoryMappedFile _mmf;

        public LeaseFixture(int length)
        {
            Expected = TestArtifacts.Pattern(length, 5);
            _path = TestArtifacts.NewTempPath();
            File.WriteAllBytes(_path, Expected);
            _mmf = MemoryMappedFile.CreateFromFile(_path, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
            Segment = new MappedSegment(_mmf, 0, length, MemoryMappedFileAccess.Read);
        }

        public byte[] Expected { get; }

        public MappedSegment Segment { get; }

        public void DisposeFileOnly()
        {
            _mmf.Dispose();
            SafeDelete();
        }

        public void Dispose()
        {
            Segment.Dispose();
            _mmf.Dispose();
            SafeDelete();
        }

        private void SafeDelete()
        {
            try
            {
                File.Delete(_path);
            }
            catch (IOException)
            {
            }
        }
    }
}
