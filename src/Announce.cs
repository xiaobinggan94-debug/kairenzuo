// ====== C1：程序内公告卡（零服务器）======
// 目的：这一版改了什么、有什么坑要躲，能当场告诉用的人。
// 做法：不做任何联网推送、不挂任何后台服务，公告就是一个本地文本文件：
//
//     %APPDATA%\SolivineQuickLook\公告.txt
//
// 第一行是非空行 = 标题，剩下的全部是正文。改完存一下，下次启动就会弹一次。
// 认「内容指纹」而不是认时间：同一份公告只弹一次，改一个字就重新弹，
// 已经弹过的那份不会再骚扰。想立刻再看一遍：更多 ▾ → 公告…（这一版有什么变化）。
//
// 为什么不做服务器推送：这程序是绿色 exe，没有分发渠道，也没有谁在后台运维。
// 真挂个公告接口，只会多一个连不上就报错的地方，还得防着被当成可疑流量。
// 本地文件足够，而且她自己就能改。
using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

partial class QueryForm
{
    internal const string ANNOUNCE_KEY = "announce_seen";

    internal static string AnnouncePath
    {
        get { return Path.Combine(LOG_DIR, "公告.txt"); }
    }

    // 文件不存在时先铺一份，省得她还要自己去猜格式
    internal static void EnsureAnnounceFile()
    {
        try
        {
            if (File.Exists(AnnouncePath)) return;
            string demo =
                "这一版有什么变化\r\n" +
                "\r\n" +
                "这张是「程序内公告卡」，本地文件驱动，不联网、不推送。\r\n" +
                "第一行当标题，下面都算正文，改这个文件就换内容：\r\n" +
                "\r\n" +
                AnnouncePath + "\r\n" +
                "\r\n" +
                "· 同一份内容只弹一次；改一个字，下次启动重新弹。\r\n" +
                "· 想立刻再看：更多 ▾ → 公告…（这一版有什么变化）。\r\n" +
                "· 不想看它自动弹：把文件清空，或者删掉。";
            File.WriteAllText(AnnouncePath, demo, new UTF8Encoding(false));
        }
        catch { }
    }

    // 读公告：首行标题，其余正文。文件没了就返回空，调用方自己决定弹不弹
    internal static bool ReadAnnounce(out string title, out string body)
    {
        return ReadAnnounceAt(AnnouncePath, out title, out body);
    }

    // 指定路径版本：自检只读自己临时目录里那份，绝不碰真实「公告.txt」
    internal static bool ReadAnnounceAt(string path, out string title, out string body)
    {
        title = "";
        body = "";
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            string raw = File.ReadAllText(path, Encoding.UTF8);
            raw = raw.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
            if (raw.Trim().Length == 0) return false;
            int nl = raw.IndexOf('\n');
            if (nl < 0) { title = raw.Trim(); return title.Length > 0; }
            title = raw.Substring(0, nl).Trim();
            body = raw.Substring(nl + 1).Trim('\n');
            // 标题空着（第一行就是个空行）就退回一句通用的，别让卡片顶上留一条白
            if (title.Length == 0) title = "这一版有什么变化";
            return true;
        }
        catch { return false; }
    }

    // 内容指纹：只用来判断「是不是同一份公告」，不做任何校验用途。
    // FNV-1a 32 位，短、无依赖、同一份内容稳定
    internal static string AnnounceFingerprint(string s)
    {
        if (s == null) s = "";
        uint h = 2166136261;
        for (int i = 0; i < s.Length; i++)
        {
            h ^= (byte)s[i];
            h *= 16777619;
            h ^= (byte)(s[i] >> 8);
            h *= 16777619;
        }
        return s.Length.ToString() + "-" + h.ToString("X8");
    }

    internal static string CurrentAnnounceFingerprint()
    {
        return AnnounceFingerprintOfFile(AnnouncePath);
    }

    // 指定文件的指纹：读不出来（没文件 / 空文件）就给空串，表示「没公告」
    internal static string AnnounceFingerprintOfFile(string path)
    {
        string t, b;
        if (!ReadAnnounceAt(path, out t, out b)) return "";
        return AnnounceFingerprint(t + "\n" + b);
    }

    // 有没有「没弹过的新公告」
    internal static bool AnnouncePending()
    {
        try
        {
            string fp = CurrentAnnounceFingerprint();
            if (fp.Length == 0) return false;              // 空文件 / 没文件：不打扰
            return GetSet(ANNOUNCE_KEY, "") != fp;
        }
        catch { return false; }
    }

    internal static void MarkAnnounceSeen()
    {
        try
        {
            string fp = CurrentAnnounceFingerprint();
            if (fp.Length == 0) return;
            if (selfTestMode) SetSetQuiet(ANNOUNCE_KEY, fp);
            else SetSet(ANNOUNCE_KEY, fp);
        }
        catch { }
    }

    // ====== 公告卡本体 ======
    // 尺寸、字体、圆角按钮全部跟「第一次用它」那张卡一个模子，不新造一套视觉
    internal class AnnounceForm : Form
    {
        float dpiScale = 1f;
        internal string TitleText = "";
        internal string BodyText = "";
        internal string FilePath = "";

        int S(int v) { return (int)Math.Round(v * dpiScale); }

        RoundButton MakeBtn(string text, int w, bool primary)
        {
            RoundButton b = new RoundButton();
            b.Text = text;
            b.Size = new Size(S(w), S(30));
            b.Radius = S(8);
            b.BackColor = Pal.Surface;
            b.Font = new Font("Microsoft YaHei UI", 9.5f);
            b.Cursor = Cursors.Hand;
            if (primary)
            {
                b.FillColor = Pal.Accent;
                b.HoverColor = Pal.AccentHover;
                b.ForeColor = Pal.OnAccent;
            }
            else
            {
                b.FillColor = Pal.Surface;
                b.HoverColor = Pal.AccentTint;
                b.ForeColor = Pal.InkSecondary;
            }
            return b;
        }

        // 文字按宽度自己换行、高度量出来给多少算多少，调用方用返回的 Bottom 往下排；
        // 老写法固定高度会把长句尾巴切掉（用户只能看到半句话）。
        Label AddFlow(string text, int x, int y, int w, float size, bool bold, Color color)
        {
            Label lb = new Label();
            lb.Text = text;
            lb.Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
            lb.ForeColor = color;
            lb.AutoSize = false;
            lb.TextAlign = ContentAlignment.TopLeft;
            lb.UseMnemonic = false;
            lb.Location = new Point(S(x), S(y));
            int wpx = S(w);
            Size need = TextRenderer.MeasureText(text, lb.Font, new Size(wpx, 4000),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding);
            lb.Size = new Size(wpx, Math.Max(need.Height + S(4), S(18)));
            Controls.Add(lb);
            return lb;
        }

        public AnnounceForm(string title, string body, string filePath)
        {
            TitleText = title;
            BodyText = body;
            FilePath = filePath;

            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) dpiScale = g.DpiX / 96f;
            }
            catch { dpiScale = 1f; }
            if (dpiScale < 1f || dpiScale > 3f) dpiScale = 1f;

            Text = "公告";
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Pal.Surface;
            ForeColor = Pal.Ink;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            ClientSize = new Size(S(460), S(376));

            // 标题和那行说明都改成按内容量高，正文框保持固定高度（它自己有滚动条），
            // 后面所有控件接在正文框底下往下排。
            int y = 14;
            y = AddFlow(title, 20, y, 420, 12.5f, true, Pal.Ink).Bottom + S(4);
            y = AddFlow("改这个小文件就能换内容：公告.txt（双击下面「打开公告文件」也行）", 20, y, 420, 8.5f, false, Pal.InkMuted).Bottom + S(10);

            Panel hair1 = new Panel();
            hair1.BackColor = Pal.Hair;
            hair1.Location = new Point(0, S(y));
            hair1.Size = new Size(S(460), S(1));
            Controls.Add(hair1);
            y += S(1) + S(10);

            RichTextBox tb = new RichTextBox();
            tb.Text = body;
            tb.ReadOnly = true;
            tb.BorderStyle = BorderStyle.None;
            tb.BackColor = Pal.Surface;
            tb.ForeColor = Pal.InkSecondary;
            tb.Font = new Font("Microsoft YaHei UI", 9.5f);
            tb.ScrollBars = RichTextBoxScrollBars.Vertical;
            tb.Location = new Point(S(20), S(y));
            tb.Size = new Size(S(420), S(206));
            tb.Tag = "公告正文";
            Controls.Add(tb);
            y = tb.Bottom + S(12);

            Panel hair2 = new Panel();
            hair2.BackColor = Pal.Hair;
            hair2.Location = new Point(0, S(y));
            hair2.Size = new Size(S(460), S(1));
            Controls.Add(hair2);
            y += S(1) + S(14);

            RoundButton btnFile = MakeBtn("打开公告文件", 118, false);
            btnFile.Location = new Point(S(20), S(y));
            btnFile.Click += delegate
            {
                try
                {
                    EnsureAnnounceFile();
                    System.Diagnostics.Process.Start("notepad.exe", "\"" + AnnouncePath + "\"");
                }
                catch (Exception ex) { MessageBox.Show(this, "没打开：" + ex.Message, "没事"); }
            };
            Controls.Add(btnFile);

            RoundButton ok = MakeBtn("知道了", 80, true);
            ok.Location = new Point(S(360), S(y));
            ok.DialogResult = DialogResult.OK;
            Controls.Add(ok);
            y = ok.Bottom + S(16);

            ClientSize = new Size(S(460), S(y));

            AcceptButton = ok;
            CancelButton = ok;
        }
    }

    internal Form BuildAnnounceForm()
    {
        EnsureAnnounceFile();
        return BuildAnnounceForm(AnnouncePath);
    }

    // 指定文件版本：自检拿临时文件建卡，不碰真实公告
    internal Form BuildAnnounceForm(string path)
    {
        string t, b;
        if (!ReadAnnounceAt(path, out t, out b)) { t = "这一版有什么变化"; b = "公告文件是空的，或者还没建。"; }
        return new AnnounceForm(t, b, path);
    }

    // 自检用：把卡上能看到的文字逐条导出来
    internal string DescribeAnnounce(Form f)
    {
        StringBuilder sb = new StringBuilder();
        AnnounceForm af = f as AnnounceForm;
        if (af == null) return "不是公告卡\r\n";
        sb.AppendLine("== 公告卡上能看到的文字 ==");
        sb.AppendLine("  [标题] " + af.TitleText);
        foreach (Control c in af.Controls)
        {
            Label lb = c as Label;
            RichTextBox rt = c as RichTextBox;
            Button bt = c as Button;
            if (lb != null) sb.AppendLine("  [文字] " + lb.Text);
            else if (rt != null) sb.AppendLine("  [" + (rt.Tag == null ? "?" : rt.Tag.ToString()) + "] " + rt.Text.Replace("\r\n", " / "));
            else if (bt != null) sb.AppendLine("  [按钮] " + bt.Text);
        }
        sb.AppendLine("公告文件   = " + af.FilePath);
        sb.AppendLine("客户区     = " + af.ClientSize);
        return sb.ToString();
    }

    // 弹公告卡。force=true 是不看指纹的「我要现在再看一遍」
    internal void ShowAnnounce(bool force)
    {
        string t, b;
        bool has = ReadAnnounce(out t, out b);
        if (!has && !force) return;
        try
        {
            using (AnnounceForm f = new AnnounceForm(has ? t : "这一版有什么变化",
                                                     has ? b : "公告文件是空的，或者还没建。点下面「打开公告文件」写点东西进去。",
                                                     AnnouncePath))
            {
                f.ShowDialog(this);
            }
        }
        catch { }
        // 只有真弹过才盖指纹：中途崩了下次还能再弹
        MarkAnnounceSeen();
    }
}
