// ============================================================================
// Cartograph
// File: Program.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Entry point of the .NET baseline, dispatching the pack, load, roundtrip and
// demo commands and mapping their outcomes onto the same process exit codes as
// the Cartograph harness so the two can be scripted and profiled identically
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

using System.IO.Hashing;

namespace Cartograph.Baseline;

/// <summary>
/// Hosts the entry point of the baseline executable
/// </summary>
/// <remarks>The baseline mirrors the Cartograph harness command for command, but stores files with plain .NET
/// primitives instead of the artifact format. Running the same tree through both programs and reading
/// the CSV each emits is the whole point: it turns "is a memory-mapped artifact worth it" into a
/// question with numbers behind it.</remarks>
internal static class Program {
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
    /// Exit code reported when the container was read but did not verify
    /// </summary>
    private const int ExitVerificationFailed = 3;

    /// <summary>
    /// Runs the baseline
    /// </summary>
    /// <param name="args">Command line arguments</param>
    /// <returns>A process exit code describing the outcome of the run</returns>
    public static int Main(string[] args) {
        if (!BaselineOptions.TryParse(args, out BaselineOptions? options, out string? error)) {
            ConsoleReport.Error(error!);
            Console.Error.WriteLine();
            BaselineOptions.PrintUsage();
            return ExitUsage;
        }

        ConsoleReport.Quiet = options!.Quiet;

        if (options.Command == BaselineCommand.Help) {
            BaselineOptions.PrintUsage();
            return ExitSuccess;
        }

        if (options.Csv) {
            RunMetrics.ReportCsvHeader();
        }

        try {
            return options.Command switch {
                BaselineCommand.Pack => RunPack(options),
                BaselineCommand.Load => RunLoad(options),
                BaselineCommand.Roundtrip => RunRoundtrip(options),
                BaselineCommand.Demo => RunDemo(options),
                _ => ExitUsage,
            };
        } catch (Exception ex) when (ex is IOException
                                     or UnauthorizedAccessException
                                     or InvalidDataException
                                     or InvalidOperationException
                                     or ArgumentException) {
            ConsoleReport.Error(ex.Message);
            return ExitFailure;
        }
    }

    /// <summary>
    /// Packs a folder into a new container
    /// </summary>
    /// <param name="options">Parsed command line</param>
    /// <returns>A process exit code describing the outcome</returns>
    private static int RunPack(BaselineOptions options) {
        string root = Path.GetFullPath(options.InputPath!);
        BaselineContainer container = BaselineContainer.Create(options.Container, root);
        string containerPath = ResolveContainerPath(options, root, container);

        ConsoleReport.Heading("PACK");
        ConsoleReport.Field("Container", container.Name);
        ConsoleReport.Field("Root", root);
        ConsoleReport.Field("Target", containerPath);

        BaselinePackResult packed = Pack(options, container, root, containerPath);

        ConsoleReport.Always($"Wrote {packed.ContainerPath}");
        EmitCsv(options, container, packed.Metrics, null, null);
        return ExitSuccess;
    }

    /// <summary>
    /// Opens an existing container and verifies it
    /// </summary>
    /// <param name="options">Parsed command line</param>
    /// <returns>A process exit code describing the outcome</returns>
    private static int RunLoad(BaselineOptions options) {
        string containerPath = Path.GetFullPath(options.InputPath!);

        // Loose treats the folder as the container, so the input path is also the source root.
        string root = options.Container == ContainerKind.Loose ? containerPath : string.Empty;
        BaselineContainer container = BaselineContainer.Create(options.Container, root);

        ConsoleReport.Heading("LOAD");
        ConsoleReport.Field("Container", container.Name);
        ConsoleReport.Field("Source", containerPath);

        LoadOutcome outcome = LoadAndVerify(options, container, containerPath, compareRoot: null);

        EmitCsv(options, container, null, outcome.OpenMetrics, outcome.VerifyMetrics);
        return Summarize(outcome);
    }

    /// <summary>
    /// Packs a folder and immediately reads the result back
    /// </summary>
    /// <param name="options">Parsed command line</param>
    /// <returns>A process exit code describing the outcome</returns>
    private static int RunRoundtrip(BaselineOptions options) {
        string root = Path.GetFullPath(options.InputPath!);
        BaselineContainer container = BaselineContainer.Create(options.Container, root);
        string containerPath = ResolveContainerPath(options, root, container);

        ConsoleReport.Heading("PACK");
        ConsoleReport.Field("Container", container.Name);
        ConsoleReport.Field("Root", root);
        ConsoleReport.Field("Target", containerPath);

        BaselinePackResult packed = Pack(options, container, root, containerPath);

        // Everything above is now closed and flushed. The load below starts from nothing but a path.
        LoadOutcome outcome = LoadAndVerify(options, container, packed.ContainerPath, root);

        EmitCsv(options, container, packed.Metrics, outcome.OpenMetrics, outcome.VerifyMetrics);
        return Summarize(outcome);
    }

    /// <summary>
    /// Generates a synthetic folder tree and round-trips it
    /// </summary>
    /// <param name="options">Parsed command line</param>
    /// <returns>A process exit code describing the outcome</returns>
    private static int RunDemo(BaselineOptions options) {
        ConsoleReport.Heading("DEMO TREE");

        using DemoTree tree = DemoTree.Create(options.DemoFileCount, options.CleanupDemo);

        ConsoleReport.Field("Root", tree.Root);
        ConsoleReport.Field("Files generated", ConsoleReport.Count(tree.FileCount));
        ConsoleReport.Field("Bytes generated", ConsoleReport.Bytes(tree.TotalBytes));

        BaselineContainer container = BaselineContainer.Create(options.Container, tree.Root);
        string containerPath = ResolveDemoContainerPath(options, tree.Root, container);

        ConsoleReport.Heading("PACK");
        ConsoleReport.Field("Container", container.Name);
        ConsoleReport.Field("Target", containerPath);

        BaselinePackResult packed = Pack(options, container, tree.Root, containerPath);

        LoadOutcome outcome = LoadAndVerify(options, container, packed.ContainerPath, tree.Root);
        EmitCsv(options, container, packed.Metrics, outcome.OpenMetrics, outcome.VerifyMetrics);
        int exitCode = Summarize(outcome);

        if (options.CleanupDemo) {
            if (container.Extension.Length > 0) {
                TryDelete(packed.ContainerPath);
            }
        } else {
            ConsoleReport.Line($"Kept the demo tree at {tree.Root}");

            if (container.Extension.Length > 0) {
                ConsoleReport.Line($"Kept the container at {packed.ContainerPath}");
            }
        }

        return exitCode;
    }

    /// <summary>
    /// Scans the source tree, packs it and prints the packing report
    /// </summary>
    /// <param name="options">Parsed command line supplying the filters</param>
    /// <param name="container">Container strategy to pack with</param>
    /// <param name="root">Fully qualified path of the folder to pack</param>
    /// <param name="containerPath">Fully qualified path of the container to write</param>
    /// <returns>A description of what was packed</returns>
    /// <exception cref="System.InvalidOperationException">No files matched, so there is nothing to pack.</exception>
    private static BaselinePackResult Pack(
        BaselineOptions options,
        BaselineContainer container,
        string root,
        string containerPath) {
        List<SourceFile> files = SourceScanner.Scan(options, root, out int skippedTooLarge, out int skippedFiltered);

        if (files.Count == 0) {
            throw new InvalidOperationException(
                $"No files under '{root}' matched the current filters, so there is nothing to pack.");
        }

        ConsoleReport.Field("Files discovered", ConsoleReport.Count(files.Count));

        string? directory = Path.GetDirectoryName(containerPath);

        if (container.Extension.Length > 0 && !string.IsNullOrEmpty(directory)) {
            Directory.CreateDirectory(directory);
        }

        BaselinePackResult result = container.Pack(containerPath, files);

        ConsoleReport.Field("Files packed", ConsoleReport.Count(result.FileCount));
        ConsoleReport.Field("Payload bytes", ConsoleReport.Bytes(result.PayloadBytes));
        ConsoleReport.Field("Container size", ConsoleReport.Bytes(result.ContainerBytes));
        ConsoleReport.Field("Pack time", ConsoleReport.Duration(result.Metrics.Elapsed));

        if (skippedFiltered > 0) {
            ConsoleReport.Field("Skipped (filtered)", ConsoleReport.Count(skippedFiltered));
        }

        if (skippedTooLarge > 0) {
            ConsoleReport.Field("Skipped (too large)", ConsoleReport.Count(skippedTooLarge));
        }

        if (!options.Quiet) {
            result.Metrics.Report();
        }

        return result;
    }

    /// <summary>
    /// Opens a container, verifies every stored file and optionally compares against the source tree
    /// </summary>
    /// <param name="options">Parsed command line controlling listing and extraction</param>
    /// <param name="container">Container strategy to open with</param>
    /// <param name="containerPath">Fully qualified path of the container to open</param>
    /// <param name="compareRoot">Folder to compare against, or <see langword="null" /> to skip the comparison</param>
    /// <returns>A description of what was read and whether it verified</returns>
    /// <exception cref="System.IO.FileNotFoundException">A file-backed container does not exist.</exception>
    private static LoadOutcome LoadAndVerify(
        BaselineOptions options,
        BaselineContainer container,
        string containerPath,
        string? compareRoot) {
        if (container.Extension.Length > 0 && !File.Exists(containerPath)) {
            throw new FileNotFoundException($"Container '{containerPath}' does not exist.", containerPath);
        }

        ConsoleReport.Heading("LOAD");

        using ContainerReader reader = container.Open(containerPath, out RunMetrics openMetrics);

        ConsoleReport.Field("Entries", ConsoleReport.Count(reader.Entries.Count));
        ConsoleReport.Field("Open time", ConsoleReport.Duration(openMetrics.Elapsed));

        if (!options.Quiet) {
            openMetrics.Report();
        }

        if (options.List) {
            ReportListing(reader, options.Top);
        }

        RunMetrics.Scope verifyScope = RunMetrics.Measure("verify");

        int verified = 0;
        int failed = 0;
        long bytesVerified = 0;
        byte[] scratch = [];

        for (int i = 0; i < reader.Entries.Count; i++) {
            BaselineEntry entry = reader.Entries[i];
            ReadOnlyMemory<byte> payload = reader.Read(i, ref scratch);

            // Always hash so the CPU cost of verification is comparable across every container mode,
            // even the ones that could not store a checksum to compare against.
            XxHash3 hasher = new();
            hasher.Append(payload.Span);
            ulong actual = hasher.GetCurrentHashAsUInt64();

            if (payload.Length != entry.Length) {
                failed++;
                ConsoleReport.Error(
                    $"length mismatch for '{entry.RelativePath}': index says {entry.Length}, payload has {payload.Length}.");
                continue;
            }

            if (entry.Checksum != 0 && actual != entry.Checksum) {
                failed++;
                ConsoleReport.Error(
                    $"checksum mismatch for '{entry.RelativePath}': expected {ConsoleReport.Checksum(entry.Checksum)}, got {ConsoleReport.Checksum(actual)}.");
                continue;
            }

            verified++;
            bytesVerified += payload.Length;
        }

        RunMetrics verifyMetrics = verifyScope.Stop(bytesVerified);

        ConsoleReport.Field("Verified", ConsoleReport.Count(verified));
        ConsoleReport.Field("Bytes verified", ConsoleReport.Bytes(bytesVerified));
        ConsoleReport.Field("Verify time", ConsoleReport.Duration(verifyMetrics.Elapsed));

        if (!options.Quiet) {
            verifyMetrics.Report();
        }

        int sourceMatches = 0;
        int sourceMismatches = 0;

        if (compareRoot is not null && Directory.Exists(compareRoot)) {
            (sourceMatches, sourceMismatches) = CompareWithSource(reader, compareRoot);
        }

        if (options.ExtractDirectory is not null) {
            Extract(reader, options.ExtractDirectory);
        }

        return new LoadOutcome {
            VerifiedRecords = verified,
            FailedRecords = failed,
            BytesVerified = bytesVerified,
            SourceMatches = sourceMatches,
            SourceMismatches = sourceMismatches,
            OpenMetrics = openMetrics,
            VerifyMetrics = verifyMetrics,
        };
    }

    /// <summary>
    /// Compares every stored file against the original file on disk
    /// </summary>
    /// <param name="reader">Reader over the opened container</param>
    /// <param name="root">Folder holding the original files</param>
    /// <returns>A tuple of the number of matching files and the number of differing files</returns>
    private static (int Matches, int Mismatches) CompareWithSource(ContainerReader reader, string root) {
        int matches = 0;
        int mismatches = 0;
        byte[] scratch = [];

        for (int i = 0; i < reader.Entries.Count; i++) {
            BaselineEntry entry = reader.Entries[i];
            string full = Path.Combine(root, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(full)) {
                continue;
            }

            ReadOnlyMemory<byte> payload = reader.Read(i, ref scratch);
            byte[] original = File.ReadAllBytes(full);

            if (payload.Span.SequenceEqual(original)) {
                matches++;
            } else {
                mismatches++;
                ConsoleReport.Error($"content mismatch for '{entry.RelativePath}'.");
            }
        }

        if (matches > 0 || mismatches > 0) {
            ConsoleReport.Field("Source matches", ConsoleReport.Count(matches));
        }

        return (matches, mismatches);
    }

    /// <summary>
    /// Writes every stored file into a destination directory
    /// </summary>
    /// <param name="reader">Reader over the opened container</param>
    /// <param name="destination">Directory to write the files into</param>
    /// <exception cref="System.IO.IOException">A stored path attempts to escape the destination.</exception>
    private static void Extract(ContainerReader reader, string destination) {
        string fullDestination = Path.GetFullPath(destination);
        Directory.CreateDirectory(fullDestination);
        byte[] scratch = [];

        for (int i = 0; i < reader.Entries.Count; i++) {
            BaselineEntry entry = reader.Entries[i];
            string target = Path.GetFullPath(
                Path.Combine(fullDestination, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)));

            if (!target.StartsWith(fullDestination, StringComparison.Ordinal)) {
                throw new IOException($"Refusing to extract '{entry.RelativePath}' outside the destination.");
            }

            string? directory = Path.GetDirectoryName(target);

            if (!string.IsNullOrEmpty(directory)) {
                Directory.CreateDirectory(directory);
            }

            ReadOnlyMemory<byte> payload = reader.Read(i, ref scratch);
            using FileStream output = new(target, FileMode.Create, FileAccess.Write, FileShare.None);
            output.Write(payload.Span);
        }

        ConsoleReport.Line($"Extracted {reader.Entries.Count} files into {fullDestination}");
    }

    /// <summary>
    /// Prints a listing of the stored files
    /// </summary>
    /// <param name="reader">Reader over the opened container</param>
    /// <param name="top">Maximum number of rows to print</param>
    private static void ReportListing(ContainerReader reader, int top) {
        ConsoleReport.Subheading("LISTING");

        int shown = Math.Min(top, reader.Entries.Count);
        List<string[]> rows = new(shown);

        for (int i = 0; i < shown; i++) {
            BaselineEntry entry = reader.Entries[i];
            rows.Add(
            [
                entry.RelativePath,
                ConsoleReport.Bytes(entry.Length),
                entry.Checksum != 0 ? ConsoleReport.Checksum(entry.Checksum) : "-",
            ]);
        }

        ConsoleReport.Table(["path", "size", "checksum"], rows, [false, true, false]);

        if (reader.Entries.Count > shown) {
            ConsoleReport.Line($"... and {reader.Entries.Count - shown} more.");
        }
    }

    /// <summary>
    /// Prints the final verdict and maps it onto an exit code
    /// </summary>
    /// <param name="outcome">Result of the load and verification run</param>
    /// <returns><see cref="ExitSuccess" /> when everything verified; otherwise <see cref="ExitVerificationFailed" /></returns>
    private static int Summarize(LoadOutcome outcome) {
        ConsoleReport.Heading("RESULT");

        if (outcome.Success) {
            ConsoleReport.Always(
                $"OK: {ConsoleReport.Count(outcome.VerifiedRecords)} entries verified, " +
                $"{ConsoleReport.Bytes(outcome.BytesVerified)} read, " +
                $"opened in {ConsoleReport.Duration(outcome.OpenMetrics.Elapsed)}.");

            if (outcome.SourceMatches > 0) {
                ConsoleReport.Always(
                    $"OK: {ConsoleReport.Count(outcome.SourceMatches)} files are byte-identical to the source tree.");
            }

            return ExitSuccess;
        }

        ConsoleReport.Error(
            $"FAILED: {ConsoleReport.Count(outcome.FailedRecords)} bad entries, " +
            $"{ConsoleReport.Count(outcome.SourceMismatches)} content mismatches.");

        return ExitVerificationFailed;
    }

    /// <summary>
    /// Emits the CSV summary lines when the caller requested them
    /// </summary>
    /// <param name="options">Parsed command line, which decides whether CSV is emitted</param>
    /// <param name="container">Container strategy that produced the metrics</param>
    /// <param name="pack">Pack metrics, or <see langword="null" /> when packing did not run</param>
    /// <param name="open">Open metrics, or <see langword="null" /> when loading did not run</param>
    /// <param name="verify">Verify metrics, or <see langword="null" /> when loading did not run</param>
    private static void EmitCsv(
        BaselineOptions options,
        BaselineContainer container,
        RunMetrics? pack,
        RunMetrics? open,
        RunMetrics? verify) {
        if (!options.Csv) {
            return;
        }

        string label = $"baseline-{container.Name}";
        pack?.ReportCsv(label);
        open?.ReportCsv(label);
        verify?.ReportCsv(label);
    }

    /// <summary>
    /// Determines where a container should be written for a pack or roundtrip run
    /// </summary>
    /// <param name="options">Parsed command line, which may carry an explicit output path</param>
    /// <param name="root">Folder being packed, used to derive a default name</param>
    /// <param name="container">Container strategy, whose extension shapes the default name</param>
    /// <returns>The fully qualified path of the container to write</returns>
    private static string ResolveContainerPath(BaselineOptions options, string root, BaselineContainer container) {
        if (container.Extension.Length == 0) {
            // Loose writes no file; the folder itself is the container.
            return root;
        }

        if (options.OutputPath is not null) {
            return Path.GetFullPath(options.OutputPath);
        }

        string name = new DirectoryInfo(root).Name;

        if (string.IsNullOrWhiteSpace(name)) {
            name = "container";
        }

        return Path.GetFullPath(name + container.Extension);
    }

    /// <summary>
    /// Determines where a container should be written for a demo run
    /// </summary>
    /// <param name="options">Parsed command line, which may carry an explicit output path</param>
    /// <param name="root">Generated tree, used as the container for the loose mode</param>
    /// <param name="container">Container strategy, whose extension shapes the default name</param>
    /// <returns>The fully qualified path of the container to write</returns>
    private static string ResolveDemoContainerPath(BaselineOptions options, string root, BaselineContainer container) {
        if (container.Extension.Length == 0) {
            return root;
        }

        if (options.OutputPath is not null) {
            return Path.GetFullPath(options.OutputPath);
        }

        string name = $"baseline-demo-{Guid.NewGuid():N}"[..40] + container.Extension;
        return Path.Combine(Path.GetTempPath(), name);
    }

    /// <summary>
    /// Deletes a file, reporting rather than throwing when it cannot be removed
    /// </summary>
    /// <param name="path">Fully qualified path of the file to delete</param>
    private static void TryDelete(string path) {
        try {
            if (File.Exists(path)) {
                File.Delete(path);
            }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            ConsoleReport.Warn($"could not delete '{path}': {ex.Message}");
        }
    }

    /// <summary>
    /// Summarizes the outcome of a load and verification run
    /// </summary>
    private sealed class LoadOutcome {
        /// <summary>
        /// Gets the number of entries whose contents matched their stored length and checksum
        /// </summary>
        /// <value>Equal to the entry count on a healthy container.</value>
        public required int VerifiedRecords {
            get; init;
        }

        /// <summary>
        /// Gets the number of entries whose contents did not match
        /// </summary>
        /// <value>Zero on a healthy container.</value>
        public required int FailedRecords {
            get; init;
        }

        /// <summary>
        /// Gets the total number of payload bytes read during verification
        /// </summary>
        /// <value>The sum of the lengths of every verified entry.</value>
        public required long BytesVerified {
            get; init;
        }

        /// <summary>
        /// Gets the number of files that matched the original tree byte for byte
        /// </summary>
        /// <value>Zero when no source comparison was requested.</value>
        public required int SourceMatches {
            get; init;
        }

        /// <summary>
        /// Gets the number of files that differed from the original tree
        /// </summary>
        /// <value>Zero when the container faithfully reproduces the source tree.</value>
        public required int SourceMismatches {
            get; init;
        }

        /// <summary>
        /// Gets the metrics captured while opening the container
        /// </summary>
        /// <value>Covers reading whatever index the container keeps.</value>
        public required RunMetrics OpenMetrics {
            get; init;
        }

        /// <summary>
        /// Gets the metrics captured while reading and verifying every entry
        /// </summary>
        /// <value>Covers the read path being profiled.</value>
        public required RunMetrics VerifyMetrics {
            get; init;
        }

        /// <summary>
        /// Gets a value indicating whether the run completed without any detected problem
        /// </summary>
        /// <value><see langword="true" /> when no entry and no source comparison failed.</value>
        public bool Success => FailedRecords == 0 && SourceMismatches == 0;
    }
}
