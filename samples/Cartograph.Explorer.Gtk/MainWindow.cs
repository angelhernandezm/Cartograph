// ============================================================================
// Cartograph
// File: MainWindow.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// The single window of the GTK4 Cartograph artifact explorer. It lists the
// files catalogued inside an artifact, previews any selected record, and works
// on it (verify or extract), while continuously reporting the other processes
// that currently share the same read-only mapping
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
using System.Text;
using Cartograph.Catalog;
using Cartograph.Explorer.Core;

namespace Cartograph.Explorer.GtkApp;

/// <summary>
/// The explorer's only window, built by composition over a <see cref="Gtk.ApplicationWindow"/>
/// </summary>
/// <remarks>
/// <para>
/// The window is composed rather than derived, because subclassing a GObject through the bindings
/// requires registering a new GType and buys nothing here. Every widget is therefore created and
/// wired up in <see cref="BuildUi"/>.
/// </para>
/// <para>
/// All artifact behaviour lives in <see cref="ArtifactSession"/>, which this window shares verbatim
/// with the Windows Forms front end. The two applications consequently differ only in how they draw,
/// which is the entire point of the split.
/// </para>
/// </remarks>
internal sealed class MainWindow : IDisposable
{
    /// <summary>How often, in milliseconds, the peer list and footprint are refreshed.</summary>
    private const uint TelemetryIntervalMs = 2000;

    /// <summary>Short name reported to peers so each instance shows which front end it is running.</summary>
    private const string FrontEndName = "GTK";

    /// <summary>The underlying GTK window.</summary>
    private readonly Gtk.ApplicationWindow _window;

    /// <summary>Entry holding the path of the artifact to open.</summary>
    private readonly Gtk.Entry _artifactPath = Gtk.Entry.New();

    /// <summary>Entry holding the substring used to filter the file list.</summary>
    private readonly Gtk.Entry _filter = Gtk.Entry.New();

    /// <summary>Entry holding the folder that extractions are written into.</summary>
    private readonly Gtk.Entry _destination = Gtk.Entry.New();

    /// <summary>Backing model of the file list, holding one string per visible entry.</summary>
    private readonly Gtk.StringList _model = Gtk.StringList.New([]);

    /// <summary>Selection model exposing the highlighted row.</summary>
    private readonly Gtk.SingleSelection _selection;

    /// <summary>Label showing the metadata of the selected record.</summary>
    private readonly Gtk.Label _details = Gtk.Label.New(string.Empty);

    /// <summary>Text view showing a bounded preview of the selected record.</summary>
    private readonly Gtk.TextView _preview = Gtk.TextView.New();

    /// <summary>Label showing the most recent status message.</summary>
    private readonly Gtk.Label _status = Gtk.Label.New("No artifact open");

    /// <summary>Label showing the live peer list and this process's memory footprint.</summary>
    private readonly Gtk.Label _peers = Gtk.Label.New(string.Empty);

    /// <summary>Button that verifies the selected record against its stored checksum.</summary>
    private readonly Gtk.Button _verifyButton = Gtk.Button.NewWithLabel("Verify");

    /// <summary>Button that extracts the selected record.</summary>
    private readonly Gtk.Button _extractButton = Gtk.Button.NewWithLabel("Extract");

    /// <summary>Button that extracts every currently visible record.</summary>
    private readonly Gtk.Button _extractAllButton = Gtk.Button.NewWithLabel("Extract all");

    /// <summary>Button that launches another copy of this program on the same artifact.</summary>
    private readonly Gtk.Button _peerButton = Gtk.Button.NewWithLabel("New instance");

    /// <summary>The open artifact, or <c>null</c> when no artifact has been opened yet.</summary>
    private ArtifactSession? _session;

    /// <summary>The entries currently shown in the list, in display order.</summary>
    private IReadOnlyList<CatalogEntry> _visible = [];

    /// <summary>Whether this window has already been disposed.</summary>
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindow" /> class
    /// </summary>
    /// <param name="application">The GTK application that owns the window.</param>
    /// <param name="initialPath">
    /// Path of an artifact to open immediately, or <c>null</c> to start with an empty window.
    /// </param>
    /// <exception cref="System.ArgumentNullException"><paramref name="application" /> is <c>null</c>.</exception>
    public MainWindow(Gtk.Application application, string? initialPath)
    {
        ArgumentNullException.ThrowIfNull(application);

        _window = Gtk.ApplicationWindow.New(application);
        _selection = Gtk.SingleSelection.New(_model);

        BuildUi();

        // The peer list and footprint are polled rather than pushed, because the registry is a set of
        // heartbeat files written by unrelated processes and there is nothing to subscribe to.
        _ = GLib.Functions.TimeoutAdd(0, TelemetryIntervalMs, RefreshTelemetry);

        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            _artifactPath.SetText(initialPath);
            OpenArtifact(initialPath);
        }
    }

    /// <summary>
    /// Shows the window
    /// </summary>
    public void Present() => _window.Present();

    /// <summary>
    /// Closes the artifact and releases the mapping held by this window
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session?.Dispose();
        _session = null;
    }

    /// <summary>
    /// Creates every widget and wires the event handlers
    /// </summary>
    private void BuildUi()
    {
        _window.SetTitle("Cartograph Explorer");
        _window.SetDefaultSize(1100, 720);

        Gtk.Box root = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        root.SetMarginTop(8);
        root.SetMarginBottom(8);
        root.SetMarginStart(8);
        root.SetMarginEnd(8);

        root.Append(BuildToolbar());
        root.Append(BuildBody());
        root.Append(BuildStatusBar());

        _window.SetChild(root);

        UpdateCommandState();
    }

    /// <summary>
    /// Builds the top row holding the artifact path, the open action and the peer launcher
    /// </summary>
    /// <returns>The populated toolbar.</returns>
    private Gtk.Box BuildToolbar()
    {
        Gtk.Box toolbar = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);

        Gtk.Label label = Gtk.Label.New("Artifact:");
        _artifactPath.SetHexpand(true);
        _artifactPath.SetPlaceholderText("Path to a .ctg artifact");

        Gtk.Button open = Gtk.Button.NewWithLabel("Open");
        open.OnClicked += (_, _) => OpenArtifact(_artifactPath.GetText());

        _peerButton.SetTooltipText("Start another copy of this program on the same artifact");
        _peerButton.OnClicked += (_, _) => LaunchPeerInstance();

        toolbar.Append(label);
        toolbar.Append(_artifactPath);
        toolbar.Append(open);
        toolbar.Append(_peerButton);

        return toolbar;
    }

    /// <summary>
    /// Builds the split body: the filterable file list on the left, the record pane on the right
    /// </summary>
    /// <returns>The populated body widget.</returns>
    private Gtk.Widget BuildBody()
    {
        Gtk.Paned split = Gtk.Paned.New(Gtk.Orientation.Horizontal);
        split.SetVexpand(true);
        split.SetStartChild(BuildFileList());
        split.SetEndChild(BuildRecordPane());

        return split;
    }

    /// <summary>
    /// Builds the filter box and the list of catalogued files
    /// </summary>
    /// <returns>The populated file-list column.</returns>
    private Gtk.Widget BuildFileList()
    {
        Gtk.Box column = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        column.SetSizeRequest(420, -1);

        _filter.SetPlaceholderText("Filter by path");
        _filter.OnChanged += (_, _) => ApplyFilter();

        Gtk.SignalListItemFactory factory = Gtk.SignalListItemFactory.New();

        factory.OnSetup += (_, args) =>
        {
            Gtk.Label cell = Gtk.Label.New(string.Empty);
            cell.SetXalign(0);
            ((Gtk.ListItem)args.Object).SetChild(cell);
        };

        factory.OnBind += (_, args) =>
        {
            Gtk.ListItem item = (Gtk.ListItem)args.Object;

            if (item.GetChild() is Gtk.Label cell)
            {
                uint position = item.GetPosition();
                cell.SetText(position < _visible.Count ? _visible[(int)position].RelativePath : string.Empty);
            }
        };

        Gtk.ListView list = Gtk.ListView.New(_selection, factory);
        _selection.OnSelectionChanged += (_, _) => UpdateSelection();

        Gtk.ScrolledWindow scroller = Gtk.ScrolledWindow.New();
        scroller.SetVexpand(true);
        scroller.SetChild(list);

        column.Append(_filter);
        column.Append(scroller);

        return column;
    }

    /// <summary>
    /// Builds the right-hand pane holding record metadata, the preview and the record actions
    /// </summary>
    /// <returns>The populated record pane.</returns>
    private Gtk.Widget BuildRecordPane()
    {
        Gtk.Box pane = Gtk.Box.New(Gtk.Orientation.Vertical, 6);
        pane.SetHexpand(true);

        _details.SetXalign(0);
        _details.SetWrap(true);
        _details.SetText("Open an artifact and select a file to preview it.");

        _preview.SetEditable(false);
        _preview.SetMonospace(true);
        _preview.SetWrapMode(Gtk.WrapMode.None);

        Gtk.ScrolledWindow scroller = Gtk.ScrolledWindow.New();
        scroller.SetVexpand(true);
        scroller.SetChild(_preview);

        _verifyButton.OnClicked += (_, _) => VerifySelected();
        _extractButton.OnClicked += (_, _) => ExtractSelected();
        _extractAllButton.OnClicked += (_, _) => ExtractAllVisible();

        _destination.SetHexpand(true);
        _destination.SetText(Path.Combine(Path.GetTempPath(), "cartograph-extract"));

        Gtk.Box actions = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        actions.Append(_verifyButton);
        actions.Append(_extractButton);
        actions.Append(_extractAllButton);
        actions.Append(Gtk.Label.New("into"));
        actions.Append(_destination);

        pane.Append(_details);
        pane.Append(scroller);
        pane.Append(actions);

        return pane;
    }

    /// <summary>
    /// Builds the bottom status strip
    /// </summary>
    /// <returns>The populated status bar.</returns>
    private Gtk.Box BuildStatusBar()
    {
        Gtk.Box bar = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);

        _status.SetXalign(0);
        _status.SetHexpand(true);
        _peers.SetXalign(1);

        bar.Append(_status);
        bar.Append(_peers);

        return bar;
    }

    /// <summary>
    /// Opens an artifact, replacing whatever this window currently holds
    /// </summary>
    /// <param name="path">Path of the artifact to open.</param>
    private void OpenArtifact(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            SetStatus("Enter the path of an artifact to open.");
            return;
        }

        try
        {
            ArtifactSession session = ArtifactSession.Open(path.Trim(), FrontEndName);

            _session?.Dispose();
            _session = session;

            _window.SetTitle(string.Format(
                CultureInfo.InvariantCulture,
                "Cartograph Explorer - {0} [pid {1}]",
                Path.GetFileName(session.Path),
                Environment.ProcessId));

            ApplyFilter();

            SetStatus(string.Format(
                CultureInfo.InvariantCulture,
                "Opened {0} - {1} files, {2} segments, {3} on disk, opened in {4}",
                Path.GetFileName(session.Path),
                DisplayFormat.Count(session.Entries.Count),
                DisplayFormat.Count(session.SegmentCount),
                DisplayFormat.Bytes(session.SizeOnDisk),
                DisplayFormat.Duration(session.OpenElapsed)));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException)
        {
            SetStatus($"Could not open the artifact: {ex.Message}");
        }
        catch (Cartograph.Format.CartographFormatException ex)
        {
            SetStatus($"Not a valid Cartograph artifact: {ex.Message}");
        }

        UpdateCommandState();
    }

    /// <summary>
    /// Re-applies the filter text to the catalogue and rebuilds the list model
    /// </summary>
    private void ApplyFilter()
    {
        if (_session is null)
        {
            return;
        }

        _visible = _session.Filter(_filter.GetText());

        string[] rows = new string[_visible.Count];
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = _visible[i].RelativePath;
        }

        _model.Splice(0, _model.GetNItems(), rows);

        UpdateSelection();
        UpdateCommandState();
    }

    /// <summary>
    /// Refreshes the metadata and preview panes to match the highlighted row
    /// </summary>
    private void UpdateSelection()
    {
        CatalogEntry? entry = SelectedEntry();

        if (_session is null || entry is null)
        {
            _details.SetText("Open an artifact and select a file to preview it.");
            _preview.GetBuffer().SetText(string.Empty, -1);
            UpdateCommandState();
            return;
        }

        _details.SetText(string.Format(
            CultureInfo.InvariantCulture,
            "{0}\nFolder: {1}    Size: {2}    Records: {3}    Segment: {4}    Modified: {5}    Checksum: {6}",
            entry.RelativePath,
            entry.Directory.Length == 0 ? "(root)" : entry.Directory,
            DisplayFormat.Bytes(entry.Length),
            DisplayFormat.Count(entry.RecordCount),
            DisplayFormat.Count(entry.SegmentIndex),
            DisplayFormat.Timestamp(entry.LastWriteUtc),
            entry.Checksum == 0 ? "not computed" : DisplayFormat.Checksum(entry.Checksum)));

        try
        {
            RecordPreview preview = _session.Preview(entry);

            StringBuilder text = new(preview.Content.ReplaceLineEndings("\n"));

            if (preview.IsTruncated)
            {
                text.Append(string.Format(
                    CultureInfo.InvariantCulture,
                    "\n\n--- showing the first {0} of {1} ---",
                    DisplayFormat.Bytes(preview.PreviewedBytes),
                    DisplayFormat.Bytes(preview.TotalBytes)));
            }

            _preview.GetBuffer().SetText(text.ToString(), -1);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _preview.GetBuffer().SetText($"Could not read this record: {ex.Message}", -1);
        }

        UpdateCommandState();
    }

    /// <summary>
    /// Verifies the selected record against the checksum recorded when the artifact was packed
    /// </summary>
    private void VerifySelected()
    {
        CatalogEntry? entry = SelectedEntry();

        if (_session is null || entry is null)
        {
            return;
        }

        try
        {
            CatalogVerification result = _session.Verify(entry);

            SetStatus(result.HasExpectedChecksum
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: {1} - read {2} in {3} ({4})",
                    entry.Name,
                    result.Matches ? "checksum matches" : "CHECKSUM MISMATCH",
                    DisplayFormat.Bytes(result.BytesRead),
                    DisplayFormat.Duration(result.Elapsed),
                    DisplayFormat.Rate(result.BytesRead, result.Elapsed))
                : string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}: packed without a checksum, read {1} in {2} ({3})",
                    entry.Name,
                    DisplayFormat.Bytes(result.BytesRead),
                    DisplayFormat.Duration(result.Elapsed),
                    DisplayFormat.Rate(result.BytesRead, result.Elapsed)));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Cartograph.Format.CartographFormatException)
        {
            SetStatus($"Verification failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Extracts the selected record into the destination folder
    /// </summary>
    private void ExtractSelected()
    {
        CatalogEntry? entry = SelectedEntry();

        if (_session is null || entry is null)
        {
            return;
        }

        string root = _destination.GetText();

        if (string.IsNullOrWhiteSpace(root))
        {
            SetStatus("Choose a destination folder first.");
            return;
        }

        try
        {
            string target = Path.Combine(root, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            string? parent = Path.GetDirectoryName(target);

            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            long written = _session.Extract(entry, target);

            SetStatus(string.Format(
                CultureInfo.InvariantCulture,
                "Extracted {0} ({1}) to {2}",
                entry.Name,
                DisplayFormat.Bytes(written),
                target));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetStatus($"Extraction failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Extracts every currently visible record into the destination folder
    /// </summary>
    private void ExtractAllVisible()
    {
        if (_session is null || _visible.Count == 0)
        {
            return;
        }

        string root = _destination.GetText();

        if (string.IsNullOrWhiteSpace(root))
        {
            SetStatus("Choose a destination folder first.");
            return;
        }

        try
        {
            long written = _session.ExtractAll(_visible, root);

            SetStatus(string.Format(
                CultureInfo.InvariantCulture,
                "Extracted {0} files ({1}) to {2}",
                DisplayFormat.Count(_visible.Count),
                DisplayFormat.Bytes(written),
                root));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetStatus($"Extraction failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Starts another copy of this program on the same artifact
    /// </summary>
    /// <remarks>
    /// This is what makes the shared mapping observable: the new process maps the very same
    /// read-only file, and both windows then list each other in the peer strip.
    /// </remarks>
    private void LaunchPeerInstance()
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            // Environment.ProcessPath is the apphost, which already knows how to locate the runtime,
            // so this works for a framework-dependent and a self-contained publish alike.
            string? executable = Environment.ProcessPath;

            if (string.IsNullOrEmpty(executable))
            {
                SetStatus("The path of the running executable is unavailable.");
                return;
            }

            using Process? started = Process.Start(new ProcessStartInfo(executable, [_session.Path])
            {
                UseShellExecute = false,
            });

            SetStatus(started is null
                ? "The sibling instance could not be started."
                : $"Started a sibling instance (pid {started.Id}) on the same artifact.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            SetStatus($"Could not start another instance: {ex.Message}");
        }
    }

    /// <summary>
    /// Refreshes the peer list and this process's memory footprint
    /// </summary>
    /// <returns>
    /// Always <see langword="true" />, so that GLib keeps the timer registered for the lifetime of
    /// the window.
    /// </returns>
    private bool RefreshTelemetry()
    {
        if (_disposed)
        {
            return false;
        }

        if (_session is null)
        {
            _peers.SetText(string.Empty);
            return true;
        }

        try
        {
            IReadOnlyList<PeerInstance> peers = _session.RefreshPeers();
            ProcessFootprint footprint = _session.Footprint;

            StringBuilder text = new();

            text.Append(CultureInfo.InvariantCulture, $"{peers.Count} instance(s) sharing this artifact: ");

            for (int i = 0; i < peers.Count; i++)
            {
                PeerInstance peer = peers[i];

                if (i > 0)
                {
                    text.Append(", ");
                }

                text.Append(CultureInfo.InvariantCulture, $"{peer.FrontEnd} pid {peer.ProcessId}");

                if (peer.IsSelf)
                {
                    text.Append(" (this one)");
                }
            }

            text.Append(CultureInfo.InvariantCulture, $"    |    working set {DisplayFormat.Bytes(footprint.WorkingSetBytes)}");
            text.Append(CultureInfo.InvariantCulture, $", managed heap {DisplayFormat.Bytes(footprint.ManagedHeapBytes)}");
            text.Append(CultureInfo.InvariantCulture, $", artifact read {DisplayFormat.Bytes(footprint.ArtifactBytesTouched)}");

            _peers.SetText(text.ToString());
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Presence information is advisory, so a transient failure must never disturb the user.
            _peers.SetText(string.Empty);
        }

        return true;
    }

    /// <summary>
    /// Returns the entry backing the highlighted row
    /// </summary>
    /// <returns>The selected entry, or <c>null</c> when nothing is selected.</returns>
    private CatalogEntry? SelectedEntry()
    {
        uint selected = _selection.GetSelected();

        return selected != Gtk.Constants.INVALID_LIST_POSITION && selected < _visible.Count
            ? _visible[(int)selected]
            : null;
    }

    /// <summary>
    /// Enables or disables the record actions to match the current selection
    /// </summary>
    private void UpdateCommandState()
    {
        bool hasArtifact = _session is not null;
        bool hasSelection = hasArtifact && SelectedEntry() is not null;

        _verifyButton.SetSensitive(hasSelection);
        _extractButton.SetSensitive(hasSelection);
        _extractAllButton.SetSensitive(hasArtifact && _visible.Count > 0);
        _peerButton.SetSensitive(hasArtifact);
    }

    /// <summary>
    /// Shows a message on the status strip
    /// </summary>
    /// <param name="message">The message to display.</param>
    private void SetStatus(string message) => _status.SetText(message);
}
