using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using NavisworksDockPanel.AddIn.Plugin.CoincidentGrouping;

namespace NavisworksDockPanel.AddIn.Plugin.ModelGrouping
{
    // Groups clash results from existing ClashResultGroups by which source MODEL they
    // involve, in a caller-supplied priority order - "group all remaining clashes touching
    // model 1 first, then all remaining clashes touching model 2", etc. Mirrors
    // CoincidentElementGroupingService's shape (same GroupSelection addressing, same
    // status-filter/remove-source-groups options) but pivots on model identity instead of
    // "coincident item".
    internal static class ModelPriorityGroupingService
    {
        internal sealed class Options
        {
            // Model names in priority order (index 0 = highest priority). A result whose
            // Item1 or Item2 belongs to the first model in this list that matches gets moved
            // there - even if it also touches a lower-priority model in the list.
            public IReadOnlyList<string> ModelPriority { get; set; } = new List<string>();

            public bool RemoveSourceGroups { get; set; } = true;

            public IReadOnlyCollection<ClashResultStatus> SelectedStatuses { get; set; }

            // When true, results that don't touch any priority model are collected into one
            // extra group (RemainingGroupName) instead of being left where they are.
            public bool GroupRemaining { get; set; }

            public string RemainingGroupName { get; set; } = "Other";
        }

        internal sealed class Result
        {
            public string TestName { get; set; }

            public int GroupsCreated { get; set; }

            public int ClashesGrouped { get; set; }

            public int ClashesUngrouped { get; set; }
        }

        public static List<Result> ExecuteOnGroups(
            Document document,
            IReadOnlyList<CoincidentElementGroupingService.GroupSelection> selections,
            Options options)
        {
            if (selections == null || selections.Count == 0)
            {
                throw new ArgumentException("Select at least one group.", nameof(selections));
            }

            if (options.ModelPriority == null || options.ModelPriority.Count == 0)
            {
                throw new ArgumentException("Select at least one model to prioritize.", nameof(options));
            }

            if (document == null || document.IsClear)
            {
                throw new InvalidOperationException("No Navisworks document is open.");
            }

            var docClash = document.Clash as DocumentClash;
            if (docClash == null)
            {
                throw new InvalidOperationException("Could not obtain the Clash module from the document.");
            }

            DocumentClashTests testsData = docClash.TestsData;

            // GroupChildIndex == -1 is a synthetic selection meaning "this test's own
            // ungrouped results" (ClashResults sitting directly under the ClashTest, never
            // wrapped in a ClashResultGroup - the normal state of a freshly-run test that
            // hasn't been manually grouped yet). ClashTest derives from GroupItem just like
            // ClashResultGroup does, so it can be used as a source container the same way.
            var entries = new List<(ClashTest Test, GroupItem Group)>();
            foreach (CoincidentElementGroupingService.GroupSelection selection in selections)
            {
                if (selection.TestIndex < 0 || selection.TestIndex >= testsData.Tests.Count)
                {
                    continue;
                }

                if (!(testsData.Tests[selection.TestIndex] is ClashTest test))
                {
                    continue;
                }

                if (selection.GroupChildIndex == -1)
                {
                    entries.Add((test, test));
                    continue;
                }

                if (selection.GroupChildIndex < 0 || selection.GroupChildIndex >= test.Children.Count)
                {
                    continue;
                }

                if (test.Children[selection.GroupChildIndex] is ClashResultGroup group)
                {
                    entries.Add((test, group));
                }
            }

            var results = new List<Result>();
            foreach (var testGroup in entries.GroupBy(e => e.Test))
            {
                ClashTest test = testGroup.Key;
                List<GroupItem> sourceGroups = testGroup.Select(e => e.Group).ToList();
                results.Add(RunOnGroups(testsData, test, sourceGroups, options));
            }

            return results;
        }

        private static Result RunOnGroups(
            DocumentClashTests testsData,
            ClashTest test,
            List<GroupItem> sourceGroups,
            Options options)
        {
            var summary = new Result { TestName = test.DisplayName ?? "(unnamed)" };

            var allResults = new List<ClashResult>();
            foreach (GroupItem group in sourceGroups)
            {
                foreach (SavedItem child in group.Children)
                {
                    if (child is ClashResult result && PassesFilter(result, options.SelectedStatuses))
                    {
                        allResults.Add(result);
                    }
                }
            }

            if (allResults.Count == 0)
            {
                return summary;
            }

            // Decide each result's destination up front (priority order, first match wins) -
            // move order doesn't matter afterward, same as CoincidentElementGroupingService's
            // pivot precompute.
            var pivotByResult = allResults.ToDictionary(r => r, r => ChooseModelPivot(r, options.ModelPriority));

            var countByModel = pivotByResult.Values
                .Where(p => p != null)
                .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            var pivotGroup = new Dictionary<string, ClashResultGroup>(StringComparer.OrdinalIgnoreCase);

            // Create groups in priority order (not dictionary enumeration order) so the
            // higher-priority model's group is always created - and therefore appears -
            // first in Clash Detective's tree.
            foreach (string modelName in options.ModelPriority)
            {
                if (!countByModel.TryGetValue(modelName, out int count) || count == 0)
                {
                    continue;
                }

                ClashResultGroup created = CreateGroup(testsData, test, $"{modelName} ({count} clashes)");
                pivotGroup[modelName] = created;
                summary.GroupsCreated++;
            }

            ClashResultGroup remainingGroup = null;
            if (options.GroupRemaining && pivotByResult.Values.Any(p => p == null))
            {
                remainingGroup = CreateGroup(testsData, test, options.RemainingGroupName);
                summary.GroupsCreated++;
            }

            foreach (GroupItem sourceGroup in sourceGroups)
            {
                for (int i = sourceGroup.Children.Count - 1; i >= 0; i--)
                {
                    if (!(sourceGroup.Children[i] is ClashResult result))
                    {
                        continue;
                    }

                    if (!PassesFilter(result, options.SelectedStatuses))
                    {
                        continue;
                    }

                    string pivot = pivotByResult.TryGetValue(result, out string p) ? p : null;
                    ClashResultGroup target = pivot != null && pivotGroup.TryGetValue(pivot, out ClashResultGroup g)
                        ? g
                        : remainingGroup;

                    if (target == null)
                    {
                        continue;
                    }

                    testsData.TestsMove(sourceGroup, i, target, target.Children.Count);
                    summary.ClashesGrouped++;
                }
            }

            summary.ClashesUngrouped = allResults.Count - summary.ClashesGrouped;

            if (options.RemoveSourceGroups)
            {
                // The test itself can appear as a "source group" (the ungrouped-results
                // pseudo-selection above) - it can't be removed from itself, so only real
                // ClashResultGroup containers are candidates for cleanup here.
                foreach (GroupItem sourceGroup in sourceGroups)
                {
                    if (sourceGroup is ClashResultGroup realGroup && realGroup.Children.Count == 0)
                    {
                        testsData.TestsRemove(test, realGroup);
                    }
                }
            }

            return summary;
        }

        // First model in priority order that this result touches (via Item1 or Item2) - or
        // null if it touches none of them, meaning it's left alone (or swept into the
        // "remaining" group when GroupRemaining is set).
        private static string ChooseModelPivot(ClashResult result, IReadOnlyList<string> modelPriority)
        {
            string model1 = ClashMatrixGenerator.GetModelNameForItem(result.Item1);
            string model2 = ClashMatrixGenerator.GetModelNameForItem(result.Item2);

            foreach (string modelName in modelPriority)
            {
                if (string.Equals(model1, modelName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(model2, modelName, StringComparison.OrdinalIgnoreCase))
                {
                    return modelName;
                }
            }

            return null;
        }

        private static ClashResultGroup CreateGroup(DocumentClashTests testsData, ClashTest test, string groupName)
        {
            var group = new ClashResultGroup { DisplayName = groupName };
            int insertIndex = test.Children.Count;
            testsData.TestsInsertCopy(test, insertIndex, group);
            return test.Children[insertIndex] as ClashResultGroup
                ?? throw new InvalidOperationException($"Unable to create clash result group '{groupName}'.");
        }

        private static bool PassesFilter(ClashResult result, IReadOnlyCollection<ClashResultStatus> statuses)
        {
            if (statuses == null || statuses.Count == 0)
            {
                return true;
            }

            return statuses.Contains(result.Status);
        }
    }
}
