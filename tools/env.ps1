# ============================================================================
# tools/env.ps1 - BiuBiuOrigin environment probe
# ============================================================================
# *** DO NOT "OPTIMIZE" THIS FILE INTO PLAIN-TEXT CHINESE ***
#   This script is intentionally pure-ASCII source: all Chinese text is built
#   from Unicode code points in the $script:TEXTS table below, and output is
#   written as explicit UTF-8 bytes. Reason: the PS 5.1 no-BOM / GBK parsing
#   trap documented in ENCODING NOTE below. This is a deliberate design, not
#   laziness and not a legacy artifact waiting to be refactored.
#
#   If someone "simplifies" it back to raw UTF-8 Chinese literals, the script
#   WILL fail to parse on PowerShell 5.1: a trailing byte of a UTF-8 Chinese
#   character is treated as half of a GBK character and swallows the newline
#   that follows it, merging lines and unbalancing quotes, which surfaces as
#   errors like "Unexpected token '}'".
#
#   To change user-facing Chinese text, edit the $script:TEXTS table (as code
#   points). Never paste plain-text Chinese into the script body or comments.
#   DO NOT "OPTIMIZE" THIS FILE INTO PLAIN-TEXT CHINESE - it will break parsing.
#
# ENCODING NOTE:
#   PowerShell 5.1 reads .ps1 files with the ANSI code page (GBK on this
#   machine) when the file has no BOM. Raw UTF-8 Chinese in a BOM-less script
#   gets mangled at parse time (some byte pairs even swallow the following
#   newline, which turns into a parse error). Therefore this file is
#   intentionally pure ASCII: all Chinese text lives in the $script:TEXTS table
#   below as Unicode code points, and output is emitted as explicit UTF-8 bytes.
#   That keeps the file UTF-8-without-BOM safe AND the console output correct.
#
#   All hard-coded paths are THIS MACHINE's defaults. Override with:
#     GODOT_BIN            Godot executable (default E:\Godot\...win64_console.exe)
#     GODOT_HOME           Godot install dir (default E:\Godot)
#     DOTNET_NUGET_SOURCE  offline NuGet source (default E:\Godot\GodotSharp\Tools\nupkgs)
#     CMAKE_BIN            cmake.exe
#     NINJA_BIN            ninja.exe
#     VCPKG_ROOT           vcpkg root (default D:\vcpkg)
#     DSH_PYTHON           python.exe with openpyxl (default DSH bundled runtime)
#
# Usage:
#   .\tools\env.ps1            summary tables
#   .\tools\env.ps1 -Json      machine readable JSON (single line)
#   .\tools\env.ps1 -Verify    per-item PASS/FAIL, exit code 1 if any FAIL
#
# Runtime: Windows PowerShell 5.1 (no PS7-only syntax is used)
# ============================================================================

[CmdletBinding()]
param(
    [switch]$Json,
    [switch]$Verify
)

$script:TEXTS = @{
    'title' = @(0x0042,0x0069,0x0075,0x0042,0x0069,0x0075,0x004F,0x0072,0x0069,0x0067,0x0069,0x006E,0x0020,0x73AF,0x5883,0x63A2,0x6D4B)
    'project-root' = @(0x9879,0x76EE,0x6839)
    'sec-toolchain' = @(0x5DE5,0x5177,0x94FE)
    'sec-python' = @(0x0050,0x0079,0x0074,0x0068,0x006F,0x006E,0x0020,0x002F,0x0020,0x6570,0x636E,0x5904,0x7406)
    'sec-project' = @(0x9879,0x76EE,0x4E0E,0x76EE,0x5F55)
    'sec-warn' = @(0x8B66,0x544A)
    'h-godot' = @(0x0047,0x006F,0x0064,0x006F,0x0074)
    'h-editor' = @(0x7F16,0x8F91,0x5668,0x7248)
    'h-dotnet' = @(0x0064,0x006F,0x0074,0x006E,0x0065,0x0074)
    'h-sdks' = @(0x0053,0x0044,0x004B,0x0020,0x5217,0x8868)
    'h-nuget' = @(0x79BB,0x7EBF,0x0020,0x004E,0x0075,0x0047,0x0065,0x0074,0x0020,0x6E90)
    'h-nupkgs' = @(0x53EF,0x7528,0x5305)
    'h-cmake' = @(0x0063,0x006D,0x0061,0x006B,0x0065)
    'h-ninja' = @(0x006E,0x0069,0x006E,0x006A,0x0061)
    'h-vcpkg' = @(0x0076,0x0063,0x0070,0x006B,0x0067)
    'h-git' = @(0x0067,0x0069,0x0074)
    'h-python' = @(0x0044,0x0053,0x0048,0x0020,0x0070,0x0079,0x0074,0x0068,0x006F,0x006E)
    'h-openpyxl' = @(0x006F,0x0070,0x0065,0x006E,0x0070,0x0079,0x0078,0x006C)
    'h-xlsxwriter' = @(0x0058,0x006C,0x0073,0x0078,0x0057,0x0072,0x0069,0x0074,0x0065,0x0072)
    'h-name' = @(0x0063,0x006F,0x006E,0x0066,0x0069,0x0067,0x002F,0x006E,0x0061,0x006D,0x0065)
    'h-asm' = @(0x0061,0x0073,0x0073,0x0065,0x006D,0x0062,0x006C,0x0079,0x005F,0x006E,0x0061,0x006D,0x0065)
    'h-csproj' = @(0x0063,0x0073,0x0070,0x0072,0x006F,0x006A)
    'h-native' = @(0x006E,0x0061,0x0074,0x0069,0x0076,0x0065,0x002F)
    'h-dataexcel' = @(0x0064,0x0061,0x0074,0x0061,0x002F,0x0065,0x0078,0x0063,0x0065,0x006C)
    'h-datagen' = @(0x0064,0x0061,0x0074,0x0061,0x002F,0x0067,0x0065,0x006E,0x0065,0x0072,0x0061,0x0074,0x0065,0x0064)
    'h-arch' = @(0x0041,0x0052,0x0043,0x0048,0x0049,0x0054,0x0045,0x0043,0x0054,0x0055,0x0052,0x0045,0x002E,0x006D,0x0064,0x0020,0x6700,0x540E,0x66F4,0x65B0)
    'h-rules' = @(0x0064,0x006F,0x0063,0x0073,0x002F,0x0072,0x0075,0x006C,0x0065,0x0073,0x002F,0x0020,0x0030,0x0031,0x007E,0x0030,0x0037)
    'rules-full' = @(0x9F50,0x5168)
    'rules-lack' = @(0x7F3A)
    'rules-none' = @(0x5168,0x90E8,0x7F3A,0x5931)
    'warn-rules-missing' = @(0x89C4,0x5219,0x6587,0x6863,0x7F3A,0x5931,0x0020,0x007B,0x0030,0x007D,0x0020,0x4EFD,0xFF1A,0x007B,0x0031,0x007D,0xFF08,0x89C4,0x77E9,0x4F53,0x7CFB,0x4E0D,0x5B8C,0x6574,0xFF0C,0x5F00,0x5DE5,0x524D,0x8BF7,0x8865,0x9F50,0xFF09)
    'present' = @(0x5B58,0x5728)
    'missing' = @(0x7F3A,0x5931)
    'absent' = @(0x672A,0x521B,0x5EFA,0xFF08,0x5F85,0x843D,0x5730,0xFF09)
    'items' = @(0x9879)
    'pkg-count' = @(0x4E2A,0x5305)
    'no-stamp' = @(0x65E0,0x65F6,0x95F4,0x6233)
    'passed' = @(0x9879,0x901A,0x8FC7)
    'summary' = @(0x6C47,0x603B)
    'pending-label' = @(0x5F85,0x843D,0x5730)
    'hint' = @(0x63D0,0x793A,0xFF1A,0x8BE6,0x7EC6,0x9010,0x9879,0x8BF7,0x7528,0x0020,0x002E,0x005C,0x0074,0x006F,0x006F,0x006C,0x0073,0x005C,0x0065,0x006E,0x0076,0x002E,0x0070,0x0073,0x0031,0x0020,0x002D,0x0056,0x0065,0x0072,0x0069,0x0066,0x0079,0xFF1B,0x673A,0x5668,0x53EF,0x8BFB,0x8BF7,0x7528,0x0020,0x002D,0x004A,0x0073,0x006F,0x006E,0x3002)
    'verify-pass' = @(0x5168,0x90E8,0x901A,0x8FC7,0xFF1A,0x73AF,0x5883,0x5C31,0x7EEA,0x3002)
    'verify-pend' = @(0x0050,0x0045,0x004E,0x0044,0x0020,0x8868,0x793A,0x300C,0x5DE5,0x7A0B,0x9AA8,0x67B6,0x5C1A,0x672A,0x843D,0x5730,0x300D,0xFF08,0x0063,0x0073,0x0070,0x0072,0x006F,0x006A,0x0020,0x002F,0x0020,0x006E,0x0061,0x0074,0x0069,0x0076,0x0065,0x0020,0x002F,0x0020,0x0064,0x0061,0x0074,0x0061,0x0020,0x76EE,0x5F55,0xFF09,0xFF0C,0x4E0D,0x5F71,0x54CD,0x73AF,0x5883,0x5224,0x5B9A,0xFF0C,0x968F,0x5F00,0x53D1,0x63A8,0x8FDB,0x81EA,0x7136,0x51FA,0x73B0,0x3002)
    'verify-fail' = @(0x68C0,0x6D4B,0x5931,0x8D25,0x0020,0x007B,0x0030,0x007D,0x0020,0x9879,0xFF0C,0x8BF7,0x5148,0x4FEE,0x590D,0x518D,0x5F00,0x5DE5,0x3002)
    'warn-vcpkg-godotcpp' = @(0x0076,0x0063,0x0070,0x006B,0x0067,0x0020,0x7684,0x0020,0x0067,0x006F,0x0064,0x006F,0x0074,0x002D,0x0063,0x0070,0x0070,0x0020,0x0070,0x006F,0x0072,0x0074,0x0020,0x7248,0x672C,0x4E3A,0x0020,0x007B,0x0030,0x007D,0xFF0C,0x4E0E,0x5F15,0x64CE,0x0020,0x0034,0x002E,0x0037,0x002E,0x0032,0x0020,0x4E0D,0x5339,0x914D,0xFF1B,0x0047,0x0044,0x0045,0x0078,0x0074,0x0065,0x006E,0x0073,0x0069,0x006F,0x006E,0x0020,0x8BF7,0x0020,0x0067,0x0069,0x0074,0x0020,0x0063,0x006C,0x006F,0x006E,0x0065,0x0020,0x0067,0x006F,0x0064,0x006F,0x0074,0x0065,0x006E,0x0067,0x0069,0x006E,0x0065,0x002F,0x0067,0x006F,0x0064,0x006F,0x0074,0x002D,0x0063,0x0070,0x0070,0x0020,0x7684,0x0020,0x0034,0x002E,0x0037,0x0020,0x5206,0x652F,0xFF0C,0x4E0D,0x8981,0x7528,0x0020,0x0076,0x0063,0x0070,0x006B,0x0067,0x0020,0x8FD9,0x4E2A,0x0020,0x0070,0x006F,0x0072,0x0074,0x3002)
    'warn-openpyxl' = @(0x5F53,0x524D,0x0020,0x0070,0x0079,0x0074,0x0068,0x006F,0x006E,0x0020,0x6CA1,0x6709,0x0020,0x006F,0x0070,0x0065,0x006E,0x0070,0x0079,0x0078,0x006C,0xFF0C,0x0045,0x0078,0x0063,0x0065,0x006C,0x0020,0x6570,0x636E,0x5904,0x7406,0x8BF7,0x6539,0x7528,0x0020,0x0044,0x0053,0x0048,0x0020,0x81EA,0x5E26,0x0020,0x0070,0x0079,0x0074,0x0068,0x006F,0x006E,0xFF08,0x89C1,0x0020,0x0041,0x0047,0x0045,0x004E,0x0054,0x0053,0x002E,0x006D,0x0064,0x0020,0x7B2C,0x0020,0x0036,0x0020,0x8282,0xFF09,0x3002)
    'warn-godot-editor' = @(0x672A,0x627E,0x5230,0x7F16,0x8F91,0x5668,0x7248,0x0020,0x0047,0x006F,0x0064,0x006F,0x0074,0xFF1A,0x007B,0x0030,0x007D)
}

# --- output: write UTF-8 bytes directly, bypassing the console code page ----
$script:Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$script:StdOut = [Console]::OpenStandardOutput()

function Out-Line([string]$text) {
    $bytes = $script:Utf8NoBom.GetBytes($text + "`r`n")
    $script:StdOut.Write($bytes, 0, $bytes.Length)
    $script:StdOut.Flush()
}

function Out-Blank() { Out-Line '' }

function T([string]$key) {
    if ($script:TEXTS.ContainsKey($key)) {
        return (-join ($script:TEXTS[$key] | ForEach-Object { [char]$_ }))
    }
    return $key
}

function TF([string]$key, [object[]]$fmtArgs) {
    return ([string]::Format((T $key), $fmtArgs))
}

# --- generic helpers --------------------------------------------------------
# required = $true  : environment item; a miss is a FAIL (blocks -Verify).
# required = $false : project skeleton item (csproj/native/data dirs). These
#                     legitimately do not exist while the project is an empty
#                     shell, so a miss is reported as PENDING, never as FAIL,
#                     and never affects the -Verify exit code.
function New-Check([string]$name, [string]$status, [bool]$ok, [string]$detail, [bool]$required) {
    return (New-Object PSObject -Property @{
        name     = $name
        status   = $status
        ok       = [bool]$ok
        detail   = $detail
        required = [bool]$required
    })
}

function Get-FirstLine([string]$text) {
    foreach ($l in ($text -split "`r?`n")) {
        $t = ([string]$l).Trim()
        if ($t.Length -gt 0) { return $t }
    }
    return ''
}

# run an external program, capture output; returns @{ ok; code; first; text }
function Invoke-Capture([string]$exe, [string[]]$argList) {
    $res = New-Object PSObject -Property @{ ok = $false; code = -1; first = ''; text = '' }
    if (-not $exe) { return $res }
    if (-not (Test-Path -LiteralPath $exe)) { return $res }
    try {
        $out = & $exe @argList 2>&1
        $res.code = $LASTEXITCODE
        $text = (@($out) | ForEach-Object { [string]$_ }) -join "`n"
        $res.text = $text.Trim()
        $res.first = Get-FirstLine $res.text
        $res.ok = (($res.code -eq 0) -or ($res.first.Length -gt 0))
    } catch {
        $res.text = ([string]$_.Exception.Message)
        $res.first = $res.text
    }
    return $res
}

function Get-VersionFrom([string]$text) {
    $m = [regex]::Match($text, '(\d+\.\d+(\.\d+)?([\-+][0-9A-Za-z\.\-]+)?)')
    if ($m.Success) { return $m.Groups[1].Value }
    return $text
}

# ============================================================================
# 0. project root (this script lives in <root>\tools)
# ============================================================================
$scriptDir = Split-Path -Parent $PSCommandPath
$ProjectRoot = Split-Path -Parent $scriptDir
if (-not $ProjectRoot -or -not (Test-Path -LiteralPath $ProjectRoot)) {
    $ProjectRoot = (Get-Location).Path
}
$ProjectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path

$checks = New-Object System.Collections.ArrayList
function Add-Check($c) { [void]$checks.Add($c) }

# ============================================================================
# 1. Godot: $env:GODOT_BIN, then PATH, then the default install dir
# ============================================================================
$GodotHome = $env:GODOT_HOME
if (-not $GodotHome) { $GodotHome = 'E:\Godot' }
$GodotConsoleDefault = Join-Path $GodotHome 'Godot_v4.7.2-stable_mono_win64_console.exe'
$GodotEditorExe      = Join-Path $GodotHome 'Godot_v4.7.2-stable_mono_win64.exe'

$godotBin = ''
$godotSource = ''
if ($env:GODOT_BIN -and (Test-Path -LiteralPath $env:GODOT_BIN)) {
    $godotBin = $env:GODOT_BIN
    $godotSource = 'GODOT_BIN'
} else {
    $cmd = Get-Command 'godot' -CommandType Application -ErrorAction SilentlyContinue
    if ($cmd -and (Test-Path -LiteralPath $cmd.Source)) {
        $godotBin = $cmd.Source
        $godotSource = 'PATH'
    } elseif (Test-Path -LiteralPath $GodotConsoleDefault) {
        $godotBin = $GodotConsoleDefault
        $godotSource = 'default'
    }
}
if ($godotBin) { $godotBin = (Resolve-Path -LiteralPath $godotBin).Path }

$godotVer = New-Object PSObject -Property @{ ok = $false; code = -1; first = ''; text = '' }
if ($godotBin) { $godotVer = Invoke-Capture $godotBin @('--version') }
$godotOk = ($godotBin -ne '') -and ($godotVer.first -match '\d+\.\d+')
Add-Check (New-Check 'Godot' $godotVer.first $godotOk ("{0} [{1}]" -f $godotBin, $godotSource) $true)
$godotEditorOk = Test-Path -LiteralPath $GodotEditorExe

# ============================================================================
# 2. dotnet SDK
# ============================================================================
$dotnetExe = 'C:\Program Files\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetExe)) {
    $c = Get-Command 'dotnet' -CommandType Application -ErrorAction SilentlyContinue
    if ($c) { $dotnetExe = $c.Source } else { $dotnetExe = '' }
}
$dotnetVer = New-Object PSObject -Property @{ ok = $false; code = -1; first = ''; text = '' }
$sdks = @()
if ($dotnetExe -and (Test-Path -LiteralPath $dotnetExe)) {
    $dotnetVer = Invoke-Capture $dotnetExe @('--version')
    $sdkRes = Invoke-Capture $dotnetExe @('--list-sdks')
    foreach ($line in ($sdkRes.text -split "`r?`n")) {
        $t = ([string]$line).Trim()
        if ($t.Length -gt 0) { $sdks += $t }
    }
}
$dotnetOk = ($dotnetExe -ne '') -and (Test-Path -LiteralPath $dotnetExe)
Add-Check (New-Check 'dotnet' $dotnetVer.first $dotnetOk $dotnetExe $true)

# ============================================================================
# 3. offline NuGet source
# ============================================================================
$NugetSrc = $env:DOTNET_NUGET_SOURCE
if (-not $NugetSrc) { $NugetSrc = 'E:\Godot\GodotSharp\Tools\nupkgs' }
$nugetOk = Test-Path -LiteralPath $NugetSrc -PathType Container
$nugetPkgs = @()
if ($nugetOk) {
    foreach ($f in (Get-ChildItem -LiteralPath $NugetSrc -Filter '*.nupkg' -File -ErrorAction SilentlyContinue)) {
        $nugetPkgs += $f.Name
    }
}
$nugetStatus = ''
if ($nugetOk) { $nugetStatus = ("{0} {1}" -f $nugetPkgs.Count, (T 'pkg-count')) }
Add-Check (New-Check 'NuGet' $nugetStatus $nugetOk $NugetSrc $true)

$needPatterns = @(
    @{ name = 'Godot.NET.Sdk';          pattern = '^Godot\.NET\.Sdk\.4\.7\.' },
    @{ name = 'GodotSharp';             pattern = '^GodotSharp\.4\.7\.' },
    @{ name = 'Godot.SourceGenerators'; pattern = '^Godot\.SourceGenerators\.4\.7\.' }
)
$pkgFound = @{}
foreach ($np in $needPatterns) {
    $hit = $false
    foreach ($p in $nugetPkgs) { if ($p -match $np.pattern) { $hit = $true; break } }
    $pkgFound[$np.name] = $hit
}
$nugetPkgsOk = $nugetOk -and $pkgFound['Godot.NET.Sdk'] -and $pkgFound['GodotSharp']

# ============================================================================
# 4. build tools: cmake / ninja
# ============================================================================
$CmakeBin = $env:CMAKE_BIN
if (-not $CmakeBin) { $CmakeBin = 'D:\env\cmake\cmake-3.31.12-windows-x86_64\bin\cmake.exe' }
$cmakeRes = Invoke-Capture $CmakeBin @('--version')
$cmakeVer = Get-VersionFrom $cmakeRes.first
$cmakeOk = Test-Path -LiteralPath $CmakeBin
Add-Check (New-Check 'cmake' $cmakeVer $cmakeOk $CmakeBin $true)

$NinjaBin = $env:NINJA_BIN
if (-not $NinjaBin) { $NinjaBin = 'D:\env\ninja\ninja.exe' }
$ninjaRes = Invoke-Capture $NinjaBin @('--version')
$ninjaOk = Test-Path -LiteralPath $NinjaBin
Add-Check (New-Check 'ninja' $ninjaRes.first $ninjaOk $NinjaBin $true)

# ============================================================================
# 5. vcpkg (plus the godot-cpp port version mismatch warning)
# ============================================================================
$VcpkgRoot = $env:VCPKG_ROOT
if (-not $VcpkgRoot) { $VcpkgRoot = 'D:\vcpkg' }
$vcpkgExe = Join-Path $VcpkgRoot 'vcpkg.exe'
$vcpkgRes = Invoke-Capture $vcpkgExe @('version')
$vcpkgOk = Test-Path -LiteralPath $vcpkgExe
$vcpkgStatus = $vcpkgRes.first
if ($vcpkgOk) {
    $m = [regex]::Match($vcpkgStatus, '(\d{4}-\d{2}-\d{2}-[0-9a-f]+)')
    if ($m.Success) { $vcpkgStatus = $m.Groups[1].Value }
}
Add-Check (New-Check 'vcpkg' $vcpkgStatus $vcpkgOk $VcpkgRoot $true)

$vcpkgGodotCppVer = ''
$vcpkgJson = Join-Path $VcpkgRoot 'ports\godot-cpp\vcpkg.json'
if (Test-Path -LiteralPath $vcpkgJson) {
    try {
        $j = Get-Content -LiteralPath $vcpkgJson -Raw | ConvertFrom-Json
        if ($j.version) { $vcpkgGodotCppVer = [string]$j.version }
    } catch {
        $vcpkgGodotCppVer = ''
    }
}
$godotCppMismatch = $false
if ($vcpkgGodotCppVer -and ($vcpkgGodotCppVer -notmatch '^4\.7')) {
    $godotCppMismatch = $true
}

# ============================================================================
# 6. Python (DSH bundled runtime has openpyxl; the system python does not)
# ============================================================================
$PyDefault = 'C:\Users\Administrator\.dsh\dsh-runtimes\dsh-primary-runtime\dependencies\python\python.exe'
$PyBin = $env:DSH_PYTHON
if (-not $PyBin) { $PyBin = $PyDefault }
$pyOk = Test-Path -LiteralPath $PyBin
$pyVer = ''
$openpyxlVer = ''
$xlsxwriterVer = ''
if ($pyOk) {
    $r = Invoke-Capture $PyBin @('-c', 'import sys; print(sys.version.split()[0])')
    if ($r.ok) { $pyVer = $r.first }
    $r = Invoke-Capture $PyBin @('-c', 'import openpyxl; print(openpyxl.__version__)')
    if ($r.ok) { $openpyxlVer = $r.first }
    $r = Invoke-Capture $PyBin @('-c', 'import xlsxwriter; print(xlsxwriter.__version__)')
    if ($r.ok) { $xlsxwriterVer = $r.first }
}
$pyStatus = $pyVer
if ($openpyxlVer) { $pyStatus = "$pyStatus / openpyxl $openpyxlVer" }
Add-Check (New-Check 'python' $pyStatus $pyOk $PyBin $true)
$openpyxlOk = $pyOk -and ($openpyxlVer -ne '')

$sysCmd = Get-Command 'python' -CommandType Application -ErrorAction SilentlyContinue
$sysPy = ''
if ($sysCmd) { $sysPy = $sysCmd.Source }

# ============================================================================
# 7. git
# ============================================================================
$gitExe = ''
$gc = Get-Command 'git' -CommandType Application -ErrorAction SilentlyContinue
if ($gc) { $gitExe = $gc.Source }
$gitRes = New-Object PSObject -Property @{ ok = $false; code = -1; first = ''; text = '' }
if ($gitExe) { $gitRes = Invoke-Capture $gitExe @('--version') }
$gitOk = ($gitExe -ne '')
Add-Check (New-Check 'git' $gitRes.first $gitOk $gitExe $true)

# ============================================================================
# 8. project files: config/name, dotnet/assembly_name, csproj
# ============================================================================
$projGodot = Join-Path $ProjectRoot 'project.godot'
$projOk = Test-Path -LiteralPath $projGodot
$projName = ''
$asmName = ''
if ($projOk) {
    foreach ($line in (Get-Content -LiteralPath $projGodot -Encoding UTF8 -ErrorAction SilentlyContinue)) {
        $t = [string]$line
        $m = [regex]::Match($t, '^\s*config/name\s*=\s*"(.*?)"\s*$')
        if ($m.Success) { $projName = $m.Groups[1].Value }
        $m = [regex]::Match($t, '^\s*project/assembly_name\s*=\s*"(.*?)"\s*$')
        if ($m.Success) { $asmName = $m.Groups[1].Value }
    }
}
$projStatus = ''
if ($projOk) { $projStatus = "name=$projName asm=$asmName" }
Add-Check (New-Check 'project.godot' $projStatus $projOk $projGodot $true)

$csprojPath = ''
$csprojOk = $false
if ($asmName) {
    $cand = Join-Path $ProjectRoot ($asmName + '.csproj')
    if (Test-Path -LiteralPath $cand) { $csprojPath = $cand; $csprojOk = $true }
}
if (-not $csprojOk) {
    $any = Get-ChildItem -LiteralPath $ProjectRoot -Filter '*.csproj' -File -ErrorAction SilentlyContinue |
           Select-Object -First 1
    if ($any) { $csprojPath = $any.FullName; $csprojOk = $true }
}
$csprojStatus = ''
if ($csprojOk) { $csprojStatus = Split-Path -Leaf $csprojPath } else { $csprojStatus = T 'absent' }
Add-Check (New-Check 'csproj' $csprojStatus $csprojOk $csprojPath $false)

# ============================================================================
# 9. directory skeleton: native/, data/excel, data/generated
# ============================================================================
$dirSpecs = @(
    @{ key = 'native';         path = 'native';         label = 'native/' },
    @{ key = 'data-excel';     path = 'data\excel';     label = 'data/excel' },
    @{ key = 'data-generated'; path = 'data\generated'; label = 'data/generated' }
)
$dirState = @{}
foreach ($ds in $dirSpecs) {
    $full = Join-Path $ProjectRoot $ds.path
    $exists = Test-Path -LiteralPath $full -PathType Container
    $dirState[$ds.key] = $exists
    $st = T 'absent'
    if ($exists) {
        $n = @(Get-ChildItem -LiteralPath $full -Force -ErrorAction SilentlyContinue).Count
        $st = "$n $(T 'items')"
    }
    Add-Check (New-Check $ds.label $st $exists $full $false)
}

# ============================================================================
# 10. ARCHITECTURE.md "last updated" timestamp
# ============================================================================
$archPath = Join-Path $ProjectRoot 'docs\architecture\ARCHITECTURE.md'
$archOk = Test-Path -LiteralPath $archPath
$archStamp = ''
if ($archOk) {
    $head = Get-Content -LiteralPath $archPath -Encoding UTF8 -TotalCount 30 -ErrorAction SilentlyContinue
    foreach ($line in $head) {
        $m = [regex]::Match([string]$line, '(\d{4}-\d{2}-\d{2})')
        if ($m.Success) { $archStamp = $m.Groups[1].Value; break }
    }
}
$archStatus = $archStamp
if (-not $archOk) { $archStatus = T 'absent' }
elseif (-not $archStamp) { $archStatus = T 'no-stamp' }
Add-Check (New-Check 'ARCHITECTURE.md' $archStatus $archOk $archPath $true)

# ============================================================================
# 10b. docs/rules/ 01~07 completeness
#      The rule set is the backbone of this workflow: a missing rule document
#      means that rule silently stops being enforced. Verify lists exactly
#      which numbers are missing so the gap is visible before starting work.
# ============================================================================
$rulesDir = Join-Path $ProjectRoot 'docs\rules'
$ruleNumbers = @('01', '02', '03', '04', '05', '06', '07')
$rulesFound = @()
$rulesMissing = @()
foreach ($num in $ruleNumbers) {
    # match "NN-*.md" (e.g. 01-gdscript-ban.md); ignore other files in the dir
    $hit = $null
    if (Test-Path -LiteralPath $rulesDir -PathType Container) {
        $hit = Get-ChildItem -LiteralPath $rulesDir -Filter ($num + '-*.md') -File -ErrorAction SilentlyContinue |
               Select-Object -First 1
    }
    if ($hit) { $rulesFound += $hit.Name } else { $rulesMissing += $num }
}
$rulesOk = ($rulesMissing.Count -eq 0)
$rulesStatus = ''
if ($rulesMissing.Count -eq 0) {
    $rulesStatus = ("{0}/7 {1}" -f $rulesFound.Count, (T 'rules-full'))
} elseif ($rulesFound.Count -eq 0) {
    $rulesStatus = (T 'rules-none')
} else {
    $rulesStatus = ("{0}/7 {1} {2}" -f $rulesFound.Count, (T 'rules-lack'), ($rulesMissing -join ', '))
}
$rulesDetail = ''
if ($rulesMissing.Count -gt 0) { $rulesDetail = 'missing: ' + ($rulesMissing -join ', ') }
else { $rulesDetail = $rulesDir }
Add-Check (New-Check 'docs/rules 01~07' $rulesStatus $rulesOk $rulesDetail $true)

# ============================================================================
# warnings (non-fatal but must be visible)
# ============================================================================
$warnings = New-Object System.Collections.ArrayList
if ($godotCppMismatch) {
    [void]$warnings.Add((TF 'warn-vcpkg-godotcpp' @($vcpkgGodotCppVer)))
}
if (-not $rulesOk -and $rulesMissing.Count -gt 0) {
    [void]$warnings.Add((TF 'warn-rules-missing' @($rulesMissing.Count, ($rulesMissing -join ', '))))
}
if (-not $openpyxlOk) {
    [void]$warnings.Add((T 'warn-openpyxl'))
}
if (-not $godotEditorOk) {
    [void]$warnings.Add((TF 'warn-godot-editor' @($GodotEditorExe)))
}

try { $host.UI.RawUI.WindowTitle = (T 'title') } catch { }

# ============================================================================
# mode A: -Json
# ============================================================================
if ($Json) {
    $failN = @($checks | Where-Object { $_.required -and (-not $_.ok) }).Count
    $pendN = @($checks | Where-Object { (-not $_.required) -and (-not $_.ok) }).Count
    $payload = [ordered]@{
        schemaVersion = 1
        generatedAt   = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        projectRoot   = $ProjectRoot
        ok            = ($failN -eq 0)
        summary       = [ordered]@{
            total   = $checks.Count
            passed  = @($checks | Where-Object { $_.ok }).Count
            failed  = $failN
            pending = $pendN
        }
        items         = @($checks | ForEach-Object {
                $st = 'ok'
                if (-not $_.ok) {
                    if ($_.required) { $st = 'fail' } else { $st = 'pending' }
                }
                [ordered]@{
                    name     = $_.name
                    status   = $_.status
                    ok       = [bool]$_.ok
                    required = [bool]$_.required
                    state    = $st
                    detail   = $_.detail
                }
            })
        godot         = [ordered]@{
            bin       = $godotBin
            source    = $godotSource
            version   = $godotVer.first
            ok        = [bool]$godotOk
            editorExe = $GodotEditorExe
            editorOk  = [bool]$godotEditorOk
            home      = $GodotHome
        }
        dotnet        = [ordered]@{
            exe     = $dotnetExe
            version = $dotnetVer.first
            ok      = [bool]$dotnetOk
            sdks    = @($sdks)
        }
        nuget         = [ordered]@{
            source   = $NugetSrc
            ok       = [bool]$nugetOk
            packages = @($nugetPkgs | Sort-Object)
            required = [ordered]@{
                'Godot.NET.Sdk'          = [bool]$pkgFound['Godot.NET.Sdk']
                'GodotSharp'             = [bool]$pkgFound['GodotSharp']
                'Godot.SourceGenerators' = [bool]$pkgFound['Godot.SourceGenerators']
                allPresent               = [bool]$nugetPkgsOk
            }
        }
        toolchain     = [ordered]@{
            cmake = [ordered]@{ bin = $CmakeBin; version = $cmakeVer; ok = [bool]$cmakeOk }
            ninja = [ordered]@{ bin = $NinjaBin; version = $ninjaRes.first; ok = [bool]$ninjaOk }
        }
        vcpkg         = [ordered]@{
            root             = $VcpkgRoot
            exe              = $vcpkgExe
            version          = $vcpkgStatus
            ok               = [bool]$vcpkgOk
            godotCppPort     = $vcpkgGodotCppVer
            godotCppMismatch = [bool]$godotCppMismatch
            note             = $(if ($godotCppMismatch) { (TF 'warn-vcpkg-godotcpp' @($vcpkgGodotCppVer)) } else { '' })
        }
        python        = [ordered]@{
            exe        = $PyBin
            version    = $pyVer
            ok         = [bool]$pyOk
            openpyxl   = $openpyxlVer
            openpyxlOk = [bool]$openpyxlOk
            xlsxwriter = $xlsxwriterVer
            systemExe  = $sysPy
        }
        git           = [ordered]@{
            exe     = $gitExe
            version = $gitRes.first
            ok      = [bool]$gitOk
        }
        project       = [ordered]@{
            godotFile    = $projGodot
            godotOk      = [bool]$projOk
            name         = $projName
            assemblyName = $asmName
            csproj       = $csprojPath
            csprojOk     = [bool]$csprojOk
        }
        paths         = [ordered]@{
            native                  = [bool]$dirState['native']
            dataExcel               = [bool]$dirState['data-excel']
            dataGenerated           = [bool]$dirState['data-generated']
            architecture            = $archPath
            architectureOk          = [bool]$archOk
            architectureLastUpdated = $archStamp
        }
        rules         = [ordered]@{
            dir     = $rulesDir
            ok      = [bool]$rulesOk
            found   = @($rulesFound)
            missing = @($rulesMissing)
            total   = $ruleNumbers.Count
            count   = $rulesFound.Count
        }
        warnings      = @($warnings)
    }
    $jsonText = $payload | ConvertTo-Json -Depth 12 -Compress
    Out-Line $jsonText
    exit 0
}

# ============================================================================
# mode B: -Verify
# ============================================================================
if ($Verify) {
    $failCount = 0
    $pendCount = 0
    foreach ($c in $checks) {
        if ($c.ok) {
            Out-Line ("{0}  {1}  {2}" -f 'PASS', $c.name.PadRight(16), $c.status)
        } elseif ($c.required) {
            $failCount++
            Out-Line ("{0}  {1}  {2}" -f 'FAIL', $c.name.PadRight(16), $c.status)
        } else {
            $pendCount++
            Out-Line ("{0}  {1}  {2}" -f 'PEND', $c.name.PadRight(16), $c.status)
        }
    }
    foreach ($w in $warnings) { Out-Line ("WARN  " + $w) }
    Out-Blank
    $okN = @($checks | Where-Object { $_.ok }).Count
    Out-Line (("PASS {0} / {1}   FAIL {2}   PEND {3}") -f $okN, $checks.Count, $failCount, $pendCount)
    if ($pendCount -gt 0) {
        Out-Line (T 'verify-pend')
    }
    if ($failCount -gt 0) {
        Out-Line ((TF 'verify-fail' @($failCount)))
        exit 1
    }
    Out-Line (T 'verify-pass')
    exit 0
}

# ============================================================================
# mode C: default summary tables
# ============================================================================
function Show-Present([bool]$flag) {
    if ($flag) { return (T 'present') }
    return (T 'absent')
}

# pad by display width: CJK chars count as 2 columns
function Pad-Display([string]$s, [int]$width) {
    $disp = 0
    foreach ($ch in $s.ToCharArray()) {
        if ([int]$ch -gt 127) { $disp += 2 } else { $disp += 1 }
    }
    $pad = $width - $disp
    if ($pad -lt 0) { $pad = 0 }
    return ($s + (' ' * $pad))
}

function Show-Grid([string]$heading, [object[]]$rows) {
    Out-Line ('[' + $heading + ']')
    $labelWidth = 0
    foreach ($r in $rows) {
        $w = 0
        foreach ($ch in ([string]$r[0]).ToCharArray()) {
            if ([int]$ch -gt 127) { $w += 2 } else { $w += 1 }
        }
        if ($w -gt $labelWidth) { $labelWidth = $w }
    }
    foreach ($r in $rows) {
        Out-Line ('  ' + (Pad-Display ([string]$r[0]) $labelWidth) + ' : ' + [string]$r[1])
    }
    Out-Blank
}

function Get-VersionOrMissing([string]$v) {
    if ($v) { return $v }
    return (T 'missing')
}

Out-Line (T 'title')
Out-Line ((T 'project-root') + ': ' + $ProjectRoot + '   ' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))
Out-Line ('=' * 62)

$toolRows = @(
    , @((T 'h-godot'),   (Get-VersionOrMissing $godotVer.first) + '   ' + $godotBin + '  [' + $godotSource + ']')
    , @((T 'h-editor'),  (Show-Present $godotEditorOk) + '   ' + $GodotEditorExe)
    , @((T 'h-dotnet'),  (Get-VersionOrMissing $dotnetVer.first) + '   ' + $dotnetExe)
    , @((T 'h-sdks'),    ($sdks -join '; '))
    , @((T 'h-nuget'),   (Show-Present $nugetOk) + '   ' + $NugetSrc)
    , @((T 'h-nupkgs'),  (($nugetPkgs | Sort-Object) -join '  '))
    , @((T 'h-cmake'),   (Get-VersionOrMissing $cmakeVer))
    , @((T 'h-ninja'),   (Get-VersionOrMissing $ninjaRes.first))
    , @((T 'h-vcpkg'),   (Get-VersionOrMissing $vcpkgStatus))
    , @((T 'h-git'),     (Get-VersionOrMissing $gitRes.first))
)
Show-Grid (T 'sec-toolchain') $toolRows

$pyRows = @(
    , @((T 'h-python'),     (Get-VersionOrMissing $pyVer) + '   ' + $PyBin)
    , @((T 'h-openpyxl'),   (Get-VersionOrMissing $openpyxlVer))
    , @((T 'h-xlsxwriter'), (Get-VersionOrMissing $xlsxwriterVer))
)
Show-Grid (T 'sec-python') $pyRows

$archDisplay = T 'absent'
if ($archOk) {
    if ($archStamp) { $archDisplay = $archStamp } else { $archDisplay = (T 'no-stamp') }
}

$projRows = @(
    , @((T 'h-name'),      $projName)
    , @((T 'h-asm'),       $asmName)
    , @((T 'h-csproj'),    $csprojStatus)
    , @((T 'h-native'),    (Show-Present $dirState['native']))
    , @((T 'h-dataexcel'), (Show-Present $dirState['data-excel']))
    , @((T 'h-datagen'),   (Show-Present $dirState['data-generated']))
    , @((T 'h-arch'),      $archDisplay)
    , @((T 'h-rules'),     $rulesStatus)
)
Show-Grid (T 'sec-project') $projRows

if ($warnings.Count -gt 0) {
    Out-Line ('[' + (T 'sec-warn') + ']')
    foreach ($w in $warnings) { Out-Line ('  ! ' + $w) }
    Out-Blank
}

$okCount = @($checks | Where-Object { $_.ok }).Count
$failCount2 = @($checks | Where-Object { $_.required -and (-not $_.ok) }).Count
$pendCount2 = @($checks | Where-Object { (-not $_.required) -and (-not $_.ok) }).Count
Out-Line ('-' * 62)
Out-Line ("$okCount / $($checks.Count)  $(T 'summary')  $(T 'passed')   FAIL $failCount2   $(T 'pending-label') $pendCount2")
Out-Line (T 'hint')
exit 0
