// ============================================================================
//  Multilingual.cs —— v2.3 第 2 步：其他语言浮层（独立可拖动小窗）
//
//  产品定位：
//    主界面一个像素都不改，只在底栏提示条右侧挂一个轻量文字入口「其他语言」。
//    点开以后弹出一块独立无边框小窗（归属主窗口、不占任务栏、跟着主窗最小化与关闭），
//    拖标题行能挪到屏幕任意位置，重开回到上次的位置，拖丢了有一键回中。窗里分三层：
//      · 顶部固定说明行（标题 + 关闭），下面是语言行（源 / 目标两个可搜索下拉）
//      · 中间是原文框、译文框
//      · 底部是「翻译」「复制」两个按钮和一行状态文字
//
//  这一版刻意不做的（按方案 2.3 的边界）：
//    语音、朗读、谐音、语气、截图、离线 —— 浮层里一个入口都不出现。
//    历史记录 / 生词本也不在浮层里落库：浮层只负责"翻出来、能复制走"。
//
//  两条硬要求在这里落地：
//    ① 同语言短路：源语言和目标语言一样（或自动识别出来是同一门语言）时不发请求，
//       直接在译文框里给一句人话提示，省一次调用、也避免模型把句子润色得面目全非。
//    ② 缓存键带语言对：同一句话翻成中文和翻成日语是两份结果，
//       键写成  ml|源|目标|原文 ，跟主界面原有的纯原文键完全隔离，互不覆盖。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

partial class QueryForm
{
    // ======================== 语言表 ========================

    internal class MlLang { public string Code; public string Name; public MlLang(string c, string n) { Code = c; Name = n; } }

    static MlLang[] mlLangTable = null;

    // 这一版只放常用的十几门，够覆盖日常跨境沟通；表本身是数据驱动的，
    // 以后加语言只往这里补一行，下拉、提示、缓存键全都跟着走，不用改逻辑。
    internal static MlLang[] MlLangs()
    {
        if (mlLangTable != null) return mlLangTable;
        mlLangTable = new MlLang[] {
            new MlLang("auto", "自动识别"),
            new MlLang("zh", "中文"),
            new MlLang("en", "英语"),
            new MlLang("ja", "日语"),
            new MlLang("ko", "韩语"),
            new MlLang("fr", "法语"),
            new MlLang("de", "德语"),
            new MlLang("es", "西班牙语"),
            new MlLang("ru", "俄语"),
            new MlLang("pt", "葡萄牙语"),
            new MlLang("it", "意大利语"),
            new MlLang("ar", "阿拉伯语"),
            new MlLang("th", "泰语"),
            new MlLang("vi", "越南语"),
            new MlLang("id", "印尼语"),
            new MlLang("hi", "印地语"),
            new MlLang("tr", "土耳其语"),
            new MlLang("nl", "荷兰语"),
            new MlLang("pl", "波兰语"),
            new MlLang("uk", "乌克兰语")
        };
        return mlLangTable;
    }

    internal static string MlName(string code)
    {
        if (code == null) return null;
        foreach (MlLang l in MlLangs())
            if (l.Code == code) return l.Name;
        return null;
    }

    // 下拉是「可搜索」的：用户可能选中，也可能手打了半个名字。
    // 认名字 → 认代码 → 认前缀，三层都认不出来就回落到默认值，绝不让空值往下走。
    internal static string MlResolve(string nameOrCode, string fallback)
    {
        string t = (nameOrCode == null ? "" : nameOrCode.Trim());
        if (t.Length == 0) return fallback;
        foreach (MlLang l in MlLangs())
            if (l.Name == t || l.Code == t) return l.Code;
        foreach (MlLang l in MlLangs())
            if (l.Name.StartsWith(t, StringComparison.OrdinalIgnoreCase)) return l.Code;
        foreach (MlLang l in MlLangs())
            if (l.Name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) return l.Code;
        return fallback;
    }

    internal static string MlPairLabel(string src, string dst)
    {
        string s = MlName(src);
        if (s == null) s = src;
        string d = MlName(dst);
        if (d == null) d = dst;
        return s + " → " + d;
    }

    // ======================== 同语言短路 ========================

    static bool MlMostlyChinese(string s, out int han, out int latin)
    {
        han = 0; latin = 0;
        foreach (char ch in s)
        {
            if (ch >= '\u4E00' && ch <= '\u9FFF') han++;
            else if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z')) latin++;
        }
        return han > 0;
    }

    // 源语言写了 auto 的时候，只能靠"这句话长什么样"猜。
    // 只做最保守的判断：汉字占绝对多数 → 中文；英文字母占多数且几乎没有汉字 → 英语。
    // 猜不出来就返回 null，表示"老实交给模型去判断"，不硬猜。
    internal static string MlGuessLang(string text)
    {
        int han, latin;
        MlMostlyChinese(text, out han, out latin);
        int total = han + latin;
        if (total == 0) return null;
        if (han >= 4 && (double)han / total >= 0.7) return "zh";
        if (latin >= 8 && han == 0) return "en";
        return null;
    }

    // 返回 null 表示"该翻就去翻"；返回非 null 就是直接摆到译文框里的提示，一个请求都不发。
    internal static string MlShortCircuit(string text, string src, string dst)
    {
        if (text == null || text.Trim().Length == 0) return null;
        string effSrc = src;
        if (effSrc == "auto") effSrc = MlGuessLang(text);
        if (effSrc == null) return null;
        if (effSrc != dst) return null;
        string nm = MlName(dst);
        if (nm == null) nm = dst;
        // 说明白"为什么没翻"，别让用户以为坏了
        return "这已经是" + nm + "了，没再让模型硬翻一遍。\n"
             + "要么把目标语言换一个，要么换一段别的语言的原文。";
    }

    // ======================== 缓存键：带语言对隔离 ========================
    // 主界面原来的缓存键就是原文本身（中英互译只有一种译法）；
    // 浮层多了「翻成哪门语言」，同一句原文会有多份结果，键必须带上语言对，
    // 否则先翻了日语，再翻中文就会直接吃到日语的缓存。
    internal static string MlCacheKey(string src, string dst, string text)
    {
        return "ml|" + src + "|" + dst + "|" + text;
    }

    // ======================== 浮层控件 ========================

    // v2.3 第 2 步：mlPanel 已从"主窗口里的面板"换成"独立无边框小窗"（MlWindow : Form）
    MlWindow mlPanel;
    ComboBox mlSrcBox, mlDstBox;
    MlInputBox mlInBox, mlOutBox;
    RoundButton mlGoBtn, mlCopyBtn, mlCloseBtn;
    Label mlTitle, mlCapSrc, mlCapDst, mlArrowLbl, mlTipLbl;
    LinkLabel mlLink, mlHomeLink;

    Font _fMlLink, _fMlTitle, _fMlText, _fMlSmall, _fMlClose, _fMlHome;

    int mlHairY;
    int mlSeqCounter;
    bool mlBusy;
    bool mlLoadingPair;
    bool mlOwnerHooked;   // 主窗的"最小化/隐藏/关闭"事件只挂一次
    bool mlWantOpen;      // 用户希望小窗开着：主窗临时最小化时先收起来，回来后照原意复原

    // 小窗能自己拉大小之后：量过的按钮宽度别再每帧重量，拉一次记一次
    bool _mlMeasured;
    int _mlHomeW, _mlGoW, _mlCopyW, _mlTxtW, _mlDocxW, _mlChkW;
    Size _mlRestoreSize;  // 双击标题行铺满工作区之前的那一档大小
    int _mlMaxTick;

    int MlNextSeq() { return Interlocked.Increment(ref mlSeqCounter); }
    bool MlIsCurrent(int seq) { return seq != 0 && seq == mlSeqCounter; }

    internal bool MlVisible { get { return mlPanel != null && mlPanel.Visible; } }

    // ---------- 底栏那个轻量文字入口 ----------
    // 只在提示条右侧占一小块：按钮条 bar 一个都不碰，5 个按钮的位置/大小/样式保持原样。
    void BuildMlLink(Panel tipHost)
    {
        if (mlLink != null || tipHost == null) return;
        mlLink = new LinkLabel();
        mlLink.Name = "mlLink";
        // v2.3 第 2 步：对外统一叫「其他语言」。旧称已从全部界面文案里清掉，--mlcheck 会扫窗口文字断言它不存在
        mlLink.Text = "其他语言";
        mlLink.AutoSize = false;
        mlLink.TextAlign = ContentAlignment.MiddleRight;
        mlLink.BackColor = Color.Transparent;
        mlLink.Font = new Font("Microsoft YaHei UI", 8.5f);
        mlLink.LinkColor = Pal.Accent;
        mlLink.ActiveLinkColor = Pal.AccentHover;
        mlLink.VisitedLinkColor = Pal.Accent;
        mlLink.LinkBehavior = LinkBehavior.HoverUnderline;
        mlLink.Cursor = Cursors.Hand;
        mlLink.Click += delegate { ToggleMultilingual(); };
        tipHost.Controls.Add(mlLink);
        mlLink.BringToFront();
    }

    // 由 ApplyMetrics / LayoutFootButtons 每次重排时调用：字号跟着缩放，位置贴右侧
    void MlPlaceLink(Panel tipHost)
    {
        if (mlLink == null || tipHost == null) return;
        float textScale = Math.Min(uiScale, 1.35f);
        mlLink.Font = Cached(ref _fMlLink, 8.5f * textScale, FontStyle.Regular);
        int lw = TextRenderer.MeasureText(mlLink.Text, mlLink.Font).Width + S(4);
        int padR = lw + S(10);
        Padding p = tipHost.Padding;
        if (p.Right != padR) tipHost.Padding = new Padding(p.Left, p.Top, padR, p.Bottom);
        mlLink.SetBounds(Math.Max(0, tipHost.ClientSize.Width - padR + S(4)), 0, lw, Math.Max(S(14), tipHost.ClientSize.Height));
    }

    void ToggleMultilingual()
    {
        if (MlVisible) HideMultilingual();
        else ShowMultilingual();
    }

    internal void HideMultilingual()
    {
        if (mlPanel == null) return;
        mlWantOpen = false;
        MlSavePos();                 // 关之前把当前位置记下来，下次打开回到老地方
        mlPanel.Hide();
        try { box.Focus(); } catch { }
    }

    internal void ShowMultilingual()
    {
        BuildMlPanel();
        MlLoadPair();
        MlHookOwner();
        mlWantOpen = true;
        // 从属窗口：挂在主窗名下，跟着主窗一起最小化/关闭，自己不占任务栏
        try { if (mlPanel.Owner == null && Visible) mlPanel.Owner = this; } catch { }
        MlPlaceOnOpen();             // 有记忆回记忆位置（钳制过），没记忆落在主窗中心
        if (!mlPanel.Visible) mlPanel.Show();
        mlPanel.BringToFront();
        try { mlPanel.Activate(); } catch { }
        try
        {
            mlInBox.Box.Focus();
            mlInBox.Box.SelectionStart = mlInBox.Box.TextLength;
        }
        catch { }

        // v2.3 第 2 步：第一次打开时说清楚"这里只做纯文本翻译"，记进配置，之后不再提
        MlFirstTipIfNeeded();
    }

    // ---------- 小窗本体 ----------
    void BuildMlPanel()
    {
        if (mlPanel != null) return;

        // v2.3 第 2 步：独立无边框小窗。以前是主窗口里的一个 Panel，居中钉死在客户区里，
        // 想挪走都挪不了；现在是一块真正的小窗，能拖、能记位置，也仍然只跟着主窗活。
        mlPanel = new MlWindow();
        mlPanel.Name = "mlPanel";
        mlPanel.BackColor = Pal.Surface;
        mlPanel.TitleHeight = S(42);                   // 标题行整块都能拖，不用对准小把手
        // v2.4：四边四角都能拉。以前窗口尺寸是写死的 560×430，翻日韩法德那些长句子
        // 只能在 430 高里面滚，看两行歇一下；现在拉到多大就多大，下次打开还是那么大。
        mlPanel.GripPx = Math.Max(4, S(6));
        mlPanel.MinimumSize = new Size(S(380), S(330));  // 地板：再小按钮就要叠起来了
        mlPanel.Paint += delegate (object s, PaintEventArgs e) { MlPanelPaint(e.Graphics); };
        mlPanel.Resize += delegate {
            if (mlPanel.ClientSize.Width > 8 && mlPanel.ClientSize.Height > 8)
            {
                RoundControl(mlPanel, S(12));
                // 拖边时每帧都会进来：只走轻量排版，别在这里量文字、改字体
                if (mlPanel.Sizing) { MlLayoutLite(); return; }
                MlLayout();
            }
        };
        mlPanel.EscPressed = delegate { HideMultilingual(); };      // 小窗里有焦点时 Esc 也能收
        // 松手才写盘，拖动过程一次都不写；顺手补一次完整排版，把拖动中省下的字体与测量一次对齐
        // 注意：补排版走 BeginInvoke 排队，不在 WM_EXITSIZEMOVE 的栈里同步排——
        // 排版会改一堆控件尺寸，同步排容易跟 Resize 打回声，一层层套到底把进程套死。
        mlPanel.DragEnded = delegate {
            MlSavePos();
            try { mlPanel.BeginInvoke((MethodInvoker)delegate { MlLayout(); }); }
            catch { MlLayout(); }
        };
        mlPanel.TitleDoubleClicked = delegate { MlToggleMax(); };   // 双击标题行 = 铺满 / 还原
        // 注意：这里不再 Controls.Add(mlPanel)——它是顶层窗口，不是主窗的子控件

        mlTitle = new Label();
        mlTitle.AutoSize = false;
        mlTitle.Text = "其他语言";
        mlTitle.TextAlign = ContentAlignment.MiddleLeft;
        mlTitle.ForeColor = Pal.Ink;
        mlTitle.BackColor = Color.Transparent;
        mlTitle.Cursor = Cursors.SizeAll;
        // 标题文字上按下也能拖：Label 是子窗口，系统不会把它当标题栏，得自己转一次
        mlTitle.MouseDown += delegate (object s, MouseEventArgs e) { MlDragByCaption(e); };
        mlTitle.DoubleClick += delegate { MlToggleMax(); };   // 标题文字上双击也认（MlToggleMax 里自带防抖）
        mlPanel.Controls.Add(mlTitle);

        mlHomeLink = new LinkLabel();
        mlHomeLink.Name = "mlHomeLink";
        mlHomeLink.Text = "回到主窗中心";
        mlHomeLink.AutoSize = false;
        mlHomeLink.TextAlign = ContentAlignment.MiddleRight;
        mlHomeLink.BackColor = Color.Transparent;
        mlHomeLink.Font = new Font("Microsoft YaHei UI", 8.5f);
        mlHomeLink.LinkColor = Pal.Accent;
        mlHomeLink.ActiveLinkColor = Pal.AccentHover;
        mlHomeLink.VisitedLinkColor = Pal.Accent;
        mlHomeLink.LinkBehavior = LinkBehavior.HoverUnderline;
        mlHomeLink.Cursor = Cursors.Hand;
        mlHomeLink.Click += delegate { MlGoHome(); };
        mlPanel.Controls.Add(mlHomeLink);

        mlCloseBtn = new RoundButton();
        mlCloseBtn.Text = "×";
        mlCloseBtn.Radius = S(6);
        mlCloseBtn.FillColor = Pal.Surface;
        mlCloseBtn.HoverColor = Pal.AccentTint;
        mlCloseBtn.ForeColor = Pal.InkMuted;
        mlCloseBtn.Cursor = Cursors.Hand;
        mlCloseBtn.Click += delegate { HideMultilingual(); };
        mlPanel.Controls.Add(mlCloseBtn);

        mlCapSrc = MkMlCaption("原文语言");
        mlCapDst = MkMlCaption("译成");
        mlPanel.Controls.Add(mlCapSrc);
        mlPanel.Controls.Add(mlCapDst);

        mlArrowLbl = new Label();
        mlArrowLbl.AutoSize = false;
        mlArrowLbl.Text = "→";
        mlArrowLbl.TextAlign = ContentAlignment.MiddleCenter;
        mlArrowLbl.ForeColor = Pal.InkMuted;
        mlArrowLbl.BackColor = Color.Transparent;
        mlPanel.Controls.Add(mlArrowLbl);

        mlSrcBox = MkMlCombo(true);
        mlDstBox = MkMlCombo(false);
        mlPanel.Controls.Add(mlSrcBox);
        mlPanel.Controls.Add(mlDstBox);

        mlInBox = new MlInputBox();
        mlInBox.Placeholder = "把要翻的文字粘到这里（Ctrl+Enter 直接翻）";
        // 原文区固定左到右：阿语原文粘进来也照 LTR 摆，用户改字时光标不来回跳
        mlInBox.Box.RightToLeft = RightToLeft.No;
        mlInBox.Box.KeyDown += delegate (object s, KeyEventArgs e) {
            if (e.Control && (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Return))
            {
                e.SuppressKeyPress = true;
                MlTranslateStart();
            }
        };
        mlPanel.Controls.Add(mlInBox);

        mlOutBox = new MlInputBox();
        mlOutBox.Placeholder = "译文会出现在这里";
        mlOutBox.Box.ReadOnly = true;
        mlPanel.Controls.Add(mlOutBox);

        mlGoBtn = MakeDialogButton("翻译", S(72), true);
        mlGoBtn.Click += delegate { MlTranslateStart(); };
        mlPanel.Controls.Add(mlGoBtn);

        mlCopyBtn = MakeDialogButton("复制", S(60), false);
        mlCopyBtn.Click += delegate { MlCopyResult(); };
        mlPanel.Controls.Add(mlCopyBtn);

        // ---- v2.3 第 2 步新增：两个导出按钮 + 「带原文」勾选（都只在小窗里，主界面不碰）----
        mlTxtBtn = MakeDialogButton("导出TXT", S(84), false);
        mlTxtBtn.Click += delegate { MlExport(false); };
        mlPanel.Controls.Add(mlTxtBtn);

        mlDocxBtn = MakeDialogButton("导出docx", S(92), false);
        mlDocxBtn.Click += delegate { MlExport(true); };
        mlPanel.Controls.Add(mlDocxBtn);

        mlWithSrcChk = new CheckBox();
        mlWithSrcChk.AutoSize = false;                 // 尺寸在 MlLayout 里按字号算，跟按钮同一行
        mlWithSrcChk.Text = "带原文";
        mlWithSrcChk.FlatStyle = FlatStyle.Flat;
        mlWithSrcChk.ForeColor = Pal.InkHint;
        mlWithSrcChk.BackColor = Color.Transparent;
        mlWithSrcChk.Cursor = Cursors.Hand;
        mlWithSrcChk.TextAlign = ContentAlignment.MiddleLeft;
        mlWithSrcChk.Checked = false;                  // 默认只导译文
        mlPanel.Controls.Add(mlWithSrcChk);

        // 译文方向跟着内容走：阿拉伯语/希伯来语一进来就摆正，不用用户手动切
        mlOutBox.Box.TextChanged += delegate { MlAutoRtl(); };

        mlTipLbl = new Label();
        mlTipLbl.AutoSize = false;
        mlTipLbl.AutoEllipsis = true;
        mlTipLbl.TextAlign = ContentAlignment.MiddleLeft;
        mlTipLbl.ForeColor = Pal.InkHint;
        mlTipLbl.BackColor = Color.Transparent;
        mlTipLbl.Text = "选好语言，粘一段话进来就能翻";
        mlPanel.Controls.Add(mlTipLbl);

        // 小窗自己也是拖放落点：文件拖到窗里任何位置都算（见 MlStep2.cs ⑤.2）
        MlInstallMlDrop();
    }

    Label MkMlCaption(string text)
    {
        Label l = new Label();
        l.AutoSize = false;
        l.Text = text;
        l.TextAlign = ContentAlignment.MiddleLeft;
        l.ForeColor = Pal.InkHint;
        l.BackColor = Color.Transparent;
        return l;
    }

    ComboBox MkMlCombo(bool allowAuto)
    {
        ComboBox c = new ComboBox();
        c.DropDownStyle = ComboBoxStyle.DropDown;      // 可下拉选、也可直接打字搜
        c.FlatStyle = FlatStyle.Flat;
        c.BackColor = Pal.InputBg;
        c.ForeColor = Pal.Ink;
        c.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        c.AutoCompleteSource = AutoCompleteSource.ListItems;
        c.MaxDropDownItems = 10;
        foreach (MlLang l in MlLangs())
        {
            if (!allowAuto && l.Code == "auto") continue;   // 目标语言不能是"自动识别"
            c.Items.Add(l.Name);
        }
        c.SelectedIndexChanged += delegate { MlSavePair(); };
        // 拖动 / 缩放小窗时窗口会被重新激活，WinForms 把焦点还给可编辑下拉框时默认「全选」里面的文字，
        // 于是「自动识别」「中文」两格整段变蓝，看着像被选中了。这里拿到焦点就把选择取消、光标停到末尾，
        // 失焦时也清一次——否则那个全选状态会一直留着，下次一激活又蓝。
        c.GotFocus += delegate
        {
            try { c.SelectionLength = 0; c.SelectionStart = c.Text.Length; } catch { }
        };
        c.LostFocus += delegate { try { c.SelectionLength = 0; } catch { } };
        c.MouseDown += delegate { try { c.SelectionLength = 0; } catch { } };
        return c;
    }

    void MlPanelPaint(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int w = mlPanel.ClientSize.Width, h = mlPanel.ClientSize.Height;
        if (w < 8 || h < 8) return;
        // 顶部说明行下面那条细线：说明行是固定的，滚不到、挤不动
        using (Pen hp = new Pen(Pal.Hair))
            g.DrawLine(hp, S(14), mlHairY, w - S(14), mlHairY);
        using (GraphicsPath gp = RoundPath(new Rectangle(0, 0, w - 1, h - 1), S(12)))
        using (Pen p = new Pen(Pal.Edge))
            g.DrawPath(p, gp);
    }

    // ---------- 摆位（独立小窗：屏幕坐标 + 位置记忆 + 边界钳制） ----------

    // 小窗默认尺寸：没存过尺寸时按这个开（不再跟着主窗缩），屏幕实在装不下才退一点
    Size MlIdealSize()
    {
        Rectangle wa = Screen.FromPoint(MlAnchorPoint()).WorkingArea;
        int w = Math.Min(S(560), Math.Max(S(300), wa.Width - S(48)));
        int h = Math.Min(S(470), Math.Max(S(240), wa.Height - S(72)));
        return new Size(w, h);
    }

    // 小窗的最小尺寸：再小底栏那两行就叠成一团了
    Size MlMinSize() { return new Size(S(380), S(330)); }

    // 用户自己拉过的大小就是他要的大小：下次照这个开。
    // 没存过 → 用默认尺寸；存过但屏幕变小了 → 钳进工作区，绝不把窗口开到屏幕外面去。
    Size MlSavedSize()
    {
        int sw = GetInt("ml_w", int.MinValue);
        int sh = GetInt("ml_h", int.MinValue);
        if (sw == int.MinValue || sh == int.MinValue || sw < 80 || sh < 80) return MlIdealSize();
        Rectangle wa = Screen.FromPoint(MlAnchorPoint()).WorkingArea;
        Size min = MlMinSize();
        int w = Math.Max(min.Width, Math.Min(sw, wa.Width));
        int h = Math.Max(min.Height, Math.Min(sh, wa.Height));
        return new Size(w, h);
    }

    // 主窗中心点：判断落在哪块屏、以及"回中"的参照物
    Point MlAnchorPoint()
    {
        Rectangle b = Bounds;
        return new Point(b.Left + b.Width / 2, b.Top + b.Height / 2);
    }

    Point MlCenterOverMain(Size sz)
    {
        Rectangle b = Bounds;
        return new Point(b.Left + (b.Width - sz.Width) / 2, b.Top + (b.Height - sz.Height) / 2);
    }

    // 屏幕边界钳制：整块窗口必须落在某块屏幕的工作区内。
    // 换显示器、改分辨率、或者手滑拖到屏幕外，重开时都不会出现"窗口在人看不见的地方"。
    internal static Point MlClampToScreen(Point p, Size sz)
    {
        Rectangle wa = Screen.FromPoint(new Point(p.X + sz.Width / 2, p.Y + sz.Height / 2)).WorkingArea;
        int x, y;
        // 屏幕比窗口还小的极端情况：贴左上角，保证标题行（拖拽点）永远够得着
        if (wa.Width >= sz.Width) x = Math.Min(Math.Max(p.X, wa.Left), wa.Right - sz.Width);
        else x = wa.Left;
        if (wa.Height >= sz.Height) y = Math.Min(Math.Max(p.Y, wa.Top), wa.Bottom - sz.Height);
        else y = wa.Top;
        return new Point(x, y);
    }

    // 打开时：存过位置就回上次那儿（钳制后），没存过就落在主窗中心
    void MlPlaceOnOpen()
    {
        if (mlPanel == null) return;
        Size sz = MlSavedSize();
        int sx = GetInt("ml_x", int.MinValue);
        int sy = GetInt("ml_y", int.MinValue);
        Point p = (sx == int.MinValue || sy == int.MinValue) ? MlCenterOverMain(sz) : new Point(sx, sy);
        p = MlClampToScreen(p, sz);
        mlPanel.SetBounds(p.X, p.Y, sz.Width, sz.Height);
    }

    // 主窗尺寸/位置变了：小窗不跟着挪（用户自己摆的位置要留住），只做一次"别跑出屏幕"的兜底
    void MlKeepOnScreen()
    {
        if (mlPanel == null || !mlPanel.Visible) return;
        Point p = MlClampToScreen(mlPanel.Location, mlPanel.Size);
        if (p != mlPanel.Location) { mlPanel.Location = p; MlSavePos(); }
    }

    // 位置 + 尺寸落盘：只在打开/收起/拖完手这几处写，拖动过程中一个像素都不写
    void MlSavePos()
    {
        if (mlPanel == null) return;
        SetSetQuiet("ml_x", mlPanel.Left.ToString());
        SetSetQuiet("ml_y", mlPanel.Top.ToString());
        SetSetQuiet("ml_w", mlPanel.Width.ToString());
        SetSetQuiet("ml_h", mlPanel.Height.ToString());
        FlushSettings();
    }

    // 双击标题行：铺满当前屏幕工作区 ↔ 回到上一档大小。
    // 系统对无边框窗不会自己做「最大化」，这活儿只能我们手动摆。
    void MlToggleMax()
    {
        if (mlPanel == null) return;
        int now = Environment.TickCount;
        if (now - _mlMaxTick < 250) return;     // NC 双击和 Label 双击可能各报一次，别来回翻
        _mlMaxTick = now;

        Rectangle wa = Screen.FromRectangle(mlPanel.Bounds).WorkingArea;
        Size cur = mlPanel.Size;
        bool isMax = Math.Abs(cur.Width - wa.Width) <= S(4) && Math.Abs(cur.Height - wa.Height) <= S(4);
        if (isMax)
        {
            Size back = _mlRestoreSize;
            if (back.Width < 80 || back.Height < 80) back = MlMinSize();
            Size min = MlMinSize();
            back = new Size(Math.Max(min.Width, Math.Min(back.Width, wa.Width)),
                            Math.Max(min.Height, Math.Min(back.Height, wa.Height)));
            mlPanel.Size = back;
            mlPanel.Location = MlClampToScreen(new Point(mlPanel.Left, mlPanel.Top), back);
        }
        else
        {
            _mlRestoreSize = cur;
            mlPanel.Bounds = wa;
        }
        MlLayout();
        MlSavePos();
    }

    // 一键回到主窗中心：拖到天边找不回来了，点标题行右边的「回到主窗中心」
    void MlGoHome()
    {
        if (mlPanel == null) return;
        Size sz = mlPanel.Size;
        mlPanel.Location = MlClampToScreen(MlCenterOverMain(sz), sz);
        MlSavePos();
    }

    // 标题行拖动：借系统的 HTCAPTION 拖动，手感跟拖真标题栏一样（松手后 WM_EXITSIZEMOVE 记位置）
    void MlDragByCaption(MouseEventArgs e)
    {
        if (e == null || e.Button != MouseButtons.Left || mlPanel == null) return;
        try
        {
            ReleaseCapture();
            SendMessage(mlPanel.Handle, 0x00A1, 2, 0);   // WM_NCLBUTTONDOWN / HTCAPTION
        }
        catch { }
    }

    // 主窗的最小化 / 隐藏 / 关闭都带着小窗一起走：从属窗口就该这样
    void MlHookOwner()
    {
        if (mlOwnerHooked) return;
        mlOwnerHooked = true;
        Resize += delegate { MlSyncOwnerState(); };
        VisibleChanged += delegate { MlSyncOwnerState(); };
        FormClosing += delegate { MlCloseForOwner(); };
    }

    void MlSyncOwnerState()
    {
        if (mlPanel == null) return;
        bool ownerGone = (WindowState == FormWindowState.Minimized) || !Visible;
        if (ownerGone)
        {
            // 只是临时藏起来：不清 mlWantOpen，等主窗回来还照原意显示
            if (mlPanel.Visible) mlPanel.Hide();
        }
        else if (mlWantOpen && !mlPanel.Visible)
        {
            try { if (mlPanel.Owner == null) mlPanel.Owner = this; } catch { }
            mlPanel.Show();
            mlPanel.BringToFront();
        }
    }

    void MlCloseForOwner()
    {
        if (mlPanel == null) return;
        MlSavePos();
        try { mlPanel.Hide(); } catch { }
        try { mlPanel.Close(); } catch { }   // 主窗关了，小窗绝不留在屏幕上
        mlPanel = null;
    }

    void MlLayout() { MlLayoutEnter(true); }

    // 拖边缩放期间走这一条：只挪位置、不改字体、一个 MeasureText 都不跑。
    // 不然每帧都量一遍按钮文字，内容会慢半拍、一格格跳出来。
    void MlLayoutLite() { MlLayoutEnter(false); }

    // 排版里改控件尺寸 → 会再抛 Resize → 又进排版。
    // 加个重入闸：里层只记「还得再排一次」，等外层排完最多补一遍，绝不嵌套着往下钻。
    bool _mlLaying, _mlLayAgain;
    void MlLayoutEnter(bool full)
    {
        if (_mlLaying) { _mlLayAgain = true; return; }
        _mlLaying = true;
        try { MlLayoutCore(full); }
        finally { _mlLaying = false; }
        if (_mlLayAgain)
        {
            _mlLayAgain = false;
            try { MlLayoutCore(true); } catch { }
        }
    }

    void MlLayoutCore(bool full)
    {
        if (mlPanel == null || mlTitle == null) return;
        float textScale = Math.Min(uiScale, 1.35f);

        Font fTxt, fSmall;
        int homeW = 0, goW, copyW, txtW, docxW, chkW;
        if (full || !_mlMeasured)
        {
            mlTitle.Font = Cached(ref _fMlTitle, 12f * textScale, FontStyle.Bold);
            fTxt = Cached(ref _fMlText, 9.5f * textScale, FontStyle.Regular);
            fSmall = Cached(ref _fMlSmall, 8.5f * textScale, FontStyle.Regular);
            mlCloseBtn.Font = Cached(ref _fMlClose, 11f * textScale, FontStyle.Regular);
            if (mlHomeLink != null)
            {
                mlHomeLink.Font = Cached(ref _fMlHome, 8.5f * textScale, FontStyle.Regular);
                homeW = TextRenderer.MeasureText(mlHomeLink.Text, mlHomeLink.Font).Width + S(6);
            }
            mlCapSrc.Font = fSmall; mlCapDst.Font = fSmall; mlArrowLbl.Font = fSmall;
            mlSrcBox.Font = fTxt; mlDstBox.Font = fTxt;
            mlWithSrcChk.Font = fSmall;
            mlTipLbl.Font = fSmall;
            mlGoBtn.Font = fTxt; mlCopyBtn.Font = fTxt; mlTxtBtn.Font = fTxt; mlDocxBtn.Font = fTxt;
            mlInBox.Box.Font = fTxt;
            mlOutBox.Box.Font = fTxt;
            goW = Math.Max(S(64), TextRenderer.MeasureText("翻译", fTxt).Width + S(28));
            copyW = Math.Max(S(56), TextRenderer.MeasureText("复制", fTxt).Width + S(24));
            txtW = Math.Max(S(76), TextRenderer.MeasureText("导出TXT", fTxt).Width + S(24));
            docxW = Math.Max(S(84), TextRenderer.MeasureText("导出docx", fTxt).Width + S(24));
            chkW = Math.Max(S(62), TextRenderer.MeasureText("带原文", fSmall).Width + S(22));
            _mlHomeW = homeW; _mlGoW = goW; _mlCopyW = copyW; _mlTxtW = txtW; _mlDocxW = docxW; _mlChkW = chkW;
            _mlMeasured = true;
        }
        else
        {
            // 字体和按钮宽度上一帧刚量过，缩放中直接拿来用
            fTxt = mlInBox.Box.Font;
            fSmall = mlTipLbl.Font;
            homeW = _mlHomeW; goW = _mlGoW; copyW = _mlCopyW; txtW = _mlTxtW; docxW = _mlDocxW; chkW = _mlChkW;
        }

        int w = mlPanel.ClientSize.Width, h = mlPanel.ClientSize.Height;
        if (w < 40 || h < 40) return;
        int pad = S(16);
        int cw = Math.Max(S(20), fSmall.Height + S(8));

        mlHairY = S(42);
        mlCloseBtn.SetBounds(w - pad - cw + S(4), S(11), cw, cw);
        // 标题行右侧多一个「回到主窗中心」：拖远了、拖到副屏拔掉之后，靠它一键找回
        if (mlHomeLink != null)
        {
            int homeH = Math.Max(S(14), mlHomeLink.Font.Height);
            mlHomeLink.SetBounds(Math.Max(pad + S(50), w - pad - cw + S(4) - S(8) - homeW), S(11) + (cw - homeH) / 2, homeW, homeH);
        }
        int titleW = (homeW > 0) ? Math.Max(S(50), mlHomeLink.Left - S(8) - pad)
                                 : Math.Max(S(80), w - pad * 2 - cw - S(6));
        mlTitle.SetBounds(pad, S(10), titleW, S(24));

        // 语言行
        int capY = mlHairY + S(9);
        int capH = Math.Max(S(13), fSmall.Height);
        int colGap = S(30);
        int colW = (w - pad * 2 - colGap) / 2;
        mlCapSrc.SetBounds(pad + S(2), capY, colW, capH);
        mlCapDst.SetBounds(pad + colW + colGap + S(2), capY, w - pad - (pad + colW + colGap), capH);
        int comboY = capY + capH + S(2);
        int comboH = Math.Max(S(24), fTxt.Height + S(10));
        mlSrcBox.SetBounds(pad, comboY, colW, comboH);
        int dstX = pad + colW + colGap;
        mlDstBox.SetBounds(dstX, comboY, Math.Max(S(60), w - pad - dstX), comboH);
        mlArrowLbl.SetBounds(pad + colW, comboY, colGap, comboH);

        // 上下两块输入区：按剩余高度四六分，窗口变矮时先保输入框
        int contentTop = comboY + comboH + S(10);
        int btnH = Math.Max(S(28), fTxt.Height + S(12));
        int bottomY = h - pad - btnH;
        // 底栏现在是两行（状态行 + 按钮行），给输入区算高度时得把两行都让出来
        int avail = Math.Max(S(120), bottomY - btnH - S(8) - S(8) - contentTop);
        int inH = (int)Math.Round(avail * 0.42);
        int outH = avail - inH - S(8);
        if (outH < S(50)) { outH = S(50); inH = Math.Max(S(50), avail - outH - S(8)); }
        mlInBox.SetBounds(pad, contentTop, w - pad * 2, inH);
        mlOutBox.SetBounds(pad, contentTop + inH + S(8), w - pad * 2, outH);

        // 底部按钮排：v2.3 第 2 步起是两行
        //   第一行：状态文字 + 「带原文」勾选
        //   第二行：翻译 | 复制 | 导出TXT | 导出docx
        // 主界面底栏那 5 个按钮不在这里，一点都没动。
        int gap = S(8);

        int row2Y = bottomY;
        int row1Y = bottomY - gap - btnH;
        int row2H = btnH;
        if (row1Y < contentTop) row1Y = contentTop;     // 窗口被拖太矮时别压到输入框上

        mlWithSrcChk.SetBounds(w - pad - chkW, row1Y, chkW, btnH);
        mlTipLbl.SetBounds(pad, row1Y, Math.Max(S(60), w - pad * 2 - chkW - S(10)), btnH);

        // 右往左排：docx 最右，依次往左
        int x = w - pad;
        mlDocxBtn.SetBounds(x - docxW, row2Y, docxW, row2H); x -= docxW + gap;
        mlTxtBtn.SetBounds(x - txtW, row2Y, txtW, row2H); x -= txtW + gap;
        mlCopyBtn.SetBounds(x - copyW, row2Y, copyW, row2H); x -= copyW + gap;
        mlGoBtn.SetBounds(x - goW, row2Y, goW, row2H);

        // 窗口太窄的话，把"复制"塞不下时就让它跟着左边贴着，宁可挤也别叠
        if (mlCopyBtn.Left < pad)
        {
            mlCopyBtn.SetBounds(pad, row2Y, copyW, row2H);
            mlGoBtn.SetBounds(pad, row1Y, goW, row2H);
        }

        int boxPad = S(7);
        mlInBox.PadPx = boxPad; mlOutBox.PadPx = boxPad;
    }

    // ---------- 语言对记忆 ----------
    void MlLoadPair()
    {
        mlLoadingPair = true;
        try
        {
            string sc = GetSet("ml_src", "auto");
            if (MlName(sc) == null) sc = "auto";
            string dc = GetSet("ml_dst", "zh");
            if (MlName(dc) == null || dc == "auto") dc = "zh";
            MlPick(mlSrcBox, sc);
            MlPick(mlDstBox, dc);
        }
        finally { mlLoadingPair = false; }
    }

    void MlPick(ComboBox c, string code)
    {
        string nm = MlName(code);
        if (c == null || nm == null) return;
        int i = c.Items.IndexOf(nm);
        if (i >= 0) c.SelectedIndex = i;
    }

    string MlSelCode(ComboBox c, string fallback)
    {
        string code = MlResolve(c.Text, fallback);
        if (c == mlDstBox && code == "auto") code = "zh";   // 目标语言永远不能是自动
        return code;
    }

    void MlSavePair()
    {
        if (mlLoadingPair || mlPanel == null || mlSrcBox == null) return;
        SetSet("ml_src", MlSelCode(mlSrcBox, "auto"));
        SetSet("ml_dst", MlSelCode(mlDstBox, "zh"));
    }

    // ---------- 翻译 ----------
    void MlTranslateStart()
    {
        if (mlPanel == null || mlBusy) return;

        string text = (mlInBox.Box.Text == null ? "" : mlInBox.Box.Text).Trim();
        string sc = MlSelCode(mlSrcBox, "auto");
        string dc = MlSelCode(mlDstBox, "zh");
        // 用户手打的名字规整回标准名，顺便把这次的选择记下来
        MlPick(mlSrcBox, sc);
        MlPick(mlDstBox, dc);
        SetSet("ml_src", sc);
        SetSet("ml_dst", dc);

        if (text.Length == 0)
        {
            MlSay("先把要翻的文字粘到上面那个框里");
            return;
        }

        // ①.5 v2.3 第 2 步：超长先处理
        //   · 超过 20 万字 / 2 MB：只翻前面一段，并且当面说清楚
        //   · 超过 5000 字：先说一共几段、大概多久，用户再点一次才开翻
        string work, gateNote;
        MlGate gate = MlBigGate(text, out work, out gateNote);
        if (gate == MlGate.Confirm)
        {
            MlSay(gateNote == null ? "内容比较长，再点一次「翻译」就开始" : gateNote);
            Log("小窗长文待确认：" + text.Length + " 字");
            return;
        }
        if (gateNote != null) MlSay(gateNote);       // 截断的说明先摆出来，翻完还会覆盖成进度
        text = work;

        // ① 同语言短路：不发请求，直接讲清楚
        string sc_info = MlShortCircuit(text, sc, dc);
        if (sc_info != null)
        {
            mlOutBox.Box.Text = sc_info;
            MlSay("没发请求：源语言和目标语言是同一门");
            Log("浮层短路：同语言 " + MlPairLabel(sc, dc) + " 字数=" + text.Length);
            return;
        }

        // ② 缓存：键里带语言对，别的语言的结果不会串过来
        string ck = MlCacheKey(sc, dc, text);
        string hit;
        if (CacheGet(ck, out hit) && hit != null && hit.Trim().Length > 0)
        {
            mlOutBox.Box.Text = hit.Trim();
            MlSay("这句翻过（" + MlPairLabel(sc, dc) + "），直接用上次的结果");
            Log("浮层缓存命中 " + MlPairLabel(sc, dc));
            return;
        }

        int seq = MlNextSeq();
        mlBusy = true;
        mlGoBtn.Enabled = false;
        mlOutBox.Box.Text = "";
        mlLastSrc = text;
        mlLastDst = "";

        // ②.5 v2.3 第 2 步：长文分段。一段能翻完的还走原来那条路，行为不变。
        int segCount = MlSplit(text, MlSegChars).Count;
        if (segCount > 1)
        {
            MlSay("正在分段翻（" + MlPairLabel(sc, dc) + "）｜已完成 0/" + segCount + " 段……");
            string callerMulti = text;
            Thread thm = new Thread(delegate () { MlDoTranslateMulti(callerMulti, sc, dc, seq); });
            thm.IsBackground = true;
            thm.Start();
            return;
        }

        MlSay("正在翻（" + MlPairLabel(sc, dc) + "）……");

        string caller = text;   // 闭包只捕不可变副本，省得后面被改
        Thread th = new Thread(delegate () { MlDoTranslate(caller, sc, dc, ck, seq); });
        th.IsBackground = true;
        th.Start();
    }

    void MlBusyDone()
    {
        mlBusy = false;
        if (mlGoBtn != null) mlGoBtn.Enabled = true;
    }

    void MlSay(string msg)
    {
        if (mlTipLbl != null) mlTipLbl.Text = msg;
    }

    void MlCopyResult()
    {
        string t = (mlOutBox.Box.Text == null ? "" : mlOutBox.Box.Text).Trim();
        if (t.Length == 0) { MlSay("还没有译文可复制"); return; }
        try
        {
            Clipboard.SetText(t);
            MlSay("译文已复制");
        }
        catch (Exception ex)
        {
            MlSay("复制失败：" + ex.Message);
        }
    }

    // ======================== 自检用的小门（只给 --mlcheck 用） ========================
    // 不参与正式逻辑，只是让自检能从外面看一眼浮层的真实状态，避免自检去猜。

    internal MlWindow MlPanelForTest { get { return mlPanel; } }
    internal Label MlTipForTest { get { return mlTipLbl; } }
    internal ComboBox MlSrcForTest { get { return mlSrcBox; } }
    internal ComboBox MlDstForTest { get { return mlDstBox; } }
    internal LinkLabel MlLinkForTest { get { return mlLink; } }
    internal LinkLabel MlHomeLinkForTest { get { return mlHomeLink; } }
    internal string MlOutTextForTest { get { return mlOutBox == null ? "" : mlOutBox.Box.Text; } }
    internal string MlInTextForTest { get { return mlInBox == null ? "" : mlInBox.Box.Text; } }

    internal void MlTestSetPair(string srcCode, string dstCode)
    {
        MlLoadingPairOn();
        try { MlPick(mlSrcBox, srcCode); MlPick(mlDstBox, dstCode); }
        finally { mlLoadingPair = false; }
    }

    internal void MlTestSetInput(string text)
    {
        if (mlInBox != null) mlInBox.Box.Text = text;
    }

    internal void MlTestTranslate() { MlTranslateStart(); }

    // ---- v2.3 第 2 步：独立小窗的几个动作，自检从外面拨一下 ----
    internal void MlTestMoveTo(int x, int y)      // 假装用户把小窗拖到了某处
    {
        if (mlPanel == null) return;
        mlPanel.Location = new Point(x, y);
        MlSavePos();
    }
    internal void MlTestGoHome() { MlGoHome(); }                    // 一键回主窗中心
    internal void MlTestReopen() { HideMultilingual(); ShowMultilingual(); }   // 关掉再打开
    internal void MlTestOwnerMinimize() { WindowState = FormWindowState.Minimized; Application.DoEvents(); }
    internal void MlTestOwnerRestore() { WindowState = FormWindowState.Normal; Application.DoEvents(); }
    internal void MlTestOwnerClose() { MlCloseForOwner(); }            // 主窗关闭时的收尾
    internal bool MlWantOpenForTest { get { return mlWantOpen; } }
    internal Point MlCenterForTest(Size sz) { return MlCenterOverMain(sz); }   // 主窗中心该在哪
    // 标题行上两个"不能当标题栏"的热点：关闭按钮 / 回到主窗中心
    internal Point MlClosePtForTest
    {
        get
        {
            if (mlPanel == null || mlCloseBtn == null) return new Point(1, 1);
            return new Point(mlCloseBtn.Left + mlCloseBtn.Width / 2, mlCloseBtn.Top + mlCloseBtn.Height / 2);
        }
    }
    internal Point MlHomePtForTest
    {
        get
        {
            if (mlPanel == null || mlHomeLink == null) return new Point(1, 1);
            return new Point(mlHomeLink.Left + mlHomeLink.Width / 2, mlHomeLink.Top + mlHomeLink.Height / 2);
        }
    }

    void MlLoadingPairOn() { mlLoadingPair = true; }

    // 把浮层当前的控件树写成一行行文本：自检拿它核对"该有的都有、不该有的一个都没有"
    internal string MlSelfDump()
    {
        StringBuilder sb = new StringBuilder();
        if (mlPanel == null) { sb.AppendLine("浮层=未创建"); return sb.ToString(); }
        sb.AppendLine("浮层 可见=" + mlPanel.Visible + "  边界=" + mlPanel.Bounds
                    + "  圆角区=" + (mlPanel.Region != null));
        foreach (Control c in mlPanel.Controls)
        {
            string label = c.GetType().Name;
            if (c is Label) label = "Label";
            else if (c is ComboBox) label = "ComboBox";
            else if (c is RoundButton) label = "RoundButton";
            else if (c is MlInputBox) label = "MlInputBox";
            string txt = c.Text;
            if (txt != null && txt.Length > 24) txt = txt.Substring(0, 24) + "…";
            sb.AppendLine("  " + label + "  text=" + txt + "  bounds=" + c.Bounds + "  可见=" + c.Visible + "  可点=" + c.Enabled);
        }
        return sb.ToString();
    }

    static string MlSystemPrompt()
    {
        return "你是多语种翻译器，服务的是一位中文母语用户。规则：\n"
             + "① 只输出译文本身。不要解释、不要加引号、不要复述原文、不要写「译文：」这类前缀。\n"
             + "② 保留原文的换行和条目编号，语气要保持一致（客气还是随意，别擅自改成书面语）。\n"
             + "③ 人名、品牌、产品型号按目标语言的习惯处理：能音译就音译，拿不准就保留原样。\n"
             + "④ 网络用语、缩写按目标语言日常聊天的说法翻，别翻成生硬的教科书腔。\n"
             + "⑤ 如果原文已经是目标语言，就原样输出，不要润色、不要改写。\n"
             + "⑥ 拿不准的专有名词宁可直译后加括号保留原文，也不要凭空编造。";
    }

    static string MlBuildPayload(string text, string sc, string dc)
    {
        return MlBuildPayload(text, sc, dc, 3000);
    }

    // v2.3 第 2 步：输出预算可变——分段翻时一段 1200 字，译文可能比原文长，3000 不够用
    static string MlBuildPayload(string text, string sc, string dc, int maxTokens)
    {
        Dictionary<string, object> sysMsg = new Dictionary<string, object>();
        sysMsg["role"] = "system";
        sysMsg["content"] = MlSystemPrompt();

        Dictionary<string, object> userMsg = new Dictionary<string, object>();
        userMsg["role"] = "user";
        userMsg["content"] = "源语言：" + (sc == "auto" ? "请根据原文自动判断" : MlName(sc))
                           + "\n目标语言：" + MlName(dc)
                           + "\n\n" + text;

        Dictionary<string, object> root = new Dictionary<string, object>();
        root["model"] = MODEL;
        root["messages"] = new object[] { sysMsg, userMsg };
        root["temperature"] = 0.2;
        root["max_tokens"] = maxTokens;
        root["stream"] = true;
        JavaScriptSerializer js = new JavaScriptSerializer();
        return js.Serialize(root);
    }

    void MlDoTranslate(string text, string sc, string dc, string cacheKey, int seq)
    {
        if (!MlIsCurrent(seq)) return;
        try
        {
            string key = ApiKey;
            if (key.Length == 0)
            {
                Log("浮层翻译跳过：还没配置 API Key");
                if (MlIsCurrent(seq))
                    Ui(delegate () {
                        if (!MlIsCurrent(seq)) return;
                        mlOutBox.Box.Text = "还没配置 API Key。\n把 apikey.txt 放到配置目录（和 settings.ini 同一个文件夹）就能翻。";
                        MlSay("缺 API Key");
                        MlBusyDone();
                    });
                return;
            }

            string payload = MlBuildPayload(text, sc, dc);
            byte[] data = Encoding.UTF8.GetBytes(payload);
            HttpWebRequest req = CreateJsonPost(API_BASE + "/chat/completions", key, data);
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
                    if (!MlIsCurrent(seq)) return;      // 有人在浮层里连点了，这条线立刻停手
                    if (Environment.TickCount - lastPaint > 120)
                    {
                        lastPaint = Environment.TickCount;
                        string snap = full;
                        Ui(delegate () {
                            if (!MlIsCurrent(seq)) return;
                            mlOutBox.Box.Text = snap;
                            MlSay("正在翻……");
                        });
                    }
                }
            }

            string content = full.Trim();
            if (content.Length == 0) throw new Exception("接口没返回内容");

            CachePut(cacheKey, content);   // 键里带语言对，这句译文只对它自己那一对语言有效
            Log("浮层翻译成功 " + MlPairLabel(sc, dc) + " " + text.Length + " 字 → " + content.Length + " 字");
            if (!MlIsCurrent(seq)) return;
            Ui(delegate () {
                if (!MlIsCurrent(seq)) return;
                mlOutBox.Box.Text = content;
                MlSay("翻好了（" + MlPairLabel(sc, dc) + "）｜Ctrl+Enter 再翻一次");
                MlBusyDone();
            });
        }
        catch (Exception ex)
        {
            Log("浮层翻译失败：" + ex.Message);
            if (MlIsCurrent(seq))
                Ui(delegate () {
                    if (!MlIsCurrent(seq)) return;
                    mlOutBox.Box.Text = "这次没翻成功：" + ex.Message + "\n网络或代理不通的话，稍后再试一次。";
                    MlSay("接口出错了，详情写在日志里");
                    MlBusyDone();
                });
        }
    }
}

// ============================================================================
//  浮层里的输入框：外框自己画圆角（TextBox 本身没法做圆角），
//  里面塞一个无边框 TextBox，读的写的都是 Box.Text。
// ============================================================================
class MlInputBox : Panel
{
    public TextBox Box;
    public string Placeholder = "";
    int padPx = 7;

    public int PadPx
    {
        get { return padPx; }
        set { padPx = value; FitBox(); Invalidate(); }
    }

    public MlInputBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = QueryForm.Pal.InputBg;

        Box = new TextBox();
        Box.BorderStyle = BorderStyle.None;
        Box.Multiline = true;
        Box.WordWrap = true;
        Box.ScrollBars = ScrollBars.Vertical;
        Box.BackColor = QueryForm.Pal.InputBg;
        Box.ForeColor = QueryForm.Pal.Ink;
        Controls.Add(Box);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        FitBox();
    }

    static GraphicsPath MlRound(Rectangle r, int radius)
    {
        GraphicsPath gp = new GraphicsPath();
        int d = Math.Max(1, radius * 2);
        if (d > r.Width) d = r.Width;
        if (d > r.Height) d = r.Height;
        gp.AddArc(r.X, r.Y, d, d, 180, 90);
        gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        gp.CloseFigure();
        return gp;
    }

    void FitBox()
    {
        if (Box == null) return;
        int w = Width - padPx * 2;
        int h = Height - padPx * 2;
        if (w < 10) w = 10;
        if (h < 10) h = 10;
        Box.SetBounds(padPx, padPx, w, h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Parent != null ? Parent.BackColor : QueryForm.Pal.Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Rectangle r = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        using (GraphicsPath gp = MlRound(r, 8))
        {
            using (SolidBrush b = new SolidBrush(QueryForm.Pal.InputBg)) g.FillPath(b, gp);
            using (Pen p = new Pen(QueryForm.Pal.InputBorder)) g.DrawPath(p, gp);
        }
        if (Placeholder.Length > 0 && Box != null && Box.TextLength == 0 && !Box.Focused)
        {
            using (SolidBrush fb = new SolidBrush(QueryForm.Pal.InkFaint))
            {
                Rectangle tr = new Rectangle(padPx + 1, padPx + 1, Math.Max(10, Width - padPx * 2 - 2), Math.Max(10, Box.Font.Height + 4));
                g.DrawString(Placeholder, Box.Font, fb, tr);
            }
        }
    }
}

// ============================================================================
//  v2.3 第 2 步：「其他语言」的小窗
//  一块真正的顶层小窗，但始终是主窗口的一部分：
//    · 归属主窗   → 设 Owner，主窗最小化/隐藏/关闭时它一起走，绝不留恋在屏幕上
//    · 不占任务栏 → ShowInTaskbar=false + WS_EX_TOOLWINDOW（也不进 Alt+Tab）
//    · 无边框     → FormBorderStyle.None，圆角与描边由外面的 MlPanelPaint 自己画
//    · 拖标题行   → 自己处理 WM_NCHITTEST，标题行整块报 HTCAPTION，交给系统拖（手感同真标题栏）
//    · 松手才记账 → WM_EXITSIZEMOVE 通知外面写位置，拖动过程中一个像素都不落盘
// ============================================================================
class MlWindow : Form
{
    public int TitleHeight = 42;
    public bool DragExited;                 // 自检用：拖动松手过没有
    public Action EscPressed;               // 窗里有焦点时按 Esc → 收窗
    public Action DragEnded;                // 拖完松手 → 记位置

    const int WM_GETMINMAXINFO = 0x0024;
    const int WM_NCHITTEST = 0x0084;
    const int WM_NCLBUTTONDBLCLK = 0x00A3;
    const int WM_ENTERSIZEMOVE = 0x0231;
    const int WM_EXITSIZEMOVE = 0x0232;
    const int HTCLIENT = 1;
    const int HTCAPTION = 2;
    const int HTLEFT = 10;
    const int HTRIGHT = 11;
    const int HTTOP = 12;
    const int HTTOPLEFT = 13;
    const int HTTOPRIGHT = 14;
    const int HTBOTTOM = 15;
    const int HTBOTTOMLEFT = 16;
    const int HTBOTTOMRIGHT = 17;

    public int GripPx = 6;                  // 四边可拉区宽度（外面按 DPI 注入，别写死像素）
    public bool Sizing;                     // 正在拖边缩放：期间只走轻量排版，一个 MeasureText 都不跑
    public Action SizingStarted;            // 开始缩放：外面拿去打标记
    public Action TitleDoubleClicked;       // 双击标题行 → 最大化 / 还原

    public MlWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        MinimizeBox = false;
        MaximizeBox = false;
        ControlBox = false;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = QueryForm.Pal.Surface;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x00000080;      // WS_EX_TOOLWINDOW
            return cp;
        }
    }

    // 拖动 / 缩放会把窗口重新激活，可编辑下拉框一旦被还回焦点就会把文字整段选中（蓝底）。
    // 这里把面板里所有下拉框的选择清掉，光标位置保留，视觉上跟没碰过一样。
    void MlClearComboSelection()
    {
        MlClearComboSelectionIn(this);
    }

    static void MlClearComboSelectionIn(Control host)
    {
        if (host == null) return;
        foreach (Control c in host.Controls)
        {
            ComboBox cb = c as ComboBox;
            if (cb != null) { try { cb.SelectionLength = 0; } catch { } }
            else MlClearComboSelectionIn(c);   // 语言框可能嵌在白卡之类的容器里，往下找一层
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_GETMINMAXINFO)
        {
            // 无边框窗口系统不替我们挡最小尺寸，拖急了能拉成一条线。
            // MINMAXINFO 里 ptMinTrackSize 在偏移 24（前三个 POINT 各 8 字节），按偏移写，省一个结构体声明。
            if (m.LParam != IntPtr.Zero)
            {
                System.Runtime.InteropServices.Marshal.WriteInt32(m.LParam, 24, Math.Max(1, MinimumSize.Width));
                System.Runtime.InteropServices.Marshal.WriteInt32(m.LParam, 28, Math.Max(1, MinimumSize.Height));
            }
            base.WndProc(ref m);
            return;
        }
        if (m.Msg == WM_NCHITTEST)
        {
            int lp = m.LParam.ToInt32();
            int sx = (short)(lp & 0xFFFF);
            int sy = (short)((lp >> 16) & 0xFFFF);
            Point cp = PointToClient(new Point(sx, sy));
            int W = ClientSize.Width, H = ClientSize.Height;
            int g = Math.Max(3, GripPx);

            // 四角和四边都要报命中码，否则只有标题行能拖、边框一根手指都拉不动。
            // 角先报：先报边的话角上只会命中单边，斜着拉就废了。
            bool onL = cp.X >= 0 && cp.X < g;
            bool onR = cp.X <= W - 1 && cp.X >= W - g;
            bool onT = cp.Y >= 0 && cp.Y < g;
            bool onB = cp.Y <= H - 1 && cp.Y >= H - g;
            if (onT && onL) { m.Result = (IntPtr)HTTOPLEFT; return; }
            if (onT && onR) { m.Result = (IntPtr)HTTOPRIGHT; return; }
            if (onB && onL) { m.Result = (IntPtr)HTBOTTOMLEFT; return; }
            if (onB && onR) { m.Result = (IntPtr)HTBOTTOMRIGHT; return; }
            if (onL) { m.Result = (IntPtr)HTLEFT; return; }
            if (onR) { m.Result = (IntPtr)HTRIGHT; return; }
            if (onB) { m.Result = (IntPtr)HTBOTTOM; return; }
            if (onT) { m.Result = (IntPtr)HTTOP; return; }   // 顶部 strip 让出来做缩放，标题文字从 y=10 起，拖拽照旧

            if (cp.Y >= 0 && cp.Y < TitleHeight)
            {
                // 标题行里只有「关闭」和「回到主窗中心」这两个可点控件照常响应，
                // 其余（标题文字、空白处）都算标题栏，按住就能拖。
                // LinkLabel 也是 Label，得先把它排掉，否则「回到主窗中心」点不动
                Control hit = GetChildAtPoint(cp);
                if (hit == null || (hit is Label && !(hit is LinkLabel))) { m.Result = (IntPtr)HTCAPTION; return; }
            }
            m.Result = (IntPtr)HTCLIENT;
            return;
        }
        if (m.Msg == WM_NCLBUTTONDBLCLK && (int)m.WParam == HTCAPTION)
        {
            // 双击标题行 = 铺满工作区 / 回到原尺寸，看长稿子不用再一行一行挪
            if (TitleDoubleClicked != null) { try { TitleDoubleClicked(); } catch { } }
            m.Result = IntPtr.Zero;
            return;
        }
        if (m.Msg == WM_ENTERSIZEMOVE)
        {
            Sizing = true;
            MlClearComboSelection();   // 一拖就把语言框里残留的全选清掉，不然看着两格发蓝
            if (SizingStarted != null) { try { SizingStarted(); } catch { } }
        }
        else if (m.Msg == WM_EXITSIZEMOVE)
        {
            Sizing = false;
            DragExited = true;
            MlClearComboSelection();   // 松手后系统会重新激活窗口，再清一次收尾
            if (DragEnded != null) { try { DragEnded(); } catch { } }
        }
        base.WndProc(ref m);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            if (EscPressed != null) { try { EscPressed(); } catch { } }
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
