namespace Cartograph.Format;

/// <summary>
/// Thrown when an artifact cannot be interpreted as a valid Cartograph file: a bad magic, an
/// unsupported version, a wrong endianness marker, a failed checksum, or an offset that falls
/// outside the bounds of the file or its segments.
/// </summary>
/// <remarks>
/// A malformed or truncated artifact must always produce this clean exception, never an
/// out-of-bounds read or a crash. The format therefore validates rigorously rather than trusting
/// the file; the on-disk artifact is treated as a trust boundary.
/// </remarks>
public sealed class CartographFormatException : Exception
{
    /// <summary>Creates the exception with a descriptive message.</summary>
    public CartographFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a descriptive message and an inner cause.</summary>
    public CartographFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
