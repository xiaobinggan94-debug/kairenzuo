// ====== v2.5.5 第一档 ⑨ + ③：第一次打开时的「指路窗」 ======
// ⑨ 装完找不到程序：
//   原来装好双击一次，小窗自己弹一下就没影了，之后再想找它：
//   · 桌面没有图标（我们从来没建过快捷方式）
//   · 开始菜单也搜不出来（是绿色 exe，不在 Program Files、没注册）
//   · 有人干脆以为装坏了，又去重新下一份
// ③ 杀毒误杀：
//   绿色 exe + 读选中文字 + 截屏 OCR 这个组合，很容易被国产杀软当可疑文件静默隔离，
//   用户看到的只是一句「它突然不见了」，完全不知道去隔离区恢复。
//
// 所以首次打开弹这张小卡片，一次说清三件事：它在托盘 / 怎么在桌面放个图标 /
// 哪天它不见了去哪儿找回来。只在真正第一次打开时弹，之后不再打扰
// （settings.ini 里 first_run_done=1）。想再看一遍：托盘菜单「关于」下面有入口。
using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

partial class QueryForm
{
    internal const string FIRST_RUN_KEY = "first_run_done";

    internal static bool FirstRunDone() { return GetSet(FIRST_RUN_KEY, "0") == "1"; }

    internal static void MarkFirstRunDone()
    {
        // 自检时绝不写用户真实的 settings.ini
        if (selfTestMode) SetSetQuiet(FIRST_RUN_KEY, "1");
        else SetSet(FIRST_RUN_KEY, "1");
    }

    // 在指定目录（null=真实桌面）放一个指向当前 exe 的快捷方式。
    // 用 WScript.Shell 反射创建，不额外引 Microsoft.CSharp（csc 引用表保持不动）。
    internal static string CreateShortcutIn(string dirOrNull, string hotkeyLabel, out string err)
    {
        err = "";
        try
        {
            string dir = string.IsNullOrEmpty(dirOrNull)
                ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
                : dirOrNull;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string exe = Application.ExecutablePath;
            string lnk = Path.Combine(dir, Path.GetFileNameWithoutExtension(exe) + ".lnk");

            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) { err = "这台机器上没有 WScript.Shell，建不了快捷方式"; return ""; }
            object sh = Activator.CreateInstance(shellType);

            object sc = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
            if (sc == null) { err = "系统不让建快捷方式"; return ""; }
            Type st = sc.GetType();
            st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { exe });
            st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(exe) });
            st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { exe + ",0" });
            st.InvokeMember("Description", BindingFlags.SetProperty, null, sc,
                new object[] { "选中文字按 " + hotkeyLabel + " 就查" });
            st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
            return lnk;
        }
        catch (Exception ex)
        {
            err = ex.Message;
            return "";
        }
    }

    // ====== 指路窗本体 ======
    internal class FirstRunForm : Form
    {
        float dpiScale = 1f;
        internal string ExePath = "";
        internal string ChosenName = "";   // 用户在这张卡里最后定下的名字

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

        public FirstRunForm(string hotkeyLabel)
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) dpiScale = g.DpiX / 96f;
            }
            catch { dpiScale = 1f; }
            if (dpiScale < 1f || dpiScale > 3f) dpiScale = 1f;

            ExePath = Application.ExecutablePath;

            Text = "第一次用它";
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Pal.Surface;
            ForeColor = Pal.Ink;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            ClientSize = new Size(S(460), S(414));

            AddText("第一次用它，先给它起个名", 20, 14, 420, 26, 12.5f, true, Pal.Ink);
            AddText("下面三件小事也说一次，以后再打开就不弹了。", 20, 42, 420, 20, 8.5f, false, Pal.InkMuted);

            Panel hair1 = new Panel();
            hair1.BackColor = Pal.Hair;
            hair1.Location = new Point(0, S(68));
            hair1.Size = new Size(S(460), S(1));
            Controls.Add(hair1);

            // ===== 起名：窗口标题、托盘提示、关于面板、导出文件头都跟着这个名字走 =====
            // 只在真正第一次打开时问一次；关掉这张卡就等于"用这个名字"，下次不再问
            AddText("给它起个名（窗口标题和托盘上都显示这个）", 20, 78, 420, 20, 9.5f, true, Pal.Ink);
            string seeded = CleanAppName(GetSet(APP_NAME_KEY, ""));
            // 预填：自己改过就用改过的；真正的第一次打开预填中性名；老用户从菜单进来预填当前名字
            if (seeded.Length == 0) seeded = FirstRunDone() ? AppTitle : APP_NAME_HINT;
            TextBox tbName = new TextBox();
            tbName.Text = seeded;
            tbName.MaxLength = APP_NAME_MAX;
            tbName.Font = new Font("Microsoft YaHei UI", 10.5f);
            tbName.BorderStyle = BorderStyle.FixedSingle;
            tbName.BackColor = Pal.Surface;
            tbName.ForeColor = Pal.Ink;
            tbName.Location = new Point(S(20), S(102));
            tbName.Width = S(420);
            tbName.Tag = "软件名";
            Controls.Add(tbName);
            AddText("最多 " + APP_NAME_MAX + " 个字；清空就还叫「" + APP_TITLE + "」。",
                    20, 136, 420, 18, 8.5f, false, Pal.InkMuted);

            Panel hair0 = new Panel();
            hair0.BackColor = Pal.Hair;
            hair0.Location = new Point(0, S(158));
            hair0.Size = new Size(S(460), S(1));
            Controls.Add(hair0);

            AddText("① 关掉小窗，它没退出", 20, 170, 420, 20, 9.5f, true, Pal.Ink);
            AddText("它收在任务栏右下角（时钟旁边）的托盘里，小饼干图标。左键单击一下，小窗就回来。",
                    20, 192, 420, 22, 8.8f, false, Pal.InkSecondary);

            AddText("② 想在桌面留个图标", 20, 222, 260, 20, 9.5f, true, Pal.Ink);
            AddText("点右边这个按钮，桌面就多一个能双击打开的。", 20, 244, 300, 20, 8.8f, false, Pal.InkSecondary);

            RoundButton btnLnk = MakeBtn("放个桌面快捷方式", 148, true);
            btnLnk.Location = new Point(S(300), S(232));
            btnLnk.Click += delegate
            {
                string err;
                string lnk = CreateShortcutIn(null, hotkeyLabel, out err);
                if (lnk.Length > 0)
                {
                    btnLnk.Text = "已经放好了";
                    btnLnk.FillColor = Pal.AccentTint;
                    btnLnk.ForeColor = Pal.Accent;
                    btnLnk.Enabled = false;
                }
                else
                {
                    btnLnk.Text = "没建成，我再试一次";
                    MessageBox.Show(this, "快捷方式没建成：" + err + "\r\n不影响用，它还是在托盘里。", "没事");
                }
            };
            Controls.Add(btnLnk);

            AddText("③ 哪天它突然不见了", 20, 278, 420, 20, 9.5f, true, Pal.Ink);
            AddText("绿色 exe 加上「读你选中的文字」，很容易被杀毒软件误判成可疑文件、悄悄隔离掉。\r\n" +
                    "真遇上：打开杀毒软件的「隔离区 / 恢复区」，把它恢复出来，再添加一次信任就好，不用重装。",
                    20, 300, 420, 40, 8.8f, false, Pal.InkSecondary);

            Panel hair2 = new Panel();
            hair2.BackColor = Pal.Hair;
            hair2.Location = new Point(0, S(344));
            hair2.Size = new Size(S(460), S(1));
            Controls.Add(hair2);

            AddText("它装在这儿", 20, 356, 76, 20, 8.5f, false, Pal.InkMuted);
            TextBox tbPath = new TextBox();
            tbPath.Text = ExePath;
            tbPath.ReadOnly = true;
            tbPath.BorderStyle = BorderStyle.None;
            tbPath.BackColor = Pal.Surface;
            tbPath.ForeColor = Pal.InkSecondary;
            tbPath.Font = new Font("Microsoft YaHei UI", 8.5f);
            tbPath.ShortcutsEnabled = true;
            tbPath.Location = new Point(S(98), S(359));
            tbPath.Width = S(340);
            tbPath.Tag = "exe路径";
            Controls.Add(tbPath);

            RoundButton ok = MakeBtn("知道了", 80, false);
            ok.Location = new Point(S(344), S(378));
            ok.DialogResult = DialogResult.OK;
            Controls.Add(ok);

            AcceptButton = ok;
            CancelButton = ok;
            // 关卡片时把输入框里的名字带走，出了这张卡再落盘（免得每敲一个字就写一次文件）
            FormClosing += delegate { ChosenName = CleanAppName(tbName.Text); };
        }
    }

    // 自检入口：把窗里能看到的文字 + 路径逐条导出来
    internal Form BuildFirstRunForm()
    {
        return new FirstRunForm(HotkeyLabel);
    }

    internal string DescribeFirstRun(Form f)
    {
        StringBuilder sb = new StringBuilder();
        FirstRunForm fr = f as FirstRunForm;
        if (fr == null) return "不是首启指路窗\r\n";
        sb.AppendLine("== 窗口里能看到的文字 ==");
        foreach (Control c in fr.Controls)
        {
            Label lb = c as Label;
            TextBox tb = c as TextBox;
            Button bt = c as Button;
            if (lb != null) sb.AppendLine("  [文字] " + lb.Text.Replace("\r\n", " / "));
            else if (tb != null) sb.AppendLine("  [" + (tb.Tag == null ? "?" : tb.Tag.ToString()) + "] " + tb.Text);
            else if (bt != null) sb.AppendLine("  [按钮] " + bt.Text);
        }
        sb.AppendLine("exe 路径   = " + fr.ExePath);
        sb.AppendLine("客户区     = " + fr.ClientSize);
        return sb.ToString();
    }

    // 首次打开时调用；关闭后立刻记「已看过」，不再重复打扰
    internal void ShowFirstRun()
    {
        try
        {
            using (FirstRunForm f = new FirstRunForm(HotkeyLabel))
            {
                f.ShowDialog();
                SaveAppName(f.ChosenName);   // 空 = 恢复内置默认名
            }
            ApplyAppName();                  // 主窗标题、托盘提示立刻跟着改
        }
        catch { }
        MarkFirstRunDone();
    }
}
