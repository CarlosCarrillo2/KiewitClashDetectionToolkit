using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;

namespace NavisworksDockPanel.AddIn.Plugin.CoincidentGrouping
{
    // JSON-friendly facade over CoincidentElementGroupingService for NativeMessageBridge -
    // mirrors ZoneGroupingController's role for the Group by Zone view. Groups are addressed
    // by (testIndex, groupChildIndex) - both are positions in DocumentClashTests.Tests /
    // ClashTest.Children, real and stable for the short window between listing groups in the
    // UI and running the grouping.
    internal static class CoincidentGroupingController
    {
        // Returns one entry per ClashTest that has at least one root-level ClashResultGroup:
        // { testIndex, testName, groups: [{ groupChildIndex, groupName, resultCount }] }
        public static List<Dictionary<string, object>> GetClashGroupTree()
        {
            var result = new List<Dictionary<string, object>>();
            Document document = Application.ActiveDocument;
            if (document == null)
            {
                return result;
            }

            var docClash = document.Clash as DocumentClash;
            if (docClash == null)
            {
                return result;
            }

            DocumentClashTests testsData = docClash.TestsData;
            for (int testIndex = 0; testIndex < testsData.Tests.Count; testIndex++)
            {
                if (!(testsData.Tests[testIndex] is ClashTest test))
                {
                    continue;
                }

                var groups = new List<Dictionary<string, object>>();
                for (int childIndex = 0; childIndex < test.Children.Count; childIndex++)
                {
                    if (!(test.Children[childIndex] is ClashResultGroup group))
                    {
                        continue;
                    }

                    int resultCount = group.Children.OfType<ClashResult>().Count();
                    groups.Add(new Dictionary<string, object>
                    {
                        ["groupChildIndex"] = childIndex,
                        ["groupName"] = group.DisplayName ?? "(unnamed)",
                        ["resultCount"] = resultCount
                    });
                }

                if (groups.Count == 0)
                {
                    continue;
                }

                result.Add(new Dictionary<string, object>
                {
                    ["testIndex"] = testIndex,
                    ["testName"] = test.DisplayName ?? "(unnamed)",
                    ["groups"] = groups
                });
            }

            return result;
        }

        public static Dictionary<string, object> GroupCoincidentElements(
            List<Dictionary<string, object>> selections,
            List<string> statuses,
            bool removeSourceGroups)
        {
            Document document = Application.ActiveDocument;
            if (document == null)
            {
                return Error("No active document.");
            }

            var groupSelections = (selections ?? new List<Dictionary<string, object>>())
                .Select(s => new CoincidentElementGroupingService.GroupSelection
                {
                    TestIndex = Convert.ToInt32(s["testIndex"]),
                    GroupChildIndex = Convert.ToInt32(s["groupChildIndex"])
                })
                .ToList();

            if (groupSelections.Count == 0)
            {
                return Error("Select at least one group.");
            }

            List<ClashResultStatus> selectedStatuses = (statuses ?? new List<string>())
                .Select(s => Enum.TryParse(s, true, out ClashResultStatus parsed) ? (ClashResultStatus?)parsed : null)
                .Where(s => s.HasValue)
                .Select(s => s.Value)
                .ToList();
            if (selectedStatuses.Count == 0)
            {
                selectedStatuses = Enum.GetValues(typeof(ClashResultStatus)).Cast<ClashResultStatus>().ToList();
            }

            var options = new CoincidentElementGroupingService.Options
            {
                RemoveSourceGroups = removeSourceGroups,
                SelectedStatuses = selectedStatuses
            };

            List<CoincidentElementGroupingService.Result> results;
            try
            {
                results = CoincidentElementGroupingService.ExecuteOnGroups(document, groupSelections, options);
            }
            catch (Exception ex)
            {
                return Error(ex.Message);
            }

            return new Dictionary<string, object>
            {
                ["errorMessage"] = null,
                ["groupsCreated"] = results.Sum(r => r.GroupsCreated),
                ["clashesGrouped"] = results.Sum(r => r.ClashesGrouped),
                ["clashesUngrouped"] = results.Sum(r => r.ClashesUngrouped)
            };
        }

        private static Dictionary<string, object> Error(string message)
        {
            return new Dictionary<string, object>
            {
                ["errorMessage"] = message,
                ["groupsCreated"] = 0,
                ["clashesGrouped"] = 0,
                ["clashesUngrouped"] = 0
            };
        }
    }
}
