# 本机数据文件说明

程序不写注册表、不写数据库，所有数据都是**纯文本文件**，统一放在这一个目录：

```
%APPDATA%\SolivineQuickLook\
```

（完整路径形如 `C:\Users\你的用户名\AppData\Roaming\SolivineQuickLook`。按 `Win + R` 输入 `%APPDATA%` 回车就能进到 `Roaming`，里面找 `SolivineQuickLook`。）

exe 本身也放在这个目录里，所以「exe + 你的数据」是同一个文件夹。**这个目录里绝大多数文件都可以删，只有下面第一张表里的五个不能随手删。**

---

## 一、核心数据（要备份，删了找不回来）

| 文件 | 是什么 | 删了会怎样 | 进仓库 | 备份脚本 |
| --- | --- | --- | --- | --- |
| `生词本数据.tsv` | **生词本的真正数据源**。TSV 五列：`日期` `单词` `音标` `意思` `近音`，一行一个词。新版按日期分组、自动去重都靠它 | 生词本清空，积累的词全没 | ❌ | ✅ |
| `生词本.txt` | 老格式的生词本（人能直接读的排列）。老版本只存这个；新版第一次运行会把里面的词自动迁进 `生词本数据.tsv`，之后两个文件都会保持 | 丢的是老格式副本，词本身还在 tsv 里 | ❌ | ✅ |
| `历史记录.txt` | 最近 20 条查询记录（时间 + 原文 + 结果摘要），点一条能翻回来 | 记录清空，不影响查询 | ❌ | ✅ |
| `settings.ini` | 设置：窗口位置与大小、字号、语气（自动/正式/随意）、朗读发音人与语速、是否显示音标、调试开关等。格式是 `键=值`，缺字段自动走默认值 | 恢复出厂设置：窗口位置、字号、语气、发音人全部回默认 | ❌ | ✅ |

备份一句命令搞定（见 [`../tools/backup-data.ps1`](../tools/backup-data.ps1)）：

```powershell
powershell -ExecutionPolicy Bypass -File tools\backup-data.ps1
```

它会把这五个文件拷到 `%USERPROFILE%\Documents\SolivineBackup\时间戳\`，其余一律不碰。

---

## 二、API Key（唯一不能靠备份保命的文件）

| 文件 | 是什么 | 进仓库 | 备份脚本 |
| --- | --- | --- | --- |
| `apikey.txt` | DeepSeek 的 API Key，**明文一行**，形如 `sk-xxxxxxxx`。程序查询时读它，读不到就只显示「还没配置 API Key」的提示 | ❌ **绝对不进** | ❌ **故意排除** |

### 为什么不进仓库

仓库即使是 Private，也不要提交 key：一旦哪天改成 Public、或者仓库被 clone 到别的机器、或者截图时带上了这个文件，key 就流出去了，别人可以用它消耗你的额度。

`src\Translate.cs` 里没有任何硬编码 key，程序只在运行时从 `%APPDATA%\SolivineQuickLook\apikey.txt` 读。`.gitignore` 里也写死了 `apikey.txt`。

### 为什么备份脚本也故意不拷它

备份目录 `Documents\SolivineBackup\` 是最容易被顺手同步到网盘、拷进 U 盘、或者打包发给别人的地方。密钥一旦进了这种「会流动」的目录，就等于离开了保险箱。所以脚本把它列进硬性排除名单，跑完还会专门提醒你一句「key 没备份，请自己另存」。

### 所以 apikey.txt 必须你本人另存到密码管理器

这是本机**唯一一份**拷贝，程序没有配置界面、不做云端同步、也没有找回功能。以下任意一件事发生，key 就只能重新去 DeepSeek 后台申请：

- 重装系统、换电脑
- 硬盘坏、误删这个目录
- 用清理软件「清空 AppData」

**正确做法：** 新建一条密码管理器记录（1Password / Bitwarden / KeePass / 手机自带密码本都行），标题写「DeepSeek Key - 饼饼翻译器」，内容就是 `sk-` 开头那串。换机器时从那一条记录里复制，粘回新建的 `apikey.txt` 即可。

> 顺带提醒：这个 key 是按量计费的，泄漏 = 别人花你的钱。真发现异常，去 DeepSeek 后台**吊销旧 key、生成新的**，然后更新密码管理器里的那条记录。

---

## 三、运行产生的临时/调试文件（可删）

这些文件删掉不影响任何功能，程序下次运行会自己重建。

| 文件 | 是什么 |
| --- | --- |
| `log.txt` | 运行日志，UTF-8 纯文本，排错时看这个。首次写入前会把老的 GBK 日志原位转成 UTF-8，并在旁边留一份 `log.txt.gbk.bak` |
| `crash.txt` | 未捕获异常记录，含版本行 + 完整堆栈。程序崩过就会有内容 |
| `ocr.ps1` / `ocr_ml.ps1` | 截图认字时程序自己写出来的临时 PowerShell 脚本（调用系统 OCR） |
| `ocr_shot.png` / `ocr_clip.png` / `paste_clip.png` | 截图 / 粘贴图片的临时图片 |
| `ocr_out.txt` / `ocr_ml_out.txt` | OCR 的输出中转文件 |
| `pos.txt` | 窗口位置临时文件 |
| `settings.ini.bak2` | 早期版本的配置备份 |
| `selftest.txt` / `selftest.rtf` / `dump.rtf` / `readcheck_deploy.txt` | 手动跑 `--selftest` / `--dump` / `--readcheck` 留下的报告 |
| `效果_*.png` / `cookie_ch_en_preview.png` / `_extracted_icon.png` | 当时调界面、挑图标留下的预览图 |
| `app.ico` / `cookie_ch_en.ico` / `en_bubble.ico` / `cookie_pixel.ico` / `cookie_g1~g3.ico` / `cookie_p6.ico` / `stardew_sign.ico` 等 | 图标副本。放同目录时程序**优先**用它们（换图标不用等系统缓存刷新），删了自动回退到 exe 内嵌的那只饼干 |

保留 `log.txt` 的价值：出问题时把最后几百行发出来，比「它不太好用」这种描述有用一百倍。

---

## 四、历史 exe 备份（可选保留）

目录里有一堆 exe，都是历次换版前留的备份，**不是程序运行需要的**：

| 命名样式 | 举例 |
| --- | --- |
| `翻译器.exe.bak_*` | `...exe.bak_unifyui_20261025` |
| `bak_v<版本>_<时间>.exe` | `bak_v251_20261028_133148.exe` |
| `_backup_*.exe` | `_backup_v22.exe` |
| `翻译器.bak_*` | `...bak_pronoun_20261026_1540.exe` |
| `速查助手_旧版*.exe` | 改名之前的老程序 |
| `rollback_20261025_172600\`（文件夹） | 早期回退包 |

- **作用**：出问题时可以快速换回上一版（改名成 `翻译器.exe` 即可）。
- **体积**：几十个 exe 加起来几十 MB，想清就清；留着最省事的是「只留最近 3 个」。
- **不进仓库**：仓库的 `.gitignore` 忽略了 `*.exe`，`release\` 里只保留两份带版本号的正式存档（线上版 / 源码重建版），其余历史 exe 只在本机。

---

## 五、数据还原

换电脑或误删后，把备份目录里的文件拷回来就行：

```powershell
# 1. 先停程序
Get-Process "翻译器" -ErrorAction SilentlyContinue | Stop-Process -Force

# 2. 把备份目录里的 5 个文件拷回数据目录（把 <时间戳> 换成实际目录名）
$bak  = Join-Path $env:USERPROFILE "Documents\SolivineBackup\20261028_145500"
$data = Join-Path $env:APPDATA "SolivineQuickLook"
Copy-Item "$bak\*" $data -Force

# 3. 重新启动
Start-Process -WindowStyle Hidden (Join-Path $data "翻译器.exe")
```

然后把 `apikey.txt` 从密码管理器里复制回来（备份里**没有**它），见第二节。

---

## 六、三条守则

1. **`apikey.txt` 永不进仓库、永不进备份、永远在密码管理器里留一条**。
2. **`settings.ini` 用支持 UTF-8 的编辑器改**（VS Code、Notepad++ 都行）。记事本改成 ANSI 会变 GBK，虽然程序有编码嗅探能读，但手改容易把键名写错，导致那一项设置读不到。
3. **换 exe 前先跑一次备份脚本**。数据文件在换版流程里本不该被动，但 exe 正在运行时覆盖，有极小概率把正在写入的 `settings.ini`、`生词本数据.tsv` 写坏——有备份就不慌。
