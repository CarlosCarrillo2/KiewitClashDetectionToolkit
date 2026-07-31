using System;
using System.Windows.Controls;
using NavisworksDockPanel.WebViewHost.Bootstrap;
using NavisworksDockPanel.WebViewHost.Diagnostics;

namespace NavisworksDockPanel.WebViewHost.UI
{
    // Must be public - loaded via reflection from NavisworksDockPanel.AddIn.dll, which
    // never references this type directly at compile time (see WebUiDockPanePlugin.cs).
    public partial class WebViewHostControl : UserControl, IDisposable
    {
        private bool _disposed;

        // Set (via reflection, from AddIn) to the handler that actually understands the
        // JSON message protocol and talks to the Navisworks API. This control only relays
        // raw strings between JS (window.chrome.webview.postMessage) and this delegate -
        // it has no idea what the messages mean, keeping WebViewHost free of any
        // Navisworks-API-specific logic.
        public Func<string, string> NativeMessageHandler { get; set; }

        // Set only when a caller (the ribbon plugin, via PostMessage) tries to push a
        // message to the page before CoreWebView2 finishes its async init - flushed as
        // soon as OnLoaded's initialization completes.
        private string _pendingMessage;

        public WebViewHostControl()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            SizeChanged += (s, e) => Log.Write($"WebViewHostControl SizeChanged: ActualSize={ActualWidth}x{ActualHeight}");
        }

        private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
        {
            Log.Write($"WebViewHostControl Loaded: ActualSize={ActualWidth}x{ActualHeight}");
            try
            {
                await WebViewBootstrapper.InitializeAsync(WebViewControl);
                Log.Write($"WebViewBootstrapper.InitializeAsync completed: ActualSize={ActualWidth}x{ActualHeight}");

                WebViewControl.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

                if (_pendingMessage != null)
                {
                    WebViewControl.CoreWebView2.PostWebMessageAsString(_pendingMessage);
                    _pendingMessage = null;
                }
            }
            catch (Exception ex)
            {
                Log.Write($"WebViewBootstrapper.InitializeAsync FAILED: {ex}");
                // Surface init failures in the pane itself rather than failing silently -
                // this is the step-2 smoke test's primary failure signal.
                System.Windows.MessageBox.Show(
                    $"WebView2 initialization failed:\n{ex}",
                    "NavisworksDockPanel",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }

        private void OnWebMessageReceived(object sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string request = e.TryGetWebMessageAsString();
                Log.Write($"WebMessageReceived: {request}");
                string response = NativeMessageHandler?.Invoke(request);
                if (response != null)
                {
                    WebViewControl.CoreWebView2.PostWebMessageAsString(response);
                }
            }
            catch (Exception ex)
            {
                Log.Write($"OnWebMessageReceived FAILED: {ex}");
            }
        }

        // Pushes an unsolicited message to the page (e.g. to switch view), as opposed to
        // OnWebMessageReceived's request/response flow driven by the page itself. Public and
        // called only via reflection from WebUiDockPanePlugin (see class-level remarks there
        // on why AddIn.dll never references this assembly directly).
        public void PostMessage(string json)
        {
            if (WebViewControl?.CoreWebView2 != null)
            {
                WebViewControl.CoreWebView2.PostWebMessageAsString(json);
            }
            else
            {
                _pendingMessage = json;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Loaded -= OnLoaded;

            if (WebViewControl?.CoreWebView2 != null)
            {
                WebViewControl.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
            }

            // Disposing CoreWebView2 explicitly here (rather than relying on Unloaded/GC)
            // prevents orphaned msedgewebview2.exe processes when the dock pane is torn down.
            WebViewControl?.Dispose();
        }
    }
}
