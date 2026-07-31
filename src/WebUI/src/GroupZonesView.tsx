import { useEffect, useMemo, useState } from "react"
import { Boxes, CircleAlert, RotateCcw, Search } from "lucide-react"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from "@/components/ui/card"
import { Checkbox } from "@/components/ui/checkbox"
import { Input } from "@/components/ui/input"
import { Separator } from "@/components/ui/separator"
import { ScrollArea } from "@/components/ui/scroll-area"
import { Badge } from "@/components/ui/badge"
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog"
import {
  native,
  CLASH_RESULT_STATUSES,
  type ClashResultStatus,
  type ExistingGroupHandling,
  type GeometryModelOption,
  type GroupByZoneResult,
  type ZoneVolume,
} from "@/lib/native"

export function GroupZonesView() {
  const [geometryModels, setGeometryModels] = useState<GeometryModelOption[]>([])
  const [geometryModelId, setGeometryModelId] = useState<number | null>(null)
  const [loadingModels, setLoadingModels] = useState(true)

  const [volumes, setVolumes] = useState<ZoneVolume[]>([])
  const [loadingVolumes, setLoadingVolumes] = useState(false)
  const [selectedVolumes, setSelectedVolumes] = useState<Record<string, boolean>>({})
  const [zoneQuery, setZoneQuery] = useState("")

  const [testNames, setTestNames] = useState<string[]>([])
  const [selectedTests, setSelectedTests] = useState<Record<string, boolean>>({})

  const [selectedStatuses, setSelectedStatuses] = useState<Record<ClashResultStatus, boolean>>(
    Object.fromEntries(CLASH_RESULT_STATUSES.map((s) => [s, true])) as Record<ClashResultStatus, boolean>
  )

  const [singleGroup, setSingleGroup] = useState(false)
  const [singleGroupName, setSingleGroupName] = useState("")
  const [groupOutsideAreas, setGroupOutsideAreas] = useState(true)
  const [existingGroupHandling, setExistingGroupHandling] = useState<ExistingGroupHandling>("keep")

  const [running, setRunning] = useState(false)
  const [showResultDialog, setShowResultDialog] = useState(false)
  const [result, setResult] = useState<GroupByZoneResult | null>(null)

  async function loadVolumes(modelId: number) {
    setLoadingVolumes(true)
    try {
      const list = await native.getZoneVolumes(modelId)
      setVolumes(list)
      setSelectedVolumes(Object.fromEntries(list.map((v) => [v.guid, true])))
    } finally {
      setLoadingVolumes(false)
    }
  }

  useEffect(() => {
    native.getGeometryModels().then((models) => {
      setGeometryModels(models)
      const defaultModel = models.find((m) => m.isLatest) ?? models[0]
      setLoadingModels(false)
      if (defaultModel) {
        setGeometryModelId(defaultModel.id)
        loadVolumes(defaultModel.id)
      }
    })

    native.getClashTestNames().then((names) => {
      setTestNames(names)
      setSelectedTests(Object.fromEntries(names.map((n) => [n, true])))
    })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const filteredVolumes = useMemo(() => {
    const q = zoneQuery.trim().toLowerCase()
    if (!q) return volumes
    return volumes.filter(
      (v) =>
        v.displayLabel.toLowerCase().includes(q) ||
        v.groupName.toLowerCase().includes(q) ||
        v.mark.toLowerCase().includes(q) ||
        v.comments.toLowerCase().includes(q)
    )
  }, [volumes, zoneQuery])

  const selectedVolumeGuids = volumes.filter((v) => selectedVolumes[v.guid]).map((v) => v.guid)
  const totalSelectedVolumes = selectedVolumeGuids.length
  const allVolumesSelected = volumes.length > 0 && volumes.every((v) => selectedVolumes[v.guid])
  const allFilteredVolumesSelected = filteredVolumes.length > 0 && filteredVolumes.every((v) => selectedVolumes[v.guid])

  function toggleAllVolumes(checked: boolean) {
    setSelectedVolumes(Object.fromEntries(volumes.map((v) => [v.guid, checked])))
  }

  function invertVolumes() {
    setSelectedVolumes((prev) => {
      const next = { ...prev }
      for (const v of volumes) next[v.guid] = !prev[v.guid]
      return next
    })
  }

  function selectFilteredVolumes() {
    setSelectedVolumes((prev) => {
      const next = { ...prev }
      for (const v of filteredVolumes) next[v.guid] = true
      return next
    })
  }

  const selectedTestNames = testNames.filter((t) => selectedTests[t])

  function toggleAllTests(checked: boolean) {
    setSelectedTests(Object.fromEntries(testNames.map((t) => [t, checked])))
  }

  function toggleStatus(status: ClashResultStatus, checked: boolean) {
    setSelectedStatuses((prev) => ({ ...prev, [status]: checked }))
  }

  const canRun =
    geometryModelId !== null &&
    selectedVolumeGuids.length > 0 &&
    selectedTestNames.length > 0 &&
    !running &&
    (!singleGroup || singleGroupName.trim().length > 0)

  async function runGrouping() {
    if (geometryModelId === null) return
    setRunning(true)
    try {
      const statuses = CLASH_RESULT_STATUSES.filter((s) => selectedStatuses[s])
      const groupResult = await native.groupByZone({
        geometryModelId,
        volumeGuids: selectedVolumeGuids,
        statuses,
        testNames: selectedTestNames,
        existingGroupHandling,
        singleGroupName: singleGroup ? singleGroupName.trim() : null,
        groupOutsideAreas,
      })
      setResult(groupResult)
      setShowResultDialog(true)
    } finally {
      setRunning(false)
    }
  }

  return (
    <Card>
      <CardHeader>
        <div className="flex items-center gap-2.5">
          <span className="flex size-7 shrink-0 items-center justify-center rounded-lg bg-primary text-sm font-semibold text-primary-foreground">
            K
          </span>
          <div>
            <CardTitle>Group by Zone</CardTitle>
            <CardDescription>Match clash results to zone volumes and group them in Clash Detective</CardDescription>
          </div>
        </div>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        <div className="rounded-md border p-3">
          <span className="mb-2 block text-xs font-medium tracking-wide text-muted-foreground uppercase">
            Geometry Model
          </span>
          <div className="flex gap-2">
            <select
              value={geometryModelId ?? ""}
              disabled={loadingModels || geometryModels.length === 0}
              onChange={(e) => setGeometryModelId(Number(e.target.value))}
              className="h-8 flex-1 rounded-lg border border-input bg-transparent px-2 text-sm outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50"
            >
              {geometryModels.length === 0 && <option value="">No models found</option>}
              {geometryModels.map((m) => (
                <option key={m.id} value={m.id}>
                  {m.displayLabel}
                </option>
              ))}
            </select>
            <Button
              variant="outline"
              size="sm"
              disabled={geometryModelId === null || loadingVolumes}
              onClick={() => geometryModelId !== null && loadVolumes(geometryModelId)}
            >
              {loadingVolumes ? "Loading..." : "Load Zones"}
            </Button>
          </div>
        </div>

        {volumes.length > 0 && (
          <>
            <div className="relative">
              <Search className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" />
              <Input
                placeholder="Filter zones by name or mark..."
                value={zoneQuery}
                onChange={(e) => setZoneQuery(e.target.value)}
                className="pl-8"
              />
            </div>

            <span className="text-sm text-muted-foreground">
              {totalSelectedVolumes} / {volumes.length} zones selected
              {zoneQuery && ` (${filteredVolumes.length} shown)`}
            </span>

            <div className="grid grid-cols-4 gap-2">
              <Button variant="outline" size="sm" onClick={() => toggleAllVolumes(true)} disabled={allVolumesSelected}>
                Select All
              </Button>
              <Button
                variant="outline"
                size="sm"
                onClick={() => toggleAllVolumes(false)}
                disabled={totalSelectedVolumes === 0}
              >
                Deselect All
              </Button>
              <Button variant="outline" size="sm" onClick={invertVolumes}>
                <RotateCcw />
                Invert
              </Button>
              <Button
                variant="outline"
                size="sm"
                onClick={selectFilteredVolumes}
                disabled={!zoneQuery.trim() || allFilteredVolumesSelected}
              >
                Select Filtered
              </Button>
            </div>

            <ScrollArea className="h-48 rounded-md border">
              <div className="flex flex-col gap-1 p-2">
                {filteredVolumes.length === 0 && (
                  <p className="p-2 text-sm text-muted-foreground">No zones match "{zoneQuery}".</p>
                )}
                {filteredVolumes.map((volume) => {
                  const isChecked = selectedVolumes[volume.guid] ?? false
                  return (
                    <div
                      key={volume.guid}
                      className={`flex items-center gap-2 rounded-md px-2 py-1.5 transition-colors ${
                        isChecked ? "bg-accent/60" : "hover:bg-accent/30"
                      }`}
                    >
                      <Checkbox
                        id={volume.guid}
                        checked={isChecked}
                        onCheckedChange={(checked) => setSelectedVolumes((prev) => ({ ...prev, [volume.guid]: checked === true }))}
                      />
                      <label htmlFor={volume.guid} className="flex-1 truncate text-sm font-medium">
                        {volume.comments || volume.displayLabel}
                      </label>
                      <Badge variant="secondary" className="shrink-0">
                        {volume.groupName}
                      </Badge>
                    </div>
                  )
                })}
              </div>
            </ScrollArea>
          </>
        )}

        {!loadingVolumes && volumes.length === 0 && geometryModelId !== null && (
          <p className="text-sm text-muted-foreground">
            No zone/volume candidates found for this model. Try "Load Zones" again or pick a different model.
          </p>
        )}

        <Separator />

        {testNames.length > 0 && (
          <div className="rounded-md border p-3">
            <div className="mb-2 flex items-center justify-between">
              <span className="text-xs font-medium tracking-wide text-muted-foreground uppercase">
                Clash Tests ({selectedTestNames.length} / {testNames.length})
              </span>
              <div className="flex gap-1.5">
                <Button variant="ghost" size="xs" onClick={() => toggleAllTests(true)}>
                  All
                </Button>
                <Button variant="ghost" size="xs" onClick={() => toggleAllTests(false)}>
                  None
                </Button>
              </div>
            </div>
            <ScrollArea className="h-28 rounded-md border">
              <div className="flex flex-col gap-1 p-2">
                {testNames.map((name) => (
                  <label key={name} className="flex items-center gap-2 rounded-md px-2 py-1 text-sm hover:bg-accent/30">
                    <Checkbox
                      checked={selectedTests[name] ?? false}
                      onCheckedChange={(checked) => setSelectedTests((prev) => ({ ...prev, [name]: checked === true }))}
                    />
                    <span className="truncate">{name}</span>
                  </label>
                ))}
              </div>
            </ScrollArea>
          </div>
        )}

        <div className="rounded-md border p-3">
          <span className="mb-2 block text-xs font-medium tracking-wide text-muted-foreground uppercase">
            Status Filter
          </span>
          <div className="grid grid-cols-2 gap-1.5">
            {CLASH_RESULT_STATUSES.map((status) => (
              <label key={status} className="flex items-center gap-2 text-sm">
                <Checkbox
                  checked={selectedStatuses[status]}
                  onCheckedChange={(checked) => toggleStatus(status, checked === true)}
                />
                {status}
              </label>
            ))}
          </div>
        </div>

        <div className="rounded-md border p-3">
          <span className="mb-2 block text-xs font-medium tracking-wide text-muted-foreground uppercase">
            Grouping Options
          </span>

          <div className="flex flex-col gap-2">
            <label className="flex items-center gap-2 text-sm">
              <Checkbox checked={singleGroup} onCheckedChange={(checked) => setSingleGroup(checked === true)} />
              Group all clashes into a single group
            </label>
            {singleGroup && (
              <Input
                placeholder="Group name"
                value={singleGroupName}
                onChange={(e) => setSingleGroupName(e.target.value)}
              />
            )}

            <label className="flex items-center gap-2 text-sm">
              <Checkbox
                checked={groupOutsideAreas}
                onCheckedChange={(checked) => setGroupOutsideAreas(checked === true)}
              />
              Group clashes outside zones ("Outside Areas")
            </label>

            <div className="flex flex-col gap-1 pt-1">
              <label htmlFor="existing-group-handling" className="text-xs text-muted-foreground">
                If groups already exist for a test
              </label>
              <select
                id="existing-group-handling"
                value={existingGroupHandling}
                onChange={(e) => setExistingGroupHandling(e.target.value as ExistingGroupHandling)}
                className="h-8 rounded-lg border border-input bg-transparent px-2 text-sm outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50"
              >
                <option value="keep">Keep existing groups and add to them</option>
                <option value="remove">Remove existing groups and start fresh</option>
              </select>
            </div>
          </div>
        </div>

        <Button className="w-full" onClick={runGrouping} disabled={!canRun}>
          <Boxes />
          {running ? "Grouping..." : `Group by Zone (${selectedVolumeGuids.length} zones, ${selectedTestNames.length} tests)`}
        </Button>
      </CardContent>

      <AlertDialog open={showResultDialog} onOpenChange={setShowResultDialog}>
        <AlertDialogContent className="sm:max-w-md">
          <AlertDialogHeader>
            <AlertDialogTitle>Group by Zone</AlertDialogTitle>
            <AlertDialogDescription>
              {result?.errorMessage
                ? result.errorMessage
                : `Grouped ${result?.groupedResults ?? 0} clash result${(result?.groupedResults ?? 0) === 1 ? "" : "s"} across ${result?.processedTests ?? 0} test${(result?.processedTests ?? 0) === 1 ? "" : "s"}.`}
            </AlertDialogDescription>
          </AlertDialogHeader>

          {result && !result.errorMessage && result.groupNames.length > 0 && (
            <div className="flex flex-col gap-1.5">
              <span className="text-xs font-medium tracking-wide text-muted-foreground uppercase">
                Groups created ({result.groupNames.length})
              </span>
              <div className="flex max-h-40 flex-wrap gap-1.5 overflow-y-auto rounded-md border p-2">
                {result.groupNames.map((name) => (
                  <Badge key={name} variant="secondary">
                    {name}
                  </Badge>
                ))}
              </div>
            </div>
          )}

          {result && result.unmatchedCount > 0 && (
            <div className="flex items-center gap-1.5 text-xs text-muted-foreground">
              <CircleAlert className="size-3.5 shrink-0" />
              {result.unmatchedCount} clash{result.unmatchedCount === 1 ? "" : "es"} could not be matched to a zone.
            </div>
          )}

          <AlertDialogFooter>
            <AlertDialogAction onClick={() => setShowResultDialog(false)}>Accept</AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </Card>
  )
}
