<#
.SYNOPSIS
    Installs a Plainstall ISO in a fresh Hyper-V virtual machine and checks the result.

.DESCRIPTION
    End-to-end test for Plainstall. It
      1. creates a generation 2 virtual machine with an empty disk,
      2. boots the ISO and waits until Windows is installed and the test account is signed in,
      3. reads build-report.json and checks every applied action inside the VM,
      4. checks that Windows Update, Defender, the Store, winget, Terminal and WinRE are intact,
      5. saves a screenshot and a result file.

    Build the ISO with 'Plainstall.Cli build --vm-test'. Only then does the answer file select the disk
    and sign in the test account one time. NEVER start a real PC from such an ISO.

    Needs: Windows PowerShell 5.1, administrator rights, the Hyper-V role.

.EXAMPLE
    .\tests\EndToEnd\Test-InVm.ps1 -IsoPath C:\out\plainstall.iso -ReportPath C:\out\build-report.json -UserName tester -Password (Read-Host -AsSecureString)
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $IsoPath,
    [Parameter(Mandatory)] [string] $ReportPath,
    # The local account the profile creates. It needs a password: PowerShell Direct refuses blank ones.
    [Parameter(Mandatory)] [string] $UserName,
    [Parameter(Mandatory)] [securestring] $Password,
    [string] $VmName = "plainstall-test",
    [string] $VmFolder = (Join-Path $env:ProgramData "Plainstall\vm-test"),
    [int] $MemoryGB = 4,
    [int] $DiskGB = 64,
    [int] $CpuCount = 4,
    # Without a virtual TPM, or with less than 4 GB RAM, the VM tests the hardware bypass.
    # By default, the VM meets the hardware requirements.
    [switch] $NoTpm,
    [string] $SwitchName = "Default Switch",
    [int] $TimeoutMinutes = 120,
    # Keep the VM after the test instead of deleting it.
    [switch] $KeepVm,
    [string] $ResultFolder = (Join-Path (Split-Path -Parent $ReportPath) "vm-test-result")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step([string] $text) { Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $text) }

# --- preconditions -------------------------------------------------------------------------------
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal] $identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated PowerShell (Run as administrator)."
}
if (-not (Get-Command Get-VM -ErrorAction SilentlyContinue)) {
    throw "The Hyper-V PowerShell module is missing. Enable the Hyper-V feature first."
}
$IsoPath = (Resolve-Path -LiteralPath $IsoPath).Path
$ReportPath = (Resolve-Path -LiteralPath $ReportPath).Path
$report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
if ($report.result -notin 'succeeded', 'completed-with-failures') { throw "The build report says the build did not succeed: $($report.result)" }
if (Get-VM -Name $VmName -ErrorAction SilentlyContinue) {
    throw "A virtual machine named '$VmName' already exists. Remove it or pass another -VmName."
}
New-Item -ItemType Directory -Force -Path $VmFolder, $ResultFolder | Out-Null
$credential = New-Object System.Management.Automation.PSCredential($UserName, $Password)

# --- helpers -------------------------------------------------------------------------------------
function Save-VmScreenshot([string] $name, [string] $path) {
    # Hyper-V hands out the screen as raw RGB565 pixels.
    $vm = Get-CimInstance -Namespace root\virtualization\v2 -ClassName Msvm_ComputerSystem -Filter "ElementName='$name'"
    $video = Get-CimAssociatedInstance -InputObject $vm -ResultClassName Msvm_VideoHead
    if (-not $video) { return $false }
    $width = [int] $video[0].CurrentHorizontalResolution
    $height = [int] $video[0].CurrentVerticalResolution
    if ($width -le 0 -or $height -le 0) { return $false }
    $service = Get-CimInstance -Namespace root\virtualization\v2 -ClassName Msvm_VirtualSystemManagementService
    $settings = Get-CimAssociatedInstance -InputObject $vm -ResultClassName Msvm_VirtualSystemSettingData |
        Where-Object { $_.VirtualSystemType -eq 'Microsoft:Hyper-V:System:Realized' } | Select-Object -First 1
    $result = Invoke-CimMethod -InputObject $service -MethodName GetVirtualSystemThumbnailImage -Arguments @{
        TargetSystem = $settings; WidthPixels = [uint16] $width; HeightPixels = [uint16] $height
    }
    if ($result.ReturnValue -ne 0 -or -not $result.ImageData) { return $false }
    Add-Type -AssemblyName System.Drawing
    $bitmap = New-Object System.Drawing.Bitmap($width, $height, [System.Drawing.Imaging.PixelFormat]::Format16bppRgb565)
    try {
        $rect = New-Object System.Drawing.Rectangle(0, 0, $width, $height)
        $data = $bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::WriteOnly, $bitmap.PixelFormat)
        [System.Runtime.InteropServices.Marshal]::Copy([byte[]] $result.ImageData, 0, $data.Scan0, [Math]::Min($result.ImageData.Length, $data.Stride * $height))
        $bitmap.UnlockBits($data)
        $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }
    return $true
}

function Send-VmKey([string] $name, [int] $virtualKey) {
    $vm = Get-CimInstance -Namespace root\virtualization\v2 -ClassName Msvm_ComputerSystem -Filter "ElementName='$name'"
    $keyboard = Get-CimAssociatedInstance -InputObject $vm -ResultClassName Msvm_Keyboard
    if ($keyboard) { Invoke-CimMethod -InputObject $keyboard -MethodName TypeKey -Arguments @{ keyCode = [uint32] $virtualKey } | Out-Null }
}

function Test-GuestReady {
    # True once the test account is signed in and Plainstall's first-logon work has finished.
    try {
        Invoke-Command -VMName $VmName -Credential $credential -ErrorAction Stop -ScriptBlock {
            $explorer = [bool] (Get-Process explorer -ErrorAction SilentlyContinue)
            $logs = 'C:\Windows\Setup\Scripts\Plainstall\Logs'
            $machineScript = Test-Path 'C:\Windows\Setup\Scripts\Plainstall\Machine.ps1'
            $machineDone = (-not $machineScript) -or (Test-Path (Join-Path $logs 'Machine.done'))
            $userScript = Test-Path 'C:\Windows\Setup\Scripts\Plainstall\User.ps1'
            $userDone = (-not $userScript) -or (Test-Path (Join-Path $env:LOCALAPPDATA 'Plainstall\User.done'))
            $explorer -and $machineDone -and $userDone
        }
    } catch { $false }
}

# --- create and start the VM ---------------------------------------------------------------------
$vhd = Join-Path $VmFolder "$VmName.vhdx"
$started = Get-Date
try {
    Write-Step "Creating virtual machine '$VmName' ($CpuCount CPUs, $MemoryGB GB RAM, $DiskGB GB disk, TPM: $(-not $NoTpm))"
    if (Test-Path -LiteralPath $vhd) { Remove-Item -LiteralPath $vhd -Force }
    $vmSwitch = Get-VMSwitch -Name $SwitchName -ErrorAction SilentlyContinue
    $vm = New-VM -Name $VmName -Generation 2 -MemoryStartupBytes ($MemoryGB * 1GB) -NewVHDPath $vhd -NewVHDSizeBytes ($DiskGB * 1GB) -Path $VmFolder
    Set-VM -VM $vm -ProcessorCount $CpuCount -AutomaticCheckpointsEnabled $false -CheckpointType Disabled -StaticMemory
    if ($vmSwitch) { Connect-VMNetworkAdapter -VMName $VmName -SwitchName $SwitchName } else { Write-Warning "Switch '$SwitchName' not found: the VM has no network." }
    $dvd = Add-VMDvdDrive -VMName $VmName -Path $IsoPath -Passthru
    Set-VMFirmware -VMName $VmName -EnableSecureBoot On -SecureBootTemplate MicrosoftWindows -FirstBootDevice $dvd
    if (-not $NoTpm) {
        Set-VMKeyProtector -VMName $VmName -NewLocalKeyProtector
        Enable-VMTPM -VMName $VmName
    }
    Start-VM -Name $VmName

    # With an empty disk the "Press any key to boot from CD or DVD" prompt normally does not appear.
    # A few Enter presses cover the case where it does.
    for ($i = 0; $i -lt 8; $i++) { Start-Sleep -Seconds 1; Send-VmKey $VmName 13 }

    Write-Step "Windows Setup is running. Waiting for the desktop of '$UserName' (up to $TimeoutMinutes minutes)."
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    $lastShot = Get-Date
    $ready = $false
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 30
        if ((Get-VM -Name $VmName).State -ne 'Running') {
            # Setup restarts the VM several times. A VM that stays off has failed.
            Start-Sleep -Seconds 20
            if ((Get-VM -Name $VmName).State -eq 'Off') { Start-VM -Name $VmName }
        }
        if (((Get-Date) - $lastShot).TotalMinutes -ge 5) {
            $null = Save-VmScreenshot $VmName (Join-Path $ResultFolder ("progress-{0:HHmm}.png" -f (Get-Date)))
            $lastShot = Get-Date
        }
        if (Test-GuestReady) { $ready = $true; break }
    }
    $null = Save-VmScreenshot $VmName (Join-Path $ResultFolder "desktop.png")
    if (-not $ready) {
        throw "The desktop was not reached within $TimeoutMinutes minutes. See the screenshots in $ResultFolder."
    }
    $installMinutes = [int] ((Get-Date) - $started).TotalMinutes
    Write-Step "Desktop reached after $installMinutes minutes. Checking the installed system."

    # --- verification inside the VM --------------------------------------------------------------
    $applied = @($report.actions | Where-Object { $_.result -eq 'applied' })
    $checks = Invoke-Command -VMName $VmName -Credential $credential -ArgumentList (, $applied) -ScriptBlock {
        param($actions)
        $results = New-Object System.Collections.Generic.List[object]
        function Add-Result($entry, $kind, $target, $ok, $detail) {
            $results.Add([pscustomobject]@{ entry = $entry; kind = $kind; target = $target; ok = [bool] $ok; detail = [string] $detail })
        }

        # The Default user's registry file, for values written offline into it.
        $defaultLoaded = $false
        & reg.exe load 'HKU\PlainstallCheck' 'C:\Users\Default\NTUSER.DAT' 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) { $defaultLoaded = $true }

        $provisioned = @(Get-AppxProvisionedPackage -Online | Select-Object -ExpandProperty DisplayName)
        $tasks = @(Get-ScheduledTask | ForEach-Object { ($_.TaskPath + $_.TaskName).TrimStart('\') })

        foreach ($a in $actions) {
            try {
                switch ($a.kind) {
                    'appx.removeProvisioned' {
                        Add-Result $a.entry $a.kind $a.target ($provisioned -notcontains $a.target) 'provisioned package list'
                    }
                    'task.delete' {
                        $prefix = $a.target.TrimEnd('\') + '\'
                        $left = @($tasks | Where-Object { $_ -eq $a.target -or $_.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
                        Add-Result $a.entry $a.kind $a.target ($left.Count -eq 0) ($left -join ', ')
                    }
                    'file.delete' {
                        Add-Result $a.entry $a.kind $a.target (-not (Test-Path -LiteralPath (Join-Path 'C:\' $a.target))) ''
                    }
                    'feature.enable' {
                        $feature = Get-WindowsOptionalFeature -Online -FeatureName $a.target -ErrorAction SilentlyContinue
                        $state = if ($feature) { [string] $feature.State } else { 'missing' }
                        Add-Result $a.entry $a.kind $a.target ($state -eq 'Enabled') "state: $state"
                    }
                    { $_ -in 'registry.set', 'registry.delete' } {
                        # target looks like "MachineSoftware\Policies\...\ValueName"
                        $hive, $rest = $a.target -split '\\', 2
                        $root = switch ($hive) {
                            'MachineSoftware' { 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE' }
                            'MachineSystem'   { 'Registry::HKEY_LOCAL_MACHINE\SYSTEM' }
                            'SystemUser'      { 'Registry::HKEY_USERS\.DEFAULT' }
                            'DefaultUser'     { if ($defaultLoaded) { 'Registry::HKEY_USERS\PlainstallCheck' } else { $null } }
                            default           { $null }   # SetupSystem lives in boot.wim and cannot be checked here
                        }
                        if (-not $root) { Add-Result $a.entry $a.kind $a.target $true 'not checkable after installation'; break }
                        if ($a.kind -eq 'registry.set') {
                            $key = Split-Path -Parent $rest
                            $name = Split-Path -Leaf $rest
                            if ($name -eq '(Default)') { $name = '(default)' }
                            $item = Get-ItemProperty -LiteralPath (Join-Path $root $key) -ErrorAction SilentlyContinue
                            $present = $item -and ($item.PSObject.Properties.Name -contains $name)
                            Add-Result $a.entry $a.kind $a.target $present $(if ($present) { "value: $($item.$name)" } else { 'value missing' })
                        } else {
                            # A deleted key must be gone. For a deleted value, the target ends with the value name.
                            # That path is not a key, so for a value this check always passes.
                            $gone = -not (Test-Path -LiteralPath (Join-Path $root $rest))
                            Add-Result $a.entry $a.kind $a.target $gone ''
                        }
                    }
                    default { Add-Result $a.entry $a.kind $a.target $true 'no automatic check for this kind' }
                }
            } catch {
                Add-Result $a.entry $a.kind $a.target $false $_.Exception.Message
            }
        }
        if ($defaultLoaded) { [gc]::Collect(); & reg.exe unload 'HKU\PlainstallCheck' 2>&1 | Out-Null }

        # Values written into the Default profile that Windows might reset when it creates an account:
        # check them in the signed-in test account too.
        # A one-value list needs the leading comma, or PowerShell flattens it into the value itself.
        $perAccount = @{
            'taskbar-never-combine' = @(
                @('Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced', 'TaskbarGlomLevel', 2),
                @('Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced', 'MMTaskbarGlomLevel', 2))
            'windowed-game-optimizations' = @(, @('Software\Microsoft\DirectX\UserGpuPreferences', 'DirectXUserGlobalSettings', 'SwapEffectUpgradeEnable=1;'))
            'short-menu-delay' = @(, @('Control Panel\Desktop', 'MenuShowDelay', '100'))
            'no-startup-delay' = @(, @('Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize', 'StartupDelayInMSec', 0))
            'no-snap-layouts-popup' = @(
                @('Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced', 'EnableSnapAssistFlyout', 0),
                @('Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced', 'EnableSnapBar', 0))
            'no-recent-files' = @(
                @('Software\Microsoft\Windows\CurrentVersion\Explorer', 'ShowRecent', 0),
                @('Software\Microsoft\Windows\CurrentVersion\Explorer', 'ShowFrequent', 0),
                @('Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced', 'Start_TrackDocs', 0))
        }
        $appliedEntries = @($actions | ForEach-Object { $_.entry } | Select-Object -Unique)
        foreach ($entryId in $perAccount.Keys) {
            if ($appliedEntries -notcontains $entryId) { continue }
            foreach ($value in $perAccount[$entryId]) {
                $item = Get-ItemProperty -LiteralPath (Join-Path 'Registry::HKEY_CURRENT_USER' $value[0]) -ErrorAction SilentlyContinue
                $actual = if ($item -and ($item.PSObject.Properties.Name -contains $value[1])) { $item.($value[1]) } else { $null }
                Add-Result $entryId 'account value' "HKCU\$($value[0])\$($value[1])" ($actual -eq $value[2]) "value: $actual"
            }
        }

        # First sign-in work (firstLogon.registry, firstLogon.command, winget) is not in the build report: Machine.ps1
        # and User.ps1 do it on the installed PC. Each step they wrote to their log is one result here.
        $firstLogonLogs = [ordered]@{
            'Machine.ps1' = 'C:\Windows\Setup\Scripts\Plainstall\Logs\Machine.log'
            'User.ps1'    = (Join-Path $env:LOCALAPPDATA 'Plainstall\Logs\User.log')
        }
        foreach ($logName in $firstLogonLogs.Keys) {
            if (-not (Test-Path -LiteralPath $firstLogonLogs[$logName])) { continue }
            foreach ($line in (Get-Content -LiteralPath $firstLogonLogs[$logName] -Encoding UTF8)) {
                # "<date> <time>  OK      <title>", "ERROR   <title>", "FAILED  <title>: <reason>", "SKIPPED <what>: <reason>"
                if ($line -match '^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}  (OK|ERROR|FAILED|SKIPPED) +(.+)$') {
                    Add-Result $logName 'first sign-in step' $Matches[2] ($Matches[1] -eq 'OK') $Matches[1]
                }
            }
        }

        # What the product promises to keep working.
        $health = [ordered]@{}
        $health['Windows Update service present'] = [bool] (Get-Service wuauserv -ErrorAction SilentlyContinue)
        $health['Update Orchestrator service present'] = [bool] (Get-Service UsoSvc -ErrorAction SilentlyContinue)
        $medic = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\WaaSMedicSvc' -ErrorAction SilentlyContinue
        $health['Update repair service not disabled'] = ($medic -and $medic.Start -ne 4)
        $health['Windows Update tasks present'] = [bool] ($tasks | Where-Object { $_ -like 'Microsoft\Windows\UpdateOrchestrator\*' })
        $defender = Get-Service WinDefend -ErrorAction SilentlyContinue
        $health['Defender service running'] = ($defender -and $defender.Status -eq 'Running')
        $health['Microsoft Store installed'] = [bool] (Get-AppxPackage -Name Microsoft.WindowsStore)
        $health['App Installer (winget) installed'] = [bool] (Get-AppxPackage -Name Microsoft.DesktopAppInstaller)
        $health['Windows Terminal installed'] = [bool] (Get-AppxPackage -Name Microsoft.WindowsTerminal)
        $health['WinRE enabled'] = ((& reagentc.exe /info 2>&1) -join ' ') -match 'Enabled'
        $health['Signed in with a local account'] = -not (Get-LocalUser -Name $env:USERNAME).PrincipalSource.ToString().Contains('MicrosoftAccount')

        $os = Get-CimInstance Win32_OperatingSystem
        [pscustomobject]@{
            os = "$($os.Caption) $($os.Version)"
            user = $env:USERNAME
            computer = $env:COMPUTERNAME
            checks = $results.ToArray()
            health = [pscustomobject] $health
            machineLog = (@('C:\Windows\Setup\Scripts\Plainstall\Logs', (Join-Path $env:LOCALAPPDATA 'Plainstall\Logs')) |
                Where-Object { Test-Path $_ } |
                ForEach-Object { Get-ChildItem $_ -File } |
                ForEach-Object { "== $($_.FullName)`n" + (Get-Content $_.FullName -Raw) }) -join "`n"
        }
    }

    $failedChecks = @($checks.checks | Where-Object { -not $_.ok })
    $failedHealth = @($checks.health.PSObject.Properties | Where-Object { -not $_.Value } | ForEach-Object Name)
    $summary = [pscustomobject]@{
        iso = $IsoPath
        installedOs = $checks.os
        user = $checks.user
        minutesToDesktop = $installMinutes
        tpm = (-not $NoTpm)
        actionsChecked = @($checks.checks).Count
        actionsFailed = $failedChecks.Count
        healthFailed = $failedHealth
        passed = ($failedChecks.Count -eq 0 -and $failedHealth.Count -eq 0)
        failedActions = $failedChecks
        health = $checks.health
    }
    $summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ResultFolder "vm-test-result.json") -Encoding UTF8
    $checks.machineLog | Set-Content -LiteralPath (Join-Path $ResultFolder "first-logon-logs.txt") -Encoding UTF8

    Write-Step "Installed: $($checks.os), signed in as $($checks.user)"
    Write-Step "Actions checked: $($summary.actionsChecked), failed: $($summary.actionsFailed)"
    foreach ($f in $failedChecks) { Write-Host ("   FAILED {0} {1} {2} {3}" -f $f.entry, $f.kind, $f.target, $f.detail) -ForegroundColor Red }
    foreach ($h in $checks.health.PSObject.Properties) { Write-Host ("   {0,-40} {1}" -f $h.Name, $(if ($h.Value) { 'ok' } else { 'FAILED' })) }
    Write-Step "Screenshot and result: $ResultFolder"
    if (-not $summary.passed) { throw "The VM test failed. See $ResultFolder\vm-test-result.json" }
    Write-Step "VM test passed."
}
finally {
    if (-not $KeepVm) {
        $existing = Get-VM -Name $VmName -ErrorAction SilentlyContinue
        if ($existing) {
            Write-Step "Removing the virtual machine."
            if ($existing.State -ne 'Off') { Stop-VM -Name $VmName -TurnOff -Force -ErrorAction SilentlyContinue }
            Remove-VM -Name $VmName -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path -LiteralPath $vhd) { Remove-Item -LiteralPath $vhd -Force -ErrorAction SilentlyContinue }
    } else {
        Write-Step "The virtual machine '$VmName' is kept, as requested."
    }
}
