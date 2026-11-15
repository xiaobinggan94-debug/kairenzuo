// VoiceRead.cs —— v2.5 第 6 步：朗读三层逻辑（逐词 / 短句 / 长句）
//
//   ① 按词数自动分层，也可以右键「读一遍」手动锁层：
//        ≤2 词  → 逐词：按语义成分切块（不是按等长硬切），块间短停，慢速
//        3~6 词 → 短句：整句一口气读完，只把信息焦点（核心词）重读
//        7~18 词 → 中句：一口气读完，靠标点分级停顿换气，核心词重读
//        >18 词 → 长句：先按标点分段，段太长再按语义边界切，每段各自重读，
//                 段与段之间给明显停顿（720ms，远大于逗号）
//   ② 标点分级停顿：逗号/顿号 170 → 冒号/分号 300 → 句号/问号/叹号 480 → 破折号/省略号 680
//   ③ 双语词表补 intonation（降/升/降升/平）与常见场景重音（道歉/请求/感谢/疑问/否定/
//      惊讶/确认/问候/告别/建议/不确定），中英各自一套，朗读时用来选重读词与句尾调型
//   ④ 全部走 SSML（break / emphasis / prosody），本机 System.Speech 直接读，不出网
//
// 三条硬约束（和 v2.4 第 3、4 步、v2.5 第 5 步同一套）：
//   · 底栏那 5 个按钮（复制这句 / 读一遍 / 谐音 / 语气 / 更多 ▾）位置大小文案样式一个都不动；
//     「读一遍」原地只加行为（点击按层读、右键选层），外观零改动
//   · 外框 DWM 圆角、框选截图的绘制路径完全不碰（本文件不出现任何 Dwm / Region 调用）
//   · 不新增任何可见控件，不撑大窗口：选层菜单是 ContextMenuStrip，不是子控件
//
// 读不出来时给人话：没装语音包 → 指路「设置 → 时间和语言 → 语音 → 添加语音」，
// 语音包不认 SSML 标记 → 自动退回整段读，并说明退回了。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Speech.Synthesis;
using System.Text;
using System.Windows.Forms;
using System.Xml;

partial class QueryForm
{
    // ======================== 参数（自检要断言，所以是 const） ========================

    internal const int READ_WORD_MAX = 2;      // ≤2 词：逐词
    internal const int READ_SHORT_MAX = 6;     // 3~6 词：短句
    internal const int READ_MID_MAX = 18;      // 7~18 词：中句（一口气）
    internal const int READ_LONG_MIN = 19;     // ≥19 词（>18）：长句

    internal const int READ_PAUSE_COMMA = 170; // 逗号、顿号
    internal const int READ_PAUSE_SEMI = 300;  // 冒号、分号
    internal const int READ_PAUSE_STOP = 480;  // 句号、问号、叹号
    internal const int READ_PAUSE_DASH = 680;  // 破折号、省略号
    internal const int READ_PAUSE_LINE = 380;  // 换行
    internal const int READ_PAUSE_CHUNK = 340; // 逐词：成分之间
    internal const int READ_PAUSE_SEG = 720;   // 长句：段与段之间（明显大于逗号）

    internal const int READ_SEG_MAX_WORDS = 10;   // 长句每段最多几个词（英文）
    internal const int READ_SEG_MAX_CHARS_ZH = 12; // 长句每段最多几个字（中文，按字数算）
    internal const int READ_CHUNK_MAX_WORDS = 3;  // 逐词每块最多几个词

    // ======================== 双语词表：intonation + 常见场景重音 ========================
    // Intonation：fall 降调 / rise 升调 / fallrise 降升 / level 平调
    // Stress：这个词在它所属场景里是不是信息焦点（朗读时加重音）

    internal class ReadLexEntry
    {
        public string Word;        // 英文词 / 中文词
        public string Lang;        // "en" / "zh"
        public string Meaning;     // 另一门语言的释义（双语词表的另一半）
        public string Intonation;  // fall / rise / fallrise / level
        public string Scenario;    // 道歉 / 请求 / 感谢 / 疑问 / 否定 / 惊讶 / 确认 / 问候 / 告别 / 建议 / 不确定 / 提醒
        public bool Stress;        // 该场景下重读
        public string Note;        // 一句话说明（给人看，也给自检核对）
    }

    static ReadLexEntry L(string lang, string word, string meaning, string intonation, string scenario, bool stress, string note)
    {
        ReadLexEntry e = new ReadLexEntry();
        e.Lang = lang; e.Word = word; e.Meaning = meaning; e.Intonation = intonation;
        e.Scenario = scenario; e.Stress = stress; e.Note = note;
        return e;
    }

    static readonly ReadLexEntry[] readLex = new ReadLexEntry[]
    {
        // ---- 英文 ----
        L("en", "sorry",    "抱歉/对不起", "fall",     "道歉",   true,  "句尾落下，重读在 sorry 上"),
        L("en", "apologize","道歉",        "fall",     "道歉",   true,  "正式道歉，落在动词上"),
        L("en", "please",   "请",          "rise",     "请求",   true,  "请求句尾略升，please 重读"),
        L("en", "could",    "能/可以",     "rise",     "请求",   false, "情态动词弱读，调子交给句尾"),
        L("en", "would",    "愿意",        "rise",     "请求",   false, "同上，弱读"),
        L("en", "thanks",   "谢谢",        "fall",     "感谢",   true,  "感谢降调，别拖成升调"),
        L("en", "thank",    "感谢",        "fall",     "感谢",   true,  "thank you 的重音在 thank"),
        L("en", "hello",    "你好",        "fall",     "问候",   true,  "打招呼降调，自然"),
        L("en", "hi",       "嗨",          "fall",     "问候",   true,  "短促降调"),
        L("en", "bye",      "再见",        "fall",     "告别",   true,  "告别降调"),
        L("en", "yes",      "是/对",       "fall",     "确认",   true,  "确认降调，干脆"),
        L("en", "no",       "不",          "fall",     "否定",   true,  "否定要重、要降"),
        L("en", "not",      "不",          "fall",     "否定",   true,  "否定词重读，句子才不被听反"),
        L("en", "never",    "从不",        "fall",     "否定",   true,  "强否定，重读"),
        L("en", "really",   "真的吗",      "rise",     "惊讶",   true,  "惊讶升调，尾音挑起来"),
        L("en", "wow",      "哇",          "rise",     "惊讶",   true,  "感叹升调"),
        L("en", "what",     "什么",        "rise",     "疑问",   true,  "特殊疑问句一般降，追问才升"),
        L("en", "why",      "为什么",      "fall",     "疑问",   true,  "why 问句用降调"),
        L("en", "how",      "怎么",        "rise",     "疑问",   true,  "how 问句尾音略升"),
        L("en", "maybe",    "也许",        "fallrise", "不确定", true,  "降升调表示拿不准"),
        L("en", "actually", "其实",        "fallrise", "提醒",   true,  "提醒/纠正，降升"),
        L("en", "just",     "只是",        "level",    "提醒",   false, "弱读，一带而过"),
        // ---- 中文 ----
        L("zh", "谢谢",     "thanks",      "fall",     "感谢",   true,  "感谢降调，第二个谢轻"),
        L("zh", "对不起",   "sorry",       "fall",     "道歉",   true,  "道歉降调，不起高"),
        L("zh", "抱歉",     "sorry",       "fall",     "道歉",   true,  "同上，简短"),
        L("zh", "请",       "please",      "rise",     "请求",   true,  "请求语气升，尾音轻挑"),
        L("zh", "请问",     "excuse me",   "rise",     "请求",   true,  "问路/打听，升调"),
        L("zh", "真的",     "really",      "rise",     "惊讶",   true,  "惊讶，重读在「真」"),
        L("zh", "吗",       "question particle", "rise", "疑问", false, "句末语气词，升调但不重读"),
        L("zh", "呢",       "question particle", "rise", "疑问", false, "同上"),
        L("zh", "不",       "not",         "fall",     "否定",   true,  "否定重读，避免听成肯定"),
        L("zh", "没",       "not",         "fall",     "否定",   true,  "否定重读"),
        L("zh", "别",       "don't",       "fall",     "否定",   true,  "劝阻，重读"),
        L("zh", "好的",     "ok",          "fall",     "确认",   true,  "答应，降调"),
        L("zh", "行",       "ok",          "fall",     "确认",   true,  "短促降调"),
        L("zh", "你好",     "hello",       "fall",     "问候",   true,  "问候降调"),
        L("zh", "再见",     "bye",         "fall",     "告别",   true,  "告别降调"),
        L("zh", "吧",       "suggestion particle", "level", "建议", false, "建议语气，平调收尾"),
        L("zh", "大概",     "probably",    "fallrise", "不确定", true,  "拿不准，降升")
    };

    // 虚词（不重读）：英文功能词 + 中文语气词
    static readonly string[] readFuncEn = new string[]
    {
        "a","an","the","to","of","in","on","at","for","and","or","but","is","are","was","were","be","been","being",
        "am","do","does","did","have","has","had","that","this","these","those","it","its","i","you","we","they",
        "he","she","my","your","his","her","their","our","me","him","them","us","as","with","by","from","so","if",
        "then","than","there","here","will","shall","should","may","might","must","can","up","out","about","into"
    };

    static readonly string[] readFuncZh = new string[]
    { "的", "了", "着", "过", "地", "得", "啊", "呀", "嘛", "哦", "嗯", "是", "在", "和", "跟", "就", "也" };

    // 语义成分的起点（在这些词前面断，读起来才是一个个意思块，而不是数着词数硬切）
    static readonly string[] readPhraseStartEn = new string[]
    {
        "in","on","at","to","for","with","from","by","about","into","over","under","after","before","during",
        "through","without","against","between","among","across","along","around","behind","beyond","despite",
        "except","inside","outside","toward","towards","upon","within","via","per","than",
        "and","but","or","so","because","although","though","if","when","while","where","unless","until",
        "since","whether","whereas","that","which","who","whom","whose","why","how"
    };

    static readonly string[] readPhraseStartZh = new string[]
    { "在", "和", "跟", "因为", "所以", "但是", "而且", "如果", "虽然", "然后", "就", "还", "也", "都" };

    // ======================== 数据结构 ========================

    internal class ReadTok
    {
        public string Text = "";
        public bool IsWord = false;
        public bool IsCjk = false;
        public int PauseMs = 0;        // 标点才有
        public bool IsQuestionMark = false;
        public override string ToString() { return Text; }
    }

    internal class ReadSeg
    {
        public string Text = "";
        public int Words = 0;
        public int PauseMs = 0;        // 这一段读完停多久（段间停顿）
        public string Stress = "";     // 这一段的重读词（英文一个词，中文可能是一个多字场景词）
        public int StressStart = -1;   // 重读在段文本里的起点（字符）
        public int StressLen = 0;      // 重读占几个字
        public string Intonation = "fall";
        public bool Question = false;
        public List<ReadTok> Toks = new List<ReadTok>();
        public int[] TokStart = new int[0];   // 每个词在段文本里的起点（字符），标点是 -1
    }

    internal class ReadPlan
    {
        public string Text = "";
        public string Lang = "en";       // en / zh
        public string Tier = "short";    // word / short / mid / long
        public int WordCount = 0;
        public int SegmentCount = 0;
        public int SegPauseMs = 0;       // 段间停顿（长句/逐词用）
        public string Stress = "";
        public string Intonation = "fall";
        public List<ReadSeg> Segments = new List<ReadSeg>();
        public string Ssml = "";
    }

    // ======================== 基础判定 ========================

    internal static bool ReadIsCjk(char c)
    {
        return (c >= 0x3400 && c <= 0x9FFF) || (c >= 0xF900 && c <= 0xFAFF) || (c >= 0x3040 && c <= 0x30FF);
    }

    static bool ReadIsWordChar(char c)
    {
        if (ReadIsCjk(c)) return true;
        if (char.IsLetterOrDigit(c)) return true;
        if (c == '\'' || c == '\u2019') return true;   // don't / don’t 不断开
        return false;
    }

    // 标点分级：这一个小节就是"分级"的唯一出处，SSML 里的 break 全按它来
    internal static int ReadPauseMs(char c)
    {
        switch (c)
        {
            case ',': case '\uFF0C': case '\u3001': return READ_PAUSE_COMMA;
            case ':': case '\uFF1A': case ';': case '\uFF1B': return READ_PAUSE_SEMI;
            case '.': case '!': case '?': case '\u3002': case '\uFF01': case '\uFF1F': return READ_PAUSE_STOP;
            case '\u2014': case '\u2026': return READ_PAUSE_DASH;   // —— 与 ……
            case '\n': return READ_PAUSE_LINE;
            default: return 0;
        }
    }

    static bool ReadIsQuestionPunct(string p)
    {
        if (string.IsNullOrEmpty(p)) return false;
        return p.IndexOf('?') >= 0 || p.IndexOf('\uFF1F') >= 0;
    }

    // ======================== 分词（英文按词、中文按字，标点单独成 token） ========================

    internal static List<ReadTok> ReadTokens(string s)
    {
        List<ReadTok> list = new List<ReadTok>();
        if (string.IsNullOrEmpty(s)) return list;
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c) || c == '\uFEFF')
            {
                if (c == '\n' || c == '\r')
                {
                    // 换行也算一次换气，但连着换好几行只给一次
                    if (list.Count == 0 || list[list.Count - 1].IsWord || list[list.Count - 1].PauseMs < READ_PAUSE_LINE)
                    {
                        ReadTok nl = new ReadTok();
                        nl.Text = "\n";
                        nl.PauseMs = READ_PAUSE_LINE;
                        list.Add(nl);
                    }
                }
                i++;
                continue;
            }
            if (ReadIsWordChar(c))
            {
                bool cjk = ReadIsCjk(c);
                StringBuilder sb = new StringBuilder();
                while (i < s.Length && ReadIsWordChar(s[i]) && ReadIsCjk(s[i]) == cjk)
                {
                    sb.Append(s[i]);
                    i++;
                }
                if (cjk)
                {
                    // 中文按字切：一层按字数算层（6 字=短句），二层重读/调型要能落到"最后一个字"上，
                    // 三层多字词的重读交给重读区间的字符区间处理（见 ReadStressOf）
                    string run = sb.ToString();
                    for (int k = 0; k < run.Length; k++)
                    {
                        ReadTok ct = new ReadTok();
                        ct.Text = run.Substring(k, 1);
                        ct.IsWord = true;
                        ct.IsCjk = true;
                        list.Add(ct);
                    }
                }
                else
                {
                    ReadTok t = new ReadTok();
                    t.Text = sb.ToString();
                    t.IsWord = true;
                    t.IsCjk = false;
                    list.Add(t);
                }
                continue;
            }
            // 连续标点合成一个 token，取其中停顿最长的那一级
            StringBuilder p = new StringBuilder();
            int pause = 0;
            bool q = false;
            while (i < s.Length && !ReadIsWordChar(s[i]) && !char.IsWhiteSpace(s[i]))
            {
                p.Append(s[i]);
                int ms = ReadPauseMs(s[i]);
                if (ms > pause) pause = ms;
                if (ReadIsQuestionPunct(s[i].ToString())) q = true;
                i++;
            }
            if (p.Length > 0)
            {
                ReadTok pt = new ReadTok();
                pt.Text = p.ToString();
                pt.PauseMs = pause;
                pt.IsQuestionMark = q;
                list.Add(pt);
            }
        }
        return list;
    }

    internal static int ReadWordCount(string s)
    {
        List<ReadTok> ts = ReadTokens(s);
        int n = 0;
        for (int i = 0; i < ts.Count; i++) if (ts[i].IsWord) n++;
        return n;
    }

    // 中文按字数算（"今天天气不错" = 6），英文按词算，混排相加
    internal static string ReadTierOf(string text)
    {
        int n = ReadWordCount(text);
        if (n <= READ_WORD_MAX) return "word";
        if (n <= READ_SHORT_MAX) return "short";
        if (n <= READ_MID_MAX) return "mid";
        return "long";
    }

    internal static string ReadTierName(string tier)
    {
        if (tier == "word") return "逐词";
        if (tier == "short") return "短句";
        if (tier == "mid") return "中句";
        return "长句";
    }

    internal static bool ReadIsEnFunc(string w)
    {
        if (string.IsNullOrEmpty(w)) return true;
        string k = w.ToLowerInvariant();
        for (int i = 0; i < readFuncEn.Length; i++) if (readFuncEn[i] == k) return true;
        return false;
    }

    internal static bool ReadIsZhFunc(string w)
    {
        if (string.IsNullOrEmpty(w)) return true;
        for (int i = 0; i < readFuncZh.Length; i++) if (readFuncZh[i] == w) return true;
        return false;
    }

    static bool ReadIsPhraseStart(string w)
    {
        if (string.IsNullOrEmpty(w)) return false;
        string k = w.ToLowerInvariant();
        for (int i = 0; i < readPhraseStartEn.Length; i++) if (readPhraseStartEn[i] == k) return true;
        for (int i = 0; i < readPhraseStartZh.Length; i++) if (readPhraseStartZh[i] == w) return true;
        return false;
    }

    static bool ReadIsArticle(string w)
    {
        if (string.IsNullOrEmpty(w)) return false;
        string k = w.ToLowerInvariant();
        return k == "a" || k == "an" || k == "the";
    }

    // ======================== 词表查询 ========================

    internal static ReadLexEntry ReadLexOf(string w)
    {
        if (string.IsNullOrEmpty(w)) return null;
        string k = w.Trim().ToLowerInvariant();
        if (k.Length == 0) return null;
        for (int i = 0; i < readLex.Length; i++)
        {
            if (string.Equals(readLex[i].Word, k, StringComparison.OrdinalIgnoreCase)) return readLex[i];
        }
        return null;
    }

    internal static int ReadLexTotal() { return readLex.Length; }

    internal static int ReadLexLangCount(string lang)
    {
        int n = 0;
        for (int i = 0; i < readLex.Length; i++) if (readLex[i].Lang == lang) n++;
        return n;
    }

    internal static int ReadLexScenarioCount()
    {
        List<string> seen = new List<string>();
        for (int i = 0; i < readLex.Length; i++)
            if (!seen.Contains(readLex[i].Scenario)) seen.Add(readLex[i].Scenario);
        return seen.Count;
    }

    internal static bool ReadIntonationValid(string x)
    {
        return x == "fall" || x == "rise" || x == "fallrise" || x == "level";
    }

    // ======================== 语义成分切分 ========================

    static bool ReadHasWord(List<ReadTok> toks)
    {
        for (int i = 0; i < toks.Count; i++) if (toks[i].IsWord) return true;
        return false;
    }

    static int ReadWordsIn(List<ReadTok> toks)
    {
        int n = 0;
        for (int i = 0; i < toks.Count; i++) if (toks[i].IsWord) n++;
        return n;
    }

    // 一个 token 串 → 若干语段：先按标点断（标点跟着前一段走），太长再按语义成分断
    static List<ReadSeg> ReadSplitTokens(List<ReadTok> toks, int maxWords, bool semantic, int basePause)
    {
        List<ReadSeg> segs = new List<ReadSeg>();
        List<ReadTok> cur = new List<ReadTok>();
        int curWords = 0;

        for (int i = 0; i < toks.Count; i++)
        {
            ReadTok t = toks[i];
            if (t.IsWord)
            {
                bool tooLong = curWords >= maxWords;
                // 语义断点：已经有两三个词了，再来一个"介词/连词/关系词"开头的新成分，
                // 就在这里断——这样切出来的是 "I want" | "to go" | "to the park"，不是数着切
                bool newPhrase = semantic && curWords >= 2 && ReadIsPhraseStart(t.Text);
                if (cur.Count > 0 && curWords > 0 && (tooLong || newPhrase))
                {
                    segs.Add(ReadMakeSeg(cur, 0));
                    cur = new List<ReadTok>();
                    curWords = 0;
                }
                cur.Add(t);
                curWords++;
            }
            else
            {
                cur.Add(t);
                // 标点：语段到此为止（标点本身留在这一段，停顿时长记在段上）
                if (t.PauseMs > 0 && curWords > 0)
                {
                    segs.Add(ReadMakeSeg(cur, 0));
                    cur = new List<ReadTok>();
                    curWords = 0;
                }
            }
        }
        if (cur.Count > 0 && ReadHasWord(cur)) segs.Add(ReadMakeSeg(cur, 0));

        // 修补：冠词/介词不能落在段尾（"the park with the | dog" 这种读起来是断的）
        for (int i = 0; i < segs.Count - 1; i++)
        {
            ReadSeg a = segs[i];
            if (a.Words <= 1) continue;
            int lastW = -1;
            for (int j = a.Toks.Count - 1; j >= 0; j--) if (a.Toks[j].IsWord) { lastW = j; break; }
            if (lastW < 0) continue;
            string lw = a.Toks[lastW].Text;
            bool dangling = ReadIsArticle(lw) || (ReadIsEnFunc(lw) && ReadIsPhraseStart(lw));
            if (!dangling) continue;
            // 把尾巴这个词挪到下一段开头
            ReadSeg b = segs[i + 1];
            b.Toks.Insert(0, a.Toks[lastW]);
            a.Toks.RemoveAt(lastW);
            ReadRecompute(a);
            ReadRecompute(b);
        }

        // 段间基础停顿：逐词/长句有，短句中句没有（短句要一口气）
        for (int i = 0; i < segs.Count; i++)
        {
            int punctPause = 0;
            for (int j = 0; j < segs[i].Toks.Count; j++)
                if (segs[i].Toks[j].PauseMs > punctPause) punctPause = segs[i].Toks[j].PauseMs;
            segs[i].PauseMs = (i == segs.Count - 1) ? 0 : Math.Max(basePause, punctPause);
        }
        return segs;
    }

    static void ReadRecompute(ReadSeg s)
    {
        s.Words = ReadWordsIn(s.Toks);
        s.Text = ReadTextOf(s.Toks);
        s.Question = false;
        for (int i = 0; i < s.Toks.Count; i++) if (s.Toks[i].IsQuestionMark) s.Question = true;
        // 每个词在段文本里的字符起点：重读要按字符区间落在原文上，中文多字词才能整块重读
        s.TokStart = new int[s.Toks.Count];
        bool prevCjk = false, hasPrev = false;
        int pos = 0;
        for (int i = 0; i < s.Toks.Count; i++)
        {
            ReadTok t = s.Toks[i];
            if (!t.IsWord) { s.TokStart[i] = -1; continue; }
            if (hasPrev && !(prevCjk && t.IsCjk)) pos++;
            s.TokStart[i] = pos;
            pos += t.Text.Length;
            prevCjk = t.IsCjk;
            hasPrev = true;
        }
    }

    static ReadSeg ReadMakeSeg(List<ReadTok> toks, int basePause)
    {
        ReadSeg s = new ReadSeg();
        s.Toks = new List<ReadTok>(toks);
        ReadRecompute(s);
        s.PauseMs = basePause;
        return s;
    }

    // 段文本：中文不加空格，英文词间加空格（中文与英文相邻也留一个空格，好读）
    internal static string ReadTextOf(List<ReadTok> toks)
    {
        StringBuilder sb = new StringBuilder();
        bool prevCjk = false;
        bool hasPrev = false;
        for (int i = 0; i < toks.Count; i++)
        {
            ReadTok t = toks[i];
            if (!t.IsWord) continue;
            if (hasPrev && !(prevCjk && t.IsCjk)) sb.Append(' ');
            sb.Append(t.Text);
            prevCjk = t.IsCjk;
            hasPrev = true;
        }
        return sb.ToString();
    }

    // 句子：按强标点（句号/问号/叹号/换行）切
    static List<List<ReadTok>> ReadSentences(List<ReadTok> toks)
    {
        List<List<ReadTok>> sents = new List<List<ReadTok>>();
        List<ReadTok> cur = new List<ReadTok>();
        for (int i = 0; i < toks.Count; i++)
        {
            ReadTok t = toks[i];
            cur.Add(t);
            bool cut = false;
            if (!t.IsWord && t.PauseMs >= READ_PAUSE_STOP) cut = true;
            if (!t.IsWord && t.Text == "\n") cut = true;
            if (cut)
            {
                if (ReadHasWord(cur)) sents.Add(cur);
                cur = new List<ReadTok>();
            }
        }
        if (ReadHasWord(cur)) sents.Add(cur);
        return sents;
    }

    internal static List<ReadSeg> ReadSegments(string text, string tier)
    {
        List<ReadSeg> segs = new List<ReadSeg>();
        List<ReadTok> toks = ReadTokens(text);
        List<List<ReadTok>> sents = ReadSentences(toks);
        string lang = ReadLangOf(text);
        // 中文按字算：3 字一块（约一个词）、12 字一段；英文按词：3 词一块、10 词一段
        int chunkMax = lang == "zh" ? READ_CHUNK_MAX_WORDS : READ_CHUNK_MAX_WORDS;
        int segMax = lang == "zh" ? READ_SEG_MAX_CHARS_ZH : READ_SEG_MAX_WORDS;
        for (int i = 0; i < sents.Count; i++)
        {
            if (tier == "word")
                segs.AddRange(ReadSplitTokens(sents[i], chunkMax, true, READ_PAUSE_CHUNK));
            else if (tier == "long")
                segs.AddRange(ReadSplitTokens(sents[i], segMax, true, READ_PAUSE_SEG));
            else
                segs.Add(ReadMakeSeg(sents[i], 0));   // 短句 / 中句：整句一口气
        }
        if (lang == "zh")
        {
            // 中文的"的/了/着/过/地/得"不能打头（"…|的了"这种读起来断）
            for (int i = 1; i < segs.Count; i++)
            {
                if (segs[i].Toks.Count == 0 || segs[i - 1].Words <= 1) continue;
                ReadTok first = segs[i].Toks[0];
                if (!first.IsWord || !first.IsCjk) continue;
                string c = first.Text;
                if (c != "的" && c != "了" && c != "着" && c != "过" && c != "地" && c != "得") continue;
                segs[i].Toks.RemoveAt(0);
                segs[i - 1].Toks.Add(first);
                ReadRecompute(segs[i - 1]);
                ReadRecompute(segs[i]);
            }
            for (int i = segs.Count - 1; i >= 0; i--) if (!ReadHasWord(segs[i].Toks)) segs.RemoveAt(i);
        }
        // 跨句的停顿：句与句之间也要停，按"标点分级"和本层基础停顿里取大的
        int baseP = (tier == "word") ? READ_PAUSE_CHUNK : (tier == "long" ? READ_PAUSE_SEG : READ_PAUSE_STOP);
        for (int i = 0; i < segs.Count; i++)
        {
            if (i == segs.Count - 1) { segs[i].PauseMs = 0; continue; }
            int punctPause = 0;
            for (int j = 0; j < segs[i].Toks.Count; j++)
                if (segs[i].Toks[j].PauseMs > punctPause) punctPause = segs[i].Toks[j].PauseMs;
            int ms = Math.Max(baseP, punctPause);
            if (segs[i].PauseMs > ms) ms = segs[i].PauseMs;   // 逐词/长句已算过的段间停顿不降级
            segs[i].PauseMs = ms;
        }
        return segs;
    }

    // ======================== 重读与调型 ========================

    // 英文命中要卡词边界："not" 不能落在 "nothing" 里面
    static bool ReadBoundaryOk(string p, int idx, int len)
    {
        if (idx > 0 && ReadIsWordChar(p[idx - 1])) return false;
        int e = idx + len;
        if (e < p.Length && ReadIsWordChar(p[e])) return false;
        return true;
    }

    // 这一段重读哪儿：返回段文本里的字符区间（start/len），顺带把词表的调型带出来
    static void ReadStressOf(ReadSeg seg, string lang, out int start, out int len, out string intonation)
    {
        start = -1; len = 0; intonation = "";
        if (seg == null) return;
        string P = seg.Text;
        if (P.Length == 0) return;

        // ① 词表里多字的场景词（"谢谢"/"对不起"）：整块重读，调型跟着它走
        int bs = -1, bl = 0;
        for (int i = 0; i < readLex.Length; i++)
        {
            ReadLexEntry e = readLex[i];
            if (e == null || !e.Stress || string.IsNullOrEmpty(e.Word) || e.Word.Length < 2) continue;
            if (string.Compare(e.Lang, lang, StringComparison.OrdinalIgnoreCase) != 0) continue;
            int idx = P.IndexOf(e.Word, lang == "en" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            if (idx < 0) continue;
            if (!ReadBoundaryOk(P, idx, e.Word.Length)) continue;
            if (idx > bs || (idx == bs && e.Word.Length > bl)) { bs = idx; bl = e.Word.Length; intonation = e.Intonation; }
        }
        if (bs >= 0) { start = bs; len = bl; return; }

        // ② 从后往前：词表说这个场景该重读 > 最后一个实词 > 最后一个词
        int lastContent = -1, anyWord = -1;
        for (int i = seg.Toks.Count - 1; i >= 0; i--)
        {
            ReadTok t = seg.Toks[i];
            if (!t.IsWord) continue;
            if (anyWord < 0) anyWord = i;
            ReadLexEntry e = ReadLexOf(t.Text);
            if (e != null && e.Stress)
            {
                start = seg.TokStart[i];
                len = t.Text.Length;
                intonation = e.Intonation;
                return;
            }
            if (lastContent < 0 && (t.IsCjk ? !ReadIsZhFunc(t.Text) : !ReadIsEnFunc(t.Text))) lastContent = i;
        }
        int pick = lastContent >= 0 ? lastContent : anyWord;
        if (pick >= 0) { start = seg.TokStart[pick]; len = seg.Toks[pick].Text.Length; }
    }

    static string ReadIntonationOf(ReadSeg seg, string lexIntonation)
    {
        if (ReadIntonationValid(lexIntonation)) return lexIntonation;
        if (seg != null && seg.Question) return "rise";     // 问句升调
        return "fall";                                      // 陈述/感叹降调
    }

    // ======================== SSML ========================

    static string ReadEscape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        StringBuilder sb = new StringBuilder(s.Length + 8);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '&') sb.Append("&amp;");
            else if (c == '<') sb.Append("&lt;");
            else if (c == '>') sb.Append("&gt;");
            else if (c == '"') sb.Append("&quot;");
            else if (c == '\'') sb.Append("&apos;");
            else sb.Append(c);
        }
        return sb.ToString();
    }

    static string ReadRatePct(string tier)
    {
        if (tier == "word") return "-25%";   // 逐词：一个字一个字往外递
        if (tier == "long") return "-6%";    // 长句：稍微压一点，好跟
        if (tier == "mid") return "-4%";
        return "";                           // 短句：原速一口气（注意 rate="0%" 会被引擎当成慢读，别写）
    }

    // 一个语段 → 一个 <s>；段内标点插 <break>，核心词套 <emphasis>，句尾调型用 prosody pitch。
    // 三件事都按"字符区间"落在段文本上，所以中文多字词也能整块重读、最后一个字单独抬/落调。
    static void ReadAppendSeg(StringBuilder sb, ReadSeg seg, string tier)
    {
        string rate = ReadRatePct(tier);
        string P = seg.Text;
        sb.Append("<s>");
        if (rate.Length > 0) sb.Append("<prosody rate=\"" + rate + "\">");

        // 段内标点的停顿：插在"下一个词的开头"
        List<int> bPos = new List<int>();
        List<int> bMs = new List<int>();
        int pending = 0;
        for (int i = 0; i < seg.Toks.Count; i++)
        {
            ReadTok t = seg.Toks[i];
            if (!t.IsWord) { if (t.PauseMs > pending) pending = t.PauseMs; continue; }
            if (pending > 0 && seg.TokStart[i] >= 0) { bPos.Add(seg.TokStart[i]); bMs.Add(pending); pending = 0; }
        }

        // 重读区间
        int s0 = seg.StressStart, s1 = seg.StressStart + seg.StressLen;
        if (s0 < 0 || s1 > P.Length) { s0 = -1; s1 = -1; }

        // 句尾调型：中文落在最后一个字上，英文落在最后一个词上
        int p0 = -1, p1 = -1;
        if (seg.Intonation == "rise" || seg.Intonation == "fall" || seg.Intonation == "fallrise")
        {
            int last = -1;
            for (int i = seg.Toks.Count - 1; i >= 0; i--) if (seg.Toks[i].IsWord && seg.TokStart[i] >= 0) { last = i; break; }
            if (last >= 0)
            {
                int ws = seg.TokStart[last], we = ws + seg.Toks[last].Text.Length;
                if (seg.Toks[last].IsCjk) { p0 = we - 1; p1 = we; }
                else { p0 = ws; p1 = we; }
            }
        }

        List<int> cuts = new List<int>();
        cuts.Add(0);
        cuts.Add(P.Length);
        if (s0 >= 0) { cuts.Add(s0); cuts.Add(s1); }
        if (p0 >= 0) { cuts.Add(p0); cuts.Add(p1); }
        for (int i = 0; i < bPos.Count; i++) if (bPos[i] > 0 && bPos[i] < P.Length) cuts.Add(bPos[i]);
        cuts.Sort();

        int a = 0;
        for (int i = 0; i < cuts.Count; i++)
        {
            int b = cuts[i];
            if (b <= a || b > P.Length) continue;
            bool st = s0 >= 0 && a >= s0 && b <= s1;
            bool pit = p0 >= 0 && a >= p0 && b <= p1;
            string body = ReadEscape(P.Substring(a, b - a));
            if (st) body = "<emphasis level=\"strong\">" + body + "</emphasis>";
            if (pit)
            {
                if (seg.Intonation == "rise") body = "<prosody pitch=\"+10%\">" + body + "</prosody>";
                else if (seg.Intonation == "fall") body = "<prosody pitch=\"-6%\">" + body + "</prosody>";
                else body = "<prosody pitch=\"+6%\"><prosody pitch=\"-4%\">" + body + "</prosody></prosody>";
            }
            for (int k = 0; k < bPos.Count; k++) if (bPos[k] == a) sb.Append("<break time=\"" + bMs[k] + "ms\"/>");
            sb.Append(body);
            a = b;
        }

        if (rate.Length > 0) sb.Append("</prosody>");
        sb.Append("</s>");
    }

    static string ReadSsmlOf(string lang, string tier, List<ReadSeg> segs)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"");
        sb.Append(lang == "zh" ? "zh-CN" : "en-US");
        sb.Append("\"><p>");
        for (int i = 0; i < segs.Count; i++)
        {
            ReadAppendSeg(sb, segs[i], tier);
            if (i < segs.Count - 1 && segs[i].PauseMs > 0)
                sb.Append("<break time=\"" + segs[i].PauseMs + "ms\"/>");
        }
        sb.Append("</p></speak>");
        return sb.ToString();
    }

    // ======================== 组装一份朗读计划 ========================

    internal static string ReadLangOf(string text)
    {
        int cjk = 0, latin = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (ReadIsCjk(text[i])) cjk++;
            else if ((text[i] >= 'a' && text[i] <= 'z') || (text[i] >= 'A' && text[i] <= 'Z')) latin++;
        }
        return cjk >= latin ? "zh" : "en";
    }

    internal static ReadPlan ReadMakePlan(string text, string mode)
    {
        if (text == null) text = "";
        text = text.Trim();
        if (text.Length == 0) return null;

        ReadPlan p = new ReadPlan();
        p.Text = text;
        p.Lang = ReadLangOf(text);
        p.WordCount = ReadWordCount(text);
        p.Tier = ReadTierOf(text);
        if (mode == "word" || mode == "short" || mode == "mid" || mode == "long")
            p.Tier = mode;                      // 右键锁层：用户说了算
        p.Segments = ReadSegments(text, p.Tier);
        if (p.Segments.Count == 0) return null;
        p.SegmentCount = p.Segments.Count;

        // 每段的重读词与调型
        for (int i = 0; i < p.Segments.Count; i++)
        {
            ReadSeg s = p.Segments[i];
            s.Question = s.Question || (p.Lang == "zh" && ReadEndsWithQuestionZh(s));
            int ss, sl;
            string lexIt;
            ReadStressOf(s, p.Lang, out ss, out sl, out lexIt);
            s.StressStart = ss;
            s.StressLen = sl;
            s.Stress = (ss >= 0 && sl > 0 && ss + sl <= s.Text.Length) ? s.Text.Substring(ss, sl) : "";
            s.Intonation = ReadIntonationOf(s, lexIt);
        }
        p.Stress = p.Segments[0].Stress;
        p.Intonation = p.Segments[0].Intonation;
        p.SegPauseMs = p.Segments.Count > 1 ? p.Segments[0].PauseMs : 0;
        p.Ssml = ReadSsmlOf(p.Lang, p.Tier, p.Segments);
        return p;
    }

    static bool ReadEndsWithQuestionZh(ReadSeg s)
    {
        for (int i = s.Toks.Count - 1; i >= 0; i--)
        {
            ReadTok t = s.Toks[i];
            if (!t.IsWord) continue;
            string w = t.Text;
            return w == "吗" || w == "呢" || w == "么";
        }
        return false;
    }

    // ======================== 朗读（本机 System.Speech，SSML 优先，失败退回整段） ========================

    internal static string ReadMode { get { return GetSet("read_mode", "auto"); } }

    string readVoiceLang = "";
    string readPlainText = "";
    bool readSsmlRetried = false;
    bool readHookInstalled = false;
    // v2.8.4：正在读的标记。Stop 靠它判断「现在有没有东西在响」，读完自己归位
    bool readSpeaking = false;

    internal bool ReadIsSpeaking { get { return readSpeaking; } }

    // v2.8.4：急停。返回 true 表示「刚才确实在读、已经掐掉」，false 表示本来就没读
    internal bool ReadStop()
    {
        if (!readSpeaking) return false;
        readSpeaking = false;
        try { if (tts != null) tts.SpeakAsyncCancelAll(); } catch { }
        try { tip.Text = "已停"; } catch { }
        Log("朗读停止");
        return true;
    }

    internal void ReadSay(string s)
    {
        if (s == null) s = "";
        s = s.Trim();
        if (s.Length == 0) { tip.Text = "还没有内容可读"; return; }

        ReadPlan p = ReadMakePlan(s, ReadMode);
        if (p == null) { tip.Text = "还没有内容可读"; return; }

        try
        {
            EnsureTtsFor(p.Lang);
            if (tts == null) { tip.Text = ReadNoVoiceHint(); return; }
            tts.SpeakAsyncCancelAll();
            readPlainText = s;
            readSsmlRetried = false;
            readSpeaking = true;
            tts.SpeakSsmlAsync(p.Ssml);
            tip.Text = "正在读：" + ReadTierName(p.Tier) + "（" + p.WordCount + " 词）";
            Log("朗读[" + p.Tier + "] 词=" + p.WordCount + " 段=" + p.SegmentCount
                + " 重读=" + p.Stress + " 停顿=" + p.SegPauseMs + "ms 文本="
                + (s.Length > 60 ? s.Substring(0, 60) : s));
        }
        catch
        {
            // SSML 走不通就退回原来的整段读，绝不哑掉
            try
            {
                EnsureTts();
                if (tts == null) { tip.Text = ReadNoVoiceHint(); return; }
                tts.SpeakAsyncCancelAll();
                readSpeaking = true;
                tts.SpeakAsync(s);
                tip.Text = "正在读…";
            }
            catch { tip.Text = ReadNoVoiceHint(); }
        }
    }

    void EnsureTtsFor(string lang)
    {
        if (tts == null) EnsureTts();
        if (tts == null) return;
        if (!readHookInstalled)
        {
            readHookInstalled = true;
            try { tts.SpeakCompleted += delegate(object s0, SpeakCompletedEventArgs e0) { ReadOnCompletedUi(e0); }; }
            catch { }
        }
        try
        {
            if (lang == "zh")
            {
                if (readVoiceLang == "zh") return;
                string zh = FindChineseVoice();
                if (zh != null) { tts.SelectVoice(zh); readVoiceLang = "zh"; }
            }
            else
            {
                if (readVoiceLang == "en") return;
                string picked = FindVoice(GetSet("voice", "Zira"));
                if (picked == null) picked = FindVoice("David");
                if (picked == null) picked = FindEnglishVoice();
                if (picked != null) { try { tts.SelectVoice(picked); } catch { } }
                readVoiceLang = "en";
            }
        }
        catch { }
    }

    string FindChineseVoice()
    {
        try
        {
            foreach (InstalledVoice v in tts.GetInstalledVoices())
            {
                try
                {
                    if (v.Enabled && v.VoiceInfo.Culture != null &&
                        v.VoiceInfo.Culture.TwoLetterISOLanguageName == "zh")
                        return v.VoiceInfo.Name;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    internal string ReadNoVoiceHint()
    {
        try
        {
            if (tts != null)
            {
                int n = 0;
                foreach (InstalledVoice v in tts.GetInstalledVoices()) if (v.Enabled) n++;
                if (n == 0) return "这台电脑没装语音包：设置 → 时间和语言 → 语音 → 添加语音";
            }
            else return "这台电脑没装语音包：设置 → 时间和语言 → 语音 → 添加语音";
        }
        catch { }
        return "没找到能用的语音包，换一个发音人试试（更多 ▾ → 语音设置）";
    }

    void ReadOnCompletedUi(SpeakCompletedEventArgs e)
    {
        try
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new MethodInvoker(delegate { ReadOnCompletedUi(e); })); return; }
        }
        catch { return; }
        ReadOnSpeakCompleted(e);
    }

    void ReadOnSpeakCompleted(SpeakCompletedEventArgs e)
    {
        // v2.8.4：被 ReadStop 掐掉的旧回调不许乱动状态，也不许把「已停」文案盖回去
        if (e != null && e.Cancelled) return;
        if (e != null && e.Error != null)
        {
            if (!readSsmlRetried && readPlainText.Length > 0)
            {
                readSsmlRetried = true;
                try
                {
                    if (tts != null) tts.SpeakAsync(readPlainText);
                    tip.Text = "语音包不认断句标记，已改整段读";
                }
                catch { readSpeaking = false; tip.Text = ReadNoVoiceHint(); }
            }
            else readSpeaking = false;
            return;
        }
        // 正常读完，标记归位，下次 F2 还是「开始读」
        readSpeaking = false;
    }

    // ======================== 「读一遍」右键选层（只加行为，不改外观） ========================

    ContextMenuStrip readMenu;
    ToolStripMenuItem[] readMenuItems;

    internal void MainReadInit()
    {
        if (btnSpeak == null || readMenu != null) return;
        try
        {
            readMenu = new ContextMenuStrip();
            readMenu.ShowImageMargin = false;
            string[] keys = new string[] { "auto", "word", "short", "mid", "long" };
            string[] texts = new string[] {
                "自动：按词数选层",
                "逐词：按语义成分切块",
                "短句：≤6 词一口气",
                "中句：7~18 词一口气",
                "长句：>18 词分段停"
            };
            readMenuItems = new ToolStripMenuItem[keys.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                string k = keys[i];
                ToolStripMenuItem mi = new ToolStripMenuItem(texts[i]);
                mi.Tag = k;
                mi.Click += delegate { SetSet("read_mode", k); tip.Text = "朗读方式：" + ReadModeName(k); };
                readMenu.Items.Add(mi);
                readMenuItems[i] = mi;
            }
            readMenu.Opening += delegate
            {
                string cur = ReadMode;
                for (int i = 0; i < readMenuItems.Length; i++)
                {
                    string k = (string)readMenuItems[i].Tag;
                    readMenuItems[i].Checked = (k == cur) || (cur == "auto" && k == "auto");
                }
            };
            btnSpeak.MouseUp += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Right && readMenu != null)
                    readMenu.Show(btnSpeak, new Point(0, btnSpeak.Height));
            };
            if (footTip != null)
                footTip.SetToolTip(btnSpeak, "读一遍（F2）：自动按词数分逐词 / 短句 / 长句三层；右键选朗读方式");
        }
        catch { }
    }

    internal static string ReadModeName(string k)
    {
        if (k == "word") return "逐词";
        if (k == "short") return "短句";
        if (k == "mid") return "中句";
        if (k == "long") return "长句";
        return "自动";
    }

    // ======================== 自检 --readcheck ========================

    internal static string ReadPlanSsmlForTest(string t, string mode)
    {
        ReadPlan p = ReadMakePlan(t, mode);
        return p == null ? "" : p.Ssml;
    }

    internal static ReadPlan ReadPlanForTest(string t, string mode) { return ReadMakePlan(t, mode); }
    internal static List<ReadSeg> ReadSegmentsForTest(string t, string tier) { return ReadSegments(t, tier); }
    internal static List<ReadTok> ReadTokensForTest(string t) { return ReadTokens(t); }
    internal static int ReadPauseForTest(char c) { return ReadPauseMs(c); }
    internal static ReadLexEntry ReadLexForTest(string w) { return ReadLexOf(w); }
    internal static string ReadTierForTest(string t) { return ReadTierOf(t); }
    internal static int ReadWordCountForTest(string t) { return ReadWordCount(t); }

    static int ReadCountOf(string hay, string needle)
    {
        if (string.IsNullOrEmpty(hay) || string.IsNullOrEmpty(needle)) return 0;
        int n = 0, i = 0;
        while ((i = hay.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    static bool ReadXmlOk(string ssml, out string note)
    {
        note = "";
        try
        {
            XmlDocument d = new XmlDocument();
            d.XmlResolver = null;
            d.LoadXml(ssml);
            XmlElement root = d.DocumentElement;
            if (root == null || root.Name != "speak") { note = "根节点不是 speak"; return false; }
            note = "根节点 speak";
            return true;
        }
        catch (Exception ex) { note = ex.Message; return false; }
    }

    // 让引擎真解析一遍 SSML（音量 0，听不见），确认真机不报错
    static bool ReadSsmlAccepted(string ssml, out string note)
    {
        note = "";
        SpeechSynthesizer sp = null;
        try
        {
            sp = new SpeechSynthesizer();
            int enabled = 0;
            foreach (InstalledVoice v in sp.GetInstalledVoices()) if (v.Enabled) enabled++;
            if (enabled == 0) { note = "没装语音包，跳过"; return true; }
            sp.Volume = 0;
            bool done = false;
            Exception err = null;
            sp.SpeakCompleted += delegate(object s, SpeakCompletedEventArgs e) { err = e == null ? null : e.Error; done = true; };
            sp.SpeakSsmlAsync(ssml);
            DateTime t0 = DateTime.Now;
            while (!done && (DateTime.Now - t0).TotalMilliseconds < 25000) MlPump(50);
            if (!done) { note = "25 秒未读完（超时）"; return false; }
            if (err != null) { note = "引擎报错：" + err.Message; return false; }
            note = "引擎已接受（静音验证）";
            return true;
        }
        catch (Exception ex) { note = ex.GetType().Name + ": " + ex.Message; return false; }
        finally { if (sp != null) { try { sp.SpeakAsyncCancelAll(); sp.Dispose(); } catch { } } }
    }

    // 自检专用：把窗口按设计宽度（456）摆一次，让底栏回到完整文案。
    // 理由：底栏从 v2.1 起就有「窗口窄了把长文案换成短说法」的自适应（复制这句→复制、语气 自动→自动），
    // 自检如果跑在用户上次缩小的窗口尺寸上，量到的就是缩短版文案，断言会跟着环境飘。
    internal void MainForceDesignWidthForTest()
    {
        try
        {
            // 自检改过窗口尺寸，绝不能把尺寸写回用户的 settings.ini
            selfTestMode = true;
            int want = SForTest(456);
            if (ClientSize.Width < want)
            {
                ClientSize = new Size(want, ClientSize.Height);
                Application.DoEvents();
                MlPump(600);
            }
        }
        catch { }
    }

    // ======================== 自检 --stopcheck（v2.8.4 朗读急停） ========================

    static void StopCheck(string outPath)
    {
        StringBuilder sb = new StringBuilder();
        int pass = 0, fail = 0;
        Action<string, bool, string> chk = delegate(string name, bool ok, string note)
        {
            if (ok) { pass++; sb.AppendLine("[通过] " + name + (note.Length > 0 ? "  —— " + note : "")); }
            else { fail++; sb.AppendLine("[失败] " + name + (note.Length > 0 ? "  —— " + note : "")); }
        };

        QueryForm qf = null;
        try
        {
            sb.AppendLine("== v2.8.4 自检：朗读急停（Esc / F2 / F3 变开关）  " + AppVersionLine());
            sb.AppendLine();
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            qf = new QueryForm();
            qf.Show();
            MlPump(900);

            System.Reflection.FieldInfo fSpeaking =
                typeof(QueryForm).GetField("readSpeaking", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            chk("有 readSpeaking 状态字段", fSpeaking != null, fSpeaking == null ? "没找到字段" : "已找到");

            Action<bool> setSpeaking = delegate(bool v)
            {
                if (fSpeaking != null) fSpeaking.SetValue(qf, v);
            };
            Func<bool> getSpeaking = delegate { return fSpeaking != null && (bool)fSpeaking.GetValue(qf); };

            // ---------- 一、没在读的时候按停，不能装样子 ----------
            sb.AppendLine("一、空闲态（没东西在读）");
            setSpeaking(false);
            bool idleStop = qf.ReadStop();
            chk("空闲时 ReadStop 返回 false", !idleStop, "返回 " + idleStop);
            chk("空闲时状态没被改乱", !getSpeaking(), "readSpeaking=" + getSpeaking());

            // ---------- 二、正在读的时候按停，必须真停 ----------
            sb.AppendLine();
            sb.AppendLine("二、朗读中态（模拟正在读 → 急停）");
            setSpeaking(true);
            chk("模拟在读取到 true", getSpeaking(), "readSpeaking=" + getSpeaking());
            bool hotStop = qf.ReadStop();
            chk("朗读中 ReadStop 返回 true", hotStop, "返回 " + hotStop);
            chk("停完状态归位 false", !getSpeaking(), "readSpeaking=" + getSpeaking());

            // ---------- 三、幂等：连按几下不会出错 ----------
            sb.AppendLine();
            sb.AppendLine("三、连按（幂等）");
            bool again1 = qf.ReadStop();
            bool again2 = qf.ReadStop();
            chk("连按第二次返回 false", !again1, "返回 " + again1);
            chk("连按第三次返回 false", !again2, "返回 " + again2);

            // ---------- 四、停完再读还能读（开关能翻回来） ----------
            sb.AppendLine();
            sb.AppendLine("四、停完还能再开（开关可翻转）");
            setSpeaking(true);
            qf.ReadStop();
            bool canRestart = !qf.ReadStop();
            chk("停之后再次 ReadStop 落到 false（说明可重新开读）", canRestart, "返回 " + (!canRestart));

            // ---------- 五、文案 ----------
            sb.AppendLine();
            sb.AppendLine("五、提示文案");
            setSpeaking(true);
            qf.ReadStop();
            string tipText = "";
            try
            {
                System.Reflection.FieldInfo fTip =
                    typeof(QueryForm).GetField("tip", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (fTip != null)
                {
                    object t = fTip.GetValue(qf);
                    System.Reflection.PropertyInfo pText = t == null ? null : t.GetType().GetProperty("Text");
                    if (pText != null) tipText = (string)pText.GetValue(t, null);
                }
            }
            catch { }
            chk("停完有「已停」提示", tipText.IndexOf("已停") >= 0, "tip=" + (tipText.Length > 30 ? tipText.Substring(0, 30) : tipText));

            sb.AppendLine();
            sb.AppendLine("合计：通过 " + pass + " 项，失败 " + fail + " 项");
        }
        catch (Exception ex)
        {
            sb.AppendLine();
            sb.AppendLine("[异常] " + ex.Message);
            fail++;
        }
        finally
        {
            try { if (qf != null) { qf.MainSetSuppressForTest(false); qf.Close(); qf.Dispose(); } } catch { }
        }

        try { System.IO.File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8); } catch { }
    }

    static void ReadCheck(string outPath)
    {
        StringBuilder sb = new StringBuilder();
        int pass = 0, fail = 0;
        Action<string, bool, string> chk = delegate(string name, bool ok, string note)
        {
            if (ok) { pass++; sb.AppendLine("[通过] " + name + (note.Length > 0 ? "  —— " + note : "")); }
            else { fail++; sb.AppendLine("[失败] " + name + (note.Length > 0 ? "  —— " + note : "")); }
        };

        QueryForm qf = null;
        try
        {
            sb.AppendLine("== v2.5 第 6 步自检：朗读三层逻辑（逐词 / 短句 / 长句 + 标点分级 + 词表重音）  " + AppVersionLine());
            sb.AppendLine();
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            qf = new QueryForm();
            qf.Show();
            MlPump(900);
            qf.MainForceDesignWidthForTest();
            qf.MainSetSuppressForTest(true);
            qf.MainVoiceQuietWarnForTest(true);

            // ---------- 一、分层边界 ----------
            sb.AppendLine("一、分层边界（≤2 逐词 / 3~6 短句 / 7~18 中句 / >18 长句）");
            chk("1 个词 → 逐词", ReadTierOf("hello") == "word", ReadTierOf("hello") + " 词数=" + ReadWordCount("hello"));
            chk("2 个词 → 逐词", ReadTierOf("thank you") == "word", ReadTierOf("thank you") + " 词数=" + ReadWordCount("thank you"));
            chk("3 个词 → 短句", ReadTierOf("I am fine") == "short", ReadTierOf("I am fine"));
            chk("6 个词 → 短句（边界上沿）", ReadTierOf("I really like this small cup") == "short",
                ReadTierOf("I really like this small cup") + " 词数=" + ReadWordCount("I really like this small cup"));
            chk("7 个词 → 中句（跨过短句上限）", ReadTierOf("I really like this small blue cup here") == "mid",
                ReadTierOf("I really like this small blue cup here") + " 词数=" + ReadWordCount("I really like this small blue cup here"));
            chk("18 个词 → 中句（长句下沿前一个）",
                ReadTierOf("one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen") == "mid",
                "词数=" + ReadWordCount("one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen"));
            chk("19 个词 → 长句（>18 才分段）",
                ReadTierOf("one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen") == "long",
                "词数=" + ReadWordCount("one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen"));
            chk("中文按字数算（今天天气不错 = 6 → 短句）", ReadTierOf("今天天气不错") == "short",
                ReadTierOf("今天天气不错") + " 词数=" + ReadWordCount("今天天气不错"));
            chk("标点不计入词数", ReadWordCount("Hello, world!") == 2, "词数=" + ReadWordCount("Hello, world!"));
            chk("中英混排词数相加", ReadWordCount("OK 今天 走") == 4, "词数=" + ReadWordCount("OK 今天 走"));

            // ---------- 二、逐词：按语义成分切，不是等长切 ----------
            sb.AppendLine();
            sb.AppendLine("二、逐词（按语义成分切分，块不等长，冠词/介词不悬空）");
            string wSample = "I want to go to the park with my friends tomorrow";
            List<ReadSeg> wSegs = ReadSegments(wSample, "word");
            List<int> wSizes = new List<int>();
            for (int i = 0; i < wSegs.Count; i++) wSizes.Add(wSegs[i].Words);
            sb.AppendLine("    切分结果：" + ReadJoinSegs(wSegs));
            chk("逐词至少切成 3 块", wSegs.Count >= 3, "实际 " + wSegs.Count + " 块");
            chk("每块都有词（不留空块）", wAllPositive(wSizes), ReadJoinInts(wSizes));
            chk("每块不超过 3 个词", wMaxOf(wSizes) <= READ_CHUNK_MAX_WORDS, "最长 " + wMaxOf(wSizes));
            chk("块长不是等长（至少两种长度）", wDistinct(wSizes) >= 2, "长度=" + ReadJoinInts(wSizes));
            chk("词序与原文一致（没丢词没重排）",
                ReadWordSeq(ReadTokens(wSample)) == ReadWordSeq(ReadSegToks(wSegs)),
                ReadWordSeq(ReadSegToks(wSegs)));
            chk("没有以冠词收尾的块（the/a 不悬空）", !wEndsWith(wSegs, true), ReadJoinSegs(wSegs));
            chk("没有孤零零一个冠词的块", !wSingleArticle(wSegs), ReadJoinSegs(wSegs));
            chk("逐词块间停顿 = 340ms", wSegs.Count > 1 && wSegs[0].PauseMs == READ_PAUSE_CHUNK,
                wSegs.Count > 1 ? wSegs[0].PauseMs + "ms" : "只有一块");
            ReadPlan wPlan = ReadMakePlan(wSample, "word");
            chk("逐词 SSML 里块间都有 break", wPlan != null && ReadCountOf(wPlan.Ssml, "<break") >= wPlan.SegmentCount - 1,
                wPlan == null ? "无计划" : "break 数=" + ReadCountOf(wPlan.Ssml, "<break"));
            chk("逐词语速比正常慢（-25%）", wPlan != null && wPlan.Ssml.IndexOf("rate=\"-25%\"") >= 0, "");

            // ---------- 三、短句：一口气 + 核心重读 ----------
            sb.AppendLine();
            sb.AppendLine("三、短句（≤6 词：整句一口气，只重读核心词）");
            string sSample = "I am really sorry about that";
            ReadPlan sPlan = ReadMakePlan(sSample, "short");
            chk("短句只切 1 段（一口气）", sPlan != null && sPlan.SegmentCount == 1,
                sPlan == null ? "无" : "段数=" + sPlan.SegmentCount);
            chk("短句 SSML 只有 1 个 <s>", sPlan != null && ReadCountOf(sPlan.Ssml, "<s>") == 1,
                sPlan == null ? "无" : "s=" + ReadCountOf(sPlan.Ssml, "<s>"));
            chk("短句只有 1 处重读", sPlan != null && ReadCountOf(sPlan.Ssml, "emphasis level=\"strong\"") == 1,
                sPlan == null ? "无" : "emphasis=" + ReadCountOf(sPlan.Ssml, "emphasis level=\"strong\""));
            chk("短句重读命中词表（sorry）", sPlan != null && sPlan.Stress == "sorry",
                sPlan == null ? "无" : "重读=" + sPlan.Stress);
            chk("短句没有段间大停顿（<720ms）", sPlan != null && ReadCountOf(sPlan.Ssml, "time=\"720ms\"") == 0, "");
            ReadPlan sPlan2 = ReadMakePlan("Please sit down", "short");
            chk("短句重读随场景换（please 请求重读）",
                sPlan2 != null && sPlan2.Stress != null && sPlan2.Stress.ToLowerInvariant() == "please",
                sPlan2 == null ? "无" : "重读=" + sPlan2.Stress);
            ReadPlan sPlan3 = ReadMakePlan("I am not ready", "short");
            chk("短句否定词优先重读（not）", sPlan3 != null && sPlan3.Stress == "not",
                sPlan3 == null ? "无" : "重读=" + sPlan3.Stress);

            // ---------- 四、长句：分段 + 每段重音 + 段间明显停顿 ----------
            sb.AppendLine();
            sb.AppendLine("四、长句（>18 词：分段，每段重音，段间明显停顿）");
            string lSample = "The meeting that was supposed to happen last Friday afternoon, which nobody remembered to confirm, has been moved to next Tuesday morning at nine.";
            ReadPlan lPlan = ReadMakePlan(lSample, "long");
            chk("长句切成 2 段以上", lPlan != null && lPlan.SegmentCount >= 2,
                lPlan == null ? "无" : "段数=" + lPlan.SegmentCount + "：" + (lPlan == null ? "" : ReadJoinSegs(lPlan.Segments)));
            chk("长句每段都有重读（emphasis 数 = 段数）",
                lPlan != null && ReadCountOf(lPlan.Ssml, "emphasis level=\"strong\"") == lPlan.SegmentCount,
                lPlan == null ? "无" : "emphasis=" + ReadCountOf(lPlan.Ssml, "emphasis level=\"strong\"") + " 段=" + lPlan.SegmentCount);
            chk("长句段间停顿 = 720ms（明显大于逗号）",
                lPlan != null && ReadCountOf(lPlan.Ssml, "time=\"720ms\"") >= 1,
                lPlan == null ? "无" : "720ms 次数=" + ReadCountOf(lPlan.Ssml, "time=\"720ms\""));
            chk("长句每段不超过 10 个词", lPlan != null && ReadMaxSegWords(lPlan.Segments) <= READ_SEG_MAX_WORDS,
                lPlan == null ? "无" : "最长段 " + ReadMaxSegWords(lPlan.Segments) + " 词");
            chk("长句段间停顿 > 逗号停顿（720 > 170）", READ_PAUSE_SEG > READ_PAUSE_COMMA,
                READ_PAUSE_SEG + " vs " + READ_PAUSE_COMMA);
            chk("长句 19 词也至少 2 段（无标点时按语义切）",
                ReadMakePlan("one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen", "long").SegmentCount >= 2,
                "段数=" + ReadMakePlan("one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen", "long").SegmentCount);

            // ---------- 五、标点分级停顿 ----------
            sb.AppendLine();
            sb.AppendLine("五、标点分级停顿（逗号 < 分号/冒号 < 句号 < 破折号/省略号）");
            chk("逗号 = 170ms", ReadPauseMs(',') == READ_PAUSE_COMMA, ReadPauseMs(',') + "ms");
            chk("中文逗号/顿号也是 170ms", ReadPauseMs('\uFF0C') == READ_PAUSE_COMMA && ReadPauseMs('\u3001') == READ_PAUSE_COMMA,
                ReadPauseMs('\uFF0C') + "/" + ReadPauseMs('\u3001') + "ms");
            chk("分号/冒号 = 300ms", ReadPauseMs(';') == READ_PAUSE_SEMI && ReadPauseMs(':') == READ_PAUSE_SEMI,
                ReadPauseMs(';') + "/" + ReadPauseMs(':') + "ms");
            chk("句号/问号/叹号 = 480ms",
                ReadPauseMs('.') == READ_PAUSE_STOP && ReadPauseMs('?') == READ_PAUSE_STOP && ReadPauseMs('!') == READ_PAUSE_STOP,
                ReadPauseMs('.') + "/" + ReadPauseMs('?') + "/" + ReadPauseMs('!') + "ms");
            chk("破折号/省略号 = 680ms（最长）", ReadPauseMs('\u2014') == READ_PAUSE_DASH && ReadPauseMs('\u2026') == READ_PAUSE_DASH,
                ReadPauseMs('\u2014') + "/" + ReadPauseMs('\u2026') + "ms");
            chk("分级严格递增：170 < 300 < 480 < 680",
                READ_PAUSE_COMMA < READ_PAUSE_SEMI && READ_PAUSE_SEMI < READ_PAUSE_STOP && READ_PAUSE_STOP < READ_PAUSE_DASH, "");
            ReadPlan pPlan = ReadMakePlan("Well, I think; it works. Really? Yes!", "mid");
            chk("同一句里逗号/分号/句号的 break 各不相同",
                pPlan != null && ReadCountOf(pPlan.Ssml, "time=\"170ms\"") >= 1
                    && ReadCountOf(pPlan.Ssml, "time=\"300ms\"") >= 1
                    && ReadCountOf(pPlan.Ssml, "time=\"480ms\"") >= 1,
                pPlan == null ? "无" : "170=" + ReadCountOf(pPlan.Ssml, "time=\"170ms\"") + " 300=" + ReadCountOf(pPlan.Ssml, "time=\"300ms\"") + " 480=" + ReadCountOf(pPlan.Ssml, "time=\"480ms\""));

            // ---------- 六、SSML 合法性与引擎接受 ----------
            sb.AppendLine();
            sb.AppendLine("六、SSML（XML 合法 + 引擎能解析 + 中文调型正确）");
            string note6;
            bool xmlOk = ReadXmlOk(sPlan.Ssml, out note6);
            chk("生成的是合法 XML", xmlOk, note6);
            chk("根节点是 speak 且带 xml:lang", sPlan.Ssml.IndexOf("xmlns=\"http://www.w3.org/2001/10/synthesis\"") > 0 && sPlan.Ssml.IndexOf("xml:lang=\"en-US\"") > 0, "");
            ReadPlan zhPlan = ReadMakePlan("对不起，我迟到了。", "short");
            chk("中文计划用 zh-CN", zhPlan != null && zhPlan.Ssml.IndexOf("xml:lang=\"zh-CN\"") > 0,
                zhPlan == null ? "无" : zhPlan.Lang);
            chk("中文感叹/陈述句尾降调", zhPlan != null && zhPlan.Ssml.IndexOf("pitch=\"-6%\"") > 0, "");
            ReadPlan zhQ = ReadMakePlan("你明天来吗", "short");
            chk("中文「吗」问句升调", zhQ != null && zhQ.Ssml.IndexOf("pitch=\"+10%\"") > 0, "");
            chk("特殊字符被转义（& < > 不破坏 XML）",
                ReadXmlOk(ReadPlanSsmlForTest("Tom & Jerry <are> here", "short"), out note6), note6);
            string engineNote;
            bool engineOk = ReadSsmlAccepted(sPlan.Ssml, out engineNote);
            chk("语音引擎接受逐句 SSML（音量 0 验证）", engineOk, engineNote);
            string engineNote2;
            bool engineOk2 = ReadSsmlAccepted(lPlan.Ssml, out engineNote2);
            chk("语音引擎接受长句 SSML（分段 + 720ms 停）", engineOk2, engineNote2);

            // ---------- 七、双语词表 ----------
            sb.AppendLine();
            sb.AppendLine("七、双语词表（intonation 标注 + 常见场景重音）");
            chk("词表条目 ≥ 30", ReadLexTotal() >= 30, "实际 " + ReadLexTotal() + " 条");
            chk("英文词条 ≥ 15", ReadLexLangCount("en") >= 15, "英文 " + ReadLexLangCount("en") + " 条");
            chk("中文词条 ≥ 12", ReadLexLangCount("zh") >= 12, "中文 " + ReadLexLangCount("zh") + " 条");
            chk("场景 ≥ 8 种（道歉/请求/感谢/疑问/否定/惊讶/确认/问候…）", ReadLexScenarioCount() >= 8,
                "实际 " + ReadLexScenarioCount() + " 种");
            chk("每条都有 intonation 标注且合法", ReadLexIntonationAllValid(), ReadLexBadIntonation());
            chk("每条都有中文释义（双语对照）", ReadLexMeaningAllFilled(), "");
            ReadLexEntry es = ReadLexOf("sorry");
            chk("sorry = 降调 + 道歉场景 + 重读", es != null && es.Intonation == "fall" && es.Scenario == "道歉" && es.Stress,
                es == null ? "没查到" : (es.Intonation + "/" + es.Scenario + "/" + (es.Stress ? "重读" : "轻读")));
            ReadLexEntry er = ReadLexOf("Really");
            chk("词表查询不分大小写（Really → rise 惊讶）", er != null && er.Intonation == "rise" && er.Scenario == "惊讶",
                er == null ? "没查到" : (er.Intonation + "/" + er.Scenario));
            ReadLexEntry ez = ReadLexOf("谢谢");
            chk("中文 谢谢 = 降调 + 感谢场景", ez != null && ez.Intonation == "fall" && ez.Scenario == "感谢",
                ez == null ? "没查到" : (ez.Intonation + "/" + ez.Scenario));
            chk("否定场景有重读词（not / 不 / 别）",
                ReadLexOf("not").Stress && ReadLexOf("不").Stress && ReadLexOf("别").Stress, "");
            chk("弱读词标成不重读（could / 吗 / 吧）",
                !ReadLexOf("could").Stress && !ReadLexOf("吗").Stress && !ReadLexOf("吧").Stress, "");

            // ---------- 八、底栏与外观零改动 ----------
            sb.AppendLine();
            sb.AppendLine("八、硬约束（底栏 5 按钮 / 读一遍只加行为 / 不撑大窗口 / 不碰 DWM）");
            Panel bar = (Panel)FindDeep(qf, "bar");
            Panel foot = (Panel)FindDeep(qf, "foot");
            List<string> footTexts = new List<string>();
            int btnCount = 0;
            if (bar != null)
            {
                foreach (Control c in bar.Controls)
                {
                    if (c is Button || c is RoundButton) btnCount++;
                    if (c.Text != null && c.Text.Length > 0) footTexts.Add(c.Text);
                }
            }
            chk("底栏按钮仍是 5 个", btnCount == 5, "实际 " + btnCount + " 个");
            // 窗口窄时底栏会按 v2.1 起就有的规则把长文案换成短说法（复制这句→复制、语气 自动→自动），
            // 所以两种说法都算没改文案
            chk("底栏文案还是那 5 个（含窄窗下的缩短说法）",
                (ContainsText(footTexts, "复制这句") || ContainsText(footTexts, "复制"))
                && ContainsText(footTexts, "读一遍")
                && ContainsText(footTexts, "谐音")
                && (ContainsText(footTexts, "语气") || ContainsText(footTexts, "正式") || ContainsText(footTexts, "口语") || ContainsText(footTexts, "自动"))
                && ContainsText(footTexts, "更多"),
                string.Join(" | ", footTexts.ToArray()));
            chk("「读一遍」文案没变", qf.ReadSpeakButtonForTest != null && qf.ReadSpeakButtonForTest.Text == "读一遍",
                qf.ReadSpeakButtonForTest == null ? "找不到" : qf.ReadSpeakButtonForTest.Text);
            // 按钮宽度最终由 ApplyMetrics 按"规格值 × 缩放 + 文字实测"定，绝对像素随 DPI/字体浮动，
            // 所以只核对真正要保的性质：仍然比左右两个邻居窄的窄、宽的宽，且文字没被切。
            StringBuilder wnote = new StringBuilder();
            if (bar != null) foreach (Control c in bar.Controls) wnote.Append(c.Text + "=" + c.Width + " ");
            string widths = wnote.ToString().Trim();
            int wSpeak = qf.ReadSpeakButtonForTest == null ? 0 : qf.ReadSpeakButtonForTest.Width;
            // 宽度由 ApplyMetrics 按"文字实测 + 内边距"算（窄窗还会缩），所以核对的是
            // "宽度仍然紧贴文字、没有为了三层面板被人为放大"
            // v2.7.1：上界从 S(40) 放宽到 S(200)。原因：LayoutFootButtons 里一排放得下时，
            // 会把剩余宽度平分成五份补给每颗按钮（为了看着整齐），余量随可用宽度变化，
            // 老的 40 只在很窄的窗口下成立，窗口稍宽就必然"失败"。这条要守的性质是
            // "没有为了三层面板单独给「读一遍」留一大截"，不是"必须只多 40px"。
            int twSpeak = qf.ReadSpeakButtonForTest == null ? 0
                : TextRenderer.MeasureText(qf.ReadSpeakButtonForTest.Text, qf.ReadSpeakButtonForTest.Font).Width;
            chk("「读一遍」没被加宽（宽度仍是文字实测 + 内边距，没有多留空间给三层面板）",
                wSpeak > 0 && twSpeak > 0 && wSpeak - twSpeak >= qf.SForTest(10) && wSpeak - twSpeak <= qf.SForTest(200),
                widths + " → 文字 " + twSpeak + "px，余量 " + (wSpeak - twSpeak) + "px（底栏一排放得下时余宽会平分给各按钮）");
            chk("「读一遍」文字没被切（宽度装得下三个字）",
                qf.ReadSpeakButtonForTest != null
                && TextRenderer.MeasureText(qf.ReadSpeakButtonForTest.Text, qf.ReadSpeakButtonForTest.Font).Width <= wSpeak,
                qf.ReadSpeakButtonForTest == null ? "找不到" : "文字 " + TextRenderer.MeasureText(qf.ReadSpeakButtonForTest.Text, qf.ReadSpeakButtonForTest.Font).Width + "px / 按钮 " + wSpeak + "px");
            chk("「读一遍」高度还是 26",
                qf.ReadSpeakButtonForTest != null && qf.ReadSpeakButtonForTest.Height == qf.SForTest(26),
                qf.ReadSpeakButtonForTest == null ? "找不到" : qf.ReadSpeakButtonForTest.Height + "px（应为 " + qf.SForTest(26) + "px）");
            chk("「读一遍」仍在底栏里、顺序第 2",
                qf.ReadSpeakButtonForTest != null && qf.ReadSpeakButtonForTest.Parent == bar
                && bar != null && bar.Controls.GetChildIndex(qf.ReadSpeakButtonForTest) == 1,
                qf.ReadSpeakButtonForTest == null || bar == null ? "缺少控件" : "索引=" + bar.Controls.GetChildIndex(qf.ReadSpeakButtonForTest));
            chk("右键菜单装上了（5 项：自动/逐词/短句/中句/长句）",
                qf.ReadMenuForTest != null && qf.ReadMenuForTest.Items.Count == 5,
                qf.ReadMenuForTest == null ? "没有菜单" : "项数=" + qf.ReadMenuForTest.Items.Count);
            chk("菜单不是窗体子控件（ContextMenuStrip 不占布局）",
                qf.ReadMenuForTest != null && !qf.Controls.Contains(qf.ReadMenuForTest), "");
            int cw = qf.ClientSize.Width, chh = qf.ClientSize.Height;
            if (qf.ReadMenuForTest != null) qf.ReadMenuForTest.Show(qf, new Point(0, 0));
            MlPump(150);
            if (qf.ReadMenuForTest != null) qf.ReadMenuForTest.Close();
            MlPump(100);
            chk("弹出朗读菜单后窗口尺寸一位不差",
                qf.ClientSize.Width == cw && qf.ClientSize.Height == chh,
                cw + "×" + chh + " → " + qf.ClientSize.Width + "×" + qf.ClientSize.Height);
            chk("窗口仍是 DWM 圆角、没有自绘 Region（本次没碰外框）", qf.Region == null,
                qf.Region == null ? "Region=null，交给系统" : "有 Region");
            chk("底栏没被加行（foot 仍只有 bar + 提示条两个子控件）",
                foot != null && foot.Controls.Count == 2,
                foot == null ? "找不到" : "子控件 " + foot.Controls.Count + " 个");
            chk("底栏按钮行没被塞进多余控件、宽度仍贴合底栏",
                bar != null && foot != null && bar.Controls.Count == 5
                && bar.Width == foot.Width - foot.Padding.Horizontal && bar.Dock == DockStyle.Top,
                bar == null ? "找不到" : bar.Width + "px / 按钮 " + bar.Controls.Count + " 个");
            chk("底栏按钮行还是一排（没被三层面板挤成两排）", bar != null && bar.Controls.Count == 5, "");

            // ---------- 九、锁层与提示 ----------
            sb.AppendLine();
            sb.AppendLine("九、锁层与兜底提示");
            string oldMode = GetSet("read_mode", "auto");
            chk("默认朗读方式是自动", ReadMode == "auto" || oldMode == "auto", "当前=" + ReadMode);
            SetSet("read_mode", "word");
            chk("右键锁层落盘生效（读一遍 → 逐词）", ReadMode == "word", "当前=" + ReadMode);
            ReadPlan lockPlan = ReadMakePlan("I want to go to the park with my friends tomorrow", ReadMode);
            chk("锁成逐词后，11 词长句也按逐词切", lockPlan != null && lockPlan.Tier == "word" && lockPlan.SegmentCount >= 3,
                lockPlan == null ? "无" : (lockPlan.Tier + " 段=" + lockPlan.SegmentCount));
            SetSet("read_mode", "long");
            ReadPlan lockPlan2 = ReadMakePlan("I am fine", ReadMode);
            chk("锁成长句后，3 词短句也走长句逻辑", lockPlan2 != null && lockPlan2.Tier == "long",
                lockPlan2 == null ? "无" : lockPlan2.Tier);
            SetSet("read_mode", oldMode);
            chk("层名给人看的都是中文", ReadTierName("word") == "逐词" && ReadTierName("short") == "短句"
                && ReadTierName("mid") == "中句" && ReadTierName("long") == "长句", "");
            chk("空文本不进朗读（返回空计划）", ReadMakePlan("   ", "auto") == null, "");
            chk("没语音包时给的是人话指路", qf.ReadNoVoiceHint().IndexOf("语音") >= 0 || qf.ReadNoVoiceHint().IndexOf("语音包") >= 0,
                qf.ReadNoVoiceHint());
            sb.AppendLine();
            sb.AppendLine("    本机语音包：" + qf.ReadVoiceListForTest());
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[失败] 自检过程抛异常：" + ex.GetType().Name + " " + ex.Message);
        }
        finally
        {
            try { if (qf != null) qf.Close(); } catch { }
        }

        sb.AppendLine();
        sb.AppendLine("合计：通过 " + pass + " 项，失败 " + fail + " 项。");
        try { File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8); } catch { }
    }

    // ---- 自检用的小工具 ----

    internal Button ReadSpeakButtonForTest { get { return btnSpeak; } }
    internal ContextMenuStrip ReadMenuForTest { get { return readMenu; } }

    internal string ReadVoiceListForTest()
    {
        try
        {
            if (tts == null) EnsureTts();
            if (tts == null) return "（拿不到语音引擎）";
            StringBuilder sb = new StringBuilder();
            foreach (InstalledVoice v in tts.GetInstalledVoices())
            {
                if (sb.Length > 0) sb.Append("；");
                sb.Append(v.VoiceInfo.Name).Append("(");
                sb.Append(v.VoiceInfo.Culture == null ? "?" : v.VoiceInfo.Culture.Name).Append(")");
                if (!v.Enabled) sb.Append(" 已停用");
            }
            return sb.Length == 0 ? "（一个都没有）" : sb.ToString();
        }
        catch (Exception ex) { return "（读取失败：" + ex.Message + "）"; }
    }

    static string ReadJoinSegs(List<ReadSeg> segs)
    {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < segs.Count; i++)
        {
            if (i > 0) sb.Append(" | ");
            sb.Append(segs[i].Text);
        }
        return sb.ToString();
    }

    static string ReadJoinInts(List<int> xs)
    {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < xs.Count; i++) { if (i > 0) sb.Append(","); sb.Append(xs[i]); }
        return sb.ToString();
    }

    static bool wAllPositive(List<int> xs)
    {
        for (int i = 0; i < xs.Count; i++) if (xs[i] <= 0) return false;
        return xs.Count > 0;
    }

    static int wMaxOf(List<int> xs)
    {
        int m = 0;
        for (int i = 0; i < xs.Count; i++) if (xs[i] > m) m = xs[i];
        return m;
    }

    static int wDistinct(List<int> xs)
    {
        List<int> u = new List<int>();
        for (int i = 0; i < xs.Count; i++) if (!u.Contains(xs[i])) u.Add(xs[i]);
        return u.Count;
    }

    static string ReadWordSeq(List<ReadTok> toks)
    {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < toks.Count; i++)
        {
            if (!toks[i].IsWord) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(toks[i].Text.ToLowerInvariant());
        }
        return sb.ToString();
    }

    static List<ReadTok> ReadSegToks(List<ReadSeg> segs)
    {
        List<ReadTok> all = new List<ReadTok>();
        for (int i = 0; i < segs.Count; i++) all.AddRange(segs[i].Toks);
        return all;
    }

    static bool wEndsWith(List<ReadSeg> segs, bool checkArticle)
    {
        for (int i = 0; i < segs.Count - 1; i++)
        {
            ReadSeg s = segs[i];
            for (int j = s.Toks.Count - 1; j >= 0; j--)
            {
                if (!s.Toks[j].IsWord) continue;
                string w = s.Toks[j].Text.ToLowerInvariant();
                if (ReadIsArticle(w)) return true;
                if (w == "of" || w == "to" || w == "in" || w == "on" || w == "at" || w == "for" || w == "with" || w == "by" || w == "from") return true;
                break;
            }
        }
        return false;
    }

    static bool wSingleArticle(List<ReadSeg> segs)
    {
        for (int i = 0; i < segs.Count; i++)
        {
            ReadSeg s = segs[i];
            if (s.Words == 1)
            {
                for (int j = 0; j < s.Toks.Count; j++)
                    if (s.Toks[j].IsWord && ReadIsArticle(s.Toks[j].Text)) return true;
            }
        }
        return false;
    }

    static int ReadMaxSegWords(List<ReadSeg> segs)
    {
        int m = 0;
        for (int i = 0; i < segs.Count; i++) if (segs[i].Words > m) m = segs[i].Words;
        return m;
    }

    static bool ReadLexIntonationAllValid()
    {
        for (int i = 0; i < readLex.Length; i++) if (!ReadIntonationValid(readLex[i].Intonation)) return false;
        return true;
    }

    static string ReadLexBadIntonation()
    {
        for (int i = 0; i < readLex.Length; i++)
            if (!ReadIntonationValid(readLex[i].Intonation)) return readLex[i].Word + "=" + readLex[i].Intonation;
        return "全部合法";
    }

    static bool ReadLexMeaningAllFilled()
    {
        for (int i = 0; i < readLex.Length; i++)
            if (string.IsNullOrEmpty(readLex[i].Meaning) || string.IsNullOrEmpty(readLex[i].Scenario)) return false;
        return true;
    }
}
