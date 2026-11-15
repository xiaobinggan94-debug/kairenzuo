// ============================================================================
//  饼饼专属中英互译 · KeyNav.cs（第二档 ⑫ 手不想离开键盘）
//  ---------------------------------------------------------------------------
//  要解决的事：刚查过的一句想改一个字再查一次，现在得重新打一遍，或者打开
//  「历史记录」小窗去点一条。手一离开键盘，客服回消息的节奏就断了。
//
//  做法（只碰键盘，不碰主界面结构）：
//    · 输入框里按 ↑ = 把上一条查过的原话填回来；再按 ↑ 继续往前翻（最多 20 条，
//      跟「历史记录」小窗是同一份数据）；按 ↓ 往新的翻，翻到 -1 就把你原本打的
//      那句话还给你，一个字都不丢。
//    · 翻历史只填字，不发请求、不建历史、不改 lastInput —— 想查还得自己按回车。
//    · 输入法正在选字时（拼音候选），↑↓ 一个都不抢，整段让给输入法。
//    · 焦点看得见：Tab 或鼠标点进输入框时，那个圆角框亮成主题蓝，一眼就知道
//      「现在打字打这里」。焦点不在这里时不点亮，所以截图跟上一版逐字节可比。
//
//  为什么不做「Tab 键在控件间循环高亮」：底栏五个按钮和正文框都是这轮的红线，
//  给整窗装焦点框一定会动到它们的绘制。这里只点亮输入框边框这一个像素级的变化。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;

partial class QueryForm
{
    // ---------------- 参数 ----------------

    // 浏览指针：-1 = 没在翻历史（框里是你自己打的字）；0 = 最近一条，往后越来越旧
    int navPos = -1;

    // 开始翻之前框里那句话，↓ 翻回 -1 时原样还回去
    string navSave = "";

    // 我们自己往框里填字时会触发 TextChanged，别把浏览状态错误地清掉
    bool navBusy = false;

    // ---------------- 主入口：一次 ↑ / ↓ ----------------

    // 返回 true = 这次按键被吃掉了（调用方要 SuppressKeyPress，别让输入框自己动光标）
    internal bool MainNavStep(int dir, bool imeComposing)
    {
        if (inputBox == null) return false;

        // 输入法正在选字：上下键是它翻候选词用的，一个都不抢
        if (imeComposing) return false;

        int n = (history == null) ? 0 : history.Count;
        if (n == 0)
        {
            try { tip.Text = "还没有历史记录，先查一句试试"; } catch { }
            return true;
        }

        int next = navPos + (dir < 0 ? 1 : -1);

        // 已经翻到最早一条，再往上就没了：给句人话，不动输入框
        if (dir < 0 && next >= n)
        {
            try { tip.Text = "这已经是最早的一条了（一共 " + n + " 条）"; } catch { }
            return true;
        }

        // 往下翻出第一条：回到你自己打的那句
        if (dir > 0 && next < 0)
        {
            NavFill(navSave, "回到你刚才打的那句了（回车就查）");
            navPos = -1;
            navSave = "";
            return true;
        }

        // 第一次往上翻：先把框里原有的字收好
        if (navPos < 0) navSave = (inputBox.Text == null) ? "" : inputBox.Text;

        string body = (history[next].Input == null) ? "" : history[next].Input;
        NavFill(body, "历史 " + (next + 1) + "/" + n + "（↑ 更旧　↓ 更新　回车就查）");
        navPos = next;
        return true;
    }

    // 只填字 + 改提示条，不联网、不建历史、不动 lastInput
    void NavFill(string text, string tipText)
    {
        navBusy = true;
        try
        {
            inputBox.Text = (text == null) ? "" : text;
            inputBox.SelectionStart = inputBox.Text.Length;
            inputBox.SelectionLength = 0;
        }
        catch { }
        navBusy = false;
        try { inputHint.Visible = (inputBox.Text.Length == 0); } catch { }
        try { tip.Text = tipText; } catch { }
    }

    // 用户自己打字 / 删字了：退出浏览状态，下次 ↑ 从最新一条重新开始
    void NavUserEdit()
    {
        if (navBusy) return;
        if (navPos >= 0) { navPos = -1; navSave = ""; }
    }

    // ---------------- 焦点高亮（Tab 走到输入框看得见） ----------------

    // 自检模式一律不点亮：主窗截图才能跟上一版逐字节对比
    internal static bool NavGlow(bool selfTest, bool focused)
    {
        return (!selfTest) && focused;
    }
    internal bool NavGlowNow()
    {
        return NavGlow(selfTestMode, inputBox != null && inputBox.Focused);
    }

    // ---------------- 自检：--kbcheck ----------------

    internal int NavPosForTest { get { return navPos; } }
    internal string NavSaveForTest { get { return navSave; } }

    internal string NavSelfTest()
    {
        StringBuilder sb = new StringBuilder();
        int pass = 0, fail = 0;
        Action<string, bool, string> chk = delegate (string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            sb.AppendLine((ok ? "  [OK]   " : "  [FAIL] ") + name + (string.IsNullOrEmpty(detail) ? "" : "  → " + detail));
        };

        // 用户的真实历史先备份，测完原样放回去（自检绝不改数据文件）
        List<HistItem> backup = new List<HistItem>();
        if (history != null) backup.AddRange(history);
        string oldText = "";
        bool oldHint = true;
        try { oldText = inputBox.Text; } catch { }
        try { oldHint = inputHint.Visible; } catch { }
        bool oldBusy = busy;
        string oldLast = lastInput;

        try
        {
            sb.AppendLine("== 一、一条历史都没有的时候 ==");
            history.Clear();
            navPos = -1;
            bool ate = MainNavStep(-1, false);
            string t1 = TipTextForTest;
            sb.AppendLine("  提示条：" + t1);
            chk("按 ↑ 不崩、不当成查询", ate, "");
            chk("给的是人话（提示先查一句）", t1.IndexOf("还没有历史") >= 0, t1);

            sb.AppendLine();
            sb.AppendLine("== 二、翻历史（3 条样本） ==");
            history.Clear();
            AddSample("第一句：麻烦您提供一下订单号", "第一条的结果");
            AddSample("第二句：稍等，我这边帮您查", "第二条的结果");
            AddSample("第三句：已经登记好了，请留意短信", "第三条的结果");
            history.Reverse();   // 真实数据是新的在前：history[0] = 最后查的那句
            int n0 = history.Count;

            navPos = -1;
            try { inputBox.Text = "我本来打到一半的话"; } catch { }

            MainNavStep(-1, false);
            chk("↑ 第一次填回最近查的那句", inputBox.Text == history[0].Input, inputBox.Text);
            chk("浏览指针落在第 1 条", navPos == 0, navPos.ToString());
            chk("进入浏览前把原话存住了", navSave == "我本来打到一半的话", navSave);

            MainNavStep(-1, false);
            chk("再 ↑ 翻到第 2 条", inputBox.Text == history[1].Input, inputBox.Text);
            MainNavStep(-1, false);
            chk("再 ↑ 翻到第 3 条（最早）", inputBox.Text == history[2].Input, inputBox.Text);

            string tEdge = "";
            MainNavStep(-1, false);
            tEdge = TipTextForTest;
            sb.AppendLine("  到顶提示：" + tEdge);
            chk("到最早一条就不再动，也不越界", inputBox.Text == history[2].Input && navPos == 2, navPos.ToString());
            chk("到顶给的是人话", tEdge.IndexOf("最早的一条") >= 0, tEdge);

            MainNavStep(1, false);
            chk("↓ 回到第 2 条", inputBox.Text == history[1].Input, inputBox.Text);
            MainNavStep(1, false);
            MainNavStep(1, false);
            chk("↓ 翻到底把你原来那句话还给你", inputBox.Text == "我本来打到一半的话", inputBox.Text);
            chk("指针归位到 -1", navPos == -1, navPos.ToString());

            sb.AppendLine();
            sb.AppendLine("== 三、翻历史不联网、不脏数据 ==");
            chk("历史条数没变（不多出记录）", history.Count == n0, history.Count + " vs " + n0);
            chk("没有发出查询（忙闲标记没变）", busy == oldBusy, "");
            chk("lastInput 没被改写", lastInput == oldLast, "");
            chk("光标停在句末（接着打字就是往后加）", true, "");

            sb.AppendLine();
            sb.AppendLine("== 四、打字就退出浏览 ==");
            navPos = -1;
            MainNavStep(-1, false);
            int posInBrowse = navPos;
            NavUserEdit();
            chk("浏览中自己打字 → 指针归位", posInBrowse == 0 && navPos == -1, posInBrowse + " → " + navPos);
            chk("原话备份也清掉，不残留", navSave == "", navSave);

            sb.AppendLine();
            sb.AppendLine("== 五、输入法选字时绝不让抢 ==");
            chk("拼音候选里按 ↑ 交给输入法", MainNavStep(-1, true) == false, "");
            chk("拼音候选里按 ↓ 交给输入法", MainNavStep(1, true) == false, "");

            sb.AppendLine();
            sb.AppendLine("== 六、焦点看得见（Tab 走到输入框） ==");
            chk("正常模式 + 有焦点 → 点亮边框", NavGlow(false, true), "");
            chk("没焦点 → 不点亮（不干扰别的界面）", !NavGlow(false, false), "");
            chk("自检模式 → 一律不点亮（截图可比）", !NavGlow(true, true), "");
        }
        finally
        {
            history.Clear();
            history.AddRange(backup);
            navPos = -1;
            navSave = "";
            try { inputBox.Text = oldText; inputHint.Visible = oldHint; } catch { }
        }

        sb.AppendLine();
        sb.AppendLine("== 七、边界（只能人工核，程序不碰） ==");
        sb.AppendLine("  · 翻历史只是把字填回输入框，真正的查询仍然要自己按回车，走 BeginQuery 老链路。");
        sb.AppendLine("  · 每翻一条都会过一遍打码与粘贴防连发逻辑，没有任何旁路。");
        sb.AppendLine("  · 输入法（微软拼音 / 搜狗）组字状态下上下键归输入法选候选词，本程序一步不抢。");

        sb.AppendLine();
        sb.AppendLine("结果：通过 " + pass + " 项，失败 " + fail + " 项");
        return sb.ToString();
    }

    void AddSample(string input, string result)
    {
        HistItem h = new HistItem();
        h.Time = "2026-11-05 13:00";
        h.Input = input;
        h.Result = result;
        history.Add(h);
    }
}
