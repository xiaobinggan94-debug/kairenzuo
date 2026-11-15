<#
.SYNOPSIS
    一键回归：把 exe 里的自检批量跑一遍，汇总通过/失败，返回可判断的退出码。

.DESCRIPTION
    每个自检都是一个独立开关（--gramcheck、--foundationcheck …），各自把结果写进一个 txt。
    这个脚本照着清单批量跑，跑完读回 txt 数「通过 / 失败」，最后打一张汇总表。

    清单分三类（-List 可看）：
      core   —— 默认就跑。纯逻辑 + 能自己跑完的窗口自检，不联网、不要麦克风、不等人。
      env    —— 要外部条件（网络 / 真 Key / OCR / 麦克风 / 别人先把引导看过）。-All 才跑。
      detail —— 只打明细不给自己下结论（控件清单、快捷键表这类），默认跑，人工扫一眼。

    判定规则（重要，别踩）：
      ① 结果文件末尾若有汇总数字（「合计 28 项：通过 28 / 失败 0」「通过=21 失败=0」），以它为准；
         不能拿「失败」二字去数——汇总行本身带「失败 0」三个字，数出来会误判成失败。
      ② 没有汇总数字，就看断言行：「[OK] / [通过] / [FAIL] / [失败]」。
      ③ detail 类不下结论，只把文件留给你。

.PARAMETER Exe
    要测的 exe。不给就自动找：release\*.exe（跳过 bak_*）→ 仓库根\*.exe。

.PARAMETER OutDir
    结果目录，默认 temp\runchecks_<时间戳>\。

.PARAMETER Only
    只跑这几项（名字见 -List）。

.PARAMETER Skip
    跳过这几项。

.PARAMETER All
    连 env 组一起跑。

.PARAMETER TimeoutSec
    单项默认超时秒数，默认 180；单项自己写了 Timeout 的以单项为准。

.PARAMETER List
    只列清单，不跑。

.EXAMPLE
    .\tools\runchecks.ps1

.EXAMPLE
    .\tools\runchecks.ps1 -All -TimeoutSec 240

.EXAMPLE
    .\tools\runchecks.ps1 -Only gramcheck,foundationcheck

.EXAMPLE
    .\tools\runchecks.ps1 -Skip mlcheck -OutDir D:\tmp\rc1
#>
[CmdletBinding()]
param(
    [string]$Exe = "",
    [string]$OutDir = "",
    [string[]]$Only = @(),
    [string[]]$Skip = @(),
    [switch]$All,
    [int]$TimeoutSec = 180,
    [switch]$List
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch {}

# ── 清单 ────────────────────────────────────────────────────────────
# Kind: core = 默认跑；env = 要外部条件，-All 才跑；detail = 默认跑但只打明细
# Timeout: 可省，省了用 -TimeoutSec
$CHECKS = @(
    @{ Name = 'pronouncheck';    Kind = 'core';   Desc = '称呼铁律兜底：他/她/TA 改成「对方」，译文正文不动' }
    @{ Name = 'abbrevcheck';     Kind = 'core';   Desc = '英文进来不显示缩写版，中文翻英文三档照旧' }
    @{ Name = 'gramcheck';       Kind = 'core';   Desc = '语法段：关掉与上版逐字一致，打开多一块，导出剔除' }
    @{ Name = 'dictcheck';       Kind = 'core';   Desc = '离线词库：能读、变形词还原、能力边界守住' }
    @{ Name = 'foundationcheck'; Kind = 'core';   Desc = '地基：编码嗅探 / 请求序号防竞态 / 配置兜底' }
    @{ Name = 'exportcheck';     Kind = 'core';   Desc = '导出 TXT / docx + 复制全文入口' }
    @{ Name = 'redactcheck';     Kind = 'core';   Desc = '脱敏：敏感内容不外泄' }
    @{ Name = 'kbcheck';         Kind = 'core';   Desc = '键盘交互（上下翻历史等）' }
    @{ Name = 'pastecheck';      Kind = 'core';   Desc = '粘贴就查：短句直翻、长段先问、不连发' }
    @{ Name = 'dpicheck';        Kind = 'core';   Desc = 'DPI 缩放与布局不挤字' }
    @{ Name = 'readcheck';       Kind = 'core';   Desc = '朗读三层逻辑：切句、重音、停顿（只算文本不发声）' }
    @{ Name = 'stopcheck';       Kind = 'core';   Desc = '朗读急停：Esc 掐声音、F2/F3 变开关、停完能再读（不出声）' }
    @{ Name = 'announcecheck';   Kind = 'core';   Desc = 'C1 公告卡：解析、指纹去重、空白不扰、卡片与菜单入口（不碰真实公告）' }
    @{ Name = 'diagcheck';       Kind = 'core';   Desc = 'A3 一键诊断包：报告各段齐全、密钥打码、真 key 不入包、zip 能生成并读回' }
    @{ Name = 'hkcheck';         Kind = 'detail'; Desc = '实测快捷键表：候选 / 显示名 / 拆键 / 坏配置落回默认' }
    @{ Name = 'aboutcheck';      Kind = 'detail'; Desc = '关于窗里的版本号、编译时间、仓库地址明细' }
    @{ Name = 'audit';           Kind = 'detail'; Desc = '主窗口控件树越界清单（窗口没撑开时全是误报，看 selftest 里的）' }

    @{ Name = 'firstruncheck';   Kind = 'env';    Desc = '首次引导窗（本机已看过引导会显示 1 项失败，属正常）' }
    @{ Name = 'mlcheck';         Kind = 'env';    Desc = '其他语言小窗；本机实测会挂住不返回，给足时间或 -Skip' ; Timeout = 300 }
    @{ Name = 'selftest';        Kind = 'env';    Desc = '界面自检 + 截图，不写结果文件（配合 --preview / --shot 用）' }
    @{ Name = 'proxycheck';      Kind = 'env';    Desc = '代理分流：本地不走代理、公网仍走（要网络）' }
    @{ Name = 'keycheck';        Kind = 'env';    Desc = 'Key 能不能用（要真 Key + 网络，会花钱）' }
    @{ Name = 'ingestcheck';     Kind = 'env';    Desc = '拖文件 / Ctrl+V 图片 OCR（要 OCR 语言包）' }
    @{ Name = 'voicecheck';      Kind = 'env';    Desc = '语音输入（要麦克风）' }
    @{ Name = 'crashtest';       Kind = 'env';    Desc = 'UI 线程抛异常后的兜底（会弹窗，需人工看）' }
    @{ Name = 'errtest';         Kind = 'env';    Desc = '出错提示条（要人工点一下）' }
    @{ Name = 'dictdemo';        Kind = 'env';    Desc = '断网结果真显示在桌面上（3 秒后自己关）' }
)

if ($List) {
    "{0,-16} {1,-6} {2}" -f '检查', '类别', '说明'
    $CHECKS | ForEach-Object { "{0,-16} {1,-6} {2}" -f $_.Name, $_.Kind, $_.Desc }
    exit 0
}

# ── 找 exe ──────────────────────────────────────────────────────────
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Exe) {
    $cand = @()
    $rel = Join-Path $repo 'release'
    if (Test-Path $rel) {
        $cand += Get-ChildItem $rel -Filter *.exe -File |
                 Where-Object { $_.Name -notlike 'bak_*' } | Sort-Object LastWriteTime -Descending
    }
    $cand += Get-ChildItem $repo -Filter *.exe -File | Sort-Object LastWriteTime -Descending
    $first = $cand | Select-Object -First 1
    if (-not $first) { Write-Host "找不到 exe。先跑 tools\build.ps1，或用 -Exe 指定。" -ForegroundColor Red; exit 2 }
    $Exe = $first.FullName
}
if (-not (Test-Path $Exe)) { Write-Host "exe 不存在：$Exe" -ForegroundColor Red; exit 2 }
$Exe = (Resolve-Path $Exe).Path
$exeItem = Get-Item $Exe

# ── 结果目录 ────────────────────────────────────────────────────────
if (-not $OutDir) {
    $OutDir = Join-Path $repo ("temp\runchecks_" + (Get-Date -Format 'yyyyMMdd_HHmmss'))
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

# ── 挑清单 ──────────────────────────────────────────────────────────
$run = $CHECKS | Where-Object { $All -or $_.Kind -ne 'env' }
if ($Only.Count -gt 0) { $run = $CHECKS | Where-Object { $Only -contains $_.Name } }
if ($Skip.Count -gt 0) { $run = $run | Where-Object { $Skip -notcontains $_.Name } }
$run = @($run)
if ($run.Count -eq 0) { Write-Host "没有要跑的项，用 -List 看清单名。" -ForegroundColor Yellow; exit 2 }

$ver = $exeItem.VersionInfo
Write-Host ""
Write-Host "被测文件 : $Exe" -ForegroundColor Cyan
Write-Host ("文件属性 : {0}   大小 {1:N0} 字节   改于 {2}" -f $ver.FileVersion, $exeItem.Length, $exeItem.LastWriteTime)
Write-Host "结果目录 : $OutDir" -ForegroundColor Cyan
Write-Host ("共 {0} 项（core/detail 为默认，env 只有 -All 才跑）" -f $run.Count)
Write-Host ("-" * 80)

# ── 跑 ──────────────────────────────────────────────────────────────
$rows = @()
$idx = 0
foreach ($c in $run) {
    $idx++
    $name = $c.Name
    $to = if ($c.ContainsKey('Timeout')) { [int]$c.Timeout } else { $TimeoutSec }
    $txt = Join-Path $OutDir ($name + '.txt')
    if (Test-Path $txt) { Remove-Item $txt -Force }
    Write-Host -NoNewline ("[{0,2}/{1}] {2,-16}" -f $idx, $run.Count, $name)

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $timedOut = $false; $exitCode = $null; $launchErr = $null
    try {
        $p = Start-Process -FilePath $Exe -ArgumentList @(('--' + $name), $txt) -PassThru
        try {
            Wait-Process -Id $p.Id -Timeout $to -ErrorAction Stop
            $exitCode = $p.ExitCode
        } catch {
            $timedOut = $true
            try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch {}
        }
    } catch {
        $launchErr = $_.Exception.Message
    }
    $sw.Stop()
    $sec = [Math]::Round($sw.Elapsed.TotalSeconds, 1)

    # ── 收结果 ──
    $pass = 0; $fail = 0; $note = ''
    if ($launchErr) {
        $verdict = '没跑起来'; $note = $launchErr
    } elseif ($timedOut) {
        $verdict = '超时'; $note = "$to 秒没结束，已强杀"
    } elseif (-not (Test-Path $txt)) {
        if ($c.Kind -eq 'env' -and $name -eq 'selftest') { $verdict = '明细'; $note = '这项本来就不写文件（界面截图类）' }
        else { $verdict = '无输出'; $note = '没生成结果文件' }
    } else {
        $text = Get-Content $txt -Raw -Encoding UTF8
        if (-not $text) { $text = '' }
        # ① 汇总数字优先
        $failNums = [regex]::Matches($text, '(?:失败|FAIL)\s*[=:：]?\s*(\d+)')
        $passNums = [regex]::Matches($text, '(?<!不)通过\s*[=:：]?\s*(\d+)')
        if ($failNums.Count -gt 0 -or $passNums.Count -gt 0) {
            if ($passNums.Count -gt 0) { $pass = [int]$passNums[$passNums.Count - 1].Groups[1].Value }
            if ($failNums.Count -gt 0) { $fail = [int]$failNums[$failNums.Count - 1].Groups[1].Value }
            if ($fail -gt 0)     { $verdict = '失败' }
            elseif ($pass -gt 0) { $verdict = '通过' }
            else                 { $verdict = '没断言'; $note = '汇总说 0 项通过' }
        } else {
            # ② 断言行
            $fail = ([regex]::Matches($text, '(?m)^\s*\[[^\]\r\n]*\][^\r\n]*(?:失败|FAIL)')).Count
            $pass = ([regex]::Matches($text, '(?m)^\s*\[(?:OK|PASS|通过)[^\]\r\n]*\]|\s\bok\s*$')).Count
            if ($pass -eq 0) { $pass = ([regex]::Matches($text, '(?m)^\s*\[[^\]\r\n]+\][^\r\n]*(?<!不)通过')).Count }
            if ($fail -gt 0)                    { $verdict = '失败' }
            elseif ($pass -gt 0)                { $verdict = '通过' }
            elseif ($c.Kind -eq 'detail')       { $verdict = '明细'; $note = '只打明细，人工扫一眼' }
            else                                { $verdict = '没断言'; $note = '文件里没有通过/失败字样' }
        }
        if ($verdict -eq '明细' -and -not $note) { $note = '只打明细，人工扫一眼' }
    }

    $color = switch ($verdict) { '通过' { 'Green' } '失败' { 'Red' } '明细' { 'Gray' } default { 'Yellow' } }
    Write-Host ("{0,6}  通过 {1,3}  失败 {2,3}  {3,5}s  {4}" -f $verdict, $pass, $fail, $sec, $note) -ForegroundColor $color

    $rows += [pscustomobject]@{
        Name = $name; Verdict = $verdict; Pass = $pass; Fail = $fail; Sec = $sec
        Exit = $exitCode; Note = $note; Kind = $c.Kind; Desc = $c.Desc; File = $txt
    }
}

# ── 汇总 ────────────────────────────────────────────────────────────
$ok      = @($rows | Where-Object { $_.Verdict -eq '通过' }).Count
$detail  = @($rows | Where-Object { $_.Verdict -eq '明细' }).Count
$bad     = @($rows | Where-Object { $_.Verdict -in @('失败', '没跑起来', '超时', '无输出') }).Count
$unknown = @($rows | Where-Object { $_.Verdict -eq '没断言' }).Count
$totalPass = ($rows | Measure-Object -Property Pass -Sum).Sum
$totalFail = ($rows | Measure-Object -Property Fail -Sum).Sum
if ($null -eq $totalPass) { $totalPass = 0 }
if ($null -eq $totalFail) { $totalFail = 0 }

Write-Host ("-" * 80)
if ($bad -eq 0 -and $unknown -eq 0) {
    Write-Host ("全过：{0} 项下了结论的都通过，{1} 条断言全中；另有 {2} 项明细已存档。" -f $ok, $totalPass, $detail) -ForegroundColor Green
} else {
    Write-Host ("有情况：通过 {0} / 有问题 {1} / 没法判定 {2} / 明细 {3}；断言 通过 {4} 失败 {5}" -f $ok, $bad, $unknown, $detail, $totalPass, $totalFail) -ForegroundColor Red
    foreach ($r in ($rows | Where-Object { $_.Verdict -in @('失败', '没跑起来', '超时', '无输出', '没断言') })) {
        Write-Host ("  [x] {0,-16} {1}  {2}" -f $r.Name, $r.Verdict, $r.Note) -ForegroundColor Red
    }
}

# 落一份汇总，方便事后翻
$sum = Join-Path $OutDir '_summary.txt'
$lines = @()
$lines += "被测 exe : $Exe"
$lines += "文件属性 : $($ver.FileVersion) / $($exeItem.Length) 字节 / 改于 $($exeItem.LastWriteTime)"
$lines += "时间     : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$lines += "总耗时   : $([Math]::Round((($rows | Measure-Object -Property Sec -Sum).Sum), 1)) 秒"
$lines += ("结果     : 通过 {0} / 有问题 {1} / 没法判定 {2} / 明细 {3}；断言 通过 {4} 失败 {5}" -f $ok, $bad, $unknown, $detail, $totalPass, $totalFail)
$lines += ''
$lines += ('{0,-16} {1,-6} {2,-8} {3,4} {4,4} {5,6}  {6}' -f '检查', '类别', '结果', '通过', '失败', '秒', '说明')
foreach ($r in $rows) {
    $lines += ('{0,-16} {1,-6} {2,-8} {3,4} {4,4} {5,6}  {6}' -f $r.Name, $r.Kind, $r.Verdict, $r.Pass, $r.Fail, $r.Sec, (($r.Note + ' ' + $r.Desc).Trim()))
}
[System.IO.File]::WriteAllLines($sum, $lines, (New-Object System.Text.UTF8Encoding($false)))
Write-Host ""
Write-Host "汇总 : $sum"
Write-Host "明细 : $OutDir"

if ($bad -gt 0) { exit 1 }
if ($unknown -gt 0) { exit 3 }
exit 0
