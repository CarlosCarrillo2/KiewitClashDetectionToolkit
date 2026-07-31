import { useState } from "react"

// Classic BIM clash-matrix layout: models on both axes, only the triangle below
// the diagonal is drawn (A vs B and B vs A are the same test), filled cells mark
// the pairs that will actually be generated.
export function ClashMatrixGrid({ pairs }: { pairs: [string, string][] }) {
  const [hovered, setHovered] = useState<{ row: string; col: string } | null>(null)
  const involved = Array.from(new Set(pairs.flat()))
  const activePairs = new Set(pairs.map(([a, b]) => `${a}|${b}`))

  function isActive(a: string, b: string) {
    return activePairs.has(`${a}|${b}`) || activePairs.has(`${b}|${a}`)
  }

  if (involved.length === 0) {
    return null
  }

  return (
    <div className="flex flex-col items-center gap-3">
      <div className="w-full overflow-auto rounded-md border">
        <div
          className="mx-auto grid w-max"
          style={{ gridTemplateColumns: `minmax(120px, auto) repeat(${involved.length}, minmax(1.5rem, 2.25rem))` }}
        >
          <div className="sticky left-0 bg-card" />
          {involved.map((name, colIdx) => (
            <div key={colIdx} className="flex items-end justify-center pb-1">
              {/* writing-mode lets the browser compute the label's height itself
                  (no manual rotation/height-estimation math, which was clipping some
                  labels) - rotate 180deg on top of vertical-rl to read bottom-to-top. */}
              <span
                className={`whitespace-nowrap text-xs transition-colors ${
                  hovered?.col === name ? "font-semibold text-foreground" : "font-medium text-muted-foreground"
                }`}
                style={{ writingMode: "vertical-rl", transform: "rotate(180deg)" }}
              >
                {name}
              </span>
            </div>
          ))}

          {involved.map((rowName, rowIdx) => (
            <div key={rowIdx} className="contents">
              <div
                className={`sticky left-0 flex items-center border-t px-2 py-1 text-xs whitespace-nowrap transition-colors ${
                  hovered?.row === rowName
                    ? "bg-accent font-semibold text-foreground"
                    : "bg-card font-medium text-muted-foreground"
                }`}
              >
                {rowName}
              </div>
              {involved.map((colName, colIdx) => {
                if (colIdx >= rowIdx) {
                  return <div key={colIdx} className="border-t" />
                }
                const active = isActive(rowName, colName)
                const isHovered = hovered?.row === rowName && hovered?.col === colName
                return (
                  <div
                    key={colIdx}
                    className="flex items-center justify-center border-t p-0.5"
                    onMouseEnter={() => setHovered({ row: rowName, col: colName })}
                    onMouseLeave={() => setHovered(null)}
                  >
                    <div
                      title={`${rowName} vs ${colName}`}
                      className={`aspect-square w-full rounded-sm transition-all ${
                        active ? "bg-primary" : "bg-muted"
                      } ${isHovered ? "ring-2 ring-foreground/60 ring-offset-1" : ""}`}
                    />
                  </div>
                )
              })}
            </div>
          ))}
        </div>
      </div>

      <div className="flex items-center gap-4 text-xs text-muted-foreground">
        <span className="flex items-center gap-1.5">
          <span className="size-3 rounded-sm bg-primary" />
          Will be tested
        </span>
        <span className="flex items-center gap-1.5">
          <span className="size-3 rounded-sm bg-muted" />
          Not tested
        </span>
      </div>
    </div>
  )
}
