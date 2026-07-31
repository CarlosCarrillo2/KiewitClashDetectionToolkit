using System;
using System.IO;

namespace NavisworksDockPanel.AddIn.Diagnostics
{
    // Temporary file logger for diagnosing the dock pane sizing issue - writes to
    // %LOCALAPPDATA%\NavisworksDockPanel\debug.log so it can be inspected directly
    // without needing the user to relay information back and forth.
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
