// ============================================================================
//  翻译器 · MainForm.cs（MainForm）
//  主窗体：窗口骨架、标题条、自绘控件、自适应布局、拖动/缩放、右键菜单、各类弹窗。
//  所有「看得见、点得到」的代码都在这个文件里。
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
    [DllImport("user32.dll")]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    // ====== 第一批 ①：问输入法"还有字没上屏吗" ======
    // 拼音打到一半敲回车，正常情况 IME 会先吃掉这个回车去选字；
    // 但有些输入法/场景下 KeyDown 照样收得到，于是 "nihao" 这种半成品被直接送去查了。
    // 这里问一下 IMM 的组合串：里面还有东西就别查，把回车让给输入法。
    [DllImport("imm32.dll")]
    static extern IntPtr ImmGetContext(IntPtr hWnd);
    [DllImport("imm32.dll")]
    static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);
    [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
    static extern int ImmGetCompositionStringW(IntPtr hIMC, int dwIndex, StringBuilder lpBuf, int dwBufLen);
    const int GCS_COMPSTR = 0x0008;

    [DllImport("user32.dll")]
    static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")]
    static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, int lParam);
    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);
    [StructLayout(LayoutKind.Sequential)]
    struct POINTAPI { public int X; public int Y; }
    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, ref POINTAPI lp);
    [DllImport("user32.dll")]
    static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    // ====== v2.5.2：单实例唤醒用的一套 API ======
    // 老逻辑：第二次双击 exe 只弹一句「已经开着了」就退出，窗口根本不出现，
    // 用户以为程序坏了。现在靠这几个 API 把已有窗口真正拉到前台。
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterWindowMessageW(string lpString);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    static extern bool IsHungAppWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")]
    static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)]
    struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }
    [DllImport("user32.dll")]
    static extern bool FlashWindowEx(ref FLASHWINFO pwfi);
    const int SW_SHOW = 5;
    const int SW_RESTORE = 9;
    const uint FLASHW_ALL = 3;   // 任务栏按钮 + 标题栏一起闪
    // Win11 原生圆角（DWM 合成器做抗锯齿，边缘不会有台阶）
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    const int DWMWCP_DONOTROUND = 1;
    const int DWMWCP_ROUND = 2;
    const int DWMWCP_ROUNDSMALL = 3;
    bool nativeCorner = false;

    // 无边框窗口要带 WS_THICKFRAME，DWM 才肯给它画圆角和阴影
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.Style |= 0x00040000; // WS_THICKFRAME
            return cp;
        }
    }

    void ApplyNativeCorner()
    {
        try
        {
            IntPtr h = Handle;
            int pref = DWMWCP_ROUND;
            int hr = DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            nativeCorner = (hr == 0) && (Environment.OSVersion.Version.Build >= 22000);
            if (nativeCorner)
            {
                // 把系统那圈 1px 边框染成极浅灰（COLORREF = 0x00BBGGRR），轮廓淡雅不刺眼
                int border = 0x00DED8D6;   // RGB(214,216,222)
                DwmSetWindowAttribute(h, 34, ref border, sizeof(int));
            }
        }
        catch { nativeCorner = false; }
        if (!nativeCorner)
        {
            // 老系统兜底：以前这里用 Region 把窗体裁成圆角，结果一放到最大，
            // 窗口还只有当初那一小块可见（Region 不会跟着放大），看着就像没最大化。
            // 现在宁可不裁圆角，只靠 Paint 里那圈浅灰边兜住轮廓，也绝不再动 Region
            ClearClipRegion();
        }
    }

    const int WM_NCLBUTTONDOWN = 0xA1;
    const int HTCAPTION = 0x2;
    const int WM_HOTKEY = 0x0312;
    const int HK_ID = 9101;
    const int HK_OCR_ID = 9102;
    const uint MOD_ALT = 0x1;
    const uint MOD_CONTROL = 0x2;
    const uint MOD_SHIFT = 0x4;
    const uint VK_SPACE = 0x20;
    const uint VK_A = 0x41;
    const int VK_LBUTTON = 0x01;

    RichTextBox box;
    Label tip;
    Button btnCopyLine;
    Button btnSpeak;
    Button btnApprox;
    Button btnTone;
    Button btnMore;
    TextBox inputBox;
    Label inputHint;
    RoundButton btnGo;
    Panel gapGo;   // 输入框与“查一下”之间的呼吸位，避免两个圆角框贴成一条线
    Panel errBar;          // ④ 只在出错时出现的一条，里面放「再试一次」（平时隐藏、不占高度）
    RoundButton btnRetry;
    static string lastFailText = "";   // 上一次查询失败的那段原文，重试时不用再选一次
    static string lastFull = "";
    bool showApprox = false;
    SpeechSynthesizer tts;
    static string lastInput = "";
    // v2.8.0 追问：记住「这一句 + 它的结果」当上下文；followMode = 现在正处在"接着问"状态
    volatile string followBaseInput = "";
    volatile string followBaseResult = "";
    bool followMode = false;
    static string lastWelcomeText = "";   // v2.4：说明页正文留一份，自检要对文案做断言
    NotifyIcon tray;
    static bool StartQuiet = false;
    volatile bool busy = false;
    bool hotkeyOk = false;
    string hotkeyKey = "ctrl+space";   // 第三批 ②：当前查询热键（存 settings.ini 的 hotkey）
    bool ocrHotkeyOk = false;
    string ocrHotkeyText = "";
    HeadPanel headPanel;
    HeadButton maxButton;     // 标题条上的"放到最大"按钮，切最大化/还原时换图标
    ToolStripItem miOcr1, miOcr2;
    ToolStripItem miTrayQuery;           // 第三批 ②：托盘「查一次（热键）」，换键后要跟着改文案
    ToolStripItem miCopyAllBox;          // v2.4：右键菜单里的「复制全文」，文案随语种变
    ToolTip footTip;                     // v2.4：只给底栏按钮挂提示，不产生新控件、不动按钮布局
    FlowLayoutPanel bar;
    float uiScale = 1f;      // 当前整体缩放（屏幕 DPI × 字号倍率）
    bool layingOut = false;  // 重排过程中不再触发重排，避免死循环
    int autoGrownH = 0;      // 为了把文字显示全，窗口自动长高了多少
    bool pendingFont = false;
    int curFontPt = 11;
    string tone = "auto";
    Size wantClient = Size.Empty;
    bool autoPopup = false;
    bool topMost = false;     // 窗口是否钉在最前面，默认关：点别的软件就该退下去
    volatile bool exitRequested = false;   // v2.5.2：正在走退出流程，防止连点重复触发
    bool closingCleanupDone = false;       // v2.5.2：退出清理只做一次（OnFormClosing 可能被多次调用）
    bool prevMouseDown = false;
    DateTime lastAuto = DateTime.MinValue;
    System.Windows.Forms.Timer autoTimer;
    bool resizing = false;   // 正在被拖动/缩放：这期间不做最小尺寸自愈，免得窗口自己往回跳
    // 真正的“鼠标在拖边框”窗口期（WM_ENTERSIZEMOVE → WM_EXITSIZEMOVE）。
    // 只有这段时间走极简重排；resizing 是由 OnResize 推断出来的，程序改尺寸时也会为真
    bool dragSizing = false;
    // 拖动中复用的底栏度量：按钮高度、每个按钮的自然宽度。
    // 拖动只会改变“可用宽度”，不会改变字号，所以这些值在整段拖动里是常量，
    // 没必要每一帧都去 MeasureText（那正是内容慢半拍、一跳一跳的根源）
    int _btnH = 0;
    int[] _btnNatW = null;
    float _btnNatScale = -1f;

    public QueryForm()
    {
        // 自检一律当「语法讲解关掉」来跑：截图基线是按老版四段/三段建立的，
        // 允许它在自检里多出一块，那基线就永远飘。只改内存，不碰你的 settings.ini。
        if (selfTestMode) SetSetQuiet("grammar", "0");
        // 双缓冲：无边框窗口拖动时是逐个控件重绘的，不开这个就会出现
        // “窗口已经挪过去了、内容还慢半拍”的撕裂感和一闪而过的残影
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        UpdateStyles();
        int W = 480, H = 660;
        ClientSize = new Size(W, H);
        // 能缩到很小，也能拉到很大，跟着自己习惯来
        MinimumSize = new Size(340, 300);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        // 置顶由设置决定，默认关：点别的软件就乖乖退到后面，像正常窗口一样
        TopMost = false;
        ShowInTaskbar = false;
        // 四周留 8px 给窗体自己：无边框窗口只有窗体能收到 WM_NCHITTEST，
        // 这一圈不被任何面板盖住，四条边和四个角才都能拖
        Padding = new Padding(8);
        BackColor = Pal.BgPage;
        Font = new Font("Microsoft YaHei UI", 10.5f);
        AutoScaleMode = AutoScaleMode.None;
        Text = AppTitle;   // 名字存 settings，首启那张卡片里能改
        try { Icon = LoadAppIcon(32); } catch { }

        showApprox = GetSet("show_approx", "0") == "1";
        tone = GetSet("tone", "auto");
        autoPopup = GetSet("auto_popup", "0") == "1";
        // ⑩ 粘贴即查：默认开（见 PasteSend.cs）。新装不用配置也能少按那两下
        pasteQueryOn = GetSet("paste_query", PasteDefaultOn) == "1";
        topMost = GetSet("topmost", "0") == "1";
        TopMost = topMost;

        curFontPt = GetInt("font_size", 11);
        if (curFontPt < 8) curFontPt = 8;
        if (curFontPt > 24) curFontPt = 24;

        LoadHistory();

        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(wa.Right - W - 60, wa.Bottom - H - 70);
        LoadPos();
        // 记住用户自己拖出来的尺寸：窗口还没显示时 WinForms 会先把 ClientSize 报成 0，
        // 这时若跑“最小尺寸自愈”就会把 500x800 直接压成 320x371，必须留一份原始值回来恢复
        wantClient = new Size(Math.Max(ClientSize.Width, MinimumSize.Width), Math.Max(ClientSize.Height, MinimumSize.Height));
        // 绝对不能挂在 LocationChanged 上：拖动时每个像素都触发，会把 settings.ini 写成千上万次，窗口直接卡死
        ResizeEnd += delegate { SavePos(); };
        FormClosing += delegate { SavePos(); };
        // 整体圆角交给 Win11 DWM 画，边缘平滑无锯齿
        ApplyNativeCorner();

        // 顶部留白 + 内嵌深色圆角标题条：窗口外壳保持浅灰，圆角处颜色连续，
        // 不会出现“深色直角压在弧线里”的毛边错觉
        Panel headHost = new Panel();
        headHost.Dock = DockStyle.Top;
        headHost.Height = 50;
        headHost.Padding = new Padding(10, 10, 10, 0);
        headHost.BackColor = Pal.BgPage;

        HeadPanel head = new HeadPanel();
        head.Dock = DockStyle.Fill;
        head.Radius = 8;
        head.BackColor = Pal.HeadBg;
        head.TitleText = AppTitle;
        head.HintText = HotkeyFriendly(hotkeyKey) + " 查 · Esc 关";
        // 右内边距原来 14：一个 40 宽的按钮里图标居中，× 实际离标题条右边约 53px，
        // 而标题文字离左边只有 28px，标题条整个重心偏左、右边空一块。
        // 这里把右边距收到 3、按钮宽度各收窄一点，两头留白才对称
        head.Padding = new Padding(0, 0, 5, 0);
        headPanel = head;

        // 放大 / 还原按钮（双击标题条也行）
        // 顺序要反着来：Dock=Right 是“后加的更靠右”，所以先加放大的，
        // 关闭才会落在最右上角，跟大家用惯的 Windows 窗口一致
        HeadButton maxb = new HeadButton();
        maxb.Name = "maxb";
        maxb.Glyph = 1;
        maxb.Dock = DockStyle.Right;
        maxb.Width = 34;
        // 方框是横平竖直的线，抗锯齿几乎不稀释，1px 全吃满 238 的白，比斜线的「×」扎眼得多；
        // 斜线每个像素只吃到七八成，实测峰值约 196~201，两个图标就一白一灰。
        // 这里把方框单独降到 × 的实测亮度，两种图标才在标题条上等重。
        maxb.ForeColor = Pal.HeadFgSub;
        maxb.Cursor = Cursors.Hand;
        maxb.Click += delegate { ToggleMax(); };
        head.Controls.Add(maxb);

        // 「×」和放大图标之间留一条空气：两个图标直接贴在一起既显挤，也容易点错。
        // 这里必须用自绘的 HeadSpacer（背景交给 HeadPanel 画），不能用普通 Panel 实填深色：
        // 标题条底边最后一行靠抗锯齿做过渡，Panel 的纯色矩形会把这一行压成硬边，
        // 于是 10px 宽的底边上冒出一条“小黑线”
        HeadSpacer headGap = new HeadSpacer();
        headGap.Name = "headGap";
        headGap.Dock = DockStyle.Right;
        headGap.Width = 10;
        head.Controls.Add(headGap);

        HeadButton close = new HeadButton();
        close.Name = "close";
        close.Glyph = 3;
        close.Dock = DockStyle.Right;
        close.Width = 36;
        close.ForeColor = Pal.HeadFg;
        close.Cursor = Cursors.Hand;
        close.Click += delegate { Hide(); };
        head.Controls.Add(close);

        maxButton = maxb;
        headHost.Controls.Add(head);

        // 拖动交给统一的 HookDrag（构造末尾递归挂上，避免同一个面板挂两次导致松手后又开始一次拖拽）
        head.MouseDoubleClick += delegate { ToggleMax(); };

        Panel body = new Panel();
        body.Dock = DockStyle.Fill;
        body.Padding = new Padding(10, 10, 10, 12);
        body.BackColor = Pal.BgPage;

        // 白色内容卡片：14px 抗锯齿圆角浮在浅灰底上，比整块白更精致，圆润感也更明显
        Panel card = new Panel();
        card.Dock = DockStyle.Fill;
        card.Padding = new Padding(14, 12, 14, 10);
        card.BackColor = Pal.Surface;
        // 只画白底 + 圆角描边不够：Panel 本身是方角白矩形，圆弧外面还挂着四个白方角，
        // 看着就是“圆角旁边又冒方角”。把卡片自己的 Region 也裁成同半径圆角，白底才真被削圆
        card.Resize += delegate { RoundControl(card, 8); };
        card.Paint += delegate (object s, PaintEventArgs e) {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            Rectangle r = new Rectangle(0, 0, card.Width - 1, card.Height - 1);
            using (System.Drawing.Drawing2D.GraphicsPath gp = RoundPath(r, 8))
            using (Pen p = new Pen(Pal.Hair))
            {
                e.Graphics.DrawPath(p, gp);
            }
        };

        box = new RichTextBox();
        box.Dock = DockStyle.Fill;
        box.ReadOnly = true;
        box.BorderStyle = BorderStyle.None;
        box.BackColor = Pal.Surface;
        box.ForeColor = Pal.Ink;
        box.Font = new Font("Microsoft YaHei UI", curFontPt);
        box.WordWrap = true;
        box.ScrollBars = RichTextBoxScrollBars.Vertical;
        box.DetectUrls = false;
        box.Cursor = Cursors.IBeam;
        // Ctrl + 滚轮 调字号
        box.MouseWheel += delegate (object s, MouseEventArgs e) {
            if ((Control.ModifierKeys & Keys.Control) == Keys.Control)
                SetFontSize(curFontPt + (e.Delta > 0 ? 1 : -1));
        };
        // 双击某句：只读这一句
        box.MouseDoubleClick += delegate (object s, MouseEventArgs e) {
            if (e.Button != MouseButtons.Left) return;
            int pos = box.GetCharIndexFromPosition(e.Location);
            box.SelectionStart = pos;
            box.SelectionLength = 0;
            SpeakCurrentLine();
        };
        card.Controls.Add(box);

        // ====== 打字查：中文→英文 也能直接在这里输入 ======
        Panel inputBar = new Panel();
        inputBar.Dock = DockStyle.Bottom;
        inputBar.Height = 40;
        inputBar.Tag = "nodrag";   // 输入区不参与整窗拖拽，免得想打字结果把窗口拖走
        inputBar.Padding = new Padding(0, 10, 0, 0);
        inputBar.BackColor = Pal.Surface;

        Panel inputWrap = new Panel();
        inputWrap.Dock = DockStyle.Fill;
        inputWrap.BackColor = Pal.Surface;
        inputWrap.Padding = new Padding(10, 6, 10, 6);
        // 自绘圆角：填充+描边都走抗锯齿，不用 Region 裁剪，边缘不会毛糙
        inputWrap.Paint += delegate (object s, PaintEventArgs e) {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, inputWrap.Width - 1, inputWrap.Height - 1);
            // v2.5.8 ⑫：Tab / 点进输入框时边框亮成主题蓝，一眼看出「现在打字打在这里」。
            // 自检模式不点亮，所以主窗截图跟上一版逐字节可比。
            bool glow = NavGlow(selfTestMode, inputBox.Focused);
            using (System.Drawing.Drawing2D.GraphicsPath gp = RoundPath(r, 8))
            using (SolidBrush b = new SolidBrush(Pal.InputBg))
            using (Pen p = new Pen(glow ? Pal.Accent : Pal.InputBorder, glow ? 1.6f : 1f))
            {
                e.Graphics.FillPath(b, gp);
                e.Graphics.DrawPath(p, gp);
            }
        };

        inputBox = new TextBox();
        inputBox.Dock = DockStyle.Fill;
        inputBox.BorderStyle = BorderStyle.None;
        inputBox.BackColor = Pal.InputBg;
        inputBox.ForeColor = Pal.Ink;
        inputBox.Font = new Font("Microsoft YaHei UI", 9.5f);
        inputBox.KeyDown += delegate (object s, KeyEventArgs e) {
            if (e.KeyCode == Keys.Enter)
            {
                // ① 这个回车可能是给输入法选字/上屏用的，不是"查一下"。
                //    直接放行、不 Suppress，让 IME 自己把字上屏，绝不拿半截拼音去查。
                if (IsImeComposing(inputBox.Handle)) return;
                e.SuppressKeyPress = true;
                QueryManual();
            }
            else if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
            {
                // v2.5.8 ⑫：↑↓ 翻历史（见 KeyNav.cs）。输入法选字时 MainNavStep 返回 false，
                // 上下键整段交给输入法翻候选词，本程序一步不抢。
                bool imeNow = IsImeComposing(inputBox.Handle);
                if (MainNavStep(e.KeyCode == Keys.Up ? -1 : 1, imeNow)) e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape) { if (followMode) ExitFollowMode(); else Hide(); }
        };
        inputWrap.Controls.Add(inputBox);

        inputHint = new Label();
        inputHint.Text = "在这里打字：中文 → 英文，英文 → 中文，回车就查";
        inputHint.Dock = DockStyle.Fill;
        inputHint.AutoSize = false;
        inputHint.TextAlign = ContentAlignment.MiddleLeft;
        inputHint.ForeColor = Pal.InkFaint;
        inputHint.BackColor = Pal.InputBg;
        inputHint.Font = new Font("Microsoft YaHei UI", 9f);
        inputHint.Cursor = Cursors.IBeam;
        inputHint.Click += delegate { inputBox.Focus(); };
        inputWrap.Controls.Add(inputHint);
        inputHint.BringToFront();

        btnGo = new RoundButton();
        btnGo.Text = "查一下";
        btnGo.Dock = DockStyle.Right;
        btnGo.Width = 68;
        btnGo.Radius = 8;
        btnGo.FillColor = Pal.AccentSoft;
        btnGo.HoverColor = Pal.AccentSoftHoverAlt;
        btnGo.BackColor = Pal.Surface;
        btnGo.ForeColor = Pal.Accent;
        btnGo.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
        btnGo.Cursor = Cursors.Hand;
        btnGo.Click += delegate { QueryManual(); };

        inputBar.Controls.Add(inputWrap);

        // 中间留 10px 空隙：输入框右圆角和按钮左圆角原本紧紧贴在一起，看着像粘在一条线上
        gapGo = new Panel();
        gapGo.Dock = DockStyle.Right;
        gapGo.Width = 10;
        gapGo.BackColor = Pal.Surface;
        gapGo.Tag = "nodrag";
        gapGo.Name = "gapGo";
        inputBar.Controls.Add(gapGo);
        inputBar.Controls.Add(btnGo);

        // v2.5 第 5 步：输入框右侧浮一个「说」（不改 Dock、不占新行、不撑大窗口，见 MainVoice.cs）
        MainVoiceInit(inputWrap);

        // ====== ④ 出错专用的一条：右边一个「再试一次」（平时隐藏，正常态布局零影响）======
        // 加在 inputBar 之前：两者都是 Dock=Bottom，先加的贴底，所以 errBar 正好落在
        // 「正文框下方、输入框上方」，就是看结果的那个位置。
        errBar = new Panel();
        errBar.Dock = DockStyle.Bottom;
        errBar.Height = S(42);
        errBar.Visible = false;          // 隐藏时既不占高度也不进控件树审计
        errBar.BackColor = Pal.Surface;
        errBar.Tag = "nodrag";
        errBar.Name = "errBar";
        errBar.Padding = new Padding(0, S(7), 0, S(7));

        btnRetry = new RoundButton();
        btnRetry.Text = "再试一次";
        btnRetry.Dock = DockStyle.Right;
        btnRetry.Width = S(104);
        btnRetry.Radius = 8;
        btnRetry.FillColor = Pal.AccentSoft;
        btnRetry.HoverColor = Pal.AccentSoftHoverAlt;
        btnRetry.BackColor = Pal.Surface;
        btnRetry.ForeColor = Pal.Accent;
        btnRetry.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
        btnRetry.Cursor = Cursors.Hand;
        btnRetry.Click += delegate { RetryLast(); };
        errBar.Controls.Add(btnRetry);

        card.Controls.Add(errBar);
        card.Controls.Add(inputBar);
        body.Controls.Add(card);

        inputBox.TextChanged += delegate { inputHint.Visible = (inputBox.Text.Length == 0); NavUserEdit(); };
        // v2.5.8 ⑫：焦点进出输入框时重画圆角框（点亮 / 熄灭），Tab 走到这里一眼看得出
        inputBox.Enter += delegate { try { inputWrap.Invalidate(); } catch { } };
        inputBox.Leave += delegate { try { inputWrap.Invalidate(); } catch { } };
        inputBox.GotFocus += delegate { inputHint.Visible = false; };
        inputBox.LostFocus += delegate { inputHint.Visible = (inputBox.Text.Length == 0); };

        Panel foot = new Panel();
        foot.Dock = DockStyle.Bottom;
        foot.Height = 66;
        foot.BackColor = Pal.BgPage;
        foot.Padding = new Padding(14, 5, 14, 4);
        foot.Paint += delegate (object s, PaintEventArgs e) {
            using (Pen p = new Pen(Pal.Hair2))
                e.Graphics.DrawLine(p, 0, 0, foot.Width, 0);
        };

        // 自适应按钮条：够宽就一排五个，变窄自动折成两排（三+二），文字不会被切成半截
        bar = new FlowLayoutPanel();
        bar.Dock = DockStyle.Top;
        bar.Height = 32;
        bar.WrapContents = true;
        // 高度不再交给流式布局"猜"：折了几行由 ApplyMetrics 自己数，
        // 数出来的行数直接定高度，最后一行就不会被底边框切掉
        bar.AutoSize = false;
        bar.AutoScroll = false;
        bar.FlowDirection = FlowDirection.LeftToRight;
        bar.Margin = new Padding(0);
        bar.Padding = new Padding(0);
        bar.BackColor = Color.Transparent;
        bar.Name = "bar";

        btnCopyLine = MakeFootButton("复制这句", 74);
        btnCopyLine.Click += delegate { CopyCurrentLine(); };
        bar.Controls.Add(btnCopyLine);

        btnSpeak = MakeFootButton("读一遍", 64);
        btnSpeak.Click += delegate { SpeakText(); };
        bar.Controls.Add(btnSpeak);

        btnApprox = MakeFootButton("谐音 关", 68);
        btnApprox.Click += delegate {
            showApprox = !showApprox;
            SetSet("show_approx", showApprox ? "1" : "0");
            UpdateApproxBtn();
            if (lastFull.Length > 0) RenderResult(lastFull);
            tip.Text = showApprox ? "谐音已经打开" : "谐音收起来了";
        };
        bar.Controls.Add(btnApprox);
        UpdateApproxBtn();

        btnTone = MakeFootButton("语气 自动", 78);
        btnTone.Click += delegate { CycleTone(); };
        bar.Controls.Add(btnTone);
        UpdateToneBtn();

        btnMore = MakeFootButton("更多 ▾", 62);
        btnMore.Click += delegate { ShowMoreMenu(); };
        bar.Controls.Add(btnMore);

        // v2.4 第 3 步：复制全文与导出都藏在「更多 ▾」里，靠气泡提示让人一眼知道里面有东西。
        // ToolTip 不是子控件，底栏 5 个按钮的几何与文案一个都没动。
        footTip = new ToolTip();
        footTip.InitialDelay = 260;
        footTip.ReshowDelay = 60;
        footTip.SetToolTip(btnMore, "更多 ▾：复制全文（Ctrl+Shift+C）、导出 TXT / Word…");

        // v2.5 第 6 步：给「读一遍」挂右键选层（逐词 / 短句 / 中句 / 长句）。
        // 只是多一个 ContextMenuStrip + 一条提示，按钮位置、大小、文案、样式一个都没动。
        MainReadInit();
        footTip.SetToolTip(btnSpeak, "读一遍（F2）：自动按词数分逐词 / 短句 / 长句三层；右键选朗读方式");

        foot.Controls.Add(bar);

        tip = new Label();
        tip.Dock = DockStyle.Fill;
        tip.AutoSize = false;
        tip.AutoEllipsis = true;
        tip.TextAlign = ContentAlignment.MiddleLeft;
        tip.Padding = new Padding(1, 0, 0, 0);
        tip.ForeColor = Pal.InkMuted;
        tip.Font = new Font("Microsoft YaHei UI", 8.5f);
        tip.Text = "";

        // 提示条单独占底部一条，避免盖住上面那排按钮（否则按钮点不动）
        Panel tipStrip = new Panel();
        tipStrip.Dock = DockStyle.Bottom;
        tipStrip.Height = 18;
        tipStrip.BackColor = Pal.BgPage;
        tipStrip.Padding = new Padding(0, 0, 22, 0);
        tipStrip.Controls.Add(tip);
        foot.Controls.Add(tipStrip);

        // v2.3 第 1 步：底栏右侧挂一个轻量文字入口（不改按钮条里的 5 个按钮，
        // 只借提示条右侧的空位，字号/位置由 MlPlaceLink 跟着缩放走）
        BuildMlLink(tipStrip);

        // 右下角那个小三角已经撤掉：四条边和四个角都能直接拖，不用再对准那 16px 的角落
        Controls.Add(body);
        Controls.Add(foot);
        Controls.Add(headHost);

        // 关键控件登记名字：重排时按名字找，够不着就跳过，不会因为漏一个就崩
        headHost.Name = "headHost";
        head.Name = "head";
        close.Name = "close";
        maxb.Name = "maxb";
        body.Name = "body";
        card.Name = "card";
        inputBar.Name = "inputBar";
        inputWrap.Name = "inputWrap";
        foot.Name = "foot";
        tipStrip.Name = "tipStrip";

        ApplyMetrics();

        Paint += delegate (object s, PaintEventArgs e) {
            // 系统圆角生效时不再自画 1px 灰框：那条直角框会横穿圆角，正是"看着毛糙"的元凶
            if (nativeCorner) return;
            ControlPaint.DrawBorder(e.Graphics, ClientRectangle,
                Pal.BtnBorder, 1, ButtonBorderStyle.Solid,
                Pal.BtnBorder, 1, ButtonBorderStyle.Solid,
                Pal.BtnBorder, 1, ButtonBorderStyle.Solid,
                Pal.BtnBorder, 1, ButtonBorderStyle.Solid);
        };

        KeyPreview = true;
        KeyDown += delegate (object s, KeyEventArgs e) {
            if (e.KeyCode == Keys.Escape)
            {
                // v2.8.4：声音还在响的时候，Esc 先掐声音、不藏窗（要藏窗再按一次）
                if (ReadStop()) { e.Handled = true; e.SuppressKeyPress = true; }
                // v2.3：浮层开着时 Esc 先收浮层，别把整个窗口一起藏了（用户会以为程序没了）
                // v2.8.0：追问状态下 Esc 先退追问，不能让一次 Esc 把窗口直接藏了
                else if (followMode) { ExitFollowMode(); e.Handled = true; e.SuppressKeyPress = true; }
                else if (MlVisible) HideMultilingual();
                else Hide();
            }
            // v2.8.0：Ctrl+F 接着问上一句。选 F 键是因为输入框里 Ctrl+F 本来就没占位
            else if (e.Control && !e.Shift && !e.Alt && e.KeyCode == Keys.F)
            {
                EnterFollowMode();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            // v2.8.4：一边读一边按 F2/F3，同一个键就是停（以前只会叠着读）
            else if (e.KeyCode == Keys.F2) { if (!ReadStop()) SpeakText(); }
            else if (e.KeyCode == Keys.F3) { if (!ReadStop()) SpeakCurrentLine(); }
            // v2.4 第 4 步 / v2.5.7 ⑩：图片就认字翻译；文字看「粘贴就查」开关——
            // 开着一句短话粘完就翻，关着（或剪贴板里没文字）原样交给输入框自己粘（见 MainIngest.cs ② / PasteSend.cs）
            else if (e.Control && !e.Shift && !e.Alt && e.KeyCode == Keys.V && MainTryPaste())
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.Control && e.Shift && e.KeyCode == Keys.C) CopyAll();
            // v2.4 第 3 步：导出快捷键（不必新增底栏按钮，键盘也够得着）
            else if (e.Control && e.Shift && e.KeyCode == Keys.S) ExportResult(false);
            else if (e.Control && e.Shift && e.KeyCode == Keys.D) ExportResult(true);
            else if (e.Control && (e.KeyCode == Keys.Oemplus || e.KeyCode == Keys.Add)) SetFontSize(curFontPt + 1);
            else if (e.Control && (e.KeyCode == Keys.OemMinus || e.KeyCode == Keys.Subtract)) SetFontSize(curFontPt - 1);
        };

        box.ContextMenuStrip = BuildBoxMenu();

        WindowState = FormWindowState.Minimized;
        Opacity = 0;

        tray = new NotifyIcon();
        try { tray.Icon = LoadAppIcon(16); }
        catch { tray.Icon = SystemIcons.Application; }
        tray.Text = AppTitle + "：选中文字按 " + HotkeyLabel;
        tray.Visible = true;
        trayStatic = tray;   // v2.1 F20：崩溃兜底要用它冒泡提示
        ContextMenuStrip menu = new ContextMenuStrip();
        miTrayQuery = menu.Items.Add("查一次（" + HotkeyLabel + "）", null, delegate { StartQuery(); });
        menu.Items.Add("读一遍（F2）", null, delegate { SpeakText(); });
        menu.Items.Add("显示窗口", null, delegate { ShowPanel(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("历史记录", null, delegate { ShowHistory(); });
        menu.Items.Add("生词本", null, delegate { ShowWordBook(); });
        menu.Items.Add("生词复习（卡片背单词）", null, delegate { ShowWordReview(); });
        miOcr1 = menu.Items.Add("截图查图里的英文" + OcrSuffix(), null, delegate { DoOcr(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("语音设置（男声/女声/语速）", null, delegate { ShowVoiceSettings(); });
        ToolStripMenuItem miTopTray = new ToolStripMenuItem("窗口总是置顶（点别的软件也不退）");
        miTopTray.Checked = topMost;
        miTopTray.Click += delegate {
            topMost = !topMost;
            miTopTray.Checked = topMost;
            TopMost = topMost;
            SetSet("topmost", topMost ? "1" : "0");
            tip.Text = topMost ? "已钉在最前面" : "已取消置顶：点别的软件就退到后面";
        };
        menu.Items.Add(miTopTray);
        // v2.5.2：文案写清代价。这个开关靠模拟 Ctrl+C 抢剪贴板取词，
        // 打开后会短暂覆盖剪贴板内容，必须提前讲明白，不能只写「选中就查」。
        ToolStripMenuItem miAuto = new ToolStripMenuItem("划词就自动查（默认关 · 会占用剪贴板短暂复制）");
        miAuto.Checked = autoPopup;
        miAuto.Click += delegate {
            autoPopup = !autoPopup;
            miAuto.Checked = autoPopup;
            SetSet("auto_popup", autoPopup ? "1" : "0");
            tip.Text = autoPopup
                ? "已打开：划完词自动查；它要模拟 Ctrl+C 取词，会短暂占用剪贴板"
                : "已关掉：还是按 " + HotkeyLabel + " 查，不碰你的剪贴板";
        };
        menu.Items.Add(miAuto);
        // ⑩ 粘贴即查：跟「划词就自动查」挨着放，都是「少按两下」这类开关
        menu.Items.Add(BuildPasteMenu());
        // 语法讲解开关（默认开）：勾掉就回到「只看译文、不啰嗦」的老样子。
        // 改了要清缓存，不然十分钟内拿到的还是旧结果，会以为开关没生效。
        ToolStripMenuItem miGrammar = new ToolStripMenuItem("语法讲解（结果最后多一块【语法】· 讲这句为什么这么说）");
        miGrammar.Checked = GrammarOn;
        miGrammar.Click += delegate {
            bool on = !miGrammar.Checked;
            miGrammar.Checked = on;
            SetSet("grammar", on ? "1" : "0");
            lock (cache) { cache.Clear(); }
            tip.Text = on
                ? "已打开语法讲解：下一次查，结果最后会多一块【语法】"
                : "已关掉语法讲解：下一次查就回到原来的样子，不多这一块";
        };
        menu.Items.Add(miGrammar);
        menu.Items.Add("字号 大一点", null, delegate { SetFontSize(curFontPt + 1); });
        menu.Items.Add("字号 小一点", null, delegate { SetFontSize(curFontPt - 1); });
        menu.Items.Add("清空查询缓存", null, delegate {
            lock (cache) { cache.Clear(); }
            tip.Text = "缓存清空了";
        });
        menu.Items.Add("再看一遍使用说明", null, delegate { ShowWelcome(); });
        // v2.5.5 第一档 ⑨/③：指路窗只在第一次打开时自己弹；想再看一遍（比如要教别人装）从这儿点
        menu.Items.Add("改个名 / 第一次用怎么开始（托盘 / 图标 / 被杀软拦）", null, delegate { ShowFirstRun(); });
        // C1 公告卡：本地文件驱动，不联网。随时想看这一版改了什么就从这儿点
        menu.Items.Add("这一版有什么变化（公告）", null, delegate { ShowAnnounce(true); });
        // v2.5.9 第一档 ①：填 / 换 API Key 的小窗。以后换 Key 不用再去 %APPDATA% 里改文件。
        menu.Items.Add("配置 API Key（换 Key 也在这儿）", null, delegate { ShowApiKeySetup(); });
        menu.Items.Add("关于（版本号 / 数据目录）", null, delegate { ShowAbout(); });
        // v2.5.4 第一档 ⑤：备份和日志都在数据目录里，给一个能直接点开的口子，
        // 不用再让用户去 %APPDATA% 里翻。
        menu.Items.Add("打开数据目录（备份 / 日志）", null, delegate { OpenDataDir(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, delegate { RequestExit(); });
        StyleMenu(menu);
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += delegate { ShowPanel(); };

        // 划词自动弹窗：只在打开开关时才干活，默认关
        autoTimer = new System.Windows.Forms.Timer();
        autoTimer.Interval = 280;
        autoTimer.Tick += delegate { AutoTick(); };
        autoTimer.Start();

        System.Windows.Forms.Timer welcome = new System.Windows.Forms.Timer();
        welcome.Interval = 700;
        welcome.Tick += delegate (object s, EventArgs e) {
            ((System.Windows.Forms.Timer)s).Stop();
            if (StartQuiet) return;
            // 自检模式绝不开自动弹窗：700ms 后正好撞上自检里的 DoEvents，
            // 模态窗会把自检线程卡死（2026-11-05 踩到过）
            if (selfTestMode) return;
            // v2.5.5 第一档 ⑨/③：真正第一次打开，先弹「指路窗」——它在托盘 / 桌面怎么放图标 /
            // 被杀毒软件隔离了去哪恢复。老用户照旧只看原来的使用说明，不多弹一个窗打扰。
            bool firstRun = !FirstRunDone();
            if (firstRun) { ShowFirstRun(); }
            // v2.5.9 第一档 ①：还是没有 Key 的话，把「填 Key」小窗端上来——
            // 与其让他在正文里读四步手工建文件，不如直接给个输入框。只弹这一次，关了就记下。
            if (ApiKey.Length == 0 && !KeyPromptDone()) { ShowApiKeySetup(); return; }
            // C1 公告卡：本地「公告.txt」里有没弹过的内容就先亮一下。
            // 首启已经有指路窗 + 使用说明，别叠着弹，等下次启动再看。
            try
            {
                EnsureAnnounceFile();
                if (!firstRun && AnnouncePending()) { ShowAnnounce(false); return; }
            }
            catch { }
            ShowWelcome();
        };
        welcome.Start();

        // 整窗可拖：除了按钮/输入框/正文，任何空白面板抓住了都能挪窗口
        HookDrag(this);
        HookDragPanels(this);
        // 所有子面板也一起上双缓冲，否则拖动时白卡、标题条、按钮条会各自闪一下，
        // 拼起来就是用户看到的“过渡不自然、像有残影”
        EnableDoubleBuffer(this);
    }

    // Panel / Label 的 DoubleBuffered 是 protected，只能反射打开
    static void EnableDoubleBuffer(Control root)
    {
        if (root == null) return;
        try
        {
            System.Reflection.PropertyInfo pi = typeof(Control).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (pi != null && !(root is Form)) pi.SetValue(root, true, null);
        }
        catch { }
        foreach (Control c in root.Controls) EnableDoubleBuffer(c);
    }

    // 手指抓哪都能拖，不用非得对准那一条细标题
    void HookDrag(Control c)
    {
        if (c == null) return;
        c.MouseDown += delegate (object s, MouseEventArgs e) {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
        };
    }

    void HookDragPanels(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c.Tag as string == "nodrag") continue;   // 输入区不抢鼠标
            Panel pn = c as Panel;
            if (pn != null) HookDrag(pn);
            HookDragPanels(c);
        }
    }

    // 优先用同目录的图标文件（跟着 exe 走，换图标不用等系统缓存刷新）
    static Icon LoadAppIcon(int size)
    {
        try
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (string name in new string[] { "cookie_ch_en.ico", "en_bubble.ico", "app.ico" })
            {
                string p = Path.Combine(dir, name);
                if (!File.Exists(p)) continue;
                try { return new Icon(p, size, size); }
                catch { return new Icon(p); }
            }
        }
        catch { }
        try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch { return SystemIcons.Application; }
    }

    static System.Drawing.Drawing2D.GraphicsPath RoundPath(Rectangle r, int radius)
    {
        System.Drawing.Drawing2D.GraphicsPath gp = new System.Drawing.Drawing2D.GraphicsPath();
        int d = radius * 2;
        if (r.Width <= d || r.Height <= d) { gp.AddRectangle(r); gp.CloseFigure(); return gp; }
        gp.AddArc(r.X, r.Y, d, d, 180, 90);
        gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        gp.CloseFigure();
        return gp;
    }

    // 圆角裁剪：让死板的直角变柔和
    // 把窗体身上残留的 Region 裁剪解掉：Region 是按旧尺寸算的，最大化后会把新区域整片裁没
    void ClearClipRegion()
    {
        try
        {
            Region old = Region;
            if (old == null) return;
            Region = null;
            old.Dispose();
        }
        catch { }
    }

    static void RoundControl(Control c, int radius)
    {
        if (c == null || c.Width <= 0 || c.Height <= 0) return;
        try
        {
            using (System.Drawing.Drawing2D.GraphicsPath gp = RoundPath(new Rectangle(0, 0, c.Width, c.Height), radius))
            {
                Region old = c.Region;
                c.Region = new Region(gp);
                if (old != null) old.Dispose();
            }
        }
        catch { }
    }

    // 只圆上方两角（标题栏用）
    static void RoundTop(Control c, int radius)
    {
        if (c == null || c.Width <= 0 || c.Height <= 0) return;
        try
        {
            int d = radius * 2;
            using (System.Drawing.Drawing2D.GraphicsPath gp = new System.Drawing.Drawing2D.GraphicsPath())
            {
                gp.AddArc(0, 0, d, d, 180, 90);
                gp.AddArc(c.Width - d, 0, d, d, 270, 90);
                gp.AddLine(c.Width, c.Height, 0, c.Height);
                gp.CloseFigure();
                Region old = c.Region;
                c.Region = new Region(gp);
                if (old != null) old.Dispose();
            }
        }
        catch { }
    }

    // 深色标题条：顶角用抗锯齿圆角，和系统窗口圆角贴合，不留直角毛边
    class HeadPanel : Panel
    {
        public int Radius = 8;
        public float ScaleF = 1f;
        public string TitleText = "";
        public string HintText = "";
        public HeadPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                   | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Pal.HeadBg;
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 四角都用 GDI+ 抗锯齿自绘：先铺满父级底色，再填圆角矩形，
            // 弧线上是连续过渡色，不会出现 1-bit 裁剪那种台阶
            Color outer = (Parent != null) ? Parent.BackColor : Pal.BgPage;
            e.Graphics.Clear(outer);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath gp = RoundPath(r, Radius))
            using (SolidBrush b = new SolidBrush(BackColor))
                e.Graphics.FillPath(b, gp);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            // 右侧到底被按钮占掉多少，按控件真实宽度算（按钮宽 + 内边距 + 10px 空隙）。
            // 以前写死 56px，比"×"和放大两个按钮加内边距还窄，提示文字就顶到按钮上，
            // 看着就是"关"和"×"挤在一起
            int btnW = 0;
            foreach (Control c in Controls)
                if (c.Visible) btnW += c.Width;
            int limit = Width - Padding.Right - btnW - (int)Math.Round(16 * ScaleF);
            if (HintText.Length > 0)
            {
                using (Font hf = new Font("Microsoft YaHei UI", 8.5f * ScaleF))
                using (SolidBrush hb = new SolidBrush(Pal.HeadFgSub))
                {
                    SizeF sz = g.MeasureString(HintText, hf);
                    float hx = limit - sz.Width;
                    if (hx > 120 * ScaleF)   // 窗口够宽才显示，窄了宁可不显示，也不挤成一团
                    {
                        g.DrawString(HintText, hf, hb, hx, (Height - sz.Height) / 2f);
                        limit = (int)hx - (int)Math.Round(12 * ScaleF);
                    }
                }
            }
            using (Font tf = new Font("Microsoft YaHei UI", 10f * ScaleF, FontStyle.Bold))
            using (SolidBrush tb = new SolidBrush(Pal.HeadFgTitle))
            using (StringFormat sf = new StringFormat())
            {
                sf.LineAlignment = StringAlignment.Center;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                float w = Math.Max(20, limit - (int)Math.Round(16 * ScaleF));
                g.DrawString(TitleText, tf, tb, new RectangleF((float)Math.Round(16 * ScaleF), 0, w, Height), sf);
            }
        }
    }

    // 标题条上的透明小按钮：平时不留底色，悬停才浮出浅色圆角
    class HeadButton : Button
    {
        bool hover = false;
        // 1=最大化(空心方框) 2=还原(叠两个方框) 3=关闭(叉)
        // 全部用矢量线条画，不再靠字体里的特殊符号：⛶ 在这台机器的雅黑里没有字形，
        // 会被画成一个问号，用户根本认不出哪个是"放到最大"
        public int Glyph = 0;
        public HeadButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                   | ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            TabStop = false;
            BackColor = Color.Transparent;
            MouseEnter += delegate { hover = true; Invalidate(); };
            MouseLeave += delegate { hover = false; Invalidate(); };
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaintBackground(e);   // 让父级画背景，右上角才跟得住窗口圆角
            if (hover)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle r = new Rectangle(4, 5, Width - 9, Height - 11);
                using (GraphicsPath gp = RoundPath(r, 7))
                using (SolidBrush b = new SolidBrush(Pal.ShadowInk))
                    e.Graphics.FillPath(b, gp);
            }
            if (Glyph == 0)
            {
                TextRenderer.DrawText(e.Graphics, Text, Font,
                    new Rectangle(0, 0, Width, Height), ForeColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                return;
            }
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int cx = Width / 2, cy = Height / 2;
            int s = Math.Max(8, Math.Min(12, Math.Min(Width, Height) / 3));
            using (Pen p = new Pen(ForeColor, 1.3f))
            {
                if (Glyph == 1)
                {
                    e.Graphics.DrawRectangle(p, cx - s / 2, cy - s / 2, s, s);
                }
                else if (Glyph == 2)
                {
                    // 还原：后面一个、前面一个错位叠起来
                    e.Graphics.DrawRectangle(p, cx - s / 2 + 2, cy - s / 2 - 2, s, s);
                    e.Graphics.DrawRectangle(p, cx - s / 2 - 1, cy - s / 2 + 1, s, s);
                }
                else if (Glyph == 3)
                {
                    int r = Math.Max(4, s / 2);
                    e.Graphics.DrawLine(p, cx - r, cy - r, cx + r, cy + r);
                    e.Graphics.DrawLine(p, cx - r, cy + r, cx + r, cy - r);
                }
            }
        }
    }

    // 标题条上的纯占位块：自己不画任何东西，只请父级 HeadPanel 把背景（含四角圆角
    // 和底边的抗锯齿过渡）画过来。用普通 Panel 实填深色的话，底边那一行会被压成纯色，
    // 在标题条下沿冒出一条 10px 长的深色细线
    class HeadSpacer : Control
    {
        public HeadSpacer()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                   | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            TabStop = false;
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            base.OnPaintBackground(e);   // 透明底会转交给父级 HeadPanel 绘制，圆角和抗锯齿都跟得住
        }
        protected override void OnPaint(PaintEventArgs e) { }
    }

    // 抗锯齿圆角按钮：不裁剪 Region，纯自绘，边缘平滑不发毛
    class RoundButton : Button
    {
        public int Radius = 8;
        public Color FillColor = Color.Empty;
        public Color HoverColor = Color.Empty;
        bool hover = false;

        public RoundButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            TabStop = false;
            MouseEnter += delegate { hover = true; Invalidate(); };
            MouseLeave += delegate { hover = false; Invalidate(); };
            MouseDown += delegate { Invalidate(); };
            MouseUp += delegate { Invalidate(); };
            EnabledChanged += delegate { Invalidate(); };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color back = BackColor;
            if (Parent != null && back.A < 255) back = Parent.BackColor;
            e.Graphics.Clear(back);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill = (FillColor == Color.Empty) ? back : FillColor;
            if (!Enabled) fill = Pal.DisabledFill;
            else if (hover && HoverColor != Color.Empty) fill = HoverColor;

            using (GraphicsPath gp = RoundPath(r, Radius))
            {
                using (SolidBrush b = new SolidBrush(fill))
                    e.Graphics.FillPath(b, gp);
                // 1px 细描边：边缘更利落，不那么“毛”
                Color line = hover ? Pal.BtnBorderHover : Pal.BtnBorder2;
                if (!Enabled) line = Pal.DisabledBorder;
                using (Pen p = new Pen(line, 1f))
                    e.Graphics.DrawPath(p, gp);
            }

            Color fc = Enabled ? ForeColor : Pal.InkDisabled;
            TextRenderer.DrawText(e.Graphics, Text, Font,
                new Rectangle(0, 0, Width, Height), fc,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    // ====== 统一菜单风格（白底 + 浅蓝选中 + 蓝色对勾）======
    class FlatMenuColorTable : ProfessionalColorTable
    {
        static Color SelB { get { return Pal.AccentSoft; } }
        static Color SelB2 { get { return Pal.AccentSoftHover; } }
        static Color Hair { get { return Pal.Hair; } }
        static Color Edge { get { return Pal.Edge; } }
        public override Color ToolStripDropDownBackground { get { return Pal.Surface; } }
        public override Color ImageMarginGradientBegin { get { return Pal.Surface; } }
        public override Color ImageMarginGradientMiddle { get { return Pal.Surface; } }
        public override Color ImageMarginGradientEnd { get { return Pal.Surface; } }
        public override Color MenuBorder { get { return Edge; } }
        public override Color MenuItemBorder { get { return Color.Empty; } }
        public override Color MenuItemSelected { get { return SelB; } }
        public override Color MenuItemSelectedGradientBegin { get { return SelB; } }
        public override Color MenuItemSelectedGradientEnd { get { return SelB; } }
        public override Color SeparatorDark { get { return Hair; } }
        public override Color SeparatorLight { get { return Hair; } }
        public override Color CheckBackground { get { return SelB; } }
        public override Color CheckSelectedBackground { get { return SelB2; } }
        public override Color CheckPressedBackground { get { return SelB2; } }
    }

    // 菜单自绘：白底、圆角浅蓝悬停、深蓝字、蓝色分隔线、蓝色对勾
    class FlatMenuRenderer : ToolStripProfessionalRenderer
    {
        public FlatMenuRenderer() : base(new FlatMenuColorTable()) { RoundedEdges = false; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(Pal.Surface)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (Pen p = new Pen(Pal.Edge))
                e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            ToolStripItem it = e.Item;
            if (!it.Selected && !it.Pressed) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(3, 1, it.Width - 6, it.Height - 2);
            using (System.Drawing.Drawing2D.GraphicsPath gp = RoundPath(r, 6))
            using (SolidBrush b = new SolidBrush(it.Pressed ? Pal.AccentSoftHover : Pal.AccentSoft))
                e.Graphics.FillPath(b, gp);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (!e.Item.Enabled) e.TextColor = Pal.InkDisabled;
            else if (e.Item.Selected || e.Item.Pressed) e.TextColor = Pal.Accent;
            else e.TextColor = Pal.Ink;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using (Pen p = new Pen(Pal.Hair))
            {
                int y = e.Item.Height / 2;
                e.Graphics.DrawLine(p, 12, y, e.Item.Width - 12, y);
            }
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Rectangle b = e.ImageRectangle;
            if (b.Width < 8 || b.Height < 8) b = new Rectangle(7, (e.Item.Height - 12) / 2, 11, 11);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen p = new Pen(Pal.Accent, 1.8f))
            {
                p.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                Point[] pts = new Point[] {
                    new Point(b.Left + (int)(b.Width * 0.10), b.Top + (int)(b.Height * 0.52)),
                    new Point(b.Left + (int)(b.Width * 0.38), b.Top + (int)(b.Height * 0.78)),
                    new Point(b.Left + (int)(b.Width * 0.92), b.Top + (int)(b.Height * 0.22))
                };
                e.Graphics.DrawLines(p, pts);
            }
        }
    }

    // 菜单统一穿上这身衣服：白底、字号 9.5、行高加大、上下留白
    static void StyleMenu(ContextMenuStrip m)
    {
        m.Renderer = new FlatMenuRenderer();
        m.Font = new Font("Microsoft YaHei UI", 9.5f);
        m.ShowImageMargin = true;
        m.ImageScalingSize = new Size(16, 16);
        m.Padding = new Padding(2, 4, 2, 4);
        foreach (ToolStripItem it in m.Items)
        {
            it.Padding = new Padding(it.Padding.Left, 5, it.Padding.Right, 5);
            // v2.8.2：字体逐个显式对齐菜单基准（含二级子菜单）。
            // 以前只设 m.Font 靠继承，个别机器上继承没生效，
            // 结果某一项字族/字号跟别人不一样，看着突兀。
            ApplyMenuFont(it, m.Font);
        }
    }

    // v2.8.2：把菜单项（含所有下级子菜单）的字体强制对齐到同一份基准，不依赖继承链
    static void ApplyMenuFont(ToolStripItem it, Font f)
    {
        if (it == null || f == null) return;
        it.Font = f;
        ToolStripDropDownItem dd = it as ToolStripDropDownItem;
        if (dd != null)
        {
            foreach (ToolStripItem sub in dd.DropDownItems)
                ApplyMenuFont(sub, f);
        }
    }

    // 弹窗按钮统一做法：圆角，主按钮蓝底白字
    static RoundButton MakeDialogButton(string text, int w, bool primary)
    {
        RoundButton b = new RoundButton();
        b.Text = text;
        b.Size = new Size(w, 28);
        b.Radius = 8;
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

    // 列表行背景：选中 = 浅蓝底 + 左侧 3px 蓝条（比整行深蓝白字清爽，正文还看得清）
    static void DrawListRowBg(Graphics g, Rectangle b, bool sel)
    {
        using (SolidBrush bg = new SolidBrush(sel ? Pal.AccentSoft : Pal.Surface))
            g.FillRectangle(bg, b);
        using (Pen p = new Pen(Pal.BorderFaint))
            g.DrawLine(p, b.Left + 14, b.Bottom - 1, b.Right - 14, b.Bottom - 1);
        if (!sel) return;
        using (SolidBrush bar = new SolidBrush(Pal.Accent))
            g.FillRectangle(bar, new Rectangle(b.Left, b.Top, 3, b.Height));
    }

    // 底栏上沿一条细线，把按钮区和列表分开
    static void HairlineTop(Panel p)
    {
        p.Paint += delegate (object s, PaintEventArgs e) {
            using (Pen pen = new Pen(Pal.Hair))
                e.Graphics.DrawLine(pen, 0, 0, p.Width, 0);
        };
    }

    Button MakeFootButton(string text, int w)
    {
        RoundButton b = new RoundButton();
        b.Text = text;
        b.Width = w;
        b.Height = 26;
        b.Radius = 8;
        b.Margin = new Padding(2, 2, 2, 2);
        // 底色原来是 242,244,249，和底栏 247,247,248 只差 5 级灰，实测几乎看不见按钮轮廓，
        // 只剩一排文字悬在底栏上。改纯白 + 稍深的描边，层次立起来又不会抢正文的注意力
        b.FillColor = Pal.Surface;
        b.HoverColor = Pal.AccentTint;
        b.BackColor = Pal.BgPage;
        b.ForeColor = Pal.InkSecondary;
        b.Font = new Font("Microsoft YaHei UI", 9f);
        b.Cursor = Cursors.Hand;
        return b;
    }

    // ====== 自适应布局：窗口怎么拖、字号怎么调，都不让文字被切、被挤、凭空消失 ======
    // 思路：所有边距/按钮高度/标题条高度都乘一个 uiScale，
    // uiScale = 屏幕DPI × 字号倍率（字号越大，框子也跟着长，字才装得下）
    // 控件引用缓存：拖边框时每帧要按名字找十来个控件，Controls.Find 是递归搜索，
    // 一帧几十次下来能吃掉几毫秒，够让窗口跟不上鼠标
    readonly System.Collections.Generic.Dictionary<string, Control> _ctlCache =
        new System.Collections.Generic.Dictionary<string, Control>();

    Control FindCtl(string name)
    {
        Control hit;
        if (_ctlCache.TryGetValue(name, out hit) && hit != null && !hit.IsDisposed) return hit;
        Control[] found = Controls.Find(name, true);
        hit = (found != null && found.Length > 0) ? found[0] : null;
        if (hit != null) _ctlCache[name] = hit;
        return hit;
    }

    void CalcScale()
    {
        // 原来每帧 new 一个 Graphics 来问 DPI，一帧几毫秒；Control.DeviceDpi 是现成的属性，
        // 拖动时每帧都要算缩放，这里省下的就是“跟手”的程度
        float dpi = 1f;
        try { int dd = DeviceDpi; if (dd > 0) dpi = dd / 96f; }
        catch { dpi = 1f; }
        float fs = curFontPt / 11f;
        if (fs < 1f) fs = 1f;
        if (fs > 1.55f) fs = 1.55f;
        uiScale = dpi * fs;
    }

    int S(int v) { return (int)Math.Round(v * uiScale); }

    // ===== 字体缓存：每次改字号/拖动窗口都会重新排版，如果每次都 new Font，
    // GDI 句柄会越攒越多，攒满之后整窗就会渲染成白板（这正是"放大后一片空白"的真凶）。
    Font _fBox, _fClose, _fMax, _fBtn, _fGo, _fInput, _fInputHint, _fTip;
    Font Cached(ref Font slot, float pt, FontStyle style)
    {
        if (pt < 5f) pt = 5f;
        if (slot != null && Math.Abs(slot.SizeInPoints - pt) < 0.05f && slot.Style == style) return slot;
        slot = new Font("Microsoft YaHei UI", pt, style);
        return slot;
    }
    public int SelfFontPt() { return curFontPt; }
    public int SelfGrown() { return autoGrownH; }

    // 自检：把关键控件的实际几何和"文字需要多少像素"都打出来，
    // 这样不用截图也能判断有没有切字、挤压、越界
    public string SelfDump()
    {
        StringBuilder sb = new StringBuilder();
        try
        {
            sb.AppendLine("ClientSize=" + ClientSize.Width + "x" + ClientSize.Height);
            sb.AppendLine("MinimumSize=" + MinimumSize.Width + "x" + MinimumSize.Height);
            sb.AppendLine("FontPt=" + curFontPt + " uiScale=" + uiScale.ToString("0.00") + " grown=" + autoGrownH);
            foreach (Control c in Controls)
                sb.AppendLine("CTL " + c.Name + " " + c.GetType().Name + " bounds=" + c.Bounds + " visible=" + c.Visible);

            if (box != null)
            {
                int lineH = (int)Math.Ceiling(box.Font.GetHeight());
                int need = -1;
                try { need = box.GetPositionFromCharIndex(Math.Max(0, box.TextLength - 1)).Y + lineH; } catch { }
                sb.AppendLine("BOX client=" + box.ClientRectangle + " textLen=" + box.TextLength
                    + " font=" + box.Font.SizeInPoints.ToString("0.0") + "pt lineH=" + lineH
                    + " needH=" + need + " overflow=" + (need > box.ClientRectangle.Height));
                sb.AppendLine("BOX scrollBars=" + box.ScrollBars + " wordWrap=" + box.WordWrap);
                sb.AppendLine("BOXLAYOUT bounds=" + box.Bounds + " margin=" + box.Margin + " dock=" + box.Dock
                    + " parent=" + (box.Parent != null ? box.Parent.Name + " client=" + box.Parent.ClientSize + " pad=" + box.Parent.Padding : "null"));
                Control gp = box.Parent != null ? box.Parent.Parent : null;
                if (gp != null) sb.AppendLine("BOXGRAND " + gp.Name + " client=" + gp.ClientSize + " pad=" + gp.Padding + " bounds=" + gp.Bounds);
            }
            Button[] bs = new Button[] { btnCopyLine, btnSpeak, btnApprox, btnTone, btnMore };
            for (int i = 0; i < bs.Length; i++)
            {
                if (bs[i] == null) continue;
                Size need = TextRenderer.MeasureText(bs[i].Text, bs[i].Font);
                sb.AppendLine("BTN " + bs[i].Text + " bounds=" + bs[i].Bounds
                    + " needText=" + need.Width + "x" + need.Height
                    + " clipped=" + (need.Width > bs[i].Width - 4 || need.Height > bs[i].Height - 2));
            }
            if (bar != null) sb.AppendLine("BAR height=" + bar.Height + " rows=" + (bar.Controls.Count > 0 ? (bar.Height / Math.Max(1, bar.Controls[0].Height + 4)) : 0));
            Control footP = FindCtl("foot");
            if (footP != null) sb.AppendLine("FOOT height=" + footP.Height + " client=" + footP.ClientRectangle);
            Control inP = FindCtl("inputBar");
            if (inP != null) sb.AppendLine("INPUTBAR height=" + inP.Height + " bounds=" + inP.Bounds);
            if (tip != null) sb.AppendLine("TIP text=" + tip.Text + " bounds=" + tip.Bounds);
        }
        catch (Exception ex) { sb.AppendLine("dumpError=" + ex.Message); }
        return sb.ToString();
    }

    // 宽度不够就把按钮文字换成短说法，绝不靠“切一半”硬塞
    void FitButtonText(Button b, string full, string brief)
    {
        if (b == null) return;
        b.Text = full;
        if (b.Width <= 0 || b.Font == null) return;
        try
        {
            Size sz = TextRenderer.MeasureText(full, b.Font);
            if (sz.Width > b.Width - S(14)) b.Text = brief;
        }
        catch { }
    }

    void RefreshFootLabels()
    {
        string toneName = (tone == "formal") ? "正式" : (tone == "casual" ? "口语" : "自动");
        FitButtonText(btnCopyLine, "复制这句", "复制");
        FitButtonText(btnSpeak, "读一遍", "朗读");
        FitButtonText(btnApprox, "谐音 " + (showApprox ? "开" : "关"), "谐音" + (showApprox ? "开" : "关"));
        FitButtonText(btnTone, "语气 " + toneName, toneName);
        FitButtonText(btnMore, "更多 ▾", "更多");
        // 文字换了，缓存下来的按钮自然宽度就作废，下一次排版必须重新量
        _btnNatScale = -1f;
    }

    // 底栏按钮条布局：按可用宽度算格宽，五个放不下就折成两排（三+二），并同步把底栏加高。
    // 拖动过程中每帧都会调它，所以只做测量，别在这里改 Font 之类昂贵的东西。
    // forceMeasure=false 表示这是拖动中的跟手帧：按钮宽度全部取缓存，一个 MeasureText 都不跑
    void LayoutFootButtons(Panel footP, Panel tipStripP, bool forceMeasure = true)
    {
        if (bar == null || footP == null) return;
        float textScale = Math.Min(uiScale, 1.35f);
        // 左右内边距跟白卡里的正文对齐：body 的 S(10) + 卡片侧边 min(S(14),16)。
        // 以前这里是写死的 S(14)，实测输入框左边缘 39px、按钮左边缘 28px，差 11px，
        // 上下两列像错开了半格。改成同一套边距后，左右两端都能落在同一条竖线上
        int sidePad = S(10) + Math.Min(S(14), 16) + S(1);
        footP.Padding = new Padding(sidePad, S(5), sidePad, S(4));
        int gap = S(4);
        int avail = footP.ClientSize.Width - footP.Padding.Horizontal;
        if (avail < S(160)) avail = S(380);
        avail -= S(2);   // 留 2px 余量，四舍五入差 1px 也会把最后一个按钮挤到下一排

        // 关键：拖动中先用缓存宽度换行，不做文字测量。measure 为 true 时才真正 MeasureText
        float btnFontPt = 9f * textScale;
        Font bf = Cached(ref _fBtn, btnFontPt, FontStyle.Regular);
        bool measure = forceMeasure || _btnNatW == null || _btnNatW.Length != 5
                       || Math.Abs(_btnNatScale - textScale) > 0.001f;
        int btnH = measure ? Math.Max(S(26), TextRenderer.MeasureText("测", bf).Height + S(8)) : _btnH;
        if (btnH <= 0) btnH = Math.Max(S(26), 26);

        // 按钮宽度按各自文字真实宽度算，再由自己决定换几行：
        // 之前用“每格至少 88”硬折行，414 宽、11 号字明明放得下也被折成两排
        Button[] btns = new Button[] { btnCopyLine, btnSpeak, btnApprox, btnTone, btnMore };
        int[] natW = new int[btns.Length];
        if (measure)
        {
            for (int i = 0; i < btns.Length; i++)
            {
                Button b = btns[i];
                int tw = (b == null || b.Text == null) ? S(60) : TextRenderer.MeasureText(b.Text, bf).Width + S(18);
                tw = Math.Max(tw, S(52));
                if (tw > avail) tw = Math.Max(S(52), avail);
                natW[i] = tw;
            }
            _btnNatW = (int[])natW.Clone();
            _btnH = btnH;
            _btnNatScale = textScale;
        }
        else
        {
            Array.Copy(_btnNatW, natW, natW.Length);
            for (int i = 0; i < natW.Length; i++)
            {
                if (natW[i] > avail) natW[i] = Math.Max(S(52), avail);
            }
        }
        int totalW = gap * (btns.Length - 1);   // 最后一颗不带右外边距，右端才能贴齐边距线
        for (int i = 0; i < btns.Length; i++) totalW += natW[i];

        if (totalW <= avail)
        {
            // 一排放得下就把多出来的宽度平分给每个按钮，看上去整齐
            int extra = (avail - totalW) / btns.Length;
            int used = 0;
            for (int i = 0; i < btns.Length; i++) { natW[i] += extra; used += natW[i]; }
            used += gap * (btns.Length - 1);
            if (used < avail) natW[btns.Length - 1] += (avail - used);
        }
        else
        {
            // 差一点点就放不下（中文字宽测量有零点几像素误差），先把每个按钮挤掉几个像素，
            // 别为了挤掉一行就把“语气 自动”折成两排
            int excess = totalW - avail;
            int floor = S(48);
            int shrinkable = 0;
            for (int i = 0; i < btns.Length; i++) shrinkable += Math.Max(0, natW[i] - floor);
            if (shrinkable >= excess && shrinkable > 0)
            {
                int cutted = 0;
                for (int i = 0; i < btns.Length; i++)
                {
                    int room = Math.Max(0, natW[i] - floor);
                    int cut = (int)Math.Round((double)room * excess / shrinkable);
                    if (cut > room) cut = room;
                    natW[i] -= cut; cutted += cut;
                }
                int left = excess - cutted;
                for (int i = btns.Length - 1; i >= 0 && left > 0; i--)
                {
                    int room = Math.Max(0, natW[i] - floor);
                    int cut = Math.Min(room, left);
                    natW[i] -= cut; left -= cut;
                }
            }
        }

        for (int i = 0; i < btns.Length; i++)
        {
            Button b = btns[i];
            if (b == null) continue;
            b.Dock = DockStyle.None;
            b.Font = bf;
            b.Height = btnH;
            b.Width = natW[i];
            RoundButton rb = b as RoundButton;
            if (rb != null) rb.Radius = btnH / 2;
            b.Margin = new Padding(0, 0, (i == btns.Length - 1) ? 0 : gap, gap);
        }

        // 关键：先把按钮条撑到真实可用宽度，否则它拿上一轮的旧宽度去换行
        bar.Width = Math.Max(S(120), avail);
        // 自己数行数，别再问 bar.Height：AutoSize 关掉后它的高度就是我们定的
        int rowCount = 1, acc = 0;
        for (int i = 0; i < btns.Length; i++)
        {
            int bw = (btns[i] == null) ? 0 : btns[i].Width;
            if (acc > 0 && acc + bw + gap > avail) { rowCount++; acc = 0; }
            acc += bw + gap;
        }
        if (rowCount < 1) rowCount = 1;
        bar.Height = rowCount * (btnH + gap);
        bar.PerformLayout();

        int barH = bar.Height;
        if (barH < btnH + gap) barH = btnH + gap;   // 极端情况下兜底
        int footPad = footP.Padding.Top + footP.Padding.Bottom;
        int tipH = (tipStripP != null) ? tipStripP.Height : S(18);
        // 底栏高度 = 按钮条 + 提示条 + 内外边距，宁可多给 2px 也不让最后一行被切
        footP.Height = barH + tipH + footPad + S(2);

        // v2.3：提示条右侧的文字入口按新宽度重新贴边（5 个按钮已经全部摆完，不受影响）
        MlPlaceLink(tipStripP);
    }

    void ApplyMetrics() { ApplyMetricsCore(false); }

    // light=true 是拖边框时每帧调的轻量版：几何、字号、内边距、底栏全部按当前尺寸连续算到位，
    // 只跳过三件要测量文字的事（刷新底栏文案、输入框吸附行数、白卡按内容收缩）。
    // 这样拖动过程中画面就是“最终样子”的连续变化，松手后不会再整体换一次样式，
    // 也就不会出现用户说的那种突兀过渡
    void ApplyMetricsCore(bool light)
    {
        if (layingOut) return;
        layingOut = true;
        try
        {
            CalcScale();
            Panel headHost = FindCtl("headHost") as Panel;
            HeadPanel head = FindCtl("head") as HeadPanel;
            Panel bodyP = FindCtl("body") as Panel;
            // 最大尺寸直接封到工作区：再怎么拖也不会拖出屏幕，省得下半截看不见还以为是没画出来
            try
            {
                Rectangle waMax = Screen.FromControl(this).WorkingArea;
                Size mx = new Size(waMax.Width, waMax.Height);
                // 最大化状态下别碰 MaximumSize：封着它会让最大化停摆，只露出旧尺寸那一块
                if (WindowState != FormWindowState.Maximized && MaximumSize != mx) MaximumSize = mx;
            }
            catch { }

            Panel cardP = FindCtl("card") as Panel;
            Panel footP = FindCtl("foot") as Panel;
            Panel tipStripP = FindCtl("tipStrip") as Panel;
            Panel inputBarP = FindCtl("inputBar") as Panel;
            Panel inputWrapP = FindCtl("inputWrap") as Panel;
            Control closeB = FindCtl("close");
            Control maxB = FindCtl("maxb");

            if (headHost != null) { headHost.Height = S(50); headHost.Padding = new Padding(S(10), S(10), S(10), 0); }
            if (head != null)
            {
                head.Radius = S(8);
                head.ScaleF = uiScale;
                head.Padding = new Padding(0, 0, S(5), 0);
                head.Invalidate();
            }
            if (closeB != null) { closeB.Width = S(36); closeB.Font = Cached(ref _fClose, 12f * uiScale, FontStyle.Regular); }
            if (maxB != null) { maxB.Width = S(34); maxB.Font = Cached(ref _fMax, 10f * uiScale, FontStyle.Regular); }
            Control gapB = FindCtl("headGap");
            if (gapB != null) gapB.Width = S(10);
            if (bodyP != null) bodyP.Padding = new Padding(S(10), S(9), S(10), S(11));
            if (cardP != null)
            {
                // 左右内边距别跟着字号无限放大：24 号字时若还按 22px 走，
                // 右边再叠一条滚动条，看着就像字被挤到左边、右边空了一大块
                int sidePad = Math.Min(S(14), 16);
                cardP.Padding = new Padding(sidePad, Math.Min(S(12), 18), sidePad, S(9));
            }
            if (inputBarP != null) { inputBarP.Height = S(40); inputBarP.Padding = new Padding(0, S(10), 0, 0); }
            if (inputWrapP != null) inputWrapP.Padding = new Padding(S(10), S(6), S(10), S(6));

            float textScale = Math.Min(uiScale, 1.35f);
            if (btnGo != null) { btnGo.Width = S(68); btnGo.Font = Cached(ref _fGo, 9f * textScale, FontStyle.Bold); }
            if (gapGo != null) gapGo.Width = S(10);
            if (inputBox != null) inputBox.Font = Cached(ref _fInput, 9.5f * textScale, FontStyle.Regular);
            if (inputHint != null)
            {
                inputHint.Font = Cached(ref _fInputHint, 9f * textScale, FontStyle.Regular);
                inputHint.AutoEllipsis = true;
                // 占位提示按输入框实际宽度选短句，字号放大/窗口变窄时自动退成短版
                int hintW = (inputBox != null && inputBox.ClientSize.Width > S(40))
                          ? inputBox.ClientSize.Width - S(10) : Width - S(140);
                if (hintW < S(80)) hintW = S(80);
                string[] hints = new string[] {
                    "在这里打字：中文 → 英文，英文 → 中文，回车就查",
                    "在这里打字：中文→英，英文→中，回车查",
                    "在这里打字，回车就查",
                    "在这里打字…"
                };
                for (int hi = 0; hi < hints.Length; hi++)
                {
                    int need = TextRenderer.MeasureText(hints[hi], inputHint.Font).Width;
                    if (need <= hintW || hi == hints.Length - 1) { inputHint.Text = hints[hi]; break; }
                }
            }
            if (tip != null) tip.Font = Cached(ref _fTip, 8.5f * textScale, FontStyle.Regular);
            if (tipStripP != null)
            {
                tipStripP.Height = Math.Max(S(18), (tip != null ? tip.Font.Height : S(14)) + S(4));
                tipStripP.Padding = new Padding(0, 0, S(22), 0);
            }

            LayoutFootButtons(footP, tipStripP);

            // v2.3 第 2 步：小窗已经是独立窗口，主窗怎么变都不去动用户摆好的位置，
            // 只做一次"别跑到屏幕外面"的兜底（换分辨率、副屏拔掉这类情况）
            if (MlVisible) MlKeepOnScreen();

            if (!light)
            {
                RefreshFootLabels();
                SnapBoxToLines(cardP);
                // 卡片跟着内容缩：内容少的时候白卡就短一截，底下露出浅灰底，不再整片白里空一大块
                FitCardToContent(cardP, bodyP, inputBarP);
            }

            // 最小尺寸跟着字号走：保证“标题+正文至少一行+输入框+底栏”永远装得下
            int hMin = (headHost != null ? headHost.Height : S(50))
                     + (footP != null ? footP.Height : S(66))
                     + (bodyP != null ? bodyP.Padding.Vertical : S(20))
                     + (cardP != null ? cardP.Padding.Vertical : S(20))
                     + (inputBarP != null ? inputBarP.Height : S(40))
                     + Math.Max(S(60), (int)(box.Font.Height * 2.2f));
            // 最小宽度不跟着字号无限放大（最多 380）：字号再大也只是正文里多换几行，
            // 绝不能把窗口硬撑宽——撑宽之后用户就再也拖不小了，看着就像"显示不全"
            int minW = Math.Min(S(320), 380);
            if (minW < 300) minW = 300;
            // 最小尺寸说的是窗口外围，那圈 8px 拖拽边也得算进去，不然内容真的装不下
            // 拖动中先不写：改 MinimumSize 会让系统当场把窗口尺寸掰回去，手感像被拽了一下
            if (!resizing) MinimumSize = new Size(minW + Padding.Horizontal, hMin + Padding.Vertical);
            Dbg("ApplyMetrics minW=" + minW + " hMin=" + hMin + " client=" + ClientSize + " outer=" + Width + "x" + Height);
            // 只在窗口真的显示出来、尺寸也合理时才做“最小尺寸自愈”。
            // 未显示阶段 ClientSize 会是 0x0/160x42 这种假尺寸，一按最小尺寸兜底就会
            // 把用户存好的 500x800 直接吞成 320x371（这正是“窗口自己变小、字挤在一起”的根因）
            if (!resizing && ClientSize.Width > 60 && ClientSize.Height > 60
                && (ClientSize.Width < MinimumSize.Width || ClientSize.Height < MinimumSize.Height))
            {
                int w = Math.Max(ClientSize.Width, MinimumSize.Width);
                int h = Math.Max(ClientSize.Height, MinimumSize.Height);
                Rectangle wa = Screen.FromControl(this).WorkingArea;
                if (h > wa.Height - 40) h = Math.Max(MinimumSize.Height, wa.Height - 40);
                ClientSize = new Size(w, h);
            }
            if (headHost != null) headHost.Invalidate(true);
        }
        catch { }
        finally { layingOut = false; }
    }

    // 正文区高度对齐到“整行”：不然滚动时最后一行总被窗口边缘劈成半截，
    // 看着就是“显示不全”。这里把卡片底部内边距补掉零头，让可见高度正好是整数行
    void SnapBoxToLines(Panel cardP)
    {
        if (box == null || cardP == null) return;
        try
        {
            int baseBottom = S(9);
            int curExtra = Math.Max(0, cardP.Padding.Bottom - baseBottom);
            int naturalH = box.ClientSize.Height + curExtra;
            int lineH = (int)Math.Ceiling(box.Font.GetHeight());
            if (lineH <= 0 || naturalH <= lineH * 2) return;
            int slack = naturalH % lineH;
            // 只补小零头；差得多说明布局刚变，等下轮再对齐，避免来回抖
            if (slack > 0 && slack < lineH)
                cardP.Padding = new Padding(cardP.Padding.Left, cardP.Padding.Top, cardP.Padding.Right, baseBottom + slack);
        }
        catch { }
    }

    // 白色卡片跟着内容走：字少的时候卡片就矮一点，底下露出浅灰底，
    // 不会出现"窗口很高、卡片撑满、正文只写上半截、下半截全是白茫茫"的情况
    void FitCardToContent(Panel cardP, Panel bodyP, Panel inputBarP)
    {
        if (cardP == null || bodyP == null || box == null) return;
        try
        {
            int availW = bodyP.ClientSize.Width - bodyP.Padding.Horizontal;
            int availH = bodyP.ClientSize.Height - bodyP.Padding.Vertical;
            if (availW < S(60) || availH < S(60)) return;

            int inputH = (inputBarP != null) ? inputBarP.Height : S(40);
            // 出错时多出来的那条「再试一次」也要算进卡片高度，否则它会把错误正文挤掉一截
            int errH = (errBar != null && errBar.Visible) ? errBar.Height : 0;
            int lineH = (int)Math.Ceiling(box.Font.GetHeight());
            if (lineH <= 0) lineH = S(20);

            // 先按最终宽度排一次版，否则量到的是旧宽度下的行数
            cardP.Dock = DockStyle.Top;
            if (cardP.Width != availW) cardP.Width = availW;
            cardP.PerformLayout();

            int needBox = box.TextLength <= 0 ? lineH * 2 : BoxContentHeight() + S(2);

            int need = needBox + inputH + errH + cardP.Padding.Vertical;
            int minH = inputH + errH + cardP.Padding.Vertical + Math.Max(S(36), lineH + S(8));
            if (need < minH) need = minH;      // 至少留两行高的正文区，别缩成一条缝

            // 窗口被拉得比内容高时，白卡直接铺满整片可用高度。
            // 以前卡片只长到"内容需要的高度"，拖大窗口后下面和右边就露出一大块浅灰，
            // 看着像窗口没画完、漏了一块空白；现在多出来的地方是白卡自己的留白，视线统一
            int target = need;
            if (availH - need > S(24)) target = availH;
            if (target > availH) target = availH;   // 内容太多就撑满，正文自己滚动
            if (target < minH) target = Math.Min(minH, availH);

            Dbg("FitCard avail=" + availW + "x" + availH + " textLen=" + box.TextLength
                + " needBox=" + needBox + " need=" + need + " target=" + target + " cardH=" + cardP.Height
                + " dock=" + cardP.Dock + " boxH=" + box.Height);
            if (cardP.Height != target) cardP.Height = target;
            // 宽度也顺手钉一次：拉宽窗口时卡片宽度靠 Dock 传递，偶尔慢一帧，
            // 右边就会漏一条灰边
            if (cardP.Width != availW) cardP.Width = availW;
        }
        catch { }
    }

    // 文字比正文区多的时候，把窗口往下长（不超过屏幕），让最后一行真的看得见；
    // 字号调小后，再把之前自动长出来的高度收回去
    void FitContent()
    {
        if (box == null || box.TextLength == 0) return;
        // 最大化状态下高度由屏幕决定，不能再去长高/缩回，否则会把最大化"顶掉"
        if (WindowState != FormWindowState.Normal) return;
        try
        {
            int lineH = (int)Math.Ceiling(box.Font.GetHeight());
            if (lineH <= 0) lineH = S(20);
            // 按“从顶部算起”的总高度量，否则用户滚过之后这里会误判成内容变少，
            // 于是窗口自己往回收高度，可用空间一变，滚动条就时有时无
            int need = BoxContentHeight() + S(10);
            int have = box.ClientRectangle.Height;
            int delta = need - have;
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            if (delta > S(4))
            {
                // 只补一点点：内容多出很多时宁可让正文滚动，也绝不把窗口自己撑成巨型长条
                // （以前一路长到 1300 多像素，看着就像窗口“自己抽风变大”）
                int cap = S(120);
                int room = Math.Min(wa.Height - Height - S(40), cap - autoGrownH);
                if (delta <= S(160))
                {
                    int add = Math.Min(delta, Math.Max(0, room));
                    if (add > S(4)) { autoGrownH += add; Height += add; }
                }
            }
            else if (delta < -S(4) && autoGrownH > 0)
            {
                int cut = Math.Min(-delta, autoGrownH);
                int nh = Height - cut;
                if (nh < MinimumSize.Height) nh = MinimumSize.Height;
                int realCut = Height - nh;
                if (realCut > 0) { autoGrownH -= realCut; Height = nh; }
            }
        }
        catch { }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // 显示后再校准一次：没被用户手动拖过的情况下，把启动时被误压小的尺寸还原
        try
        {
            if (wantClient.Width > 0 && wantClient.Height > 0
                && (ClientSize.Width < wantClient.Width || ClientSize.Height < wantClient.Height))
            {
                int w = wantClient.Width, h = wantClient.Height;
                Rectangle wa = Screen.FromControl(this).WorkingArea;
                if (w > wa.Width) w = wa.Width;
                if (h > wa.Height - 40) h = wa.Height - 40;
                ClientSize = new Size(w, h);
            }
        }
        catch { }

        // v2.3 第 2 步：拖文件进来就自动分流（中文/英文进主界面，其它语言进小窗）。
        // 只加拖放入口，主界面任何控件的位置、大小、样式都没动。
        try { MlInstallDrop(); } catch { }
    }

    System.Windows.Forms.Timer layoutTimer;

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        SyncMaxGlyph();   // 最大化/还原时把标题条上的小图标跟着换
        // 老系统那套圆角是用 Region 硬裁出来的，Region 只对“设上去那一刻的尺寸”有效：
        // 一放到最大，能看见的还只有原来那一小块，其余全被裁掉——看着就是“点了最大化没反应”。
        // 所以尺寸一变就把旧裁剪丢掉，圆角交给 DWM 自己画
        ClearClipRegion();
        if (layingOut || !IsHandleCreated) return;
        // 拖边框时 Resize 每像素触发一次，ApplyMetrics 要做大量文字测量、还会改
        // MinimumSize 触发新一轮重排，同步跑下来界面就卡成一片白/缺字。
        // 所以拖的当下只做最便宜的事：把“会改变可用高度”的几处立刻同步到位，
        // 完整重排（含文字测量、卡片按内容收缩）合并到停顿后再跑一次
        resizing = true;
        LiveRelayout();
        if (layoutTimer == null)
        {
            layoutTimer = new System.Windows.Forms.Timer();
            layoutTimer.Interval = 40;
            layoutTimer.Tick += delegate
            {
                layoutTimer.Stop();
                // 松手 40ms 后才允许“最小尺寸自愈”，拖动中弹回去是最伤手感的
                if (dragSizing) return;   // 还在拖：定格画面顶着，重排留给松手那一刻
                resizing = false;
                if (!layingOut && IsHandleCreated) ApplyMetrics();
            };
        }
        layoutTimer.Stop();
        layoutTimer.Start();
    }

    // 拖动中的实时布局：底栏按钮折行、底栏高度、白卡高度当场算清，
    // 让每一帧都是自洽的完整画面。以前只把白卡拉满、底栏却还停在上一个尺寸，
    // 松手瞬间底栏再“跳”一下，看着就是过渡不自然、像有残影
    int _lastLiveTick = 0;

    void LiveRelayout()
    {
        // 鼠标正按在边框上拖的整段时间，走“跟手模式”：只改必须跟着尺寸走的几个 Bounds，
        // 一个 MeasureText 都不跑、一个 Font/Padding 都不设。
        // 以前每帧都跑 ApplyMetricsCore：五个按钮各测一遍文字、逐个控件改字体边距、
        // 再强制 PerformLayout 两次，一帧几十毫秒，比屏幕刷新还慢；
        // 边框已经拖过去了、里面却还是上一帧的画面，过一会儿才“跳”出来——这就是那个延迟帧感
        if (dragSizing && !forceLegacyDrag) { DragFollow(); return; }

        // 不是鼠标拖动而是程序自己改尺寸（最大化/还原、改字号）时，才把完整几何算一遍；
        // 这时候慢一点无所谓，但一家都不能省
        int nowTick = Environment.TickCount;
        if (!forceLegacyDrag && resizing && nowTick - _lastLiveTick < 12) return;
        _lastLiveTick = nowTick;

        Panel bodyP = null, cardP = null;
        try
        {
            bodyP = FindCtl("body") as Panel;
            cardP = FindCtl("card") as Panel;
            if (bodyP == null || cardP == null) return;

            SuspendLayout();
            bodyP.SuspendLayout();

            ApplyMetricsCore(true);

            int availW = bodyP.ClientSize.Width - bodyP.Padding.Horizontal;
            int availH = bodyP.ClientSize.Height - bodyP.Padding.Vertical;
            if (availW >= S(60) && availH >= S(60))
            {
                if (cardP.Width != availW) cardP.Width = availW;
                if (cardP.Height != availH) cardP.Height = availH;
            }
        }
        catch { }
        finally
        {
            try { Panel fp = FindCtl("foot") as Panel; if (fp != null) { fp.ResumeLayout(true); fp.PerformLayout(); } } catch { }
            try { if (bodyP != null) { bodyP.ResumeLayout(true); bodyP.PerformLayout(); } } catch { }
            ResumeLayout(true);
            PerformLayout();
        }
    }

    // 拖动边框时的每帧动作：只让“必须跟着窗口变的东西”变——白卡尺寸、正文宽度、底栏按钮宽度。
    // 标题栏高度、内边距、字号这些只跟 DPI/字号有关的，整段拖动里本来就不会变，
    // 每帧重设一遍纯属自找卡顿（而且每次设置都会让系统重排、重绘一遍整窗）
    void DragFollow()
    {
        if (dragTestNoLayout) return;
        // 拖动整段时间里，内容区换成一张“定格画面”，随着边框一起拉伸。
        // 文字重排、按钮折行、卡片收缩这些事，留到松手后一次性做完。
        // 原来每帧都去实时重排：WinForms 一层层重新布局 + RichTextBox 按新宽度重排文字，
        // 一帧几十毫秒，鼠标每像素发一次消息，队列越堆越长——表现出来就是
        // 边框已经过去了，里面的东西慢半拍才“跳”出来
        if (BeginDragSnapshot()) return;

        try
        {
            Panel bodyP = FindCtl("body") as Panel;
            Panel cardP = FindCtl("card") as Panel;
            Panel footP = FindCtl("foot") as Panel;
            Panel tipStripP = FindCtl("tipStrip") as Panel;
            if (bodyP == null || cardP == null) return;

            int availW = bodyP.ClientSize.Width - bodyP.Padding.Horizontal;
            int availH = bodyP.ClientSize.Height - bodyP.Padding.Vertical;
            if (availW < S(60) || availH < S(60)) return;

            // 白卡铺满可用区域，拖动中不按内容收缩，松手后再决定要不要缩回来
            if (cardP.Width != availW || cardP.Height != availH)
                cardP.SetBounds(cardP.Left, cardP.Top, availW, availH);

            // 底栏用缓存宽度重排：按钮跟着窗口宽窄连续变，但不重新量文字
            LayoutFootButtons(footP, tipStripP, false);
        }
        catch { }
    }

    // ---- 拖动期的“定格画面” ----
    // 抓一张内容区快照盖在上面，拖动中来多少帧都只是把这张图拉伸一下，
    // 不触发任何子控件布局与文字重排，所以能真正贴着边框走
    Panel dragSnapOverlay;
    Bitmap dragSnapBmp;
    Dictionary<Control, bool> dragHidden;

    bool BeginDragSnapshot()
    {
        try
        {
            if (dragSnapOverlay != null) return true;
            Panel bodyP = FindCtl("body") as Panel;
            if (bodyP == null || bodyP.ClientSize.Width <= 0 || bodyP.ClientSize.Height <= 0) return false;

            // 抓整个客户区，再按 body 在窗口里的位置裁出来
            Rectangle br = bodyP.RectangleToScreen(bodyP.ClientRectangle);
            Rectangle fr = RectangleToScreen(ClientRectangle);
            int ox = br.Left - fr.Left, oy = br.Top - fr.Top;

            using (Bitmap whole = new Bitmap(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height),
                                              System.Drawing.Imaging.PixelFormat.Format24bppRgb))
            {
                // 这里必须抓“屏幕上的真实像素”，不能用 PrintWindow：
                // 正文那个富文本框不支持 WM_PRINT，抓出来是一片黑，盖上去就是黑块
                bool ok = false;
                try
                {
                    using (Graphics gb = Graphics.FromImage(whole))
                    {
                        // 先把整块铺成底色：窗口有部分在屏幕外时，那部分填底色而不是留黑
                        gb.Clear(bodyP.BackColor);
                        Rectangle need = new Rectangle(fr.Left, fr.Top, whole.Width, whole.Height);
                        Rectangle grab = Rectangle.Intersect(need, Screen.FromHandle(Handle).Bounds);
                        if (grab.Width > 0 && grab.Height > 0)
                        {
                            gb.CopyFromScreen(grab.Left, grab.Top,
                                              grab.Left - need.Left, grab.Top - need.Top, grab.Size,
                                              CopyPixelOperation.SourceCopy | CopyPixelOperation.CaptureBlt);
                            ok = true;
                        }
                    }
                }
                catch { ok = false; }

                if (!ok)
                {
                    // 极端情况（窗口被完全遮住等）退回 PrintWindow，宁可糊一点也不要黑
                    try
                    {
                        using (Graphics gb = Graphics.FromImage(whole))
                        {
                            IntPtr hdc = gb.GetHdc();
                            try { ok = PrintWindow(Handle, hdc, 2); } catch { ok = false; }
                            gb.ReleaseHdc(hdc);
                        }
                    }
                    catch { }
                }
                if (!ok) return false;

                Rectangle crop = new Rectangle(ox, oy, bodyP.ClientSize.Width, bodyP.ClientSize.Height);
                crop.Intersect(new Rectangle(0, 0, whole.Width, whole.Height));
                if (crop.Width <= 0 || crop.Height <= 0) return false;
                dragSnapBmp = whole.Clone(crop, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            }
            if (dragSnapBmp == null) return false;

            // 万一是黑图（被遮挡、抓拍失败），宁可放弃定格画面走普通路径，也不让用户看见黑块
            if (DarkRatio(dragSnapBmp) > 0.5f)
            {
                dragSnapBmp.Dispose();
                dragSnapBmp = null;
                return false;
            }

            Panel ov = new SnapPanel(dragSnapBmp);
            ov.Dock = DockStyle.Fill;
            ov.BackColor = bodyP.BackColor;
            ov.Name = "dragSnap";

            // 顺序很重要：先把定格画面盖上去，再把原控件收起来。
            // 反过来做的话，中间会有一小段“旧控件已隐藏、新画面还没上”的空档，
            // 那一下就是用户看到的黑闪
            bodyP.Controls.Add(ov);
            ov.BringToFront();
            dragSnapOverlay = ov;

            // 原来那堆控件收起来：它们不再参与每帧的布局和重绘，也就不可能慢半拍
            dragHidden = new Dictionary<Control, bool>();
            List<Control> kids = new List<Control>();
            foreach (Control c in bodyP.Controls) kids.Add(c);
            foreach (Control c in kids)
            {
                if (c == ov) continue;
                dragHidden[c] = c.Visible;
                c.Visible = false;
            }
            return true;
        }
        catch
        {
            EndDragSnapshot();
            return false;
        }
    }

    // 粗略估一张图里“接近纯黑”的像素占比，用来判断快照是不是抓瞎了
    static float DarkRatio(Bitmap bm)
    {
        try
        {
            int dark = 0, total = 0;
            int stepX = Math.Max(1, bm.Width / 60), stepY = Math.Max(1, bm.Height / 60);
            for (int y = 0; y < bm.Height; y += stepY)
            {
                for (int x = 0; x < bm.Width; x += stepX)
                {
                    Color c = bm.GetPixel(x, y);
                    total++;
                    if (c.R < 40 && c.G < 40 && c.B < 40) dark++;
                }
            }
            return total == 0 ? 0f : (float)dark / total;
        }
        catch { return 0f; }
    }

    void EndDragSnapshot()
    {
        try
        {
            Panel bodyP = dragSnapOverlay == null ? null : dragSnapOverlay.Parent as Panel;
            if (dragSnapOverlay != null)
            {
                if (bodyP != null) bodyP.Controls.Remove(dragSnapOverlay);
                dragSnapOverlay.Dispose();
                dragSnapOverlay = null;
            }
            if (bodyP != null && dragHidden != null)
            {
                foreach (KeyValuePair<Control, bool> kv in dragHidden)
                {
                    try { if (!kv.Key.IsDisposed) kv.Key.Visible = kv.Value; } catch { }
                }
            }
            dragHidden = null;
            if (dragSnapBmp != null) { dragSnapBmp.Dispose(); dragSnapBmp = null; }
        }
        catch { }
    }

    // ---- 拖动跟手性自测（--dragtest）----
    // 把窗口当成“鼠标正按在边框上”一步步放大，每步立刻检查内容有没有跟上窗口，
    // 并统计单帧耗时。跟得上 60Hz 的标准是：单帧 ≤16.7ms、内容零滞后
    internal static bool forceLegacyDrag = false;   // 测老路径用，便于对比
    internal static bool dragTestNoLayout = false;  // 测“纯窗口重绘”基线用

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmFlush();

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    static double Avg(List<double> v)
    {
        if (v == null || v.Count == 0) return 0;
        double s = 0; foreach (double d in v) s += d;
        return s / v.Count;
    }

    public void RunDragTest(string outPath, string shotsDir)
    {
        StringBuilder sb = new StringBuilder();
        try
        {
            // 截图要抓真实屏幕，先把窗口抬到最前面，否则抓到的是压在它上面的东西
            try { Activate(); BringToFront(); } catch { }
            System.Threading.Thread.Sleep(160);
            ShowPanel();
            Application.DoEvents();
            System.Threading.Thread.Sleep(300);
            Application.DoEvents();

            // 进入“鼠标按在边框上”的窗口期
            Message m0 = Message.Create(Handle, 0x0231, IntPtr.Zero, IntPtr.Zero);
            WndProc(ref m0);

            int w0 = Width, h0 = Height;
            int lagFrames = 0, maxLag = 0;
            List<double> costs = new List<double>();
            List<double> tSize = new List<double>(), tEv = new List<double>(), tFlush = new List<double>();
            System.Diagnostics.Stopwatch sw = new System.Diagnostics.Stopwatch();
            for (int step = 1; step <= 90; step++)
            {
                sw.Reset(); sw.Start();
                SetWindowPos(Handle, IntPtr.Zero, Left, Top, w0 + step * 4, h0 + step * 3,
                    0x0004 | 0x0010 | 0x0002);   // NOZORDER | NOACTIVATE | NOMOVE
                double a = sw.Elapsed.TotalMilliseconds;
                Application.DoEvents();
                double b = sw.Elapsed.TotalMilliseconds;
                try { DwmFlush(); } catch { }
                sw.Stop();
                costs.Add(sw.Elapsed.TotalMilliseconds);
                tSize.Add(a); tEv.Add(b - a); tFlush.Add(sw.Elapsed.TotalMilliseconds - b);

                if (shotsDir != null && shotsDir.Length > 0 && (step % 15 == 0))
                {
                    try
                    {
                        using (Bitmap bm = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                        {
                            Rectangle wr = RectangleToScreen(ClientRectangle);
                            bool ok2 = false;
                            try
                            {
                                using (Graphics gb = Graphics.FromImage(bm))
                                {
                                    gb.CopyFromScreen(wr.Left, wr.Top, 0, 0, new Size(bm.Width, bm.Height),
                                                      CopyPixelOperation.SourceCopy | CopyPixelOperation.CaptureBlt);
                                    ok2 = true;
                                }
                            }
                            catch { ok2 = false; }
                            if (!ok2)
                            {
                                using (Graphics gb = Graphics.FromImage(bm))
                                {
                                    IntPtr hdc = gb.GetHdc();
                                    try { PrintWindow(Handle, hdc, 2); } catch { }
                                    gb.ReleaseHdc(hdc);
                                }
                            }
                            bm.Save(Path.Combine(shotsDir, "drag_" + step.ToString("000") + ".png"), ImageFormat.Png);
                        }
                    }
                    catch { }
                }

                Panel bodyP = FindCtl("body") as Panel;
                Panel cardP = FindCtl("card") as Panel;
                Panel ovP = FindCtl("dragSnap") as Panel;
                if (ovP != null && bodyP != null)
                {
                    // 定格画面路线：只要这张图铺满了内容区，用户看到的就一直是“满的”
                    int aw = bodyP.ClientSize.Width - bodyP.Padding.Horizontal;
                    int ah = bodyP.ClientSize.Height - bodyP.Padding.Vertical;
                    int d = Math.Max(Math.Abs(ovP.Width - aw), Math.Abs(ovP.Height - ah));
                    if (d > 1) { lagFrames++; if (d > maxLag) maxLag = d; }
                }
                else if (bodyP != null && cardP != null)
                {
                    int availW = bodyP.ClientSize.Width - bodyP.Padding.Horizontal;
                    int availH = bodyP.ClientSize.Height - bodyP.Padding.Vertical;
                    int d = Math.Max(Math.Abs(cardP.Width - availW), Math.Abs(cardP.Height - availH));
                    if (d > 1) { lagFrames++; if (d > maxLag) maxLag = d; }
                }
            }

            Message m1 = Message.Create(Handle, 0x0232, IntPtr.Zero, IntPtr.Zero);
            WndProc(ref m1);
            Application.DoEvents();

            costs.Sort();
            double sum = 0; foreach (double c in costs) sum += c;
            double avg = sum / costs.Count;
            double p95 = costs[(int)(costs.Count * 0.95)];
            double mx = costs[costs.Count - 1];
            sb.AppendLine((forceLegacyDrag ? "【老路径：每帧完整重排】" : "【新路径：拖动手势极简跟随】"));
            sb.AppendLine("步数=" + costs.Count);
            sb.AppendLine("内容未跟上窗口的帧数=" + lagFrames + "（最大偏差 " + maxLag + "px）");
            sb.AppendLine("每帧耗时 平均=" + avg.ToString("F2") + "ms  P95=" + p95.ToString("F2")
                          + "ms  最大=" + mx.ToString("F2") + "ms");
            sb.AppendLine("其中 SetWindowPos=" + Avg(tSize).ToString("F2")
                          + "ms  DoEvents(布局+重绘)=" + Avg(tEv).ToString("F2")
                          + "ms  DwmFlush(等屏幕上屏)=" + Avg(tFlush).ToString("F2") + "ms");
            sb.AppendLine("判定标准：单帧 ≤16.7ms 才跟得上 60Hz 屏幕；内容未跟上帧数应为 0");
            sb.AppendLine("ExStyle=0x" + CreateParams.ExStyle.ToString("X")
                          + "  layered=" + ((CreateParams.ExStyle & 0x80000) != 0));
            sb.AppendLine("尺寸 " + w0 + "x" + h0 + " → " + Width + "x" + Height);
        }
        catch (Exception ex) { sb.AppendLine("ERR " + ex.Message); }
        try { File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8); } catch { }
    }

    // 缩放过程中先把白卡铺到和可用区域一样大，避免中途露出一条浅灰/像透明的空白
    void StretchCardNow()
    {
        try
        {
            Panel bodyP = FindCtl("body") as Panel;
            Panel cardP = FindCtl("card") as Panel;
            if (bodyP == null || cardP == null) return;
            int availW = bodyP.ClientSize.Width - bodyP.Padding.Horizontal;
            int availH = bodyP.ClientSize.Height - bodyP.Padding.Vertical;
            if (availW < 60 || availH < 60) return;
            if (cardP.Width != availW) cardP.Width = availW;
            if (cardP.Height < availH) cardP.Height = availH;
        }
        catch { }
    }

    internal ContextMenuStrip BuildBoxMenu()
    {
        ContextMenuStrip m = new ContextMenuStrip();
        m.Items.Add("读这一句（F3）", null, delegate { SpeakCurrentLine(); });
        m.Items.Add("复制这一句", null, delegate { CopyCurrentLine(); });
        m.Items.Add("复制选中的文字", null, delegate {
            if (box.SelectionLength > 0) { try { Clipboard.SetText(box.SelectedText); tip.Text = "选中的已经复制"; } catch { } }
            else tip.Text = "先选中一句话";
        });
        miCopyAllBox = m.Items.Add(CopyAllLabel() + "（Ctrl+Shift+C）", null, delegate { CopyAll(); });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(RedactMenuItem());   // ⑪ 心里不踏实的时候，看看到底哪几处被打码了
        m.Items.Add("清空窗口", null, delegate { box.Clear(); lastFull = ""; tip.Text = "清空了"; });
        StyleMenu(m);
        return m;
    }

    // 「更多 ▾」的内容单独立出来：自检要能拿到菜单项做断言，但不需要真的把菜单弹出来
    internal ContextMenuStrip BuildMoreMenu()
    {
        ContextMenuStrip m = new ContextMenuStrip();

        // ---- v2.4 第 3 步：复制全文放第一条（加粗），文案跟着当前语种变 ----
        ToolStripMenuItem miCopyAll = new ToolStripMenuItem(CopyAllLabel(), null, delegate { CopyAll(); });
        miCopyAll.ShortcutKeyDisplayString = "Ctrl+Shift+C";
        // v2.5.2：加粗挪到 StyleMenu(m) 之后再做（见方法末尾）。
        // 以前在这里就 new Font(m.Font, Bold)：此刻菜单还没穿过 StyleMenu，
        // m.Font 还是 WinForms 默认的宋体 9pt，于是只有这一项字族/字号跟别人不一样，看着像另一套皮肤。
        m.Items.Add(miCopyAll);
        m.Items.Add(new ToolStripSeparator());

        // ---- 导出：默认只导译文，勾了这个才把原文一起写进去 ----
        ToolStripMenuItem miSrc = new ToolStripMenuItem("导出时带上原文（不勾就只导译文）");
        miSrc.Checked = ExportWithSrc;
        ToolStripMenuItem miTxt = new ToolStripMenuItem();
        ToolStripMenuItem miDocx = new ToolStripMenuItem();
        Action refresh = delegate {
            miTxt.Text = "导出 TXT 到桌面" + (miSrc.Checked ? "（带原文）" : "（只译文）");
            miDocx.Text = "导出 Word（.docx）到桌面" + (miSrc.Checked ? "（带原文）" : "（只译文）");
        };
        miSrc.Click += delegate {
            miSrc.Checked = !miSrc.Checked;      // 不用 CheckOnClick，逻辑只留一处
            ExportWithSrc = miSrc.Checked;
            refresh();
            tip.Text = miSrc.Checked ? "导出会连原文一起写进去" : "导出只写译文";
        };
        miTxt.Click += delegate { ExportResult(false); };
        miDocx.Click += delegate { ExportResult(true); };
        refresh();
        m.Items.Add(miSrc);
        m.Items.Add(miTxt);
        m.Items.Add(miDocx);
        m.Items.Add(new ToolStripSeparator());

        m.Items.Add("历史记录…（最近 20 条）", null, delegate { ShowHistory(); });
        miOcr2 = m.Items.Add("截个图，查图里的英文" + OcrSuffix(), null, delegate { DoOcr(); });
        m.Items.Add("生词本…（可导出）", null, delegate { ShowWordBook(); });
        m.Items.Add("生词复习…（卡片背单词）", null, delegate { ShowWordReview(); });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("语音设置（男声/女声/语速）", null, delegate { ShowVoiceSettings(); });
        // ---- 第三批 ②：换快捷键。收进子菜单，主菜单不为了五组键多长五行 ----
        ToolStripMenuItem miHk = new ToolStripMenuItem("换快捷键（Ctrl+空格 撞车了就换一组）");
        foreach (string c in HotkeyChoices)
        {
            string key = c;   // 自己接一份：循环变量被闭包捕获会一路都指到最后那个
            ToolStripMenuItem mi = new ToolStripMenuItem(HotkeyFriendly(key) + (key == "ctrl+space" ? "（默认）" : ""));
            mi.Checked = (NormalizeHotkey(hotkeyKey) == key);
            mi.Click += delegate { SwitchHotkey(key); };
            miHk.DropDownItems.Add(mi);
        }
        m.Items.Add(miHk);
        ToolStripMenuItem miTop = new ToolStripMenuItem("窗口总是置顶（点别的软件也不退）");
        miTop.Checked = topMost;
        miTop.Click += delegate {
            topMost = !topMost;
            miTop.Checked = topMost;
            TopMost = topMost;
            SetSet("topmost", topMost ? "1" : "0");
            tip.Text = topMost ? "已钉在最前面" : "已取消置顶：点别的软件就退到后面";
        };
        m.Items.Add(miTop);
        // v2.5.2：与托盘菜单同一句文案，两处入口不能各说各的
        ToolStripMenuItem miAuto = new ToolStripMenuItem("划词就自动查（默认关 · 会占用剪贴板短暂复制）");
        miAuto.Checked = autoPopup;
        miAuto.Click += delegate {
            autoPopup = !autoPopup;
            miAuto.Checked = autoPopup;
            SetSet("auto_popup", autoPopup ? "1" : "0");
            tip.Text = autoPopup
                ? "已打开：划完词自动查；它要模拟 Ctrl+C 取词，会短暂占用剪贴板"
                : "已关掉：还是按 " + HotkeyLabel + " 查，不碰你的剪贴板";
        };
        m.Items.Add(miAuto);
        // ⑩ 粘贴即查：与托盘菜单同一句文案，两处入口不能各说各的
        m.Items.Add(BuildPasteMenu());
        m.Items.Add("字号 大一点", null, delegate { SetFontSize(curFontPt + 1); });
        m.Items.Add("字号 小一点", null, delegate { SetFontSize(curFontPt - 1); });
        m.Items.Add("清空查询缓存", null, delegate {
            lock (cache) { cache.Clear(); }
            tip.Text = "缓存清空了";
        });
        // A3 一键诊断包：出问题的时候点一下，桌面上出来一个 zip，发过来就能看
        m.Items.Add("生成诊断包（出问题了发这个）", null, delegate { ShowDiagPack(); });
        m.Items.Add("再看一遍使用说明", null, delegate { ShowWelcome(); });
        // C1 公告卡：跟托盘菜单同一句文案，两处入口不能各说各的
        m.Items.Add("这一版有什么变化（公告）", null, delegate { ShowAnnounce(true); });
        StyleMenu(m);
        // v2.5.2：StyleMenu 已把菜单字体定成 Microsoft YaHei UI 9.5，现在取基准才对得上。
        // 只加粗这一项，字族与字号跟其余项完全一致；其余菜单项与配色一律不动。
        // v2.8.2：加粗改成从菜单基准派生（只改字重），绝不再自己写死字族与字号。
        // 以前硬编码 new Font("Microsoft YaHei UI", 9.5f, Bold)，在系统默认字体被改过的机器上
        // 会跟其余项差一截，看着像另一套皮肤。
        // v2.8.3：按用户要求撤掉加粗——「复制全文」跟菜单其余项完全平齐，不再抢眼。
        // 这里不设 Font，直接沿用 StyleMenu 给整张菜单定下的字体（字族/字号/字重全一致）。
        return m;
    }

    void ShowMoreMenu()
    {
        ContextMenuStrip m = BuildMoreMenu();
        Size ms = m.GetPreferredSize(Size.Empty);
        int mx = btnMore.Width - ms.Width;
        int my = -ms.Height - 2;
        if (btnMore.PointToScreen(new Point(mx, my)).X < 0) mx = -ms.Width + btnMore.Width;
        m.Show(btnMore, new Point(mx, my));
    }

    void UpdateToneBtn()
    {
        if (btnTone == null) return;
        if (tone == "formal") { btnTone.BackColor = Pal.AccentSoft; }
        else if (tone == "casual") { btnTone.BackColor = Pal.ToneCasualBg; }
        else { btnTone.BackColor = Pal.Surface; }
        RefreshFootLabels();
    }

    void CycleTone()
    {
        if (tone == "auto") tone = "formal";
        else if (tone == "formal") tone = "casual";
        else tone = "auto";
        SetSet("tone", tone);
        UpdateToneBtn();
        string label = (tone == "auto") ? "跟着场景来" : (tone == "formal" ? "一律正式书面" : "一律日常口语");
        tip.Text = "语气：" + label;
        // 换了语气，手上这句立刻按新语气重查一次
        if (lastInput.Length > 0 && !busy)
        {
            lock (cache) { cache.Remove(lastInput); }
            BeginQuery(lastInput, true);
        }
    }

    // ====== 字号 / 语音 / 使用说明 ======
    void SetFontSize(int pt)
    {
        if (pt < 8) pt = 8;
        if (pt > 24) pt = 24;
        if (pt == curFontPt) { tip.Text = "字号已经是 " + pt; return; }
        curFontPt = pt;
        try
        {
            // 绝不能 Dispose RichTextBox 正在用的旧字体：一 Dispose，整块文字会直接渲染成空白
            box.Font = Cached(ref _fBox, pt, FontStyle.Regular);
        }
        catch { }
        if (!selfTestMode) SetSet("font_size", pt.ToString());
        // 整窗的框子跟着字号一起长，别让字撑破框子
        ApplyMetrics();
        string content = lastFull.Length > 0 ? lastFull : box.Text;
        if (busy) pendingFont = true;   // 正在流式输出，等它结束后再按新字号重排一遍
        else if (content.Length > 0) RenderResult(content);
        else { box.Invalidate(); box.Refresh(); }
        FitContent();
        tip.Text = "字号 " + pt + "（Ctrl+滚轮 也能调）";
    }

    void ShowVoiceSettings()
    {
        ShowPanel();
        VoiceForm f = new VoiceForm(GetSet("voice", "Zira"), GetInt("rate", -1));
        if (f.ShowDialog(this) == DialogResult.OK)
        {
            f.Collect();
            SetSet("voice", f.VoiceName);
            SetSet("rate", f.Rate.ToString());
            if (tts != null) { try { tts.SpeakAsyncCancelAll(); tts.Dispose(); } catch { } tts = null; }
            tip.Text = "已换：" + (f.VoiceName == "David" ? "男声 David" : "女声 Zira") + "，语速 " + f.Rate;
            SpeakText();
        }
        f.Dispose();
    }

    // ====== v2.5.3 第一档 ⑥：「关于」 ======
    // 只是打开一个独立小窗；主界面、底栏按钮、外框圆角全部不动。
    void ShowAbout()
    {
        // 不带 owner：托盘右键时主窗可能正藏着，带 owner 会把一个隐藏窗口当父窗体。
        using (AboutForm f = new AboutForm())
            f.ShowDialog();
    }

    // ====== v2.5.4 第一档 ⑤：打开数据目录 ======
    // 备份 exe、log.txt、生词本全都在这一个目录里，用户想看自己数据长什么样，
    // 托盘点一下就到，不用记 %APPDATA% 路径。
    void OpenDataDir()
    {
        try
        {
            Directory.CreateDirectory(LOG_DIR);
            Process.Start("explorer.exe", "\"" + LOG_DIR + "\"");
        }
        catch (Exception ex)
        {
            tip.Text = "打不开数据目录：" + ex.Message;
        }
    }

    // 自检用：把托盘右键菜单一项一项念出来（确认「关于」在位、文案没写歪）
    internal string TrayMenuText()
    {
        StringBuilder sb = new StringBuilder();
        if (tray == null || tray.ContextMenuStrip == null) return "托盘菜单还没建\r\n";
        for (int i = 0; i < tray.ContextMenuStrip.Items.Count; i++)
        {
            ToolStripItem it = tray.ContextMenuStrip.Items[i];
            if (it is ToolStripSeparator) { sb.AppendLine("  ----"); continue; }
            sb.AppendLine("  " + it.Text);
        }
        return sb.ToString();
    }

    // 自检入口：外面只拿到 Form，不必认识嵌套类
    internal Form BuildAboutForm() { return new AboutForm(); }

    // 自检用：把关于窗里「能看到的文字」和「复制按钮带走的原文」逐条导出来
    internal string DescribeAbout(Form f)
    {
        StringBuilder sb = new StringBuilder();
        AboutForm a = f as AboutForm;
        if (a == null) return "不是关于窗\r\n";
        sb.AppendLine("== 窗口里能看到的文字 ==");
        foreach (Control c in a.Controls)
        {
            Label lb = c as Label;
            TextBox tb = c as TextBox;
            Button bt = c as Button;
            if (tb != null) sb.AppendLine("  [" + (tb.Tag == null ? "?" : tb.Tag.ToString()) + "] " + tb.Text);
            else if (bt != null) sb.AppendLine("  按钮：" + bt.Text);
            else if (lb != null && lb.Text.Length > 0) sb.AppendLine("  文字：" + lb.Text);
        }
        sb.AppendLine();
        sb.AppendLine("== 「复制这段信息」会带走的原文 ==");
        sb.AppendLine(a.InfoBlock.Replace("\r\n", "\r\n  "));
        return sb.ToString();
    }

    void ShowWelcome()
    {
        ShowPanel();
        lastWelcomeText = AppTitle + "已经在后台待命了。\n\n" +
                   "【最常用的】\n" +
                   "1. 选中文字 → Ctrl+空格 查；Esc 关掉（程序还在右下角托盘里）。\n" +
                   "2. 想自己打：下面输入框打，回车就查。\n" +
                   "3. 想发出去：Ctrl+Shift+C 复制全文；双击某一句只复制那句。\n" +
                   "4. 翻回上一句：输入框里按 ↑ 翻回上一条，按 ↓ 往回走。\n" +
                   "5. 听发音：F2 读整段，F3 读当前这句。\n" +
                   "6. 字太小：Ctrl+滚轮 调字号。\n" +
                   "7. 想问一句：查完按 Ctrl+F，接着问「这里的 for 是介词吗」，回车就发；Esc 退出追问。\n\n" +
                   "【其他】\n" +
                   "· 复制了文字或图片：Ctrl+V 就自动开始查（是图先认字再查）。\n" +
                   "· 有文件：txt / srt / docx / pdf 拖进来松手。\n" +
                   "· 屏幕上的字选不中：按截图键" + (ocrHotkeyText.Length > 0 ? "（" + ocrHotkeyText + "）" : "（窗口上写着）") +
                   " 框住它，认字再查。\n" +
                   "· 不想打字：点输入框右边「说」，说完自动查（右键切中/英文识别）。\n" +
                   "· 整篇稿子：切到「其他语言」，日韩法德西俄等 19 种语言，能导 TXT / docx。\n\n" +
                   "· 结果里那两条【你可以这样回】，复制就能发出去。\n" +
                   "· 语气：对外沟通、工作回复用「正式」，日常闲聊用「口语」，切了立刻按新的重查。\n" +
                   "· 音标看不会：开「谐音」，会显示中文近音。\n\n" +
                   "· 历史记录：最近 20 条，点一条能翻回来。\n" +
                   "· 生词本：可导出 CSV，Excel 直接打开。\n\n" +
                   "· 导出 TXT 用 Ctrl+Shift+S，导出 Word 用 Ctrl+Shift+D；默认只导译文，勾一下连原文一起写。\n" +
                   "· 划词就自动查：默认关。开了鼠标划完词就查，但要模拟 Ctrl+C 取词，会短暂占用剪贴板。\n" +
                   "· 同一句 10 分钟内再查不走网络，秒出结果。\n\n" +
                   "· 没网：单词还能查（2 万个常用词在本地），整句翻不了。\n" +
                   "· 报错了：点「再试一次」。\n" +
                   "· 说 Key 不对：托盘图标右键 → 配置 API Key，重粘一遍。\n" +
                   "· 图标突然不见了：多半被杀毒隔离，去隔离区恢复再加信任，不用重装。";
        SetBoxText(lastWelcomeText);
        tip.Text = HotkeyLabel + " 查｜" + (ocrHotkeyText.Length > 0 ? ocrHotkeyText + " 截图查" : "截图查看菜单") + "｜Ctrl+Shift+C 复制全部｜Esc 关";
        if (!hotkeyOk)
        {
            SetBoxText("热键没注册上：" + HotkeyLabel + " 可能被别的软件占用了。\n\n" +
                       "可以先把占用的软件关掉，再重新打开本程序；\n" +
                       "也可以点「更多 ▾ → 换快捷键」换一组试试；\n" +
                       "或者双击右下角托盘里的饼干图标，一样能查，只是没有快捷键。");
            tip.Text = "热键注册失败";
        }
        // 说明页是纯文字，字号一大就超出可视区。这里跟查词结果一样按内容把窗口长高一点，
        // 不然用户看到的就是"显示不全 / 被切掉"
        FitContent();
    }

    string OcrSuffix() { return ocrHotkeyText.Length > 0 ? "（" + ocrHotkeyText + "）" : ""; }

    // 界面上所有写热键的地方都从这里取，换键后靠 ApplyHotkeyText 一次刷全
    string HotkeyLabel { get { return HotkeyFriendly(hotkeyKey); } }

    void ApplyHotkeyText()
    {
        try
        {
            string hk = HotkeyLabel;
            if (headPanel != null)
            {
                headPanel.TitleText = AppTitle;
                headPanel.HintText = hk + " 查 · Esc 关";
                headPanel.Invalidate();
            }
            string suffix = ocrHotkeyText.Length > 0 ? "（" + ocrHotkeyText + "）" : "";
            if (miOcr1 != null) miOcr1.Text = "截图查图里的英文" + suffix;
            if (miOcr2 != null) miOcr2.Text = "截个图，查图里的英文" + suffix;
            if (miTrayQuery != null) miTrayQuery.Text = "查一次（" + hk + "）";
            if (tray != null) tray.Text = AppTitle + "：选中文字按 " + hk;
            tip.Text = hk + " 查｜Ctrl+Shift+C 复制全部｜Esc 关";
        }
        catch { }
    }

    // 第三批 ②：注册 / 换绑查询热键。
    // 注意：换绑前一定会把旧键放掉，所以这里返回 false 时调用方必须负责「把旧键抢回来」，
    // 否则用户点一下没换成，最后连原来那个键也没了。
    bool TryRegisterQueryHotkey(string key)
    {
        try
        {
            uint mods, vk;
            if (!HotkeyParts(key, out mods, out vk)) return false;
            if (hotkeyOk) { try { UnregisterHotKey(Handle, HK_ID); } catch { } hotkeyOk = false; }
            hotkeyOk = RegisterHotKey(Handle, HK_ID, mods, vk);
            if (!hotkeyOk) Log("热键注册失败：" + HotkeyFriendly(key));
            return hotkeyOk;
        }
        catch { return false; }
    }

    // 用户点了「更多 ▾ → 换快捷键 → 某组」
    internal void SwitchHotkey(string key)
    {
        key = NormalizeHotkey(key);
        string old = NormalizeHotkey(hotkeyKey);
        if (key == old)
        {
            tip.Text = "现在用的就是 " + HotkeyFriendly(key);
            return;
        }
        if (TryRegisterQueryHotkey(key))
        {
            hotkeyKey = key;
            if (selfTestMode) SetSetQuiet("hotkey", key); else SetSet("hotkey", key);
            ApplyHotkeyText();
            tip.Text = "热键换好了：" + HotkeyFriendly(key);
            Log("查询热键已换成 " + HotkeyFriendly(key) + "（原来是 " + HotkeyFriendly(old) + "）");
            return;
        }
        // 新键被别人占着：把旧键抢回来，别让用户落到「一个热键都没有」的地步
        bool back = TryRegisterQueryHotkey(old);
        hotkeyKey = old;
        ApplyHotkeyText();
        if (back) tip.Text = HotkeyFriendly(key) + " 被别的软件占着，还是用 " + HotkeyFriendly(old);
        else tip.Text = "热键没抢回来，重开一下程序就好";
        Log("换键失败：" + HotkeyFriendly(key) + " 被占用，退回 " + HotkeyFriendly(old) + " 结果=" + back);
    }

    // 启动时按 settings.ini 注册；存的那组被后装的软件抢了就退回默认
    void SetupQueryHotkey()
    {
        string want = NormalizeHotkey(GetSet("hotkey", "ctrl+space"));
        hotkeyKey = want;
        if (TryRegisterQueryHotkey(want)) return;
        if (want == "ctrl+space") return;      // 默认键也占着，那就是真没了，交给欢迎页提示
        hotkeyKey = "ctrl+space";
        if (TryRegisterQueryHotkey("ctrl+space"))
        {
            if (selfTestMode) SetSetQuiet("hotkey", "ctrl+space"); else SetSet("hotkey", "ctrl+space");
            Log("settings 里的 " + HotkeyFriendly(want) + " 没注册上，已退回 Ctrl+空格");
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetupQueryHotkey();
        // 截图热键：依次试几组，挑第一组没被别的软件占用的
        uint[] mods = new uint[] { MOD_CONTROL | MOD_SHIFT, MOD_CONTROL | MOD_ALT, MOD_CONTROL | MOD_SHIFT, MOD_CONTROL | MOD_ALT, MOD_CONTROL | MOD_SHIFT };
        uint[] vks = new uint[] { VK_A, 0x53 /*S*/, 0x58 /*X*/, 0x51 /*Q*/, 0x46 /*F*/ };
        string[] names = new string[] { "Ctrl+Shift+A", "Ctrl+Alt+S", "Ctrl+Shift+X", "Ctrl+Alt+Q", "Ctrl+Shift+F" };
        for (int i = 0; i < vks.Length; i++)
        {
            if (RegisterHotKey(Handle, HK_OCR_ID, mods[i], vks[i]))
            {
                ocrHotkeyOk = true;
                ocrHotkeyText = names[i];
                break;
            }
        }
        Log("窗口句柄已建立，热键注册=" + hotkeyOk + " 截图热键=" + ocrHotkeyOk + " " + ocrHotkeyText);
        ApplyHotkeyText();
    }

    // ====== 四边四角都可拖拽缩放（无边框窗口必须自己交出 HT* 命中结果）======
    const int WM_NCHITTEST = 0x0084;
    const int HTCLIENT = 1, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
              HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    void CalcHitTest(ref Message m)
    {
        base.WndProc(ref m);
        if ((int)m.Result != HTCLIENT) return;
        if (WindowState != FormWindowState.Normal) return;
        int lp = m.LParam.ToInt32();
        Point p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
        int b = 8;   // 边缘抓取宽度：6px 太考验手感，放宽到 8px
        bool left = p.X <= b, right = p.X >= ClientSize.Width - b;
        bool top = p.Y <= b, bottom = p.Y >= ClientSize.Height - b;
        if (top && left) m.Result = (IntPtr)HTTOPLEFT;
        else if (top && right) m.Result = (IntPtr)HTTOPRIGHT;
        else if (bottom && left) m.Result = (IntPtr)HTBOTTOMLEFT;
        else if (bottom && right) m.Result = (IntPtr)HTBOTTOMRIGHT;
        else if (left) m.Result = (IntPtr)HTLEFT;
        else if (right) m.Result = (IntPtr)HTRIGHT;
        else if (top) m.Result = (IntPtr)HTTOP;
        else if (bottom) m.Result = (IntPtr)HTBOTTOM;
    }

    // 最大化 / 还原（自己实现，因为窗口无系统边框按钮）
    void ToggleMax()
    {
        Rectangle wa = Screen.FromControl(this).WorkingArea;
        if (WindowState == FormWindowState.Maximized)
        {
            WindowState = FormWindowState.Normal;
            MaximumSize = wa.Size;   // 还原后重新把最大尺寸封回工作区
            if (restW > 0 && restH > 0)
            {
                int w = Math.Min(restW, wa.Width);
                int h = Math.Min(restH, wa.Height);
                Size = new Size(w, h);
                int x = Math.Max(wa.Left, Math.Min(Left, wa.Right - w));
                int y = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - h));
                Location = new Point(x, y);
            }
        }
        else
        {
            restW = Width; restH = Height;
            ClearClipRegion();                 // 先解掉可能残留的裁剪，否则最大化后只露出原来那块
            MaximumSize = Size.Empty;          // MaximumSize 压着会让最大化停在旧尺寸，必须先松绑
            MaximizedBounds = wa;
            WindowState = FormWindowState.Maximized;
        }
        SyncMaxGlyph();
        // 状态刚切完时系统还会再调一次 WM_SIZE，这里补一刀强制重排重绘，
        // 免得内容留在旧表面上（表现为"窗口框放大了，里面的字还在左上角"）
        try
        {
            if (IsHandleCreated)
                BeginInvoke(new Action(delegate
                {
                    if (!IsHandleCreated) return;
                    PerformLayout();
                    Invalidate(true);
                    Update();
                }));
        }
        catch { }
    }

    // 图标跟着状态换：正常态画一个方框，最大化态画"叠两个方框"（还原）
    void SyncMaxGlyph()
    {
        if (maxButton == null) return;
        maxButton.Glyph = (WindowState == FormWindowState.Maximized) ? 2 : 1;
        maxButton.Invalidate();
    }

    int restW = 0, restH = 0;

    // ① 输入法组合串里还有内容 = 拼音/五笔还没选字上屏。
    // 取不到 IME 上下文（英文输入、输入法关着）就当没在打字，照常查。
    internal static bool IsImeComposing(IntPtr hwnd)
    {
        IntPtr hIMC = IntPtr.Zero;
        try
        {
            hIMC = ImmGetContext(hwnd);
            if (hIMC == IntPtr.Zero) return false;
            // dwBufLen 给 0 就是"只问长度、不取内容"，-1 表示没组合串
            int n = ImmGetCompositionStringW(hIMC, GCS_COMPSTR, null, 0);
            return n > 0;
        }
        catch { return false; }
        finally
        {
            if (hIMC != IntPtr.Zero) { try { ImmReleaseContext(hwnd, hIMC); } catch { } }
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST) { CalcHitTest(ref m); return; }
        // v2.5.2：第二次启动时新进程发来的「请把窗口显示出来」，别再用弹框打发用户
        if (WM_SHOWME != 0 && m.Msg == (int)WM_SHOWME)
        {
            try { ShowPanel(); } catch { }
            return;
        }
        // 双击标题条 = 放到最大 / 还原（Windows 的老习惯）
        // 标题条被 HookDrag 转成了 HTCAPTION 拖动，所以这里必须自己拦 NCLBUTTONDBLCLK，
        // 光靠控件的 DoubleClick 是收不到的
        if (m.Msg == 0x00A4)
        {
            CalcHitTest(ref m);
            if ((int)m.Result == HTCAPTION) { ToggleMax(); return; }
        }
        // 卡片跟着内容缩之后，窗口下半部分是灰底，鼠标常常落在灰底上：
        // 这里兜一层 Ctrl+滚轮，焦点不在正文框时也能调字号
        if (m.Msg == 0x020A && (ModifierKeys & Keys.Control) == Keys.Control)
        {
            // 焦点在正文框时由 box 自己处理，这里不能再算一次，否则一格滚成两号
            if (ActiveControl != box)
            {
                int delta2 = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                SetFontSize(curFontPt + (delta2 > 0 ? 1 : -1));
                return;
            }
        }
        // 用户亲手拖完边角后记一次尺寸，启动时的尺寸变化绝不写盘（否则每次开机会缩一点）
        if (m.Msg == 0x0231)
        {
            // WM_ENTERSIZEMOVE：鼠标真的按上了边框。整段拖动期间只做最便宜的几何跟随，
            // 松手（0x0232）当场补一次完整重排，而不是再等 40ms 抖一下
            dragSizing = true;
            // 趁尺寸还没被拖动改掉，先把现在的画面抓下来当“定格画面”
            BeginDragSnapshot();
        }
        if (m.Msg == 0x0232)
        {
            dragSizing = false;
            EndDragSnapshot();   // 先撤掉定格画面、把真控件放回来，再做完整重排
            SavePos();
            if (layoutTimer != null) layoutTimer.Stop();
            resizing = false;
            if (!layingOut && IsHandleCreated) ApplyMetrics();
        }
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HK_ID)
        {
            StartQuery();
            return;
        }
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HK_OCR_ID)
        {
            DoOcr();
            return;
        }
        // ③ 显示器插拔 / 改分辨率：立刻确认窗口还在不在屏幕里，别等用户喊"窗口又不见了"
        if (m.Msg == 0x007E)
        {
            try
            {
                if (EnsureOnScreen() && Visible) Invalidate();
            }
            catch { }
        }
        base.WndProc(ref m);
    }

    void ShowPanel()
    {
        // 已经最大化的时候别硬拉回 Normal：查完一个词窗口自己从全屏缩回去，最容易被当成"又坏了"
        if (WindowState != FormWindowState.Maximized) WindowState = FormWindowState.Normal;
        // ③ 拔掉副屏之后窗口还记着旧坐标（那块屏已经不存在了），每次唤出前先确认它够得着
        EnsureOnScreen();
        Opacity = 1;
        Show();
        // v2.4 第 4 步：主窗口（含所有子控件）接上拖文件；幂等，多次调只挂一次（见 MainIngest.cs ①）
        MainInstallDrop();
        BringToFront();
        Activate();
    }

    // ====== v2.5.2：单实例——把已经开着的那个窗口叫出来 ======
    // 用户第二次双击 exe 时，真正想要的是"窗口出现"，而不是一句"程序已经开着了"。
    // 这里只做三件事：找到它的窗口 → 让它自己 ShowPanel（认消息的新版）→ 不行就外部强制显示。
    internal const string APP_TITLE = "翻译器";
    // 自定义唤起消息：同一个系统内同名消息 ID 一致，新旧版本都注册同一个名字。
    static readonly uint WM_SHOWME = RegisterWindowMessageW("SolivineQuickLook_ShowMe_v252");

    internal static IntPtr FindInstanceWindow(out uint pid)
    {
        pid = 0;
        // out 参数不能被匿名委托捕获，这里用局部变量中转
        IntPtr found = IntPtr.Zero;
        uint foundPid = 0;
        uint self = 0;
        try { self = (uint)Process.GetCurrentProcess().Id; } catch { }
        try
        {
            EnumWindows(delegate(IntPtr h, IntPtr lp)
            {
                try
                {
                    StringBuilder sb = new StringBuilder(512);
                    GetWindowTextW(h, sb, sb.Capacity);
                    if (sb.Length == 0) return true;
                    // 只认主窗标题；托盘气泡、菜单之类没有标题，一律跳过
                    if (sb.ToString() != AppTitle) return true;
                    uint p;
                    GetWindowThreadProcessId(h, out p);
                    if (p == self) return true;   // 自己（理论上不会，保险）
                    found = h;
                    foundPid = p;
                    return false;
                }
                catch { return true; }
            }, IntPtr.Zero);
        }
        catch { }
        pid = foundPid;
        return found;
    }

    // 把 hwnd 拉到前台并闪一下任务栏。false = 这个窗口救不活（僵尸）
    internal static bool ActivateInstanceWindow(IntPtr h)
    {
        if (h == IntPtr.Zero) return false;
        try
        {
            // 第一步：让它自己把窗口叫出来。新版认 WM_SHOWME，走的是正常 ShowPanel，
            // 状态最干净（布局、拖文件、置顶这些都跟着走一遍）
            try { PostMessageW(h, WM_SHOWME, IntPtr.Zero, IntPtr.Zero); } catch { }
            for (int i = 0; i < 8; i++)
            {
                Thread.Sleep(60);
                if (IsWindowVisible(h)) return BringToFrontSoft(h);
            }
            // 第二步：旧版本不认这条消息（它是在这次升级之前部署的），
            // 外部强制显示；窗口最小化时先 SW_RESTORE 再 SW_SHOW
            try { ShowWindow(h, SW_RESTORE); } catch { }
            try { ShowWindow(h, SW_SHOW); } catch { }
            for (int i = 0; i < 10; i++)
            {
                Thread.Sleep(60);
                if (IsWindowVisible(h)) return BringToFrontSoft(h);
            }
        }
        catch { }
        return false;
    }

    static bool BringToFrontSoft(IntPtr h)
    {
        try { BringWindowToTop(h); } catch { }
        try { SetForegroundWindow(h); } catch { }
        try
        {
            FLASHWINFO fw = new FLASHWINFO();
            fw.cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO));
            fw.hwnd = h;
            fw.dwFlags = FLASHW_ALL;
            fw.uCount = 2;
            fw.dwTimeout = 0;
            FlashWindowEx(ref fw);
        }
        catch { }
        return true;
    }

    // 半退出僵尸（窗口没了 / 消息循环卡死）：结束所有旧实例，由本次启动接管
    internal static int KillOtherInstances()
    {
        int killed = 0;
        uint self = 0;
        string me = "";
        try
        {
            Process cur = Process.GetCurrentProcess();
            self = (uint)cur.Id;
            me = cur.ProcessName;
        }
        catch { return 0; }
        try
        {
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == self) continue;
                    if (p.ProcessName != me) continue;
                    p.Kill();
                    killed++;
                    p.WaitForExit(3000);
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
        }
        catch { }
        return killed;
    }


    string GetSelectedText(out string savedClip, out bool fromSelection)
    {
        string before = null;
        try { if (Clipboard.ContainsText()) before = Clipboard.GetText(); } catch { }
        savedClip = before;
        fromSelection = false;
        string after = null;
        try
        {
            SendKeys.SendWait("^c");
            for (int i = 0; i < 12; i++)
            {
                Thread.Sleep(60);
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        after = Clipboard.GetText();
                        if (after != before) break;
                    }
                }
                catch { }
            }
        }
        catch { }
        string picked = null;
        if (after != null && after != before) { picked = after; fromSelection = true; }
        else if (!string.IsNullOrEmpty(before)) picked = before;
        else if (!string.IsNullOrEmpty(after)) picked = after;
        return (picked == null) ? "" : picked.Trim();
    }

    // 取完选区后把剪贴板还回去，避免用户的复制内容被“查一次”覆盖掉
    void RestoreClipboardLater(string saved)
    {
        Thread t = new Thread(delegate () {
            Thread.Sleep(700);
            Ui(delegate () {
                try
                {
                    if (string.IsNullOrEmpty(saved)) Clipboard.Clear();
                    else Clipboard.SetText(saved);
                }
                catch { }
            });
        });
        t.IsBackground = true;
        t.Start();
    }

    void StartQuery()
    {
        if (busy) return;
        Log("热键触发");
        string savedClip;
        bool fromSelection;
        string text = GetSelectedText(out savedClip, out fromSelection);
        if (fromSelection) RestoreClipboardLater(savedClip);
        Log("取到文字长度=" + text.Length + " 来自选区=" + fromSelection);
        ShowPanel();
        if (text.Length == 0)
        {
            SetBoxText("没有拿到文字。\n\n两种用法：\n1）先在聊天窗口选中要查的那句话，再按 " + HotkeyLabel + "；\n2）或者直接在下面输入框里打中文/英文，回车就查（中文会翻成英文）。");
            tip.Text = "没选中内容，可以打字查";
            return;
        }
        BeginQuery(text, false);
    }

    // 输入框里打字查（中文→英文 / 英文→中文）
    void QueryManual()
    {
        string t = inputBox.Text.Trim();
        if (t.Length == 0) { pasteAskText = null; tip.Text = "先打点字，再回车"; inputBox.Focus(); return; }
        // ⑩ 粘贴即查：刚粘进来的长文还在等一句「要翻吗」，这个回车就是回答
        if (PasteConfirmLong(t)) return;
        // v2.8.0：处在「接着问」状态时，这一句是追问，不是新查一句
        if (followMode && followBaseResult.Length > 0)
        {
            inputBox.Text = "";
            ShowPanel();
            BeginFollowUp(t);
            return;
        }
        // 不再因为 busy 就拦下来：连点两次是常见操作，靠请求序号保证只显示最后一次的结果
        inputBox.Text = "";
        ShowPanel();
        BeginQuery(t, false);
    }

    // ====== v2.8.0 追问：查完一句接着问 ======
    // 故意不新开输入条：主窗口的布局是按像素算好的，多一行控件会把下面全顶歪。
    // 直接复用她已经在用的那只输入框，只是换个提示语、标个状态，回车就发。
    internal void EnterFollowMode()
    {
        if (busy) { tip.Text = "等这条查完再追问"; return; }
        if (followBaseResult.Length == 0)
        {
            tip.Text = "先查一句，查完才能追问";
            return;
        }
        ShowPanel();
        followMode = true;
        SetInputHint("追问：接着问这句，比如「这里的 for 是介词吗」，回车就发");
        tip.Text = "追问中：问这句话的细节｜回车发出，Esc 退出";
        try { inputBox.Focus(); } catch { }
    }

    internal void ExitFollowMode()
    {
        if (!followMode) return;
        followMode = false;
        SetInputHint("在这里打字：中文 → 英文，英文 → 中文，回车就查");
        tip.Text = "退出追问，回到普通查询";
    }

    void SetInputHint(string text)
    {
        if (inputHint == null) return;
        inputHint.Text = text;
        inputHint.Visible = (inputBox == null || inputBox.Text.Length == 0);
    }

    internal void BeginFollowUp(string question)
    {
        followMode = false;
        SetInputHint("在这里打字：中文 → 英文，英文 → 中文，回车就查");
        ShowErrorBar(false);
        busy = true;
        string q = question == null ? "" : question.Trim();
        streamPrev = "";
        SetBoxText(FollowPrefix(q) + "正在问，别关窗口……");
        tip.Text = "追问中";
        int seq = NextSeq();
        Thread t = new Thread(delegate () { DoFollowUp(q, seq); });
        t.IsBackground = true;
        t.Start();
    }

    // ④ 出错时才出现的那条：显示/收起「再试一次」
    void ShowErrorBar(bool on)
    {
        if (errBar == null) return;
        try
        {
            if (errBar.Visible == on) return;
            errBar.Visible = on;
            // 卡片高度跟着重算一次，错误正文才不会被这条挤成半截
            ApplyMetrics();
        }
        catch { }
    }

    // ④ 自检专用（--errtest）：把界面切到"查询失败"状态，方便核对人话文案和「再试一次」
    internal void ShowErrorDemo(string raw)
    {
        lastFailText = "自检用的一句原文，用来验证重试按钮拿得到它";
        SetBoxText(HumanizeError(raw));
        tip.Text = "出错了，点「再试一次」";
        ShowErrorBar(true);
        Application.DoEvents();
        PerformLayout();
        Application.DoEvents();
    }

    // 点一下「再试一次」但不联网：把 lastFailText 清空，它应该只提示、不发起请求
    internal bool RetryClickTest(out string tipAfter)
    {
        lastFailText = "";
        if (btnRetry != null) btnRetry.PerformClick();
        Application.DoEvents();
        tipAfter = tip == null ? "" : tip.Text;
        return tipAfter.IndexOf("没有可重试") >= 0;
    }

    // ========================================================================
    //  第三批 ② 自检专用（--hkcheck）：候选表 / 写坏配置兜底 / 真机换键 / 失败退回，
    //  一次跑完输出成文本，人工照着核。只读不算数——换键这条路必须真注册一次才算过。
    // ========================================================================
    volatile bool hkHoldProbed = false;
    volatile bool hkHoldOk = false;
    volatile bool hkHoldRelease = false;

    internal string HotkeySelfTest()
    {
        StringBuilder sb = new StringBuilder();
        int fails = 0;

        sb.AppendLine("== ② 换快捷键：候选表 / 显示名 / 拆键 ==");
        foreach (string k in HotkeyChoices)
        {
            uint md, vk;
            bool okp = HotkeyParts(k, out md, out vk);
            bool pass = okp && md != 0 && vk != 0;
            if (!pass) fails++;
            sb.AppendLine("  " + k.PadRight(18) + "→ " + HotkeyFriendly(k).PadRight(16)
                + " mods=0x" + md.ToString("X") + " vk=0x" + vk.ToString("X2")
                + (pass ? "  ok" : "  拆键失败"));
        }

        sb.AppendLine();
        sb.AppendLine("== settings.ini 手改坏了要落回默认（不能让用户热键全没了）==");
        string[][] bad = new string[][] {
            new string[]{ null, "ctrl+space" },
            new string[]{ "", "ctrl+space" },
            new string[]{ "   ", "ctrl+space" },
            new string[]{ "Ctrl+Shift+D", "ctrl+shift+d" },        // 大小写、多余空格要宽容
            new string[]{ "ctrl - shift - d", "ctrl+shift+d" },
            new string[]{ "alt+f4", "ctrl+space" },                // 不在候选表里的一律不认
            new string[]{ "随便写的", "ctrl+space" }
        };
        foreach (string[] b in bad)
        {
            string got = NormalizeHotkey(b[0]);
            bool pass = got == b[1];
            if (!pass) fails++;
            sb.AppendLine("  " + (b[0] == null ? "(null)" : "'" + b[0] + "'").PadRight(22)
                + "→ " + got.PadRight(18) + (pass ? "ok" : "不对！期望 " + b[1]));
        }

        sb.AppendLine();
        sb.AppendLine("== 真机：换键 → 文案同步 → 被占用时退回原键 → 还原 ==");
        string orig = NormalizeHotkey(hotkeyKey);
        SwitchHotkey("ctrl+shift+d");
        bool switched = (NormalizeHotkey(hotkeyKey) == "ctrl+shift+d") && hotkeyOk;
        if (!switched) fails++;
        sb.AppendLine("  [换到 Ctrl+Shift+D] hotkeyOk=" + hotkeyOk + " 当前=" + HotkeyFriendly(hotkeyKey)
            + " 提示='" + tip.Text + "' " + (switched ? "ok" : "失败！"));
        sb.AppendLine("    标题条='" + (headPanel != null ? headPanel.HintText : "(无)") + "'");
        sb.AppendLine("    托盘菜单='" + (miTrayQuery != null ? miTrayQuery.Text : "(无)") + "'");
        sb.AppendLine("    提示条='" + tip.Text + "'");

        // 热键是全系统唯一的：后台线程抢下 Ctrl+Alt+空格 注册，主线程再去切它就必须失败
        hkHoldOk = false;
        hkHoldProbed = false;
        hkHoldRelease = false;
        Thread th = new Thread(delegate ()
        {
            hkHoldOk = RegisterHotKey(IntPtr.Zero, 9770, MOD_CONTROL | MOD_ALT, VK_SPACE);
            hkHoldProbed = true;
            while (!hkHoldRelease) { Application.DoEvents(); Thread.Sleep(20); }
            try { UnregisterHotKey(IntPtr.Zero, 9770); } catch { }
        });
        th.IsBackground = true;
        th.Start();
        for (int i = 0; i < 200 && !hkHoldProbed; i++) { Application.DoEvents(); Thread.Sleep(10); }

        string before = NormalizeHotkey(hotkeyKey);
        SwitchHotkey("ctrl+alt+space");
        bool keptOld = (NormalizeHotkey(hotkeyKey) == before) && hotkeyOk;
        if (!keptOld) fails++;
        sb.AppendLine("  [占住 Ctrl+Alt+空格 再去切它] 占位成功=" + hkHoldOk + " 当前=" + HotkeyFriendly(hotkeyKey)
            + " hotkeyOk=" + hotkeyOk + (keptOld ? "  → 已退回原键 ok" : "  → 没退回去！"));
        if (!hkHoldOk) sb.AppendLine("    说明：这组键本来就被别的软件占着，等于天然测到了「占用」场景。");
        sb.AppendLine("    提示='" + tip.Text + "'");
        hkHoldRelease = true;
        try { th.Join(1500); } catch { }

        SwitchHotkey(orig);
        bool restored = (NormalizeHotkey(hotkeyKey) == orig) && hotkeyOk;
        sb.AppendLine("  [还原] 回到 " + HotkeyFriendly(hotkeyKey) + " hotkeyOk=" + hotkeyOk
            + (restored ? " ok" : "（没还原成）"));
        if (!restored)
        {
            // 这台机器上主程序正开着，默认那组键还是它在占着，自检进程当然抢不回来。
            // 这不算失败项：恰恰说明「换不上就退回」这条路是通的（当前停在临时键上）。
            sb.AppendLine("    说明：多半是另一个正在运行的实例还占着 " + HotkeyFriendly(orig)
                + "，自检抢不到属正常，退出后不影响那个实例。");
        }
        sb.AppendLine();
        sb.AppendLine("结论：" + (fails == 0 ? "全部通过" : fails + " 项没通过"));
        return sb.ToString();
    }

    // ④ 重试：拿上一次失败的那句原文重查一遍（不走缓存——失败那次本来也没存缓存）
    void RetryLast()
    {
        string t = lastFailText;
        if (t == null || t.Trim().Length == 0) { tip.Text = "没有可重试的内容，再查一次就好"; return; }
        ShowErrorBar(false);
        Log("点「再试一次」，重查 " + t.Length + " 字");
        BeginQuery(t, true);
    }

    void BeginQuery(string text, bool ignoreCache)
    {
        ShowErrorBar(false);      // 新的一次查询开始，旧的「再试一次」先收起来
        string hit;
        if (!ignoreCache && CacheGet(text, out hit))
        {
            lastInput = text;
            // 缓存秒回也要领新号：把还在路上的旧请求作废，否则它稍后回来会把这段缓存结果盖掉
            NextSeq();
            RenderResult(hit);
            followBaseInput = text;      // 缓存命中的结果同样能拿来追问
            followBaseResult = hit;
            tip.Text = "10 分钟内查过，直接用";
            Log("缓存命中，省一次请求");
            return;
        }
        busy = true;
        lastInput = text;
        streamPrev = "";
        SetBoxText(text + "\n\n————\n正在查，别关窗口……");
        tip.Text = "查询中";
        int seq = NextSeq();   // 领号：只有最新这一号才有资格往界面上写
        Thread t = new Thread(delegate () { DoQuery(text, seq); });
        t.IsBackground = true;
        t.Start();
    }

    void UpdateApproxBtn()
    {
        if (btnApprox == null) return;
        btnApprox.BackColor = showApprox ? Pal.WarnBg : Pal.Surface;
        btnApprox.FlatAppearance.BorderColor = showApprox
            ? Pal.WarnEdge
            : Pal.GrayMark;
        RefreshFootLabels();
    }

    void Ui(Action a)
    {
        try { if (IsHandleCreated) Invoke(a); } catch { }
    }

    static Color HeadingColor(string name)
    {
        if (name.IndexOf("注意") >= 0) return Pal.Danger;
        return Pal.Accent;
    }

    // 每次换文字都先把前景色归零，避免整段被上一轮的颜色染成一片
    // 关键：RichTextBox 会把 \n 存成 \r\n，着色时用的下标必须基于归一化后的同一份文本
    static string ToRichText(string s)
    {
        return s.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
    }

    // 正文“从顶部算起”的真实内容高度。
    // 坑：GetPositionFromCharIndex 给的是「相对可见区」的坐标，用户往下滚过之后，
    // 最后一行的 Y 会跟着滚上去（滚到底时甚至只剩几百像素），拿它当内容高度，
    // 白卡就被算矮了 —— 表现就是卡片忽然缩成一小截、滚动条跟着没影、字也看不全。
    // 这里把已经滚过去的行数补回来，得到的总高度与滚动位置无关。
    int BoxContentHeight()
    {
        if (box == null) return 0;
        int lineH = (int)Math.Ceiling(box.Font.GetHeight());
        if (lineH <= 0) lineH = S(20);
        if (box.TextLength <= 0) return lineH * 2;
        int lastY;
        try { lastY = box.GetPositionFromCharIndex(box.TextLength - 1).Y; }
        catch { return lineH * 2; }
        // 已经滚过去的像素量（EM_GETSCROLLPOS），加上去才是“从顶部算起”的总高度。
        // 只补行号不行：自动换行时 RichEdit 的“行号”和“行高”口径不一致，误差能到几百像素。
        int h = lastY + lineH + GetScrollY();
        if (h < lineH) h = lineH;
        return h;
    }

    void SetScrollY(int y)
    {
        try
        {
            if (box == null) return;
            POINTAPI pt = new POINTAPI();
            pt.X = 0;
            pt.Y = y < 0 ? 0 : y;
            SendMessage(box.Handle, 0x4DE, IntPtr.Zero, ref pt);   // EM_SETSCROLLPOS
        }
        catch { }
    }

    int GetScrollY()
    {
        int y = 0;
        try
        {
            POINTAPI pt = new POINTAPI();
            if (box != null && SendMessage(box.Handle, 0x4DD, IntPtr.Zero, ref pt) != IntPtr.Zero) y = pt.Y;
        }
        catch { }
        return y < 0 ? 0 : y;
    }

    // 正文当前是不是已经贴在底部（不贴底说明用户正翻着上面在看）
    bool BoxAtBottom()
    {
        if (box == null) return true;
        try
        {
            int lineH = (int)Math.Ceiling(box.Font.GetHeight());
            if (lineH <= 0) return true;
            int contentH = BoxContentHeight();          // 已把滚动量算进去了
            int bottomEdge = contentH - GetScrollY();   // 当前视口下沿
            // 容差给两行：正文末尾还有行距和底部留白，滚到最底时量出来会差十几像素，
            // 只留半行容差会把“其实已经在底部”误判成“用户正翻着上面”，于是不再跟随新内容
            return bottomEdge <= box.ClientRectangle.Height + lineH * 2;
        }
        catch { return true; }
    }

    // 兜底：内容确实超出、可竖向滚动条却没被画出来（RichEdit 重排后偶尔不重算），
    // 重设一次 ScrollBars 逼它重新算，避免出现“内容滚不动、边上还没条”的怪状态
    void EnsureScrollBar()
    {
        if (box == null || box.TextLength <= 0) return;
        try
        {
            int lineH = (int)Math.Ceiling(box.Font.GetHeight());
            if (lineH <= 0) return;
            if (BoxContentHeight() <= box.ClientRectangle.Height + lineH / 2) return;
            int style = GetWindowLong(box.Handle, -16);   // GWL_STYLE
            if ((style & 0x00200000) != 0) return;        // WS_VSCROLL 已经在
            RichTextBoxScrollBars keep = box.ScrollBars;
            box.ScrollBars = RichTextBoxScrollBars.None;
            box.ScrollBars = keep;
            box.Invalidate();
        }
        catch { }
    }

    // 流式输出专用：只把新增的那一小截贴到末尾，不整篇重建。
    // 以前每 120ms 调一次 SetBoxText(全文)，RichEdit 会把内容整个重排、滚动位置归零，
    // 紧接着又被 ScrollToCaret 拉到最底 —— 用户正好滚轮往上回看时会被硬拽回来，
    // 滚动条也跟着从顶闪到底，看着就是“滚轮条一直在跳、还经常自己不见了”。
    string streamPrev = "";
    // v2.7.3 诊断计数：流式期间有多少次是「纯追加/尾部替换」，多少次退化成整篇重建
    internal static int streamAppends = 0;
    internal static int streamRebuilds = 0;

    void StreamPaint(string snap)
    {
        if (box == null) return;
        if (snap == null) snap = "";
        bool atBottom = BoxAtBottom();
        int keepScroll = GetScrollY();
        bool appended = false;
        if (streamPrev.Length > 0 && snap.Length > streamPrev.Length && snap.StartsWith(streamPrev))
        {
            string add = snap.Substring(streamPrev.Length);
            try
            {
                box.SuspendLayout();
                bool ro = box.ReadOnly;
                box.ReadOnly = false;          // RichEdit 只读时塞不进新字，临时放开
                box.SelectionStart = box.TextLength;
                box.SelectionLength = 0;
                box.SelectionColor = Pal.Ink;
                box.AppendText(add);
                box.ReadOnly = ro;
                box.ResumeLayout();
                appended = true;
                streamAppends++;
            }
            catch { try { box.ReadOnly = true; } catch { } }
        }
        else if (streamPrev.Length > 0 && box.TextLength > 0)
        {
            // v2.7.3：快照不是「上一次的纯末尾追加」时，别再整篇 SetBoxText 重建（RichEdit 全量重排
            // 一次二三百毫秒，四十来帧下来就是十几秒）。改成只把公共前缀之后那一小截替换掉，
            // 前面的行原地不动，重排范围小得多。缩写在写、语法块冒头这类打断前缀的情况都走这里。
            int k = 0, lim = Math.Min(snap.Length, streamPrev.Length);
            while (k < lim && snap[k] == streamPrev[k]) k++;
            if (k > 0 && k < box.TextLength)
            {
                try
                {
                    int tail = box.TextLength - k;
                    box.SuspendLayout();
                    bool ro = box.ReadOnly;
                    box.ReadOnly = false;
                    box.SelectionStart = k;
                    box.SelectionLength = tail;
                    box.SelectionColor = Pal.Ink;
                    box.SelectedText = snap.Substring(k);
                    box.ReadOnly = ro;
                    box.ResumeLayout();
                    appended = true;
                    streamAppends++;
                }
                catch { try { box.ReadOnly = true; } catch { } }
            }
        }
        if (!appended) { SetBoxText(snap); streamRebuilds++; }
        streamPrev = snap;
        EnsureScrollBar();
        if (atBottom)
        {
            try
            {
                box.SelectionStart = box.TextLength;
                box.SelectionLength = 0;
                box.ScrollToCaret();
            }
            catch { }
        }
        else
        {
            // AppendText 会把插入符带到末尾、顺手把视口拽到底部，用户正翻着上面看历史时
            // 每来一段新内容就被拽回去 —— 这里把视口放回原处，继续往下贴字但不打断阅读
            SetScrollY(keepScroll);
        }
    }

    void SetBoxText(string s)
    {
        Color body = Pal.Ink;
        string t = ToRichText(s);
        try
        {
            box.SuspendLayout();
            box.SelectionStart = 0;
            box.SelectionLength = 0;
            box.Text = "";
            box.SelectionColor = body;
            box.Text = t;
            // 整篇先刷成正文黑，再逐段上色
            box.SelectAll();
            box.SelectionColor = body;
            box.SelectionFont = box.Font;
            box.SelectionLength = 0;
            box.SelectionStart = 0;
            box.SelectionColor = body;
        }
        catch { box.Text = s; }
        finally { box.ResumeLayout(); }
    }

    void RenderResult(string content)
    {
        // v3 第 4 步：英文进来时【缩写版】整块不显示（中文翻英文时三档照旧）
        // 语法讲解开关：关掉就把【语法】整块挡掉，跟上一版逐字一致
        string gated = GrammarGate(AbbrevGate(content));
        lastFull = gated;
        string shown = NormalizeAbbrev(GuardPronoun(showApprox ? gated : StripApprox(gated)));
        // RichTextBox 内部把一次换行算作 1 个字符，而 Text 返回的是 \r\n（2 个）
        // 所以着色用的下标必须按「换行=1 字符」来算，否则每过一行就错位一格，越往后越偏，最后整篇都被染成标题色
        string rt = shown.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r");
        Color body = Pal.Ink;
        SetBoxText(shown);

        Font bold = null;
        try
        {
            bold = new Font(box.Font, FontStyle.Bold);

            // 1) 小节标题：每个标题用自己的颜色 + 加粗
            int idx = 0;
            while (true)
            {
                int s = rt.IndexOf('【', idx);
                if (s < 0) break;
                int e = rt.IndexOf('】', s + 1);
                if (e < 0) break;
                string name = rt.Substring(s + 1, e - s - 1);
                box.Select(s, e - s + 1);
                box.SelectionColor = HeadingColor(name);
                box.SelectionFont = bold;
                idx = e + 1;
            }

            // 2) 缩写三档：标签深蓝加粗，句子黑色，每档独占一行（跟生词一样排）
            string[] tags = new string[] { "轻度｜", "中度｜", "重度｜", "轻度|", "中度|", "重度|" };
            for (int t = 0; t < tags.Length; t++)
            {
                idx = 0;
                while (true)
                {
                    int s = rt.IndexOf(tags[t], idx);
                    if (s < 0) break;
                    box.Select(s, tags[t].Length);
                    box.SelectionColor = Pal.Accent;
                    box.SelectionFont = bold;
                    idx = s + tags[t].Length;
                }
            }
        }
        catch { }
        finally { if (bold != null) bold.Dispose(); }

        box.Select(0, 0);
        box.SelectionLength = 0;
        box.SelectionColor = body;
        box.SelectionFont = box.Font;
        box.ScrollToCaret();
        // 强制重画一遍：改了字号后 RichTextBox 有时不刷新，屏幕上就"看着空白"
        box.Invalidate();
        box.Refresh();
        EnsureScrollBar();
        if (!busy) FitContent();
        // v2.4：这一屏是什么语种，「复制全文」那两处入口的文案就跟着换
        RefreshCopyLabels();
        if (File.Exists(Path.Combine(LOG_DIR, "dump.flag")))
        {
            try { File.WriteAllText(Path.Combine(LOG_DIR, "dump.rtf"), box.Rtf, Encoding.ASCII); } catch { }
        }
    }

    // 光标/点击位置所在的那一行（按逻辑行取，够读一整句）
    string CurrentLineText()
    {
        if (box.TextLength == 0) return "";
        string all = box.Text;
        int idx = box.SelectionStart;
        if (idx > all.Length) idx = all.Length;
        int start = (idx <= 0) ? 0 : all.LastIndexOf('\n', Math.Max(0, idx - 1)) + 1;
        int end = all.IndexOf('\n', idx);
        if (end < 0) end = all.Length;
        if (start >= end) return "";
        return all.Substring(start, end - start).Trim();
    }

    string PickCurrentText()
    {
        if (box.SelectionLength > 0) return box.SelectedText.Trim();
        string line = CurrentLineText();
        if (line.Length == 0) return box.Text.Trim();
        return line;
    }

    void CopyAll()
    {
        string t = CopyAllText();
        if (t.Length == 0) { tip.Text = "还没有内容可以复制"; return; }
        string lg = MainOutLangName();
        try
        {
            Clipboard.SetText(t);
            tip.Text = "全文都复制好了" + (lg.Length > 0 ? "（" + lg + "）" : "");
        }
        catch { tip.Text = "复制失败，手动选一下"; }
    }

    void CopyCurrentLine()
    {
        if (box.TextLength == 0) { tip.Text = "还没有内容可以复制"; return; }
        string t = PickCurrentText();
        if (t.Length == 0) { tip.Text = "没定位到句子，先点一下那一行"; return; }
        try { Clipboard.SetText(t); tip.Text = "这一句复制好了：" + (t.Length > 18 ? t.Substring(0, 18) + "…" : t); }
        catch { tip.Text = "复制失败，手动选一下"; }
    }

    // ====== 划词自动弹窗（可选，默认关）======
    void AutoTick()
    {
        bool down = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        if (!prevMouseDown && down)
        {
            downPos = Cursor.Position; downAt = DateTime.Now; moved = false;
        }
        else if (down && prevMouseDown)
        {
            Point c = Cursor.Position;
            if (Math.Abs(c.X - downPos.X) > 4 || Math.Abs(c.Y - downPos.Y) > 4) moved = true;
        }
        else if (prevMouseDown && !down)
        {
            // 只有"划选"或"双击选词"才去取词；单纯点一下绝不去按 Ctrl+C，免得抢剪贴板
            bool dbl = (!moved) && (DateTime.Now - downAt).TotalMilliseconds < 420;
            Log("划词[诊断]：鼠标抬起 拖动=" + moved + " 按住=" + (int)(DateTime.Now - downAt).TotalMilliseconds + "ms 双击判定=" + dbl);
            if (moved || dbl) AutoAfterClick(); else Log("划词[诊断]：抬起但不是划选也不是双击，忽略");
        }
        prevMouseDown = down;
    }
    Point downPos = Point.Empty;
    DateTime downAt = DateTime.MinValue;
    bool moved = false;

    void AutoAfterClick()
    {
        Log("划词[诊断]：进入取词 开关=" + autoPopup + " busy=" + busy + " 距上次=" + (int)(DateTime.Now - lastAuto).TotalMilliseconds + "ms 前台是本程序=" + (GetForegroundWindow() == Handle));
        if (!autoPopup || busy) return;
        if ((DateTime.Now - lastAuto).TotalMilliseconds < 900) return;
        if (GetForegroundWindow() == Handle) return;   // 在自己窗口里点，不理
        Thread t = new Thread(delegate () {
            string before = null;
            try { if (Clipboard.ContainsText()) before = Clipboard.GetText(); } catch { }
            string got = TryCopySelection(320);
            Log("划词[诊断]：取词结果 空=" + (got == null) + " 长度=" + (got == null ? 0 : got.Trim().Length) + " 剪贴板原文=" + (before == null ? "(无)" : (before.Length > 20 ? before.Substring(0, 20) : before)));
            if (got == null) return;
            got = got.Trim();
            if (got.Length < 2 || got.Length > 400) { Log("划词[诊断]：被长度过滤 长度=" + got.Length); return; }
            if (got == before) { Log("划词[诊断]：被基线过滤 got==before"); return; }
            if (got == lastInput) { Log("划词[诊断]：被 lastInput 过滤 got=" + got); return; }
            bool hasLetter = false;
            foreach (char c in got) if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) { hasLetter = true; break; }
            if (!hasLetter) { Log("划词[诊断]：被无英文字母过滤"); return; }
            Log("划词[诊断]：命中，去弹窗查询：" + (got.Length > 24 ? got.Substring(0, 24) : got));
            lastAuto = DateTime.Now;
            Ui(delegate () {
                ShowPanel();
                BeginQuery(got, false);
            });
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
    }

    static string TryCopySelection(int waitMs)
    {
        string before = null;
        try { if (Clipboard.ContainsText()) before = Clipboard.GetText(); } catch { }
        try { SendKeys.SendWait("^c"); } catch { return null; }
        int waited = 0;
        while (waited < waitMs)
        {
            Thread.Sleep(40);
            waited += 40;
            try
            {
                if (Clipboard.ContainsText())
                {
                    string now = Clipboard.GetText();
                    if (now != before) return now;
                }
            }
            catch { }
        }
        return null;
    }

    // ====== 历史记录 ======
    void ShowHistory()
    {
        if (history.Count == 0) { tip.Text = "还没有历史记录，先查一句试试"; return; }
        List<string[]> rows = new List<string[]>();
        foreach (HistItem h in history)
        {
            string t = h.Time;
            if (t.Length >= 16) t = t.Substring(5, 11);
            rows.Add(new string[] { t, h.Input, h.Result });
        }
        HistoryForm f = new HistoryForm(rows, this);
        f.ShowDialog(this);
        if (f.Picked != null && f.Picked.Length > 0)
        {
            ShowPanel();
            // 回看历史时按这条记录当时的输入判语言：英文进来的旧记录同样不显示【缩写版】
            for (int k = 0; k < history.Count; k++)
                if (history[k].Result == f.Picked) { lastInput = history[k].Input; break; }
            RenderResult(f.Picked);
            tip.Text = "翻回来一条历史记录";
        }
        f.Dispose();
    }

    // ====== 生词本 ======
    void ShowWordBook()
    {
        RebuildWordsTxt(null);
        WordBookForm f = new WordBookForm(this);
        f.ShowDialog(this);
        if (f.StartReview)
        {
            f.Dispose();
            ShowWordReview();
            return;
        }
        f.Dispose();
    }

    // ====== 生词复习（卡片背单词）======
    void ShowWordReview()
    {
        WordReviewForm f = new WordReviewForm(this);
        f.ShowDialog(this);
        f.Dispose();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // v2.5.2：退出清理全部收在这里（托盘、热键、语音、定时器），
        // 菜单点击回调里一个都不碰 —— 原因见 RequestExit 的注释。
        // 这里必须幂等 + 每一步单独兜住异常：任何一步抛出去，
        // 都会让"托盘已隐藏、窗口还在"的僵尸状态再出现一次。
        if (!closingCleanupDone)
        {
            closingCleanupDone = true;
            try { UnregisterHotKey(Handle, HK_ID); } catch { }
            try { UnregisterHotKey(Handle, HK_OCR_ID); } catch { }
            try { SavePos(); } catch { }
            try
            {
                if (autoTimer != null) { autoTimer.Stop(); autoTimer.Dispose(); autoTimer = null; }
            }
            catch { }
            if (tts != null) { try { tts.SpeakAsyncCancelAll(); tts.Dispose(); } catch { } tts = null; }
            if (tray != null)
            {
                try { tray.Visible = false; } catch { }
                try { tray.Dispose(); } catch { }
            }
        }
        base.OnFormClosing(e);
    }

    // ====== v2.5.2：退出流程 ======
    // 「退出」曾经是：delegate { tray.Visible = false; Application.Exit(); }
    // 两个致命问题：
    //   1) 回调还在 ToolStrip 的鼠标消息里，Application.Exit() 内部要枚举 Application.OpenForms
    //      并逐个关窗，关窗过程又改动这个集合 → InvalidOperationException「集合已修改」。
    //   2) tray.Visible 在这行前面就先设了 false，异常把它后面的一切全打断：
    //      托盘没了、热键没注销、窗口还活着、任务栏也看不见 —— 用户彻底打不开的僵尸进程。
    // 现在：只置标志 + BeginInvoke 回到消息循环再关，清理动作全交给 OnFormClosing，
    // 最后用 Environment.Exit(0) 兜底保证进程一定消失。
    void RequestExit()
    {
        if (exitRequested) return;
        exitRequested = true;
        Log("用户从托盘菜单点了「退出」");
        try
        {
            BeginInvoke((MethodInvoker)delegate { DoExit(); });
        }
        catch
        {
            // 窗口句柄已经没了（正在关），直接走退出兜底，别再拖住进程
            DoExit();
        }
    }

    static void DoExit()
    {
        try { Application.Exit(); }
        catch (Exception ex)
        {
            try { Log("Application.Exit 抛异常，转入 Environment.Exit 兜底：" + ex.GetType().Name + " " + ex.Message); } catch { }
        }
        // 兜底：Application.Exit 抛异常、或有窗体/线程没关干净时，保证进程真的消失，
        // 而不是变成"没有托盘图标、没有热键、任务栏也看不见"的僵尸
        try { Environment.Exit(0); } catch { }
    }

    // 列表项两行绘制：上大下小，行高按字体实测算，绝不叠字
    static void DrawTwoLine(Graphics g, Rectangle b, string l1, string l2, Font f1, Font f2, Color c1, Color c2)
    {
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        int padL = 14;
        int wAvail = Math.Max(40, b.Width - padL - 14);
        Size m1 = TextRenderer.MeasureText(g, "测", f1, new Size(wAvail, 1000), TextFormatFlags.NoPadding);
        Size m2 = TextRenderer.MeasureText(g, "测", f2, new Size(wAvail, 1000), TextFormatFlags.NoPadding);
        int h1 = m1.Height;
        int h2 = m2.Height;
        int total = h1 + h2 + 6;
        int top = b.Top + Math.Max(4, (b.Height - total) / 2);
        TextRenderer.DrawText(g, l1, f1, new Rectangle(b.Left + padL, top, wAvail, h1), c1,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, l2, f2, new Rectangle(b.Left + padL, top + h1 + 5, wAvail, h2), c2,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
    }

    // 底部按钮统一靠右排列，跟着面板宽度走，绝不跑出窗口
    static void LayoutRight(Panel bottom, int rightPad, params Control[] btns)
    {
        EventHandler f = delegate {
            int x = bottom.ClientSize.Width - rightPad;
            for (int i = btns.Length - 1; i >= 0; i--)
            {
                if (btns[i] == null) continue;
                x -= btns[i].Width;
                btns[i].Left = x;
                x -= 8;
            }
        };
        bottom.Resize += f;
        f(null, EventArgs.Empty);
    }

    // ====== v2.5.3 第一档 ⑥：「关于」小窗 ======
    // 原来程序里任何地方都看不到版本号：截图来问「这是怎么回事」，没人知道你手上是哪一版。
    // 现在这里给全六样：程序名 / 版本号 / 编译时间 / 源码编号 / 仓库地址 / 数据目录，
    // 外加「复制这段信息」——截图或粘一行发过来，版本就能对上。
    internal class AboutForm : Form
    {
        float dpiScale = 1f;

        // 「复制这段信息」用的整块文本（排查时整段贴过来就够）
        internal string InfoBlock = "";

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

        // 一行「标签 + 可选中复制的值」：值用无边框只读 TextBox，鼠标能选、能 Ctrl+C，
        // 比 Label 强的地方就在这儿（路径太长时可以只挑一截复制）。
        void AddRow(string label, string value, int y)
        {
            Label lb = new Label();
            lb.Text = label;
            lb.Font = new Font("Microsoft YaHei UI", 9f);
            lb.ForeColor = Pal.InkMuted;
            lb.AutoSize = false;
            lb.TextAlign = ContentAlignment.MiddleLeft;
            lb.Location = new Point(S(20), S(y));
            lb.Size = new Size(S(76), S(22));
            Controls.Add(lb);

            TextBox tb = new TextBox();
            tb.Text = value;
            tb.ReadOnly = true;
            tb.BorderStyle = BorderStyle.None;
            tb.BackColor = Pal.Surface;
            tb.ForeColor = Pal.InkSecondary;
            tb.Font = new Font("Microsoft YaHei UI", 9f);
            tb.ShortcutsEnabled = true;
            tb.Location = new Point(S(100), S(y + 4));
            tb.Width = S(300);
            tb.Tag = label;      // 自检按标签取值用
            Controls.Add(tb);
        }

        public AboutForm()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) dpiScale = g.DpiX / 96f;
            }
            catch { dpiScale = 1f; }
            if (dpiScale < 1f || dpiScale > 3f) dpiScale = 1f;

            string ver = APP_VER;
            string stamp = AppStamp();
            string rev = SrcRev();
            string repo = "https://github.com/xiaobinggan94-debug/kairenzuo";
            string dataDir = LOG_DIR;

            InfoBlock = AppTitle + " v" + ver + "\r\n" +
                        "编译 " + stamp + "\r\n" +
                        "源码 " + rev + "\r\n" +
                        "仓库 " + repo + "\r\n" +
                        "数据 " + dataDir + "\r\n";

            Text = "关于";
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Pal.Surface;
            ForeColor = Pal.Ink;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            ClientSize = new Size(S(420), S(252));

            Label name = new Label();
            name.Text = AppTitle;
            name.Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold);
            name.ForeColor = Pal.Ink;
            name.AutoSize = true;
            name.Location = new Point(S(20), S(16));
            Controls.Add(name);

            Label v = new Label();
            v.Text = "v" + ver;
            v.Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
            v.ForeColor = Pal.Accent;
            v.AutoSize = true;
            v.Location = new Point(S(20), S(46));
            Controls.Add(v);

            Label st = new Label();
            st.Text = "编译 " + stamp;
            st.Font = new Font("Microsoft YaHei UI", 8.5f);
            st.ForeColor = Pal.InkMuted;
            st.AutoSize = false;
            st.TextAlign = ContentAlignment.MiddleRight;
            st.Location = new Point(S(150), S(44));
            st.Size = new Size(S(250), S(22));
            Controls.Add(st);

            Panel hair1 = new Panel();
            hair1.BackColor = Pal.Hair;
            hair1.Location = new Point(0, S(76));
            hair1.Size = new Size(S(420), S(1));
            Controls.Add(hair1);

            AddRow("源码编号", rev, 88);
            AddRow("仓库地址", repo, 114);
            AddRow("数据目录", dataDir, 140);

            Label hint = new Label();
            hint.Text = "截图发我、或者点「复制这段信息」，版本就能对上。";
            hint.Font = new Font("Microsoft YaHei UI", 8.5f);
            hint.ForeColor = Pal.InkMuted;
            hint.AutoSize = true;
            hint.Location = new Point(S(20), S(167));
            Controls.Add(hint);

            Panel hair2 = new Panel();
            hair2.BackColor = Pal.Hair;
            hair2.Location = new Point(0, S(190));
            hair2.Size = new Size(S(420), S(1));
            Controls.Add(hair2);

            RoundButton btnOpen = MakeBtn("打开数据目录", 116, false);
            btnOpen.Location = new Point(S(20), S(204));
            btnOpen.Click += delegate {
                try { Process.Start("explorer.exe", "\"" + LOG_DIR + "\""); }
                catch (Exception ex) { MessageBox.Show(this, "打不开：" + ex.Message, "关于"); }
            };
            Controls.Add(btnOpen);

            RoundButton btnCopy = MakeBtn("复制这段信息", 112, false);
            btnCopy.Location = new Point(S(144), S(204));
            btnCopy.Click += delegate {
                try { Clipboard.SetText(InfoBlock); btnCopy.Text = "已复制"; }
                catch { btnCopy.Text = "复制失败"; }
            };
            Controls.Add(btnCopy);

            RoundButton ok = MakeBtn("知道了", 80, true);
            ok.Location = new Point(S(320), S(204));
            ok.DialogResult = DialogResult.OK;
            Controls.Add(ok);

            AcceptButton = ok;
            CancelButton = ok;
        }
    }

    // ====== 语音设置小窗 ======
    // 2026-10-25 重做外观：原来是一排写死坐标的系统灰按钮，跟主面板的圆角浅色风格对不上。
    // 现在统一走 RoundButton + 蓝色主按钮，补了「先听一句」试听，语速给中文档位提示。
    class VoiceForm : Form
    {
        ComboBox cbVoice;
        TrackBar tbRate;
        Label lbRate;
        RoundButton btnTry;
        SpeechSynthesizer preview;
        float dpiScale = 1f;

        public string VoiceName = "Zira";
        public int Rate = -1;

        static Color InkC { get { return Pal.Ink; } }
        static Color SubC { get { return Pal.InkMuted; } }
        static Color HairC { get { return Pal.Hair; } }

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

        public VoiceForm(string voice, int rate)
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) dpiScale = g.DpiX / 96f;
            }
            catch { dpiScale = 1f; }
            if (dpiScale < 1f || dpiScale > 3f) dpiScale = 1f;

            Text = "语音设置";
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Pal.Surface;
            ForeColor = InkC;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            ClientSize = new Size(S(372), S(258));

            Label cap = new Label();
            cap.Text = "读的时候用哪个声音";
            cap.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            cap.ForeColor = InkC;
            cap.AutoSize = true;
            cap.Location = new Point(S(20), S(18));
            Controls.Add(cap);

            cbVoice = new ComboBox();
            cbVoice.DropDownStyle = ComboBoxStyle.DropDownList;
            cbVoice.FlatStyle = FlatStyle.Flat;
            cbVoice.Font = new Font("Microsoft YaHei UI", 9.5f);
            cbVoice.Location = new Point(S(20), S(44));
            cbVoice.Width = S(332);
            cbVoice.Items.Add("女声 Zira（清楚，适合听句子）");
            cbVoice.Items.Add("男声 David（低一点，适合长段）");
            cbVoice.SelectedIndex = (voice == "David") ? 1 : 0;
            // 下拉列表也走自绘，选中项跟菜单同色（浅蓝底 + 深蓝字），不再是系统那种饱和蓝
            cbVoice.DrawMode = DrawMode.OwnerDrawFixed;
            cbVoice.ItemHeight = S(24);
            cbVoice.DrawItem += delegate (object s, DrawItemEventArgs e) {
                if (e.Index < 0 || e.Index >= cbVoice.Items.Count) return;
                bool sel = (e.State & DrawItemState.Selected) != 0;
                bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
                bool hot = sel && !edit;
                using (SolidBrush bg = new SolidBrush(hot ? Pal.AccentSoft : Pal.Surface))
                    e.Graphics.FillRectangle(bg, e.Bounds);
                Rectangle tb = new Rectangle(e.Bounds.Left + S(6), e.Bounds.Top, e.Bounds.Width - S(10), e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, cbVoice.Items[e.Index].ToString(), cbVoice.Font, tb,
                    hot ? Pal.Accent : InkC,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            };
            Controls.Add(cbVoice);

            Label hint = new Label();
            hint.Text = "换完点「就这样」立刻生效，下次读就用这个声音。";
            hint.ForeColor = SubC;
            hint.Font = new Font("Microsoft YaHei UI", 8.5f);
            hint.AutoSize = true;
            hint.Location = new Point(S(20), S(80));
            Controls.Add(hint);

            Label l2 = new Label();
            l2.Text = "语速";
            l2.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            l2.ForeColor = InkC;
            l2.AutoSize = true;
            l2.Location = new Point(S(20), S(112));
            Controls.Add(l2);

            lbRate = new Label();
            lbRate.AutoSize = false;
            lbRate.TextAlign = ContentAlignment.MiddleRight;
            lbRate.Font = new Font("Microsoft YaHei UI", 9f);
            lbRate.ForeColor = Pal.Accent;
            lbRate.Location = new Point(S(220), S(110));
            lbRate.Size = new Size(S(132), S(20));
            Controls.Add(lbRate);

            int v0 = (rate < -10) ? -10 : (rate > 6 ? 6 : rate);
            tbRate = new TrackBar();
            tbRate.Minimum = -10;
            tbRate.Maximum = 6;
            tbRate.TickFrequency = 2;
            tbRate.SmallChange = 1;
            tbRate.LargeChange = 2;
            tbRate.AutoSize = false;
            tbRate.Height = S(36);
            tbRate.Location = new Point(S(14), S(130));
            tbRate.Width = S(342);
            tbRate.Value = v0;
            Controls.Add(tbRate);
            // 先挂事件再赋文本，避免初值时 lbRate 还是 null
            lbRate.Text = RateText(v0);
            tbRate.ValueChanged += delegate { lbRate.Text = RateText(tbRate.Value); };

            Label tSlow = new Label();
            tSlow.Text = "慢";
            tSlow.ForeColor = SubC;
            tSlow.Font = new Font("Microsoft YaHei UI", 8.5f);
            tSlow.AutoSize = true;
            tSlow.Location = new Point(S(18), S(166));
            Controls.Add(tSlow);

            Label tMid = new Label();
            tMid.Text = "正常";
            tMid.ForeColor = SubC;
            tMid.Font = new Font("Microsoft YaHei UI", 8.5f);
            tMid.AutoSize = true;
            tMid.Location = new Point(S(156), S(166));
            Controls.Add(tMid);

            Label tFast = new Label();
            tFast.Text = "快";
            tFast.ForeColor = SubC;
            tFast.Font = new Font("Microsoft YaHei UI", 8.5f);
            tFast.AutoSize = true;
            tFast.Location = new Point(S(322), S(166));
            Controls.Add(tFast);

            Panel hair = new Panel();
            hair.BackColor = HairC;
            hair.Location = new Point(0, S(196));
            hair.Size = new Size(Width, S(1));
            hair.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Controls.Add(hair);

            btnTry = MakeBtn("先听一句", 92, false);
            btnTry.Location = new Point(S(20), S(210));
            btnTry.Click += delegate { PlayPreview(); };
            Controls.Add(btnTry);

            Button cancel = MakeBtn("算了", 80, false);
            cancel.Location = new Point(S(168), S(210));
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            Button ok = MakeBtn("就这样", 96, true);
            ok.Location = new Point(S(256), S(210));
            ok.DialogResult = DialogResult.OK;
            Controls.Add(ok);

            AcceptButton = ok;
            CancelButton = cancel;

            FormClosed += delegate
            {
                try
                {
                    if (preview != null) { preview.SpeakAsyncCancelAll(); preview.Dispose(); }
                }
                catch { }
                preview = null;
            };
        }

        static string RateText(int v)
        {
            string tag;
            if (v >= 4) tag = "很快";
            else if (v >= 1) tag = "稍快";
            else if (v == 0) tag = "正常";
            else if (v >= -4) tag = "稍慢";
            else tag = "很慢";
            return (v > 0 ? "+" + v : v.ToString()) + " · " + tag;
        }

        void PlayPreview()
        {
            try
            {
                if (preview == null) preview = new SpeechSynthesizer();
                try { preview.SpeakAsyncCancelAll(); } catch { }
                string key = (cbVoice.SelectedIndex == 1) ? "David" : "Zira";
                string picked = null;
                foreach (InstalledVoice v in preview.GetInstalledVoices())
                {
                    try
                    {
                        if (!v.Enabled) continue;
                        if (v.VoiceInfo.Name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) { picked = v.VoiceInfo.Name; break; }
                    }
                    catch { }
                }
                if (picked != null) { try { preview.SelectVoice(picked); } catch { } }
                int r = tbRate.Value;
                preview.Rate = (r < -10) ? -10 : (r > 10 ? 10 : r);
                preview.Volume = 100;
                preview.SpeakAsync("This is how I will read it. The meeting has been moved to three o'clock tomorrow afternoon.");
            }
            catch { }
        }

        public void Collect()
        {
            VoiceName = (cbVoice.SelectedIndex == 1) ? "David" : "Zira";
            Rate = tbRate.Value;
        }
    }

    // ====== 框选截图小窗 ======
    class CaptureForm : Form
    {
        Point start;
        bool dragging;
        Rectangle rect;
        Bitmap shot;
        public Image Captured;

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        public CaptureForm()
        {
            try { SetProcessDPIAware(); } catch { }
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = SystemInformation.VirtualScreen;
            TopMost = true;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            Cursor = Cursors.Cross;
            BackColor = Pal.Shade;
            Opacity = 0.28;
            KeyPreview = true;
            KeyDown += delegate (object s, KeyEventArgs e) {
                if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            };
            MouseDown += delegate (object s, MouseEventArgs e) {
                if (e.Button == MouseButtons.Right) { DialogResult = DialogResult.Cancel; Close(); return; }
                start = e.Location;
                dragging = true;
            };
            MouseMove += delegate (object s, MouseEventArgs e) {
                if (!dragging) return;
                Point p = e.Location;
                int x = Math.Min(start.X, p.X);
                int y = Math.Min(start.Y, p.Y);
                int w = Math.Abs(p.X - start.X);
                int h = Math.Abs(p.Y - start.Y);
                rect = new Rectangle(x, y, w, h);
                Invalidate();
            };
            MouseUp += delegate (object s, MouseEventArgs e) {
                dragging = false;
                if (rect.Width < 6 || rect.Height < 6) { DialogResult = DialogResult.Cancel; Close(); return; }
                try
                {
                    shot = new Bitmap(rect.Width, rect.Height);
                    using (Graphics g = Graphics.FromImage(shot))
                        g.CopyFromScreen(rect.Left + Bounds.Left, rect.Top + Bounds.Top, 0, 0, rect.Size);
                    Captured = (Image)shot;
                }
                catch { Captured = null; }
                DialogResult = DialogResult.OK;
                Close();
            };
            Paint += delegate (object s, PaintEventArgs e) {
                if (rect.Width > 0)
                {
                    using (Pen p = new Pen(Pal.Mark, 2))
                        e.Graphics.DrawRectangle(p, rect);
                    using (SolidBrush b = new SolidBrush(Pal.Surface))
                    using (Font f = new Font("Microsoft YaHei UI", 10f))
                        e.Graphics.DrawString("框住要查的英文，松手就认字（右键/Esc 取消）", f, b, 20, 20);
                }
            };
        }
    }

    // ====== 历史记录小窗 ======
    class HistoryForm : Form
    {
        List<string[]> items;       // 全量
        List<string[]> shown;       // 当前筛出来的
        ListBox lb;
        TextBox search;
        Label hint;
        public string Picked;

        public HistoryForm(List<string[]> list, IWin32Window owner)
        {
            items = list;
            shown = new List<string[]>(list);
            Text = "历史记录（最近 20 条）";
            ClientSize = new Size(540, 520);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            BackColor = Pal.Surface;
            MinimizeBox = false;

            Label hint = new Label();
            hint.Text = "点一条，再按「翻回来看」或直接双击 = 重新显示那次的结果；上面可以打字搜索";
            hint.Dock = DockStyle.Top;
            hint.Height = 26;
            hint.Padding = new Padding(8, 0, 0, 0);
            hint.TextAlign = ContentAlignment.MiddleLeft;
            hint.ForeColor = Pal.InkHint;
            Controls.Add(hint);

            // v2.8.0：搜索条。原文和结果一起搜，边打边筛（历史只有 20 条，本地过滤零开销）
            Panel searchHost = new Panel();
            searchHost.Dock = DockStyle.Top;
            searchHost.Height = 40;
            searchHost.Padding = new Padding(10, 6, 10, 6);
            search = new TextBox();
            search.Dock = DockStyle.Fill;
            search.BorderStyle = BorderStyle.FixedSingle;
            search.Font = new Font("Microsoft YaHei UI", 10f);
            search.ForeColor = Pal.Ink;
            search.BackColor = Pal.Surface;
            search.TextChanged += delegate { Refilter(); };
            search.KeyDown += delegate (object s, KeyEventArgs e) {
                if (e.KeyCode == Keys.Escape)
                {
                    if (search.Text.Length > 0) { search.Text = ""; e.SuppressKeyPress = true; }
                }
                else if (e.KeyCode == Keys.Enter) { Take(); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Down)
                {
                    if (lb.Items.Count > 0) lb.SelectedIndex = Math.Min(lb.SelectedIndex + 1, lb.Items.Count - 1);
                    e.SuppressKeyPress = true;
                }
                else if (e.KeyCode == Keys.Up)
                {
                    if (lb.Items.Count > 0) lb.SelectedIndex = Math.Max(lb.SelectedIndex - 1, 0);
                    e.SuppressKeyPress = true;
                }
            };
            searchHost.Controls.Add(search);
            Controls.Add(searchHost);

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 46;
            RoundButton ok = MakeDialogButton("翻回来看", 100, true);
            ok.Location = new Point(336, 9);
            ok.Click += delegate { Take(); };
            bottom.Controls.Add(ok);
            RoundButton close = MakeDialogButton("关掉", 80, false);
            close.DialogResult = DialogResult.Cancel;
            close.Location = new Point(444, 9);
            bottom.Controls.Add(close);
            HairlineTop(bottom);
            LayoutRight(bottom, 14, ok, close);
            Controls.Add(bottom);

            lb = new ListBox();
            lb.Dock = DockStyle.Fill;
            lb.IntegralHeight = false;
            lb.BorderStyle = BorderStyle.None;
            lb.ItemHeight = 54;
            lb.DrawMode = DrawMode.OwnerDrawFixed;
            lb.DrawItem += delegate (object s, DrawItemEventArgs e) {
                if (e.Index < 0 || e.Index >= shown.Count) return;
                bool sel = e.State.HasFlag(DrawItemState.Selected);
                DrawListRowBg(e.Graphics, e.Bounds, sel);
                string[] it = shown[e.Index];
                string when = it.Length > 0 ? it[0] : "";
                string q = it.Length > 1 ? it[1].Replace("\r", " ").Replace("\n", " ").Trim() : "";
                Color cBody = sel ? Pal.Accent : Pal.Ink;
                Color cWhen = sel ? Pal.InkOnSoft : Pal.InkMuted3;
                using (Font fb = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold))
                using (Font fs = new Font("Microsoft YaHei UI", 8.5f))
                {
                    DrawTwoLine(e.Graphics, e.Bounds, q, when, fb, fs, cBody, cWhen);
                }
            };
            lb.DoubleClick += delegate { Take(); };
            Controls.Add(lb);
            lb.BringToFront();
            Refilter();                       // 先按空关键词铺一遍，标题里的条数也在这里更新
        }

        // 关键词同时搜「原文」和「翻出来的译文」；结果为空时列表干净，不会残留上一次的行
        void Refilter()
        {
            string kw = search == null ? "" : search.Text.Trim();
            shown.Clear();
            for (int i = 0; i < items.Count; i++)
            {
                string[] it = items[i];
                if (it == null || it.Length < 3) continue;
                if (kw.Length == 0
                    || it[1].IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0
                    || it[2].IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                    shown.Add(it);
            }
            lb.BeginUpdate();
            try
            {
                lb.Items.Clear();
                for (int i = 0; i < shown.Count; i++) lb.Items.Add(shown[i][1]);
            }
            finally { lb.EndUpdate(); }
            if (shown.Count > 0) lb.SelectedIndex = 0;
            Text = kw.Length == 0
                ? "历史记录（" + items.Count + " 条）"
                : "历史记录（筛出 " + shown.Count + " / " + items.Count + " 条）";
        }

        void Take()
        {
            if (lb.SelectedIndex < 0 || lb.SelectedIndex >= shown.Count) { DialogResult = DialogResult.Cancel; Close(); return; }
            Picked = shown[lb.SelectedIndex][2];
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    // ====== 生词本小窗 ======
    class WordBookForm : Form
    {
        RichTextBox view;
        QueryForm owner;
        Label stat;
        public bool StartReview;      // 点了「开始复习」，关掉小窗后直接进复习卡片
        public WordBookForm(QueryForm o)
        {
            owner = o;
            Text = "生词本（去重 + 按日期分组）";
            ClientSize = new Size(560, 480);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            BackColor = Pal.Surface;
            MinimizeBox = false;

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 48;

            // v2.8.0：原来只能看，看完就关。现在底栏多一个「开始复习」，直接进卡片背单词
            RoundButton review = MakeDialogButton("开始复习", 100, true);
            review.Location = new Point(130, 10);
            review.Click += delegate { StartReview = true; DialogResult = DialogResult.OK; Close(); };
            bottom.Controls.Add(review);

            RoundButton exp = MakeDialogButton("导出 CSV", 100, false);
            exp.Location = new Point(244, 10);
            exp.Click += delegate { ExportCsv(); };
            bottom.Controls.Add(exp);

            RoundButton open = MakeDialogButton("打开文件夹", 110, false);
            open.Location = new Point(352, 10);
            open.Click += delegate {
                try { Process.Start("explorer.exe", "\"" + Path.GetDirectoryName(WordsTxtPath()) + "\""); } catch { }
            };
            bottom.Controls.Add(open);

            RoundButton close = MakeDialogButton("关掉", 80, false);
            close.DialogResult = DialogResult.Cancel;
            close.Location = new Point(466, 10);
            bottom.Controls.Add(close);
            HairlineTop(bottom);
            LayoutRight(bottom, 14, review, exp, open, close);
            Controls.Add(bottom);

            stat = new Label();
            stat.Dock = DockStyle.Top;
            stat.Height = 26;
            stat.Padding = new Padding(8, 0, 0, 0);
            stat.TextAlign = ContentAlignment.MiddleLeft;
            stat.ForeColor = Pal.InkHint;
            Controls.Add(stat);

            view = new RichTextBox();
            view.Dock = DockStyle.Fill;
            view.ReadOnly = true;
            view.BorderStyle = BorderStyle.None;
            view.BackColor = Pal.Surface;
            view.Font = new Font("Microsoft YaHei UI", 9.5f);
            view.WordWrap = true;
            Controls.Add(view);
            view.BringToFront();

            Fill();
        }

        void Fill()
        {
            List<string[]> rows = ReadWordsTsV();
            int days = 0;
            string lastDay = "";
            // 日期标题深蓝加粗、单词黑色、释义淡灰，一眼分层，不再是一整片黑字
            Font fHead = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            Font fWord = new Font("Microsoft YaHei UI", 9.5f);
            Font fMean = new Font("Microsoft YaHei UI", 9f);
            Color cHead = Pal.Accent;
            Color cWord = Pal.Ink;
            Color cMean = Pal.InkMuted4;
            try
            {
                view.Clear();
                if (rows.Count == 0)
                {
                    view.SelectionFont = fMean;
                    view.SelectionColor = cMean;
                    view.AppendText("生词本还是空的。\r\n\r\n每次查完，结果里的英文单词会自动进这里（自动去重，同一天重复查到同一个词只留一条）。");
                    stat.Text = "一共 0 条";
                    return;
                }
                foreach (string[] r in rows)
                {
                    string day = r.Length > 0 ? r[0] : "";
                    if (day != lastDay)
                    {
                        if (lastDay != "") view.AppendText("\r\n");
                        view.SelectionFont = fHead;
                        view.SelectionColor = cHead;
                        view.AppendText("【" + (day.Length >= 10 ? day.Substring(0, 10) : day) + "】\r\n");
                        lastDay = day;
                        days++;
                    }
                    string word = r.Length > 1 ? r[1] : "";
                    string phon = r.Length > 2 ? r[2] : "";
                    string mean = r.Length > 3 ? r[3] : "";
                    string approx = r.Length > 4 ? r[4] : "";
                    // 每个词两行：第一行单词 + 音标，第二行中文意思（缩进），不会糊成一团
                    view.SelectionFont = fWord;
                    view.SelectionColor = cWord;
                    view.AppendText("· " + word + (phon.Length > 0 ? "   " + phon : "") + "\r\n");
                    string second = "     " + mean;
                    if (approx.Length > 0) second += "  近音：" + approx;
                    if (second.Trim().Length > 0)
                    {
                        view.SelectionFont = fMean;
                        view.SelectionColor = cMean;
                        view.AppendText(second + "\r\n");
                    }
                }
                view.SelectionStart = 0;
                view.SelectionLength = 0;
                stat.Text = "一共 " + rows.Count + " 条，分布在 " + days + " 天";
            }
            finally
            {
                fHead.Dispose();
                fWord.Dispose();
                fMean.Dispose();
            }
        }

        void ExportCsv()
        {
            try
            {
                List<string[]> rows = ReadWordsTsV();
                if (rows.Count == 0) { MessageBox.Show("生词本是空的，先查几句再来导出。"); return; }
                Directory.CreateDirectory(LOG_DIR);
                string csv = Path.Combine(LOG_DIR, "生词本_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".csv");
                StringBuilder sb = new StringBuilder();
                sb.Append("日期,单词,音标,意思,近音\r\n");
                foreach (string[] r in rows)
                {
                    string d = r.Length > 0 ? r[0] : "";
                    string w = r.Length > 1 ? r[1] : "";
                    string p = r.Length > 2 ? r[2] : "";
                    string m = r.Length > 3 ? r[3] : "";
                    string a = r.Length > 4 ? r[4] : "";
                    sb.Append(Quote(d) + "," + Quote(w) + "," + Quote(p) + "," + Quote(m) + "," + Quote(a) + "\r\n");
                }
                File.WriteAllText(csv, sb.ToString(), new UTF8Encoding(true));
                MessageBox.Show("导出好了：\n" + csv);
                try { Process.Start("explorer.exe", "/select,\"" + csv + "\""); } catch { }
            }
            catch (Exception ex)
            {
                MessageBox.Show("导出失败：" + ex.Message);
            }
        }

        static string Quote(string s)
        {
            if (s == null) s = "";
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
}

// ============================================================================
//  SnapPanel：拖动窗口时铺在窗口上的「定格画面」面板
//  独立类（不属于 QueryForm），只服务拖动流程，所以跟着主窗体放。
// ============================================================================

// 拖动中的“定格画面”面板：把快照按当前尺寸拉伸铺满。
// 这里刻意不用 BackgroundImage——WinForms 那套走的是高质量插值，
// 一张四五十万像素的图每帧要画几十毫秒；自己画一张、双线性缩放，一两毫秒就够
class SnapPanel : Panel
{
    internal Bitmap Img;
    IntPtr hBmp;
    IntPtr hMemDc;

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
        IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, int rop);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    static extern bool DeleteDC(IntPtr hdc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr hObj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    static extern int SetStretchBltMode(IntPtr hdc, int mode);

    internal SnapPanel(Bitmap img)
    {
        Img = img;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Img == null) { base.OnPaint(e); return; }
        try
        {
            if (hBmp == IntPtr.Zero) hBmp = Img.GetHbitmap();
            if (hMemDc == IntPtr.Zero) hMemDc = CreateCompatibleDC(IntPtr.Zero);
            IntPtr hdc = e.Graphics.GetHdc();
            IntPtr old = SelectObject(hMemDc, hBmp);
            // SetStretchBltMode = COLORONCOLOR：拉伸时不做插值，够快
            // 最后的 rop 必须是 SRCCOPY(0x00CC0020) 才是“照原样复制”，
            // 之前误写成 0x00330008（NOTSRCCOPY，反色），整块画面直接黑白颠倒
            SetStretchBltMode(hdc, 3);
            StretchBlt(hdc, 0, 0, ClientSize.Width, ClientSize.Height,
                       hMemDc, 0, 0, Img.Width, Img.Height, 0x00CC0020);
            SelectObject(hMemDc, old);
            e.Graphics.ReleaseHdc(hdc);
        }
        catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (hBmp != IntPtr.Zero) { try { DeleteObject(hBmp); } catch { } hBmp = IntPtr.Zero; }
        if (hMemDc != IntPtr.Zero) { try { DeleteDC(hMemDc); } catch { } hMemDc = IntPtr.Zero; }
        base.Dispose(disposing);
    }
}
