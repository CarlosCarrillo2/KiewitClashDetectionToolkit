using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;

namespace NavisworksDockPanel.AddIn.Plugin.ZoneGrouping
{
    // JSON-friendly facade over VolumeClashGroupingService for NativeMessageBridge - mirrors
    // ClashMatrixGenerator's role for the Clash Test Generation view. Geometry models are
    // addressed by their position in a freshly recomputed GetGeometryModels() list (that
    // ordering is deterministic for a given document state); volumes are addressed by their
    // real, stable Navisworks Guid.
    internal static class ZoneGroupingController
    {
        public static List<Dictionary<string, object>> GetGeometryModels()
        {
            Document document = Application.ActiveDocument;
            var result = new List<Dictionary<string, object>>();
            if (document == null)
            {
                return result;
            }

            List<GeometryModelOption> models = VolumeClashGroupingService.GetGeometryModels(document);
            for (int i = 0; i < models.Count; i++)
            {
                result.Add(new Dictionary<string, object>
                {
                    ["id"] = i,
                    ["displayLabel"] = models[i].DisplayLabel,
                    ["isLatest"] = models[i].IsLatest
                });
            }

            return result;
        }

        public static List<Dictionary<string, object>> GetZoneVolumes(int geometryModelId)
        {
            var result = new List<Dictionary<string, object>>();
            Document document = Application.ActiveDocument;
            if (document == null)
            {
                return result;
            }

            List<GeometryModelOption> models = VolumeClashGroupingService.GetGeometryModels(document);
            if (geometryModelId < 0 || geometryModelId >= models.Count)
            {
                return result;
            }

            GeometryModelOption selected = models[geometryModelId];
            IReadOnlyList<VolumeCandidate> volumes = VolumeClashGroupingService.GetVolumeCandidates(document, models, selected);

            foreach (VolumeCandidate volume in volumes)
            {
                result.Add(new Dictionary<string, object>
                {
                    ["guid"] = volume.InstanceGuid.ToString(),
                    ["displayLabel"] = volume.DisplayLabel,
                    ["groupName"] = volume.GroupName,
                    ["mark"] = volume.MarkParameter ?? string.Empty,
                    ["comments"] = volume.CommentsParameter ?? string.Empty
                });
            }

            return result;
        }

        public static List<string> GetClashTestNames()
        {
            Document document = Application.ActiveDocument;
            if (document == null)
            {
                return new List<string>();
            }

            return VolumeClashGroupingService.GetExistingClashTests(document)
                .Select(test => test.DisplayName)
                .ToList();
        }

        public static Dictionary<string, object> GroupByZone(
            int geometryModelId,
            List<string> volumeGuids,
            List<string> statuses,
            List<string> testNames,
            string existingGroupHandling,
            string singleGroupName,
            bool groupOutsideAreas)
        {
            Document document = Application.ActiveDocument;
            if (document == null)
            {
                return ToDictionary(ZoneGroupingResult.Error("No active document."));
            }

            List<GeometryModelOption> models = VolumeClashGroupingService.GetGeometryModels(document);
            if (geometryModelId < 0 || geometryModelId >= models.Count)
            {
                return ToDictionary(ZoneGroupingResult.Error("Invalid geometry model selection - reload zones and try again."));
            }

            GeometryModelOption geometryModel = models[geometryModelId];
            IReadOnlyList<VolumeCandidate> allVolumes = VolumeClashGroupingService.GetVolumeCandidates(document, models, geometryModel);

            var guidSet = new HashSet<string>(volumeGuids ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            List<VolumeCandidate> selectedVolumes = guidSet.Count == 0
                ? allVolumes.ToList()
                : allVolumes.Where(v => guidSet.Contains(v.InstanceGuid.ToString())).ToList();

            if (selectedVolumes.Count == 0)
            {
                return ToDictionary(ZoneGroupingResult.Error("No zones were selected - select at least one zone and try again."));
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

            VolumeClashGroupingService.ExistingGroupHandling handling =
                string.Equals(existingGroupHandling, "remove", StringComparison.OrdinalIgnoreCase)
                    ? VolumeClashGroupingService.ExistingGroupHandling.RemoveAndRegroup
                    : VolumeClashGroupingService.ExistingGroupHandling.KeepAndRegroup;

            ZoneGroupingResult result = VolumeClashGroupingService.ExecuteGrouping(
                document,
                geometryModel,
                selectedVolumes,
                selectedStatuses,
                testNames,
                handling,
                string.IsNullOrWhiteSpace(singleGroupName) ? null : singleGroupName,
                groupOutsideAreas);

            return ToDictionary(result);
        }

        private static Dictionary<string, object> ToDictionary(ZoneGroupingResult result)
        {
            return new Dictionary<string, object>
            {
                ["errorMessage"] = result.ErrorMessage,
                ["processedTests"] = result.ProcessedTests,
                ["groupedResults"] = result.GroupedResults,
                ["groupNames"] = result.GroupNames,
                ["unmatchedCount"] = result.UnmatchedCount,
                ["unmatchedLogPath"] = result.UnmatchedLogPath
            };
        }
    }
}
