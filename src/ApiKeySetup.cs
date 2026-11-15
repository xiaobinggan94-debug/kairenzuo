// ApiKeySetup.cs —— v2.5.9 第一档 ①：首启填 Key 小窗（不用再手工建文件）
//
// 改之前：程序发现没有 apikey.txt，只会在正文里写「先去这个文件夹、新建文件、改扩展名、粘一行」
//         四步说明。对不熟电脑的人来说，找 %APPDATA% 再改扩展名，比翻译本身还难。
// 改之后：第一次打开（或从托盘右键菜单点）弹一个小窗：粘贴 Key → 点「测一下」→「保存」，
//         存好立刻生效，不用重启、不用动人任何文件夹。
//
// ★ 只新增一个独立模态小窗 + 三条辅助函数，主界面的控件一个都没碰
//   （验收标准：--audit 的控件树和改造前逐字节相同）。
// ★ 所有自检分支里 selfTestMode 为真时绝不弹这个窗：模态窗撞上自检里的
//   Application.DoEvents() 会把自检线程永久卡住（2026-11-05 踩过一次，记住了）。

using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;

partial class QueryForm
{
    internal const string KEY_PROMPT_KEY = "key_prompt_done";

    // ====== 一、把粘进来的一坨文字，洗成真正能用的 key ======
    // 用户常见的粘法：前后带空格、从网页复制时带上说明文字、粘了两行、带了中文引号。
    // 规则和以前读 apikey.txt 时一致：先找 sk- 开头的；找不到（换了别家兼容服务）再找
    // 第一段「够长且全是字母数字横线下划线点」的，避免把标点或汉字当成 key。
    internal static string CleanKeyText(string raw)
    {
        if (raw == null) return "";
        string s = raw.Replace("\uFEFF", "").Trim();
        if (s.Length == 0) return "";
        string[] parts = s.Split(new char[] { ' ', '\t', '\r', '\n', '"', '\'', '，', ',', '；', ';', '：', ':', '、', '。' },
                                 StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].StartsWith("sk-", StringComparison.OrdinalIgnoreCase)) return parts[i];
        }
        for (int i = 0; i < parts.Length; i++)
        {
            string p = parts[i];
            if (p.Length < 16) continue;
            bool ok = true;
            for (int j = 0; j < p.Length; j++)
            {
                char c = p[j];
                bool ascii = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
                if (!ascii && c != '-' && c != '_' && c != '.') { ok = false; break; }
            }
            if (ok) return p;
        }
        return "";
    }

    // ====== 二、「测一下」的判定：只把返回码和返回体重说成人话，不联网也能考 ======
    internal static string KeyVerdict(int status, string body)
    {
        string b = body == null ? "" : body.ToLowerInvariant();
        if (status >= 200 && status < 300) return "通了，这个 Key 能用";
        if (status == 401 || status == 403
            || b.IndexOf("invalid_api_key") >= 0
            || b.IndexOf("incorrect api key") >= 0
            || b.IndexOf("authentication") >= 0
            || b.IndexOf("api key") >= 0 && b.IndexOf("invalid") >= 0)
            return "Key 不对（写错了、过期了，或者复制时多带了空格换行）";
        if (status == 402 || b.IndexOf("insufficient") >= 0 || b.IndexOf("balance") >= 0)
            return "Key 是对的，但余额/额度用完了";
        if (status == 429) return "对方的服务器说「问得太频繁」，等一分钟再试";
        if (status >= 500) return "对方服务器忙（不是你的问题），过一会儿再试";
        if (status == 0) return "连不上网（先确认电脑能上网；挂着代理的话看是不是代理的事）";
        return "没试成，对方返回 " + status;
    }

    // ====== 三、真发一个「只要 1 个 token」的请求去验证 ======
    // 放在后台线程里跑，主窗不会僵住；最长等 15 秒。
    internal static string TestApiKeyOnline(string key)
    {
        try
        {
            string body = "{\"model\":\"" + MODEL + "\","
                        + "\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],"
                        + "\"max_tokens\":1,\"stream\":false}";
            byte[] data = Encoding.UTF8.GetBytes(body);
            HttpWebRequest req = CreateJsonPost(API_BASE + "/chat/completions", key, data);
            req.Timeout = 15000;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            {
                string txt = "";
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) txt = sr.ReadToEnd();
                return KeyVerdict((int)resp.StatusCode, txt);
            }
        }
        catch (WebException wex)
        {
            HttpWebResponse r = wex.Response as HttpWebResponse;
            if (r != null)
            {
                string txt = "";
                int code = 0;
                try { code = (int)r.StatusCode; } catch { }
                try { using (StreamReader sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8)) txt = sr.ReadToEnd(); } catch { }
                try { r.Close(); } catch { }
                return KeyVerdict(code, txt);
            }
            return KeyVerdict(0, wex.Message);
        }
        catch (Exception ex) { return KeyVerdict(0, ex.Message); }
    }

    // ====== 四、保存：先备份旧的，再写一行，写完立刻生效 ======
    internal static bool SaveKeyText(string rawKey, out string msg)
    {
        return SaveKeyTextTo(ApiKeyFile, rawKey, out msg);
    }

    // 单独给出「存到哪个文件」，自检就能存到临时目录，不会动你真实的 apikey.txt
    internal static bool SaveKeyTextTo(string path, string rawKey, out string msg)
    {
        msg = "";
        string k = CleanKeyText(rawKey);
        if (k.Length == 0) { msg = "还没填 Key（看起来不像一串 Key）"; return false; }
        try
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(path))
            {
                // 覆盖前留一份，万一原来的 Key 还能用、你手滑存错了，能翻回来
                try { File.Copy(path, path + ".bak_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"), true); } catch { }
            }
            File.WriteAllText(path, k + "\r\n", new UTF8Encoding(false));
            // 清掉内存里的旧值：存完立刻生效，不用重启（只对真实那个文件做）
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(ApiKeyFile), StringComparison.OrdinalIgnoreCase))
            {
                apiKeyCache = null;
                Log("API Key 已保存（长度 " + k.Length + "）");
            }
            msg = "存好了";
            return true;
        }
        catch (Exception ex) { msg = "存不进去：" + ex.Message; return false; }
    }

    internal static bool KeyPromptDone() { return GetSet(KEY_PROMPT_KEY, "0") == "1"; }

    internal static void MarkKeyPromptDone()
    {
        // 和首启指路窗一个规矩：自检时绝不写用户真实的 settings.ini
        try
        {
            if (selfTestMode) SetSetQuiet(KEY_PROMPT_KEY, "1");
            else SetSet(KEY_PROMPT_KEY, "1");
        }
        catch { }
    }

    // ====== 五、小窗本体 ======
    internal class ApiKeyForm : Form
    {
        float dpiScale = 1f;
        internal TextBox Box;
        internal Label Status;
        RoundButton BtnTest;
        RoundButton BtnSave;
        RoundButton BtnLater;
        internal string SavedKey = "";
        internal bool Saved = false;

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

        void AddText(string text, int x, int y, int w, int h, float size, bool bold, Color color)
        {
            Label lb = new Label();
            lb.Text = text;
            lb.Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
            lb.ForeColor = color;
            lb.AutoSize = false;
            lb.TextAlign = ContentAlignment.TopLeft;
            lb.Location = new Point(S(x), S(y));
            lb.Size = new Size(S(w), S(h));
            Controls.Add(lb);
        }

        public ApiKeyForm(bool firstTime)
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) dpiScale = g.DpiX / 96f;
            }
            catch { dpiScale = 1f; }
            if (dpiScale < 1f || dpiScale > 3f) dpiScale = 1f;

            Text = "填 API Key";
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Pal.Surface;
            ForeColor = Pal.Ink;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            ClientSize = new Size(S(460), S(272));

            AddText(firstTime ? "先填一个 API Key，就能开始用了" : "换个 API Key", 20, 14, 420, 26, 12.5f, true, Pal.Ink);
            AddText("填这一次就好，以后不用再管。", 20, 42, 420, 20, 8.5f, false, Pal.InkMuted);

            Panel hair1 = new Panel();
            hair1.BackColor = Pal.Hair;
            hair1.Location = new Point(0, S(68));
            hair1.Size = new Size(S(460), S(1));
            Controls.Add(hair1);

            AddText("它只存在你自己电脑上，用来让程序去问翻译；不会上传，也不会写进任何代码里。",
                    20, 80, 420, 20, 8.8f, false, Pal.InkSecondary);

            AddText("Key", 20, 112, 34, 22, 9.5f, false, Pal.InkSecondary);
            Box = new TextBox();
            Box.Font = new Font("Consolas", 10f);
            Box.Location = new Point(S(56), S(108));
            Box.Width = S(384);
            Box.BorderStyle = BorderStyle.FixedSingle;
            Box.BackColor = Color.White;
            Box.ForeColor = Pal.Ink;
            Box.Tag = "apikey输入";
            Controls.Add(Box);

            AddText("形如 sk- 开头的一长串。粘完瞄一眼前后有没有多带的空格或换行，剩下的它自己会挑。",
                    20, 138, 420, 20, 8.5f, false, Pal.InkMuted);
            AddText("存在：" + ApiKeyFile, 20, 158, 420, 18, 8f, false, Pal.InkFaint);

            Status = new Label();
            Status.Text = "";
            Status.Font = new Font("Microsoft YaHei UI", 8.8f);
            Status.ForeColor = Pal.InkMuted;
            Status.AutoSize = false;
            Status.TextAlign = ContentAlignment.MiddleLeft;
            Status.Location = new Point(S(20), S(180));
            Status.Size = new Size(S(420), S(22));
            Status.Tag = "测试结果";
            Controls.Add(Status);

            Panel hair2 = new Panel();
            hair2.BackColor = Pal.Hair;
            hair2.Location = new Point(0, S(210));
            hair2.Size = new Size(S(460), S(1));
            Controls.Add(hair2);

            BtnTest = MakeBtn("测一下", 84, false);
            BtnTest.Location = new Point(S(20), S(226));
            BtnTest.Click += delegate
            {
                string k = CleanKeyText(Box.Text);
                if (k.Length == 0)
                {
                    Say("先把 Key 粘进上面的框里", false);
                    Box.Focus();
                    return;
                }
                BtnTest.Enabled = false;
                Say("正在试…（最多等 15 秒）", false);
                Thread t = new Thread(delegate ()
                {
                    string v = TestApiKeyOnline(k);
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            bool good = v.StartsWith("通了");
                            Say(v, good);
                            BtnTest.Enabled = true;
                        });
                    }
                    catch { }
                });
                t.IsBackground = true;
                t.Start();
            };
            Controls.Add(BtnTest);

            BtnLater = MakeBtn("先不配", 76, false);
            BtnLater.Location = new Point(S(274), S(226));
            BtnLater.DialogResult = DialogResult.Cancel;
            Controls.Add(BtnLater);

            BtnSave = MakeBtn("保存", 80, true);
            BtnSave.Location = new Point(S(360), S(226));
            BtnSave.Click += delegate
            {
                string msg;
                if (!SaveKeyText(Box.Text, out msg))
                {
                    Say(msg, false);
                    Box.Focus();
                    return;
                }
                SavedKey = CleanKeyText(Box.Text);
                Saved = true;
                Say("存好了，回到小窗点「查一下」就行", true);
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(BtnSave);

            AcceptButton = BtnSave;
            CancelButton = BtnLater;
        }

        internal void Say(string text, bool good)
        {
            Status.Text = text;
            Status.ForeColor = good ? Pal.Accent : Pal.InkMuted;
        }
    }

    // ====== 六、给自检用的入口（自检里绝不 ShowDialog）======
    internal Form BuildApiKeyForm(bool firstTime)
    {
        return new ApiKeyForm(firstTime);
    }

    internal string DescribeApiKey(Form f)
    {
        StringBuilder sb = new StringBuilder();
        ApiKeyForm kf = f as ApiKeyForm;
        if (kf == null) return "不是填 Key 小窗\r\n";
        sb.AppendLine("== 窗口里能看到的文字 ==");
        foreach (Control c in kf.Controls)
        {
            Label lb = c as Label;
            TextBox tb = c as TextBox;
            Button bt = c as Button;
            if (lb != null) sb.AppendLine("  [文字] " + lb.Text);
            else if (tb != null) sb.AppendLine("  [" + (tb.Tag == null ? "?" : tb.Tag.ToString()) + "] 宽 " + tb.Width);
            else if (bt != null) sb.AppendLine("  [按钮] " + bt.Text);
        }
        sb.AppendLine("客户区     = " + kf.ClientSize);
        sb.AppendLine("apikey.txt = " + ApiKeyFile);
        sb.AppendLine("已配置     = " + (ApiKey.Length > 0 ? "是（长 " + ApiKey.Length + "）" : "否"));
        return sb.ToString();
    }

    // 首次缺 Key 时调用；关掉之后记一笔，以后不再自动弹（想要还能从托盘菜单点开）
    internal void ShowApiKeySetup()
    {
        if (selfTestMode) return;
        try
        {
            using (ApiKeyForm f = new ApiKeyForm(ApiKey.Length == 0))
            {
                f.ShowDialog(this);
                if (f.Saved && f.SavedKey.Length > 0 && tip != null)
                    tip.Text = "API Key 存好了，点「查一下」试试";
            }
        }
        catch { }
        MarkKeyPromptDone();
    }
}
