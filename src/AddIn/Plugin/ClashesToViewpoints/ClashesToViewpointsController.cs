using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using NavisworksDockPanel.AddIn.Plugin.CoincidentGrouping;

namespace NavisworksDockPanel.AddIn.Plugin.ClashesToViewpoints
{
    // JSON-friendly facade over ClashesToViewpointsService for NativeMessageBridge - mirrors
    // ModelPriorityGroupingController's role. Reuses CoincidentGroupingController's
    // getClashGroupTree (same tests/groups tree already used by Group by Model Priority and
    // Group All Involving Items) so the React tree-selection UI doesn't need its own bridge
    // action.
    internal static class ClashesToViewpointsController
    {
        public static Dictionary<string, object> CreateViewpoints(List<Dictionary<string, object>> selections)
        {
            Document document = Application.ActiveDocument;
            if (document == null)
            {
                return Error("No active document.");
            }

            var docClash = document.Clash as DocumentClash;
            if (docClash == null)
            {
                return Error("Could not obtain the Clash module from the document.");
            }

            DocumentClashTests testsData = docClash.TestsData;
            List<CoincidentElementGroupingService.GroupSelection> groupSelections = ParseGroupSelections(selections);

            var selected = new Dictionary<ClashTest, List<ClashResultGroup>>();
            foreach (CoincidentElementGroupingService.GroupSelection selection in groupSelections)
            {
                if (selection.TestIndex < 0 || selection.TestIndex >= testsData.Tests.Count)
                {
                    continue;
                }

                if (!(testsData.Tests[selection.TestIndex] is ClashTest test))
                {
                    continue;
                }

                if (selection.GroupChildIndex < 0 || selection.GroupChildIndex >= test.Children.Count)
                {
                    continue;
                }

                if (!(test.Children[selection.GroupChildIndex] is ClashResultGroup group))
                {
                    continue;
                }

                if (!group.Children.OfType<ClashResult>().Any())
                {
                    continue;
                }

                if (!selected.TryGetValue(test, out List<ClashResultGroup> groups))
                {
                    groups = new List<ClashResultGroup>();
                    selected[test] = groups;
                }

                groups.Add(group);
            }

            if (selected.Count == 0)
            {
                return Error("No clash groups with results were selected.");
            }

            ClashesToViewpointsService.Result result;
            try
            {
                result = ClashesToViewpointsService.Execute(document, selected);
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }

            return new Dictionary<string, object>
            {
                ["errorMessage"] = result.ErrorMessage,
                ["foldersCreated"] = result.FoldersCreated,
                ["viewpointsCreated"] = result.ViewpointsCreated,
                ["totalGroups"] = result.TotalGroups
            };
        }

        private static Dictionary<string, object> Error(string message)
        {
            return new Dictionary<string, object>
            {
                ["errorMessage"] = message,
                ["foldersCreated"] = 0,
                ["viewpointsCreated"] = 0,
                ["totalGroups"] = 0
            };
        }

        private static List<CoincidentElementGroupingService.GroupSelection> ParseGroupSelections(
            List<Dictionary<string, object>> selections)
        {
            return (selections ?? new List<Dictionary<string, object>>())
                .Select(s => new CoincidentElementGroupingService.GroupSelection
                {
                    TestIndex = Convert.ToInt32(s["testIndex"]),
                    GroupChildIndex = Convert.ToInt32(s["groupChildIndex"])
                })
                .ToList();
        }
    }
}
