using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;

namespace NavisworksDockPanel.AddIn.Plugin
{
    // Settings shown in the panel's "Settings" section, mirroring Navisworks' own Clash
    // Detective test settings (Type / Tolerance / Link / Step / Composite Object Clashing).
    // Link only picks a SimulationType here (None/Timeliner) - there's no UI yet for
    // choosing *which* saved Timeliner simulation to attach (ClashTest.AnimatorSimulation),
    // so a Timeliner-linked test is created without one, same as Step (sec)
    // (ClashTest.SimulationStep) only ever affects Timeliner-driven Clearance tests.
    internal sealed class ClashSettings
    {
        public string TestType { get; set; }
        public double Tolerance { get; set; }
        public bool MergeComposites { get; set; }
        public string Link { get; set; }
        public double Step { get; set; }

        public static ClashSettings Default => new ClashSettings
        {
            TestType = "Hard",
            Tolerance = 0.01,
            MergeComposites = true,
            Link = "None",
            Step = 0.1
        };

        public ClashTestType ToApiTestType()
        {
            switch (TestType)
            {
                case "HardConservative": return ClashTestType.HardConservative;
                case "Clearance": return ClashTestType.Clearance;
                case "Duplicate": return ClashTestType.Duplicate;
                default: return ClashTestType.Hard;
            }
        }

        public SimulationType ToApiSimulationType()
        {
            return Link == "Timeliner" ? SimulationType.Timeliner : SimulationType.None;
        }
    }

    // Generates a classical BIM "clash matrix": every model in the current document
    // clashed against every other model, exactly once (upper triangle only - A vs B is
    // the same test as B vs A). Uses the real Navisworks Clash Detective API
    // (Autodesk.Navisworks.Api.Clash), not a mock.
    internal static class ClashMatrixGenerator
    {
        public static List<string> GetModelNames()
        {
            Document doc = Application.ActiveDocument;
            return doc == null ? new List<string>() : GetClashUnits(doc).Select(u => u.Name).ToList();
        }

        // Maps a ModelItem back to the same model/unit name GetModelNames() lists, by walking
        // up its ancestor chain until it reaches one of GetClashUnits()'s designated items
        // (a Model.RootItem for the multi-model case, or a top-level child for a single merged
        // NWD). Used by features that group clash results by source model (e.g. Group by
        // Model priority) so their model names line up exactly with the Clash Test Generation
        // view's own model list.
        public static string GetModelNameForItem(ModelItem item)
        {
            if (item == null)
            {
                return string.Empty;
            }

            Document doc = Application.ActiveDocument;
            if (doc == null)
            {
                return string.Empty;
            }

            List<(string Name, ModelItem Item)> units = GetClashUnits(doc);
            foreach (var unit in units)
            {
                for (ModelItem current = item; current != null; current = current.Parent)
                {
                    if (ReferenceEquals(current, unit.Item))
                    {
                        return unit.Name;
                    }
                }
            }

            return string.Empty;
        }

        // Reverse of GetModelNameForItem - looks up the root ModelItem for a clash unit by
        // the same name GetModelNames()/GetModelNameForItem() use, so callers can select/
        // highlight that model's actual geometry in the Navisworks view.
        public static ModelItem GetModelRootItem(string modelName)
        {
            if (string.IsNullOrEmpty(modelName))
            {
                return null;
            }

            Document doc = Application.ActiveDocument;
            if (doc == null)
            {
                return null;
            }

            return GetClashUnits(doc)
                .Where(u => string.Equals(u.Name, modelName, System.StringComparison.OrdinalIgnoreCase))
                .Select(u => u.Item)
                .FirstOrDefault();
        }

        // The tolerance the user types is only meaningful alongside a unit label - and it
        // must be the active document's own units, not a hardcoded one, since Navisworks
        // documents can be authored in anything from millimeters to miles.
        public static string GetUnitsLabel()
        {
            Document doc = Application.ActiveDocument;
            if (doc == null)
            {
                return "m";
            }

            switch (doc.Units)
            {
                case Units.Meters: return "m";
                case Units.Centimeters: return "cm";
                case Units.Millimeters: return "mm";
                case Units.Feet: return "ft";
                case Units.Inches: return "in";
                case Units.Yards: return "yd";
                case Units.Kilometers: return "km";
                case Units.Miles: return "mi";
                case Units.Micrometers: return "µm";
                case Units.Mils: return "mil";
                case Units.Microinches: return "µin";
                default: return doc.Units.ToString();
            }
        }

        // selectedNames: null or empty runs every model against every other model.
        // Exactly one selected name clashes that model against every other model in the
        // document (not just the selection). Two or more selected names clash every-pair
        // only among that subset. Mirrors computeMatrixPairs() on the React side, which
        // renders the same pairing as a preview before this runs.
        //
        // combineSingleVsAll only applies to the exactly-one-selected case: instead of one
        // ClashTest per pairing (model vs other1, model vs other2, ...), it produces a single
        // ClashTest with SelectionA = the one model and SelectionB = every other model
        // combined - "model vs all" as one test.
        public static string GenerateAndRun(IEnumerable<string> selectedNames, ClashSettings settings, bool combineSingleVsAll = false)
        {
            settings = settings ?? ClashSettings.Default;

            Document doc = Application.ActiveDocument;
            if (doc == null)
            {
                return "No active document.";
            }

            List<(string Name, ModelItem Item)> allUnits = GetClashUnits(doc);
            List<string> wantedList = selectedNames?.ToList();
            bool singleVsAll = wantedList != null && wantedList.Count == 1;

            DocumentClashTests testsData = doc.GetClash().TestsData;
            var createdTests = new List<ClashTest>();

            if (singleVsAll && combineSingleVsAll)
            {
                (string Name, ModelItem Item) only = allUnits.FirstOrDefault(u => u.Name == wantedList[0]);
                List<(string Name, ModelItem Item)> others = allUnits.Where(u => u.Name != wantedList[0]).ToList();

                if (others.Count == 0)
                {
                    return "Only 1 model in the document - need at least 2 to generate a clash test.";
                }

                var combinedTest = new ClashTest
                {
                    DisplayName = $"{only.Name} vs All",
                    TestType = settings.ToApiTestType(),
                    Tolerance = settings.Tolerance,
                    MergeComposites = settings.MergeComposites,
                    SimulationType = settings.ToApiSimulationType(),
                    SimulationStep = settings.Step
                };
                combinedTest.SelectionA.Selection.CopyFrom(new ModelItemCollection { only.Item });
                combinedTest.SelectionB.Selection.CopyFrom(others.Select(o => o.Item));

                testsData.TestsAddCopy(combinedTest);
                createdTests.Add(combinedTest);
            }
            else
            {
                List<(string Name, ModelItem Item)> pairsA;
                List<(string Name, ModelItem Item)> pairsB;

                if (singleVsAll)
                {
                    (string Name, ModelItem Item) only = allUnits.FirstOrDefault(u => u.Name == wantedList[0]);
                    pairsA = new List<(string, ModelItem)> { only };
                    pairsB = allUnits.Where(u => u.Name != wantedList[0]).ToList();
                }
                else
                {
                    List<(string Name, ModelItem Item)> units = wantedList == null || wantedList.Count == 0
                        ? allUnits
                        : allUnits.Where(u => wantedList.Contains(u.Name)).ToList();

                    if (units.Count < 2)
                    {
                        return $"Only {units.Count} model(s) selected - need at least 2 to generate a clash matrix.";
                    }

                    pairsA = units;
                    pairsB = units;
                }

                for (int i = 0; i < pairsA.Count; i++)
                {
                    int startJ = singleVsAll ? 0 : i + 1;
                    for (int j = startJ; j < pairsB.Count; j++)
                    {
                        var test = new ClashTest
                        {
                            DisplayName = $"{pairsA[i].Name} vs {pairsB[j].Name}",
                            TestType = settings.ToApiTestType(),
                            Tolerance = settings.Tolerance,
                            MergeComposites = settings.MergeComposites,
                            SimulationType = settings.ToApiSimulationType(),
                            SimulationStep = settings.Step
                        };
                        test.SelectionA.Selection.CopyFrom(new ModelItemCollection { pairsA[i].Item });
                        test.SelectionB.Selection.CopyFrom(new ModelItemCollection { pairsB[j].Item });

                        testsData.TestsAddCopy(test);
                        createdTests.Add(test);
                    }
                }
            }

            testsData.TestsRunAllTests();

            var summary = new StringBuilder();
            summary.AppendLine($"Generated and ran {createdTests.Count} clash test(s):");
            summary.AppendLine();

            foreach (ClashTest liveTest in testsData.Tests.OfType<ClashTest>())
            {
                if (createdTests.Any(t => t.DisplayName == liveTest.DisplayName))
                {
                    summary.AppendLine($"  {liveTest.DisplayName}: {liveTest.Children.Count} clash result(s)");
                }
            }

            return summary.ToString();
        }

        // Handles two shapes of document:
        //  - a live session with multiple appended models (Document.Models has 2+ entries,
        //    each Model is its own clash unit)
        //  - a single merged .nwd (Document.Models has exactly 1 entry - everything was
        //    combined and saved together), where the original per-file distinction survives
        //    only as top-level nodes in that one model's tree, so those become the units
        //    instead.
        private static List<(string Name, ModelItem Item)> GetClashUnits(Document doc)
        {
            List<Model> models = doc.Models.ToList();

            if (models.Count >= 2)
            {
                return models
                    .Select((model, index) => (ModelDisplayName(model, index), model.RootItem))
                    .ToList();
            }

            if (models.Count == 1)
            {
                List<ModelItem> topLevelItems = models[0].RootItem.Children.ToList();
                if (topLevelItems.Count >= 2)
                {
                    return topLevelItems.Select(item => (item.DisplayName, item)).ToList();
                }

                return new List<(string, ModelItem)> { (ModelDisplayName(models[0], 0), models[0].RootItem) };
            }

            return new List<(string, ModelItem)>();
        }

        private static string ModelDisplayName(Model model, int index)
        {
            string fileName = model.FileName;
            return string.IsNullOrEmpty(fileName) ? $"Model {index + 1}" : System.IO.Path.GetFileNameWithoutExtension(fileName);
        }

        // Flattens the clash test tree (tests can be nested inside GroupItem folders in
        // Clash Detective) into the list the Delete Clash Tests panel filters/selects from.
        // Guid is SavedItem's own stable identifier - DisplayName alone isn't unique enough
        // to safely target a single test for deletion.
        public static List<Dictionary<string, object>> GetClashTests()
        {
            var result = new List<Dictionary<string, object>>();
            Document doc = Application.ActiveDocument;
            if (doc == null)
            {
                return result;
            }

            CollectTests(doc.GetClash().TestsData.Tests, result);
            return result;
        }

        private static void CollectTests(SavedItemCollection items, List<Dictionary<string, object>> result)
        {
            foreach (SavedItem item in items)
            {
                if (item is ClashTest test)
                {
                    result.Add(new Dictionary<string, object>
                    {
                        ["guid"] = test.Guid.ToString(),
                        ["displayName"] = test.DisplayName,
                        ["resultCount"] = test.Children.Count
                    });
                }
                else if (item is GroupItem group)
                {
                    CollectTests(group.Children, result);
                }
            }
        }

        // guids: the Guid strings (see GetClashTests) of the tests to delete, as picked in
        // the Delete Clash Tests panel.
        public static string DeleteClashTests(IEnumerable<string> guids)
        {
            Document doc = Application.ActiveDocument;
            if (doc == null)
            {
                return "No active document.";
            }

            var wanted = new HashSet<string>(guids ?? Enumerable.Empty<string>(), System.StringComparer.OrdinalIgnoreCase);
            if (wanted.Count == 0)
            {
                return "No clash tests selected.";
            }

            DocumentClashTests testsData = doc.GetClash().TestsData;
            int deleted = RemoveMatching(testsData, testsData.Tests, wanted);
            return $"Deleted {deleted} clash test(s).";
        }

        // Snapshots each SavedItemCollection with ToList() before removing from it, since
        // TestsRemove/TestsRemove(parent, item) mutate the live collection being enumerated.
        private static int RemoveMatching(DocumentClashTests testsData, SavedItemCollection items, HashSet<string> guids)
        {
            int count = 0;
            foreach (SavedItem item in items.OfType<SavedItem>().ToList())
            {
                if (item is ClashTest test && guids.Contains(test.Guid.ToString()))
                {
                    if (test.Parent == null)
                    {
                        testsData.TestsRemove(test);
                    }
                    else
                    {
                        testsData.TestsRemove(test.Parent, test);
                    }
                    count++;
                }
                else if (item is GroupItem group)
                {
                    count += RemoveMatching(testsData, group.Children, guids);
                }
            }
            return count;
        }
    }
}
