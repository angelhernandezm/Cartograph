// ============================================================================
// Cartograph
// File: InstancePresence.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Lightweight cross-process presence registry that lets every explorer instance
// holding the same artifact discover its peers, making the shared read-only
// mapping visible in the user interface
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
using System.IO.Hashing;
using System.Text;

namespace Cartograph.Explorer.Core;

/// <summary>
/// Describes one explorer process that currently has an artifact open
/// </summary>
/// <param name="ProcessId">Operating-system identifier of the peer process.</param>
/// <param name="FrontEnd">Name of the user interface the peer is running, such as <c>WinForms</c> or <c>GTK</c>.</param>
/// <param name="OpenedUtc">The instant at which the peer opened the artifact.</param>
/// <param name="MappedBytes">Bytes of the artifact the peer reports having touched so far.</param>
/// <param name="IsSelf">Whether the peer is the process doing the enumeration.</param>
public readonly record struct PeerInstance(
    int ProcessId,
    string FrontEnd,
    DateTime OpenedUtc,
    long MappedBytes,
    bool IsSelf);

/// <summary>
/// Publishes and discovers explorer instances that share one artifact file
/// </summary>
/// <remarks>
/// <para>
/// Cartograph's cross-process page sharing is invisible by construction: the operating system simply
/// backs every mapping of the same read-only file with one set of physical pages. This type makes it
/// observable. Each instance drops a small heartbeat file into a per-artifact directory under the
/// system temporary folder and refreshes it periodically; enumerating that directory therefore
/// yields the live set of processes reading the artifact.
/// </para>
/// <para>
/// The registry is advisory only. It never gates access to the artifact, a crashed instance simply
/// ages out, and a failure to read or write a heartbeat is swallowed rather than surfaced, because
/// losing presence information must never stop a user from reading their data.
/// </para>
/// </remarks>
public sealed class InstancePresence : IDisposable {
    /// <summary>
    /// Age beyond which a heartbeat file is treated as belonging to a dead process
    /// </summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Extension used for heartbeat files
    /// </summary>
    private const string HeartbeatExtension = ".peer";

    /// <summary>
    /// Directory holding the heartbeat files for one artifact
    /// </summary>
    private readonly string _directory;

    /// <summary>
    /// Path of this instance's own heartbeat file
    /// </summary>
    private readonly string _heartbeatPath;

    /// <summary>
    /// Name of the front end that created this instance
    /// </summary>
    private readonly string _frontEnd;

    /// <summary>
    /// Instant at which this instance opened the artifact
    /// </summary>
    private readonly DateTime _openedUtc = DateTime.UtcNow;

    /// <summary>
    /// Identifier of the current process
    /// </summary>
    private readonly int _processId = Environment.ProcessId;

    /// <summary>
    /// Non-zero once <see cref="Dispose" /> has run
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="InstancePresence" /> class and announces it
    /// </summary>
    /// <param name="artifactPath">Fully qualified path of the artifact being shared</param>
    /// <param name="frontEnd">Short name of the user interface, used only for display</param>
    /// <exception cref="System.ArgumentException"><paramref name="artifactPath" /> is <see langword="null" /> or empty.</exception>
    /// <exception cref="System.ArgumentException"><paramref name="frontEnd" /> is <see langword="null" /> or empty.</exception>
    public InstancePresence(string artifactPath, string frontEnd) {
        ArgumentException.ThrowIfNullOrEmpty(artifactPath);
        ArgumentException.ThrowIfNullOrEmpty(frontEnd);

        _frontEnd = frontEnd;
        _directory = Path.Combine(Path.GetTempPath(), "cartograph-explorer", KeyFor(artifactPath));
        _heartbeatPath = Path.Combine(
            _directory,
            _processId.ToString(CultureInfo.InvariantCulture) + HeartbeatExtension);

        Refresh(0);
    }

    /// <summary>
    /// Refreshes this instance's heartbeat and returns every live peer, including this one
    /// </summary>
    /// <param name="mappedBytes">Bytes of the artifact this instance has touched so far</param>
    /// <returns>
    /// The live peers ordered by process identifier. The list is never empty in practice, because it
    /// always contains this instance, but it degrades to an empty list if the registry is unusable.
    /// </returns>
    public IReadOnlyList<PeerInstance> Refresh(long mappedBytes) {
        if (_disposed) {
            return [];
        }

        Publish(mappedBytes);

        return Enumerate();
    }

    /// <summary>
    /// Writes this instance's heartbeat file
    /// </summary>
    /// <param name="mappedBytes">Bytes of the artifact this instance has touched so far</param>
    private void Publish(long mappedBytes) {
        try {
            Directory.CreateDirectory(_directory);

            string payload = string.Join(
                '\n',
                _processId.ToString(CultureInfo.InvariantCulture),
                _frontEnd,
                _openedUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                mappedBytes.ToString(CultureInfo.InvariantCulture));

            // FileShare.Read keeps a peer's concurrent enumeration from failing mid-write, and the
            // payload is small enough that a torn read simply yields a line that fails to parse.
            using FileStream stream = new(
                _heartbeatPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read);
            using StreamWriter writer = new(stream, Encoding.UTF8);

            writer.Write(payload);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Presence is advisory; a registry that cannot be written must not break the explorer.
        }
    }

    /// <summary>
    /// Reads every live heartbeat and removes the stale ones
    /// </summary>
    /// <returns>The peers whose heartbeat is recent and whose process is still alive</returns>
    private IReadOnlyList<PeerInstance> Enumerate() {
        List<PeerInstance> peers = [];

        try {
            if (!Directory.Exists(_directory)) {
                return peers;
            }

            DateTime cutoff = DateTime.UtcNow - StaleAfter;

            foreach (string file in Directory.EnumerateFiles(_directory, "*" + HeartbeatExtension)) {
                try {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) {
                        File.Delete(file);
                        continue;
                    }

                    if (TryParse(file, out PeerInstance peer) && IsAlive(peer.ProcessId)) {
                        peers.Add(peer);
                    }
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    // A peer rewriting or deleting its own heartbeat races with this walk; skip it.
                }
            }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return peers;
        }

        peers.Sort(static (left, right) => left.ProcessId.CompareTo(right.ProcessId));

        return peers;
    }

    /// <summary>
    /// Parses one heartbeat file
    /// </summary>
    /// <param name="path">Path of the heartbeat file to read</param>
    /// <param name="peer">Receives the parsed peer when the file is well formed</param>
    /// <returns><see langword="true" /> when the file could be parsed</returns>
    private bool TryParse(string path, out PeerInstance peer) {
        peer = default;

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(stream, Encoding.UTF8);

        string[] lines = reader.ReadToEnd().Split('\n');

        if (lines.Length < 4
            || !int.TryParse(lines[0], CultureInfo.InvariantCulture, out int processId)
            || !long.TryParse(lines[2], CultureInfo.InvariantCulture, out long openedTicks)
            || !long.TryParse(lines[3], CultureInfo.InvariantCulture, out long mappedBytes)
            || openedTicks < 0
            || openedTicks > DateTime.MaxValue.Ticks) {
            return false;
        }

        peer = new PeerInstance(
            processId,
            lines[1],
            new DateTime(openedTicks, DateTimeKind.Utc),
            mappedBytes,
            processId == _processId);

        return true;
    }

    /// <summary>
    /// Determines whether a process identifier still refers to a running process
    /// </summary>
    /// <param name="processId">Identifier to test</param>
    /// <returns><see langword="true" /> when the process is still running</returns>
    private static bool IsAlive(int processId) {
        if (processId == Environment.ProcessId) {
            return true;
        }

        try {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) {
            return false;
        }
    }

    /// <summary>
    /// Derives the registry directory name for an artifact path
    /// </summary>
    /// <param name="artifactPath">Path of the artifact</param>
    /// <returns>A stable hexadecimal key that is safe to use as a directory name</returns>
    /// <remarks>
    /// The path is normalized to lower case on Windows and macOS, whose file systems are
    /// case-insensitive by default, so two instances that opened the same file through differently
    /// cased paths still find one another.
    /// </remarks>
    private static string KeyFor(string artifactPath) {
        string full = Path.GetFullPath(artifactPath);

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) {
            full = full.ToLowerInvariant();
        }

        return XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(full)).ToString("x16", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Removes this instance's heartbeat so peers stop listing it
    /// </summary>
    public void Dispose() {
        if (_disposed) {
            return;
        }

        _disposed = true;

        try {
            File.Delete(_heartbeatPath);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // The heartbeat ages out on its own, so a failed delete is harmless.
        }
    }
}
