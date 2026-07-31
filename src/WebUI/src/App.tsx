import { useEffect, useState } from "react"
import { ClashTestView } from "@/ClashTestView"
import { DeleteClashTestsView } from "@/DeleteClashTestsView"
import { GroupZonesView } from "@/GroupZonesView"
import { GroupInvolvingItemsView } from "@/GroupInvolvingItemsView"
import { GroupByModelView } from "@/GroupByModelView"
import { SignOffAreaView } from "@/SignOffAreaView"
import { ClashesToViewpointsView } from "@/ClashesToViewpointsView"
import { onSetView } from "@/lib/native"

function App() {
  const [view, setView] = useState("generate")

  useEffect(() => onSetView(setView), [])

  return (
    <div className="flex min-h-svh flex-col gap-4 p-4">
      {view === "delete" ? (
        <DeleteClashTestsView />
      ) : view === "zones" ? (
        <GroupZonesView />
      ) : view === "involvingItems" ? (
        <GroupInvolvingItemsView />
      ) : view === "modelPriority" ? (
        <GroupByModelView />
      ) : view === "signOff" ? (
        <SignOffAreaView />
      ) : view === "clashesToViewpoints" ? (
        <ClashesToViewpointsView />
      ) : (
        <ClashTestView />
      )}
    </div>
  )
}

export default App
