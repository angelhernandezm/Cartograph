using System.Buffers;
using System.IO.MemoryMappedFiles;

namespace Cartograph;

/// <summary>
/// A read-only memory-mapped file tiled into allocation-granularity-aligned windows.
/// </summary>
/// <remarks>
/// <para>
/// Opening is O(1): the file is mapped and its windows are created, but no payload is read until a
/// page is touched. Windows are each at most <see cref="int.MaxValue"/> bytes so that arbitrarily
/// large files (well beyond 2 GiB) can be presented as a single logical byte range through
/// <see cref="Slice"/>, which stitches the touched windows into one <see cref="ReadOnlySequence{Byte}"/>.
/// </para>
/// <para>
/// The default window size is large; tests and specialised callers can force a small window to
/// exercise the multi-view stitching path without creating a huge file.
/// </para>
/// </remarks>
public sealed class MappedFile : IDisposable
{
    /// <summary>The default window size (256 MiB), a multiple of the Windows allocation granularity.</summary>
    public const long DefaultWindowSize = 256L * 1024 * 1024;

    private readonly MemoryMappedFile _mappedFile;
    private readonly MappedSegment[] _windows;
    private readonly long _windowSize;
    private int _disposed;

    private MappedFile(MemoryMappedFile mappedFile, long length, long windowSize)
    {
        _mappedFile = mappedFile;
        Length = length;
        _windowSize = windowSize;

        int windowCount = length == 0 ? 0 : checked((int)((length + windowSize - 1) / windowSize));
        _windows = new MappedSegment[windowCount];
        for (int i = 0; i < windowCount; i++)
        {
            long offset = (long)i * windowSize;
            int size = (int)Math.Min(windowSize, length - offset);
            _windows[i] = new MappedSegment(mappedFile, offset, size, MemoryMappedFileAccess.Read);
        }
    }

    /// <summary>The total length of the mapped file in bytes.</summary>
    public long Length { get; }

    /// <summary>The window size used to tile the file.</summary>
    public long WindowSize => _windowSize;

    /// <summary>The number of windows tiling the file.</summary>
    public int WindowCount => _windows.Length;

    /// <summary>
    /// Opens <paramref name="path"/> read-only and maps it. The requested <paramref name="windowSize"/>
    /// is rounded up to a multiple of the allocation granularity and capped so each window fits an
    /// <see cref="int"/>.
    /// </summary>
    public static MappedFile OpenRead(string path, long windowSize = DefaultWindowSize)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowSize);

        long length = new FileInfo(path).Length;
        if (length == 0)
        {
            throw new ArgumentException("Cannot memory-map an empty file.", nameof(path));
        }

        long granularity = Platform.AllocationGranularity;
        long aligned = Platform.AlignUp(windowSize, granularity);
        long maxAligned = Platform.AlignDown(int.MaxValue, granularity);
        long effective = Math.Min(aligned, maxAligned);

        MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(
            path,
            FileMode.Open,
            mapName: null,
            capacity: 0,
            MemoryMappedFileAccess.Read);

        try
        {
            return new MappedFile(mmf, length, effective);
        }
        catch
        {
            mmf.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Produces a zero-copy <see cref="MappedSlice"/> over <c>[offset, offset + length)</c>, spanning
    /// as many windows as necessary. The returned slice holds leases on every touched window.
    /// </summary>
    public MappedSlice Slice(long offset, long length)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (offset + length > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Requested range extends past the end of the file.");
        }

        if (length == 0)
        {
            return new MappedSlice(ReadOnlySequence<byte>.Empty, []);
        }

        int firstWindow = (int)(offset / _windowSize);
        int lastWindow = (int)((offset + length - 1) / _windowSize);
        int windowSpan = lastWindow - firstWindow + 1;

        List<ReadOnlyMemory<byte>> chunks = new(windowSpan);
        ViewLease[] leases = new ViewLease[windowSpan];

        long position = offset;
        long remaining = length;
        int leaseIndex = 0;
        try
        {
            for (int w = firstWindow; w <= lastWindow; w++)
            {
                MappedSegment window = _windows[w];
                long windowStart = (long)w * _windowSize;
                int inWindowOffset = (int)(position - windowStart);
                int take = (int)Math.Min(remaining, window.Length - inWindowOffset);

                ViewLease lease = window.Lease();
                leases[leaseIndex++] = lease;
                chunks.Add(lease.Memory.Slice(inWindowOffset, take));

                position += take;
                remaining -= take;
            }
        }
        catch
        {
            for (int i = 0; i < leaseIndex; i++)
            {
                leases[i].Dispose();
            }

            throw;
        }

        return new MappedSlice(MappedSequence.Create(chunks), leases);
    }

    /// <summary>Gets the window at <paramref name="index"/> for direct access (e.g. prefaulting).</summary>
    public MappedSegment GetWindow(int index)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _windows[index];
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (MappedSegment window in _windows)
        {
            window.Dispose();
        }

        _mappedFile.Dispose();
    }
}
