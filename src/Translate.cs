// ============================================================================
//  翻译器 · Translate.cs（Translate）
//  联网翻译与语音：DeepSeek 接口调用、SSE 流式解析、请求体拼装、TTS 朗读、截图 OCR。
//  所有「出网、出声、认字」的代码都在这个文件里。
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
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

partial class QueryForm
{
    const string API_BASE = "https://api.deepseek.com/v1";

    // ------------------------------------------------------------------------
    //  代理分流（v2.1 F2）
    //  系统挂着 127.0.0.1:7890 这类代理时，HttpWebRequest 默认继承系统代理，
    //  连本机 127.0.0.1:11434（本地模型）也会被代理劫走，现象是「本地模型莫名其妙连不上」。
    //  规则：目标是回环/内网地址 → 直连；公网 → 跟随系统代理。
    // ------------------------------------------------------------------------
    static bool IsLocalHost(string host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        host = host.Trim().ToLower();
        if (host == "localhost" || host == "::1" || host == "[::1]") return true;
        IPAddress ip;
        if (!IPAddress.TryParse(host.Trim('[', ']'), out ip)) return false;
        if (IPAddress.IsLoopback(ip)) return true;
        byte[] b = ip.GetAddressBytes();
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && b.Length == 4)
        {
            if (b[0] == 127) return true;                                        // 127/8 回环
            if (b[0] == 10) return true;                                         // 10/8
            if (b[0] == 192 && b[1] == 168) return true;                         // 192.168/16
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;            // 172.16/12
            if (b[0] == 169 && b[1] == 254) return true;                         // 链路本地
        }
        return false;
    }

    static string HostOf(string url)
    {
        try { return new Uri(url).Host; } catch { return ""; }
    }

    // ---- 自检：--proxycheck（F2 代理分流验证）--------------------------------
    // 1) 打印各地址的代理判定；2) 起本机小监听，分别用「旧行为(跟随系统代理)」和
    //    「新行为(统一出口自动直连)」请求它，看旧的是不是被代理劫走、新的是不是通。
    internal static void ProxyCheck(string outPath)
    {
        StringBuilder sb = new StringBuilder();
        TcpListener srv = null;
        try
        {
            IWebProxy sysProxy = WebRequest.DefaultWebProxy;
            string sysText = "无";
            try { if (sysProxy != null) sysText = sysProxy.GetProxy(new Uri("https://api.deepseek.com/")).ToString(); }
            catch { sysText = "读取失败"; }
            sb.AppendLine("系统默认代理 = " + sysText);
            sb.AppendLine();

            string[] samples = new string[] {
                "https://api.deepseek.com/v1/chat/completions",
                "http://127.0.0.1:11434/api/chat",
                "http://localhost:11434/api/chat",
                "http://[::1]:11434/api/chat",
                "http://192.168.1.10:8080/x",
                "http://10.0.0.5:5000/x",
                "http://172.20.3.4/x",
                "http://172.32.0.1/x"
            };
            foreach (string u in samples)
                sb.AppendLine("[判定] " + u + "  ->  " + (IsLocalHost(HostOf(u)) ? "直连(Proxy=null)" : "跟随系统代理"));
            sb.AppendLine();

            int targetPort = 18765;
            int proxyPort = 18766;
            int[] targetHits = new int[1];
            int[] proxyHits = new int[1];
            srv = StartEchoServer(IPAddress.Loopback, targetPort, targetHits, "{\"ok\":true,\"from\":\"target\"}");
            TcpListener fakeProxy = StartEchoServer(IPAddress.Loopback, proxyPort, proxyHits, "{\"ok\":true,\"from\":\"fakeproxy\"}");
            Thread.Sleep(150);
            try
            {
                string loopUrl = "http://127.0.0.1:" + targetPort + "/api/chat";
                sb.AppendLine("[实测A·回环地址] " + loopUrl);
                sb.AppendLine("    新行为(统一出口 -> 自动直连): " + TryPost(loopUrl, false, 5000));
                sb.AppendLine("    目标服务收到 = " + targetHits[0]);
                sb.AppendLine();

                // 把默认代理换成一个「假代理」（也是本机 echo，会记账），用它当探针，
                // 就能看清请求到底是交给了代理，还是自己直连出去了。
                IWebProxy keep = WebRequest.DefaultWebProxy;
                try
                {
                    WebRequest.DefaultWebProxy = new WebProxy("http://127.0.0.1:" + proxyPort);
                    string lanUrl = "http://192.168.1.10:" + targetPort + "/api/chat";
                    sb.AppendLine("[实测B·内网地址 192.168.1.10（本机并没有这个地址），默认代理已换成假代理]");
                    int p0 = proxyHits[0];
                    sb.AppendLine("    旧行为(跟随代理): " + TryPost(lanUrl, true, 4000));
                    sb.AppendLine("      -> 假代理收到 = " + (proxyHits[0] - p0) + " 次（大于 0 就是被代理劫走）");
                    int p1 = proxyHits[0];
                    sb.AppendLine("    新行为(判为内网 -> 直连): " + TryPost(lanUrl, false, 4000));
                    sb.AppendLine("      -> 假代理收到 = " + (proxyHits[0] - p1) + " 次（应为 0，说明根本没过代理）");
                }
                finally { WebRequest.DefaultWebProxy = keep; }
                sb.AppendLine();

                sb.AppendLine("[实测C·公网] https://api.deepseek.com/v1/chat/completions（不带 Key，401 也算通路正常）");
                sb.AppendLine("    " + TryPost("https://api.deepseek.com/v1/chat/completions", false, 20000));
                sb.AppendLine();
                sb.AppendLine("目标服务总收到 = " + targetHits[0] + "   假代理总收到 = " + proxyHits[0]);
            }
            finally { try { fakeProxy.Stop(); } catch { } }
        }
        catch (Exception ex)
        {
            sb.AppendLine("自检异常: " + ex.GetType().Name + " " + ex.Message);
        }
        finally
        {
            try { if (srv != null) srv.Stop(); } catch { }
            try { File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true)); } catch { }
        }
    }

    static TcpListener StartEchoServer(IPAddress bindAddress, int port, int[] counter, string replyBody)
    {
        TcpListener lst = new TcpListener(bindAddress, port);
        lst.Start();
        TcpListener captured = lst;
        Thread th = new Thread(delegate()
        {
                while (true)
                {
                    TcpClient c = null;
                    try { c = captured.AcceptTcpClient(); }
                    catch { return; }
                    try
                    {
                        NetworkStream ns = c.GetStream();
                        ns.ReadTimeout = 4000;
                        ns.WriteTimeout = 4000;
                        byte[] buf = new byte[8192];
                        int total = 0;
                        int headEnd = -1;
                        while (total < buf.Length)
                        {
                            int n = ns.Read(buf, total, buf.Length - total);
                            if (n <= 0) break;
                            total += n;
                            string sofar = Encoding.ASCII.GetString(buf, 0, total);
                            headEnd = sofar.IndexOf("\r\n\r\n");
                            if (headEnd >= 0) break;
                        }
                        // 必须把请求体读完再回包：不读干净就 close，客户端会收到 RST，
                        // 表现为「远程主机强迫关闭了一个现有的连接」，会把测试结果带偏。
                        if (headEnd >= 0)
                        {
                            string header = Encoding.ASCII.GetString(buf, 0, headEnd + 4);
                            int bodyLen = 0;
                            Match m = Regex.Match(header, @"(?i)content-length:\s*(\d+)");
                            if (m.Success) int.TryParse(m.Groups[1].Value, out bodyLen);
                            int got = total - (headEnd + 4);
                            while (got < bodyLen)
                            {
                                int n2 = ns.Read(buf, 0, Math.Min(buf.Length, bodyLen - got));
                                if (n2 <= 0) break;
                                got += n2;
                            }
                        }
                        byte[] body = Encoding.UTF8.GetBytes(replyBody);
                        string head = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "
                                    + body.Length + "\r\nConnection: close\r\n\r\n";
                        byte[] hb = Encoding.ASCII.GetBytes(head);
                        ns.Write(hb, 0, hb.Length);
                        ns.Write(body, 0, body.Length);
                        ns.Flush();
                        Interlocked.Increment(ref counter[0]);
                        try { c.Client.Shutdown(System.Net.Sockets.SocketShutdown.Both); } catch { }
                        Thread.Sleep(80);
                    }
                    catch { }
                    finally { try { c.Close(); } catch { } }
                }
            });
        th.IsBackground = true;
        th.Start();
        return captured;
    }

    static string TryPost(string url, bool forceSystemProxy, int timeoutMs)
    {
        try
        {
            byte[] body = Encoding.UTF8.GetBytes("{\"messages\":[]}");
            HttpWebRequest req = CreateJsonPost(url, "", body);
            if (forceSystemProxy) req.Proxy = WebRequest.DefaultWebProxy;  // 复现旧行为
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            using (Stream s = req.GetRequestStream()) { s.Write(body, 0, body.Length); }
            using (HttpWebResponse r = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
            {
                string t = sr.ReadToEnd();
                if (t.Length > 120) t = t.Substring(0, 120);
                return "成功 HTTP " + (int)r.StatusCode + "  返回=" + t;
            }
        }
        catch (WebException we)
        {
            string t = "";
            try
            {
                if (we.Response != null)
                    using (StreamReader sr = new StreamReader(we.Response.GetResponseStream(), Encoding.UTF8))
                    {
                        t = sr.ReadToEnd();
                        if (t.Length > 120) t = t.Substring(0, 120);
                    }
            }
            catch { }
            return "失败 " + we.Status + ": " + we.Message + (t.Length > 0 ? "  返回=" + t : "");
        }
        catch (Exception ex) { return "失败 " + ex.GetType().Name + ": " + ex.Message; }
    }

    // 统一的 JSON POST 出口：所有出网请求都从这里走，代理规则只在这一处决定
    static HttpWebRequest CreateJsonPost(string url, string key, byte[] body)
    {
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
        req.Method = "POST";
        req.ContentType = "application/json";
        req.Timeout = 40000;
        req.ReadWriteTimeout = 40000;
        req.AllowReadStreamBuffering = false;
        if (key != null && key.Length > 0) req.Headers["Authorization"] = "Bearer " + key;
        // ★ 本地/内网目标直连，绕开系统代理；公网目标显式跟随系统代理
        if (IsLocalHost(HostOf(url))) req.Proxy = null;
        else req.Proxy = WebRequest.DefaultWebProxy;
        if (body != null) req.ContentLength = body.Length;
        return req;
    }
    // API Key 不写死在源码里（仓库要公开）：运行时从 %APPDATA%\SolivineQuickLook\apikey.txt 读取
    static string apiKeyCache = null;

    static string ApiKeyFile { get { return Path.Combine(LOG_DIR, "apikey.txt"); } }

    static string ApiKey
    {
        get
        {
            if (apiKeyCache != null) return apiKeyCache;
            apiKeyCache = "";
            try
            {
                string keyPath = ApiKeyFile;
                if (!File.Exists(keyPath)) return apiKeyCache;
                // v2.5.9 第一档①：清洗规则抽到 ApiKeySetup.cs 的 CleanKeyText，
                // 和「填 Key 小窗」里保存、测试用的是同一套，避免「存进去了却读不出来」。
                apiKeyCache = CleanKeyText(ReadTextAuto(keyPath));
            }
            catch { apiKeyCache = ""; }
            return apiKeyCache;
        }
    }

    const string MODEL = "deepseek-flash";

    void EnsureTts()
    {
        if (tts != null) return;
        tts = new SpeechSynthesizer();
        int rate = GetInt("rate", -1);
        if (rate < -10) rate = -10;
        if (rate > 10) rate = 10;
        tts.Rate = rate;
        tts.Volume = 100;
        string picked = FindVoice(GetSet("voice", "Zira"));
        if (picked == null) picked = FindVoice("David");
        if (picked == null) picked = FindEnglishVoice();
        if (picked != null)
        {
            try { tts.SelectVoice(picked); } catch { }
        }
    }

    string FindVoice(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        foreach (InstalledVoice v in tts.GetInstalledVoices())
        {
            try
            {
                if (!v.Enabled) continue;
                if (v.VoiceInfo.Name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) return v.VoiceInfo.Name;
            }
            catch { }
        }
        return null;
    }

    string FindEnglishVoice()
    {
        foreach (InstalledVoice v in tts.GetInstalledVoices())
        {
            try
            {
                if (v.Enabled && v.VoiceInfo.Culture != null &&
                    v.VoiceInfo.Culture.TwoLetterISOLanguageName == "en")
                    return v.VoiceInfo.Name;
            }
            catch { }
        }
        return null;
    }

    // 把「近音：xxx」剥掉，让结果更清爽
    static string StripApprox(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (s.IndexOf("近音") < 0) return s;
        string r = Regex.Replace(s, @"[ \t]*[|｜][ \t]*近音[ \t]*[:：][^|｜·\r\n]*", "");
        r = Regex.Replace(r, @"[ \t]*近音[ \t]*[:：][^|｜·\r\n]*", "");
        r = Regex.Replace(r, @"([ \t]*·[ \t]*){2,}", " · ");
        r = Regex.Replace(r, @"[ \t]+·[ \t]*(\r?\n)", "$1");
        r = Regex.Replace(r, @"[ \t]+$", "", RegexOptions.Multiline);
        r = Regex.Replace(r, @"(\r?\n){3,}", "\n\n");
        return r;
    }

    static bool HasChinese(string s)
    {
        foreach (char c in s)
            if (c >= 0x4e00 && c <= 0x9fff) return true;
        return false;
    }

    static string ExtractEnglish(string content)
    {
        int i = content.IndexOf("【英文】");
        if (i < 0) return "";
        int start = i + 4;
        int end = content.IndexOf('\n', start);
        string line = (end < 0) ? content.Substring(start) : content.Substring(start, end - start);
        return line.Trim();
    }

    void SpeakText()
    {
        string s = "";
        if (lastInput.Length > 0 && !HasChinese(lastInput)) s = lastInput;
        else s = ExtractEnglish(box.Text);
        if (s.Length == 0) s = box.Text;
        s = s.Trim();
        if (s.Length == 0) { tip.Text = "还没有内容可读"; return; }
        Say(s);
    }

    // 点哪句读哪句
    void SpeakCurrentLine()
    {
        string t = PickCurrentText();
        if (t.Length == 0) { tip.Text = "点一下要读的那一行，再双击"; return; }
        Say(t);
    }

    void Say(string s)
    {
        // v2.5 第 6 步：朗读统一走三层逻辑（VoiceRead.cs），
        // 这里只做分流——逐词/短句/长句由 ReadSay 按词数决定，读不出来时它自己退回整段读
        if (s == null) s = "";
        if (s.Trim().Length > 0)
        {
            ReadSay(s);
            return;
        }
        try
        {
            EnsureTts();
            tts.SpeakAsyncCancelAll();
            tts.SpeakAsync(s);
            tip.Text = "正在读…";
            Log("朗读：" + (s.Length > 60 ? s.Substring(0, 60) : s));
        }
        catch
        {
            tip.Text = "没找到可用的语音包";
        }
    }

    // ====== 请求序号：连点两次时只认最后一次 ======
    // 每发起一次查询就领一个递增号；线程把结果写回界面之前先对号，号旧了就当场丢掉。
    // 没有这一层的话，两次请求乱序返回时，旧结果会盖掉新结果（第二次点了却看到第一次的译文）。
    static int reqSeq = 0;

    static int NextSeq() { return Interlocked.Increment(ref reqSeq); }

    static int CurSeq() { return Interlocked.CompareExchange(ref reqSeq, 0, 0); }

    internal static bool IsCurrent(int seq) { return seq == CurSeq(); }

    // 只有「这还是最新那次请求」时才执行 a；已经过期的直接丢
    internal static bool ApplyIfCurrent(int seq, Action a)
    {
        if (!IsCurrent(seq)) return false;
        a();
        return true;
    }

    void DoQuery(string text, int seq)
    {
        if (!IsCurrent(seq)) return;
        streamPrev = "";          // 新的一次查询：上一轮的快照作废，首帧走整篇写入
        streamAppends = 0;
        streamRebuilds = 0;
        try
        {
            string key = ApiKey;
            if (key.Length == 0)
            {
                Log("查询跳过：还没配置 API Key");
                if (IsCurrent(seq))
                    Ui(delegate ()
                    {
                        if (!IsCurrent(seq)) return;
                        SetBoxText(MissingKeyText());
                        tip.Text = "缺 API Key，看框里的说明";
                    });
                return;
            }
            // ⑪ 发出去之前先打码：手机号 / 订单号 / 证件号 / 邮箱换成记号，
            //    真号码不出网；等 AI 回来再在本机换回真号（对照表只在内存，不落盘）
            string sendText;
            RedactMap rmap = RedactText(text, out sendText);
            lastRedact = rmap;
            string payload = BuildPayload(sendText, rmap.Empty ? null : MarkRule);
            byte[] data = Encoding.UTF8.GetBytes(payload);
            HttpWebRequest req = CreateJsonPost(API_BASE + "/chat/completions", key, data);
            using (Stream s = req.GetRequestStream()) { s.Write(data, 0, data.Length); }
            string full = "";
            int lines = 0;
            string firstLine = "";
            int lastPaint = 0;
            // v2.7.3 诊断：把「等首字」和「界面重绘」分开计时，慢在哪一头一眼看得出来
            int tStart = Environment.TickCount;
            int tFirst = -1;
            int paintMs = 0;
            int paintCnt = 0;
            JavaScriptSerializer js = new JavaScriptSerializer();
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    line = line.Trim();
                    lines++;
                    if (firstLine.Length == 0 && line.Length > 0)
                        firstLine = line.Length > 150 ? line.Substring(0, 150) : line;
                    if (line.Length < 6 || !line.StartsWith("data:")) continue;
                    string p = line.Substring(5).Trim();
                    if (p.Length == 0 || p == "[DONE]") continue;
                    string delta = DeltaText(js, p);
                    if (delta == null) continue;
                    full = full + delta;
                    if (tFirst < 0) tFirst = Environment.TickCount - tStart;
                    if (!IsCurrent(seq)) return;   // 已经有人点了新的一次，这条线立刻停手，别再往界面刷
                    // v2.7.3：节流回到 200ms。v2.7.2 的 260ms 反而更慢，原因是快照被 StripGrammarSection
                    // 削成了「不增长」的一小段，StreamPaint 每帧都拿不到可追加的前缀，退化成整篇重建 ——
                    // 次数虽然少了，每次却更贵。现在快照恢复单调增长，纯追加才算数。
                    if (Environment.TickCount - lastPaint > 200)
                    {
                        lastPaint = Environment.TickCount;
                        string snap = showApprox ? full : StripApprox(full);
                        // 英文进来时边写边把【缩写版】挡掉，避免先闪一下再消失
                        snap = AbbrevGate(snap);
                        // 语法讲解关掉时，【语法】标题刚冒头就该拦住，不能等写完才消失
                        snap = GrammarGate(snap);
                        // 边写边把记号换回真号，不然你会先看到一串 [[M1]]
                        int ig;
                        snap = RedactRestore(snap, rmap, out ig);

                        int tPaint = Environment.TickCount;
                        Ui(delegate () {
                            if (!IsCurrent(seq)) return;
                            StreamPaint(snap);
                            tip.Text = "正在写……";
                        });
                        paintMs += Environment.TickCount - tPaint;
                        paintCnt++;
                    }
                }
            }
            if (full.Trim().Length == 0) throw new Exception("接口没返回内容 [行数=" + lines + " 首行=" + firstLine + "]");
            string content = full.Trim();
            int leftMarks = 0;
            content = RedactRestore(content, rmap, out leftMarks);   // 换回真号码
            int totalMs = Environment.TickCount - tStart;
            Log("查询成功，返回 " + content.Length + " 字｜首字 " + (tFirst < 0 ? -1 : tFirst) + "ms 总计 " + totalMs
                + "ms 重绘 " + paintMs + "ms/" + paintCnt + "次（追加 " + streamAppends + " 重建 " + streamRebuilds + "）");
            CachePut(text, content);          // 缓存照存：这段译文对它自己那句话是有效的
            if (!IsCurrent(seq)) return;      // 已被更新的一次接管：不写历史、不动界面
            SaveWords(content);
            AddHistory(text, content);
            // v2.8.0：把这一句的「原句 + 结果」记成追问上下文
            followBaseInput = text;
            followBaseResult = content;
            Ui(delegate () {
                if (!IsCurrent(seq)) return;
                RenderResult(content);
                tip.Text = RedactTip("双击某句只读那句｜Ctrl+F 接着问｜Esc 关", leftMarks);
                ShowErrorBar(false);          // 这次成了，把上回的「再试一次」收掉
            });
        }
        catch (Exception ex)
        {
            string msg = ex.Message;
            WebException we = ex as WebException;
            if (we != null && we.Response != null)
            {
                try
                {
                    using (StreamReader sr = new StreamReader(we.Response.GetResponseStream(), Encoding.UTF8))
                    { msg = msg + "\n\n" + sr.ReadToEnd(); }
                }
                catch { }
            }
            // 超时/网络这类前缀不再手动拼：下面 HumanizeError 会把原因说清，原文留给排查
            Log("查询失败：" + msg.Replace("\n", " "));
            string final = msg;
            if (!IsCurrent(seq)) return;   // 旧请求失败，不该顶掉新请求正在显示的界面

            // ⑮ 断网离线查词：网真的不通时，别让她只看到「连不上服务器」——
            //    本地词库里就地查一下这几个词，好歹知道客户那句话在说什么。
            //    只有「网络本身不通」才降级；Key 错 / 余额没了 / 被限流这三类必须原样报出去，
            //    要是被离线结果盖住，她永远不知道要去充钱或者换 Key。
            string offline = null;
            if (IsNetworkishError(final))
            {
                offline = OfflineAnswer(text);
                if (offline != null) Log("网络不通，改走离线词库");
            }
            string offlineSnap = offline;
            Ui(delegate () {
                if (!IsCurrent(seq)) return;
                lastFailText = text;                  // 记下这句，点「再试一次」不用再选一遍
                if (offlineSnap != null)
                {
                    RenderResult(offlineSnap);        // 离线结果照样走同一套着色，看着跟平时一样
                    tip.Text = "网断了，这是本地词库查的｜网回来点再试一次";
                    ShowErrorBar(true);               // 重试条留着，网一通点一下就是完整结果
                    return;
                }
                SetBoxText(HumanizeError(final));     // ④ 原始异常翻成人话，原文仍旧附在最后
                tip.Text = "出错了，点「再试一次」";
                ShowErrorBar(true);
            });
        }
        finally
        {
            // busy 只由「当前这一次」负责收尾：旧请求退出时不能把新请求的"查询中"状态抹掉
            if (IsCurrent(seq))
            {
                busy = false;
                // 流式过程中如果调过字号，这里再按最终字号重排一次，保证看得全
                if (pendingFont)
                {
                    pendingFont = false;
                    Ui(delegate () {
                        string content = lastFull.Length > 0 ? lastFull : box.Text;
                        if (content.Length > 0) RenderResult(content);
                    });
                }
            }
        }
    }

    // ====== v2.8.0 追问：查完一句还能接着问 ======
    // 把「原句 → 上一轮结果」当成对话历史一起发过去，这样「这句」「那个词」指的是谁，AI 心里有数。
    // 追问不进缓存、不写历史记录、也不往生词本乱塞：它是对同一句的补充，不是新的一次查询。

    internal static string FollowPrefix(string question)
    {
        return "【追问】" + (question == null ? "" : question.Trim()) + "\r\n\r\n————\r\n";
    }

    static Dictionary<string, object> ChatMsg(string role, string content)
    {
        Dictionary<string, object> m = new Dictionary<string, object>();
        m["role"] = role;
        m["content"] = content;
        return m;
    }

    internal static string BuildFollowPayload(string original, string prevResult, string question, string extraRule)
    {
        string sys = BuildSystemPrompt()
            + "\r\n\r\n【追问模式】用户已经查过一句，现在是对那句的追问。"
            + "\r\n只回答这次问的那一点，不要上一轮的译文整篇重抄一遍；"
            + "回答仍旧用【英文】【中文】【生词】【语法】这些小标题分块，没内容的小标题直接不写；"
            + "一次说清，不要反问用户，也不要让用户再补充信息。";
        if (!string.IsNullOrEmpty(extraRule)) sys = sys + "\r\n" + extraRule;
        List<object> msgs = new List<object>();
        msgs.Add(ChatMsg("system", sys));
        msgs.Add(ChatMsg("user", original == null ? "" : original));
        if (prevResult != null && prevResult.Trim().Length > 0)
            msgs.Add(ChatMsg("assistant", prevResult));
        msgs.Add(ChatMsg("user", question == null ? "" : question));
        Dictionary<string, object> root = new Dictionary<string, object>();
        root["model"] = MODEL;
        root["messages"] = msgs.ToArray();
        root["temperature"] = 0.3;
        root["max_tokens"] = 3000;
        root["stream"] = true;
        JavaScriptSerializer js = new JavaScriptSerializer();
        return js.Serialize(root);
    }

    // 自检用（离线）：只看报文里三条消息的顺序对不对，不发请求
    internal static string FollowPayloadCheck()
    {
        try
        {
            string p = BuildFollowPayload("He is down for it.",
                "【英文】He is down for it.\r\n【中文】他愿意。",
                "这里的 for 是介词吗", null);
            int iOrig = p.IndexOf("He is down for it.", StringComparison.Ordinal);
            int iPrev = p.IndexOf("他愿意", StringComparison.Ordinal);
            int iAsk = p.IndexOf("这里的 for 是介词吗", StringComparison.Ordinal);
            int iSys = p.IndexOf("\"role\":\"system\"", StringComparison.Ordinal);
            bool ok = iSys >= 0 && iOrig > iSys && iPrev > iOrig && iAsk > iPrev
                && p.Contains("\"stream\":true") && p.Contains("\"model\"");
            return ok
                ? "  追问报文：system → 原句 → 上轮结果 → 这次追问，顺序正常，走流式"
                : "  追问报文：!! 顺序不对（system=" + iSys + " 原句=" + iOrig + " 上轮=" + iPrev + " 追问=" + iAsk + "）";
        }
        catch (Exception ex)
        {
            return "  追问报文：!! 拼报文抛异常 " + ex.GetType().Name + " " + ex.Message;
        }
    }

    void DoFollowUp(string question, int seq)
    {
        if (!IsCurrent(seq)) return;
        streamPrev = "";
        streamAppends = 0;
        streamRebuilds = 0;
        string q = question == null ? "" : question.Trim();
        string prefix = FollowPrefix(q);
        try
        {
            string key = ApiKey;
            if (key.Length == 0)
            {
                Log("追问跳过：还没配置 API Key");
                if (IsCurrent(seq))
                    Ui(delegate ()
                    {
                        if (!IsCurrent(seq)) return;
                        SetBoxText(MissingKeyText());
                        tip.Text = "缺 API Key，看框里的说明";
                    });
                return;
            }
            // 追问里也可能带手机号、单号，照样先打码再出网
            string sendQ;
            RedactMap rmap = RedactText(q, out sendQ);
            string payload = BuildFollowPayload(followBaseInput, followBaseResult, sendQ, rmap.Empty ? null : MarkRule);
            byte[] data = Encoding.UTF8.GetBytes(payload);
            HttpWebRequest req = CreateJsonPost(API_BASE + "/chat/completions", key, data);
            using (Stream s = req.GetRequestStream()) { s.Write(data, 0, data.Length); }
            string full = "";
            int lastPaint = 0;
            int tStart = Environment.TickCount;
            int tFirst = -1;
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
                    if (tFirst < 0) tFirst = Environment.TickCount - tStart;
                    if (!IsCurrent(seq)) return;
                    // 追问体量小，同样是 200ms 一刷，跟主查询一个手感
                    if (Environment.TickCount - lastPaint > 200)
                    {
                        lastPaint = Environment.TickCount;
                        string snap = AbbrevGate(full);
                        snap = GrammarGate(snap);
                        int ig;
                        snap = RedactRestore(snap, rmap, out ig);
                        string paint = prefix + snap;
                        Ui(delegate ()
                        {
                            if (!IsCurrent(seq)) return;
                            StreamPaint(paint);
                            tip.Text = "正在写追问的回答……";
                        });
                    }
                }
            }
            if (full.Trim().Length == 0) throw new Exception("接口没返回内容（追问）");
            string content = full.Trim();
            int leftMarks = 0;
            content = RedactRestore(content, rmap, out leftMarks);
            Log("追问成功，" + content.Length + " 字｜首字 " + (tFirst < 0 ? -1 : tFirst) + "ms 总计 "
                + (Environment.TickCount - tStart) + "ms");
            if (!IsCurrent(seq)) return;
            // 追问的答案接在上一轮后面，下次再追问时 AI 能看到前面聊过什么
            followBaseResult = (followBaseResult + "\r\n\r\n【追问】" + q + "\r\n" + content).Trim();
            string shown = prefix + content;
            Ui(delegate ()
            {
                if (!IsCurrent(seq)) return;
                RenderResult(shown);
                tip.Text = RedactTip("追问完了｜Ctrl+F 再问一句，Esc 关", leftMarks);
                ShowErrorBar(false);
            });
        }
        catch (Exception ex)
        {
            string msg = ex.Message;
            WebException we = ex as WebException;
            if (we != null && we.Response != null)
            {
                try
                {
                    using (StreamReader sr = new StreamReader(we.Response.GetResponseStream(), Encoding.UTF8))
                    { msg = msg + "\n\n" + sr.ReadToEnd(); }
                }
                catch { }
            }
            Log("追问失败：" + msg.Replace("\n", " "));
            string final = msg;
            if (!IsCurrent(seq)) return;
            Ui(delegate ()
            {
                if (!IsCurrent(seq)) return;
                RenderResult(prefix + HumanizeError(final));
                tip.Text = "追问没成功，上面是原因｜Ctrl+F 再试一次";
                ShowErrorBar(true);
            });
        }
        finally
        {
            if (IsCurrent(seq)) busy = false;
        }
    }

    // ====== ④ 报错说人话 ======
    // 以前出错直接把 "远程服务器返回错误: (401) 未经授权。" 这种原文甩在框里，
    // 用户英语是入门水平，看到这个只能干瞪眼。这里按关键词归成 8 类常见原因，
    // 每类给一句「怎么回事 + 怎么办」，原始报错仍然附在最后，方便排查时对照。
    internal static string HumanizeError(string raw)
    {
        string s = (raw == null ? "" : raw).ToLowerInvariant();
        string what;
        string how;

        if (s.Contains("401") || s.Contains("invalid_api_key") || s.Contains("incorrect api key")
            || s.Contains("unauthorized") || s.Contains("authentication") || s.Contains("invalid token"))
        {
            what = "API Key 不对（Key 写错了、过期了，或者复制时多带了空格换行）";
            how = "用记事本打开 " + ApiKeyFile + "，换成能用的 Key（只留一行，前后别留空格），存盘后点下面的「再试一次」。";
        }
        else if (s.Contains("402") || s.Contains("insufficient") || s.Contains("quota") || s.Contains("balance")
            || s.Contains("余额") || s.Contains("欠费") || s.Contains("arrear"))
        {
            what = "Key 的额度用完了（余额不足）";
            how = "去这个 Key 所属的平台官网充点钱，或者换一个还有额度的 Key，存盘后点「再试一次」。";
        }
        else if (s.Contains("429") || s.Contains("rate limit") || s.Contains("too many requests"))
        {
            what = "查得太快了，服务器让你缓一缓";
            how = "等十秒左右，再点「再试一次」就好。连着猛按回车也会这样，慢一点没关系。";
        }
        else if (s.Contains("timed out") || s.Contains("timeout") || s.Contains("超时"))
        {
            what = "等太久没等到回应（网慢，或者服务器这回卡住了）";
            how = "先点「再试一次」；还不行就看看网通不通，代理/梯子关掉再试一次（国内直连一般更快）。";
        }
        else if (s.Contains("name resolution") || s.Contains("no such host") || s.Contains("无法解析") || s.Contains("dns"))
        {
            what = "找不到服务器地址（多半是断网，或者代理没开）";
            how = "先确认网还通不通；用代理的话确认代理软件开着，然后点「再试一次」。";
        }
        else if (s.Contains("connection") || s.Contains("refused") || s.Contains("无法连接")
            || s.Contains("网络") || s.Contains("unreachable") || s.Contains("ssl") || s.Contains("证书"))
        {
            what = "连不上服务器（网络断了、被代理挡了，或者证书被拦）";
            how = "确认能正常上网后再点「再试一次」；反复不行就把 log.txt 发给我看看。";
        }
        else if (s.Contains("(500") || s.Contains("(502") || s.Contains("(503") || s.Contains("(504")
            || s.Contains("overloaded") || s.Contains("server error") || s.Contains("服务器"))
        {
            what = "服务器那边忙，或者临时出故障了（不是你这边的问题）";
            how = "过几分钟再点「再试一次」，通常等等就好。";
        }
        else if (s.Contains("接口没返回内容"))
        {
            what = "服务器有回应，但一个字都没给（偶尔会这样）";
            how = "点「再试一次」；连着几次都空，就把这句话换个说法再查。";
        }
        else
        {
            what = "这次没查到，具体原因没认出来";
            how = "点「再试一次」；还不行就把下面那段原文发给我，我帮你看。";
        }

        StringBuilder sb = new StringBuilder();
        sb.AppendLine("这次没查出来。");
        sb.AppendLine();
        sb.AppendLine("怎么回事：" + what);
        sb.AppendLine();
        sb.AppendLine("怎么办：" + how);
        sb.AppendLine();
        sb.AppendLine("------------");
        sb.AppendLine("原始报错（排查用，看不懂可以不管）：");
        string r = (raw == null ? "" : raw).Trim();
        if (r.Length > 800) r = r.Substring(0, 800) + "……";
        sb.Append(r);
        return sb.ToString();
    }

    public class StreamDelta { public string role { get; set; } public string content { get; set; } public string reasoning_content { get; set; } }
    public class StreamMsg { public string content { get; set; } }
    public class StreamChoice { public StreamDelta delta { get; set; } public StreamMsg message { get; set; } public string finish_reason { get; set; } }
    public class StreamChunk { public StreamChoice[] choices { get; set; } }
    static string BuildPayload(string text)
    {
        return BuildPayload(text, null);
    }

    // extraRule：打码时补一句「记号原样照抄」，别让 AI 把 [[M1]] 翻译成别的
    static string BuildPayload(string text, string extraRule)
    {
        string sys = BuildSystemPrompt();
        if (!string.IsNullOrEmpty(extraRule)) sys = sys + "\r\n" + extraRule;
        Dictionary<string, object> sysMsg = new Dictionary<string, object>();
        sysMsg["role"] = "system";
        sysMsg["content"] = sys;
        Dictionary<string, object> userMsg = new Dictionary<string, object>();
        userMsg["role"] = "user";
        userMsg["content"] = text;
        Dictionary<string, object> root = new Dictionary<string, object>();
        root["model"] = MODEL;
        root["messages"] = new object[] { sysMsg, userMsg };
        root["temperature"] = 0.3;
        root["max_tokens"] = 3000;
        root["stream"] = true;
        JavaScriptSerializer js = new JavaScriptSerializer();
        return js.Serialize(root);
    }

    static string DeltaText(JavaScriptSerializer js, string json)
    {
        try
        {
            StreamChunk sc = js.Deserialize<StreamChunk>(json);
            if (sc != null && sc.choices != null && sc.choices.Length > 0)
            {
                StreamChoice ch0 = sc.choices[0];
                if (ch0 != null)
                {
                    if (ch0.message != null && !string.IsNullOrEmpty(ch0.message.content))
                        return ch0.message.content;
                    if (ch0.delta != null && !string.IsNullOrEmpty(ch0.delta.content))
                        return ch0.delta.content;
                }
            }
            return null;
        }
        catch { }
        int idx = json.IndexOf("\"delta\":", StringComparison.Ordinal);
        if (idx < 0) return null;
        int ci = json.IndexOf("\"content\":", idx, StringComparison.Ordinal);
        if (ci < 0) return null;
        int i = ci + 10;
        while (i < json.Length && (json[i] == ' ' || json[i] == '\t')) i++;
        if (i >= json.Length || json[i] != '"') return null;
        i++;
        StringBuilder sb = new StringBuilder();
        while (i < json.Length)
        {
            char ch = json[i];
            if (ch == '\\')
            {
                if (i + 1 >= json.Length) break;
                char n = json[i + 1];
                if (n == 'n') { sb.Append('\n'); i += 2; }
                else if (n == 't') { sb.Append('\t'); i += 2; }
                else if (n == 'r') { sb.Append('\r'); i += 2; }
                else if (n == 'b') { sb.Append('\b'); i += 2; }
                else if (n == 'f') { sb.Append('\f'); i += 2; }
                else if (n == '"') { sb.Append('"'); i += 2; }
                else if (n == '\\') { sb.Append('\\'); i += 2; }
                else if (n == '/') { sb.Append('/'); i += 2; }
                else if (n == 'u')
                {
                    if (i + 5 < json.Length)
                    {
                        try { sb.Append((char)Convert.ToInt32(json.Substring(i + 2, 4), 16)); }
                        catch { }
                        i += 6;
                    }
                    else break;
                }
                else { sb.Append(n); i += 2; }
            }
            else if (ch == '"') break;
            else { sb.Append(ch); i++; }
        }
        if (sb.Length == 0) return null;
        return sb.ToString();
    }

    // ====== 截图 → 认字 → 翻译 ======
    const string OCR_SCRIPT = @"param([string]$Path,[string]$Out,[string]$Lang)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function Await($WinRtTask, $ResultType) {
    $asTask = $asTaskGeneric.MakeGenericMethod($ResultType)
    $netTask = $asTask.Invoke($null, @($WinRtTask))
    $netTask.Wait(-1) | Out-Null
    $netTask.Result
}
[Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime] | Out-Null
[Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null
[Windows.Graphics.Imaging.BitmapDecoder, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null
[Windows.Globalization.Language, Windows.Globalization, ContentType = WindowsRuntime] | Out-Null
$engine = $null
try {
    if ($Lang) {
        $lg = New-Object Windows.Globalization.Language($Lang)
        $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($lg)
    }
} catch { }
if ($engine -eq $null) { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages() }
$enc = New-Object System.Text.UTF8Encoding($false)
if ($engine -eq $null) { [IO.File]::WriteAllText($Out, '', $enc); exit 0 }
$file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($Path)) ([Windows.Storage.StorageFile])
$stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
$decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
$bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
$result = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
[IO.File]::WriteAllText($Out, $result.Text, $enc)
";

    void DoOcr()
    {
        string imgPath = null;
        Hide();
        Application.DoEvents();
        Thread.Sleep(160);
        try
        {
            using (CaptureForm cf = new CaptureForm())
            {
                if (cf.ShowDialog() == DialogResult.OK && cf.Captured != null)
                {
                    Directory.CreateDirectory(LOG_DIR);
                    imgPath = Path.Combine(LOG_DIR, "ocr_shot.png");
                    cf.Captured.Save(imgPath, ImageFormat.Png);
                    cf.Captured.Dispose();
                }
            }
        }
        catch { }
        if (imgPath == null)
        {
            // 没框选：如果剪贴板里有图（比如刚按 Win+Shift+S 截的），就用它
            try
            {
                if (Clipboard.ContainsImage())
                {
                    Image im = Clipboard.GetImage();
                    if (im != null)
                    {
                        Directory.CreateDirectory(LOG_DIR);
                        imgPath = Path.Combine(LOG_DIR, "ocr_clip.png");
                        im.Save(imgPath, ImageFormat.Png);
                        im.Dispose();
                    }
                }
            }
            catch { }
        }
        ShowPanel();
        if (imgPath == null) { tip.Text = "这次没截图，也没在剪贴板里找到图"; return; }
        tip.Text = "正在认图里的字…";
        string path = imgPath;
        Thread t = new Thread(delegate () {
            string text = RunOcr(path);
            Ui(delegate () {
                text = (text == null) ? "" : text.Trim();
                text = Regex.Replace(text, @"\s+", " ").Trim();
                if (text.Length == 0)
                {
                    tip.Text = "没认出字来，框大一点、字清楚点再试";
                    return;
                }
                BeginQuery(text, false);
            });
        });
        t.IsBackground = true;
        t.Start();
    }

    string RunOcr(string imgPath)
    {
        try
        {
            Directory.CreateDirectory(LOG_DIR);
            string script = Path.Combine(LOG_DIR, "ocr.ps1");
            File.WriteAllText(script, OCR_SCRIPT, new UTF8Encoding(false));
            string outFile = Path.Combine(LOG_DIR, "ocr_out.txt");
            if (File.Exists(outFile)) File.Delete(outFile);
            ProcessStartInfo psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script +
                "\" -Path \"" + imgPath + "\" -Out \"" + outFile + "\" -Lang en-US");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            using (Process p = Process.Start(psi))
            {
                if (!p.WaitForExit(20000))
                {
                    try { p.Kill(); } catch { }
                    return "";
                }
            }
            if (!File.Exists(outFile)) return "";
            return ReadTextAuto(outFile);
        }
        catch (Exception ex)
        {
            Log("OCR 失败：" + ex.Message);
            return "";
        }
    }
}
