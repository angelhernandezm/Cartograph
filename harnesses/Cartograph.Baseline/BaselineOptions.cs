// ============================================================================
// Cartograph
// File: BaselineOptions.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Parses the baseline command line, mirroring the Cartograph harness verbs,
// filters and size handling so the two programs accept the same input, and
// adds the two baseline-only switches: which .NET container to use and whether
// to emit machine readable CSV
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

namespace Cartograph.Baseline;

/// <summary>
/// Identifies the operation the baseline was asked to perform
/// </summary>
internal enum BaselineCommand
{
    /// <summary>
    /// Print the usage text and exit
    /// </summary>
    Help = 0,

    /// <summary>
    /// Recursively pack a folder into a new container
    /// </summary>
    Pack = 1,

    /// <summary>
    /// Open an existing container and report on it
    /// </summary>
    Load = 2,

    /// <summary>
    /// Pack a folder and immediately reopen and verify the result
    /// </summary>
    Roundtrip = 3,

    /// <summary>
    /// Generate a synthetic folder tree and round-trip it
    /// </summary>
    Demo = 4,
}

/// <summary>
/// Holds the fully parsed command line for a single baseline invocation
/// </summary>
/// <remarks>
/// The surface deliberately mirrors the Cartograph harness so the two programs pack and load the
/// same trees the same way. Options that are meaningful only to the artifact format, such as the
/// segment grouping or the read strategy, are dropped, and two switches are added: <c>--container</c>
/// selects which plain .NET storage strategy to profile, and <c>--csv</c> requests the machine
/// readable summary used to build the comparison table.
/// </remarks>
internal sealed class BaselineOptions
{
    /// <summary>
    /// Default upper bound on the size of any individual packed file
    /// </summary>
    public const long DefaultMaxFileSize = long.MaxValue;

    /// <summary>
    /// Default upper bound on the total number of payload bytes packed in one run
    /// </summary>
    public const long DefaultMaxTotalBytes = long.MaxValue;

    /// <summary>
    /// Default upper bound on the number of files packed in one run
    /// </summary>
    public const int DefaultMaxFiles = 200_000;

    /// <summary>
    /// Gets the operation to perform
    /// </summary>
    /// <value>The command selected on the command line, or inferred from the first positional argument.</value>
    public BaselineCommand Command { get; private set; } = BaselineCommand.Help;

    /// <summary>
    /// Gets the container strategy to profile
    /// </summary>
    /// <value>The value of <c>--container</c>; defaults to <see cref="ContainerKind.NaiveStream" />.</value>
    public ContainerKind Container { get; private set; } = ContainerKind.NaiveStream;

    /// <summary>
    /// Gets the positional input path
    /// </summary>
    /// <value>
    /// A folder for <see cref="BaselineCommand.Pack" /> and <see cref="BaselineCommand.Roundtrip" />,
    /// a container file for <see cref="BaselineCommand.Load" />, and <see langword="null" /> otherwise.
    /// </value>
    public string? InputPath { get; private set; }

    /// <summary>
    /// Gets the path of the container to create
    /// </summary>
    /// <value>The value of <c>--out</c>, or <see langword="null" /> to derive one from the input path.</value>
    public string? OutputPath { get; private set; }

    /// <summary>
    /// Gets the include patterns applied to relative paths
    /// </summary>
    /// <value>An empty list means every file is included.</value>
    public List<string> Includes { get; } = [];

    /// <summary>
    /// Gets the exclude patterns applied to relative paths
    /// </summary>
    /// <value>Patterns are evaluated after <see cref="Includes" /> and win when both match.</value>
    public List<string> Excludes { get; } = [];

    /// <summary>
    /// Gets the maximum size of a single packed file
    /// </summary>
    /// <value>Files larger than this are skipped and reported; defaults to <see cref="DefaultMaxFileSize" />.</value>
    public long MaxFileSize { get; private set; } = DefaultMaxFileSize;

    /// <summary>
    /// Gets the maximum total number of payload bytes to pack
    /// </summary>
    /// <value>Packing stops once this budget is exhausted; defaults to <see cref="DefaultMaxTotalBytes" />.</value>
    public long MaxTotalBytes { get; private set; } = DefaultMaxTotalBytes;

    /// <summary>
    /// Gets the maximum number of files to pack
    /// </summary>
    /// <value>Defaults to <see cref="DefaultMaxFiles" />.</value>
    public int MaxFiles { get; private set; } = DefaultMaxFiles;

    /// <summary>
    /// Gets a value indicating whether reparse points are traversed
    /// </summary>
    /// <value>
    /// <see langword="true" /> when symbolic links and junctions are followed; otherwise
    /// <see langword="false" />, which is the default and avoids cycles.
    /// </value>
    public bool FollowLinks { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the catalog listing is printed
    /// </summary>
    /// <value><see langword="true" /> when <c>--list</c> was supplied.</value>
    public bool List { get; private set; }

    /// <summary>
    /// Gets the maximum number of catalog rows to print
    /// </summary>
    /// <value>The value of <c>--top</c>; defaults to twenty.</value>
    public int Top { get; private set; } = 20;

    /// <summary>
    /// Gets the directory into which packed files are extracted
    /// </summary>
    /// <value>The value of <c>--extract</c>, or <see langword="null" /> when extraction is not requested.</value>
    public string? ExtractDirectory { get; private set; }

    /// <summary>
    /// Gets a value indicating whether informational output is suppressed
    /// </summary>
    /// <value><see langword="true" /> when <c>--quiet</c> was supplied.</value>
    public bool Quiet { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the machine readable CSV summary is emitted
    /// </summary>
    /// <value><see langword="true" /> when <c>--csv</c> was supplied.</value>
    public bool Csv { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the container produced by a demo run is deleted afterwards
    /// </summary>
    /// <value><see langword="true" /> unless <c>--keep</c> was supplied.</value>
    public bool CleanupDemo { get; private set; } = true;

    /// <summary>
    /// Gets the number of files generated by the demo tree
    /// </summary>
    /// <value>The value of <c>--demo-files</c>; defaults to one hundred and twenty.</value>
    public int DemoFileCount { get; private set; } = 120;

    /// <summary>
    /// Parses a command line into a <see cref="BaselineOptions" /> instance
    /// </summary>
    /// <param name="args">Raw arguments as received by the entry point</param>
    /// <param name="options">On success, the parsed options; otherwise <see langword="null" /></param>
    /// <param name="error">On failure, a human readable description of the problem; otherwise <see langword="null" /></param>
    /// <returns><see langword="true" /> when the command line was understood; otherwise <see langword="false" /></returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="args" /> is <see langword="null" />.</exception>
    public static bool TryParse(string[] args, out BaselineOptions? options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        options = null;
        error = null;

        BaselineOptions parsed = new();
        int index = 0;

        if (args.Length == 0)
        {
            parsed.Command = BaselineCommand.Help;
            options = parsed;
            return true;
        }

        string first = args[0];

        switch (first.ToLowerInvariant())
        {
            case "pack":
                parsed.Command = BaselineCommand.Pack;
                index = 1;
                break;

            case "load":
                parsed.Command = BaselineCommand.Load;
                index = 1;
                break;

            case "roundtrip":
                parsed.Command = BaselineCommand.Roundtrip;
                index = 1;
                break;

            case "demo":
                parsed.Command = BaselineCommand.Demo;
                index = 1;
                break;

            case "help":
            case "-h":
            case "--help":
            case "-?":
            case "/?":
                parsed.Command = BaselineCommand.Help;
                options = parsed;
                return true;

            default:
                // No verb: infer one from the shape of the first positional argument.
                parsed.Command = Directory.Exists(first) ? BaselineCommand.Roundtrip : BaselineCommand.Load;
                index = 0;
                break;
        }

        for (; index < args.Length; index++)
        {
            string arg = args[index];

            if (!arg.StartsWith('-'))
            {
                if (parsed.InputPath is not null)
                {
                    error = $"Unexpected positional argument '{arg}'; a path was already supplied.";
                    return false;
                }

                parsed.InputPath = arg;
                continue;
            }

            switch (arg.ToLowerInvariant())
            {
                case "-o":
                case "--out":
                    if (!TryTakeValue(args, ref index, arg, out string? outPath, out error))
                    {
                        return false;
                    }

                    parsed.OutputPath = outPath;
                    break;

                case "--container":
                    if (!TryTakeValue(args, ref index, arg, out string? container, out error))
                    {
                        return false;
                    }

                    if (!TryParseContainer(container!, out ContainerKind kind))
                    {
                        error = $"Unknown container '{container}'; expected zip-store, zip-deflate, naive, naive-stream or loose.";
                        return false;
                    }

                    parsed.Container = kind;
                    break;

                case "--include":
                    if (!TryTakeValue(args, ref index, arg, out string? include, out error))
                    {
                        return false;
                    }

                    parsed.Includes.Add(include!);
                    break;

                case "--exclude":
                    if (!TryTakeValue(args, ref index, arg, out string? exclude, out error))
                    {
                        return false;
                    }

                    parsed.Excludes.Add(exclude!);
                    break;

                case "--max-file-size":
                    if (!TryTakeSize(args, ref index, arg, out long maxFile, out error))
                    {
                        return false;
                    }

                    parsed.MaxFileSize = maxFile;
                    break;

                case "--max-total":
                    if (!TryTakeSize(args, ref index, arg, out long maxTotal, out error))
                    {
                        return false;
                    }

                    parsed.MaxTotalBytes = maxTotal;
                    break;

                case "--max-files":
                    if (!TryTakeInt32(args, ref index, arg, out int maxFiles, out error))
                    {
                        return false;
                    }

                    parsed.MaxFiles = maxFiles;
                    break;

                case "--follow-links":
                    parsed.FollowLinks = true;
                    break;

                case "--list":
                    parsed.List = true;
                    break;

                case "--top":
                    if (!TryTakeInt32(args, ref index, arg, out int top, out error))
                    {
                        return false;
                    }

                    parsed.Top = top;
                    break;

                case "--extract":
                    if (!TryTakeValue(args, ref index, arg, out string? extract, out error))
                    {
                        return false;
                    }

                    parsed.ExtractDirectory = extract;
                    break;

                case "--csv":
                    parsed.Csv = true;
                    break;

                case "--quiet":
                case "-q":
                    parsed.Quiet = true;
                    break;

                case "--keep":
                    parsed.CleanupDemo = false;
                    break;

                case "--demo-files":
                    if (!TryTakeInt32(args, ref index, arg, out int demoFiles, out error))
                    {
                        return false;
                    }

                    parsed.DemoFileCount = demoFiles;
                    break;

                case "-h":
                case "--help":
                    parsed.Command = BaselineCommand.Help;
                    options = parsed;
                    return true;

                default:
                    error = $"Unknown option '{arg}'.";
                    return false;
            }
        }

        if (parsed.Command is BaselineCommand.Pack or BaselineCommand.Roundtrip && parsed.InputPath is null)
        {
            error = "A root folder is required. Try: dotnet-baseline roundtrip <folder>";
            return false;
        }

        if (parsed.Command == BaselineCommand.Load && parsed.InputPath is null)
        {
            error = "A container path is required. Try: dotnet-baseline load <container>";
            return false;
        }

        options = parsed;
        return true;
    }

    /// <summary>
    /// Writes the usage text to the standard output stream
    /// </summary>
    public static void PrintUsage()
    {
        Console.WriteLine(
            $"""
            dotnet-baseline - pack a folder tree using only .NET primitives and read it back.

            This is the profiling counterpart to the Cartograph harness. It solves the same problem
            with no reference to Cartograph, so the two can be compared honestly.

            USAGE
              dotnet-baseline demo [options]
              dotnet-baseline pack <folder> [--out <container>] [options]
              dotnet-baseline load <container> [options]
              dotnet-baseline roundtrip <folder> [--out <container>] [options]
              dotnet-baseline <folder>            (same as roundtrip)
              dotnet-baseline <container>         (same as load)

            COMMANDS
              demo         Generate a synthetic folder tree, then round-trip it.
              pack         Recursively read <folder> and write a new container.
              load         Open a container, report on it and verify its contents.
              roundtrip    pack followed by load, plus a byte-for-byte comparison
                           against the files still on disk.

            CONTAINER
              --container <mode>      zip-store | zip-deflate | naive |
                                      naive-stream (default) | loose
                                        zip-store     ZIP with compression disabled.
                                        zip-deflate   ZIP with Deflate; smaller, more CPU.
                                        naive         Flat file read whole into memory.
                                        naive-stream  Flat file, index only, seek per record.
                                        loose         No container; read files in place.

            PACKING OPTIONS
              -o, --out <path>        Container to write. Default: <folder-name><ext>
              --include <pattern>     Only pack relative paths matching the pattern.
                                      Repeatable. Supports * and ?.
              --exclude <pattern>     Skip relative paths matching the pattern.
                                      Repeatable. Applied after --include.
              --max-file-size <size>  Skip files larger than this. Default: {DescribeSizeDefault(DefaultMaxFileSize)}
              --max-total <size>      Stop once this many payload bytes are packed.
                                      Default: {DescribeSizeDefault(DefaultMaxTotalBytes)}
              --max-files <n>         Stop after this many files. Default: {DefaultMaxFiles}
              --follow-links          Traverse symlinks and junctions. Off by default.

            LOADING OPTIONS
              --list                  Print the container listing.
              --top <n>               Rows to print with --list. Default: 20
              --extract <dir>         Write every stored file into <dir>.

            DEMO OPTIONS
              --demo-files <n>        Files to generate. Default: 120
              --keep                  Keep the generated tree and container.

            GENERAL
              --csv                   Emit a machine readable CSV summary line per phase.
              -q, --quiet             Only print warnings, errors and the summary.
              -h, --help              Show this text.

            SIZES
              Accept a plain byte count or a K / M / G suffix, e.g. 512K, 8M, 2G.

            EXIT CODES
              0  success
              1  bad command line
              2  runtime failure
              3  verification failure
            """);
    }

    /// <summary>
    /// Maps a container name onto a <see cref="ContainerKind" />
    /// </summary>
    /// <param name="text">Raw container name from the command line</param>
    /// <param name="kind">On success, the matching kind; otherwise <see cref="ContainerKind.NaiveStream" /></param>
    /// <returns><see langword="true" /> when the name was recognised; otherwise <see langword="false" /></returns>
    private static bool TryParseContainer(string text, out ContainerKind kind)
    {
        switch (text.ToLowerInvariant())
        {
            case "zip-store":
            case "zipstore":
            case "store":
                kind = ContainerKind.ZipStore;
                return true;

            case "zip-deflate":
            case "zipdeflate":
            case "deflate":
            case "zip":
                kind = ContainerKind.ZipDeflate;
                return true;

            case "naive":
            case "whole":
                kind = ContainerKind.Naive;
                return true;

            case "naive-stream":
            case "naivestream":
            case "stream":
                kind = ContainerKind.NaiveStream;
                return true;

            case "loose":
            case "none":
                kind = ContainerKind.Loose;
                return true;

            default:
                kind = ContainerKind.NaiveStream;
                return false;
        }
    }

    /// <summary>
    /// Renders a default size for the usage text, collapsing an unlimited default to a word
    /// </summary>
    /// <param name="value">Default size in bytes</param>
    /// <returns>The word <c>unlimited</c> when <paramref name="value" /> is <see cref="long.MaxValue" />; otherwise a compact byte figure</returns>
    private static string DescribeSizeDefault(long value)
    {
        return value == long.MaxValue ? "unlimited" : ConsoleReport.Bytes(value);
    }

    /// <summary>
    /// Reads the value that follows an option
    /// </summary>
    /// <param name="args">Full argument array</param>
    /// <param name="index">Index of the option; advanced past the consumed value on success</param>
    /// <param name="option">Option name, used only for diagnostics</param>
    /// <param name="value">On success, the consumed value; otherwise <see langword="null" /></param>
    /// <param name="error">On failure, a description of the problem; otherwise <see langword="null" /></param>
    /// <returns><see langword="true" /> when a value was available; otherwise <see langword="false" /></returns>
    private static bool TryTakeValue(string[] args, ref int index, string option, out string? value, out string? error)
    {
        if (index + 1 >= args.Length)
        {
            value = null;
            error = $"Option '{option}' requires a value.";
            return false;
        }

        index++;
        value = args[index];
        error = null;
        return true;
    }

    /// <summary>
    /// Reads a positive 32-bit integer that follows an option
    /// </summary>
    /// <param name="args">Full argument array</param>
    /// <param name="index">Index of the option; advanced past the consumed value on success</param>
    /// <param name="option">Option name, used only for diagnostics</param>
    /// <param name="value">On success, the parsed value; otherwise zero</param>
    /// <param name="error">On failure, a description of the problem; otherwise <see langword="null" /></param>
    /// <returns><see langword="true" /> when a positive integer was parsed; otherwise <see langword="false" /></returns>
    private static bool TryTakeInt32(string[] args, ref int index, string option, out int value, out string? error)
    {
        value = 0;

        if (!TryTakeValue(args, ref index, option, out string? raw, out error))
        {
            return false;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value <= 0)
        {
            error = $"Option '{option}' expects a positive integer but received '{raw}'.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads a byte size that follows an option, honouring K, M and G suffixes
    /// </summary>
    /// <param name="args">Full argument array</param>
    /// <param name="index">Index of the option; advanced past the consumed value on success</param>
    /// <param name="option">Option name, used only for diagnostics</param>
    /// <param name="value">On success, the size in bytes; otherwise zero</param>
    /// <param name="error">On failure, a description of the problem; otherwise <see langword="null" /></param>
    /// <returns><see langword="true" /> when a positive size was parsed; otherwise <see langword="false" /></returns>
    private static bool TryTakeSize(string[] args, ref int index, string option, out long value, out string? error)
    {
        value = 0;

        if (!TryTakeValue(args, ref index, option, out string? raw, out error))
        {
            return false;
        }

        string text = raw!.Trim();
        long multiplier = 1;

        if (text.EndsWith("KiB", StringComparison.OrdinalIgnoreCase) ||
            text.EndsWith("MiB", StringComparison.OrdinalIgnoreCase) ||
            text.EndsWith("GiB", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = char.ToUpperInvariant(text[^3]) switch
            {
                'K' => 1024L,
                'M' => 1024L * 1024,
                _ => 1024L * 1024 * 1024,
            };

            text = text[..^3];
        }
        else if (text.Length > 0)
        {
            char suffix = char.ToUpperInvariant(text[^1]);

            if (suffix is 'K' or 'M' or 'G')
            {
                multiplier = suffix switch
                {
                    'K' => 1024L,
                    'M' => 1024L * 1024,
                    _ => 1024L * 1024 * 1024,
                };

                text = text[..^1];
            }
        }

        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long units) || units <= 0)
        {
            error = $"Option '{option}' expects a positive size but received '{raw}'.";
            return false;
        }

        value = units * multiplier;
        return true;
    }
}
