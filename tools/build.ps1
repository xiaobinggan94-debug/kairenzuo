<#
    kairenzuo 一键编译脚本（.NET Framework 4.x / csc.exe）

    用法：
        powershell -ExecutionPolicy Bypass -File tools\build.ps1
        powershell -ExecutionPolicy Bypass -File tools\build.ps1 -OutputName "自定义.exe"

    说明：
      - 自动定位 Framework64/Framework 下最高版本 v4.* 目录里的 csc.exe，找不到再退回
        C:\Windows\Microsoft.NET\Framework*\v3.5\csc.exe。
      - 自动在 GAC（%WINDIR%\Microsoft.NET\assembly）里查找 System.Speech.dll 的真实
        v4.0 路径（该程序集不在 csc 默认引用列表里，必须显式 /r: 全路径）。
      - 使用 /noconfig 关闭默认响应文件 csc.rsp，再显式引用全部所需程序集，因此
        System.Web.Extensions（System.Web.Script.Serialization）可以显式列出而不会
        触发 CS1703「已导入具有相同标识的程序集」。
      - 只编译 src\ 下的源码；src\preview_shot.cs 是独立的临时截图小程序（自带 Main），
        参与编译会与 Program.cs 的 Main 冲突，故固定排除。
      - 输出到 release\，编译结束后打印文件大小与 SHA256。
#>

[CmdletBinding()]
param(
    [string]$OutputName = [char]0x7FFB + [char]0x8BD1 + [char]0x5668 + ".exe",
    [switch]$NoOptimize
)

$ErrorActionPreference = "Stop"

$root      = Split-Path -Parent $PSScriptRoot
$srcDir    = Join-Path $root "src"
$iconPath  = Join-Path $root "assets\icons\cookie4.ico"
$dictPath  = Join-Path $root "assets\dict\offline_dict.txt"   # ⑮ 离线词库，编成嵌入资源 OfflineDict
$releaseDir= Join-Path $root "release"
$outPath   = Join-Path $releaseDir $OutputName
$excludeSources = @("preview_shot.cs")   # 自带 Main 的独立小工具，不参与主程序编译

function Fail([string]$msg) {
    Write-Host "[ERROR] $msg" -ForegroundColor Red
    exit 1
}

# ---------- 1. 定位 csc.exe ----------
function Find-Csc {
    $netRoot = Join-Path $env:WINDIR "Microsoft.NET"
    $found = @()
    foreach ($flavor in @("Framework64", "Framework")) {
        $flavorDir = Join-Path $netRoot $flavor
        if (-not (Test-Path $flavorDir)) { continue }
        $verDirs = Get-ChildItem $flavorDir -Directory -ErrorAction SilentlyContinue |
                   Where-Object { $_.Name -match '^v4\.' } |
                   Sort-Object { try { [version]($_.Name.Substring(1)) } catch { [version]"0.0" } } -Descending
        foreach ($vd in $verDirs) {
            $c = Join-Path $vd.FullName "csc.exe"
            if (Test-Path $c) { $found += $c }
        }
    }
    if ($found.Count -eq 0) {
        foreach ($flavor in @("Framework64", "Framework")) {
            $legacy = Join-Path $netRoot (Join-Path $flavor "v3.5\csc.exe")
            if (Test-Path $legacy) { $found += $legacy }
        }
    }
    if ($found.Count -eq 0) { return $null }
    return $found[0]
}

# ---------- 2. 在 GAC 里找程序集真实路径 ----------
function Find-GacAssembly([string]$simpleName) {
    $hits = @()
    foreach ($gacRoot in @((Join-Path $env:WINDIR "Microsoft.NET\assembly"), (Join-Path $env:WINDIR "assembly"))) {
        if (-not (Test-Path $gacRoot)) { continue }
        $hits += Get-ChildItem -Path $gacRoot -Recurse -Filter "$simpleName.dll" -ErrorAction SilentlyContinue
    }
    if ($hits.Count -eq 0) { return $null }
    $ranked = $hits | Sort-Object -Property @{ Expression = {
                    try { [version]($_.VersionInfo.FileVersion.Split(' ')[0]) } catch { [version]"0.0" }
                } } -Descending
    return $ranked[0].FullName
}

# 框架目录里自带的程序集优先（与 csc 同版本最稳），找不到再查 GAC
function Resolve-Reference([string]$simpleName, [string]$frameworkDir) {
    $local = Join-Path $frameworkDir "$simpleName.dll"
    if (Test-Path $local) { return $local }
    return (Find-GacAssembly $simpleName)
}

$csc = Find-Csc
if (-not $csc) { Fail "没找到 csc.exe，请确认已安装 .NET Framework 4.x" }
$frameworkDir = Split-Path -Parent $csc

if (-not (Test-Path $srcDir))   { Fail "源码目录不存在：$srcDir" }
if (-not (Test-Path $iconPath)) { Fail "图标文件不存在：$iconPath" }
if (-not (Test-Path $dictPath)) { Fail "离线词库不存在：$dictPath（⑮ 断网查词要用，别漏了）" }
if (-not (Test-Path $releaseDir)) { New-Item -ItemType Directory -Path $releaseDir | Out-Null }

# ---------- 3. 解析引用 ----------
$refNames = @("System", "System.Core", "System.Drawing", "System.Windows.Forms", "System.Xml", "System.IO.Compression")
$refPaths = @()
foreach ($n in $refNames) {
    $p = Resolve-Reference $n $frameworkDir
    if (-not $p -or -not (Test-Path $p)) { Fail "找不到引用程序集：$n.dll" }
    $refPaths += $p
}

# System.Speech 必须用 GAC 里的真实路径显式引用（不在 csc 默认引用列表）
$speech = Find-GacAssembly "System.Speech"
if (-not $speech) { $speech = Resolve-Reference "System.Speech" $frameworkDir }
if (-not $speech -or -not (Test-Path $speech)) { Fail "找不到 System.Speech.dll（请确认本机安装了 .NET Framework 4.x 语音组件）" }
$refPaths += $speech

# System.Web.Extensions：用 /noconfig 关掉 csc.rsp 之后显式引用，不会出现 CS1703
$webExt = Find-GacAssembly "System.Web.Extensions"
if (-not $webExt) { $webExt = Resolve-Reference "System.Web.Extensions" $frameworkDir }
if (-not $webExt -or -not (Test-Path $webExt)) { Fail "找不到 System.Web.Extensions.dll" }
$refPaths += $webExt

# ---------- 4. 收集源码 ----------
$sources = Get-ChildItem $srcDir -Filter *.cs -File |
           Where-Object { $excludeSources -notcontains $_.Name } |
           Sort-Object Name
if (-not $sources -or $sources.Count -eq 0) { Fail "src\ 下没有可编译的 .cs 文件" }

# ---------- 4.5 注入构建号（源码版本号不再手写） ----------
# 程序的 AppVersionLine() 会从嵌入资源 SrcRev 里读这个字符串。
# 这样"日志里写的源码版本"永远等于编译时的 HEAD，不会像以前那样写死 r8eb97f 之后一路飘。
# 工作区有未提交改动时加 -dirty 后缀，明确区分"纯提交版"和"改了没提交编出来的版本"。
$rev = ""
try {
    $rev = ((& git -C $root rev-parse --short HEAD 2>$null) -join "").Trim()
} catch { }
if (-not $rev) { $rev = "nogit" }
try {
    $dirty = (& git -C $root status --porcelain 2>$null)
    if ($dirty -and $dirty.Trim().Length -gt 0) { $rev = "$rev-dirty" }
} catch { }
$revFile = Join-Path $env:TEMP "solivine_srcrev.txt"
[System.IO.File]::WriteAllText($revFile, $rev, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "==> 构建号   : $rev（嵌入资源 SrcRev）"

if (Test-Path $outPath) { Remove-Item $outPath -Force }

# ---------- 5. 编译 ----------
$cscArgs = New-Object System.Collections.Generic.List[string]
$cscArgs.Add("/nologo")
$cscArgs.Add("/noconfig")
$cscArgs.Add("/target:winexe")
$cscArgs.Add("/platform:anycpu")
$cscArgs.Add("/codepage:65001")
if (-not $NoOptimize) { $cscArgs.Add("/optimize+") }
$cscArgs.Add("/win32icon:$iconPath")
$cscArgs.Add("/out:$outPath")
$cscArgs.Add("/resource:$revFile,SrcRev")
# ⑮ 离线词库：20225 常用词 + 20795 变形词，约 1.66 MB。嵌进 exe，交付仍是单文件，
#    她拷过去就能断网查词，不用额外放词典文件在旁边。
$cscArgs.Add("/resource:$dictPath,OfflineDict")
foreach ($r in $refPaths) { $cscArgs.Add("/r:$r") }
foreach ($s in $sources)  { $cscArgs.Add($s.FullName) }

Write-Host "==> csc.exe : $csc"
Write-Host "==> 源码     : $($sources.Count) 个（已排除：$($excludeSources -join ', ')）"
foreach ($r in $refPaths) { Write-Host "==> 引用     : $r" }
Write-Host "==> 输出     : $outPath"
Write-Host ""

& $csc @cscArgs
$code = $LASTEXITCODE
if ($code -ne 0) { Fail "编译失败，csc 退出码 $code" }
if (-not (Test-Path $outPath)) { Fail "编译结束但没生成 exe：$outPath" }

# ---------- 6. 打印大小与 SHA256 ----------
$fi   = Get-Item $outPath
$hash = (Get-FileHash $outPath -Algorithm SHA256).Hash
Write-Host ""
Write-Host "================ 编译完成 ================" -ForegroundColor Green
Write-Host ("文件 : {0}" -f $fi.FullName)
Write-Host ("大小 : {0} 字节 ({1:N1} KB)" -f $fi.Length, ($fi.Length / 1KB))
Write-Host ("SHA256: {0}" -f $hash)
Write-Host "==========================================" -ForegroundColor Green
