<#
Tests for tools/doctor.ps1. No framework needed; runs on Windows PowerShell 5.1 and pwsh 7.
Run: powershell -NoProfile -ExecutionPolicy Bypass -File tools/tests/doctor.tests.ps1
Exits 1 if any case fails.
#>

$ErrorActionPreference = 'Stop'
$DoctorPath = Join-Path $PSScriptRoot '..\doctor.ps1'
. $DoctorPath

$script:Passed = 0
$script:Failed = 0

function Test-Case([string]$Name, [scriptblock]$Body) {
    try {
        & $Body
        $script:Passed++
        Write-Host "PASS  $Name"
    } catch {
        $script:Failed++
        Write-Host "FAIL  $Name -- $($_.Exception.Message)"
    }
}

function Assert-Equal($Expected, $Actual, [string]$What = 'value') {
    if ($Expected -ne $Actual) { throw "${What}: expected '$Expected', got '$Actual'" }
}

function Assert-True($Condition, [string]$What) {
    if (-not $Condition) { throw $What }
}

$Lines = { param([string[]]$Text) $Text -join [Environment]::NewLine }

# --- Version parsing and plain tools ---------------------------------------

Test-Case 'Get-VersionText takes the first dotted version' {
    Assert-Equal '2.56.0' (Get-VersionText 'git version 2.56.0.windows.1')
    Assert-Equal $null (Get-VersionText 'no digits here')
}

Test-Case 'Git found reports its version and no fix' {
    $r = Test-Tool -Name 'Git' -Fix 'winget install --id Git.Git -e' -Probe (New-ProbeResult -Output 'git version 2.56.0.windows.1')
    Assert-Equal 'Git' $r.Name 'name'
    Assert-True $r.Ok 'should be ok'
    Assert-Equal '2.56.0' $r.Found 'found'
    Assert-Equal '' $r.Fix 'fix'
}

Test-Case 'Git not on PATH is missing with its winget fix' {
    $r = Test-Tool -Name 'Git' -Fix 'winget install --id Git.Git -e' -Probe (New-ProbeResult -Path $null)
    Assert-True (-not $r.Ok) 'should be missing'
    Assert-Equal 'not found' $r.Found 'found'
    Assert-Equal 'winget install --id Git.Git -e' $r.Fix 'fix'
}

Test-Case 'gh multi-line version output is parsed' {
    $out = & $Lines 'gh version 2.102.0 (2026-09-30)', 'https://github.com/cli/cli/releases/tag/v2.102.0'
    $r = Test-Tool -Name 'gh' -Fix 'x' -Probe (New-ProbeResult -Output $out)
    Assert-True $r.Ok 'should be ok'
    Assert-Equal '2.102.0' $r.Found 'found'
}

Test-Case 'A tool that exits non-zero is missing' {
    $r = Test-Tool -Name 'gh' -Fix 'x' -Probe (New-ProbeResult -Output 'boom' -ExitCode 1)
    Assert-True (-not $r.Ok) 'should be missing'
}

# --- .NET SDK ----------------------------------------------------------------

Test-Case '.NET picks the newest 10.x SDK' {
    $out = & $Lines '8.0.100 [C:\Program Files\dotnet\sdk]', '10.0.401 [C:\Program Files\dotnet\sdk]', '10.0.100 [C:\Program Files\dotnet\sdk]'
    $r = Test-DotnetSdk -Probe (New-ProbeResult -Output $out)
    Assert-True $r.Ok 'should be ok'
    Assert-Equal '10.0.401' $r.Found 'found'
}

Test-Case '.NET without a 10.x SDK is missing and lists what it found' {
    $out = & $Lines '8.0.100 [C:\Program Files\dotnet\sdk]', '9.0.300 [C:\Program Files\dotnet\sdk]'
    $r = Test-DotnetSdk -Probe (New-ProbeResult -Output $out)
    Assert-True (-not $r.Ok) 'should be missing'
    Assert-True ($r.Found -like '*9.0.300*') "found should list SDKs, got '$($r.Found)'"
    Assert-Equal 'winget install --id Microsoft.DotNet.SDK.10 -e' $r.Fix 'fix'
}

Test-Case '.NET runtime only (no SDKs) is missing' {
    $r = Test-DotnetSdk -Probe (New-ProbeResult -Output '')
    Assert-True (-not $r.Ok) 'should be missing'
    Assert-Equal 'no SDKs' $r.Found 'found'
}

# --- Flutter -----------------------------------------------------------------

function New-FlutterJson([string]$Version, [string]$Root = 'E:\\sdk\\flutter') {
    @"
{
  "frameworkVersion": "$Version",
  "channel": "stable",
  "flutterRoot": "$Root"
}
"@
}

Test-Case 'Flutter 3.47.x is ok even with noise before the JSON' {
    $out = "Downloading Dart SDK...`n" + (New-FlutterJson '3.47.7')
    $r = Test-Flutter -Probe (New-ProbeResult -Output $out)
    Assert-True $r.Ok 'should be ok'
    Assert-Equal '3.47.7 (stable)' $r.Found 'found'
}

Test-Case 'Flutter on another minor is missing with a checkout fix' {
    $r = Test-Flutter -Probe (New-ProbeResult -Output (New-FlutterJson '3.50.0'))
    Assert-True (-not $r.Ok) 'should be missing'
    Assert-Equal '3.50.0 (stable)' $r.Found 'found'
    Assert-Equal 'git -C "E:\sdk\flutter" checkout 3.47.7' $r.Fix 'fix'
}

Test-Case 'Flutter with unreadable output is missing' {
    $r = Test-Flutter -Probe (New-ProbeResult -Output 'something went wrong')
    Assert-True (-not $r.Ok) 'should be missing'
    Assert-Equal 'no version output' $r.Found 'found'
}

Test-Case 'Flutter not installed points at the install page' {
    $r = Test-Flutter -Probe (New-ProbeResult -Path $null)
    Assert-True (-not $r.Ok) 'should be missing'
    Assert-True ($r.Fix -like '*docs.flutter.dev*') "fix should link the docs, got '$($r.Fix)'"
}

# --- adb ---------------------------------------------------------------------

Test-Case 'adb on PATH reports the platform-tools version' {
    $out = & $Lines 'Android Debug Bridge version 1.0.41', 'Version 35.0.2-12147458', 'Installed as C:\sdk\platform-tools\adb.exe'
    $r = Test-Adb -Probe (New-ProbeResult -Output $out) -SdkAdbPath $null
    Assert-True $r.Ok 'should be ok'
    Assert-Equal '35.0.2' $r.Found 'found'
}

Test-Case 'adb only in the Android SDK says to add it to PATH' {
    $r = Test-Adb -Probe (New-ProbeResult -Path $null) -SdkAdbPath 'C:\sdk\platform-tools\adb.exe'
    Assert-True (-not $r.Ok) 'should be missing'
    Assert-Equal 'not on PATH' $r.Found 'found'
    Assert-Equal 'Add C:\sdk\platform-tools to your user PATH' $r.Fix 'fix'
}

Test-Case 'adb nowhere points at Android Studio' {
    $r = Test-Adb -Probe (New-ProbeResult -Path $null) -SdkAdbPath $null
    Assert-True (-not $r.Ok) 'should be missing'
    Assert-True ($r.Fix -like 'winget install --id Google.AndroidStudio -e*') "fix: '$($r.Fix)'"
}

# --- Python ------------------------------------------------------------------

Test-Case 'Python 3.12 and 3.14 are ok' {
    Assert-True (Test-Python -Probe (New-ProbeResult -Output 'Python 3.12.0')).Ok '3.12.0 should be ok'
    $r = Test-Python -Probe (New-ProbeResult -Output 'Python 3.14.8')
    Assert-True $r.Ok '3.14.8 should be ok'
    Assert-Equal '3.14.8' $r.Found 'found'
}

Test-Case 'Python 3.11 is missing' {
    $r = Test-Python -Probe (New-ProbeResult -Output 'Python 3.11.9')
    Assert-True (-not $r.Ok) 'should be missing'
    Assert-Equal '3.11.9 (need 3.12+)' $r.Found 'found'
    Assert-Equal 'winget install --id Python.Python.3.14 -e' $r.Fix 'fix'
}

Test-Case 'The Microsoft Store python stub is missing' {
    $stub = 'C:\Users\me\AppData\Local\Microsoft\WindowsApps\python.exe'
    $r = Test-Python -Probe (New-ProbeResult -Path $stub -ExitCode 9009 -Output 'Python was not found; run without arguments to install from the Microsoft Store')
    Assert-True (-not $r.Ok) 'should be missing'
    Assert-Equal 'Microsoft Store stub' $r.Found 'found'
}

# --- Python packages -----------------------------------------------------------

Test-Case 'An installed package reports its distribution and version' {
    $r = Test-PythonPackage -Name 'pygame' -Fix 'python -m pip install pygame-ce' -PythonOk $true -Probe (New-ProbeResult -Output 'pygame-ce 2.5.8')
    Assert-True $r.Ok 'should be ok'
    Assert-Equal 'pygame-ce 2.5.8' $r.Found 'found'
}

Test-Case 'A missing package gets its pip fix' {
    $r = Test-PythonPackage -Name 'vgamepad' -Fix 'python -m pip install vgamepad' -PythonOk $true -Probe (New-ProbeResult -ExitCode 1 -Output 'PackageNotFoundError: vgamepad')
    Assert-True (-not $r.Ok) 'should be missing'
    Assert-Equal 'not installed' $r.Found 'found'
    Assert-Equal 'python -m pip install vgamepad' $r.Fix 'fix'
}

Test-Case 'Packages are not checked without a working Python' {
    $r = Test-PythonPackage -Name 'vgamepad' -Fix 'python -m pip install vgamepad' -PythonOk $false -Probe $null
    Assert-True (-not $r.Ok) 'should be missing'
    Assert-Equal 'not checked: needs Python' $r.Found 'found'
}

# --- Python .venv --------------------------------------------------------------

$VenvPython = 'E:\repo\.venv\Scripts\python.exe'

Test-Case 'No venv warning when python is the .venv one, whatever the case and slashes' {
    Assert-Equal $null (Get-VenvWarning -PythonPath $VenvPython -VenvPython $VenvPython) 'same path'
    Assert-Equal $null (Get-VenvWarning -PythonPath 'e:/repo/.venv/Scripts/python.exe' -VenvPython $VenvPython) 'other spelling'
}

Test-Case 'A system python warns with its path and how to activate the .venv' {
    $w = Get-VenvWarning -PythonPath 'C:\Python314\python.exe' -VenvPython $VenvPython
    Assert-True ($w -like "*not the repo's .venv (C:\Python314\python.exe)*") "warning: '$w'"
    Assert-True ($w -like '*.venv\Scripts\Activate.ps1*') "should say how to activate: '$w'"
    Assert-True ($w -like '*source .venv/Scripts/activate*') "should cover Git Bash: '$w'"
}

Test-Case 'python not on PATH warns too' {
    $w = Get-VenvWarning -PythonPath $null -VenvPython $VenvPython
    Assert-True ($w -like "*not the repo's .venv (not on PATH)*") "warning: '$w'"
}

Test-Case 'A repo without a .venv warns to make one' {
    $w = Get-VenvWarning -PythonPath 'C:\Python314\python.exe' -VenvPython $null
    Assert-True ($w -like '*no .venv*README.md, "Python tools"*') "warning: '$w'"
}

Test-Case "Find-VenvPython looks in the repo root's .venv" {
    $expected = Join-Path $PSScriptRoot '..\..\.venv\Scripts\python.exe'
    if (Test-Path -LiteralPath $expected) {
        Assert-Equal ([IO.Path]::GetFullPath($expected)) ([IO.Path]::GetFullPath((Find-VenvPython))) 'path'
    } else {
        Assert-Equal $null (Find-VenvPython) 'no .venv'
    }
}

# --- ViGEmBus ----------------------------------------------------------------

$PadOk = { New-ProbeResult -Output 'ok: created and removed one virtual Xbox 360 pad' }

Test-Case 'ViGEmBus is ok when vigem_check creates a pad on the 1.22.0 driver' {
    $r = Test-ViGEmBus -PythonOk $true -ServiceStatus 'Running' -DriverVersion ([version]'1.21.442.0') -Probe (& $PadOk)
    Assert-True $r.Ok 'should be ok'
    Assert-Equal 'pad created and removed (driver 1.21.442.0)' $r.Found 'found'
}

Test-Case 'ViGEmBus driver older than setup 1.22.0 is missing even when a pad works' {
    $r = Test-ViGEmBus -PythonOk $true -ServiceStatus 'Running' -DriverVersion ([version]'1.17.333.0') -Probe (& $PadOk)
    Assert-True (-not $r.Ok) 'should be missing'
    Assert-Equal 'driver 1.17.333.0; setup 1.22.0 has 1.21.442.0' $r.Found 'found'
    Assert-Equal 'winget install --id ViGEm.ViGEmBus -e --force (as admin)' $r.Fix 'fix'
    $noPython = Test-ViGEmBus -PythonOk $false -ServiceStatus 'Running' -DriverVersion ([version]'1.17.333.0') -Probe $null
    Assert-Equal $r.Found $noPython.Found 'found without Python'
}

Test-Case 'ViGEmBus pad without a driver file to version is missing' {
    $r = Test-ViGEmBus -PythonOk $true -ServiceStatus 'Running' -DriverVersion $null -Probe (& $PadOk)
    Assert-True (-not $r.Ok) 'should be missing'
    Assert-Equal 'pad created, but ViGEmBus.sys not found' $r.Found 'found'
}

Test-Case 'ViGEmBus is not tested without vgamepad' {
    $r = Test-ViGEmBus -PythonOk $true -ServiceStatus 'Running' -DriverVersion ([version]'1.21.442.0') -Probe (New-ProbeResult -ExitCode 2 -Output 'vgamepad is not installed')
    Assert-True (-not $r.Ok) 'should be missing'
    Assert-Equal 'not tested: needs vgamepad (service Running)' $r.Found 'found'
    Assert-Equal $script:VgamepadFix $r.Fix 'fix'
}

Test-Case 'Get-DriverFileVersion reads numeric parts and returns null for a missing file' {
    # cmd.exe's FileVersion string carries text like "(WinBuild.160101.0800)" after the number.
    $v = Get-DriverFileVersion (Join-Path $env:SystemRoot 'System32\cmd.exe')
    Assert-True ($v -is [version]) "should be a [version], got '$v'"
    Assert-Equal 10 $v.Major 'major'
    Assert-Equal $null (Get-DriverFileVersion (Join-Path $env:SystemRoot 'System32\drivers\padlink-no-such.sys')) 'missing file'
}

Test-Case 'ViGEmBus pad failure points at the driver installer' {
    $r = Test-ViGEmBus -PythonOk $true -ServiceStatus $null -Probe (New-ProbeResult -ExitCode 3 -Output 'ViGEmBus could not create a pad: VIGEM_ERROR_BUS_NOT_FOUND')
    Assert-True (-not $r.Ok) 'should be missing'
    Assert-Equal 'pad test failed (service not installed)' $r.Found 'found'
    Assert-Equal 'winget install --id ViGEm.ViGEmBus -e (as admin)' $r.Fix 'fix'
}

Test-Case 'ViGEmBus is not tested without Python' {
    $r = Test-ViGEmBus -PythonOk $false -ServiceStatus 'Running' -Probe $null
    Assert-True (-not $r.Ok) 'should be missing'
    Assert-Equal 'not tested: needs Python (service Running)' $r.Found 'found'
}

# --- Table and exit code -----------------------------------------------------

Test-Case 'Table aligns columns and shows a fix only on missing rows' {
    $results = @(
        (New-CheckResult -Name 'Git' -Ok $true -Found '2.56.0'),
        (New-CheckResult -Name 'adb' -Ok $false -Found 'not found' -Fix 'install adb')
    )
    $table = @(Format-DoctorTable $results)
    Assert-Equal 4 $table.Count 'line count (header, rule, 2 rows)'
    Assert-True ($table[0] -match '^Check\s+Status\s+Found\s+Fix$') "header: '$($table[0])'"
    $statusColumn = $table[0].IndexOf('Status')
    Assert-Equal 'ok' $table[2].Substring($statusColumn, 2) 'ok row status column'
    Assert-Equal 'missing' $table[3].Substring($statusColumn, 7) 'missing row status column'
    Assert-True ($table[2] -match '2\.56\.0$') "ok row should end at Found: '$($table[2])'"
    Assert-True ($table[3] -match 'install adb$') "missing row should end with its fix: '$($table[3])'"
}

Test-Case 'Exit code is 0 when all ok and 1 when anything is missing' {
    $ok = New-CheckResult -Name 'Git' -Ok $true -Found '2.56.0'
    $missing = New-CheckResult -Name 'adb' -Ok $false -Found 'not found' -Fix 'x'
    Assert-Equal 0 (Get-DoctorExitCode @($ok, $ok)) 'all ok'
    Assert-Equal 1 (Get-DoctorExitCode @($ok, $missing)) 'one missing'
}

# --- Probing real commands ---------------------------------------------------

Test-Case 'Invoke-Probe captures output and exit code' {
    $p = Invoke-Probe 'cmd' @('/c', 'echo', 'hello')
    Assert-Equal 0 $p.ExitCode 'exit code'
    Assert-Equal 'hello' $p.Output 'output'
    Assert-Equal 3 (Invoke-Probe 'cmd' @('/c', 'exit', '3')).ExitCode 'exit code 3'
}

Test-Case 'Invoke-Probe reports a command that is not on PATH' {
    $p = Invoke-Probe 'padlink-no-such-tool' @('--version')
    Assert-Equal $null $p.Path 'path'
}

Test-Case 'doctor.ps1 exits 1 and lists Git as missing when PATH is empty' {
    $shell = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    $savedPath = $env:PATH
    try {
        $env:PATH = Join-Path $env:SystemRoot 'System32'
        $out = & $shell -NoProfile -ExecutionPolicy Bypass -File $DoctorPath 2>&1 | ForEach-Object { "$_" }
        $code = $LASTEXITCODE
    } finally {
        $env:PATH = $savedPath
    }
    $text = $out -join "`n"
    Assert-Equal 1 $code 'exit code'
    Assert-True ($text -match '(?m)^Git\s+missing\s+not found\s+winget install --id Git\.Git -e') "Git row not found in:`n$text"
    # pip install vgamepad opens vgamepad's bundled ViGEmBus 1.17 setup, which downgrades the driver.
    Assert-True ($text -match ('(?m)^vgamepad\s+missing\s+not checked: needs Python\s+' + [regex]::Escape($script:VgamepadFix))) "vgamepad row not found in:`n$text"
    Assert-True ($script:VgamepadFix -like '*cancel*') "vgamepad fix should say to cancel the old setup: '$script:VgamepadFix'"
    Assert-True ($text -match "(?m)^Warning: (python is not the repo's \.venv \(not on PATH\)|the repo has no \.venv)") "venv warning not found in:`n$text"
    Assert-True ($text -match 'checks missing') "summary line not found in:`n$text"
}

Write-Host ''
Write-Host "$script:Passed passed, $script:Failed failed"
if ($script:Failed -gt 0) { exit 1 }
exit 0
