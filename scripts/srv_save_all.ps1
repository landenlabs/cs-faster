<#
.SYNOPSIS
    Captures every Windows service's current start-type/state as a JSON "baseline" - a
    PowerShell-only fallback for Faster.exe's own baseline capture, usable even if the app
    itself is unavailable or you just don't trust it yet.

.DESCRIPTION
    Walks every service via Win32_Service (no elevation required - this is a read-only
    inventory, nothing is changed) and writes a JSON file in the exact shape Faster's own
    Baseline/ServiceSnapshot classes use:

        {
          "CapturedUtc": "2026-07-04T12:34:56.0000000Z",
          "Services": {
            "wuauserv": {
              "ServiceName": "wuauserv",
              "DisplayName": "Windows Update",
              "StartType": "Automatic",
              "DelayedAutoStart": false,
              "HasTriggers": false,
              "WasRunning": true,
              "CapturedUtc": "2026-07-04T12:34:56.0000000Z"
            },
            ...
          }
        }

    By default this OVERWRITES the same baseline.json the GUI/CLI reads and writes
    (%LocalAppData%\Faster\baseline.json), so running this script has the same effect as the
    app's own "Re-capture Baseline" button - use -OutputPath to write somewhere else instead
    (e.g. to snapshot the machine's state before testing a risky list, without touching the
    baseline srv_set.ps1's -RestoreAll and the app's "Restore All to Baseline" fall back to).

.PARAMETER OutputPath
    Where to write the captured baseline. Defaults to the same file Faster.exe itself uses
    (%LocalAppData%\Faster\baseline.json), so this script is a drop-in stand-in for the app's
    own baseline capture with no extra setup.

.EXAMPLE
    .\srv_save_all.ps1
    Refreshes the app's real baseline.json - equivalent to clicking "Re-capture Baseline" in
    the GUI, but from a PowerShell prompt (or a scheduled task) with no GUI involved at all.

.EXAMPLE
    .\srv_save_all.ps1 -OutputPath .\before-test.json
    Snapshots the current state to a throwaway file, so you can diff it against
    -OutputPath .\after-test.json, or feed it to srv_set.ps1 -BaselinePath .\before-test.json
    -RestoreAll to undo whatever you're about to try.
#>
[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $env:LOCALAPPDATA 'Faster\baseline.json')
)

$ErrorActionPreference = 'Stop'

# ---- Field-level helpers, one per ServiceSnapshot property the managed Win32_Service class
# doesn't expose directly (mirrors Faster's own RegistryHelpers.cs) --------------------------- #

function Convert-Win32StartModeToStartType {
    # Win32_Service.StartMode is "Boot"/"System"/"Auto"/"Manual"/"Disabled" - Faster's own
    # ServiceStartMode enum (and the JSON it reads/writes) spells the automatic one "Automatic",
    # not "Auto". Everything else already matches. An unrecognized value (shouldn't happen -
    # these five are the whole set per the Win32_Service schema) falls back to "Manual" rather
    # than aborting the whole capture over one odd service.
    param([string]$Win32StartMode)
    switch ($Win32StartMode) {
        'Auto' { 'Automatic' }
        'Boot' { 'Boot' }
        'System' { 'System' }
        'Manual' { 'Manual' }
        'Disabled' { 'Disabled' }
        default { 'Manual' }
    }
}

function Get-ServiceDelayedAutoStartFlag {
    # HKLM\SYSTEM\CurrentControlSet\Services\<name>'s DelayedAutoStart value - only meaningful
    # for Automatic services; a missing value (most services don't set it) means "not delayed".
    param([string]$ServiceName)
    try {
        $val = (Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" `
            -Name DelayedAutoStart -ErrorAction Stop).DelayedAutoStart
        return [bool]$val
    }
    catch {
        return $false   # missing value, missing key, or access denied - treat as "not delayed"
    }
}

function Get-ServiceHasTriggersFlag {
    # Presence of a non-empty TriggerInfo subkey - informational only, same as the app: neither
    # this script nor Faster.exe ever add/remove/modify a trigger, only report whether one exists.
    param([string]$ServiceName)
    try {
        $path = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName\TriggerInfo"
        if (-not (Test-Path -LiteralPath $path)) { return $false }
        return @(Get-ChildItem -LiteralPath $path -ErrorAction Stop).Count -gt 0
    }
    catch {
        return $false
    }
}

# ---- Capture ---------------------------------------------------------------------------------- #

$capturedUtc = [DateTime]::UtcNow
# "o" (round-trip) is exactly the format .NET's System.Text.Json uses for DateTime by default -
# using it here keeps this script's output byte-for-byte compatible with what Faster.exe itself
# would have written, so the two are truly interchangeable.
$capturedUtcText = $capturedUtc.ToString('o')

Write-Host "Capturing current configuration for every Windows service ..."

$services = [ordered]@{}
$captured = 0
$skipped = 0

foreach ($svc in (Get-CimInstance -ClassName Win32_Service)) {
    try {
        $name = $svc.Name
        $startType = Convert-Win32StartModeToStartType -Win32StartMode $svc.StartMode
        $delayedAutoStart = if ($startType -eq 'Automatic') { Get-ServiceDelayedAutoStartFlag -ServiceName $name } else { $false }
        $hasTriggers = Get-ServiceHasTriggersFlag -ServiceName $name
        $wasRunning = ($svc.State -eq 'Running')

        # Property names/casing below are load-bearing, not cosmetic: Faster's own JSON reader
        # (System.Text.Json, no PropertyNameCaseInsensitive option set) matches property names
        # case-SENSITIVELY, so these must be spelled exactly like ServiceSnapshot.cs's properties.
        $services[$name] = [ordered]@{
            ServiceName      = $name
            DisplayName      = $svc.DisplayName
            StartType        = $startType
            DelayedAutoStart = $delayedAutoStart
            HasTriggers      = $hasTriggers
            WasRunning       = $wasRunning
            CapturedUtc      = $capturedUtcText
        }
        $captured++
    }
    catch {
        # One service that can't be fully inspected (rare - access-denied edge cases) shouldn't
        # blank out the whole capture, same isolation policy as BaselineStore.Capture() in the app.
        Write-Warning "Skipped '$($svc.Name)': $($_.Exception.Message)"
        $skipped++
    }
}

$baseline = [ordered]@{
    CapturedUtc = $capturedUtcText
    Services    = $services
}

# -Depth must cover Baseline -> Services -> <one service> (3 levels) - ConvertTo-Json's own
# default of 2 would silently flatten each service's properties to "@{...}" strings instead of
# real nested JSON objects.
$json = $baseline | ConvertTo-Json -Depth 6

$outDir = Split-Path -Path $OutputPath -Parent
if ($outDir -and -not (Test-Path -LiteralPath $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

# Temp file + rename, the same "never leave a half-written, corrupt store behind" pattern
# AppPaths.WriteAtomic uses in the app. UTF8Encoding($false) = no BOM, matching .NET's own
# File.WriteAllText default so the file this script writes and the file Faster.exe writes are
# byte-for-byte the same shape.
$tempPath = "$OutputPath.tmp"
[System.IO.File]::WriteAllText($tempPath, $json, [System.Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $tempPath -Destination $OutputPath -Force

Write-Host ""
Write-Host "Captured $captured service(s)$(if ($skipped -gt 0) { " ($skipped skipped)" }) to:" -ForegroundColor Green
Write-Host "  $OutputPath" -ForegroundColor Green
