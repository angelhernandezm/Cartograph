// ============================================================================
// Cartograph
// File: Program.cs
// Author: Angel Hernandez (me@angelhernandezm.com)
// Description:
// Entry point of the Windows explorer, which starts one window per process so
// that several instances can map and read the same artifact side by side
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

namespace Cartograph.Explorer.WinForms;

/// <summary>
/// Hosts the entry point of the Windows explorer
/// </summary>
/// <remarks>
/// There is deliberately no single-instance guard. Running the executable again starts a second,
/// fully independent process, and pointing both at the same artifact is the whole point of the
/// sample: the operating system backs every read-only mapping of that file with one set of physical
/// pages, so the second window costs almost nothing.
/// </remarks>
internal static class Program
{
    /// <summary>
    /// Starts the application
    /// </summary>
    /// <param name="args">
    /// Command line arguments. The first, when present, is the path of an artifact to open
    /// immediately.
    /// </param>
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        string? artifactPath = args.Length > 0 ? args[0] : null;

        Application.Run(new MainForm(artifactPath));
    }
}
