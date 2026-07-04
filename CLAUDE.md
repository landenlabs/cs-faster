# Faster — project index

Windows desktop utility (C# WinForms) for saving named "service profiles" and switching
between them to tune system performance: capture a baseline of every service's configuration,
then create named lists that stop-and-disable or start-and-restore groups of services. Has
both a GUI and a headless command-line mode. See `README.md` for the user-facing feature tour.

- **Framework:** .NET 10 (`net10.0-windows`), WinForms, nullable + implicit usings enabled.
- **Namespace / assembly:** `Faster`. Single project, flat directory (no subfolders) - sibling
  project to `cs-b4browse`, same author/conventions, but a separate codebase (no shared code).
- **Author:** Dennis Lang — LanDen Labs (2026). Apache-2.0.
- **Elevation:** only for commands that change something - `--activate`, `--restore`,
  `--recapture-baseline`, and the GUI. `Program.Main` checks `WindowsPrincipal.IsInRole(Administrator)`
  for just those and, if not elevated, relaunches itself via `ShellExecute`'s `"runas"` verb (UAC
  prompt). The read-only commands (`--help`, `--list`, `--show`, `--delete`, `--baseline`) never
  check elevation, and deliberately run before that check in `Main` - a UAC relaunch spawns a new
  process whose console can't reattach to the caller's terminal, which would otherwise swallow
  `--help`/`--list` output too. `app.manifest` declares `asInvoker`, not `requireAdministrator`: a
  manifest-declared requireAdministrator caused a side-by-side activation error on some SDKs, so
  elevation is requested at runtime instead.

## Build & run

```pwsh
dotnet build
dotnet run
Faster.exe --list | --show <name> | --activate <name> | --restore | --delete <name>
Faster.exe --baseline | --recapture-baseline
Faster.exe --help
```

## Data flow

1. **Baseline** (`BaselineStore`) - on first run (GUI or any headless command), walks
   `ServiceController.GetServices()` and records each service's `ServiceSnapshot` (start type,
   delayed-auto flag, trigger presence via `RegistryHelpers`, running state) into
   `%ProgramData%\Faster\baseline.json`. Re-captured on demand (GUI button /
   `--recapture-baseline`), never automatically overwritten otherwise.
2. **Lists** (`ListStore`) - user-named `ServiceListDefinition`s (each a `List<ServiceListItem>`)
   in `%ProgramData%\Faster\lists.json`. Each item has one `ServiceTargetAction`: `Stop`,
   `Start`, or `RestoreToBaseline`.
3. **Activation** (`ServiceOps.Activate`) - applies every item in a list: `sc.exe config` for
   the start type (no managed API exists for this), then a dependency-aware stop/start. Each
   item is exception-isolated and returns a `ServiceActionResult` - one failing service does not
   abort the rest of the list. Both the GUI (`MainForm.ActivateSelectedList`) and the headless
   `--activate` command (`CliRunner.Activate`) go through this same path.
4. **Restore-all** (`--restore` / the GUI's "Restore All to Baseline") - a shortcut that skips
   `lists.json` entirely: builds a one-off, in-memory `ServiceListDefinition` with a
   `RestoreToBaseline` item for every service in the baseline and runs it through the same
   `ServiceOps.Activate` path as a saved list. Nothing is written to `lists.json`.

## Files

| File | Role |
| --- | --- |
| `ServiceSnapshot.cs` | One service's captured config (model). |
| `ServiceListItem.cs` | One service's entry in a named list + `ServiceTargetAction` enum (model). |
| `ServiceListDefinition.cs` | A named list of items (model). |
| `Baseline.cs` | The full machine-wide snapshot, keyed by service name (model). |
| `AppPaths.cs` | `%ProgramData%\Faster` paths + atomic (temp+move) JSON writes. |
| `BaselineStore.cs` | Capture/load/save the baseline; `LoadOrCapture()` is the "first run" entry point. |
| `ListStore.cs` | Load/save/CRUD `lists.json`. |
| `RegistryHelpers.cs` | Read-only: `DelayedAutoStart` flag, trigger-start presence. |
| `ServiceOps.cs` | The engine - `sc.exe config`, dependency-aware stop/start, `Activate()`. |
| `Program.cs` | Entry point - argument parsing, `AttachConsole` for headless output, GUI launch. |
| `CliRunner.cs` | Headless command implementations (`--list/--show/--activate/--delete/--baseline`). |
| `MainForm.cs` | Main window - service grid (checkable) + saved-lists panel. |
| `NewListDialog.cs` | Modal: name a new list, pick its action + target start type. |
| `ServiceCatalog.cs` | Curated category/purpose lookup for well-known service names. |
| `ServiceDetailsDialog.cs` | Read-only per-service details popup (WMI fields + live resource metrics). |
| `ServiceMetrics.cs` | `ServiceMetrics` model + `ServiceMetricsCollector` (PID/memory/handles/threads/CPU sampling). |
| `app.manifest` | `asInvoker` (see Elevation above) + per-monitor DPI awareness. |

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

## Conventions

- Files are flat in the repo root, one top-level type per file, `Faster` namespace (small UI
  helper types, e.g. `MainForm.ServiceRow`, are nested private classes instead).
- Every service-affecting operation in `ServiceOps` isolates its own exceptions and reports a
  per-service result rather than throwing across a whole list activation.
- Storage is machine-wide (`%ProgramData%`), not per-user, since services are a machine-level
  concept.
- Trigger-start events are recorded (informational) but never modified - only the plain start
  type, delayed-auto flag, and running state are captured/restored.
