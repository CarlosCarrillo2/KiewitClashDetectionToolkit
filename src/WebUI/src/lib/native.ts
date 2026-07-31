// Thin wrapper around the WebView2 <-> native message bridge (see
// WebViewHostControl.xaml.cs / NativeMessageBridge.cs on the C# side). Falls back to mock
// data when run outside WebView2 (e.g. `npm run dev` in a normal browser) so the UI stays
// usable for frontend-only iteration.

type WebView2Global = {
  postMessage: (message: string) => void
  addEventListener: (type: "message", listener: (event: MessageEvent<string>) => void) => void
  removeEventListener: (type: "message", listener: (event: MessageEvent<string>) => void) => void
}

function getWebView2(): WebView2Global | undefined {
  return (window as unknown as { chrome?: { webview?: WebView2Global } }).chrome?.webview
}

function waitForAction<T extends { action: string }>(action: string): Promise<T> {
  const webview = getWebView2()
  if (!webview) {
    return Promise.reject(new Error("WebView2 not available"))
  }

  return new Promise((resolve) => {
    function handler(event: MessageEvent<string>) {
      try {
        const data = JSON.parse(event.data) as T
        if (data.action === action) {
          webview!.removeEventListener("message", handler)
          resolve(data)
        }
      } catch {
        // ignore malformed/unrelated messages
      }
    }
    webview.addEventListener("message", handler)
  })
}

const MOCK_MODELS = ["Architecture.rvt", "Structure.rvt", "Mechanical.nwc"]
const MOCK_UNITS = "m"

export type ClashTestInfo = {
  guid: string
  displayName: string
  resultCount: number
}

const MOCK_CLASH_TESTS: ClashTestInfo[] = [
  { guid: "mock-1", displayName: "Architecture.rvt vs Structure.rvt", resultCount: 12 },
  { guid: "mock-2", displayName: "Architecture.rvt vs Mechanical.nwc", resultCount: 4 },
  { guid: "mock-3", displayName: "Structure.rvt vs Mechanical.nwc", resultCount: 0 },
]

// Pushed unprompted from the C# side (WebUiDockPanePlugin.ShowView) when a ribbon button
// activates/refocuses the dock pane, so the page can switch views without a page reload.
export type SetViewMessage = { action: "setView"; view: string }

export function onSetView(callback: (view: string) => void): () => void {
  const webview = getWebView2()
  if (!webview) {
    return () => {}
  }

  function handler(event: MessageEvent<string>) {
    try {
      const data = JSON.parse(event.data) as { action?: string; view?: string }
      if (data.action === "setView" && typeof data.view === "string") {
        callback(data.view)
      }
    } catch {
      // ignore malformed/unrelated messages
    }
  }

  webview.addEventListener("message", handler)
  return () => webview.removeEventListener("message", handler)
}

// Mirrors Autodesk.Navisworks.Api.Clash.ClashTestType (Autodesk.Navisworks.Clash.dll).
// "Custom" is deliberately omitted - Navisworks itself doesn't offer it as a user-selectable
// type either.
export type ClashTestType = "Hard" | "HardConservative" | "Clearance" | "Duplicate"

// Mirrors Autodesk.Navisworks.Api.SimulationType, minus "Animator" - Navisworks' classic
// clash settings Link dropdown only offers None/Timeliner.
export type ClashLink = "None" | "Timeliner"

export type ClashSettings = {
  testType: ClashTestType
  tolerance: number
  mergeComposites: boolean
  link: ClashLink
  step: number
}

// Mirrors Autodesk.Navisworks.Api.Clash.ClashResultStatus.
export type ClashResultStatus = "New" | "Active" | "Reviewed" | "Approved" | "Resolved"

export const CLASH_RESULT_STATUSES: ClashResultStatus[] = ["New", "Active", "Reviewed", "Approved", "Resolved"]

export type GeometryModelOption = {
  id: number
  displayLabel: string
  isLatest: boolean
}

export type ZoneVolume = {
  guid: string
  displayLabel: string
  groupName: string
  mark: string
  comments: string
}

export type ExistingGroupHandling = "keep" | "remove"

export type GroupByZoneRequest = {
  geometryModelId: number
  volumeGuids: string[]
  statuses: ClashResultStatus[]
  testNames: string[]
  existingGroupHandling: ExistingGroupHandling
  singleGroupName: string | null
  groupOutsideAreas: boolean
}

export type GroupByZoneResult = {
  errorMessage: string | null
  processedTests: number
  groupedResults: number
  groupNames: string[]
  unmatchedCount: number
  unmatchedLogPath: string | null
}

export type ClashGroupInfo = {
  groupChildIndex: number
  groupName: string
  resultCount: number
}

export type ClashGroupTestNode = {
  testIndex: number
  testName: string
  groups: ClashGroupInfo[]
}

export type CoincidentGroupSelection = {
  testIndex: number
  groupChildIndex: number
}

export type CoincidentGroupingResult = {
  errorMessage: string | null
  groupsCreated: number
  clashesGrouped: number
  clashesUngrouped: number
}

export type ModelPriorityGroupingResult = {
  errorMessage: string | null
  groupsCreated: number
  clashesGrouped: number
  clashesUngrouped: number
}

export type ClashesToViewpointsResult = {
  errorMessage: string | null
  foldersCreated: number
  viewpointsCreated: number
  totalGroups: number
}

export type SignOffViewpointsResult = {
  errorMessage: string | null
  createdCount: number
  viewpointNames: string[]
  skippedCount: number
}

const MOCK_GEOMETRY_MODELS: GeometryModelOption[] = [
  { id: 0, displayLabel: "Architecture.rvt (Latest)", isLatest: true },
  { id: 1, displayLabel: "Structure.rvt", isLatest: false },
]

const MOCK_CLASH_GROUP_TREE: ClashGroupTestNode[] = [
  {
    testIndex: 0,
    testName: "Architecture.rvt vs Structure.rvt",
    groups: [
      { groupChildIndex: 0, groupName: "101", resultCount: 5 },
      { groupChildIndex: 1, groupName: "102", resultCount: 3 },
    ],
  },
  {
    testIndex: 1,
    testName: "Architecture.rvt vs Mechanical.nwc",
    groups: [{ groupChildIndex: 0, groupName: "OUTSIDE AREAS", resultCount: 2 }],
  },
]

const MOCK_ZONE_VOLUMES: ZoneVolume[] = [
  {
    guid: "zone-1",
    displayLabel: "Level 1 - Zone A - 101 [Architecture.rvt]",
    groupName: "101",
    mark: "101",
    comments: "Mechanical Room",
  },
  {
    guid: "zone-2",
    displayLabel: "Level 1 - Zone B - 102 [Architecture.rvt]",
    groupName: "102",
    mark: "102",
    comments: "Electrical Room",
  },
  {
    guid: "zone-3",
    displayLabel: "Level 2 - Zone A - 201 [Architecture.rvt]",
    groupName: "201",
    mark: "201",
    comments: "",
  },
]

const MOCK_CLASH_TEST_NAMES = ["Architecture.rvt vs Structure.rvt", "Architecture.rvt vs Mechanical.nwc"]

export const native = {
  isAvailable(): boolean {
    return getWebView2() !== undefined
  },

  async getModels(): Promise<{ models: string[]; units: string }> {
    const webview = getWebView2()
    if (!webview) {
      return { models: MOCK_MODELS, units: MOCK_UNITS }
    }

    const responsePromise = waitForAction<{ action: "models"; models: string[]; units: string }>("models")
    webview.postMessage(JSON.stringify({ action: "getModels" }))
    const result = await responsePromise
    return { models: result.models, units: result.units }
  },

  async runClash(selected: string[], settings: ClashSettings, combineSingleVsAll = false): Promise<string> {
    const webview = getWebView2()
    if (!webview) {
      return `(preview mode - no Navisworks connection)\nWould clash: ${selected.join(", ")}\nSettings: ${JSON.stringify(settings)}${combineSingleVsAll ? "\n(combined into a single test)" : ""}`
    }

    const responsePromise = waitForAction<{ action: "clashResult"; summary: string }>("clashResult")
    webview.postMessage(JSON.stringify({ action: "runClash", selected, settings, combineSingleVsAll }))
    return (await responsePromise).summary
  },

  async getClashTests(): Promise<ClashTestInfo[]> {
    const webview = getWebView2()
    if (!webview) {
      return MOCK_CLASH_TESTS
    }

    const responsePromise = waitForAction<{ action: "clashTests"; tests: ClashTestInfo[] }>("clashTests")
    webview.postMessage(JSON.stringify({ action: "getClashTests" }))
    return (await responsePromise).tests
  },

  async deleteClashTests(guids: string[]): Promise<string> {
    const webview = getWebView2()
    if (!webview) {
      return `(preview mode - no Navisworks connection)\nWould delete: ${guids.join(", ")}`
    }

    const responsePromise = waitForAction<{ action: "clashTestsDeleted"; summary: string }>("clashTestsDeleted")
    webview.postMessage(JSON.stringify({ action: "deleteClashTests", guids }))
    return (await responsePromise).summary
  },

  async getGeometryModels(): Promise<GeometryModelOption[]> {
    const webview = getWebView2()
    if (!webview) {
      return MOCK_GEOMETRY_MODELS
    }

    const responsePromise = waitForAction<{ action: "geometryModels"; models: GeometryModelOption[] }>("geometryModels")
    webview.postMessage(JSON.stringify({ action: "getGeometryModels" }))
    return (await responsePromise).models
  },

  async getZoneVolumes(geometryModelId: number): Promise<ZoneVolume[]> {
    const webview = getWebView2()
    if (!webview) {
      return MOCK_ZONE_VOLUMES
    }

    const responsePromise = waitForAction<{ action: "zoneVolumes"; volumes: ZoneVolume[] }>("zoneVolumes")
    webview.postMessage(JSON.stringify({ action: "getZoneVolumes", geometryModelId }))
    return (await responsePromise).volumes
  },

  async getClashTestNames(): Promise<string[]> {
    const webview = getWebView2()
    if (!webview) {
      return MOCK_CLASH_TEST_NAMES
    }

    const responsePromise = waitForAction<{ action: "clashTestNames"; names: string[] }>("clashTestNames")
    webview.postMessage(JSON.stringify({ action: "getClashTestNames" }))
    return (await responsePromise).names
  },

  async groupByZone(request: GroupByZoneRequest): Promise<GroupByZoneResult> {
    const webview = getWebView2()
    if (!webview) {
      return {
        errorMessage: null,
        processedTests: request.testNames.length,
        groupedResults: request.volumeGuids.length * 2,
        groupNames: MOCK_ZONE_VOLUMES.filter((v) => request.volumeGuids.includes(v.guid)).map((v) => v.groupName),
        unmatchedCount: 0,
        unmatchedLogPath: null,
      }
    }

    const responsePromise = waitForAction<{ action: "zoneGroupingResult" } & GroupByZoneResult>("zoneGroupingResult")
    webview.postMessage(JSON.stringify({ action: "groupByZone", ...request }))
    return await responsePromise
  },

  async getClashGroupTree(): Promise<ClashGroupTestNode[]> {
    const webview = getWebView2()
    if (!webview) {
      return MOCK_CLASH_GROUP_TREE
    }

    const responsePromise = waitForAction<{ action: "clashGroupTree"; tests: ClashGroupTestNode[] }>("clashGroupTree")
    webview.postMessage(JSON.stringify({ action: "getClashGroupTree" }))
    return (await responsePromise).tests
  },

  async getModelPriorityClashTree(): Promise<ClashGroupTestNode[]> {
    const webview = getWebView2()
    if (!webview) {
      return MOCK_CLASH_GROUP_TREE
    }

    const responsePromise = waitForAction<{ action: "modelPriorityClashTree"; tests: ClashGroupTestNode[] }>(
      "modelPriorityClashTree"
    )
    webview.postMessage(JSON.stringify({ action: "getModelPriorityClashTree" }))
    return (await responsePromise).tests
  },

  async getModelsInvolvedInGroups(
    selections: CoincidentGroupSelection[],
    statuses: ClashResultStatus[]
  ): Promise<string[]> {
    const webview = getWebView2()
    if (!webview) {
      const mockModels = ["Architecture.rvt", "Structure.rvt", "Mechanical.nwc"]
      return mockModels.slice(0, Math.min(mockModels.length, Math.max(1, selections.length)))
    }

    const responsePromise = waitForAction<{ action: "modelsInvolvedInGroups"; models: string[] }>(
      "modelsInvolvedInGroups"
    )
    webview.postMessage(JSON.stringify({ action: "getModelsInvolvedInGroups", selections, statuses }))
    return (await responsePromise).models
  },

  async selectModelRoot(modelName: string): Promise<{ errorMessage: string | null }> {
    const webview = getWebView2()
    if (!webview) {
      return { errorMessage: null }
    }

    const responsePromise = waitForAction<{ action: "modelRootSelected"; errorMessage: string | null }>(
      "modelRootSelected"
    )
    webview.postMessage(JSON.stringify({ action: "selectModelRoot", modelName }))
    return await responsePromise
  },

  async groupCoincidentElements(
    selections: CoincidentGroupSelection[],
    statuses: ClashResultStatus[],
    removeSourceGroups: boolean
  ): Promise<CoincidentGroupingResult> {
    const webview = getWebView2()
    if (!webview) {
      return {
        errorMessage: null,
        groupsCreated: selections.length,
        clashesGrouped: selections.length * 3,
        clashesUngrouped: 1,
      }
    }

    const responsePromise = waitForAction<{ action: "coincidentGroupingResult" } & CoincidentGroupingResult>(
      "coincidentGroupingResult"
    )
    webview.postMessage(JSON.stringify({ action: "groupCoincidentElements", selections, statuses, removeSourceGroups }))
    return await responsePromise
  },

  async groupByModelPriority(
    selections: CoincidentGroupSelection[],
    statuses: ClashResultStatus[],
    modelPriority: string[],
    removeSourceGroups: boolean,
    groupRemaining: boolean,
    remainingGroupName: string | null
  ): Promise<ModelPriorityGroupingResult> {
    const webview = getWebView2()
    if (!webview) {
      return {
        errorMessage: null,
        groupsCreated: modelPriority.length + (groupRemaining ? 1 : 0),
        clashesGrouped: selections.length * 2,
        clashesUngrouped: groupRemaining ? 0 : 1,
      }
    }

    const responsePromise = waitForAction<{ action: "modelGroupingResult" } & ModelPriorityGroupingResult>(
      "modelGroupingResult"
    )
    webview.postMessage(
      JSON.stringify({
        action: "groupByModelPriority",
        selections,
        statuses,
        modelPriority,
        removeSourceGroups,
        groupRemaining,
        remainingGroupName,
      })
    )
    return await responsePromise
  },

  async createSignOffViewpoints(geometryModelId: number, volumeGuids: string[]): Promise<SignOffViewpointsResult> {
    const webview = getWebView2()
    if (!webview) {
      return {
        errorMessage: null,
        createdCount: volumeGuids.length,
        viewpointNames: volumeGuids,
        skippedCount: 0,
      }
    }

    const responsePromise = waitForAction<{ action: "signOffViewpointsCreated" } & SignOffViewpointsResult>(
      "signOffViewpointsCreated"
    )
    webview.postMessage(JSON.stringify({ action: "createSignOffViewpoints", geometryModelId, volumeGuids }))
    return await responsePromise
  },

  async createClashesToViewpoints(selections: CoincidentGroupSelection[]): Promise<ClashesToViewpointsResult> {
    const webview = getWebView2()
    if (!webview) {
      return {
        errorMessage: null,
        foldersCreated: new Set(selections.map((s) => s.testIndex)).size,
        viewpointsCreated: selections.length,
        totalGroups: selections.length,
      }
    }

    const responsePromise = waitForAction<{ action: "clashesToViewpointsCreated" } & ClashesToViewpointsResult>(
      "clashesToViewpointsCreated"
    )
    webview.postMessage(JSON.stringify({ action: "createClashesToViewpoints", selections }))
    return await responsePromise
  },
}
