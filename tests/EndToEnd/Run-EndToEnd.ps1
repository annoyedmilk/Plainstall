<#
.SYNOPSIS
    Builds a test ISO with Plainstall and installs it in a Hyper-V virtual machine.

.DESCRIPTION
    One command for the whole end-to-end test:
      1. builds the command-line tool,
      2. writes a test profile (Recommended preset, local account "tester" with a random password, the image
         check DISM ScanHealth switched on),
      3. runs 'Plainstall.Cli build --vm-test' against a Windows 11 ISO,
      4. runs Test-InVm.ps1, which installs the result in a new virtual machine and checks it.

    Everything it writes goes to -OutDir, including a transcript, so the run can be read afterwards.
    The ISO it produces erases disk 0 of whatever boots it. It is for the virtual machine only.

    Needs: administrator rights, the .NET 10 SDK, and for step 4 the Hyper-V role.

.EXAMPLE
    .\tests\EndToEnd\Run-EndToEnd.ps1 -IsoSource "$env:USERPROFILE\Downloads\Windows11.iso"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $IsoSource,
    [string] $OutDir = (Join-Path $env:ProgramData "Plainstall\e2e"),
    [string] $Preset = "recommended",
    # Regional values for the test profile. Explicit, so the test does not depend on this PC's settings.
    [string] $Region = "de-CH",
    [string] $Keyboard = "de-CH",
    [string] $TimeZone = "W. Europe Standard Time",
    # Only build the ISO. Do not start a virtual machine.
    [switch] $SkipVm,
    # Leave out the image check (DISM ScanHealth) at the end of the build. It adds 5 to 15 minutes.
    [switch] $SkipScanHealth,
    # Use the last Release build of the command-line tool. Do not build it again. Use this switch when the
    # elevated account has no NuGet cache or cannot get to the package feed.
    [switch] $NoBuild,
    [switch] $NoTpm,
    [switch] $KeepVm,
    [int] $TimeoutMinutes = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal] $identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated PowerShell (Run as administrator)."
}

# This script lives in tests\EndToEnd, two folders below the root of the repository.
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$IsoSource = (Resolve-Path -LiteralPath $IsoSource).Path
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$transcript = Join-Path $OutDir ("end-to-end-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
Start-Transcript -Path $transcript | Out-Null
$exitCode = 1
try {
    $cli = Join-Path $repo "src\Plainstall.Cli\bin\Release\net10.0-windows\Plainstall.Cli.exe"
    if ($NoBuild) {
        Write-Host "== 1. Using the command-line tool as built: $cli"
        if (-not (Test-Path -LiteralPath $cli)) { throw "There is no Release build of the command-line tool at '$cli'. Run without -NoBuild." }
    } else {
        Write-Host "== 1. Building the command-line tool"
        & dotnet build (Join-Path $repo "src\Plainstall.Cli\Plainstall.Cli.csproj") -c Release -nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "The build of the command-line tool failed." }
    }

    Write-Host "== 2. Writing the test profile"
    $profilePath = Join-Path $OutDir "vm-test.plainstall.json"
    $profile = [ordered]@{
        schema  = 1
        name    = "VM test ($Preset)"
        windows = [ordered]@{ source = "iso"; language = "auto"; edition = "Professional" }
        apps    = [ordered]@{ preset = $Preset }
        tweaks  = [ordered]@{
            preset = $Preset
            enable = @("dark-theme", "classic-context-menu", "no-search-history", "no-snap-layouts-popup",
                "no-cloud-search", "no-recent-files")
        }
        setup   = [ordered]@{
            localAccount         = [ordered]@{ name = "tester"; askPassword = $true; password = $null }
            computerName         = "PLAINSTALL-VM"
            skipPrivacyPages     = $true
            region               = $Region
            keyboard             = $Keyboard
            timezone             = $TimeZone
            bypassHardwareChecks = $true
        }
        verify  = [ordered]@{ scanHealth = (-not $SkipScanHealth) }
    }
    $profile | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $profilePath -Encoding UTF8

    # A random password for the throwaway account. It exists in memory and inside the test ISO only.
    $chars = 'abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789'.ToCharArray()
    $plain = -join (1..20 | ForEach-Object { $chars | Get-Random })
    $secure = ConvertTo-SecureString $plain -AsPlainText -Force

    Write-Host "== 3. Building the test ISO (this takes a while)"
    $iso = Join-Path $OutDir "plainstall-vm-test.iso"
    $work = Join-Path $OutDir "work"
    $plain | & $cli build --profile $profilePath --source $IsoSource --iso $iso --work $work --vm-test --overwrite --password-stdin
    $buildExit = $LASTEXITCODE
    $reportPath = Join-Path $OutDir "build-report.json"
    if (Test-Path -LiteralPath $reportPath) {
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        $byResult = $report.actions | Group-Object result | ForEach-Object { "$($_.Name): $($_.Count)" }
        Write-Host ("   result: {0}; actions: {1}" -f $report.result, ($byResult -join ', '))
        foreach ($failed in @($report.actions | Where-Object { $_.result -eq 'failed' })) {
            Write-Host ("   FAILED {0} {1} {2}: {3}" -f $failed.entry, $failed.kind, $failed.target, $failed.reason)
        }
        foreach ($warning in @($report.warnings)) { Write-Host "   warning: $warning" }
        foreach ($check in @($report.actions | Where-Object { $_.kind -eq 'image.scanHealth' })) {
            Write-Host ("   image check (ScanHealth): {0}, {1}" -f $check.result, $check.reason)
        }
    }
    # A build that did not apply all changes still makes a usable ISO. The VM test then checks each change
    # that was applied. The report keeps the list of the failed changes.
    $usable = (Test-Path -LiteralPath $iso) -and (Test-Path -LiteralPath $reportPath) -and
        ($report.result -in 'succeeded', 'completed-with-failures')
    if (-not $usable) { throw "Plainstall.Cli build ended with exit code $buildExit." }
    if ($report.result -ne 'succeeded') { Write-Host "   Some changes failed. The VM test checks the changes that were applied." }
    Write-Host ("   ISO: {0} ({1:N1} GB)" -f $iso, ((Get-Item -LiteralPath $iso).Length / 1GB))

    if ($SkipVm) {
        Write-Host "== 4. Skipped (no virtual machine requested)"
    } else {
        Write-Host "== 4. Installing in a virtual machine"
        & (Join-Path $PSScriptRoot "Test-InVm.ps1") -IsoPath $iso -ReportPath $reportPath -UserName "tester" -Password $secure `
            -NoTpm:$NoTpm -KeepVm:$KeepVm -TimeoutMinutes $TimeoutMinutes -ResultFolder (Join-Path $OutDir "vm-test-result")
    }
    $exitCode = 0
    Write-Host "== End-to-end test finished."
}
catch {
    Write-Host ("== End-to-end test FAILED: " + $_.Exception.Message) -ForegroundColor Red
}
finally {
    Stop-Transcript | Out-Null
    Write-Host "Transcript: $transcript"
}
exit $exitCode
