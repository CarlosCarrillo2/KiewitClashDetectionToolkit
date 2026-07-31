import { useEffect, useMemo, useState } from "react"
import { GitCompare, RotateCcw } from "lucide-react"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from "@/components/ui/card"
import { Checkbox } from "@/components/ui/checkbox"
import { Separator } from "@/components/ui/separator"
import { ScrollArea } from "@/components/ui/scroll-area"
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
  Drawer,
  DrawerClose,
  DrawerContent,
  DrawerDescription,
  DrawerFooter,
  DrawerHeader,
  DrawerTitle,
} from "@/components/ui/drawer"
import { ClashMatrixGrid } from "@/ClashMatrixGrid"
import { native, type ClashTestType, type ClashLink } from "@/lib/native"

// Models are named "<project> - <discipline>.<ext> - <SEGMENT-CODE>" or
// "<code>....ext - CODE 3D View". Pull the trailing discipline code out of the
// last " - " segment so each row can show it as a badge.
function getCategory(name: string): string | null {
  const lastSegment = name.split(" - ").pop() ?? name
  const trimmed = lastSegment.replace(/\.(rvt|nwc|nwd|ifc)$/i, "").trim()
  if (!trimmed) return null
  if (trimmed.includes("-")) {
    const parts = trimmed.split("-")
    return parts[parts.length - 1].trim().toUpperCase() || null
  }
  return trimmed.split(/\s+/)[0]?.toUpperCase() || null
}

// Mirrors ClashMatrixGenerator.GenerateAndRun on the C# side: a single selected
// model is clashed against every other model in the document, while 2+ selected
// models are clashed pairwise only among themselves.
function computeMatrixPairs(models: string[], selectedNames: string[]): [string, string][] {
  if (selectedNames.length === 1) {
    return models.filter((m) => m !== selectedNames[0]).map((other) => [selectedNames[0], other])
  }

  const pairs: [string, string][] = []
  for (let i = 0; i < selectedNames.length; i++) {
    for (let j = i + 1; j < selectedNames.length; j++) {
      pairs.push([selectedNames[i], selectedNames[j]])
    }
  }
  return pairs
}

const TEST_TYPES: { value: ClashTestType; label: string }[] = [
  { value: "Hard", label: "Hard" },
  { value: "HardConservative", label: "Hard (Conservative)" },
  { value: "Clearance", label: "Clearance" },
  { value: "Duplicate", label: "Duplicates" },
]

export function ClashTestView() {
  const [models, setModels] = useState<string[]>([])
  const [selected, setSelected] = useState<Record<string, boolean>>({})
  const [loading, setLoading] = useState(true)
  const [running, setRunning] = useState(false)
  const [showResultDialog, setShowResultDialog] = useState(false)
  const [previewOpen, setPreviewOpen] = useState(false)
  const [unitsLabel, setUnitsLabel] = useState("m")
  const [testType, setTestType] = useState<ClashTestType>("Hard")
  const [tolerance, setTolerance] = useState("0.01")
  const [mergeComposites, setMergeComposites] = useState(true)
  const [link, setLink] = useState<ClashLink>("None")
  const [step, setStep] = useState("0.1")
  const [combineSingleVsAll, setCombineSingleVsAll] = useState(false)

  useEffect(() => {
    native.getModels().then(({ models: names, units }) => {
      setModels(names)
      setSelected(Object.fromEntries(names.map((n) => [n, true])))
      setUnitsLabel(units)
      setLoading(false)
    })
  }, [])

  const selectedNames = models.filter((m) => selected[m])
  const allSelected = models.length > 0 && selectedNames.length === models.length
  const categories = useMemo(
    () => Object.fromEntries(models.map((m) => [m, getCategory(m)])),
    [models]
  )
  const matrixPairs = useMemo(
    () => computeMatrixPairs(models, selectedNames),
    [models, selectedNames]
  )
  const isSingleVsAll = selectedNames.length === 1
  const willCombine = isSingleVsAll && combineSingleVsAll
  const testsToRun = willCombine ? Math.min(1, matrixPairs.length) : matrixPairs.length

  useEffect(() => {
    if (!isSingleVsAll && combineSingleVsAll) {
      setCombineSingleVsAll(false)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isSingleVsAll])

  function toggleAll(checked: boolean) {
    setSelected(Object.fromEntries(models.map((m) => [m, checked])))
  }

  function toggleOne(name: string, checked: boolean) {
    setSelected((prev) => ({ ...prev, [name]: checked }))
  }

  function invertSelection() {
    setSelected(Object.fromEntries(models.map((m) => [m, !selected[m]])))
  }

  async function confirmRun() {
    setRunning(true)
    try {
      await native.runClash(
        selectedNames,
        {
          testType,
          tolerance: parseFloat(tolerance) || 0,
          mergeComposites,
          link,
          step: parseFloat(step) || 0,
        },
        willCombine
      )
      setPreviewOpen(false)
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
            <CardTitle>Clash Test Generation</CardTitle>
            <CardDescription>Select the models, generate the matrix and run the clash test</CardDescription>
          </div>
        </div>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        {loading && <p className="text-sm text-muted-foreground">Loading models...</p>}

        {!loading && models.length === 0 && (
          <p className="text-sm text-muted-foreground">No models found in the current document.</p>
        )}

        {!loading && models.length > 0 && (
          <>
            <span className="text-sm text-muted-foreground">
              {selectedNames.length} / {models.length} models selected
            </span>

            <div className="grid grid-cols-3 gap-2">
              <Button variant="outline" size="sm" onClick={() => toggleAll(true)} disabled={allSelected}>
                Select All
              </Button>
              <Button
                variant="outline"
                size="sm"
                onClick={() => toggleAll(false)}
                disabled={selectedNames.length === 0}
              >
                Deselect All
              </Button>
              <Button variant="outline" size="sm" onClick={invertSelection}>
                <RotateCcw />
                Invert
              </Button>
            </div>

            <label
              className={`flex items-center gap-2 text-sm ${isSingleVsAll ? "" : "text-muted-foreground"}`}
            >
              <Checkbox
                checked={combineSingleVsAll}
                disabled={!isSingleVsAll}
                onCheckedChange={(checked) => setCombineSingleVsAll(checked === true)}
              />
              Model vs All (single clash test)
            </label>
            {!isSingleVsAll && (
              <p className="text-xs text-muted-foreground">
                Select exactly 1 model to combine it against every other model as one clash test.
              </p>
            )}

            <Separator />

            <ScrollArea className="h-64 rounded-md border">
              <div className="flex flex-col gap-1 p-2">
                {models.map((name) => {
                  const isChecked = selected[name] ?? false
                  const category = categories[name]
                  return (
                    <div
                      key={name}
                      className={`flex items-center gap-2 rounded-md px-2 py-1.5 transition-colors ${
                        isChecked ? "bg-accent/60" : "hover:bg-accent/30"
                      }`}
                    >
                      <Checkbox
                        id={name}
                        checked={isChecked}
                        onCheckedChange={(checked) => toggleOne(name, checked === true)}
                      />
                      <label htmlFor={name} className="flex-1 truncate text-sm">
                        {name}
                      </label>
                      {category && (
                        <span className="shrink-0 text-xs font-medium text-muted-foreground">
                          {category}
                        </span>
                      )}
                    </div>
                  )
                })}
              </div>
            </ScrollArea>

            <div className="rounded-md border p-3">
              <span className="mb-2 block text-xs font-medium tracking-wide text-muted-foreground uppercase">
                Settings
              </span>
              <div className="grid grid-cols-2 gap-x-3 gap-y-2">
                <div className="flex flex-col gap-1">
                  <label htmlFor="test-type" className="text-xs text-muted-foreground">
                    Type
                  </label>
                  <select
                    id="test-type"
                    value={testType}
                    onChange={(e) => setTestType(e.target.value as ClashTestType)}
                    className="h-8 rounded-lg border border-input bg-transparent px-2 text-sm outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50"
                  >
                    {TEST_TYPES.map((t) => (
                      <option key={t.value} value={t.value}>
                        {t.label}
                      </option>
                    ))}
                  </select>
                </div>

                <div className="flex flex-col gap-1">
                  <label htmlFor="tolerance" className="text-xs text-muted-foreground">
                    Tolerance ({unitsLabel})
                  </label>
                  <input
                    id="tolerance"
                    type="number"
                    step="0.001"
                    min="0"
                    value={tolerance}
                    onChange={(e) => setTolerance(e.target.value)}
                    className="h-8 rounded-lg border border-input bg-transparent px-2 text-sm outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50"
                  />
                </div>

                <div className="flex flex-col gap-1">
                  <label htmlFor="link" className="text-xs text-muted-foreground">
                    Link
                  </label>
                  <select
                    id="link"
                    value={link}
                    onChange={(e) => setLink(e.target.value as ClashLink)}
                    className="h-8 rounded-lg border border-input bg-transparent px-2 text-sm outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50"
                  >
                    <option value="None">None</option>
                    <option value="Timeliner">Timeliner</option>
                  </select>
                </div>

                <div className="flex flex-col gap-1">
                  <label htmlFor="step" className="text-xs text-muted-foreground">
                    Step (sec)
                  </label>
                  <input
                    id="step"
                    type="number"
                    step="0.01"
                    min="0"
                    value={step}
                    onChange={(e) => setStep(e.target.value)}
                    disabled={link === "None"}
                    className="h-8 rounded-lg border border-input bg-transparent px-2 text-sm outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 disabled:bg-input/30 disabled:text-muted-foreground"
                  />
                </div>
              </div>

              <label className="mt-3 flex items-center gap-2 text-sm">
                <Checkbox
                  checked={mergeComposites}
                  onCheckedChange={(checked) => setMergeComposites(checked === true)}
                />
                Composite Object Clashing
              </label>
            </div>

            <Button
              className="w-full"
              onClick={() => setPreviewOpen(true)}
              disabled={selectedNames.length < 1 || running}
            >
              <GitCompare />
              Run clash test ({selectedNames.length} selected)
            </Button>
          </>
        )}
      </CardContent>

      <Drawer open={previewOpen} onOpenChange={setPreviewOpen}>
        <DrawerContent>
          <DrawerHeader>
            <DrawerTitle>Clash Matrix Preview</DrawerTitle>
            <DrawerDescription>
              {matrixPairs.length === 0
                ? "No other models available to test against."
                : willCombine
                  ? `1 clash test will be generated (${selectedNames[0]} vs the ${matrixPairs.length} other model${matrixPairs.length === 1 ? "" : "s"} combined).`
                  : `${matrixPairs.length} clash test${matrixPairs.length === 1 ? "" : "s"} will be generated.`}
            </DrawerDescription>
          </DrawerHeader>

          <div className="flex-1 overflow-auto px-4 py-2">
            <ClashMatrixGrid pairs={matrixPairs} />
          </div>

          <DrawerFooter>
            <Button onClick={confirmRun} disabled={matrixPairs.length === 0 || running}>
              <GitCompare />
              {running ? "Running..." : `Run ${testsToRun} clash test${testsToRun === 1 ? "" : "s"}`}
            </Button>
            <DrawerClose render={<Button variant="outline">Cancel</Button>} />
          </DrawerFooter>
        </DrawerContent>
      </Drawer>

      <AlertDialog open={showResultDialog} onOpenChange={setShowResultDialog}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Clash Test Generation</AlertDialogTitle>
            <AlertDialogDescription>Clash tests generated successfully.</AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogAction onClick={() => setShowResultDialog(false)}>Accept</AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </Card>
  )
}
