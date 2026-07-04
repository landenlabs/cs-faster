# Faster — project index

Windows desktop utility (C# WinForms) for saving named "service profiles" and switching
between them to tune system performance: capture a baseline of every service's configuration,
then create named lists that stop-and-disable or start-and-restore groups of services. Has
both a GUI and a headless command-line mode. See `README.md` for the user-facing feature tour.

- **Framework:** .NET 10 (`net10.0-windows`), WinForms, nullable + implicit usings enabled.
- **Namespace / assembly:** `Faster`. Single project, flat directory (no subfolders) - sibling
  project to `cs-b4browse`, same author/conventions, but a separate codebase (no shared code).
- **Author:** Dennis Lang — LanDen Labs (2026). Apache-2.0.
- **Elevation:** `Elevation.cs` (`IsAdmin`, `RelaunchAsAdmin()`) is the one source of truth,
  used by both `Program.cs` and `MainForm.cs` - mirrors `cs-b4browse`'s `Elevation.cs`. The
  headless mutating commands (`--activate`, `--restore`, `--recapture-baseline`) auto-elevate in
  `Program.Main`, relaunching via `ShellExecute`'s `"runas"` verb (UAC prompt) if not already
  elevated; the read-only commands (`--help`, `--list`, `--show`, `--delete`, `--baseline`) never
  check elevation on their own and deliberately run before that check - a UAC relaunch spawns a
  new process whose console can't reattach to the caller's terminal, which would otherwise
  swallow `--help`/`--list` output too. `--admin` is a separate, explicit gate checked before
  either of the above (right after `EnsureConsole()`, before the read-only dispatch) - it forces
  the same relaunch for any command, read-only or mutating, and is excluded from the
  "unrecognized argument" check at the end of `Main` so `Faster.exe --admin` alone launches the
  GUI pre-elevated instead of erroring. Exists for batch/script use, where making elevation
  explicit (or forcing it for a command that wouldn't otherwise need it) is more useful than
  Faster inferring it from which specific command was given. The **GUI always launches
  unelevated** by default (unless started with `--admin`), regardless of the
  process's actual rights, and elevates on demand instead: a "Run as Admin" toolbar button (first/
  leftmost, only present when not already elevated), a purple-dot call-out drawn on the left edge
  of "Activate '&lt;name&gt;'" / "Restore All to Baseline" (`MainForm.AddAdminDot`, also only when
  not elevated), a bottom-status-bar indicator ("Administrator" in blue / "Standard user" in purple,
  click to elevate - `MainForm.UpdateAdminStatusLabel`), and a relaunch-first prompt if either of
  those two buttons is actually clicked while unelevated (`MainForm.ConfirmElevateForAction`) -
  since Windows has no way to elevate a running process, only to start a new elevated one,
  answering "yes" there relaunches Faster and exits the current instance; the original click
  doesn't proceed. `app.manifest` declares `asInvoker`, not `requireAdministrator`: a
  manifest-declared requireAdministrator caused a side-by-side activation error on some SDKs, so
  elevation is requested at runtime instead.

## Build & run

```pwsh
dotnet build
dotnet run
Faster.exe --list | --show <name> | --activate <name> | --restore | --delete <name>
Faster.exe --baseline | --recapture-baseline
Faster.exe --admin [any other command]   # force the UAC prompt regardless of the command
Faster.exe --help
```

## Data flow

1. **Baseline** (`BaselineStore`) - on first run (GUI or any headless command), walks
   `ServiceController.GetServices()` and records each service's `ServiceSnapshot` (start type,
   delayed-auto flag, trigger presence via `RegistryHelpers`, running state) into
   `%LocalAppData%\Faster\baseline.json`. Re-captured on demand (GUI button /
   `--recapture-baseline`), never automatically overwritten otherwise.
2. **Lists** (`ListStore`) - user-named `ServiceListDefinition`s, each a `List<ServiceListItem>`,
   one JSON file per list under `%LocalAppData%\Faster\user_lists\<sanitized-name>.json` (any
   character invalid in a Windows filename is swapped for `_`; a collision between two names that
   sanitize to the same filename gets a numeric suffix - see `ListStore.AllocateNewFilePath`). A
   list is looked up by its `Name` field inside the file, not by filename, so a file renamed by
   hand still round-trips correctly. `ModifiedUtc` is never written into the JSON - `ListStore`
   fills it in after reading, from the file's own last-write time, so the "Saved lists" table's
   Modified column always reflects the filesystem directly with nothing to keep in sync by hand.
   A flat `lists.json` from before this per-file layout, if one exists, is never read - there's
   no migration, it's simply ignored. Each item has one `ServiceTargetAction`: `Stop`, `Start`,
   or `RestoreToBaseline`.
3. **Activation** (`ServiceOps.Activate`) - applies every item in a list: `sc.exe config` for
   the start type (no managed API exists for this), then a dependency-aware stop/start. Each
   item is exception-isolated and returns a `ServiceActionResult` - one failing service does not
   abort the rest of the list. Both the GUI (`MainForm.ActivateSelectedList`) and the headless
   `--activate` command (`CliRunner.Activate`) go through this same path.
4. **Restore-all** (`--restore` / the GUI's "Restore All to Baseline") - a shortcut that skips
   `user_lists\` entirely: builds a one-off, in-memory `ServiceListDefinition` with a
   `RestoreToBaseline` item for every service in the baseline and runs it through the same
   `ServiceOps.Activate` path as a saved list. Nothing is written to disk via `ListStore`.

## Files

| File | Role |
| --- | --- |
| `ServiceSnapshot.cs` | One service's captured config (model). |
| `ServiceListItem.cs` | One service's entry in a named list + `ServiceTargetAction` enum (model). |
| `ServiceListDefinition.cs` | A named list of items (model). |
| `Baseline.cs` | The full machine-wide snapshot, keyed by service name (model). |
| `AppPaths.cs` | `%LocalAppData%\Faster` paths + atomic (temp+move) JSON writes. |
| `BaselineStore.cs` | Capture/load/save the baseline; `LoadOrCapture()` is the "first run" entry point. |
| `ListStore.cs` | Load/save/CRUD the one-JSON-file-per-list `user_lists\` store (see Data flow above). |
| `Elevation.cs` | `IsAdmin` + `RelaunchAsAdmin()` - shared by `Program.cs` and `MainForm.cs`. |
| `RegistryHelpers.cs` | Read-only: `DelayedAutoStart` flag, trigger-start presence. |
| `ServiceOps.cs` | The engine - `sc.exe config`, dependency-aware stop/start, `Activate()`. |
| `Program.cs` | Entry point - argument parsing, `AttachConsole` for headless output, GUI launch. |
| `CliRunner.cs` | Headless command implementations (`--list/--show/--activate/--delete/--baseline`). |
| `MainForm.cs` | Main window - service grid (checkable) + a right-hand tab strip ("Lists" / "Details" / "About"). |
| `NewListDialog.cs` | One modal behind three flows, all sharing the same per-row grid (Service/Action/Start Type/Delayed): Create a new list, Update an existing one in place, and a read-only "Show Details" view. A "Set all rows to" bar bulk-fills the common one-action case (Create/Update only). |
| `ServiceCatalog.cs` | Curated category/purpose lookup for well-known service names. |
| `ServiceDetailsPanel.cs` | The "Details" tab's content - label/value tables (WMI fields + live resource metrics) for whichever row is currently selected in the grid. |
| `ServiceDetailsDialog.cs` | Unused - superseded by `ServiceDetailsPanel.cs`/the "Details" tab; kept on disk pending removal. |
| `ServiceMetrics.cs` | `ServiceMetrics` model + `ServiceMetricsCollector` (PID/memory/handles/threads/CPU sampling). |
| `AboutPanel.cs` | The "About" tab's content - icon, name/version/description/legal text (version/build date/copyright from `AppInfo`), plus an "Open Settings Folder" button. |
| `AppInfo.cs` | Version/BuildDate/Copyright/Company - read from assembly metadata that MSBuild derives from `Faster.csproj`. Mirrors `cs-b4browse`'s `AppInfo.cs` exactly. |
| `AppIcon.cs` | Loads `icon.ico`/`icon.png`/`dark-light.png` (embedded resource first, loose file next to the exe as a debug-run fallback) for every window's title-bar icon, the About tab's image, and the toolbar's theme-toggle icon. |
| `Theme.cs` | Light/dark theme - `Current`/`Toggle()`/`Apply()`, a colour palette, `ApplyToTree(Control)` (generic type-driven recolouring + button styling + native-scrollbar theming for a whole form), persisted to `%LocalAppData%\Faster\theme.txt`. Mirrors `cs-b4browse`'s `Theme.cs` minus that app's separate font-scaling feature. |
| `HelpDialog.cs` | Modal opened by the toolbar's right-edge "Help" button - a read-only `RichTextBox` feature tour mirroring README.md's intro/"How it works" (skipping the CLI and Architecture sections, which are for repo readers, not end users), with `DetectUrls` + `LinkClicked` opening the GitHub repo link, the full README link, and a "Resources" section of Microsoft Learn pages on Windows services in the default browser. |
| `icon.ico` / `icon.png` | The app icon - `icon.ico` is a multi-resolution (16-256px) `.ico` generated from `icon.png`; `icon.ico` doubles as the `<ApplicationIcon>` (the .exe's own Explorer/file-properties icon) and, via `AppIcon`, the runtime window icon; `icon.png` is the source art and the About tab's larger display image. |
| `app.manifest` | `asInvoker` (see Elevation above) + per-monitor DPI awareness. |
| `VERSION` | Bare `X.Y.Z`, kept in sync with `Faster.csproj`'s `<Version>` by `set-version.ps1` (see Versioning below). |
| `.github/workflows/publish.yml` | On a `v*` tag push: publishes a self-contained single-file win-x64 build, zips it (portable), packages an MSIX (`makeappx`) and an MSI (WiX, via `.github/packaging/faster.wxs`), optionally code-signs all three, and attaches them to a GitHub Release. Mirrors `cs-b4browse`'s workflow of the same name. |
| `.github/packaging/AppxManifest.xml` / `faster.wxs` | Templates (`{VERSION}`/`{EXEDIR}` placeholders) for the MSIX/MSI packaging steps above. |

## Left grid

- **Select-all header checkbox** (`MainForm._selectAllCheck`) - a real tri-state `CheckBox`
  overlaid directly on the "Use" column's header cell (`PositionSelectAllHeaderCheck`, re-run on
  every grid/column resize - `DataGridView` has no built-in header checkbox, so this is a normal
  child control positioned over that cell's `GetCellDisplayRectangle`, not owner-drawn). Shows
  Checked/Unchecked/Indeterminate based on how many of the currently VISIBLE rows (`_rows`, not
  `_allRows`) are checked; clicking it (`ToggleSelectAllVisible`) checks all of them if they
  weren't all already checked, otherwise unchecks all of them - a filtered-out service is left
  exactly as it was either way.
- Pressing **Esc** while the "Saved lists" table (`_listsGrid`) has focus clears its selection
  back to none (standard "back out of this" convention) without touching any grid checkboxes -
  selecting vs. deselecting a list is orthogonal to what's currently checked, so
  `ApplyListSelectionToChecks`'s existing no-op guard for "nothing selected" already does the
  right thing here for free.
- The grid's left panel (`SplitContainer.Panel1`, `FixedPanel = Panel2`) absorbs all resize when
  the main window is widened, so the grid itself already grows with the window; the "Display
  Name" column additionally has `AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill` so it - and
  only it - soaks up any resulting extra horizontal space instead of leaving blank space past the
  last column, while every other column keeps its explicit `Width` and stays manually resizable.
- `Grid_CellFormatting` also tints the Start Type column - plain "Automatic" the same light green
  as a Running cell, "Automatic (Delayed)" a visibly deeper shade of the same green - so the two
  read apart at a glance; Manual/Disabled/Boot/System keep the grid's normal colors.

## Right-hand panel

The right side of the main window is a `TabControl` with three tabs, sized by `SplitContainer`
with `FixedPanel = Panel2` (so it stays a constant pixel width when the window is resized,
instead of scaling proportionally):
- **Lists** - a sortable **table** (`_listsGrid`, a read-only `DataGridView` bound to
  `BindingList<ListRow>` - `ListRow` is a thin view over `ServiceListDefinition`: `Modified`,
  `# Services`, `Name`) plus a button stack below it: `Save {n} checked service(s) as...` /
  `Restore All to Baseline` / `Activate '<name>'` / `Update '<name>'` / `Details of '<name>'` /
  `Delete '<name>'`, in that order - the first two are global (no list selection needed: Save
  Checked reads `_allRows`' checked services, Restore All touches every service on the machine),
  then the four list-scoped actions below them. `Name` has
  `AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill` (same idiom as the main grid's Display
  Name column) so it soaks up any extra width; `Modified`/`# Services` keep fixed, resizable
  widths. Clicking a column header sorts by that column (`ListsGrid_ColumnHeaderMouseClick` +
  `RebuildListRows`, mirroring the main grid's `Grid_ColumnHeaderMouseClick`/`ApplyFilterAndSort`
  pattern) - the current selection (by list name) survives a re-sort rather than resetting.
  `ServiceListDefinition.ModifiedUtc` is `[JsonIgnore]` - it's never written into the list's own
  JSON file, `ListStore` fills it in on load from that file's last-write time (see Data flow
  above), so this column always reflects the filesystem with nothing hand-stamped to keep in sync.
  - Selecting a list row checks exactly its services in the grid (and unchecks everything else -
    `ApplyListSelectionToChecks`), so "Activate '<name>'" and "what's in this list" are visually
    the same thing. `SelectedListOrNull()` is the silent lookup (used internally, e.g. by
    `UpdateListActionButtons` and to preserve selection across a sort); `SelectedList()` is the
    same lookup with a "select a list first" `MessageBox` fallback, for explicit button clicks.
  - Activate/Update/Show Details/Delete are disabled until a list row is selected
    (`UpdateListActionButtons`, called on every `_listsGrid.SelectionChanged` and after
    `LoadData`); no colored/tinted styling on enable, just the system button's normal
    enabled/disabled look, consistent with how the rest of the app signals state (e.g. the
    "needs admin" purple dot, never a colored fill). Each button's label names its target
    directly rather than saying "Selected List" - e.g. `Activate 'Gaming Mode'`,
    `Update 'Gaming Mode'`, `Details of 'Gaming Mode'`, `Delete 'Gaming Mode'` - falling back to
    `Activate...`/`Update...`/`Details...`/`Delete...` while disabled.
  - **Update** (`MainForm.UpdateSelectedList`) opens `NewListDialog` pre-seeded with the
    currently checked services and the selected list's existing per-service actions (carried
    over by service-name match), then overwrites that exact list on OK - no name to type, no
    "replace this list?" prompt, unlike creating a new list with a name that happens to collide.
  - **Show Details** (`MainForm.ShowSelectedListDetails`) opens the same dialog in its read-only
    mode (`readOnly: true`) instead of the old plain-text `MessageBox` dump, so a mixed list
    (some services Stop, others Start or Restore) reads as a table.
- **Details** - everything known about the currently selected grid row (was previously a
  right-click "Details..." modal popup - that menu item is gone; selecting a row, by any
  means, now updates this tab live via `_grid.SelectionChanged` -> `MainForm.UpdateDetailsPanel`
  -> `ServiceDetailsPanel.ShowService`). Rendered as titled label/value tables (bold label
  column, regular value column, bordered grid cells) rather than one text block: an Overview
  table (name/category/start type/state/baseline), a Purpose paragraph, a WMI-sourced table
  (description/account/type/binary path), and a Resource Usage table (see below) - the WMI and
  metrics tables start as "Loading..." placeholders and are swapped in place once their
  background fetch finishes, discarding the result if the selection has since moved on.
- **About** (`AboutPanel.cs`) - icon (`AppIcon.LoadImage`) beside the name/version, then
  description/legal text, plus an "Open Settings Folder" button that launches `explorer.exe` at
  `AppPaths.RootDir`. Version/BuildDate/Copyright/Company come from `AppInfo` (see Versioning
  below); the description line still comes straight from the assembly's `AssemblyDescription`
  attribute, since `AppInfo` has no property for it. Deliberately simpler than cs-b4browse's
  `AboutForm`: no animated GIF/update check/repo-link button, since Faster has no
  `UpdateCheck` equivalent.

## Versioning - single source

`Faster.csproj`'s `<Version>` is the **one** number to change; everything else derives from it.
`c:\opt\projects\common\set-version.ps1` (a generic, cross-repo bumper) rewrites `<Version>` in
the csproj, the bare `VERSION` file, and the `README.md` `<!-- VERSION -->`/`<!-- DATE -->`
markers together, then commits/tags/pushes to trigger the `publish.yml` release workflow.

`AppInfo` is the runtime source for the version/build date shown in-app (the `MainForm` title
bar and the "About" tab): `<Version>` -> `AssemblyInformationalVersion` -> `AppInfo.Version`, and
a build-time-stamped `AssemblyMetadata("BuildDate")` (added in `Faster.csproj`) -> `AppInfo.
BuildDate`. So the in-app version/date follow the csproj automatically - there is no second
hand-edited constant to keep in sync. This mirrors `cs-b4browse`'s `AppInfo.cs`/versioning setup
exactly (the sibling widget projects under `_dlang/widgets` use a lighter-weight VERSION-file-only
convention with no `AppInfo`/`BuildDate` equivalent - Faster follows cs-b4browse's fuller pattern
instead, since both are WinForms apps with an in-app About view).

## Resource metrics

The grid starts up without any per-process sampling - CPU% needs a real wall-clock delay to
compute, so doing it for every service on every load/refresh would make the grid feel slow. A
"Metrics" button (between "Refresh" and "Re-capture Baseline") adds six columns (PID, Memory,
Handles, Threads, CPU %, Process) on first click and samples every running service's host
process via `ServiceMetricsCollector.CollectAll()` (one shared ~300ms CPU-sampling window across
every distinct host process, not one per service); later clicks ("Refresh Metrics") re-sample
without re-adding the columns. The Details popup shows the same numbers for just the selected
service via `CollectOne()`. Because several services often share one host process (most commonly
several packed into one `svchost.exe`), the numbers are that whole process's total whenever
`SharedWithCount > 0` - both the grid ("shared x`N`") and the Details popup call this out rather
than presenting a false per-service breakdown.

## Theme

Light/dark, toggled by a `dark-light.png` icon button in the toolbar (immediately left of
"Help" - `MainForm._themeToggle`, a `PictureBox` rather than a `Button` so the icon can render
at its native aspect ratio via `PictureBoxSizeMode.Zoom`; shown as-is, not tinted per theme,
since the half-black/half-white circle already reads as "light/dark" on its own). `Theme.cs`
mirrors `cs-b4browse`'s of the same name (same palette, same `Application.SetColorMode`-based
approach - `WFO5001` suppressed in `Faster.csproj` since that API is still experimental) minus
that app's separate content-font-scaling feature, which Faster doesn't have. Choice persisted
to `%LocalAppData%\Faster\theme.txt`; applied once at GUI startup (`Program.Main`, before
`MainForm` is constructed) and live-updated afterward via `Theme.Changed`.

`Theme.ApplyToTree(Control)` is the one call each window needs: it walks every descendant and
recolours it purely by control **type** (`Panel`/`TabPage`/etc. -> `Theme.Panel`,
`DataGridView`/`RichTextBox`/`TextBox`/`ComboBox` -> `Theme.Surface`+`Theme.Text`,
`Label`/`CheckBox` -> `Theme.Text`), then styles every `Button` and native-themes every
scrollable control's OS-drawn scrollbar. This is a deliberately simpler approach than
cs-b4browse's own per-named-field `ApplyThemeColors` (Faster's control tree has no per-row
severity tinting to preserve), so no Panel/Label/etc. needs to be promoted to a field just to
be reachable here. `MainForm` calls it once at construction and again on every `Theme.Changed`
(it's the one long-lived window); `NewListDialog`/`HelpDialog` call it once at construction only
- both are modal, so the toolbar's toggle button is unreachable for as long as one is open, and
each is rebuilt from scratch the next time it's opened anyway. Two things `ApplyToTree`
deliberately does NOT touch, because they're semantic status colours rather than theme chrome
(same principle as cs-b4browse's fixed severity colours): the main grid's `Grid_CellFormatting`
running/stopped/start-type/baseline-mismatch tinting, and the admin purple accent (`AdminAccent` - "Run as
Admin"'s text, the status bar's "Standard user" label, the purple dot on
Activate/Restore-All) - `MainForm.ApplyThemeColors` re-asserts those two right after calling
`ApplyToTree`, since its generic Button/Label pass would otherwise reset them to the theme's
neutral text colour. Native scrollbar theming additionally needs a real Win32 handle to take
effect, which doesn't exist yet at construction time - each window's `OnShown` override calls
`Theme.ApplyScrollbarTheme` again once it does.

## Recovery scripts (`scripts/`)

Standalone PowerShell fallback for when the app itself is unavailable, untrusted for a specific
change, or you just want to test a list's effect before letting the GUI/CLI touch anything -
same underlying idea as `ServiceOps`/`BaselineStore`, reimplemented without any dependency on
the app or the .NET runtime, so it still works if `Faster.exe` itself is broken. Both scripts
read/write the exact same JSON shapes and default file locations as the app
(`%LocalAppData%\Faster\baseline.json`, `%LocalAppData%\Faster\user_lists\*.json`), so the app
and the scripts are fully interchangeable - a list saved from the GUI can be applied by
`srv_set.ps1`, and a baseline captured by `srv_save_all.ps1` is exactly what the GUI reads on
its next launch.

- **`srv_save_all.ps1`** - read-only inventory (no elevation needed): walks every service via
  `Get-CimInstance Win32_Service` plus the same two registry reads `RegistryHelpers.cs` does
  (`DelayedAutoStart`, `TriggerInfo` presence), and writes a `Baseline`-shaped JSON file
  (`-OutputPath`, defaults to the app's real `baseline.json`). Running it with no arguments is
  the script equivalent of the GUI's "Re-capture Baseline" button.
- **`srv_set.ps1`** - applies a `ServiceListDefinition`-shaped JSON (`-InputPath`, accepting
  either a full path or a bare saved-list name resolved against `user_lists\`) to the live
  machine, mirroring `ServiceOps.ApplyItem` item-for-item: `sc.exe config` for the start type
  (same argument mapping table, including the Boot/System/delayed-auto cases `Set-Service` can't
  express), then `Stop-Service -Force`/`Start-Service`. `RestoreToBaseline` items are resolved
  against `-BaselinePath` (same default as above). `-RestoreAll` skips `-InputPath` and builds
  the same one-off "every service, RestoreToBaseline" list `MainForm.RestoreAllToBaseline`/
  `CliRunner.RestoreBaseline` construct, for a fast "put it all back" recovery path. Supports
  the standard `-WhatIf` (preview only, no elevation required) and `-Confirm` switches -
  `ConfirmImpact = 'High'` means every service prompts for confirmation by default even without
  passing `-Confirm`, deliberately, since disabling the wrong service is exactly the risk this
  script exists to guard against; add `-Confirm:$false` once a list is trusted.

## Conventions

- Files are flat in the repo root, one top-level type per file, `Faster` namespace (small UI
  helper types, e.g. `MainForm.ServiceRow`, are nested private classes instead).
- Every service-affecting operation in `ServiceOps` isolates its own exceptions and reports a
  per-service result rather than throwing across a whole list activation.
- Storage is per-user (`%LocalAppData%`) rather than machine-wide: it avoids ACL conflicts when
  the app is run elevated on one launch and as a standard user on the next (see `AppPaths.cs`).
- Trigger-start events are recorded (informational) but never modified - only the plain start
  type, delayed-auto flag, and running state are captured/restored.
