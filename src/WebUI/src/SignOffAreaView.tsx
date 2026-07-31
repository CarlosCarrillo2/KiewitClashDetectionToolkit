import { useEffect, useMemo, useState } from "react"
import { CircleAlert, RotateCcw, Search, Video } from "lucide-react"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from "@/components/ui/card"
import { Checkbox } from "@/components/ui/checkbox"
import { Input } from "@/components/ui/input"
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
import { native, type GeometryModelOption, type SignOffViewpointsResult, type ZoneVolume } from "@/lib/native"

// First iteration: pick a model, check which sign off areas (volumes/zones - same detection
// as Group by Zone) to sign off, and create one saved Viewpoint per checked area, framed on
// its geometry, filed under a "Sign Off Areas" folder in Saved Viewpoints.
export function SignOffAreaView() {
  const [geometryModels, setGeometryModels] = useState<GeometryModelOption[]>([])
  const [geometryModelId, setGeometryModelId] = useState<number | null>(null)
  const [loadingModels, setLoadingModels] = useState(true)

  const [areas, setAreas] = useState<ZoneVolume[]>([])
  const [loadingAreas, setLoadingAreas] = useState(false)
  const [selectedAreas, setSelectedAreas] = useState<Record<string, boolean>>({})
  const [query, setQuery] = useState("")

  const [running, setRunning] = useState(false)
  const [showResultDialog, setShowResultDialog] = useState(false)
  const [result, setResult] = useState<SignOffViewpointsResult | null>(null)

  async function loadAreas(modelId: number) {
    setLoadingAreas(true)
    try {
      const list = await native.getZoneVolumes(modelId)
      setAreas(list)
      setSelectedAreas(Object.fromEntries(list.map((a) => [a.guid, true])))
    } finally {
      setLoadingAreas(false)
    }
  }

  useEffect(() => {
    native.getGeometryModels().then((models) => {
      setGeometryModels(models)
      const defaultModel = models.find((m) => m.isLatest) ?? models[0]
      setLoadingModels(false)
      if (defaultModel) {
        setGeometryModelId(defaultModel.id)
        loadAreas(defaultModel.id)
      }
    })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const filteredAreas = useMemo(() => {
    const q = query.trim().toLowerCase()
    if (!q) return areas
    return areas.filter(
      (a) =>
        a.displayLabel.toLowerCase().includes(q) ||
        a.groupName.toLowerCase().includes(q) ||
        a.mark.toLowerCase().includes(q) ||
        a.comments.toLowerCase().includes(q)
    )
  }, [areas, query])

  const selectedGuids = areas.filter((a) => selectedAreas[a.guid]).map((a) => a.guid)
  const allSelected = areas.length > 0 && areas.every((a) => selectedAreas[a.guid])

  function toggleAll(checked: boolean) {
    setSelectedAreas(Object.fromEntries(areas.map((a) => [a.guid, checked])))
  }

  function invertSelection() {
    setSelectedAreas((prev) => {
      const next = { ...prev }
      for (const a of areas) next[a.guid] = !prev[a.guid]
      return next
    })
  }

  const canRun = geometryModelId !== null && selectedGuids.length > 0 && !running

  async function runCreate() {
    if (geometryModelId === null) return
    setRunning(true)
    try {
      const createResult = await native.createSignOffViewpoints(geometryModelId, selectedGuids)
      setResult(createResult)
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
            <CardTitle>Sign Off Area</CardTitle>
            <CardDescription>Create a saved viewport for each sign off area you select</CardDescription>
          </div>
        </div>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        <div className="rounded-md border p-3">
          <span className="mb-2 block text-xs font-medium tracking-wide text-muted-foreground uppercase">
            Model
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
              disabled={geometryModelId === null || loadingAreas}
              onClick={() => geometryModelId !== null && loadAreas(geometryModelId)}
            >
              {loadingAreas ? "Loading..." : "Load Areas"}
            </Button>
          </div>
        </div>

        {areas.length > 0 && (
          <>
            <div className="relative">
              <Search className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" />
              <Input
                placeholder="Filter areas by name or mark..."
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                className="pl-8"
              />
            </div>

            <span className="text-sm text-muted-foreground">
              {selectedGuids.length} / {areas.length} areas selected
              {query && ` (${filteredAreas.length} shown)`}
            </span>

            <div className="grid grid-cols-3 gap-2">
              <Button variant="outline" size="sm" onClick={() => toggleAll(true)} disabled={allSelected}>
                Select All
              </Button>
              <Button
                variant="outline"
                size="sm"
                onClick={() => toggleAll(false)}
                disabled={selectedGuids.length === 0}
              >
                Deselect All
              </Button>
              <Button variant="outline" size="sm" onClick={invertSelection}>
                <RotateCcw />
                Invert
              </Button>
            </div>

            <ScrollArea className="h-64 rounded-md border">
              <div className="flex flex-col gap-1 p-2">
                {filteredAreas.length === 0 && (
                  <p className="p-2 text-sm text-muted-foreground">No areas match "{query}".</p>
                )}
                {filteredAreas.map((area) => {
                  const isChecked = selectedAreas[area.guid] ?? false
                  return (
                    <div
                      key={area.guid}
                      className={`flex items-center gap-2 rounded-md px-2 py-1.5 transition-colors ${
                        isChecked ? "bg-accent/60" : "hover:bg-accent/30"
                      }`}
                    >
                      <Checkbox
                        id={area.guid}
                        checked={isChecked}
                        onCheckedChange={(checked) =>
                          setSelectedAreas((prev) => ({ ...prev, [area.guid]: checked === true }))
                        }
                      />
                      <label htmlFor={area.guid} className="flex-1 truncate text-sm font-medium">
                        {area.comments || area.displayLabel}
                      </label>
                      <Badge variant="secondary" className="shrink-0">
                        {area.groupName}
                      </Badge>
                    </div>
                  )
                })}
              </div>
            </ScrollArea>
          </>
        )}

        {!loadingAreas && areas.length === 0 && geometryModelId !== null && (
          <p className="text-sm text-muted-foreground">
            No sign off area candidates found for this model. Try "Load Areas" again or pick a different model.
          </p>
        )}

        <Button className="w-full" onClick={runCreate} disabled={!canRun}>
          <Video />
          {running ? "Creating..." : `Create Viewports (${selectedGuids.length} areas)`}
        </Button>
      </CardContent>

      <AlertDialog open={showResultDialog} onOpenChange={setShowResultDialog}>
        <AlertDialogContent className="sm:max-w-md">
          <AlertDialogHeader>
            <AlertDialogTitle>Sign Off Area</AlertDialogTitle>
            <AlertDialogDescription>
              {result?.errorMessage
                ? result.errorMessage
                : `Created ${result?.createdCount ?? 0} viewport${(result?.createdCount ?? 0) === 1 ? "" : "s"} in "Sign Off Areas".`}
            </AlertDialogDescription>
          </AlertDialogHeader>

          {result && !result.errorMessage && result.viewpointNames.length > 0 && (
            <div className="flex flex-col gap-1.5">
              <span className="text-xs font-medium tracking-wide text-muted-foreground uppercase">
                Viewports created ({result.viewpointNames.length})
              </span>
              <div className="flex max-h-40 flex-wrap gap-1.5 overflow-y-auto rounded-md border p-2">
                {result.viewpointNames.map((name) => (
                  <Badge key={name} variant="secondary">
                    {name}
                  </Badge>
                ))}
              </div>
            </div>
          )}

          {result && result.skippedCount > 0 && (
            <div className="flex items-center gap-1.5 text-xs text-muted-foreground">
              <CircleAlert className="size-3.5 shrink-0" />
              {result.skippedCount} area{result.skippedCount === 1 ? "" : "s"} had no geometry to frame and were skipped.
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
