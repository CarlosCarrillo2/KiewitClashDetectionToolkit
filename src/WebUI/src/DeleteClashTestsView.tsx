import { useEffect, useMemo, useState } from "react"
import { Trash2, RotateCcw, Search } from "lucide-react"
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
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog"
import { native, type ClashTestInfo } from "@/lib/native"

export function DeleteClashTestsView() {
  const [tests, setTests] = useState<ClashTestInfo[]>([])
  const [selected, setSelected] = useState<Record<string, boolean>>({})
  const [loading, setLoading] = useState(true)
  const [deleting, setDeleting] = useState(false)
  const [query, setQuery] = useState("")
  const [confirmOpen, setConfirmOpen] = useState(false)
  const [showResultDialog, setShowResultDialog] = useState(false)
  const [resultMessage, setResultMessage] = useState("")

  function load() {
    setLoading(true)
    return native.getClashTests().then((list) => {
      setTests(list)
      setSelected(Object.fromEntries(list.map((t) => [t.guid, false])))
      setLoading(false)
    })
  }

  useEffect(() => {
    load()
  }, [])

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase()
    if (!q) return tests
    return tests.filter((t) => t.displayName.toLowerCase().includes(q))
  }, [tests, query])

  const selectedGuids = filtered.filter((t) => selected[t.guid]).map((t) => t.guid)
  const allSelectedCount = Object.values(selected).filter(Boolean).length
  const allFilteredSelected = filtered.length > 0 && filtered.every((t) => selected[t.guid])
  const emptyTestGuids = tests.filter((t) => t.resultCount === 0).map((t) => t.guid)

  function toggleAll(checked: boolean) {
    setSelected((prev) => {
      const next = { ...prev }
      for (const t of filtered) next[t.guid] = checked
      return next
    })
  }

  function toggleOne(guid: string, checked: boolean) {
    setSelected((prev) => ({ ...prev, [guid]: checked }))
  }

  function invertSelection() {
    setSelected((prev) => {
      const next = { ...prev }
      for (const t of filtered) next[t.guid] = !prev[t.guid]
      return next
    })
  }

  function selectEmptyTests() {
    setSelected(Object.fromEntries(tests.map((t) => [t.guid, t.resultCount === 0])))
  }

  async function confirmDelete() {
    setDeleting(true)
    try {
      const summary = await native.deleteClashTests(selectedGuids)
      setConfirmOpen(false)
      setResultMessage(summary)
      setShowResultDialog(true)
      await load()
    } finally {
      setDeleting(false)
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
            <CardTitle>Delete Clash Tests</CardTitle>
            <CardDescription>Search, select and remove existing clash tests</CardDescription>
          </div>
        </div>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        {loading && <p className="text-sm text-muted-foreground">Loading clash tests...</p>}

        {!loading && tests.length === 0 && (
          <p className="text-sm text-muted-foreground">No clash tests found in the current document.</p>
        )}

        {!loading && tests.length > 0 && (
          <>
            <div className="relative">
              <Search className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" />
              <Input
                placeholder="Filter clash tests..."
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                className="pl-8"
              />
            </div>

            <span className="text-sm text-muted-foreground">
              {allSelectedCount} / {tests.length} clash tests selected
              {query && ` (${filtered.length} shown)`}
            </span>

            <div className="grid grid-cols-2 gap-2">
              <Button variant="outline" size="sm" onClick={() => toggleAll(true)} disabled={allFilteredSelected}>
                Select All
              </Button>
              <Button
                variant="outline"
                size="sm"
                onClick={() => toggleAll(false)}
                disabled={filtered.every((t) => !selected[t.guid])}
              >
                Deselect All
              </Button>
              <Button variant="outline" size="sm" onClick={invertSelection}>
                <RotateCcw />
                Invert
              </Button>
              <Button
                variant="outline"
                size="sm"
                onClick={selectEmptyTests}
                disabled={emptyTestGuids.length === 0}
              >
                Select Empty ({emptyTestGuids.length})
              </Button>
            </div>

            <Separator />

            <ScrollArea className="h-64 rounded-md border">
              <div className="flex flex-col gap-1 p-2">
                {filtered.length === 0 && (
                  <p className="p-2 text-sm text-muted-foreground">No clash tests match "{query}".</p>
                )}
                {filtered.map((test) => {
                  const isChecked = selected[test.guid] ?? false
                  return (
                    <div
                      key={test.guid}
                      className={`flex items-center gap-2 rounded-md px-2 py-1.5 transition-colors ${
                        isChecked ? "bg-accent/60" : "hover:bg-accent/30"
                      }`}
                    >
                      <Checkbox
                        id={test.guid}
                        checked={isChecked}
                        onCheckedChange={(checked) => toggleOne(test.guid, checked === true)}
                      />
                      <label htmlFor={test.guid} className="flex-1 truncate text-sm">
                        {test.displayName}
                      </label>
                      <Badge variant="secondary" className="shrink-0">
                        {test.resultCount} result{test.resultCount === 1 ? "" : "s"}
                      </Badge>
                    </div>
                  )
                })}
              </div>
            </ScrollArea>

            <Button
              variant="destructive"
              className="w-full"
              onClick={() => setConfirmOpen(true)}
              disabled={selectedGuids.length < 1 || deleting}
            >
              <Trash2 />
              Delete clash test{selectedGuids.length === 1 ? "" : "s"} ({selectedGuids.length} selected)
            </Button>
          </>
        )}
      </CardContent>

      <AlertDialog open={confirmOpen} onOpenChange={setConfirmOpen}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Delete {selectedGuids.length} clash test{selectedGuids.length === 1 ? "" : "s"}?</AlertDialogTitle>
            <AlertDialogDescription>
              This permanently removes the selected clash test{selectedGuids.length === 1 ? "" : "s"} and their results
              from Clash Detective. This cannot be undone from this panel.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction variant="destructive" onClick={confirmDelete} disabled={deleting}>
              <Trash2 />
              {deleting ? "Deleting..." : "Delete"}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>

      <AlertDialog open={showResultDialog} onOpenChange={setShowResultDialog}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Delete Clash Tests</AlertDialogTitle>
            <AlertDialogDescription>{resultMessage}</AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogAction onClick={() => setShowResultDialog(false)}>Accept</AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </Card>
  )
}
