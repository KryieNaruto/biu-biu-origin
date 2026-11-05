# tools/check_all.ps1 - BiuBiuOrigin one-shot verification entry point
# ============================================================================
# *** DO NOT "OPTIMIZE" THIS FILE INTO PLAIN-TEXT CHINESE ***
#   This script is intentionally pure-ASCII source (docs/rules/02-tech-selection.md
#   section 3.5, option 1). PowerShell 5.1 reads a BOM-less .ps1 with the ANSI code
#   page (GBK on this machine); UTF-8 Chinese bytes then get re-interpreted as GBK
#   pairs and can swallow a following newline or a quote, which surfaces as a
#   bizarre parse error far away from the real line. Keep every byte below 0x80.
#
# WHAT IT DOES (the Definition of Done checklist of docs/rules/07, step 5):
#   step 1/4  dotnet build            -> main project, default configuration
#   step 1b/4 dotnet build -c Host    -> the Godot-coupled layers (scripts/host/**)
#   step 2/4  tools/check_architecture.ps1
#   step 3/4  dotnet test             -> tests/BiuBiuOrigin.Tests.csproj (ECS.md section 10)
#   step 4/4  tools/dungeon_check     -> 200-seed dungeon property test (ARCHITECTURE.md 14.4)
#
#   Exit code: 0 only when every step passed. Any failure returns a non-zero code.
#   Failures are reported for ALL steps (later steps still run) so one invocation
#   shows the whole picture; the exit code is the first failing step's code.
#
# WHY STEP 4 EXISTS (and why it is a tool, not a test):
#   The dungeon planner lives in scripts/spawn/rooms/**, which the tests project must not
#   compile -- the tests project's whole reason to exist is proving that Game.Core has zero
#   engine dependency (see BLOCKING RULE below), and widening its <Compile Include> to the
#   gameplay layers would quietly weaken that proof. So the 200-seed property check is a
#   standalone console app under tools/dungeon_check/ (namespace DungeonCheck, NOT Game.*,
#   because the guard's GS007 rejects Game.* sources outside scripts/). It validates every
#   generated layout against DungeonValidation's six invariants and exits non-zero on any
#   failure, so check_all can gate on it exactly like a test suite.
#
# BLOCKING RULE (documented on purpose, not an accident):
#   The layering guard asserts that Game.Core sources contain 0 "using Godot", so the
#   tests project MUST NOT transitively reference GodotSharp. It therefore does NOT
#   use <ProjectReference> to BiuBiuOrigin.csproj (Sdk=Godot.NET.Sdk): a ProjectReference
#   drags the whole GodotSharp reference closure into the test process and destroys the
#   "test without the engine" property. Instead the tests project compiles the engine-free
#   Game.Core sources directly (<Compile Include="..\scripts\core\**\*.cs">) and guards
#   that the list never grows a Godot-typed file (Game_Core_Layering.cs).
#
#   Because of that split, `dotnet build` alone does NOT build scripts/host/** (the only
#   layer allowed to reference the engine). Step 1 runs two builds: the default build
#   (what the Definition of Done means by "dotnet build", emitted to .godot/mono/temp/bin
#   so the Godot editor stays happy) and the "host" configuration that compiles the
#   Godot-coupled sources too. Skipping either one lets a broken host layer pass.
#
#   The tests project emits into tests/bin and tests/obj, which are in the architecture
#   guard's ignore list (bin/**, obj/**), so the guard never scans the MSTest DLLs.
#
# USAGE
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\check_all.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\check_all.ps1 -SkipTests
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\check_all.ps1 -NoRestore
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\check_all.ps1 -DungeonSeeds 32
#
#   -SkipTests     run build (x2) + guard + dungeon check only. Use while waiting on
#                  another agent's uncommitted kernel work; exit 0 no longer means
#                  "tests green".
#   -SkipDungeon   skip step 4/4 (use when iterating on the dungeon planner itself and
#                  you only need the compile/guard/tests signal).
#   -DungeonSeeds  seed count for step 4/4. Default 200 (the ARCHITECTURE.md 14.4 number);
#                  drop it for a fast local pass, keep it for the DoD run.
#   -NoRestore     pass --no-restore to dotnet build / dotnet test (offline re-runs).
#   -ProjectPath   repo root. Default: the parent folder of this script (<repo>\tools\..).
# ============================================================================

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$ProjectPath = '',

    [switch]$SkipTests,

    [switch]$SkipDungeon,

    [int]$DungeonSeeds = 200,

    [switch]$NoRestore
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# 0. Console encoding (cosmetic only; never fatal)
# ---------------------------------------------------------------------------
try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
    if (-not [Console]::IsOutputRedirected) {
        $null = & cmd.exe /c "chcp 65001 >nul 2>nul"
    }
} catch {
    # ignore: a wrong code page only garbles text, it never changes the exit code
}

# ---------------------------------------------------------------------------
# 1. Paths
# ---------------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $scriptDir = $PSScriptRoot
    if ([string]::IsNullOrWhiteSpace($scriptDir)) {
        $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
    }
    $ProjectPath = Split-Path -Parent $scriptDir
}

try {
    $RootFull = (Resolve-Path -LiteralPath $ProjectPath -ErrorAction Stop).ProviderPath
} catch {
    Write-Host ("[check_all] error: project root not found -> " + $ProjectPath) -ForegroundColor Red
    exit 2
}

$MainCsproj = Join-Path $RootFull 'BiuBiuOrigin.csproj'
$TestCsproj = Join-Path $RootFull 'tests\BiuBiuOrigin.Tests.csproj'
$GuardPs1 = Join-Path $RootFull 'tools\check_architecture.ps1'
$DungeonCsproj = Join-Path $RootFull 'tools\dungeon_check\DungeonCheck.csproj'

# ---------------------------------------------------------------------------
# 2. Step runner
# ---------------------------------------------------------------------------
$script:Results = New-Object System.Collections.ArrayList

function Invoke-Step {
    <#
      Runs one child process, streams its output unchanged (so the CI log reads like a
      manual run), and records name / exit code / seconds. Never throws on failure:
      the caller decides whether the step is blocking.
    #>
    param(
        [string]$Name,
        [string]$Exe,
        [string[]]$ArgList
    )

    Write-Host ''
    Write-Host ('=== ' + $Name + ' ===') -ForegroundColor Cyan
    Write-Host ('    ' + $Exe + ' ' + ($ArgList -join ' ')) -ForegroundColor DarkGray

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $code = 1
    try {
        # Everything the child prints goes straight to the host, NOT down the pipeline.
        # This matters: anything this function emits to the output stream is captured by
        # the caller's assignment ($c = Invoke-Step ...). If build/test output leaked here,
        # $c would become an array of strings, $failCode would become that text, and
        # "exit $failCode" would fail with a bogus code -- reporting FAILED while returning 0.
        & $Exe @ArgList | Out-Host
        $code = $LASTEXITCODE
        if ($null -eq $code) { $code = 0 }
    } catch {
        Write-Host ('    [exception] ' + $_.Exception.Message) -ForegroundColor Red
        $code = 1
    }
    $sw.Stop()

    $item = New-Object psobject -Property @{
        Name    = $Name
        Code    = [int]$code
        Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 2)
    }
    $null = $script:Results.Add($item)

    if ($code -eq 0) {
        Write-Host ('--- ' + $Name + ': OK (' + $item.Seconds + 's)') -ForegroundColor Green
    } else {
        Write-Host ('--- ' + $Name + ': FAILED exit=' + $code + ' (' + $item.Seconds + 's)') -ForegroundColor Red
    }

    # The ONLY thing this function returns to its caller.
    return [int]$code
}

# ---------------------------------------------------------------------------
# 3. Preconditions (missing files are hard failures, never silent skips)
# ---------------------------------------------------------------------------
$fatal = 0
if (-not (Test-Path -LiteralPath $MainCsproj)) {
    Write-Host ('[check_all] error: missing ' + $MainCsproj) -ForegroundColor Red
    $fatal++
}
if (-not (Test-Path -LiteralPath $GuardPs1)) {
    Write-Host ('[check_all] error: missing ' + $GuardPs1) -ForegroundColor Red
    $fatal++
}
if (-not $SkipTests) {
    if (-not (Test-Path -LiteralPath $TestCsproj)) {
        Write-Host ('[check_all] error: missing ' + $TestCsproj) -ForegroundColor Red
        $fatal++
    }
}
if (-not $SkipDungeon) {
    if (-not (Test-Path -LiteralPath $DungeonCsproj)) {
        Write-Host ('[check_all] error: missing ' + $DungeonCsproj) -ForegroundColor Red
        $fatal++
    }
}
if ($fatal -gt 0) {
    Write-Host '[check_all] FAILED (preconditions)' -ForegroundColor Red
    exit 2
}

Write-Host ('[check_all] root      : ' + $RootFull)
Write-Host ('[check_all] main      : dotnet build BiuBiuOrigin.csproj (default config)')
Write-Host ('[check_all] host layer: dotnet build BiuBiuOrigin.csproj -c Host (Godot-coupled sources)')
Write-Host ('[check_all] guard     : tools/check_architecture.ps1')
if ($SkipTests) {
    Write-Host '[check_all] tests     : SKIPPED (-SkipTests)'
} else {
    Write-Host ('[check_all] tests     : ' + $TestCsproj)
}
if ($SkipDungeon) {
    Write-Host '[check_all] dungeon   : SKIPPED (-SkipDungeon)'
} else {
    Write-Host ('[check_all] dungeon   : ' + $DungeonCsproj + ' --seeds ' + $DungeonSeeds)
}

Push-Location -LiteralPath $RootFull
$failCode = 0

try {
    # -----------------------------------------------------------------------
    # Step 1: main build, default configuration (Godot-compatible output paths)
    # -----------------------------------------------------------------------
    $argList = @('build', $MainCsproj, '-nologo', '-v', 'minimal')
    if ($NoRestore) { $argList += '--no-restore' }
    $c = Invoke-Step -Name '1/4 dotnet build (main, default config)' -Exe 'dotnet' -ArgList $argList
    if ($c -ne 0 -and $failCode -eq 0) { $failCode = $c }

    # -----------------------------------------------------------------------
    # Step 1b: same project, Host configuration
    #
    #   The default configuration only compiles scripts/core/** (engine-free). The
    #   engine-coupled layers (scripts/host/**, the only place allowed to use Godot)
    #   are compiled by the "Host" configuration. If the property is not defined yet
    #   the standard Debug configuration is built twice, which costs a cached no-op
    #   build and keeps this script working before and after the property lands.
    # -----------------------------------------------------------------------
    $argList = @('build', $MainCsproj, '-nologo', '-v', 'minimal', '-c', 'Host')
    if ($NoRestore) { $argList += '--no-restore' }
    $c = Invoke-Step -Name '1b/4 dotnet build (main, -c Host: Godot-coupled layers)' -Exe 'dotnet' -ArgList $argList
    if ($c -ne 0 -and $failCode -eq 0) { $failCode = $c }

    # -----------------------------------------------------------------------
    # Step 2: architecture guard (GS001~GS007). Runs through PowerShell 5.1
    # (powershell.exe) - not pwsh - per docs/rules/02-tech-selection.md section 3.5 S5.
    # -----------------------------------------------------------------------
    $ps51 = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path -LiteralPath $ps51)) { $ps51 = 'powershell.exe' }
    $guardArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $GuardPs1)
    $c = Invoke-Step -Name '2/4 tools/check_architecture.ps1' -Exe $ps51 -ArgList $guardArgs
    if ($c -ne 0 -and $failCode -eq 0) { $failCode = $c }

    # -----------------------------------------------------------------------
    # Step 3: ECS test suite (docs/architecture/ECS.md section 10)
    # -----------------------------------------------------------------------
    if (-not $SkipTests) {
        $argList = @('test', $TestCsproj, '-nologo', '-v', 'minimal',
                     '--logger', 'console;verbosity=normal')
        if ($NoRestore) { $argList += '--no-restore' }
        $c = Invoke-Step -Name '3/4 dotnet test (tests/BiuBiuOrigin.Tests.csproj)' -Exe 'dotnet' -ArgList $argList
        if ($c -ne 0 -and $failCode -eq 0) { $failCode = $c }
    }

    # -----------------------------------------------------------------------
    # Step 4: dungeon 200-seed property check (ARCHITECTURE.md 14.4, invariants 1-6)
    #
    #   Why a separate tool instead of an MSTest case: see the header comment.
    #   `dotnet run` propagates the tool's exit code, so a failed invariant fails
    #   check_all exactly like a red test would.
    # -----------------------------------------------------------------------
    if (-not $SkipDungeon) {
        $reportPath = Join-Path $RootFull 'tools\dungeon_check\report.md'
        $argList = @('run', '--project', $DungeonCsproj, '-c', 'Release',
                     '--', '--seeds', "$DungeonSeeds", '--out', $reportPath)
        if ($NoRestore) { $argList += '--no-restore' }
        $c = Invoke-Step -Name ('4/4 tools/dungeon_check (seeds=' + $DungeonSeeds + ')') -Exe 'dotnet' -ArgList $argList
        if ($c -ne 0 -and $failCode -eq 0) { $failCode = $c }

        # Step 4b: the SAME property test, but with the defaults the 3D scene actually ships.
        #
        #   Why: step 4 runs an editor-friendly 9x24 / side=3 config. The 3D debug scene
        #   runs DungeonOptions' defaults, and on 2026-11-05 those two drifted far enough
        #   apart that 8x8 / side=1 could not fit a hidden room AT ALL -- every seed threw
        #   "地牢生成失败" on scene load, while step 4 stayed green the whole time.
        #   A green step 4 must mean "the game can open", so the shipped numbers get their
        #   own pass. --options mirrors DungeonOptions' *Default constants (see the comment
        #   on DungeonOptionsDefaults() in tools/dungeon_check/Program.cs).
        $argList = @('run', '--project', $DungeonCsproj, '-c', 'Release',
                     '--', '--options', '--seeds', "$DungeonSeeds",
                     '--out', (Join-Path $RootFull 'tools\dungeon_check\report-options.md'))
        if ($NoRestore) { $argList += '--no-restore' }
        $c = Invoke-Step -Name ('4b/4 tools/dungeon_check --options (3D 实机默认值)') -Exe 'dotnet' -ArgList $argList
        if ($c -ne 0 -and $failCode -eq 0) { $failCode = $c }
    }
} finally {
    Pop-Location
}

# ---------------------------------------------------------------------------
# 4. Summary
# ---------------------------------------------------------------------------
Write-Host ''
Write-Host '=== check_all summary ===' -ForegroundColor Cyan
foreach ($r in $script:Results) {
    $tag = 'PASS'
    $color = 'Green'
    if ($r.Code -ne 0) { $tag = 'FAIL'; $color = 'Red' }
    $line = '  [{0}] {1}  exit={2}  {3}s' -f $tag, $r.Name, $r.Code, $r.Seconds
    Write-Host $line -ForegroundColor $color
}

if ($failCode -eq 0) {
    Write-Host '[check_all] RESULT: all steps passed' -ForegroundColor Green
    exit 0
}

Write-Host ('[check_all] RESULT: FAILED (first failing exit code = ' + $failCode + ')') -ForegroundColor Red
exit $failCode
