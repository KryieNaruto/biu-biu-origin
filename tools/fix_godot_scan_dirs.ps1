<#
.SYNOPSIS
    补齐「Godot 不该扫描」目录里的 .gdignore 标记，消除编辑器启动时的噪音报错。

.DESCRIPTION
    背景（真实踩坑，2026-11-09）：
      Godot 不读 .gitignore，会扫描项目内所有目录。构建目录里的 .obj / .png / .lib
      会被误判成模型 / 贴图资源去导入，于是启动就刷：
          ERROR: Couldn't read OBJ file 'res://tools/asset_viewer2/build/.../x.cpp.obj'
          ERROR: Error importing 'res://...'
      这不是资源坏了，是 Godot 把 C++ 编译中间产物当成 3D 模型在导入。

    为什么需要本脚本：
      Godot 唯一支持的跳过机制是「在该目录里放一个空的 .gdignore」。
      但构建目录本身被 .gitignore 排除（且是 -gitignore 的硬限制：被排除目录内的
      文件无法用 ! 规则重新包含），所以 .gdignore 这辈子进不了库 ——
      只在本机有效，任何人克隆后都会重新踩坑。故本脚本在构建前 / 克隆后跑一次即可。

.PARAMETER Root
    项目根目录，默认是本脚本所在 tools/ 的上一级。

.PARAMETER Check
    只检查不写盘，返回码 0 = 全部就绪；10 = 有缺失。

.EXAMPLE
    .\tools\fix_godot_scan_dirs.ps1
    .\tools\fix_godot_scan_dirs.ps1 -Check
#>
[CmdletBinding()]
param(
    [string] $Root,
    [switch] $Check
)

$ErrorActionPreference = 'Stop'

# param 默认值里不能直接用 $PSScriptRoot（求值时机过早，会是空串），故在函数体里兜底。
if (-not $Root) {
    $scriptDir = $PSScriptRoot
    if (-not $scriptDir) {
        $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
    }
    $Root = Split-Path -Parent $scriptDir
}

# ---------------------------------------------------------------------------
# 需要 .gdignore 的目录清单
# ---------------------------------------------------------------------------
# 判据：该目录会出现「非 Godot 产物」且「扩展名可能被 Godot 误判」的文件。
#   .obj  -> Godot 的 OBJ 模型导入器会尝试解析
#   .png/.jpg -> 贴图导入器
#   .lib/.exp -> 无导入器，但会拖慢扫描
# 每新增一个 C++ 工具工程或第三方源码树，就在此表加一行。
$ScanDirs = @(
    'native/build'
    'native/build-debug'
    'native/build-mt'
    'native/gdextension/libgodot-cpp'
    'tools/asset_viewer/build'
    'tools/asset_viewer/dist'
    'tools/asset_viewer2/build'
    'tools/asset_viewer2/dist'
)

$created = @()
$missing = @()
$ok = @()

foreach ($rel in $ScanDirs) {
    $dir = Join-Path $Root $rel
    if (-not (Test-Path -LiteralPath $dir)) { continue }   # 目录不存在 -> 无需标记

    $marker = Join-Path $dir '.gdignore'
    if (Test-Path -LiteralPath $marker) {
        $ok += $rel
        continue
    }

    if ($Check) {
        $missing += $rel
    }
    else {
        # 空文件即可；Godot 只判断存在性，不读内容
        New-Item -ItemType File -Path $marker -Force | Out-Null
        $created += $rel
    }
}

# ---------------------------------------------------------------------------
# 输出
# ---------------------------------------------------------------------------
Write-Output "[fix_godot_scan_dirs] root = $Root"
foreach ($d in $ok) { Write-Output "  [OK]      $d" }
foreach ($d in $created) { Write-Output "  [CREATED] $d" }
foreach ($d in $missing) { Write-Output "  [MISSING] $d" }

if ($missing.Count -gt 0) {
    Write-Output ""
    Write-Output "有 $($missing.Count) 个目录缺 .gdignore，跑一次不带 -Check 的本脚本即可修复。"
    exit 10
}

if ($created.Count -gt 0) {
    Write-Output ""
    Write-Output "已补 $($created.Count) 个标记。建议重启 Godot 编辑器让它重新扫描。"
}

exit 0
