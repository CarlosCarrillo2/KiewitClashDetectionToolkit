using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using Autodesk.Navisworks.Api.ComApi;
using Autodesk.Navisworks.Api.Interop.ComApi;

namespace NavisworksDockPanel.AddIn.Plugin.ZoneGrouping
{
    // Ported from the other Kiewit Navisworks plugin's "Group by Zone V2" feature
    // (VolumeClashGroupingService.cs) - this is the shared matching/grouping engine used by
    // both its V1 and V2 dialogs there. Only the matching/grouping engine and its data
    // classes were ported; the WinForms dialog, progress bar, and visual-debug/redline
    // inspector tooling were left behind since this project drives the same engine from a
    // React/shadcn dock pane view (GroupZonesView.tsx) instead of a native dialog - see
    // ZoneGroupingController.cs for the JSON-friendly entry points the native message bridge
    // calls, and ExecuteGrouping below (adapted to return a summary string instead of
    // showing its own MessageBox, and to take the existing-groups decision as a parameter
    // instead of prompting for it itself).
    internal static class VolumeClashGroupingService
    {
        private const string DefaultNameLabel = "Volume";
        private const string OutsideAreasGroupName = "OUTSIDE AREAS";
        private const double VolumeMatchTolerance = 10.0;
        private const double GeometryRayOffset = 0.01;
        private const double GeometryIntersectionTolerance = 0.0001;
        private const double PlanarMeshZThreshold = 1.0;

        internal enum ExistingGroupHandling
        {
            KeepAndRegroup,
            RemoveAndRegroup
        }

        internal static ZoneGroupingResult ExecuteGrouping(
            Document document,
            GeometryModelOption geometryModel,
            IReadOnlyList<VolumeCandidate> selectedVolumes,
            IReadOnlyCollection<ClashResultStatus> selectedStatuses,
            IReadOnlyCollection<string> selectedTestDisplayNames,
            ExistingGroupHandling groupHandling,
            string singleGroupName = null,
            bool groupOutsideAreas = false)
        {
            IReadOnlyList<ClashTest> allTests = GetExistingClashTests(document);
            if (allTests.Count == 0)
            {
                return ZoneGroupingResult.Error("The active Navisworks document does not contain any clash tests to group.");
            }

            IReadOnlyList<ClashTest> tests = selectedTestDisplayNames != null && selectedTestDisplayNames.Count > 0
                ? allTests.Where(t => selectedTestDisplayNames.Contains(t.DisplayName, StringComparer.OrdinalIgnoreCase)).ToList()
                : allTests;

            if (tests.Count == 0)
            {
                return ZoneGroupingResult.Error("None of the selected clash tests were found in the document.");
            }

            int groupedCount = 0;
            int processedTests = 0;
            var groupingSummaries = new List<TestGroupingSummary>();
            var unmatchedEntries = new List<UnmatchedClashDebugEntry>();
            var geometryCache = new Dictionary<Guid, VolumeGeometryMesh>();

            foreach (ClashTest test in tests)
            {
                TestGroupingSummary summary = GroupResultsByVolume(
                    document,
                    test,
                    selectedVolumes,
                    selectedStatuses,
                    groupHandling,
                    geometryModel,
                    geometryCache,
                    unmatchedEntries,
                    singleGroupName,
                    groupOutsideAreas);

                groupedCount += summary.GroupedResultsCount;
                if (summary.GroupNames.Count > 0)
                {
                    groupingSummaries.Add(summary);
                }

                processedTests++;
            }

            string unmatchedLogPath = null;
            if (unmatchedEntries.Count > 0)
            {
                PluginDiagnostics.WriteUnmatchedLog(BuildUnmatchedDebugLog(unmatchedEntries));
                unmatchedLogPath = PluginDiagnostics.UnmatchedLogFilePath;
            }

            List<string> uniqueGroupNames = groupingSummaries
                .SelectMany(summary => summary.GroupNames)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new ZoneGroupingResult
            {
                ProcessedTests = processedTests,
                GroupedResults = groupedCount,
                GroupNames = uniqueGroupNames,
                UnmatchedCount = unmatchedEntries.Count,
                UnmatchedLogPath = unmatchedLogPath
            };
        }

        internal static List<GeometryModelOption> GetGeometryModels(Document document)
        {
            var models = new List<GeometryModelOption>();
            var datedModels = new List<Tuple<GeometryModelOption, DateTime>>();

            if (ShouldUseNwdHierarchyFallback(document))
            {
                PluginDiagnostics.Write("Using NWD hierarchy fallback for geometry model selection.");
                return GetGeometryModelsFromNwdHierarchy(document);
            }

            for (int i = 0; i < document.Models.Count; i++)
            {
                Model model = document.Models[i];
                if (!IsGeometryModelCandidate(model))
                {
                    continue;
                }

                string filePath = GetBestModelFilePath(model);
                string displayName = GetModelDisplayName(model, i, filePath);
                var option = new GeometryModelOption(model, i, filePath, displayName);
                models.Add(option);

                DateTime lastWriteUtc;
                if (TryGetLastWriteUtc(filePath, out lastWriteUtc))
                {
                    datedModels.Add(Tuple.Create(option, lastWriteUtc));
                }
            }

            GeometryModelOption latest = datedModels
                .OrderByDescending(tuple => tuple.Item2)
                .ThenByDescending(tuple => tuple.Item1.ModelIndex)
                .Select(tuple => tuple.Item1)
                .FirstOrDefault();

            if (latest == null && models.Count > 0)
            {
                latest = models.OrderByDescending(model => model.ModelIndex).First();
            }

            if (latest != null)
            {
                latest.IsLatest = true;
                latest.DisplayLabel = latest.DisplayLabel + " (Latest)";
            }

            return models
                .OrderByDescending(model => model.IsLatest)
                .ThenBy(model => model.DisplayLabel, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal static IReadOnlyList<VolumeCandidate> GetVolumeCandidates(
            Document document,
            IReadOnlyList<GeometryModelOption> geometryModels,
            GeometryModelOption selectedGeometry)
        {
            if (selectedGeometry == null)
            {
                return Array.Empty<VolumeCandidate>();
            }

            List<VolumeCandidate> candidates = BuildVolumeCandidates(document, new[] { selectedGeometry });

            return candidates
                .OrderBy(candidate => candidate.SourceModelName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.DisplayLabel, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<VolumeCandidate> BuildVolumeCandidates(Document document, IEnumerable<GeometryModelOption> sourceModels)
        {
            var candidates = new List<VolumeCandidate>();
            var seenGuids = new HashSet<Guid>();

            foreach (GeometryModelOption modelOption in sourceModels)
            {
                IEnumerable<ModelItem> items = GetItemsForGeometryModel(modelOption);
                foreach (ModelItem item in items)
                {
                    if (!item.HasGeometry)
                    {
                        continue;
                    }

                    Guid itemGuid = item.InstanceGuid;
                    if (itemGuid == Guid.Empty || !seenGuids.Add(itemGuid))
                    {
                        continue;
                    }

                    VolumeMetadata metadata = GetVolumeMetadata(item, modelOption.DisplayName);
                    if (!metadata.IsCandidate)
                    {
                        continue;
                    }

                    candidates.Add(new VolumeCandidate(document, modelOption.ModelIndex, item, modelOption.DisplayName, metadata));
                }
            }

            return candidates;
        }

        private static bool ShouldUseNwdHierarchyFallback(Document document)
        {
            if (document == null || document.Models.Count != 1)
            {
                return false;
            }

            string fileName = document.FileName ?? string.Empty;
            return fileName.EndsWith(".nwd", StringComparison.OrdinalIgnoreCase);
        }

        private static List<GeometryModelOption> GetGeometryModelsFromNwdHierarchy(Document document)
        {
            var models = new List<GeometryModelOption>();
            Model containerModel = document.Models[0];
            ModelItem rootItem = containerModel?.RootItem;
            if (rootItem == null)
            {
                return models;
            }

            int childIndex = 0;
            foreach (ModelItem child in rootItem.Children)
            {
                if (!HasGeometryInSubtree(child) || LooksLikeViewContainer(CleanText(child.DisplayName)))
                {
                    childIndex++;
                    continue;
                }

                string displayName = GetHierarchyNodeDisplayName(child, childIndex);
                models.Add(new GeometryModelOption(containerModel, 0, document.FileName, displayName, child, displayName));
                childIndex++;
            }

            if (models.Count == 0 && HasGeometryInSubtree(rootItem))
            {
                string fallbackName = GetModelDisplayName(containerModel, 0, document.FileName);
                models.Add(new GeometryModelOption(containerModel, 0, document.FileName, fallbackName, rootItem, fallbackName));
            }

            return models
                .OrderBy(model => model.DisplayLabel, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static IEnumerable<ModelItem> GetItemsForGeometryModel(GeometryModelOption modelOption)
        {
            ModelItem scopeRoot = modelOption?.ScopeRootItem;
            if (scopeRoot == null)
            {
                return Enumerable.Empty<ModelItem>();
            }

            return scopeRoot.DescendantsAndSelf;
        }

        private static bool HasGeometryInSubtree(ModelItem item)
        {
            return item != null && item.DescendantsAndSelf.Any(descendant => descendant.HasGeometry);
        }

        private static string GetHierarchyNodeDisplayName(ModelItem item, int childIndex)
        {
            string displayName = CleanText(item?.DisplayName);
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                return displayName;
            }

            return $"Hierarchy Node {childIndex + 1}";
        }

        internal static TestGroupingSummary GroupResultsByVolume(
            Document document,
            ClashTest test,
            IReadOnlyList<VolumeCandidate> selectedVolumes,
            IReadOnlyCollection<ClashResultStatus> selectedStatuses,
            ExistingGroupHandling groupHandling,
            GeometryModelOption geometryModel,
            IDictionary<Guid, VolumeGeometryMesh> geometryCache,
            IList<UnmatchedClashDebugEntry> unmatchedEntries,
            string singleGroupName = null,
            bool groupOutsideAreas = false)
        {
            PluginDiagnostics.Write($"GroupResultsByVolume started for test '{test?.DisplayName}'.");
            DocumentClashTests testsData = GetClashTests(document);
            var volumesByGuid = selectedVolumes.ToDictionary(volume => volume.InstanceGuid);
            var summary = new TestGroupingSummary(test?.DisplayName ?? "Unnamed Test");

            if (groupHandling == ExistingGroupHandling.RemoveAndRegroup)
            {
                RemoveExistingGroups(testsData, test);
            }

            var groupsByName = GetRootGroupsByName(test);

            summary.GroupedResultsCount += RegroupResultsInContainer(
                document,
                test,
                test,
                testsData,
                volumesByGuid,
                selectedStatuses,
                groupsByName,
                summary,
                geometryModel,
                geometryCache,
                unmatchedEntries,
                singleGroupName,
                groupOutsideAreas);

            return summary;
        }

        private static int RegroupResultsInContainer(
            Document document,
            ClashTest test,
            GroupItem container,
            DocumentClashTests testsData,
            IReadOnlyDictionary<Guid, VolumeCandidate> volumesByGuid,
            IReadOnlyCollection<ClashResultStatus> selectedStatuses,
            IDictionary<string, ClashResultGroup> groupsByName,
            TestGroupingSummary summary,
            GeometryModelOption geometryModel,
            IDictionary<Guid, VolumeGeometryMesh> geometryCache,
            IList<UnmatchedClashDebugEntry> unmatchedEntries,
            string singleGroupName = null,
            bool groupOutsideAreas = false)
        {
            int groupedResults = 0;

            for (int index = container.Children.Count - 1; index >= 0; index--)
            {
                SavedItem child;
                try
                {
                    child = container.Children[index];
                }
                catch (Exception ex)
                {
                    PluginDiagnostics.WriteException($"Failed reading container child at index {index}", ex);
                    throw;
                }

                if (child is ClashResultGroup nestedGroup)
                {
                    groupedResults += RegroupResultsInContainer(
                        document,
                        test,
                        nestedGroup,
                        testsData,
                        volumesByGuid,
                        selectedStatuses,
                        groupsByName,
                        summary,
                        geometryModel,
                        geometryCache,
                        unmatchedEntries,
                        singleGroupName,
                        groupOutsideAreas);

                    if (nestedGroup.Children.Count == 0)
                    {
                        PluginDiagnostics.Write($"Removing empty group '{nestedGroup.DisplayName}'.");
                        testsData.TestsRemove(container, nestedGroup);
                    }

                    continue;
                }

                ClashResult result = child as ClashResult;
                if (result == null)
                {
                    PluginDiagnostics.Write($"Skipping non-result child at index {index}.");
                    continue;
                }

                if (!ShouldProcessResultStatus(result, selectedStatuses))
                {
                    PluginDiagnostics.Write(
                        $"Skipping clash result at index {index} because status '{result.Status}' is not enabled in the filter.");
                    continue;
                }

                PluginDiagnostics.Write($"Processing clash result at index {index}.");
                VolumeMatchResult match = MatchVolume(document, result, volumesByGuid, geometryCache, index);
                VolumeCandidate matchedVolume = match?.Volume;
                if (matchedVolume == null)
                {
                    unmatchedEntries?.Add(CreateUnmatchedEntry(test, result, geometryModel, volumesByGuid.Values, match));
                }

                string targetGroupName = singleGroupName != null && matchedVolume != null
                    ? singleGroupName
                    : GetTargetGroupName(matchedVolume, groupOutsideAreas);
                if (string.IsNullOrWhiteSpace(targetGroupName))
                {
                    PluginDiagnostics.Write($"No selected volume match found for result at index {index}.");
                    continue;
                }

                string matchType = match?.MatchType ?? "unmatched";
                PluginDiagnostics.Write(
                    $"Match analysis for result {index}: bbox={match?.BoundingBoxSummary ?? "none"} | " +
                    $"geometry={match?.GeometrySummary ?? "none"} | final={match?.DecisionSummary ?? "unmatched"}.");
                PluginDiagnostics.Write(
                    $"Matched result at index {index} to '{targetGroupName}' using {matchType} match.");

                ClashResultGroup targetGroup;
                if (!groupsByName.TryGetValue(targetGroupName, out targetGroup))
                {
                    PluginDiagnostics.Write($"Creating group '{targetGroupName}'.");
                    targetGroup = CreateGroup(testsData, test, targetGroupName);
                    groupsByName.Add(targetGroupName, targetGroup);
                }

                if (ReferenceEquals(container, targetGroup))
                {
                    summary.GroupNames.Add(targetGroupName);
                    PluginDiagnostics.Write($"Result at index {index} already belongs to group '{targetGroupName}'.");
                    continue;
                }

                PluginDiagnostics.Write($"Moving result at index {index} into group '{targetGroupName}'.");
                testsData.TestsMove(container, index, targetGroup, targetGroup.Children.Count);
                summary.GroupNames.Add(targetGroupName);
                groupedResults++;
            }

            return groupedResults;
        }

        private static bool ShouldProcessResultStatus(
            ClashResult result,
            IReadOnlyCollection<ClashResultStatus> selectedStatuses)
        {
            if (result == null)
            {
                return false;
            }

            if (selectedStatuses == null || selectedStatuses.Count == 0)
            {
                return true;
            }

            return selectedStatuses.Contains(result.Status);
        }

        private static string GetTargetGroupName(VolumeCandidate matchedVolume, bool groupOutsideAreas)
        {
            if (matchedVolume == null)
            {
                return groupOutsideAreas ? OutsideAreasGroupName : null;
            }

            return matchedVolume.GroupName;
        }

        private static UnmatchedClashDebugEntry CreateUnmatchedEntry(
            ClashTest test,
            ClashResult result,
            GeometryModelOption geometryModel,
            IEnumerable<VolumeCandidate> volumes,
            VolumeMatchResult match)
        {
            Point3D center = result?.Center;
            var nearestCandidates = (volumes ?? Enumerable.Empty<VolumeCandidate>())
                .Where(volume => volume != null && volume.HasBounds)
                .Select(volume => new
                {
                    Volume = volume,
                    Distance = GetDistanceToBounds(center, volume)
                })
                .OrderBy(pair => pair.Distance)
                .ThenBy(pair => pair.Volume.DisplayLabel, StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .Select(pair => new UnmatchedVolumeCandidateDebugInfo(
                    pair.Volume.DisplayLabel,
                    pair.Volume.GroupName,
                    pair.Distance,
                    pair.Volume.MinX,
                    pair.Volume.MinY,
                    pair.Volume.MinZ,
                    pair.Volume.MaxX,
                    pair.Volume.MaxY,
                    pair.Volume.MaxZ))
                .ToList();

            return new UnmatchedClashDebugEntry(
                test?.DisplayName ?? "Unnamed Test",
                result?.DisplayName ?? "Unnamed Clash",
                result?.Status.ToString() ?? string.Empty,
                geometryModel?.DisplayLabel ?? string.Empty,
                center?.X ?? 0d,
                center?.Y ?? 0d,
                center?.Z ?? 0d,
                SafeDisplayName(result?.Item1),
                SafeDisplayName(result?.Item2),
                match?.BoundingBoxSummary ?? string.Empty,
                match?.GeometrySummary ?? string.Empty,
                match?.DecisionSummary ?? string.Empty,
                nearestCandidates);
        }

        private static double GetDistanceToBounds(Point3D point, VolumeCandidate volume)
        {
            if (point == null || volume == null || !volume.HasBounds)
            {
                return double.MaxValue;
            }

            double dx = GetAxisDistance(point.X, volume.MinX, volume.MaxX);
            double dy = GetAxisDistance(point.Y, volume.MinY, volume.MaxY);
            double dz = GetAxisDistance(point.Z, volume.MinZ, volume.MaxZ);
            return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        }

        private static double GetAxisDistance(double value, double min, double max)
        {
            if (value < min)
            {
                return min - value;
            }

            if (value > max)
            {
                return value - max;
            }

            return 0d;
        }

        private static string SafeDisplayName(ModelItem item)
        {
            return CleanText(item?.DisplayName);
        }

        private static string BuildUnmatchedDebugLog(IReadOnlyList<UnmatchedClashDebugEntry> entries)
        {
            var builder = new StringBuilder();
            builder.AppendLine("NavisworksDockPanel Unmatched Clash Debug Log");
            builder.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine();

            foreach (UnmatchedClashDebugEntry entry in entries)
            {
                builder.AppendLine($"Test: {entry.TestName}");
                builder.AppendLine($"Clash: {entry.ClashName}");
                builder.AppendLine($"Status: {entry.Status}");
                builder.AppendLine($"Geometry Model: {entry.GeometryModel}");
                builder.AppendLine($"Center: X={entry.CenterX:F3}, Y={entry.CenterY:F3}, Z={entry.CenterZ:F3}");
                builder.AppendLine($"Item 1: {entry.Item1Name}");
                builder.AppendLine($"Item 2: {entry.Item2Name}");
                builder.AppendLine($"Bounding Box Match: {entry.BoundingBoxSummary}");
                builder.AppendLine($"Geometry Match: {entry.GeometrySummary}");
                builder.AppendLine($"Decision: {entry.DecisionSummary}");

                if (entry.NearestCandidates.Count == 0)
                {
                    builder.AppendLine("Nearest Volumes: none");
                }
                else
                {
                    builder.AppendLine("Nearest Volumes:");
                    foreach (UnmatchedVolumeCandidateDebugInfo candidate in entry.NearestCandidates)
                    {
                        builder.AppendLine(
                            $"  - {candidate.DisplayLabel} | Group={candidate.GroupName} | Distance={candidate.DistanceToBounds:F3} | " +
                            $"Bounds=({candidate.MinX:F3}, {candidate.MinY:F3}, {candidate.MinZ:F3}) -> ({candidate.MaxX:F3}, {candidate.MaxY:F3}, {candidate.MaxZ:F3})");
                    }
                }

                builder.AppendLine();
            }

            return builder.ToString().TrimEnd();
        }

        internal static bool HasExistingGroups(IReadOnlyList<ClashTest> tests)
        {
            return tests.Any(test => test.Children.OfType<ClashResultGroup>().Any());
        }

        private static Dictionary<string, ClashResultGroup> GetRootGroupsByName(ClashTest test)
        {
            return test.Children
                .OfType<ClashResultGroup>()
                .GroupBy(group => group.DisplayName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        }

        private static void RemoveExistingGroups(DocumentClashTests testsData, ClashTest test)
        {
            for (int index = test.Children.Count - 1; index >= 0; index--)
            {
                ClashResultGroup group = test.Children[index] as ClashResultGroup;
                if (group == null)
                {
                    continue;
                }

                PluginDiagnostics.Write($"Flattening existing group '{group.DisplayName}'.");
                MoveResultsToRootAndRemoveNestedGroups(testsData, test, group);
                testsData.TestsRemove(test, group);
            }
        }

        private static void MoveResultsToRootAndRemoveNestedGroups(
            DocumentClashTests testsData,
            ClashTest test,
            ClashResultGroup group)
        {
            for (int index = group.Children.Count - 1; index >= 0; index--)
            {
                SavedItem child = group.Children[index];

                if (child is ClashResultGroup nestedGroup)
                {
                    MoveResultsToRootAndRemoveNestedGroups(testsData, test, nestedGroup);
                    testsData.TestsRemove(group, nestedGroup);
                    continue;
                }

                if (child is ClashResult)
                {
                    testsData.TestsMove(group, index, test, test.Children.Count);
                }
            }
        }

        internal static IReadOnlyList<ClashTest> GetExistingClashTests(Document document)
        {
            DocumentClashTests testsData = GetClashTests(document);
            var tests = new List<ClashTest>();

            for (int index = 0; index < testsData.Tests.Count; index++)
            {
                if (testsData.Tests[index] is ClashTest test)
                {
                    tests.Add(test);
                }
            }

            return tests;
        }

        private static ClashResultGroup CreateGroup(DocumentClashTests testsData, ClashTest test, string groupName)
        {
            var group = new ClashResultGroup
            {
                DisplayName = groupName
            };

            int insertIndex = test.Children.Count;
            testsData.TestsInsertCopy(test, insertIndex, group);

            return test.Children[insertIndex] as ClashResultGroup
                ?? throw new InvalidOperationException($"Unable to create clash result group '{groupName}'.");
        }

        private static VolumeMatchResult MatchVolume(
            Document document,
            ClashResult result,
            IReadOnlyDictionary<Guid, VolumeCandidate> volumesByGuid,
            IDictionary<Guid, VolumeGeometryMesh> geometryCache,
            int resultIndex)
        {
            var uniqueVolumes = volumesByGuid.Values
                .GroupBy(volume => volume.InstanceGuid)
                .Select(group => group.First())
                .ToList();

            return TryMatchByCenter(document, result, uniqueVolumes, geometryCache, resultIndex);
        }

        private static VolumeMatchResult TryMatchByCenter(
            Document document,
            ClashResult result,
            IReadOnlyList<VolumeCandidate> volumes,
            IDictionary<Guid, VolumeGeometryMesh> geometryCache,
            int resultIndex)
        {
            try
            {
                return FindVolumeByResultCenter(document, result, volumes, geometryCache);
            }
            catch (Exception ex)
            {
                PluginDiagnostics.WriteException($"MatchVolume failed for result {resultIndex} using Center", ex);
                throw;
            }
        }

        private static VolumeMatchResult FindVolumeByResultCenter(
            Document document,
            ClashResult result,
            IReadOnlyList<VolumeCandidate> volumes,
            IDictionary<Guid, VolumeGeometryMesh> geometryCache)
        {
            if (result == null || volumes == null || volumes.Count == 0)
            {
                return VolumeMatchResult.Unmatched("No clash result or candidate volumes were available.");
            }

            Point3D center = result.Center;
            BoundingBoxMatchSnapshot boundingBoxMatch = FindBoundingBoxCandidates(center, volumes);
            VolumeGeometryContainment geometryMatch = FindGeometryMatch(document, center, boundingBoxMatch.AllCandidates, geometryCache);

            if (geometryMatch.HasMatch)
            {
                return VolumeMatchResult.FromGeometry(geometryMatch.Volume, boundingBoxMatch, geometryMatch, "Matched with real volume mesh.");
            }

            if (geometryMatch.GeometryWasEvaluated)
            {
                return VolumeMatchResult.Unmatched(
                    boundingBoxMatch,
                    geometryMatch,
                    "Bounding box candidates found, but the clash center was outside all extracted volume meshes.");
            }

            if (boundingBoxMatch.AllCandidates.Count > 0)
            {
                return VolumeMatchResult.Unmatched(
                    boundingBoxMatch,
                    geometryMatch,
                    "Bounding box candidates found, but geometry could not be extracted for any candidate volume.");
            }

            return VolumeMatchResult.Unmatched(boundingBoxMatch, geometryMatch, "No bounding box or geometry match was found.");
        }

        private static BoundingBoxMatchSnapshot FindBoundingBoxCandidates(Point3D center, IReadOnlyList<VolumeCandidate> volumes)
        {
            var strictCandidates = new List<VolumeCandidate>();
            var toleranceCandidates = new List<VolumeCandidate>();
            BoundingBoxCandidateMatch toleranceCandidate = null;
            double toleranceDistance = double.MaxValue;

            foreach (VolumeCandidate volume in volumes)
            {
                if (!volume.HasBounds)
                {
                    continue;
                }

                bool isStrict = ContainsPoint(volume, center);
                if (isStrict)
                {
                    strictCandidates.Add(volume);
                }

                if (!ContainsPoint(volume, center, VolumeMatchTolerance))
                {
                    continue;
                }

                if (!isStrict)
                {
                    toleranceCandidates.Add(volume);
                }

                double distance = GetDistanceToBounds(center, volume);
                if (distance < toleranceDistance)
                {
                    toleranceDistance = distance;
                    toleranceCandidate = new BoundingBoxCandidateMatch(volume, $"tolerance ({VolumeMatchTolerance:0.###})", distance);
                }
            }

            BoundingBoxCandidateMatch strictMatch = strictCandidates.Count == 0
                ? null
                : strictCandidates
                    .Select(volume => new BoundingBoxCandidateMatch(volume, "strict", GetDistanceToBounds(center, volume)))
                    .OrderBy(match => GetBoundsVolume(match.Volume))
                    .ThenBy(match => match.Volume.DisplayLabel, StringComparer.OrdinalIgnoreCase)
                    .First();

            return new BoundingBoxMatchSnapshot(strictCandidates, toleranceCandidates, strictMatch, toleranceCandidate);
        }

        private static VolumeGeometryContainment FindGeometryMatch(
            Document document,
            Point3D center,
            IReadOnlyList<VolumeCandidate> candidates,
            IDictionary<Guid, VolumeGeometryMesh> geometryCache)
        {
            if (candidates == null || candidates.Count == 0)
            {
                return VolumeGeometryContainment.NotEvaluated("No bounding box candidates.");
            }

            bool anyGeometryEvaluated = false;
            var insideMatches = new List<VolumeGeometryContainment>();
            var inspectedGeometries = new List<VolumeGeometryMesh>();

            foreach (VolumeCandidate volume in candidates)
            {
                VolumeGeometryMesh mesh = GetOrCreateVolumeGeometryMesh(document, volume, geometryCache);
                inspectedGeometries.Add(mesh);
                if (mesh == null || !mesh.CanEvaluateContainment)
                {
                    continue;
                }

                anyGeometryEvaluated = true;
                bool isInside = IsPointInsideMesh(center, mesh);
                if (isInside)
                {
                    insideMatches.Add(VolumeGeometryContainment.Match(volume, mesh, "inside-mesh"));
                }
            }

            if (insideMatches.Count > 0)
            {
                return insideMatches
                    .OrderBy(match => GetBoundsVolume(match.Volume))
                    .ThenBy(match => match.Volume.DisplayLabel, StringComparer.OrdinalIgnoreCase)
                    .First();
            }

            if (anyGeometryEvaluated)
            {
                string summary = string.Join(
                    "; ",
                    inspectedGeometries
                        .Where(mesh => mesh != null)
                        .Select(mesh => $"{mesh.Volume.DisplayLabel}: {mesh.Summary}"));
                return VolumeGeometryContainment.EvaluatedNoMatch(summary);
            }

            string unavailableSummary = string.Join(
                "; ",
                inspectedGeometries
                    .Where(mesh => mesh != null)
                    .Select(mesh => $"{mesh.Volume.DisplayLabel}: {mesh.Summary}"));

            return VolumeGeometryContainment.NotEvaluated(
                string.IsNullOrWhiteSpace(unavailableSummary)
                    ? "Geometry extraction was unavailable."
                    : unavailableSummary);
        }

        private static VolumeGeometryMesh GetOrCreateVolumeGeometryMesh(
            Document document,
            VolumeCandidate volume,
            IDictionary<Guid, VolumeGeometryMesh> geometryCache)
        {
            if (volume == null)
            {
                return null;
            }

            if (geometryCache != null && geometryCache.TryGetValue(volume.InstanceGuid, out VolumeGeometryMesh cached))
            {
                return cached;
            }

            VolumeGeometryMesh mesh = ExtractVolumeGeometryMesh(document, volume, true);
            if (geometryCache != null)
            {
                geometryCache[volume.InstanceGuid] = mesh;
            }

            return mesh;
        }

        private static MeshPoint ApplyTransform(MeshPoint point, InwLTransform3f transform)
        {
            if (transform == null)
            {
                return point;
            }

            try
            {
                Array matrix = transform.Matrix as Array;
                if (matrix == null || matrix.Length < 16)
                {
                    return point;
                }

                // Column-major 4x4 matrix (Navisworks COM convention):
                //   col0 = [m0,m1,m2,m3]  (X basis)
                //   col1 = [m4,m5,m6,m7]  (Y basis)
                //   col2 = [m8,m9,m10,m11] (Z basis)
                //   col3 = [m12,m13,m14,m15] (translation)
                //
                // world = M * local:
                //   x_w = m0*x + m4*y + m8*z  + m12
                //   y_w = m1*x + m5*y + m9*z  + m13
                //   z_w = m2*x + m6*y + m10*z + m14
                int lb = matrix.GetLowerBound(0);
                double m0  = Convert.ToDouble(matrix.GetValue(lb + 0));
                double m1  = Convert.ToDouble(matrix.GetValue(lb + 1));
                double m2  = Convert.ToDouble(matrix.GetValue(lb + 2));
                double m4  = Convert.ToDouble(matrix.GetValue(lb + 4));
                double m5  = Convert.ToDouble(matrix.GetValue(lb + 5));
                double m6  = Convert.ToDouble(matrix.GetValue(lb + 6));
                double m8  = Convert.ToDouble(matrix.GetValue(lb + 8));
                double m9  = Convert.ToDouble(matrix.GetValue(lb + 9));
                double m10 = Convert.ToDouble(matrix.GetValue(lb + 10));
                double m12 = Convert.ToDouble(matrix.GetValue(lb + 12));
                double m13 = Convert.ToDouble(matrix.GetValue(lb + 13));
                double m14 = Convert.ToDouble(matrix.GetValue(lb + 14));

                double wx = m0 * point.X + m4 * point.Y + m8  * point.Z + m12;
                double wy = m1 * point.X + m5 * point.Y + m9  * point.Z + m13;
                double wz = m2 * point.X + m6 * point.Y + m10 * point.Z + m14;

                return new MeshPoint(wx, wy, wz);
            }
            catch
            {
                return point;
            }
        }

        // Internal (not private) so other features that need real-geometry containment tests
        // against a volume (e.g. SignOffAreaController matching model elements against a sign
        // off area) can reuse this extraction instead of re-implementing it.
        internal static VolumeGeometryMesh ExtractVolumeGeometryMesh(Document document, VolumeCandidate volume, bool writeDiagnostics)
        {
            if (document == null || volume == null)
            {
                return VolumeGeometryMesh.Unavailable(volume, "No document or volume was available for geometry extraction.");
            }

            ModelItem item = ResolveModelItem(document, volume);
            if (item == null)
            {
                return VolumeGeometryMesh.Unavailable(volume, "The volume could not be resolved back to a Navisworks model item.");
            }

            try
            {
                ModelGeometry modelGeometry = item.Geometry;
                InwOaPath path = ComApiBridge.ToInwOaPath(item);
                InwNodeFragsColl fragments = path?.Fragments();
                if (fragments == null)
                {
                    return VolumeGeometryMesh.Unavailable(volume, "The COM path did not expose fragments.");
                }

                var triangles = new List<MeshTriangle>();
                int fragmentCount = 0;

                foreach (object fragmentObject in fragments)
                {
                    if (!(fragmentObject is InwOaFragment3 fragment))
                    {
                        continue;
                    }

                    fragmentCount++;
                    var fragmentTriangles = new List<MeshTriangle>();
                    var callback = new SimplePrimitivesCallback(fragmentTriangles);
                    fragment.GenerateSimplePrimitives(nwEVertexProperty.eNORMAL, callback);

                    InwLTransform3f localToWorld = fragment.GetLocalToWorldMatrix();

                    foreach (MeshTriangle tri in fragmentTriangles)
                    {
                        triangles.Add(new MeshTriangle(
                            ApplyTransform(tri.A, localToWorld),
                            ApplyTransform(tri.B, localToWorld),
                            ApplyTransform(tri.C, localToWorld)));
                    }
                }

                if (triangles.Count == 0)
                {
                    VolumeGeometryMesh unavailableMesh = VolumeGeometryMesh.Unavailable(
                        volume,
                        $"Navisworks exposed {fragmentCount} fragment(s), but no triangle primitives were generated.",
                        modelGeometry?.IsSolid ?? false,
                        fragmentCount,
                        0);
                    if (writeDiagnostics)
                    {
                        PluginDiagnostics.Write($"Geometry extraction unavailable for '{volume.DisplayLabel}': {unavailableMesh.Summary}");
                    }
                    return unavailableMesh;
                }

                VolumeGeometryMesh mesh = VolumeGeometryMesh.Create(volume, modelGeometry, fragmentCount, triangles);
                if (writeDiagnostics)
                {
                    PluginDiagnostics.Write($"Geometry extraction for '{volume.DisplayLabel}': {mesh.Summary}");
                }
                return mesh;
            }
            catch (Exception ex)
            {
                if (writeDiagnostics)
                {
                    PluginDiagnostics.WriteException($"Failed extracting geometry for volume '{volume.DisplayLabel}'", ex);
                }
                return VolumeGeometryMesh.Unavailable(volume, ex.Message);
            }
        }

        // Internal (not private) - see ExtractVolumeGeometryMesh above for why.
        internal static bool IsPointInsideMesh(Point3D point, VolumeGeometryMesh mesh)
        {
            if (point == null || mesh == null || !mesh.CanEvaluateContainment)
            {
                return false;
            }

            if (mesh.IsClosedMesh || mesh.IsSolidFlag)
            {
                return IsPointInsideClosedMesh(point, mesh);
            }

            return IsPointInsideOpenMesh(point, mesh);
        }

        private static bool IsPointInsideClosedMesh(Point3D point, VolumeGeometryMesh mesh)
        {
            var rayOrigin = new MeshPoint(
                point.X - GeometryRayOffset,
                point.Y + GeometryIntersectionTolerance,
                point.Z + (GeometryIntersectionTolerance * 2));

            int intersections = 0;

            for (int i = 0; i < mesh.Triangles.Count; i++)
            {
                if (IntersectsPositiveXRay(rayOrigin, mesh.Triangles[i]))
                {
                    intersections++;
                }
            }

            return (intersections % 2) == 1;
        }

        private static bool IsPointInsideOpenMesh(Point3D point, VolumeGeometryMesh mesh)
        {
            if (mesh.Volume != null && mesh.Volume.HasBounds)
            {
                double z = point.Z;
                if (z < mesh.Volume.MinZ - VolumeMatchTolerance || z > mesh.Volume.MaxZ + VolumeMatchTolerance)
                {
                    return false;
                }
            }

            var horizontalTriangles = ExtractHorizontalTriangles(mesh.Triangles);
            if (horizontalTriangles.Count > 0)
            {
                return IsPointInside2DTriangles(point.X, point.Y, horizontalTriangles);
            }

            var boundaryEdges = ExtractBoundaryEdgesXY(mesh.Triangles);
            if (boundaryEdges.Count > 0)
            {
                return IsPointInsideBoundaryEdges(point.X, point.Y, boundaryEdges);
            }

            return false;
        }

        private static List<MeshTriangle> ExtractHorizontalTriangles(IReadOnlyList<MeshTriangle> triangles)
        {
            var result = new List<MeshTriangle>();
            double bestZ = double.MinValue;

            for (int i = 0; i < triangles.Count; i++)
            {
                MeshTriangle tri = triangles[i];
                MeshPoint normal = MeshPoint.Cross(tri.B - tri.A, tri.C - tri.A);
                double absZ = Math.Abs(normal.Z);
                double length = Math.Sqrt(normal.X * normal.X + normal.Y * normal.Y + normal.Z * normal.Z);
                if (length < GeometryIntersectionTolerance) continue;

                double nz = absZ / length;
                if (nz > 0.9)
                {
                    double avgZ = (tri.A.Z + tri.B.Z + tri.C.Z) / 3.0;
                    if (avgZ > bestZ)
                    {
                        bestZ = avgZ;
                    }
                }
            }

            if (bestZ == double.MinValue) return result;

            for (int i = 0; i < triangles.Count; i++)
            {
                MeshTriangle tri = triangles[i];
                MeshPoint normal = MeshPoint.Cross(tri.B - tri.A, tri.C - tri.A);
                double absZ = Math.Abs(normal.Z);
                double length = Math.Sqrt(normal.X * normal.X + normal.Y * normal.Y + normal.Z * normal.Z);
                if (length < GeometryIntersectionTolerance) continue;

                double nz = absZ / length;
                if (nz > 0.9)
                {
                    double avgZ = (tri.A.Z + tri.B.Z + tri.C.Z) / 3.0;
                    if (Math.Abs(avgZ - bestZ) < PlanarMeshZThreshold)
                    {
                        result.Add(tri);
                    }
                }
            }

            return result;
        }

        private static bool IsPointInside2DTriangles(double px, double py, IReadOnlyList<MeshTriangle> triangles)
        {
            for (int i = 0; i < triangles.Count; i++)
            {
                MeshTriangle tri = triangles[i];
                if (IsPointInTriangle2D(px, py, tri.A.X, tri.A.Y, tri.B.X, tri.B.Y, tri.C.X, tri.C.Y))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsPointInTriangle2D(
            double px, double py,
            double ax, double ay, double bx, double by, double cx, double cy)
        {
            double d1 = (px - bx) * (ay - by) - (ax - bx) * (py - by);
            double d2 = (px - cx) * (by - cy) - (bx - cx) * (py - cy);
            double d3 = (px - ax) * (cy - ay) - (cx - ax) * (py - ay);

            bool hasNeg = (d1 < 0) || (d2 < 0) || (d3 < 0);
            bool hasPos = (d1 > 0) || (d2 > 0) || (d3 > 0);

            return !(hasNeg && hasPos);
        }

        private static List<KeyValuePair<MeshPoint, MeshPoint>> ExtractBoundaryEdgesXY(IReadOnlyList<MeshTriangle> triangles)
        {
            var edgeCounts = new Dictionary<string, KeyValuePair<MeshPoint, MeshPoint>>();
            var edgeOccurrences = new Dictionary<string, int>();

            for (int i = 0; i < triangles.Count; i++)
            {
                MeshTriangle tri = triangles[i];
                CountBoundaryEdge(edgeCounts, edgeOccurrences, tri.A, tri.B);
                CountBoundaryEdge(edgeCounts, edgeOccurrences, tri.B, tri.C);
                CountBoundaryEdge(edgeCounts, edgeOccurrences, tri.C, tri.A);
            }

            var boundary = new List<KeyValuePair<MeshPoint, MeshPoint>>();
            foreach (var kvp in edgeCounts)
            {
                if (edgeOccurrences[kvp.Key] == 1)
                {
                    boundary.Add(kvp.Value);
                }
            }

            return boundary;
        }

        private static void CountBoundaryEdge(
            IDictionary<string, KeyValuePair<MeshPoint, MeshPoint>> edgeCounts,
            IDictionary<string, int> edgeOccurrences,
            MeshPoint a, MeshPoint b)
        {
            string key = CreateBoundaryEdgeKey(a, b);
            if (!edgeOccurrences.ContainsKey(key))
            {
                edgeOccurrences[key] = 1;
                edgeCounts[key] = new KeyValuePair<MeshPoint, MeshPoint>(a, b);
            }
            else
            {
                edgeOccurrences[key]++;
            }
        }

        private static string CreateBoundaryEdgeKey(MeshPoint a, MeshPoint b)
        {
            long ax = (long)Math.Round(a.X * 1000);
            long ay = (long)Math.Round(a.Y * 1000);
            long bx = (long)Math.Round(b.X * 1000);
            long by = (long)Math.Round(b.Y * 1000);

            if (ax < bx || (ax == bx && ay < by))
                return $"{ax},{ay}|{bx},{by}";
            return $"{bx},{by}|{ax},{ay}";
        }

        private static bool IsPointInsideBoundaryEdges(
            double px, double py,
            IReadOnlyList<KeyValuePair<MeshPoint, MeshPoint>> edges)
        {
            int intersections = 0;
            for (int i = 0; i < edges.Count; i++)
            {
                var edge = edges[i];
                if (RayCrossesEdge2D(px, py, edge.Key.X, edge.Key.Y, edge.Value.X, edge.Value.Y))
                {
                    intersections++;
                }
            }

            return (intersections % 2) == 1;
        }

        private static bool RayCrossesEdge2D(double px, double py, double ax, double ay, double bx, double by)
        {
            if ((ay <= py && by > py) || (by <= py && ay > py))
            {
                double t = (py - ay) / (by - ay);
                double hitX = ax + t * (bx - ax);
                if (px < hitX)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IntersectsPositiveXRay(MeshPoint origin, MeshTriangle triangle)
        {
            MeshPoint edge1 = triangle.B - triangle.A;
            MeshPoint edge2 = triangle.C - triangle.A;
            MeshPoint direction = MeshPoint.UnitX;

            MeshPoint p = MeshPoint.Cross(direction, edge2);
            double determinant = MeshPoint.Dot(edge1, p);
            if (Math.Abs(determinant) < GeometryIntersectionTolerance)
            {
                return false;
            }

            double inverseDeterminant = 1d / determinant;
            MeshPoint t = origin - triangle.A;
            double u = MeshPoint.Dot(t, p) * inverseDeterminant;
            if (u < -GeometryIntersectionTolerance || u > 1d + GeometryIntersectionTolerance)
            {
                return false;
            }

            MeshPoint q = MeshPoint.Cross(t, edge1);
            double v = MeshPoint.Dot(direction, q) * inverseDeterminant;
            if (v < -GeometryIntersectionTolerance || (u + v) > 1d + GeometryIntersectionTolerance)
            {
                return false;
            }

            double distance = MeshPoint.Dot(edge2, q) * inverseDeterminant;
            return distance > GeometryIntersectionTolerance;
        }

        private static double GetBoundsVolume(VolumeCandidate volume)
        {
            if (volume == null || !volume.HasBounds)
            {
                return double.MaxValue;
            }

            return Math.Abs(volume.MaxX - volume.MinX)
                * Math.Abs(volume.MaxY - volume.MinY)
                * Math.Abs(volume.MaxZ - volume.MinZ);
        }

        private static bool ContainsPoint(VolumeCandidate volume, Point3D point)
        {
            return ContainsPoint(volume, point, 0d);
        }

        private static bool ContainsPoint(VolumeCandidate volume, Point3D point, double tolerance)
        {
            if (volume == null || !volume.HasBounds || point == null)
            {
                return false;
            }

            return point.X >= volume.MinX - tolerance && point.X <= volume.MaxX + tolerance
                && point.Y >= volume.MinY - tolerance && point.Y <= volume.MaxY + tolerance
                && point.Z >= volume.MinZ - tolerance && point.Z <= volume.MaxZ + tolerance;
        }

        private static DocumentClashTests GetClashTests(Document document)
        {
            var clash = document.Clash as DocumentClash;
            if (clash == null)
            {
                throw new InvalidOperationException("This Navisworks edition does not expose Clash Detective data.");
            }

            return clash.TestsData;
        }

        // Internal (not private) so other features that address volumes/zones by the same
        // VolumeCandidate (e.g. SignOffAreaController) can resolve back to a live ModelItem
        // without duplicating this lookup.
        internal static ModelItem ResolveModelItem(Document document, VolumeCandidate volume)
        {
            if (document == null || volume == null || string.IsNullOrWhiteSpace(volume.PathId))
            {
                return null;
            }

            try
            {
                return document.Models.ResolvePathId(new Autodesk.Navisworks.Api.DocumentParts.ModelItemPathId
                {
                    ModelIndex = volume.ModelIndex,
                    PathId = volume.PathId
                });
            }
            catch
            {
                return null;
            }
        }

        private static bool IsGeometryModelCandidate(Model model)
        {
            if (model?.RootItem == null)
            {
                return false;
            }

            string displayName = GetModelDisplayName(model, -1, GetBestModelFilePath(model));
            if (LooksLikeViewContainer(displayName))
            {
                return false;
            }

            if (model.RootItem.DescendantsAndSelf.Any(item => item.HasGeometry))
            {
                return true;
            }

            return false;
        }

        private static string GetBestModelFilePath(Model model)
        {
            return model.SourceFileName
                ?? model.FileName
                ?? string.Empty;
        }

        private static string GetModelDisplayName(Model model, int modelIndex, string filePath)
        {
            string rootName = CleanText(model?.RootItem?.DisplayName);
            if (!string.IsNullOrWhiteSpace(rootName))
            {
                return rootName;
            }

            string fileName = SafeGetFileName(filePath);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }

            string sourceName = CleanText(model?.SourceFileName);
            if (!string.IsNullOrWhiteSpace(sourceName))
            {
                return SimplifyAccDisplayName(sourceName);
            }

            string modelName = CleanText(model?.FileName);
            if (!string.IsNullOrWhiteSpace(modelName))
            {
                return SimplifyAccDisplayName(modelName);
            }

            return modelIndex >= 0 ? $"Model {modelIndex + 1}" : "Model";
        }

        private static string SafeGetFileName(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFileName(path);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string SimplifyAccDisplayName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string simplified = value.Trim();

            int queryIndex = simplified.IndexOf('?');
            if (queryIndex >= 0)
            {
                simplified = simplified.Substring(0, queryIndex);
            }

            simplified = simplified.Replace("%20", " ");
            simplified = simplified.Replace("%7B", "{").Replace("%7D", "}");
            simplified = simplified.Replace('/', '\\');

            try
            {
                string fileName = Path.GetFileName(simplified);
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    simplified = fileName;
                }
            }
            catch
            {
            }

            return simplified;
        }

        private static bool LooksLikeViewContainer(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string text = value.Trim();
            return text.Equals("Views", StringComparison.OrdinalIgnoreCase)
                || text.Equals("Saved Views", StringComparison.OrdinalIgnoreCase)
                || text.Equals("Coordination Views", StringComparison.OrdinalIgnoreCase)
                || text.Equals("Viewpoints", StringComparison.OrdinalIgnoreCase)
                || text.IndexOf("coordination view", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool TryGetLastWriteUtc(string filePath, out DateTime value)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    value = File.GetLastWriteTimeUtc(filePath);
                    return true;
                }
            }
            catch
            {
            }

            value = DateTime.MinValue;
            return false;
        }

        private static VolumeMetadata GetVolumeMetadata(ModelItem item, string sourceModelName)
        {
            string displayName = CleanText(item.DisplayName);
            string classDisplayName = CleanText(item.ClassDisplayName);
            string category = string.Empty;
            string nameParameter = string.Empty;
            string commentsParameter = string.Empty;
            string markParameter = string.Empty;
            bool hasKeyword = ContainsVolumeKeyword(displayName) || ContainsVolumeKeyword(classDisplayName);

            foreach (PropertyCategory propertyCategory in item.PropertyCategories)
            {
                string categoryName = CleanText(propertyCategory.DisplayName);
                if (string.IsNullOrWhiteSpace(category) && categoryName.IndexOf("category", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    category = GetBestPropertyValue(propertyCategory.Properties);
                }

                if (ContainsVolumeKeyword(categoryName))
                {
                    hasKeyword = true;
                }

                foreach (DataProperty property in propertyCategory.Properties)
                {
                    string propertyName = CleanText(property.DisplayName);
                    string propertyValue = GetPropertyValue(property);
                    if (string.IsNullOrWhiteSpace(propertyValue))
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(nameParameter) &&
                        (propertyName.Equals("Name", StringComparison.OrdinalIgnoreCase) ||
                         propertyName.IndexOf("Volume Name", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        nameParameter = propertyValue;
                    }

                    if (string.IsNullOrWhiteSpace(commentsParameter) &&
                        (propertyName.IndexOf("Comments",    StringComparison.OrdinalIgnoreCase) >= 0 ||
                         propertyName.IndexOf("Comentarios", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         propertyName.IndexOf("Commentaires",StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        commentsParameter = propertyValue;
                    }

                    if (string.IsNullOrWhiteSpace(markParameter) &&
                        (propertyName.Equals("Mark",   StringComparison.OrdinalIgnoreCase) ||
                         propertyName.Equals("Marca",  StringComparison.OrdinalIgnoreCase) ||
                         propertyName.Equals("Marque", StringComparison.OrdinalIgnoreCase) ||
                         propertyName.Equals("Number", StringComparison.OrdinalIgnoreCase) ||
                         propertyName.Equals("Número", StringComparison.OrdinalIgnoreCase)))
                    {
                        markParameter = propertyValue;
                    }

                    if (!hasKeyword &&
                        (ContainsVolumeKeyword(propertyName) || ContainsVolumeKeyword(propertyValue)))
                    {
                        hasKeyword = true;
                    }
                }
            }

            string resolvedName = FirstNonEmpty(nameParameter, displayName, DefaultNameLabel);
            string resolvedComments = CleanText(commentsParameter);
            string resolvedMark = CleanText(markParameter);
            bool hasMark = !string.IsNullOrWhiteSpace(resolvedMark);
            bool isCandidate = hasKeyword || hasMark;
            string groupName = BuildGroupName(resolvedName, resolvedMark);
            string label = string.IsNullOrWhiteSpace(resolvedMark)
                ? $"{resolvedName} [{sourceModelName}]"
                : $"{resolvedName} - {resolvedMark} [{sourceModelName}]";

            return new VolumeMetadata(
                isCandidate,
                resolvedName,
                resolvedComments,
                resolvedMark,
                label,
                groupName,
                $"{label} {resolvedComments} {category} {classDisplayName}");
        }

        private static string BuildGroupName(string nameParameter, string markParameter)
        {
            if (!string.IsNullOrWhiteSpace(markParameter))
            {
                return markParameter;
            }

            return FirstNonEmpty(nameParameter, markParameter, DefaultNameLabel);
        }

        private static string GetBestPropertyValue(DataPropertyCollection properties)
        {
            foreach (DataProperty property in properties)
            {
                string value = GetPropertyValue(property);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        private static string GetPropertyValue(DataProperty property)
        {
            try
            {
                return CleanText(property.Value.ToDisplayString());
            }
            catch
            {
                try
                {
                    return CleanText(property.Value.ToString());
                }
                catch
                {
                    return string.Empty;
                }
            }
        }

        private static bool ContainsVolumeKeyword(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return value.IndexOf("volume", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("volumen", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("zone", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("zona", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("mass", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                string trimmed = CleanText(value);
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    return trimmed;
                }
            }

            return string.Empty;
        }

        private static string CleanText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        internal static BoundingBox3D GetItemBoundingBox(ModelItem item)
        {
            if (item == null)
            {
                return null;
            }

            var collection = new ModelItemCollection();
            collection.Add(item);
            BoundingBox3D boundingBox = collection.BoundingBox();
            return boundingBox != null && !boundingBox.IsEmpty ? boundingBox : null;
        }
    }

    internal sealed class GeometryModelOption
    {
        public GeometryModelOption(Model model, int modelIndex, string filePath, string displayName)
            : this(model, modelIndex, filePath, displayName, model?.RootItem, null)
        {
        }

        public GeometryModelOption(Model model, int modelIndex, string filePath, string displayName, ModelItem scopeRootItem, string displayLabel)
        {
            Model = model;
            ModelIndex = modelIndex;
            FilePath = filePath;
            ScopeRootItem = scopeRootItem;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? filePath : displayName;
            DisplayLabel = string.IsNullOrWhiteSpace(displayLabel) ? DisplayName : displayLabel;
        }

        public Model Model { get; }

        public int ModelIndex { get; }

        public string FilePath { get; }

        public ModelItem ScopeRootItem { get; }

        public string DisplayName { get; }

        public string DisplayLabel { get; set; }

        public bool IsLatest { get; set; }

        public override string ToString()
        {
            return DisplayLabel;
        }
    }

    internal sealed class VolumeCandidate
    {
        public VolumeCandidate(Document document, int modelIndex, ModelItem item, string sourceModelName, VolumeMetadata metadata)
        {
            InstanceGuid = item?.InstanceGuid ?? Guid.Empty;
            ModelIndex = modelIndex;
            PathId = CreatePathId(document, item);
            SourceModelName = sourceModelName;
            NameParameter = metadata.NameParameter;
            CommentsParameter = metadata.CommentsParameter;
            MarkParameter = metadata.MarkParameter;
            DisplayLabel = metadata.DisplayLabel;
            GroupName = metadata.GroupName;
            SearchText = metadata.SearchText;
            LoadBounds(VolumeClashGroupingService.GetItemBoundingBox(item));
        }

        public Guid InstanceGuid { get; }

        public int ModelIndex { get; }

        public string PathId { get; }

        public string SourceModelName { get; }

        public string NameParameter { get; }

        public string CommentsParameter { get; }

        public string MarkParameter { get; }

        public string DisplayLabel { get; }

        public string GroupName { get; }

        public string SearchText { get; }

        public double MinX { get; private set; }

        public double MinY { get; private set; }

        public double MinZ { get; private set; }

        public double MaxX { get; private set; }

        public double MaxY { get; private set; }

        public double MaxZ { get; private set; }

        public bool HasBounds { get; private set; }

        public override string ToString()
        {
            return DisplayLabel;
        }

        private static string CreatePathId(Document document, ModelItem item)
        {
            if (document == null || item == null)
            {
                return string.Empty;
            }

            try
            {
                return document.Models.CreatePathId(item)?.PathId ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private void LoadBounds(BoundingBox3D boundingBox)
        {
            if (boundingBox == null || boundingBox.IsEmpty)
            {
                HasBounds = false;
                return;
            }

            MinX = boundingBox.Min.X;
            MinY = boundingBox.Min.Y;
            MinZ = boundingBox.Min.Z;
            MaxX = boundingBox.Max.X;
            MaxY = boundingBox.Max.Y;
            MaxZ = boundingBox.Max.Z;
            HasBounds = true;
        }
    }

    internal sealed class VolumeMetadata
    {
        public VolumeMetadata(
            bool isCandidate,
            string nameParameter,
            string commentsParameter,
            string markParameter,
            string displayLabel,
            string groupName,
            string searchText)
        {
            IsCandidate = isCandidate;
            NameParameter = nameParameter;
            CommentsParameter = commentsParameter;
            MarkParameter = markParameter;
            DisplayLabel = displayLabel;
            GroupName = groupName;
            SearchText = searchText;
        }

        public bool IsCandidate { get; }

        public string NameParameter { get; }

        public string CommentsParameter { get; }

        public string MarkParameter { get; }

        public string DisplayLabel { get; }

        public string GroupName { get; }

        public string SearchText { get; }
    }

    internal sealed class VolumeMatchResult
    {
        public VolumeMatchResult(
            VolumeCandidate volume,
            string matchType,
            string boundingBoxSummary,
            string geometrySummary,
            string decisionSummary)
        {
            Volume = volume;
            MatchType = matchType;
            BoundingBoxSummary = boundingBoxSummary;
            GeometrySummary = geometrySummary;
            DecisionSummary = decisionSummary;
        }

        public VolumeCandidate Volume { get; }

        public string MatchType { get; }

        public string BoundingBoxSummary { get; }

        public string GeometrySummary { get; }

        public string DecisionSummary { get; }

        public static VolumeMatchResult FromGeometry(
            VolumeCandidate volume,
            BoundingBoxMatchSnapshot boundingBoxMatch,
            VolumeGeometryContainment geometryMatch,
            string decision)
        {
            return new VolumeMatchResult(
                volume,
                "geometry-mesh",
                boundingBoxMatch?.Summary ?? "none",
                geometryMatch?.Summary ?? "none",
                decision);
        }

        public static VolumeMatchResult Unmatched(
            BoundingBoxMatchSnapshot boundingBoxMatch,
            VolumeGeometryContainment geometryMatch,
            string decision)
        {
            return new VolumeMatchResult(
                null,
                "unmatched",
                boundingBoxMatch?.Summary ?? "none",
                geometryMatch?.Summary ?? "none",
                decision);
        }

        public static VolumeMatchResult Unmatched(string decision)
        {
            return new VolumeMatchResult(null, "unmatched", "none", "none", decision);
        }
    }

    internal sealed class TestGroupingSummary
    {
        public TestGroupingSummary(string testName)
        {
            TestName = string.IsNullOrWhiteSpace(testName) ? "Unnamed Test" : testName;
            GroupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public string TestName { get; }

        public HashSet<string> GroupNames { get; }

        public int GroupedResultsCount { get; set; }
    }

    // What ExecuteGrouping hands back to ZoneGroupingController - structured so the
    // GroupZonesView result dialog can render a compact summary (unique group names as
    // badges) instead of a verbose per-test text dump. ErrorMessage set means grouping never
    // ran (e.g. no clash tests in the document); everything else is left at its default.
    internal sealed class ZoneGroupingResult
    {
        public string ErrorMessage { get; set; }

        public int ProcessedTests { get; set; }

        public int GroupedResults { get; set; }

        public List<string> GroupNames { get; set; } = new List<string>();

        public int UnmatchedCount { get; set; }

        public string UnmatchedLogPath { get; set; }

        public static ZoneGroupingResult Error(string message)
        {
            return new ZoneGroupingResult { ErrorMessage = message };
        }
    }

    internal sealed class UnmatchedClashDebugEntry
    {
        public UnmatchedClashDebugEntry(
            string testName,
            string clashName,
            string status,
            string geometryModel,
            double centerX,
            double centerY,
            double centerZ,
            string item1Name,
            string item2Name,
            string boundingBoxSummary,
            string geometrySummary,
            string decisionSummary,
            IReadOnlyList<UnmatchedVolumeCandidateDebugInfo> nearestCandidates)
        {
            TestName = testName;
            ClashName = clashName;
            Status = status;
            GeometryModel = geometryModel;
            CenterX = centerX;
            CenterY = centerY;
            CenterZ = centerZ;
            Item1Name = item1Name;
            Item2Name = item2Name;
            BoundingBoxSummary = boundingBoxSummary;
            GeometrySummary = geometrySummary;
            DecisionSummary = decisionSummary;
            NearestCandidates = nearestCandidates ?? Array.Empty<UnmatchedVolumeCandidateDebugInfo>();
        }

        public string TestName { get; }

        public string ClashName { get; }

        public string Status { get; }

        public string GeometryModel { get; }

        public double CenterX { get; }

        public double CenterY { get; }

        public double CenterZ { get; }

        public string Item1Name { get; }

        public string Item2Name { get; }

        public string BoundingBoxSummary { get; }

        public string GeometrySummary { get; }

        public string DecisionSummary { get; }

        public IReadOnlyList<UnmatchedVolumeCandidateDebugInfo> NearestCandidates { get; }
    }

    internal sealed class UnmatchedVolumeCandidateDebugInfo
    {
        public UnmatchedVolumeCandidateDebugInfo(
            string displayLabel,
            string groupName,
            double distanceToBounds,
            double minX,
            double minY,
            double minZ,
            double maxX,
            double maxY,
            double maxZ)
        {
            DisplayLabel = displayLabel;
            GroupName = groupName;
            DistanceToBounds = distanceToBounds;
            MinX = minX;
            MinY = minY;
            MinZ = minZ;
            MaxX = maxX;
            MaxY = maxY;
            MaxZ = maxZ;
        }

        public string DisplayLabel { get; }

        public string GroupName { get; }

        public double DistanceToBounds { get; }

        public double MinX { get; }

        public double MinY { get; }

        public double MinZ { get; }

        public double MaxX { get; }

        public double MaxY { get; }

        public double MaxZ { get; }
    }

    internal sealed class BoundingBoxCandidateMatch
    {
        public BoundingBoxCandidateMatch(VolumeCandidate volume, string matchType, double distanceToBounds)
        {
            Volume = volume;
            MatchType = matchType;
            DistanceToBounds = distanceToBounds;
        }

        public VolumeCandidate Volume { get; }

        public string MatchType { get; }

        public double DistanceToBounds { get; }
    }

    internal sealed class BoundingBoxMatchSnapshot
    {
        public BoundingBoxMatchSnapshot(
            IReadOnlyList<VolumeCandidate> strictCandidates,
            IReadOnlyList<VolumeCandidate> toleranceCandidates,
            BoundingBoxCandidateMatch strictMatch,
            BoundingBoxCandidateMatch toleranceMatch)
        {
            StrictCandidates = strictCandidates ?? Array.Empty<VolumeCandidate>();
            ToleranceCandidates = toleranceCandidates ?? Array.Empty<VolumeCandidate>();
            StrictMatch = strictMatch;
            ToleranceMatch = toleranceMatch;
            MatchType = strictMatch?.MatchType ?? toleranceMatch?.MatchType ?? "none";
            Summary = BuildSummary();
        }

        public IReadOnlyList<VolumeCandidate> StrictCandidates { get; }

        public IReadOnlyList<VolumeCandidate> ToleranceCandidates { get; }

        public IReadOnlyList<VolumeCandidate> AllCandidates
        {
            get
            {
                if (ToleranceCandidates.Count == 0) return StrictCandidates;
                if (StrictCandidates.Count == 0) return ToleranceCandidates;
                var all = new List<VolumeCandidate>(StrictCandidates.Count + ToleranceCandidates.Count);
                all.AddRange(StrictCandidates);
                all.AddRange(ToleranceCandidates);
                return all;
            }
        }

        public BoundingBoxCandidateMatch StrictMatch { get; }

        public BoundingBoxCandidateMatch ToleranceMatch { get; }

        public string MatchType { get; }

        public string Summary { get; }

        private string BuildSummary()
        {
            string strictNames = StrictCandidates.Count == 0
                ? "none"
                : string.Join(", ", StrictCandidates.Select(volume => volume.DisplayLabel));
            string toleranceNames = ToleranceCandidates.Count == 0
                ? "none"
                : string.Join(", ", ToleranceCandidates.Select(volume => volume.DisplayLabel));
            return $"strict=[{strictNames}] | tolerance=[{toleranceNames}]";
        }
    }

    internal sealed class VolumeGeometryContainment
    {
        private VolumeGeometryContainment(
            VolumeCandidate volume,
            bool hasMatch,
            bool geometryWasEvaluated,
            string summary)
        {
            Volume = volume;
            HasMatch = hasMatch;
            GeometryWasEvaluated = geometryWasEvaluated;
            Summary = summary;
        }

        public VolumeCandidate Volume { get; }

        public bool HasMatch { get; }

        public bool GeometryWasEvaluated { get; }

        public string Summary { get; }

        public static VolumeGeometryContainment Match(VolumeCandidate volume, VolumeGeometryMesh mesh, string matchType)
        {
            return new VolumeGeometryContainment(
                volume,
                true,
                true,
                $"{volume?.DisplayLabel ?? "unknown"} ({matchType}; {mesh?.Summary ?? "no mesh summary"})");
        }

        public static VolumeGeometryContainment EvaluatedNoMatch(string summary)
        {
            return new VolumeGeometryContainment(null, false, true, summary);
        }

        public static VolumeGeometryContainment NotEvaluated(string summary)
        {
            return new VolumeGeometryContainment(null, false, false, summary);
        }
    }

    internal sealed class VolumeGeometryMesh
    {
        private const double EdgeQuantization = 0.001;

        private VolumeGeometryMesh(
            VolumeCandidate volume,
            IReadOnlyList<MeshTriangle> triangles,
            bool isSolidFlag,
            bool isClosedMesh,
            int fragmentCount,
            string summary,
            bool canEvaluateContainment)
        {
            Volume = volume;
            Triangles = triangles ?? Array.Empty<MeshTriangle>();
            IsSolidFlag = isSolidFlag;
            IsClosedMesh = isClosedMesh;
            FragmentCount = fragmentCount;
            Summary = summary;
            CanEvaluateContainment = canEvaluateContainment;
        }

        public VolumeCandidate Volume { get; }

        public IReadOnlyList<MeshTriangle> Triangles { get; }

        public bool IsSolidFlag { get; }

        public bool IsClosedMesh { get; }

        public int FragmentCount { get; }

        public string Summary { get; }

        public bool CanEvaluateContainment { get; }

        public static VolumeGeometryMesh Create(
            VolumeCandidate volume,
            ModelGeometry geometry,
            int fragmentCount,
            IReadOnlyList<MeshTriangle> triangles)
        {
            bool isSolidFlag = geometry?.IsSolid ?? false;
            bool isClosedMesh = LooksLikeClosedMesh(triangles);
            bool canEvaluateContainment = triangles != null
                && triangles.Count > 0;
            string summary =
                $"triangles={triangles?.Count ?? 0}, fragments={fragmentCount}, isSolid={isSolidFlag}, closedMesh={isClosedMesh}, containmentReady={canEvaluateContainment}";

            return new VolumeGeometryMesh(
                volume,
                triangles,
                isSolidFlag,
                isClosedMesh,
                fragmentCount,
                summary,
                canEvaluateContainment);
        }

        public static VolumeGeometryMesh Unavailable(
            VolumeCandidate volume,
            string reason,
            bool isSolidFlag = false,
            int fragmentCount = 0,
            int triangleCount = 0)
        {
            string summary =
                $"triangles={triangleCount}, fragments={fragmentCount}, isSolid={isSolidFlag}, unavailable={Clean(reason)}";
            return new VolumeGeometryMesh(volume, Array.Empty<MeshTriangle>(), isSolidFlag, false, fragmentCount, summary, false);
        }

        private static bool LooksLikeClosedMesh(IReadOnlyList<MeshTriangle> triangles)
        {
            if (triangles == null || triangles.Count == 0)
            {
                return false;
            }

            var edgeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < triangles.Count; i++)
            {
                MeshTriangle triangle = triangles[i];
                CountEdge(edgeCounts, triangle.A, triangle.B);
                CountEdge(edgeCounts, triangle.B, triangle.C);
                CountEdge(edgeCounts, triangle.C, triangle.A);
            }

            return edgeCounts.Count > 0 && edgeCounts.Values.All(count => count == 2);
        }

        private static void CountEdge(IDictionary<string, int> edgeCounts, MeshPoint first, MeshPoint second)
        {
            string key = CreateEdgeKey(first, second);
            edgeCounts[key] = edgeCounts.TryGetValue(key, out int count) ? count + 1 : 1;
        }

        private static string CreateEdgeKey(MeshPoint first, MeshPoint second)
        {
            string firstKey = CreatePointKey(first);
            string secondKey = CreatePointKey(second);
            return string.CompareOrdinal(firstKey, secondKey) <= 0
                ? firstKey + "|" + secondKey
                : secondKey + "|" + firstKey;
        }

        private static string CreatePointKey(MeshPoint point)
        {
            return Quantize(point.X) + "," + Quantize(point.Y) + "," + Quantize(point.Z);
        }

        private static long Quantize(double value)
        {
            return (long)Math.Round(value / EdgeQuantization);
        }

        private static string Clean(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        }
    }

    [ComVisible(true)]
    internal sealed class SimplePrimitivesCallback : InwSimplePrimitivesCB
    {
        private readonly IList<MeshTriangle> _triangles;

        public SimplePrimitivesCallback(IList<MeshTriangle> triangles)
        {
            _triangles = triangles ?? throw new ArgumentNullException(nameof(triangles));
        }

        public void Line(InwSimpleVertex v1, InwSimpleVertex v2)
        {
        }

        public void Point(InwSimpleVertex v1)
        {
        }

        public void SnapPoint(InwSimpleVertex v1)
        {
        }

        public void Triangle(InwSimpleVertex v1, InwSimpleVertex v2, InwSimpleVertex v3)
        {
            try
            {
                MeshPoint a = ToMeshPoint(v1);
                MeshPoint b = ToMeshPoint(v2);
                MeshPoint c = ToMeshPoint(v3);
                _triangles.Add(new MeshTriangle(a, b, c));
            }
            catch
            {
            }
        }

        private static MeshPoint ToMeshPoint(InwSimpleVertex vertex)
        {
            try
            {
                if (vertex == null) return MeshPoint.Zero;

                object coordObj = vertex.coord;

                if (coordObj is InwLPos3f position)
                {
                    return new MeshPoint(position.data1, position.data2, position.data3);
                }

                if (coordObj is Array coordArray && coordArray.Length >= 3)
                {
                    int lb = coordArray.GetLowerBound(0);
                    double x = Convert.ToDouble(coordArray.GetValue(lb));
                    double y = Convert.ToDouble(coordArray.GetValue(lb + 1));
                    double z = Convert.ToDouble(coordArray.GetValue(lb + 2));
                    return new MeshPoint(x, y, z);
                }

                // Navisworks COM interop: coord may be a COM object with data1/data2/data3 properties
                if (coordObj != null)
                {
                    Type coordType = coordObj.GetType();
                    object d1 = coordType.InvokeMember("data1", System.Reflection.BindingFlags.GetProperty, null, coordObj, null);
                    object d2 = coordType.InvokeMember("data2", System.Reflection.BindingFlags.GetProperty, null, coordObj, null);
                    object d3 = coordType.InvokeMember("data3", System.Reflection.BindingFlags.GetProperty, null, coordObj, null);
                    if (d1 != null && d2 != null && d3 != null)
                    {
                        return new MeshPoint(Convert.ToDouble(d1), Convert.ToDouble(d2), Convert.ToDouble(d3));
                    }
                }
            }
            catch
            {
            }

            return MeshPoint.Zero;
        }
    }

    internal struct MeshTriangle
    {
        public MeshTriangle(MeshPoint a, MeshPoint b, MeshPoint c)
        {
            A = a;
            B = b;
            C = c;
        }

        public MeshPoint A { get; }

        public MeshPoint B { get; }

        public MeshPoint C { get; }
    }

    internal struct MeshPoint
    {
        public static readonly MeshPoint Zero = new MeshPoint(0d, 0d, 0d);
        public static readonly MeshPoint UnitX = new MeshPoint(1d, 0d, 0d);

        public MeshPoint(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public double X { get; }

        public double Y { get; }

        public double Z { get; }

        public static MeshPoint operator -(MeshPoint left, MeshPoint right)
        {
            return new MeshPoint(left.X - right.X, left.Y - right.Y, left.Z - right.Z);
        }

        public static double Dot(MeshPoint left, MeshPoint right)
        {
            return (left.X * right.X) + (left.Y * right.Y) + (left.Z * right.Z);
        }

        public static MeshPoint Cross(MeshPoint left, MeshPoint right)
        {
            return new MeshPoint(
                (left.Y * right.Z) - (left.Z * right.Y),
                (left.Z * right.X) - (left.X * right.Z),
                (left.X * right.Y) - (left.Y * right.X));
        }
    }
}
