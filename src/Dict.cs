// ============================================================================
//  饼饼专属中英互译 · Dict.cs（第二档 ⑮ 断网也能查词）
//  ---------------------------------------------------------------------------
//  要解决的事：网断了、代理挂了、机场到期了，按 Ctrl+空格 出来一整屏
//  「连不上服务器」——可那正是她最需要看懂客户那句话的时候。
//
//  做法：把 20225 个常用词（按 BNC + 当代语料库词频挑的）和 20795 条变形词
//  （went→go、customers→customer 这种）编进 exe 当嵌入资源，网断了就地查。
//
//  几条死规矩（跟她原来的红线对齐）：
//    · 不拆文件：词库是嵌入资源，交付还是单个 exe，拷过去就能用；
//    · 不拖慢唤出：只有「联网真的失败」那一下才去加载，平时一次都不碰它；
//    · 不改界面：结果照样走 RenderResult，底栏 5 个按钮一个没动；
//    · 不编造：查不到就说没查到，绝不猜一个意思糊弄她。
//
//  能力边界（先写清楚，免得她期待落空）：
//    能：英文单词 / 短词组 → 中文意思 + 音标；复数过去式自动还原成原型
//    不能：整句通顺翻译、【你可以这样回】话术（那得联网让 AI 现写）
//    不能：中文查英文（本地没建反向索引，硬扫要几百毫秒，不值当）
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

partial class QueryForm
{
    // ====== 词库本体 ======
    // 资源名由 tools\build.ps1 的 /resource:文件,OfflineDict 决定，改名要两边一起改。
    const string DICT_RES = "OfflineDict";
    const int DICT_MAX_WORDS = 14;        // 一句里最多列这么多个词，再多框里就装不下了
    const int DICT_MIN_EXPECT = 20000;    // 少于这个数说明词库被裁坏了，自检要报出来

    static Dictionary<string, string[]> dictMain;   // 词 → [音标, 中文释义]
    static Dictionary<string, string> dictExch;     // 变形词 → 原型
    static bool dictInited;                         // 只加载一次（成功失败都算数，免得反复试）
    static string dictError;                        // 加载失败的原因（人话）
    static readonly object dictLock = new object();

    // 英文词：字母开头，允许中间带 ' 和 -（don't、e-mail、well-known）
    static readonly Regex DictWordRe = new Regex(@"[A-Za-z][A-Za-z'\-]*");

    // ================================================================
    //  一、加载
    // ================================================================
    // 懒加载：第一次真要查词了才读资源。启动、唤出、联网查询全程都不碰。
    static void EnsureDict()
    {
        if (dictInited) return;
        lock (dictLock)
        {
            if (dictInited) return;
            dictInited = true;
            try
            {
                Stream s = typeof(QueryForm).Assembly.GetManifestResourceStream(DICT_RES);
                if (s == null)
                {
                    dictError = "这个 exe 里没带离线词库（编的时候漏了 /resource）";
                    Log("离线词库加载失败：" + dictError);
                    return;
                }
                Dictionary<string, string[]> main = new Dictionary<string, string[]>(40000, StringComparer.Ordinal);
                Dictionary<string, string> exch = new Dictionary<string, string>(40000, StringComparer.Ordinal);
                using (s)
                using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                {
                    string line;
                    bool inExch = false;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (line.Length == 0) continue;
                        if (line[0] == '#')
                        {
                            if (line.StartsWith("#EXCH")) inExch = true;
                            continue;
                        }
                        if (inExch)
                        {
                            int t = line.IndexOf('\t');
                            if (t > 0 && t < line.Length - 1)
                                exch[line.Substring(0, t)] = line.Substring(t + 1);
                        }
                        else
                        {
                            string[] p = line.Split('\t');
                            if (p.Length < 3 || p[0].Length == 0) continue;
                            main[p[0]] = new string[] { p[1], p[2] };
                        }
                    }
                }
                dictMain = main;
                dictExch = exch;
                Log("离线词库就绪：" + main.Count + " 词 / " + exch.Count + " 条变形词");
            }
            catch (Exception ex)
            {
                dictError = "离线词库读不出来（" + ex.GetType().Name + "）";
                Log("离线词库加载异常：" + ex.Message);
            }
        }
    }

    // 词库现在能不能用（界面提示用）
    internal static bool DictReady
    {
        get
        {
            EnsureDict();
            return dictMain != null && dictMain.Count > 0;
        }
    }

    internal static int DictWordCount { get { EnsureDict(); return dictMain == null ? 0 : dictMain.Count; } }
    internal static string DictError { get { return dictError; } }

    // ================================================================
    //  二、查一个词
    // ================================================================
    // 先查原型，查不到再走变形表还原（went → go），都查不到就认账。
    // origin 不为空表示这个词是变形，值就是原型，界面上要标一句。
    static bool DictFind(string word, out string phonetic, out string meaning, out string origin)
    {
        phonetic = null;
        meaning = null;
        origin = null;
        if (string.IsNullOrEmpty(word)) return false;
        EnsureDict();
        if (dictMain == null) return false;

        string k = word.Trim().Trim('\'', '-').ToLowerInvariant();
        if (k.Length == 0) return false;

        string[] v;
        if (dictMain.TryGetValue(k, out v))
        {
            phonetic = v[0];
            meaning = v[1];
            return true;
        }
        string baseWord;
        if (dictExch != null && dictExch.TryGetValue(k, out baseWord) && dictMain.TryGetValue(baseWord, out v))
        {
            phonetic = v[0];
            meaning = v[1];
            origin = baseWord;
            return true;
        }
        return false;
    }

    // ================================================================
    //  三、把一句话变成「离线释义」结果
    // ================================================================
    // 返回 null 表示离线帮不上忙（中文输入 / 一个词都没查到 / 词库没装），
    // 调用方拿到 null 就照原样显示联网那条报错，什么都不用改。
    internal static string OfflineAnswer(string text, out int hit, out int miss)
    {
        hit = 0;
        miss = 0;
        if (string.IsNullOrEmpty(text)) return null;
        if (HasChinese(text)) return null;          // 中文查英文本地不管，别硬撑
        if (!DictReady) return null;

        // 抽词 + 去重（保住出现的先后顺序，查出来的词跟她看到的句子顺序一致）
        List<string> words = new List<string>();
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in DictWordRe.Matches(text))
        {
            string w = m.Value.Trim('\'', '-').ToLowerInvariant();
            if (w.Length == 0) continue;
            if (seen.Add(w)) words.Add(w);
        }
        if (words.Count == 0) return null;

        List<string> lines = new List<string>();
        List<string> lost = new List<string>();
        int over = 0;
        for (int i = 0; i < words.Count; i++)
        {
            string ph, mean, origin;
            if (DictFind(words[i], out ph, out mean, out origin))
            {
                hit++;
                if (lines.Count < DICT_MAX_WORDS)
                {
                    string head = words[i];
                    if (!string.IsNullOrEmpty(origin)) head = head + "（" + origin + " 的变形）";
                    string ipa = string.IsNullOrEmpty(ph) ? "—" : ph;
                    lines.Add(head + " | " + ipa + " | " + Brief(mean, 70));
                }
                else over++;
            }
            else
            {
                miss++;
                if (lost.Count < 8) lost.Add(words[i]);
            }
        }

        if (hit == 0) return null;      // 一个都没查到，不如让她看原来的报错

        StringBuilder sb = new StringBuilder();
        if (words.Count == 1)
        {
            // 就一个词：按「单词卡」排，一眼看到读音和意思
            string ph, mean, origin;
            DictFind(words[0], out ph, out mean, out origin);
            sb.AppendLine("【离线释义】" + words[0] + "（网断了，这是本地词库查的）");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(origin)) sb.AppendLine("原型 | " + origin);
            sb.AppendLine("读音 | " + (string.IsNullOrEmpty(ph) ? "这个词没配音标" : ph));
            sb.AppendLine("意思 | " + mean);
        }
        else
        {
            sb.AppendLine("【离线释义】网断了，本地词库替你查了 " + hit + " 个词（整句翻译和回复话术要等联网）");
            sb.AppendLine();
            foreach (string ln in lines) sb.AppendLine(ln);
            if (over > 0) sb.AppendLine("……还有 " + over + " 个词没列出来，句子太长啦");
        }

        if (lost.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("【没查到】" + string.Join("、", lost.ToArray())
                + (miss > lost.Count ? " 等 " + miss + " 个" : "")
                + "（本地词库只有常用词，这几个联网才查得准）");
        }

        sb.AppendLine();
        sb.AppendLine("【说明】离线只给单词意思，整句翻译和「你可以这样回」要联网；网一回来点「再试一次」就是完整结果。");
        return sb.ToString();
    }

    internal static string OfflineAnswer(string text)
    {
        int hit, miss;
        return OfflineAnswer(text, out hit, out miss);
    }

    // 列表里一行放不下几百字的释义：截到 max 字，尽量断在分号/逗号上，
    // 免得她把「…付还, 偿还借」这种半截词当完整意思记下来。单词卡模式不截。
    static string Brief(string mean, int max)
    {
        if (string.IsNullOrEmpty(mean) || mean.Length <= max) return mean;
        string cut = mean.Substring(0, max);
        int at = cut.LastIndexOfAny(new char[] { '；', ';', '，', ',' });
        if (at > max / 2) cut = cut.Substring(0, at);
        cut = cut.TrimEnd('；', ';', '，', ',', ' ');
        return cut + "…";
    }

    // ================================================================
    //  四、什么时候才该走离线
    // ================================================================
    // 只有「网络本身不通」才降级。Key 错、余额没了、查太快被限流这三类必须原样报出去，
    // 一旦被离线结果盖住，她就永远不知道要去充钱或者换 Key。
    internal static bool IsNetworkishError(string raw)
    {
        string s = (raw == null ? "" : raw).ToLowerInvariant();

        // 先排除「不是网络问题」的三种
        if (s.Contains("401") || s.Contains("invalid_api_key") || s.Contains("incorrect api key")
            || s.Contains("unauthorized") || s.Contains("authentication") || s.Contains("invalid token")) return false;
        if (s.Contains("402") || s.Contains("insufficient") || s.Contains("quota") || s.Contains("balance")
            || s.Contains("余额") || s.Contains("欠费") || s.Contains("arrear")) return false;
        if (s.Contains("429") || s.Contains("rate limit") || s.Contains("too many requests")) return false;

        if (s.Contains("timed out") || s.Contains("timeout") || s.Contains("超时")) return true;
        if (s.Contains("name resolution") || s.Contains("no such host") || s.Contains("dns")
            || s.Contains("无法解析") || s.Contains("找不到服务器")) return true;
        if (s.Contains("connection") || s.Contains("refused") || s.Contains("unreachable")
            || s.Contains("无法连接") || s.Contains("连不上") || s.Contains("网络")) return true;
        // .NET 断网最常见的原话就是「基础连接已经关闭: 连接被意外关闭。」——
        // 里面一个「无法连接」都没有，只靠上面那几条会漏，这里按「连接 + 坏消息」认。
        if (s.Contains("基础连接已经关闭") || s.Contains("连接被意外关闭") || s.Contains("连接已关闭")
            || s.Contains("连接中断") || s.Contains("底层连接") || s.Contains("远程主机强迫关闭")) return true;
        if (s.Contains("unable to connect") || s.Contains("no internet") || s.Contains("offline")) return true;
        return false;
    }

    // ================================================================
    //  四点半、自检专用：把界面切到「断网离线查词」那一幕
    // ================================================================
    // 只给 --dictshot 用，平时不调。走的还是正式那条路（RenderResult + 重试条），
    // 所以截出来的样子就是她断网时真看到的。
    // 自检用：按「单词 → 整句 → 离线接不了的中文」轮着往界面上摆，外面好截三张图
    internal void OfflineDemoPump()
    {
        string[] texts = new string[] { "refund", "Sorry, your package arrived broken. Can we get a refund?", "你好，在吗" };
        int k = 0;
        ShowOfflineDemo(OfflineAnswer(texts[0]));
        System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
        t.Interval = 1300;
        t.Tick += delegate
        {
            k++;
            if (k >= texts.Length) { t.Stop(); return; }
            ShowOfflineDemo(OfflineAnswer(texts[k]));
        };
        t.Start();
    }

    internal void ShowOfflineDemo(string content)
    {
        if (content == null)
        {
            // 离线帮不上忙 → 照原样显示联网报错，界面不该有任何不同
            SetBoxText(HumanizeError("无法连接到远程服务器"));
            tip.Text = "出错了，点「再试一次」";
        }
        else
        {
            RenderResult(content);
            tip.Text = "网断了，这是本地词库查的｜网回来点再试一次";
        }
        ShowErrorBar(true);
        Application.DoEvents();
        PerformLayout();
        Application.DoEvents();
    }

    // ================================================================
    //  五、自检：--dictcheck
    // ================================================================
    internal static void DictCheck(string outPath)
    {
        StringBuilder sb = new StringBuilder();
        int pass = 0, fail = 0;
        Action<string, bool, string> chk = delegate (string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            sb.AppendLine("[" + (ok ? "通过" : "失败") + "] " + name + (string.IsNullOrEmpty(detail) ? "" : "　→ " + detail));
        };

        sb.AppendLine("断网离线查词自检（离线词库 / 变形词还原 / 能力边界）");
        sb.AppendLine();
        sb.AppendLine("== 一、词库加载 ==");
        long t0 = Stopwatch.GetTimestamp();
        EnsureDict();
        long loadMs = (long)((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
        sb.AppendLine("  加载耗时 " + loadMs + " ms");
        chk("词库能读出来（嵌入资源在）", DictReady, dictError);
        chk("词条数够（≥" + DICT_MIN_EXPECT + "）", DictWordCount >= DICT_MIN_EXPECT, DictWordCount + " 词");
        chk("变形词表也在", dictExch != null && dictExch.Count > 10000, (dictExch == null ? 0 : dictExch.Count) + " 条");
        chk("加载够快（<500ms，不拖慢唤出）", loadMs < 500, loadMs + " ms");

        sb.AppendLine();
        sb.AppendLine("== 二、查词正确性 ==");
        string ph, mean, origin;
        bool okC1 = DictFind("customer", out ph, out mean, out origin);
        sb.AppendLine("  customer → " + ph + " | " + (mean == null ? "" : mean));
        chk("常用词能查到", okC1 && mean != null && mean.Length > 4, mean);
        bool okC2 = DictFind("refund", out ph, out mean, out origin);
        sb.AppendLine("  refund → " + ph + " | " + (mean == null ? "" : mean));
        chk("工作词（退款）能查到", okC2 && mean != null && mean.Contains("偿还"), mean);

        bool okC3 = DictFind("went", out ph, out mean, out origin);
        sb.AppendLine("  went → 原型 " + origin + " | " + (mean == null ? "" : mean));
        chk("过去式还原成原型（went→go）", okC3 && origin == "go", origin);
        bool okC4 = DictFind("customers", out ph, out mean, out origin);
        sb.AppendLine("  customers → 原型 " + origin);
        chk("复数还原成原型（customers→customer）", okC4 && origin == "customer", origin);

        bool okC5 = !DictFind("zzzzqqqqx", out ph, out mean, out origin);
        chk("生造词老实说查不到（不瞎猜）", okC5, "zzzzqqqqx");

        sb.AppendLine();
        sb.AppendLine("== 三、能力边界 ==");
        chk("中文输入不硬撑（返回 null，走原来的报错）", OfflineAnswer("你好，在吗") == null, "");
        chk("空输入不出结果", OfflineAnswer("") == null, "");
        chk("纯符号不出结果", OfflineAnswer("!!! ... ???") == null, "");
        string oneOut = OfflineAnswer("refund");
        chk("单个词给「单词卡」格式", oneOut != null && oneOut.Contains("【离线释义】") && oneOut.Contains("读音 |") && oneOut.Contains("意思 |"), "");
        string sentOut = OfflineAnswer("Sorry, the zzzzqqqqx arrived broken. Can we get a refund?");
        sb.AppendLine("  整句结果 → " + (sentOut == null ? "null" : sentOut.Split('\n').Length + " 行"));
        chk("整句能挑出词并列出意思", sentOut != null && sentOut.Contains("【离线释义】") && sentOut.Contains("refund"), "");
        chk("结果里如实写明「整句翻译要联网」", sentOut != null && sentOut.Contains("要联网"), "");
        chk("不认识的不同词会进【没查到】", sentOut != null && sentOut.Contains("【没查到】"), "");

        sb.AppendLine();
        sb.AppendLine("== 四、什么时候不该走离线 ==");
        chk("断网类报错才降级（连不上服务器）", IsNetworkishError("基础连接已经关闭: 连接被意外关闭。"), "");
        chk("断网类报错才降级（底层连接被重置）", IsNetworkishError("无法从传输连接读取数据: 远程主机强迫关闭了一个现有的连接。"), "");
        chk("断网类报错才降级（域名解析不了）", IsNetworkishError("远程名称无法解析: 'api.deepseek.com'"), "");
        chk("断网类报错才降级（超时）", IsNetworkishError("操作超时 The operation has timed out"), "");
        chk("Key 错了不降级（要让她看见去改 Key）", !IsNetworkishError("远程服务器返回错误: (401) 未经授权。"), "");
        chk("余额没了不降级（要让她看见去充钱）", !IsNetworkishError("402 Insufficient Balance"), "");
        chk("被限流不降级（要让她看见等一会儿）", !IsNetworkishError("429 rate limit reached"), "");

        sb.AppendLine();
        sb.AppendLine("== 五、数据干净度 ==");
        bool tabClean = true, cyrClean = true;
        int checkedWords = 0;
        foreach (KeyValuePair<string, string[]> kv in dictMain)
        {
            if (checkedWords >= 3000) break;
            checkedWords++;
            if (kv.Value[1].IndexOf('\t') >= 0) tabClean = false;
            if (kv.Value[1].IndexOf('\r') >= 0 || kv.Value[1].IndexOf('\n') >= 0) tabClean = false;
            if (kv.Value[0].IndexOf('\u04d9') >= 0 || kv.Value[1].IndexOf('\u04d9') >= 0) cyrClean = false;
        }
        chk("释义里没有制表符/换行（不会撑破排版）", tabClean, "抽查 " + checkedWords + " 条");
        chk("音标里没有西里尔字母（不会显示成方块）", cyrClean, "抽查 " + checkedWords + " 条");
        chk("词条键全是小写（大写输入也能查到）",
            DictFind("Customer", out ph, out mean, out origin) && DictFind("CUSTOMER", out ph, out mean, out origin), "");

        sb.AppendLine();
        sb.AppendLine("== 六、查询速度 ==");
        int N = 2000;
        long t1 = Stopwatch.GetTimestamp();
        int okCnt = 0;
        for (int i = 0; i < N; i++)
        {
            if (DictFind("customer", out ph, out mean, out origin)) okCnt++;
        }
        double perMs = (Stopwatch.GetTimestamp() - t1) * 1000.0 / Stopwatch.Frequency / N;
        sb.AppendLine("  " + N + " 次查询共 " + ((Stopwatch.GetTimestamp() - t1) * 1000.0 / Stopwatch.Frequency).ToString("F1") + " ms，平均 " + perMs.ToString("F4") + " ms/次");
        chk("单次查询够快（<1ms，体感无等待）", perMs < 1.0, perMs.ToString("F4") + " ms");
        chk("连查多次结果稳定", okCnt == N, okCnt + "/" + N);

        sb.AppendLine();
        sb.AppendLine("合计 " + (pass + fail) + " 项：通过 " + pass + " / 失败 " + fail);
        sb.AppendLine(fail == 0
            ? "结论：全过。断网时能查词，中文和整句翻译仍然要联网，这条边界守住了。"
            : "结论：有失败项，见上。");
        try { File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true)); } catch { }
    }
}
