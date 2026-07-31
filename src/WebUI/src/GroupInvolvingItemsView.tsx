import { useEffect, useState } from "react"
import { CircleAlert, Users } from "lucide-react"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from "@/components/ui/card"
import { Checkbox } from "@/components/ui/checkbox"
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
  type CoincidentGroupingResult,
} from "@/lib/native"

function selectionKey(testIndex: number, groupChildIndex: number): string {
  return `${testIndex}:${groupChildIndex}`
}

export function GroupInvolvingItemsView() {
  const [tests, setTests] = useState<ClashGroupTestNode[]>([])
  const [loading, setLoading] = useState(true)
  const [selected, setSelected] = useState<Record<string, boolean>>({})

  const [selectedStatuses, setSelectedStatuses] = useState<Record<ClashResultStatus, boolean>>(
    Object.fromEntries(CLASH_RESULT_STATUSES.map((s) => [s, true])) as Record<ClashResultStatus, boolean>
  )
  const [keepSourceGroups, setKeepSourceGroups] = useState(false)

  const [running, setRunning] = useState(false)
  const [showResultDialog, setShowResultDialog] = useState(false)
  const [result, setResult] = useState<CoincidentGroupingResult | null>(null)

  function load() {
    setLoading(true)
    return native.getClashGroupTree().then((list) => {
      setTests(list)
      const initial: Record<string, boolean> = {}
      for (const test of list) {
        for (const group of test.groups) {
          initial[selectionKey(test.testIndex, group.groupChildIndex)] = true
        }
      }
      setSelected(initial)
      setLoading(false)
    })
  }

  useEffect(() => {
    load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const allKeys = tests.flatMap((test) => test.groups.map((group) => selectionKey(test.testIndex, group.groupChildIndex)))
  const selectedCount = allKeys.filter((key) => selected[key]).length

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

  const canRun = selectedCount > 0 && !running

  async function runGrouping() {
    setRunning(true)
    try {
      const selections = tests.flatMap((test) =>
        test.groups
          .filter((group) => selected[selectionKey(test.testIndex, group.groupChildIndex)])
          .map((group) => ({ testIndex: test.testIndex, groupChildIndex: group.groupChildIndex }))
      )
      const statuses = CLASH_RESULT_STATUSES.filter((s) => selectedStatuses[s])
      const groupResult = await native.groupCoincidentElements(selections, statuses, !keepSourceGroups)
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
            <CardTitle>Group All Involving Items by Group</CardTitle>
            <CardDescription>
              Regroup results from existing clash groups by the model item they share in common
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

            <ScrollArea className="h-56 rounded-md border">
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

            <label className="flex items-center gap-2 text-sm">
              <Checkbox checked={keepSourceGroups} onCheckedChange={(checked) => setKeepSourceGroups(checked === true)} />
              Keep source groups after regrouping
            </label>

            <Button className="w-full" onClick={runGrouping} disabled={!canRun}>
              <Users />
              {running ? "Grouping..." : `Run Grouping (${selectedCount} groups)`}
            </Button>
          </>
        )}
      </CardContent>

      <AlertDialog open={showResultDialog} onOpenChange={setShowResultDialog}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Group All Involving Items by Group</AlertDialogTitle>
            <AlertDialogDescription>
              {result?.errorMessage ??
                `${result?.groupsCreated ?? 0} group${(result?.groupsCreated ?? 0) === 1 ? "" : "s"} created, ${result?.clashesGrouped ?? 0} clash${(result?.clashesGrouped ?? 0) === 1 ? "" : "es"} grouped.`}
            </AlertDialogDescription>
          </AlertDialogHeader>

          {result && !result.errorMessage && result.clashesUngrouped > 0 && (
            <div className="flex items-center gap-1.5 text-xs text-muted-foreground">
              <CircleAlert className="size-3.5 shrink-0" />
              {result.clashesUngrouped} clash{result.clashesUngrouped === 1 ? "" : "es"} could not be grouped (no
              repeated item).
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
