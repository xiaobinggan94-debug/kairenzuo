<#
    Solivine 数据备份脚本

    用途：
        把 %APPDATA%\SolivineQuickLook 下 5 个核心数据文件，拷到
        %USERPROFILE%\Documents\SolivineBackup\<时间戳>\ 下，每次一个独立目录，绝不覆盖旧备份。

    备份：
        生词本.txt / 生词本数据.tsv / settings.ini / 历史记录.txt

    硬性排除（脚本里用命名单兜底，即使白名单被误改也拷不出去）：
        全部 *.exe / *.dll、log.txt、crash.txt、全部 *.png、apikey.txt

    用法：
        powershell -ExecutionPolicy Bypass -File tools\backup-data.ps1
        powershell -ExecutionPolicy Bypass -File tools\backup-data.ps1 -Source "D:\某个目录" -Root "E:\我的备份"

    参数：
        -Source  数据目录，默认 %APPDATA%\SolivineQuickLook
        -Root    备份根目录，默认 %USERPROFILE%\Documents\SolivineBackup
#>

[CmdletBinding()]
param(
    [string]$Source = (Join-Path $env:APPDATA "SolivineQuickLook"),
    [string]$Root   = (Join-Path ([Environment]::GetFolderPath("MyDocuments")) "SolivineBackup")
)

$ErrorActionPreference = "Stop"

# ---------- 备份清单（只用 UTF-8 存盘时中文才不会乱码，见文件末尾提示）----------
$whiteList = @(
    "生词本.txt",
    "生词本数据.tsv",
    "settings.ini",
    "历史记录.txt"
)

# ---------- 硬性排除 ----------
$blockedNames = @("apikey.txt", "log.txt", "crash.txt")
$blockedExts  = @(".exe", ".dll", ".png")

function Test-Blocked([string]$name) {
    if ($blockedNames -contains $name) { return $true }
    $ext = [System.IO.Path]::GetExtension($name)
    if ($ext -and ($blockedExts -contains $ext.ToLowerInvariant())) { return $true }
    return $false
}

function Fail([string]$msg) {
    Write-Host ""
    Write-Host "[失败] $msg" -ForegroundColor Red
    exit 1
}

# ---------- 1. 检查数据目录 ----------
if (-not (Test-Path -LiteralPath $Source)) {
    Fail "找不到数据目录：$Source`n        请确认程序至少运行过一次，或用 -Source 指定目录。"
}

# ---------- 2. 建本次备份目录（时间戳，已存在则加后缀）----------
$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
if (-not (Test-Path -LiteralPath $Root)) {
    New-Item -ItemType Directory -Path $Root -Force | Out-Null
}
$dest = Join-Path $Root $stamp
if (Test-Path -LiteralPath $dest) {
    $n = 2
    while (Test-Path -LiteralPath (Join-Path $Root ($stamp + "_" + $n))) { $n++ }
    $dest = Join-Path $Root ($stamp + "_" + $n)
}
New-Item -ItemType Directory -Path $dest -Force | Out-Null

# ---------- 3. 逐项拷贝 ----------
$copied = @()
$missing = @()

foreach ($name in $whiteList) {
    if (Test-Blocked $name) { Fail "清单里出现了不允许备份的文件：$name（脚本已拒绝执行）" }

    $src = Join-Path $Source $name
    if (-not (Test-Path -LiteralPath $src)) {
        $missing += $name
        continue
    }
    try {
        Copy-Item -LiteralPath $src -Destination (Join-Path $dest $name) -Force
    }
    catch {
        Fail "拷贝 $name 失败：$($_.Exception.Message)"
    }
    $copied += (Get-Item -LiteralPath (Join-Path $dest $name))
}

if ($copied.Count -eq 0) {
    Remove-Item -LiteralPath $dest -Recurse -Force -ErrorAction SilentlyContinue
    Fail "一个文件都没备份到（数据目录里没有这 5 个文件）。已删除空的备份目录：$dest"
}

# ---------- 4. 复核：备份目录里绝不能出现排除项 ----------
$leaked = Get-ChildItem -LiteralPath $dest -File -Recurse | Where-Object { Test-Blocked $_.Name }
if ($leaked -and $leaked.Count -gt 0) {
    $names = ($leaked | ForEach-Object { $_.Name }) -join ", "
    Remove-Item -LiteralPath $dest -Recurse -Force -ErrorAction SilentlyContinue
    Fail "复核发现不该出现的文件（$names），已整目录删除，请检查白名单。"
}

# ---------- 5. 打印清单 ----------
Write-Host ""
Write-Host "================ 备份完成 ================" -ForegroundColor Green
Write-Host ("来源 : {0}" -f $Source)
Write-Host ("去向 : {0}" -f $dest)
Write-Host ("数量 : {0} 个文件" -f $copied.Count)
Write-Host ""
Write-Host ("{0,-22} {1,12}  {2}" -f "文件", "大小(字节)", "修改时间")
Write-Host ("{0,-22} {1,12}  {2}" -f ("-" * 20), ("-" * 10), ("-" * 16))
$total = 0
foreach ($f in $copied) {
    $total += $f.Length
    $mt = $f.LastWriteTime.ToString("yyyy-MM-dd HH:mm")
    Write-Host ("{0,-22} {1,12}  {2}" -f $f.Name, $f.Length, $mt)
}
Write-Host ("{0,-22} {1,12}" -f "合计", $total)
Write-Host "==========================================" -ForegroundColor Green

if ($missing.Count -gt 0) {
    Write-Host ""
    Write-Host ("[提示] 以下文件不存在，已跳过（不影响其他文件备份）：{0}" -f ($missing -join ", ")) -ForegroundColor Yellow
}

# ---------- 6. 明确说明排除项与 key 的去向 ----------
Write-Host ""
Write-Host "已按要求排除：全部 .exe / .dll、log.txt、crash.txt、全部 .png、apikey.txt" -ForegroundColor Gray

$keyPath = Join-Path $Source "apikey.txt"
if (Test-Path -LiteralPath $keyPath) {
    Write-Host ""
    Write-Host "[重要] apikey.txt 没有备份，也不会自动备份。" -ForegroundColor Yellow
    Write-Host "       它是 API Key 明文，只适合放密码管理器（1Password / Bitwarden / KeePass 等），" -ForegroundColor Yellow
    Write-Host "       不适合进网盘、U 盘或发给别人。请自行另存一份到密码管理器，" -ForegroundColor Yellow
    Write-Host "       换电脑时从那一条记录里复制回 %APPDATA%\SolivineQuickLook\apikey.txt。" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "还原方法：先停掉程序，把上面目录里的文件拷回数据目录即可（详见 docs\DATA.md）。"
Write-Host ""
exit 0
