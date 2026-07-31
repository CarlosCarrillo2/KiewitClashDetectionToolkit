using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;
using Autodesk.Navisworks.Api.ComApi;
using Autodesk.Navisworks.Api.Interop;
using Autodesk.Navisworks.Api.Interop.ComApi;
using NavisworksDockPanel.AddIn.Diagnostics;

namespace NavisworksDockPanel.AddIn.Plugin.ClashesToViewpoints
{
    // Ported near-verbatim from the other Kiewit Navisworks plugin's "Viewpoint V2" feature
    // (ViewpointV2Service.cs) - for each selected ClashResultGroup, isolates the items involved
    // in its clashes (red = side A, green = side B, grey/transparent = everything else),
    // frames an isometric camera on them via the low-level COM API (InwOpAnonView/InwNvCamera -
    // NOT the managed Viewpoint.Position/PointAt/ZoomBox, which throws "Viewer not set" on a
    // freshly-constructed Viewpoint), adds redline text labels naming the clashing items, and
    // saves one SavedViewpoint per group under a folder named after its ClashTest.
    //
    // Runs in two phases, exactly as the source project did: phase 1 creates every viewpoint
    // with just the camera/section box (no colors yet), phase 2 goes back and applies color
    // overrides + redline labels + re-saves via ReplaceFromCurrentView. The source project's
    // own diagnostics (ContainsAppearanceOverrides/InwOpView2 COM re-activation dance in
    // PersistFinalColorState) show this ordering was hard-won - color overrides didn't reliably
    // persist into a SavedViewpoint without it - so it's kept as-is rather than "simplified".
    //
    // Only the entry point changed: no WinForms selection dialog / ProgressDialog / MessageBox -
    // the caller (ClashesToViewpointsController) supplies the selected groups (from the React
    // tree UI) and gets a Result back instead of a dialog.
    internal static class ClashesToViewpointsService
    {
        private sealed class PendingViewpointState
        {
            public FolderItem Folder { get; set; }
            public string SavedDisplayName { get; set; }
            public ModelItemCollection ClashItems { get; set; }
            public ModelItemCollection ClashItemsA { get; set; }
            public ModelItemCollection ClashItemsB { get; set; }
        }

        internal sealed class Result
        {
            public string ErrorMessage { get; set; }
            public int FoldersCreated { get; set; }
            public int ViewpointsCreated { get; set; }
            public int TotalGroups { get; set; }
        }

        // Models/roots whose name matches these patterns are always kept hidden in the created
        // viewpoints - carried over verbatim, this matches the sign-off-boundary file naming
        // convention used across this same document family (e.g. MINER-SIGNOFF-AREAS-CKJV-R25).
        private static readonly string[] AlwaysHiddenPatterns = { "MINER-SIGNOFF-AREAS" };
        private static readonly Color ClashColorA = new Color(1.0f, 0.0f, 0.0f);
        private static readonly Color ClashColorB = new Color(0.0f, 1.0f, 0.0f);
        private static readonly Color ContextColor = new Color(0.6f, 0.6f, 0.6f);

        public static Result Execute(Document document, Dictionary<ClashTest, List<ClashResultGroup>> selected)
        {
            if (document == null || document.IsClear)
            {
                return new Result { ErrorMessage = "No Navisworks document is open." };
            }

            if (selected == null || selected.Count == 0)
            {
                return new Result { ErrorMessage = "No clash groups selected." };
            }

            int total = selected.Values.Sum(l => l.Count);
            int done = 0, created = 0, folders = 0;
            InwOpState10 com = ComApiBridge.State;
            var pendingStates = new List<PendingViewpointState>();

            D($"--- PHASE 1: create viewpoints without colors (total={total}) ---");

            foreach (var kv in selected)
            {
                FolderItem folder = GetOrCreateFolder(document, kv.Key.DisplayName ?? "(unnamed)");
                if (folder == null)
                {
                    done += kv.Value.Count;
                    continue;
                }

                folders++;

                foreach (ClashResultGroup group in kv.Value)
                {
                    bool ok = ProcessGroup(document, com, group, folder, pendingStates);
                    D($"  ProcessGroup '{group.DisplayName}' -> ok={ok}");
                    if (ok)
                    {
                        created++;
                    }

                    done++;
                }
            }

            D($"--- PHASE 1 done: {created}/{total} viewpoints created, pendingStates={pendingStates.Count} ---");
            D("--- PHASE 2: apply colors and re-save ---");

            PersistFinalColorState(document, com, pendingStates);

            D("--- PHASE 2 done, resetting scene ---");

            document.Models.ResetAllHidden();
            document.Models.ResetTemporaryMaterials(document.Models.RootItems);
            document.Models.OverrideTemporaryTransparency(document.Models.RootItems, 0);
            ClearSectionBox(document);
            HideAlwaysHidden(document);

            D($"=== DONE: {folders} folder(s), {created}/{total} viewpoint(s) ===");

            return new Result
            {
                ErrorMessage = null,
                FoldersCreated = folders,
                ViewpointsCreated = created,
                TotalGroups = total
            };
        }

        private static bool ProcessGroup(
            Document document, InwOpState10 com,
            ClashResultGroup group, FolderItem folder,
            List<PendingViewpointState> pendingStates)
        {
            D($"  [ProcessGroup] '{group.DisplayName}'");

            var clashItemsA = new ModelItemCollection();
            var clashItemsB = new ModelItemCollection();
            ModelItemCollection clashItems = CollectClashItems(group, clashItemsA, clashItemsB);
            D($"    clashItems={clashItems.Count} A={clashItemsA.Count} B={clashItemsB.Count}");
            if (clashItems.Count == 0)
            {
                D("    SKIP: no clash items");
                return false;
            }

            BoundingBox3D bbox = clashItems.BoundingBox();
            if (bbox == null || bbox.IsEmpty)
            {
                D("    SKIP: bbox empty");
                return false;
            }

            document.Models.ResetTemporaryMaterials(document.Models.RootItems);
            document.Models.OverrideTemporaryTransparency(document.Models.RootItems, 0);
            document.Models.ResetAllHidden();

            double pad = Math.Max(2.0, (bbox.Max.X - bbox.Min.X) * 0.5);
            var sectionBox = new BoundingBox3D(
                new Point3D(bbox.Min.X - pad, bbox.Min.Y - pad, bbox.Min.Z - pad),
                new Point3D(bbox.Max.X + pad, bbox.Max.Y + pad, bbox.Max.Z + pad));
            ApplySectionBox(document, sectionBox);
            FitViewToBox(document, sectionBox);

            SetIsoCamera(com, sectionBox);

            if (com.CurrentView is InwOpView opViewBefore)
            {
                opViewBefore.ApplyHideAttribs = true;
                opViewBefore.ApplyMaterialAttribs = true;
            }

            var savedViewpoint = new SavedViewpoint(document.CurrentViewpoint.CreateCopy());
            document.SavedViewpoints.AddCopy(folder, savedViewpoint);

            SavedViewpoint saved = folder.Children.OfType<SavedViewpoint>().LastOrDefault();
            if (saved == null)
            {
                D("    ERROR: saved viewpoint null after AddCopy");
                return false;
            }

            document.SavedViewpoints.EditDisplayName(saved, group.DisplayName ?? "(unnamed)");
            document.SavedViewpoints.CurrentSavedViewpoint = saved;
            document.SavedViewpoints.ReplaceFromCurrentView(saved);

            pendingStates.Add(new PendingViewpointState
            {
                Folder = folder,
                SavedDisplayName = saved.DisplayName,
                ClashItems = clashItems,
                ClashItemsA = clashItemsA,
                ClashItemsB = clashItemsB
            });

            return true;
        }

        private static void ApplyColorOverrides(
            Document document,
            ModelItemCollection clashItems,
            ModelItemCollection clashItemsA,
            ModelItemCollection clashItemsB,
            bool usePermanent)
        {
            ModelItemCollection renderableA = ExpandToRenderableNodes(clashItemsA);
            ModelItemCollection renderableB = ExpandToRenderableNodes(clashItemsB);
            ModelItemCollection renderableAll = ExpandToRenderableNodes(clashItems);

            var allItems = new ModelItemCollection();
            foreach (Model m in document.Models)
            {
                if (m?.RootItem != null)
                {
                    allItems.Add(m.RootItem);
                }
            }

            if (allItems.Count > 0)
            {
                if (usePermanent)
                {
                    document.Models.OverridePermanentColor(allItems, ContextColor);
                    document.Models.OverridePermanentTransparency(allItems, 0.65);
                }
                else
                {
                    document.Models.OverrideTemporaryColor(allItems, ContextColor);
                    document.Models.OverrideTemporaryTransparency(allItems, 0.65);
                }
            }

            if (usePermanent)
            {
                document.Models.OverridePermanentColor(renderableA, ClashColorA);
                document.Models.OverridePermanentColor(renderableB, ClashColorB);
                document.Models.OverridePermanentTransparency(renderableAll, 0);
            }
            else
            {
                document.Models.OverrideTemporaryColor(renderableA, ClashColorA);
                document.Models.OverrideTemporaryColor(renderableB, ClashColorB);
                document.Models.OverrideTemporaryTransparency(renderableAll, 0);
            }
        }

        private static void FitViewToBox(Document document, BoundingBox3D bbox)
        {
            Viewpoint vp = document.CurrentViewpoint.CreateCopy();
            vp.ZoomBox(bbox);
            document.CurrentViewpoint.CopyFrom(vp);
        }

        private static void ApplySectionBox(Document document, BoundingBox3D bbox)
        {
            Viewpoint vp = document.CurrentViewpoint.CreateCopy();
            var clipPlanes = new ClipPlaneSet
            {
                Enabled = true,
                Mode = ClipPlaneSetMode.Box
            };
            clipPlanes.FitToBox(bbox);
            vp.ClipPlanes = clipPlanes;
            document.CurrentViewpoint.CopyFrom(vp);
        }

        private static void ClearSectionBox(Document document)
        {
            Viewpoint vp = document.CurrentViewpoint.CreateCopy();
            var clipPlanes = new ClipPlaneSet
            {
                Enabled = false,
                Mode = ClipPlaneSetMode.Box
            };
            vp.ClipPlanes = clipPlanes;
            document.CurrentViewpoint.CopyFrom(vp);
        }

        private static void HideAlwaysHidden(Document document)
        {
            var toHide = new ModelItemCollection();
            var seen = new HashSet<ModelItem>();
            foreach (Model model in document.Models)
            {
                if (model?.RootItem == null)
                {
                    continue;
                }

                string fileName = model.FileName ?? string.Empty;
                string rootName = model.RootItem.DisplayName ?? string.Empty;

                bool hideModel = AlwaysHiddenPatterns.Any(p =>
                    fileName.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    rootName.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);

                if (hideModel)
                {
                    foreach (ModelItem child in model.RootItem.Children)
                    {
                        AddItemAndDescendants(toHide, seen, child);
                    }

                    continue;
                }

                foreach (ModelItem child in model.RootItem.Children)
                {
                    string childName = child?.DisplayName ?? string.Empty;
                    bool hideChild = AlwaysHiddenPatterns.Any(p =>
                        childName.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (hideChild)
                    {
                        AddItemAndDescendants(toHide, seen, child);
                    }
                }
            }

            if (toHide.Count > 0)
            {
                document.Models.SetHidden(toHide, true);
            }
        }

        private static void AddItemAndDescendants(ModelItemCollection target, HashSet<ModelItem> seen, ModelItem item)
        {
            if (item == null)
            {
                return;
            }

            foreach (ModelItem node in item.DescendantsAndSelf)
            {
                if (seen.Add(node))
                {
                    target.Add(node);
                }
            }
        }

        // Positions the camera via the low-level COM API rather than the managed
        // Viewpoint.Position/PointAt (see class comment) - avoids "Viewer not set".
        private static void SetIsoCamera(InwOpState10 com, BoundingBox3D bbox)
        {
            double cx = (bbox.Min.X + bbox.Max.X) / 2.0;
            double cy = (bbox.Min.Y + bbox.Max.Y) / 2.0;
            double cz = (bbox.Min.Z + bbox.Max.Z) / 2.0;

            double dx = bbox.Max.X - bbox.Min.X;
            double dy = bbox.Max.Y - bbox.Min.Y;
            double dz = bbox.Max.Z - bbox.Min.Z;
            double diagonal = Math.Sqrt(dx * dx + dy * dy + dz * dz);

            const double k = 0.5773502692;

            if (!(com.CurrentView is InwOpAnonView anonView))
            {
                return;
            }

            InwNvViewPoint comVp = anonView.ViewPoint;
            InwNvCamera cam = comVp.Camera;

            InwLPos3f pos = cam.Position;
            double px = pos.data1, py = pos.data2, pz = pos.data3;
            double dist = Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy) + (pz - cz) * (pz - cz));

            if (dist < 1e-6)
            {
                dist = diagonal * 1.2;
            }

            pos.data1 = cx - k * dist;
            pos.data2 = cy - k * dist;
            pos.data3 = cz + k * dist;
            cam.Position = pos;

            InwLPos3f target = (InwLPos3f)pos.Copy();
            target.data1 = cx;
            target.data2 = cy;
            target.data3 = cz;
            cam.PointAt(target);

            InwLVec3f up = cam.GetUpVector();
            up.data1 = 0;
            up.data2 = 0;
            up.data3 = 1;
            cam.AlignUp(up);

            comVp.Camera = cam;
            anonView.ViewPoint = comVp;
        }

        private static ModelItemCollection CollectClashItems(
            ClashResultGroup group,
            ModelItemCollection clashItemsA,
            ModelItemCollection clashItemsB)
        {
            var col = new ModelItemCollection();
            foreach (ClashResult r in group.Children.OfType<ClashResult>())
            {
                ModelItem item1 = PreferRenderableItem(r.Item1, r.CompositeItem1);
                ModelItem item2 = PreferRenderableItem(r.Item2, r.CompositeItem2);

                if (item1 != null && !clashItemsA.Contains(item1))
                {
                    clashItemsA.Add(item1);
                }

                if (item2 != null && !clashItemsB.Contains(item2))
                {
                    clashItemsB.Add(item2);
                }

                if (item1 != null && !col.Contains(item1))
                {
                    col.Add(item1);
                }

                if (item2 != null && !col.Contains(item2))
                {
                    col.Add(item2);
                }
            }

            return col;
        }

        private static void PersistFinalColorState(
            Document document,
            InwOpState10 com,
            List<PendingViewpointState> pendingStates)
        {
            if (document == null || pendingStates == null || pendingStates.Count == 0)
            {
                return;
            }

            InwOpState11 com11 = com as InwOpState11;

            foreach (PendingViewpointState state in pendingStates)
            {
                if (state?.Folder == null || string.IsNullOrWhiteSpace(state.SavedDisplayName))
                {
                    continue;
                }

                SavedViewpoint saved = state.Folder.Children
                    .OfType<SavedViewpoint>()
                    .FirstOrDefault(x => string.Equals(x.DisplayName, state.SavedDisplayName, StringComparison.Ordinal));

                if (saved == null)
                {
                    D($"    ERROR: '{state.SavedDisplayName}' not found");
                    continue;
                }

                // Re-activate the view through the raw COM layer so the managed
                // Apply*Attribs flags actually stick before we bake overrides in below -
                // ported verbatim, this sequencing came from hard debugging in the source
                // project (see PersistFinalColorState's original diagnostics).
                InwOpView2 comView2 = com11 != null
                    ? FindComView2(com11, state.Folder.DisplayName, state.SavedDisplayName)
                    : null;

                if (comView2 != null)
                {
                    comView2.ApplyMaterialAttribs = true;
                    comView2.ApplyHideAttribs = true;

                    try
                    {
                        com11.ApplyView((InwOpView)(object)comView2);
                        if (com.CurrentView is InwOpView comViewNow)
                        {
                            comViewNow.ApplyMaterialAttribs = true;
                            comViewNow.ApplyHideAttribs = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        D($"    ApplyView exception: {ex.Message}");
                    }
                }
                else
                {
                    document.SavedViewpoints.CurrentSavedViewpoint = saved;
                }

                document.Models.ResetAllHidden();
                document.Models.ResetTemporaryMaterials(document.Models.RootItems);
                document.Models.OverrideTemporaryTransparency(document.Models.RootItems, 0);

                ApplyColorOverrides(document, state.ClashItems, state.ClashItemsA, state.ClashItemsB, usePermanent: false);
                HideAlwaysHidden(document);

                document.SavedViewpoints.ReplaceFromCurrentView(saved);

                SavedViewpoint savedRefreshed = state.Folder.Children
                    .OfType<SavedViewpoint>()
                    .FirstOrDefault(x => string.Equals(x.DisplayName, state.SavedDisplayName, StringComparison.Ordinal));
                AddClashRedlineLabels(document, state.Folder, savedRefreshed ?? saved, state.ClashItemsA, state.ClashItemsB);

                document.Models.ResetTemporaryMaterials(document.Models.RootItems);
            }
        }

        private static void AddClashRedlineLabels(
            Document document,
            FolderItem folder,
            SavedViewpoint saved,
            ModelItemCollection clashItemsA,
            ModelItemCollection clashItemsB)
        {
            if (document == null || saved == null || folder == null)
            {
                return;
            }

            View activeView = document.ActiveView;
            if (activeView == null)
            {
                return;
            }

            int savedIndex = folder.Children.IndexOf(saved);
            if (savedIndex < 0)
            {
                return;
            }

            if (!(saved.CreateCopy() is SavedViewpoint editableSaved))
            {
                return;
            }

            LcOpRedlineList redlines = editableSaved.EditRedlines();
            if (redlines == null)
            {
                return;
            }

            redlines.Clear();

            double halfH = 20.0, halfW = 30.0;
            try
            {
                Viewpoint vp = document.CurrentViewpoint.CreateCopy();
                double hf = vp.HeightField;
                double vw = activeView.Width;
                double vh = activeView.Height;
                halfH = hf / 2.0;
                halfW = halfH * (vw / vh);
            }
            catch
            {
                // Fall back to the defaults above - label placement isn't worth failing over.
            }

            double labelX = -halfW + halfW * 0.03;
            double yStart = halfH - halfH * 0.05;
            double yStep = halfH * 0.06;
            const int textThickness = 3;

            var seen = new HashSet<ModelItem>();
            int labelIdx = 0;

            void AddLabel(ModelItem item, string sideLabel, Color color)
            {
                if (item == null || !seen.Add(item))
                {
                    return;
                }

                try
                {
                    string modelName = GetModelName(item);
                    string elementId = GetElementId(item);
                    string systemName = GetSystemName(item);

                    string tagText =
                        $"{sideLabel} | Model: {modelName} | ID: {elementId ?? string.Empty}" +
                        (string.IsNullOrWhiteSpace(systemName) ? string.Empty : $" | Sys: {systemName}");

                    double y = yStart - labelIdx * yStep;
                    var t = new LcOpRedlineText(tagText, new Point2D(labelX, y));
                    t.SetLineColor(color);
                    t.SetLineThickness(textThickness);
                    redlines.Add(t);
                    labelIdx++;
                }
                catch (Exception ex)
                {
                    D($"    label '{item?.DisplayName}': {ex.GetType().Name}: {ex.Message}");
                }
            }

            foreach (ModelItem item in clashItemsA ?? new ModelItemCollection())
            {
                AddLabel(item, "[A]", ClashColorA);
            }

            bool hasA = clashItemsA != null && clashItemsA.Count > 0;
            bool hasB = clashItemsB != null && clashItemsB.Count > 0;
            if (hasA && hasB)
            {
                labelIdx += 1;
            }

            foreach (ModelItem item in clashItemsB ?? new ModelItemCollection())
            {
                AddLabel(item, "[B]", ClashColorB);
            }

            document.SavedViewpoints.ReplaceWithCopy(folder, savedIndex, editableSaved);
            SavedViewpoint savedAfterReplace = folder.Children
                .OfType<SavedViewpoint>()
                .FirstOrDefault(x => string.Equals(x.DisplayName, saved.DisplayName, StringComparison.Ordinal));
            if (savedAfterReplace != null)
            {
                document.SavedViewpoints.CurrentSavedViewpoint = savedAfterReplace;
            }
        }

        private static InwOpView2 FindComView2(InwOpState11 com11, string folderName, string viewName)
        {
            try
            {
                InwSavedViewsColl root = com11.SavedViews();

                foreach (object item in root)
                {
                    if (item is InwOpFolderView folder)
                    {
                        if (!string.Equals(folder.name, folderName, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        InwSavedViewsColl folderViews = folder.SavedViews();

                        foreach (object sub in folderViews)
                        {
                            if (sub is InwOpView2 view2)
                            {
                                if (string.Equals(view2.name, viewName, StringComparison.Ordinal))
                                {
                                    return view2;
                                }
                            }
                            else if (sub is InwOpView view)
                            {
                                if (string.Equals(view.name, viewName, StringComparison.Ordinal))
                                {
                                    return view as InwOpView2;
                                }
                            }
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                D($"    FindComView2 exception: {ex.Message}");
                return null;
            }
        }

        private static ModelItem PreferRenderableItem(ModelItem primary, ModelItem fallback)
        {
            if (IsRenderableItem(primary))
            {
                return primary;
            }

            if (IsRenderableItem(fallback))
            {
                return fallback;
            }

            return primary ?? fallback;
        }

        private static bool IsRenderableItem(ModelItem item)
        {
            return item != null && item.DescendantsAndSelf.Any(x => x.HasGeometry);
        }

        private static string GetModelName(ModelItem item)
        {
            if (item == null)
            {
                return string.Empty;
            }

            string modelFileName = item.Model?.FileName;
            if (!string.IsNullOrWhiteSpace(modelFileName))
            {
                return Path.GetFileNameWithoutExtension(modelFileName);
            }

            string sourceFile = GetPropValue(item, "source file");
            string normalizedSourceFile = NormalizeSourceFileName(sourceFile);
            if (!string.IsNullOrWhiteSpace(normalizedSourceFile))
            {
                return normalizedSourceFile;
            }

            string topAncestor = item.AncestorsAndSelf
                .Reverse()
                .Select(x => x?.DisplayName)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

            if (!string.IsNullOrWhiteSpace(topAncestor))
            {
                string ext = Path.GetExtension(topAncestor);
                if (string.Equals(ext, ".nwd", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".nwc", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".rvt", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".ifc", StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetFileNameWithoutExtension(topAncestor);
                }
            }

            return string.Empty;
        }

        private static string GetSystemName(ModelItem item)
        {
            if (item == null)
            {
                return null;
            }

            string systemName = GetPropValue(
                item,
                "system name",
                "system abbreviation",
                "piping system",
                "mechanical system",
                "system type",
                "service type");

            if (!string.IsNullOrWhiteSpace(systemName))
            {
                return systemName;
            }

            string electricalStandard = GetPropValue(item, "standard");
            if (!string.IsNullOrWhiteSpace(electricalStandard))
            {
                return electricalStandard;
            }

            string ceiConduitGrouped = GetPropValue(item, "cei_conduit_grouped");
            if (!string.IsNullOrWhiteSpace(ceiConduitGrouped))
            {
                return $"Grouped {ceiConduitGrouped}";
            }

            string wireSize = GetPropValue(item, "cei_conduit_wiresize", "wire size");
            if (!string.IsNullOrWhiteSpace(wireSize))
            {
                return wireSize;
            }

            return null;
        }

        private static string GetElementId(ModelItem item)
        {
            if (item == null)
            {
                return null;
            }

            string propValue = GetPropValue(
                item,
                "element id",
                "revit id",
                "object id",
                "entity handle",
                "handle");

            if (!string.IsNullOrWhiteSpace(propValue))
            {
                return propValue;
            }

            string displayName = item.DisplayName ?? string.Empty;
            int openBracket = displayName.LastIndexOf('[');
            int closeBracket = displayName.LastIndexOf(']');
            if (openBracket >= 0 && closeBracket > openBracket)
            {
                string bracketValue = displayName.Substring(openBracket + 1, closeBracket - openBracket - 1).Trim();
                if (!string.IsNullOrWhiteSpace(bracketValue))
                {
                    return bracketValue;
                }
            }

            return null;
        }

        private static string GetPropValue(ModelItem item, params string[] keys)
        {
            if (item == null || keys == null || keys.Length == 0)
            {
                return null;
            }

            foreach (PropertyCategory cat in item.PropertyCategories)
            {
                if (cat == null)
                {
                    continue;
                }

                foreach (DataProperty prop in cat.Properties)
                {
                    if (prop == null || string.IsNullOrWhiteSpace(prop.DisplayName))
                    {
                        continue;
                    }

                    bool matches = keys.Any(key =>
                        !string.IsNullOrWhiteSpace(key) &&
                        prop.DisplayName.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0);

                    if (!matches)
                    {
                        continue;
                    }

                    string value = GetVariantValue(prop.Value);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }

            return null;
        }

        private static string NormalizeSourceFileName(string sourceFile)
        {
            if (string.IsNullOrWhiteSpace(sourceFile))
            {
                return null;
            }

            string trimmed = sourceFile.Trim();
            int queryIndex = trimmed.IndexOf('?');
            if (queryIndex >= 0)
            {
                trimmed = trimmed.Substring(0, queryIndex);
            }

            string fileName = Path.GetFileNameWithoutExtension(trimmed);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            if (string.Equals(fileName, "0", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return fileName;
        }

        private static string GetVariantValue(VariantData value)
        {
            if (value == null || value.IsNone)
            {
                return null;
            }

            try
            {
                if (value.IsDisplayString) return value.ToDisplayString();
                if (value.IsIdentifierString) return value.ToIdentifierString();
                if (value.IsBoolean) return value.ToBoolean().ToString();
                if (value.IsInt32) return value.ToInt32().ToString();
                if (value.IsInt64) return value.ToInt64().ToString();
                if (value.IsNat32) return value.ToNat32().ToString();
                if (value.IsNat64) return value.ToNat64().ToString();
                if (value.IsDouble) return value.ToDouble().ToString();
                if (value.IsDoubleLength) return value.ToDoubleLength().ToString();
                if (value.IsDoubleArea) return value.ToDoubleArea().ToString();
                if (value.IsDoubleVolume) return value.ToDoubleVolume().ToString();
                if (value.IsDoubleAngle) return value.ToDoubleAngle().ToString();
                if (value.IsDateTime) return value.ToDateTime().ToString("o");
                if (value.IsNamedConstant) return value.ToNamedConstant().DisplayName;
            }
            catch (Exception ex)
            {
                D($"    VariantData conversion failed: {ex.GetType().Name}: {ex.Message}");
            }

            return null;
        }

        private static ModelItemCollection ExpandToRenderableNodes(ModelItemCollection source)
        {
            var result = new ModelItemCollection();
            var seen = new HashSet<ModelItem>();

            foreach (ModelItem item in source)
            {
                if (item == null)
                {
                    continue;
                }

                bool addedGeometryNode = false;
                foreach (ModelItem node in item.DescendantsAndSelf)
                {
                    if (!IsDeepestGeometryNode(node) || !seen.Add(node))
                    {
                        continue;
                    }

                    result.Add(node);
                    addedGeometryNode = true;
                }

                if (!addedGeometryNode && seen.Add(item))
                {
                    result.Add(item);
                }
            }

            return result;
        }

        private static bool IsDeepestGeometryNode(ModelItem item)
        {
            if (item == null || !item.HasGeometry)
            {
                return false;
            }

            return !item.Children.Any(child => child != null && child.DescendantsAndSelf.Any(x => x.HasGeometry));
        }

        private static FolderItem GetOrCreateFolder(Document document, string name)
        {
            FolderItem root = document.SavedViewpoints.RootItem;
            FolderItem existing = root.Children
                .OfType<FolderItem>()
                .FirstOrDefault(f => string.Equals(f.DisplayName, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                return existing;
            }

            var folder = new FolderItem { DisplayName = name };
            int insertIndex = root.Children.Count;
            document.SavedViewpoints.InsertCopy(root, insertIndex, folder);
            return root.Children[insertIndex] as FolderItem;
        }

        private static void D(string msg) => Log.Write($"[ClashesToViewpoints] {msg}");
    }
}
