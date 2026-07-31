using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace NavisworksDockPanel.WebViewHost.Bootstrap
{
    /// <summary>
    /// Handles CoreWebView2 environment creation and navigation for the dock pane's
    /// WebView2 control. Centralized here because the user-data-folder and virtual-host
    /// mapping choices are easy to get subtly wrong (see README "Gotchas").
    /// </summary>
    internal static class WebViewBootstrapper
    {
        private const string VirtualHostName = "appassets.local";

        // When set, the pane navigates straight to the Vite dev server instead of the
        // built/copied WebUiAssets - lets you iterate on the React/shadcn UI with the
        // pane already open and just refreshing it, no Navisworks restart, no C# rebuild,
        // no re-deploy. See README "Fast UI iteration" for the step-by-step.
        private const string DevServerUrlEnvVar = "NAVISWORKSDOCKPANEL_DEV_URL";

        // Created once per Navisworks process and reused for every pane open/close cycle.
        // CoreWebView2Environment does not implement IDisposable in any currently-published
        // SDK version (checked 1.0.2903.40 through 1.0.3967.48 via reflection) - there is no
        // API to explicitly tear one down. Creating a fresh environment on every
        // CreateControlPane (the previous behavior) each spins up its own browser process
        // group against the same user-data-folder; disposing just the WPF control released
        // that control's own reference but never the environment object itself, so
        // repeatedly opening/closing the dock pane accumulated orphaned msedgewebview2.exe
        // processes (confirmed: 20+ piled up over a handful of open/close cycles). Since
        // there's no way to dispose an environment, the only real fix is to not create more
        // than one per process in the first place.
        private static Task<CoreWebView2Environment> _environmentTask;

        public static async Task InitializeAsync(WebView2 webView)
        {
            CoreWebView2Environment environment = await GetOrCreateEnvironmentAsync();
            await webView.EnsureCoreWebView2Async(environment);

            string devServerUrl = Environment.GetEnvironmentVariable(DevServerUrlEnvVar);
            if (!string.IsNullOrEmpty(devServerUrl))
            {
                webView.CoreWebView2.Navigate(devServerUrl);
                return;
            }

            string webUiFolder = GetWebUiAssetsFolder();
            webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                VirtualHostName,
                webUiFolder,
                CoreWebView2HostResourceAccessKind.Allow);

            webView.CoreWebView2.Navigate($"https://{VirtualHostName}/index.html");
        }

        private static async Task<CoreWebView2Environment> GetOrCreateEnvironmentAsync()
        {
            if (_environmentTask == null)
            {
                _environmentTask = CreateEnvironmentAsync();
            }

            try
            {
                return await _environmentTask;
            }
            catch
            {
                // Let the next pane open retry from scratch instead of permanently caching
                // a failed creation.
                _environmentTask = null;
                throw;
            }
        }

        private static Task<CoreWebView2Environment> CreateEnvironmentAsync()
        {
            // Default WebView2 user data folder resolves relative to the host executable's
            // directory (i.e. the Navisworks install dir), which ordinary users can't write to.
            // Always point it somewhere under the current user's profile instead.
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NavisworksDockPanel",
                "WebView2UDF");
            Directory.CreateDirectory(userDataFolder);

            return CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder);
        }

        private static string GetWebUiAssetsFolder()
        {
            string assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            return Path.Combine(assemblyDir ?? string.Empty, "WebUiAssets");
        }
    }
}
