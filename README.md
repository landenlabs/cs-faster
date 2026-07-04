# Faster

<img src="icon.png" width="96" align="right" alt="Faster icon">

<!-- VERSION -->v1.0.0<br>
<!-- DATE -->04-Jul-2026<br>

Save your Windows machine's current service configuration as a baseline, then build named
lists that stop-and-disable or start-and-restore groups of services - a quick way to switch
parts of Windows on or off to tune performance (e.g. "Gaming Mode" disables indexing/telemetry
services; activating it again later, or a "Restore" list, puts them back).

## Build & run

```pwsh
dotnet build                          # or open Faster.sln / Faster.csproj in Visual Studio
dotnet run                            # launch the GUI
Faster.exe                            # launch the GUI
Faster.exe --list                     # list saved lists
Faster.exe --show "Gaming Mode"       # show one list's services + planned actions
Faster.exe --activate "Gaming Mode"   # apply a saved list now
Faster.exe --restore                  # restore EVERY service to the baseline - no saved list needed
Faster.exe --delete "Gaming Mode"     # delete a saved list
Faster.exe --baseline                 # show the baseline (captures one first if missing)
Faster.exe --recapture-baseline       # force a fresh baseline capture
Faster.exe --admin                    # force the UAC prompt - combine with any other command
Faster.exe --help                     # usage
```

`--activate`, `--restore`, and `--recapture-baseline` request Administrator on their own - each
change a service's configuration, so `Program.Main` checks whether the process is already
elevated and, if not, relaunches itself with a UAC prompt (`Elevation.RelaunchAsAdmin`,
`ShellExecute` + `"runas"`), rather than declaring `requireAdministrator` in `app.manifest`. (A
manifest-declared `requireAdministrator` caused this app's apphost to fail to launch at all with
a side-by-side activation error on some SDKs - `app.manifest` here uses `asInvoker`, and
elevation happens at runtime instead.) `--help`, `--list`, `--show`, `--delete`, and `--baseline`
are read-only and never elevate on their own, so they always print straight to the calling
console - no UAC prompt, no risk of losing their output.

`--admin` forces that same UAC prompt/relaunch up front, regardless of which command follows it
- a no-op if already elevated. It exists for batch/script use: the three commands above already
elevate automatically, so `--admin`'s value there is mainly making a script's intent to elevate
explicit, but it can also force an otherwise read-only command to run elevated. Running
`Faster.exe --admin` with no other command launches the GUI pre-elevated. Because a UAC relaunch
spawns a new, separate process, running one of these elevating commands from a non-elevated
terminal means its console output may not reliably reattach to that terminal; run from an
already-elevated prompt if you need to see that output inline.

**The GUI always launches unelevated**, no matter what rights the launching process has, and
elevates on demand instead: a "Run as Admin" button is the first (leftmost) toolbar button
whenever you're not already elevated, "Activate Selected List" and "Restore All to Baseline" get
a small purple dot as a heads-up that they need admin, and the bottom-left status label reads
"Administrator" (blue) or "Standard user" (purple, click to elevate) - same pattern as
`cs-b4browse`. Clicking Activate or Restore while unelevated offers to relaunch as Administrator
first rather than letting every service in the list fail with an access-denied error.

## How it works

- **First run** (GUI or any headless command) captures a *baseline*: every service's start
  type (Boot/System/Automatic/Manual/Disabled), its "Delayed Start" flag, whether it has any
  trigger-start events registered (informational only - triggers are never modified), and
  whether it was running - saved to `%LocalAppData%\Faster\baseline.json`.
- **Named lists** (`%LocalAppData%\Faster\user_lists\`, one JSON file per list) are user-created
  groups of services. Each service in a list gets its own action, chosen per-row in a small grid
  when you save or update the list: **Stop** (set a start type, usually Disabled, then stop it),
  **Start** (set a start type, usually Automatic, then start it), or **Restore to baseline** (put
  it back exactly how the baseline found it). A "Set all rows to" bar bulk-fills the common case
  where every service in the list gets the same action; individual rows can still be overridden
  by hand for a mixed list (e.g. stop two services, start one, restore two others).
- **The "Saved lists" panel**: a sortable table (Modified date / # services / Name - click a
  header to sort by it) plus a button underneath it for every action. Check the services you
  want in the grid (a select-all checkbox above the checkbox column checks/unchecks every
  currently visible row in one click), then press **"Save n checked service(s) as..."** to name
  and save them as a new list. Selecting an existing list in the table instead checks exactly its
  services and enables **Activate**, **Update**, **Details**, and **Delete** - each button names
  the list it acts on (e.g. `Activate 'Gaming Mode'`) and stays greyed out until a list is
  selected. **Update** re-saves the selected list from whatever's currently checked, with no
  extra confirmation. **Details** shows the same per-service grid, read-only. Press Esc while the
  table has focus to clear the selection (checked services are left as they are).
- **Activating** a list applies every item and reports success/failure per service - one
  stubborn service (e.g. a protected system service) doesn't block the rest of the list.
- **Restore All to Baseline** is the one action that isn't tied to a saved list: it restores
  *every* service on the machine to the baseline in one click, with no list required.
- **Resource metrics** are opt-in: press the **Metrics** button (between Refresh and Re-capture
  Baseline) to add PID/Memory/Handles/Threads/CPU %/Process columns and sample every running
  service's host process - kept out of the default load so the grid stays fast to open. Press it
  again ("Refresh Metrics") to re-sample. The same numbers appear in a service's Details popup.
  Because Windows often groups several services into one host process (e.g. a shared
  `svchost.exe`), a "shared x`N`" note means the numbers are for the whole process, not that one
  service alone.
- The **About** tab (bottom-right) shows the app's version/build date and an "Open Settings
  Folder" shortcut; the **Help** button (top-right of the toolbar) opens this same feature tour
  plus links to the GitHub repo and a few general resources on Windows services.

Storage is per-user (`%LocalAppData%`), so lists/baseline don't collide across accounts and
don't require the account that first ran Faster to keep matching ACLs for later runs, elevated
or not.

## Known limitations

- Changing a start type uses `sc.exe config` (there is no managed .NET API for it).
- Stopping a service first stops any of its *currently running* dependents; starting a service
  relies on the SCM's own dependency handling rather than Faster forcing open unrelated
  services' configuration.
- Trigger-start events (e.g. a service that starts itself on a device-arrival event) are
  reported but never edited - only the plain start type and running state are captured/restored.
- If `Microsoft.Win32.Registry` types aren't resolved at build time, add
  `<PackageReference Include="Microsoft.Win32.Registry" Version="5.0.0" />` to `Faster.csproj`
  (on a `net*-windows` TFM this is normally unnecessary, but SDK versions vary).

## Recovery scripts

`scripts\srv_save_all.ps1` and `scripts\srv_set.ps1` are a standalone PowerShell fallback that
reads and writes the exact same JSON files as the app (`baseline.json`, `user_lists\*.json`),
for testing what a list would do or recovering a machine without needing the app itself:

```pwsh
scripts\srv_save_all.ps1                              # capture a baseline (no admin needed)
scripts\srv_set.ps1 -InputPath "Gaming Mode" -WhatIf   # preview a saved list, no admin needed
scripts\srv_set.ps1 -InputPath "Gaming Mode"           # apply it (prompts per service)
scripts\srv_set.ps1 -RestoreAll -Confirm:$false        # put every service back to baseline, no prompts
```

Both scripts prompt for confirmation per service by default (disabling the wrong service is
exactly the risk they exist to guard against) and support `-WhatIf` for a no-changes preview
that doesn't even require Administrator.

## Architecture

- `ServiceSnapshot.cs` / `ServiceListItem.cs` / `ServiceListDefinition.cs` / `Baseline.cs` -
  plain model classes.
- `AppPaths.cs`, `BaselineStore.cs`, `ListStore.cs` - JSON persistence under `%LocalAppData%\Faster`.
- `Elevation.cs` - `IsAdmin` + `RelaunchAsAdmin()`, shared by `Program.cs` and `MainForm.cs`.
- `RegistryHelpers.cs` - reads `DelayedAutoStart` and trigger presence from the registry.
- `ServiceOps.cs` - the engine: `sc.exe config` for start type, dependency-aware stop/start,
  per-item exception isolation.
- `CliRunner.cs` / `Program.cs` - headless command parsing and dispatch.
- `MainForm.cs` / `NewListDialog.cs` / `ServiceDetailsPanel.cs` - the GUI. The right side of the
  main window is a "Lists" / "Details" tab strip - "Details" shows the selected row's info as
  formatted label/value tables and updates live as the grid selection changes.
- `ServiceCatalog.cs` - category/purpose lookup for well-known services.
- `ServiceMetrics.cs` - on-demand PID/memory/handles/threads/CPU sampling (the Metrics button
  and the Details tab).
