namespace Cartograph.Format;

/// <summary>Selects the <see cref="IChunkSource"/> strategy used to read record payloads.</summary>
public enum ChunkSourceKind
{
    /// <summary>Memory-map the file (default). Zero-copy; wins on random reads over a hot page cache.</summary>
    Mapped = 0,

    /// <summary>Read into pooled buffers via <see cref="System.IO.RandomAccess"/>. Async-friendly; often wins on large sequential scans.</summary>
    RandomAccess = 1,
}

/// <summary>Options controlling how an <see cref="Artifact"/> is opened.</summary>
public sealed class ArtifactOpenOptions
{
    /// <summary>The default options: memory-mapped access with checksum verification enabled.</summary>
    public static ArtifactOpenOptions Default { get; } = new();

    /// <summary>Which chunk source strategy to use for record reads.</summary>
    public ChunkSourceKind ChunkSource { get; init; } = ChunkSourceKind.Mapped;

    /// <summary>
    /// The mapped window size, used only when <see cref="ChunkSource"/> is
    /// <see cref="ChunkSourceKind.Mapped"/>. A small value forces the multi-view stitching path.
    /// </summary>
    public long WindowSize { get; init; } = MappedFile.DefaultWindowSize;

    /// <summary>Whether to verify each record's checksum on read (recommended). Defaults to <see langword="true"/>.</summary>
    public bool VerifyChecksums { get; init; } = true;
}
