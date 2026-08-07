# RevitPluginTest

A Revit 2027 plugin built around a hot-reloadable Core assembly, used here to
build and iterate on an electrical conduit routing tool without ever
restarting Revit for a Core-side change.

## Architecture

The plugin is split into two assemblies with very different lifecycles:

- **Host** (`RevitPluginTest.csproj`, namespace `RevitPluginTest`) — registered
  via the `.addin` manifest, loaded once per Revit session, **never
  reloadable**. Owns everything Revit or the plugin itself holds a permanent
  reference to: the ribbon, the dockable pane, the `Scheduler`, the debug
  overlay window's own vertical registration. Changing Host code requires
  closing Revit, rebuilding, and relaunching.
- **Core** (`RevitPluginTest.Core.csproj`, namespace `RevitPluginTest.Core`) —
  loaded into a collectible `AssemblyLoadContext`, rebuilt and reloaded via the
  panel's **Reload Core** button with Revit left running. Everything that
  doesn't need a permanent reference from Revit lives here, including the
  debug overlay's drawing logic and the entire conduit-routing feature.

The rule of thumb used throughout: if Revit or Host code ends up holding a
live reference to something, it has to be Host-owned. Everything else can go
in Core and be iterated on freely.

**Command dispatch.** `Scheduler` (Host) keeps separate "stable" (Host) and
"reloadable" (Core) function tables keyed by name, so a Core reload can't
orphan Host functionality or leave a stale reference behind. Any public
static method tagged `[Schedulable("Name")]` becomes callable by that name,
from the console, a panel button, or another command. `CommandText` parses a
small console grammar (`Name(arg1, arg2)`, including typed `XYZ(...)` and
`Color(...)` literals).

**Dynamic panel.** The dockable pane's UI is built in code, not XAML. Core
registers buttons, dropdowns, checkboxes, and sliders through `DynamicPanel`
by name/data; Host does the actual WPF construction and event wiring. This is
how Core-side features (the conduit-routing controls, for example) can add
their own UI without ever requiring a Host change or a Revit restart.

**Debug overlay.** A borderless, click-through, always-on-top WPF window
projects persistent debug primitives (lines, circles, text, arrows, conduit
run rectangles) onto the active Revit view in real time. Orthographic views
only — Revit's API doesn't expose enough to project onto a perspective 3D
view.

## Functional overview

- **Console panel** — buttons, a typed command console (tab completion,
  history, `help`), and a copyable live log.
- **Reload Core** — rebuild and hot-swap the Core assembly without
  restarting Revit.
- **Select Similar / Draw Selected** — selection utilities.
- **Conduit routing test tool** — the main feature:
  - Mark selected elements as **Sources** or **Receptacles**.
  - **Find Nearest Source** computes and draws the conduit path from a
    selected receptacle to every source (green = shortest, red =
    alternatives). A valid path must leave/enter each wall perpendicular to
    it, point toward the building interior (verified via Room data where
    available), and turn only at right angles.
  - **Build Graph** connects every receptacle to its nearest source at once
    and draws the full result.
  - **Allow Intersections** toggles whether the router tries to avoid
    different receptacles' paths crossing each other in plan.
  - **Run Tolerance** controls how close and parallel two paths need to be
    before they're merged into one shared "conduit run", drawn as a bank
    rectangle instead of overlapping lines.

## Prerequisites

- Revit 2027 installed at its default location
  (`C:\Program Files\Autodesk\Revit 2027\`).
- .NET 10 SDK.
- Visual Studio 2022+ (optional — `dotnet build` works fine on its own).

## Build & run

No manual path editing is required — clone and build.

- `RevitAPI.dll`/`RevitAPIUI.dll` are referenced via **absolute** `HintPath`s
  (`C:\Program Files\Autodesk\Revit 2027\...`) in both `.csproj` files, so
  they resolve regardless of where the repo is cloned to. If your Revit 2027
  install lives somewhere else, update those two `<HintPath>` entries.
- `RevitPluginTest.addin` (checked into the repo) is a **template** containing
  the literal token `__ASSEMBLY_PATH__` in place of a real path — it isn't a
  usable manifest by itself. Every Host build regenerates the real manifest
  from this template, substituting `__ASSEMBLY_PATH__` with that build's
  actual absolute output DLL path, and deploys it straight to
  `%APPDATA%\Autodesk\Revit\Addins\2027\RevitPluginTest.addin` (see the
  `CopyAddinManifest` target in `RevitPluginTest.csproj`) — the folder Revit
  actually scans. So it's always correct, however deep the repo happens to be
  nested on disk.

### 1. Build Host, then Core

```powershell
dotnet build RevitPluginTest.csproj
dotnet build RevitPluginTest.Core\RevitPluginTest.Core.csproj
```

Core references Host's already-built DLL via a plain `HintPath`, not a
`ProjectReference`, on purpose: building Core must never depend on rebuilding
Host, whose output Revit may have locked while running.

### 2. Launch Revit 2027

The addin loads automatically on startup (it's an `Application`-type addin,
not a `Command`). Open a project and the dockable panel should appear.

## Development workflow

- **Editing Core code** (anything under `RevitPluginTest.Core\`): rebuild
  Core (`dotnet build RevitPluginTest.Core\RevitPluginTest.Core.csproj`) with
  Revit left running, then click **Reload Core** in the panel. No restart
  needed.
- **Editing Host code** (`PluginPanel.cs`, `DynamicPanel.cs`, `Scheduler.cs`,
  `RevitPluginTestApplication.cs`, anything at the repo root): close Revit
  first (its build output is locked while running), rebuild Host, then
  relaunch Revit.
- When unsure which side a change lives on: if Revit or Host code ends up
  holding a reference to it, it's Host; otherwise it's fair game for Core.

## Project layout

```
RevitPluginTest.addin              Manifest template (see Build & run)
RevitPluginTest.csproj             Host project
RevitPluginTest.sln
*.cs (repo root)                   Host source
RevitPluginTest.Core/
  RevitPluginTest.Core.csproj      Core project
  *.cs                             Core source (conduit routing, debug overlay, etc.)
```
