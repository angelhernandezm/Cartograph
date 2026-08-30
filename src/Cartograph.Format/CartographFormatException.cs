// ============================================================================
// Cartograph
// File: CartographFormatException.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Custom exception type thrown when an artifact fails validation (bad magic,
// wrong version, checksum mismatch, truncation, or out-of-bounds offsets).
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
    /// <param name="message">A human-readable message describing why the artifact is invalid.</param>
    public CartographFormatException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a descriptive message and an inner cause.</summary>
    /// <param name="message">A human-readable message describing why the artifact is invalid.</param>
    /// <param name="innerException">The underlying exception that triggered this failure.</param>
    public CartographFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
