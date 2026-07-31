using System;
using System.Windows.Forms;
using Autodesk.Navisworks.Api.Plugins;

namespace NavisworksDockPanel.AddIn.Plugin
{
    // Ribbon tab that opens the Clash Test Generation dock pane, where the user picks
    // which models to clash and triggers the actual clash matrix from the shadcn UI.
    // Pattern (attributes + base class) confirmed against a working Navisworks 2026 ribbon
    // plugin found on this machine, and against the public BCFier Navisworks add-in.
    [Plugin("NavisworksDockPanel.Ribbon", "AcmeDev", DisplayName = "Kiewit Ribbon", ToolTip = "Adds the Kiewit ribbon tab")]
    [RibbonLayout("TestDockPaneRibbon.xaml")]
    [RibbonTab("TestDockPaneTab")]
    [Command("ActivateWebUiDockPane", DisplayName = "Clash Test Generation", ToolTip = "Show/hide the Clash Test Generation panel")]
    [Command("ActivateDeleteClashDockPane", DisplayName = "Delete Clash Tests", ToolTip = "Show/hide the Delete Clash Tests panel")]
    [Command("GroupZones", DisplayName = "Group Zones", ToolTip = "Show/hide the Group by Zone panel")]
    [Command("GroupInvolvingItems", DisplayName = "Group All Involving Items by Group", ToolTip = "Show/hide the Group All Involving Items by Group panel")]
    [Command("GroupByModel", DisplayName = "Group by Model Priority", ToolTip = "Show/hide the Group by Model Priority panel")]
    [Command("SignOffArea", DisplayName = "Sign Off Area", ToolTip = "Show/hide the Sign Off Area panel")]
    [Command("ClashesToViewpoints", DisplayName = "Clashes to Viewpoints", ToolTip = "Show/hide the Clashes to Viewpoints panel")]
    public class TestDockPaneRibbonPlugin : CommandHandlerPlugin
    {
        // Must match WebUiDockPanePlugin's [Plugin] Name + Vendor ("Name.Vendor").
        private const string DockPanePluginId = "NavisworksDockPanel.WebUiDockPane.AcmeDev";

        public override int ExecuteCommand(string name, params string[] parameters)
        {
            string view;
            switch (name)
            {
                case "ActivateWebUiDockPane": view = "generate"; break;
                case "ActivateDeleteClashDockPane": view = "delete"; break;
                case "GroupZones": view = "zones"; break;
                case "GroupInvolvingItems": view = "involvingItems"; break;
                case "GroupByModel": view = "modelPriority"; break;
                case "SignOffArea": view = "signOff"; break;
                case "ClashesToViewpoints": view = "clashesToViewpoints"; break;
                default: return 0;
            }

            try
            {
                PluginRecord record = Autodesk.Navisworks.Api.Application.Plugins.FindPlugin(DockPanePluginId);
                if (record == null)
                {
                    MessageBox.Show($"FindPlugin returned null for id '{DockPanePluginId}'.", "NavisworksDockPanel debug");
                    return 1;
                }

                if (!record.IsLoaded)
                {
                    record.LoadPlugin();
                }

                if (record.LoadedPlugin is WebUiDockPanePlugin dockPane)
                {
                    // Same button pressed again while its own view is already showing:
                    // toggle the pane closed, same as before this button had a sibling.
                    // Otherwise (pane hidden, or switching from the other view): show/bring
                    // to front and (re)point it at the requested view.
                    if (dockPane.Visible && dockPane.CurrentView == view)
                    {
                        dockPane.Visible = false;
                    }
                    else
                    {
                        dockPane.ShowView(view);
                        dockPane.Visible = true;
                        dockPane.ActivatePane();
                    }
                }
                else
                {
                    MessageBox.Show(
                        $"Plugin loaded but is not a WebUiDockPanePlugin (was: {record.LoadedPlugin?.GetType().FullName ?? "null"}).",
                        "NavisworksDockPanel debug");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "NavisworksDockPanel debug - ExecuteCommand failed");
            }

            return 0;
        }

        public override CommandState CanExecuteCommand(string name)
        {
            return new CommandState(true);
        }

        public override bool CanExecuteRibbonTab(string name)
        {
            return true;
        }

        public override bool TryShowCommandHelp(string name)
        {
            return false;
        }
    }
}
