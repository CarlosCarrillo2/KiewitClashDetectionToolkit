using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.DocumentParts;
using NavisworksDockPanel.AddIn.Diagnostics;
using NavisworksDockPanel.AddIn.Plugin.ZoneGrouping;

namespace NavisworksDockPanel.AddIn.Plugin.SignOff
{
    // "Sign Off Area": pick a geometry model (same dropdown data as Group by Zone) and check
    // which volumes/zones to sign off (same VolumeCandidate detection as Group by Zone - no
    // separate zone-finding engine). For each checked area this:
    //   1. Reads the area's own geometry (VolumeClashGroupingService's mesh extraction).
    //   2. Finds which model elements are inside/intersecting that geometry (real mesh
    //      containment via corner-point sampling, not just bounding-box overlap).
    //   3. The Sign Off Areas folder + saved viewpoint name double as the "classification" -
    //      there's no persistable custom item property in the Navisworks API to tag elements
    //      with, so the grouping itself (which viewpoint captured which elements) is it.
    //   4-6. Hides everything else, frames the camera on the area, and bakes both the camera
    //      and the hide/require state into one SavedViewpoint via CaptureRuntimeOverrides,
    //      filed under a "Sign Off Areas" folder in Saved Viewpoints.
    internal static class SignOffAreaController
    {
        private const string SignOffFolderName = "Sign Off Areas";

        public static Dictionary<string, object> CreateSignOffViewpoints(int geometryModelId, List<string> volumeGuids)
        {
            var total = Stopwatch.StartNew();
            Log.Write($"[SignOff] CreateSignOffViewpoints started - geometryModelId={geometryModelId}, requestedGuids={volumeGuids?.Count ?? 0}");

            Document document = Application.ActiveDocument;
            if (document == null)
            {
                return Error("No active document.");
            }

            var phase = Stopwatch.StartNew();
            List<GeometryModelOption> models = VolumeClashGroupingService.GetGeometryModels(document);
            Log.Write($"[SignOff] GetGeometryModels took {phase.ElapsedMilliseconds} ms ({models.Count} models).");

            if (geometryModelId < 0 || geometryModelId >= models.Count)
            {
                return Error("Invalid geometry model selection - reload sign off areas and try again.");
            }

            GeometryModelOption geometryModel = models[geometryModelId];

            // Re-runs the same full-tree property scan "Load Areas" already ran on the
            // frontend, just to turn the selected guids back into VolumeCandidate objects.
            phase.Restart();
            IReadOnlyList<VolumeCandidate> allVolumes = VolumeClashGroupingService.GetVolumeCandidates(document, models, geometryModel);
            Log.Write($"[SignOff] GetVolumeCandidates (full-tree rescan) took {phase.ElapsedMilliseconds} ms ({allVolumes.Count} candidates found).");

            var guidSet = new HashSet<string>(volumeGuids ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            List<VolumeCandidate> selected = guidSet.Count == 0
                ? new List<VolumeCandidate>()
                : allVolumes.Where(v => guidSet.Contains(v.InstanceGuid.ToString())).ToList();

            if (selected.Count == 0)
            {
                return Error("No sign off areas were selected - select at least one and try again.");
            }

            // The sign off areas themselves usually live in a dedicated boundary/zone file that
            // has little or no real construction geometry of its own - the elements actually
            // worth isolating are in the OTHER appended models. One full pass over every
            // geometry-bearing element across those other models, reused for every selected
            // area below - the expensive part (real-geometry containment) still happens per
            // area, but at least the tree walk + bounding-box lookup itself is not repeated.
            phase.Restart();
            List<ElementCandidate> elements = CollectElementsInScope(models, geometryModel, selected);
            Log.Write($"[SignOff] Scanned {elements.Count} candidate element(s) with geometry across {models.Count - 1} other model(s) in {phase.ElapsedMilliseconds} ms.");

            phase.Restart();
            DocumentSavedViewpoints savedViewpoints = document.SavedViewpoints;
            FolderItem signOffFolder = FindOrCreateFolder(savedViewpoints, savedViewpoints.RootItem, SignOffFolderName);
            Log.Write($"[SignOff] FindOrCreateFolder took {phase.ElapsedMilliseconds} ms.");

            // "Hide others" has to hide EVERY model's root (the zone-source file included -
            // its boundary/mass volumes aren't meant to be visible either), not just the one
            // the areas came from - the matched elements live in the other models, so hiding
            // only the zone-source model left everything else exactly as visible as before.
            List<ModelItem> allScopeRoots = models
                .Select(m => m.ScopeRootItem)
                .Where(root => root != null)
                .ToList();

            var createdNames = new List<string>();
            int skipped = 0;

            // CaptureRuntimeOverrides() snapshots whatever is hidden/required on the LIVE
            // document right now - so hide state and camera position both have to be pushed
            // onto document.Models / document.CurrentViewpoint before each capture, then
            // cleaned up afterward so the user isn't left staring at a half-hidden model.
            Viewpoint originalViewpoint = document.CurrentViewpoint.CreateCopy();

            try
            {
                foreach (VolumeCandidate area in selected)
                {
                    var areaTimer = Stopwatch.StartNew();
                    document.Models.ResetAllHidden();

                    if (!area.HasBounds)
                    {
                        skipped++;
                        Log.Write($"[SignOff]   '{area.DisplayLabel}' skipped (no bounds).");
                        continue;
                    }

                    var areaBox = new BoundingBox3D(
                        new Point3D(area.MinX, area.MinY, area.MinZ),
                        new Point3D(area.MaxX, area.MaxY, area.MaxZ));

                    var meshTimer = Stopwatch.StartNew();
                    VolumeGeometryMesh areaMesh = VolumeClashGroupingService.ExtractVolumeGeometryMesh(document, area, false);
                    long meshMs = meshTimer.ElapsedMilliseconds;

                    var matchTimer = Stopwatch.StartNew();
                    List<ModelItem> matched = MatchElementsToArea(area, areaBox, areaMesh, elements);
                    long matchMs = matchTimer.ElapsedMilliseconds;

                    if (matched.Count == 0)
                    {
                        skipped++;
                        Log.Write(
                            $"[SignOff]   '{area.DisplayLabel}' skipped (0 elements matched out of {elements.Count} " +
                            $"candidates - mesh={meshMs}ms, match={matchMs}ms).");
                        continue;
                    }

                    document.Models.SetHidden(allScopeRoots, true);
                    document.Models.SetHidden(matched, false);

                    Point3D center = areaBox.Center;
                    double diagonal = areaBox.Size.Length;
                    double distance = Math.Max(diagonal, 1.0) * 1.5;
                    Vector3D direction = new Vector3D(1, -1, 0.8).Normalize();
                    Point3D cameraPosition = center.Add(direction * distance);

                    Viewpoint desired = document.CurrentViewpoint.CreateCopy();
                    desired.Position = cameraPosition;
                    desired.PointAt(center);
                    desired.ZoomBox(areaBox);
                    document.CurrentViewpoint.CopyFrom(desired);

                    string name = BuildViewpointName(area);
                    SavedViewpoint captured = savedViewpoints.CaptureRuntimeOverrides();
                    captured.DisplayName = name;

                    int insertIndex = signOffFolder.Children.Count;
                    savedViewpoints.InsertCopy(signOffFolder, insertIndex, captured);
                    createdNames.Add(name);

                    Log.Write(
                        $"[SignOff]   '{name}' created in {areaTimer.ElapsedMilliseconds}ms " +
                        $"({matched.Count}/{elements.Count} elements matched - mesh={meshMs}ms, match={matchMs}ms).");
                }
            }
            finally
            {
                document.Models.ResetAllHidden();
                document.CurrentViewpoint.CopyFrom(originalViewpoint);
            }

            Log.Write($"[SignOff] CreateSignOffViewpoints finished in {total.ElapsedMilliseconds} ms - created={createdNames.Count}, skipped={skipped}.");

            if (createdNames.Count == 0)
            {
                return Error("None of the selected sign off areas matched any elements.");
            }

            return new Dictionary<string, object>
            {
                ["errorMessage"] = null,
                ["createdCount"] = createdNames.Count,
                ["viewpointNames"] = createdNames,
                ["skippedCount"] = skipped
            };
        }

        // One pass over every OTHER model's scope (not the one the sign off areas themselves
        // came from - that file is usually just the boundary/zone definitions, with little or
        // no real construction geometry of its own). Also excludes the area items' own guids
        // as a safety net in case a model legitimately mixes zone volumes with real geometry.
        private static List<ElementCandidate> CollectElementsInScope(
            List<GeometryModelOption> models,
            GeometryModelOption zoneSourceModel,
            List<VolumeCandidate> areas)
        {
            var excludedGuids = new HashSet<Guid>(areas.Select(a => a.InstanceGuid));
            var result = new List<ElementCandidate>();

            foreach (GeometryModelOption model in models)
            {
                // ModelIndex alone isn't a safe exclusion key here: in the single-merged-NWD
                // fallback (GetGeometryModelsFromNwdHierarchy), every top-level "model" shares
                // the same underlying ModelIndex (0) - only ScopeRootItem actually identifies
                // which top-level node this option represents.
                if (ReferenceEquals(model.ScopeRootItem, zoneSourceModel.ScopeRootItem))
                {
                    continue;
                }

                ModelItem scopeRoot = model.ScopeRootItem;
                if (scopeRoot == null)
                {
                    continue;
                }

                foreach (ModelItem item in scopeRoot.DescendantsAndSelf)
                {
                    if (!item.HasGeometry || excludedGuids.Contains(item.InstanceGuid))
                    {
                        continue;
                    }

                    BoundingBox3D box = VolumeClashGroupingService.GetItemBoundingBox(item);
                    if (box == null)
                    {
                        continue;
                    }

                    result.Add(new ElementCandidate(item, box));
                }
            }

            return result;
        }

        // Bounding-box pre-filter (cheap) first, then a real-geometry containment test (mesh
        // ray-casting via VolumeClashGroupingService.IsPointInsideMesh, reused from Group by
        // Zone) sampling each surviving candidate's bounding-box corners plus its center - an
        // element counts as "inside/intersecting" if any sampled point falls inside the area's
        // actual mesh. This is an approximation of true mesh-vs-mesh intersection (which this
        // codebase has no primitive for), not exact for elements that clip through a wall of
        // the area without any sampled point landing inside it.
        private static List<ModelItem> MatchElementsToArea(
            VolumeCandidate area,
            BoundingBox3D areaBox,
            VolumeGeometryMesh areaMesh,
            List<ElementCandidate> elements)
        {
            var matched = new List<ModelItem>();
            bool canUseMesh = areaMesh != null && areaMesh.CanEvaluateContainment;

            foreach (ElementCandidate candidate in elements)
            {
                if (!areaBox.Intersects(candidate.BoundingBox))
                {
                    continue;
                }

                if (!canUseMesh)
                {
                    // No usable mesh for this area (extraction failed) - fall back to the
                    // bounding-box overlap that already passed above.
                    matched.Add(candidate.Item);
                    continue;
                }

                if (GetSampledCorners(candidate.BoundingBox).Any(point => VolumeClashGroupingService.IsPointInsideMesh(point, areaMesh)))
                {
                    matched.Add(candidate.Item);
                }
            }

            return matched;
        }

        private static IEnumerable<Point3D> GetSampledCorners(BoundingBox3D box)
        {
            Point3D min = box.Min;
            Point3D max = box.Max;

            yield return box.Center;
            yield return new Point3D(min.X, min.Y, min.Z);
            yield return new Point3D(min.X, min.Y, max.Z);
            yield return new Point3D(min.X, max.Y, min.Z);
            yield return new Point3D(min.X, max.Y, max.Z);
            yield return new Point3D(max.X, min.Y, min.Z);
            yield return new Point3D(max.X, min.Y, max.Z);
            yield return new Point3D(max.X, max.Y, min.Z);
            yield return new Point3D(max.X, max.Y, max.Z);
        }

        private static FolderItem FindOrCreateFolder(DocumentSavedViewpoints savedViewpoints, FolderItem parent, string name)
        {
            FolderItem existing = parent.Children.OfType<FolderItem>()
                .FirstOrDefault(f => string.Equals(f.DisplayName, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                return existing;
            }

            var folder = new FolderItem { DisplayName = name };
            int insertIndex = parent.Children.Count;
            savedViewpoints.InsertCopy(parent, insertIndex, folder);
            return parent.Children[insertIndex] as FolderItem
                ?? throw new InvalidOperationException($"Unable to create saved-viewpoint folder '{name}'.");
        }

        private static string BuildViewpointName(VolumeCandidate volume)
        {
            string label = !string.IsNullOrWhiteSpace(volume.MarkParameter)
                ? volume.MarkParameter
                : !string.IsNullOrWhiteSpace(volume.NameParameter)
                    ? volume.NameParameter
                    : volume.DisplayLabel;

            return $"{label} [{volume.SourceModelName}]";
        }

        private static Dictionary<string, object> Error(string message)
        {
            return new Dictionary<string, object>
            {
                ["errorMessage"] = message,
                ["createdCount"] = 0,
                ["viewpointNames"] = new List<string>(),
                ["skippedCount"] = 0
            };
        }

        private sealed class ElementCandidate
        {
            public ElementCandidate(ModelItem item, BoundingBox3D boundingBox)
            {
                Item = item;
                BoundingBox = boundingBox;
            }

            public ModelItem Item { get; }

            public BoundingBox3D BoundingBox { get; }
        }
    }
}
