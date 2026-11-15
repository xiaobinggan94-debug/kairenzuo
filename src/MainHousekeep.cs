// ============================================================================
//  饼饼专属中英互译 · MainHousekeep.cs（第一档 ⑤：数据目录自动打扫）
//  每次启动悄悄扫一遍 %APPDATA%\SolivineQuickLook\：
//      ① 旧备份 exe —— 只留最近 5 个，其余删掉
//      ② settings.ini 的备份 —— 只留最近 3 个
//      ③ log.txt 超过 1MB —— 打包挪进 log_archive\，归档只留最近 5 份
//      ④ 程序自己的临时图/临时文本 —— 直接删
//  铁律：这四样是用户的东西，一个字节都不碰
//      apikey.txt（密钥）、settings.ini（设置）、生词本(.txt/.tsv)、
//      历史记录.txt
//  打扫全程 try/catch，出任何错都只是少清一个文件，绝不影响启动。
// ----------------------------------------------------------------------------
//  自检：本程序 --housekeep <输出文件> [--dry]
//      --dry 只列清单不删，用来先看要删什么
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

partial class QueryForm
{
    // 旧备份 exe 留几个 / settings 备份留几个 / 日志多大算大
    internal const int HK_KEEP_BAK_EXE = 5;
    internal const int HK_KEEP_BAK_INI = 3;
    internal const int HK_KEEP_LOG_ARCHIVE = 5;
    internal const long HK_LOG_LIMIT = 1024L * 1024L;

    // 程序自己的临时产物（白名单，写死名字，不按通配符乱扫）
    static readonly string[] HK_TEMP_FILES = new string[] {
        "ocr_shot.png", "paste_clip.png", "_extracted_icon.png", "ocr_out.txt", "ocr_ml_out.txt"
    };

    // 用户数据：只读不删，打扫完逐条报一下还在不在
    static readonly string[] HK_USER_FILES = new string[] {
        "apikey.txt", "settings.ini", "生词本.txt", "生词本数据.tsv", "历史记录.txt"
    };

    // 是不是"备份 exe"：名字里带 bak / _backup_ / 备份 / 旧版，且本身是 exe（含 xxx.exe.bak_xxx 这种）
    static bool LooksLikeBakExe(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string n = name.ToLowerInvariant();
        bool isExe = n.EndsWith(".exe") || n.IndexOf(".exe.") >= 0;
        if (!isExe) return false;
        return n.IndexOf("bak") >= 0 || n.IndexOf("_backup_") >= 0 || n.IndexOf("备份") >= 0 || n.IndexOf("旧版") >= 0;
    }

    static bool LooksLikeBakIni(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return name.ToLowerInvariant().StartsWith("settings.ini.bak");
    }

    static void SortNewestFirst(List<FileInfo> list)
    {
        list.Sort(delegate (FileInfo a, FileInfo b) { return b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc); });
    }

    // dryRun=true：只报告要删什么，不动手
    internal static string Housekeep(bool dryRun)
    {
        StringBuilder sb = new StringBuilder();
        int deleted = 0;
        long freed = 0;
        try
        {
            sb.AppendLine("== 数据目录打扫" + (dryRun ? "（预演，什么都没删）" : "") +
                          "  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("目录：" + LOG_DIR);
            if (!Directory.Exists(LOG_DIR)) { sb.AppendLine("目录还不存在，跳过"); return sb.ToString(); }

            // ---------- ① 旧备份 exe ----------
            List<FileInfo> baks = new List<FileInfo>();
            try
            {
                foreach (string f in Directory.GetFiles(LOG_DIR))
                {
                    FileInfo fi = new FileInfo(f);
                    if (LooksLikeBakExe(fi.Name)) baks.Add(fi);
                }
            }
            catch { }
            SortNewestFirst(baks);
            sb.AppendLine();
            sb.AppendLine("① 旧备份 exe：" + baks.Count + " 个，留最近 " + HK_KEEP_BAK_EXE + " 个");
            for (int i = 0; i < baks.Count; i++)
            {
                if (i < HK_KEEP_BAK_EXE) { sb.AppendLine("   留   " + baks[i].Name); continue; }
                long len = baks[i].Length;
                bool ok = false;
                if (!dryRun) { try { File.Delete(baks[i].FullName); ok = true; } catch { ok = false; } }
                sb.AppendLine("   " + (dryRun ? "将删 " : (ok ? "删掉 " : "删不掉")) + baks[i].Name +
                              "  (" + (len / 1024) + " KB)");
                if (dryRun || ok) { deleted++; freed += len; }
            }

            // ---------- ② settings.ini 的备份 ----------
            List<FileInfo> inis = new List<FileInfo>();
            try
            {
                foreach (string f in Directory.GetFiles(LOG_DIR))
                {
                    FileInfo fi = new FileInfo(f);
                    if (LooksLikeBakIni(fi.Name)) inis.Add(fi);
                }
            }
            catch { }
            SortNewestFirst(inis);
            sb.AppendLine();
            sb.AppendLine("② 设置备份：" + inis.Count + " 个，留最近 " + HK_KEEP_BAK_INI + " 个（settings.ini 本体不动）");
            for (int i = 0; i < inis.Count; i++)
            {
                if (i < HK_KEEP_BAK_INI) { sb.AppendLine("   留   " + inis[i].Name); continue; }
                long len = inis[i].Length;
                bool ok = false;
                if (!dryRun) { try { File.Delete(inis[i].FullName); ok = true; } catch { ok = false; } }
                sb.AppendLine("   " + (dryRun ? "将删 " : (ok ? "删掉 " : "删不掉")) + inis[i].Name + "  (" + len + " 字节)");
                if (dryRun || ok) { deleted++; freed += len; }
            }

            // ---------- ③ 日志太大就归档 ----------
            string logPath = Path.Combine(LOG_DIR, "log.txt");
            string arcDir = Path.Combine(LOG_DIR, "log_archive");
            sb.AppendLine();
            long logLen = 0;
            try { if (File.Exists(logPath)) logLen = new FileInfo(logPath).Length; } catch { }
            sb.AppendLine("③ log.txt：" + (logLen / 1024) + " KB（超过 " + (HK_LOG_LIMIT / 1024) + " KB 就归档）");
            if (logLen > HK_LOG_LIMIT)
            {
                if (dryRun)
                {
                    sb.AppendLine("   将归档：log.txt → log_archive\\log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
                }
                else
                {
                    try
                    {
                        Directory.CreateDirectory(arcDir);
                        string dst = Path.Combine(arcDir, "log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
                        File.Move(logPath, dst);
                        sb.AppendLine("   已归档：" + Path.GetFileName(dst) + "（" + (logLen / 1024) + " KB）");
                    }
                    catch (Exception ex) { sb.AppendLine("   归档没成功（不影响使用）：" + ex.Message); }
                }
            }
            else sb.AppendLine("   还小，不用归档");

            // 归档目录自己也要收口：只留最近 5 份
            if (Directory.Exists(arcDir))
            {
                List<FileInfo> arcs = new List<FileInfo>();
                try { foreach (string f in Directory.GetFiles(arcDir)) arcs.Add(new FileInfo(f)); } catch { }
                SortNewestFirst(arcs);
                int killed = 0;
                for (int i = HK_KEEP_LOG_ARCHIVE; i < arcs.Count; i++)
                {
                    try
                    {
                        if (!dryRun)
                        {
                            long len = arcs[i].Length;
                            File.Delete(arcs[i].FullName);
                            freed += len; deleted++;
                        }
                        killed++;
                    }
                    catch { }
                }
                sb.AppendLine("   log_archive\\ 共 " + arcs.Count + " 份，留最近 " + HK_KEEP_LOG_ARCHIVE + " 份" +
                              (killed > 0 ? ("，" + (dryRun ? "将删 " : "删掉 ") + killed + " 份旧的") : ""));
            }

            // ---------- ④ 程序自己的临时图 ----------
            sb.AppendLine();
            sb.AppendLine("④ 临时图/临时文本：");
            bool anyTemp = false;
            foreach (string name in HK_TEMP_FILES)
            {
                string p = Path.Combine(LOG_DIR, name);
                try
                {
                    if (!File.Exists(p)) continue;
                    anyTemp = true;
                    long len = new FileInfo(p).Length;
                    bool ok = false;
                    if (!dryRun) { try { File.Delete(p); ok = true; } catch { ok = false; } }
                    sb.AppendLine("   " + (dryRun ? "将删 " : (ok ? "删掉 " : "删不掉")) + name + "  (" + len + " 字节)");
                    if (dryRun || ok) { deleted++; freed += len; }
                }
                catch { }
            }
            if (!anyTemp) sb.AppendLine("   没有，干净");

            // ---------- ⑤ 用户数据点一遍名 ----------
            sb.AppendLine();
            sb.AppendLine("⑤ 用户数据（一个都没碰）：");
            foreach (string name in HK_USER_FILES)
            {
                string p = Path.Combine(LOG_DIR, name);
                try
                {
                    if (File.Exists(p)) sb.AppendLine("   在   " + name + "  (" + new FileInfo(p).Length + " 字节)");
                    else sb.AppendLine("   无   " + name + "（本来就没有）");
                }
                catch { sb.AppendLine("   看不清 " + name); }
            }

            sb.AppendLine();
            sb.AppendLine("== " + (dryRun ? "预演结果：共 " + deleted + " 个文件待清，可省 " + (freed / 1024) + " KB"
                                            : "打扫完成：清了 " + deleted + " 个文件，省出 " + (freed / 1024) + " KB"));
        }
        catch (Exception ex)
        {
            sb.AppendLine("打扫过程出错（不影响使用）：" + ex.Message);
        }
        return sb.ToString();
    }

    // 启动时调用的那一版：悄悄干，只在日志里留一行
    internal static void HousekeepQuiet()
    {
        try
        {
            string rep = Housekeep(false);
            string last = "";
            string[] lines = rep.Replace("\r\n", "\n").Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
                if (lines[i].Trim().Length > 0) { last = lines[i].Trim(); break; }
            if (last.Length > 0) Log("数据目录打扫：" + last.TrimStart('=', ' '));
        }
        catch { }
    }
}
