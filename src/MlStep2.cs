// MlStep2.cs —— v2.3 第 2 步：其他语言小窗补齐
//
// 这一瓣只做四件事，全部只挂在「其他语言」小窗和"拖文件进来"这两条线上：
//   ① 导出：TXT / 真 docx（默认只导译文，可选带原文），落盘到系统真实桌面
//   ② 长文：按约 1200 字分段翻，报「已完成 x/n 段」；单段失败只重投那一段
//   ③ 拖文件分流：中文/英文进主界面，其它语言文字进小窗；文件夹与 .lnk 明说不行
//   ④ 从右到左（阿拉伯语等）的译文方向自动摆正，导出 docx 时给段落指定支持字体
//
// 约束（和主界面有关的一律不碰）：底栏 5 个按钮、主界面控件树、小窗里不许出现
// 语音/朗读/谐音/语气/截图/离线这些入口——本文件一个新入口都没往主界面加。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

partial class QueryForm
{
    // ======================== 上限与分段参数 ========================
    // 一次请求塞太多字，模型会被截断、返回变慢，用户还不知道翻完没翻完。
    // 所以：按 1200 字切段；超过 5000 字先给一次预估；超过 20 万字或 2MB 只翻前面一段。
    internal const int MlSegChars = 1200;
    internal const int MlWarnChars = 5000;
    internal const int MlMaxChars = 200000;
    internal const int MlMaxBytes = 2 * 1024 * 1024;

    // 小窗底部新增的两个导出按钮 + 「带原文」勾选（都是在 mlPanel 里，不动主界面）
    RoundButton mlTxtBtn, mlDocxBtn;
    CheckBox mlWithSrcChk;

    // 分段状态：失败段留着，用户再点一次「翻译」只重投这几段，不整篇重来
    internal class MlSeg
    {
        public string Src;
        public string Dst;
        public bool Ok;
        public string Err;
        public MlSeg(string s) { Src = s; }
    }
    List<MlSeg> mlSegs;
    string mlSegHash = "";
    int mlSegTotal = 0;
    bool mlSegHasFail = false;

    // 导出用的最近一次原文/译文（用户翻完又改了输入框，导出的还得是实际翻的那份）
    string mlLastSrc = "";
    string mlLastDst = "";
    internal string MlLastExportPath = "";

    // 超长原文的"已经提醒过一次"记号：同一份文本只提醒一次，用户再点就开翻
    string mlBigOkKey = "";

    // 首次打开提示：只提示一次，记进配置
    internal const string MlTipKey = "ml_text_only_tip";
    internal int MlFirstTipCount = 0;

    // 拖文件分流：自检里不想真去联网翻译，用这个开关把请求掐掉
    bool mlSuppressQuery = false;
    bool mlDropOn = false;

    internal enum MlGate { Proceed, Confirm }

    // ======================== ① 超长原文：先提醒，再截断 ========================

    // 返回值：Confirm = 这次只说话不翻（用户再点一次才开始）；Proceed = 接着翻
    internal MlGate MlBigGate(string text, out string work, out string note)
    {
        work = text;
        note = null;
        if (text == null) return MlGate.Proceed;

        int chars = text.Length;
        int bytes = Encoding.UTF8.GetByteCount(text);

        // 硬上限：20 万字 / 2MB。超了不报错、不干等，直接说明白只翻前 N 字。
        if (chars > MlMaxChars || bytes > MlMaxBytes)
        {
            int keep = MlTruncateLen(text);
            if (keep < 1) keep = 1;
            work = text.Substring(0, keep);
            note = "原文 " + MlSizeLabel(chars, bytes) + "，超过单次上限（20 万字 / 2 MB），这次只翻前 " + keep + " 字";
            Log("小窗超上限截断：原文 " + MlSizeLabel(chars, bytes) + " → 前 " + keep + " 字");
            return MlGate.Proceed;
        }

        // 5000 字以上：翻之前先给一次预估，别让用户对着空窗口干等
        if (chars > MlWarnChars)
        {
            string key = MlHash(text);
            if (mlBigOkKey != key)
            {
                mlBigOkKey = key;                        // 记下来：下一击就是"确认，开翻"
                int segs = MlSplit(text, MlSegChars).Count;
                note = "共 " + chars + " 字，按 " + MlSegChars + " 字一段会分 " + segs + " 段，"
                     + MlEtaLabel(segs) + "；确认的话再点一次「翻译」就开始";
                return MlGate.Confirm;
            }
        }
        return MlGate.Proceed;
    }

    // 截到多少字：先按 20 万字卡，再按 2MB 卡，尽量切在段落/句子边界上，不要切半个词
    internal static int MlTruncateLen(string text)
    {
        int n = text.Length;
        if (n > MlMaxChars) n = MlMaxChars;
        while (n > 1 && Encoding.UTF8.GetByteCount(text.Substring(0, n)) > MlMaxBytes) n = n - Math.Max(1, n / 50);
        if (n >= text.Length) return text.Length;
        int floor = Math.Max(1, n - 200);                 // 往回找一个舒服的断点
        for (int i = n - 1; i >= floor; i--)
        {
            char c = text[i];
            if (c == '\n' || c == '。' || c == '！' || c == '？' || c == '.' || c == '!' || c == '?' || c == ';' || c == '；')
                return i + 1;
        }
        return n;
    }

    static string MlSizeLabel(int chars, int bytes)
    {
        string b;
        if (bytes >= 1024 * 1024) b = (bytes / 1048576.0).ToString("0.0") + " MB";
        else if (bytes >= 1024) b = (bytes / 1024.0).ToString("0") + " KB";
        else b = bytes + " 字节";
        return chars + " 字 / " + b;
    }

    static string MlEtaLabel(int segs)
    {
        int sec = segs * 4;                       // 一句 1200 字大概 4 秒上下，粗估就行，别装精确
        if (sec < 60) return "估计 " + Math.Max(3, sec) + " 秒左右";
        return "估计 " + (sec / 60) + " 分钟上下";
    }

    // 短字符串指纹：只用来判断"是不是同一份/同一组参数"，不做安全用途
    internal static string MlHash(string s)
    {
        if (s == null) s = "";
        uint h = 2166136261;
        for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619; }
        return h.ToString("x8");
    }

    // ======================== ② 分段 ========================
    // 切段原则：先按段落切，段落内部按句末标点切，最后贪心拼到 perSeg 以内。
    // 一个字都不丢（除了段间的空白/换行在拼段时会归一），顺序原样保留。
    internal static List<string> MlSplit(string text, int perSeg)
    {
        List<string> outp = new List<string>();
        if (text == null) return outp;
        if (perSeg < 200) perSeg = 200;
        string t = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (t.Trim().Length == 0) return outp;

        string[] paras = t.Split('\n');
        List<string> pieces = new List<string>();
        foreach (string para in paras)
        {
            string p = para;
            if (p.Length == 0) { pieces.Add("\n"); continue; }
            if (p.Length <= perSeg) { pieces.Add(p); pieces.Add("\n"); continue; }
            int i = 0;
            while (i < p.Length)
            {
                int cut = MlSentenceCut(p, i, Math.Min(p.Length, i + perSeg));
                if (cut <= i) cut = Math.Min(p.Length, i + perSeg);
                pieces.Add(p.Substring(i, cut - i));
                i = cut;
            }
            pieces.Add("\n");
        }

        StringBuilder cur = new StringBuilder();
        foreach (string piece in pieces)
        {
            if (piece == "\n")
            {
                if (cur.Length > 0 && cur[cur.Length - 1] != '\n') cur.Append('\n');
                continue;
            }
            if (cur.Length > 0 && cur.Length + piece.Length > perSeg)
            {
                string full = cur.ToString().Trim('\n');
                if (full.Length > 0) outp.Add(full);
                cur.Length = 0;
            }
            string rest = piece;
            while (rest.Length > perSeg)                 // 一整句就超长（没有标点）：硬切
            {
                if (cur.Length > 0) { string full2 = cur.ToString().Trim('\n'); if (full2.Length > 0) outp.Add(full2); cur.Length = 0; }
                outp.Add(rest.Substring(0, perSeg));
                rest = rest.Substring(perSeg);
            }
            cur.Append(rest);
        }
        if (cur.Length > 0)
        {
            string last = cur.ToString().Trim('\n');
            if (last.Length > 0) outp.Add(last);
        }
        return outp;
    }

    static int MlSentenceCut(string p, int from, int hardEnd)
    {
        if (hardEnd >= p.Length) return p.Length;
        int minEnd = Math.Min(hardEnd, from + 120);      // 别切得太碎
        for (int i = hardEnd - 1; i >= minEnd; i--)
        {
            char c = p[i];
            if (c == '\n' || c == '。' || c == '！' || c == '？' || c == '；' || c == '…'
                || c == '.' || c == '!' || c == '?' || c == ';') return i + 1;
        }
        for (int i = hardEnd - 1; i >= minEnd; i--)
            if (char.IsWhiteSpace(p[i])) return i + 1;
        return hardEnd;
    }

    // ======================== 单次请求（流式） ========================
    // 从 MlDoTranslate 里抽出来的：一次 HTTP、边收边回调、失败抛异常。
    // 分段翻和单段翻都走这一条，行为完全一致，不会出现"整篇能翻、分段就挂"。
    string MlHttpOnce(string text, string sc, string dc, int seq, Action<string> onSnap, int maxTokens)
    {
        return MlHttpOnceCore(text, sc, dc, delegate () { return MlIsCurrent(seq); }, onSnap, maxTokens);
    }

    // 主界面整篇分段翻用的是另一套"当前请求号"（NextSeq/IsCurrent），
    // 所以这里把手上的作废判断抽成 alive 回调，两条路共用同一份 HTTP / 流式逻辑。
    internal string MlHttpOnceAlive(string text, string sc, string dc, Func<bool> alive, Action<string> onSnap, int maxTokens)
    {
        return MlHttpOnceCore(text, sc, dc, alive, onSnap, maxTokens);
    }

    string MlHttpOnceCore(string text, string sc, string dc, Func<bool> alive, Action<string> onSnap, int maxTokens)
    {
        string key = ApiKey;
        if (key.Length == 0) throw new Exception("还没配置 API Key");

        string payload = MlBuildPayload(text, sc, dc, maxTokens);
        byte[] data = Encoding.UTF8.GetBytes(payload);
        HttpWebRequest req = CreateJsonPost(API_BASE + "/chat/completions", key, data);
        try
        {
            using (Stream s = req.GetRequestStream()) { s.Write(data, 0, data.Length); }

            string full = "";
            int lastPaint = 0;
            JavaScriptSerializer js = new JavaScriptSerializer();
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length < 6 || !line.StartsWith("data:")) continue;
                    string p = line.Substring(5).Trim();
                    if (p.Length == 0 || p == "[DONE]") continue;
                    string delta = DeltaText(js, p);
                    if (delta == null) continue;
                    full = full + delta;
                    if (alive != null && !alive()) return full;
                    // v2.7.2：跟单词查询那条路对齐，节流 150ms → 260ms，少几次整篇重排
                    if (onSnap != null && Environment.TickCount - lastPaint > 260)
                    {
                        lastPaint = Environment.TickCount;
                        onSnap(full);
                    }
                }
            }
            string content = full.Trim();
            if (content.Length == 0) throw new Exception("接口没返回内容");
            return content;
        }
        catch (WebException wex)
        {
            string extra = "";
            try
            {
                if (wex.Response != null)
                {
                    using (StreamReader er = new StreamReader(wex.Response.GetResponseStream(), Encoding.UTF8))
                        extra = er.ReadToEnd();
                }
            }
            catch { }
            if (extra.Length > 160) extra = extra.Substring(0, 160) + "…";
            throw new Exception(wex.Message + (extra.Length > 0 ? "｜" + extra : ""));
        }
    }

    // 分段时给足输出预算：一段 1200 字，译文不会比原文短太多（有些语言会更长）
    internal static int MlMaxTokensFor(string text)
    {
        int n = (text == null ? 0 : text.Length) * 3 + 1200;
        if (n < 1200) n = 1200;
        if (n > 8000) n = 8000;
        return n;
    }

    // ======================== 分段翻译主循环 ========================
    // 分段续投的判定单独拎出来：同一份原文 + 同一语言对 + 上次确实有失败段 + 段数没变，
    // 才认定为"接着上次翻"，否则当新的一篇从头来。
    internal string SegHashOf(string text, string sc, string dc)
    {
        return MlHash(sc + "\u0001" + dc + "\u0001" + text);
    }

    internal bool SegCanResume(string h, int n)
    {
        return mlSegs != null && mlSegHash == h && mlSegHasFail && mlSegs.Count == n;
    }

    // 进度文案：所有"第几段/共几段"的说法都从这里出，别各处各写一套
    internal static string MlSegProgress(int done, int total, int failed)
    {
        if (failed > 0) return "已完成 " + done + "/" + total + " 段（" + failed + " 段没成，稍后可只重投这几段）";
        return "已完成 " + done + "/" + total + " 段";
    }

    void MlDoTranslateMulti(string text, string sc, string dc, int seq)
    {
        List<string> srcs = MlSplit(text, MlSegChars);
        int n = srcs.Count;
        if (n <= 1)
        {
            // 理论上走不到（单段走老路），真走到就当单段处理，绝不空转
            MlDoTranslate(text, sc, dc, MlCacheKey(sc, dc, text), seq);
            return;
        }

        string h = SegHashOf(text, sc, dc);
        bool resume = SegCanResume(h, n);
        List<MlSeg> segs;
        if (resume)
        {
            segs = mlSegs;
            for (int i = 0; i < segs.Count; i++)
                if (!segs[i].Ok) { segs[i].Dst = null; segs[i].Err = null; }
            Log("小窗分段重投：只重翻上次失败的段（共 " + n + " 段）");
        }
        else
        {
            segs = new List<MlSeg>();
            for (int i = 0; i < n; i++) segs.Add(new MlSeg(srcs[i]));
            mlSegHash = h;
        }
        mlSegs = segs;
        mlSegTotal = n;
        mlSegHasFail = false;

        int done = 0, failed = 0;
        for (int i = 0; i < n; i++)
        {
            if (!MlIsCurrent(seq)) return;
            if (segs[i].Ok) { done++; continue; }

            string err = null;
            bool ok = false;
            for (int attempt = 1; attempt <= 2 && !ok; attempt++)
            {
                if (!MlIsCurrent(seq)) return;
                try
                {
                    int idx = i;
                    string got = MlHttpOnce(segs[i].Src, sc, dc, seq,
                        delegate (string snap)
                        {
                            string live = MlAssembleLive(segs, idx, snap, true);
                            Ui(delegate ()
                            {
                                if (!MlIsCurrent(seq)) return;
                                mlOutBox.Box.Text = live;
                                MlSay("正在翻第 " + (idx + 1) + "/" + n + " 段……");
                            });
                        }, MlMaxTokensFor(segs[i].Src));
                    if (!MlIsCurrent(seq)) return;
                    segs[i].Dst = (got == null ? "" : got.Trim());
                    segs[i].Ok = segs[i].Dst.Length > 0;
                    ok = segs[i].Ok;
                    if (!ok) err = "接口返回空";
                }
                catch (Exception ex)
                {
                    err = ex.Message;
                    if (attempt == 1) Thread.Sleep(500);      // 单段失败先就地补一次，别整篇重来
                }
            }
            segs[i].Err = ok ? null : (err == null ? "未知原因" : err);
            if (!ok) { failed++; mlSegHasFail = true; }
            done++;

            string body = MlAssembleLive(segs, -1, null, true);
            int d = done, f = failed;
            Ui(delegate ()
            {
                if (!MlIsCurrent(seq)) return;
                mlOutBox.Box.Text = body;
                if (f > 0) MlSay(MlSegProgress(d, n, f));
                else MlSay(MlSegProgress(d, n, 0));
            });
        }

        if (!MlIsCurrent(seq)) return;

        string whole = MlAssembleLive(segs, -1, null, false);
        mlLastSrc = text;
        mlLastDst = whole;
        if (failed == 0)
        {
            CachePut(MlCacheKey(sc, dc, text), whole);
            Log("小窗分段翻译成功 " + MlPairLabel(sc, dc) + " " + text.Length + " 字 / " + n + " 段 → " + whole.Length + " 字");
            Ui(delegate ()
            {
                if (!MlIsCurrent(seq)) return;
                mlOutBox.Box.Text = whole;
                MlSay("翻好了（" + MlPairLabel(sc, dc) + "）｜共 " + n + " 段｜Ctrl+Enter 再翻一次");
                MlBusyDone();
            });
        }
        else
        {
            int f2 = failed;
            Ui(delegate ()
            {
                if (!MlIsCurrent(seq)) return;
                mlOutBox.Box.Text = whole;
                MlSay("还有 " + f2 + " 段没翻成（其余 " + (n - f2) + " 段已在上面）；再点一次「翻译」只重投这几段");
                MlBusyDone();
            });
        }
    }

    // 拼装显示：idx = 正在翻的那一段下标（-1 表示没有在翻的），snap 是它的实时内容
    static string MlAssembleLive(List<MlSeg> segs, int idx, string snap, bool mark)
    {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < segs.Count; i++)
        {
            if (sb.Length > 0) sb.Append('\n');
            MlSeg g = segs[i];
            if (i == idx)
            {
                sb.Append(snap == null ? "" : snap);
                continue;
            }
            if (g.Ok) { if (g.Dst != null) sb.Append(g.Dst); }
            else if (mark)
            {
                if (g.Err != null) sb.Append("〔第 " + (i + 1) + " 段没翻成：" + MlOneLine(g.Err, 60) + "〕");
                else sb.Append("〔第 " + (i + 1) + " 段待翻〕");
            }
        }
        return sb.ToString();
    }

    internal static string MlOneLine(string s, int max)
    {
        if (s == null) return "";
        string t = Regex.Replace(s, @"\s+", " ").Trim();
        if (t.Length > max) t = t.Substring(0, max) + "…";
        return t;
    }

    // ======================== ③ 从右到左（RTL） ========================

    static bool MlIsRtlChar(char c)
    {
        return (c >= '\u0590' && c <= '\u05FF')     // 希伯来
            || (c >= '\u0600' && c <= '\u06FF')     // 阿拉伯
            || (c >= '\u0700' && c <= '\u074F')     // 叙利亚
            || (c >= '\u0750' && c <= '\u077F')
            || (c >= '\u0780' && c <= '\u07BF')     // 塔纳
            || (c >= '\u08A0' && c <= '\u08FF')
            || (c >= '\uFB1D' && c <= '\uFDFF')
            || (c >= '\uFE70' && c <= '\uFEFF');
    }

    // 一段译文是不是"以从右到左的文字为主"：RTL 字母不少于非 RTL 字母的一半，就算
    internal static bool MlRtlDominant(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        int rtl = 0, other = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (MlIsRtlChar(c)) rtl++;
            else if (char.IsLetter(c)) other++;
        }
        if (rtl < 3) return false;
        return rtl >= other;
    }

    // 译文框的文字方向跟着内容走：阿拉伯语/希伯来语右对齐从右往左，其它语言恢复常规。
    // 原文框一律 LTR：阿语原文粘进来也按左到右摆，用户照着改字时光标不会来回跳。
    void MlAutoRtl()
    {
        if (mlOutBox == null || mlOutBox.Box == null) return;
        string t = mlOutBox.Box.Text;
        bool rtl = MlRtlDominant(t);
        RightToLeft want = rtl ? RightToLeft.Yes : RightToLeft.No;
        if (mlOutBox.Box.RightToLeft != want) mlOutBox.Box.RightToLeft = want;
        if (rtl)
        {
            // RTL 盒子里光标默认停在最右（行首），标点跟着段落方向重排；
            // 只读框不会有编辑光标，这里把插入点挪到开头顶着，滚动条也由系统挪到左边
            try { if (!mlOutBox.Box.Focused && mlOutBox.Box.TextLength > 0) mlOutBox.Box.Select(0, 0); }
            catch { }
        }
        // 译文转成 RTL 时，原文区"回退"到 LTR（是回退，不是跟随）
        if (mlInBox != null && mlInBox.Box != null && mlInBox.Box.RightToLeft != RightToLeft.No)
            mlInBox.Box.RightToLeft = RightToLeft.No;
    }

    // ======================== ④ 语言判定（拖文件分流用） ========================

    static void MlCountScript(string s, out int han, out int kana, out int hangul, out int latinA,
                              out int latinX, out int cyr, out int arab, out int hebr, out int thai,
                              out int deva, out int other)
    {
        han = kana = hangul = latinA = latinX = cyr = arab = hebr = thai = deva = other = 0;
        if (s == null) return;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c >= '\u4E00' && c <= '\u9FFF') han++;
            else if (c >= '\u3040' && c <= '\u30FF') kana++;
            else if (c >= '\uAC00' && c <= '\uD7AF') hangul++;
            else if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) latinA++;
            else if ((c >= '\u00C0' && c <= '\u024F')) latinX++;
            else if (c >= '\u0400' && c <= '\u04FF') cyr++;
            else if (MlIsRtlChar(c) && (c < '\u0590' || c > '\u05FF')) arab++;
            else if (c >= '\u0590' && c <= '\u05FF') hebr++;
            else if (c >= '\u0E00' && c <= '\u0E7F') thai++;
            else if (c >= '\u0900' && c <= '\u097F') deva++;
            else if (char.IsLetter(c)) other++;
        }
    }

    // 只认"能一眼看出来"的语言；看不出来返回 ""，由调用方回落
    internal static string MlLangOfText(string text)
    {
        int han, kana, hangul, latinA, latinX, cyr, arab, hebr, thai, deva, other;
        MlCountScript(text, out han, out kana, out hangul, out latinA, out latinX, out cyr, out arab, out hebr, out thai, out deva, out other);
        int total = han + kana + hangul + latinA + latinX + cyr + arab + hebr + thai + deva + other;
        if (total < 4) return "";

        if (kana * 50 >= total) return "ja";          // 有假名基本就是日语（汉字再多也算）
        if (hangul * 50 >= total) return "ko";
        if (han * 2 >= total) return "zh";
        if (cyr * 2 >= total) return "ru";
        if (arab * 2 >= total) return "ar";
        if (hebr * 2 >= total) return "he";
        if (thai * 2 >= total) return "th";
        if (deva * 2 >= total) return "hi";

        int latin = latinA + latinX;
        if (latin * 2 >= total)
        {
            if (latinX * 20 >= total)                 // 变音符号够多 → 不是英语
            {
                string o = MlLatinOther(text);
                return o == null ? "xx" : o;          // "xx" = 认得是拉丁字母的外语但认不出具体哪门，别硬塞成法语
            }
            string guess = MlLatinOther(text);
            if (guess != null) return guess;          // 虚词命中两处以上才认，避免"英语里出现个 le"就误判
            return "en";
        }
        return "";
    }

    // 靠高频虚词认拉丁语系：命中两种以上才算，宁可不认也不乱认
    static string MlLatinOther(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        string low = " " + s.ToLowerInvariant().Replace('\r', ' ').Replace('\n', ' ') + " ";
        string[,] tab = new string[,] {
            { "fr", " le| la| les| des| une| est| dans| pour| avec| vous| nous|" },
            { "de", " der| die| das| und| nicht| ist| ein| eine| mit| für| ich|" },
            { "es", " el| los| las| que| una| para| con| por| como| está| muy|" },
            { "vi", " của| và| không| những| được| một| cho| này|" },
            { "pt", " não| uma| com| para| você| isso| mais|" },
            { "it", " che| non| una| per| sono| questo| anche|" },
            { "tr", " bir| ve| bu| için| ile| değil|" },
            { "id", " yang| dan| untuk| dengan| tidak| ini|" },
            { "pl", " jest| nie| się| oraz| dla|" },
            { "nl", " het| een| niet| van| voor|" }
        };
        int best = 0; string code = null;
        for (int r = 0; r < tab.GetLength(0); r++)
        {
            string[] words = tab[r, 1].Split('|');
            int hit = 0;
            for (int i = 0; i < words.Length; i++)
            {
                string w = words[i].Trim();       // 词表是按 " le| la|" 排的，不 Trim 会变成"双空格"永远匹配不上
                if (w.Length == 0) continue;
                if (low.IndexOf(" " + w + " ", StringComparison.Ordinal) >= 0) hit++;
            }
            if (hit > best) { best = hit; code = tab[r, 0]; }
        }
        return best >= 2 ? code : null;
    }

    // 分流结论：中文/英文（或者根本看不出语言）进主界面；其它语言进小窗
    internal static string MlRouteKind(string text)
    {
        string lang = MlLangOfText(text);
        if (lang == null || lang.Length == 0) return "main";
        if (lang == "zh" || lang == "en") return "main";
        return "ml";
    }

    // ======================== ⑤ 拖文件进来：自动分流 ========================

    // 主窗和它下面的所有控件都接上"拖文件"：拖到输入框、拖到空白处都算数
    internal void MlInstallDrop()
    {
        if (mlDropOn) return;
        mlDropOn = true;
        try
        {
            AllowDrop = true;
            DragEnter += MlDropEnter;
            DragDrop += MlDropFiles;
            MlAttachDropChildren(this);
        }
        catch (Exception ex) { Log("挂拖放失败：" + ex.Message); }
    }

    void MlAttachDropChildren(Control root)
    {
        foreach (Control c in root.Controls)
        {
            try
            {
                c.AllowDrop = true;
                c.DragEnter += MlDropEnter;
                c.DragDrop += MlDropFiles;
            }
            catch { }
            MlAttachDropChildren(c);
        }
    }

    void MlDropEnter(object s, DragEventArgs e)
    {
        if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        else e.Effect = DragDropEffects.None;
    }

    void MlDropFiles(object s, DragEventArgs e)
    {
        string[] files = null;
        try { files = e.Data.GetData(DataFormats.FileDrop) as string[]; } catch { }
        if (files == null || files.Length == 0) return;
        MainDropHintHide();                      // 松手了，接收提示立刻撤掉（见 MainIngest.cs）
        try { tip.Text = "正在读文件…"; Application.DoEvents(); } catch { }
        string summary = MlRouteDropped(files);
        try { tip.Text = summary; } catch { }
    }

    // 核心分流：读得出来的按语言分，读不出来的明说不行（不静默失败）
    internal string MlRouteDropped(string[] paths)
    {
        if (paths == null || paths.Length == 0) return "没接到文件";
        List<string> toMain = new List<string>();
        List<string> toMl = new List<string>();
        List<string> skipped = new List<string>();
        List<string> notes = new List<string>();
        string mlLang = "";

        foreach (string raw in paths)
        {
            string path = raw == null ? "" : raw.Trim();
            if (path.Length == 0) continue;
            string nm = Path.GetFileName(path);
            if (nm.Length == 0) nm = path;

            if (Directory.Exists(path)) { skipped.Add("文件夹不支持：" + nm); continue; }
            if (!File.Exists(path)) { skipped.Add("找不到文件：" + nm); continue; }

            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".lnk" || ext == ".url" || ext == ".pif") { skipped.Add("快捷方式不支持：" + nm + "（请拖源文件本身）"); continue; }

            string why = null;
            string text = MlReadAnyFile(path, ext, ref why);
            if (text == null)
            {
                skipped.Add((why == null ? "这种文件暂不支持：" : why + "：") + nm);
                continue;
            }
            // v2.4 第 4 步：字幕只翻台词，序号/时间轴/WEBVTT 头对翻译没用（见 MainIngest.cs ③）
            if (ext == ".srt" || ext == ".vtt") text = MainSrtStrip(text);
            text = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (text.Length == 0) { skipped.Add("里面没有可翻的文字：" + nm); continue; }

            // 超上限的提前截，别让用户等半天才发现只翻了一半
            if (text.Length > MlMaxChars || Encoding.UTF8.GetByteCount(text) > MlMaxBytes)
            {
                int keep = MlTruncateLen(text);
                text = text.Substring(0, keep);
                // 说清楚"只翻了前面一段"，别让用户以为全文都翻完了
                notes.Add(nm + " 超过单次上限（20 万字 / 2 MB），这次只翻前 " + keep + " 字");
                Log("拖入文件超上限，截断 " + nm + " → 前 " + keep + " 字");
            }

            string kind = MlRouteKind(text);
            if (kind == "main") toMain.Add(text);
            else
            {
                toMl.Add(text);
                string l = MlLangOfText(text);
                if (l != null && l.Length > 0 && MlName(l) != null) mlLang = l;
            }
        }

        StringBuilder sb = new StringBuilder();
        if (toMain.Count > 0)
        {
            string all = string.Join("\n\n", toMain.ToArray());
            MlApplyToMain(all);
            sb.Append("已读入主界面：" + toMain.Count + " 个（中文/英文走主界面）");
        }
        if (toMl.Count > 0)
        {
            string all = string.Join("\n\n", toMl.ToArray());
            MlApplyToMl(all, mlLang);
            if (sb.Length > 0) sb.Append("；");
            string langName = (mlLang != null && mlLang.Length > 0 && MlName(mlLang) != null) ? MlName(mlLang) : "其他语言";
            sb.Append(langName + "内容已转到「其他语言」小窗：" + toMl.Count + " 个");
        }
        if (skipped.Count > 0)
        {
            if (sb.Length > 0) sb.Append("；");
            sb.Append(string.Join("；", skipped.ToArray()));
        }
        if (notes.Count > 0)
        {
            if (sb.Length > 0) sb.Append("；");
            sb.Append(string.Join("；", notes.ToArray()));
        }
        if (sb.Length == 0) sb.Append("这次没有可以翻的内容");
        Log("拖文件分流：" + sb.ToString());
        return sb.ToString();
    }

    // 单个文件 → 文本。
    //   · 纯文本（txt/md/srt/csv…）→ 直接读正文
    //   · docx → 抽段落文字；带密码的（OLE 加密壳）明说加密
    //   · pdf → 自己解 FlateDecode + ToUnicode 抽文字
    //   · 图片 → 只提示不支持（要认图里的字走主界面截图取字）
    // 任何一步失败都返回 null 并写好 why，由调用方原样告诉用户，绝不抛到界面上。
    string MlReadAnyFile(string path, string ext, ref string why)
    {
        if (MlIsImageExt(ext))
        {
            why = "图片暂不支持直接拖进来（要认图里的字，请用主界面截图取字）";
            return null;
        }
        if (ext == ".docx")
        {
            if (MlLooksOle(path)) { why = "这个 docx 设了密码（加密件），先去密码再拖"; return null; }
            string dt = MlDocxText(path);
            if (dt == null) { why = "docx 解不开（可能已加密或文件损坏）"; return null; }
            return dt;
        }
        if (ext == ".doc") { why = "老 .doc 暂不支持，请另存为 .docx"; return null; }
        if (ext == ".pdf") return MlPdfText(path, ref why);
        if (MlIsTextExt(ext))
        {
            try
            {
                FileInfo fi = new FileInfo(path);
                if (fi.Length > 8L * 1024 * 1024) { why = "文件太大（超过 8 MB）"; return null; }
            }
            catch { }
            try { return ReadTextAuto(path); }
            catch (Exception ex) { why = "读不出来（" + MlOneLine(ex.Message, 40) + "）"; return null; }
        }
        why = "这种文件暂不支持";
        return null;
    }

    // 认图片里的字。跟主界面截图取字用的是同一份 OCR 脚本，只是这里能指定语种
    // （阿拉伯语/俄语的图必须用对应语言包，不然认出来是一串乱码）。
    string RunOcrLang(string imgPath, string lang)
    {
        try
        {
            Directory.CreateDirectory(LOG_DIR);
            string script = Path.Combine(LOG_DIR, "ocr_ml.ps1");
            File.WriteAllText(script, OCR_SCRIPT, new UTF8Encoding(false));
            string outFile = Path.Combine(LOG_DIR, "ocr_ml_out.txt");
            if (File.Exists(outFile)) File.Delete(outFile);
            string useLang = (lang != null && lang.Length > 0) ? lang : "en-US";
            ProcessStartInfo psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script +
                "\" -Path \"" + imgPath + "\" -Out \"" + outFile + "\" -Lang " + useLang);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
            {
                if (!p.WaitForExit(25000))
                {
                    try { p.Kill(); } catch { }
                    return "";
                }
            }
            if (!File.Exists(outFile)) return "";
            return ReadTextAuto(outFile);
        }
        catch (Exception ex)
        {
            Log("小窗 OCR 失败：" + ex.Message);
            return "";
        }
    }

    static bool MlIsImageExt(string ext)
    {
        switch (ext)
        {
            case ".png": case ".jpg": case ".jpeg": case ".bmp": case ".gif":
            case ".webp": case ".tif": case ".tiff": return true;
        }
        return false;
    }

    static bool MlIsTextExt(string ext)
    {
        switch (ext)
        {
            case ".txt": case ".md": case ".log": case ".csv": case ".srt":
            case ".json": case ".html": case ".htm": case ".xml": case ".yaml":
            case ".yml": case ".ini": case ".tsv": return true;
        }
        return false;
    }

    // 图片认字用哪种语言包：文件名里有语言线索就用它，没有就让系统按用户配置来
    static string MlOcrLangFor(string fileNameHint)
    {
        string h = (fileNameHint == null ? "" : fileNameHint).ToLowerInvariant();
        if (h.IndexOf("jp") >= 0 || h.IndexOf("ja") >= 0 || h.IndexOf("日") >= 0) return "ja-JP";
        if (h.IndexOf("kr") >= 0 || h.IndexOf("ko") >= 0 || h.IndexOf("韩") >= 0) return "ko-KR";
        if (h.IndexOf("ar") >= 0 || h.IndexOf("阿") >= 0) return "ar-SA";
        if (h.IndexOf("ru") >= 0 || h.IndexOf("俄") >= 0) return "ru-RU";
        return "";      // 空 = 交给系统按用户语言排
    }

    void MlApplyToMain(string text)
    {
        try
        {
            if (inputBox != null) inputBox.Text = text;
        }
        catch { }
        try
        {
            if (mlPanel != null && mlPanel.Visible) HideMultilingual();   // 中英文归主界面，小窗别挡着
        }
        catch { }
        if (!mlSuppressQuery)
        {
            // v2.4 第 4 步：主界面收文件统一走「短文本一条请求 / 长文本分段」的编排（MainIngest.cs）
            try { MainStartDocTranslate(text); } catch (Exception ex) { Log("拖入后主界面翻译失败：" + ex.Message); }
        }
    }

    void MlApplyToMl(string text, string lang)
    {
        try
        {
            ShowMultilingual();
            if (mlInBox != null) mlInBox.Box.Text = text;
            if (lang != null && lang.Length > 0) MlTestSetPair(lang, MlTargetFor(lang));
            if (!mlSuppressQuery) MlTranslateStart();
            else MlSay("已放入小窗（自检模式，不联网）");
        }
        catch (Exception ex) { Log("拖入后小窗处理失败：" + ex.Message); }
    }

    // 源语言是 X 时默认翻成中文（本来就是中文才不会是 X，所以基本都成立）
    internal static string MlTargetFor(string srcLang)
    {
        if (srcLang == "zh") return "en";
        return "zh";
    }

    // ======================== ⑥ 导出：TXT / 真 docx ========================

    // 桌面位置一律问系统要，绝不硬拼 "C:\Users\xxx\Desktop"（OneDrive 重定向、多用户都会错）
    internal static string MlDesktopDir()
    {
        try
        {
            string d = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (d != null && d.Length > 0 && Directory.Exists(d)) return d;
        }
        catch { }
        try
        {
            StringBuilder sb = new StringBuilder(260);
            if (SHGetFolderPath(IntPtr.Zero, 0x0010, IntPtr.Zero, 0, sb) == 0)
            {
                string s = sb.ToString();
                if (s.Length > 0 && Directory.Exists(s)) return s;
            }
        }
        catch { }
        try { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
        catch { return ""; }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetFolderPath(IntPtr hwndOwner, int nFolder, IntPtr hToken, int dwFlags, StringBuilder pszPath);

    void MlExport(bool docx)
    {
        string dst = (mlOutBox == null || mlOutBox.Box.Text == null) ? "" : mlOutBox.Box.Text.Trim();
        if (dst.Length == 0) { MlSay("还没有译文，先翻一段再导出"); return; }
        bool withSrc = (mlWithSrcChk != null && mlWithSrcChk.Checked);
        string src = withSrc ? MlSrcForExport() : "";
        string dir = MlDesktopDir();
        if (dir == null || dir.Length == 0) { MlSay("找不到桌面目录，导出没做成"); return; }
        try
        {
            string p = MlWriteExport(dir, docx, src, dst, withSrc);
            MlLastExportPath = p;
            MlSay("已导出到桌面：" + Path.GetFileName(p) + (withSrc ? "（带原文）" : "（只有译文）"));
            Log("小窗导出 " + (docx ? "docx" : "txt") + " → " + p + " 带原文=" + withSrc
                + " RTL=" + MlRtlDominant(dst) + " 字数=" + dst.Length);
        }
        catch (Exception ex)
        {
            MlSay("导出失败：" + MlOneLine(ex.Message, 60));
            Log("小窗导出失败：" + ex.Message);
        }
    }

    string MlSrcForExport()
    {
        if (mlLastSrc != null && mlLastSrc.Trim().Length > 0) return mlLastSrc;
        if (mlInBox != null && mlInBox.Box.Text != null) return mlInBox.Box.Text.Trim();
        return "";
    }

    // 真正的落盘：返回写好的完整路径。自检也走这里（只是换个目录）
    internal string MlWriteExport(string dir, bool docx, string src, string dst, bool withSrc)
    {
        if (dst == null) dst = "";
        Directory.CreateDirectory(dir);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string baseName = (docx ? "其他语言译文_" : "其他语言译文_") + stamp;
        string ext = docx ? ".docx" : ".txt";
        string path = Path.Combine(dir, baseName + ext);
        int dup = 2;
        while (File.Exists(path)) { path = Path.Combine(dir, baseName + "_" + dup + ext); dup++; }

        if (docx) MlDocxSave(path, src, dst, withSrc);
        else File.WriteAllText(path, MlTxtBody(src, dst, withSrc), new UTF8Encoding(true));
        return path;
    }

    static string MlTxtBody(string src, string dst, bool withSrc)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("其他语言小窗 · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine();
        if (withSrc && src != null && src.Trim().Length > 0)
        {
            sb.AppendLine("===== 原文 =====");
            sb.AppendLine(src.Trim());
            sb.AppendLine();
            sb.AppendLine("===== 译文 =====");
        }
        sb.AppendLine((dst == null ? "" : dst.Trim()));
        return sb.ToString();
    }

    // ---- docx：自己写 OOXML（不依赖 Word / OpenXML SDK，写出来的是 Word 能直接开的真 docx）----
    const string ML_DOCX_CT =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
        "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
        "</Types>";

    const string ML_DOCX_RELS =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
        "</Relationships>";

    const string ML_DOCX_DOCRELS =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
        "</Relationships>";

    const string ML_DOCX_STYLES =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
        "<w:docDefaults><w:rPrDefault><w:rPr>" +
        "<w:rFonts w:ascii=\"Microsoft YaHei\" w:hAnsi=\"Microsoft YaHei\" w:eastAsia=\"Microsoft YaHei\" w:cs=\"Arial\"/>" +
        "<w:sz w:val=\"22\"/><w:szCs w:val=\"22\"/>" +
        "</w:rPr></w:rPrDefault></w:docDefaults>" +
        "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\">" +
        "<w:name w:val=\"Normal\"/><w:qFormat/>" +
        "<w:pPr><w:spacing w:after=\"120\" w:line=\"300\" w:lineRule=\"auto\"/></w:pPr>" +
        "</w:style>" +
        "</w:styles>";

    static string MlXmlEsc(string s)
    {
        if (s == null) return "";
        StringBuilder sb = new StringBuilder(s.Length + 16);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&apos;"); break;
                case '\t': sb.Append(' '); break;
                default:
                    if (c < 0x20 || c == 0xFFFE || c == 0xFFFF) sb.Append(' ');
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    // 单个段落。rtl=true 时：段属性加 <w:bidi/>、对齐靠右、run 加 <w:rtl/>，
    // 并且把复杂文种字体（w:cs）指到 Arial——不然阿拉伯语在某些机器上会变成方框。
    static string MlDocxParaXml(string line, bool rtl, bool bold, string style)
    {
        string asciiFont = rtl ? "Arial" : "Microsoft YaHei";
        string csFont = "Arial";
        string ppr = "<w:pPr>";
        if (style != null && style.Length > 0) ppr = "<w:pPr><w:pStyle w:val=\"" + style + "\"/>";
        if (rtl) ppr += "<w:bidi/><w:jc w:val=\"right\"/>";
        ppr += "</w:pPr>";
        string rpr = "<w:rPr><w:rFonts w:ascii=\"" + asciiFont + "\" w:hAnsi=\"" + asciiFont
                   + "\" w:eastAsia=\"Microsoft YaHei\" w:cs=\"" + csFont + "\"/>"
                   + (rtl ? "<w:rtl/>" : "")
                   + (bold ? "<w:b/>" : "")
                   + "<w:sz w:val=\"22\"/><w:szCs w:val=\"22\"/></w:rPr>";
        return "<w:p>" + ppr + "<w:r>" + rpr + "<w:t xml:space=\"preserve\">" + MlXmlEsc(line) + "</w:t></w:r></w:p>";
    }

    internal static string MlDocxDocumentXml(string src, string dst, bool withSrc)
    {
        StringBuilder body = new StringBuilder();
        body.Append(MlDocxParaXml("其他语言小窗 · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), false, true, null));
        if (withSrc && src != null && src.Trim().Length > 0)
        {
            body.Append(MlDocxParaXml("原文", false, true, null));
            foreach (string line in src.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                body.Append(MlDocxParaXml(line, MlRtlDominant(line), false, null));
            body.Append(MlDocxParaXml("译文", false, true, null));
        }
        string d = dst == null ? "" : dst;
        foreach (string line in d.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            body.Append(MlDocxParaXml(line, MlRtlDominant(line), false, null));

        return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
             + "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">"
             + "<w:body>" + body.ToString()
             + "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/>"
             + "<w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"851\" w:footer=\"992\" w:gutter=\"0\"/>"
             + "</w:sectPr></w:body></w:document>";
    }

    internal static void MlDocxSave(string path, string src, string dst, bool withSrc)
    {
        List<string> names = new List<string>();
        List<byte[]> datas = new List<byte[]>();
        names.Add("[Content_Types].xml"); datas.Add(Encoding.UTF8.GetBytes(ML_DOCX_CT));
        names.Add("_rels/.rels"); datas.Add(Encoding.UTF8.GetBytes(ML_DOCX_RELS));
        names.Add("word/document.xml"); datas.Add(Encoding.UTF8.GetBytes(MlDocxDocumentXml(src, dst, withSrc)));
        names.Add("word/styles.xml"); datas.Add(Encoding.UTF8.GetBytes(ML_DOCX_STYLES));
        names.Add("word/_rels/document.xml.rels"); datas.Add(Encoding.UTF8.GetBytes(ML_DOCX_DOCRELS));
        MlZipWrite(path, names, datas);
    }

    // ---- 极简 zip 写：全部 stored（不压缩），Word/WPS 都认；不引第三方库 ----
    static void MlZipWrite(string path, List<string> names, List<byte[]> datas)
    {
        uint[] crcs = new uint[names.Count];
        uint[] offsets = new uint[names.Count];
        using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (BinaryWriter bw = new BinaryWriter(fs))
        {
            for (int i = 0; i < names.Count; i++)
            {
                byte[] nameBytes = Encoding.UTF8.GetBytes(names[i]);
                byte[] data = datas[i];
                crcs[i] = MlCrc32(data);
                offsets[i] = (uint)fs.Position;
                bw.Write((uint)0x04034b50);
                bw.Write((ushort)20);            // version needed
                bw.Write((ushort)0x0800);        // UTF-8 文件名
                bw.Write((ushort)0);             // stored
                bw.Write((ushort)0);             // time
                bw.Write((ushort)0);             // date
                bw.Write(crcs[i]);
                bw.Write((uint)data.Length);
                bw.Write((uint)data.Length);
                bw.Write((ushort)nameBytes.Length);
                bw.Write((ushort)0);
                bw.Write(nameBytes);
                bw.Write(data);
            }
            uint cdStart = (uint)fs.Position;
            for (int i = 0; i < names.Count; i++)
            {
                byte[] nameBytes = Encoding.UTF8.GetBytes(names[i]);
                bw.Write((uint)0x02014b50);
                bw.Write((ushort)20);            // version made by
                bw.Write((ushort)20);            // version needed
                bw.Write((ushort)0x0800);
                bw.Write((ushort)0);
                bw.Write((ushort)0);
                bw.Write((ushort)0);
                bw.Write(crcs[i]);
                bw.Write((uint)datas[i].Length);
                bw.Write((uint)datas[i].Length);
                bw.Write((ushort)nameBytes.Length);
                bw.Write((ushort)0);             // extra
                bw.Write((ushort)0);             // comment
                bw.Write((ushort)0);             // disk
                bw.Write((ushort)0);             // internal attr
                bw.Write((uint)0);               // external attr
                bw.Write(offsets[i]);
                bw.Write(nameBytes);
            }
            uint cdSize = (uint)fs.Position - cdStart;
            bw.Write((uint)0x06054b50);
            bw.Write((ushort)0);
            bw.Write((ushort)0);
            bw.Write((ushort)names.Count);
            bw.Write((ushort)names.Count);
            bw.Write(cdSize);
            bw.Write(cdStart);
            bw.Write((ushort)0);
        }
    }

    static uint[] mlCrcTab = null;

    static uint MlCrc32(byte[] data)
    {
        if (mlCrcTab == null)
        {
            mlCrcTab = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = ((c & 1) != 0) ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
                mlCrcTab[i] = c;
            }
        }
        uint crc = 0xFFFFFFFFu;
        if (data != null)
            for (int i = 0; i < data.Length; i++) crc = mlCrcTab[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    // ---- 极简 zip 读：只解析中央目录，支持 stored / deflate 两种（docx 基本都是 deflate）----
    static byte[] MlZipEntry(byte[] zip, string wantName)
    {
        if (zip == null || zip.Length < 40) return null;
        int eocd = -1;
        int lowBound = Math.Max(0, zip.Length - 65558);
        for (int i = zip.Length - 22; i >= lowBound; i--)
            if (BitConverter.ToUInt32(zip, i) == 0x06054b50u) { eocd = i; break; }
        if (eocd < 0) return null;
        int count = BitConverter.ToUInt16(zip, eocd + 10);
        int p = (int)BitConverter.ToUInt32(zip, eocd + 16);
        for (int n = 0; n < count && p + 46 <= zip.Length; n++)
        {
            if (BitConverter.ToUInt32(zip, p) != 0x02014b50u) break;
            int method = BitConverter.ToUInt16(zip, p + 10);
            int csize = (int)BitConverter.ToUInt32(zip, p + 20);
            int nameLen = BitConverter.ToUInt16(zip, p + 28);
            int extraLen = BitConverter.ToUInt16(zip, p + 30);
            int cmtLen = BitConverter.ToUInt16(zip, p + 32);
            int lho = (int)BitConverter.ToUInt32(zip, p + 42);
            string nm = Encoding.UTF8.GetString(zip, p + 46, nameLen);
            if (string.Equals(nm, wantName, StringComparison.OrdinalIgnoreCase))
            {
                int lnl = BitConverter.ToUInt16(zip, lho + 26);
                int lel = BitConverter.ToUInt16(zip, lho + 28);
                int dataOff = lho + 30 + lnl + lel;
                if (method == 0)
                {
                    byte[] raw = new byte[csize];
                    Array.Copy(zip, dataOff, raw, 0, csize);
                    return raw;
                }
                using (MemoryStream ms = new MemoryStream(zip, dataOff, csize))
                using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Decompress))
                using (MemoryStream outMs = new MemoryStream())
                {
                    byte[] buf = new byte[8192];
                    int r;
                    while ((r = ds.Read(buf, 0, buf.Length)) > 0) outMs.Write(buf, 0, r);
                    return outMs.ToArray();
                }
            }
            p += 46 + nameLen + extraLen + cmtLen;
        }
        return null;
    }

    // docx → 纯文本：按段落取 w:t，段落之间换行。读不出来返回 null（调用方负责说明白）
    internal static string MlDocxText(string path)
    {
        try
        {
            byte[] zip = File.ReadAllBytes(path);
            byte[] doc = MlZipEntry(zip, "word/document.xml");
            if (doc == null) return null;
            string xml = Encoding.UTF8.GetString(doc);
            StringBuilder outp = new StringBuilder();
            MatchCollection ps = Regex.Matches(xml, "<w:p[ >].*?</w:p>|<w:p/>", RegexOptions.Singleline);
            if (ps.Count == 0)
            {
                // 兜底：没有段落标记就整体抽 w:t
                foreach (Match t in Regex.Matches(xml, "<w:t[^>]*>(.*?)</w:t>", RegexOptions.Singleline))
                    outp.Append(MlXmlUnesc(t.Groups[1].Value));
            }
            else
            {
                foreach (Match pm in ps)
                {
                    StringBuilder line = new StringBuilder();
                    foreach (Match t in Regex.Matches(pm.Value, "<w:t[^>]*>(.*?)</w:t>", RegexOptions.Singleline))
                        line.Append(MlXmlUnesc(t.Groups[1].Value));
                    string one = line.ToString();
                    one = Regex.Replace(one, "\u000B", "\n");
                    outp.Append(one);
                    outp.Append('\n');
                }
            }
            return outp.ToString();
        }
        catch (Exception ex)
        {
            Log("读 docx 失败：" + path + " → " + ex.Message);
            return null;
        }
    }

    static string MlXmlUnesc(string s)
    {
        if (s == null) return "";
        string t = s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
                   .Replace("&apos;", "'").Replace("&amp;", "&");
        if (t.IndexOf("&#") >= 0)
        {
            t = Regex.Replace(t, @"&#x([0-9A-Fa-f]+);", delegate (Match m)
            {
                try { return ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString(); } catch { return ""; }
            });
            t = Regex.Replace(t, @"&#(\d+);", delegate (Match m)
            {
                try { return ((char)int.Parse(m.Groups[1].Value)).ToString(); } catch { return ""; }
            });
        }
        return t;
    }

    // ======================== ⑤.5 PDF 抽文字 ========================
    // 不引第三方库（这个 exe 一直是单文件、拷走就能用），所以自己来：
    //   ① 找出所有内容流，FlateDecode 的自己解压
    //   ② 正文字符串按 ToUnicode 表翻译成真字符；没有表就按字节形态兜底
    //   ③ 扫描件 / 加密件 / 字体没带表的，一律给一句人话，绝不抛异常

    internal static string MlPdfText(string path, ref string why)
    {
        try
        {
            FileInfo fi = new FileInfo(path);
            if (fi.Length > 64L * 1024 * 1024) { why = "PDF 太大（超过 64 MB）"; return null; }
            byte[] b = File.ReadAllBytes(path);
            if (b.Length < 16 || Encoding.ASCII.GetString(b, 0, 5) != "%PDF-")
            { why = "这不是一个正常的 PDF"; return null; }

            // 加密件：/Encrypt 一般躺在尾部 trailer 里，全文件找一遍最稳
            if (MlByteIndexOf(b, Encoding.ASCII.GetBytes("/Encrypt"), 0) >= 0)
            { why = "PDF 加了密码（加密件），先去密码再拖"; return null; }

            List<byte[]> streams = MlPdfStreams(b);
            if (streams.Count == 0) { why = "PDF 里没找到可读的正文层（多为扫描件）"; return null; }

            // 第一遍：把字体自带的 ToUnicode 表拼起来，第二遍才拿它解正文
            Dictionary<int, string> cmap = new Dictionary<int, string>();
            List<byte[]> bodies = new List<byte[]>();
            foreach (byte[] st in streams)
            {
                string ascii = MlAsciiSlice(st, 0, Math.Min(st.Length, 65536));
                if (ascii.IndexOf("beginbfchar", StringComparison.Ordinal) >= 0
                    || ascii.IndexOf("beginbfrange", StringComparison.Ordinal) >= 0)
                { MlPdfParseCMap(ascii, cmap); continue; }
                if (ascii.IndexOf("BT", StringComparison.Ordinal) >= 0) bodies.Add(st);
            }

            StringBuilder sb = new StringBuilder();
            foreach (byte[] st in bodies)
            {
                string one = MlPdfPageText(st, cmap);
                if (one == null) continue;
                one = MlPdfClean(one);
                if (one.Length == 0) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(one);
                if (sb.Length > 4000000) break;         // 别为了一个超大 PDF 把内存吃满
            }

            string text = MlPdfClean(sb.ToString());
            if (text.Length < 2)
            { why = "这个 PDF 里抽不出文字（多为扫描件），请先转成 docx 或 txt"; return null; }

            // 抽出来一堆私用区字符 = 字体没带字符表，硬塞给用户也是乱码
            int bad = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if ((c >= '\uE000' && c <= '\uF8FF') || c == '\uFFFD') bad++;
            }
            if (bad * 3 > text.Length)
            { why = "这个 PDF 的字体没带字符表，抽出来是乱码（多为扫描件或加了保护），请先转成 docx 或 txt"; return null; }

            return text;
        }
        catch (Exception ex)
        {
            Log("抽 PDF 失败：" + path + " → " + ex.Message);
            why = "这个 PDF 读不了（" + MlOneLine(ex.Message, 30) + "）";
            return null;
        }
    }

    // 文件头是不是 OLE 复合文档（加密的 docx/xlsx 就是这层壳，不是 zip）
    internal static bool MlLooksOle(string path)
    {
        try
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] h = new byte[8];
                if (fs.Read(h, 0, 8) < 8) return false;
                return h[0] == 0xD0 && h[1] == 0xCF && h[2] == 0x11 && h[3] == 0xE0;
            }
        }
        catch { return false; }
    }

    // 把 PDF 里所有 stream…endstream 段取出来：图片跳过，FlateDecode 的自己解压
    static List<byte[]> MlPdfStreams(byte[] b)
    {
        List<byte[]> list = new List<byte[]>();
        byte[] kStream = Encoding.ASCII.GetBytes("stream");
        byte[] kEnd = Encoding.ASCII.GetBytes("endstream");
        int p = 0;
        while (p < b.Length)
        {
            int s = MlByteIndexOf(b, kStream, p);
            if (s < 0) break;
            int start = s + 6;
            if (start < b.Length && b[start] == 13) start++;
            if (start < b.Length && b[start] == 10) start++;
            int e = MlByteIndexOf(b, kEnd, start);
            if (e < 0) break;
            int len = e - start;
            p = e + 9;
            if (len <= 0) continue;

            byte[] raw = new byte[len];
            Array.Copy(b, start, raw, 0, len);

            // 这段流属于谁，看它前面那一小段字典
            int dfrom = Math.Max(0, s - 1200);
            string dict = MlAsciiSlice(b, dfrom, s - dfrom);
            if (dict.IndexOf("/Image", StringComparison.Ordinal) >= 0) continue;
            if (dict.IndexOf("/FlateDecode", StringComparison.Ordinal) >= 0)
            {
                byte[] inf = MlInflate(raw);
                if (inf != null) list.Add(inf);
            }
            else if (dict.IndexOf("/Filter", StringComparison.Ordinal) < 0)
            {
                list.Add(raw);                       // 没压缩的内容流，原样留着
            }
            if (list.Count > 4000) break;
        }
        return list;
    }

    // zlib 头（78 9C / 78 DA…）要跳 2 字节；也有裸 deflate 的，两种都试一遍
    static byte[] MlInflate(byte[] data)
    {
        for (int skip = 0; skip <= 2; skip += 2)
        {
            if (data.Length - skip < 8) continue;
            try
            {
                using (MemoryStream ms = new MemoryStream(data, skip, data.Length - skip))
                using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Decompress))
                using (MemoryStream outMs = new MemoryStream())
                {
                    byte[] buf = new byte[16384];
                    int r;
                    while ((r = ds.Read(buf, 0, buf.Length)) > 0) outMs.Write(buf, 0, r);
                    if (outMs.Length > 0) return outMs.ToArray();
                }
            }
            catch { }
        }
        return null;
    }

    // 内容流 → 文字：只认 BT…ET 里的 Tj / TJ / ' / " 四种出字指令
    static string MlPdfPageText(byte[] s, Dictionary<int, string> cmap)
    {
        StringBuilder outp = new StringBuilder();
        List<string> pend = new List<string>();
        int i = 0, n = s.Length;
        bool inText = false;

        while (i < n)
        {
            byte c = s[i];

            if (c == (byte)'(')
            {
                int j = i + 1, depth = 1;
                List<byte> bs = new List<byte>();
                while (j < n && depth > 0)
                {
                    byte ch = s[j];
                    if (ch == (byte)'\\')
                    {
                        j++;
                        if (j >= n) break;
                        byte e2 = s[j];
                        if (e2 == (byte)'n') { bs.Add(10); j++; }
                        else if (e2 == (byte)'r') { bs.Add(13); j++; }
                        else if (e2 == (byte)'t') { bs.Add(9); j++; }
                        else if (e2 == (byte)'b') { bs.Add(8); j++; }
                        else if (e2 == (byte)'f') { bs.Add(12); j++; }
                        else if (e2 == (byte)'\r') { j++; if (j < n && s[j] == 10) j++; }
                        else if (e2 == (byte)'\n') { j++; }
                        else if (e2 >= '0' && e2 <= '7')
                        {
                            int v = 0, k = 0;
                            while (k < 3 && j < n && s[j] >= '0' && s[j] <= '7') { v = v * 8 + (s[j] - '0'); j++; k++; }
                            bs.Add((byte)(v & 0xFF));
                        }
                        else { bs.Add(e2); j++; }
                    }
                    else if (ch == (byte)'(') { depth++; bs.Add(ch); j++; }
                    else if (ch == (byte)')') { depth--; j++; if (depth == 0) break; bs.Add(ch); }
                    else { bs.Add(ch); j++; }
                }
                pend.Add(MlPdfDecode(bs.ToArray(), cmap));
                i = j;
                continue;
            }

            if (c == (byte)'<' && i + 1 < n && s[i + 1] != (byte)'<')
            {
                int j = i + 1, hi = -1;
                List<byte> bs = new List<byte>();
                while (j < n && s[j] != (byte)'>')
                {
                    int v = MlHexVal(s[j]);
                    if (v >= 0)
                    {
                        if (hi < 0) hi = v;
                        else { bs.Add((byte)((hi << 4) | v)); hi = -1; }
                    }
                    j++;
                }
                if (hi >= 0) bs.Add((byte)(hi << 4));
                pend.Add(MlPdfDecode(bs.ToArray(), cmap));
                i = j < n ? j + 1 : j;
                continue;
            }

            if (c == (byte)'/' )
            {
                // 名字对象（字体名之类）跳过，别把 /F1 当成出字
                i++;
                while (i < n && !MlPdfSpace(s[i]) && s[i] != (byte)'/' && s[i] != (byte)'<' && s[i] != (byte)'[') i++;
                continue;
            }

            if (MlPdfSpace(c) || c == (byte)'[' || c == (byte)']' || c == (byte)'>' || c == (byte)'{' || c == (byte)'}')
            { i++; continue; }

            // 操作符
            int st = i;
            while (i < n && !MlPdfSpace(s[i]) && s[i] != (byte)'(' && s[i] != (byte)'<'
                   && s[i] != (byte)'[' && s[i] != (byte)'/' && s[i] != (byte)']') i++;
            string op = Encoding.ASCII.GetString(s, st, i - st);

            if (op == "BT") { inText = true; pend.Clear(); continue; }
            if (op == "ET")
            {
                inText = false;
                if (outp.Length > 0 && outp[outp.Length - 1] != '\n') outp.Append('\n');
                pend.Clear();
                continue;
            }
            if (!inText) { pend.Clear(); continue; }

            bool take = (op == "Tj" || op == "TJ" || op == "'" || op == "\"");
            if (take)
            {
                if (op == "'" || op == "\"") MlPdfNewLine(outp);
                for (int k = 0; k < pend.Count; k++) outp.Append(pend[k]);
                pend.Clear();
                continue;
            }
            if (op == "T*" || op == "TD" || op == "Td") { MlPdfNewLine(outp); pend.Clear(); continue; }
            pend.Clear();
        }
        return outp.ToString();
    }

    static void MlPdfNewLine(StringBuilder sb)
    {
        if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
    }

    static bool MlPdfSpace(byte c)
    {
        return c == 0 || c == 9 || c == 10 || c == 12 || c == 13 || c == 32;
    }

    static int MlHexVal(byte c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
    }

    // 字节码 → 字符：有 ToUnicode 表就照表来（这才是正路），没有就按字节形态猜
    static string MlPdfDecode(byte[] b, Dictionary<int, string> cmap)
    {
        if (b == null || b.Length == 0) return "";
        if (cmap != null && cmap.Count > 0)
        {
            int big = 0, small = 0;
            foreach (int k in cmap.Keys) { if (k > 0xFF) big++; else small++; }

            if (big > small && b.Length % 2 == 0)
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i + 1 < b.Length; i += 2)
                {
                    string v;
                    if (cmap.TryGetValue((b[i] << 8) | b[i + 1], out v)) sb.Append(v);
                }
                if (sb.Length > 0) return sb.ToString();
            }
            else
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < b.Length; i++)
                {
                    string v;
                    if (cmap.TryGetValue(b[i], out v)) sb.Append(v);
                }
                if (sb.Length > 0) return sb.ToString();
            }
        }

        // 没有 ToUnicode 表：命令流里多是 UTF-16BE 字节（阿语/中文都这样），
        // 但也可能是 UTF-8 或单字节老编码，所以每种都解一遍，按"像不像人话"打分取最高的
        string best = null;
        double bestScore = -1;

        string[] tries = new string[]
        {
            MlPdfTryUtf8(b),
            b.Length % 2 == 0 ? MlPdfTryU16(b, true) : null,
            b.Length % 2 == 0 ? MlPdfTryU16(b, false) : null,
            MlPdfTryLatin(b)
        };
        foreach (string t in tries)
        {
            if (t == null || t.Length == 0) continue;
            double sc = MlPdfScore(t);
            if (sc > bestScore + 0.0001) { bestScore = sc; best = t; }
        }
        if (best != null) return best;
        return "";
    }

    // 解出来的东西像不像正常文字：控制字符、替换符、私用区一律重罚
    static double MlPdfScore(string s)
    {
        if (string.IsNullOrEmpty(s)) return -1;
        double good = 0, bad = 0;
        foreach (char c in s)
        {
            if (c == '\n' || c == '\r' || c == '\t' || c == ' ') { good++; continue; }
            if (c < 0x20) { bad += 3; continue; }                    // 控制字符 → 基本可以断定解错了
            if (c >= 0x7F && c < 0xA0) { bad += 3; continue; }
            if (c == '\uFFFD') { bad += 3; continue; }
            if (c >= 0xE000 && c <= 0xF8FF) { bad += 2; continue; }   // 私用区 → 中文/日文缺表的典型
            if (c >= 0xD800 && c <= 0xDFFF) { bad += 2; continue; }
            good++;
        }
        return good / (good + bad);
    }

    static string MlPdfTryU16(byte[] b, bool bigEndian)
    {
        try
        {
            string u = bigEndian ? Encoding.BigEndianUnicode.GetString(b) : Encoding.Unicode.GetString(b);
            return u;
        }
        catch { return null; }
    }

    static string MlPdfTryUtf8(byte[] b)
    {
        try { return new UTF8Encoding(false, true).GetString(b); }
        catch { return null; }
    }

    static string MlPdfTryLatin(byte[] b)
    {
        StringBuilder latin = new StringBuilder();
        for (int i = 0; i < b.Length; i++)
        {
            byte c = b[i];
            latin.Append(c >= 32 || c == 10 || c == 13 || c == 9 ? (char)c : ' ');
        }
        return latin.ToString();
    }

    // ToUnicode CMap：beginbfchar（码 → 字）与 beginbfrange（码区间 → 字/字表）
    static void MlPdfParseCMap(string txt, Dictionary<int, string> map)
    {
        if (txt == null || map == null) return;
        foreach (Match blk in Regex.Matches(txt, @"beginbfchar(.*?)endbfchar", RegexOptions.Singleline))
        {
            foreach (Match p in Regex.Matches(blk.Groups[1].Value, @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>"))
            {
                try { map[Convert.ToInt32(p.Groups[1].Value, 16)] = MlHexToUnicode(p.Groups[2].Value); }
                catch { }
            }
        }
        foreach (Match blk in Regex.Matches(txt, @"beginbfrange(.*?)endbfrange", RegexOptions.Singleline))
        {
            string body = blk.Groups[1].Value;
            foreach (Match p in Regex.Matches(body,
                @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>\s*(?:<([0-9A-Fa-f]+)>|\[(.*?)\])", RegexOptions.Singleline))
            {
                try
                {
                    int lo = Convert.ToInt32(p.Groups[1].Value, 16);
                    int hi = Convert.ToInt32(p.Groups[2].Value, 16);
                    if (hi < lo || hi - lo > 65535) continue;
                    if (p.Groups[3].Success)
                    {
                        int basev = Convert.ToInt32(p.Groups[3].Value, 16);
                        for (int c = lo; c <= hi; c++) map[c] = ((char)(basev + (c - lo))).ToString();
                    }
                    else
                    {
                        MatchCollection arr = Regex.Matches(p.Groups[4].Value, @"<([0-9A-Fa-f]+)>");
                        int c = lo;
                        foreach (Match a in arr)
                        {
                            if (c > hi) break;
                            map[c] = MlHexToUnicode(a.Groups[1].Value);
                            c++;
                        }
                    }
                }
                catch { }
            }
        }
    }

    static string MlHexToUnicode(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return "";
        if (hex.Length % 4 != 0) hex = hex.PadRight(hex.Length + (4 - hex.Length % 4), '0');
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i + 4 <= hex.Length; i += 4)
        {
            try { sb.Append((char)Convert.ToInt32(hex.Substring(i, 4), 16)); }
            catch { }
        }
        return sb.ToString();
    }

    // 抽完之后收拾一下：控制字符清掉、连续空行压成一行
    static string MlPdfClean(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        StringBuilder sb = new StringBuilder(s.Length);
        int nl = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\r') continue;
            if (c == '\n')
            {
                nl++;
                if (nl > 2) continue;
                sb.Append('\n');
                continue;
            }
            nl = 0;
            if (c == '\t') { sb.Append(' '); continue; }
            if (c < 32) continue;
            if (c == '\u0000' || c == '\uFFFE' || c == '\uFFFF') continue;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    static string MlAsciiSlice(byte[] b, int from, int len)
    {
        if (b == null || len <= 0 || from >= b.Length) return "";
        if (from + len > b.Length) len = b.Length - from;
        char[] cs = new char[len];
        for (int i = 0; i < len; i++)
        {
            byte c = b[from + i];
            cs[i] = c < 128 ? (char)c : ' ';
        }
        return new string(cs);
    }

    static int MlByteIndexOf(byte[] hay, byte[] needle, int from)
    {
        if (hay == null || needle == null || needle.Length == 0) return -1;
        int limit = hay.Length - needle.Length;
        for (int i = Math.Max(0, from); i <= limit; i++)
        {
            if (hay[i] != needle[0]) continue;
            bool ok = true;
            for (int k = 1; k < needle.Length; k++)
            {
                if (hay[i + k] != needle[k]) { ok = false; break; }
            }
            if (ok) return i;
        }
        return -1;
    }

    // ======================== ⑤.2 拖进小窗：读正文 → 填源框 → 直接开翻 ========================

    MlWindow mlDropWin;          // 已经挂过拖放的小窗实例（小窗重建后要重挂）

    internal void MlInstallMlDrop()
    {
        if (mlPanel == null || mlDropWin == mlPanel) return;
        mlDropWin = mlPanel;
        try
        {
            mlPanel.AllowDrop = true;
            mlPanel.DragEnter += MlSmallDropEnter;
            mlPanel.DragDrop += MlSmallDropFiles;
            MlAttachSmallDrop(mlPanel);
        }
        catch (Exception ex) { Log("小窗挂拖放失败：" + ex.Message); }
    }

    void MlAttachSmallDrop(Control root)
    {
        foreach (Control c in root.Controls)
        {
            // 文本框自己会处理"拖一段字进来"，别抢它：只给外层和按钮挂文件拖放
            bool isText = c is TextBoxBase;
            if (!isText)
            {
                try
                {
                    c.AllowDrop = true;
                    c.DragEnter += MlSmallDropEnter;
                    c.DragDrop += MlSmallDropFiles;
                }
                catch { }
            }
            MlAttachSmallDrop(c);
        }
    }

    void MlSmallDropEnter(object s, DragEventArgs e)
    {
        bool files = e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effect = files ? DragDropEffects.Copy : DragDropEffects.None;
        if (files) MlSay("松手就放进小窗直接翻");
    }

    void MlSmallDropFiles(object s, DragEventArgs e)
    {
        string[] files = null;
        try { files = e.Data.GetData(DataFormats.FileDrop) as string[]; } catch { }
        if (files == null || files.Length == 0) return;
        string msg;
        try { msg = MlDropToSmall(files); }
        catch (Exception ex) { msg = "这个文件没收进来：" + MlOneLine(ex.Message, 40); }
        MlSay(msg);
        Log("小窗收文件：" + msg);
    }

    // 拖进小窗的落地逻辑：读得出正文就填源框并开翻；读不出就只说原因，绝不抛异常
    internal string MlDropToSmall(string[] paths)
    {
        if (paths == null || paths.Length == 0) return "没接到文件";
        List<string> bad = new List<string>();

        foreach (string raw in paths)
        {
            string path = raw == null ? "" : raw.Trim();
            if (path.Length == 0) continue;
            string nm = Path.GetFileName(path);
            if (nm.Length == 0) nm = path;

            if (Directory.Exists(path)) { bad.Add("文件夹不支持：" + nm); continue; }
            if (!File.Exists(path)) { bad.Add("找不到文件：" + nm); continue; }

            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".lnk" || ext == ".url" || ext == ".pif")
            { bad.Add("快捷方式不支持：" + nm + "（请拖源文件本身）"); continue; }

            string why = null;
            string text = null;
            try { text = MlReadAnyFile(path, ext, ref why); }
            catch (Exception ex) { why = "读不出来（" + MlOneLine(ex.Message, 30) + "）"; }
            if (text == null) { bad.Add((why == null ? "这种文件暂不支持" : why) + "：" + nm); continue; }

            text = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (text.Length == 0) { bad.Add("里面没有可翻的文字：" + nm); continue; }

            if (text.Length > MlMaxChars || Encoding.UTF8.GetByteCount(text) > MlMaxBytes)
            {
                int keep = MlTruncateLen(text);
                if (keep < 1) keep = 1;
                text = text.Substring(0, keep);
                Log("小窗拖入超上限，截断 " + nm + " → 前 " + keep + " 字");
            }

            string lang = MlLangOfText(text);
            if (lang == null || lang.Length == 0 || MlName(lang) == null) lang = "";

            MlApplyToMl(text, lang);          // 填源框 + 摆好语言对 + 直接开翻（自检模式不联网）
            string head = bad.Count > 0 ? string.Join("；", bad.ToArray()) + "；" : "";
            return head + nm + " 已放进小窗（" + text.Length + " 字），正在翻…";
        }

        return bad.Count > 0 ? string.Join("；", bad.ToArray()) : "没接到文件";
    }

    // ======================== ⑦ 首次打开小窗：只提示一次 ========================
    // 需求：第一次打开时说清楚三件事（只做纯文本翻译 / 能拖出主窗 / 支持导出），
    // 记进配置后不再重复。提示占一行，所以措辞压到一句里，别把标签撑爆。
    internal const string MlFirstTipText = "纯文本翻译｜可拖出主窗｜四边能拉大｜译文能导出 TXT / docx";

    void MlFirstTipIfNeeded()
    {
        if (GetSet(MlTipKey, "0") == "1") return;
        SetSet(MlTipKey, "1");
        FlushSettings();
        MlFirstTipCount++;
        MlSay(MlFirstTipText);
        Log("小窗首次打开提示：" + MlFirstTipText + "（已记配置 " + MlTipKey + "=1）");
    }

    // ======================== ⑧ 自检用的小门 ========================

    internal Button MlTxtBtnForTest { get { return mlTxtBtn; } }
    internal Button MlDocxBtnForTest { get { return mlDocxBtn; } }
    internal CheckBox MlWithSrcForTest { get { return mlWithSrcChk; } }
    internal void MlTestSetWithSrc(bool on) { if (mlWithSrcChk != null) mlWithSrcChk.Checked = on; }
    internal void MlTestSuppressQuery() { mlSuppressQuery = true; }
    internal bool MlOutRtlForTest { get { return mlOutBox != null && mlOutBox.Box.RightToLeft == RightToLeft.Yes; } }
    internal bool MlInRtlForTest { get { return mlInBox != null && mlInBox.Box.RightToLeft == RightToLeft.Yes; } }
    internal string MlFirstTipTextForTest { get { return MlFirstTipText; } }
    internal string MlDropToSmallForTest(string[] paths) { return MlDropToSmall(paths); }
    internal string MlDropHookedForTest() { return mlPanel != null && mlPanel.AllowDrop ? "已挂拖放" : "没挂拖放"; }
    internal void MlTestResetFirstTip() { SetSet(MlTipKey, "0"); FlushSettings(); }
    internal string MlTipKeyForTest { get { return MlTipKey; } }
    internal void MlTestFirstTip() { MlFirstTipIfNeeded(); }
    internal void MlTestOutText(string t) { if (mlOutBox != null) mlOutBox.Box.Text = t; }
    internal bool MlTestExportDefaultTargetsDesktop()
    {
        string desk = MlDesktopDir();
        if (desk == null || desk.Length == 0) return false;
        string would = Path.Combine(desk, "其他语言译文_probe.txt");   // 只算路径，不落盘
        return string.Equals(Path.GetDirectoryName(would), desk, StringComparison.OrdinalIgnoreCase);
    }
    internal string MlTestExportTo(string dir, bool docx, bool withSrc)
    {
        string src = withSrc ? MlSrcForExport() : "";
        string dst = (mlOutBox == null || mlOutBox.Box.Text == null) ? "" : mlOutBox.Box.Text.Trim();
        return MlWriteExport(dir, docx, src, dst, withSrc);
    }
    internal List<MlSeg> MlSegsForTest { get { return mlSegs; } }
    internal bool MlSegHasFailForTest { get { return mlSegHasFail; } }

    // 造一份"上次翻到一半、有失败段"的现场：第一段成功、其余失败
    internal void MlTestSeedSegs(string text, string sc, string dc)
    {
        List<string> srcs = MlSplit(text, MlSegChars);
        List<MlSeg> segs = new List<MlSeg>();
        for (int i = 0; i < srcs.Count; i++)
        {
            MlSeg g = new MlSeg(srcs[i]);
            if (i == 0) { g.Ok = true; g.Dst = "已经翻好的第一段"; }
            else { g.Ok = false; g.Err = "模拟失败"; }
            segs.Add(g);
        }
        mlSegs = segs;
        mlSegHash = SegHashOf(text, sc, dc);
        mlSegTotal = segs.Count;
        mlSegHasFail = true;
    }
    internal int MlSegOkCountForTest
    {
        get
        {
            if (mlSegs == null) return 0;
            int k = 0;
            foreach (MlSeg g in mlSegs) if (g.Ok) k++;
            return k;
        }
    }
    internal string MlMainInputForTest { get { return inputBox == null ? "" : inputBox.Text; } }

    // ---------- v2.4 小窗可缩放：自检门 ----------
    internal Size MlMinSizeForTest { get { return MlMinSize(); } }
    internal int MlOutBoxHeightForTest { get { return mlOutBox == null ? -1 : mlOutBox.Box.Height; } }
    internal int MlInBoxHeightForTest { get { return mlInBox == null ? -1 : mlInBox.Box.Height; } }
    internal Size MlSavedSizeForTest { get { return MlSavedSize(); } }
    internal void MlTestToggleMax() { MlToggleMax(); }
    // 底栏那几个按钮拉大拉小之后还在窗口里吗（越界就是排版没跟上）
    internal bool MlFootInsideForTest
    {
        get
        {
            if (mlPanel == null) return false;
            Control[] all = new Control[] { mlSrcBox, mlDstBox, mlGoBtn, mlCopyBtn, mlCloseBtn, mlTxtBtn, mlDocxBtn, mlWithSrcChk, mlTipLbl };
            int w = mlPanel.ClientSize.Width, h = mlPanel.ClientSize.Height;
            foreach (Control c in all)
            {
                if (c == null || !c.Visible) continue;
                if (c.Right > w + 2) return false;
                if (c.Bottom > h + 2) return false;
            }
            return true;
        }
    }
}
