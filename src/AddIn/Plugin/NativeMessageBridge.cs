using System.Collections.Generic;
using System.Web.Script.Serialization;
using NavisworksDockPanel.AddIn.Plugin.ClashesToViewpoints;
using NavisworksDockPanel.AddIn.Plugin.CoincidentGrouping;
using NavisworksDockPanel.AddIn.Plugin.ModelGrouping;
using NavisworksDockPanel.AddIn.Plugin.SignOff;
using NavisworksDockPanel.AddIn.Plugin.ZoneGrouping;

namespace NavisworksDockPanel.AddIn.Plugin
{
    // Dispatches JSON messages posted from the React/shadcn UI (via
    // window.chrome.webview.postMessage) to the real Navisworks API logic, and returns a
    // JSON response to post back. WebViewHost only relays raw strings - it has no idea
    // what the messages mean, keeping all business logic and JSON handling here.
    internal static class NativeMessageBridge
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public static string Handle(string requestJson)
        {
            var request = Json.Deserialize<Dictionary<string, object>>(requestJson);
            string action = request.TryGetValue("action", out object a) ? a as string : null;

            switch (action)
            {
                case "getModels":
                    return Json.Serialize(new Dictionary<string, object>
                    {
                        ["action"] = "models",
                        ["models"] = ClashMatrixGenerator.GetModelNames(),
                        ["units"] = ClashMatrixGenerator.GetUnitsLabel()
                    });

                case "runClash":
                    List<string> selected = request.TryGetValue("selected", out object s)
                        ? Json.ConvertToType<List<string>>(s)
                        : null;
                    ClashSettings settings = request.TryGetValue("settings", out object st)
                        ? ParseSettings(Json.ConvertToType<Dictionary<string, object>>(st))
                        : ClashSettings.Default;
                    bool combineSingleVsAll = request.TryGetValue("combineSingleVsAll", out object csva)
                        && Json.ConvertToType<bool>(csva);
                    string summary = ClashMatrixGenerator.GenerateAndRun(selected, settings, combineSingleVsAll);
                    return Json.Serialize(new Dictionary<string, object>
                    {
                        ["action"] = "clashResult",
                        ["summary"] = summary
                    });

                case "getClashTests":
                    return Json.Serialize(new Dictionary<string, object>
                    {
                        ["action"] = "clashTests",
                        ["tests"] = ClashMatrixGenerator.GetClashTests()
                    });

                case "deleteClashTests":
                    List<string> guids = request.TryGetValue("guids", out object g)
                        ? Json.ConvertToType<List<string>>(g)
                        : null;
                    string deleteSummary = ClashMatrixGenerator.DeleteClashTests(guids);
                    return Json.Serialize(new Dictionary<string, object>
                    {
                        ["action"] = "clashTestsDeleted",
                        ["summary"] = deleteSummary
                    });

                case "getGeometryModels":
                    return Json.Serialize(new Dictionary<string, object>
                    {
                        ["action"] = "geometryModels",
                        ["models"] = ZoneGroupingController.GetGeometryModels()
                    });

                case "getZoneVolumes":
                    int geometryModelId = request.TryGetValue("geometryModelId", out object gmid)
                        ? Json.ConvertToType<int>(gmid)
                        : -1;
                    return Json.Serialize(new Dictionary<string, object>
                    {
                        ["action"] = "zoneVolumes",
                        ["volumes"] = ZoneGroupingController.GetZoneVolumes(geometryModelId)
                    });

                case "getClashTestNames":
                    return Json.Serialize(new Dictionary<string, object>
                    {
                        ["action"] = "clashTestNames",
                        ["names"] = ZoneGroupingController.GetClashTestNames()
                    });

                case "groupByZone":
                    int gmId = request.TryGetValue("geometryModelId", out object gmid2)
                        ? Json.ConvertToType<int>(gmid2)
                        : -1;
                    List<string> volumeGuids = request.TryGetValue("volumeGuids", out object vg)
                        ? Json.ConvertToType<List<string>>(vg)
                        : null;
                    List<string> statuses = request.TryGetValue("statuses", out object stat)
                        ? Json.ConvertToType<List<string>>(stat)
                        : null;
                    List<string> testNames = request.TryGetValue("testNames", out object tn)
                        ? Json.ConvertToType<List<string>>(tn)
                        : null;
                    string existingGroupHandling = request.TryGetValue("existingGroupHandling", out object egh)
                        ? egh as string
                        : null;
                    string singleGroupName = request.TryGetValue("singleGroupName", out object sgn)
                        ? sgn as string
                        : null;
                    bool groupOutsideAreas = request.TryGetValue("groupOutsideAreas", out object goa)
                        && Json.ConvertToType<bool>(goa);
                    Dictionary<string, object> groupResult = ZoneGroupingController.GroupByZone(
                        gmId, volumeGuids, statuses, testNames, existingGroupHandling, singleGroupName, groupOutsideAreas);
                    groupResult["action"] = "zoneGroupingResult";
                    return Json.Serialize(groupResult);

                case "getClashGroupTree":
                    return Json.Serialize(new Dictionary<string, object>
                    {
                        ["action"] = "clashGroupTree",
                        ["tests"] = CoincidentGroupingController.GetClashGroupTree()
                    });

                case "groupCoincidentElements":
                    List<Dictionary<string, object>> selections = request.TryGetValue("selections", out object sel)
                        ? Json.ConvertToType<List<Dictionary<string, object>>>(sel)
                        : null;
                    List<string> coincidentStatuses = request.TryGetValue("statuses", out object cstat)
                        ? Json.ConvertToType<List<string>>(cstat)
                        : null;
                    bool removeSourceGroups = !request.TryGetValue("removeSourceGroups", out object rsg)
                        || Json.ConvertToType<bool>(rsg);
                    Dictionary<string, object> coincidentResult = CoincidentGroupingController.GroupCoincidentElements(
                        selections, coincidentStatuses, removeSourceGroups);
                    coincidentResult["action"] = "coincidentGroupingResult";
                    return Json.Serialize(coincidentResult);

                case "getModelPriorityClashTree":
                    return Json.Serialize(new Dictionary<string, object>
                    {
                        ["action"] = "modelPriorityClashTree",
                        ["tests"] = ModelPriorityGroupingController.GetClashTree()
                    });

                case "getModelsInvolvedInGroups":
                    List<Dictionary<string, object>> involvedSelections = request.TryGetValue("selections", out object isel)
                        ? Json.ConvertToType<List<Dictionary<string, object>>>(isel)
                        : null;
                    List<string> involvedStatuses = request.TryGetValue("statuses", out object istat)
                        ? Json.ConvertToType<List<string>>(istat)
                        : null;
                    return Json.Serialize(new Dictionary<string, object>
                    {
                        ["action"] = "modelsInvolvedInGroups",
                        ["models"] = ModelPriorityGroupingController.GetModelsInvolvedInGroups(involvedSelections, involvedStatuses)
                    });

                case "createClashesToViewpoints":
                    List<Dictionary<string, object>> ctvSelections = request.TryGetValue("selections", out object ctvSel)
                        ? Json.ConvertToType<List<Dictionary<string, object>>>(ctvSel)
                        : null;
                    Dictionary<string, object> ctvResult = ClashesToViewpointsController.CreateViewpoints(ctvSelections);
                    ctvResult["action"] = "clashesToViewpointsCreated";
                    return Json.Serialize(ctvResult);

                case "createSignOffViewpoints":
                    int signOffModelId = request.TryGetValue("geometryModelId", out object somid)
                        ? Json.ConvertToType<int>(somid)
                        : -1;
                    List<string> signOffVolumeGuids = request.TryGetValue("volumeGuids", out object sovg)
                        ? Json.ConvertToType<List<string>>(sovg)
                        : null;
                    Dictionary<string, object> signOffResult = SignOffAreaController.CreateSignOffViewpoints(signOffModelId, signOffVolumeGuids);
                    signOffResult["action"] = "signOffViewpointsCreated";
                    return Json.Serialize(signOffResult);

                case "selectModelRoot":
                    string selectModelName = request.TryGetValue("modelName", out object smn) ? smn as string : null;
                    Dictionary<string, object> selectResult = ModelPriorityGroupingController.SelectModelRoot(selectModelName);
                    selectResult["action"] = "modelRootSelected";
                    return Json.Serialize(selectResult);

                case "groupByModelPriority":
                    List<Dictionary<string, object>> modelSelections = request.TryGetValue("selections", out object msel)
                        ? Json.ConvertToType<List<Dictionary<string, object>>>(msel)
                        : null;
                    List<string> modelStatuses = request.TryGetValue("statuses", out object mstat)
                        ? Json.ConvertToType<List<string>>(mstat)
                        : null;
                    List<string> modelPriority = request.TryGetValue("modelPriority", out object mp)
                        ? Json.ConvertToType<List<string>>(mp)
                        : null;
                    bool modelRemoveSourceGroups = !request.TryGetValue("removeSourceGroups", out object mrsg)
                        || Json.ConvertToType<bool>(mrsg);
                    bool groupRemaining = request.TryGetValue("groupRemaining", out object gr)
                        && Json.ConvertToType<bool>(gr);
                    string remainingGroupName = request.TryGetValue("remainingGroupName", out object rgn)
                        ? rgn as string
                        : null;
                    Dictionary<string, object> modelResult = ModelPriorityGroupingController.GroupByModelPriority(
                        modelSelections, modelStatuses, modelPriority, modelRemoveSourceGroups, groupRemaining, remainingGroupName);
                    modelResult["action"] = "modelGroupingResult";
                    return Json.Serialize(modelResult);

                default:
                    return Json.Serialize(new Dictionary<string, object>
                    {
                        ["action"] = "error",
                        ["message"] = $"Unknown action '{action}'"
                    });
            }
        }

        private static ClashSettings ParseSettings(Dictionary<string, object> settings)
        {
            if (settings == null)
            {
                return ClashSettings.Default;
            }

            return new ClashSettings
            {
                TestType = settings.TryGetValue("testType", out object tt) ? tt as string : ClashSettings.Default.TestType,
                Tolerance = settings.TryGetValue("tolerance", out object tol) ? Json.ConvertToType<double>(tol) : ClashSettings.Default.Tolerance,
                MergeComposites = settings.TryGetValue("mergeComposites", out object mc) && Json.ConvertToType<bool>(mc),
                Link = settings.TryGetValue("link", out object lk) ? lk as string : ClashSettings.Default.Link,
                Step = settings.TryGetValue("step", out object stp) ? Json.ConvertToType<double>(stp) : ClashSettings.Default.Step
            };
        }
    }
}
