// ============================================================================
//  饼饼专属中英互译 · CacheStore.cs（CacheStore）
//  本地数据：结果缓存、历史记录、生词本的读写与导出。
//  %APPDATA%\SolivineQuickLook\ 下的文件都由这个文件负责。
//  ---------------------------------------------------------------------------
//  这是 partial class QueryForm 的其中一瓣，必须和另外 5 个 .cs 一起编译：
//      csc /target:winexe /codepage:65001 *.cs
//  单独编译本文件会报「找不到另一半」，属于正常现象。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

partial class QueryForm
{
    static string WordsFile { get { return Path.Combine(LOG_DIR, "生词本.txt"); } }
    static string WordsDataFile { get { return Path.Combine(LOG_DIR, "生词本数据.tsv"); } }
    static string HistoryFile { get { return Path.Combine(LOG_DIR, "历史记录.txt"); } }
    // ====== 结果缓存：同一句话 10 分钟内不再重复请求 ======
    class CacheItem { public string Text; public DateTime At; }
    static Dictionary<string, CacheItem> cache = new Dictionary<string, CacheItem>();
    const int CACHE_MINUTES = 10;

    static bool CacheGet(string key, out string val)
    {
        val = null;
        lock (cache)
        {
            CacheItem ci;
            if (!cache.TryGetValue(key, out ci)) return false;
            if ((DateTime.Now - ci.At).TotalMinutes >= CACHE_MINUTES) { cache.Remove(key); return false; }
            val = ci.Text;
            return true;
        }
    }

    static void CachePut(string key, string val)
    {
        lock (cache)
        {
            CacheItem ci = new CacheItem();
            ci.Text = val;
            ci.At = DateTime.Now;
            cache[key] = ci;
            if (cache.Count > 200) cache.Clear();
        }
    }

    // ====== 历史记录：最近 20 条，落盘保存 ======
    class HistItem { public string Time; public string Input; public string Result; }
    static List<HistItem> history = new List<HistItem>();

    static void LoadHistory()
    {
        history.Clear();
        try
        {
            if (!File.Exists(HistoryFile)) return;
            HistItem cur = null;
            StringBuilder body = new StringBuilder();
            foreach (string raw in ReadLinesAuto(HistoryFile))
            {
                if (raw.StartsWith("### "))
                {
                    if (cur != null) { cur.Result = body.ToString().Trim(); history.Add(cur); }
                    cur = new HistItem();
                    cur.Time = raw.Substring(4).Trim();
                    body.Length = 0;
                    continue;
                }
                if (cur == null) continue;
                if (raw.StartsWith("输入：")) { cur.Input = raw.Substring(3).Trim(); continue; }
                if (raw == "----") continue;
                body.AppendLine(raw);
            }
            if (cur != null) { cur.Result = body.ToString().Trim(); history.Add(cur); }
        }
        catch { }
    }

    static void SaveHistory()
    {
        try
        {
            while (history.Count > 20) history.RemoveAt(history.Count - 1);
            Directory.CreateDirectory(LOG_DIR);
            StringBuilder sb = new StringBuilder();
            foreach (HistItem h in history)
            {
                sb.AppendLine("### " + h.Time);
                sb.AppendLine("输入：" + h.Input);
                sb.AppendLine("----");
                sb.AppendLine(h.Result);
                sb.AppendLine();
            }
            File.WriteAllText(HistoryFile, sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }

    static void AddHistory(string input, string result)
    {
        HistItem h = new HistItem();
        h.Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        h.Input = input;
        h.Result = result;
        history.Insert(0, h);
        SaveHistory();
    }

    // ====== 生词本：去重 + 按日期分组 + 可导出 ======
    static void SaveWords(string content)
    {
        try
        {
            int i = content.IndexOf("【生词】");
            if (i < 0) return;
            int start = i + 4;
            int end = content.IndexOf("【", start);
            string seg = (end < 0) ? content.Substring(start) : content.Substring(start, end - start);

            Dictionary<string, string[]> known = new Dictionary<string, string[]>();
            List<string[]> all = new List<string[]>();
            if (File.Exists(WordsDataFile))
            {
                foreach (string raw in ReadLinesAuto(WordsDataFile))
                {
                    if (raw.Trim().Length == 0) continue;
                    string[] p = raw.Split('\t');
                    if (p.Length < 3) continue;
                    string key = p[1].Trim().ToLower();
                    if (key.Length == 0) continue;
                    if (!known.ContainsKey(key)) known[key] = p;
                    all.Add(p);
                }
            }

            bool added = false;
            string today = DateTime.Now.ToString("yyyy-MM-dd");
            foreach (string raw in seg.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line == "没有" || line.IndexOf('|') < 0) continue;
                string[] f = line.Split('|');
                if (f.Length < 3) continue;
                string word = f[0].Trim();
                if (word.Length < 2) continue;
                string key2 = word.ToLower();
                if (known.ContainsKey(key2)) continue;
                string phon = f[1].Trim();
                string mean = f[2].Trim();
                string approx = (f.Length > 3) ? f[3].Trim() : "";
                string[] rec = new string[] { today, word, phon, mean, approx };
                known[key2] = rec;
                all.Add(rec);
                added = true;
            }
            if (!added) { RebuildWordsTxt(all); return; }

            Directory.CreateDirectory(LOG_DIR);
            StringBuilder tsv = new StringBuilder();
            foreach (string[] r in all)
            {
                string[] f2 = new string[5];
                for (int k = 0; k < 5; k++) f2[k] = (k < r.Length) ? r[k].Replace("\t", " ") : "";
                tsv.AppendLine(string.Join("\t", f2));
            }
            File.WriteAllText(WordsDataFile, tsv.ToString(), Encoding.UTF8);
            RebuildWordsTxt(all);
        }
        catch { }
    }

    // 把数据文件整理成人看得懂的 txt：按日期分组，同一天同一个词只留一次
    static void RebuildWordsTxt(List<string[]> all)
    {
        try
        {
            if (all == null)
            {
                all = new List<string[]>();
                if (File.Exists(WordsDataFile))
                    foreach (string raw in ReadLinesAuto(WordsDataFile))
                    {
                        if (raw.Trim().Length == 0) continue;
                        string[] p = raw.Split('\t');
                        if (p.Length >= 3) all.Add(p);
                    }
            }
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("饼饼的生词本　（自动去重，按日期分组，同一个词只出现一次）");
            sb.AppendLine("整理时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            sb.AppendLine();
            List<string> dates = new List<string>();
            foreach (string[] r in all)
            {
                string d = r[0];
                if (!dates.Contains(d)) dates.Add(d);
            }
            dates.Sort();
            for (int k = dates.Count - 1; k >= 0; k--)
            {
                string d = dates[k];
                int cnt = 0;
                sb.AppendLine("===== " + d + " =====");
                foreach (string[] r in all)
                {
                    if (r[0] != d) continue;
                    cnt++;
                    string line = "  " + r[1];
                    if (r.Length > 2 && r[2].Length > 0) line += "  " + r[2];
                    if (r.Length > 3 && r[3].Length > 0) line += "  " + r[3];
                    if (r.Length > 4 && r[4].Length > 0) line += "  " + r[4];
                    sb.AppendLine(line);
                }
                sb.AppendLine("  （这一天 " + cnt + " 个）");
                sb.AppendLine();
            }
            if (all.Count == 0) sb.AppendLine("（还没有生词，查过带【生词】的内容后会自动记进来）");
            Directory.CreateDirectory(LOG_DIR);
            File.WriteAllText(WordsFile, sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }

    static void ExportWordsCsv(string path)
    {
        try
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("日期,单词,音标,意思,近音");
            if (File.Exists(WordsDataFile))
                foreach (string raw in ReadLinesAuto(WordsDataFile))
                {
                    if (raw.Trim().Length == 0) continue;
                    string[] p = raw.Split('\t');
                    List<string> cells = new List<string>();
                    for (int k = 0; k < 5; k++)
                    {
                        string v = (k < p.Length) ? p[k] : "";
                        v = v.Replace("\"", "\"\"");
                        cells.Add("\"" + v + "\"");
                    }
                    sb.AppendLine(string.Join(",", cells.ToArray()));
                }
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return;
        }
        catch { }
    }

    // ====== 生词本数据读取（TSV：日期 单词 音标 意思 近音）======
    // 老版本的“生词本.txt”只有空格分隔，第一次运行新版时自动搬进 TSV，免得以前的词丢了
    static void MigrateOldWords()
    {
        try
        {
            if (File.Exists(WordsDataFile)) return;
            if (!File.Exists(WordsFile)) return;
            List<string[]> all = new List<string[]>();
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            foreach (string raw in ReadLinesAuto(WordsFile))
            {
                string line = raw.Trim();
                if (line.Length < 8 || line.IndexOf('|') < 0) continue;
                if (line.StartsWith("速查助手生词本")) continue;
                string[] f = line.Split('|');
                string left = f[0].Trim();
                string date = "", word = left;
                if (left.Length > 12 && left[5] == ' ' && left[8] == ':')
                {
                    date = left.Substring(0, 11).Trim();
                    word = left.Substring(11).Trim();
                }
                else { date = DateTime.Now.ToString("yyyy-MM-dd"); }
                word = word.Trim();
                if (word.Length < 2) continue;
                string key = word.ToLower();
                if (seen.ContainsKey(key)) continue;
                seen[key] = true;
                string phon = f.Length > 1 ? f[1].Trim() : "";
                string approx = f.Length > 2 ? f[2].Trim() : "";
                string mean = f.Length > 3 ? f[3].Trim() : "";
                all.Add(new string[] { date, word, phon, mean, approx });
            }
            if (all.Count == 0) return;
            StringBuilder tsv = new StringBuilder();
            foreach (string[] r in all) tsv.AppendLine(string.Join("\t", r));
            Directory.CreateDirectory(LOG_DIR);
            File.WriteAllText(WordsDataFile, tsv.ToString(), Encoding.UTF8);
            RebuildWordsTxt(all);
            Log("老生词本已迁移 " + all.Count + " 条");
        }
        catch { }
    }

    static string WordsTxtPath() { return WordsFile; }

    static List<string[]> ReadWordsTsV()
    {
        List<string[]> all = new List<string[]>();
        try
        {
            if (!File.Exists(WordsDataFile)) return all;
            foreach (string raw in ReadLinesAuto(WordsDataFile))
            {
                if (raw.Trim().Length == 0) continue;
                string[] p = raw.Split('\t');
                if (p.Length >= 2) all.Add(p);
            }
        }
        catch { }
        return all;
    }
}
