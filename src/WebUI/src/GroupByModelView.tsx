import { useEffect, useMemo, useState } from "react"
import { ArrowDown, ArrowUp, CircleAlert, Layers, RefreshCw, Eye } from "lucide-react"
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
  type ClashGroupTestNode,
  type ClashResultStatus,
  type ModelPriorityGroupingResult,
} from "@/lib/native"

function selectionKey(testIndex: number, groupChildIndex: number): string {
  return `${testIndex}:${groupChildIndex}`
}

export function GroupByModelView() {
  const [tests, setTests] = useState<ClashGroupTestNode[]>([])
  const [loading, setLoading] = useState(true)
  const [selected, setSelected] = useState<Record<string, boolean>>({})

  const [allModels, setAllModels] = useState<string[]>([])
  const [loadingModels, setLoadingModels] = useState(false)
  const [modelsCalculated, setModelsCalculated] = useState(false)
  const [modelsStale, setModelsStale] = useState(false)
  const [priorityOrder, setPriorityOrder] = useState<string[]>([])
  const [locatingModel, setLocatingModel] = useState<string | null>(null)
  const [modelSearch, setModelSearch] = useState("")

  const [selectedStatuses, setSelectedStatuses] = useState<Record<ClashResultStatus, boolean>>(
    Object.fromEntries(CLASH_RESULT_STATUSES.map((s) => [s, true])) as Record<ClashResultStatus, boolean>
  )
  const [keepSourceGroups, setKeepSourceGroups] = useState(false)
  const [groupRemaining, setGroupRemaining] = useState(true)
  const [remainingGroupName, setRemainingGroupName] = useState("Other")

  const [running, setRunning] = useState(false)
  const [showResultDialog, setShowResultDialog] = useState(false)
  const [result, setResult] = useState<ModelPriorityGroupingResult | null>(null)

  function load() {
    setLoading(true)
    return native.getModelPriorityClashTree().then((tree) => {
      setTests(tree)
      const initial: Record<string, boolean> = {}
      for (const test of tree) {
        for (const group of test.groups) {
          initial[selectionKey(test.testIndex, group.groupChildIndex)] = true
        }
      }
      setSelected(initial)
      setAllModels([])
      setPriorityOrder([])
      setModelsCalculated(false)
      setModelsStale(false)
      setLoading(false)
    })
  }

  useEffect(() => {
    load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const allKeys = tests.flatMap((test) => test.groups.map((group) => selectionKey(test.testIndex, group.groupChildIndex)))
  const selectedCount = allKeys.filter((key) => selected[key]).length

  const currentSelections = useMemo(
    () =>
      tests.flatMap((test) =>
        test.groups
          .filter((group) => selected[selectionKey(test.testIndex, group.groupChildIndex)])
          .map((group) => ({ testIndex: test.testIndex, groupChildIndex: group.groupChildIndex }))
      ),
    [tests, selected]
  )

  // "Models to Group" only scans the current group selection + status filter when the user
  // explicitly asks it to (calculateModels, wired to a button) - scanning on every checkbox
  // click was too slow to feel responsive. Once calculated, further selection/status changes
  // just flag the result as stale instead of silently re-scanning.
  useEffect(() => {
    if (modelsCalculated) {
      setModelsStale(true)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [currentSelections, selectedStatuses])

  async function calculateModels() {
    if (currentSelections.length === 0) {
      setAllModels([])
      setPriorityOrder([])
      setModelsCalculated(true)
      setModelsStale(false)
      return
    }

    setLoadingModels(true)
    try {
      const statuses = CLASH_RESULT_STATUSES.filter((s) => selectedStatuses[s])
      const models = await native.getModelsInvolvedInGroups(currentSelections, statuses)
      setAllModels(models)
      setPriorityOrder((prev) => prev.filter((m) => models.includes(m)))
      setModelsCalculated(true)
      setModelsStale(false)
    } finally {
      setLoadingModels(false)
    }
  }

  function toggleAll(checked: boolean) {
    setSelected(Object.fromEntries(allKeys.map((key) => [key, checked])))
  }

  function toggleTest(test: ClashGroupTestNode, checked: boolean) {
    setSelected((prev) => {
      const next = { ...prev }
      for (const group of test.groups) next[selectionKey(test.testIndex, group.groupChildIndex)] = checked
      return next
    })
  }

  function toggleGroup(testIndex: number, groupChildIndex: number, checked: boolean) {
    setSelected((prev) => ({ ...prev, [selectionKey(testIndex, groupChildIndex)]: checked }))
  }

  function toggleStatus(status: ClashResultStatus, checked: boolean) {
    setSelectedStatuses((prev) => ({ ...prev, [status]: checked }))
  }

  async function locateModel(model: string) {
    setLocatingModel(model)
    try {
      await native.selectModelRoot(model)
    } finally {
      setLocatingModel(null)
    }
  }

  function toggleModelIncluded(model: string, included: boolean) {
    setPriorityOrder((prev) => (included ? [...prev, model] : prev.filter((m) => m !== model)))
  }

  function moveModel(index: number, direction: -1 | 1) {
    setPriorityOrder((prev) => {
      const next = [...prev]
      const target = index + direction
      if (target < 0 || target >= next.length) return prev
      ;[next[index], next[target]] = [next[target], next[index]]
      return next
    })
  }

  const filteredModels = useMemo(
    () => allModels.filter((model) => model.toLowerCase().includes(modelSearch.trim().toLowerCase())),
    [allModels, modelSearch]
  )

  const canRun = selectedCount > 0 && priorityOrder.length > 0 && !running

  async function runGrouping() {
    setRunning(true)
    try {
      const statuses = CLASH_RESULT_STATUSES.filter((s) => selectedStatuses[s])
      const groupResult = await native.groupByModelPriority(
        currentSelections,
        statuses,
        priorityOrder,
        !keepSourceGroups,
        groupRemaining,
        groupRemaining ? remainingGroupName.trim() || "Other" : null
      )
      setResult(groupResult)
      setShowResultDialog(true)
      if (!groupResult.errorMessage) {
        await load()
      }
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
            <CardTitle>Group by Model Priority</CardTitle>
            <CardDescription>
              Regroup results from existing clash groups by source model - highest priority model first
            </CardDescription>
          </div>
        </div>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        {loading && <p className="text-sm text-muted-foreground">Loading clash groups...</p>}

        {!loading && tests.length === 0 && (
          <p className="text-sm text-muted-foreground">No clash groups found in the current document.</p>
        )}

        {!loading && tests.length > 0 && (
          <>
            <div className="flex items-center justify-between">
              <span className="text-sm text-muted-foreground">
                {selectedCount} / {allKeys.length} groups selected
              </span>
              <div className="flex gap-1.5">
                <Button variant="outline" size="sm" onClick={() => toggleAll(true)}>
                  Select All
                </Button>
                <Button variant="outline" size="sm" onClick={() => toggleAll(false)}>
                  Deselect All
                </Button>
              </div>
            </div>

            <ScrollArea className="h-40 rounded-md border">
              <div className="flex flex-col gap-2 p-2">
                {tests.map((test) => {
                  const testKeys = test.groups.map((g) => selectionKey(test.testIndex, g.groupChildIndex))
                  const testChecked = testKeys.every((key) => selected[key])
                  return (
                    <div key={test.testIndex} className="flex flex-col gap-1">
                      <label className="flex items-center gap-2 rounded-md px-2 py-1 text-sm font-medium hover:bg-accent/30">
                        <Checkbox checked={testChecked} onCheckedChange={(checked) => toggleTest(test, checked === true)} />
                        <span className="truncate">{test.testName}</span>
                      </label>
                      <div className="ml-5 flex flex-col gap-1">
                        {test.groups.map((group) => {
                          const key = selectionKey(test.testIndex, group.groupChildIndex)
                          const isChecked = selected[key] ?? false
                          return (
                            <div
                              key={key}
                              className={`flex items-center gap-2 rounded-md px-2 py-1 transition-colors ${
                                isChecked ? "bg-accent/60" : "hover:bg-accent/30"
                              }`}
                            >
                              <Checkbox
                                id={key}
                                checked={isChecked}
                                onCheckedChange={(checked) =>
                                  toggleGroup(test.testIndex, group.groupChildIndex, checked === true)
                                }
                              />
                              <label htmlFor={key} className="flex-1 truncate text-sm">
                                {group.groupName}
                              </label>
                              <Badge variant="secondary" className="shrink-0">
                                {group.resultCount} result{group.resultCount === 1 ? "" : "s"}
                              </Badge>
                            </div>
                          )
                        })}
                      </div>
                    </div>
                  )
                })}
              </div>
            </ScrollArea>

            <Separator />

            <div className="rounded-md border p-3">
              <div className="mb-2 flex items-center justify-between gap-2">
                <span className="text-xs font-medium tracking-wide text-muted-foreground uppercase">
                  Models to Group
                </span>
                <Button
                  variant="outline"
                  size="sm"
                  onClick={calculateModels}
                  disabled={selectedCount === 0 || loadingModels}
                >
                  <RefreshCw />
                  {loadingModels ? "Calculating..." : modelsCalculated ? "Recalculate" : "Calculate Models"}
                </Button>
              </div>
              <p className="mb-2 text-xs text-muted-foreground">
                Only models actually involved in the groups selected above will be listed - select your groups, then
                calculate.
              </p>

              {modelsStale && !loadingModels && (
                <div className="mb-2 flex items-center gap-1.5 text-xs text-amber-600 dark:text-amber-500">
                  <CircleAlert className="size-3.5 shrink-0" />
                  Selection changed - recalculate to refresh this list.
                </div>
              )}

              {loadingModels && <p className="text-sm text-muted-foreground">Scanning selected groups...</p>}

              {!loadingModels && !modelsCalculated && (
                <p className="text-sm text-muted-foreground">
                  Select groups above, then click "Calculate Models" to see which models are involved.
                </p>
              )}

              {!loadingModels && modelsCalculated && allModels.length === 0 && (
                <p className="text-sm text-muted-foreground">No models found for the current selection/status filter.</p>
              )}

              {!loadingModels && allModels.length > 0 && (
                <Input
                  className="mb-2"
                  placeholder="Search models..."
                  value={modelSearch}
                  onChange={(e) => setModelSearch(e.target.value)}
                />
              )}

              {!loadingModels && allModels.length > 0 && filteredModels.length === 0 && (
                <p className="text-sm text-muted-foreground">No models match "{modelSearch}".</p>
              )}

              {!loadingModels && filteredModels.length > 0 && (
                <div className="grid grid-cols-2 gap-1.5">
                  {filteredModels.map((model) => (
                    <div key={model} className="flex items-center gap-1">
                      <label className="flex flex-1 items-center gap-2 text-sm">
                        <Checkbox
                          checked={priorityOrder.includes(model)}
                          onCheckedChange={(checked) => toggleModelIncluded(model, checked === true)}
                        />
                        <span className="truncate">{model}</span>
                      </label>
                      <Button
                        variant="ghost"
                        size="icon-xs"
                        title="Show this model's geometry in the Navisworks view"
                        onClick={() => locateModel(model)}
                        disabled={locatingModel === model}
                      >
                        <Eye />
                      </Button>
                    </div>
                  ))}
                </div>
              )}
            </div>

            <div className="rounded-md border p-3">
              <span className="mb-2 block text-xs font-medium tracking-wide text-muted-foreground uppercase">
                Priority Order (highest first)
              </span>
              {priorityOrder.length === 0 ? (
                <p className="text-sm text-muted-foreground">Check models above to add them here.</p>
              ) : (
                <div className="flex flex-col gap-1">
                  {priorityOrder.map((model, index) => (
                    <div key={model} className="flex items-center gap-2 rounded-md border px-2 py-1.5">
                      <Badge className="shrink-0">{index + 1}</Badge>
                      <span className="flex-1 truncate text-sm">{model}</span>
                      <Button
                        variant="ghost"
                        size="icon-xs"
                        onClick={() => moveModel(index, -1)}
                        disabled={index === 0}
                      >
                        <ArrowUp />
                      </Button>
                      <Button
                        variant="ghost"
                        size="icon-xs"
                        onClick={() => moveModel(index, 1)}
                        disabled={index === priorityOrder.length - 1}
                      >
                        <ArrowDown />
                      </Button>
                    </div>
                  ))}
                </div>
              )}
            </div>

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

            <div className="flex flex-col gap-2">
              <label className="flex items-center gap-2 text-sm">
                <Checkbox checked={keepSourceGroups} onCheckedChange={(checked) => setKeepSourceGroups(checked === true)} />
                Keep source groups after regrouping
              </label>

              <label className="flex items-center gap-2 text-sm">
                <Checkbox checked={groupRemaining} onCheckedChange={(checked) => setGroupRemaining(checked === true)} />
                Group remaining clashes into a single group
              </label>
              {groupRemaining && (
                <Input
                  placeholder="Remaining group name"
                  value={remainingGroupName}
                  onChange={(e) => setRemainingGroupName(e.target.value)}
                />
              )}
            </div>

            <Button className="w-full" onClick={runGrouping} disabled={!canRun}>
              <Layers />
              {running ? "Grouping..." : `Run Grouping (${priorityOrder.length} models)`}
            </Button>
          </>
        )}
      </CardContent>

      <AlertDialog open={showResultDialog} onOpenChange={setShowResultDialog}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Group by Model Priority</AlertDialogTitle>
            <AlertDialogDescription>
              {result?.errorMessage ??
                `${result?.groupsCreated ?? 0} group${(result?.groupsCreated ?? 0) === 1 ? "" : "s"} created, ${result?.clashesGrouped ?? 0} clash${(result?.clashesGrouped ?? 0) === 1 ? "" : "es"} grouped.`}
            </AlertDialogDescription>
          </AlertDialogHeader>

          {result && !result.errorMessage && result.clashesUngrouped > 0 && (
            <div className="flex items-center gap-1.5 text-xs text-muted-foreground">
              <CircleAlert className="size-3.5 shrink-0" />
              {result.clashesUngrouped} clash{result.clashesUngrouped === 1 ? "" : "es"} didn't touch any priority
              model and were left ungrouped.
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
