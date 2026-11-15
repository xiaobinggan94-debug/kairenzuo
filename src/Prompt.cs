// ============================================================================
//  饼饼专属中英互译 · Prompt.cs（Prompt）
//  提示词：系统提示词正文、提示词拼装、近音/缩写展开规则、没配 Key 时的提示文案。
//  想调翻译风格、加小节标题、改语气规则，改这个文件就够了。
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
    // 没配置 key 时给用户看的提示（纯中文，照着做就能用）
    // v2.5.9 第一档①：先说「点右键就能填」，把手工建文件降成备选——原来只有四步手工，
    // 对着不熟电脑的人来说比翻译本身还难。
    static string MissingKeyText()
    {
        return "还没配置 API Key，一行都不用自己建：\n\n"
            + "1. 在任务栏右下角（时钟旁边）的小饼干图标上点右键\n\n"
            + "2. 点「配置 API Key（换 Key 也在这儿）」\n\n"
            + "3. 把 Key 粘进去，点「测一下」看看通不通，通了就点「保存」\n\n"
            + "4. 关掉那个小窗，回来点「查一下」\n\n"
            + "（也可以手工放：把 apikey.txt 放进这个文件夹，只写一行 sk- 开头的那种：\n" + LOG_DIR + "）\n\n"
            + "apikey.txt 只存在你自己的电脑上，不会上传到 GitHub。";
    }
    static string SYSTEM_PROMPT = string.Join("\n", new string[] {
        "你是中英双向速查助手，服务一位中文母语、英语基础较弱（约小学水平）的学习者，她主要在电脑上和外国朋友在线聊天。",
        "",
        "先判断输入语言，只输出对应段落，不要开场白、不要总结、不要复述原文。",
        "",
        "【输入是英文】输出四段，不要写【缩写版】：",
        "【中文意思】自然的中文，一句说完，不要逐字硬翻。",
        "【生词】挑她最可能不认识的词，每个一行：单词 | 音标 | 意思 | 近音：汉字。最多 3 个、每个不超过 12 字；没有就写「没有」。",
        "【你可以这样回】两条能直接复制发出去的英文，每条不超过 12 个词，后面用括号写中文意思。",
        "【注意】只在该提醒时说一句：这句能不能对不熟的人说、是不是年轻人常用、有没有过时。没有就写「没有」。",
        "",
        "【输入是中文】输出三段：",
        "【英文】一句自然口语的英文。",
        "【音标】挑关键词给音标，最多 4 个词，用「 · 」分隔；每个词后面接「近音：汉字」，例如 swamped /swɒmpt/ 近音：斯万普特。",
        "【缩写版】写三档，每档单独占一行、依次写三行，格式严格为「轻度 | 正常缩一点的句子（中文）」「中度 | 更短的句子（中文）」「重度 | 最狠的缩写、像老外打字那样（中文）」，行与行之间直接换行，不要用｜把三档黏在一行。哪一档不存在、或你不确定真实有这种缩写，就只写「没有」三个字，绝对不要为了凑齐三档硬编造缩写。",
        "",
        "通用规则：只要写中文近音，必须写成「近音：汉字」这个固定样子（冒号后面只放汉字，不夹英文字母）；不要 emoji；不确定的缩写不要硬造；全程 280 字以内，她在聊天间隙看，要短要快。",
        "",
        "【称呼与性别（必须遵守）】",
        "1. 凡是讲解释、点评、【注意】【你可以这样回】里的中文说明，一律用「对方」，不许写「他」「她」「TA」，也不许猜对方是男是女。",
        "2. 如果原文里出现了名字，优先直接叫名字。",
        "3. 同一段里第二次提到，改用「这句」「这话」「这条」指代，不要重复「对方」。",
        "4. 但译文正文必须忠实原文：原文 he 就译「他」，she 就译「她」，they 就译「他们」，不许替原文改性别。",
        "5. 中文翻英文时，指代不明（没说是男是女）默认用 they。"
    });

    // ------------------------------------------------------------------
    // 语法讲解（方案 A）：结果块最后多一段【语法】，讲这句话为什么这么说。
    // 关掉时提示词里连「语法」两个字都不出现，跟上一版逐字一致（--gramcheck 会验）。
    // 基线提示词 SYSTEM_PROMPT 一个字都没改：语法规则是「追加块 + 覆盖段数」，
    // 所以关掉就等于没这功能，不用回滚代码。
    // ------------------------------------------------------------------
    internal static bool GrammarOn { get { return GetSet("grammar", "1") == "1"; } }

    static string GrammarRuleText()
    {
        return string.Join("\n", new string[] {
            "",
            "【补充：这次要多给一段【语法】，段数按下文覆盖上文的说法】",
            "输入是英文 → 五段：中文意思、生词、你可以这样回、注意、语法。",
            "输入是中文 → 四段：英文、音标、缩写版、语法。",
            "",
            "【语法】放在最后，讲这句话的语言结构。只讲 1 条；一句话里确实有两个独立考点才讲 2 条，不许凑数。",
            "每条严格按下面五行写，第 2 到第 5 行行首两个全角空格，不许合并成一行：",
            "1. 正名：中文语法名（英文原名）",
            "　　构成：结构公式",
            "　　为什么非它不可：换别的会错在哪",
            "　　对照：先说另一种说法，再说差在哪（照这个顺序，不要出现加号、竖线这类符号）",
            "　　依据：原文里抠出来的词",
            "",
            "术语不设限：从句、非谓语、倒装、虚拟、独立主格、完成进行、悬垂结构，该叫什么就叫什么，不许因为她英语弱就换成大白话糊过去。",
            "英文原名必须给，她要能自己搜得到、能自己核对。",
            "第一行必须以「1. 正名：」开头，中文翻英文时也一样，「正名：」这三个字任何情况下都不许省；",
            "「依据」那行只把原文里抠出来的词贴上去，不要在旁边解释，字数让给前面几行。",
            "四条铁律，违反就算讲错：",
            "① 名字必须和结构严格对应（have been doing 只能是现在完成进行时），不许张冠李戴；",
            "② 一句同时符合两个名字时选更具体那个，不许并列两个名字糊过去；",
            "③ 原文里没有的成分不许编，没有从句就不许说这里省略了从句；",
            "④ 真拿不准（口语省略、网络缩写、非标准写法），直接写「这是口语省略，不算标准结构」，不许硬起一个学术名充数。",
            "输入是中文时，讲的是「这个中文意思，英文为什么选这个结构、这个时态、这个情态词」，并给一句换成别的说法会差在哪。",
            "",
            "开头说的 280 字只算除【语法】以外的段落；【语法】单独不超过 110 字（五行结构一行都不许省，靠压字数而不是砍行）；全篇不超过 390 字。",
            "单个词、两三个词的短语、纯名词或纯感叹（ok / good morning / nice weather today），或简单主谓句没什么可讲的，【语法】只写「没有」三个字，绝对不许硬凑。"
        });
    }

    static string BuildSystemPrompt()
    {
        string t = GetSet("tone", "auto");
        string p = SYSTEM_PROMPT;
        if (t == "formal")
            p = p + "\n\n【这次的语气】一律给正式、书面一点的表达：完整句子、不用网络缩写，适合对外沟通或工作场合。";
        else if (t == "casual")
            p = p + "\n\n【这次的语气】一律给日常口语的表达：像朋友聊天，可以用常见缩写和轻松说法（gonna、kinda 这类少用，别过头）。";
        if (GrammarOn) p = p + "\n" + GrammarRuleText();
        return p;
    }

    // ------------------------------------------------------------------
    // 称呼铁律的代码兜底：讲解/点评段落里的「他 / 她 / TA」统一改成「对方」。
    // 只动【注意】和【你可以这样回】里的中文说明，译文正文（【英文】【中文意思】）一个字都不碰，
    // 因为译文要忠实原文的性别。prompt 已经写死了这条规则，这里只是防止模型偶尔不听话。
    // ------------------------------------------------------------------
    static string GuardPronoun(string s)
    {
        try
        {
            if (string.IsNullOrEmpty(s)) return s;
            int i = 0;
            StringBuilder sb = new StringBuilder();
            while (i < s.Length)
            {
                int st = s.IndexOf('【', i);
                if (st < 0) { sb.Append(s.Substring(i)); break; }
                sb.Append(s.Substring(i, st - i));
                int en = s.IndexOf('】', st + 1);
                if (en < 0) { sb.Append(s.Substring(st)); break; }
                string title = s.Substring(st + 1, en - st - 1).Trim();
                int next = s.IndexOf('【', en + 1);
                int segEnd = (next < 0) ? s.Length : next;
                string seg = s.Substring(en + 1, segEnd - en - 1);

                if (title == "注意")
                    seg = ReplacePron(seg);
                else if (title.IndexOf("语法") >= 0)
                    seg = ReplacePron(seg);   // 语法讲解举例时也会出现「他/她」，跟点评段一样统一成「对方」
                else if (title == "你可以这样回")
                    seg = Regex.Replace(seg, @"[（(]([^（）()]*)[）)]",
                        delegate(Match m) { return "（" + ReplacePron(m.Groups[1].Value) + "）"; });

                sb.Append('【').Append(s.Substring(st + 1, en - st - 1)).Append('】').Append(seg);
                i = segEnd;
            }
            return sb.ToString();
        }
        catch { return s; }
    }

    static string ReplacePron(string x)
    {
        if (string.IsNullOrEmpty(x)) return x;
        // 「其他」不是人称代词，先摘出去防止被误伤
        x = x.Replace("其他", "\u0001").Replace("其她", "\u0003").Replace("其中", "\u0002");
        x = Regex.Replace(x, @"(?<![A-Za-z])[Tt][Aa](?![A-Za-z])", "对方");
        x = x.Replace("他们", "对方").Replace("她们", "对方");
        x = x.Replace("他", "对方").Replace("她", "对方");
        x = x.Replace("\u0001", "其他").Replace("\u0002", "其中").Replace("\u0003", "其她");
        // 「TA 通常」换成「对方」后留着空格很难看，收掉
        x = Regex.Replace(x, "对方[ \u00A0]+", "对方");
        return x;
    }

    // ------------------------------------------------------------------
    // 自检：--pronouncheck。称呼铁律的代码兜底有没有真的生效，跑这个看结果。
    // ------------------------------------------------------------------
    internal static void PronounCheck(string outPath)
    {
        StringBuilder sb = new StringBuilder();
        try
        {
            sb.AppendLine("称呼铁律自检（代码兜底 GuardPronoun）");
            sb.AppendLine();

            // 1. 点评段里不许出现他/她/TA
            string caseA = "【注意】他很在意这话，你要当心；她说没事也不一定真没事，TA 通常忍着。其他时候随便聊。";
            string rA = GuardPronoun(caseA);
            string rA2 = rA.Replace("其他", "");   // 「其他」里的「他」不算人称代词
            bool okA = rA2.IndexOf("他") < 0 && rA2.IndexOf("她") < 0 && rA.IndexOf("TA") < 0 && rA.IndexOf("对方") > 0 && rA.IndexOf("其他时候") > 0 && rA.IndexOf("对方 ") < 0;
            sb.AppendLine("[A·点评段改称呼] " + (okA ? "通过" : "失败"));
            sb.AppendLine("   原=" + caseA);
            sb.AppendLine("   后=" + rA);

            // 2. 【你可以这样回】只动括号里的中文说明，英文原句一个字不改
            string caseB = "【你可以这样回】\nSure, take your time.\uff08他会理解的\uff09\nSounds good to me.\uff08她要是方便就这个点\uff09";
            string rB = GuardPronoun(caseB);
            bool okB = rB.Contains("Sure, take your time.") && rB.Contains("Sounds good to me.")
                && rB.Contains("\uff08对方会理解的\uff09") && rB.IndexOf("他会") < 0 && rB.IndexOf("她要是") < 0;
            sb.AppendLine("[B·回复括号内改称呼] " + (okB ? "通过" : "失败"));
            sb.AppendLine("   后=" + rB.Replace("\n", " / "));

            // 3. 译文正文必须忠实原文，一个他/她都不许动
            string caseC = "【中文意思】他说他很忙，让她晚点再打过来。\n【英文】He said he was busy, so he asked her to call later.";
            string rC = GuardPronoun(caseC);
            bool okC = rC == caseC;
            sb.AppendLine("[C·译文正文不动] " + (okC ? "通过" : "失败"));
            sb.AppendLine("   后=" + rC.Replace("\n", " / "));

            // 4. 英文单词里的 ta 不能被误伤（data / take 之类）
            string caseD = "【注意】This is data about take-away, TA 说的那种别学。";
            string rD = GuardPronoun(caseD);
            bool okD = rD.Contains("data") && rD.Contains("take-away") && rD.Contains("对方说的那种");
            sb.AppendLine("[D·英文单词不误伤] " + (okD ? "通过" : "失败"));
            sb.AppendLine("   后=" + rD);

            // 5. 提示词里确实带上了这条规则（不是只靠代码兜底）
            bool okE = BuildSystemPrompt().Contains("称呼与性别") && BuildSystemPrompt().Contains("一律用「对方」") && BuildSystemPrompt().Contains("默认用 they");
            sb.AppendLine("[E·提示词已写入] " + (okE ? "通过" : "失败"));
            sb.AppendLine();

            bool all = okA && okB && okC && okD && okE;
            sb.AppendLine(all ? "结论：五条全过，称呼铁律生效。" : "结论：有失败项，见上。");
        }
        catch (Exception ex)
        {
            sb.AppendLine("自检异常: " + ex.GetType().Name + " " + ex.Message);
        }
        finally
        {
            try { File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true)); } catch { }
        }
    }

    // 结果里的【小节】标题加粗上色，长文本更好扫
    // 把「缩写版」里的三档拆成三行，样式跟生词一致：轻度 | 句子
    static string NormalizeAbbrev(string s)
    {
        try
        {
            int i = s.IndexOf("【缩写版】");
            if (i < 0) return s;
            int start = i + "【缩写版】".Length;
            int end = s.IndexOf('【', start);
            if (end < 0) end = s.Length;
            string seg = s.Substring(start, end - start).Replace("\r", "").Replace("\n", " ").Trim();
            if (seg.Length == 0) return s;
            int pm = seg.IndexOf("轻度");
            int pd = seg.IndexOf("中度");
            int ph = seg.IndexOf("重度");
            if (pm < 0 || pd < 0 || ph < 0 || !(pm < pd && pd < ph)) return s;
            string a = seg.Substring(pm + 2, pd - pm - 2);
            string b = seg.Substring(pd + 2, ph - pd - 2);
            string d = seg.Substring(ph + 2);
            Func<string, string> cl = delegate(string x)
            {
                x = x.Trim().TrimStart('｜', '|', ':', '：', ' ').TrimEnd('｜', '|', ' ', '。');
                x = x.Trim();
                if (x.Length == 0 || x == "无" || x == "没有" || x == "無") return "没有";
                return x;
            };
            StringBuilder sb = new StringBuilder();
            sb.Append("\n轻度 | ").Append(cl(a)).Append("\n");
            sb.Append("中度 | ").Append(cl(b)).Append("\n");
            sb.Append("重度 | ").Append(cl(d)).Append("\n");
            return s.Substring(0, start) + sb.ToString() + s.Substring(end);
        }
        catch { return s; }
    }

    // ------------------------------------------------------------------
    // v3 第 4 步：英文进来时【缩写版】整块不显示。
    // 她是拿别人发来的英文来查，只要【中文意思】【生词】【你可以这样回】【注意】四段，
    // 三档缩写对她没用还占地方。中文翻英文时三档照旧保留。
    // ------------------------------------------------------------------
    // 这次查的是不是英文：输入非空、且一个汉字都没有。
    static bool EnglishInputNow()
    {
        return lastInput != null && lastInput.Trim().Length > 0 && !HasChinese(lastInput);
    }

    // 把【缩写版】这一块（标题 + 后面的三档内容）整段删掉，
    // 顺带收掉留下的空标题、连续空行，避免出现「【注意】前面空一行」这种难看的样子。
    // 流式过程中标题刚出现、后面还没写完的情况也按「删到结尾」处理，不会残留半截。
    static string StripAbbrevSection(string s)
    {
        try
        {
            if (string.IsNullOrEmpty(s)) return s;
            // 标题写成【缩写版】/【缩写】都算，模型偶尔会自作主张省字
            Match m = Regex.Match(s, @"[ \t]*【\s*缩写(版)?\s*】");
            if (!m.Success) return s;
            int i = m.Index;
            int end = s.IndexOf('【', i + m.Length);
            if (end < 0) s = s.Substring(0, i);
            else s = s.Substring(0, i) + s.Substring(end);
            // 行尾空白、连续空行收一收
            s = Regex.Replace(s, @"[ \t]+(\r?\n)", "$1");
            s = Regex.Replace(s, @"(\r?\n){3,}", "\n\n");
            s = s.TrimEnd('\r', '\n', ' ', '\t');
            return s;
        }
        catch { return s; }
    }

    // 英文输入时统一走这一层：先砍【缩写版】，再交给 NormalizeAbbrev 做三档拆行
    static string AbbrevGate(string s)
    {
        if (EnglishInputNow()) return StripAbbrevSection(s);
        return s;
    }

    // ------------------------------------------------------------------
    // 语法讲解的滤网：开关关掉时，把【语法】整块砍掉。
    // 提示词已经不加这条规则了，但模型偶尔会自己多写一段（照老习惯），
    // 所以关掉之后必须由代码兜底，保证跟上一版逐字一致。
    // 这里也顺手给导出用：导出文件默认不带语法讲解，看着干净。
    // ------------------------------------------------------------------
    static string StripGrammarSection(string s)
    {
        try
        {
            if (string.IsNullOrEmpty(s)) return s;
            // 标题写成【语法】/【语法点】/【语法结构】都算，模型会自作主张换字
            Match m = Regex.Match(s, @"[ \t]*【\s*语法[^】]*】");
            if (!m.Success) return s;
            int i = m.Index;
            int end = s.IndexOf('【', i + m.Length);
            if (end < 0) s = s.Substring(0, i);
            else s = s.Substring(0, i) + s.Substring(end);
            s = Regex.Replace(s, @"[ \t]+(\r?\n)", "$1");
            s = Regex.Replace(s, @"(\r?\n){3,}", "\n\n");
            s = s.TrimEnd('\r', '\n', ' ', '\t');
            return s;
        }
        catch { return s; }
    }

    // 模型偶尔会把「正名：」三个字省掉（中文翻英文时尤其容易，实测出现过
    // 「1. 现在完成进行时（Present Perfect Continuous）」这种半对的样子），
    // 这里做一次字符级修补：只动【语法】块的第一行，别的一个字不碰。
    static string NormalizeGrammarSection(string s)
    {
        try
        {
            if (string.IsNullOrEmpty(s)) return s;
            Match m = Regex.Match(s, @"【\s*语法[^】]*】");
            if (!m.Success) return s;
            int start = m.Index + m.Length;
            int end = s.IndexOf('【', start);
            if (end < 0) end = s.Length;
            string block = s.Substring(start, end - start);
            string[] lines = Regex.Split(block, "\r?\n");
            bool changed = false;
            for (int k = 0; k < lines.Length; k++)
            {
                string t = lines[k].Trim();
                if (t.Length == 0) continue;
                if (t.IndexOf("正名", StringComparison.Ordinal) >= 0) break;   // 已经写对了
                if (t == "没有" || t == "无" || t == "沒有") break;           // 干脆没考点
                Match num = Regex.Match(t, @"^([0-9]+)[.、．]\s*");
                string body = num.Success ? t.Substring(num.Length).Trim() : t;
                string head = num.Success ? num.Groups[1].Value + ". " : "1. ";
                lines[k] = head + "正名：" + body;
                changed = true;
                break;
            }
            if (!changed) return s;
            string nl = block.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            return s.Substring(0, start) + string.Join(nl, lines) + s.Substring(end);
        }
        catch { return s; }
    }

    // 结果渲染、流式画画都从这儿过：开关关 → 砍；开关开 → 修补「正名：」后放行
    static string GrammarGate(string s)
    {
        if (!GrammarOn) return StripGrammarSection(s);
        return NormalizeGrammarSection(s);
    }

    // ------------------------------------------------------------------
    // 自检：--abbrevcheck。英文进来不显示缩写版、中文进来三档保留，跑这个看结果。
    // ------------------------------------------------------------------
    internal static void AbbrevCheck(string outPath)
    {
        StringBuilder sb = new StringBuilder();
        try
        {
            sb.AppendLine("缩写版按输入语言分流自检（AbbrevGate / StripAbbrevSection）");
            sb.AppendLine();

            string en = "【中文意思】今天忙得不可开交。\n【生词】swamped | /swɒmpt/ | 忙得不可开交 | 近音：斯万普特\n【你可以这样回】I'm swamped today. (我今天忙死了)\n【缩写版】\n轻度 | I'm swamped today.\n中度 | Swamped today.\n重度 | 没有\n【注意】没有";
            string cn = "【英文】I'm swamped today.\n【音标】swamped /swɒmpt/ 近音：斯万普特\n【缩写版】\n轻度 | 我今天忙死了\n中度 | 今天忙死\n重度 | 忙死\n";
            string zhIn = "我今天忙死了";

            // 1. 英文输入：缩写版整块消失，其余四段一段不少
            string before = lastInput;
            lastInput = "I'm swamped today, can we talk later?";
            string r1 = AbbrevGate(en);
            bool okA = r1.IndexOf("缩写版") < 0 && r1.IndexOf("轻度") < 0 && r1.IndexOf("中度") < 0 && r1.IndexOf("重度") < 0
                && r1.IndexOf("【中文意思】") >= 0 && r1.IndexOf("【生词】") >= 0 && r1.IndexOf("【你可以这样回】") >= 0 && r1.IndexOf("【注意】") >= 0;
            sb.AppendLine("[A·英文输入不显示缩写版] " + (okA ? "通过" : "失败"));
            sb.AppendLine("   后=" + r1.Replace("\n", " / "));

            // 2. 不留空标题、不留三连空行
            bool okB = r1.IndexOf("【缩写版】") < 0 && Regex.Matches(r1, @"(\r?\n){3,}").Count == 0
                && !Regex.IsMatch(r1, @"【\s*】") && !r1.Contains("\n\n【注意】");
            sb.AppendLine("[B·无空标题与多余空行] " + (okB ? "通过" : "失败"));
            sb.AppendLine("   后=" + r1.Replace("\n", " / "));

            // 3. 中文输入：三档照旧，一段不少
            lastInput = zhIn;
            string r3 = AbbrevGate(cn);
            bool okC = r3 == cn && r3.IndexOf("【缩写版】") >= 0 && r3.IndexOf("轻度") >= 0 && r3.IndexOf("中度") >= 0 && r3.IndexOf("重度") >= 0;
            sb.AppendLine("[C·中文输入三档保留] " + (okC ? "通过" : "失败"));
            sb.AppendLine("   后=" + r3.Replace("\n", " / "));

            // 4. 英文输入后再走 NormalizeAbbrev：没有缩写版时原样返回，不会补出空行
            lastInput = "ok see you then";
            string r4 = NormalizeAbbrev(AbbrevGate(en));
            bool okD = r4.IndexOf("缩写版") < 0 && Regex.Matches(r4, @"(\r?\n){3,}").Count == 0;
            sb.AppendLine("[D·与三档拆行逻辑不打架] " + (okD ? "通过" : "失败"));
            sb.AppendLine("   后=" + r4.Replace("\n", " / "));

            // 5. 提示词里英文段已经不含缩写版、中文段还保留
            string p = BuildSystemPrompt();
            int enPos = p.IndexOf("【输入是英文】");
            int cnPos = p.IndexOf("【输入是中文】");
            string enSeg = (enPos >= 0 && cnPos > enPos) ? p.Substring(enPos, cnPos - enPos) : "";
            string cnSeg = (cnPos >= 0) ? p.Substring(cnPos) : "";
            bool okE = enSeg.Length > 0 && !Regex.IsMatch(enSeg, @"(?m)^【缩写版】") && enSeg.IndexOf("四段") >= 0
                && cnSeg.IndexOf("【缩写版】") >= 0 && cnSeg.IndexOf("三档") >= 0;
            sb.AppendLine("[E·提示词段数已同步] " + (okE ? "通过" : "失败"));
            sb.AppendLine("   英文段=" + enSeg.Replace("\n", " / "));
            sb.AppendLine("   中文段=" + cnSeg.Length + " 字，含【缩写版】=" + (cnSeg.IndexOf("【缩写版】") >= 0));

            // 6. 流式半截（标题刚出现、内容没写完）也要砍干净
            lastInput = "let's catch up tomorrow";
            string half = "【中文意思】明天聊聊吧。\n【缩写版】\n轻度 | let's catch";
            string r6 = AbbrevGate(half);
            bool okF = r6.IndexOf("缩写版") < 0 && r6.IndexOf("轻度") < 0 && r6.IndexOf("【中文意思】") >= 0;
            sb.AppendLine("[F·流式半截不残留] " + (okF ? "通过" : "失败"));
            sb.AppendLine("   后=" + r6.Replace("\n", " / "));

            lastInput = before;
            sb.AppendLine();

            // 7. 模型只写【缩写】不写【缩写版】也要砍掉
            lastInput = "sounds good, see you at six";
            string r7 = AbbrevGate("【中文意思】行，六点见。\n【缩写】\n轻度 | ok see u 6\n【注意】没有");
            bool okG = r7.IndexOf("缩写") < 0 && r7.IndexOf("ok see u 6") < 0 && r7.IndexOf("【注意】") >= 0;
            sb.AppendLine("[G·缩写标题简写也拦] " + (okG ? "通过" : "失败"));
            sb.AppendLine("   后=" + r7.Replace("\n", " / "));

            bool all = okA && okB && okC && okD && okE && okF && okG;
            sb.AppendLine(all ? "结论：七条全过，英文不显示缩写版、中文三档保留。" : "结论：有失败项，见上。");
        }
        catch (Exception ex)
        {
            sb.AppendLine("自检异常: " + ex.GetType().Name + " " + ex.Message);
        }
        finally
        {
            try { File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true)); } catch { }
        }
    }

    // ------------------------------------------------------------------
    // 自检：--gramcheck。语法讲解这一段：开关关掉要跟上一版逐字一致，
    // 打开要多出五段/五行/四条铁律，导出永远不带语法。
    // 全程只改内存里的设置（SetSetQuiet），不碰用户的 settings.ini。
    // ------------------------------------------------------------------
    internal static void GrammarCheck(string outPath)
    {
        StringBuilder sb = new StringBuilder();
        string origSw = GetSet("grammar", "1");
        string origInput = lastInput;
        try
        {
            sb.AppendLine("语法讲解自检（GrammarOn / GrammarRuleText / StripGrammarSection / 导出剔除）");
            sb.AppendLine();

            string en = "【中文意思】我在这儿等了两个钟头。\n"
                + "【生词】waiting | /ˈweɪtɪŋ/ | 等 | 近音：威廷\n"
                + "【你可以这样回】Sorry to keep you waiting. (抱歉让你等)\n"
                + "【注意】没有\n"
                + "【语法】\n"
                + "1. 正名：现在完成进行时（present perfect continuous）\n"
                + "　　构成：have/has been + 现在分词\n"
                + "　　为什么非它不可：从过去一直等到现在还没停，现完只说做完，说不出「还在继续」\n"
                + "　　对照：现在完成时 I have waited，听着像等完了、不等了\n"
                + "　　依据：have been waiting";
            string cn = "【英文】I've been waiting for two hours.\n"
                + "【音标】waiting /ˈweɪtɪŋ/ 近音：威廷\n"
                + "【缩写版】\n轻度 | 我等了两钟头\n中度 | 等了两钟头\n重度 | 等两钟头\n"
                + "【语法】\n"
                + "1. 正名：现在完成进行时（present perfect continuous）\n"
                + "　　构成：have/has been + 现在分词\n"
                + "　　为什么非它不可：中文「等了两个钟头」是持续，英文要用进行态才不显得等完了\n"
                + "　　对照：I have waited for two hours，像等完了；加 been waiting 才是还在等\n"
                + "　　依据：等了两个钟头";

            // A. 关掉开关：语法整块消失，前面的段一段不少
            SetSetQuiet("grammar", "0");
            string r1 = GrammarGate(en);
            bool okA = !GrammarOn && r1.IndexOf("语法") < 0 && r1.IndexOf("present perfect") < 0 && r1.IndexOf("正名") < 0
                && r1.IndexOf("【中文意思】") >= 0 && r1.IndexOf("【生词】") >= 0
                && r1.IndexOf("【你可以这样回】") >= 0 && r1.IndexOf("【注意】") >= 0;
            sb.AppendLine("[A·关掉后语法整块消失] " + (okA ? "通过" : "失败"));
            sb.AppendLine("   后=" + r1.Replace("\n", " / "));

            // B. 关掉后不留空标题、不留三连空行、结尾不留空白
            bool okB = !Regex.IsMatch(r1, @"【\s*】") && Regex.Matches(r1, @"(\r?\n){3,}").Count == 0
                && r1 == r1.TrimEnd() && !r1.EndsWith("\n");
            sb.AppendLine("[B·无空标题与多余空行] " + (okB ? "通过" : "失败"));
            sb.AppendLine("   结尾=" + (r1.Length > 12 ? r1.Substring(r1.Length - 12) : r1).Replace("\n", " / "));

            // C. 关掉后提示词里连「语法」都不出现；语气没动时应当逐字等于基线
            string p0 = BuildSystemPrompt();
            bool toneAuto = GetSet("tone", "auto") == "auto";
            bool okC = p0.IndexOf("语法") < 0 && p0.IndexOf("正名") < 0
                && (!toneAuto || p0 == SYSTEM_PROMPT);
            sb.AppendLine("[C·关掉后提示词与本机基线逐字一致] " + (okC ? "通过" : "失败") + "（语气=" + GetSet("tone", "auto") + "）");
            sb.AppendLine("   基线 " + SYSTEM_PROMPT.Length + " 字，当前 " + p0.Length + " 字");

            // D. 打开：追加块写到点子上（五段 / 五行 / 四条铁律 / 字数 / 英文原名 / 拿不准怎么说）
            SetSetQuiet("grammar", "1");
            string p1 = BuildSystemPrompt();
            bool okD = GrammarOn && p1.Length > p0.Length && p1.StartsWith(p0)
                && p1.IndexOf("五段") >= 0 && p1.IndexOf("四段") >= 0
                && p1.IndexOf("正名") >= 0 && p1.IndexOf("构成") >= 0
                && p1.IndexOf("为什么非它不可") >= 0 && p1.IndexOf("对照") >= 0 && p1.IndexOf("依据") >= 0
                // v2.7.2：语法段字数上限 180 → 110、全篇 450 → 390，这里跟着口径一起走
                && p1.IndexOf("英文原名") >= 0 && p1.IndexOf("110") >= 0 && p1.IndexOf("390") >= 0
                && p1.IndexOf("这是口语省略，不算标准结构") >= 0 && p1.IndexOf("术语不设限") >= 0
                && p1.IndexOf("全角空格") >= 0;
            sb.AppendLine("[D·打开后提示词多出语法段规则] " + (okD ? "通过" : "失败"));
            sb.AppendLine("   追加 " + (p1.Length - p0.Length) + " 字");

            // E. 打开时语法段原样保留，跟「缩写版」滤网、「近音」滤网都不打架
            lastInput = "I have been waiting for two hours.";
            string rEn = StripApprox(AbbrevGate(GrammarGate(en)));
            bool okE = rEn.IndexOf("【语法】") >= 0 && rEn.IndexOf("present perfect") >= 0 && rEn.IndexOf("依据") >= 0
                && rEn.IndexOf("【缩写版】") < 0 && rEn.IndexOf("近音") < 0 && rEn.IndexOf("【生词】") >= 0;
            sb.AppendLine("[E·打开时英文进的语法段保留] " + (okE ? "通过" : "失败"));
            lastInput = "我等了他两个小时";
            string rCn = NormalizeAbbrev(GuardPronoun(GrammarGate(AbbrevGate(cn))));
            bool okF = rCn.IndexOf("【语法】") >= 0 && rCn.IndexOf("【缩写版】") >= 0 && rCn.IndexOf("【英文】") >= 0;
            sb.AppendLine("[F·打开时中文进的语法段保留] " + (okF ? "通过" : "失败"));

            // G. 模型把语法塞在中间（不是最后）：砍掉后前后两段都在，不留大洞
            SetSetQuiet("grammar", "0");
            string mid = "【中文意思】行。\n【语法】\n1. 正名：省略句（ellipsis）\n　　构成：省掉重复成分\n【注意】没有";
            string rMid = GrammarGate(mid);
            bool okG = rMid.IndexOf("语法") < 0 && rMid.IndexOf("省略句") < 0
                && rMid.IndexOf("【中文意思】") >= 0 && rMid.IndexOf("【注意】") >= 0
                && Regex.Matches(rMid, @"(\r?\n){3,}").Count == 0;
            sb.AppendLine("[G·语法被塞在中间也砍干净] " + (okG ? "通过" : "失败"));
            sb.AppendLine("   后=" + rMid.Replace("\n", " / "));

            // H. 流式半截：标题刚冒出来、内容一个字没写，也要立刻砍掉（不能先闪一下）
            string half = "【中文意思】行，六点见。\n【语法】\n1. 正名：现在";
            string rHalf = GrammarGate(half);
            bool okH = rHalf.IndexOf("语法") < 0 && rHalf.IndexOf("正名") < 0 && rHalf.IndexOf("【中文意思】") >= 0;
            sb.AppendLine("[H·流式半截不闪一下] " + (okH ? "通过" : "失败"));
            sb.AppendLine("   后=" + rHalf.Replace("\n", " / "));

            // I. 导出永远不带语法（开关开不开都一样）
            SetSetQuiet("grammar", "1");
            string ex1 = StripGrammarSection(en);
            string ex2 = StripGrammarSection(cn);
            bool okI = ex1.IndexOf("语法") < 0 && ex2.IndexOf("语法") < 0
                && ex1.IndexOf("【注意】") >= 0 && ex2.IndexOf("【缩写版】") >= 0 && ex2.IndexOf("【英文】") >= 0;
            sb.AppendLine("[I·导出文本剔除语法] " + (okI ? "通过" : "失败"));

            // J. 开关真的读配置：写 0 读关、写 1 读开（不是写死常量）
            SetSetQuiet("grammar", "0");
            bool offRead = !GrammarOn;
            SetSetQuiet("grammar", "1");
            bool onRead = GrammarOn;
            bool okJ = offRead && onRead;
            sb.AppendLine("[J·开关存得进读得出] " + (okJ ? "通过" : "失败"));

            // K. 实测模型会省掉「正名：」：中文进写成「1. 现在完成进行时（...）」，代码要补回来
            SetSetQuiet("grammar", "1");
            string lost = "【英文】I've been waiting for you for two hours.\n【音标】waiting /ˈweɪtɪŋ/\n"
                + "【缩写版】\n轻度 | 我已经等你两个小时了\n中度 | 等你两小时了\n重度 | waitin 2hrs 4 u\n"
                + "【语法】\n"
                + "1. 现在完成进行时（Present Perfect Continuous）\n"
                + "　　构成：have/has been + 动词-ing\n"
                + "　　为什么非它不可：一般过去只讲过去，丢掉一直等到现在的意思\n"
                + "　　对照：I waited two hours 只是叙述过去\n"
                + "　　依据：等了……两个小时了";
            string rK = GrammarGate(lost);
            bool okK = rK.IndexOf("1. 正名：现在完成进行时") >= 0
                && rK.IndexOf("构成：have/has been") >= 0 && rK.IndexOf("依据：等了") >= 0
                && rK.IndexOf("【缩写版】") >= 0;
            sb.AppendLine("[K·省掉「正名：」时自动补回] " + (okK ? "通过" : "失败"));
            sb.AppendLine("   后=" + rK.Replace("\n", " / "));

            // L. 已经写对的、或干脆写「没有」的，一个字都不许动
            string rL1 = GrammarGate(en);
            string noPoint = "【中文意思】行。\n【语法】\n没有";
            string rL2 = GrammarGate(noPoint);
            bool okL = rL1 == NormalizeGrammarSection(en) && rL1.IndexOf("1. 正名：现在完成进行时") >= 0
                && rL1.IndexOf("正名：正名") < 0
                && rL2 == noPoint;
            sb.AppendLine("[L·写对的不重改、没有的不硬补] " + (okL ? "通过" : "失败"));

            bool all = okA && okB && okC && okD && okE && okF && okG && okH && okI && okJ && okK && okL;
            sb.AppendLine(all
                ? "结论：十二条全过。关掉跟上一版逐字一致，打开多一块语法并补全「正名：」，导出不带。"
                : "结论：有失败项，见上。");
        }
        catch (Exception ex)
        {
            sb.AppendLine("自检异常: " + ex.GetType().Name + " " + ex.Message);
        }
        finally
        {
            SetSetQuiet("grammar", origSw);
            lastInput = origInput;
            try { File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true)); } catch { }
        }
    }
}
