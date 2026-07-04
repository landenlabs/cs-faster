<#
.SYNOPSIS
    Applies a saved service list to the live machine - a PowerShell-only fallback for
    Faster.exe's own "Activate" (and "Restore All to Baseline"), usable to test what a list
    would actually do, or to recover a machine if the app itself is unavailable.

.DESCRIPTION
    Reads a JSON file in the same shape as one of Faster's own user_lists\<name>.json files
    (a "Name"/"Items" list, each item a ServiceName/Action/TargetStartType/TargetDelayedAutoStart)
    and applies every item to the live machine, mirroring ServiceOps.ApplyItem in the app exactly:

      - Stop:              set the start type, then stop the service (dependents are stopped
                            first, via Stop-Service -Force, same as the app's dependency handling).
      - Start:              set the start type, then start the service if it isn't running.
      - RestoreToBaseline:  ignore the item's own TargetStartType and instead put the service
                            back exactly how -BaselinePath's baseline.json found it (start type,
                            delayed-auto flag, running/stopped state).

    One failing service does not stop the rest of the list from being applied - same
    exception-isolation policy as the app - and a summary is printed at the end.

    SAFETY: this script supports the standard PowerShell -WhatIf and -Confirm switches.
      -WhatIf   previews exactly what would change, for every item, without touching anything -
                and does NOT require Administrator, so you can sanity-check a list from a
                standard PowerShell prompt before ever risking a real change.
      (default) each item's action prompts for a Yes/No/Yes-to-All/Suspend confirmation before
                it's applied - this is a deliberate default given how easy it is to disable the
                wrong service, not an accident. Add -Confirm:$false once you trust a list (e.g.
                for unattended/scheduled use).

.PARAMETER InputPath
    Path to a ServiceListDefinition JSON file (one of Faster's own user_lists\*.json files, or
    a hand-written one in the same shape). If this doesn't resolve to an existing file and
    looks like a bare name (no ".json", no path separator), the script also tries
    "<Faster's user_lists folder>\<InputPath>.json" - so you can just pass a saved list's name.
    Mutually exclusive with -RestoreAll.

.PARAMETER RestoreAll
    Skip -InputPath entirely and instead build a one-off list, in memory, with a
    RestoreToBaseline item for every service in -BaselinePath's baseline - the script
    equivalent of the app's own "Restore All to Baseline" button / "--restore" CLI command.
    This is the "undo everything" fallback if a list leaves the machine in a bad state.

.PARAMETER BaselinePath
    Baseline JSON to resolve RestoreToBaseline items against (from either -InputPath's list or
    -RestoreAll). Defaults to the same file Faster.exe itself reads/writes
    (%LocalAppData%\Faster\baseline.json) - captured by the app's own first run, its
    "Re-capture Baseline" button, or this folder's own srv_save_all.ps1.

.EXAMPLE
    .\srv_set.ps1 -InputPath 'Gaming Mode' -WhatIf
    Shows exactly what applying the saved list "Gaming Mode" would do, without changing
    anything and without needing Administrator.

.EXAMPLE
    .\srv_set.ps1 -InputPath 'C:\opt\Gaming Mode.json'
    Applies that list for real, prompting per-service for confirmation (the default).

.EXAMPLE
    .\srv_set.ps1 -RestoreAll -Confirm:$false
    Restores every service in the baseline back to how it was captured, no per-service prompts -
    the "something's wrong, put it all back" emergency button.
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Position = 0)]
    [Alias('Input')]
    [string]$InputPath,

    [switch]$RestoreAll,

    [string]$BaselinePath = (Join-Path $env:LOCALAPPDATA 'Faster\baseline.json')
)

$ErrorActionPreference = 'Stop'

if (-not $InputPath -and -not $RestoreAll) {
    Write-Error "Specify -InputPath <file.json> (or a saved list's name) or -RestoreAll. Run 'Get-Help .\srv_set.ps1 -Full' for details."
    exit 2
}
if ($InputPath -and $RestoreAll) {
    Write-Error "-InputPath and -RestoreAll are mutually exclusive - pick one."
    exit 2
}

# ---- sc.exe start-type mapping - byte-for-byte the same switch ServiceOps.SetStartType uses in
# the app, since there is no managed API for this (Set-Service -StartupType can't express
# Boot/System, and can't reliably express the delayed-auto flag on older PowerShell either). ---- #
function Set-ServiceStartTypeViaSc {
    param(
        [Parameter(Mandatory)][string]$ServiceName,
        [Parameter(Mandatory)][string]$StartType,
        [bool]$DelayedAutoStart
    )
    $arg = switch ($StartType) {
        'Boot' { 'boot' }
        'System' { 'system' }
        'Automatic' { if ($DelayedAutoStart) { 'delayed-auto' } else { 'auto' } }
        'Manual' { 'demand' }
        'Disabled' { 'disabled' }
        default { 'demand' }
    }
    # The space after "start=" is not a typo - sc.exe's own option parser requires it, exactly
    # as noted in ServiceOps.cs. PowerShell passes $ServiceName/'start='/$arg as three separate
    # argv entries here, which is the correct split regardless of whether the name has spaces.
    $output = & sc.exe config $ServiceName start= $arg 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe config start= $arg failed (exit $LASTEXITCODE): $($output -join ' ')"
    }
}

function Get-StartTypeDescription {
    param([string]$StartType, [bool]$DelayedAutoStart)
    if ($StartType -eq 'Automatic' -and $DelayedAutoStart) { 'Automatic (Delayed Start)' } else { $StartType }
}

function New-ActionResult {
    param([string]$ServiceName, [string]$DisplayName, [bool]$Success, [string]$Message)
    [pscustomobject]@{ ServiceName = $ServiceName; DisplayName = $DisplayName; Success = $Success; Message = $Message }
}

# ---- Applies one item for real (start type + stop/start/restore) - never called unless
# ShouldProcess has already said yes, so it does not need to know about -WhatIf/-Confirm itself. #
function Invoke-ServiceListItemForReal {
    param($Item, $Baseline, [string]$DisplayName)

    $name = $Item.ServiceName
    switch ([string]$Item.Action) {
        'Stop' {
            $startType = if ($Item.TargetStartType) { [string]$Item.TargetStartType } else { 'Disabled' }
            $delayed = [bool]$Item.TargetDelayedAutoStart
            Set-ServiceStartTypeViaSc -ServiceName $name -StartType $startType -DelayedAutoStart $delayed
            if ((Get-Service -Name $name).Status -ne 'Stopped') {
                Stop-Service -Name $name -Force   # -Force also stops any running dependents first
            }
            return New-ActionResult $name $DisplayName $true `
                "Start type set to $(Get-StartTypeDescription $startType $delayed); stopped."
        }
        'Start' {
            $startType = if ($Item.TargetStartType) { [string]$Item.TargetStartType } else { 'Automatic' }
            $delayed = [bool]$Item.TargetDelayedAutoStart
            Set-ServiceStartTypeViaSc -ServiceName $name -StartType $startType -DelayedAutoStart $delayed
            if ((Get-Service -Name $name).Status -ne 'Running') {
                Start-Service -Name $name
            }
            return New-ActionResult $name $DisplayName $true `
                "Start type set to $(Get-StartTypeDescription $startType $delayed); started."
        }
        'RestoreToBaseline' {
            if (-not $Baseline) {
                return New-ActionResult $name $DisplayName $false "No baseline loaded - can't restore."
            }
            $snapProp = $Baseline.Services.PSObject.Properties[$name]
            if (-not $snapProp) {
                return New-ActionResult $name $DisplayName $false "No baseline snapshot recorded for this service - can't restore."
            }
            $snap = $snapProp.Value
            $startType = [string]$snap.StartType
            $delayed = [bool]$snap.DelayedAutoStart
            Set-ServiceStartTypeViaSc -ServiceName $name -StartType $startType -DelayedAutoStart $delayed
            $current = (Get-Service -Name $name).Status
            if ([bool]$snap.WasRunning) {
                if ($current -ne 'Running') { Start-Service -Name $name }
            }
            else {
                if ($current -ne 'Stopped') { Stop-Service -Name $name -Force }
            }
            $stateText = if ([bool]$snap.WasRunning) { 'running' } else { 'stopped' }
            return New-ActionResult $name $DisplayName $true `
                "Restored to baseline: $(Get-StartTypeDescription $startType $delayed), $stateText."
        }
        default {
            return New-ActionResult $name $DisplayName $false "Unrecognized action '$($Item.Action)'."
        }
    }
}

# ---- Load the list (from -InputPath, resolving a bare name against Faster's own user_lists
# folder, or built in-memory for -RestoreAll) --------------------------------------------------- #

$userListsDir = Join-Path $env:LOCALAPPDATA 'Faster\user_lists'

function Resolve-ListInputPath {
    param([string]$Path)
    if (Test-Path -LiteralPath $Path) { return (Resolve-Path -LiteralPath $Path).ProviderPath }

    # A bare name (no .json extension, no path separator) is tried against Faster's own saved
    # lists, so "-InputPath 'Gaming Mode'" works exactly like typing a name in the app's Lists tab.
    if ($Path -notmatch '[\\/]' -and $Path -notmatch '\.json$') {
        $candidate = Join-Path $userListsDir "$Path.json"
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    return $null
}

$baseline = $null
function Get-BaselineOrWarn {
    if (-not (Test-Path -LiteralPath $BaselinePath)) {
        Write-Warning "No baseline found at '$BaselinePath' - RestoreToBaseline items will fail. Run '.\srv_save_all.ps1' first, or pass -BaselinePath."
        return $null
    }
    try {
        return Get-Content -LiteralPath $BaselinePath -Raw | ConvertFrom-Json
    }
    catch {
        Write-Warning "Could not parse baseline at '$BaselinePath': $($_.Exception.Message)"
        return $null
    }
}

if ($RestoreAll) {
    $baseline = Get-BaselineOrWarn
    if (-not $baseline -or -not $baseline.Services.PSObject.Properties.Count) {
        Write-Error "Baseline is empty or missing - nothing to restore."
        exit 1
    }
    $items = foreach ($prop in $baseline.Services.PSObject.Properties) {
        $s = $prop.Value
        [pscustomobject]@{
            ServiceName            = $s.ServiceName
            DisplayName            = $s.DisplayName
            Action                 = 'RestoreToBaseline'
            TargetStartType        = $s.StartType
            TargetDelayedAutoStart = $s.DelayedAutoStart
        }
    }
    $list = [pscustomobject]@{ Name = '(restore-all)'; Items = @($items) }
}
else {
    $resolvedPath = Resolve-ListInputPath -Path $InputPath
    if (-not $resolvedPath) {
        Write-Error "Could not find '$InputPath' as a file, or as a saved list under '$userListsDir'."
        exit 2
    }
    try {
        $list = Get-Content -LiteralPath $resolvedPath -Raw | ConvertFrom-Json
    }
    catch {
        Write-Error "Could not parse '$resolvedPath' as JSON: $($_.Exception.Message)"
        exit 2
    }
    if (-not $list.Items -or @($list.Items).Count -eq 0) {
        Write-Host "'$resolvedPath' has no services in it - nothing to do."
        exit 0
    }
    if (@($list.Items) | Where-Object { $_.Action -eq 'RestoreToBaseline' }) {
        $baseline = Get-BaselineOrWarn
    }
}

# ---- Elevation check - skipped entirely for -WhatIf, which never changes anything and so
# never needs Administrator; this lets you preview a list from a standard prompt. ---- #
if (-not $WhatIfPreference) {
    # Spelled out as separate statements (rather than a one-line cast-and-chain) so this works
    # unchanged on both Windows PowerShell 5.1 and PowerShell 7+ - this is meant to work as a
    # fallback even on a stock, nothing-extra-installed Windows machine.
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    $isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        Write-Error "Changing service start types/state requires Administrator. Re-launch PowerShell as Administrator, or add -WhatIf to preview without elevation."
        exit 1
    }
}

Write-Host "Applying '$($list.Name)' ($(@($list.Items).Count) service(s)) ..."
Write-Host ""

$results = foreach ($item in @($list.Items)) {
    $name = [string]$item.ServiceName
    $displayName = if ($item.DisplayName) { [string]$item.DisplayName } else { $name }

    $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
    if (-not $svc) {
        New-ActionResult $name $displayName $false "Service not found on this machine."
        continue
    }
    $displayName = $svc.DisplayName

    $actionDescription = switch ([string]$item.Action) {
        'Stop' { "set start type + stop" }
        'Start' { "set start type + start" }
        'RestoreToBaseline' { "restore to baseline" }
        default { "apply '$($item.Action)'" }
    }

    if ($PSCmdlet.ShouldProcess("$displayName ($name)", $actionDescription)) {
        try {
            Invoke-ServiceListItemForReal -Item $item -Baseline $baseline -DisplayName $displayName
        }
        catch {
            New-ActionResult $name $displayName $false $_.Exception.Message
        }
    }
    else {
        # -WhatIf, or the user answered "No" at the confirmation prompt - not a failure, just
        # not applied; counted separately from real successes/failures in the summary below.
        New-ActionResult $name $displayName $true "Skipped (not confirmed)."
    }
}

Write-Host ""
foreach ($r in $results) {
    if ($r.Success) {
        Write-Host ("OK    {0} ({1}) - {2}" -f $r.DisplayName, $r.ServiceName, $r.Message) -ForegroundColor Green
    }
    else {
        Write-Host ("FAIL  {0} ({1}) - {2}" -f $r.DisplayName, $r.ServiceName, $r.Message) -ForegroundColor Red
    }
}

$failedCount = @($results | Where-Object { -not $_.Success }).Count
Write-Host ""
if ($failedCount -eq 0) {
    Write-Host "$(@($results).Count) service(s) processed, 0 failed." -ForegroundColor Green
}
else {
    Write-Host "$(@($results).Count) service(s) processed, $failedCount FAILED - see above." -ForegroundColor Yellow
}
exit ([int]($failedCount -gt 0))
