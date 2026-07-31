using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using NavisworksDockPanel.AddIn.Plugin.CoincidentGrouping;

namespace NavisworksDockPanel.AddIn.Plugin.ModelGrouping
{
    // JSON-friendly facade over ModelPriorityGroupingService for NativeMessageBridge - mirrors
    // CoincidentGroupingController's role for the Group All Involving Items view. Reuses
    // that view's group tree (getClashGroupTree) and the Clash Test Generation view's model
    // list (getModels) - no need to duplicate either on this feature's own actions.
    internal static class ModelPriorityGroupingController
    {
        // Own tree fetch for this feature (rather than reusing CoincidentGroupingController's
        // GetClashGroupTree) because a freshly-run clash test normally has its results sitting
        // directly under the ClashTest as flat ClashResults, never wrapped in a
        // ClashResultGroup - CoincidentGroupingController's version skips such tests entirely
        // (they have zero real groups), which left this feature with nothing to select on a
        // document that hadn't been manually grouped yet. Every test that has ANY results
        // (grouped or not) is included here; ungrouped results get a synthetic entry at
        // groupChildIndex -1 that ModelPriorityGroupingService treats as "the test itself".
        public static List<Dictionary<string, object>> GetClashTree()
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
                int ungroupedCount = 0;

                foreach (SavedItem child in test.Children)
                {
                    if (child is ClashResult)
                    {
                        ungroupedCount++;
                    }
                }

                for (int childIndex = 0; childIndex < test.Children.Count; childIndex++)
                {
                    if (!(test.Children[childIndex] is ClashResultGroup group))
                    {
                        continue;
                    }

                    groups.Add(new Dictionary<string, object>
                    {
                        ["groupChildIndex"] = childIndex,
                        ["groupName"] = group.DisplayName ?? "(unnamed)",
                        ["resultCount"] = group.Children.OfType<ClashResult>().Count()
                    });
                }

                if (ungroupedCount > 0)
                {
                    groups.Insert(0, new Dictionary<string, object>
                    {
                        ["groupChildIndex"] = -1,
                        ["groupName"] = "(Ungrouped Results)",
                        ["resultCount"] = ungroupedCount
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

        // Scans the currently selected groups' clash results and returns the distinct source
        // model names actually involved (via ClashMatrixGenerator.GetModelNameForItem), sorted
        // alphabetically. Called live as the user (de)selects groups in the tree, so the
        // "Models to Group" list only ever offers models that are actually relevant to what's
        // selected - never the whole document's model list.
        public static List<string> GetModelsInvolvedInGroups(List<Dictionary<string, object>> selections, List<string> statuses)
        {
            var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Document document = Application.ActiveDocument;
            if (document == null)
            {
                return models.ToList();
            }

            var docClash = document.Clash as DocumentClash;
            if (docClash == null)
            {
                return models.ToList();
            }

            DocumentClashTests testsData = docClash.TestsData;
            List<CoincidentElementGroupingService.GroupSelection> groupSelections = ParseGroupSelections(selections);
            List<ClashResultStatus> selectedStatuses = ParseStatuses(statuses);

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

                IEnumerable<SavedItem> children;
                if (selection.GroupChildIndex == -1)
                {
                    // Synthetic selection: the test's own ungrouped results (see GetClashTree).
                    children = test.Children;
                }
                else
                {
                    if (selection.GroupChildIndex < 0 || selection.GroupChildIndex >= test.Children.Count)
                    {
                        continue;
                    }

                    if (!(test.Children[selection.GroupChildIndex] is ClashResultGroup group))
                    {
                        continue;
                    }

                    children = group.Children;
                }

                foreach (SavedItem child in children)
                {
                    if (!(child is ClashResult result) || !selectedStatuses.Contains(result.Status))
                    {
                        continue;
                    }

                    string model1 = ClashMatrixGenerator.GetModelNameForItem(result.Item1);
                    string model2 = ClashMatrixGenerator.GetModelNameForItem(result.Item2);
                    if (!string.IsNullOrEmpty(model1))
                    {
                        models.Add(model1);
                    }

                    if (!string.IsNullOrEmpty(model2))
                    {
                        models.Add(model2);
                    }
                }
            }

            return models.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // Selects and focuses the given model's root item in the Navisworks view, so the
        // user can visually confirm which model a name in the "Models to Group" list actually
        // refers to before committing it to the priority order.
        public static Dictionary<string, object> SelectModelRoot(string modelName)
        {
            Document document = Application.ActiveDocument;
            if (document == null)
            {
                return Error("No active document.");
            }

            ModelItem root = ClashMatrixGenerator.GetModelRootItem(modelName);
            if (root == null)
            {
                return Error($"Could not find model '{modelName}'.");
            }

            document.CurrentSelection.CopyFrom(new ModelItemCollection { root });

            if (document.ActiveView != null)
            {
                document.ActiveView.FocusOnCurrentSelection();
            }

            return new Dictionary<string, object> { ["errorMessage"] = null };
        }

        public static Dictionary<string, object> GroupByModelPriority(
            List<Dictionary<string, object>> selections,
            List<string> statuses,
            List<string> modelPriority,
            bool removeSourceGroups,
            bool groupRemaining,
            string remainingGroupName)
        {
            Document document = Application.ActiveDocument;
            if (document == null)
            {
                return Error("No active document.");
            }

            List<CoincidentElementGroupingService.GroupSelection> groupSelections = ParseGroupSelections(selections);

            if (groupSelections.Count == 0)
            {
                return Error("Select at least one group.");
            }

            List<string> priority = (modelPriority ?? new List<string>())
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .ToList();
            if (priority.Count == 0)
            {
                return Error("Select at least one model to prioritize.");
            }

            List<ClashResultStatus> selectedStatuses = ParseStatuses(statuses);

            var options = new ModelPriorityGroupingService.Options
            {
                ModelPriority = priority,
                RemoveSourceGroups = removeSourceGroups,
                SelectedStatuses = selectedStatuses,
                GroupRemaining = groupRemaining,
                RemainingGroupName = string.IsNullOrWhiteSpace(remainingGroupName) ? "Other" : remainingGroupName
            };

            List<ModelPriorityGroupingService.Result> results;
            try
            {
                results = ModelPriorityGroupingService.ExecuteOnGroups(document, groupSelections, options);
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

        private static List<ClashResultStatus> ParseStatuses(List<string> statuses)
        {
            List<ClashResultStatus> selectedStatuses = (statuses ?? new List<string>())
                .Select(s => Enum.TryParse(s, true, out ClashResultStatus parsed) ? (ClashResultStatus?)parsed : null)
                .Where(s => s.HasValue)
                .Select(s => s.Value)
                .ToList();

            return selectedStatuses.Count == 0
                ? Enum.GetValues(typeof(ClashResultStatus)).Cast<ClashResultStatus>().ToList()
                : selectedStatuses;
        }
    }
}
