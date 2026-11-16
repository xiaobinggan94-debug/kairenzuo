// ============================================================================
//  饼饼专属中英互译 · DiagPack.cs（A3：一键诊断包）
//
//  出问题的时候，不用再一句一句问「你系统什么版本」「日志里写了啥」——
//  菜单点一下，桌面上出一个 zip，里面就三样东西：
//      ① 诊断报告.txt   —— 版本 / 构建号 / 系统 / 屏幕 / 内存 / 磁盘 / 设置快照 /
//                          数据目录清单 / 日志尾部 / 崩溃记录尾部
//      ② 日志尾部.txt   —— log.txt 最后 150 行
//      ③ 崩溃记录尾部.txt —— crash.txt 最后 40 行（没有就整份包里没这个文件）
//
//  铁律（这是会发出去的文件，宁可少写也不能带出去）：
//      · apikey.txt 的内容绝不进包，报告里只写「在 / 不在 + 字节数」
//      · 日志和崩溃记录过一遍打码（sk-xxx、32 位以上裸十六进制）再进包
//      · 设置快照只取白名单键，不 dump 整个 settings.ini
//      · 生词本 / 历史记录 的内容一个字都不碰，只报文件名与大小
//
//  自检：本程序 --diagcheck <输出文件>
//      不弹窗、不联网、只把包生成到指定目录，逐条断言报告内容与打码效果。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

partial class QueryForm
{
    // 尾部留多少行：日志够看现场，又不至于把包撑成几十兆
    internal const int DG_LOG_TAIL_LINES = 150;
    internal const int DG_CRASH_TAIL_LINES = 40;

    // 设置快照只取这些键（白名单，不许在这里加任何跟密钥有关的键）
    static readonly string[] DG_SETTING_KEYS = new string[] {
        "软件名", "hotkey", "topmost", "auto_popup", "paste_query", "tone",
        "font_size", "voice", "rate", "voice_lang", "read_mode", "grammar",
        "show_approx", "export_with_src", "ml_src", "ml_dst"
    };

    // ---------- 物理内存：只为一句话去引 Microsoft.VisualBasic 不值当，直接问内核 ----------
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    // ---------- 打码：这是唯一一道防线，任何进包的文本都必须先过这里 ----------
    internal static string MaskSecrets(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        string r = s;
        try
        {
            // AI Key 的常见写法：sk-xxxxxxxx
            r = Regex.Replace(r, @"sk-[A-Za-z0-9_\-]{6,}", "sk-***已打码***");
            // 裸的十六进制长串（32 位以上，哈希/密钥都用这种长相）
            r = Regex.Replace(r, @"[0-9a-fA-F]{32,}", "***已打码***");
            // 带 Bearer / api_key= / token= 的那些
            r = Regex.Replace(r, @"(?i)(bearer\s+)[A-Za-z0-9_\-\.]{8,}", "$1***已打码***");
            r = Regex.Replace(r, @"(?i)((?:api[_-]?key|token|secret|password)\s*[=:]\s*)[^\s""']{6,}", "$1***已打码***");
        }
        catch { }
        return r;
    }

    // 文件尾巴：读不到就返回空串，绝不因为日志读不动让整份报告作废
    static string TailLines(string path, int lines)
    {
        try
        {
            if (!File.Exists(path)) return "";
            string[] all = File.ReadAllLines(path, Encoding.UTF8);
            if (all.Length == 0) return "";
            int start = all.Length > lines ? all.Length - lines : 0;
            StringBuilder sb = new StringBuilder();
            for (int i = start; i < all.Length; i++) sb.AppendLine(all[i]);
            return sb.ToString();
        }
        catch { return ""; }
    }

    static string FileSha256(string path)
    {
        try
        {
            using (FileStream fs = File.OpenRead(path))
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] h = sha.ComputeHash(fs);
                StringBuilder sb = new StringBuilder(h.Length * 2);
                for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("X2"));
                return sb.ToString();
            }
        }
        catch { return "读不出来"; }
    }

    static string FmtSize(long bytes)
    {
        if (bytes < 1024) return bytes + " 字节";
        if (bytes < 1024L * 1024L) return (bytes / 1024.0).ToString("0.0") + " KB";
        if (bytes < 1024L * 1024L * 1024L) return (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB";
        return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.00") + " GB";
    }

    // ---------- 诊断报告正文 ----------
    internal static string DiagReport()
    {
        StringBuilder sb = new StringBuilder();
        try
        {
            sb.AppendLine("===== " + APP_TITLE + " · 一键诊断报告 =====");
            sb.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();

            // ---- 版本 ----
            sb.AppendLine("【版本】");
            sb.AppendLine("  程序版本：" + AppVersionLine());
            try
            {
                System.Reflection.Assembly asm = typeof(QueryForm).Assembly;
                string info = "";
                try
                {
                    object[] attrs = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
                    if (attrs != null && attrs.Length > 0)
                        info = ((System.Reflection.AssemblyInformationalVersionAttribute)attrs[0]).InformationalVersion;
                }
                catch { }
                sb.AppendLine("  程序集版本：" + asm.GetName().Version
                              + (info.Length > 0 ? ("（InformationalVersion=" + info + "）") : ""));
            }
            catch { }
            try
            {
                string exe = Application.ExecutablePath;
                FileInfo fi = new FileInfo(exe);
                sb.AppendLine("  exe 路径：" + exe);
                sb.AppendLine("  exe 大小：" + FmtSize(fi.Length) + "  修改时间：" + fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("  exe 哈希：" + FileSha256(exe) + "（SHA256）");
            }
            catch { }
            sb.AppendLine("  进程位数：" + (Environment.Is64BitProcess ? "64 位" : "32 位")
                          + "  进程 ID：" + Process.GetCurrentProcess().Id);
            sb.AppendLine("  管理员  ：" + (IsRunAsAdmin() ? "是" : "否"));
            sb.AppendLine();

            // ---- 系统 ----
            sb.AppendLine("【系统】");
            try
            {
                sb.AppendLine("  Windows ：" + Environment.OSVersion.VersionString + "（老接口，怕新系统认不准，真实版本看下面一行）");
                string prod = RegRead(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName");
                string disp = RegRead(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion");
                string build = RegRead(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuild");
                string ubr = RegRead(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR");
                sb.AppendLine("  版本名称：" + prod + (disp.Length > 0 ? ("  " + disp) : "")
                              + (build.Length > 0 ? ("  Build " + build + (ubr.Length > 0 ? ("." + ubr) : "")) : ""));
            }
            catch { }
            sb.AppendLine("  CLR     ：" + Environment.Version + "  " + RuntimeEnvironmentVersion());
            sb.AppendLine("  系统目录：" + Environment.SystemDirectory);
            sb.AppendLine("  临时目录：" + Path.GetTempPath());
            sb.AppendLine("  当前时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                          + "  时区：" + TimeZoneInfo.Local.DisplayName);
            sb.AppendLine();

            // ---- 屏幕 ----
            sb.AppendLine("【屏幕】");
            try
            {
                Screen[] scr = Screen.AllScreens;
                sb.AppendLine("  显示器数：" + scr.Length);
                for (int i = 0; i < scr.Length; i++)
                {
                    sb.AppendLine("    [" + i + "]" + (scr[i].Primary ? "主屏 " : " ")
                                  + scr[i].Bounds.Width + "×" + scr[i].Bounds.Height
                                  + "  可用区 " + scr[i].WorkingArea.Width + "×" + scr[i].WorkingArea.Height
                                  + "  位置 (" + scr[i].Bounds.X + "," + scr[i].Bounds.Y + ")"
                                  + "  设备 " + scr[i].DeviceName);
                }
                sb.AppendLine("  系统缩放：" + DpiScaleText());
                sb.AppendLine("  主屏可用：" + (Screen.PrimaryScreen != null
                              ? (Screen.PrimaryScreen.Bounds.Width + "×" + Screen.PrimaryScreen.Bounds.Height) : "未知"));
            }
            catch (Exception ex) { sb.AppendLine("  取屏幕信息失败：" + ex.Message); }
            sb.AppendLine();

            // ---- 内存与磁盘 ----
            sb.AppendLine("【内存】");
            try
            {
                MEMORYSTATUSEX ms = new MEMORYSTATUSEX();
                ms.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
                if (GlobalMemoryStatusEx(ref ms))
                {
                    sb.AppendLine("  物理内存：共 " + FmtSize((long)ms.ullTotalPhys) + "  可用 " + FmtSize((long)ms.ullAvailPhys)
                                  + "  占用 " + ms.dwMemoryLoad + "%");
                }
                else sb.AppendLine("  物理内存：读不出来");
            }
            catch { sb.AppendLine("  物理内存：读不出来"); }
            try
            {
                Process p = Process.GetCurrentProcess();
                sb.AppendLine("  本程序  ：工作集 " + FmtSize(p.WorkingSet64) + "  私有 " + FmtSize(p.PrivateMemorySize64));
            }
            catch { }
            sb.AppendLine();

            sb.AppendLine("【磁盘】");
            try
            {
                string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
                DriveInfo di = new DriveInfo(Path.GetPathRoot(exeDir));
                sb.AppendLine("  程序所在盘：" + di.Name + "  剩余 " + FmtSize(di.AvailableFreeSpace) + " / 共 " + FmtSize(di.TotalSize));
                DriveInfo sys = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory));
                sb.AppendLine("  系统盘    ：" + sys.Name + "  剩余 " + FmtSize(sys.AvailableFreeSpace) + " / 共 " + FmtSize(sys.TotalSize));
            }
            catch { }
            sb.AppendLine();

            // ---- 设置快照 ----
            sb.AppendLine("【设置快照】（白名单键，值已过打码；密钥不在其中）");
            for (int i = 0; i < DG_SETTING_KEYS.Length; i++)
            {
                string k = DG_SETTING_KEYS[i];
                string v = "";
                try { v = GetSet(k, ""); } catch { }
                if (v.Length == 0) v = "（没设，用默认）";
                sb.AppendLine("  " + k + " = " + MaskSecrets(v));
            }
            sb.AppendLine();

            // ---- 数据目录清单 ----
            sb.AppendLine("【数据目录】" + LOG_DIR);
            try
            {
                if (!Directory.Exists(LOG_DIR)) sb.AppendLine("  目录还不存在（程序可能还没正常启动过一次）");
                else
                {
                    string[] files = Directory.GetFiles(LOG_DIR);
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                    sb.AppendLine("  顶层文件 " + files.Length + " 个：");
                    for (int i = 0; i < files.Length; i++)
                    {
                        string name = Path.GetFileName(files[i]);
                        long len = 0;
                        try { len = new FileInfo(files[i]).Length; } catch { }
                        // 只看名字与大小，内容一律不读
                        sb.AppendLine("    " + name + "  (" + FmtSize(len) + ")"
                                      + (name.Equals("apikey.txt", StringComparison.OrdinalIgnoreCase) ? "  ← 内容不采集" : ""));
                    }
                    string[] dirs = Directory.GetDirectories(LOG_DIR);
                    if (dirs.Length > 0)
                    {
                        sb.AppendLine("  子目录：");
                        for (int i = 0; i < dirs.Length; i++)
                        {
                            int n = 0;
                            try { n = Directory.GetFiles(dirs[i]).Length; } catch { }
                            sb.AppendLine("    " + Path.GetFileName(dirs[i]) + "\\  (" + n + " 个文件)");
                        }
                    }
                }
            }
            catch (Exception ex) { sb.AppendLine("  列目录失败：" + ex.Message); }
            try
            {
                string keyPath = Path.Combine(LOG_DIR, "apikey.txt");
                sb.AppendLine("  API Key ：" + (File.Exists(keyPath) ? ("已配置（" + FmtSize(new FileInfo(keyPath).Length) + "），内容不入包") : "还没配"));
            }
            catch { }
            sb.AppendLine();

            // ---- 日志尾部 ----
            sb.AppendLine("【日志尾部】log.txt 最后 " + DG_LOG_TAIL_LINES + " 行（已打码）");
            string logTail = TailLines(Path.Combine(LOG_DIR, "log.txt"), DG_LOG_TAIL_LINES);
            sb.AppendLine(MaskSecrets(logTail).TrimEnd());
            sb.AppendLine();

            // ---- 崩溃记录 ----
            sb.AppendLine("【崩溃记录】crash.txt");
            string crashPath = Path.Combine(LOG_DIR, "crash.txt");
            if (!File.Exists(crashPath)) sb.AppendLine("  没有这个文件——说明程序一次都没崩过，这是好事");
            else
            {
                sb.AppendLine("  文件大小：" + FmtSize(new FileInfo(crashPath).Length) + "，下面是最新 " + DG_CRASH_TAIL_LINES + " 行");
                sb.AppendLine(MaskSecrets(TailLines(crashPath, DG_CRASH_TAIL_LINES)).TrimEnd());
            }
            sb.AppendLine();
            sb.AppendLine("===== 报告结束 =====");
        }
        catch (Exception ex)
        {
            sb.AppendLine();
            sb.AppendLine("报告生成中途出错（已尽力写完上面那些）：" + ex.Message);
        }
        return sb.ToString();
    }

    static bool IsRunAsAdmin()
    {
        try
        {
            System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent();
            System.Security.Principal.WindowsPrincipal p = new System.Security.Principal.WindowsPrincipal(id);
            return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    static string RegRead(string subKey, string valueName)
    {
        try
        {
            object v = Registry.GetValue("HKEY_LOCAL_MACHINE\\" + subKey, valueName, null);
            return v == null ? "" : v.ToString();
        }
        catch { return ""; }
    }

    static string RuntimeEnvironmentVersion()
    {
        try
        {
            object v = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Version", null);
            return v == null ? "" : (".NET Framework " + v);
        }
        catch { return ""; }
    }

    static string DpiScaleText()
    {
        try
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                float d = g.DpiX;
                return (d / 96.0f * 100.0f).ToString("0") + "%（DpiX=" + d.ToString("0") + "）";
            }
        }
        catch { return "读不出来"; }
    }

    // ---------- 打包 ----------
    // 目录不存在就建；zip 失败（老系统缺压缩库）自动降级成一个 txt，绝不让用户空手而归
    internal static string MakeDiagPack(string outDir)
    {
        if (string.IsNullOrEmpty(outDir)) outDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string report = DiagReport();

        Dictionary<string, string> files = new Dictionary<string, string>();
        files["诊断报告.txt"] = report;
        string logTail = TailLines(Path.Combine(LOG_DIR, "log.txt"), DG_LOG_TAIL_LINES);
        if (logTail.Length > 0) files["日志尾部.txt"] = MaskSecrets(logTail);
        string crashPath = Path.Combine(LOG_DIR, "crash.txt");
        if (File.Exists(crashPath))
        {
            string ct = TailLines(crashPath, DG_CRASH_TAIL_LINES);
            if (ct.Length > 0) files["崩溃记录尾部.txt"] = MaskSecrets(ct);
        }

        string zipPath = Path.Combine(outDir, "诊断包_" + stamp + ".zip");
        try
        {
            WriteZip(zipPath, files);
            return zipPath;
        }
        catch
        {
            // 降级：一个 txt 也能说明问题
            string txtPath = Path.Combine(outDir, "诊断报告_" + stamp + ".txt");
            File.WriteAllText(txtPath, report, new UTF8Encoding(true));
            return txtPath;
        }
    }

    static void WriteZip(string zipPath, Dictionary<string, string> files)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using (FileStream fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
        using (ZipArchive za = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (KeyValuePair<string, string> kv in files)
            {
                ZipArchiveEntry e = za.CreateEntry(kv.Key, CompressionLevel.Optimal);
                using (Stream s = e.Open())
                using (StreamWriter w = new StreamWriter(s, new UTF8Encoding(true)))
                {
                    w.Write(kv.Value);
                }
            }
        }
    }

    // 菜单入口：「更多」→ 生成诊断包
    internal void ShowDiagPack()
    {
        try
        {
            string p = MakeDiagPack(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            tip.Text = "诊断包放到桌面了：" + Path.GetFileName(p);
            Log("生成诊断包：" + p);
            try { Process.Start("explorer.exe", "/select,\"" + p + "\""); } catch { }
        }
        catch (Exception ex)
        {
            tip.Text = "诊断包没生成成功：" + ex.Message;
            Log("生成诊断包失败：" + ex.Message);
        }
    }

    // ---------- 自检：--diagcheck <输出文件> ----------
    internal static void DiagCheck(string outPath)
    {
        StringBuilder sb = new StringBuilder();
        int pass = 0, fail = 0;
        Action<string, bool, string> A = delegate(string name, bool ok, string hint)
        {
            if (ok) { pass++; sb.AppendLine("[通过] " + name + (hint.Length > 0 ? ("　" + hint) : "")); }
            else { fail++; sb.AppendLine("[失败] " + name + (hint.Length > 0 ? ("　" + hint) : "")); }
        };
        sb.AppendLine("== A3 一键诊断包自检 " + AppVersionLine() + " ==");

        string tmpDir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        string packPath = "";
        try
        {
            if (!Directory.Exists(tmpDir)) Directory.CreateDirectory(tmpDir);

            // ① 报告正文
            string report = DiagReport();
            A("报告有标题", report.IndexOf("一键诊断报告") >= 0, "");
            A("报告带版本行", report.IndexOf("程序版本：v" + APP_VER) >= 0, "找 v" + APP_VER);
            A("报告含系统段", report.IndexOf("【系统】") >= 0 && report.IndexOf("CLR") >= 0, "");
            A("报告含屏幕段", report.IndexOf("【屏幕】") >= 0 && report.IndexOf("显示器数") >= 0, "");
            A("报告含内存段", report.IndexOf("【内存】") >= 0 && report.IndexOf("物理内存") >= 0, "");
            A("报告含磁盘段", report.IndexOf("【磁盘】") >= 0 && report.IndexOf("程序所在盘") >= 0, "");
            A("报告含设置快照", report.IndexOf("【设置快照】") >= 0 && report.IndexOf("hotkey = ") >= 0, "");
            A("报告含数据目录清单", report.IndexOf("【数据目录】") >= 0, "");
            A("报告含日志段", report.IndexOf("【日志尾部】") >= 0, "");
            A("apikey 只报在不在", report.IndexOf("内容不入包") >= 0 || report.IndexOf("还没配") >= 0, "");

            // ② 打码：这是最要紧的一条
            string fake = "Authorization: Bearer sk-abcdef1234567890XYZ 以及裸串 0123456789abcdef0123456789abcdef";
            string masked = MaskSecrets(fake);
            A("sk- 已打码", masked.IndexOf("abcdef1234567890XYZ") < 0, masked.Length > 0 ? "结果：" + masked : "");
            A("裸十六进制已打码", masked.IndexOf("0123456789abcdef0123456789abcdef") < 0, "");
            A("打码不动普通文字", MaskSecrets("今天天气不错 voice=Zira").IndexOf("今天天气不错") >= 0, "");

            // ③ 真 key 绝不进包
            string keyPath = Path.Combine(LOG_DIR, "apikey.txt");
            if (File.Exists(keyPath))
            {
                // v1.0.0 第三条起盘上存的是密文，得先还原成真 key 再比。
                // 拿密文前 8 位去比是白比——那 8 位就是公开前缀 "dpapi1:"，永远命中、永远误报。
                string k = KeyVault.ReadKeyText(keyPath, false);
                bool leaked = false;
                string hint = "只比前 8 位，不打印 key 本身";
                if (k.Length >= 8)
                {
                    string head = k.Substring(0, 8);
                    leaked = report.IndexOf(head) >= 0 || MaskSecrets(k).IndexOf(head) >= 0;
                }
                else
                {
                    // 解不开（换了电脑 / 换了 Windows 账户）就退一步：密文本体也不许进报告
                    hint = "本机 key 解不开，改验密文本体";
                    string raw = "";
                    try { raw = File.ReadAllText(keyPath, Encoding.UTF8).Trim(); } catch { }
                    string body = raw.StartsWith(KeyVault.SEAL_PREFIX, StringComparison.Ordinal)
                        ? raw.Substring(KeyVault.SEAL_PREFIX.Length).Trim()
                        : raw;
                    if (body.Length >= 8) leaked = report.IndexOf(body.Substring(0, 8), StringComparison.Ordinal) >= 0;
                }
                A("真 key 不在报告里", !leaked, hint);
            }
            else A("真 key 不在报告里", true, "本机还没配 key，跳过");

            // ④ 打包
            packPath = MakeDiagPack(tmpDir);
            A("诊断包已生成", File.Exists(packPath), Path.GetFileName(packPath));
            FileInfo pfi = new FileInfo(packPath);
            A("诊断包非空", pfi.Length > 500, FmtSize(pfi.Length));
            if (packPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                int entries = 0;
                string inside = "";
                bool hasReport = false;
                using (FileStream fs = new FileStream(packPath, FileMode.Open, FileAccess.Read))
                using (ZipArchive za = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    entries = za.Entries.Count;
                    for (int i = 0; i < za.Entries.Count; i++)
                    {
                        if (za.Entries[i].FullName == "诊断报告.txt") hasReport = true;
                        if (za.Entries[i].FullName == "诊断报告.txt")
                        {
                            using (Stream s = za.Entries[i].Open())
                            using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                                inside = r.ReadToEnd();
                        }
                    }
                }
                A("zip 里至少一个文件", entries >= 1, entries + " 个条目");
                A("zip 里报告文件名正确", hasReport, "诊断报告.txt");
                A("zip 里的报告能读回", inside.IndexOf("一键诊断报告") >= 0, "");
            }
            else A("诊断包已生成", true, "压缩库不可用，已降级成 txt（可接受）");
        }
        catch (Exception ex)
        {
            A("自检过程没崩", false, ex.Message);
        }
        sb.AppendLine();
        sb.AppendLine("合计：通过 " + pass + " 项，失败 " + fail + " 项");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true));
        }
        catch { }
    }
}
