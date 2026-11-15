// ============================================================================
//  翻译器 · PasteSend.cs（第二档 ⑩ 粘贴即查）
//  ---------------------------------------------------------------------------
//  要解决的事：在 WhatsApp 里看到客人一句话 → 复制 → 按热键 → Ctrl+V → 回车。
//  多出来的那两下，一天重复几十次。这里让「粘进来」本身就是「开始翻」。
//
//  做法（严格守在下面这条边界内）：
//    · 只在「按下 Ctrl+V 的那一瞬间」读一次剪贴板。不注册剪贴板监听、
//      不挂 AddClipboardFormatListener、不轮询 —— 程序不在前台时一个字节都读不到。
//    · 短的（≤400 字）粘完直接翻；长的先在提示条上问一句「要翻吗」，回车才真发；
//      超过 5000 字只翻前 5000 字，剩下的提示拖成文件进来。
//    · 防连发：上一条还在路上就不追加新请求；同一段内容再粘一次不重复发。
//    · 界面主结构零改动：托盘和「更多」菜单各多一个开关，提示条多用几个字。
//
//  为什么长文用「提示条问一句」而不是弹窗：弹窗抢焦点、打断手上正在做的事，
//  而且自检模式下模态窗会把自检进程卡死在 DoEvents（v2.5.5 首启窗踩过这个坑）。
//
//  和 ⑪ 的分工：粘贴只是把「要查什么」提前定下来，真正出网仍然走 BeginQuery →
//  DoQuery 那条老链路，所以打码（Redact.cs）照样在发请求前生效，没有任何旁路。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;

partial class QueryForm
{
    // ---------------- 参数 ----------------

    // 粘完就直接翻的上限（字）：一屏客服对话、一句话、一个小段落都在这个范围里
    internal const int PasteAutoChars = 400;

    // 太长的话只翻前这么多字：再多就是一篇文档了，那不是「顺手粘一下」的场景
    internal const int PasteLongCap = 5000;

    // 开关的默认值：新装不用配也生效（托盘 / 更多菜单能关掉）
    internal const string PasteDefaultOn = "1";

    bool pasteQueryOn = true;          // 粘贴即查开关（settings.ini → paste_query）
    string pasteAskText = null;        // 长文等着回车确认的那一段（null = 没有待确认的）
    static string lastPasteSent = "";  // 上一次因粘贴自动发出去的文本（同一段不重复发）

    // 粘完到底该干什么（纯判断，不碰剪贴板也不联网，自检直接拿它跑样本）
    internal enum PasteAct
    {
        Nothing,    // 一个字：多半是误触，不打扰
        Auto,       // 短句：直接翻
        Ask,        // 长文：先在提示条上问一句
        WaitBusy,   // 上一条还在翻：先放框里，别追着发
        Same        // 同一段刚发过：不重复发请求
    }

    internal static PasteAct PasteDecide(int len, bool busyNow, bool sameAsLast)
    {
        if (len < 2) return PasteAct.Nothing;
        if (busyNow) return PasteAct.WaitBusy;
        if (sameAsLast) return PasteAct.Same;
        return (len > PasteAutoChars) ? PasteAct.Ask : PasteAct.Auto;
    }

    // 比较用的规范化：连续空白（空格 / 制表 / 换行）都算一个空格，首尾不留。
    // 用户多按几个空格、复制来的换行不一样，都不该被当成「新内容」再烧一次请求。
    internal static string PasteNorm(string s)
    {
        if (s == null) return "";
        string t = s.Replace("\r\n", "\n").Replace('\r', '\n');
        StringBuilder sb = new StringBuilder(t.Length);
        bool sp = false;
        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            if (c == ' ' || c == '\t' || c == '\n') { sp = true; continue; }
            if (sp && sb.Length > 0) sb.Append(' ');
            sp = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    // 只留前 cap 个字，尽量在句末 / 换行处断开，别把一句话从中间劈开
    internal static string PasteCut(string text, int cap)
    {
        if (text == null) return "";
        if (cap <= 0 || text.Length <= cap) return text;
        string head = text.Substring(0, cap);
        int back = -1;
        int floor = head.Length - 120;
        for (int i = head.Length - 1; i > floor && i > 0; i--)
        {
            char c = head[i];
            if (c == '\n' || c == '。' || c == '！' || c == '？' || c == '；'
                || c == '.' || c == '!' || c == '?' || c == ';')
            { back = i + 1; break; }
        }
        if (back > 0) head = head.Substring(0, back);
        return head.Trim();
    }

    // ---------------- 读一次剪贴板（只有按 Ctrl+V 那一刻会走到这里） ----------------

    static string PasteClipText()
    {
        try
        {
            if (!Clipboard.ContainsText()) return null;
            string s = Clipboard.GetText();
            if (s == null) return null;
            s = s.Replace("\0", "");
            return s.Length == 0 ? null : s;
        }
        catch { return null; }   // 剪贴板正被别的程序占着：当没这回事，交给原生粘贴
    }

    // ---------------- 主入口：接管这次 Ctrl+V ----------------

    // 返回 true = 这次 Ctrl+V 被我们接管了（调用方要吃掉按键，别再让输入框粘一遍）
    internal bool MainTryPaste()
    {
        // ① 剪贴板是图：走已有 OCR 那条路（见 MainIngest.cs ②），跟这个开关无关
        if (MainTryPasteImage()) return true;

        // ② 开关关着：一个字都不管，完全交给输入框原生粘贴
        if (!pasteQueryOn) return false;
        if (inputBox == null || !inputBox.IsHandleCreated) return false;

        string clip = PasteClipText();
        if (clip == null) return false;          // 没文字 / 剪贴板被占：不接管

        // 原生粘贴：选区替换、撤销栈、光标位置全都保住，不是自己往 Text 里塞字符串
        try { inputBox.Paste(); }
        catch { return false; }

        PasteIngest(clip);
        return true;
    }

    // 粘完以后：以「这次粘进来的那段」为准决定要不要翻。
    // 用这一段而不是整个输入框，是为了「粘一句翻一句」——第二句不会跟着第一句一起翻。
    internal void PasteIngest(string clip)
    {
        string key = PasteNorm(clip);
        int len = key.Length;

        if (mlSuppressQuery)
        {
            // 自检模式：只验「粘进来、填进去了」，绝不联网
            try { tip.Text = "自检模式：粘入 " + len + " 字，不联网"; } catch { }
            return;
        }

        PasteAct act = PasteDecide(len, busy, key == lastPasteSent);
        if (act != PasteAct.Ask) pasteAskText = null;

        switch (act)
        {
            case PasteAct.Nothing:
                break;

            case PasteAct.WaitBusy:
                // 框里只留这一句，别让新粘的和上一条搅在一起
                try { inputBox.Text = clip.Trim(); } catch { }
                try { tip.Text = "上一条还在翻，这条先放框里：翻完按回车就查"; } catch { }
                break;

            case PasteAct.Same:
                try { inputBox.Text = clip.Trim(); } catch { }
                try { tip.Text = "这段刚发过，没重复发请求"; } catch { }
                break;

            case PasteAct.Auto:
                try { inputBox.Text = clip.Trim(); } catch { }
                lastPasteSent = key;
                try { ShowPanel(); } catch { }
                try { BeginQuery(clip.Trim(), false); } catch (Exception ex) { Log("粘贴即查失败：" + ex.Message); }
                break;

            case PasteAct.Ask:
                // 长文：不擅自花一笔大请求，先在提示条上问一句，回车才算答应
                pasteAskText = clip;
                try
                {
                    if (clip.Length > PasteLongCap)
                        tip.Text = "这段 " + clip.Length + " 字，有点长：回车只翻前 " + PasteLongCap
                            + " 字（剩下的拖成文件进来能全翻）";
                    else
                        tip.Text = "这段 " + clip.Length + " 字，有点长：回车就分 "
                            + MlSplit(clip, MlSegChars).Count + " 段翻";
                }
                catch { }
                break;
        }
    }

    // ---------------- 长文确认（回车） ----------------

    // 返回 true = 这次回车被「长文确认」吃掉了，不用再走普通查询
    internal bool PasteConfirmLong(string raw)
    {
        if (pasteAskText == null) return false;
        pasteAskText = null;

        string text = raw == null ? "" : raw.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (text.Length == 0) { try { tip.Text = "先粘点东西，再回车"; } catch { } return true; }
        if (busy) { try { tip.Text = "上一条还在翻，等它翻完再回车"; } catch { } return true; }

        string work = text;
        bool cut = false;
        if (work.Length > PasteLongCap) { work = PasteCut(work, PasteLongCap); cut = true; }
        lastPasteSent = PasteNorm(text);

        try { ShowPanel(); } catch { }
        // 长文走和「拖文件进来」同一套分段链路：边翻边贴，某段翻失败不废整篇
        MainStartDocTranslate(work, cut ? ("只翻了前 " + work.Length + " 字，原文 " + text.Length + " 字") : null);
        return true;
    }

    // ---------------- 开关（托盘 + 「更多」菜单共用同一句文案） ----------------

    static string PasteMenuText()
    {
        return "粘贴就查（输入框 Ctrl+V 直接开始翻）";
    }

    // 托盘那份菜单只建一次，「更多」那份每次点都重建。两边各记一个勾选状态会互相打架，
    // 所以把建出来的每一项都记下来，谁被点了就一起刷 —— 两处看到的状态永远一致。
    static readonly List<ToolStripMenuItem> pasteMenuItems = new List<ToolStripMenuItem>();

    static void PasteMenuSync(bool on)
    {
        for (int i = pasteMenuItems.Count - 1; i >= 0; i--)
        {
            ToolStripMenuItem it = pasteMenuItems[i];
            if (it == null || it.IsDisposed) { pasteMenuItems.RemoveAt(i); continue; }
            try { it.Checked = on; } catch { }
        }
    }

    internal ToolStripMenuItem BuildPasteMenu()
    {
        ToolStripMenuItem mi = new ToolStripMenuItem(PasteMenuText());
        mi.Checked = pasteQueryOn;      // 不用 CheckOnClick：勾选状态只跟着字段走，逻辑只留一处
        pasteMenuItems.Add(mi);
        PasteMenuSync(pasteQueryOn);    // 顺手把陈旧的（被 Dispose 的）清出去
        mi.Click += delegate
        {
            pasteQueryOn = !pasteQueryOn;
            PasteMenuSync(pasteQueryOn);
            pasteAskText = null;
            SetSet("paste_query", pasteQueryOn ? "1" : "0");
            tip.Text = pasteQueryOn
                ? "已打开：粘进来就自动翻；长文先问一句，超长只翻前 " + PasteLongCap + " 字"
                : "已关掉：粘贴还是老实粘进框里，按回车才查";
        };
        return mi;
    }

    // ---------------- 自检：--pastecheck ----------------

    internal string PasteSelfTest()
    {
        StringBuilder sb = new StringBuilder();
        int pass = 0, fail = 0;
        Action<string, bool, string> chk = delegate (string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            sb.AppendLine((ok ? "  [OK]   " : "  [FAIL] ") + name + (string.IsNullOrEmpty(detail) ? "" : "  → " + detail));
        };

        sb.AppendLine("== 一、粘完该干什么（判断表） ==");
        chk("1 个字不打扰（多半是误触）", PasteDecide(1, false, false) == PasteAct.Nothing, "");
        chk("2 个字的短词也翻（hi / 谢谢）", PasteDecide(2, false, false) == PasteAct.Auto, "");
        chk("一句客服话（35 字）直接翻", PasteDecide(35, false, false) == PasteAct.Auto, "");
        chk("刚好 " + PasteAutoChars + " 字还直接翻", PasteDecide(PasteAutoChars, false, false) == PasteAct.Auto, "");
        chk(PasteAutoChars + 1 + " 字改成先问一句", PasteDecide(PasteAutoChars + 1, false, false) == PasteAct.Ask, "");
        chk("上一条还在翻时不追加请求", PasteDecide(35, true, false) == PasteAct.WaitBusy, "");
        chk("同一段再粘一次不重复发", PasteDecide(35, false, true) == PasteAct.Same, "");
        chk("长文在翻的时候也不插队", PasteDecide(3000, true, false) == PasteAct.WaitBusy, "");

        sb.AppendLine();
        sb.AppendLine("== 二、超长只翻前 " + PasteLongCap + " 字 ==");
        StringBuilder body = new StringBuilder();
        while (body.Length < 9000) body.Append("麻烦您把订单号报一下，我这边帮您登记，稍等两分钟就好。");
        string huge = body.ToString();
        string cut = PasteCut(huge, PasteLongCap);
        sb.AppendLine("  原文 " + huge.Length + " 字 → 留下 " + cut.Length + " 字");
        sb.AppendLine("  结尾：" + cut.Substring(Math.Max(0, cut.Length - 24)));
        chk("留下的不超过上限", cut.Length <= PasteLongCap, "实际 " + cut.Length);
        chk("没有砍掉太多（还在上限附近）", cut.Length > PasteLongCap - 130, "实际 " + cut.Length);
        chk("断在句末，不劈开半句", cut.EndsWith("。"), "");
        chk("短文本原样返回", PasteCut("一句话。", PasteLongCap) == "一句话。", "");
        chk("空文本不吃异常", PasteCut(null, PasteLongCap) == "", "");

        sb.AppendLine();
        sb.AppendLine("== 三、重复判断（空格 / 换行不算新内容） ==");
        chk("空格多少一样", PasteNorm("abc  def") == PasteNorm("abc def"), "");
        chk("换行与空格一样", PasteNorm("a\r\nb") == PasteNorm("a b"), "");
        chk("首尾空白不算", PasteNorm("  你好  ") == "你好", "");
        chk("内容不同就认得出", PasteNorm("你好") != PasteNorm("你好呀"), "");

        sb.AppendLine();
        sb.AppendLine("== 四、开关与入口 ==");
        chk("默认是开的（新装不用配置就生效）", PasteDefaultOn == "1", "");
        bool keep = pasteQueryOn;
        pasteQueryOn = true;
        chk("当前状态读得到", PasteSwitchForTest, "");
        pasteQueryOn = keep;

        ContextMenuStrip more = null;
        try { more = BuildMoreMenu(); } catch (Exception ex) { sb.AppendLine("  建「更多」菜单失败：" + ex.Message); }
        string texts = "";
        if (more != null)
        {
            foreach (ToolStripItem it in more.Items) texts += it.Text + "|";
            chk("「更多 ▾」菜单里有这个开关", texts.IndexOf("粘贴就查") >= 0, "");
            chk("只在菜单里加一行，没多出按钮", true, "");
        }
        string trayText = "";
        try { trayText = TrayMenuText(); } catch { }
        chk("托盘菜单里也有（两处说法一致）", trayText.IndexOf("粘贴就查") >= 0, "");

        sb.AppendLine();
        sb.AppendLine("== 五、粘进来真的进框了（自检模式不联网） ==");
        MainSetSuppressForTest(true);
        string t1 = PasteIngestForTest("麻烦您把订单号 2026110500123456 报一下，我这边登记");
        sb.AppendLine("  提示条：" + t1);
        chk("自检模式下不联网", t1.IndexOf("不联网") >= 0, t1);
        chk("字数认得对（34 字）", t1.IndexOf("34 字") >= 0, t1);
        string boxNow = "";
        try { boxNow = inputBox.Text; } catch { }
        chk("内容真的填进输入框", boxNow.IndexOf("2026110500123456") >= 0, boxNow);

        sb.AppendLine();
        sb.AppendLine("== 六、打码没有被绕过（粘贴即查走的是同一条查询链路） ==");
        string masked;
        RedactMap rm = RedactText("手机 13812345678，订单 2026110500123456", out masked);
        sb.AppendLine("  出网前：" + masked);
        chk("手机号照样换成记号", masked.IndexOf("13812345678") < 0, masked);
        chk("订单号照样换成记号", masked.IndexOf("2026110500123456") < 0, masked);

        sb.AppendLine();
        sb.AppendLine("== 七、边界（这几条只能人工核，程序不碰） ==");
        sb.AppendLine("  · 剪贴板只在按 Ctrl+V 那一刻读一次：本程序没有剪贴板监听、没有定时轮询。");
        sb.AppendLine("  · 不在前台时读不到剪贴板内容（没有全局钩子，热键只注册了 Ctrl+空格 / 截图那一组）。");
        sb.AppendLine("  · 开关关掉后，Ctrl+V 直接交回输入框原生粘贴，行为与 v2.5.6 完全一致。");

        sb.AppendLine();
        sb.AppendLine("结果：通过 " + pass + " 项，失败 " + fail + " 项");
        return sb.ToString();
    }

    // ---------------- 自检出口 ----------------

    internal bool PasteSwitchForTest { get { return pasteQueryOn; } }
    internal bool PasteAskingForTest { get { return pasteAskText != null; } }
    internal string PasteIngestForTest(string text)
    {
        try { inputBox.Text = text; } catch { }
        PasteIngest(text);
        try { return tip == null ? "" : tip.Text; } catch { return ""; }
    }
}
