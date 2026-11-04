# =============================================================================
#  build_native.ps1 -- one-shot GDExtension build (PowerShell 5.1 compatible)
# =============================================================================
#  Rules: docs/rules/02-tech-selection.md 3.5 -> this file is PURE ASCII, no BOM.
#  Do not add non-ASCII bytes here. All messages are ASCII by design.
#
#  Steps:
#    1. locate vcvars64.bat and import MSVC environment (cl.exe is NOT on PATH)
#    2. ensure godot-cpp at the pinned commit under native/gdextension/libgodot-cpp
#    3. ensure native/gdextension/gdextension_interface.h matches the engine
#    4. cmake configure + ninja build -> addons/biu_biu_native/bin/*.dll
#
#  Usage:
#    powershell -NoProfile -ExecutionPolicy Bypass -File tools\build_native.ps1
#    powershell -NoProfile -ExecutionPolicy Bypass -File tools\build_native.ps1 -Config Debug
#    powershell -NoProfile -ExecutionPolicy Bypass -File tools\build_native.ps1 -SkipInterfaceDump
# =============================================================================

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Config = 'Release',
    [switch]$SkipInterfaceDump,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

# --- pinned facts (keep in sync with native/README.md) ------------------------
$GodotCppRepo    = 'https://github.com/godotengine/godot-cpp'
$GodotCppCommit  = 'e103d348c4b2d8df7b819126d855e19381cb9a87'
$GodotCppApiVer  = '4.7'

$NativeDir = $PSScriptRoot
$RepoRoot = Split-Path -Parent $NativeDir
$GodotCppDir = Join-Path $NativeDir 'gdextension\libgodot-cpp'
$InterfaceHeader = Join-Path $NativeDir 'gdextension\gdextension_interface.h'
$BuildDir = Join-Path $NativeDir 'build'
$BinDir = Join-Path $RepoRoot 'addons\biu_biu_native\bin'
$Cmake = 'D:\env\cmake\cmake-3.31.12-windows-x86_64\bin\cmake.exe'
$Ninja = 'D:\env\ninja\ninja.exe'
$GodotExe = 'E:\Godot\Godot_v4.7.2-stable_mono_win64_console.exe'

function Write-Step($msg) {
    Write-Host ''
    Write-Host "==> $msg" -ForegroundColor Cyan
}

function Die($msg) {
    Write-Host "ERROR: $msg" -ForegroundColor Red
    exit 1
}

# --- step 0: sanity check tools ----------------------------------------------
Write-Step 'Checking toolchain'
if (-not (Test-Path $Cmake))  { Die "cmake not found: $Cmake" }
if (-not (Test-Path $Ninja))  { Die "ninja not found: $Ninja" }
if (-not (Test-Path $GodotExe)) { Die "godot not found: $GodotExe" }
Write-Host "cmake : $Cmake"
Write-Host "ninja : $Ninja"
Write-Host "godot : $GodotExe"

# --- step 1: import MSVC environment via vcvars64 -----------------------------
Write-Step 'Importing MSVC environment (vcvars64.bat)'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { Die 'vswhere.exe not found; install Visual Studio with C++ tools' }
$vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) { Die 'No Visual Studio C++ x64 toolset found via vswhere' }
$vcvars = Join-Path $vsPath 'VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path $vcvars)) { Die "vcvars64.bat not found: $vcvars" }

# Run vcvars64 and capture the resulting environment, then apply it in-process.
$envDump = & cmd.exe /c "`"$vcvars`" >nul 2>&1 && set"
foreach ($line in $envDump) {
    $idx = $line.IndexOf('=')
    if ($idx -gt 0) {
        $name = $line.Substring(0, $idx)
        $value = $line.Substring($idx + 1)
        Set-Item -Path ("env:" + $name) -Value $value -ErrorAction SilentlyContinue
    }
}
$cl = Get-Command cl.exe -ErrorAction SilentlyContinue
if (-not $cl) { Die 'cl.exe still not on PATH after vcvars64 import' }
Write-Host "cl.exe : $($cl.Source)"

# --- step 2: godot-cpp at pinned commit --------------------------------------
Write-Step "Ensuring godot-cpp at $GodotCppCommit"
if (-not (Test-Path (Join-Path $GodotCppDir 'CMakeLists.txt'))) {
    Write-Host 'godot-cpp missing; cloning master (shallow) ...'
    & git clone --depth 1 --branch master $GodotCppRepo $GodotCppDir
    if ($LASTEXITCODE -ne 0) { Die 'git clone godot-cpp failed' }
}
$head = (& git -C $GodotCppDir rev-parse HEAD).Trim()
Write-Host "godot-cpp HEAD : $head"
if ($head -ne $GodotCppCommit) {
    Write-Host "WARN: HEAD differs from pinned commit $GodotCppCommit" -ForegroundColor Yellow
    Write-Host '      Fetching pinned commit ...' -ForegroundColor Yellow
    & git -C $GodotCppDir fetch --depth 1 origin $GodotCppCommit
    if ($LASTEXITCODE -ne 0) { Die 'git fetch of pinned commit failed' }
    & git -C $GodotCppDir checkout --detach $GodotCppCommit
    if ($LASTEXITCODE -ne 0) { Die 'git checkout of pinned commit failed' }
    $head = (& git -C $GodotCppDir rev-parse HEAD).Trim()
    Write-Host "godot-cpp HEAD now : $head"
}

# --- step 3: gdextension_interface.h for this exact engine --------------------
if (-not $SkipInterfaceDump) {
    Write-Step 'Dumping gdextension_interface.h'
    # NOTE: the engine writes the header into the --path PROJECT ROOT, not the cwd.
    #       We dump into a scratch dir then move it to native/gdextension/.
    $scratch = Join-Path $env:TEMP ('biu_dump_' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $scratch | Out-Null
    Push-Location $scratch
    try {
        & $GodotExe --headless --dump-gdextension-interface --path $RepoRoot | Out-Null
    } finally {
        Pop-Location
    }
    $dumped = Join-Path $RepoRoot 'gdextension_interface.h'
    if (-not (Test-Path $dumped)) { Die 'engine did not produce gdextension_interface.h in project root' }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $InterfaceHeader) | Out-Null
    Move-Item -Force $dumped $InterfaceHeader
    Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
    $len = (Get-Item $InterfaceHeader).Length
    Write-Host "interface header : $InterfaceHeader ($len bytes)"
    if ($len -lt 10000) { Die 'dumped interface header looks truncated' }
} else {
    Write-Step 'Skipping interface dump (-SkipInterfaceDump)'
    if (-not (Test-Path $InterfaceHeader)) { Die "interface header missing: $InterfaceHeader" }
}

# --- step 4: cmake configure + build -----------------------------------------
Write-Step "Configuring CMake ($Config)"
if ($Clean -and (Test-Path $BuildDir)) {
    Write-Host "removing $BuildDir"
    Remove-Item -Recurse -Force $BuildDir
}

$configureArgs = @(
    '-S', $NativeDir,
    '-B', $BuildDir,
    '-G', 'Ninja',
    "-DCMAKE_MAKE_PROGRAM=$Ninja",
    "-DCMAKE_BUILD_TYPE=$Config",
    "-DGODOTCPP_API_VERSION=$GodotCppApiVer"
)
Write-Host "cmake $($configureArgs -join ' ')"
& $Cmake @configureArgs
if ($LASTEXITCODE -ne 0) { Die 'cmake configure failed' }

Write-Step 'Building with ninja'
& $Cmake --build $BuildDir
if ($LASTEXITCODE -ne 0) { Die 'build failed' }

# --- step 5: verify artifact --------------------------------------------------
Write-Step 'Verifying artifact'
$expected = Join-Path $BinDir ("biu_biu_native.windows.{0}.x86_64.dll" -f $Config)
if (-not (Test-Path $expected)) {
    Write-Host "expected: $expected" -ForegroundColor Red
    Get-ChildItem $BinDir -ErrorAction SilentlyContinue | Select-Object Name, Length | Format-Table
    Die 'artifacts not found where .gdextension expects'
}
$item = Get-Item $expected
Write-Host ("OK  {0}  ({1:N0} bytes)" -f $item.Name, $item.Length) -ForegroundColor Green
Write-Host ''
Write-Host 'Build finished. Next: verify the extension is loaded by Godot:'
Write-Host ("  {0} --headless --path {1} --quit" -f $GodotExe, $RepoRoot)
