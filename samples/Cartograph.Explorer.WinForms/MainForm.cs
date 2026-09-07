// ============================================================================
// Cartograph
// File: MainForm.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Main window of the Windows explorer: lists the files catalogued inside a
// Cartograph artifact, previews, extracts and verifies any selected record,
// and shows the other processes sharing the same mapping
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

using System.Diagnostics;
using System.Globalization;
using Cartograph.Catalog;
using Cartograph.Explorer.Core;

namespace Cartograph.Explorer.WinForms;

/// <summary>
/// The single window of the Windows explorer
/// </summary>
/// <remarks>
/// The window owns at most one <see cref="ArtifactSession" /> at a time. Everything the user can do
/// with a record is delegated to that session, so this type is concerned only with layout, threading
/// and error presentation.
/// </remarks>
internal sealed class MainForm : Form
{
    /// <summary>
    /// Name this front end publishes to the cross-process presence registry
    /// </summary>
    private const string FrontEndName = "WinForms";

    /// <summary>
    /// Interval at which peers and the process footprint are re-sampled
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// List of catalogued files, run in virtual mode so a catalog with a million entries still opens
    /// instantly
    /// </summary>
    private readonly ListView _files = new();

    /// <summary>
    /// Read-only view of the selected record's leading bytes
    /// </summary>
    private readonly TextBox _preview = new();

    /// <summary>
    /// List of processes currently sharing the artifact
    /// </summary>
    private readonly ListView _peers = new();

    /// <summary>
    /// Search box filtering the catalog by relative path
    /// </summary>
    private readonly ToolStripTextBox _search = new();

    /// <summary>
    /// Status bar label describing the open artifact
    /// </summary>
    private readonly ToolStripStatusLabel _artifactStatus = new("No artifact open");

    /// <summary>
    /// Status bar label describing the last operation
    /// </summary>
    private readonly ToolStripStatusLabel _operationStatus = new(string.Empty);

    /// <summary>
    /// Status bar label describing this process' memory footprint
    /// </summary>
    private readonly ToolStripStatusLabel _footprintStatus = new(string.Empty);

    /// <summary>
    /// Timer driving the peer and footprint refresh
    /// </summary>
    private readonly System.Windows.Forms.Timer _poll = new();

    /// <summary>
    /// Menu items that are only meaningful while an artifact is open
    /// </summary>
    private readonly List<ToolStripItem> _needsArtifact = [];

    /// <summary>
    /// Menu items that are only meaningful while a record is selected
    /// </summary>
    private readonly List<ToolStripItem> _needsSelection = [];

    /// <summary>
    /// The open artifact, or <see langword="null" /> when no artifact is loaded
    /// </summary>
    private ArtifactSession? _session;

    /// <summary>
    /// The catalog entries currently shown, after filtering
    /// </summary>
    private IReadOnlyList<CatalogEntry> _visible = [];

    /// <summary>
    /// Set while a long-running operation is in flight, so a second one cannot be started
    /// </summary>
    private bool _busy;

    /// <summary>
    /// Initializes a new instance of the <see cref="MainForm" /> class
    /// </summary>
    /// <param name="artifactPath">
    /// Artifact to open on startup, or <see langword="null" /> to start with an empty window
    /// </param>
    public MainForm(string? artifactPath)
    {
        Text = "Cartograph Explorer";
        MinimumSize = new Size(900, 560);
        Size = new Size(1280, 800);
        StartPosition = FormStartPosition.WindowsDefaultLocation;

        BuildLayout();

        _poll.Interval = (int)PollInterval.TotalMilliseconds;
        _poll.Tick += (_, _) => RefreshTelemetry();
        _poll.Start();

        UpdateCommandState();

        if (!string.IsNullOrWhiteSpace(artifactPath))
        {
            // Deferred until the window is on screen, so a failure to open shows as a dialog over a
            // real window rather than over nothing, and so the constructor never touches a handle
            // that does not exist yet.
            Shown += (_, _) => OpenArtifact(artifactPath);
        }
    }

    /// <summary>
    /// Composes the whole control tree
    /// </summary>
    private void BuildLayout()
    {
        SplitContainer outer = new()
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 560,
        };

        SplitContainer right = new()
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
        };

        ConfigureFileList();
        ConfigurePreview();
        ConfigurePeerList();

        outer.Panel1.Controls.Add(_files);
        right.Panel1.Controls.Add(_preview);
        right.Panel2.Controls.Add(_peers);
        outer.Panel2.Controls.Add(right);

        StatusStrip status = new();
        _artifactStatus.Spring = true;
        _artifactStatus.TextAlign = ContentAlignment.MiddleLeft;
        status.Items.AddRange([_artifactStatus, _operationStatus, _footprintStatus]);

        Controls.Add(outer);
        Controls.Add(BuildToolStrip());
        Controls.Add(BuildMenu());
        Controls.Add(status);

        // Docked controls are laid out in reverse z-order, so the split container has to be added
        // first for the menu, tool strip and status bar to claim their edges before it fills the rest.
        outer.SplitterDistance = 620;
        right.SplitterDistance = 460;
    }

    /// <summary>
    /// Builds the main menu
    /// </summary>
    /// <returns>The configured menu strip</returns>
    private MenuStrip BuildMenu()
    {
        ToolStripMenuItem open = new("&Open artifact...", null, (_, _) => PromptOpenArtifact())
        {
            ShortcutKeys = Keys.Control | Keys.O,
        };

        ToolStripMenuItem newWindow = new("Open a &second instance", null, (_, _) => LaunchPeerInstance())
        {
            ShortcutKeys = Keys.Control | Keys.N,
            ToolTipText = "Starts another copy of this program on the same artifact, "
                + "so both processes share one physical copy of its pages.",
        };

        ToolStripMenuItem close = new("&Close artifact", null, (_, _) => CloseArtifact());
        ToolStripMenuItem exit = new("E&xit", null, (_, _) => Close());

        ToolStripMenuItem file = new("&File");
        file.DropDownItems.AddRange([open, newWindow, close, new ToolStripSeparator(), exit]);

        ToolStripMenuItem verify = new("&Verify record", null, async (_, _) => await VerifySelectedAsync())
        {
            ShortcutKeys = Keys.Control | Keys.R,
        };

        ToolStripMenuItem extract = new("&Extract record...", null, (_, _) => ExtractSelected())
        {
            ShortcutKeys = Keys.Control | Keys.E,
        };

        ToolStripMenuItem copyPath = new("&Copy relative path", null, (_, _) => CopySelectedPath());

        ToolStripMenuItem record = new("&Record");
        record.DropDownItems.AddRange([verify, extract, copyPath]);

        ToolStripMenuItem extractAll = new("Extract &all shown files...", null, async (_, _) => await ExtractAllAsync());
        ToolStripMenuItem details = new("Artifact &details", null, (_, _) => ShowArtifactDetails());

        ToolStripMenuItem artifact = new("&Artifact");
        artifact.DropDownItems.AddRange([extractAll, details]);

        _needsArtifact.AddRange([close, newWindow, extractAll, details]);
        _needsSelection.AddRange([verify, extract, copyPath]);

        MenuStrip menu = new();
        menu.Items.AddRange([file, record, artifact]);

        MainMenuStrip = menu;

        return menu;
    }

    /// <summary>
    /// Builds the search tool strip
    /// </summary>
    /// <returns>The configured tool strip</returns>
    private ToolStrip BuildToolStrip()
    {
        _search.Width = 320;
        _search.ToolTipText = "Filter the catalog by relative path";
        _search.TextChanged += (_, _) => ApplyFilter();

        ToolStrip strip = new() { GripStyle = ToolStripGripStyle.Hidden };
        strip.Items.Add(new ToolStripLabel("Filter:"));
        strip.Items.Add(_search);

        return strip;
    }

    /// <summary>
    /// Configures the virtual list of catalogued files
    /// </summary>
    private void ConfigureFileList()
    {
        _files.Dock = DockStyle.Fill;
        _files.View = View.Details;
        _files.FullRowSelect = true;
        _files.MultiSelect = true;
        _files.HideSelection = false;
        _files.VirtualMode = true;
        _files.VirtualListSize = 0;

        _files.Columns.Add("File", 320);
        _files.Columns.Add("Size", 90, HorizontalAlignment.Right);
        _files.Columns.Add("Records", 70, HorizontalAlignment.Right);
        _files.Columns.Add("Segment", 130);
        _files.Columns.Add("Modified (UTC)", 150);
        _files.Columns.Add("Checksum", 150);

        _files.RetrieveVirtualItem += OnRetrieveVirtualItem;
        _files.SelectedIndexChanged += (_, _) => OnSelectionChanged();
    }

    /// <summary>
    /// Configures the record preview pane
    /// </summary>
    private void ConfigurePreview()
    {
        _preview.Dock = DockStyle.Fill;
        _preview.Multiline = true;
        _preview.ReadOnly = true;
        _preview.ScrollBars = ScrollBars.Both;
        _preview.WordWrap = false;
        _preview.Font = new Font(FontFamily.GenericMonospace, 9f);
        _preview.Text = "Open an artifact and select a file to preview it.";
    }

    /// <summary>
    /// Configures the peer process list
    /// </summary>
    private void ConfigurePeerList()
    {
        _peers.Dock = DockStyle.Fill;
        _peers.View = View.Details;
        _peers.FullRowSelect = true;
        _peers.HideSelection = false;

        _peers.Columns.Add("Process", 90, HorizontalAlignment.Right);
        _peers.Columns.Add("Front end", 100);
        _peers.Columns.Add("Opened (UTC)", 160);
        _peers.Columns.Add("Artifact bytes read", 150, HorizontalAlignment.Right);
    }

    /// <summary>
    /// Supplies a row to the virtual file list on demand
    /// </summary>
    /// <param name="sender">The list raising the event</param>
    /// <param name="e">Event carrying the index to materialize</param>
    private void OnRetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if (_session is null || e.ItemIndex < 0 || e.ItemIndex >= _visible.Count)
        {
            e.Item = new ListViewItem(string.Empty);
            return;
        }

        CatalogEntry entry = _visible[e.ItemIndex];

        ListViewItem item = new(entry.RelativePath);
        item.SubItems.Add(DisplayFormat.Bytes(entry.Length));
        item.SubItems.Add(DisplayFormat.Count(entry.RecordCount));
        item.SubItems.Add(_session.Catalog.GetGroupName(entry));
        item.SubItems.Add(DisplayFormat.Timestamp(entry.LastWriteUtc));
        item.SubItems.Add(DisplayFormat.Checksum(entry.Checksum));

        e.Item = item;
    }

    /// <summary>
    /// Asks the user for an artifact and opens it
    /// </summary>
    private void PromptOpenArtifact()
    {
        using OpenFileDialog dialog = new()
        {
            Title = "Open a Cartograph artifact",
            Filter = "Cartograph artifacts (*.ctg)|*.ctg|All files (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            OpenArtifact(dialog.FileName);
        }
    }

    /// <summary>
    /// Opens an artifact, replacing any artifact already open
    /// </summary>
    /// <param name="path">Path of the artifact to open</param>
    private void OpenArtifact(string path)
    {
        CloseArtifact();

        try
        {
            _session = ArtifactSession.Open(path, FrontEndName);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
            or Format.CartographFormatException or ArgumentException)
        {
            ShowError("The artifact could not be opened.", ex);
            return;
        }

        Text = $"Cartograph Explorer - {System.IO.Path.GetFileName(_session.Path)} "
            + $"[pid {Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}]";

        _artifactStatus.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"{_session.Path}  -  {DisplayFormat.Bytes(_session.SizeOnDisk)} on disk, "
            + $"{DisplayFormat.Count(_session.Entries.Count)} files in {_session.SegmentCount} segments, "
            + $"opened in {DisplayFormat.Duration(_session.OpenElapsed)}");

        _operationStatus.Text = string.Empty;

        ApplyFilter();
        RefreshTelemetry();
        UpdateCommandState();
    }

    /// <summary>
    /// Closes the open artifact, if any
    /// </summary>
    private void CloseArtifact()
    {
        _session?.Dispose();
        _session = null;
        _visible = [];

        _files.VirtualListSize = 0;
        _files.Invalidate();
        _peers.Items.Clear();

        _preview.Text = "Open an artifact and select a file to preview it.";
        _artifactStatus.Text = "No artifact open";
        _footprintStatus.Text = string.Empty;

        Text = "Cartograph Explorer";

        UpdateCommandState();
    }

    /// <summary>
    /// Starts a second copy of this program on the same artifact
    /// </summary>
    /// <exception cref="System.InvalidOperationException">
    /// The path of the running executable is unavailable, so no sibling process can be started. The
    /// exception is caught locally and surfaced to the user as a message box.
    /// </exception>
    private void LaunchPeerInstance()
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            // Environment.ProcessPath is the apphost, which already knows how to find the runtime, so
            // this works for both a framework-dependent and a self-contained publish.
            string? executable = Environment.ProcessPath;

            if (string.IsNullOrEmpty(executable))
            {
                throw new InvalidOperationException("The path of the running executable is unavailable.");
            }

            using Process? started = Process.Start(new ProcessStartInfo(executable, [_session.Path])
            {
                UseShellExecute = false,
            });

            _operationStatus.Text = started is null
                ? "The second instance did not start."
                : string.Create(CultureInfo.InvariantCulture, $"Started instance pid {started.Id}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ShowError("A second instance could not be started.", ex);
        }
    }

    /// <summary>
    /// Re-applies the search filter and rebuilds the visible list
    /// </summary>
    private void ApplyFilter()
    {
        if (_session is null)
        {
            return;
        }

        _visible = _session.Filter(_search.Text);

        _files.VirtualListSize = _visible.Count;
        _files.Invalidate();

        OnSelectionChanged();
    }

    /// <summary>
    /// Gets the entry backing the current selection
    /// </summary>
    /// <returns>The selected entry, or <see langword="null" /> when nothing is selected</returns>
    private CatalogEntry? SelectedEntry()
    {
        if (_session is null || _files.SelectedIndices.Count == 0)
        {
            return null;
        }

        int index = _files.SelectedIndices[0];

        return index >= 0 && index < _visible.Count ? _visible[index] : null;
    }

    /// <summary>
    /// Refreshes the preview pane after the selection changed
    /// </summary>
    private void OnSelectionChanged()
    {
        UpdateCommandState();

        CatalogEntry? entry = SelectedEntry();

        if (_session is null || entry is null)
        {
            _preview.Text = _session is null
                ? "Open an artifact and select a file to preview it."
                : "Select a file to preview it.";
            return;
        }

        try
        {
            RecordPreview preview = _session.Preview(entry);

            string truncation = preview.IsTruncated
                ? $", showing the first {DisplayFormat.Bytes(preview.PreviewedBytes)}"
                : string.Empty;

            string header = string.Create(
                CultureInfo.InvariantCulture,
                $"{entry.RelativePath}\r\n"
                + $"{DisplayFormat.Bytes(entry.Length)} in {DisplayFormat.Count(entry.RecordCount)} record(s), "
                + $"segment {entry.SegmentIndex} ({_session.Catalog.GetGroupName(entry)}), "
                + $"global record {DisplayFormat.Count(entry.GlobalIndex)}\r\n"
                + $"rendered as {preview.Kind.ToString().ToLowerInvariant()}{truncation}\r\n"
                + $"{new string('-', 78)}\r\n");

            // A TextBox only honours CRLF, and the hex dump and most packed text files use bare LF.
            _preview.Text = header + preview.Content.ReplaceLineEndings("\r\n");
            _preview.Select(0, 0);
        }
        catch (Exception ex) when (ex is IOException or Format.CartographFormatException)
        {
            _preview.Text = $"The record could not be read.\r\n\r\n{ex.Message}";
        }
    }

    /// <summary>
    /// Verifies the selected record against its recorded checksum
    /// </summary>
    /// <returns>A task that completes when verification has finished</returns>
    private async Task VerifySelectedAsync()
    {
        ArtifactSession? session = _session;
        CatalogEntry? entry = SelectedEntry();

        if (session is null || entry is null || _busy)
        {
            return;
        }

        SetBusy(true);

        Progress<long> progress = new(read => _operationStatus.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"Verifying {entry.Name}: {DisplayFormat.Bytes(read)}"));

        try
        {
            CatalogVerification result = await Task.Run(() => session.Verify(entry, progress));

            string verdict = !result.HasExpectedChecksum
                ? "read cleanly (the packer recorded no whole-file checksum to compare against)"
                : result.Matches ? "matches its recorded checksum" : "DOES NOT match its recorded checksum";

            _operationStatus.Text = string.Create(
                CultureInfo.InvariantCulture,
                $"{entry.Name} {verdict} - {DisplayFormat.Bytes(result.BytesRead)} in "
                + $"{DisplayFormat.Duration(result.Elapsed)} ({DisplayFormat.Rate(result.BytesRead, result.Elapsed)})");

            if (result.HasExpectedChecksum && !result.Matches)
            {
                MessageBox.Show(
                    this,
                    $"'{entry.RelativePath}' does not match the checksum recorded when it was packed.\n\n"
                    + $"Recorded: {DisplayFormat.Checksum(result.ExpectedChecksum)}\n"
                    + $"Computed: {DisplayFormat.Checksum(result.ComputedChecksum)}",
                    "Checksum mismatch",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex) when (ex is IOException or Format.CartographFormatException)
        {
            ShowError("The record could not be verified.", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Extracts the selected record to a file the user chooses
    /// </summary>
    private void ExtractSelected()
    {
        ArtifactSession? session = _session;
        CatalogEntry? entry = SelectedEntry();

        if (session is null || entry is null)
        {
            return;
        }

        using SaveFileDialog dialog = new()
        {
            Title = "Extract record",
            FileName = entry.Name,
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            long written = session.Extract(entry, dialog.FileName);

            _operationStatus.Text = string.Create(
                CultureInfo.InvariantCulture,
                $"Extracted {DisplayFormat.Bytes(written)} to {dialog.FileName}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or Format.CartographFormatException)
        {
            ShowError("The record could not be extracted.", ex);
        }
    }

    /// <summary>
    /// Extracts every file currently shown into a folder the user chooses
    /// </summary>
    /// <returns>A task that completes when extraction has finished</returns>
    private async Task ExtractAllAsync()
    {
        ArtifactSession? session = _session;

        if (session is null || _busy || _visible.Count == 0)
        {
            return;
        }

        using FolderBrowserDialog dialog = new()
        {
            Description = "Choose a folder to reconstruct the packed tree into",
            UseDescriptionForTitle = true,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        // Snapshot the filtered view, because the user can keep typing in the filter while this runs.
        CatalogEntry[] entries = [.. _visible];
        string destination = dialog.SelectedPath;

        SetBusy(true);

        Progress<int> progress = new(done => _operationStatus.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"Extracting {DisplayFormat.Count(done)} of {DisplayFormat.Count(entries.Length)} files"));

        try
        {
            long start = Stopwatch.GetTimestamp();
            long written = await Task.Run(() => session.ExtractAll(entries, destination, progress));
            TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

            _operationStatus.Text = string.Create(
                CultureInfo.InvariantCulture,
                $"Extracted {DisplayFormat.Count(entries.Length)} files ({DisplayFormat.Bytes(written)}) "
                + $"in {DisplayFormat.Duration(elapsed)} ({DisplayFormat.Rate(written, elapsed)})");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or Format.CartographFormatException)
        {
            ShowError("The files could not be extracted.", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Copies the selected record's relative path to the clipboard
    /// </summary>
    private void CopySelectedPath()
    {
        CatalogEntry? entry = SelectedEntry();

        if (entry is null)
        {
            return;
        }

        Clipboard.SetText(entry.RelativePath);
        _operationStatus.Text = $"Copied {entry.RelativePath}";
    }

    /// <summary>
    /// Shows the artifact-level metadata recovered from the catalog
    /// </summary>
    private void ShowArtifactDetails()
    {
        if (_session is null)
        {
            return;
        }

        FileCatalog catalog = _session.Catalog;

        string details = string.Create(
            CultureInfo.InvariantCulture,
            $"Artifact: {_session.Path}\n"
            + $"Size on disk: {DisplayFormat.Bytes(_session.SizeOnDisk)}\n"
            + $"Payload described by the catalog: {DisplayFormat.Bytes(catalog.TotalBytes)}\n"
            + $"Files: {DisplayFormat.Count(catalog.Entries.Count)}\n"
            + $"Segments: {_session.SegmentCount} (segment 0 holds the catalog)\n"
            + $"Grouping: {catalog.GroupingMode}\n"
            + $"Packed from: {catalog.SourceRoot}\n"
            + $"Created: {DisplayFormat.Timestamp(catalog.CreatedUtc)}\n"
            + $"Read strategy: {_session.ChunkSource}\n"
            + $"Open took: {DisplayFormat.Duration(_session.OpenElapsed)}");

        MessageBox.Show(this, details, "Artifact details", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// Re-samples the peer list and the process footprint
    /// </summary>
    private void RefreshTelemetry()
    {
        if (_session is null)
        {
            return;
        }

        IReadOnlyList<PeerInstance> peers = _session.RefreshPeers();

        _peers.BeginUpdate();

        try
        {
            _peers.Items.Clear();

            foreach (PeerInstance peer in peers)
            {
                ListViewItem row = new(peer.ProcessId.ToString(CultureInfo.InvariantCulture));
                row.SubItems.Add(peer.IsSelf ? peer.FrontEnd + " (this window)" : peer.FrontEnd);
                row.SubItems.Add(DisplayFormat.Timestamp(peer.OpenedUtc));
                row.SubItems.Add(DisplayFormat.Bytes(peer.MappedBytes));

                if (peer.IsSelf)
                {
                    row.Font = new Font(_peers.Font, FontStyle.Bold);
                }

                _peers.Items.Add(row);
            }
        }
        finally
        {
            _peers.EndUpdate();
        }

        ProcessFootprint footprint = _session.Footprint;

        _footprintStatus.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"{peers.Count} instance(s) sharing  |  working set {DisplayFormat.Bytes(footprint.WorkingSetBytes)}  |  "
            + $"managed heap {DisplayFormat.Bytes(footprint.ManagedHeapBytes)}  |  "
            + $"artifact bytes read {DisplayFormat.Bytes(footprint.ArtifactBytesTouched)}");
    }

    /// <summary>
    /// Enables or disables the commands according to what is currently open and selected
    /// </summary>
    private void UpdateCommandState()
    {
        bool hasArtifact = _session is not null && !_busy;
        bool hasSelection = hasArtifact && SelectedEntry() is not null;

        foreach (ToolStripItem item in _needsArtifact)
        {
            item.Enabled = hasArtifact;
        }

        foreach (ToolStripItem item in _needsSelection)
        {
            item.Enabled = hasSelection;
        }
    }

    /// <summary>
    /// Marks a long-running operation as started or finished
    /// </summary>
    /// <param name="busy"><see langword="true" /> while the operation runs</param>
    private void SetBusy(bool busy)
    {
        _busy = busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;

        UpdateCommandState();
    }

    /// <summary>
    /// Reports a failure to the user
    /// </summary>
    /// <param name="summary">Short description of what failed</param>
    /// <param name="error">The exception that caused it</param>
    private void ShowError(string summary, Exception error) =>
        MessageBox.Show(
            this,
            $"{summary}\n\n{error.Message}",
            "Cartograph Explorer",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);

    /// <summary>
    /// Releases the window's resources and closes the artifact
    /// </summary>
    /// <param name="disposing"><see langword="true" /> when called from <see cref="System.IDisposable.Dispose" /></param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _poll.Stop();
            _poll.Dispose();
            _session?.Dispose();
            _session = null;
        }

        base.Dispose(disposing);
    }
}
