using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using Autodesk.Navisworks.Api.Plugins;
using NavisworksDockPanel.AddIn.Diagnostics;

namespace NavisworksDockPanel.AddIn.Plugin
{
    // NOTE: verify the exact [Plugin]/[DockPanePlugin] constructor overloads against the
    // Autodesk.Navisworks.Api.Plugins assembly actually installed with your Navisworks
    // version - signatures have drifted slightly across SDK releases.
    //
    // Deliberately loads NavisworksDockPanel.WebViewHost.dll (and its WebView2 dependency)
    // by reflection, never by direct reference. An earlier version referenced WebView2
    // types directly from this assembly, and Navisworks silently failed to register ANY
    // plugin in it - not just this one, but the unrelated ribbon plugin in the same DLL -
    // apparently because the plugin scanner's type enumeration can't tolerate an
    // unresolvable private (non-GAC) dependency anywhere in the assembly. Splitting the
    // WebView2 code into its own assembly, touched only on demand (when the pane is
    // actually opened), keeps this entry assembly's dependency surface to just
    // Autodesk.Navisworks.Api + the .NET Framework/GAC, which is known to load fine
    // (confirmed against a trivial isolation plugin with the same dependency profile).
    //
    // ROOT CAUSE of the sizing bug (confirmed via file logging, see Diagnostics/Log.cs):
    // Navisworks' docking manager (Syncfusion) resizes the pane's WinForms container by
    // direct bounds manipulation, NOT through .NET's normal layout engine - so a child
    // control's Dock = Fill never actually re-fires after the initial CreateControlPane
    // call, even though the parent's own Resize event keeps firing correctly. This is true
    // regardless of WPF/ElementHost vs plain WinForms hosting - both were tried and both
    // showed the identical symptom (control frozen at its initial size). The fix is to
    // explicitly resize our control from the parent's Resize event instead of relying on
    // Dock = Fill at all.
    [Plugin("NavisworksDockPanel.WebUiDockPane", "AcmeDev", DisplayName = "Clash Test Generation", ToolTip = "React/shadcn panel")]
    [DockPanePlugin(600, 900, AutoScroll = false, MinimumWidth = 450, MinimumHeight = 600)]
    public class WebUiDockPanePlugin : DockPanePlugin
    {
        private const string WebViewHostAssemblyName = "NavisworksDockPanel.WebViewHost";
        private const string WebViewHostControlTypeName = "NavisworksDockPanel.WebViewHost.UI.WebViewHostControl";

        private static bool _resolveHandlerRegistered;

        private ElementHost _host;
        private object _control; // actual type: WebViewHost.UI.WebViewHostControl, held only reflectively
        private Control _parent;

        // Which of the panel's views the React app is currently showing ("generate" or
        // "delete") - tracked here (not in the React app's own state) so the ribbon plugin
        // can decide whether re-pressing a button should switch view or toggle the pane
        // closed, without needing a round trip to the page first.
        public string CurrentView { get; private set; } = "generate";

        // Pushes {"action":"setView","view":view} to the page via WebViewHostControl.PostMessage
        // (reflection only - see class-level remarks on why this assembly never references
        // WebViewHost directly). view is always one of the two internal constants above,
        // never external input, so no JSON escaping is needed here.
        public void ShowView(string view)
        {
            CurrentView = view;
            _control?.GetType().GetMethod("PostMessage")
                ?.Invoke(_control, new object[] { $"{{\"action\":\"setView\",\"view\":\"{view}\"}}" });
        }

        public override Control CreateControlPane()
        {
            Log.Write("CreateControlPane: start");
            try
            {
                EnsureAssemblyResolveRegistered();

                // A fresh control per pane creation - dock panes can be destroyed and
                // recreated by Navisworks, and reusing a stale WebView2 instance across
                // that cycle is a common source of the disposal bugs this scaffold guards against.
                Assembly webViewHostAssembly = Assembly.LoadFrom(GetSiblingDllPath(WebViewHostAssemblyName));
                Type controlType = webViewHostAssembly.GetType(WebViewHostControlTypeName, throwOnError: true);
                _control = Activator.CreateInstance(controlType);

                // Wire the WebView2<->native message bridge via reflection - WebViewHost
                // only knows about Func<string,string>, never about NativeMessageBridge or
                // any Navisworks API type, keeping the assembly split intact.
                controlType.GetProperty("NativeMessageHandler")
                    ?.SetValue(_control, new Func<string, string>(NativeMessageBridge.Handle));

                var element = (FrameworkElement)_control;
                element.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
                element.VerticalAlignment = VerticalAlignment.Stretch;

                _host = new ElementHost
                {
                    Dock = DockStyle.Fill,
                    AutoSize = false,
                    Child = element
                };

                // The real fix: sync our size from the PARENT's Resize event, since Dock =
                // Fill alone never re-fires here (see class-level comment above). Hooked via
                // ParentChanged since the parent isn't known until Navisworks actually docks
                // this control somewhere.
                _host.ParentChanged += (s, e) =>
                {
                    if (_parent != null)
                    {
                        _parent.Resize -= OnParentResize;
                    }

                    _parent = _host.Parent;
                    Log.Write($"ParentChanged: parent={_parent?.GetType().FullName ?? "null"}, parentClientSize={_parent?.ClientSize}");

                    if (_parent != null)
                    {
                        _parent.Resize += OnParentResize;
                        SyncToParentSize();
                    }
                };

                return _host;
            }
            catch (Exception ex)
            {
                Log.Write($"CreateControlPane: FAILED - {ex}");
                // CreateControlPane failures are otherwise swallowed silently by Navisworks'
                // pane host - surface them so a synchronous construction failure (as opposed
                // to the async WebView2 init failure handled inside WebViewHostControl itself)
                // is visible.
                System.Windows.Forms.MessageBox.Show(ex.ToString(), "NavisworksDockPanel debug - CreateControlPane failed");
                throw;
            }
        }

        private void OnParentResize(object sender, EventArgs e) => SyncToParentSize();

        private void SyncToParentSize()
        {
            if (_parent == null || _host == null)
            {
                return;
            }

            _host.Location = System.Drawing.Point.Empty;
            _host.Size = _parent.ClientSize;
            Log.Write($"SyncToParentSize: host.Size set to {_host.Size}");
        }

        public override void DestroyControlPane(Control pane)
        {
            Log.Write("DestroyControlPane");
            if (_parent != null)
            {
                _parent.Resize -= OnParentResize;
                _parent = null;
            }

            (_control as IDisposable)?.Dispose();
            _host?.Dispose();
            _control = null;
            _host = null;
        }

        private static void EnsureAssemblyResolveRegistered()
        {
            if (_resolveHandlerRegistered)
            {
                return;
            }

            _resolveHandlerRegistered = true;
            string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;

            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                string simpleName = new AssemblyName(args.Name).Name;
                string candidate = Path.Combine(pluginDir, simpleName + ".dll");
                return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
            };
        }

        private static string GetSiblingDllPath(string assemblyName)
        {
            string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
            return Path.Combine(pluginDir, assemblyName + ".dll");
        }
    }
}
