using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DeployTool
{
    // Double-click this (after `dotnet build`) to redeploy the add-in everywhere Navisworks
    // looks for it, without opening a terminal or elevated PowerShell.
    //
    // Portable by design: DeployTool.csproj's BundleAddInPayload target copies AddIn's built
    // output into a "Payload" folder right next to this exe at build time, and this reads from
    // that folder (relative to its own location) instead of a hardcoded path into this
    // checkout's source tree. Zip src\DeployTool\bin\Debug as a whole and it deploys correctly
    // on any machine that has it - no repo checkout required there.
    //
    // Deliberately only targets the per-user %APPDATA% plugin folder, never the machine-wide
    // Program Files one - Navisworks discovers add-ins from either location (see README), and
    // skipping Program Files means this never needs admin rights or a UAC prompt, which is what
    // makes it safe to hand to someone else who may not have (or want to grant) admin on their
    // machine. app.manifest matches this: requestedExecutionLevel="asInvoker".
    internal static class Program
    {
        private const string Year = "2026";

        private static void Main()
        {
            // Every run's console output is tee'd to a log file - the console window is
            // easy to dismiss without reading closely, and a locked-file skip (see
            // CopyDirectory) is exactly the kind of thing that's silently missed that way.
            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NavisworksDockPanel",
                "deploy.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? ".");
            var logWriter = new StreamWriter(logPath, append: false) { AutoFlush = true };
            Console.SetOut(new TeeWriter(Console.Out, logWriter));

            try
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string exeDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName) ?? ".";
                string sourceDir = Path.Combine(exeDir, "Payload");

                DeployPlugin(
                    sourceDir,
                    destDirs: new[]
                    {
                        Path.Combine(appData, "Autodesk", $"Navisworks Manage {Year}", "Plugins", "NavisworksDockPanel.AddIn"),
                    });

                RemoveLeftover(Path.Combine(appData, "Autodesk", "ApplicationPlugins", "NavisworksDockPanel.bundle"));

                Console.WriteLine();
                Console.WriteLine("Done. Fully exit Navisworks if it's running, then relaunch it.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAILED:");
                Console.WriteLine(ex);
            }

            Console.WriteLine();
            Console.WriteLine("Press Enter to close...");
            Console.ReadLine();
        }

        private static void DeployPlugin(string sourceDir, string[] destDirs)
        {
            if (!Directory.Exists(sourceDir))
            {
                Console.WriteLine($"SKIP (not built yet): {sourceDir}");
                return;
            }

            foreach (string destDir in destDirs)
            {
                Console.WriteLine($"Deploying {sourceDir}");
                Console.WriteLine($"       -> {destDir}");

                // Deliberately NOT a delete-then-recopy: a single file locked by an
                // orphaned msedgewebview2.exe (WebUiAssets content stays open for as long
                // as that process lives - see WebViewHostControl's disposal notes) makes
                // Directory.Delete(recursive: true) throw before anything gets recreated,
                // silently leaving the whole destDir on its previous, possibly-incomplete
                // state - which is exactly the WebUiAssets-keeps-disappearing pattern seen
                // here repeatedly. Copying file-by-file with overwrite=true achieves the
                // same end result for everything that isn't locked, and a locked file just
                // gets skipped (logged below) instead of taking the whole deploy down with it.
                CopyDirectory(sourceDir, destDir);
            }
        }

        private static void RemoveLeftover(string path)
        {
            if (Directory.Exists(path))
            {
                Console.WriteLine($"Removing leftover: {path}");
                Directory.Delete(path, recursive: true);
            }
        }

        private static void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);

            foreach (string filePath in Directory.GetFiles(sourceDir))
            {
                string destFile = Path.Combine(destDir, Path.GetFileName(filePath));
                try
                {
                    File.Copy(filePath, destFile, overwrite: true);
                }
                catch (Exception ex)
                {
                    // Most commonly: the file is still open in a running Navisworks/
                    // msedgewebview2.exe process. Skip it and keep going rather than
                    // aborting the whole deploy - this file just stays on its old content
                    // until the process holding it is closed and this tool runs again.
                    Console.WriteLine($"  SKIP (in use?): {destFile}");
                    Console.WriteLine($"    {ex.Message}");
                }
            }

            foreach (string subDir in Directory.GetDirectories(sourceDir))
            {
                string destSubDir = Path.Combine(destDir, Path.GetFileName(subDir));
                CopyDirectory(subDir, destSubDir);
            }
        }

        // Duplicates everything written to one TextWriter into a second one - used to
        // mirror the console output into deploy.log without changing any Console.WriteLine
        // call sites.
        private sealed class TeeWriter : TextWriter
        {
            private readonly TextWriter _first;
            private readonly TextWriter _second;

            public TeeWriter(TextWriter first, TextWriter second)
            {
                _first = first;
                _second = second;
            }

            public override Encoding Encoding => _first.Encoding;

            public override void Write(char value)
            {
                _first.Write(value);
                _second.Write(value);
            }

            public override void Write(string value)
            {
                _first.Write(value);
                _second.Write(value);
            }

            public override void WriteLine(string value)
            {
                _first.WriteLine(value);
                _second.WriteLine(value);
            }
        }
    }
}
