# 编译、自检与部署

这份文档只讲一件事：**怎么把 `src\` 里的源码变成能双击运行的 exe，怎么证明改完没把老功能弄坏，怎么安全地换到线上那份 exe。**

日常改代码的说明、功能清单、API Key 配置，见仓库根目录的 [`README.md`](../README.md)。

---

## 一、环境要求

| 项 | 要求 | 说明 |
| --- | --- | --- |
| 操作系统 | Windows 10 / 11（Windows 7 及以上的 .NET 4.x 也能跑） | 只在 Windows 上编译，跨平台不适用 |
| .NET Framework | 4.x（Win10/11 自带 4.8） | 提供 `csc.exe` 与运行库，**不需要**装 Visual Studio |
| 编译器 | `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` | 32 位系统会在 `Framework\` 下，脚本自己找 |
| PowerShell | 5.1（系统自带） | 编译脚本是 `.ps1` |
| 第三方库 | 无 | 全部用 .NET 自带程序集，没有 NuGet、没有 package.json |

需要的程序集一共 7 个，全是系统自带的：`System` / `System.Core` / `System.Drawing` / `System.Windows.Forms` / `System.Xml` / `System.Speech` / `System.Web.Extensions`。

> `System.Speech` 用来朗读（SAPI），`System.Web.Extensions` 里的 `System.Web.Script.Serialization` 用来解析接口返回的 JSON。

---

## 二、一条命令编译

在仓库根目录打开 PowerShell：

```powershell
powershell -ExecutionPolicy Bypass -File tools\build.ps1
```

跑完在 `release\` 下生成 `翻译器.exe`，控制台最后打印文件大小和 SHA256：

```
================ 编译完成 ================
文件 : E:\GitHub\kairenzuo\release\翻译器.exe
大小 : 2234880 字节 (2,182.5 KB)
SHA256: FD50FF39...
==========================================
```

### build.ps1 参数

| 参数 | 默认值 | 作用 |
| --- | --- | --- |
| `-OutputName` | `翻译器.exe` | 改输出文件名，例如 `-OutputName "测试版.exe"`；仍然输出到 `release\` |
| `-NoOptimize` | 关（即**默认开优化**） | 加 `-NoOptimize` 则不加 `/optimize+`；不优化时体积会明显变大（约 +27 KB），排查编译参数差异时才用 |

两个参数可以一起用：

```powershell
powershell -ExecutionPolicy Bypass -File tools\build.ps1 -OutputName "对照_未优化.exe" -NoOptimize
```

### 脚本内部做了四件事

**1. 找 csc.exe**（`Find-Csc`）

按 `%WINDIR%\Microsoft.NET\Framework64` → `Framework` 的顺序，各目录下挑名字匹配 `^v4\.` 的版本目录，按版本号从高到低试，取第一个存在 `csc.exe` 的。两级都没有 v4.x 才退回 `v3.5\csc.exe`（老系统兜底）。

**2. 找程序集真实路径**（`Resolve-Reference` + `Find-GacAssembly`）

- `System` / `System.Core` / `System.Drawing` / `System.Windows.Forms` / `System.Xml`：优先用 csc 同目录（框架目录）里的同名 dll，与编译器版本最匹配。
- 框架目录里没有的，去 GAC（`%WINDIR%\Microsoft.NET\assembly` 和老的 `%WINDIR%\assembly`）递归搜同名 dll，按文件版本从高到低取第一个。
- **`System.Speech` 必须走 GAC 全路径**：它不在 csc 的默认引用列表里，只写 `/r:System.Speech.dll` 会报 `CS0006 未能找到元数据文件`，必须写 `/r:C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Speech\v4.0_4.0.0.0__31bf3856ad364e35\System.Speech.dll` 这种全路径。脚本自动找到它，不用手改。
- 同理 `System.Web.Extensions` 也从 GAC 找。

**3. 收集源码**

只取 `src\` 下顶层的 `*.cs`，按文件名排序后全部传给 csc；**固定排除 `src\preview_shot.cs`**——那是个自带 `Main` 的独立截图小工具，混进来会和 `Program.cs` 的 `Main` 冲突，报 `CS0017 程序定义了多个入口点`。

**4. 编译并收尾**

```
/nologo /noconfig /target:winexe /platform:anycpu /codepage:65001
/optimize+ /win32icon:assets\icons\cookie4.ico /out:release\翻译器.exe
/r:<7 个程序集全路径>  <src\*.cs 全路径>
```

| 参数 | 为什么 |
| --- | --- |
| `/noconfig` | 关掉默认响应文件 `csc.rsp`，否则手动 `/r:System.Web.Extensions.dll` 会撞 CS1703（见第四节） |
| `/codepage:65001` | 源码里有大量中文，声明按 UTF-8 读，避免注释和字符串变乱码 |
| `/optimize+` | 打开 IL 优化（内联、折叠、裁剪），默认开启；关掉大约多 27 KB |
| `/target:winexe` | 生成 GUI 程序，双击不弹黑框 |
| `/platform:anycpu` | 64/32 位系统都能跑 |
| `/win32icon` | 把 `assets\icons\cookie4.ico` 嵌进 exe，资源管理器里显示像素猫脸图标 |

---

## 二·补、另一条路：用项目文件编译（.csproj）

`src\SolivineQuickLook.csproj` 是同一份配方的「工程化写法」，给装了 .NET SDK 或 Visual Studio 的机器用。它和 `build.ps1` 编出来的是同一个程序——界面审计输出逐字节相同（见第三节的自检办法）。

```powershell
dotnet build src\SolivineQuickLook.csproj -c Release
```

产物在 `src\bin\Release\net472\翻译器.exe`，**不会动 `release\` 里的工作副本**。

| 项目 | csproj 里的写法 | 对应 build.ps1 的 |
| --- | --- | --- |
| 目标框架 | `<TargetFramework>net472</TargetFramework>` | 系统 `csc.exe`（4.x 运行时） |
| 输出类型 | `WinExe` / `AnyCPU` / `Optimize` | `/target:winexe /platform:anycpu /optimize+` |
| 程序图标 | `<ApplicationIcon>..\assets\icons\cookie4.ico` | `/win32icon:...` |
| 离线词库 | `EmbeddedResource` + `LogicalName=OfflineDict` | `/resource:...,OfflineDict` |
| 构建号 | `GenerateSrcRev` 目标，编译时跑 `git rev-parse` 写进 `SrcRev` | 脚本里先算好写临时文件，再 `/resource:` |
| 源码范围 | `*.cs`，显式排除 `preview_shot.cs` | 同 |
| 程序集属性 | `GenerateAssemblyInfo=false`（属性写在 `Program.cs` 的 `[assembly:]`） | 同（csc 不自动生成） |

三条要点：

- 要改 exe 文件名就改 `<AssemblyName>`；改图标或词库路径要同步改这里，并确认 `src\Dict.cs` 的 `DICT_RES` 仍是 `OfflineDict`。
- 构建号是编译时现取的：工作区有未提交改动会自动变成 `7610298-dirty` 这种样子，跑 `--version` 就能看到。
- `bin\`、`obj\` 已在 `.gitignore` 里，不会误提交。

---

## 三、编译完记得自检

改完源码**不要直接部署**，先跑自检确认没弄坏老功能。完整清单见第六节。

最小验收（每次改动都该跑）：

```powershell
# 控件清单：主窗口 + 三个小窗的结构快照
release\翻译器.exe --audit release\audit.txt

# 全量自检：逐项打印 [PASS]
release\翻译器.exe --selftest --preview --sz 480x660 --dump release\selftest.rtf --render release\selftest.png
```

如果只是重构、不该有任何界面变化，正确做法是：**改前跑一次 `--audit` 存一份，改后再跑一次，两次输出逐字节相同**才说明真的「逻辑零改动」。

---

## 四、CS1703 与 System.Web.Extensions

**报错长这样：**

```
error CS1703: 已导入具有相同标识的程序集 "...\System.Web.Extensions.dll"。
请删除其中一个重复引用。
```

**原因：** csc 默认会读同目录下的响应文件 `csc.rsp`，里面已经自动引了一大串程序集（`System.dll`、`System.Xml.dll`、`System.Web.Extensions.dll` 等）。脚本为了显式列出 `System.Web.Extensions` 又加了一条 `/r:`，同一个程序集被引两次 → CS1703。

**解法：** 加 `/noconfig` 关掉 `csc.rsp`，然后**把需要的程序集全部点名列出**。代价是原本靠 `csc.rsp` 白送的 `System.dll`、`System.Xml.dll` 也得自己写上——脚本的 `$refNames` 已经列全，不用管。

**结论：** `/noconfig` + 7 条显式 `/r:` 是唯一稳定的组合，两者必须成对出现。只加 `/noconfig` 不加引用会变成一片 `CS0246 找不到类型`；只加 `/r:` 不加 `/noconfig` 就是 CS1703。

---

## 五、常见错误对照表

| 报错 / 现象 | 原因 | 怎么办 |
| --- | --- | --- |
| `[ERROR] 没找到 csc.exe，请确认已安装 .NET Framework 4.x` | 系统缺 .NET Framework 4.x，或框架目录被裁剪过 | 装 .NET Framework 4.8 运行库；确认 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` 存在 |
| `[ERROR] 找不到 System.Speech.dll（请确认本机安装了 .NET Framework 4.x 语音组件）` | GAC 里没有 `System.Speech`（精简版系统、Server 核心版常见） | 装 .NET Framework 4.8 完整运行库（含语音组件）；控制面板 →「程序和功能」→ 启用/关闭 Windows 功能 → .NET Framework 4.x 全勾上 |
| `error CS0006: 未能找到元数据文件 "System.Speech.dll"` | 只写了 `/r:System.Speech.dll` 这种简名，csc 不会去 GAC 找 | 必须写 GAC 里的全路径；用 `tools\build.ps1` 就不会遇到 |
| `error CS1703: 已导入具有相同标识的程序集` | `csc.rsp` 已引 + 手动又引一次 | 加 `/noconfig`，见第四节 |
| `error CS0234: 命名空间中不存在类型或命名空间名 "Script"` | 少了 `System.Web.Extensions` 引用（`JavaScriptSerializer` 在这里） | 确认 `/r:` 里有 `System.Web.Extensions.dll` 全路径；`/noconfig` 之后必须显式加 |
| `error CS0017: 程序定义了多个入口点` | `src\preview_shot.cs` 被一起编了 | 排除它；`build.ps1` 的 `$excludeSources` 已固定排除 |
| `error CS0103: 当前上下文中不存在名称 "XXX"` / `CS0115` | 漏了某个 `partial` 文件，或 `src\` 下新增文件没被收集 | 主类 `QueryForm` 拆成多个 `partial` 文件，**必须整个 `src\` 一起编**，单独编一个文件必然缺另一半 |
| 编译通过但界面中文全是乱码 | 没加 `/codepage:65001`，源码按 ANSI 解读 | 用 `build.ps1`；手写命令时保留 `/codepage:65001` |
| 警告 `CS1690` / `CS0219` | 语音设置字段访问、未使用变量 | 不影响运行，可忽略 |
| 双击 exe 提示没配置 key | 缺 `%APPDATA%\SolivineQuickLook\apikey.txt` | 见 README 第三节；**不要把 key 写进源码** |

---

## 六、自检入口清单

程序内置了一批命令行自检，全部不需要联网（`--selftest` 里的查询链路除外）、不碰真实配置（会造临时文件并在结束时删掉）。参数格式统一是「参数名 + 输出文件路径」。

| 命令 | 验什么 | 输出 |
| --- | --- | --- |
| `--version` | 打印版本行 `v1.0.0  编译=…  源码=…` | 控制台 + `version.txt`（已 gitignore） |
| `--audit <文件>` | 控件树快照：主窗口、历史记录、生词本三个窗体逐控件 dump（类名、区域、文字、可见性） | `audit.txt`（默认），同目录额外生成两张小窗截图 png |
| `--selftest` | 全量功能自检，逐项打印 `[PASS]` / `[FAIL]` | 控制台 + 报告文件 |
| `--selftest --dump <文件>` | 在上面基础上，把渲染出的文字内容导出成带格式的 rtf | `selftest.rtf` |
| `--selftest --render <文件>` | 真实绘制窗口并截图存盘（用来人眼确认排版没崩） | png |
| `--selftest --preview --sz 480x660` | 按指定尺寸跑一遍，验窄窗口下的自适应布局 | png |
| `--foundationcheck <文件>` | 地基 21 项：编码嗅探（UTF-8 带/不带 BOM、GBK、UTF-16、老日志）、请求序号防竞态、settings 缺字段兜底 | 报告 txt |
| `--exportcheck <文件>` | 导出链路：TXT / Word（docx 结构）内容正确性 | 报告 txt |
| `--ingestcheck <文件>` | 拖文件 / 粘贴图片自动分流与 OCR 取字 | 报告 txt |
| `--voicecheck <文件>` | 语音输入链路（设备枚举、识别回调） | 报告 txt |
| `--readcheck <文件>` | 朗读三层逻辑：逐词语义切分、短句一口气 + 核心重读、长句分段重音 + 段间停顿、标点分级停顿 | 报告 txt |
| `--stopcheck <文件>` | 朗读急停：空闲时按停返回 false 且不改状态、朗读中按停真停并归位、连按幂等、停完能重新开读、提示条出现「已停」（全程不出声） | 报告 txt |
| `--announcecheck <文件>` | 公告卡：首行当标题、正文原样、内容指纹去重（同内容不重弹、改一字重弹）、文件缺失时铺样例不报错、弹过后「公告…」仍能翻出来（全程不读真实公告文件） | 报告 txt |
| `--diagcheck <文件>` | 一键诊断包：报告各段齐全（版本/系统/屏幕/内存/磁盘/设置/目录/日志）、`sk-` 与长十六进制串已打码、普通文字不被误伤、真 key 的任何片段都不入包、zip 能真生成并读回 | 报告 txt + zip |
| `--mlcheck <文件> [--mlshot <目录>]` | 其他语言小窗：方向（阿语/希伯来语右对齐）、导出、长文分流、首次引导 | 报告 txt（+ 截图） |
| `--proxycheck <文件>` | 出网代理分流：本机 / 内网直连、公网跟随系统代理（起探针实测是否被代理劫持） | 报告 txt |
| `--pronouncheck <文件>` | 称呼铁律：解释/点评里的「他/她/TA」是否被换成「对方」，译文正文是否忠实原文 | 报告 txt |
| `--crashtest <目录>` | 全局异常兜底：造一次崩溃，确认 `crash.txt` 有记录、程序能自己退干净 | 目录内产物 |
| `--snapform <目录>` | 把四个窗体各截一张图（做发布配图用） | png |
| `--dictcheck <文件>` | 断网离线查词 28 项：词库加载耗时、常用词/变形词还原（went→go、customers→customer）、能力边界（中文不硬撑、查不到就说查不到）、**哪些错误才该降级**（连不上才降级，401/402/429 必须原样报出去）、释义干净度、查询速度 | 报告 txt |
| `--dictshot <文件>` | 在 `--dictcheck` 之上把界面切到「断网出离线结果」这一幕，做控件树审计并截图（自检环境主窗口客户区常年 0×0，这里会自己撑成 420×640） | 报告 txt + png |
| `--dictdemo` | 真的把离线结果弹在桌面上（单词 → 整句 → 中文不接，三段轮播，4 秒后自己关），供外面用 `PrintWindow` 截真实渲染图 | 无（截屏用） |

> `--readcheck` 在 2026-10-28 那次部署版上有 1 项失败（既有问题，重构前就在），不属于回归；其余五项自检输出与重构前逐行一致。

---

## 七、部署：把新 exe 换到线上那份

线上运行的 exe 不在仓库里，而是拷到数据目录：

```
%APPDATA%\SolivineQuickLook\翻译器.exe
```

（完整路径形如 `C:\Users\你的用户名\AppData\Roaming\SolivineQuickLook\翻译器.exe`。放这里是为了让 exe 和 `settings.ini`、`apikey.txt` 待在一起。）

**必须按这四步走，顺序不能换。** 尤其第一步不能省——exe 正在运行的时候覆盖会报「文件被占用」，硬覆盖还可能把正在写的 `settings.ini`、`生词本数据.tsv` 写坏。

### 第 1 步：停进程

```powershell
Get-Process "翻译器" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800
```

（平时也可以右键托盘饼干 → 退出，效果一样。用 `Stop-Process -Force` 是因为托盘程序可能停在模态弹窗上。）

### 第 2 步：给旧 exe 留带时间戳的备份

```powershell
$data = Join-Path $env:APPDATA "SolivineQuickLook"
$stamp = Get-Date -Format "yyyyMMdd_HHmmss"
Copy-Item (Join-Path $data "翻译器.exe") (Join-Path $data "翻译器.exe.bak_$stamp")
```

备份文件落在同一个目录，命名如 `翻译器.exe.bak_20261028_1331`。**这一步是唯一能回退的凭据**，别跳过。

### 第 3 步：覆盖 exe

```powershell
Copy-Item "release\翻译器.exe" (Join-Path $data "翻译器.exe") -Force
```

### 第 4 步：静默启动并确认版本

```powershell
Start-Process -WindowStyle Hidden (Join-Path $data "翻译器.exe")
Start-Sleep -Seconds 2
& (Join-Path $data "翻译器.exe") --version
```

`--version` 打印的版本号要和这次发布的一致（例如 `v1.0.0`），`编译=` 后面的时间应该是你刚拷进去那一刻（程序读的是 exe 文件的修改时间）。

**回退：** 把第 2 步的 `.bak_时间戳` 文件拷回成 `翻译器.exe`（先停进程），再启动即可。

> 数据文件（生词本、历史记录、settings.ini）不受换 exe 影响，不用动。想额外保险，先跑一次 `tools\backup-data.ps1`，见 [`DATA.md`](DATA.md)。

---

## 八、关于 exe 体积与可复现性

### 为什么单个 exe 有 2.2 MB：那是编进去的离线词库

`assets/dict/offline_dict.txt`（1.66 MB UTF-8 文本：20225 个常用词 + 20795 条变形词）由 `build.ps1` 用 `/resource:…,OfflineDict` 编进 exe，程序只在不联网那条路上读它。

- 交付仍是单个 exe，不需要额外的词库文件，拷到别的电脑直接能用；
- 换词库 / 扩词库：改 `assets/dict/offline_dict.txt` 后重新编译；资源名 `OfflineDict` 两边必须一致（`build.ps1` 的 `/resource` 与 `src/Dict.cs` 的 `DICT_RES` 常量）；
- 体积档位参考：只留 3000 词约 +0.3 MB，常用 2 万词 +1.66 MB（当前这档），全量 ECDICT 76 万词约 +60 MB——全量不适合编进 exe，会明显拖慢启动；
- 词库是懒加载：启动、唤出、平时联网查询全程不读它，只有真的走到「网络不通」那一步才加载一次（实测 32 ms）。

### 可复现性：体积可复现，哈希不可复现

| 能做到 | 做不到 |
| --- | --- |
| 同样的参数 + 同样的源码 → **同样的体积**（可稳定复现） | 同样的 SHA256 |

原因：这种老式 `csc.exe` 会把编译时间写进 PE 头（COFF TimeDateStamp），每次编译还会生成新的 MVID（模块版本 ID），两处都随机/随时间变化，所以**同源码编两次，大小一样、哈希不同**。这也是为什么验收不能靠「比对哈希」，只能靠第六节的行为自检（`--audit` 输出逐字节比对、`--selftest` 全 `[PASS]`）。

另外注意：程序里 `AppVersionLine()` 打印的 `编译=` 是**运行时读 exe 文件的修改时间**，不是编进 exe 的常量；`源码=` 后面的编号由 `build.ps1` 编译时自动从 git 取当前提交短号（工作区有未提交改动时会带 `-dirty`），所以发布前记得先提交再编译。

### exe 不进仓库，走 Release 附件

`.gitignore` 里排除了 `release/` 与 `*.exe`，编译产物不提交。对外发布的 exe 直接挂在 GitHub Release 上：

```powershell
# 1) 先提交源码，再编译（这样 exe 里的「源码」编号对应得上这次提交）
git add -A; git commit -m "v1.0.0"
tools\build.ps1

# 2) 打 tag 并推送
git tag v1.0.0; git push origin main --tags

# 3) 把 release\翻译器.exe 传到该 tag 的 Release，正文附上 SHA256
Get-FileHash release\翻译器.exe -Algorithm SHA256
```

> `release\翻译器.exe`（不带版本后缀）是 `build.ps1` 的默认输出，属于本地工作副本，永远不进仓库。
