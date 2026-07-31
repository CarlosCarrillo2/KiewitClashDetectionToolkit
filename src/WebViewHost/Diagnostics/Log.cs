using System;
using System.IO;

namespace NavisworksDockPanel.WebViewHost.Diagnostics
{
    // Temporary file logger for diagnosing the dock pane sizing issue - writes to the
    // same file as AddIn's copy (NavisworksDockPanel.AddIn.Diagnostics.Log) so both
    // assemblies' log lines interleave in one place.
    internal static class Log
    {
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NavisworksDockPanel",
            "debug.log");

        public static void Write(string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}\r\n");
            }
            catch
            {
                // Logging must never break the actual functionality.
            }
        }
    }
}
