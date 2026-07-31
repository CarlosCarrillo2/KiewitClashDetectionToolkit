using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;

namespace NavisworksDockPanel.AddIn.Plugin.CoincidentGrouping
{
    // Ported from the other Kiewit Navisworks plugin's "Group All Involving Items by Group"
    // feature (CoincidentElementGroupingService.cs / CoincidentElementGroupingForm.cs). Takes
    // a caller-supplied set of existing ClashResultGroups, finds model items that appear in
    // 2+ of their results ("coincident" elements), and creates one new root-level group per
    // such item - moving matching results out of their source groups into it. Only the
    // matching/grouping engine was ported; the WinForms tree-picker dialog was left behind
    // since this project drives it from a React/shadcn dock pane view (GroupInvolvingItemsView.tsx).
    internal static class CoincidentElementGroupingService
    {
        internal sealed class Options
        {
            // When true the source groups are deleted after their results are regrouped
            // (only once they're empty - a source group with leftover ungrouped results stays).
            public bool RemoveSourceGroups { get; set; } = true;

            public IReadOnlyCollection<ClashResultStatus> SelectedStatuses { get; set; }
        }

        internal sealed class Result
        {
            public string TestName { get; set; }

            public int GroupsCreated { get; set; }

            public int ClashesGrouped { get; set; }

            public int ClashesUngrouped { get; set; }
        }

        // Identifies one ClashResultGroup by its position: testIndex into
        // DocumentClashTests.Tests, groupChildIndex into that ClashTest's own Children
        // collection (both stable for the short window between listing groups and running).
        internal sealed class GroupSelection
        {
            public int TestIndex { get; set; }

            public int GroupChildIndex { get; set; }
        }

        public static List<Result> ExecuteOnGroups(Document document, IReadOnlyList<GroupSelection> selections, Options options)
        {
            if (selections == null || selections.Count == 0)
            {
                throw new ArgumentException("Select at least one group.", nameof(selections));
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

            var entries = new List<(ClashTest Test, ClashResultGroup Group)>();
            foreach (GroupSelection selection in selections)
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

                if (test.Children[selection.GroupChildIndex] is ClashResultGroup group)
                {
                    entries.Add((test, group));
                }
            }

            var results = new List<Result>();
            foreach (var testGroup in entries.GroupBy(e => e.Test))
            {
                ClashTest test = testGroup.Key;
                List<ClashResultGroup> sourceGroups = testGroup.Select(e => e.Group).ToList();
                results.Add(RunOnGroups(testsData, test, sourceGroups, options));
            }

            return results;
        }

        private static Result RunOnGroups(
            DocumentClashTests testsData,
            ClashTest test,
            List<ClashResultGroup> sourceGroups,
            Options options)
        {
            var summary = new Result { TestName = test.DisplayName ?? "(unnamed)" };

            var allResults = new List<ClashResult>();
            var resultToSource = new Dictionary<ClashResult, string>();

            foreach (ClashResultGroup group in sourceGroups)
            {
                string groupName = group.DisplayName ?? "(unnamed)";
                foreach (SavedItem child in group.Children)
                {
                    if (child is ClashResult result && PassesFilter(result, options.SelectedStatuses))
                    {
                        allResults.Add(result);
                        resultToSource[result] = groupName;
                    }
                }
            }

            if (allResults.Count == 0)
            {
                return summary;
            }

            var frequency = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (ClashResult result in allResults)
            {
                Register(result.Item1, frequency);
                Register(result.Item2, frequency);
            }

            var coincident = new HashSet<string>(
                frequency.Where(kv => kv.Value >= 2).Select(kv => kv.Key),
                StringComparer.Ordinal);

            if (coincident.Count == 0)
            {
                return summary;
            }

            var pivotCount = new Dictionary<string, int>(StringComparer.Ordinal);
            var pivotSources = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            foreach (ClashResult result in allResults)
            {
                string pivot = ChoosePivot(result, frequency, coincident);
                if (pivot == null)
                {
                    continue;
                }

                pivotCount[pivot] = pivotCount.TryGetValue(pivot, out int v) ? v + 1 : 1;

                if (!pivotSources.TryGetValue(pivot, out HashSet<string> src))
                {
                    pivotSources[pivot] = src = new HashSet<string>(StringComparer.Ordinal);
                }

                if (resultToSource.TryGetValue(result, out string sourceName))
                {
                    src.Add(sourceName);
                }
            }

            var pivotGroup = new Dictionary<string, ClashResultGroup>(StringComparer.Ordinal);
            int groupCounter = 1;
            foreach (KeyValuePair<string, int> kv in pivotCount)
            {
                if (kv.Value < 2)
                {
                    continue;
                }

                string sources = pivotSources.TryGetValue(kv.Key, out HashSet<string> s)
                    ? string.Join(" · ", s.OrderBy(x => x))
                    : string.Empty;
                string groupName = string.IsNullOrEmpty(sources)
                    ? $"Group {groupCounter} ({kv.Value} clashes)"
                    : $"{sources} - Group {groupCounter} ({kv.Value} clashes)";

                int insertIndex = test.Children.Count;
                testsData.TestsInsertCopy(test, insertIndex, new ClashResultGroup { DisplayName = groupName });

                if (test.Children[insertIndex] is ClashResultGroup created)
                {
                    pivotGroup[kv.Key] = created;
                    summary.GroupsCreated++;
                    groupCounter++;
                }
            }

            if (pivotGroup.Count == 0)
            {
                return summary;
            }

            foreach (ClashResultGroup sourceGroup in sourceGroups)
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

                    string pivot = ChoosePivot(result, frequency, coincident);
                    if (pivot == null || !pivotGroup.TryGetValue(pivot, out ClashResultGroup target))
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
                foreach (ClashResultGroup sourceGroup in sourceGroups)
                {
                    if (sourceGroup.Children.Count == 0)
                    {
                        testsData.TestsRemove(test, sourceGroup);
                    }
                }
            }

            return summary;
        }

        private static string ChoosePivot(ClashResult result, Dictionary<string, int> frequency, HashSet<string> coincident)
        {
            string k1 = GetItemKey(result.Item1);
            string k2 = GetItemKey(result.Item2);

            bool c1 = !string.IsNullOrEmpty(k1) && coincident.Contains(k1);
            bool c2 = !string.IsNullOrEmpty(k2) && coincident.Contains(k2);

            if (!c1 && !c2) return null;
            if (c1 && !c2) return k1;
            if (!c1) return k2;

            int f1 = frequency.TryGetValue(k1, out int v1) ? v1 : 0;
            int f2 = frequency.TryGetValue(k2, out int v2) ? v2 : 0;
            return f1 >= f2 ? k1 : k2;
        }

        private static void Register(ModelItem item, Dictionary<string, int> frequency)
        {
            string key = GetItemKey(item);
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            frequency[key] = frequency.TryGetValue(key, out int v) ? v + 1 : 1;
        }

        private static bool PassesFilter(ClashResult result, IReadOnlyCollection<ClashResultStatus> statuses)
        {
            if (statuses == null || statuses.Count == 0)
            {
                return true;
            }

            return statuses.Contains(result.Status);
        }

        // Stable key for a ModelItem. Priority: Revit element id -> Navisworks path id ->
        // instance guid -> a textual fallback (display name + category + root ancestor name).
        internal static string GetItemKey(ModelItem item)
        {
            if (item == null)
            {
                return string.Empty;
            }

            try
            {
                foreach (PropertyCategory category in item.PropertyCategories)
                {
                    string categoryName = category.DisplayName ?? string.Empty;
                    bool isRevitCategory =
                        categoryName.IndexOf("revit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        categoryName.IndexOf("element", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!isRevitCategory)
                    {
                        continue;
                    }

                    foreach (DataProperty property in category.Properties)
                    {
                        string propertyName = property.DisplayName ?? string.Empty;
                        bool isIdProperty =
                            propertyName.Equals("Element Id", StringComparison.OrdinalIgnoreCase) ||
                            propertyName.Equals("UniqueId", StringComparison.OrdinalIgnoreCase) ||
                            propertyName.Equals("Id", StringComparison.OrdinalIgnoreCase);
                        if (!isIdProperty)
                        {
                            continue;
                        }

                        string value = SafePropertyValue(property);
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            return "r:" + value;
                        }
                    }
                }
            }
            catch
            {
            }

            try
            {
                Document document = Application.ActiveDocument;
                string pathId = document?.Models?.CreatePathId(item)?.PathId ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(pathId))
                {
                    return "p:" + pathId;
                }
            }
            catch
            {
            }

            try
            {
                if (item.InstanceGuid != Guid.Empty)
                {
                    return "g:" + item.InstanceGuid.ToString("D");
                }
            }
            catch
            {
            }

            string displayName2 = item.DisplayName ?? string.Empty;
            string category2 = GetFirstCategoryValue(item);
            string sourceFile = GetRootAncestorName(item);
            return $"f:{displayName2}|{category2}|{sourceFile}";
        }

        private static string SafePropertyValue(DataProperty property)
        {
            try
            {
                return property?.Value?.ToDisplayString() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetFirstCategoryValue(ModelItem item)
        {
            try
            {
                foreach (PropertyCategory category in item.PropertyCategories)
                {
                    if ((category.DisplayName ?? string.Empty).IndexOf("category", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    foreach (DataProperty property in category.Properties)
                    {
                        string value = SafePropertyValue(property);
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            return value;
                        }
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string GetRootAncestorName(ModelItem item)
        {
            try
            {
                ModelItem current = item;
                while (current?.Parent != null)
                {
                    current = current.Parent;
                }

                return current?.DisplayName ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
