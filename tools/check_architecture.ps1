<#
.SYNOPSIS
    BiuBiuOrigin architecture guard (GS001~GS007). Run before declaring a task "done".

.DESCRIPTION
    Part of the Definition of Done (rule 7 / docs/rules/07-definition-of-done.md):
        dotnet build
        .\tools\check_architecture.ps1        <- this script, must exit 0
        update docs/architecture/ARCHITECTURE.md
    Honours the AGENTS.md red lines; the GDScript whitelist is driven by
    config/architecture-rules.json (the only place an exception may be registered).

    Check ids (all explained in docs/rules/01-gdscript-ban.md and docs/rules/02-tech-selection.md):
        GS001  error    .gd outside allowGDScript / inside forbidGDScript       (rule 1)
        GS002  warning  a UI folder mixes .gd and .cs (UI may use both)         (rule 1/2)
        GS003  error    *.cpp/*.h/*.hpp outside native/                         (rule 2)
        GS004  warning  scripts/ui/** references Native.* or using Game.*       (rule 2)
        GS005  warning  numeric literals hardcoded in scripts/**/*.cs           (rule 4)
        GS006  error    docs/architecture/ARCHITECTURE.md missing               (rule 3)
               warning  ARCHITECTURE.md has no changelog heading or is stale        (rule 3)
        GS007  warning  Game.* assembly / namespace outside scripts/            (rule 2)

    Exit code: 1 if a NEW error exists (not baselined), otherwise 0 (warnings keep 0).
    -UpdateBaseline always exits 0.

    The project starts as an empty shell: missing scripts/, native/, config/, docs/
    or missing git are normal and never throw. Safe to run at any time.

.PARAMETER ProjectPath
    Project root. Default: the parent folder of this script (<repo>\tools\..).

.PARAMETER Baseline
    Baseline json, absolute or relative to the project root.
    Default: docs/architecture/.guard-baseline.json

.PARAMETER UpdateBaseline
    Write every current violation into the baseline (UTF-8 no BOM), exit 0.

.PARAMETER AsJson
    Emit one json object on stdout (or into -OutFile).

.PARAMETER Quiet
    Only the summary line (and the fix list when not -AsJson).

.PARAMETER OutFile
    Also write the report to this file (UTF-8 no BOM). With -AsJson the file holds json,
    otherwise a markdown table. Default: none.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\check_architecture.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\check_architecture.ps1 -AsJson -OutFile out\guard.json

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\check_architecture.ps1 -UpdateBaseline
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$ProjectPath = '',

    [string]$Baseline = 'docs/architecture/.guard-baseline.json',

    [switch]$UpdateBaseline,

    [switch]$AsJson,

    [switch]$Quiet,

    [string]$OutFile = ''
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# NOTE: this file is kept pure ASCII on purpose. Every user-facing string is built from
# \uXXXX escapes or $MSG tables, so it parses identically under any console code page
# (including PowerShell 5.1 reading a BOM-less file). Do not "simplify" the escapes.

# =============================================================================
# 0. Console encoding (cosmetic; never fatal)
# =============================================================================
try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
    if (-not [Console]::IsOutputRedirected) {
        $null = & cmd.exe /c "chcp 65001 >nul 2>nul"
    }
} catch {
    # ignore
}

# =============================================================================
# 1. Text tables
# =============================================================================
$MSG = @{}

$MSG.SCAN_HEADER   = "scan root : {0}   files={1}"
$MSG.RESULT_OK     = "[OK]   no new violations"
$MSG.RESULT_WARN   = "[WARN] warnings only - exit 0"
$MSG.RESULT_FAIL   = "[FAIL] new errors found - fix before declaring the task done"
$MSG.PATH_MISSING  = "error: project root not found -> {0}"

# StrictMode 2.0 refuses $MSG.$computedName lookups, so read through Get-Msg.
function Get-Msg {
    param([string]$Name)
    if ($MSG.ContainsKey($Name)) { return [string]$MSG[$Name] }
    return ''
}

$MSG.GS001_DESC = "GDScript outside whitelist (rule 1)"
$MSG.GS002_DESC = "UI folder mixes .gd and .cs (warning only: the UI layer may use both)"
$MSG.GS003_DESC = "C++ source must live under native/ (rule 2)"
$MSG.GS004_DESC = "UI must not reference Game.Combat/Actor/Skill/Spawn or Native.* (rule 2)"
$MSG.GS005_DESC = "numeric literal hardcoded in C# gameplay code (rule 4)"
$MSG.GS006_DESC = "architecture outline missing or stale (rule 3)"
$MSG.GS007_DESC = "Game.* assembly / namespace outside scripts/ (rule 2)"

$MSG.HINT_GS001 = "Rewrite this script in C# under scripts/, or move the logic into GDExtension (native/). If it is a pure UI layout / editor-only tool, move it to scripts/ui/** or addons/** and register the exception in config/architecture-rules.json + AGENTS.md."
$MSG.HINT_GS002 = "Keep one language per UI sub-folder, or split the folder. Mixed UI language is a warning, not a blocker."
$MSG.HINT_GS003 = "Move C++ code to native/src/, native/include/ (entry point to native/gdextension/). No source trees outside native/."
$MSG.HINT_GS004 = "UI may only read Game.Core (interfaces) and Game.Data (DTOs). Route Combat/Actor/Skill/Spawn through a view-model on Game.Core; never call Native.* from UI."
$MSG.HINT_GS005 = "Move gameplay numbers into data tables (Excel -> data/generated/) or a Game.Data numeric type. Real constants get a named const; a justified line gets a trailing // arch-ok: <reason>."
$MSG.HINT_GS006 = "Update docs/architecture/ARCHITECTURE.md (in place), keep the changelog section (the '## ' + changelog heading) and refresh the timestamp line. See docs/rules/07-definition-of-done.md."
$MSG.HINT_GS007 = "Put Game.* assemblies/sources under scripts/<layer>/. Non-game tools may live under tools/ with any assembly name."



$BOM_CHAR = [string][char]0xFEFF
# Changelog heading, numbers built from code points so this source stays pure ASCII:
#   '##' + optional "\s*(<digits>[.,)\u3001\uFF09]\s*)?" + changelog chars
$CHANGELOG_WORD = [string][char]0x53D8 + [char]0x66F4 + [char]0x65E5 + [char]0x5FD7
$OUTLINE_CHANGELOG_HEADING = '## ' + $CHANGELOG_WORD
$CHANGELOG_HEADING_RX = New-Object System.Text.RegularExpressions.Regex ('^##\s*(?:\d+\s*[.,)\u3001\uFF09]?\s*)?' + $CHANGELOG_WORD)
$OUTLINE_DATE_RX = New-Object System.Text.RegularExpressions.Regex '\d{4}-\d{2}-\d{2}'

# =============================================================================
# 2. Small helpers
# =============================================================================
function Get-Count {
    # StrictMode 2.0 turns a single-element array into a bare object, so .Count would throw.
    # NOTE: "return @($Value).Count" is argument-mode output and would yield the STRING "1";
    # the array must be materialised into a variable before reading .Length.
    param($Value)
    if ($null -eq $Value) { return 0 }
    $arr = @($Value)
    return [int]$arr.Length
}

function Get-Utf8NoBom {
    return New-Object System.Text.UTF8Encoding $false
}

function Write-TextFileUtf8NoBom {
    param([string]$Path, [string]$Content)
    $full = [System.IO.Path]::GetFullPath($Path)
    $dir = [System.IO.Path]::GetDirectoryName($full)
    if ($dir.Length -gt 0 -and -not (Test-Path -LiteralPath $dir)) {
        $null = New-Item -ItemType Directory -Path $dir -Force
    }
    [System.IO.File]::WriteAllText($full, $Content, (Get-Utf8NoBom))
}

function Read-TextFileSafe {
    param([string]$FullPath)
    try {
        $bytes = [System.IO.File]::ReadAllBytes($FullPath)
        if ($bytes.Length -eq 0) { return '' }
        return [System.IO.File]::ReadAllText($FullPath, [System.Text.Encoding]::UTF8)
    } catch {
        return ''
    }
}

function Normalize-RelPath {
    param([string]$Path)
    if ([string]::IsNullOrEmpty($Path)) { return '' }
    $p = $Path -replace '\\', '/'
    while ($p.StartsWith('./')) { $p = $p.Substring(2) }
    return $p
}

function Get-RelToRoot {
    # Forward-slash path relative to the project root ('' for the root itself).
    param([string]$RootFull, [string]$Full)
    $r = $RootFull
    if (-not $r.EndsWith('\')) { $r = $r + '\' }
    if ($Full.Length -ge $r.Length -and $Full.Substring(0, $r.Length).Equals($r, [System.StringComparison]::OrdinalIgnoreCase)) {
        return (Normalize-RelPath $Full.Substring($r.Length))
    }
    return (Normalize-RelPath $Full)
}

function Test-PatternMatch {
    <#
      Glob semantics used everywhere (deliberately simple and predictable):
        'scripts/ui/**'   -> anything under scripts/ui/
        'scripts/ui/*.gd' -> .gd anywhere under scripts/ui/ (single * crosses folders)
        'scripts/ui'      -> the folder and everything below it
        '*.cs'            -> any .cs at any depth
      ** and * are both "any characters"; '?' is one character.
    #>
    param([string]$Pattern, [string]$Path)
    if ([string]::IsNullOrEmpty($Pattern)) { return $false }
    $p = (Normalize-RelPath $Pattern.Trim()).TrimStart([char]'/')
    if ($p.Length -eq 0) { return $false }
    $hadTrailingSlash = $p.EndsWith('/')
    $p = $p.TrimEnd([char]'/')
    $pathN = (Normalize-RelPath $Path).TrimStart([char]'/')

    if ($pathN.Length -eq 0) { return $false }

    $rx = [regex]::Escape($p)
    $rx = $rx.Replace('\*\*', '.*')
    $rx = $rx.Replace('\*', '[^/]*')
    $rx = $rx.Replace('\?', '[^/]')

    if ([regex]::IsMatch($pathN, '^(?i)' + $rx + '$')) { return $true }
    # Folder patterns (no file extension, or explicit trailing slash) also match their subtree.
    $looksLikeFolder = ($hadTrailingSlash -or ($p.IndexOf('.') -lt 0))
    if ($looksLikeFolder) {
        if ([regex]::IsMatch($pathN, '^(?i)' + $rx + '/')) { return $true }
    }
    return $false
}

function Test-AnyPattern {
    param($Patterns, [string]$Path)
    if ($null -eq $Patterns) { return $false }
    foreach ($pt in @($Patterns)) {
        if ($pt -isnot [string]) { continue }
        if (Test-PatternMatch -Pattern $pt -Path $Path) { return $true }
    }
    return $false
}

function Get-FirstMatchingPattern {
    param($Patterns, [string]$Path)
    if ($null -eq $Patterns) { return '' }
    foreach ($pt in @($Patterns)) {
        if ($pt -isnot [string]) { continue }
        if (Test-PatternMatch -Pattern $pt -Path $Path) { return $pt }
    }
    return ''
}

function Get-Prop {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $prop = $Object.PSObject.Properties[$Name]
    if ($null -eq $prop) { return $null }
    return $prop.Value
}

function Get-StrArray {
    <#
      Returns a STRONG-TYPED string array (never a bare string, never char[]).
      Under PowerShell 5.1 "return @($v)" with one string leaks a string whose [0] is a
      System.Char ("scripts/"[0] = 's'), which silently truncated every config path.
      Always build the result with ,@() and copy element by element into a typed array.
    #>
    param($Object, [string]$Name)
    $out = New-Object System.Collections.ArrayList
    $v = Get-Prop -Object $Object -Name $Name
    if ($null -ne $v) {
        if ($v -is [string]) {
            if ($v.Trim().Length -gt 0) { $null = $out.Add([string]$v) }
        } else {
            foreach ($i in @($v)) {
                if ($null -eq $i) { continue }
                $s = [string]$i
                if ($s.Length -gt 0) { $null = $out.Add($s) }
            }
        }
    }
    [string[]]$typed = @()
    foreach ($s in $out) { $typed += [string]$s }
    return , $typed
}
function Get-BoolOr {
    param($Object, [string]$Name, [bool]$Default)
    $v = Get-Prop -Object $Object -Name $Name
    if ($null -eq $v) { return $Default }
    try { return [bool]$v } catch { return $Default }
}

function Get-IntOr {
    param($Object, [string]$Name, [int]$Default)
    $v = Get-Prop -Object $Object -Name $Name
    if ($null -eq $v) { return $Default }
    $n = 0
    if ([int]::TryParse([string]$v, [ref]$n)) { return $n }
    return $Default
}

function Get-StrOr {
    param($Object, [string]$Name, [string]$Default)
    $v = Get-Prop -Object $Object -Name $Name
    if ($null -eq $v) { return $Default }
    $s = [string]$v
    if ($s.Length -eq 0) { return $Default }
    return $s
}

function Get-Sha4 {
    param([string]$Text)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Text))
    } finally {
        $sha.Dispose()
    }
    $sb = New-Object System.Text.StringBuilder
    for ($i = 0; $i -lt 4; $i++) { $null = $sb.Append($bytes[$i].ToString('x2')) }
    return $sb.ToString()
}

# =============================================================================
# 3. Project root
# =============================================================================
if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $scriptDir = $PSScriptRoot
    if ([string]::IsNullOrWhiteSpace($scriptDir)) {
        $scriptDir = Split-Path -Parent -Path $MyInvocation.MyCommand.Path
    }
    $ProjectPath = Split-Path -Parent -Path $scriptDir
}
$RootFull = [System.IO.Path]::GetFullPath($ProjectPath)
$RootFull = $RootFull.TrimEnd('\')

if (-not (Test-Path -LiteralPath $RootFull -PathType Container)) {
    $msg = $MSG.PATH_MISSING -f $RootFull
    if ($AsJson) {
        ([ordered]@{ tool = 'check_architecture.ps1'; fatal = $msg; exitCode = 1; violations = @() }) | ConvertTo-Json -Depth 8
    } else {
        Write-Host $msg -ForegroundColor Red
    }
    exit 1
}

$BaselineFull = $Baseline
if (-not [System.IO.Path]::IsPathRooted($BaselineFull)) {
    $BaselineFull = [System.IO.Path]::GetFullPath((Join-Path $RootFull $Baseline))
}

# =============================================================================
# 4. Read config/architecture-rules.json  (missing / broken -> built-in defaults)
# =============================================================================
$ConfigRel = 'config/architecture-rules.json'
$ConfigFull = Join-Path $RootFull 'config\architecture-rules.json'
$Cfg = $null
$CfgState = 'missing'      # missing | loaded | error
$CfgError = ''
if (Test-Path -LiteralPath $ConfigFull -PathType Leaf) {
    try {
        $cfgText = Read-TextFileSafe $ConfigFull
        if ([string]::IsNullOrWhiteSpace($cfgText) -or $cfgText.Trim() -eq '{}') {
            $CfgState = 'missing'
        } else {
            $Cfg = $cfgText | ConvertFrom-Json
            $CfgState = 'loaded'
        }
    } catch {
        $CfgState = 'error'
        $CfgError = $_.Exception.Message
        $Cfg = $null
    }
}

# ---- defaults (the shipped config mirrors these; the script never depends on it) ----
$AllowGDScript = Get-StrArray $Cfg 'allowGDScript'
if ((Get-Count $AllowGDScript) -eq 0 -and $null -eq (Get-Prop $Cfg 'allowGDScript')) {
    $AllowGDScript = @('scripts/ui/**', 'addons/**', 'tools/probes/**')
}
$ForbidGDScript = Get-StrArray $Cfg 'forbidGDScript'
$IgnorePatterns = Get-StrArray $Cfg 'ignore'
if ($null -eq (Get-Prop $Cfg 'ignore')) {
    $IgnorePatterns = @('.godot/**', 'addons/**', 'obj/**', 'bin/**', 'native/build/**', 'native/gdextension/libgodot-cpp/**', 'data/generated/**', 'tools/probes/**')
}

$Layers = Get-Prop $Cfg 'layers'
$LogicRoots = Get-StrArray $Layers 'logicRoots'
if ((Get-Count $LogicRoots) -eq 0) { $LogicRoots = @('scripts/') }
$NativeRoot = Get-StrOr $Layers 'nativeRoot' 'native/'
$UiRoot = Get-StrOr $Layers 'uiRoot' 'scripts/ui/'
$NativeRootPattern = (Normalize-RelPath $NativeRoot).TrimEnd([char]'/') + '/**'
$UiRootPattern = (Normalize-RelPath $UiRoot).TrimEnd([char]'/') + '/**'

# ui roots: uiRoot plus any allowGDScript entry that lives under it (usually just uiRoot)
$UiRootPatterns = @($UiRootPattern)
foreach ($a in $AllowGDScript) {
    $an = Normalize-RelPath $a
    if ($UiRootPatterns -contains $an) { continue }
    if ($an.StartsWith((Normalize-RelPath $UiRoot).TrimEnd([char]'/') + '/')) {
        if (-not $UiRootPatterns.Contains($an)) { $UiRootPatterns += $an }
    }
}

$Num = Get-Prop $Cfg 'numericLiterals'
$NumEnabled = Get-BoolOr $Num 'enabled' $true
$NumScope = Get-StrArray $Num 'scope'
if ((Get-Count $NumScope) -eq 0) { $NumScope = @('scripts/**/*.cs') }
$NumExclude = Get-StrArray $Num 'exclude'
$NumEscapeComment = Get-StrOr $Num 'escapeComment' '// arch-ok:'
$NumMaxReported = Get-IntOr $Num 'maxReported' 50
if ($NumMaxReported -lt 1) { $NumMaxReported = 1 }
$NumAllowValuesRaw = Get-Prop $Num 'allowValues'
$NumAllowTokens = @()
$NumAllowInts = @()
$NumAllowFloats = @()
if ($null -ne $NumAllowValuesRaw) {
    foreach ($v in @($NumAllowValuesRaw)) {
        if ($null -eq $v) { continue }
        $numAllowTokens += ([string]$v).ToLowerInvariant()
        $s = ([string]$v).Trim().ToLowerInvariant()
        $isFloat = $s.Contains('.') -or $s.Contains('e')
        if ($isFloat) { $NumAllowFloats += $s } else { $NumAllowInts += $s }
    }
}
if ((Get-Count $NumAllowTokens) -eq 0) {
    $NumAllowTokens = @('0', '1', '-1', '0.0', '1.0', '0.5')
    $NumAllowInts = @('0', '1', '-1')
    $NumAllowFloats = @('0.0', '1.0', '0.5')
}

# =============================================================================
# 5. Walk the project once (missing folders simply do not appear)
# =============================================================================
$AllFiles = New-Object System.Collections.ArrayList
$walkError = ''
try {
    $fsItems = Get-ChildItem -LiteralPath $RootFull -Recurse -File -Force -ErrorAction SilentlyContinue
    foreach ($fi in $fsItems) {
        if ($null -eq $fi) { continue }
        $null = $AllFiles.Add([pscustomobject]@{
            rel  = (Get-RelToRoot -RootFull $RootFull -Full $fi.FullName)
            name = $fi.Name
            full = $fi.FullName
            ext  = $fi.Extension.ToLowerInvariant()
            time = $fi.LastWriteTime
        })
    }
} catch {
    $walkError = $_.Exception.Message
}

# ignore filter (applies to everything below)
$UsefulFiles = New-Object System.Collections.ArrayList
foreach ($f in $AllFiles) {
    if (Test-AnyPattern -Patterns $IgnorePatterns -Path $f.rel) { continue }
    $null = $UsefulFiles.Add($f)
}

$GdFiles = @()
$CsFiles = @()
$CppFiles = @()
$SceneFiles = @()
foreach ($f in $UsefulFiles) {
    if ($f.ext -eq '.gd') { $GdFiles += $f; continue }
    if ($f.ext -eq '.cs') { $CsFiles += $f; continue }
    if ($f.ext -eq '.tscn') { $SceneFiles += $f; continue }
    if ($f.ext -eq '.cpp' -or $f.ext -eq '.cc' -or $f.ext -eq '.cxx' -or $f.ext -eq '.c' -or $f.ext -eq '.h' -or $f.ext -eq '.hpp' -or $f.ext -eq '.hxx' -or $f.ext -eq '.inl') { $CppFiles += $f; continue }
}

# =============================================================================
# 6. Violation collector
# =============================================================================
$Violations = New-Object System.Collections.ArrayList

function Add-Violation {
    param(
        [string]$Rule,
        [string]$Severity,
        [string]$FileRel,
        [int]$Line,
        [string]$Message,
        [string]$Hint,
        [string]$Key
    )
    $f = $FileRel
    if ([string]::IsNullOrEmpty($f)) { $f = '.' }
    $sev = 'warning'
    if ($Severity -eq 'error') { $sev = 'error' }
    if ([string]::IsNullOrEmpty($Key)) {
        if ($Line -gt 0) {
            $Key = $Rule + '|' + $f + '|' + [string]$Line + '|' + (Get-Sha4 $Message)
        } else {
            $Key = $Rule + '|' + $f + '|' + (Get-Sha4 $Message)
        }
    }
    $color = 'Yellow'
    if ($sev -eq 'error') { $color = 'Red' }
    $null = $Violations.Add([pscustomobject]@{
        rule     = $Rule
        severity = $sev
        file     = $f
        line     = $Line
        message  = $Message
        hint     = $Hint
        key      = $Key
        color    = $color
    })
}

# =============================================================================
# 7. GS001 / GS002  -  GDScript whitelist + UI language consistency
# =============================================================================
$WhitelistedGd = New-Object System.Collections.ArrayList
foreach ($f in $GdFiles) {
    $rel = $f.rel
    $forbidPat = Get-FirstMatchingPattern -Patterns $ForbidGDScript -Path $rel
    $allowPat = Get-FirstMatchingPattern -Patterns $AllowGDScript -Path $rel

    if ($forbidPat.Length -gt 0 -and $allowPat.Length -eq 0) {
        Add-Violation -Rule 'GS001' -Severity 'error' -FileRel $rel -Line 1 `
            -Message ((Get-Msg 'GS001_DESC') + ' -> forbidGDScript: ' + $forbidPat) -Hint $MSG.HINT_GS001 -Key ('GS001|forbid|' + $rel)
        continue
    }
    if ($allowPat.Length -gt 0) {
        $null = $WhitelistedGd.Add($f)
        continue
    }
    Add-Violation -Rule 'GS001' -Severity 'error' -FileRel $rel -Line 1 `
        -Message ((Get-Msg 'GS001_DESC') + ' -> not in allowGDScript: [' + ($AllowGDScript -join ', ') + ']') -Hint $MSG.HINT_GS001 -Key ('GS001|' + $rel)
}

# GS002: one language per folder. RULE 1 ALLOWS .gd IN THE UI LAYER, SO THIS IS ALWAYS A
# WARNING (Lead decision 2026-11-03). Only folders under the UI root(s) are reported.
$dirHasGd = @{}
$dirHasCs = @{}
$dirOrder = New-Object System.Collections.ArrayList
foreach ($f in (@($GdFiles) + @($CsFiles))) {
    $rel = $f.rel
    $idx = $rel.LastIndexOf('/')
    $dir = ''
    if ($idx -ge 0) { $dir = $rel.Substring(0, $idx) }
    $shallow = $dir + '/'
    if (-not (Test-AnyPattern -Patterns $UiRootPatterns -Path $shallow)) { continue }
    if (-not $dirHasGd.ContainsKey($dir)) { $dirHasGd[$dir] = 0 }
    if (-not $dirHasCs.ContainsKey($dir)) { $dirHasCs[$dir] = 0 }
    if ($f.ext -eq '.gd') { $dirHasGd[$dir] = $dirHasGd[$dir] + 1 } else { $dirHasCs[$dir] = $dirHasCs[$dir] + 1 }
    if (-not $dirOrder.Contains($dir)) { $null = $dirOrder.Add($dir) }
}
foreach ($dir in $dirOrder) {
    if ($dirHasGd[$dir] -gt 0 -and $dirHasCs[$dir] -gt 0) {
        $label = $dir
        if ($label.Length -eq 0) { $label = '.' }
        Add-Violation -Rule 'GS002' -Severity 'warning' -FileRel ($dir + '/') -Line 0 `
            -Message ((Get-Msg 'GS002_DESC') + ' -> ' + $label + ' .gd=' + $dirHasGd[$dir] + ' .cs=' + $dirHasCs[$dir]) `
            -Hint $MSG.HINT_GS002 -Key ('GS002|' + $dir)
    }
}

# Scenes must attach the language the whitelist allows for their own folder.
foreach ($f in $SceneFiles) {
    $text = Read-TextFileSafe $f.full
    if ($text.IndexOf('ExtResource', [System.StringComparison]::Ordinal) -lt 0) { continue }
    $sceneAllow = Get-FirstMatchingPattern -Patterns $AllowGDScript -Path $f.rel
    if ($sceneAllow.Length -eq 0) { continue }
    if (-not [regex]::IsMatch($text, '(?i)\.cs\b')) { continue }
    Add-Violation -Rule 'GS001' -Severity 'warning' -FileRel $f.rel -Line 0 `
        -Message ('scene under a GDScript whitelist folder attaches C# scripts; keep one language (' + $sceneAllow + ')') `
        -Hint $MSG.HINT_GS002 -Key ('GS001|scene|' + $f.rel)
}

# =============================================================================
# 8. GS003  -  C++ sources must live under native/
# =============================================================================
foreach ($f in $CppFiles) {
    if (Test-AnyPattern -Patterns $IgnorePatterns -Path $f.rel) { continue }
    if (Test-AnyPattern -Patterns @($NativeRootPattern) -Path $f.rel) { continue }
    Add-Violation -Rule 'GS003' -Severity 'error' -FileRel $f.rel -Line 1 `
        -Message ((Get-Msg 'GS003_DESC') + ' -> expected under ' + $NativeRoot) -Hint $MSG.HINT_GS003 -Key ('GS003|' + $f.rel)
}

# =============================================================================
# 9. GS004  -  UI layer must not reach into Native.* / Game.* directly
# =============================================================================
# UI layer may READ from Game.Core / Game.Data (read-only views and DTOs); it must never reach
# into Game.Combat / Actor / Skill / Spawn, nor call Native.* directly.
# Official wording: docs/architecture/ARCHITECTURE.md 2.3 / 2.4 (Lead ruling 2026-11-03).
$NativeRefRegex  = [regex]'(?<![A-Za-z0-9_])Native\.[A-Za-z_]'
$UiForbiddenLayerRx = [regex]'(?<![A-Za-z0-9_.])Game\.(?:Combat|Actor|Skill|Spawn)(?![A-Za-z0-9_])'

foreach ($f in (@($GdFiles) + @($CsFiles))) {
    if (-not (Test-AnyPattern -Patterns $UiRootPatterns -Path $f.rel)) { continue }
    if (Test-AnyPattern -Patterns $IgnorePatterns -Path $f.rel) { continue }
    $text = Read-TextFileSafe $f.full
    if ($text.Length -eq 0) { continue }
    $lines = $text -split "`n"
    $ln = 0
    foreach ($line in $lines) {
        $ln++
        $bare = $line.Replace($BOM_CHAR, '').TrimEnd("`r")
        $trimmed = $bare.Trim()
        # skip pure comment lines; a trailing comment is stripped below so "// Game.Combat is banned"
        # inside a comment never raises a finding
        if ($trimmed.StartsWith('//') -or $trimmed.StartsWith('#') -or $trimmed.StartsWith('*') -or $trimmed.StartsWith('/*')) { continue }
        $code = $bare
        $cIx = $code.IndexOf('//')
        if ($cIx -ge 0) { $code = $code.Substring(0, $cIx) }
        $hit = ''
        if ($NativeRefRegex.IsMatch($code)) {
            $hit = 'Native.*'
        } else {
            $mLayer = $UiForbiddenLayerRx.Match($code)
            if ($mLayer.Success) {
                $hit = 'Game.' + $mLayer.Value.Substring(5)
            }
        }
        if ($hit.Length -eq 0) { continue }
        $snippet = $trimmed
        if ($snippet.Length -gt 120) { $snippet = $snippet.Substring(0, 120) }
        Add-Violation -Rule 'GS004' -Severity 'warning' -FileRel $f.rel -Line $ln `
            -Message ((Get-Msg 'GS004_DESC') + ' -> ' + $hit + ' : ' + $snippet) -Hint $MSG.HINT_GS004
    }
}
# =============================================================================
# 10. GS005  -  numeric literals hardcoded in C# gameplay code
# =============================================================================
$NumTokenRegex = [regex]'(?<![A-Za-z0-9_.])\d+(?:\.\d+)?(?:[eE][-+]\d+)?[fFdDmM]?(?![A-Za-z0-9_])'
$NumAssignRegex = [regex]'(?<![<>=!+\-*/%&|^])=(?![=>])'
$NumStats = [pscustomobject]@{ scannedFiles = 0; candidates = 0; reported = 0; shown = 0; hidden = 0; escaped = 0; excludedFiles = 0 }

if ($NumEnabled) {
    foreach ($f in $CsFiles) {
        if (-not (Test-AnyPattern -Patterns $NumScope -Path $f.rel)) { continue }
        if (Test-AnyPattern -Patterns $NumExclude -Path $f.rel) { $NumStats.excludedFiles++; continue }
        if (Test-AnyPattern -Patterns $IgnorePatterns -Path $f.rel) { continue }
        $text = Read-TextFileSafe $f.full
        if ($text.Length -eq 0) { continue }
        $NumStats.scannedFiles++
        $lines = $text -split "`n"
        $inBlock = $false
        $ln = 0
        foreach ($line0 in $lines) {
            $ln++
            $line = $line0.Replace($BOM_CHAR, '').TrimEnd("`r")
            $t = $line.Trim()
            $skip = $false
            if ($inBlock) {
                $endIx = $t.IndexOf('*/')
                if ($endIx -lt 0) {
                    $skip = $true
                } else {
                    $t = $t.Substring($endIx + 2).Trim()
                    $inBlock = $false
                }
            }
            if (-not $skip) {
                if ($t.Length -eq 0 -or $t.StartsWith('//') -or $t.StartsWith('/*') -or $t.StartsWith('#')) {
                    $skip = $true
                } elseif ($t.IndexOf($NumEscapeComment, [System.StringComparison]::Ordinal) -ge 0) {
                    $skip = $true
                    $NumStats.escaped++
                } elseif ($t.Contains('/*') -and $t.IndexOf('*/', $t.IndexOf('/*')) -lt 0) {
                    $inBlock = $true
                    $skip = $true
                }
            }
            if ($skip) { continue }

            # strip string / char literals and trailing comment so "1.5" in a message is not a number
            $s = [regex]::Replace($t, '"(?:[^"\\]|\\.)*"', '""')
            $s = [regex]::Replace($s, "'(?:[^'\\]|\\.)*'", "''")
            $cIx = $s.IndexOf('//')
            if ($cIx -ge 0) { $s = $s.Substring(0, $cIx) }

            $mAsg = $NumAssignRegex.Match($s)
            if (-not $mAsg.Success) { continue }
            $rhs = $s.Substring($mAsg.Index + 1)
            foreach ($mNum in $NumTokenRegex.Matches($rhs)) {
                $tok = $mNum.Value
                $NumStats.candidates++
                # negative numbers: look one char to the left
                $tokStart = $mAsg.Index + 1 + $mNum.Index
                $signedTok = $tok
                if ($tokStart -gt 0 -and $s.Substring($tokStart - 1, 1) -eq '-') {
                    $signedTok = '-' + $tok
                }
                $low = $signedTok.ToLowerInvariant()
                if ($NumAllowTokens -contains $low) { continue }
                $base = $low.TrimEnd([char]'f', [char]'d', [char]'m')
                if ($NumAllowInts -contains $base -or $NumAllowFloats -contains $base -or $NumAllowFloats -contains $low) { continue }
                $NumStats.reported++
                $snippet = $t
                if ($snippet.Length -gt 110) { $snippet = $snippet.Substring(0, 110) }
                if ($NumStats.shown -lt $NumMaxReported) {
                    $NumStats.shown++
                    Add-Violation -Rule 'GS005' -Severity 'warning' -FileRel $f.rel -Line $ln `
                        -Message ((Get-Msg 'GS005_DESC') + ' -> ' + $signedTok + '   ' + $snippet) `
                        -Hint $MSG.HINT_GS005 -Key ('GS005|' + $f.rel + '|' + [string]$ln + '|' + (Get-Sha4 $t))
                } else {
                    $NumStats.hidden++
                }
            }
        }
    }
}

# =============================================================================
# 11. GS006  -  architecture outline present / fresh
# =============================================================================
$OutlineRel = 'docs/architecture/ARCHITECTURE.md'
$OutlineFull = Join-Path $RootFull 'docs\architecture\ARCHITECTURE.md'

$newestSource = $null
foreach ($f in $UsefulFiles) {
    $rel = $f.rel
    if ($rel.StartsWith('docs/')) { continue }
    if ($rel.StartsWith('.godot/')) { continue }
    if ($rel.StartsWith('tools/')) { continue }
    $isSource = $false
    if ($rel.StartsWith('scripts/') -or $rel.StartsWith('native/') -or $rel.StartsWith('config/') -or $rel.StartsWith('addons/') -or $rel.StartsWith('shaders/') -or $rel.StartsWith('scenes/') -or $rel.StartsWith('data/')) { $isSource = $true }
    if ($f.name -eq 'project.godot') { $isSource = $true }
    if ($f.name -eq 'BiuBiuOrigin.csproj') { $isSource = $true }
    if (-not $isSource) { continue }
    if ($f.ext -eq '.import' -or $f.ext -eq '.uid') { continue }
    if ($null -eq $newestSource -or $f.time -gt $newestSource) { $newestSource = $f.time }
}

$outlineInfo = [ordered]@{ path = $OutlineRel; exists = $false; hasChangelog = $false; stamp = ''; newestSource = ''; state = 'missing' }
if ($null -ne $newestSource) { $outlineInfo.newestSource = $newestSource.ToString('yyyy-MM-dd HH:mm') }

if (-not (Test-Path -LiteralPath $OutlineFull -PathType Leaf)) {
    Add-Violation -Rule 'GS006' -Severity 'error' -FileRel $OutlineRel -Line 0 `
        -Message ((Get-Msg 'GS006_DESC') + ' -> file does not exist') -Hint $MSG.HINT_GS006 -Key ('GS006|missing')
} else {
    $outlineInfo.exists = $true
    $outlineInfo.state = 'present'
    $oText = Read-TextFileSafe $OutlineFull
    # Changelog heading: "## <n>. changelog", "## <n>. changelog", "## changelog" ... all accepted.
    # The numbering prefix is optional and may use "." / "\\u3001" / ")" as separator. Matching used to be
    # StartsWith("## changelog"), which forced authorless documents to omit the numbering.
    $hasChangelog = $false
    foreach ($ol in ($oText -split "`n")) {
        if ($CHANGELOG_HEADING_RX.IsMatch($ol)) { $hasChangelog = $true; break }
    }
    $outlineInfo.hasChangelog = $hasChangelog
    if (-not $hasChangelog) {
        $outlineInfo.state = 'no-changelog'
        Add-Violation -Rule 'GS006' -Severity 'warning' -FileRel $OutlineRel -Line 0 `
            -Message ((Get-Msg 'GS006_DESC') + ' -> missing heading "' + $OUTLINE_CHANGELOG_HEADING + '"') -Hint $MSG.HINT_GS006 -Key ('GS006|changelog')
    }

    # Timestamp = the FIRST yyyy-MM-dd found while scanning the first 40 lines top-down.
    # The old code took the LAST non-empty line, so the usual layout
    #     "> status: v1  last update: 2026-11-03" + "## 0. quick table"
    # was mis-reported as stale. A changelog row far below never sets the stamp because the
    # scan stops at the first hit and never looks past line 40.
    $stampDate = $null
    $oLines = @($oText -split "`n")
    if ($oLines.Length -gt 40) { $oLines = $oLines[0..39] }
    foreach ($ol in $oLines) {
        $hits = $OUTLINE_DATE_RX.Matches($ol)
        if ($hits.Count -eq 0) { continue }
        $firstDate = $hits[0].Value
        $parsed = [datetime]::MinValue
        if ([datetime]::TryParseExact($firstDate, 'yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::None, [ref]$parsed)) {
            $stampDate = $parsed
            break
        }
    }

    if ($null -ne $stampDate) { $outlineInfo.stamp = $stampDate.ToString('yyyy-MM-dd') }

    # Staleness is judged ONLY through the outline's own timestamp line (a yyyy-MM-dd found
    # near the top of the document, e.g. "> last update: 2026-11-03"). File mtime is
    # deliberately NOT used: filesystem clock ordering is not a contract.
    # ordering. No stamp + sources present = warning (the outline cannot be verified).
    $stale = $false
    $why = ''
    if ($null -ne $newestSource) {
        if ($null -ne $stampDate) {
            # day granularity: a stamp of "today" always counts as fresh
            if ($stampDate.Date -lt $newestSource.Date) {
                $stale = $true
                $why = 'timestamp ' + $stampDate.ToString('yyyy-MM-dd') + ' < newest source ' + $newestSource.ToString('yyyy-MM-dd')
            }
        } else {
            $stale = $true
            $why = 'no yyyy-MM-dd timestamp line in the header, cannot prove it is fresh (newest source ' + $newestSource.ToString('yyyy-MM-dd HH:mm') + ')'
        }
    }
    if ($stale) {
        if ($outlineInfo.state -eq 'present') { $outlineInfo.state = 'stale' }
        Add-Violation -Rule 'GS006' -Severity 'warning' -FileRel $OutlineRel -Line 0 `
            -Message ((Get-Msg 'GS006_DESC') + ' -> ' + $why) -Hint $MSG.HINT_GS006 -Key 'GS006|stale'
    }
}

# =============================================================================
# 12. GS007  -  assembly / folder layering (loose)
# =============================================================================
$logicRootPrefix = ((Normalize-RelPath ($LogicRoots[0])).TrimEnd([char]'/')).TrimStart([char]'/') + '/'
foreach ($f in $UsefulFiles) {
    $rel = $f.rel
    $asmName = ''
    if ($f.ext -eq '.csproj') {
        $text = Read-TextFileSafe $f.full
        $m = [regex]::Match($text, '(?is)<AssemblyName>\s*([^<\s]+)\s*</AssemblyName>')
        if ($m.Success) { $asmName = $m.Groups[1].Value } else { $asmName = [System.IO.Path]::GetFileNameWithoutExtension($f.name) }
    } elseif ($f.ext -eq '.cs') {
        $text = Read-TextFileSafe $f.full
        $m = [regex]::Match($text, '(?m)^\s*namespace\s+(Game\.[A-Za-z_][\w.]*)')
        if ($m.Success) { $asmName = $m.Groups[1].Value }
    } else {
        continue
    }
    if ($asmName.Length -eq 0) { continue }
    if (-not $asmName.StartsWith('Game.')) { continue }
    if ($rel.StartsWith($logicRootPrefix)) { continue }
    if (Test-AnyPattern -Patterns $IgnorePatterns -Path $rel) { continue }
    Add-Violation -Rule 'GS007' -Severity 'warning' -FileRel $rel -Line 1 `
        -Message ((Get-Msg 'GS007_DESC') + ' -> ' + $asmName + ' lives outside ' + $logicRootPrefix) -Hint $MSG.HINT_GS007 -Key ('GS007|' + $rel)
}

# =============================================================================
# 13. Baseline
# =============================================================================
$BaseEntries = @()
$BaseState = 'absent'
$BaseBroken = ''
if (Test-Path -LiteralPath $BaselineFull -PathType Leaf) {
    try {
        $bText = Read-TextFileSafe $BaselineFull
        if ([string]::IsNullOrWhiteSpace($bText)) {
            $BaseState = 'empty'
        } else {
            $bObj = $bText | ConvertFrom-Json
            $items = Get-Prop $bObj 'entries'
            foreach ($it in @($items)) {
                if ($null -eq $it) { continue }
                $BaseEntries += [pscustomobject]@{ key = [string](Get-Prop $it 'key') }
            }
            $BaseState = 'loaded'
        }
    } catch {
        $BaseState = 'broken'
        $BaseBroken = $_.Exception.Message
        $BaseEntries = @()
    }
}

$NewViolations = New-Object System.Collections.ArrayList
$ExistingCount = 0
$FixedCount = 0
$BaseKeySet = @{}
foreach ($e in $BaseEntries) { if ($e.key.Length -gt 0) { $BaseKeySet[$e.key] = $true } }
foreach ($v in $Violations) {
    if ($BaseKeySet.ContainsKey($v.key)) {
        $ExistingCount++
    } else {
        $null = $NewViolations.Add($v)
    }
}
# "fixed" = a baseline entry whose key no longer occurs at all (the violation was repaired or
# the file was deleted). Entries are classified exactly once, so no double counting.
if ($BaseKeySet.Count -gt 0) {
    $CurrentKeySet = @{}
    foreach ($v in $Violations) { $CurrentKeySet[$v.key] = $true }
    # HashTable.Keys is not enumerable by foreach in PowerShell 5.1 (returns the KeyCollection);
    # materialise it with @() first. Same trap as Get-Count above.
    $baseKeyList = @($BaseKeySet.Keys)
    foreach ($k in $baseKeyList) {
        if (-not $CurrentKeySet.ContainsKey($k)) { $FixedCount++ }
    }
}
if ($UpdateBaseline) {
    $now = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ssK')
    $entryList = New-Object System.Collections.ArrayList
    foreach ($v in $Violations) {
        $null = $entryList.Add([ordered]@{
            key      = $v.key
            rule     = $v.rule
            severity = $v.severity
            file     = $v.file
            line     = $v.line
            hash     = (Get-Sha4 $v.message)
            message  = $v.message
        })
    }
    $bOut = [ordered]@{
        tool       = 'tools/check_architecture.ps1'
        version    = 1
        updatedAt  = $now
        note       = 'Generated by -UpdateBaseline. Do not hand-edit. See docs/rules/02-tech-selection.md.'
        summary    = ('errors=' + [string](Get-Count $Violations))
        count      = (Get-Count $Violations)
        entries    = @($entryList)
    }
    $bJson = $bOut | ConvertTo-Json -Depth 8
    Write-TextFileUtf8NoBom -Path $BaselineFull -Content ($bJson + "`n")
    $relBase = Get-RelToRoot -RootFull $RootFull -Full ([System.IO.Path]::GetFullPath($BaselineFull))
    Write-Host ''
    Write-Host ('baseline written: ' + $relBase + '  (' + [string](Get-Count $Violations) + ' entries)') -ForegroundColor Green
    Write-Host (('new=0 existing=' + [string]$ExistingCount + ' fixed=' + [string]$FixedCount)) -ForegroundColor DarkGray
    exit 0
}

# =============================================================================
# 14. Report
# =============================================================================
$NewErrors = 0
$NewWarns = 0
foreach ($v in $NewViolations) {
    if ($v.severity -eq 'error') { $NewErrors++ } else { $NewWarns++ }
}
$exitCode = 0
if ($NewErrors -gt 0) { $exitCode = 1 }

$summaryText = 'new=' + [string](Get-Count $NewViolations) + ' existing=' + [string]$ExistingCount + ' fixed=' + [string]$FixedCount + ' errors=' + [string]$NewErrors + ' warnings=' + [string]$NewWarns

if ($AsJson) {
    $jsonViolations = @()
    foreach ($v in $NewViolations) {
        $jsonViolations += ([ordered]@{
            id       = $v.rule
            severity = $v.severity
            file     = $v.file
            line     = $v.line
            message  = $v.message
            key      = $v.key
        })
    }
    $jOut = [ordered]@{
        tool          = 'check_architecture.ps1'
        version       = 'v1'
        projectRoot   = $RootFull
        config        = $ConfigRel
        configState   = $CfgState
        configError   = $CfgError
        baseline      = $Baseline
        baselineState = $BaseState
        baselineError = $BaseBroken
        files         = (Get-Count $AllFiles)
        scanned       = (Get-Count $UsefulFiles)
        gdFiles       = (Get-Count $GdFiles)
        gdWhitelisted = (Get-Count $WhitelistedGd)
        csFiles       = (Get-Count $CsFiles)
        cppFiles      = (Get-Count $CppFiles)
        sceneFiles    = (Get-Count $SceneFiles)
        outline       = $outlineInfo
        numeric       = $NumStats
        summary       = [ordered]@{
            newItems      = (Get-Count $NewViolations)
            existingItems = $ExistingCount
            fixedItems    = $FixedCount
            errors        = $NewErrors
            warnings      = $NewWarns
        }
        violations    = @($jsonViolations)
        exitCode      = $exitCode
    }
    $jText = $jOut | ConvertTo-Json -Depth 10
    if ($OutFile.Length -gt 0) {
        $ofPath = $OutFile
        if (-not [System.IO.Path]::IsPathRooted($ofPath)) { $ofPath = Join-Path $RootFull $ofPath }
        Write-TextFileUtf8NoBom -Path $ofPath -Content ($jText + "`n")
        Write-Host ('report written: ' + $ofPath) -ForegroundColor DarkGray
    } else {
        Write-Output $jText
    }
} else {
    if (-not $Quiet) {
        Write-Host ('[check_architecture] ' + ($MSG.SCAN_HEADER -f $RootFull, (Get-Count $UsefulFiles))) -ForegroundColor Cyan
        Write-Host ('[check_architecture] gd=' + [string](Get-Count $GdFiles) + ' (whitelisted=' + [string](Get-Count $WhitelistedGd) + ') cs=' + [string](Get-Count $CsFiles) + ' cpp=' + [string](Get-Count $CppFiles) + ' tscn=' + [string](Get-Count $SceneFiles)) -ForegroundColor DarkCyan
        if ($CfgState -eq 'loaded') {
            Write-Host ('[check_architecture] config loaded: ' + $ConfigRel + '  allowGDScript=[' + ($AllowGDScript -join ', ') + ']') -ForegroundColor DarkCyan
        } elseif ($CfgState -eq 'error') {
            Write-Host ('[check_architecture] config parse error: ' + $ConfigRel + ' -> ' + $CfgError + ' (built-in defaults used)') -ForegroundColor Yellow
        } else {
            Write-Host ('[check_architecture] config not found: ' + $ConfigRel + ' (built-in defaults used)') -ForegroundColor DarkYellow
        }
        if ($BaseState -eq 'broken') {
            Write-Host ('[check_architecture] baseline is broken (' + $BaseBroken + '); ignored, run -UpdateBaseline to rebuild') -ForegroundColor DarkYellow
        } elseif ($BaseState -eq 'absent') {
            Write-Host ('[check_architecture] no baseline yet: ' + $Baseline + ' (all findings are new; -UpdateBaseline to freeze)') -ForegroundColor DarkGray
        }
        Write-Host ''

        if ((Get-Count $NewViolations) -eq 0) {
            Write-Host '   (no new violations)' -ForegroundColor Green
        }
        foreach ($v in $NewViolations) {
            $lineLabel = ''
            if ($v.line -gt 0) { $lineLabel = (':' + [string]$v.line).PadRight(6) } else { $lineLabel = ' '.PadRight(6) }
            $filePart = $v.file
            if ($filePart.Length -gt 52) { $filePart = '...' + $filePart.Substring($filePart.Length - 49) }
            $head = '  ' + $v.rule.PadRight(6) + ' ' + $v.severity.PadRight(8) + ' ' + $filePart.PadRight(52) + $lineLabel + ' ' + $v.message
            Write-Host $head -ForegroundColor $v.color
            if ($v.hint.Length -gt 0) {
                Write-Host ('           fix: ' + $v.hint) -ForegroundColor DarkGray
            }
        }
        Write-Host ''
        if ($NumStats.reported -gt 0) {
            Write-Host ('  GS005 detail: numeric candidates=' + [string]$NumStats.candidates + ' reported=' + [string]$NumStats.reported + ' shown=' + [string]$NumStats.shown + ' hidden=' + [string]$NumStats.hidden + ' files=' + [string]$NumStats.scannedFiles + ' escaped=' + [string]$NumStats.escaped + ' excludedDirs=' + [string]$NumStats.excludedFiles) -ForegroundColor DarkGray
        }
        if ($outlineInfo.exists) {
            Write-Host ('  outline: ' + $outlineInfo.path + ' state=' + $outlineInfo.state + ' stamp=' + $outlineInfo.stamp + ' newestSource=' + $outlineInfo.newestSource) -ForegroundColor DarkGray
        }
    } else {
        foreach ($v in $NewViolations) {
            if ($v.severity -ne 'error') { continue }
            Write-Host ('  ' + $v.rule + ' ' + $v.file + ':' + $v.line + ' ' + $v.message) -ForegroundColor Red
        }
    }

    Write-Host ('[check_architecture] ' + $summaryText) -ForegroundColor Cyan

    if ($OutFile.Length -gt 0) {
        $sb = New-Object System.Text.StringBuilder
        $null = $sb.AppendLine('# check_architecture report')
        $null = $sb.AppendLine('')
        $null = $sb.AppendLine('- projectRoot: ' + $RootFull)
        $null = $sb.AppendLine('- scanned files: ' + [string](Get-Count $UsefulFiles))
        $null = $sb.AppendLine('- config: ' + $ConfigRel + ' (' + $CfgState + ')')
        $null = $sb.AppendLine('- baseline: ' + $Baseline + ' (' + $BaseState + ')')
        $null = $sb.AppendLine('')
        $null = $sb.AppendLine('| rule | severity | file | line | message |')
        $null = $sb.AppendLine('|---|---|---|---|---|')
        foreach ($v in $NewViolations) {
            $null = $sb.AppendLine('| ' + $v.rule + ' | ' + $v.severity + ' | ' + $v.file + ' | ' + $v.line + ' | ' + ($v.message -replace '\|', '/') + ' |')
        }
        $null = $sb.AppendLine('')
        $null = $sb.AppendLine($summaryText)
        $ofPath = $OutFile
        if (-not [System.IO.Path]::IsPathRooted($ofPath)) { $ofPath = Join-Path $RootFull $ofPath }
        Write-TextFileUtf8NoBom -Path $ofPath -Content ($sb.ToString())
        Write-Host ('report written: ' + $ofPath) -ForegroundColor DarkGray
    }

    if ($exitCode -eq 0) {
        if ($NewWarns -gt 0) { Write-Host $MSG.RESULT_WARN -ForegroundColor Yellow }
        else { Write-Host $MSG.RESULT_OK -ForegroundColor Green }
    } else {
        Write-Host $MSG.RESULT_FAIL -ForegroundColor Red
    }
}

exit $exitCode
