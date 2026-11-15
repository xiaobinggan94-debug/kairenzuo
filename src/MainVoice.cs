// MainVoice.cs —— v2.5 第 5 步：主界面语音输入（中文 / 英文，停口 1.8 秒自动开翻）
//
//   ① 输入框右侧一个「说」按钮：点一次开始听，再点一次停；录音中按钮变红、文案变「停」
//   ② 说完静音 1.8 秒自动收尾，收完立刻走 QueryManual() 直接翻译，不用再点「查一下」
//   ③ 最长 30 秒还没收口就自动收；一个字都没听到就提示重说，绝不去翻一段空文本
//   ④ 识别到的字实时写进输入框（SpeechHypothesized 的中间结果也照写），用户看得见听到了什么
//   ⑤ 默认中文识别，右键「说」切英文；没引擎 / 没麦克风 / 没权限 一律给人话提示，绝不崩
//
// 三条硬约束（和 v2.4 第 3、4 步同一套）：
//   · 底栏那 5 个按钮（复制这句 / 读一遍 / 谐音 / 语气 / 更多 ▾）位置大小文案样式一个都不动
//   · 「说」按钮是浮在输入框内部右侧的小圆角按钮：不改 Dock 结构、不占新的行、不撑大窗口，
//     输入框与「查一下」的宽度一个像素都不变（输入框只是把右侧内边距用 EM_SETMARGINS 让出来，
//     文字不会跑到按钮底下）
//   · 外框 DWM 圆角、框选截图的绘制路径完全不碰
//
// 识别引擎：System.Speech.Recognition（跟「读一遍」用的 System.Speech 同一个程序集），
// 只认系统里装了的识别器；Windows 语音组件没装/语言包不全时如实说，不假装能用。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Speech.Recognition;
using System.Text;
using System.Threading;
using System.Windows.Forms;

partial class QueryForm
{
    // ======================== 参数（自检要断言，所以是 const） ========================

    internal const int VOICE_STOP_SILENCE_MS = 1800;   // 停口多久算说完
    internal const int VOICE_MAX_MS = 30000;           // 最长听多久
    internal const int VOICE_TICK_MS = 100;            // 计时器粒度
    const int EM_SETMARGINS = 0x00D3;
    const int EC_RIGHTMARGIN = 0x0002;
    const int EC_LEFTMARGIN = 0x0001;

    // ======================== 状态 ========================

    RoundButton btnMic;               // 浮在输入框右侧的「说」
    ToolTip micTip;
    ContextMenuStrip micMenu;
    ToolStripMenuItem miMicZh, miMicEn;
    System.Windows.Forms.Timer micTimer;
    SpeechRecognitionEngine micEngine;
    LayoutEventHandler micPlaceHandler;

    bool micOn;                       // 正在听
    DateTime micStartAt;              // 这一轮开始听的时间（算 30 秒上限）
    long micLastSoundTicks;           // 最后一次听到动静的时刻（跨线程，用 Interlocked）
    string micPrefix = "";            // 开始听之前输入框里已有的字
    readonly object micLock = new object();   // 引擎线程写、界面线程读，统一在这把锁底下
    StringBuilder micFinal = new StringBuilder();   // 已经定稿的识别结果
    string micPartial = "";           // 正在猜的中间结果
    int micTickShownSec = -1;         // 上次刷新秒数提示的时刻，避免每 100ms 重绘

    internal string lastVoiceWarn = "";      // 最近一次给人看的提示（自检要断言是人话）
    bool micWarnQuiet;                      // 自检模式：不弹模态框
    internal static bool voiceForceNoEngineForTest;   // 自检：强制走「没引擎」分支

    // 识别语言：zh（默认）/ en
    internal static string MicLang { get { return GetSet("voice_lang", "zh"); } }

    // ======================== 装按钮（挂在输入框容器里，不占布局） ========================

    internal void MainVoiceInit(Panel inputWrap)
    {
        if (inputWrap == null || btnMic != null) return;
        try
        {
            btnMic = new RoundButton();
            btnMic.Name = "btnMic";
            btnMic.Text = "说";
            btnMic.TabStop = false;
            btnMic.Cursor = Cursors.Hand;
            btnMic.BackColor = Pal.InputBg;
            btnMic.ForeColor = Pal.Accent;
            btnMic.FillColor = Pal.AccentSoft;
            btnMic.HoverColor = Pal.AccentSoftHoverAlt;
            btnMic.Radius = S(6);
            btnMic.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            btnMic.Click += delegate { MainVoiceToggle(); };
            btnMic.MouseUp += delegate (object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right) MainVoiceShowMenu();
            };

            micTip = new ToolTip();
            micTip.SetToolTip(btnMic, "说：点一下开始听，再点一下结束；说完停 1.8 秒自动开翻（右键切中文 / 英文识别）");

            inputWrap.Controls.Add(btnMic);
            btnMic.BringToFront();

            MainVoiceBuildMenu();
            MainVoicePlace(btnMic, inputWrap);

            if (micPlaceHandler != null) inputWrap.Layout -= micPlaceHandler;
            micPlaceHandler = delegate (object s2, LayoutEventArgs le) { MainVoicePlace(btnMic, inputWrap); };
            inputWrap.Layout += micPlaceHandler;

            // 输入框右侧让出按钮的位置：文字不会钻到按钮底下，宽度/位置都不改
            if (inputBox != null)
            {
                inputBox.HandleCreated += delegate { MainVoiceTextMargin(); };
                if (inputBox.IsHandleCreated) MainVoiceTextMargin();
            }

            micTimer = new System.Windows.Forms.Timer();
            micTimer.Interval = VOICE_TICK_MS;
            micTimer.Tick += MainVoiceTick;

            FormClosing += delegate { MainVoiceShutdown(); };
            Deactivate += delegate { if (micOn) MainVoiceCancel("窗口被切走了，先停"); };
        }
        catch (Exception ex) { Log("挂「说」按钮失败：" + ex.Message); }
    }

    // 只做定位和尺寸：按钮是浮层，不参与 Dock，不改变任何已有控件的几何
    void MainVoicePlace(RoundButton b, Panel wrap)
    {
        if (b == null || wrap == null) return;
        try
        {
            int ih = wrap.ClientSize.Height;
            int w = S(34);
            int h = Math.Max(S(18), ih - S(8));
            if (b.Width != w || b.Height != h) b.Size = new Size(w, h);
            int x = wrap.ClientSize.Width - S(10) - w;
            if (x < S(4)) x = S(4);
            Point np = new Point(x, Math.Max(0, (ih - h) / 2));
            if (b.Location != np) b.Location = np;
            if (inputHint != null) inputHint.Padding = new Padding(0, 0, w + S(12), 0);
            float fs = 9f * Math.Min(uiScale, 1.35f);
            if (b.Font == null || Math.Abs(b.Font.Size - fs) > 0.2f)
                b.Font = new Font("Microsoft YaHei UI", fs, FontStyle.Bold);
            MainVoiceTextMargin();
        }
        catch { }
    }

    // 输入框右侧留白（EM_SETMARGINS）。只影响文字排版，不改控件宽度
    void MainVoiceTextMargin()
    {
        try
        {
            if (inputBox == null || !inputBox.IsHandleCreated) return;
            int right = (btnMic != null ? btnMic.Width : S(34)) + S(12);
            SendMessage(inputBox.Handle, EM_SETMARGINS, EC_LEFTMARGIN | EC_RIGHTMARGIN, right << 16);
        }
        catch { }
    }

    // ======================== 右键：切中 / 英 ========================

    void MainVoiceBuildMenu()
    {
        micMenu = new ContextMenuStrip();
        try { micMenu.Renderer = new FlatMenuRenderer(); } catch { }
        micMenu.ShowImageMargin = false;

        miMicZh = new ToolStripMenuItem("中文识别（普通话）");
        miMicEn = new ToolStripMenuItem("English 识别");
        miMicZh.Click += delegate { MainVoiceSetLang("zh"); };
        miMicEn.Click += delegate { MainVoiceSetLang("en"); };
        micMenu.Items.Add(miMicZh);
        micMenu.Items.Add(miMicEn);
        micMenu.Items.Add(new ToolStripSeparator());
        ToolStripMenuItem miHelp = new ToolStripMenuItem("听不了怎么办");
        miHelp.Click += delegate { MainVoiceHelp(); };
        micMenu.Items.Add(miHelp);
        MainVoiceSyncMenu();
    }

    void MainVoiceSyncMenu()
    {
        try
        {
            bool zh = MicLang != "en";
            if (miMicZh != null) miMicZh.Checked = zh;
            if (miMicEn != null) miMicEn.Checked = !zh;
        }
        catch { }
    }

    internal void MainVoiceSetLang(string lang)
    {
        try
        {
            SetSet("voice_lang", lang == "en" ? "en" : "zh");
            MainVoiceSyncMenu();
            if (micOn) MainVoiceCancel("换识别语言，先停");
            if (tip != null)
                tip.Text = (MicLang == "en") ? "以后「说」按英文听（右键可切回中文）" : "以后「说」按中文听（右键可切英文）";
        }
        catch { }
    }

    void MainVoiceShowMenu()
    {
        try
        {
            if (micMenu == null) return;
            MainVoiceSyncMenu();
            micMenu.Show(btnMic, new Point(-micMenu.Width + btnMic.Width, btnMic.Height));
        }
        catch { }
    }

    // 引擎线程写、界面线程读：取一份干净快照（锁内拼，避免 StringBuilder 边写边读）
    string MicSnapshot()
    {
        lock (micLock) return MicCompose(micPrefix, micFinal.ToString(), micPartial);
    }

    // 取完就清，交给调用方去落输入框 / 去翻译
    string MicTakeText()
    {
        string t;
        lock (micLock)
        {
            t = MicCompose(micPrefix, micFinal.ToString(), micPartial);
            micPrefix = "";
            micFinal.Length = 0;
            micPartial = "";
        }
        return t;
    }

    bool MicHasText()
    {
        lock (micLock) return micFinal.Length > 0 || (micPartial != null && micPartial.Length > 0);
    }

    // ======================== 开始 / 收尾 ========================

    void MainVoiceToggle()
    {
        if (micOn) { MainVoiceFinish("你按停了"); return; }
        MainVoiceStart();
    }

    void MainVoiceStart()
    {
        if (micOn) return;
        string err = "";
        SpeechRecognitionEngine eng = MainVoiceBuildEngine(out err);
        if (eng == null) { MainVoiceWarn(err); return; }

        try
        {
            eng.SpeechDetected += MainVoiceOnDetected;
            eng.SpeechHypothesized += MainVoiceOnHypothesized;
            eng.SpeechRecognized += MainVoiceOnRecognized;
            eng.RecognizeCompleted += MainVoiceOnCompleted;
            eng.SetInputToDefaultAudioDevice();   // 没麦克风 / 被占用 / 没权限 都会在这里炸
        }
        catch (Exception ex)
        {
            try { eng.Dispose(); } catch { }
            MainVoiceWarn(MainVoiceMicErrorText(ex));
            return;
        }

        micEngine = eng;
        string prefixNow = (inputBox == null) ? "" : (inputBox.Text ?? "").Trim();
        lock (micLock)
        {
            micPrefix = prefixNow;
            micFinal.Length = 0;
            micPartial = "";
        }
        micOn = true;
        micStartAt = DateTime.Now;
        micTickShownSec = -1;
        Interlocked.Exchange(ref micLastSoundTicks, DateTime.Now.Ticks);
        if (inputHint != null) inputHint.Visible = false;
        MainVoicePaintState();
        micTimer.Start();
        try { eng.RecognizeAsync(RecognizeMode.Multiple); }
        catch (Exception ex) { MainVoiceAbort(MainVoiceMicErrorText(ex)); return; }
        if (tip != null) tip.Text = "正在听…说完停 1.8 秒自动开翻（再点一下「说」立刻收）";
    }

    // 收尾：把听到的字留在输入框，然后直接开翻（不再需要点「查一下」）
    void MainVoiceFinish(string reason)
    {
        if (!micOn) return;
        micOn = false;
        MainVoiceStopEngine();
        MainVoicePaintState();
        if (micTimer != null) micTimer.Stop();

        string text = MicTakeText();

        try
        {
            if (inputBox != null)
            {
                inputBox.Text = text;
                inputBox.SelectionStart = text.Length;
                inputBox.SelectionLength = 0;
            }
            if (inputHint != null) inputHint.Visible = (text.Length == 0);
        }
        catch { }

        if (text.Length == 0)
        {
            if (tip != null) tip.Text = "没听到声音（" + reason + "）——再点一下「说」重说一次";
            return;
        }
        if (tip != null) tip.Text = "听完了（" + reason + "），正在翻…";
        QueryManual();     // 直接开翻
    }

    // 用户主动取消 / 窗口切走：留在输入框里，不翻译
    void MainVoiceCancel(string reason)
    {
        if (!micOn) return;
        micOn = false;
        MainVoiceStopEngine();
        MainVoicePaintState();
        if (micTimer != null) micTimer.Stop();
        string text = MicTakeText();
        try
        {
            if (inputBox != null && text.Length > 0) inputBox.Text = text;
            if (inputHint != null) inputHint.Visible = (inputBox == null || inputBox.Text.Length == 0);
        }
        catch { }
        if (tip != null) tip.Text = (text.Length > 0 ? "停下了，听到的字留在输入框里。" : "停下了（" + reason + "）。");
    }

    void MainVoiceAbort(string msg)
    {
        micOn = false;
        MainVoiceStopEngine();
        MainVoicePaintState();
        if (micTimer != null) micTimer.Stop();
        MainVoiceWarn(msg);
    }

    void MainVoiceStopEngine()
    {
        SpeechRecognitionEngine e = micEngine;
        micEngine = null;
        if (e == null) return;
        try { e.RecognizeAsyncCancel(); } catch { }
        try { e.Dispose(); } catch { }
    }

    void MainVoiceShutdown()
    {
        try { micOn = false; MainVoiceStopEngine(); } catch { }
        try { if (micTimer != null) { micTimer.Stop(); micTimer.Dispose(); micTimer = null; } } catch { }
        try { if (micMenu != null) { micMenu.Dispose(); micMenu = null; } } catch { }
        try { if (micTip != null) { micTip.Dispose(); micTip = null; } } catch { }
    }

    // ======================== 录音中的样子 ========================

    internal void MainVoicePaintState()
    {
        if (btnMic == null) return;
        try
        {
            if (micOn)
            {
                btnMic.Text = "停";
                btnMic.FillColor = Pal.Danger;
                btnMic.HoverColor = Pal.Danger;
                btnMic.ForeColor = Pal.OnAccent;
                if (micTip != null) micTip.SetToolTip(btnMic, "正在听…再点一下立刻收尾并翻译");
            }
            else
            {
                btnMic.Text = "说";
                btnMic.FillColor = Pal.AccentSoft;
                btnMic.HoverColor = Pal.AccentSoftHoverAlt;
                btnMic.ForeColor = Pal.Accent;
                if (micTip != null) micTip.SetToolTip(btnMic, "说：点一下开始听，再点一下结束；说完停 1.8 秒自动开翻（右键切中文 / 英文识别）");
            }
            btnMic.Invalidate();
        }
        catch { }
    }

    // ======================== 计时：停口 1.8 秒 / 上限 30 秒 ========================

    void MainVoiceTick(object sender, EventArgs e)
    {
        if (!micOn) return;
        try
        {
            long last = Interlocked.Read(ref micLastSoundTicks);
            int silence = (int)((DateTime.Now.Ticks - last) / TimeSpan.TicksPerMillisecond);
            int total = (int)(DateTime.Now - micStartAt).TotalMilliseconds;

            if (total >= VOICE_MAX_MS) { MainVoiceFinish("到 30 秒上限了"); return; }

            bool got = MicHasText();
            if (got && silence >= VOICE_STOP_SILENCE_MS) { MainVoiceFinish("说完停口了"); return; }

            int sec = total / 1000 + 1;
            if (sec != micTickShownSec && tip != null)
            {
                micTickShownSec = sec;
                tip.Text = "正在听…" + sec + " 秒（说完停 1.8 秒自动翻，最长 30 秒）";
            }
        }
        catch { }
    }

    // ======================== 引擎回调 ========================

    void MainVoiceOnDetected(object sender, SpeechDetectedEventArgs e)
    {
        try { Interlocked.Exchange(ref micLastSoundTicks, DateTime.Now.Ticks); } catch { }
    }

    void MainVoiceOnHypothesized(object sender, SpeechHypothesizedEventArgs e)
    {
        try
        {
            if (e.Result == null || string.IsNullOrEmpty(e.Result.Text)) return;
            lock (micLock) micPartial = e.Result.Text;
            Interlocked.Exchange(ref micLastSoundTicks, DateTime.Now.Ticks);
            Ui(delegate { if (micOn) MainVoiceShowLive(); });
        }
        catch { }
    }

    void MainVoiceOnRecognized(object sender, SpeechRecognizedEventArgs e)
    {
        try
        {
            if (e.Result == null || string.IsNullOrEmpty(e.Result.Text)) return;
            if (e.Result.Confidence < 0.10f && e.Result.Text.Trim().Length < 2) return;   // 杂音，不写进框
            lock (micLock)
            {
                string cur = micFinal.ToString();
                micFinal.Length = 0;
                micFinal.Append(MicJoin(cur, e.Result.Text));
                micPartial = "";
            }
            Interlocked.Exchange(ref micLastSoundTicks, DateTime.Now.Ticks);
            Ui(delegate { if (micOn) MainVoiceShowLive(); });
        }
        catch { }
    }

    void MainVoiceOnCompleted(object sender, RecognizeCompletedEventArgs e)
    {
        try
        {
            if (!micOn) return;             // 是我们自己 cancel 的，不用管
            bool bad = (e.Error != null);
            Ui(delegate { if (micOn) MainVoiceFinish(bad ? "引擎中途歇了，先按听到的翻" : "引擎自己收了"); });
        }
        catch { }
    }

    // 实时把听到的字写进输入框（中间结果也写，用户看得见）
    void MainVoiceShowLive()
    {
        try
        {
            if (!micOn || inputBox == null) return;
            string t = MicSnapshot();
            if (inputBox.Text == t) return;
            inputBox.Text = t;
            inputBox.SelectionStart = t.Length;
            inputBox.SelectionLength = 0;
            if (inputHint != null) inputHint.Visible = (t.Length == 0);
        }
        catch { }
    }

    // ======================== 引擎挑选与提示文案 ========================

    internal SpeechRecognitionEngine MainVoiceBuildEngine(out string err)
    {
        err = "";
        if (voiceForceNoEngineForTest)
        {
            err = MainVoiceNoEngineText(null);
            return null;
        }

        List<RecognizerInfo> all = new List<RecognizerInfo>();
        try { all.AddRange(SpeechRecognitionEngine.InstalledRecognizers()); }
        catch (Exception ex) { err = MainVoiceNoEngineText(ex); return null; }
        if (all.Count == 0) { err = MainVoiceNoEngineText(null); return null; }

        bool wantEn = (MicLang == "en");
        RecognizerInfo pick = MicPick(all, wantEn ? "en" : "zh");
        if (pick == null)
        {
            string have = MicCultureList(all);
            err = wantEn
                ? "系统里没装英文语音识别引擎（现在只有：" + have + "）。右键「说」切回中文识别，或者先用打字。"
                : "系统里没装中文语音识别引擎（现在只有：" + have + "）。右键「说」可以切英文识别，或者先用打字。";
            return null;
        }

        try
        {
            SpeechRecognitionEngine eng = new SpeechRecognitionEngine(pick);
            eng.LoadGrammar(new DictationGrammar());
            return eng;
        }
        catch (Exception ex)
        {
            err = "语音识别引擎起不来（" + MicCultureName(pick) + "）：" + ex.Message + "。先用打字或粘贴吧。";
            return null;
        }
    }

    // 优先取标准中文（zh-CN）→ 其它中文；英文同理
    static RecognizerInfo MicPick(List<RecognizerInfo> all, string prefix)
    {
        RecognizerInfo loose = null;
        string exact = (prefix == "zh") ? "zh-CN" : "en-US";
        foreach (RecognizerInfo r in all)
        {
            if (r == null || r.Culture == null || r.Culture.Name == null) continue;
            string n = r.Culture.Name;
            if (n.Equals(exact, StringComparison.OrdinalIgnoreCase)) return r;
            if (loose == null && n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) loose = r;
        }
        return loose;
    }

    static string MicCultureName(RecognizerInfo r)
    {
        try { return (r == null || r.Culture == null) ? "?" : r.Culture.Name; }
        catch { return "?"; }
    }

    static string MicCultureList(List<RecognizerInfo> all)
    {
        List<string> names = new List<string>();
        foreach (RecognizerInfo r in all)
        {
            string n = MicCultureName(r);
            if (!names.Contains(n)) names.Add(n);
        }
        return names.Count == 0 ? "一个都没有" : string.Join("、", names.ToArray());
    }

    internal static string MainVoiceNoEngineText(Exception ex)
    {
        string s = "这台电脑没有可用的语音识别引擎（Windows 的「语音识别」组件没装，或语言包不全）。打字、粘贴、拖文件都照常能用。";
        if (ex != null) s += "（" + ex.GetType().Name + "）";
        return s;
    }

    internal static string MainVoiceMicErrorText(Exception ex)
    {
        if (ex == null) return "麦克风打不开。先看看是不是被别的程序占着。";
        string m = ex.Message == null ? "" : ex.Message;
        int hr = 0;
        try { hr = Marshal.GetHRForException(ex); } catch { }
        if (hr == unchecked((int)0x80070005) || m.IndexOf("denied", StringComparison.OrdinalIgnoreCase) >= 0 ||
            m.IndexOf("拒绝", StringComparison.Ordinal) >= 0)
            return "系统没让这个程序用麦克风。去「设置 → 隐私和安全性 → 麦克风」把权限打开，再点「说」。";
        if (ex is InvalidOperationException || m.IndexOf("audio", StringComparison.OrdinalIgnoreCase) >= 0 ||
            m.IndexOf("device", StringComparison.OrdinalIgnoreCase) >= 0)
            return "找不到麦克风，或者它正被别的程序占着（录音、开会、语音输入的软件最常见）。关掉那个程序再点「说」。";
        return "麦克风打不开：" + m + "。检查一下麦克风插好没有、有没有被别的程序占着。";
    }

    void MainVoiceWarn(string msg)
    {
        if (string.IsNullOrEmpty(msg)) msg = "现在听不了，先用打字或粘贴吧。";
        lastVoiceWarn = msg;
        try { if (tip != null) tip.Text = msg; } catch { }
        if (micWarnQuiet) return;
        try { MessageBox.Show(this, msg, "说：现在听不了", MessageBoxButtons.OK, MessageBoxIcon.Information); } catch { }
    }

    void MainVoiceHelp()
    {
        string txt =
            "「说」听不了，通常是这三件事：\r\n\r\n" +
            "① 系统没装语音识别组件\r\n" +
            "   设置 → 时间和语言 → 语言和区域 → 中文(简体) → 语言选项 → 语音，装一下「语音识别」。\r\n\r\n" +
            "② 麦克风被占用\r\n" +
            "   录音、会议、别的语音输入软件会独占麦克风，关掉它们再试。\r\n\r\n" +
            "③ 没给这个程序麦克风权限\r\n" +
            "   设置 → 隐私和安全性 → 麦克风，允许桌面应用使用麦克风。\r\n\r\n" +
            "听不了也不影响用：打字、回车、拖文件、粘贴图片都照常。";
        try { MessageBox.Show(this, txt, "说：听不了怎么办", MessageBoxButtons.OK, MessageBoxIcon.Information); } catch { }
    }

    // ======================== 文本拼接（自检可直测） ========================

    // 定稿 + 中间结果 + 之前输入框里的字 → 一行干净文本
    internal static string MicCompose(string prefix, string final, string partial)
    {
        string tail = MicJoin(final, partial);
        string all = MicJoin(prefix, tail);
        return MicClean(all);
    }

    // 拼接两段：都是西文单词之间补一个空格，中文直接连
    internal static string MicJoin(string a, string b)
    {
        a = MicClean(a);
        b = MicClean(b);
        if (a.Length == 0) return b;
        if (b.Length == 0) return a;
        char x = a[a.Length - 1];
        char y = b[0];
        // 下一段以英文/数字开头，且前一串不是标点收尾 → 补一个空格（「今天 HELLO」而不是「今天HELLO」）
        bool wordB = (y < 128 && char.IsLetterOrDigit(y));
        bool noSpaceAfter = "，。、！？；：（）【】《》,.!?;:\"'".IndexOf(x) >= 0;
        return (wordB && !noSpaceAfter) ? a + " " + b : a + b;
    }

    // 换行/制表都归一成空格，首尾去空白，连续空格合一个
    internal static string MicClean(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        StringBuilder sb = new StringBuilder(s.Length);
        bool lastSpace = false;
        foreach (char c in s)
        {
            char ch = (c == '\r' || c == '\n' || c == '\t') ? ' ' : c;
            if (ch == ' ')
            {
                if (lastSpace || sb.Length == 0) continue;
                lastSpace = true;
                sb.Append(' ');
            }
            else { lastSpace = false; sb.Append(ch); }
        }
        return sb.ToString().Trim();
    }

    // ======================== 自检用的钩子 ========================

    internal int SForTest(int v) { return S(v); }
    internal bool MainVoiceOnForTest { get { return micOn; } }
    internal ContextMenuStrip MainVoiceMenuForTest { get { return micMenu; } }
    internal void MainVoiceStartForTest() { MainVoiceStart(); }
    internal void MainVoiceFinishForTest(string reason) { MainVoiceFinish(reason); }
    internal string MainVoiceLangForTest { get { return MicLang; } }
    internal Control MainVoiceButtonForTest { get { return btnMic; } }
    internal Color MainVoiceFillForTest { get { return (btnMic == null) ? Color.Empty : btnMic.FillColor; } }
    internal void MainVoiceQuietWarnForTest(bool quiet) { micWarnQuiet = quiet; }
    internal void MainVoiceStateForTest(bool on) { micOn = on; MainVoicePaintState(); }
    internal string MainVoiceEngineListForTest()
    {
        try
        {
            List<RecognizerInfo> all = new List<RecognizerInfo>();
            all.AddRange(SpeechRecognitionEngine.InstalledRecognizers());
            return MicCultureList(all);
        }
        catch (Exception ex) { return "枚举失败：" + ex.GetType().Name; }
    }

    // 卸掉再装一次：用来证明「装了按钮之后，窗体和输入框的尺寸一点没变」
    internal void MainVoiceReinstallForTest()
    {
        try
        {
            if (btnMic != null)
            {
                Control p = btnMic.Parent;
                if (p != null) p.Controls.Remove(btnMic);
                btnMic.Dispose();
            }
        }
        catch { }
        btnMic = null;
        Panel wrap = FindCtl("inputWrap") as Panel;
        MainVoiceInit(wrap);
    }

    internal void MainVoiceDisposeForTest()
    {
        try
        {
            if (btnMic != null)
            {
                Control p = btnMic.Parent;
                if (p != null) p.Controls.Remove(btnMic);
                btnMic.Dispose();
            }
        }
        catch { }
        btnMic = null;
    }
}
