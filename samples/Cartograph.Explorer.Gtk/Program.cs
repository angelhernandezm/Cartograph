// ============================================================================
// Cartograph
// File: Program.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Entry point of the GTK4 Cartograph artifact explorer, the Linux counterpart
// of the Windows Forms explorer. The process is deliberately registered as a
// non-unique GTK application so that any number of copies can run side by side
// over one shared, read-only artifact mapping
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

namespace Cartograph.Explorer.GtkApp;

/// <summary>
/// Hosts the entry point of the GTK4 Cartograph artifact explorer
/// </summary>
/// <remarks>
/// <para>
/// The application identifier is registered with <see cref="Gio.ApplicationFlags.NonUnique"/>. That
/// flag is the single most important detail in this file: without it GTK treats the first process as
/// the owner of the application identifier and every later launch merely asks that first process to
/// present its window, which would make it impossible to demonstrate several processes sharing one
/// artifact mapping.
/// </para>
/// <para>
/// The namespace is deliberately <c>Cartograph.Explorer.GtkApp</c> rather than
/// <c>Cartograph.Explorer.Gtk</c>, because the latter would shadow the binding's own <c>Gtk</c>
/// namespace and force every widget reference to be written as <c>global::Gtk</c>.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>
    /// The reverse-DNS identifier GTK registers this application under
    /// </summary>
    private const string ApplicationId = "com.angelhernandezm.cartograph.explorer";

    /// <summary>
    /// Runs the explorer, optionally opening an artifact supplied on the command line
    /// </summary>
    /// <param name="args">
    /// Command-line arguments. When the first argument is present it is treated as the path of the
    /// artifact to open at start-up, which is how a window launches a sibling process onto the same
    /// file.
    /// </param>
    /// <returns>The process exit code produced by the GTK main loop.</returns>
    private static int Main(string[] args)
    {
        string? initialPath = args.Length > 0 ? args[0] : null;

        Gtk.Application application = Gtk.Application.New(ApplicationId, Gio.ApplicationFlags.NonUnique);

        MainWindow? window = null;

        application.OnActivate += (sender, _) =>
        {
            window = new MainWindow((Gtk.Application)sender, initialPath);
            window.Present();
        };

        // The artifact path is consumed above rather than handed to GTK, which would reject it as an
        // unknown option, so the main loop is started with no arguments of its own.
        int exitCode = application.RunWithSynchronizationContext(null);

        window?.Dispose();

        return exitCode;
    }
}
