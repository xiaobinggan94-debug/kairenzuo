// MainIngest.cs —— v2.4 第 4 步：主界面收文件的两种入口
//
//   ① 把文档拖到主窗口任意位置 → 读正文 → 自动填进输入框 → 自动开翻
//   ② 主界面按 Ctrl+V，剪贴板里是图片 → 走已有 OCR 链路认字 → 自动开翻
//      （剪贴板是文字时一律不抢，原样交给输入框自己粘）
//
// 三条硬约束：
//   · 底栏那 5 个按钮（复制这句 / 读一遍 / 谐音 / 语气 / 更多 ▾）位置大小文案样式一个都不动
//   · 主窗口视觉不新增控件：接收提示是拖拽期间才出现、松手即销毁的临时覆盖层
//   · 不新增底栏按钮：两种入口都不占界面位置
//
// 复用而不是重写：读文件、编码嗅探、分段、长文上限、OCR 脚本全部走 MlStep2.cs / Translate.cs
// 里已有的实现，本文件只负责「接线 + 提示 + 主界面分段翻译的编排」。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

partial class QueryForm
{
    // ======================== 参数 ========================

    // 拖入提示层（只在拖拽过程中存在，松手/移出立刻 Dispose）
    bool mainDropOn;
    MainDropHint mainDropHint;

    // 剪贴板里那张图临时落盘的位置：OCR 脚本只认文件路径，不认内存里的 Bitmap
    static string MainClipPath { get { return Path.Combine(LOG_DIR, "paste_clip.png"); } }

    // ======================== ① 拖文件进来：接线 ========================

    // 主窗口和它下面所有控件都接上：拖到正文、拖到空白处、拖到底栏边角都算数。
    // 文件怎么读、怎么分流沿用 MlStep2.cs 的 MlInstallDrop / MlRouteDropped，这里只加"看得见的接收提示"。
    internal void MainInstallDrop()
    {
        if (mainDropOn) return;
        mainDropOn = true;
        try
        {
            AllowDrop = true;
            DragEnter += MainDropOver;
            DragOver += MainDropOver;
            DragLeave += MainDropLeave;
            MainAttachHint(this);
            MlInstallDrop();                     // 真正读文件 / 分流的那一套（幂等）
        }
        catch (Exception ex) { Log("主界面挂拖放失败：" + ex.Message); }
    }

    void MainAttachHint(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c == null || c.Name == "dropHint") continue;
            try
            {
                c.AllowDrop = true;
                c.DragEnter += MainDropOver;
                c.DragOver += MainDropOver;
                c.DragLeave += MainDropLeave;
            }
            catch { }
            MainAttachHint(c);
        }
    }

    static bool MainDragHasFiles(DragEventArgs e)
    {
        try { return e != null && e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop); }
        catch { return false; }
    }

    // 拖到哪儿都给出同一个"能收"的信号：鼠标变复制 + 覆盖层明说松手会发生什么
    void MainDropOver(object s, DragEventArgs e)
    {
        bool files = MainDragHasFiles(e);
        try { e.Effect = files ? DragDropEffects.Copy : DragDropEffects.None; } catch { }
        if (!files) { MainDropHintHide(); return; }
        if (mainDropHint == null) MainDropHintShow(MainDragNames(e));
    }

    void MainDropLeave(object s, EventArgs e)
    {
        // 子控件之间挪动也会触发 Leave，只有鼠标真的出了窗口才收提示，否则会一路闪
        try
        {
            Rectangle wr = RectangleToScreen(ClientRectangle);
            if (!wr.Contains(Control.MousePosition)) MainDropHintHide();
        }
        catch { MainDropHintHide(); }
    }

    static string MainDragNames(DragEventArgs e)
    {
        string[] files = null;
        try { files = e.Data.GetData(DataFormats.FileDrop) as string[]; } catch { }
        if (files == null || files.Length == 0) return "";
        string first = "";
        try { first = Path.GetFileName(files[0]); } catch { }
        if (first == null || first.Length == 0) first = files[0];
        if (files.Length == 1) return first;
        return first + " 等 " + files.Length + " 个";
    }

    void MainDropHintShow(string names)
    {
        if (mainDropHint != null)
        {
            mainDropHint.Line2 = names;
            mainDropHint.Invalidate();
            return;
        }
        try
        {
            MainDropHint h = new MainDropHint();
            h.Line2 = names;
            h.Dock = DockStyle.Fill;
            mainDropHint = h;
            Controls.Add(h);
            h.BringToFront();
        }
        catch (Exception ex) { Log("拖入提示层建不出来：" + ex.Message); mainDropHint = null; }
    }

    // 提示层用完就 Dispose：不能让它在控件树里多留一秒（主界面视觉必须回到原样）
    internal void MainDropHintHide()
    {
        MainDropHint h = mainDropHint;
        mainDropHint = null;
        if (h == null) return;
        try { Controls.Remove(h); } catch { }
        try { h.Dispose(); } catch { }
        try { if (box != null) box.Focus(); } catch { }
    }

    // ======================== ① 读进来以后：填框 + 自动开翻 ========================

    // 主界面入口统一走这里：短文本一条请求，长文本按 1200 字分段逐段翻（复用小窗那套分段/上限）
    internal void MainStartDocTranslate(string text)
    {
        MainStartDocTranslate(text, null);
    }

    // note：这次翻要额外跟用户交代一句（⑩ 粘贴即查超长时用来说「只翻了前 5000 字」）。
    // 传 null 时行为与老版本完全一致，拖文件进来那条路一个字都没动。
    internal void MainStartDocTranslate(string text, string note)
    {
        if (text == null) text = "";
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (text.Length == 0) return;

        if (mlSuppressQuery)
        {
            // 自检模式：只验"读进来、填进去"，绝不联网
            try { tip.Text = "自检模式：已读入 " + text.Length + " 字，不联网"; } catch { }
            return;
        }

        if (text.Length <= MlSegChars)
        {
            try { BeginQuery(text, false); } catch (Exception ex) { Log("主界面翻译失败：" + ex.Message); }
            return;
        }

        // 长文：分段。先把原文铺出来，边翻边往下贴译文，别让用户对着空窗口等
        busy = true;
        lastInput = text;
        streamPrev = "";
        int segs = MlSplit(text, MlSegChars).Count;
        string head = text + "\n\n————\n长文分段翻，共 " + segs + " 段"
            + (string.IsNullOrEmpty(note) ? "" : "（" + note + "）") + "，别关窗口……";
        try { SetBoxText(head); } catch { }
        try { tip.Text = "长文分段翻译：共 " + segs + " 段"; } catch { }
        int seq = NextSeq();
        Thread t = new Thread(delegate () { MainDocRun(text, seq); });
        t.IsBackground = true;
        t.Start();
    }

    // 一段一段翻：某段失败单独重试一次，还不成就跳过并记账，最后统一说明白
    void MainDocRun(string text, int seq)
    {
        string lang = MlLangOfText(text);
        string sc = (lang == "zh" || lang == "en") ? lang : "en";
        string dc = (sc == "zh") ? "en" : "zh";

        List<string> srcs = MlSplit(text, MlSegChars);
        int n = srcs.Count;
        StringBuilder acc = new StringBuilder();
        int failed = 0;
        string lastErr = "";

        try
        {
            for (int i = 0; i < n; i++)
            {
                if (!IsCurrent(seq)) return;
                string seg = srcs[i];
                string got = null;
                for (int attempt = 1; attempt <= 2 && got == null; attempt++)
                {
                    if (!IsCurrent(seq)) return;
                    string pre = acc.ToString();
                    int idx = i + 1;
                    try
                    {
                        got = MlHttpOnceAlive(seg, sc, dc,
                            delegate () { return IsCurrent(seq); },
                            delegate (string snap)
                            {
                                Ui(delegate ()
                                {
                                    if (!IsCurrent(seq)) return;
                                    StreamPaint(pre + snap);
                                    tip.Text = "正在翻第 " + idx + "/" + n + " 段…";
                                });
                            }, MlMaxTokensFor(seg));
                        if (got != null) got = got.Trim();
                        if (got != null && got.Length == 0) { got = null; lastErr = "接口返回空"; }
                    }
                    catch (Exception ex)
                    {
                        lastErr = ex.Message;
                        if (attempt == 1) { try { Thread.Sleep(600); } catch { } }
                    }
                }

                if (got != null)
                {
                    if (acc.Length > 0) acc.Append("\n\n");
                    acc.Append(got);
                }
                else failed++;

                string done = acc.ToString();
                int idx2 = i + 1;
                Ui(delegate ()
                {
                    if (!IsCurrent(seq)) return;
                    if (done.Length > 0) StreamPaint(done);
                    tip.Text = "已翻 " + idx2 + "/" + n + " 段" + (failed > 0 ? "（" + failed + " 段没成）" : "");
                });
            }

            string final = acc.ToString().Trim();
            if (final.Length == 0)
            {
                string why = lastErr.Length > 0 ? lastErr : "没拿到译文";
                Ui(delegate ()
                {
                    if (!IsCurrent(seq)) return;
                    SetBoxText("查不动：" + why);
                    tip.Text = "分段翻译失败，可以再拖一次";
                    busy = false;
                });
                return;
            }

            try { CachePut(text, final); } catch { }
            try { AddHistory(text, final); } catch { }
            bool full = (failed == 0);
            Ui(delegate ()
            {
                if (!IsCurrent(seq)) return;
                RenderResult(final);
                tip.Text = full
                    ? "翻好了｜共 " + n + " 段"
                    : "翻好了｜共 " + n + " 段，" + failed + " 段没成（再拖一次会重新翻）";
                busy = false;
            });
        }
        catch (Exception ex)
        {
            Log("主界面分段翻译异常：" + ex.Message);
            Ui(delegate () { if (IsCurrent(seq)) { SetBoxText("查不动：" + ex.Message); busy = false; } });
        }
    }

    // ======================== ② Ctrl+V：剪贴板是图就认字 ========================

    // 只有「有图且没文字」才抢；用户剪贴板里是字（或图文都有）时一律放行给输入框自己粘，
    // 免得把人家刚复制的一句话变成一次莫名其妙的 OCR。
    internal static bool MainWantPasteImage(bool hasImage, bool hasText)
    {
        return hasImage && !hasText;
    }

    // 返回 true = 这次 Ctrl+V 被我们接管了（调用方要把按键吃掉，别让输入框再粘一遍）
    internal bool MainTryPasteImage()
    {
        bool hasImg = false, hasTxt = false;
        try { hasImg = Clipboard.ContainsImage(); hasTxt = Clipboard.ContainsText(); }
        catch { return false; }                       // 剪贴板被别的程序占着：当没这回事
        if (!MainWantPasteImage(hasImg, hasTxt)) return false;

        string path = MainClipPath;
        try
        {
            Directory.CreateDirectory(LOG_DIR);
            using (Image im = Clipboard.GetImage())
            {
                if (im == null) return false;
                if (File.Exists(path)) File.Delete(path);
                im.Save(path, ImageFormat.Png);
            }
        }
        catch (Exception ex)
        {
            Log("剪贴板图片落盘失败：" + ex.Message);
            return false;
        }

        MainOcrClipboardAsync(path);
        return true;
    }

    void MainOcrClipboardAsync(string imgPath)
    {
        try { ShowPanel(); } catch { }
        try { if (mlPanel != null && mlPanel.Visible) HideMultilingual(); } catch { }
        try { tip.Text = "剪贴板里是张图，正在认字…"; } catch { }

        string p = imgPath;
        Thread t = new Thread(delegate ()
        {
            string text = MainOcrFile(p);
            text = MainTidyOcrText(text);
            if (text.Length == 0)
            {
                Ui(delegate () { try { tip.Text = "图里没认出字来，截大一点、字清楚点再试"; } catch { } });
                return;
            }
            int len = text.Length;
            Ui(delegate ()
            {
                try { if (inputBox != null) inputBox.Text = text; } catch { }
                if (mlSuppressQuery) { try { tip.Text = "自检模式：认出 " + len + " 字，不联网"; } catch { } return; }
                MainStartDocTranslate(text);
            });
        });
        t.IsBackground = true;
        t.Start();
    }

    // 截图/剪贴板里的文字常被识别成"一行一个词"，直接翻会把断行当语义；先按中英文习惯收一下
    internal static string MainTidyOcrText(string s)
    {
        if (s == null) return "";
        string t = s.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] lines = t.Split('\n');
        StringBuilder sb = new StringBuilder();
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0) { if (sb.Length > 0 && !sb.ToString().EndsWith("\n")) sb.Append('\n'); continue; }
            if (sb.Length > 0)
            {
                char prev = sb[sb.Length - 1];
                // 中文之间直接接上，英文之间补一个空格（否则会把两个词粘成一个）
                if (prev == '\n') { }
                else if (MainIsHan(prev) || MainIsHan(line[0])) { }
                else sb.Append(' ');
            }
            sb.Append(line);
        }
        return sb.ToString().Trim();
    }

    static bool MainIsHan(char c)
    {
        return c >= '\u4E00' && c <= '\u9FFF';
    }

    // 认图里的字：先按中文语言包试（中文截图最常见），没认出来再按英文试一遍，
    // 用的是 MlStep2.cs 里那份和小窗同一套 OCR 脚本，不另起炉灶。
    internal string MainOcrFile(string imgPath)
    {
        if (imgPath == null || imgPath.Length == 0) return "";
        if (!File.Exists(imgPath)) return "";
        string t = "";
        try { t = RunOcrLang(imgPath, "zh-Hans-CN"); } catch (Exception ex) { Log("OCR(中文)失败：" + ex.Message); }
        if (t != null && t.Trim().Length > 0) return t;
        try { t = RunOcrLang(imgPath, "en-US"); } catch (Exception ex) { Log("OCR(英文)失败：" + ex.Message); }
        return t == null ? "" : t;
    }

    // ======================== ③ 字幕：只要台词 ========================

    // srt / vtt 里的序号、时间轴、"WEBVTT"/"NOTE" 这类结构行对翻译毫无用处，
    // 留着白烧上下文还容易让模型照着格式瞎编。这里只留台词，台词顺序不变。
    internal static string MainSrtStrip(string s)
    {
        if (s == null) return "";
        string[] lines = s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        StringBuilder sb = new StringBuilder();
        foreach (string raw in lines)
        {
            string t = raw == null ? "" : raw.Trim();
            if (t.Length == 0) continue;
            if (Regex.IsMatch(t, @"^\d{1,6}$")) continue;                        // 纯序号
            if (t.IndexOf("-->") >= 0) continue;                                 // 时间轴（"00:00:01,000 --> 00:00:03,000"）
            if (Regex.IsMatch(t, @"^\d{1,2}:\d{2}(:\d{2})?[,.]\d{1,3}$")) continue;   // 单独一行的时间码
            if (Regex.IsMatch(t, @"^WEBVTT\b.*$")) continue;
            if (Regex.IsMatch(t, @"^(NOTE|STYLE|REGION)\b.*$")) continue;
            t = Regex.Replace(t, "<[^>]{1,24}>", "");                             // <i> </i> <font ...> 这类内联标签
            t = Regex.Replace(t, @"\{\\[^}]*\}", "");                             // ASS 的花括号特效码
            t = t.Trim();
            if (t.Length == 0) continue;
            sb.Append(t).Append('\n');
        }
        return sb.ToString().Trim();
    }

    // ======================== 自检出口 ========================

    internal bool MainDropOnForTest { get { return mainDropOn; } }
    internal bool MainHintAliveForTest { get { return mainDropHint != null; } }
    internal void MainSetSuppressForTest(bool v) { mlSuppressQuery = v; }
    internal bool MainSuppressForTest { get { return mlSuppressQuery; } }
    internal bool MainBusyForTest { get { return busy; } }
    internal void MainDropEnterForTest(string[] names) { MainDropHintShow(names == null ? "" : string.Join("、", names)); }
    internal void MainDropLeaveForTest() { MainDropHintHide(); }
    internal void MainIngestForTest(string text) { MainStartDocTranslate(text); }
    internal string MainRouteForTest(string[] paths) { return MlRouteDropped(paths); }
    internal string MainClipPathForTest { get { return MainClipPath; } }
    internal int MainSegCountForTest(int chars)
    {
        return MlSplit(new string('中', chars), MlSegChars).Count;
    }
    internal int MainSegCharsForTest { get { return MlSegChars; } }
    internal int MainMaxCharsForTest { get { return MlMaxChars; } }
    internal string MainOcrFileForTest(string p) { return MainOcrFile(p); }
    internal static string MainTidyForTest(string s) { return MainTidyOcrText(s); }
    internal static string MainSrtForTest(string s) { return MainSrtStrip(s); }
    internal bool MainPasteProbeForTest() { return MainTryPasteImage(); }
}

// 拖拽期间的临时接收提示：整块浅蓝底 + 虚线框 + 居中两行字。
// 不常驻、不进布局、不进底栏；松手或移出立刻 Dispose，所以主界面平时一个像素都不变。
class MainDropHint : Panel
{
    public string Line1 = "松手就开始读文件并翻译";
    public string Line2 = "";

    public MainDropHint()
    {
        Name = "dropHint";
        BackColor = QueryForm.Pal.AccentSoft;
        Cursor = Cursors.Default;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
               | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        Rectangle r = ClientRectangle;
        if (r.Width < 24 || r.Height < 24) return;
        int pad = 8;
        using (Pen p = new Pen(QueryForm.Pal.Accent, 2))
        {
            p.DashStyle = DashStyle.Dash;
            g.DrawRectangle(p, pad, pad, r.Width - pad * 2 - 1, r.Height - pad * 2 - 1);
        }
        Rectangle area = new Rectangle(pad + 12, pad + 12, r.Width - pad * 2 - 24, r.Height - pad * 2 - 24);
        if (area.Width <= 0 || area.Height <= 0) return;
        using (Font f1 = new Font("Microsoft YaHei UI", 13f, FontStyle.Bold))
        using (Font f2 = new Font("Microsoft YaHei UI", 9f))
        {
            Size s1 = TextRenderer.MeasureText(Line1, f1);
            Size s2 = (Line2 != null && Line2.Length > 0) ? TextRenderer.MeasureText(Line2, f2) : Size.Empty;
            int totalH = s1.Height + (s2.Height > 0 ? 6 + s2.Height : 0);
            int y = area.Y + Math.Max(0, (area.Height - totalH) / 2);
            TextRenderer.DrawText(g, Line1, f1, new Rectangle(area.X, y, area.Width, s1.Height),
                QueryForm.Pal.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
            if (s2.Height > 0)
                TextRenderer.DrawText(g, Line2, f2, new Rectangle(area.X, y + s1.Height + 6, area.Width, s2.Height + 2),
                    QueryForm.Pal.InkOnSoft, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis);
        }
    }
}
