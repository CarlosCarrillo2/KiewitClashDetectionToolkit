# Navisworks Dock Panel (WebView2 + React/shadcn)

A Navisworks Manage 2026 add-in that adds a dockable pane hosting a React + shadcn/ui web
app via WebView2, opened from a custom ribbon tab. The C# side only ever talks to the real
Navisworks API and Clash Detective data; the UI is a normal React app that happens to render
inside Navisworks instead of a browser tab.

## Features

Each feature is its own ribbon button, opening the same dock pane pointed at a different view:

- **Clash Test Generation** - pick models, generate the pairwise clash matrix (or "one model
  vs. all others" as a single combined test), configure test settings (type, tolerance,
  composite object clashing, Timeliner link), and run it.
- **Delete Clash Tests** - browse and bulk-delete existing clash tests, including ones nested
  inside group folders.
- **Group by Zone** - match existing clash results against zone/volume geometry (detected by
  keyword + Mark/Comments properties, e.g. Revit rooms or masses) using real mesh containment
  (ray-casting against the zone's actual geometry, not just its bounding box), and regroup
  results into per-zone `ClashResultGroup`s in Clash Detective.
- **Group All Involving Items by Group** - within a set of existing clash groups, find model
  items that recur across 2+ results ("coincident" items) and split those results out into
  their own group per item.
- **Group by Model Priority** - regroup clash results from selected groups (or a test's raw,
  ungrouped results) by source model, in a user-defined priority order; includes a "locate"
  action per model that selects/frames its geometry in the Navisworks view before you commit
  to an order.
- **Sign Off Area** - pick a model, select which zone/volume "sign off areas" to process, and
  for each one: find every real-geometry element from the *other* appended models that falls
  inside that area (again via mesh containment, not bounding-box), hide everything else, frame
  the camera, and save the result as one Saved Viewpoint per area (with its own hide/require
  state baked in via `CaptureRuntimeOverrides`).
- **Clashes to Viewpoints** - for each selected clash group, isolate the clashing items (red
  for side A, green for side B, grey/transparent context), frame an isometric camera on them
  via the low-level COM API, add redline text labels (model, element ID, system), and save one
  Saved Viewpoint per group under a folder named after its clash test.

## Layout

```
src/AddIn/            Entry assembly Navisworks actually scans (C#, .NET Framework 4.8)
  Plugin/WebUiDockPanePlugin.cs      DockPanePlugin - loads WebViewHost by reflection
  Plugin/TestDockPaneRibbonPlugin.cs Ribbon tab + one command per feature, routes to a view name
  Plugin/NativeMessageBridge.cs      Dispatches JSON messages from the WebView2 UI to the feature services below
  Plugin/ClashMatrixGenerator.cs     Clash Test Generation + Delete Clash Tests
  Plugin/ZoneGrouping/               Group by Zone (zone/volume detection + real-geometry matching engine)
  Plugin/CoincidentGrouping/         Group All Involving Items by Group
  Plugin/ModelGrouping/              Group by Model Priority
  Plugin/SignOff/                    Sign Off Area
  Plugin/ClashesToViewpoints/        Clashes to Viewpoints
  TestDockPaneRibbon.xaml            Ribbon layout (+ en-US/es-ES locale copies)
src/WebViewHost/      WebView2 UI, isolated in its own assembly (see "Why two assemblies")
  UI/WebViewHostControl.xaml(.cs)    WPF UserControl hosting the WebView2 control
  Bootstrap/WebViewBootstrapper.cs   WebView2 environment init + virtual host mapping + dev-server toggle
  WebUiAssets/                       populated by build/copy-webui-dist.ps1, ships as Content
src/WebUI/            React + Vite + TypeScript + Tailwind v4 + shadcn/ui
  src/lib/native.ts                 Typed wrapper over the WebView2 <-> C# message bridge, with a mock fallback for `npm run dev` in a plain browser
  src/*View.tsx                     One component per feature, switched on by App.tsx
src/DeployTool/       Double-click .exe that deploys AddIn + WebViewHost to Navisworks (no admin needed)
build/copy-webui-dist.ps1       builds WebUI and copies dist/ into WebViewHost/WebUiAssets
build/launch-navisworks-dev.ps1 launches Navisworks with the pane pointed at the Vite dev server
```

## Why two assemblies?

Navisworks' plugin loader reflects over an assembly to find `[Plugin]`-attributed types. If
that assembly references any dependency it can't resolve, the *entire* assembly silently
fails to register - not just the one type that needed the bad dependency. We hit this
directly: once `WebUiDockPanePlugin` referenced `Microsoft.Web.WebView2.*` (a private, NuGet
dependency, not in the GAC), Navisworks stopped loading anything from that DLL at all,
including the completely unrelated ribbon plugin in the same file. A deliberately
dependency-free test plugin loaded fine, which isolated the cause.

The fix: `src/AddIn` (what Navisworks scans) has **zero** reference to WebView2 - not even a
`ProjectReference` (which would add a compile-time assembly reference even without any code
using it). `WebUiDockPanePlugin.CreateControlPane()` instead loads
`NavisworksDockPanel.WebViewHost.dll` via `Assembly.LoadFrom` + reflection, only at the
moment the pane is actually opened, with an `AssemblyResolve` handler to find WebView2's own
DLLs alongside it. AddIn's `.csproj` copies WebViewHost's build output into its own output
folder as plain files (an MSBuild `Copy` target, not a project reference) so the files are
there at runtime without ever showing up in AddIn's own assembly metadata.

If you add new functionality, keep this boundary: anything touching WebView2 goes in
`WebViewHost`, and `AddIn`'s plugin classes only ever talk to it through reflection.

## Ribbon: activating the dock pane

Every feature button lives under one ribbon tab (`TestDockPaneRibbonPlugin.cs`, XAML tab id
`TestDockPaneTab`) and shares a single dock pane instance. Each button maps to a view name
(`"generate"`, `"delete"`, `"zones"`, `"involvingItems"`, `"modelPriority"`, `"signOff"`,
`"clashesToViewpoints"`); clicking one looks up `WebUiDockPanePlugin` by its plugin ID
(`NavisworksDockPanel.WebUiDockPane.AcmeDev`), loads it if needed, calls `ShowView(view)` to
push a `{action: "setView", view}` message into the already-running React app, then
`Visible = true` + `ActivatePane()`. Clicking the same button again while its view is already
showing toggles the pane closed instead.

Adding a new feature means: a C# service + JSON-facade controller under `src/AddIn/Plugin/`, a
case in `NativeMessageBridge.Handle`, a `native.ts` wrapper function (with a mock fallback), a
React view component, a `view === "..."` branch in `App.tsx`, and a `[Command(...)]` +
ribbon-XAML button + `ExecuteCommand` case wiring it up. The existing features under
`src/AddIn/Plugin/*Grouping/`, `SignOff/`, and `ClashesToViewpoints/` are the reference pattern
to copy.

## Deploying the add-in

Navisworks Manage 2026 discovers add-ins from a `Plugins\<FolderName>\<FolderName-matching-DLL>`
folder - either under its install directory (admin-write-protected) or per-user under
`%APPDATA%\Autodesk\Navisworks Manage 2026\Plugins\`. `src/DeployTool` builds a small
`DeployNavisworksPlugins.exe` that deploys to the per-user `%APPDATA%` location only - no admin
rights or UAC prompt needed, so the built `src\DeployTool\bin\Debug` folder (exe + its bundled
`Payload` subfolder) can be zipped and handed to someone else to run on their own machine.

Two things the deploy must get right, or Navisworks silently ignores the plugin with no
error at all:
- The subfolder name must exactly match the DLL's base name.
- The ribbon XAML must also exist inside a locale subfolder matching Navisworks' running UI
  language (e.g. `en-US\TestDockPaneRibbon.xaml`, `es-ES\TestDockPaneRibbon.xaml`) - a
  root-level copy alone is not enough. `AddIn.csproj` already copies both.

## Dev loop

**C# changes** (ribbon, dock pane, WebView2 bootstrap logic) always need a full rebuild +
redeploy + Navisworks restart - .NET Framework can't unload an assembly from a running
process, so there's no hot-reload for this part:
```powershell
dotnet build src/WebViewHost/WebViewHost.csproj
dotnet build src/AddIn/AddIn.csproj
dotnet build src/DeployTool/DeployTool.csproj
```
The last build step re-bundles AddIn's freshly built output into `DeployTool`'s own output
folder (see "Deploying the add-in" below) - build it last, or `DeployNavisworksPlugins.exe`
will redeploy stale bits. Then double-click `src\DeployTool\bin\Debug\DeployNavisworksPlugins.exe`,
fully exit Navisworks, and relaunch it.

**UI changes** (React/shadcn) can skip all of that. `WebViewBootstrapper` checks the
`NAVISWORKSDOCKPANEL_DEV_URL` environment variable and, if set, points the pane at a live
Vite dev server instead of the built assets:
```bash
cd src/WebUI
npm run dev
```
Then, instead of launching Navisworks normally:
```powershell
./build/launch-navisworks-dev.ps1
```
Open any of the ribbon buttons - the pane now points at your dev server. Edit anything under
`src/WebUI/src/`, save, and it hot-reloads inside the pane like a browser tab.

To add more shadcn components:
```bash
cd src/WebUI
npx shadcn@latest add <component>
```

## Verification

- Rebuild + redeploy, launch Navisworks, confirm the ribbon tab and all of its buttons appear.
- Click a button: the shadcn UI should render inside the pane (or a message box should appear
  describing exactly what failed - both `WebUiDockPanePlugin` and `TestDockPaneRibbonPlugin`
  surface errors instead of failing silently).
- Close/reopen the pane a few times; check Task Manager for orphaned `msedgewebview2.exe`
  processes to confirm WebView2 disposal is working.
- Confirm `npm run dev` + `launch-navisworks-dev.ps1` gives live UI reload with no rebuild.

## Diagnostics

`Log.Write` (`src/AddIn/Diagnostics/Log.cs` and its `src/WebViewHost/Diagnostics/Log.cs`
counterpart) append timestamped lines to `%LOCALAPPDATA%\NavisworksDockPanel\debug.log` - both
WebView2-bridge traffic and feature-specific `[FeatureName] ...` entries (e.g. `[SignOff]`,
`[ClashesToViewpoints]`) land there. It's the first place to look when a feature silently does
nothing or takes longer than expected. `DeployNavisworksPlugins.exe` writes its own
`deploy.log` next to it.
