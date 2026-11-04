# =============================================================================
#  build.ps1 —— AssetViewer 一键构建脚本（PowerShell 5.1 兼容，无 PS7 专有语法）
# -----------------------------------------------------------------------------
#  用途：vcvars64 -> CMake configure (Ninja) -> build -> 可选运行/自测
#  用法（在工程根 D:\workspace\myspace\godot_game\biu-biu-origin 下）：
#      .\tools\asset_viewer\build.ps1                # 配置 + 编译 Release
#      .\tools\asset_viewer\build.ps1 -Config Debug  # 配置 + 编译 Debug
#      .\tools\asset_viewer\build.ps1 -Clean         # 清空 build 目录后重来
#      .\tools\asset_viewer\build.ps1 -Target av_sheet   # 只构建某个目标
#      .\tools\asset_viewer\build.ps1 -Run           # 编译后启动 asset_viewer.exe
#      .\tools\asset_viewer\build.ps1 -Test          # 编译后跑 ctest（loader_smoke 等）
#      .\tools\asset_viewer\build.ps1 -Test -Run     # 两者都做
#      .\tools\asset_viewer\build.ps1 -StaticRuntime # 用 /MT 静态运行库（等价 -DAV_STATIC_RUNTIME=ON）
#      .\tools\asset_viewer\build.ps1 -GenerateOnly  # 只 configure，不编译
#
#  约定：
#    * 构建目录  : tools\asset_viewer\build          （已 .gitignore）
#    * 产物目录  : tools\asset_viewer\build\out      （asset_viewer.exe / 各测试 exe / .lib）
#    * 运行 cwd  : 工程根（程序能直接找到 素材\）
#    * 若未激活 Developer 环境，脚本会自动 call vcvars64.bat（只影响本次构建子进程）。
# =============================================================================
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release', 'RelWithDebInfo', 'MinSizeRel')]
    [string]$Config = 'Release',

    # 生成器：Ninja（默认，快）/ VS（可视化调试，打开 .sln）
    [ValidateSet('Ninja', 'VS')]
    [string]$Generator = 'Ninja',

    [switch]$Clean,
    [switch]$GenerateOnly,
    [switch]$Run,
    [switch]$Test,
    [switch]$StaticRuntime,
    [switch]$WarningsAsErrors,
    [switch]$NoTestTargets,

    # 只构建指定目标（如 asset_viewer / loader_smoke / av_sheet）
    [string]$Target = '',

    # 透传给 cmake configure 的额外参数
    [string[]]$ExtraArgs = @()
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# PS 5.1（尤其是 Windows PowerShell 5.1.26100 起）会把子进程 stderr 包装成 RemoteException 混进管道，
# 设置此变量可还原「纯文本」读取，否则 cmake 的 warning 会被写成 System.Management.Automation.RemoteException。
$PSNativeCommandUseErrorActionPreference = $false
# 本脚本自己控制输出：进度/摘要走 stderr（与 Console 同屏、不被管道吞掉），
# 子进程输出走 stdout（可被 `> log` 收走）。
$script:ShowExternalOutput = $true
try {
    $host.UI.RawUI.WindowTitle = 'AssetViewer build'
}
catch { }

# -----------------------------------------------------------------------------
# 0. UTF-8 控制台（中文输出不乱码；PowerShell 5.1 下必须显式设置）
# -----------------------------------------------------------------------------
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $OutputEncoding = [System.Text.Encoding]::UTF8
    $env:PYTHONIOENCODING = 'utf-8'
}
catch {
    # 非交互/被重定向时设置失败不影响构建
}

function Write-Step  ([string]$m) { [Console]::Error.WriteLine(''); [Console]::Error.WriteLine('==> ' + $m) }
function Write-Ok    ([string]$m) { [Console]::Error.WriteLine('    [OK]   ' + $m) }
function Write-Info  ([string]$m) { [Console]::Error.WriteLine('    [INFO] ' + $m) }
function Write-Warn2 ([string]$m) { [Console]::Error.WriteLine('    [WARN] ' + $m) }
function Write-Fail  ([string]$m) { [Console]::Error.WriteLine('    [FAIL] ' + $m) }

# -----------------------------------------------------------------------------
# 1. 路径解析（脚本可能从任意 cwd 调用）
# -----------------------------------------------------------------------------
$ScriptDir    = Split-Path -Parent $MyInvocation.MyCommand.Definition
$ProjectDir   = Split-Path -Parent (Split-Path -Parent $ScriptDir)   # tools\asset_viewer -> 工程根
$SourceDir    = $ScriptDir
$BuildDir     = Join-Path $SourceDir 'build'
$OutDir       = Join-Path $BuildDir 'out'
$SheetDir     = Join-Path $BuildDir '_sheet'
$AssetRoot    = Join-Path $ProjectDir '素材'

Write-Step "AssetViewer 构建"
Write-Info "源码目录 : $SourceDir"
Write-Info "构建目录 : $BuildDir"
Write-Info "产物目录 : $OutDir"
Write-Info "工程根   : $ProjectDir"
Write-Info "素材根   : $AssetRoot"
Write-Info "配置     : $Config   生成器: $Generator   静态运行库: $($StaticRuntime.IsPresent)"

# -----------------------------------------------------------------------------
# 2. 工具探测（cmake / ninja / vcvars64）
# -----------------------------------------------------------------------------
function Find-FirstExisting {
    param([string[]]$Candidates)
    foreach ($c in $Candidates) {
        if ($c -and (Test-Path -LiteralPath $c)) { return $c }
    }
    return $null
}

$CmakeExe = Find-FirstExisting @(
    $env:AV_CMAKE,
    'D:\env\cmake\cmake-3.31.12-windows-x86_64\bin\cmake.exe',
    'D:\env\cmake\bin\cmake.exe',
    (Get-Command cmake -ErrorAction SilentlyContinue | ForEach-Object { $_.Source })
)
if (-not $CmakeExe) { Write-Fail "找不到 cmake.exe（可用环境变量 AV_CMAKE 指定）"; exit 1 }

$NinjaExe = Find-FirstExisting @(
    $env:AV_NINJA,
    'D:\env\ninja\ninja.exe',
    (Get-Command ninja -ErrorAction SilentlyContinue | ForEach-Object { $_.Source })
)
if ($Generator -eq 'Ninja' -and -not $NinjaExe) {
    Write-Fail "找不到 ninja.exe（可用环境变量 AV_NINJA 指定，或 -Generator VS）"
    exit 1
}

$Vcvars = Find-FirstExisting @(
    $env:AV_VCVARS,
    'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat',
    'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat',
    'C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat',
    'C:\Program Files\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvars64.bat',
    'C:\Program Files\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat'
)
$HaveCl = $null -ne (Get-Command cl.exe -ErrorAction SilentlyContinue)
if (-not $HaveCl -and -not $Vcvars) {
    Write-Fail "cl.exe 不在 PATH，且找不到 vcvars64.bat。请安装『使用 C++ 的桌面开发』工作负载，或用 AV_VCVARS 指定 vcvars64.bat 路径。"
    exit 1
}
if (-not $HaveCl) {
    Write-Info "cl.exe 不在 PATH —— 将通过 vcvars64.bat 激活：$Vcvars"
}

Write-Info "cmake    : $CmakeExe"
if ($NinjaExe) { Write-Info "ninja    : $NinjaExe" }

# -----------------------------------------------------------------------------
# 3. 把一条命令包进 vcvars64 环境里执行（cmd /c "call vcvars64 && ..."）
#    注意：vcvars 只影响子进程，不会污染当前 PowerShell 会话。
# -----------------------------------------------------------------------------
function Invoke-InDevShell {
    param(
        [Parameter(Mandatory = $true)][string[]]$CommandLine,
        [string]$WorkDir = $ProjectDir,
        [switch]$AllowFail
    )
    $line = ''
    if (-not $HaveCl) {
        $line = 'call "' + $Vcvars + '" >nul 2>&1 && '
    }
    $line += 'cd /d "' + $WorkDir + '" && '
    $line += ($CommandLine -join ' && ')

    # 临时关闭全局 ErrorActionPreference：PS5.1 下子进程写 stderr 会变成 NativeCommandError，
    # 在 'Stop' 模式下直接把脚本掀翻（cmake 的 warning 就写在 stderr）。
    # 注意：必须是 $global:（函数内的 $ErrorActionPreference 赋值只改局部作用域，拦不住原生命令的 stderr 报错）
    $savedEap = $ErrorActionPreference
    $global:ErrorActionPreference = 'Continue'
    Write-Info ("cmd /c " + $line)
    try {
        & cmd.exe /d /c $line 2>&1 | Where-Object { $script:ShowExternalOutput -or ([string]$_).StartsWith($sig) } | ForEach-Object { Write-Host ([string]$_) }
    }
    catch {
        Write-Warn2 ("外部命令输出处理：{0}" -f $_.Exception.Message)
    }
    finally {
        $code = $LASTEXITCODE
        $global:ErrorActionPreference = $savedEap
    }
    if ($code -ne 0 -and -not $AllowFail) {
        Write-Fail "命令失败，退出码 $code"
        exit $code
    }
    return $code
}

function Quote([string]$s) { return '"' + $s + '"' }

# -----------------------------------------------------------------------------
# 4. 清空构建目录（可选）
# -----------------------------------------------------------------------------
if ($Clean -and (Test-Path -LiteralPath $BuildDir)) {
    Write-Step "清理构建目录"
    # 保留：_probe（工具链探针源码）、_sheet（Lead 验收拼版与 index.txt）
    Get-ChildItem -LiteralPath $BuildDir -Force |
        Where-Object { $_.Name -ne '_probe' -and $_.Name -ne '_sheet' } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
    Write-Ok "已清空 $BuildDir（保留 _probe、_sheet）"
}

# -----------------------------------------------------------------------------
# 5. Configure
# -----------------------------------------------------------------------------
Write-Step "配置（CMake configure）"

$genArgs = @()
if ($Generator -eq 'Ninja') {
    $NinjaPath = $NinjaExe.Replace('\', '/')
    $genArgs = @('-G', 'Ninja', ('-DCMAKE_MAKE_PROGRAM=' + $NinjaPath))
} else {
    $genArgs = @('-G', 'Visual Studio 18 2026', '-A', 'x64')
}

$optionArgs = @(
    '-DCMAKE_BUILD_TYPE=' + $Config,
    '-DAV_BUILD_TESTS=' + $(if ($NoTestTargets) { 'OFF' } else { 'ON' }),
    '-DAV_ASSET_ROOT=' + ($AssetRoot -replace '\\', '/')
)
if ($StaticRuntime)      { $optionArgs += '-DAV_STATIC_RUNTIME=ON' }
if ($WarningsAsErrors)   { $optionArgs += '-DAV_WARNINGS_AS_ERRORS=ON' }

$configureCmd = @(
    (Quote $CmakeExe),
    '-S', (Quote $SourceDir),
    '-B', (Quote $BuildDir)
) + $genArgs + $optionArgs + $ExtraArgs

$rc = Invoke-InDevShell -CommandLine @($configureCmd -join ' ')
if ($rc -ne 0) { exit $rc }

$cmakeCache = Join-Path $BuildDir 'CMakeCache.txt'
if (-not (Test-Path -LiteralPath $cmakeCache)) {
    Write-Fail "配置未生成 CMakeCache.txt，请查看上方输出。"
    exit 1
}

if ($GenerateOnly) {
    Write-Ok "仅配置完成（-GenerateOnly）。构建目录：$BuildDir"
    exit 0
}

# -----------------------------------------------------------------------------
# 6. Build
# -----------------------------------------------------------------------------
Write-Step "编译（CMake build）"
# 静默 reconfigure 一次：Ninja + MSVC 的 deps 规则在首次生成 build.ninja 前会带着 /showIncludes 进编译命令行，
# 第一次 build 前重跑 configure 可确保编译命令行干净（屏幕上不会刷「注意: 包含文件」）。
$sig = '-- AssetViewer:'
$script:ShowExternalOutput = $false
$null = Invoke-InDevShell -CommandLine @($configureCmd -join ' ') -AllowFail
$script:ShowExternalOutput = $true
$buildArgs = @((Quote $CmakeExe), '--build', (Quote $BuildDir))
if ($Generator -ne 'Ninja') { $buildArgs += @('--config', $Config) }
if ($Target) { $buildArgs += @('--target', $Target) }
$buildArgs += @('--', '-k', '0')   # Ninja：单步失败继续编译其它文件，一次看全错误

$rc = Invoke-InDevShell -CommandLine @($buildArgs -join ' ')
$script:BuildFailed = ($rc -ne 0)
if ($rc -eq 0) { Write-Ok "编译成功" } else { Write-Warn2 "编译失败（退出码 $rc），见上方 error C*/LNK* 行" }

# -----------------------------------------------------------------------------
# 7. 产物清单
# -----------------------------------------------------------------------------
Write-Step "产物"
if (Test-Path -LiteralPath $OutDir) {
    $arts = Get-ChildItem -LiteralPath $OutDir -File | Sort-Object Name
    if ($arts) {
        foreach ($a in $arts) {
            Write-Host ("    {0,-24} {1,12:N0} 字节  {2}" -f $a.Name, $a.Length, $a.LastWriteTime.ToString('HH:mm:ss'))
        }
    } else {
        Write-Info "（暂无产物：等协作者源文件落地，见上方 CMake 警告）"
    }
} else {
    Write-Info "（产物目录尚未创建）"
}

# -----------------------------------------------------------------------------
# 8. 自测（ctest）
# -----------------------------------------------------------------------------
$testExit = 0
if ($Test) {
    Write-Step "自测（ctest）"
    if ($script:BuildFailed) {
        Write-Warn2 "编译未成功，跳过 ctest"
    } else {
        # ctest 与 cmake 同目录分发；找不到时退回 PATH 里的 ctest
        $CtestExe = Join-Path (Split-Path -Parent $CmakeExe) 'ctest.exe'
        if (-not (Test-Path -LiteralPath $CtestExe)) { $CtestExe = 'ctest' }
        $ctestCmd = @(
            (Quote $CtestExe),
            '--test-dir', (Quote $BuildDir),
            '--output-on-failure'
        )
        if ($Generator -ne 'Ninja') { $ctestCmd += @('-C', $Config) }
        $testExit = Invoke-InDevShell -CommandLine @($ctestCmd -join ' ') -AllowFail
        if ($testExit -eq 0) { Write-Ok "ctest 全部通过" } else { Write-Warn2 "ctest 有失败（退出码 $testExit）" }
    }
}

# -----------------------------------------------------------------------------
# 9. 运行（asset_viewer.exe）
# -----------------------------------------------------------------------------
$runExit = 0
if ($Run) {
    Write-Step "启动 asset_viewer"
    $exe = Join-Path $OutDir 'asset_viewer.exe'
    if (-not (Test-Path -LiteralPath $exe)) {
        Write-Warn2 "未找到 $exe（app_main.cpp 可能尚未落地），跳过运行"
    } else {
        Write-Info "cwd = $ProjectDir"
        Push-Location -LiteralPath $ProjectDir
        try {
            & $exe @()
            $runExit = $LASTEXITCODE
            if ($null -eq $runExit) { $runExit = 0 }
        }
        finally {
            Pop-Location
        }
        Write-Info "asset_viewer 退出码 $runExit"
    }
}

# -----------------------------------------------------------------------------
# 10. 汇总
# -----------------------------------------------------------------------------
Write-Step "完成"
if ($script:BuildFailed) {
    Write-Host "    结果：编译失败 —— 见上方 error C*/LNK* 行。" -ForegroundColor Red
    exit 1
}
Write-Host "    结果：OK" -ForegroundColor Green
Write-Host "    产物：$OutDir" -ForegroundColor Green
Write-Host "    拼版：cmake --build `"$BuildDir`" --target av_sheet   (或 -Target av_sheet)"
if ($Test -and $testExit -ne 0) { exit $testExit }
exit 0
