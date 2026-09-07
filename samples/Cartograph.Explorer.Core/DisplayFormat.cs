// ============================================================================
// Cartograph
// File: DisplayFormat.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Culture-invariant formatting helpers shared by the explorer front ends so
// that byte counts, rates and durations read identically on Windows and Linux
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

namespace Cartograph.Explorer.Core;

/// <summary>
/// Formats the quantities the explorer displays
/// </summary>
public static class DisplayFormat
{
    /// <summary>
    /// Unit suffixes used when scaling a byte count, from bytes upwards
    /// </summary>
    private static readonly string[] ByteUnits = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];

    /// <summary>
    /// Formats a byte count using binary units
    /// </summary>
    /// <param name="bytes">The value to format</param>
    /// <returns>A string such as <c>3.42 MiB</c>, or <c>512 B</c> for values below one kibibyte</returns>
    public static string Bytes(long bytes)
    {
        if (bytes < 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
        }

        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < ByteUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.##} {ByteUnits[unit]}");
    }

    /// <summary>
    /// Formats a count with thousands separators
    /// </summary>
    /// <param name="value">The value to format</param>
    /// <returns>A string such as <c>1,048,576</c>.</returns>
    public static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats a duration at a resolution appropriate to its magnitude
    /// </summary>
    /// <param name="elapsed">The duration to format</param>
    /// <returns>A string such as <c>412 us</c>, <c>18.4 ms</c> or <c>2.31 s</c>.</returns>
    public static string Duration(TimeSpan elapsed)
    {
        double milliseconds = elapsed.TotalMilliseconds;

        if (milliseconds < 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{elapsed.TotalMicroseconds:0} us");
        }

        return milliseconds < 1000
            ? string.Create(CultureInfo.InvariantCulture, $"{milliseconds:0.0} ms")
            : string.Create(CultureInfo.InvariantCulture, $"{elapsed.TotalSeconds:0.00} s");
    }

    /// <summary>
    /// Formats a throughput figure
    /// </summary>
    /// <param name="bytes">Number of bytes transferred</param>
    /// <param name="elapsed">Time the transfer took</param>
    /// <returns>
    /// A string such as <c>1.82 GiB/s</c>, or <c>n/a</c> when the elapsed time is too small to give a
    /// meaningful rate
    /// </returns>
    public static string Rate(long bytes, TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds <= 0)
        {
            return "n/a";
        }

        return Bytes((long)(bytes / elapsed.TotalSeconds)) + "/s";
    }

    /// <summary>
    /// Formats a 64-bit checksum
    /// </summary>
    /// <param name="checksum">The value to format</param>
    /// <returns>
    /// A sixteen-digit lowercase hexadecimal string, or <c>not computed</c> when the value is zero
    /// </returns>
    public static string Checksum(ulong checksum) =>
        checksum == 0 ? "not computed" : checksum.ToString("x16", CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats a UTC instant for display
    /// </summary>
    /// <param name="instant">The instant to format</param>
    /// <returns>A sortable string such as <c>2026-09-06 11:19:24Z</c>.</returns>
    public static string Timestamp(DateTime instant) =>
        instant.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
