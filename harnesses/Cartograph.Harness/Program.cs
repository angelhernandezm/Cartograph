// ============================================================================
// Cartograph
// File: Program.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Entry point of the Cartograph harness, dispatching the pack, load, roundtrip
// and demo commands and mapping their outcomes onto process exit codes
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
using Cartograph.Format;

namespace Cartograph.Harness;

/// <summary>
/// Hosts the entry point of the harness executable
/// </summary>
/// <remarks>
/// The harness is a worked example rather than a product: it demonstrates the complete lifecycle of
/// a Cartograph artifact by packing a folder tree into one, closing it, reopening it from scratch
/// and proving that every byte survived the round trip.
/// </remarks>
internal static class Program
{
    /// <summary>
    /// Exit code reported when the requested work completed successfully
    /// </summary>
    private const int ExitSuccess = 0;

    /// <summary>
    /// Exit code reported when the command line could not be parsed
    /// </summary>
    private const int ExitUsage = 1;

    /// <summary>
    /// Exit code reported when the run failed for a runtime reason
    /// </summary>
    private const int ExitFailure = 2;

    /// <summary>
    /// Exit code reported when the artifact was read but did not verify
    /// </summary>
    private const int ExitVerificationFailed = 3;

    /// <summary>
    /// Default extension applied to generated artifacts
    /// </summary>
    private const string ArtifactExtension = ".ctg";

    /// <summary>
    /// Runs the harness
    /// </summary>
    /// <param name="args">Command line arguments</param>
    /// <returns>A process exit code describing the outcome of the run</returns>
    public static async Task<int> Main(string[] args)
    {
        if (!HarnessOptions.TryParse(args, out HarnessOptions? options, out string? error))
        {
            ConsoleReport.Error(error!);
            Console.Error.WriteLine();
            HarnessOptions.PrintUsage();
            return ExitUsage;
        }

        ConsoleReport.Quiet = options!.Quiet;

        if (options.Command == HarnessCommand.Help)
        {
            HarnessOptions.PrintUsage();
            return ExitSuccess;
        }

        if (options.Csv)
        {
            RunMetrics.ReportCsvHeader();
        }

        try
        {
            return options.Command switch
            {
                HarnessCommand.Pack => RunPack(options),
                HarnessCommand.Load => await RunLoadAsync(options).ConfigureAwait(false),
                HarnessCommand.Roundtrip => await RunRoundtripAsync(options).ConfigureAwait(false),
                HarnessCommand.Demo => await RunDemoAsync(options).ConfigureAwait(false),
                HarnessCommand.Manifest => RunManifest(options),
                _ => ExitUsage,
            };
        }
        catch (CartographFormatException ex)
        {
            ConsoleReport.Error($"the artifact is not readable: {ex.Message}");
            return ExitFailure;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or InvalidDataException
                                   or InvalidOperationException
                                   or ArgumentException)
        {
            ConsoleReport.Error(ex.Message);
            return ExitFailure;
        }
    }

    /// <summary>
    /// Packs a folder into a new artifact
    /// </summary>
    /// <param name="options">Parsed command line</param>
    /// <returns>A process exit code describing the outcome</returns>
    private static int RunPack(HarnessOptions options)
    {
        string root = Path.GetFullPath(options.InputPath!);
        string artifactPath = ResolveArtifactPath(options, root);

        ConsoleReport.Heading("PACK");
        ConsoleReport.Field("Root", root);
        ConsoleReport.Field("Artifact", artifactPath);
        ConsoleReport.Field("Grouping", options.Grouping.ToString().ToLowerInvariant());

        PackResult result = FolderPacker.Pack(options, root, artifactPath);
        ReportPack(result);

        ConsoleReport.Always($"Wrote {artifactPath}");
        EmitPackCsv(options, result);
        return ExitSuccess;
    }

    /// <summary>
    /// Opens an existing artifact and verifies it
    /// </summary>
    /// <param name="options">Parsed command line</param>
    /// <returns>A process exit code describing the outcome</returns>
    private static async Task<int> RunLoadAsync(HarnessOptions options)
    {
        string artifactPath = Path.GetFullPath(options.InputPath!);

        ConsoleReport.Heading("LOAD");

        LoadResult result = await ArtifactLoader.LoadAsync(options, artifactPath, compareRoot: null)
            .ConfigureAwait(false);

        EmitLoadCsv(options, result);
        return Summarize(result);
    }

    /// <summary>
    /// Packs a folder and immediately reads the result back
    /// </summary>
    /// <param name="options">Parsed command line</param>
    /// <returns>A process exit code describing the outcome</returns>
    private static async Task<int> RunRoundtripAsync(HarnessOptions options)
    {
        string root = Path.GetFullPath(options.InputPath!);
        string artifactPath = ResolveArtifactPath(options, root);

        ConsoleReport.Heading("PACK");
        ConsoleReport.Field("Root", root);
        ConsoleReport.Field("Artifact", artifactPath);
        ConsoleReport.Field("Grouping", options.Grouping.ToString().ToLowerInvariant());

        PackResult packed = FolderPacker.Pack(options, root, artifactPath);
        ReportPack(packed);

        // Everything above is now closed and flushed. The load below starts from nothing but a path,
        // which is the whole point of the exercise.
        LoadResult loaded = await ArtifactLoader.LoadAsync(options, artifactPath, root).ConfigureAwait(false);

        EmitPackCsv(options, packed);
        EmitLoadCsv(options, loaded);
        return Summarize(loaded);
    }

    /// <summary>
    /// Prints an artifact's header and segment manifest without reading any record payload
    /// </summary>
    /// <remarks>
    /// Deliberately structural: it reports what the artifact claims about itself, so it stays usable
    /// on an artifact far larger than memory and on one whose payload is damaged. Use
    /// <see cref="HarnessCommand.Load"/> to actually verify record contents.
    /// </remarks>
    /// <param name="options">Parsed command line</param>
    /// <returns>A process exit code describing the outcome</returns>
    private static int RunManifest(HarnessOptions options)
    {
        string artifactPath = Path.GetFullPath(options.InputPath!);
        var file = new FileInfo(artifactPath);

        if (!file.Exists)
        {
            ConsoleReport.Error($"artifact not found: {artifactPath}");
            return ExitFailure;
        }

        // Checksum verification is a read-path concern; opening for structure alone must not pay it.
        var openOptions = new ArtifactOpenOptions
        {
            ChunkSource = options.Strategy,
            VerifyChecksums = false,
        };

        using Artifact artifact = Artifact.Open(artifactPath, openOptions);
        ArtifactHeader header = artifact.Header;

        ConsoleReport.Heading("HEADER");
        ConsoleReport.Field("Artifact", artifactPath);
        ConsoleReport.Field("File size", ConsoleReport.Bytes(file.Length));
        ConsoleReport.Field("Format version", $"{header.VersionMajor}.{header.VersionMinor}");
        ConsoleReport.Field("Pointer size", $"{header.PointerSize} bytes");
        ConsoleReport.Field("Manifest offset", ConsoleReport.Count((long)header.ManifestOffset));
        ConsoleReport.Field("Manifest length", ConsoleReport.Bytes((long)header.ManifestLength));
        ConsoleReport.Field("Content length", ConsoleReport.Bytes((long)header.ContentLength));
        ConsoleReport.Field("Read strategy", artifact.SourceKind.ToString().ToLowerInvariant());

        // ContentLength is written before the file is closed, so a mismatch means truncation.
        if ((long)header.ContentLength != file.Length)
        {
            ConsoleReport.Warn(
                $"content length ({header.ContentLength:N0}) does not match the file size " +
                $"({file.Length:N0}); the artifact may be truncated.");
        }

        ConsoleReport.Heading("SEGMENTS");

        var rows = new List<string[]>(artifact.Segments.Count);
        long payloadTotal = 0;

        foreach (ArtifactSegment segment in artifact.Segments)
        {
            payloadTotal += segment.DataLength;
            rows.Add(
            [
                segment.SegmentId.ToString(CultureInfo.InvariantCulture),
                ConsoleReport.Count(segment.RecordCount),
                ConsoleReport.Bytes(segment.DataLength),
                ConsoleReport.Count(segment.DataOffset),
                ConsoleReport.Count(segment.DirectoryOffset),
                ConsoleReport.Count(segment.PayloadOffset),
                segment.IsPayloadFirst ? "payload-first" : "directory-first",
                ConsoleReport.Checksum(segment.Checksum),
            ]);
        }

        ConsoleReport.Table(
            ["id", "records", "payload", "data@", "dir@", "payload@", "layout", "checksum"],
            rows,
            [true, true, true, true, true, true, false, false]);

        ConsoleReport.Blank();
        ConsoleReport.Field("Live segments", ConsoleReport.Count(artifact.Segments.Count));
        ConsoleReport.Field("Records", ConsoleReport.Count(artifact.RecordCount));
        ConsoleReport.Field("Payload", ConsoleReport.Bytes(payloadTotal));

        return ExitSuccess;
    }

    /// <summary>
    /// Generates a synthetic folder tree and round-trips it
    /// </summary>
    /// <param name="options">Parsed command line</param>
    /// <returns>A process exit code describing the outcome</returns>
    private static async Task<int> RunDemoAsync(HarnessOptions options)
    {
        ConsoleReport.Heading("DEMO TREE");

        using DemoTree tree = DemoTree.Create(options.DemoFileCount, options.CleanupDemo);

        ConsoleReport.Field("Root", tree.Root);
        ConsoleReport.Field("Files generated", ConsoleReport.Count(tree.FileCount));
        ConsoleReport.Field("Bytes generated", ConsoleReport.Bytes(tree.TotalBytes));

        string artifactPath = options.OutputPath is not null
            ? Path.GetFullPath(options.OutputPath)
            : Path.Combine(Path.GetTempPath(), $"cartograph-demo-{Guid.NewGuid():N}"[..40] + ArtifactExtension);

        ConsoleReport.Heading("PACK");
        ConsoleReport.Field("Artifact", artifactPath);
        ConsoleReport.Field("Grouping", options.Grouping.ToString().ToLowerInvariant());

        PackResult packed = FolderPacker.Pack(options, tree.Root, artifactPath);
        ReportPack(packed);

        LoadResult loaded = await ArtifactLoader.LoadAsync(options, artifactPath, tree.Root).ConfigureAwait(false);

        EmitPackCsv(options, packed);
        EmitLoadCsv(options, loaded);
        int exitCode = Summarize(loaded);

        if (options.CleanupDemo)
        {
            TryDelete(artifactPath);
        }
        else
        {
            ConsoleReport.Line($"Kept the demo tree at {tree.Root}");
            ConsoleReport.Line($"Kept the artifact at {artifactPath}");
        }

        return exitCode;
    }

    /// <summary>
    /// Prints the outcome of a packing run
    /// </summary>
    /// <param name="result">Result to describe</param>
    private static void ReportPack(PackResult result)
    {
        ConsoleReport.Field("Files packed", ConsoleReport.Count(result.Catalog.Entries.Count));
        ConsoleReport.Field("Records written", ConsoleReport.Count(result.RecordsWritten));
        ConsoleReport.Field("Payload bytes", ConsoleReport.Bytes(result.Catalog.TotalBytes));
        ConsoleReport.Field("Segments written", ConsoleReport.Count(result.SegmentCount));
        ConsoleReport.Field("Artifact size", ConsoleReport.Bytes(result.ArtifactBytes));
        ConsoleReport.Field("Scan time", ConsoleReport.Duration(result.ScanElapsed));
        ConsoleReport.Field("Write time", ConsoleReport.Duration(result.WriteElapsed));

        if (result.SkippedFiltered > 0)
        {
            ConsoleReport.Field("Skipped (filtered)", ConsoleReport.Count(result.SkippedFiltered));
        }

        if (result.SkippedTooLarge > 0)
        {
            ConsoleReport.Field("Skipped (too large)", ConsoleReport.Count(result.SkippedTooLarge));
        }

        if (result.SkippedUnreadable > 0)
        {
            ConsoleReport.Field("Skipped (unreadable)", ConsoleReport.Count(result.SkippedUnreadable));
        }

        if (result.StopReason is not null)
        {
            ConsoleReport.Warn(result.StopReason);
        }
    }

    /// <summary>
    /// Prints the final verdict and maps it onto an exit code
    /// </summary>
    /// <param name="result">Result of the load and verification run</param>
    /// <returns><see cref="ExitSuccess" /> when everything verified; otherwise <see cref="ExitVerificationFailed" /></returns>
    private static int Summarize(LoadResult result)
    {
        ConsoleReport.Heading("RESULT");

        if (result.Success)
        {
            ConsoleReport.Always(
                $"OK: {ConsoleReport.Count(result.VerifiedRecords)} records verified, " +
                $"{ConsoleReport.Bytes(result.BytesVerified)} read, " +
                $"opened in {ConsoleReport.Duration(result.OpenElapsed)}.");

            if (result.SourceMatches > 0)
            {
                ConsoleReport.Always(
                    $"OK: {ConsoleReport.Count(result.SourceMatches)} files are byte-identical to the source tree.");
            }

            return ExitSuccess;
        }

        ConsoleReport.Error(
            $"FAILED: {ConsoleReport.Count(result.FailedRecords)} bad records, " +
            $"{ConsoleReport.Count(result.SourceMismatches)} content mismatches.");

        return ExitVerificationFailed;
    }

    /// <summary>
    /// Emits the pack-phase CSV line when the caller requested CSV output
    /// </summary>
    /// <param name="options">Parsed command line, which decides whether CSV is emitted</param>
    /// <param name="result">Pack result carrying the metrics to emit</param>
    private static void EmitPackCsv(HarnessOptions options, PackResult result)
    {
        if (options.Csv)
        {
            result.Metrics.ReportCsv(Label(options));
        }
    }

    /// <summary>
    /// Emits the open- and verify-phase CSV lines when the caller requested CSV output
    /// </summary>
    /// <param name="options">Parsed command line, which decides whether CSV is emitted</param>
    /// <param name="result">Load result carrying the metrics to emit</param>
    private static void EmitLoadCsv(HarnessOptions options, LoadResult result)
    {
        if (!options.Csv)
        {
            return;
        }

        string label = Label(options);
        result.OpenMetrics.ReportCsv(label);
        result.VerifyMetrics?.ReportCsv(label);
    }

    /// <summary>
    /// Builds the CSV label identifying this program and the read strategy it used
    /// </summary>
    /// <param name="options">Parsed command line supplying the read strategy</param>
    /// <returns>A label such as <c>cartograph-mapped</c></returns>
    private static string Label(HarnessOptions options) =>
        $"cartograph-{options.Strategy.ToString().ToLowerInvariant()}";

    /// <summary>
    /// Determines where the artifact should be written
    /// </summary>
    /// <param name="options">Parsed command line, which may carry an explicit output path</param>
    /// <param name="root">Folder being packed, used to derive a default name</param>
    /// <returns>The fully qualified path of the artifact to write</returns>
    private static string ResolveArtifactPath(HarnessOptions options, string root)
    {
        if (options.OutputPath is not null)
        {
            return Path.GetFullPath(options.OutputPath);
        }

        string name = new DirectoryInfo(root).Name;

        if (string.IsNullOrWhiteSpace(name))
        {
            name = "artifact";
        }

        return Path.GetFullPath(name + ArtifactExtension);
    }

    /// <summary>
    /// Deletes a file, reporting rather than throwing when it cannot be removed
    /// </summary>
    /// <param name="path">Fully qualified path of the file to delete</param>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ConsoleReport.Warn($"could not delete '{path}': {ex.Message}");
        }
    }
}
