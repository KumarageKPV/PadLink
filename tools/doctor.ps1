<#
.SYNOPSIS
    Checks the PadLink toolchain and prints a table of what is ok or missing.

.DESCRIPTION
    Checks Git, gh, the .NET 10 SDK, Flutter 3.47.x, adb, Python 3.12+ with vgamepad and
    pygame (pygame-ce), and whether ViGEmBus is the 1.22.0 driver and can create a pad
    (via tools/vigem_check.py).
    Every check is required: exits 1 if any is missing, 0 if all are ok.
    Also warns, without failing, when `python` is not the repo's .venv (README.md, "Python tools").

    Run: powershell -NoProfile -ExecutionPolicy Bypass -File tools/doctor.ps1

    Dot-sourcing the script loads its functions without running the checks
    (tools/tests/doctor.tests.ps1 does this).
#>

[CmdletBinding()]
param()

# Targets from docs/design.md (Tech stack). Bump FlutterPin when the project moves to 3.50.
$script:FlutterPin = '3.47.7'
$script:PythonMinimum = '3.12'
$script:DotnetMajor = 10
$script:PythonFix = 'winget install --id Python.Python.3.14 -e'
# The ViGEmBus 1.22.0 setup installs driver file 1.21.442.0.
$script:ViGEmSetupVersion = '1.22.0'
$script:ViGEmDriverMinimum = [version]'1.21.442.0'
# Installing vgamepad runs its bundled ViGEmBus 1.17.333 setup, which would downgrade the driver.
$script:VgamepadFix = 'python -m pip install vgamepad (cancel the ViGEmBus setup it opens)'

# Prints "<distribution> <version>"; fails if the distribution is not installed.
$script:PackageVersionCode = 'import sys, importlib.metadata as m; print(sys.argv[1], m.version(sys.argv[1]))'

function New-ProbeResult {
    # Path is the resolved command, or $null when the command is not on PATH.
    param($Path = 'tool.exe', [int]$ExitCode = 0, [string]$Output = '')
    [pscustomobject]@{ Path = $Path; ExitCode = $ExitCode; Output = $Output }
}

function New-CheckResult {
    param([string]$Name, [bool]$Ok, [string]$Found = '', [string]$Fix = '')
    [pscustomobject]@{ Name = $Name; Ok = $Ok; Found = $Found; Fix = $Fix }
}

function Invoke-Probe {
    # Runs a command from PATH and captures its exit code and combined output.
    param([string]$Command, [string[]]$Arguments = @())
    $resolved = Get-Command $Command -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $resolved) { return New-ProbeResult -Path $null -ExitCode -1 }
    # Native stderr lines must not become terminating errors.
    $ErrorActionPreference = 'Continue'
    try {
        $lines = & $resolved.Source @Arguments 2>&1 | ForEach-Object { "$_" }
        $exitCode = $LASTEXITCODE
    } catch {
        return New-ProbeResult -Path $resolved.Source -ExitCode -1 -Output $_.Exception.Message
    }
    New-ProbeResult -Path $resolved.Source -ExitCode $exitCode -Output (($lines -join "`n").Trim())
}

function Get-VersionText([string]$Text) {
    if ($Text -match '(\d+\.\d+(?:\.\d+)?)') { return $Matches[1] }
    return $null
}

function Test-Tool {
    # A tool that only needs to be on PATH and print a version.
    param([string]$Name, [string]$Fix, $Probe)
    if ($null -eq $Probe.Path) { return New-CheckResult $Name $false 'not found' $Fix }
    $version = Get-VersionText $Probe.Output
    if ($Probe.ExitCode -ne 0 -or -not $version) { return New-CheckResult $Name $false 'no version output' $Fix }
    New-CheckResult $Name $true $version
}

function Test-DotnetSdk {
    # Probe: dotnet --list-sdks
    param($Probe)
    $name = ".NET $script:DotnetMajor SDK"
    $fix = "winget install --id Microsoft.DotNet.SDK.$script:DotnetMajor -e"
    if ($null -eq $Probe.Path) { return New-CheckResult $name $false 'not found' $fix }
    $sdks = @($Probe.Output -split "`r?`n" | ForEach-Object { if ($_ -match '^(\d+\.\d+\.\d+)') { [version]$Matches[1] } })
    $wanted = @($sdks | Where-Object { $_.Major -eq $script:DotnetMajor } | Sort-Object -Descending)
    if ($wanted.Count -gt 0) { return New-CheckResult $name $true $wanted[0].ToString() }
    if ($sdks.Count -eq 0) { return New-CheckResult $name $false 'no SDKs' $fix }
    New-CheckResult $name $false ('only ' + (($sdks | ForEach-Object { $_.ToString() }) -join ', ')) $fix
}

function Test-Flutter {
    # Probe: flutter --version --machine (JSON, sometimes after progress lines)
    param($Probe)
    $minor = ($script:FlutterPin -split '\.')[0..1] -join '.'
    $name = "Flutter $minor.x"
    $installFix = "Install Flutter $script:FlutterPin (stable): https://docs.flutter.dev/install/archive"
    if ($null -eq $Probe.Path) { return New-CheckResult $name $false 'not found' $installFix }
    $info = $null
    $start = $Probe.Output.IndexOf('{')
    $end = $Probe.Output.LastIndexOf('}')
    if ($start -ge 0 -and $end -gt $start) {
        try { $info = $Probe.Output.Substring($start, $end - $start + 1) | ConvertFrom-Json } catch { $info = $null }
    }
    if (-not $info -or -not $info.frameworkVersion) { return New-CheckResult $name $false 'no version output' $installFix }
    $found = "$($info.frameworkVersion) ($($info.channel))"
    if ($info.frameworkVersion -like "$minor.*") { return New-CheckResult $name $true $found }
    New-CheckResult $name $false $found "git -C `"$($info.flutterRoot)`" checkout $script:FlutterPin"
}

function Find-SdkAdb {
    # adb.exe in an Android SDK folder, for when it is installed but not on PATH.
    foreach ($root in @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT, (Join-Path $env:LOCALAPPDATA 'Android\Sdk'))) {
        if (-not $root) { continue }
        $adb = Join-Path $root 'platform-tools\adb.exe'
        if (Test-Path -LiteralPath $adb) { return $adb }
    }
    return $null
}

function Test-Adb {
    # Probe: adb version
    param($Probe, $SdkAdbPath)
    $installFix = 'winget install --id Google.AndroidStudio -e, then SDK Manager > SDK Tools > Android SDK Platform-Tools'
    if ($null -eq $Probe.Path) {
        if ($SdkAdbPath) {
            return New-CheckResult 'adb' $false 'not on PATH' "Add $(Split-Path -Parent $SdkAdbPath) to your user PATH"
        }
        return New-CheckResult 'adb' $false 'not found' $installFix
    }
    if ($Probe.ExitCode -ne 0) { return New-CheckResult 'adb' $false 'no version output' $installFix }
    # The second line holds the platform-tools version: "Version 35.0.2-12147458".
    if ($Probe.Output -cmatch 'Version (\d+\.\d+\.\d+)') { return New-CheckResult 'adb' $true $Matches[1] }
    New-CheckResult 'adb' $true (Get-VersionText $Probe.Output)
}

function Test-Python {
    # Probe: python --version
    param($Probe)
    $name = "Python $script:PythonMinimum+"
    if ($null -eq $Probe.Path) { return New-CheckResult $name $false 'not found' $script:PythonFix }
    if ($Probe.ExitCode -ne 0) {
        # The App Execution Alias stub exits 9009 and offers the Microsoft Store.
        $found = if ($Probe.Path -like '*\WindowsApps\*') { 'Microsoft Store stub' } else { 'no version output' }
        return New-CheckResult $name $false $found $script:PythonFix
    }
    $version = Get-VersionText $Probe.Output
    if (-not $version) { return New-CheckResult $name $false 'no version output' $script:PythonFix }
    if ([version]$version -ge [version]$script:PythonMinimum) { return New-CheckResult $name $true $version }
    New-CheckResult $name $false "$version (need $script:PythonMinimum+)" $script:PythonFix
}

function Find-VenvPython {
    # python.exe in the repo-root .venv, or $null when there is no .venv.
    $python = Join-Path (Split-Path -Parent $PSScriptRoot) '.venv\Scripts\python.exe'
    if (Test-Path -LiteralPath $python) { return $python }
    return $null
}

function Get-VenvWarning {
    # Returns a warning when `python` is not the repo's .venv, or $null.
    # PythonPath: the resolved python command, $null when it is not on PATH.
    # VenvPython: Find-VenvPython, $null when the repo has no .venv.
    param($PythonPath, $VenvPython)
    if (-not $VenvPython) { return 'the repo has no .venv. Make one with the steps in README.md, "Python tools", then activate it.' }
    if ($PythonPath -and [IO.Path]::GetFullPath($PythonPath) -eq [IO.Path]::GetFullPath($VenvPython)) { return $null }
    $found = if ($PythonPath) { $PythonPath } else { 'not on PATH' }
    "python is not the repo's .venv ($found). Activate it: .venv\Scripts\Activate.ps1, or in Git Bash: source .venv/Scripts/activate"
}

function Test-PythonPackage {
    # Probe: Invoke-PackageProbe
    param([string]$Name, [string]$Fix, [bool]$PythonOk, $Probe)
    if (-not $PythonOk) { return New-CheckResult $Name $false 'not checked: needs Python' $Fix }
    if ($Probe.ExitCode -ne 0 -or -not $Probe.Output) { return New-CheckResult $Name $false 'not installed' $Fix }
    New-CheckResult $Name $true (($Probe.Output -split "`n")[-1].Trim())
}

function Invoke-PackageProbe([string[]]$Distributions) {
    # Tries each distribution name in turn (pygame-ce installs the same `pygame` module).
    foreach ($distribution in $Distributions) {
        $probe = Invoke-Probe 'python' @('-c', $script:PackageVersionCode, $distribution)
        if ($probe.ExitCode -eq 0) { return $probe }
    }
    $probe
}

function Get-ViGEmServiceStatus {
    $service = Get-Service -Name 'ViGEmBus' -ErrorAction SilentlyContinue
    if ($service) { return "$($service.Status)" }
    return $null
}

function Get-DriverFileVersion([string]$Path) {
    # Built from the numeric parts: FileVersion strings can carry text after the number.
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $info = (Get-Item -LiteralPath $Path).VersionInfo
    [version]::new($info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart, $info.FilePrivatePart)
}

function Test-ViGEmBus {
    # Probe: python tools/vigem_check.py (exit 0 ok, 2 no vgamepad, 3 driver failed)
    # DriverVersion: Get-DriverFileVersion of ViGEmBus.sys, $null when the file is missing.
    param([bool]$PythonOk, $ServiceStatus, [version]$DriverVersion, $Probe)
    $driverFix = 'winget install --id ViGEm.ViGEmBus -e (as admin)'
    $reinstallFix = 'winget install --id ViGEm.ViGEmBus -e --force (as admin)'
    $service = if ($ServiceStatus) { "service $ServiceStatus" } else { 'service not installed' }
    # An older driver still creates pads, so the version is checked before the pad test.
    if ($DriverVersion -and $DriverVersion -lt $script:ViGEmDriverMinimum) {
        $found = "driver $DriverVersion; setup $script:ViGEmSetupVersion has $script:ViGEmDriverMinimum"
        return New-CheckResult 'ViGEmBus' $false $found $reinstallFix
    }
    if (-not $PythonOk) {
        $fix = if ($ServiceStatus) { $script:PythonFix } else { $driverFix }
        return New-CheckResult 'ViGEmBus' $false "not tested: needs Python ($service)" $fix
    }
    if ($Probe.ExitCode -eq 0) {
        if (-not $DriverVersion) { return New-CheckResult 'ViGEmBus' $false 'pad created, but ViGEmBus.sys not found' $reinstallFix }
        return New-CheckResult 'ViGEmBus' $true "pad created and removed (driver $DriverVersion)"
    }
    if ($Probe.ExitCode -eq 2) {
        $fix = if ($ServiceStatus) { $script:VgamepadFix } else { $driverFix }
        return New-CheckResult 'ViGEmBus' $false "not tested: needs vgamepad ($service)" $fix
    }
    New-CheckResult 'ViGEmBus' $false "pad test failed ($service)" $driverFix
}

function Format-DoctorTable([object[]]$Results) {
    $columns = 'Name', 'Status', 'Found', 'Fix'
    $header = [pscustomobject]@{ Name = 'Check'; Status = 'Status'; Found = 'Found'; Fix = 'Fix' }
    $rows = @(foreach ($result in $Results) {
            [pscustomobject]@{
                Name   = $result.Name
                Status = $(if ($result.Ok) { 'ok' } else { 'missing' })
                Found  = $result.Found
                Fix    = $result.Fix
            }
        })
    $widths = @{}
    foreach ($column in $columns) {
        $widths[$column] = (@($header) + $rows | ForEach-Object { "$($_.$column)".Length } | Measure-Object -Maximum).Maximum
    }
    $rule = [pscustomobject]@{}
    foreach ($column in $columns) { $rule | Add-Member -NotePropertyName $column -NotePropertyValue ('-' * $widths[$column]) }
    $format = "{0,-$($widths['Name'])}  {1,-$($widths['Status'])}  {2,-$($widths['Found'])}  {3}"
    foreach ($row in @($header, $rule) + $rows) {
        ($format -f $row.Name, $row.Status, $row.Found, $row.Fix).TrimEnd()
    }
}

function Get-DoctorExitCode([object[]]$Results) {
    if (@($Results | Where-Object { -not $_.Ok }).Count -gt 0) { return 1 }
    return 0
}

function Invoke-Doctor {
    $pythonProbe = Invoke-Probe 'python' @('--version')
    $python = Test-Python $pythonProbe
    $venvWarning = Get-VenvWarning -PythonPath $pythonProbe.Path -VenvPython (Find-VenvPython)
    $vgamepad = $null
    $pygame = $null
    $vigem = $null
    if ($python.Ok) {
        $vgamepad = Invoke-PackageProbe @('vgamepad')
        $pygame = Invoke-PackageProbe @('pygame-ce', 'pygame')
        $vigem = Invoke-Probe 'python' @((Join-Path $PSScriptRoot 'vigem_check.py'))
    }
    $results = @(
        (Test-Tool -Name 'Git' -Fix 'winget install --id Git.Git -e' -Probe (Invoke-Probe 'git' @('--version'))),
        (Test-Tool -Name 'gh' -Fix 'winget install --id GitHub.cli -e' -Probe (Invoke-Probe 'gh' @('--version'))),
        (Test-DotnetSdk -Probe (Invoke-Probe 'dotnet' @('--list-sdks'))),
        (Test-Flutter -Probe (Invoke-Probe 'flutter' @('--version', '--machine'))),
        (Test-Adb -Probe (Invoke-Probe 'adb' @('version')) -SdkAdbPath (Find-SdkAdb)),
        $python,
        (Test-PythonPackage -Name 'vgamepad' -Fix $script:VgamepadFix -PythonOk $python.Ok -Probe $vgamepad),
        (Test-PythonPackage -Name 'pygame' -Fix 'python -m pip install pygame-ce' -PythonOk $python.Ok -Probe $pygame),
        (Test-ViGEmBus -PythonOk $python.Ok -ServiceStatus (Get-ViGEmServiceStatus) `
                -DriverVersion (Get-DriverFileVersion (Join-Path $env:SystemRoot 'System32\drivers\ViGEmBus.sys')) -Probe $vigem)
    )

    Write-Host 'PadLink toolchain doctor'
    Write-Host ''
    Format-DoctorTable $results | ForEach-Object { Write-Host $_ }
    Write-Host ''
    if ($venvWarning) { Write-Host "Warning: $venvWarning" }
    $missing = @($results | Where-Object { -not $_.Ok }).Count
    if ($missing -eq 0) {
        Write-Host "All $($results.Count) checks ok."
    } else {
        Write-Host "$missing of $($results.Count) checks missing. Run each Fix, then re-run tools/doctor.ps1."
    }
    Get-DoctorExitCode $results
}

if ($MyInvocation.InvocationName -ne '.') {
    exit (Invoke-Doctor)
}
