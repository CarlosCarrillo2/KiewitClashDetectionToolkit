using System;
using System.IO;
using NavisworksDockPanel.AddIn.Diagnostics;

namespace NavisworksDockPanel.AddIn.Plugin.ZoneGrouping
{
    // Thin adapter so VolumeClashGroupingService.cs (ported near-verbatim from the other
    // Kiewit Navisworks plugin's "Group by Zone V2" feature) can keep its original
    // PluginDiagnostics.* call sites unchanged, backed by this project's own file logger.
    internal static class PluginDiagnostics
    {
        public static readonly string UnmatchedLogFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NavisworksDockPanel",
            "zone-grouping-unmatched.log");

        public static void Write(string message) => Log.Write(message);

        public static void WriteException(string context, Exception ex) => Log.Write($"{context}: {ex}");

        public static void WriteUnmatchedLog(string content)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(UnmatchedLogFilePath));
                File.WriteAllText(UnmatchedLogFilePath, content);
            }
            catch
            {
                // Logging must never break the actual functionality.
            }
        }
    }
}
