// ============================================================================
// Cartograph
// File: ConsoleReport.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Console formatting helpers used by the harness to render headings, aligned
// key/value pairs, tables, byte counts and elapsed times
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

namespace Cartograph.Harness;

/// <summary>
/// Provides the small set of console formatting primitives shared by every harness command
/// </summary>
/// <remarks>
/// Nothing here is Cartograph-specific; the type exists only so the packing and loading code can
/// stay focused on the library under test rather than on presentation concerns.
/// </remarks>
internal static class ConsoleReport
{
    /// <summary>
    /// Width, in characters, of the horizontal rules drawn around section headings
    /// </summary>
    private const int RuleWidth = 78;

    /// <summary>
    /// Number of characters reserved for the label column of a key/value line
    /// </summary>
    private const int LabelWidth = 22;

    /// <summary>
    /// Suffixes used when scaling a raw byte count to a human readable magnitude
    /// </summary>
    private static readonly string[] ByteUnits = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];

    /// <summary>
    /// Gets or sets a value indicating whether informational output is suppressed
    /// </summary>
    /// <value>
    /// <see langword="true" /> when only warnings, errors and the final summary should be written;
    /// otherwise <see langword="false" />.
    /// </value>
    public static bool Quiet { get; set; }

    /// <summary>
    /// Writes a section heading framed by a horizontal rule
    /// </summary>
    /// <param name="title">Heading text to display</param>
    public static void Heading(string title)
    {
        if (Quiet)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine(new string('=', RuleWidth));
        Console.WriteLine(title);
        Console.WriteLine(new string('=', RuleWidth));
    }

    /// <summary>
    /// Writes a sub-heading followed by a light horizontal rule
    /// </summary>
    /// <param name="title">Sub-heading text to display</param>
    public static void Subheading(string title)
    {
        if (Quiet)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine(new string('-', RuleWidth));
    }

    /// <summary>
    /// Writes an aligned label and value pair
    /// </summary>
    /// <param name="label">Label shown in the left column</param>
    /// <param name="value">Value shown in the right column</param>
    public static void Field(string label, string value)
    {
        if (Quiet)
        {
            return;
        }

        Console.WriteLine("{0}: {1}", label.PadRight(LabelWidth), value);
    }

    /// <summary>
    /// Writes an informational line
    /// </summary>
    /// <param name="message">Message to display</param>
    public static void Line(string message)
    {
        if (Quiet)
        {
            return;
        }

        Console.WriteLine(message);
    }

    /// <summary>
    /// Writes a blank separator line
    /// </summary>
    public static void Blank()
    {
        if (Quiet)
        {
            return;
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Writes a warning to the standard error stream
    /// </summary>
    /// <param name="message">Warning text to display</param>
    /// <remarks>Warnings are never suppressed by <see cref="Quiet" />.</remarks>
    public static void Warn(string message) => Console.Error.WriteLine("warning: {0}", message);

    /// <summary>
    /// Writes an error to the standard error stream
    /// </summary>
    /// <param name="message">Error text to display</param>
    /// <remarks>Errors are never suppressed by <see cref="Quiet" />.</remarks>
    public static void Error(string message) => Console.Error.WriteLine("error: {0}", message);

    /// <summary>
    /// Writes a line to the standard output stream regardless of the <see cref="Quiet" /> setting
    /// </summary>
    /// <param name="message">Message to display</param>
    public static void Always(string message) => Console.WriteLine(message);

    /// <summary>
    /// Renders a fixed-width table with a header row and a separator rule
    /// </summary>
    /// <param name="headers">Column headings</param>
    /// <param name="rows">Row values; each row must have the same length as <paramref name="headers" /></param>
    /// <param name="rightAlign">
    /// Optional flags selecting which columns are right aligned; when <see langword="null" /> every
    /// column is left aligned
    /// </param>
    /// <exception cref="System.ArgumentNullException"><paramref name="headers" /> is <see langword="null" />.</exception>
    /// <exception cref="System.ArgumentNullException"><paramref name="rows" /> is <see langword="null" />.</exception>
    public static void Table(string[] headers, IReadOnlyList<string[]> rows, bool[]? rightAlign = null)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);

        if (Quiet)
        {
            return;
        }

        int[] widths = new int[headers.Length];
        for (int c = 0; c < headers.Length; c++)
        {
            widths[c] = headers[c].Length;
        }

        foreach (string[] row in rows)
        {
            for (int c = 0; c < headers.Length && c < row.Length; c++)
            {
                widths[c] = Math.Max(widths[c], row[c].Length);
            }
        }

        Console.WriteLine(Compose(headers, widths, rightAlign));
        Console.WriteLine(Compose([.. widths.Select(w => new string('-', w))], widths, rightAlign: null));

        foreach (string[] row in rows)
        {
            Console.WriteLine(Compose(row, widths, rightAlign));
        }
    }

    /// <summary>
    /// Formats a byte count using binary magnitudes
    /// </summary>
    /// <param name="bytes">Number of bytes to format</param>
    /// <returns>A string such as <c>1.44 MiB</c>, or <c>-</c> when the count is negative</returns>
    public static string Bytes(long bytes)
    {
        if (bytes < 0)
        {
            return "-";
        }

        double value = bytes;
        int unit = 0;

        while (value >= 1024d && unit < ByteUnits.Length - 1)
        {
            value /= 1024d;
            unit++;
        }

        return unit == 0
            ? string.Format(CultureInfo.InvariantCulture, "{0} B", bytes)
            : string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1}", value, ByteUnits[unit]);
    }

    /// <summary>
    /// Formats an elapsed time, choosing a unit that keeps the value readable
    /// </summary>
    /// <param name="elapsed">Duration to format</param>
    /// <returns>A string expressed in microseconds, milliseconds or seconds</returns>
    public static string Duration(TimeSpan elapsed)
    {
        double milliseconds = elapsed.TotalMilliseconds;

        if (milliseconds < 1d)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.###} us", elapsed.TotalMicroseconds);
        }

        return milliseconds < 1000d
            ? string.Format(CultureInfo.InvariantCulture, "{0:0.###} ms", milliseconds)
            : string.Format(CultureInfo.InvariantCulture, "{0:0.###} s", elapsed.TotalSeconds);
    }

    /// <summary>
    /// Formats an unsigned 64-bit checksum as a fixed-width hexadecimal literal
    /// </summary>
    /// <param name="checksum">Checksum value to format</param>
    /// <returns>A sixteen digit hexadecimal string</returns>
    public static string Checksum(ulong checksum) => checksum.ToString("x16", CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats an integral value using digit group separators
    /// </summary>
    /// <param name="value">Value to format</param>
    /// <returns>A grouped decimal string such as <c>1,048,576</c></returns>
    public static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Joins a row of cells into a single padded line
    /// </summary>
    /// <param name="cells">Cell values for the row</param>
    /// <param name="widths">Column widths to pad each cell to</param>
    /// <param name="rightAlign">Optional per-column right alignment flags</param>
    /// <returns>The composed line, with trailing whitespace removed</returns>
    private static string Compose(string[] cells, int[] widths, bool[]? rightAlign)
    {
        StringBuilder builder = new();

        for (int c = 0; c < widths.Length; c++)
        {
            string cell = c < cells.Length ? cells[c] : string.Empty;
            bool right = rightAlign is not null && c < rightAlign.Length && rightAlign[c];

            builder.Append(right ? cell.PadLeft(widths[c]) : cell.PadRight(widths[c]));

            if (c < widths.Length - 1)
            {
                builder.Append("  ");
            }
        }

        return builder.ToString().TrimEnd();
    }
}
