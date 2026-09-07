// ============================================================================
// Cartograph
// File: RecordPreview.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Bounded preview of a record's bytes, rendered as decoded text when the
// payload is textual and as a canonical hex dump otherwise
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

namespace Cartograph.Explorer.Core;

/// <summary>
/// Identifies how a preview chose to render a record's bytes
/// </summary>
public enum PreviewKind {
    /// <summary>The record is empty, so there is nothing to render.</summary>
    Empty,

    /// <summary>The bytes decoded cleanly as UTF-8 text and are rendered as text.</summary>
    Text,

    /// <summary>The bytes are binary and are rendered as a hex dump.</summary>
    Binary,
}

/// <summary>
/// A bounded, render-ready view of the beginning of a record
/// </summary>
/// <remarks>
/// The explorer never materializes a whole file to show it. It reads a fixed prefix and turns that
/// into a preview, which is what keeps the user interface responsive on an artifact whose individual
/// records may be larger than memory.
/// </remarks>
public sealed class RecordPreview {
    /// <summary>
    /// Default number of bytes a preview reads from the start of a record
    /// </summary>
    public const int DefaultPrefixBytes = 64 * 1024;

    /// <summary>
    /// Number of bytes rendered per line of a hex dump
    /// </summary>
    private const int HexBytesPerLine = 16;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordPreview" /> class
    /// </summary>
    /// <param name="kind">How the bytes were rendered</param>
    /// <param name="content">The rendered text</param>
    /// <param name="previewedBytes">Number of bytes that were rendered</param>
    /// <param name="totalBytes">Total length of the underlying file</param>
    private RecordPreview(PreviewKind kind, string content, int previewedBytes, long totalBytes) {
        Kind = kind;
        Content = content;
        PreviewedBytes = previewedBytes;
        TotalBytes = totalBytes;
    }

    /// <summary>
    /// Gets the way the bytes were rendered
    /// </summary>
    /// <value>One of the <see cref="PreviewKind" /> values.</value>
    public PreviewKind Kind {
        get;
    }

    /// <summary>
    /// Gets the rendered preview text
    /// </summary>
    /// <value>Decoded text for a textual record, a hex dump for a binary one.</value>
    public string Content {
        get;
    }

    /// <summary>
    /// Gets the number of bytes that were rendered
    /// </summary>
    /// <value>At most the prefix size requested when the preview was built.</value>
    public int PreviewedBytes {
        get;
    }

    /// <summary>
    /// Gets the total length of the underlying file
    /// </summary>
    /// <value>The catalogued length, which may be far larger than <see cref="PreviewedBytes" />.</value>
    public long TotalBytes {
        get;
    }

    /// <summary>
    /// Gets a value indicating whether the preview shows only part of the file
    /// </summary>
    /// <value><see langword="true" /> when bytes were left unread.</value>
    public bool IsTruncated => PreviewedBytes < TotalBytes;

    /// <summary>
    /// Builds a preview from the leading bytes of a record
    /// </summary>
    /// <param name="prefix">The bytes read from the start of the record</param>
    /// <param name="totalBytes">Total length of the underlying file</param>
    /// <returns>A render-ready preview</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="prefix" /> is <see langword="null" />.</exception>
    public static RecordPreview Create(byte[] prefix, long totalBytes) {
        ArgumentNullException.ThrowIfNull(prefix);

        if (prefix.Length == 0) {
            return new RecordPreview(
                PreviewKind.Empty,
                totalBytes == 0 ? "(empty file)" : "(no bytes were read)",
                0,
                totalBytes);
        }

        return TryDecodeText(prefix, out string? text)
            ? new RecordPreview(PreviewKind.Text, text, prefix.Length, totalBytes)
            : new RecordPreview(PreviewKind.Binary, HexDump(prefix), prefix.Length, totalBytes);
    }

    /// <summary>
    /// Attempts to decode a byte prefix as UTF-8 text
    /// </summary>
    /// <param name="prefix">Bytes to decode</param>
    /// <param name="text">Receives the decoded text when the bytes are textual</param>
    /// <returns><see langword="true" /> when the bytes look like text</returns>
    /// <remarks>
    /// A NUL byte is treated as proof of binary content, which is the same heuristic <c>git</c> and
    /// <c>grep</c> use. Beyond that the bytes must decode as strict UTF-8, so a truncated multi-byte
    /// sequence at the very end of the prefix is trimmed before decoding rather than failing the whole
    /// preview.
    /// </remarks>
    private static bool TryDecodeText(byte[] prefix, out string text) {
        text = string.Empty;

        ReadOnlySpan<byte> span = prefix;

        // Strip a UTF-8 byte order mark so it does not surface as a stray glyph in the viewer.
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF) {
            span = span[3..];
        }

        if (span.IndexOf((byte)0) >= 0) {
            return false;
        }

        span = TrimPartialRune(span);

        try {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(span);
        } catch (DecoderFallbackException) {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Removes a multi-byte UTF-8 sequence that the prefix cut in half
    /// </summary>
    /// <param name="span">Bytes to trim</param>
    /// <returns>The span without its trailing incomplete sequence, if it had one</returns>
    private static ReadOnlySpan<byte> TrimPartialRune(ReadOnlySpan<byte> span) {
        // A UTF-8 sequence is at most four bytes, so at most three trailing bytes can be incomplete.
        int limit = Math.Min(3, span.Length);

        for (int back = 1; back <= limit; back++) {
            byte candidate = span[^back];

            if ((candidate & 0b1100_0000) == 0b1000_0000) {
                // A continuation byte; keep walking back towards the lead byte.
                continue;
            }

            int expected = candidate switch {
                >= 0b1111_0000 => 4,
                >= 0b1110_0000 => 3,
                >= 0b1100_0000 => 2,
                _ => 1,
            };

            return expected > back ? span[..^back] : span;
        }

        return span;
    }

    /// <summary>
    /// Renders bytes as a canonical hex dump
    /// </summary>
    /// <param name="bytes">Bytes to render</param>
    /// <returns>
    /// One line per sixteen bytes, each showing the offset, the hexadecimal values and the printable
    /// ASCII rendering
    /// </returns>
    private static string HexDump(ReadOnlySpan<byte> bytes) {
        // Offset, hex column, gutter and ASCII column come to a little under 80 characters per line.
        StringBuilder builder = new(((bytes.Length / HexBytesPerLine) + 1) * 80);

        for (int offset = 0; offset < bytes.Length; offset += HexBytesPerLine) {
            ReadOnlySpan<byte> line = bytes[offset..Math.Min(offset + HexBytesPerLine, bytes.Length)];

            builder.Append(offset.ToString("x8", CultureInfo.InvariantCulture)).Append("  ");

            for (int i = 0; i < HexBytesPerLine; i++) {
                builder.Append(i < line.Length
                    ? line[i].ToString("x2", CultureInfo.InvariantCulture)
                    : "  ");

                builder.Append(i == (HexBytesPerLine / 2) - 1 ? "  " : ' ');
            }

            builder.Append(' ');

            foreach (byte value in line) {
                builder.Append(value is >= 0x20 and < 0x7F ? (char)value : '.');
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }
}
