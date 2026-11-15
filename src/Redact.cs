// ============================================================================
//  翻译器 · Redact.cs（第二档 ⑪ 发出去之前先打码）
//  ---------------------------------------------------------------------------
//  要解决的事：做线上客服，对话里全是手机号、订单号、地址、邮箱。
//  现在这些是原样发给 DeepSeek 的，靠「别发订单号」根本规避不了。
//
//  做法：发请求之前，把「像隐私的东西」换成 [[M1]] [[O2]] 这种占位记号；
//        AI 回来看起来是译好的句子，但号码位置还是记号；回到本机再换回真号码。
//        · ①原来那串东西会整句翻译一次 → 号码被打散、还可能被翻错，答非所问
//        · ②真号码根本没必要出网，出网就有痕迹
//
//  红线（都是明确约定过的）：
//    · 不去监听整个系统的复制动作（最容易被杀软盯上，还会把密码抓进来）
//    · 对照表只放内存，不写硬盘、不进历史、不进缓存
//    · 界面主结构一个字不动：只在结果框下面那行提示里多几个字 + 右键菜单多一项
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

partial class QueryForm
{
    // 一次查询的打码对照表（只在内存里活这一次）
    internal class RedactMap
    {
        public List<string[]> Pairs = new List<string[]>();   // 每行：{ 记号, 真值, 类别名 }
        public int Count { get { return Pairs.Count; } }
        public bool Empty { get { return Pairs.Count == 0; } }
    }

    // 最近一次查询的对照表；换一次查询就整张换掉，不攒、不落盘
    RedactMap lastRedact = new RedactMap();

    // ---- 认「像隐私的东西」的四条规则（顺序要紧：长的先认，别被短规则切碎） ----
    static readonly Regex RxIdCard = new Regex(@"(?<![0-9A-Za-z])\d{17}[\dXx](?![0-9A-Za-z])", RegexOptions.Compiled);
    static readonly Regex RxPhone = new Regex(@"(?<!\d)(?:\+?86[-\s]?)?1[3-9]\d{9}(?!\d)", RegexOptions.Compiled);
    static readonly Regex RxMail = new Regex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);
    static readonly Regex RxLongNum = new Regex(@"(?<!\d)\d{8,}(?!\d)", RegexOptions.Compiled);

    // 还原时认记号：[[M1]] 是标准写法，但 AI 有时候会改成 【M1】/ 〔M1〕 / [ M1 ]，都认
    static readonly Regex RxMark = new Regex(@"[\[{【〔]{1,2}\s*([MOIE])\s*(\d+)\s*[\]}】〕]{1,2}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 打码时顺带给 AI 的一句叮嘱：记号别动
    internal const string MarkRule = "原文里形如 [[M1]] [[O2]] 的方括号记号是占位符（M=手机号 O=订单号 I=证件号 E=邮箱），"
        + "必须原样照抄、不许翻译、不许改写、不许加空格或去掉任意一个方括号。";

    // ---------------- 打码 ----------------
    internal static RedactMap RedactText(string text, out string masked)
    {
        RedactMap m = new RedactMap();
        masked = text == null ? "" : text;
        if (masked.Length == 0) return m;
        Dictionary<string, int> seqs = new Dictionary<string, int>();
        masked = MaskBy(masked, RxIdCard, "I", "身份证", m, seqs);
        masked = MaskBy(masked, RxPhone, "M", "手机号", m, seqs);
        masked = MaskBy(masked, RxMail, "E", "邮箱", m, seqs);
        masked = MaskBy(masked, RxLongNum, "O", "订单/长数字", m, seqs);
        return m;
    }

    static string MaskBy(string src, Regex rx, string tag, string label, RedactMap m, Dictionary<string, int> seqs)
    {
        return rx.Replace(src, delegate (Match mt)
        {
            string real = mt.Value;
            int n;
            seqs.TryGetValue(tag, out n);
            n++;
            seqs[tag] = n;
            string mark = "[[" + tag + n + "]]";
            m.Pairs.Add(new string[] { mark, real, label });
            return mark;
        });
    }

    // ---------------- 还原 ----------------
    // left = 有几个记号没能换回真号（AI 把记号改得面目全非时会这样，如实报给用户）
    internal static string RedactRestore(string text, RedactMap map, out int left)
    {
        left = 0;
        if (map == null || map.Empty || string.IsNullOrEmpty(text)) return text;
        string outp = text;
        for (int i = 0; i < map.Pairs.Count; i++)
        {
            string mark = map.Pairs[i][0];                 // 例如 [[M1]]
            string real = map.Pairs[i][1].Replace("$", "$$");
            string inner = mark.Substring(2, mark.Length - 4);   // 例如 M1
            Regex loose = new Regex(@"[\[{【〔]{1,2}\s*" + inner + @"\s*[\]}】〕]{1,2}", RegexOptions.IgnoreCase);
            if (loose.IsMatch(outp)) outp = loose.Replace(outp, real);
            else left++;
        }
        return outp;
    }

    // 结果区那行提示里要显示的一句话（没有打码就不打扰）
    internal string RedactTip(string tail, int leftMarks)
    {
        if (lastRedact == null || lastRedact.Empty) return tail;
        string head = "已打码 " + lastRedact.Count + " 处（右键可看对照）";
        if (leftMarks > 0) head = "打码 " + lastRedact.Count + " 处，其中 " + leftMarks + " 处没换回来";
        return head + "｜" + tail;
    }

    // 对照文本：只给用户看，不进请求、不落盘
    internal string DescribeRedact(RedactMap m)
    {
        if (m == null || m.Empty) return "这次没有需要打码的内容（手机号 / 订单号 / 证件号 / 邮箱都没出现）。\r\n";
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("这次发出去之前，把下面 " + m.Count + " 处换成了记号：");
        sb.AppendLine();
        for (int i = 0; i < m.Pairs.Count; i++)
            sb.AppendLine("  " + m.Pairs[i][0] + "  ←  " + m.Pairs[i][2] + "：" + m.Pairs[i][1]);
        sb.AppendLine();
        sb.AppendLine("真号码只在你这台机器上换回来，没有写进硬盘，也没进历史和缓存。");
        return sb.ToString();
    }

    // ---------------- 右键菜单：看这次打码了什么 ----------------
    internal ToolStripMenuItem RedactMenuItem()
    {
        ToolStripMenuItem it = new ToolStripMenuItem("看这次打码了什么");
        it.Click += delegate (object s, EventArgs e) { ShowRedactSheet(); };
        return it;
    }

    void ShowRedactSheet()
    {
        RedactSheetForm f = new RedactSheetForm(DescribeRedact(lastRedact));
        try { f.ShowDialog(this); } catch { }
    }

    // 只读对照小窗（跟程序里其它小窗一样的手感：圆角 + 同一套配色）
    internal class RedactSheetForm : Form
    {
        public RedactSheetForm(string body)
        {
            Text = "这次打码了什么";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(460, 300);
            BackColor = Color.White;

            TextBox t = new TextBox();
            t.Multiline = true;
            t.ReadOnly = true;
            t.ScrollBars = ScrollBars.Vertical;
            t.BorderStyle = BorderStyle.FixedSingle;
            t.Font = new Font("Microsoft YaHei UI", 9f);
            t.Location = new Point(16, 16);
            t.Size = new Size(428, 220);
            t.Text = body.Replace("\r\n", "\r\n");
            t.Tag = "对照";
            Controls.Add(t);

            Button ok = new Button();
            ok.Text = "知道了";
            ok.DialogResult = DialogResult.OK;
            ok.Location = new Point(364, 250);
            ok.Size = new Size(80, 30);
            ok.Font = new Font("Microsoft YaHei UI", 9f);
            Controls.Add(ok);
            AcceptButton = ok; CancelButton = ok;
        }
    }

    // ---------------- 自检：--redactcheck ----------------
    // 老老实实跑几组样本：该打码的打到、不该动的别动、回来能一字不差换回真号
    internal string RedactSelfTest()
    {
        StringBuilder sb = new StringBuilder();
        int pass = 0, fail = 0;
        Action<string, bool, string> chk = delegate (string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            sb.AppendLine((ok ? "  [OK]   " : "  [FAIL] ") + name + (string.IsNullOrEmpty(detail) ? "" : "  → " + detail));
        };

        // ① 客服真实场景那一句
        string s1 = "麻烦您把订单号 2026110500123456 报一下，我这边登记，您的手机 13812345678 对吗？邮箱 chen@example.com";
        string m1;
        RedactMap r1 = RedactText(s1, out m1);
        sb.AppendLine("== 样本一（客服真实句子）==");
        sb.AppendLine("  原文：" + s1);
        sb.AppendLine("  发出：" + m1);
        sb.AppendLine();
        chk("长订单号被打码", m1.IndexOf("2026110500123456") < 0 && m1.IndexOf("[[O") >= 0, "");
        chk("手机号被打码", m1.IndexOf("13812345678") < 0 && m1.IndexOf("[[M") >= 0, "");
        chk("邮箱被打码", m1.IndexOf("chen@example.com") < 0 && m1.IndexOf("[[E") >= 0, "");
        chk("共打码 3 处", r1.Count == 3, "实际 " + r1.Count);

        // ② 还原要一字不差（模拟 AI 回一句英文，记号照抄）
        string fake = "Please tell me your order [[O1]], and is your phone [[M1]] correct? Mail [[E1]] too.";
        int left1;
        string back1 = RedactRestore(fake, r1, out left1);
        sb.AppendLine("== 样本二（AI 回来的译文，记号照抄）==");
        sb.AppendLine("  回来：" + fake);
        sb.AppendLine("  还原：" + back1);
        sb.AppendLine();
        chk("订单号换回真号", back1.IndexOf("2026110500123456") >= 0, "");
        chk("手机号换回真号", back1.IndexOf("13812345678") >= 0, "");
        chk("邮箱换回真号", back1.IndexOf("chen@example.com") >= 0, "");
        chk("没有漏还原的记号", left1 == 0, "漏 " + left1);
        chk("记号本身没留在译文里", back1.IndexOf("[[") < 0, "");

        // ③ AI 把记号改写成中文括号 / 中间加空格，也得能换回来
        string fake2 = "订单【O1】、手机〔 M1 〕、邮箱 [ E1 ]";
        int left2;
        string back2 = RedactRestore(fake2, r1, out left2);
        chk("AI 改了括号写法也能还原", back2.IndexOf("2026110500123456") >= 0 && back2.IndexOf("13812345678") >= 0 && left2 == 0, back2);

        // ④ 不该动的别动：普通句子、年份、短数字、单词
        string s4 = "这个价格是含配送的吗？2026 年 11 月 5 号，第 3 次联系，共 128 元。";
        string m4;
        RedactMap r4 = RedactText(s4, out m4);
        sb.AppendLine("== 样本四（普通句子别乱打码）==");
        sb.AppendLine("  原文：" + s4);
        sb.AppendLine("  发出：" + m4);
        sb.AppendLine();
        chk("普通句子零改动", m4 == s4, m4 == s4 ? "" : m4);
        chk("普通句子零计数", r4.Count == 0, "实际 " + r4.Count);

        // ⑤ 证件号 / 带 +86 的手机 / 纯英文句子
        string s5 = "身份证 350102199001011234，国际号 +86 13912345678，please check the status of my refund.";
        string m5;
        RedactMap r5 = RedactText(s5, out m5);
        sb.AppendLine("== 样本五（证件号 / 带 +86 / 纯英文）==");
        sb.AppendLine("  原文：" + s5);
        sb.AppendLine("  发出：" + m5);
        sb.AppendLine();
        chk("身份证被打码", m5.IndexOf("350102199001011234") < 0, "");
        chk("带 +86 的手机整体打码", m5.IndexOf("13912345678") < 0 && m5.IndexOf("+86") < 0, m5);
        chk("纯英文句子没被误伤", m5.IndexOf("refund") >= 0, "");

        // ⑥ 对照表就是一串文字，别把真号写进任何落盘的地方：这里只校验它能生成
        string sheet = DescribeRedact(r1);
        chk("能生成对照文本", sheet.IndexOf("13812345678") >= 0, "");
        chk("对照文本说明了不落盘", sheet.IndexOf("没有写进硬盘") >= 0, "");
        sb.AppendLine("== 对照文本 ==");
        sb.AppendLine(sheet.Replace("\r\n", "\r\n  "));
        sb.AppendLine();
        sb.AppendLine("结果：通过 " + pass + " 项，失败 " + fail + " 项");
        return sb.ToString();
    }
}
