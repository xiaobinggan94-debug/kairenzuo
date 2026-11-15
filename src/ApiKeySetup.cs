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
        // v1.0.0：换了服务商之后 400 十有八九是模型名不对（地址通了、Key 也认，就是没有这个模型）。
        // 404 同理——地址少写一段或者多写一段，入口根本没落在 /v1 上。
        if (status == 400 || status == 404
            || b.IndexOf("model") >= 0 && (b.IndexOf("not exist") >= 0 || b.IndexOf("not found") >= 0 || b.IndexOf("invalid") >= 0))
            return "地址通了，但模型名可能不对（填对方家的模型名再试一次）";
        return "没试成，对方返回 " + status;
    }

    // ====== 三、真发一个「只要 1 个 token」的请求去验证 ======
    // 放在后台线程里跑，主窗不会僵住；最长等 15 秒。
    internal static string TestApiKeyOnline(string key)
    {
        return TestApiKeyOnline(key, "", "");
    }

    // v1.0.0：测的必须是「窗里这一刻填的」地址和模型，而不是已经存好的那份。
    // 不然刚填好别家地址点「测一下」，它还在拿 DeepSeek 的门口试，怎么都是 401。
    // 两个 override 传空 = 用 ini 里存好的（再空就是 DeepSeek 官方那套）。
    // v1.0.0：出网络异常时留一句原始原因（只给自检和日志看，界面上还是说人话）
    internal static string LastTestError = "";

    internal static string TestApiKeyOnline(string key, string baseOverride, string modelOverride)
    {
        LastTestError = "";
        string apiBase = (baseOverride == null || baseOverride.Trim().Length == 0) ? API_BASE : ResolveApiBase(baseOverride);
        string model = (modelOverride == null || modelOverride.Trim().Length == 0) ? MODEL : ResolveApiModel(modelOverride);
        try
        {
            string body = "{\"model\":\"" + model + "\","
                        + "\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],"
                        + "\"max_tokens\":1,\"stream\":false}";
            byte[] data = Encoding.UTF8.GetBytes(body);
            HttpWebRequest req = CreateJsonPost(apiBase + "/chat/completions", key, data);
            req.Timeout = 15000;
            // v1.0.0 修：以前这里只标了 ContentLength，却从来没把正文写出去，
            // 结果「测一下」每次都抛 ProtocolViolationException，界面上显示成"连不上网"。
            using (Stream up = req.GetRequestStream()) up.Write(data, 0, data.Length);
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
            LastTestError = apiBase + " | " + wex.Message;
            return KeyVerdict(0, wex.Message);
        }
        catch (Exception ex) { LastTestError = apiBase + " | " + ex.GetType().Name + ": " + ex.Message; return KeyVerdict(0, ex.Message); }
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
        internal TextBox BoxBase;    // v1.0.0：服务地址（留空 = DeepSeek 官方）
        internal TextBox BoxModel;   // v1.0.0：模型名（留空 = deepseek-flash）
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

        Label AddText(string text, int x, int y, int w, int h, float size, bool bold, Color color)
        {
            Label lb = new Label();
            lb.Text = text;
            lb.Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
            lb.ForeColor = color;
            lb.AutoSize = false;
            lb.TextAlign = ContentAlignment.TopLeft;
            lb.UseMnemonic = false;
            lb.Location = new Point(S(x), S(y));
            lb.Size = new Size(S(w), S(h));
            Controls.Add(lb);
            return lb;
        }

        // 说明文字一律走这个：按可用宽度自己换行，高度按真算出来的行数给。
        // 老写法是固定 420×20 的单行 Label，句子一长尾巴就被切掉（"…不会写进任何代" 那种），
        // 用户看到的就是"这字怎么没了"。
        Label AddWrap(string text, int x, int y, int w, float size, bool bold, Color color)
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
            int hpx = Math.Max(need.Height + S(4), S(18));
            lb.Size = new Size(wpx, hpx);
            Controls.Add(lb);
            return lb;
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

            // 从上往下流式排：每一块的高度都是量出来的，句子再长也只是把窗口往下撑，
            // 不会再出现"字到这儿就被切掉"。底栏 y 由前面累计出来，不写死。
            int y = 14;
            Label t1 = AddWrap(firstTime ? "先填一个 API Key，就能开始用了" : "换个 API Key",
                               20, y, 420, 12.5f, true, Pal.Ink);
            y = t1.Bottom + S(4);
            Label t2 = AddWrap("填这一次就好，以后不用再管。", 20, y, 420, 8.5f, false, Pal.InkMuted);
            y = t2.Bottom + S(12);

            Panel hair1 = new Panel();
            hair1.BackColor = Pal.Hair;
            hair1.Location = new Point(0, S(y));
            hair1.Size = new Size(S(460), S(1));
            Controls.Add(hair1);
            y += S(1) + S(12);

            Label note = AddWrap("它只存在你自己电脑上，用来让程序去问翻译；不会上传，也不会写进任何代码里。",
                                 20, y, 420, 8.8f, false, Pal.InkSecondary);
            y = note.Bottom + S(14);

            Label lbKey = AddText("Key", 20, y + S(4), 34, 22, 9.5f, false, Pal.InkSecondary);
            Box = new TextBox();
            Box.Font = new Font("Consolas", 10f);
            Box.Location = new Point(S(56), S(y));
            Box.Width = S(384);
            Box.BorderStyle = BorderStyle.FixedSingle;
            Box.BackColor = Color.White;
            Box.ForeColor = Pal.Ink;
            Box.Tag = "apikey输入";
            Controls.Add(Box);
            y = Box.Bottom + S(10);

            Label hint = AddWrap("形如 sk- 开头的一长串。粘完瞄一眼前后有没有多带的空格或换行，剩下的它自己会挑。",
                                 20, y, 420, 8.5f, false, Pal.InkMuted);
            y = hint.Bottom + S(6);
            // 路径可能很长，同样换行显示，不再腰斩成 "sqk_m..."
            Label pathLb = AddWrap("存在：" + ApiKeyFile, 20, y, 420, 8f, false, Pal.InkFaint);
            y = pathLb.Bottom + S(8);

            // ---- v1.0.0 换服务商（可选）：留空 = DeepSeek 官方；不换的人这段当没看过 ----
            Panel hairSvc = new Panel();
            hairSvc.BackColor = Pal.Hair;
            hairSvc.Location = new Point(0, S(y));
            hairSvc.Size = new Size(S(460), S(1));
            Controls.Add(hairSvc);
            y += S(1) + S(12);

            Label svcTip = AddWrap("想换别家？下面两个填上就行，留空还是 DeepSeek 官方那套。",
                                   20, y, 420, 8.5f, false, Pal.InkMuted);
            y = svcTip.Bottom + S(8);

            Label lbBase = AddText("地址", 20, y + S(3), 34, 20, 8.8f, false, Pal.InkSecondary);
            BoxBase = new TextBox();
            BoxBase.Font = new Font("Consolas", 9.5f);
            BoxBase.Location = new Point(S(56), S(y));
            BoxBase.Width = S(384);
            BoxBase.BorderStyle = BorderStyle.FixedSingle;
            BoxBase.BackColor = Color.White;
            BoxBase.ForeColor = Pal.Ink;
            BoxBase.Tag = "服务地址输入";
            BoxBase.Text = GetSet("api_base", "");
            Controls.Add(BoxBase);
            y = BoxBase.Bottom + S(6);

            Label lbModel = AddText("模型", 20, y + S(3), 34, 20, 8.8f, false, Pal.InkSecondary);
            BoxModel = new TextBox();
            BoxModel.Font = new Font("Consolas", 9.5f);
            BoxModel.Location = new Point(S(56), S(y));
            BoxModel.Width = S(384);
            BoxModel.BorderStyle = BorderStyle.FixedSingle;
            BoxModel.BackColor = Color.White;
            BoxModel.ForeColor = Pal.Ink;
            BoxModel.Tag = "模型名输入";
            BoxModel.Text = GetSet("api_model", "");
            Controls.Add(BoxModel);
            y = BoxModel.Bottom + S(6);

            Label svcHint = AddWrap("Kimi / 通义 / 智谱 / 硅基流动 / OpenRouter 都行（Key 得是对应那家的）。改完先点「测一下」，通了再保存。",
                                    20, y, 420, 8f, false, Pal.InkFaint);
            y = svcHint.Bottom + S(8);

            Status = new Label();
            Status.Text = "";
            Status.Font = new Font("Microsoft YaHei UI", 8.8f);
            Status.ForeColor = Pal.InkMuted;
            Status.AutoSize = false;
            Status.TextAlign = ContentAlignment.MiddleLeft;
            Status.Location = new Point(S(20), S(y));
            Status.Size = new Size(S(420), S(22));
            Status.Tag = "测试结果";
            Controls.Add(Status);
            y = Status.Bottom + S(10);

            Panel hair2 = new Panel();
            hair2.BackColor = Pal.Hair;
            hair2.Location = new Point(0, S(y));
            hair2.Size = new Size(S(460), S(1));
            Controls.Add(hair2);
            y += S(1) + S(14);

            int btnY = y;
            BtnTest = MakeBtn("测一下", 84, false);
            BtnTest.Location = new Point(S(20), S(btnY));
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
                // v1.0.0：拿框里这一刻的地址/模型去试，不是存过的那份
                string bTest = (BoxBase.Text ?? "").Trim();
                string mTest = (BoxModel.Text ?? "").Trim();
                Thread t = new Thread(delegate ()
                {
                    string v = TestApiKeyOnline(k, bTest, mTest);
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
            BtnLater.Location = new Point(S(274), S(btnY));
            BtnLater.DialogResult = DialogResult.Cancel;
            Controls.Add(BtnLater);

            BtnSave = MakeBtn("保存", 80, true);
            BtnSave.Location = new Point(S(360), S(btnY));
            BtnSave.Click += delegate
            {
                string msg;
                if (!SaveKeyText(Box.Text, out msg))
                {
                    Say(msg, false);
                    Box.Focus();
                    return;
                }
                // v1.0.0：地址和模型一起存进 settings.ini。留空也照存（空就是恢复默认），
                // 清了框点保存就等于退回 DeepSeek 官方，不用手改文件。
                if (!selfTestMode)
                {
                    SetSet("api_base", (BoxBase.Text ?? "").Trim());
                    SetSet("api_model", (BoxModel.Text ?? "").Trim());
                }
                SavedKey = CleanKeyText(Box.Text);
                Saved = true;
                Say("存好了，回到小窗点「查一下」就行", true);
                DialogResult = DialogResult.OK;
                Close();
            };
            Controls.Add(BtnSave);

            // 窗高跟着内容走：最后一行按钮的底 + 一圈留白。文字换行变多，窗自己往下长。
            ClientSize = new Size(S(460), S(btnY) + S(30) + S(20));

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
