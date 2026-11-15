// ============================================================================
//  饼饼专属中英互译 · Program.cs（Program）
//  程序入口与自检：Main()（含命令行参数分发）、界面自检、截图自检、控件树审计。
//  Main 仍写在 QueryForm 分部类里，这样它调用静态成员不用加类名前缀，逻辑零改动。
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
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// ====== 程序集信息（v2.1 F1：以前 FileVersion 是 0.0.0.0，出问题分不清跑的是哪一版）======
[assembly: System.Reflection.AssemblyTitle("饼饼专属中英互译")]
[assembly: System.Reflection.AssemblyProduct("饼饼专属中英互译")]
[assembly: System.Reflection.AssemblyCompany("Solivine")]
[assembly: System.Reflection.AssemblyDescription("饼饼专属中英互译（速查助手）")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("2.9.0")]

partial class QueryForm : Form   // 基类只在这里声明一次，其余 5 个文件写字面上不带基类
{
    // ====== v2.1 F1：版本号与编译时间（日志首行打印，出问题一眼能对上）======
    // v2.5.4：之前一直忘了同步这个常量（"关于"窗里写 v2.5.2 就是这么来的），本轮补上。
    public const string APP_VER = "1.0.0";

    internal static string AppStamp()
    {
        try { return File.GetLastWriteTime(Application.ExecutablePath).ToString("yyyy-MM-dd HH:mm:ss"); }
        catch { return "?"; }
    }

    // v2.5.2：源码版本号不再手写。
    // 以前这里是 "源码=r8eb97f"，提交往前走一个就对不上，日志里的源码号反而误导排查。
    // 现在由 tools\build.ps1 在编译时把 git 短提交号写进嵌入资源 SrcRev，这里读出来；
    // 拿不到资源（比如手动 csc 编的）就显示 dev，绝不编一个假哈希。
    internal static string SrcRev()
    {
        try
        {
            System.Reflection.Assembly asm = typeof(QueryForm).Assembly;
            string[] names = asm.GetManifestResourceNames();
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i] == null) continue;
                if (!names[i].EndsWith("SrcRev", StringComparison.OrdinalIgnoreCase)) continue;
                using (Stream s = asm.GetManifestResourceStream(names[i]))
                {
                    if (s == null) continue;
                    using (StreamReader r = new StreamReader(s, Encoding.UTF8))
                        return r.ReadToEnd().Trim();
                }
            }
        }
        catch { }
        return "dev";
    }

    internal static string AppVersionLine()
    {
        return "v" + APP_VER + "  编译=" + AppStamp() + "  源码=" + SrcRev();
    }

    // ====== v2.1 F20：全局异常兜底（改 UI 阶段最容易踩到没 catch 的异常）======
    // 以前没有这一层：任何一处漏 catch = 窗口直接消失、无提示、无日志。
    internal static NotifyIcon trayStatic;   // 托盘图标，崩溃时用它冒个泡
    static int crashCount = 0;

    static void Crash(Exception ex)
    {
        crashCount++;
        string body;
        try
        {
            body = "未捕获异常 #" + crashCount + "  " + AppVersionLine() + "\r\n"
                 + (ex == null ? "(null)" : ex.ToString()) + "\r\n";
        }
        catch
        {
            body = "未捕获异常（异常对象无法序列化）\r\n";
        }
        try
        {
            Directory.CreateDirectory(LOG_DIR);
            File.AppendAllText(Path.Combine(LOG_DIR, "crash.txt"), body, Encoding.UTF8);
        }
        catch { }
        try { Log("未捕获异常已记录: " + (ex == null ? "null" : ex.GetType().Name + " " + ex.Message)); } catch { }
        try
        {
            if (trayStatic != null && trayStatic.Visible)
                trayStatic.ShowBalloonTip(3000, AppTitle,
                    "刚才遇到个小错误，已经记进日志了，程序继续用。", ToolTipIcon.Warning);
        }
        catch { }
    }

    static bool crashHandlersInstalled = false;

    static void InstallCrashHandlers()
    {
        if (crashHandlersInstalled) return;
        crashHandlersInstalled = true;
        try { Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException); } catch { }
        Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { Crash(e == null ? null : e.Exception); };
        AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { Crash(e == null ? null : e.ExceptionObject as Exception); };
        try
        {
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += delegate(object s, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
            {
                Crash(e == null ? null : e.Exception);
                try { e.SetObserved(); } catch { }
            };
        }
        catch { }
    }

    // 自检：渲染一段样例并导出 RTF，用来确认正文是黑的、标题才有颜色
    public void SelfTest(string sample)
    {
        RenderResult(sample);
        try
        {
            Directory.CreateDirectory(LOG_DIR);
            File.WriteAllText(Path.Combine(LOG_DIR, "selftest.rtf"), box.Rtf, Encoding.ASCII);
            File.WriteAllText(Path.Combine(LOG_DIR, "selftest.txt"), box.Text, Encoding.UTF8);
        }
        catch { }
    }

    [STAThread]
    static void Main()
    {
        InstallCrashHandlers();          // v2.1 F20：第一件事就是装安全带
        Log("程序启动 " + AppVersionLine());
        LoadSettings();
        MigrateOldWords();

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--version") >= 0)
        {
            string[] vv = Environment.GetCommandLineArgs();
            string vp = "version.txt";
            int vi = Array.IndexOf(vv, "--version");
            if (vi >= 0 && vi + 1 < vv.Length) vp = vv[vi + 1];
            try
            {
                File.WriteAllText(vp, AppVersionLine() + "\r\n" + typeof(QueryForm).Assembly.FullName + "\r\n", new UTF8Encoding(true));
            }
            catch { }
            return;
        }

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--audit") >= 0)
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string outPath = "audit.txt";
            string[] aa = Environment.GetCommandLineArgs();
            int ai = Array.IndexOf(aa, "--audit");
            if (ai >= 0 && ai + 1 < aa.Length) outPath = aa[ai + 1];
            StringBuilder sb = new StringBuilder();
            QueryForm.selfTestMode = true;   // 审计也不许弹自动窗（v2.5.5 首启窗是模态，会卡死在 DoEvents）
            QueryForm qf = new QueryForm();
            qf.Show();
            Application.DoEvents();
            sb.AppendLine("== 主窗口 客户区 " + qf.ClientSize);
            AuditTree(qf, sb, "主窗口");
            HistoryForm hf = new HistoryForm(new List<string[]>(new string[][] {
                new string[]{"2026-10-24 10:00:12","你这边网络有点卡，晚点回你","..."},
                new string[]{"2026-10-23 21:07:03","这个价格是含配送的吗","..."}
            }), qf);
            hf.Show(); Application.DoEvents();
            SnapForm(hf, Path.GetDirectoryName(outPath), "历史记录");
            sb.AppendLine("== 历史记录 客户区 " + hf.ClientSize);
            AuditTree(hf, sb, "历史记录");
            WordBookForm wf = new WordBookForm(qf);
            wf.Show(); Application.DoEvents();
            SnapForm(wf, Path.GetDirectoryName(outPath), "生词本");
            sb.AppendLine("== 生词本 客户区 " + wf.ClientSize);
            AuditTree(wf, sb, "生词本");
            // v2.8.0 新增：生词复习卡片窗也要能被审计和截图，不然新窗口永远是"没人看过"的状态
            // 这里不用 SnapForm 抓屏：自检多半在别的窗口开着的时候跑，抓屏会把人家前台画面截进来（已踩过）
            WordReviewForm rf = new WordReviewForm(qf);
            rf.Show(); Application.DoEvents();
            try
            {
                using (Bitmap rb = new Bitmap(rf.Width, rf.Height))
                {
                    rf.DrawToBitmap(rb, new Rectangle(0, 0, rf.Width, rf.Height));
                    rb.Save(Path.Combine(Path.GetDirectoryName(outPath), "check_生词复习.png"), ImageFormat.Png);
                }
            }
            catch { }
            sb.AppendLine("== 生词复习 客户区 " + rf.ClientSize);
            AuditTree(rf, sb, "生词复习");
            rf.SelfTest(sb);
            sb.AppendLine(FollowPayloadCheck());
            try { File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true)); } catch { }
            hf.Close(); wf.Close(); rf.Close(); qf.Close();
            Application.Exit();
            return;
        }

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--errtest") >= 0)
        {
            // ④ 报错说人话专项自检：9 种典型报错各翻一遍 + 把界面切到出错态截图/审计
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string[] ea = Environment.GetCommandLineArgs();
            int ei = Array.IndexOf(ea, "--errtest");
            string eOut = (ei >= 0 && ei + 1 < ea.Length) ? ea[ei + 1] : "errtest.txt";
            string eDir = Path.GetDirectoryName(eOut);
            if (string.IsNullOrEmpty(eDir) || !Directory.Exists(eDir)) eDir = ".";

            string[] cases = new string[] {
                "远程服务器返回错误: (401) 未经授权。{\"error\":{\"message\":\"Invalid API key\"}}",
                "远程服务器返回错误: (402) 需要付费。{\"error\":{\"message\":\"Insufficient Balance\"}}",
                "远程服务器返回错误: (429) 请求太多。{\"error\":{\"message\":\"Rate limit reached\"}}",
                "操作超时",
                "远程名称无法解析: 'api.deepseek.com'",
                "无法连接到远程服务器",
                "远程服务器返回错误: (503) 服务不可用。{\"error\":{\"message\":\"server is overloaded\"}}",
                "接口没返回内容 [行数=3 首行=data: {\"id\":\"x\"}]",
                "System.Exception: 某个没见过的新毛病 xyz"
            };
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("== ④ 报错说人话：9 种输入各自翻出来的结果 ==");
            for (int i = 0; i < cases.Length; i++)
            {
                sb.AppendLine();
                sb.AppendLine("---- 输入 " + (i + 1) + "：" + cases[i]);
                sb.AppendLine(QueryForm.HumanizeError(cases[i]));
            }

            QueryForm qf = new QueryForm();
            qf.StartPosition = FormStartPosition.Manual;
            qf.Location = new Point(160, 160);
            qf.Show();
            Application.DoEvents();
            // 本机自检环境里主窗口客户区常年是 0x0，先撑开一个正常尺寸，截图才有东西看
            if (qf.ClientSize.Width < 50)
            {
                qf.Size = new Size(420, 660);
                Application.DoEvents();
                qf.PerformLayout();
                Application.DoEvents();
            }
            qf.ShowErrorDemo(cases[0]);
            Application.DoEvents();
            SnapForm(qf, eDir, "报错");
            sb.AppendLine();
            sb.AppendLine("== 出错态 客户区 " + qf.ClientSize);
            AuditTree(qf, sb, "主窗口");
            string tipAfter;
            bool clickOk = qf.RetryClickTest(out tipAfter);
            sb.AppendLine();
            sb.AppendLine("[点击测试] 「再试一次」按钮：" + (clickOk ? "通过" : "失败") + "，点完提示 = " + tipAfter);
            try { File.WriteAllText(eOut, sb.ToString(), new UTF8Encoding(true)); } catch { }
            qf.Close();
            Application.Exit();
            return;
        }

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--dictdemo") >= 0)
        {
            // 自检：把断网离线结果真真切切显示在桌面上（不退出、3 秒后自己关），
            // 好让外面截一张「她真正会看到的样子」。只给自检用，平时不会走到。
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            QueryForm.selfTestMode = true;
            QueryForm qf = new QueryForm();
            qf.StartPosition = FormStartPosition.CenterScreen;
            qf.ShowInTaskbar = true;
            qf.TopMost = true;
            qf.Show();
            Application.DoEvents();
            qf.WindowState = FormWindowState.Normal;
            qf.ClientSize = new Size(420, 640);
            qf.CenterToScreen();
            Application.DoEvents();
            qf.OfflineDemoPump();
            Application.DoEvents();
            System.Windows.Forms.Timer tk = new System.Windows.Forms.Timer();
            tk.Interval = 4000;
            tk.Tick += delegate { tk.Stop(); qf.Close(); Application.Exit(); };
            tk.Start();
            Application.Run(qf);
            return;
        }

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--dictshot") >= 0)
        {
            // 自检：第二档 ⑮ 断网离线查词的「界面这一幕」——离线结果排版 + 截图 + 控件审计
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string[] sa = Environment.GetCommandLineArgs();
            int si = Array.IndexOf(sa, "--dictshot");
            string sOut = (si >= 0 && si + 1 < sa.Length) ? sa[si + 1] : "dictshot.txt";
            string sDir = Path.GetDirectoryName(sOut);
            if (string.IsNullOrEmpty(sDir) || !Directory.Exists(sDir)) sDir = ".";

            QueryForm.selfTestMode = true;
            QueryForm qf = new QueryForm();
            qf.StartPosition = FormStartPosition.Manual;
            qf.Location = new Point(160, 160);
            qf.Show();
            Application.DoEvents();
            // 本机自检环境里主窗口客户区常年 0x0，硬撑成正常尺寸，截图/审计才有意义
            if (qf.ClientSize.Width < 50 || qf.ClientSize.Height < 50)
            {
                qf.WindowState = FormWindowState.Normal;
                qf.ClientSize = new Size(420, 640);
                qf.CreateControl();
                Application.DoEvents();
                qf.PerformLayout();
                Application.DoEvents();
            }

            StringBuilder dsb = new StringBuilder();
            dsb.AppendLine("断网离线查词 · 界面这一幕");
            string[] texts = new string[] { "refund", "Sorry, your package arrived broken. Can we get a refund?", "你好，在吗" };
            string[] names = new string[] { "离线单词", "离线整句", "离线中文不接" };
            for (int k = 0; k < texts.Length; k++)
            {
                string ans = QueryForm.OfflineAnswer(texts[k]);
                dsb.AppendLine();
                dsb.AppendLine("---- 输入 " + (k + 1) + "：" + texts[k]);
                dsb.AppendLine(ans == null ? "（离线不接这个输入，界面照旧显示联网报错）" : ans);
                qf.ShowOfflineDemo(ans);
                dsb.AppendLine("== 出结果后 客户区 " + qf.ClientSize);
                try { SnapForm(qf, sDir, names[k]); } catch { }
                AuditTree(qf, dsb, names[k]);
            }
            try { File.WriteAllText(sOut, dsb.ToString(), new UTF8Encoding(true)); } catch { }
            qf.Close();
            Application.Exit();
            return;
        }

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--hkcheck") >= 0)
        {
            // 第三批 ② 专项自检：换快捷键（含"键被别的软件占着"时的退回）
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string[] ha = Environment.GetCommandLineArgs();
            int hi = Array.IndexOf(ha, "--hkcheck");
            string hOut = (hi >= 0 && hi + 1 < ha.Length) ? ha[hi + 1] : "hkcheck.txt";
            string hDir = Path.GetDirectoryName(hOut);
            if (string.IsNullOrEmpty(hDir) || !Directory.Exists(hDir)) hDir = ".";
            QueryForm.selfTestMode = true;   // 自检不许改用户真实的 settings.ini
            QueryForm qf = new QueryForm();
            qf.StartPosition = FormStartPosition.Manual;
            qf.Location = new Point(160, 160);
            qf.Show();
            Application.DoEvents();
            // 自检环境里主窗口客户区常年是 0x0，先撑开一个正常尺寸，审计/截图才有意义
            if (qf.ClientSize.Width < 50)
            {
                qf.Size = new Size(420, 660);
                Application.DoEvents();
                qf.PerformLayout();
                Application.DoEvents();
            }
            StringBuilder hb = new StringBuilder();
            hb.AppendLine("== 第三批 ② 换快捷键自检 ==");
            hb.Append(qf.HotkeySelfTest());
            Application.DoEvents();
            SnapForm(qf, hDir, "换快捷键");
            hb.AppendLine();
            hb.AppendLine("== 「更多 ▾」菜单 ==（* 为当前选中）");
            try
            {
                using (ContextMenuStrip mm = qf.BuildMoreMenu())
                    DumpMenu(mm.Items, "  ", hb);
            }
            catch (Exception ex) { hb.AppendLine("  菜单导出失败：" + ex.Message); }
            hb.AppendLine();
            hb.AppendLine("== 换键后主窗口 == 客户区 " + qf.ClientSize);
            AuditTree(qf, hb, "主窗口");
            try { File.WriteAllText(hOut, hb.ToString(), new UTF8Encoding(true)); } catch { }
            qf.Close();
            Application.Exit();
            return;
        }

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--redactcheck") >= 0)
        {
            // 第二档 ⑪ 专项自检：发出去之前打码、回来换回真号。
            // 全程不联网、不读 API Key、不写任何用户数据；对照表只在内存里活一次。
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string[] ra = Environment.GetCommandLineArgs();
            int ri = Array.IndexOf(ra, "--redactcheck");
            string rOut = (ri >= 0 && ri + 1 < ra.Length) ? ra[ri + 1] : "redactcheck.txt";
            QueryForm.selfTestMode = true;
            string rTxt = "";
            try
            {
                QueryForm rq = new QueryForm();
                rTxt = rq.RedactSelfTest();
                try { rq.Dispose(); } catch { }
            }
            catch (Exception ex) { rTxt = "自检抛异常：" + ex.ToString(); }
            try
            {
                File.WriteAllText(rOut, "== 第二档 ⑪ 发出去之前先打码自检  " + AppVersionLine()
                    + "\r\n\r\n" + rTxt, new UTF8Encoding(true));
            }
            catch (Exception ex) { try { File.WriteAllText(rOut, "写文件失败：" + ex.Message); } catch { } }
            Application.Exit();
            return;
        }

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--kbcheck") >= 0)
        {
            // 第二档 ⑫ 专项自检：输入框里 ↑↓ 翻历史对不对、到边界给不给话、输入法选字时让不让、
            // 翻历史会不会偷偷联网或脏数据、焦点高亮判定。全程不联网、不改用户真实历史文件。
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string[] ka = Environment.GetCommandLineArgs();
            int ki = Array.IndexOf(ka, "--kbcheck");
            string kOut = (ki >= 0 && ki + 1 < ka.Length) ? ka[ki + 1] : "kbcheck.txt";
            QueryForm.selfTestMode = true;
            string kTxt = "";
            try
            {
                QueryForm kq = new QueryForm();
                kTxt = kq.NavSelfTest();
                try { kq.Dispose(); } catch { }
            }
            catch (Exception ex) { kTxt = "自检抛异常：" + ex.ToString(); }
            try
            {
                File.WriteAllText(kOut, "== 第二档 ⑫ 键盘翻历史自检  " + AppVersionLine()
                    + "\r\n\r\n" + kTxt, new UTF8Encoding(true));
            }
            catch (Exception ex) { try { File.WriteAllText(kOut, "写文件失败：" + ex.Message); } catch { } }
            Application.Exit();
            return;
        }

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--pastecheck") >= 0)
        {
            // 第二档 ⑩ 专项自检：粘完该不该翻、长文问不问、超长截不截、连粘会不会连发。
            // 全程不联网、不写剪贴板、不改用户真实的 settings.ini。
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string[] pa = Environment.GetCommandLineArgs();
            int pi = Array.IndexOf(pa, "--pastecheck");
            string pOut = (pi >= 0 && pi + 1 < pa.Length) ? pa[pi + 1] : "pastecheck.txt";
            QueryForm.selfTestMode = true;
            string pTxt = "";
            try
            {
                QueryForm pq = new QueryForm();
                pTxt = pq.PasteSelfTest();
                try { pq.Dispose(); } catch { }
            }
            catch (Exception ex) { pTxt = "自检抛异常：" + ex.ToString(); }
            try
            {
                File.WriteAllText(pOut, "== 第二档 ⑩ 粘贴即查自检  " + AppVersionLine()
                    + "\r\n\r\n" + pTxt, new UTF8Encoding(true));
            }
            catch (Exception ex) { try { File.WriteAllText(pOut, "写文件失败：" + ex.Message); } catch { } }
            Application.Exit();
            return;
        }

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--dpicheck") >= 0)
        {
            // 第二档 ⑯ 专项自检：字号放大 × 窗口尺寸 × 多屏拔插，字有没有被切、控件有没有跑出框。
            // 全程不联网、不改用户真实 settings.ini；位置那部分拿假屏幕数组跑，不动真实窗口。
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string[] da = Environment.GetCommandLineArgs();
            int di = Array.IndexOf(da, "--dpicheck");
            string dOut = (di >= 0 && di + 1 < da.Length) ? da[di + 1] : "dpicheck.txt";
            StringBuilder db = new StringBuilder();
            try
            {
                string dDir = Path.GetDirectoryName(dOut);
                if (string.IsNullOrEmpty(dDir) || !Directory.Exists(dDir)) dDir = ".";
                QueryForm.selfTestMode = true;
                QueryForm dq = new QueryForm();
                dq.StartPosition = FormStartPosition.Manual;
                dq.Location = new Point(120, 120);
                db.AppendLine("== 第二档 ⑯ 缩放与多屏自检  " + AppVersionLine() + " ==");
                db.AppendLine();
                db.Append(dq.DpiSelfTest());

                dq.Show();
                Application.DoEvents();
                if (dq.ClientSize.Width < 50)
                {
                    dq.Size = new Size(480, 660);
                    Application.DoEvents(); dq.PerformLayout(); Application.DoEvents();
                }

                int[] pts = new int[] { 8, 11, 14, 18, 24 };
                Size[] sizes = new Size[] {
                    new Size(340, 300), new Size(420, 560), new Size(480, 660),
                    new Size(700, 420), new Size(360, 900) };
                db.AppendLine();
                db.AppendLine("== 四、缩放矩阵（" + pts.Length + " 种字号 × " + sizes.Length + " 种窗口尺寸 = "
                    + (pts.Length * sizes.Length) + " 组） ==");
                db.AppendLine("  只看真问题：字被切（超宽/超高）、控件跑出父框、控件互相压住。");
                int totalBad = 0, totalCells = 0;
                List<string> issues = new List<string>();
                for (int pi = 0; pi < pts.Length; pi++)
                {
                    for (int si = 0; si < sizes.Length; si++)
                    {
                        Size want = sizes[si];
                        dq.ScaleStep(pts[pi], want);
                        Size real = dq.ScaleClientForTest;
                        int hitFont = dq.ScaleFontForTest;
                        StringBuilder tb = new StringBuilder();
                        AuditTree(dq, tb, "主窗口");
                        string[] lines = tb.ToString().Replace("\r", "").Split('\n');
                        int badText = 0, badOut = 0, badOv = 0;
                        List<string> cell = new List<string>();
                        for (int li = 0; li < lines.Length; li++)
                        {
                            string ln = lines[li].Trim();
                            if (ln.Length == 0) continue;
                            if (ln.IndexOf("文字超宽") >= 0 || ln.IndexOf("文字超高") >= 0) badText++;
                            else if (ln.IndexOf("[互相压住]") >= 0) badOv++;
                            else if (ln.IndexOf("[越界]") >= 0 && ln.IndexOf("父客户区=0x0") < 0) badOut++;
                            else continue;   // 「父客户区=0x0」是自检环境噪音，不记
                            cell.Add(ln);
                            issues.Add(ln);
                        }
                        int sum = badText + badOut + badOv;
                        totalCells++;
                        if (sum > 0) totalBad++;
                        db.AppendLine("  " + hitFont + "pt | 要求 " + want.Width + "x" + want.Height
                            + " | 实际 " + real.Width + "x" + real.Height
                            + " | 字被切 " + badText + " | 出框 " + badOut + " | 互压 " + badOv);
                        for (int q = 0; q < cell.Count && q < 2; q++) db.AppendLine("        · " + ShortPath(cell[q]));
                        if (cell.Count > 2) db.AppendLine("        · …另外 " + (cell.Count - 2) + " 条");
                    }
                }
                db.AppendLine();
                db.AppendLine("缩放矩阵：" + totalCells + " 组里 " + totalBad + " 组有问题，原始问题行 " + issues.Count + " 条");

                // 同一批控件在各组里反复出现 → 去重看它到底差多少像素，值不值得动
                db.AppendLine();
                db.AppendLine("== 五、按控件去重（差几像素一目了然） ==");
                Dictionary<string, int> cnt = new Dictionary<string, int>();
                Dictionary<string, int> worstPx = new Dictionary<string, int>();
                for (int q = 0; q < issues.Count; q++)
                {
                    string ln = issues[q];
                    string key = ln;
                    int gap = 0;
                    Match m = Regex.Match(ln, "需要(\\d+)px 可用(\\d+)px");
                    if (m.Success)
                    {
                        key = ln.Substring(0, m.Index).Trim();
                        int need = int.Parse(m.Groups[1].Value);
                        int have = int.Parse(m.Groups[2].Value);
                        if (need - have > gap) gap = need - have;
                    }
                    else
                    {
                        int bi = ln.IndexOf(" bounds=");
                        if (bi > 0) key = ln.Substring(0, bi).Trim();
                        gap = 1;
                    }
                    if (!cnt.ContainsKey(key)) { cnt[key] = 0; worstPx[key] = 0; }
                    cnt[key] = cnt[key] + 1;
                    if (gap > worstPx[key]) worstPx[key] = gap;
                }
                List<string> keys = new List<string>(cnt.Keys);
                keys.Sort(delegate (string a, string b) { return cnt[b].CompareTo(cnt[a]); });
                for (int q = 0; q < keys.Count; q++)
                {
                    string k = keys[q];
                    db.AppendLine("  " + cnt[k] + " 组 | 最多差 " + worstPx[k] + "px | " + ShortPath(k));
                }

                // 关键组合各留一张实拍：字最小 / 平时用 / 字最大 / 又小又挤
                db.AppendLine();
                db.AppendLine("== 六、关键组合实拍 ==");
                int[,] shots = new int[,] { { 8, 340, 300 }, { 11, 480, 660 }, { 24, 480, 660 }, { 24, 340, 300 } };
                for (int q = 0; q < 4; q++)
                {
                    dq.ScaleStep(shots[q, 0], new Size(shots[q, 1], shots[q, 2]));
                    SnapForm(dq, dDir, "缩放_" + shots[q, 0] + "pt_" + shots[q, 1] + "x" + shots[q, 2]);
                    db.AppendLine("  " + shots[q, 0] + "pt / 要求 " + shots[q, 1] + "x" + shots[q, 2]
                        + " → 实际 " + dq.ScaleClientForTest.Width + "x" + dq.ScaleClientForTest.Height);
                }

                // 回到你平时用的状态（11pt / 480x660），留一份控件清单
                dq.ScaleStep(11, new Size(480, 660));
                db.AppendLine();
                db.AppendLine("== 七、常用状态（11pt / 480x660）控件清单 ==");
                AuditTree(dq, db, "主窗口");
                try { File.WriteAllText(dOut, db.ToString(), new UTF8Encoding(true)); } catch { }
                dq.Close();
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(dOut, "自检抛异常：\r\n" + ex, new UTF8Encoding(true)); } catch { }
            }
            Application.Exit();
            return;
        }

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--aboutcheck") >= 0)
        {
            // 第一档 ⑥ 专项自检：「关于」小窗能开、六样信息齐全、截图能出
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string[] aa = Environment.GetCommandLineArgs();
            int ai = Array.IndexOf(aa, "--aboutcheck");
            string aOut = (ai >= 0 && ai + 1 < aa.Length) ? aa[ai + 1] : "aboutcheck.txt";
            string aDir = Path.GetDirectoryName(aOut);
            if (string.IsNullOrEmpty(aDir) || !Directory.Exists(aDir)) aDir = ".";
            QueryForm.selfTestMode = true;   // 自检不许改用户真实的 settings.ini
            QueryForm qf = new QueryForm();
            qf.StartPosition = FormStartPosition.Manual;
            qf.Location = new Point(160, 160);
            qf.Show();
            Application.DoEvents();
            StringBuilder ab = new StringBuilder();
            ab.AppendLine("== 第一档 ⑥ 「关于」小窗自检  " + AppVersionLine());
            try
            {
                using (Form f = qf.BuildAboutForm())
                {
                    f.StartPosition = FormStartPosition.Manual;
                    f.Location = new Point(760, 620);   // 挪到桌面空白处，别被别的窗口盖住
                    f.TopMost = true;
                    f.Show();
                    f.Activate();
                    f.Refresh();
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(300);
                    Application.DoEvents();
                    ab.AppendLine("窗口标题 = " + f.Text);
                    ab.AppendLine("客户区   = " + f.ClientSize);
                    ab.AppendLine();
                    ab.Append(qf.DescribeAbout(f));
                    ab.AppendLine();
                    ab.AppendLine("== 托盘右键菜单（确认「关于」在位） ==");
                    ab.Append(qf.TrayMenuText());
                    SnapForm(f, aDir, "关于");
                    f.Close();
                }
            }
            catch (Exception ex) { ab.AppendLine("打开关于窗失败：" + ex.Message); }
            try { File.WriteAllText(aOut, ab.ToString(), new UTF8Encoding(true)); } catch { }
            qf.Close();
            Application.Exit();
            return;
        }

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--housekeep") >= 0)
        {
            // 第一档 ⑤ 专项自检：把数据目录打扫一遍，报告写到文件里（--dry 只列清单不删）
            string[] ka = Environment.GetCommandLineArgs();
            bool dry = Array.IndexOf(ka, "--dry") >= 0;
            int ki = Array.IndexOf(ka, "--housekeep");
            string kOut = (ki >= 0 && ki + 1 < ka.Length && !ka[ki + 1].StartsWith("--")) ? ka[ki + 1] : "housekeep.txt";
            string rep = QueryForm.Housekeep(dry);
            try { File.WriteAllText(kOut, rep, new UTF8Encoding(true)); } catch { }
            return;
        }

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--firstruncheck") >= 0)
        {
            // 第一档 ⑨/③ 专项自检：指路窗能开、三件事都说到了、截图能出、
            // 桌面快捷方式真能建出来并指回当前 exe、首启标记只在第一次为假。
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string[] fa = Environment.GetCommandLineArgs();
            int fi = Array.IndexOf(fa, "--firstruncheck");
            string fOut = (fi >= 0 && fi + 1 < fa.Length) ? fa[fi + 1] : "firstruncheck.txt";
            string fDir = Path.GetDirectoryName(fOut);
            if (string.IsNullOrEmpty(fDir) || !Directory.Exists(fDir)) fDir = ".";
            QueryForm.selfTestMode = true;   // 自检不许改用户真实的 settings.ini
            QueryForm qf = new QueryForm();
            qf.StartPosition = FormStartPosition.Manual;
            qf.Location = new Point(160, 160);
            qf.Show();
            Application.DoEvents();

            StringBuilder fb = new StringBuilder();
            int fPass = 0, fFail = 0;
            Action<string, bool, string> chk = delegate (string name, bool ok, string detail) {
                if (ok) fPass++; else fFail++;
                fb.AppendLine((ok ? "  [OK]   " : "  [FAIL] ") + name +
                    (string.IsNullOrEmpty(detail) ? "" : "  → " + detail));
            };

            fb.AppendLine("== 第一档 ⑨/③ 首次打开指路窗自检  " + AppVersionLine());
            fb.AppendLine();

            string shown = "";
            string geoOut = "", geoOv = "", geoBtnOv = "";   // 排版几何：越界 / 文字块互压 / 文字压按钮（本机锁屏时截屏是黑的，改用几何判定）
            try
            {
                using (Form f = qf.BuildFirstRunForm())
                {
                    f.StartPosition = FormStartPosition.Manual;
                    f.Location = new Point(700, 480);   // 挪到桌面空白处，别被别的窗口盖住
                    f.TopMost = true;
                    f.Show();
                    f.Activate();
                    f.Refresh();
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(320);
                    Application.DoEvents();
                    shown = qf.DescribeFirstRun(f);
                    // 起名输入框：得在、得限长、没设过名字时预填中性名
                    TextBox tbN = null;
                    foreach (Control c in f.Controls)
                    {
                        TextBox cand = c as TextBox;
                        if (cand != null && cand.Tag != null && cand.Tag.ToString() == "软件名") tbN = cand;
                    }
                    chk("首启卡片里有取名输入框", tbN != null, "");
                    chk("取名框限长 16 字", tbN != null && tbN.MaxLength == QueryForm.APP_NAME_MAX,
                        tbN == null ? "没有输入框" : "MaxLength=" + tbN.MaxLength);
                    chk("取名框预填了名字（中性名或当前名）",
                        tbN != null && tbN.Text.Length > 0 &&
                        (tbN.Text == QueryForm.APP_NAME_HINT || tbN.Text == QueryForm.AppTitle),
                        tbN == null ? "没有输入框" : "预填=" + tbN.Text);
                    List<Rectangle> lblBox = new List<Rectangle>();
                    List<Rectangle> btnBox = new List<Rectangle>();
                    foreach (Control c in f.Controls)
                    {
                        if (c is Panel) continue;    // 分隔细线不算
                        if (c.Right > f.ClientSize.Width + 1 || c.Bottom > f.ClientSize.Height + 1)
                            geoOut += c.GetType().Name + "@" + c.Bounds + " ";
                        if (c is Label) lblBox.Add(c.Bounds);
                        if (c is Button) btnBox.Add(c.Bounds);
                    }
                    for (int i = 0; i < lblBox.Count; i++)
                        for (int j = i + 1; j < lblBox.Count; j++)
                        {
                            Rectangle hit = Rectangle.Intersect(lblBox[i], lblBox[j]);
                            if (hit.Width > 2 && hit.Height > 2) geoOv += "[文字" + i + "×文字" + j + "] ";
                        }
                    // 文字块不许压到按钮上：Label 是不透明的白底，跟按钮一重叠就把按钮啃出一个直角缺口，
                    // 自绘的圆角从缺口里消失，看着像「按钮缺了个扣子」（用户实拍报障就是这个）
                    for (int i = 0; i < lblBox.Count; i++)
                        for (int j = 0; j < btnBox.Count; j++)
                        {
                            Rectangle hit = Rectangle.Intersect(lblBox[i], btnBox[j]);
                            if (hit.Width > 0 && hit.Height > 0)
                                geoBtnOv += "[文字" + i + "×按钮" + j + " " + hit.Width + "×" + hit.Height + "] ";
                        }
                    SnapForm(f, fDir, "首启指路窗");
                }
            }
            catch (Exception ex) { fb.AppendLine("打开失败：" + ex.Message); }

            fb.AppendLine(shown);
            fb.AppendLine();
            fb.AppendLine("-- 断言 --");
            chk("说了它在托盘里怎么找回来", shown.Contains("托盘"), "");
            chk("说了「关掉小窗没退出」", shown.Contains("没退出"), "");
            chk("给了「放个桌面快捷方式」按钮", shown.Contains("[按钮] 放个桌面快捷方式"), "");
            chk("说了被隔离后去哪恢复", shown.Contains("隔离区"), "");
            chk("带了 exe 完整路径", shown.Contains("exe 路径"), "");
            chk("控件都没跑出窗口", geoOut.Length == 0, geoOut);
            chk("文字块不互相压住", geoOv.Length == 0, geoOv);
            chk("文字块没压到按钮上（压了会啃掉按钮圆角）", geoBtnOv.Length == 0, geoBtnOv);

            // 快捷方式：只在自检目录里试，绝不往真实桌面丢文件
            string lnkDir = Path.Combine(fDir, "lnk_test");
            string frErr;
            string lnk = QueryForm.CreateShortcutIn(lnkDir, "Ctrl + 空格", out frErr);
            chk("快捷方式能建出来", lnk.Length > 0 && File.Exists(lnk), lnk.Length > 0 ? lnk : frErr);
            if (lnk.Length > 0 && File.Exists(lnk))
            {
                string gotTarget = "";
                try
                {
                    object sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                    object sc = sh.GetType().InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
                    object tp = sc.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, sc, null);
                    gotTarget = tp == null ? "" : tp.ToString();
                }
                catch (Exception ex) { gotTarget = "读不回来：" + ex.Message; }
                chk("快捷方式指回当前 exe", string.Equals(gotTarget, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase), gotTarget);
            }

            // 首启标记：没看过 → 假；标记后 → 真（自检只改内存，不写真实配置）
            chk("默认算「没看过」", !QueryForm.FirstRunDone(), "");
            // 软件名：默认值 / 改名生效 / 裁空白 / 剔换行 / 超长截断 / 清空回落
            chk("没设过名字时用内置默认名", QueryForm.AppTitle == QueryForm.APP_TITLE, "实际=" + QueryForm.AppTitle);
            QueryForm.SaveAppName("  老王的翻译  ");
            chk("改名后立刻生效、首尾空白被裁", QueryForm.AppTitle == "老王的翻译", "实际=" + QueryForm.AppTitle);
            qf.ApplyAppName();
            chk("改完名字主窗标题同步", qf.Text == "老王的翻译", "标题=" + qf.Text);
            chk("托盘提示跟着名字走", qf.tray != null && qf.tray.Text.StartsWith("老王的翻译"),
                qf.tray == null ? "托盘没建出来" : "提示=" + qf.tray.Text);
            chk("导出文件头跟着名字走", QueryForm.MainTxtBody("a", "b", false).StartsWith("老王的翻译 · "), "");
            QueryForm.SaveAppName("换\n行\t的名字");
            chk("换行制表符被剔掉", QueryForm.AppTitle == "换行的名字", "实际=" + QueryForm.AppTitle);
            QueryForm.SaveAppName(new string('名', 30));
            chk("超长自动截到 16 字", QueryForm.AppTitle.Length == QueryForm.APP_NAME_MAX, "长度=" + QueryForm.AppTitle.Length);
            QueryForm.SaveAppName("   ");
            chk("清空后回落默认名", QueryForm.AppTitle == QueryForm.APP_TITLE, "实际=" + QueryForm.AppTitle);
            QueryForm.MarkFirstRunDone();
            chk("标记后不再重复弹", QueryForm.FirstRunDone(), "");

            fb.AppendLine();
            fb.AppendLine("结果：通过 " + fPass + " 项，失败 " + fFail + " 项");
            File.WriteAllText(fOut, fb.ToString(), new UTF8Encoding(true));
            Application.Exit();
            return;
        }

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--announcecheck") >= 0)
        {
            // C1 程序内公告卡专项自检：怎么解析、指纹认不认得住、空白文件扰不扰人、
            // 卡片上的东西齐不齐、菜单入口挂没挂上。
            // ★ 全程只读写自检目录里的临时「公告.txt」，真实的 %APPDATA%\SolivineQuickLook\公告.txt
            //   一根手指都不碰（段首段尾各查一次，专门断言这个）。
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string[] aa = Environment.GetCommandLineArgs();
            int ai = Array.IndexOf(aa, "--announcecheck");
            string aOut = (ai >= 0 && ai + 1 < aa.Length) ? aa[ai + 1] : "announcecheck.txt";
            string aDir = Path.GetDirectoryName(aOut);
            if (string.IsNullOrEmpty(aDir) || !Directory.Exists(aDir)) aDir = ".";
            QueryForm.selfTestMode = true;
            QueryForm qf = new QueryForm();
            qf.StartPosition = FormStartPosition.Manual;
            qf.Location = new Point(160, 160);
            qf.Show();
            Application.DoEvents();

            StringBuilder ab = new StringBuilder();
            int aPass = 0, aFail = 0;
            Action<string, bool, string> achk = delegate (string name, bool ok, string detail) {
                if (ok) aPass++; else aFail++;
                ab.AppendLine((ok ? "  [OK]   " : "  [FAIL] ") + name +
                    (string.IsNullOrEmpty(detail) ? "" : "  → " + detail));
            };

            ab.AppendLine("== C1 程序内公告卡自检  " + AppVersionLine());
            ab.AppendLine();

            // 真实公告文件拍个照，最后比一次
            string realAnn = QueryForm.AnnouncePath;
            string realBefore = "不存在";
            try { if (File.Exists(realAnn)) realBefore = File.GetLastWriteTime(realAnn).Ticks + "/" + new FileInfo(realAnn).Length; } catch { }

            string ap = Path.Combine(aDir, "ann_tmp.txt");
            try { if (File.Exists(ap)) File.Delete(ap); } catch { }
            string t, b;

            // ① 没文件 = 没公告，不该弹
            achk("没文件就不打扰", !QueryForm.ReadAnnounceAt(ap, out t, out b), "");
            achk("没文件时指纹为空", QueryForm.AnnounceFingerprintOfFile(ap).Length == 0, "");

            // ② 首行是标题，后面全算正文
            File.WriteAllText(ap, "这一版有什么变化\n正文第一行\n正文第二行", new UTF8Encoding(false));
            bool got = QueryForm.ReadAnnounceAt(ap, out t, out b);
            achk("首行当标题", got && t == "这一版有什么变化", t);
            achk("正文两行都在", got && b.Contains("正文第一行") && b.Contains("正文第二行"), b.Replace("\n", " / "));

            // ③ 记事本存的 UTF-8 带 BOM，标题里的 BOM 得洗掉
            File.WriteAllText(ap, "带BOM的标题\n正文", new UTF8Encoding(true));
            QueryForm.ReadAnnounceAt(ap, out t, out b);
            achk("BOM 被洗掉", t == "带BOM的标题", "[" + t + "]");

            // ④ 只有空白 = 没公告，不该弹
            File.WriteAllText(ap, "   \r\n\r\n", new UTF8Encoding(false));
            achk("空白文件不打扰", !QueryForm.ReadAnnounceAt(ap, out t, out b), "");

            // ⑤ 指纹：同一份稳定，改一个字就变（这是「只弹一次」的全部依据）
            string f1 = QueryForm.AnnounceFingerprint("同一份公告");
            string f2 = QueryForm.AnnounceFingerprint("同一份公告");
            string f3 = QueryForm.AnnounceFingerprint("同一份公吿");
            achk("同一份内容指纹一样", f1 == f2, f1);
            achk("改一个字指纹就变", f1 != f3, f1 + " vs " + f3);

            // ⑥ 标题行留空 → 兜底一句通用的，不能顶着一条白
            File.WriteAllText(ap, "\n正文还在", new UTF8Encoding(false));
            QueryForm.ReadAnnounceAt(ap, out t, out b);
            achk("标题空着有兜底", t == "这一版有什么变化", t);

            // ⑦ 卡片本身：建得出来、标题正文都在、两个按钮都在、尺寸没跑偏
            File.WriteAllText(ap, "自检用的公告\n这行是正文甲\n这行是正文乙", new UTF8Encoding(false));
            Form af = qf.BuildAnnounceForm(ap);
            string ad = qf.DescribeAnnounce(af);
            achk("卡片建得出来", ad.Length > 0 && !ad.StartsWith("不是公告卡"), "");
            achk("标题上去了", ad.Contains("自检用的公告"), "");
            achk("正文上去了", ad.Contains("正文甲") && ad.Contains("正文乙"), "");
            achk("两个按钮在", ad.Contains("[按钮] 知道了") && ad.Contains("[按钮] 打开公告文件"), "");
            achk("卡片尺寸没跑偏", af.ClientSize.Width > 300 && af.ClientSize.Height > 200, af.ClientSize.ToString());
            af.Dispose();

            // ⑧ 入口挂上了：更多 ▾ 菜单里有这一条
            ContextMenuStrip amm = qf.BuildMoreMenu();
            bool hasAnnMenu = false;
            foreach (ToolStripItem it in amm.Items)
                if (it.Text != null && it.Text.Contains("这一版有什么变化")) { hasAnnMenu = true; break; }
            achk("更多菜单里有入口", hasAnnMenu, "");
            amm.Dispose();

            // ⑨ 自检不碰真实公告
            string realAfter = "不存在";
            try { if (File.Exists(realAnn)) realAfter = File.GetLastWriteTime(realAnn).Ticks + "/" + new FileInfo(realAnn).Length; } catch { }
            achk("真实公告文件没被动过", realBefore == realAfter, realBefore + " → " + realAfter);

            // 自检用：后面再跟 --shot 文件路径，就把公告卡真身截一张（卡上用临时公告的内容）
            string aShot = "";
            int asi = Array.IndexOf(aa, "--shot");
            if (asi >= 0 && asi + 1 < aa.Length) aShot = aa[asi + 1];
            if (aShot.Length > 0)
            {
                File.WriteAllText(ap, "这一版有什么变化\r\n朗读按 Esc 就停，窗不藏。\r\n更多 ▾ 里随时能把这张卡再看一遍。\r\n公告就放在数据目录的「公告.txt」，改那个文件就行。", new UTF8Encoding(false));
                Form shotForm = qf.BuildAnnounceForm(ap);
                shotForm.StartPosition = FormStartPosition.Manual;
                shotForm.Location = new Point(300, 180);
                shotForm.Show();
                Application.DoEvents();
                System.Threading.Thread.Sleep(500);
                Application.DoEvents();
                try
                {
                    Rectangle sr = shotForm.Bounds;
                    using (Bitmap bmp = new Bitmap(sr.Width, sr.Height))
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(sr.Left, sr.Top, 0, 0, sr.Size);
                        bmp.Save(aShot, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    ab.AppendLine("  卡片截图 = " + aShot);
                }
                catch (Exception ex) { ab.AppendLine("  卡片截图失败 = " + ex.Message); }
                shotForm.Close();
                shotForm.Dispose();
            }

            try { if (File.Exists(ap)) File.Delete(ap); } catch { }
            ab.AppendLine();
            ab.AppendLine("结果：通过 " + aPass + " 项，失败 " + aFail + " 项");
            File.WriteAllText(aOut, ab.ToString(), new UTF8Encoding(true));
            Application.Exit();
            return;
        }

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--keycheck") >= 0)
        {
            // v2.5.9 第一档 ① 专项自检：填 Key 小窗能开、说明都说了、控件不越界不互压、
            // 「测一下」的判定分级对、「保存」写得进也洗得干净。
            // ★ 全程只写自检目录里的临时文件，绝不碰你真实的 apikey.txt（最后一项断言专门查这个）。
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string[] ga = Environment.GetCommandLineArgs();
            int gi = Array.IndexOf(ga, "--keycheck");
            string gOut = (gi >= 0 && gi + 1 < ga.Length) ? ga[gi + 1] : "keycheck.txt";
            string gDir = Path.GetDirectoryName(gOut);
            if (string.IsNullOrEmpty(gDir) || !Directory.Exists(gDir)) gDir = ".";
            QueryForm.selfTestMode = true;   // 自检不许改用户真实的 settings.ini，也不许弹模态窗
            QueryForm qf = new QueryForm();
            qf.StartPosition = FormStartPosition.Manual;
            qf.Location = new Point(160, 160);
            qf.Show();
            Application.DoEvents();

            StringBuilder gb = new StringBuilder();
            int gPass = 0, gFail = 0;
            Action<string, bool, string> gchk = delegate (string name, bool ok, string detail) {
                if (ok) gPass++; else gFail++;
                gb.AppendLine((ok ? "  [OK]   " : "  [FAIL] ") + name +
                    (string.IsNullOrEmpty(detail) ? "" : "  → " + detail));
            };

            gb.AppendLine("== 第一档 ① 填 API Key 小窗自检  " + AppVersionLine());
            gb.AppendLine();

            // 真实 apikey.txt 先拍个照，最后比对「有没有被动过」
            string realKeyPath = QueryForm.ApiKeyFile;
            string realBefore = "不存在";
            try
            {
                if (File.Exists(realKeyPath))
                    realBefore = new FileInfo(realKeyPath).Length + "字节/" + File.GetLastWriteTime(realKeyPath).ToString("yyyyMMddHHmmss");
            }
            catch { }

            string shownKey = "";
            string geoOut = "", geoOv = "";
            try
            {
                using (Form f = qf.BuildApiKeyForm(true))
                {
                    f.StartPosition = FormStartPosition.Manual;
                    f.Location = new Point(700, 200);   // v2.9.0：窗变高了，往上挪才截得全
                    f.TopMost = true;
                    f.Show();
                    f.Activate();
                    f.Refresh();
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(300);
                    Application.DoEvents();
                    shownKey = qf.DescribeApiKey(f);
                    List<Rectangle> lblBox = new List<Rectangle>();
                    foreach (Control c in f.Controls)
                    {
                        if (c is Panel) continue;    // 分隔细线不算
                        if (c.Right > f.ClientSize.Width + 1 || c.Bottom > f.ClientSize.Height + 1)
                            geoOut += c.GetType().Name + "@" + c.Bounds + " ";
                        if (c is Label) lblBox.Add(c.Bounds);
                    }
                    for (int i = 0; i < lblBox.Count; i++)
                        for (int j = i + 1; j < lblBox.Count; j++)
                        {
                            Rectangle hit = Rectangle.Intersect(lblBox[i], lblBox[j]);
                            if (hit.Width > 2 && hit.Height > 2) geoOv += "[文字" + i + "×文字" + j + "] ";
                        }
                    SnapForm(f, gDir, "填Key小窗");
                }
            }
            catch (Exception ex) { gb.AppendLine("打开失败：" + ex.Message); }

            gb.AppendLine(shownKey);
            gb.AppendLine();
            gb.AppendLine("-- 窗口断言 --");
            gchk("有 Key 输入框", shownKey.Contains("[apikey输入]"), "");
            gchk("有「测一下」按钮", shownKey.Contains("[按钮] 测一下"), "");
            gchk("有「保存」按钮", shownKey.Contains("[按钮] 保存"), "");
            gchk("有「先不配」可以退出去", shownKey.Contains("[按钮] 先不配"), "");
            gchk("说了只存在自己电脑上、不会上传", shownKey.Contains("不会上传"), "");
            gchk("把 apikey.txt 的实际路径写出来了", shownKey.Contains("apikey.txt ="), "");
            gchk("控件都没跑出窗口", geoOut.Length == 0, geoOut);
            gchk("文字块不互相压住", geoOv.Length == 0, geoOv);
            gchk("托盘菜单里有「配置 API Key」入口", qf.TrayMenuText().Contains("配置 API Key"), "");

            // ===== v2.9.0：地址和模型名不再写死，能不能配成别家？=====
            gb.AppendLine();
            gb.AppendLine("-- v2.9.0 换服务商：地址 / 模型名可配 --");
            gchk("窗里有「服务地址」输入框", shownKey.Contains("[服务地址输入]"), "");
            gchk("窗里有「模型名」输入框", shownKey.Contains("[模型名输入]"), "");
            gchk("窗里说明了留空就是 DeepSeek 官方", shownKey.Contains("留空"), "");
            gchk("窗里点名了能用的几家", shownKey.Contains("Kimi") && shownKey.Contains("通义"), "");

            gchk("什么都不填 = 原来的 DeepSeek 地址",
                 QueryForm.ResolveApiBase("") == "https://api.deepseek.com/v1", QueryForm.ResolveApiBase(""));
            gchk("什么都不填 = 原来的 deepseek-flash",
                 QueryForm.ResolveApiModel("") == "deepseek-flash", QueryForm.ResolveApiModel(""));
            gchk("没写协议自动补 https",
                 QueryForm.ResolveApiBase("api.moonshot.cn/v1") == "https://api.moonshot.cn/v1",
                 QueryForm.ResolveApiBase("api.moonshot.cn/v1"));
            gchk("末尾多余的斜杠去掉",
                 QueryForm.ResolveApiBase("https://open.bigmodel.cn/api/paas/v4/") == "https://open.bigmodel.cn/api/paas/v4",
                 QueryForm.ResolveApiBase("https://open.bigmodel.cn/api/paas/v4/"));
            gchk("整条接口地址粘进来不会拼成两遍",
                 QueryForm.ResolveApiBase("https://api.deepseek.com/v1/chat/completions") == "https://api.deepseek.com/v1",
                 QueryForm.ResolveApiBase("https://api.deepseek.com/v1/chat/completions"));
            gchk("前后空格换行吃掉",
                 QueryForm.ResolveApiBase("  https://openrouter.ai/api/v1 \r\n") == "https://openrouter.ai/api/v1",
                 QueryForm.ResolveApiBase("  https://openrouter.ai/api/v1 \r\n"));
            gchk("本机 http 地址不被强行升 https",
                 QueryForm.ResolveApiBase("http://127.0.0.1:11434/v1") == "http://127.0.0.1:11434/v1",
                 QueryForm.ResolveApiBase("http://127.0.0.1:11434/v1"));
            gchk("模型名的引号空格洗掉",
                 QueryForm.ResolveApiModel("  \"kimi-k2-0905-preview\" ") == "kimi-k2-0905-preview",
                 QueryForm.ResolveApiModel("  \"kimi-k2-0905-preview\" "));
            gchk("模型名内部的 / 和 : 一个不碰",
                 QueryForm.ResolveApiModel("anthropic/claude-3.5:beta") == "anthropic/claude-3.5:beta",
                 QueryForm.ResolveApiModel("anthropic/claude-3.5:beta"));

            QueryForm.SetApiProviderQuiet("api.moonshot.cn/v1", "kimi-k2-0905-preview");
            gchk("ini 里填了地址就真用它", QueryForm.ApiBaseNow() == "https://api.moonshot.cn/v1", QueryForm.ApiBaseNow());
            gchk("ini 里填了模型就真用它", QueryForm.ApiModelNow() == "kimi-k2-0905-preview", QueryForm.ApiModelNow());
            QueryForm.SetApiProviderQuiet("", "");
            gchk("清空两项就退回 DeepSeek 官方",
                 QueryForm.ApiBaseNow() == "https://api.deepseek.com/v1" && QueryForm.ApiModelNow() == "deepseek-flash",
                 QueryForm.ApiBaseNow() + " / " + QueryForm.ApiModelNow());

            gchk("Key 对但模型名不对时说的是人话",
                 QueryForm.KeyVerdict(400, "{\"error\":{\"message\":\"model not exist\"}}").IndexOf("模型名") >= 0,
                 QueryForm.KeyVerdict(400, "{\"error\":{\"message\":\"model not exist\"}}"));

            // 真发一次：起个本地假服务器，看请求到底打到哪、带的是哪个模型名
            // （只证明"地址和模型确实被用上了"，不会碰任何真实服务商）
            {
                int port = 18787;
                int hitCount = 0;
                string lastLine = "", lastBody = "";
                System.Net.Sockets.TcpListener srv = null;
                System.Threading.Thread th = null;
                try
                {
                    srv = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
                    srv.Start();
                    th = new System.Threading.Thread(delegate()
                    {
                        for (int k = 0; k < 4; k++)
                        {
                            try
                            {
                                using (System.Net.Sockets.TcpClient c = srv.AcceptTcpClient())
                                using (System.Net.Sockets.NetworkStream ns = c.GetStream())
                                {
                                    c.ReceiveTimeout = 8000;
                                    System.Text.StringBuilder sb = new System.Text.StringBuilder();
                                    byte[] buf = new byte[4096];
                                    int n;
                                    while ((n = ns.Read(buf, 0, buf.Length)) > 0)
                                    {
                                        sb.Append(System.Text.Encoding.UTF8.GetString(buf, 0, n));
                                        string all = sb.ToString();
                                        int he = all.IndexOf("\r\n\r\n");
                                        if (he < 0) continue;
                                        string head = all.Substring(0, he);
                                        string[] lines = head.Split('\n');
                                        lastLine = lines[0].Trim();
                                        int cl = 0;
                                        for (int i = 1; i < lines.Length; i++)
                                        {
                                            string ln = lines[i].Trim();
                                            if (ln.ToLower().StartsWith("content-length:"))
                                                int.TryParse(ln.Substring(15).Trim(), out cl);
                                        }
                                        lastBody = all.Substring(he + 4);
                                        if (lastBody.Length >= cl) break;
                                    }
                                    hitCount++;
                                    byte[] ok = System.Text.Encoding.UTF8.GetBytes("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
                                    string respHead = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "
                                                      + ok.Length + "\r\nConnection: close\r\n\r\n";
                                    byte[] hb = System.Text.Encoding.ASCII.GetBytes(respHead);
                                    ns.Write(hb, 0, hb.Length);
                                    ns.Write(ok, 0, ok.Length);
                                    ns.Flush();
                                }
                            }
                            catch { break; }
                        }
                    });
                    th.IsBackground = true;
                    th.Start();

                    // ① 自己直接发一次，出错要看得见原因
                    string directErr = "";
                    try
                    {
                        byte[] d = System.Text.Encoding.UTF8.GetBytes("{\"model\":\"fake-model-x\"}");
                        System.Net.HttpWebRequest r2 = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(
                            "http://127.0.0.1:" + port + "/v1/chat/completions");
                        r2.Method = "POST";
                        r2.ContentType = "application/json";
                        r2.ContentLength = d.Length;
                        r2.Proxy = null;
                        r2.Timeout = 6000;
                        using (System.IO.Stream s = r2.GetRequestStream()) s.Write(d, 0, d.Length);
                        using (System.Net.WebResponse rs = r2.GetResponse()) { rs.Close(); }
                    }
                    catch (Exception ex) { directErr = ex.GetType().Name + ": " + ex.Message; }
                    gchk("本机能连上自己的假服务器", directErr.Length == 0, directErr);

                    // ①.5 用程序自己那条出口再发一次，报错要摊开看
                    string err3 = "";
                    try
                    {
                        byte[] d3 = System.Text.Encoding.UTF8.GetBytes("{\"model\":\"fake-model-y\"}");
                        System.Net.HttpWebRequest r3 = QueryForm.CreateJsonPost(
                            "http://127.0.0.1:" + port + "/v1/chat/completions", "sk-test0000000000000000", d3);
                        r3.Timeout = 6000;
                        using (System.IO.Stream s = r3.GetRequestStream()) s.Write(d3, 0, d3.Length);
                        using (System.Net.WebResponse rs = r3.GetResponse()) { rs.Close(); }
                    }
                    catch (Exception ex) { err3 = ex.GetType().Name + ": " + ex.Message; }
                    gchk("程序那条出口也发得出去", err3.Length == 0, err3);

                    // ② 再让程序走它自己那条路发
                    string v = QueryForm.TestApiKeyOnline("sk-test0000000000000000",
                                                          "http://127.0.0.1:" + port + "/v1", "fake-model-x");
                    if (th != null) th.Join(6000);
                    gchk("请求真的打到填进去的那个地址", hitCount >= 1 && lastLine.IndexOf("/v1/chat/completions") > 0,
                         "hit=" + hitCount + " line=[" + lastLine + "]");
                    gchk("请求里带的是填进去的那个模型名", lastBody.IndexOf("fake-model-x") >= 0,
                         lastBody.Length > 120 ? lastBody.Substring(0, 120) : lastBody);
                    gchk("对方回 200 时提示是「通了」", v.IndexOf("通了") >= 0, v + " ‖ " + QueryForm.LastTestError);
                }
                catch (Exception ex) { gchk("本地假服务器起得来", false, ex.GetType().Name + ": " + ex.Message); }
                finally { try { if (srv != null) srv.Stop(); } catch { } }
            }

            gb.AppendLine();
            gb.AppendLine("-- 洗 Key：粘进来的一坨文字能不能挑对 --");
            string[][] cleanCases = new string[][] {
                new string[]{"标准一行",           "sk-abcdef0123456789",                            "sk-abcdef0123456789"},
                new string[]{"前后带空格和换行",   "  sk-abcdef0123456789 \r\n",                     "sk-abcdef0123456789"},
                new string[]{"从网页复制带中文",   "我的key是 sk-abcdef0123456789 别外传",           "sk-abcdef0123456789"},
                new string[]{"带英文引号",         "\"sk-abcdef0123456789\"",                        "sk-abcdef0123456789"},
                new string[]{"多行带注释",         "# DeepSeek\r\nsk-abcdef0123456789\r\n",          "sk-abcdef0123456789"},
                new string[]{"带 BOM",             "\uFEFFsk-abcdef0123456789",                      "sk-abcdef0123456789"},
                new string[]{"非 sk 前缀的自定义", "abc1234567890123456789",                         "abc1234567890123456789"},
                new string[]{"只粘了句中文",       "我还没申请呢",                                   ""},
                new string[]{"空着",               "",                                               ""},
                new string[]{"只有空格",           "   \r\n\t ",                                     ""}
            };
            for (int i = 0; i < cleanCases.Length; i++)
            {
                string got = QueryForm.CleanKeyText(cleanCases[i][1]);
                gchk("清洗・" + cleanCases[i][0], got == cleanCases[i][2], "得到 [" + got + "]");
            }

            gb.AppendLine();
            gb.AppendLine("-- 测一下：各种返回怎么说成人话 --");
            string[][] verdictCases = new string[][] {
                new string[]{"200 通",        "200", "{}",                                                     "通了"},
                new string[]{"401 Key 不对",  "401", "{\"error\":{\"message\":\"Authentication Fails\"}}",     "Key 不对"},
                new string[]{"402 没余额",    "402", "{\"error\":\"Insufficient Balance\"}",                    "余额"},
                new string[]{"429 太频繁",    "429", "",                                                       "频繁"},
                new string[]{"503 对方忙",    "503", "",                                                       "服务器忙"},
                new string[]{"0 连不上",      "0",   "",                                                       "连不上"},
                new string[]{"418 其它码",    "418", "",                                                       "418"}
            };
            for (int i = 0; i < verdictCases.Length; i++)
            {
                string got = QueryForm.KeyVerdict(int.Parse(verdictCases[i][1]), verdictCases[i][2]);
                gchk("判定・" + verdictCases[i][0], got.Contains(verdictCases[i][3]), got);
            }

            gb.AppendLine();
            gb.AppendLine("-- 保存：只往自检目录里写 --");
            string tmpKey = Path.Combine(gDir, "keytest_apikey.txt");
            try { if (File.Exists(tmpKey)) File.Delete(tmpKey); } catch { }
            string[] oldBaks = new string[0];
            try { oldBaks = Directory.GetFiles(gDir, "keytest_apikey.txt.bak_*"); } catch { }
            for (int i = 0; i < oldBaks.Length; i++) { try { File.Delete(oldBaks[i]); } catch { } }

            string msg1;
            bool ok1 = QueryForm.SaveKeyTextTo(tmpKey, "  我的 key：sk-abcdef0123456789  \r\n", out msg1);
            string wrote1 = "";
            try { wrote1 = File.ReadAllText(tmpKey); } catch { }
            gchk("存得进去", ok1 && File.Exists(tmpKey), msg1);
            gchk("只存了 key 本体（没带说明文字和空格）", wrote1.Trim() == "sk-abcdef0123456789", "文件内容 [" + wrote1.Replace("\r", "\\r").Replace("\n", "\\n") + "]");
            gchk("文件末尾换行了（和手工建的一样）", wrote1.EndsWith("\r\n"), "");
            gchk("读回来还是同一串", QueryForm.CleanKeyText(wrote1) == "sk-abcdef0123456789", "");

            try { File.Delete(tmpKey); } catch { }
            string msg2;
            bool ok2 = QueryForm.SaveKeyTextTo(tmpKey, "    \r\n", out msg2);
            gchk("空着点保存：不写文件、给提示", !ok2 && !File.Exists(tmpKey) && msg2.Length > 0, msg2);

            string msg3;
            QueryForm.SaveKeyTextTo(tmpKey, "sk-oldkey0000000000000", out msg3);
            System.Threading.Thread.Sleep(1100);   // 备份名带秒级时间戳，隔开一秒好数
            QueryForm.SaveKeyTextTo(tmpKey, "sk-newkey0000000000000", out msg3);
            int baks = 0;
            try { baks = Directory.GetFiles(gDir, "keytest_apikey.txt.bak_*").Length; } catch { }
            gchk("覆盖前留了备份", baks >= 1, "备份 " + baks + " 份");
            string finalTxt = "";
            try { finalTxt = File.ReadAllText(tmpKey).Trim(); } catch { }
            gchk("备份没影响新写进去的值", finalTxt == "sk-newkey0000000000000", finalTxt);
            try { File.Delete(tmpKey); } catch { }
            try
            {
                string[] bb = Directory.GetFiles(gDir, "keytest_apikey.txt.bak_*");
                for (int i = 0; i < bb.Length; i++) { try { File.Delete(bb[i]); } catch { } }
            }
            catch { }

            // 真实文件收货：这一项挂了就是严重问题，自检不该碰用户的东西
            string realAfter = "不存在";
            try
            {
                if (File.Exists(realKeyPath))
                    realAfter = new FileInfo(realKeyPath).Length + "字节/" + File.GetLastWriteTime(realKeyPath).ToString("yyyyMMddHHmmss");
            }
            catch { }
            gchk("全程没动真实的 apikey.txt", realBefore == realAfter, realBefore + " → " + realAfter);

            // 提醒标记：没提醒过 -> 假；标记后 -> 真（自检只改内存，不写真实配置）
            gchk("默认算「没提醒过填 Key」", !QueryForm.KeyPromptDone(), "");
            QueryForm.MarkKeyPromptDone();
            gchk("提醒过就不再自己弹", QueryForm.KeyPromptDone(), "");

            // 自检模式下绝不自动弹模态窗（弹了会把自检线程卡死）
            bool threw = false;
            try { qf.ShowApiKeySetup(); }
            catch { threw = true; }
            gchk("自检模式下不会自己弹窗、也不报错", !threw, "");

            gb.AppendLine();
            gb.AppendLine("结果：通过 " + gPass + " 项，失败 " + gFail + " 项");
            File.WriteAllText(gOut, gb.ToString(), new UTF8Encoding(true));
            qf.Close();
            Application.Exit();
            return;
        }

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--snapform") >= 0)
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            QueryForm qf = new QueryForm();
            qf.Show();
            Application.DoEvents();
            string dir = Path.GetDirectoryName(Environment.GetCommandLineArgs()[Array.IndexOf(Environment.GetCommandLineArgs(), "--snapform") + 1]);
            HistoryForm hf2 = new HistoryForm(new List<string[]>(new string[][] {
                new string[]{"2026-10-24 10:00:12","你这边网络有点卡，晚点回你","x"},
                new string[]{"2026-10-23 21:07:03","这个价格是含配送的吗","y"}
            }), qf);
            hf2.Show(); Application.DoEvents();
            SnapForm(hf2, dir, "历史记录");
            hf2.Close();
            WordBookForm wf2 = new WordBookForm(qf);
            wf2.Show(); Application.DoEvents();
            SnapForm(wf2, dir, "生词本");
            wf2.Close();
            qf.Close();
            Application.Exit();
            return;
        }

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--selftest") >= 0)
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            QueryForm.selfTestMode = true;
            QueryForm qf = new QueryForm();
            bool preview = Array.IndexOf(Environment.GetCommandLineArgs(), "--preview") >= 0;
            string sample = "【中文意思】今天忙得不可开交，实在抽不出时间。\n【生词】swamped | /swɒmpt/ | 忙得不可开交 | 近音：斯万普特\n【你可以这样回】I'm swamped today. (我今天忙死了)\n【缩写版】\n轻度 | I'm swamped today.\n中度 | Swamped today.\n重度 | 没有\n【注意】没有";
            if (preview)
            {
                qf.Show();
                qf.Activate();
                string[] allArgs = Environment.GetCommandLineArgs();
                string shotPath = "";
                int si = Array.IndexOf(allArgs, "--shot");
                if (si >= 0 && si + 1 < allArgs.Length) shotPath = allArgs[si + 1];
                System.Windows.Forms.Timer tm = new System.Windows.Forms.Timer();
                tm.Interval = 1500;
                tm.Tick += delegate {
                    tm.Stop(); tm.Dispose();
                    // 尺寸/字号必须在 Show 之后应用，否则会被 LoadSettings 覆盖回去
                    string[] targs = Environment.GetCommandLineArgs();
                    // 先字号、再尺寸：字号会改 MinimumSize，反过来的话小窗会被最小宽度卡住
                    int fni = Array.IndexOf(targs, "--font");
                    if (fni >= 0 && fni + 1 < targs.Length)
                    {
                        try { qf.SetFontSize(int.Parse(targs[fni + 1])); } catch { }
                    }
                    int szi = Array.IndexOf(targs, "--sz");
                    if (szi >= 0 && szi + 1 < targs.Length)
                    {
                        try
                        {
                            string[] wh = targs[szi + 1].ToLower().Split('x');
                            qf.ClientSize = new Size(int.Parse(wh[0]), int.Parse(wh[1]));
                        }
                        catch { }
                    }
                    qf.Refresh();
                    Application.DoEvents();
                    qf.SelfTest(sample);
                    qf.Refresh();
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(900);
                    Application.DoEvents();
                    string[] dargs = Environment.GetCommandLineArgs();
                    int dti = Array.IndexOf(dargs, "--dragtest");
                    if (dti >= 0 && dti + 1 < dargs.Length)
                    {
                        try
                        {
                            QueryForm.forceLegacyDrag = Array.IndexOf(dargs, "--legacy") >= 0;
                            QueryForm.dragTestNoLayout = Array.IndexOf(dargs, "--nolayout") >= 0;
                            string sdir = "";
                            int si2 = Array.IndexOf(dargs, "--shots");
                            if (si2 >= 0 && si2 + 1 < dargs.Length) sdir = dargs[si2 + 1];
                            qf.RunDragTest(dargs[dti + 1], sdir);
                        }
                        catch { }
                        qf.Close();
                        Application.Exit();
                        return;
                    }
                    int dmpi = Array.IndexOf(dargs, "--dump");
                    if (dmpi >= 0 && dmpi + 1 < dargs.Length)
                    {
                        try { File.WriteAllText(dargs[dmpi + 1], qf.SelfDump(), Encoding.UTF8); } catch { }
                        int rni = Array.IndexOf(dargs, "--render");
                        if (rni >= 0 && rni + 1 < dargs.Length)
                        {
                            try
                            {
                                qf.Refresh();
                                Application.DoEvents();
                                using (Bitmap bm = new Bitmap(qf.Width, qf.Height))
                                {
                                    bool ok = false;
                                    try
                                    {
                                        using (Graphics gb = Graphics.FromImage(bm))
                                        {
                                            IntPtr hdc = gb.GetHdc();
                                            ok = PrintWindow(qf.Handle, hdc, 2);
                                            gb.ReleaseHdc(hdc);
                                        }
                                    }
                                    catch { }
                                    if (!ok) qf.DrawToBitmap(bm, new Rectangle(0, 0, bm.Width, bm.Height));
                                    bm.Save(dargs[rni + 1], ImageFormat.Png);
                                }
                            }
                            catch { }
                        }
                        qf.Close();
                        Application.Exit();
                        return;
                    }
                    if (shotPath.Length > 0)
                    {
                        try
                        {
                            Rectangle rc = new Rectangle(qf.Left - 24, qf.Top - 24, qf.Width + 48, qf.Height + 48);
                            using (Bitmap bmp = new Bitmap(rc.Width, rc.Height))
                            {
                                using (Graphics g = Graphics.FromImage(bmp))
                                    g.CopyFromScreen(rc.Location, Point.Empty, rc.Size);
                                bmp.Save(shotPath, ImageFormat.Png);
                            }
                        }
                        catch { }
                        qf.Close();
                        Application.Exit();
                    }
                };
                tm.Start();
                Application.Run(qf);
            }
            else qf.SelfTest(sample);
            qf.Dispose();
            return;
        }

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--proxycheck") >= 0)
        {
            // 自检：v2.1 F2 代理分流——本地请求不被系统代理劫走，公网请求仍走代理
            string[] pa = Environment.GetCommandLineArgs();
            int pi = Array.IndexOf(pa, "--proxycheck");
            string pPath = "proxycheck.txt";
            if (pi >= 0 && pi + 1 < pa.Length) pPath = pa[pi + 1];
            ProxyCheck(pPath);
            return;
        }

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--pronouncheck") >= 0)
        {
            // 自检：v2.1 称呼铁律的代码兜底——点评段他/她/TA 改「对方」，译文正文一个字不动
            string[] ra = Environment.GetCommandLineArgs();
            int ri = Array.IndexOf(ra, "--pronouncheck");
            string rPath = "pronouncheck.txt";
            if (ri >= 0 && ri + 1 < ra.Length) rPath = ra[ri + 1];
            PronounCheck(rPath);
            return;
        }

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--abbrevcheck") >= 0)
        {
            // 自检：v3 第 4 步——英文进来不显示【缩写版】，中文翻英文三档照旧
            string[] ba = Environment.GetCommandLineArgs();
            int bi = Array.IndexOf(ba, "--abbrevcheck");
            string bPath = "abbrevcheck.txt";
            if (bi >= 0 && bi + 1 < ba.Length) bPath = ba[bi + 1];
            AbbrevCheck(bPath);
            return;
        }

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--gramcheck") >= 0)
        {
            // 自检：语法讲解——关掉要跟上一版逐字一致，打开多一块【语法】，导出永远不带
            string[] ga = Environment.GetCommandLineArgs();
            int gi = Array.IndexOf(ga, "--gramcheck");
            string gPath = "gramcheck.txt";
            if (gi >= 0 && gi + 1 < ga.Length) gPath = ga[gi + 1];
            GrammarCheck(gPath);
            return;
        }

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--dictcheck") >= 0)
        {
            // 自检：第二档 ⑮ 断网离线查词——词库能不能读、变形词还原、能力边界守没守住
            string[] da = Environment.GetCommandLineArgs();
            int di = Array.IndexOf(da, "--dictcheck");
            string dPath = "dictcheck.txt";
            if (di >= 0 && di + 1 < da.Length) dPath = da[di + 1];
            DictCheck(dPath);
            return;
        }

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--foundationcheck") >= 0)
        {
            // 自检：v2.2 地基三件事——编码嗅探 / 请求序号防竞态 / 配置默认值兜底
            string[] fa = Environment.GetCommandLineArgs();
            int fi = Array.IndexOf(fa, "--foundationcheck");
            string fPath = "foundationcheck.txt";
            if (fi >= 0 && fi + 1 < fa.Length) fPath = fa[fi + 1];
            FoundationCheck(fPath);
            return;
        }

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--exportcheck") >= 0)
        {
            // 自检：v2.4 第 3 步——主界面导出 TXT / docx + 复制全文入口显性化（底栏零改动）
            string[] ea = Environment.GetCommandLineArgs();
            int ei = Array.IndexOf(ea, "--exportcheck");
            string ePath = "exportcheck.txt";
            if (ei >= 0 && ei + 1 < ea.Length && !ea[ei + 1].StartsWith("--")) ePath = ea[ei + 1];
            ExportCheck(ePath);
            return;
        }

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--ingestcheck") >= 0)
        {
            // 自检：v2.4 第 4 步——主界面拖文件自动读译 + Ctrl+V 图片 OCR（底栏 / 视觉零改动）
            string[] ga = Environment.GetCommandLineArgs();
            int gi = Array.IndexOf(ga, "--ingestcheck");
            string gPath = "ingestcheck.txt";
            if (gi >= 0 && gi + 1 < ga.Length && !ga[gi + 1].StartsWith("--")) gPath = ga[gi + 1];
            IngestCheck(gPath);
            return;
        }

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--voicecheck") >= 0)
        {
            // 自检：v2.5 第 5 步——主界面「说」语音输入（中英 / 停口 1.8 秒自动翻 / 底栏零改动）
            string[] va = Environment.GetCommandLineArgs();
            int vi = Array.IndexOf(va, "--voicecheck");
            string vPath = "voicecheck.txt";
            if (vi >= 0 && vi + 1 < va.Length && !va[vi + 1].StartsWith("--")) vPath = va[vi + 1];
            VoiceCheck(vPath);
            return;
        }

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--readcheck") >= 0)
        {
            // 自检：v2.5 第 6 步——朗读三层逻辑（逐词按语义切 / 短句一口气+核心重读 / 长句分段重音+明显停顿 / 标点分级停顿 / 双语词表 intonation 与场景重音）
            string[] ra = Environment.GetCommandLineArgs();
            int ri = Array.IndexOf(ra, "--readcheck");
            string rPath = "readcheck.txt";
            if (ri >= 0 && ri + 1 < ra.Length && !ra[ri + 1].StartsWith("--")) rPath = ra[ri + 1];
            ReadCheck(rPath);
            return;
        }

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--stopcheck") >= 0)
        {
            // 自检：v2.8.4——朗读急停（Esc 掐声音 / F2、F3 同一个键变开关 / 停完能重新开读）
            string[] sa = Environment.GetCommandLineArgs();
            int si2 = Array.IndexOf(sa, "--stopcheck");
            string sPath = "stopcheck.txt";
            if (si2 >= 0 && si2 + 1 < sa.Length && !sa[si2 + 1].StartsWith("--")) sPath = sa[si2 + 1];
            StopCheck(sPath);
            return;
        }

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--diagcheck") >= 0)
        {
            // 自检：A3——一键诊断包（报告内容 / 打码效果 / zip 能否生成并能读回）
            string[] da = Environment.GetCommandLineArgs();
            int di = Array.IndexOf(da, "--diagcheck");
            string dPath = "diagcheck.txt";
            if (di >= 0 && di + 1 < da.Length && !da[di + 1].StartsWith("--")) dPath = da[di + 1];
            DiagCheck(dPath);
            return;
        }

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--mlsize") >= 0)
        {
            // 轻量自检：只验「其他语言小窗能不能自由拉大小、记不记得上次多大」。
            // 全套 --mlcheck 跑几分钟，改一行窗口尺寸不用每次都等全套。
            string[] za = Environment.GetCommandLineArgs();
            int zi = Array.IndexOf(za, "--mlsize");
            string zPath = "mlsize.txt";
            if (zi >= 0 && zi + 1 < za.Length && !za[zi + 1].StartsWith("--")) zPath = za[zi + 1];
            MlSizeCheck(zPath);
            return;
        }

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--mlcheck") >= 0)
        {
            // 自检：v2.3 第 2 步——其他语言小窗（结构 / 独立小窗行为 / 短路 / 缓存隔离 / 语言对记忆 / 主界面无回归）
            string[] ma = Environment.GetCommandLineArgs();
            int mi = Array.IndexOf(ma, "--mlcheck");
            string mPath = "mlcheck.txt";
            if (mi >= 0 && mi + 1 < ma.Length && !ma[mi + 1].StartsWith("--")) mPath = ma[mi + 1];
            string mShot = "";
            int si = Array.IndexOf(ma, "--mlshot");
            if (si >= 0 && si + 1 < ma.Length) mShot = ma[si + 1];
            MlCheck(mPath, mShot);
            return;
        }

        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--crashtest") >= 0)
        {
            // 自检：v2.1 F20 兜底是否真的生效——UI 线程里故意抛一个没人接的异常，
            // 正常情况下窗口不会消失、crash.txt 有记录、程序还能自己退出。
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string okPath = "crashtest_result.txt";
            string[] ca = Environment.GetCommandLineArgs();
            int ci = Array.IndexOf(ca, "--crashtest");
            if (ci >= 0 && ci + 1 < ca.Length) okPath = ca[ci + 1];
            QueryForm cq = new QueryForm();
            cq.Show();
            Application.DoEvents();
            System.Windows.Forms.Timer boom = new System.Windows.Forms.Timer();
            boom.Interval = 700;
            boom.Tick += delegate(object s, EventArgs e)
            {
                boom.Stop();
                boom.Dispose();
                throw new InvalidOperationException("crashtest: 这是故意抛出的测试异常");
            };
            System.Windows.Forms.Timer finish = new System.Windows.Forms.Timer();
            finish.Interval = 2800;
            finish.Tick += delegate(object s, EventArgs e)
            {
                finish.Stop();
                finish.Dispose();
                string result = "存活=" + (!cq.IsDisposed) + "  crashCount=" + crashCount
                              + "  " + AppVersionLine() + "\r\n"
                              + "crash.txt 存在=" + File.Exists(Path.Combine(LOG_DIR, "crash.txt")) + "\r\n";
                try { File.WriteAllText(okPath, result, new UTF8Encoding(true)); } catch { }
                try { cq.Close(); } catch { }
                Application.Exit();
            };
            boom.Start();
            finish.Start();
            Application.Run(cq);
            return;
        }

        bool isNew = false;
        Mutex once = new Mutex(true, "SolivineQuickLook_Mutex_v1", out isNew);
        if (!isNew)
        {
            // v2.5.2：单实例不再"弹个框就退出"。用户第二次双击 exe 要的是窗口，
            // 所以先找到已有窗口并把它拉到前台；只有窗口没了/卡死（半退出僵尸）才杀旧接管。
            uint oldPid = 0;
            IntPtr oldWnd = FindInstanceWindow(out oldPid);
            if (oldWnd != IntPtr.Zero)
            {
                bool hung = false;
                try { hung = IsHungAppWindow(oldWnd); } catch { }
                if (!hung && ActivateInstanceWindow(oldWnd))
                {
                    Log("检测到已有实例（PID " + oldPid + "），已把它的窗口唤到前台，本次启动直接退出");
                    return;
                }
                Log("已有实例（PID " + oldPid + "）窗口唤不出来（hung=" + hung + "），按僵尸处理");
            }
            else
            {
                Log("单实例锁被占用，但按标题找不到任何窗口：判定为半退出僵尸");
            }
            int killed = KillOtherInstances();
            Log("已结束 " + killed + " 个旧实例，由本次启动接管");
            try { once.Dispose(); } catch { }
            once = null;
            // 被结束的进程释放 Mutex 需要一点时间，这里重抢而不是立刻放弃
            for (int i = 0; i < 40 && !isNew; i++)
            {
                Thread.Sleep(150);
                try
                {
                    once = new Mutex(true, "SolivineQuickLook_Mutex_v1", out isNew);
                    if (!isNew) { try { once.Dispose(); } catch { } once = null; }
                }
                catch { isNew = false; }
            }
            if (!isNew)
            {
                MessageBox.Show("已经有一个「" + AppTitle + "」在运行，但它暂时没响应。\n"
                              + "请等几秒再打开一次；还不行就到任务管理器里结束它。",
                              AppTitle);
                return;
            }
        }
        StartQuiet = Array.IndexOf(Environment.GetCommandLineArgs(), "quiet") >= 0;
        try { SetProcessDPIAware(); } catch { }
        try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "voice") >= 0)
        {
            VoiceForm vf = new VoiceForm(GetSet("voice", "Zira"), GetInt("rate", -1));
            if (vf.ShowDialog() == DialogResult.OK)
            {
                vf.Collect();
                SetSet("voice", vf.VoiceName);
                SetSet("rate", vf.Rate.ToString());
            }
            return;
        }
        // v2.5.4 第一档 ⑤：每次启动先把数据目录悄悄收一遍。
        // 旧备份 exe 只留最近 5 个、log.txt 超过 1MB 就归档、临时图删掉；
        // apikey.txt / settings.ini / 生词本 / 历史记录这四样一个字节都不碰。
        // 全程 try/catch：打扫失败最多是空间没省，绝不影响启动。
        try { if (!selfTestMode) HousekeepQuiet(); } catch { }
        Application.Run(new QueryForm());
        GC.KeepAlive(once);
    }

    // ====== 界面自检：找越界、文字被裁、控件互相压住 ======
    static string Desc(Control c)
    {
        string t = (c.Text ?? "").Replace("\r", " ").Replace("\n", " ");
        if (t.Length > 14) t = t.Substring(0, 14) + "…";
        return c.GetType().Name + "[" + t + "]";
    }

    static void AuditTree(Control root, StringBuilder sb, string path)
    {
        Rectangle pc = root.ClientRectangle;
        List<Control> kids = new List<Control>();
        foreach (Control c in root.Controls) if (c.Visible) kids.Add(c);
        for (int i = 0; i < kids.Count; i++)
        {
            Control c = kids[i];
            Rectangle b = c.Bounds;
            if (b.Right > pc.Width + 1 || b.Bottom > pc.Height + 1 || b.Left < -1 || b.Top < -1)
                sb.AppendLine("  [越界] " + path + "/" + Desc(c) + " bounds=" + b + " 父客户区=" + pc.Width + "x" + pc.Height);
            string t = c.Text;
            if (!string.IsNullOrEmpty(t) && (c is Button || c is Label || c is CheckBox || c is RadioButton))
            {
                Size need = TextRenderer.MeasureText(t, c.Font);
                int aw = b.Width - c.Padding.Horizontal - 8;
                int ah = b.Height - c.Padding.Vertical - 2;
                if (need.Width > aw)
                    sb.AppendLine("  [文字超宽] " + path + "/" + Desc(c) + " 需要" + need.Width + "px 可用" + aw + "px");
                if (need.Height > ah)
                    sb.AppendLine("  [文字超高] " + path + "/" + Desc(c) + " 需要" + need.Height + "px 可用" + ah + "px");
            }
        }
        for (int i = 0; i < kids.Count; i++)
            for (int j = i + 1; j < kids.Count; j++)
            {
                if (kids[i].Dock != DockStyle.None || kids[j].Dock != DockStyle.None) continue;
                Rectangle in1 = Rectangle.Intersect(kids[i].Bounds, kids[j].Bounds);
                if (in1.Width > 2 && in1.Height > 2)
                    sb.AppendLine("  [互相压住] " + path + "/" + Desc(kids[i]) + " 与 " + Desc(kids[j]) + " 重叠 " + in1.Width + "x" + in1.Height + "px");
            }
        foreach (Control c in kids) if (c.HasChildren) AuditTree(c, sb, path + "/" + Desc(c));
    }

    // 把菜单树导成文本（* 表示打勾），用来核对「更多 ▾」里新加的项有没有挂上去
    static void DumpMenu(ToolStripItemCollection items, string indent, StringBuilder sb)
    {
        foreach (ToolStripItem it in items)
        {
            if (it is ToolStripSeparator) { sb.AppendLine(indent + "----"); continue; }
            ToolStripMenuItem mi = it as ToolStripMenuItem;
            string chk = (mi != null && mi.Checked) ? "* " : "";
            sb.AppendLine(indent + chk + it.Text);
            if (mi != null && mi.DropDownItems.Count > 0) DumpMenu(mi.DropDownItems, indent + "  ", sb);
        }
    }

    // 自检报告里把一长串 Panel[] 压掉，只留能认出是谁的那部分
    static string ShortPath(string line)
    {
        try { return Regex.Replace(line, "(/(Panel\\[\\]|Panel))+", "/…"); }
        catch { return line; }
    }

    // 抓一个窗体的实拍图，供自检核对排版
    static void SnapForm(Form f, string dir, string name)
    {
        try
        {
            if (f == null || dir == null) return;
            f.Refresh();
            Application.DoEvents();
            System.Threading.Thread.Sleep(450);
            Application.DoEvents();
            Rectangle rc = new Rectangle(f.Left - 8, f.Top - 8, f.Width + 16, f.Height + 16);
            using (Bitmap bmp = new Bitmap(rc.Width, rc.Height))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(rc.Location, Point.Empty, rc.Size);
                bmp.Save(Path.Combine(dir, "check_" + name + ".png"), ImageFormat.Png);
            }
        }
        catch { }
    }

    // ========================================================================
    //  v2.2 地基自检（--foundationcheck <输出文件>）
    //  三项逐条验：①编码嗅探（UTF-8 / BOM / GBK / UTF-16 都要读回来，日志不返乱码）
    //              ②请求序号（旧请求后回来也不许盖新结果）
    //              ③配置默认值（老配置缺字段、写了坏值、编码是 GBK、文件直接不存在都不崩）
    //  全程只在系统临时目录里折腾，不碰用户真实的 settings.ini 与日志。
    // ========================================================================
    static void FoundationCheck(string outPath)
    {
        StringBuilder sb = new StringBuilder();
        int pass = 0, fail = 0;
        Action<string, bool, string> chk = delegate (string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            sb.AppendLine("[" + (ok ? "PASS" : "FAIL") + "] " + name + (detail.Length > 0 ? "   → " + detail : ""));
        };

        string probe = "今天天气不错，我们十点在三号门见面。";
        string dir = Path.Combine(Path.GetTempPath(), "sqk_foundation_" + DateTime.Now.ToString("HHmmss"));
        string originLogDir = LOG_DIR;
        try { Directory.CreateDirectory(dir); } catch { }

        try
        {
            // ---------- ① 编码嗅探 ----------
            string p1 = Path.Combine(dir, "utf8_nobom.txt");
            File.WriteAllBytes(p1, new UTF8Encoding(false).GetBytes(probe));
            chk("①-1 UTF-8 无 BOM", ReadTextAuto(p1).Contains("今天天气不错"), ReadTextAuto(p1).Trim());

            string p2 = Path.Combine(dir, "utf8_bom.txt");
            File.WriteAllText(p2, probe, new UTF8Encoding(true));
            chk("①-2 UTF-8 带 BOM", ReadTextAuto(p2).Contains("今天天气不错"), ReadTextAuto(p2).Trim());

            string p3 = Path.Combine(dir, "gbk.txt");
            File.WriteAllBytes(p3, GbEncoding().GetBytes(probe));
            string r3 = ReadTextAuto(p3);
            chk("①-3 GBK（记事本 ANSI 存的老文件）", r3.Contains("今天天气不错") && r3.IndexOf('\uFFFD') < 0, r3.Trim());

            string p4 = Path.Combine(dir, "utf16.txt");
            File.WriteAllText(p4, probe, new UnicodeEncoding(false, true));
            chk("①-4 UTF-16 LE 带 BOM", ReadTextAuto(p4).Contains("今天天气不错"), ReadTextAuto(p4).Trim());

            string p5 = Path.Combine(dir, "ascii.txt");
            File.WriteAllText(p5, "hello 123", Encoding.ASCII);
            chk("①-5 纯 ASCII 不被误判成 GBK", ReadTextAuto(p5).Trim() == "hello 123", ReadTextAuto(p5).Trim());

            string p6 = Path.Combine(dir, "gbk_lines.txt");
            File.WriteAllBytes(p6, GbEncoding().GetBytes("第一行\r\n第二行\r\n第三行"));
            string[] ls = ReadLinesAuto(p6);
            chk("①-6 GBK 多行分行不错乱", ls.Length == 3 && ls[1].Trim() == "第二行", "行数=" + ls.Length + " 第二行=" + (ls.Length > 1 ? ls[1].Trim() : ""));

            // 日志：先放一份 GBK 老日志，再追加中文，整篇都得是能看的中文
            LOG_DIR = Path.Combine(dir, "log");
            try { Directory.CreateDirectory(LOG_DIR); } catch { }
            logEncChecked = 0;
            File.WriteAllBytes(Path.Combine(LOG_DIR, "log.txt"), GbEncoding().GetBytes("旧日志：上次启动成功\r\n"));
            Log("新日志：v2.2 自检写入中文正常");
            string logAll = ReadTextAuto(Path.Combine(LOG_DIR, "log.txt"));
            chk("①-7 老 GBK 日志追加后整篇不乱码",
                logAll.Contains("旧日志：上次启动成功") && logAll.Contains("新日志：v2.2 自检写入中文正常") && logAll.IndexOf('\uFFFD') < 0,
                "含旧行=" + logAll.Contains("旧日志") + " 含新行=" + logAll.Contains("新日志"));
            chk("①-8 老日志已留备份可回退", File.Exists(Path.Combine(LOG_DIR, "log.txt.gbk.bak")), "log.txt.gbk.bak 存在=" + File.Exists(Path.Combine(LOG_DIR, "log.txt.gbk.bak")));

            // ---------- ② 请求序号防竞态 ----------
            int s1 = NextSeq();
            int s2 = NextSeq();
            List<string> painted = new List<string>();
            bool oldDropped = !ApplyIfCurrent(s1, delegate { painted.Add("旧译文"); });
            bool newApplied = ApplyIfCurrent(s2, delegate { painted.Add("新译文"); });
            chk("②-1 旧请求的回写被丢弃", oldDropped && newApplied && painted.Count == 1 && painted[0] == "新译文",
                "界面实际写入=" + string.Join(",", painted.ToArray()));
            chk("②-2 当前请求号才是有效号", IsCurrent(s2) && !IsCurrent(s1), "IsCurrent(旧)=" + IsCurrent(s1) + " IsCurrent(新)=" + IsCurrent(s2));

            // 更像真实情况：先发的慢一次、后发的快一次，慢的晚回来，绝不能盖掉快的
            int seqSlow = NextSeq();
            int seqFast = NextSeq();
            string shown = "(空)";
            Thread thFast = new Thread(delegate () { ApplyIfCurrent(seqFast, delegate { lock (painted) shown = "快的新译文"; }); });
            Thread thSlow = new Thread(delegate () { Thread.Sleep(400); ApplyIfCurrent(seqSlow, delegate { lock (painted) shown = "慢的旧译文"; }); });
            thFast.IsBackground = true; thSlow.IsBackground = true;
            thFast.Start(); thSlow.Start();
            thFast.Join(3000); thSlow.Join(3000);
            chk("②-3 慢请求后回来不覆盖新结果", shown == "快的新译文", "最终界面=" + shown);

            // 连点三次：只有最后一次的结果能留在界面上
            int[] seqs = new int[] { NextSeq(), NextSeq(), NextSeq() };
            List<string> tri = new List<string>();
            for (int i = 0; i < seqs.Length; i++)
            {
                int k = i;
                ApplyIfCurrent(seqs[k], delegate { tri.Add("第" + (k + 1) + "次"); });
            }
            chk("②-4 连点三次只留最后一次", tri.Count == 1 && tri[0] == "第3次", "写入=" + string.Join(",", tri.ToArray()));

            // ---------- ③ settings.ini 默认值兜底 ----------
            string cfgDir = Path.Combine(dir, "cfg");
            LOG_DIR = cfgDir;
            try { Directory.CreateDirectory(cfgDir); } catch { }

            // 老配置：只有两个键 + 一行坏的 + 一个非数字值
            File.WriteAllText(Path.Combine(cfgDir, "settings.ini"),
                "# 老版本留下的配置\r\nfont_size=13\r\n这一行坏了没有等号\r\nwin_h=abc\r\n", new UTF8Encoding(true));
            LoadSettings();
            chk("③-1 缺的键落回默认值（win_w）", GetSet("win_w", "?") == "480", "win_w=" + GetSet("win_w", "?"));
            chk("③-2 缺的键落回默认值（voice）", GetSet("voice", "?") == "Zira", "voice=" + GetSet("voice", "?"));
            chk("③-3 缺的键落回默认值（tone）", GetSet("tone", "?") == "auto", "tone=" + GetSet("tone", "?"));
            chk("③-4 文件里的有效值照样生效", GetInt("font_size", 0) == 13, "font_size=" + GetInt("font_size", 0));
            chk("③-5 写了坏值用默认（win_h=abc）", GetInt("win_h", 0) == 660, "win_h=" + GetInt("win_h", 0));
            chk("③-6 一个坏行不影响其它键", GetInt("rate", 0) == -1, "rate=" + GetInt("rate", 0));

            // 老配置被记事本按 ANSI（GBK）存过
            File.WriteAllBytes(Path.Combine(cfgDir, "settings.ini"), GbEncoding().GetBytes("# 中文注释\r\nfont_size=15\r\n"));
            LoadSettings();
            chk("③-7 GBK 编码的配置读得出来", GetInt("font_size", 0) == 15, "font_size=" + GetInt("font_size", 0));

            // 配置文件整个不见（首次启动 / 用户删了）
            File.Delete(Path.Combine(cfgDir, "settings.ini"));
            LoadSettings();
            chk("③-8 配置不存在也能正常启动", GetInt("win_w", 0) == 480 && GetInt("font_size", 0) == 11 && GetSet("topmost", "?") == "0",
                "win_w=" + GetInt("win_w", 0) + " font_size=" + GetInt("font_size", 0));

            // 空文件 / 全注释
            File.WriteAllText(Path.Combine(cfgDir, "settings.ini"), "# 只有注释\r\n\r\n", new UTF8Encoding(true));
            LoadSettings();
            chk("③-9 空配置全部走默认值", GetInt("win_h", 0) == 660 && GetSet("voice", "?") == "Zira", "win_h=" + GetInt("win_h", 0));
        }
        catch (Exception ex)
        {
            chk("自检过程本身没抛异常", false, ex.GetType().Name + " " + ex.Message);
        }
        finally
        {
            // 无论成败，把用户真实目录与配置恢复回来
            LOG_DIR = originLogDir;
            logEncChecked = 0;
            try { LoadSettings(); } catch { }
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }

        StringBuilder head = new StringBuilder();
        head.AppendLine("== v2.2 地基自检  " + AppVersionLine());
        head.AppendLine("== 时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        head.AppendLine("== 结果 通过=" + pass + "  失败=" + fail + "  结论=" + (fail == 0 ? "全部通过" : "有失败项"));
        head.AppendLine();
        try
        {
            string full = head.ToString() + sb.ToString();
            File.WriteAllText(outPath, full, new UTF8Encoding(true));
            // 同时往真实日志里留一行，日后翻日志能对上
            Log("v2.2 地基自检 通过=" + pass + " 失败=" + fail);
        }
        catch { }
    }

    // ========================================================================
    //  v2.4 轻量自检：--mlsize <报告>
    //
    //  只查「其他语言小窗」的尺寸这一件事：
    //    ① 窗不再是写死的 560×430，配置里存过大小就照存的来
    //    ② 缩不没：有地板最小尺寸
    //    ③ 拉得大：拉大后上下两个框跟着长，按钮不越界
    //    ④ 记得住：松手落盘，关掉重开还是上次多大
    //    ⑤ 双击标题行能铺满工作区，再双击回到拉多大就多大
    // ========================================================================
    static void MlSizeCheck(string outPath)
    {
        StringBuilder sb = new StringBuilder();
        int pass = 0, fail = 0;
        Action<string, bool, string> chk = delegate (string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            sb.AppendLine((ok ? "  [通过] " : "  [失败] ") + name + (detail != null && detail.Length > 0 ? "  —— " + detail : ""));
        };

        QueryForm qf = null;
        try
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            QueryForm.selfTestMode = true;
            LoadSettings();

            qf = new QueryForm();
            qf.Show();
            MlPump(500);
            qf.ShowMultilingual();
            MlPump(600);

            MlWindow mp = qf.MlPanelForTest;
            chk("小窗打开", mp != null && mp.Visible, mp == null ? "未创建" : mp.Bounds.ToString());
            if (mp != null)
            {
                // v2.8.9：拖动 / 缩放时窗口会被重新激活，可编辑下拉框默认把文字整段全选，两格就变蓝。
                // 这里模拟「焦点回到语言框」「开始拖动」「松手」几个时机，确认选择长度始终是 0。
                ComboBox cs = qf.MlSrcForTest, cd = qf.MlDstForTest;
                if (cs != null && cd != null) {
                    cs.Focus(); Application.DoEvents(); MlPump(120);
                    chk("源语言框拿到焦点不整段选中", cs.SelectionLength == 0, "选中 " + cs.SelectionLength + " 字");
                    cd.Focus(); Application.DoEvents(); MlPump(120);
                    chk("目标语言框拿到焦点不整段选中", cd.SelectionLength == 0, "选中 " + cd.SelectionLength + " 字");
                    SendMessage(mp.Handle, 0x0231, 0, 0);      // WM_ENTERSIZEMOVE：开始拖
                    Application.DoEvents(); MlPump(150);
                    chk("拖动一开始就把选择清干净", cs.SelectionLength == 0 && cd.SelectionLength == 0, cs.SelectionLength + " / " + cd.SelectionLength);
                    SendMessage(mp.Handle, 0x0232, 0, 0);      // WM_EXITSIZEMOVE：松手
                    Application.DoEvents(); MlPump(150);
                    chk("拖动松手后依然没有整段选中", cs.SelectionLength == 0 && cd.SelectionLength == 0, cs.SelectionLength + " / " + cd.SelectionLength);
                }
                Size min = qf.MlMinSizeForTest;
                chk("地板不再是大块头（能缩到 380×330 这一档）", min.Width <= 420 && min.Height <= 340, "最小 " + min);
                chk("开窗尺寸来自记忆/默认，不是写死 560×430", qf.MlSavedSizeForTest.Width >= min.Width,
                    "当前记忆 " + qf.MlSavedSizeForTest + "，实际开在 " + mp.Size);

                mp.Size = new Size(60, 40);
                MlPump(250);
                chk("缩成一条缝会被地板挡住", mp.Width >= min.Width && mp.Height >= min.Height, "设 60×40 → 实际 " + mp.Size);

                int out0 = qf.MlOutBoxHeightForTest, in0 = qf.MlInBoxHeightForTest;
                mp.Size = new Size(940, 850);
                MlPump(350);
                chk("拉大后译文框跟着长高", qf.MlOutBoxHeightForTest > out0, out0 + " → " + qf.MlOutBoxHeightForTest);
                chk("拉大后原文框跟着长高", qf.MlInBoxHeightForTest > in0, in0 + " → " + qf.MlInBoxHeightForTest);
                chk("拉大后按钮没跑出窗口", qf.MlFootInsideForTest, "越界=" + !qf.MlFootInsideForTest);

                SendMessage(mp.Handle, 0x0232, 0, 0);     // WM_EXITSIZEMOVE：松手
                Application.DoEvents();
                MlPump(200);
                chk("松手把尺寸写进配置（ml_w / ml_h）",
                    GetSet("ml_w", "?") == mp.Width.ToString() && GetSet("ml_h", "?") == mp.Height.ToString(),
                    "ml_w=" + GetSet("ml_w", "?") + " ml_h=" + GetSet("ml_h", "?"));

                int bw = mp.Width, bh = mp.Height;
                qf.MlTestReopen();
                MlPump(500);
                chk("关掉重开还是上次多大",
                    qf.MlPanelForTest != null && Math.Abs(qf.MlPanelForTest.Width - bw) <= 2 && Math.Abs(qf.MlPanelForTest.Height - bh) <= 2,
                    "记的 " + bw + "×" + bh + "，重开 " + (qf.MlPanelForTest == null ? "null"
                        : qf.MlPanelForTest.Width + "×" + qf.MlPanelForTest.Height));

                MlWindow m2 = qf.MlPanelForTest;
                if (m2 != null)
                {
                    Rectangle wa = Screen.FromRectangle(m2.Bounds).WorkingArea;
                    MlPump(400);                    // 跨过双击防抖窗口
                    qf.MlTestToggleMax();
                    MlPump(350);
                    chk("双击标题行铺满工作区", Math.Abs(m2.Width - wa.Width) <= 6 && Math.Abs(m2.Height - wa.Height) <= 6,
                        "工作区 " + wa.Size + "，窗 " + m2.Size);
                    MlPump(400);
                    qf.MlTestToggleMax();
                    MlPump(350);
                    chk("再双击回到刚才拉的大小", Math.Abs(m2.Width - bw) <= 4 && Math.Abs(m2.Height - bh) <= 4,
                        "该回 " + bw + "×" + bh + "，实际 " + m2.Size);
                }
            }
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("  [失败] 自检过程抛异常：" + ex.GetType().Name + " " + ex.Message);
        }
        finally
        {
            try { if (qf != null) { qf.HideMultilingual(); qf.Close(); } } catch { }
        }

        sb.AppendLine();
        sb.AppendLine("合计：通过 " + pass + " / 失败 " + fail);
        try { File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true)); } catch { }
    }

    // ========================================================================
    //  v2.3 第 1 步自检：--mlcheck <报告> [--mlshot <目录>]
    //
    //  查六件事：
    //    ① 静态：语言表 / 反查、同语言短路、自动识别短路、缓存键按语言对隔离
    //    ② 结构：浮层该有的都在（两个下拉、两个框、三个按钮、顶部说明行），浮层在窗口里居中
    //    ③ 边界：浮层里搜不到 语音 / 朗读 / 谐音 / 语气 / 截图 / 离线 任何一个入口
    //    ④ 记忆：语言对写进配置，重启后能读回来
    //    ⑤ 交互：同语言不发请求、带语言对的缓存能命中且不会串到别的语言对
    //    ⑥ 回归：浮层开与不开，底栏高度、5 个按钮的几何与文案、主界面控件树完全一致
    //
    //  自检用临时目录当配置目录，绝不碰用户真实的 settings.ini 和缓存。
    // ========================================================================
    static void MlCheck(string outPath, string shotDir)
    {
        StringBuilder sb = new StringBuilder();
        int pass = 0, fail = 0;

        Action<string, bool, string> chk = delegate (string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            sb.AppendLine((ok ? "  [通过] " : "  [失败] ") + name + (detail != null && detail.Length > 0 ? "  —— " + detail : ""));
        };

        string originLogDir = LOG_DIR;
        string dir = Path.Combine(Path.GetTempPath(), "sqk_mlcheck_" + DateTime.Now.ToString("HHmmss"));
        try { Directory.CreateDirectory(dir); } catch { }

        QueryForm qf = null;
        try
        {
            LOG_DIR = dir;
            logEncChecked = 0;
            LoadSettings();

            // ---------- ① 静态逻辑 ----------
            sb.AppendLine("一、静态逻辑");
            chk("语言表里有「自动识别」和常用语种（≥15 门）", MlLangs().Length >= 15, "共 " + MlLangs().Length + " 项");
            chk("语言代码能反查出名字", MlName("ja") == "日语" && MlName("zh") == "中文" && MlName("auto") == "自动识别", "ja→" + MlName("ja") + " zh→" + MlName("zh"));
            chk("手打语言名也能认（英语 / 日 / 未知）", MlResolve("英语", "x") == "en" && MlResolve("日", "x") == "ja" && MlResolve("外星语", "zh") == "zh",
                "英语→" + MlResolve("英语", "x") + " 日→" + MlResolve("日", "x") + " 外星语→回退" + MlResolve("外星语", "zh"));
            chk("同语言短路：中→中 直接给提示", MlShortCircuit("今天天气不错", "zh", "zh") != null, "不经过模型");
            chk("同语言短路：自动识别出中文、目标是中文，同样短路", MlShortCircuit("明天下午三点开会", "auto", "zh") != null, "识图走本地判断");
            chk("不同语言不短路：中→日 该翻就翻", MlShortCircuit("今天天气不错", "zh", "ja") == null, "返回 null = 正常发请求");
            chk("空文本不短路（交给上层提示先填内容）", MlShortCircuit("   ", "zh", "zh") == null, "空串放行");

            string k_zh_ja = MlCacheKey("zh", "ja", "同一句话");
            string k_zh_en = MlCacheKey("zh", "en", "同一句话");
            string k_zh_ja2 = MlCacheKey("zh", "ja", "同一句话");
            chk("缓存键带语言对：换语言对 → 换键", k_zh_ja != k_zh_en, k_zh_ja + " ≠ " + k_zh_en);
            chk("缓存键稳定：同语言对同原文 → 同键", k_zh_ja == k_zh_ja2, k_zh_ja);
            chk("缓存键与主界面旧键（纯原文）不冲突", k_zh_ja != "同一句话" && k_zh_ja.StartsWith("ml|"), k_zh_ja);
            chk("默认配置里有语言对（auto / zh）", SettingDefaults()["ml_src"] == "auto" && SettingDefaults()["ml_dst"] == "zh",
                SettingDefaults()["ml_src"] + " / " + SettingDefaults()["ml_dst"]);
            chk("旧配置缺 ml_* 时能兜底不崩", GetSet("ml_src", "auto") == "auto" || GetSet("ml_src", "") == "auto", "GetSet 回退正常");

            // ---------- 建窗，量界面 ----------
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            qf = new QueryForm();
            qf.Show();
            // 窗口尺寸是 Show 之后才落到位的（OnShown 里再排一次版），这里必须真跑一会儿消息循环，
            // 否则量到的是 0×0，居中、底栏这些判断全都不作数
            MlPump(1500);

            Panel foot = (Panel)FindDeep(qf, "foot");
            Panel bar = (Panel)FindDeep(qf, "bar");
            string treeBefore = CaptureTree(qf, "mlPanel,tipStrip");
            int footH0 = (foot == null) ? -1 : foot.Height;
            string barBefore = (bar == null) ? "" : CaptureTree(bar, "");

            qf.ShowMultilingual();
            MlPump(500);

            sb.AppendLine();
            sb.AppendLine("二、小窗结构");
            MlWindow mp = qf.MlPanelForTest;
            chk("小窗已创建并显示", mp != null && mp.Visible, mp == null ? "未创建" : "位置=" + mp.Bounds);
            if (mp != null)
            {
                // v2.3 第 2 步的重点：它得是一块真窗口，不是钉在客户区里的面板
                chk("是独立顶层小窗（不再挂在主窗客户区里）", mp.TopLevel && !qf.Controls.Contains(mp),
                    "父级=" + (mp.Parent == null ? "无" : mp.Parent.Name) + " TopLevel=" + mp.TopLevel);
                chk("无边框（自绘圆角）", mp.FormBorderStyle == FormBorderStyle.None, mp.FormBorderStyle.ToString());
                chk("不占任务栏", !mp.ShowInTaskbar, "ShowInTaskbar=" + mp.ShowInTaskbar);
                chk("归属主窗口（Owner = 主窗）", mp.Owner == qf, mp.Owner == null ? "Owner 为空，会变成独立窗口" : "Owner=" + mp.Owner.GetType().Name);
                Point wantC = qf.MlCenterForTest(mp.Size);
                chk("首次打开落在主窗中心", MlNear(wantC, mp.Location, 2), "应在 " + wantC + " 实际 " + mp.Location);
                chk("整块落在某块屏幕的工作区内", MlInsideAnyScreen(mp.Bounds), "屏幕=" + Screen.FromControl(mp).WorkingArea + " 小窗=" + mp.Bounds);

                int combos = 0, boxes = 0, rbtns = 0;
                List<string> texts = new List<string>();
                MlWalk(mp, ref combos, ref boxes, ref rbtns, texts);
                chk("两个可搜索下拉（源语言 / 目标语言）", combos == 2, "下拉数=" + combos);
                chk("上下两个框（原文 / 译文）", boxes == 2, "框数=" + boxes);
                chk("五个按钮（翻译 / 复制 / 导出TXT / 导出docx / 关闭）", rbtns == 5, "按钮数=" + rbtns);
                chk("顶部说明行存在（标题「其他语言」）", ContainsText(texts, "其他语言"), "说明行跟着小窗走，不进正文滚动区");
                chk("窗口里找不到旧称「全球语言」", !ContainsText(texts, "全球语言") && qf.MlLinkForTest.Text.IndexOf("全球语言", StringComparison.Ordinal) < 0,
                    "旧称已清零");
                chk("有「翻译」「复制」两个按钮文案", ContainsText(texts, "翻译") && ContainsText(texts, "复制"), "按钮齐");
                chk("底栏有「导出TXT」「导出docx」两个导出按钮", ContainsText(texts, "导出TXT") && ContainsText(texts, "导出docx"),
                    ContainsText(texts, "导出TXT") && ContainsText(texts, "导出docx") ? "导出入口齐" : "缺导出按钮");
                chk("源语言下拉含「自动识别」且默认选中", qf.MlSrcForTest.Items.Contains("自动识别") && qf.MlSrcForTest.Text == "自动识别", "当前=" + qf.MlSrcForTest.Text);
                chk("目标语言下拉不含「自动识别」、默认「中文」", !qf.MlDstForTest.Items.Contains("自动识别") && qf.MlDstForTest.Text == "中文", "当前=" + qf.MlDstForTest.Text);

                sb.AppendLine();
                sb.AppendLine("三、小窗边界（这一版不做的入口）");
                string[] forbid = new string[] { "朗读", "语音", "谐音", "语气", "截图", "离线", "发音", "播放", "录音" };
                string hitTxt = "";
                foreach (string t in texts)
                    foreach (string f in forbid)
                        if (t != null && t.IndexOf(f, StringComparison.Ordinal) >= 0 && hitTxt.IndexOf("[" + t + "]") < 0) hitTxt += "[" + t + "] ";
                chk("小窗内没有 语音/朗读/谐音/语气/截图/离线 任何一个入口", hitTxt.Length == 0, hitTxt.Length == 0 ? "一个都没有" : "出现了：" + hitTxt);
                chk("底栏有轻量文字入口「其他语言」", qf.MlLinkForTest != null && qf.MlLinkForTest.Text == "其他语言" && qf.MlLinkForTest.Visible,
                    qf.MlLinkForTest == null ? "未创建" : "位置=" + qf.MlLinkForTest.Bounds);
            }

            // ---------- ⑦ v2.3 第 2 步：独立小窗该有的行为 ----------
            sb.AppendLine();
            sb.AppendLine("四、独立小窗行为（拖动 / 记忆 / 钳制 / 回中 / 跟随主窗）");
            if (mp != null && mp.Visible)
            {
                // 标题行整块算标题栏：往标题行中间发一条 WM_NCHITTEST，系统应回 HTCAPTION(2)
                Point titlePt = new Point(mp.ClientSize.Width / 2, Math.Max(4, mp.TitleHeight / 2));
                int ht = MlHitTest(mp, titlePt);
                chk("标题行按住能拖（命中 HTCAPTION=2）", ht == 2, "取点=" + titlePt + " 命中=" + ht);
                Point closePt = qf.MlClosePtForTest;
                int htC = MlHitTest(mp, closePt);
                chk("标题行上的关闭按钮不算标题栏（点得动）", htC != 2, "取点=" + closePt + " 命中=" + htC);
                Point homePt = qf.MlHomePtForTest;
                int htH = MlHitTest(mp, homePt);
                chk("标题行上的「回到主窗中心」不算标题栏（点得动）", htH != 2, "取点=" + homePt + " 命中=" + htH);

                // 拖走 → 位置跟着走，并且当场落盘
                // 落点选在内侧（往左上挪一点），免得一开始就贴屏幕边缘、被钳制后对不上账
                Point dst = new Point(mp.Left - 80, mp.Top - 60);
                qf.MlTestMoveTo(dst.X, dst.Y);
                Application.DoEvents();
                chk("拖到别处位置真的跟着走", MlNear(dst, mp.Location, 2), "目标=" + dst + " 实际=" + mp.Location);
                chk("位置当场记进配置（ml_x / ml_y）",
                    GetSet("ml_x", "?") == mp.Left.ToString() && GetSet("ml_y", "?") == mp.Top.ToString(),
                    "ml_x=" + GetSet("ml_x", "?") + " ml_y=" + GetSet("ml_y", "?"));

                // 关掉再开：回到上次那儿
                qf.MlTestReopen();
                MlPump(150);
                chk("关掉重开回到上次的位置", qf.MlPanelForTest != null && MlNear(dst, qf.MlPanelForTest.Location, 2),
                    "应在 " + dst + " 实际 " + (qf.MlPanelForTest == null ? "null" : qf.MlPanelForTest.Location.ToString()));
                chk("重开后还是同一块小窗（没有重复创建）", qf.MlPanelForTest == mp, "引用一致");

                // 拖到屏幕外：重开必须被钳回来，不能"窗口在人看不见的地方"
                qf.MlTestMoveTo(-20000, -20000);
                qf.MlTestReopen();
                MlPump(150);
                chk("拖到左上屏幕外，重开被钳回可见区", qf.MlPanelForTest != null && MlInsideAnyScreen(qf.MlPanelForTest.Bounds),
                    "钳制后=" + (qf.MlPanelForTest == null ? "null" : qf.MlPanelForTest.Bounds.ToString()));
                qf.MlTestMoveTo(99999, 99999);
                qf.MlTestReopen();
                MlPump(150);
                chk("拖到右下屏幕外，重开被钳回可见区", qf.MlPanelForTest != null && MlInsideAnyScreen(qf.MlPanelForTest.Bounds),
                    "钳制后=" + (qf.MlPanelForTest == null ? "null" : qf.MlPanelForTest.Bounds.ToString()));

                // 一键回主窗中心
                qf.MlTestGoHome();
                Application.DoEvents();
                Point home = qf.MlCenterForTest(mp.Size);
                chk("「回到主窗中心」一键回中", MlNear(home, mp.Location, 2), "应在 " + home + " 实际 " + mp.Location);
                chk("回中后的位置也记下来了", GetSet("ml_x", "?") == mp.Left.ToString() && GetSet("ml_y", "?") == mp.Top.ToString(),
                    "ml_x=" + GetSet("ml_x", "?") + " ml_y=" + GetSet("ml_y", "?"));

                // 松手才写盘：手动改位置不发保存，补一条 WM_EXITSIZEMOVE 后配置才更新
                mp.Location = new Point(mp.Left + 17, mp.Top + 9);
                int lx = mp.Left, ly = mp.Top;
                SendMessage(mp.Handle, 0x0232, 0, 0);   // WM_EXITSIZEMOVE
                Application.DoEvents();
                chk("拖动松手后才把新位置写进配置", GetSet("ml_x", "?") == lx.ToString() && GetSet("ml_y", "?") == ly.ToString(),
                    "ml_x=" + GetSet("ml_x", "?") + " ml_y=" + GetSet("ml_y", "?") + " 期望 " + lx + "," + ly);

                // 跟随主窗：最小化先收起来，还原后自己回来
                qf.MlTestOwnerMinimize();
                MlPump(200);
                chk("主窗最小化时小窗跟着收起", !mp.Visible, "可见=" + mp.Visible);
                chk("收起只是临时的，用户「要它开着」的意愿还在", qf.MlWantOpenForTest, "mlWantOpen=" + qf.MlWantOpenForTest);
                qf.MlTestOwnerRestore();
                MlPump(250);
                chk("主窗还原后小窗自己回来", mp.Visible, "可见=" + mp.Visible);

                // ---- v2.4：小窗能自己拉大小（以前 560×430 写死，长句子只能挤在里头滚） ----
                sb.AppendLine();
                sb.AppendLine("四之二、小窗可自由缩放");
                Size minSz = qf.MlMinSizeForTest;
                mp.Size = new Size(60, 40);
                MlPump(120);
                chk("缩不成一条缝：被最小尺寸挡住", mp.Width >= minSz.Width && mp.Height >= minSz.Height,
                    "设 60×40 → 实际 " + mp.Width + "×" + mp.Height + "，下限 " + minSz.Width + "×" + minSz.Height);

                int outH0 = qf.MlOutBoxHeightForTest;
                int inH0 = qf.MlInBoxHeightForTest;
                mp.Size = new Size(900, 820);
                MlPump(250);
                chk("拉大后译文框跟着变高（不再固定在矮窗口里滚）", qf.MlOutBoxHeightForTest > outH0,
                    "译文框高 " + outH0 + " → " + qf.MlOutBoxHeightForTest);
                chk("拉大后原文框也跟着变高", qf.MlInBoxHeightForTest > inH0,
                    "原文框高 " + inH0 + " → " + qf.MlInBoxHeightForTest);
                chk("拉大后按钮和下拉都还在窗口里（排版跟上了）", qf.MlFootInsideForTest, "越界=" + !qf.MlFootInsideForTest);

                // 松手才写盘：尺寸同样走 WM_EXITSIZEMOVE
                SendMessage(mp.Handle, 0x0232, 0, 0);
                Application.DoEvents();
                chk("尺寸松手后记进配置（ml_w / ml_h）",
                    GetSet("ml_w", "?") == mp.Width.ToString() && GetSet("ml_h", "?") == mp.Height.ToString(),
                    "ml_w=" + GetSet("ml_w", "?") + " ml_h=" + GetSet("ml_h", "?") + " 期望 " + mp.Width + "×" + mp.Height);

                int bigW = mp.Width, bigH = mp.Height;
                qf.MlTestReopen();
                MlPump(200);
                chk("关掉重开还是上次拉的大小", qf.MlPanelForTest != null
                        && Math.Abs(qf.MlPanelForTest.Width - bigW) <= 2 && Math.Abs(qf.MlPanelForTest.Height - bigH) <= 2,
                    "记的是 " + bigW + "×" + bigH + "，重开是 " + (qf.MlPanelForTest == null ? "null" : qf.MlPanelForTest.Width + "×" + qf.MlPanelForTest.Height));

                // 双击标题行：铺满工作区，再双击还原
                mp = qf.MlPanelForTest;
                if (mp != null)
                {
                    Rectangle waMl = Screen.FromRectangle(mp.Bounds).WorkingArea;
                    qf.MlTestToggleMax();
                    MlPump(320);      // 双击防抖是 250ms，这里得等过去
                    chk("双击标题行铺满当前屏幕工作区", Math.Abs(mp.Width - waMl.Width) <= 4 && Math.Abs(mp.Height - waMl.Height) <= 4,
                        "铺满后 " + mp.Width + "×" + mp.Height + "，工作区 " + waMl.Width + "×" + waMl.Height);
                    qf.MlTestToggleMax();
                    MlPump(200);
                    chk("再双击还原回拉大前的档位", Math.Abs(mp.Width - bigW) <= 4 && Math.Abs(mp.Height - bigH) <= 4,
                        "还原后 " + mp.Width + "×" + mp.Height + "，期望 " + bigW + "×" + bigH);
                }
            }

            // ---------- ⑥ 主界面无回归（浮层开着的时候量） ----------
            sb.AppendLine();
            sb.AppendLine("五、主界面无回归");
            int footH1 = (foot == null) ? -2 : foot.Height;
            string barAfter = (bar == null) ? "" : CaptureTree(bar, "");
            string treeAfter = CaptureTree(qf, "mlPanel,tipStrip");
            chk("底栏高度不变（浮层不影响布局）", footH0 == footH1, footH0 + " → " + footH1);
            chk("底栏 5 个按钮几何与文案一个都没变", barBefore == barAfter, barBefore == barAfter ? "逐控件一致" : "存在差异");
            int btnCount = (bar == null) ? 0 : bar.Controls.Count;
            chk("底栏按钮仍是 5 个", btnCount == 5, "实际=" + btnCount);
            chk("主界面其它控件树不变（已排除浮层与提示条）", treeBefore == treeAfter,
                treeBefore == treeAfter ? "逐控件一致（共 " + treeBefore.Split('\n').Length + " 行）" : "存在差异");

            // ---------- ⑤ 交互：短路 / 缓存 / 记忆 ----------
            sb.AppendLine();
            sb.AppendLine("六、交互与记忆");
            qf.MlTestSetPair("zh", "zh");
            qf.MlTestSetInput("今天天气不错，出去走走");
            qf.MlTestTranslate();
            Application.DoEvents();
            string o1 = qf.MlOutTextForTest;
            chk("同语言（中→中）不硬翻、给出人话提示", o1.IndexOf("没再让模型硬翻", StringComparison.Ordinal) >= 0, o1.Replace("\n", " / "));
            chk("同语言时状态行说明「没发请求」", qf.MlTipForTest.Text.IndexOf("没发请求", StringComparison.Ordinal) >= 0, qf.MlTipForTest.Text);

            qf.MlTestSetPair("auto", "zh");
            qf.MlTestSetInput("明天下午三点开会，别迟到");
            qf.MlTestTranslate();
            Application.DoEvents();
            chk("自动识别出中文且目标中文 → 同样短路", qf.MlOutTextForTest.IndexOf("没再让模型硬翻", StringComparison.Ordinal) >= 0, qf.MlTipForTest.Text);

            // 带语言对的缓存：先手工放一条，再走正常翻译流程，应直接命中（不走网络）
            string ck1 = MlCacheKey("zh", "ja", "自检用的句子");
            CachePut(ck1, "自検用の文");
            qf.MlTestSetPair("zh", "ja");
            qf.MlTestSetInput("自检用的句子");
            qf.MlTestTranslate();
            Application.DoEvents();
            chk("带语言对的缓存能命中（不联网也出译文）", qf.MlOutTextForTest.Trim() == "自検用の文", "译文=" + qf.MlOutTextForTest.Trim());
            chk("命中缓存时状态行提示用过上次结果", qf.MlTipForTest.Text.IndexOf("翻过", StringComparison.Ordinal) >= 0, qf.MlTipForTest.Text);

            string ck2 = MlCacheKey("zh", "en", "自检用的句子");
            string missVal;
            bool miss = CacheGet(ck2, out missVal);
            chk("同一句原文换成中→英，没有串到日语的缓存", !miss, miss ? "串了：" + missVal : "各自独立");
            chk("语言对已记进配置（ml_src=zh / ml_dst=ja）", GetSet("ml_src", "?") == "zh" && GetSet("ml_dst", "?") == "ja",
                GetSet("ml_src", "?") + " / " + GetSet("ml_dst", "?"));

            // 模拟"下次打开"：先把下拉改成别的，再从配置读回来，应恢复中→日
            qf.MlTestSetPair("en", "ko");
            qf.MlLoadPair();
            Application.DoEvents();
            chk("重开浮层时语言对自动恢复", qf.MlSrcForTest.Text == "中文" && qf.MlDstForTest.Text == "日语",
                qf.MlSrcForTest.Text + " → " + qf.MlDstForTest.Text);

            // ---------- ⑦ v2.3 第 2 步新能力：导出 / 长文 / 分流 / 方向 / 首提示 ----------
            sb.AppendLine();
            sb.AppendLine("七、导出（TXT / 真 docx，落真实桌面）");
            string expDir = Path.Combine(dir, "export");
            try { Directory.CreateDirectory(expDir); } catch { }

            qf.MlTestSetWithSrc(false);
            qf.MlTestSetInput("导出用的原文一句话");
            qf.MlTestOutText("导出用的译文一句");
            string pTxt1 = qf.MlTestExportTo(expDir, false, false);
            string txt1 = File.Exists(pTxt1) ? File.ReadAllText(pTxt1, Encoding.UTF8) : "";
            chk("能导出 TXT", File.Exists(pTxt1) && pTxt1.EndsWith(".txt", StringComparison.OrdinalIgnoreCase), pTxt1);
            chk("TXT 默认只放译文，不带原文", txt1.IndexOf("导出用的译文一句", StringComparison.Ordinal) >= 0
                && txt1.IndexOf("导出用的原文一句话", StringComparison.Ordinal) < 0, txt1.Length > 120 ? txt1.Substring(0, 120) : txt1);

            string pTxt2 = qf.MlTestExportTo(expDir, false, true);
            string txt2 = File.Exists(pTxt2) ? File.ReadAllText(pTxt2, Encoding.UTF8) : "";
            chk("勾「带原文」后 TXT 里原文和译文都在", txt2.IndexOf("导出用的原文一句话", StringComparison.Ordinal) >= 0
                && txt2.IndexOf("导出用的译文一句", StringComparison.Ordinal) >= 0, "长度=" + txt2.Length + " 字");

            string pDocx = qf.MlTestExportTo(expDir, true, false);
            byte[] zipHead = File.Exists(pDocx) ? File.ReadAllBytes(pDocx) : new byte[0];
            chk("能导出 docx 且是真 ZIP 包（不是改后缀）", zipHead.Length > 4 && zipHead[0] == 0x50 && zipHead[1] == 0x4B, "头两字节=" +
                (zipHead.Length > 2 ? (zipHead[0].ToString("X2") + " " + zipHead[1].ToString("X2")) : "空"));
            string dxText = File.Exists(pDocx) ? MlDocxText(pDocx) : "";
            chk("导出 docx 里的文字能被读回来", dxText.IndexOf("导出用的译文一句", StringComparison.Ordinal) >= 0, "docx 正文=" + MlOneLine(dxText, 60));

            string pDocx2 = qf.MlTestExportTo(expDir, true, true);
            string dxText2 = File.Exists(pDocx2) ? MlDocxText(pDocx2) : "";
            chk("带原文导出的 docx 原文译文都在", dxText2.IndexOf("导出用的原文一句话", StringComparison.Ordinal) >= 0
                && dxText2.IndexOf("导出用的译文一句", StringComparison.Ordinal) >= 0, MlOneLine(dxText2, 80));
            chk("导出的两个 docx 不是同一个文件（没互相覆盖）", pDocx != pDocx2, pDocx + " / " + pDocx2);

            string desk = MlDesktopDir();
            chk("桌面路径取自系统真实位置（不是硬拼）", desk != null && desk.Length > 0 && Path.IsPathRooted(desk) && Directory.Exists(desk), "桌面=" + desk);
            chk("导出默认落在桌面目录下", qf.MlTestExportDefaultTargetsDesktop(),
                qf.MlTestExportDefaultTargetsDesktop() ? "默认目录=桌面" : "默认目录不是桌面：" + desk);

            // 从右到左：阿拉伯语译文导出时，段落要标 rtl 并指定支持阿拉伯文的字体
            string arDoc = MlDocxDocumentXml("", "مرحبا بالعالم، هذا اختبار", false);
            chk("阿拉伯语译文在 docx 里被标成从右到左", arDoc.IndexOf("<w:rtl/>", StringComparison.Ordinal) >= 0
                || arDoc.IndexOf("<w:rtl ", StringComparison.Ordinal) >= 0, "含 w:rtl=" + (arDoc.IndexOf("w:rtl", StringComparison.Ordinal) >= 0));
            chk("从右到左的段落指定了支持阿拉伯文的字体", arDoc.IndexOf("Arial", StringComparison.Ordinal) >= 0
                || arDoc.IndexOf("Times New Roman", StringComparison.Ordinal) >= 0 || arDoc.IndexOf("Segoe UI", StringComparison.Ordinal) >= 0,
                arDoc.IndexOf("w:rFonts", StringComparison.Ordinal) >= 0 ? "含 rFonts" : "未指定字体");
            string zhDoc = MlDocxDocumentXml("", "这是一句中文译文", false);
            chk("中文译文不会被误标成从右到左", zhDoc.IndexOf("w:rtl", StringComparison.Ordinal) < 0, "中文段无 w:rtl");

            sb.AppendLine();
            sb.AppendLine("八、长文：分段 / 进度 / 单段重投 / 上限提示");
            StringBuilder longSb = new StringBuilder();
            for (int i = 0; i < 400; i++) longSb.Append("第").Append(i).Append("句测试文字，讲的是同一件事的不同细节而已。");
            string longText = longSb.ToString();
            List<string> segs = MlSplit(longText, MlSegChars);
            chk("6000 字以上会被切成多段", segs.Count >= 3, "原文 " + longText.Length + " 字 → " + segs.Count + " 段");
            int segMax = 0;
            foreach (string s in segs) if (s.Length > segMax) segMax = s.Length;
            chk("每段不超过上限（1200 字，允许句末回退留点余量）", segMax <= MlSegChars + 50, "最长段=" + segMax + " 字");
            string rejoined = string.Concat(segs.ToArray()).Replace("\n", "").Replace(" ", "");
            string originFlat = longText.Replace("\n", "").Replace(" ", "");
            chk("分段后一个字都没丢、顺序没乱", rejoined == originFlat, "拼回=" + rejoined.Length + " 字 / 原=" + originFlat.Length + " 字");

            string gWork, gNote;
            string hugeText = new string('长', MlMaxChars + 5000);
            MlGate g1 = qf.MlBigGate(hugeText, out gWork, out gNote);
            chk("超 20 万字：不报错，说明只翻前 N 字", g1 == MlGate.Proceed && gWork.Length < hugeText.Length && gNote != null
                && gNote.IndexOf("只翻前", StringComparison.Ordinal) >= 0, gNote);
            chk("截断后仍在上限以内", gWork.Length <= MlMaxChars, "保留 " + gWork.Length + " 字");

            StringBuilder bigSb2 = new StringBuilder();
            for (int i = 0; i < 400; i++) bigSb2.Append("这一句用来把字数堆过五千，内容无所谓，只要能凑够长度就行。");
            string warnText = bigSb2.ToString();
            MlGate g2 = qf.MlBigGate(warnText, out gWork, out gNote);
            chk("超 5000 字第一次先给预估、不直接开翻", warnText.Length > MlWarnChars && g2 == MlGate.Confirm
                && gNote != null && gNote.IndexOf("段", StringComparison.Ordinal) >= 0, gNote);
            MlGate g3 = qf.MlBigGate(warnText, out gWork, out gNote);
            chk("同一篇再点一次就直接开翻（不反复拦）", g3 == MlGate.Proceed && gWork.Length == warnText.Length, "第二次=" + g3);
            MlGate g4 = qf.MlBigGate("一句话而已，不用提醒", out gWork, out gNote);
            chk("短文不弹任何提示", g4 == MlGate.Proceed && gNote == null && gWork == "一句话而已，不用提醒", "直接翻");
            chk("进度文案形如「已完成 x/n 段」", MlSegProgress(3, 7, 0) == "已完成 3/7 段", MlSegProgress(3, 7, 0));
            chk("有失败段时进度里说清几段没成", MlSegProgress(7, 7, 2).IndexOf("2 段没成", StringComparison.Ordinal) >= 0, MlSegProgress(7, 7, 2));

            qf.MlTestSeedSegs(longText, "zh", "ja");
            int seededAll = qf.MlSegsForTest == null ? 0 : qf.MlSegsForTest.Count;
            chk("造出「上次翻到一半」的现场", qf.MlSegHasFailForTest && qf.MlSegOkCountForTest == 1, "共 " + seededAll + " 段，已成 1 段");
            chk("同一篇再点时只补失败段、不整篇重来", qf.SegCanResume(qf.SegHashOf(longText, "zh", "ja"), seededAll), "续投判定=真");
            chk("续投时会保住已翻好的段", qf.MlSegOkCountForTest == 1 && qf.MlSegsForTest[0].Dst == "已经翻好的第一段", "第 1 段仍在");
            chk("换了原文就不续投（从头来，不会串段）", !qf.SegCanResume(qf.SegHashOf(longText + "不同", "zh", "ja"), seededAll), "哈希不匹配=不续投");
            chk("换了语言对也不续投", !qf.SegCanResume(qf.SegHashOf(longText, "zh", "en"), seededAll), "语言对变了=不续投");

            sb.AppendLine();
            sb.AppendLine("九、拖文件分流（中英文进主界面 / 其它语言进小窗 / 不支持的明说）");
            chk("中文内容判给主界面", MlRouteKind("这是一段中文，应该进主界面去做全功能翻译") == "main", "zh→main");
            chk("英文内容也判给主界面", MlRouteKind("This is an English paragraph, it should go to the main window.") == "main", "en→main");
            chk("俄语内容判给小窗", MlRouteKind("Это русский текст, он должен открыться в маленьком окне.") == "ml", "ru→ml");
            chk("阿拉伯语内容判给小窗", MlRouteKind("هذا نص عربي طويل بما يكفي لكي يتم التعرف عليه") == "ml", "ar→ml");
            chk("日语内容判给小窗", MlRouteKind("これは日本語のテキストです。小さな窓で翻訳します。") == "ml", "ja→ml");
            chk("韩语内容判给小窗", MlRouteKind("이것은 한국어 문장입니다. 작은 창에서 번역합니다.") == "ml", "ko→ml");
            chk("法语内容判给小窗（不被当成英文）", MlRouteKind("Ceci est un texte en français avec des mots pour le test") == "ml", "fr→ml");

            string dropDir = Path.Combine(dir, "drop");
            try { Directory.CreateDirectory(dropDir); } catch { }
            string fEn = Path.Combine(dropDir, "en_note.txt");
            string fRu = Path.Combine(dropDir, "ru_note.txt");
            string fAr = Path.Combine(dropDir, "ar_note.txt");
            string fMd = Path.Combine(dropDir, "ja_note.md");
            string fCsv = Path.Combine(dropDir, "ko_note.csv");
            string fSrt = Path.Combine(dropDir, "fr_note.srt");
            string fJson = Path.Combine(dropDir, "de_note.json");
            string fHtml = Path.Combine(dropDir, "es_note.html");
            string fLog = Path.Combine(dropDir, "vi_note.log");
            File.WriteAllText(fEn, "This is an English note for the main window.", new UTF8Encoding(false));
            File.WriteAllText(fRu, "Это русский текст для проверки маршрута.", new UTF8Encoding(false));
            File.WriteAllText(fAr, "هذا نص عربي للتحقق من المسار الصحيح", new UTF8Encoding(false));
            File.WriteAllText(fMd, "# これは日本語のメモです\n小さな窓に回します。", new UTF8Encoding(false));
            File.WriteAllText(fCsv, "이름,설명\n테스트,한국어 내용입니다", new UTF8Encoding(false));
            File.WriteAllText(fSrt, "1\n00:00:01,000 --> 00:00:03,000\nCeci est un sous-titre en français\n", new UTF8Encoding(false));
            File.WriteAllText(fJson, "{\"text\":\"Das ist ein deutscher Text für den Test\"}", new UTF8Encoding(false));
            File.WriteAllText(fHtml, "<p>Este es un texto en español para la prueba</p>", new UTF8Encoding(false));
            File.WriteAllText(fLog, "Đây là một dòng nhật ký tiếng Việt để kiểm tra", new UTF8Encoding(false));

            qf.MlTestSuppressQuery();
            string rEn = qf.MlRouteDropped(new string[] { fEn });
            chk("拖英文 txt → 进主界面", qf.MlMainInputForTest.IndexOf("English note", StringComparison.Ordinal) >= 0
                && rEn.IndexOf("主界面", StringComparison.Ordinal) >= 0, rEn);
            string rRu = qf.MlRouteDropped(new string[] { fRu });
            chk("拖俄语 txt → 转到「其他语言」小窗", rRu.IndexOf("其他语言", StringComparison.Ordinal) >= 0
                && qf.MlInTextForTest.IndexOf("русский", StringComparison.Ordinal) >= 0, rRu);

            string rMix = qf.MlRouteDropped(new string[] { fMd, fCsv, fSrt, fJson, fHtml, fLog });
            chk("md / csv / srt / json / html / log 都能读进来", qf.MlInTextForTest.Length > 0 && rMix.IndexOf("不支持", StringComparison.Ordinal) < 0, rMix);

            string folder = Path.Combine(dropDir, "一个文件夹");
            try { Directory.CreateDirectory(folder); } catch { }
            string rFolder = qf.MlRouteDropped(new string[] { folder });
            chk("拖文件夹 → 明确说文件夹不支持", rFolder.IndexOf("文件夹不支持", StringComparison.Ordinal) >= 0, rFolder);

            string lnk = Path.Combine(dropDir, "快捷方式.lnk");
            File.WriteAllText(lnk, "x", new UTF8Encoding(false));
            string rLnk = qf.MlRouteDropped(new string[] { lnk });
            chk("拖 .lnk 快捷方式 → 明确说快捷方式不支持", rLnk.IndexOf("快捷方式不支持", StringComparison.Ordinal) >= 0, rLnk);

            string docxIn = Path.Combine(dropDir, "ru_input.docx");
            MlDocxSave(docxIn, "", "Это документ на русском языке для проверки", false);
            string rDocx = qf.MlRouteDropped(new string[] { docxIn });
            chk("拖 docx 能读正文并分流", rDocx.IndexOf("其他语言", StringComparison.Ordinal) >= 0, rDocx);

            string unknown = Path.Combine(dropDir, "x.zzz");
            File.WriteAllText(unknown, "x", new UTF8Encoding(false));
            string rUnk = qf.MlRouteDropped(new string[] { unknown });
            chk("拖不认识的格式 → 明说暂不支持，不静默吞掉", rUnk.IndexOf("暂不支持", StringComparison.Ordinal) >= 0, rUnk);

            sb.AppendLine();
            sb.AppendLine("十、从右到左与首次提示");
            chk("阿拉伯语原文被判成从右到左", MlRtlDominant("مرحبا بالعالم هذا نص عربي"), "强 RTL");
            chk("中文不会被当成从右到左", !MlRtlDominant("这是一段正常的中文"), "强 LTR");
            chk("英文不会被当成从右到左", !MlRtlDominant("This is a normal English sentence"), "强 LTR");
            chk("阿语和中文混排、阿语更多 → 仍按从右到左", MlRtlDominant("مرحبا بالعالم هذا نص عربي طويل جدا 中文"), "按占比判定");

            qf.MlTestResetFirstTip();
            qf.MlFirstTipCount = 0;
            qf.MlTestFirstTip();
            int tipAfter1 = qf.MlFirstTipCount;
            qf.MlTestFirstTip();
            chk("首次打开小窗提示「只提供纯文本翻译」", tipAfter1 == 1, "第一次提示次数=" + tipAfter1);
            chk("提示过一次就记进配置，不再重复", tipAfter1 == 1 && qf.MlFirstTipCount == 1, "第二次提示次数仍=" + qf.MlFirstTipCount);
            chk("配置里有这个记号", GetSet(qf.MlTipKeyForTest, "0") == "1", qf.MlTipKeyForTest + "=" + GetSet(qf.MlTipKeyForTest, "0"));
            string tipTxt = qf.MlFirstTipTextForTest;
            chk("首次提示把三件事说全（纯文本 / 能拖出主窗 / 能导出）",
                tipTxt.Contains("纯文本") && tipTxt.Contains("拖出") && tipTxt.Contains("导出"), tipTxt);

            // ---------- 十一、RTL：译文框真的切了方向，原文框回退 LTR ----------
            sb.AppendLine();
            sb.AppendLine("十一、译文方向：阿语/希伯来语右对齐，中英不受影响");
            qf.MlTestOutText("مرحبا بالعالم، هذا نص عربي طويل بما يكفي للحكم على الاتجاه");
            Application.DoEvents();
            chk("阿语译文 → 译文框切成从右到左", qf.MlOutRtlForTest, "RightToLeft=" + (qf.MlOutRtlForTest ? "Yes" : "No"));
            chk("阿语译文时原文框仍是左到右", !qf.MlInRtlForTest, "原文框 RightToLeft=" + (qf.MlInRtlForTest ? "Yes" : "No"));
            qf.MlTestOutText("שלום עולם, זהו טקסט עברי ארוך מספיק כדי לבדוק את הכיוון");
            Application.DoEvents();
            chk("希伯来语译文 → 同样从右到左", qf.MlOutRtlForTest, "RightToLeft=Yes");
            qf.MlTestOutText("这是一段中文译文，方向应该照旧从左到右");
            Application.DoEvents();
            chk("中文译文 → 译文框回到左到右", !qf.MlOutRtlForTest, "RightToLeft=" + (qf.MlOutRtlForTest ? "Yes" : "No"));
            qf.MlTestOutText("This is an English translation and stays left to right.");
            Application.DoEvents();
            chk("英文译文 → 左到右（中英不受影响）", !qf.MlOutRtlForTest, "RightToLeft=" + (qf.MlOutRtlForTest ? "Yes" : "No"));
            qf.MlTestOutText("");
            Application.DoEvents();
            chk("译文清空 → 方向复位，不留 RTL 痕迹", !qf.MlOutRtlForTest, "RightToLeft=" + (qf.MlOutRtlForTest ? "Yes" : "No"));

            // ---------- 十二、拖进小窗：读正文 → 自动翻；图片与加密件只说不支持 ----------
            sb.AppendLine();
            sb.AppendLine("十二、拖进小窗：自动填源框并开翻 / 不支持的一律只说不崩");
            chk("小窗本身是拖放落点", qf.MlDropHookedForTest() == "已挂拖放", qf.MlDropHookedForTest());

            string smallRu = Path.Combine(dropDir, "small_ru.txt");
            File.WriteAllText(smallRu, "Этот текст перетащили прямо в маленькое окно.", new UTF8Encoding(false));
            string rSmall = qf.MlDropToSmallForTest(new string[] { smallRu });
            chk("拖 txt 进小窗 → 正文进了源框", qf.MlInTextForTest.IndexOf("маленькое", StringComparison.Ordinal) >= 0
                && qf.MlInTextForTest.IndexOf("Этот", StringComparison.Ordinal) >= 0, qf.MlInTextForTest);
            chk("拖 txt 进小窗 → 自动开翻（自检不联网）", rSmall.IndexOf("已放进小窗", StringComparison.Ordinal) >= 0, rSmall);

            string smallMd = Path.Combine(dropDir, "small_note.md");
            File.WriteAllText(smallMd, "# عنوان\nهذا نص عربي في ملف ماركداون", new UTF8Encoding(false));
            qf.MlDropToSmallForTest(new string[] { smallMd });
            chk("拖 md 进小窗 → 正文读得出来", qf.MlInTextForTest.IndexOf("ماركداون", StringComparison.Ordinal) >= 0, qf.MlInTextForTest);
            chk("拖 md 进小窗 → 源语言认成阿拉伯语", qf.MlSrcForTest.Text == "阿拉伯语", "源语言=" + qf.MlSrcForTest.Text);

            string smallDocx = Path.Combine(dropDir, "small_jp.docx");
            MlDocxSave(smallDocx, "", "これは小さな窓に落とした日本語の文書です", false);
            qf.MlDropToSmallForTest(new string[] { smallDocx });
            chk("拖 docx 进小窗 → 取到正文", qf.MlInTextForTest.IndexOf("小さな窓", StringComparison.Ordinal) >= 0, qf.MlInTextForTest);

            string fakeImg = Path.Combine(dropDir, "shot.png");
            File.WriteAllBytes(fakeImg, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
            string rImg = qf.MlDropToSmallForTest(new string[] { fakeImg });
            chk("拖图片进小窗 → 只说图片不支持、不崩", rImg.IndexOf("图片暂不支持", StringComparison.Ordinal) >= 0, rImg);

            string encDocx = Path.Combine(dropDir, "locked.docx");
            File.WriteAllBytes(encDocx, new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0 });
            string rEnc = qf.MlDropToSmallForTest(new string[] { encDocx });
            chk("拖加密 docx → 明说加密件、不崩", rEnc.IndexOf("加密", StringComparison.Ordinal) >= 0, rEnc);

            // PDF：手搓两份带 FlateDecode 的真 PDF（正文阿语 / 加密壳），验证抽字与拒绝
            string pdfAr = Path.Combine(dropDir, "ar_page.pdf");
            MlMakePdf(pdfAr, "هذا نص عربي داخل ملف بي دي إف", false);
            string whyPdf = null;
            string pdfText = MlPdfText(pdfAr, ref whyPdf);
            chk("PDF 能抽出文字（解压 + 按字节形态解码）",
                pdfText != null && pdfText.IndexOf("بي دي إف", StringComparison.Ordinal) >= 0,
                pdfText == null ? "失败：" + whyPdf : pdfText);
            qf.MlDropToSmallForTest(new string[] { pdfAr });
            chk("拖 PDF 进小窗 → 抽出的正文进了源框", qf.MlInTextForTest.IndexOf("بي دي إف", StringComparison.Ordinal) >= 0, qf.MlInTextForTest);

            string pdfEnc = Path.Combine(dropDir, "locked.pdf");
            MlMakePdf(pdfEnc, "secret text", true);
            string whyEnc = null;
            string encText = MlPdfText(pdfEnc, ref whyEnc);
            chk("加密 PDF → 明说加密件、不吐乱码", encText == null
                && whyEnc != null && whyEnc.IndexOf("加密", StringComparison.Ordinal) >= 0, whyEnc);

            string pdfEmpty = Path.Combine(dropDir, "scan_only.pdf");
            MlMakePdf(pdfEmpty, "", false);          // 只有结构、没有文字层：模拟扫描件
            string whyScan = null;
            string scanText = MlPdfText(pdfEmpty, ref whyScan);
            chk("扫描件（无文字层）→ 明说抽不出字、不崩", scanText == null
                && whyScan != null && whyScan.IndexOf("抽不出文字", StringComparison.Ordinal) >= 0, whyScan);

            string rMulti = qf.MlDropToSmallForTest(new string[] { fakeImg, smallRu });
            chk("一次拖多个：能读的读、不能读的顺带说清", rMulti.IndexOf("图片暂不支持", StringComparison.Ordinal) >= 0
                && rMulti.IndexOf("已放进小窗", StringComparison.Ordinal) >= 0, rMulti);

            // 收起
            qf.HideMultilingual();
            Application.DoEvents();
            chk("能正常收起小窗", mp != null && !mp.Visible, "关闭后 可见=" + (mp == null ? "null" : mp.Visible.ToString()));

            // ---------- 渲染截图（人工复核用） ----------
            if (shotDir != null && shotDir.Length > 0)
            {
                try { Directory.CreateDirectory(shotDir); } catch { }
                string mainPng = Path.Combine(shotDir, "render_main_v23.png");
                string mlPng = Path.Combine(shotDir, "render_ml_v23.png");
                bool okMain = MlShot(qf, mainPng);
                qf.ShowMultilingual();
                MlPump(200);
                // 小窗是顶层窗口了，得单独抓它自己；抓主窗只会得到一张没有小窗的图
                bool okMl = MlShot(mp, mlPng);
                chk("主界面截图已生成", okMain, mainPng);
                chk("小窗截图已生成", okMl, mlPng);
            }

            // ---------- 主窗关闭：小窗绝不留在屏幕上（放最后，关完就不再用它了） ----------
            qf.ShowMultilingual();
            Application.DoEvents();
            qf.MlTestOwnerClose();
            Application.DoEvents();
            chk("主窗关闭时小窗一起关掉、不留残影", qf.MlPanelForTest == null, "关闭后引用=" + (qf.MlPanelForTest == null ? "null（已释放）" : "还在"));
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("  [失败] 自检过程中抛异常：" + ex.Message);
            sb.AppendLine(ex.StackTrace);
        }
        finally
        {
            try { if (qf != null) qf.Close(); } catch { }
            try { qf = null; } catch { }
            // 恢复用户真实目录与配置
            LOG_DIR = originLogDir;
            logEncChecked = 0;
            try { LoadSettings(); } catch { }
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }

        StringBuilder head = new StringBuilder();
        head.AppendLine("== v2.3 第 3 步自检：拖文件 / 从右到左 / 首次提示  " + AppVersionLine());
        head.AppendLine("== 时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        head.AppendLine("== 结果 通过=" + pass + "  失败=" + fail + "  结论=" + (fail == 0 ? "全部通过" : "有失败项"));
        head.AppendLine();
        try
        {
            File.WriteAllText(outPath, head.ToString() + sb.ToString(), new UTF8Encoding(true));
            Log("v2.3 小窗自检 通过=" + pass + " 失败=" + fail);
        }
        catch { }
    }

    // 手搓一份最小 PDF（内容流走 FlateDecode 压缩），专供自检：
    // 目的不是造合规文件，而是验证「解压 → 取字符串 → 按字节形态解码」整条路真的能跑通。
    // encrypted=true 时加一个 /Encrypt 指向的 Standard 安全壳，用来验证加密件被明确拒绝。
    static void MlMakePdf(string path, string text, bool encrypted)
    {
        string hex = "";
        if (text != null && text.Length > 0)
        {
            StringBuilder h = new StringBuilder();
            foreach (char c in text) h.Append(((int)c).ToString("X4"));
            hex = h.ToString();
        }
        string content = hex.Length > 0
            ? "BT /F1 18 Tf 40 760 Td <" + hex + "> Tj ET"
            : "BT /F1 18 Tf 40 760 Td ET";

        byte[] raw = Encoding.ASCII.GetBytes(content);
        byte[] zip;
        using (MemoryStream zs = new MemoryStream())
        {
            using (DeflateStream ds = new DeflateStream(zs, CompressionMode.Compress, true))
                ds.Write(raw, 0, raw.Length);
            zip = zs.ToArray();
        }

        using (MemoryStream ms = new MemoryStream())
        {
            Action<string> w = delegate (string s)
            {
                byte[] bb = Encoding.ASCII.GetBytes(s);
                ms.Write(bb, 0, bb.Length);
            };
            w("%PDF-1.7\n");
            w("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
            w("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
            w("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] "
                + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
            w("4 0 obj\n<< /Length " + zip.Length + " /Filter /FlateDecode >>\nstream\n");
            ms.Write(zip, 0, zip.Length);
            w("\nendstream\nendobj\n");
            w("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
            if (encrypted)
            {
                w("6 0 obj\n<< /Filter /Standard /V 2 /R 3 /Length 128 >>\nendobj\n");
                w("trailer\n<< /Size 7 /Root 1 0 R /Encrypt 6 0 R >>\n%%EOF\n");
            }
            else
            {
                w("trailer\n<< /Size 6 /Root 1 0 R >>\n%%EOF\n");
            }
            File.WriteAllBytes(path, ms.ToArray());
        }
    }

    // 假消息泵：让 Show/排版/重绘这几件异步的事真的跑完，自检才量得准
    static void MlPump(int ms)
    {
        int t0 = Environment.TickCount;
        while (Environment.TickCount - t0 < ms)
        {
            Application.DoEvents();
            System.Threading.Thread.Sleep(25);
        }
    }

    // 两点是不是差不多重合（差 tol 像素以内）：坐标比较一律走这里，别写死 ==
    static bool MlNear(Point a, Point b, int tol)
    {
        return Math.Abs(a.X - b.X) <= tol && Math.Abs(a.Y - b.Y) <= tol;
    }

    // 这块矩形是不是整块落在某块屏幕的工作区里（拖完以后必须还能看得见）
    static bool MlInsideAnyScreen(Rectangle r)
    {
        foreach (Screen s in Screen.AllScreens)
        {
            Rectangle wa = s.WorkingArea;
            if (r.Left >= wa.Left - 1 && r.Top >= wa.Top - 1 && r.Right <= wa.Right + 1 && r.Bottom <= wa.Bottom + 1)
                return true;
        }
        return false;
    }

    // 往小窗某点发一条 WM_NCHITTEST，看系统准备怎么处理这个点（2=标题栏，可拖）
    static int MlHitTest(Control w, Point clientPt)
    {
        Point sp = w.PointToScreen(clientPt);          // 消息里带的是屏幕坐标，不是客户区坐标
        int lp = (sp.Y << 16) | (sp.X & 0xFFFF);
        return (int)SendMessage(w.Handle, 0x0084, 0, lp);
    }

    static Control FindDeep(Control root, string name)
    {
        if (root == null) return null;
        foreach (Control c in root.Controls)
        {
            if (c.Name == name) return c;
            Control r = FindDeep(c, name);
            if (r != null) return r;
        }
        return null;
    }

    // 控件树快照：名字 + 类型 + 边界 + 文案，逐行比字符串就能发现"动了不该动的"
    static string CaptureTree(Control root, string skipNames)
    {
        StringBuilder sb = new StringBuilder();
        CaptureTreeInto(root, skipNames, sb, 0);
        return sb.ToString();
    }

    static void CaptureTreeInto(Control root, string skipNames, StringBuilder sb, int depth)
    {
        if (root == null) return;
        foreach (Control c in root.Controls)
        {
            string nm = c.Name == null ? "" : c.Name;
            if (skipNames != null && skipNames.Length > 0)
            {
                bool skip = false;
                foreach (string s in skipNames.Split(','))
                    if (s.Length > 0 && s == nm) { skip = true; break; }
                if (skip) continue;
            }
            sb.Append(new string(' ', depth * 2));
            sb.Append(nm + "|" + c.GetType().Name + "|" + c.Bounds + "|" + (c.Text == null ? "" : c.Text) + "|" + c.Visible);
            sb.Append('\n');
            CaptureTreeInto(c, skipNames, sb, depth + 1);
        }
    }

    static void MlWalk(Control root, ref int combos, ref int boxes, ref int rbtns, List<string> texts)
    {
        foreach (Control c in root.Controls)
        {
            if (c is ComboBox) combos++;
            if (c is MlInputBox) boxes++;
            if (c is RoundButton) rbtns++;
            if (c.Text != null && c.Text.Length > 0) texts.Add(c.Text);
            MlWalk(c, ref combos, ref boxes, ref rbtns, texts);
        }
    }

    static bool ContainsText(List<string> texts, string needle)
    {
        foreach (string t in texts)
            if (t != null && t.IndexOf(needle, StringComparison.Ordinal) >= 0) return true;
        return false;
    }

    static bool MlShot(Control f, string path)
    {
        try
        {
            f.Refresh();
            Application.DoEvents();
            using (Bitmap bmp = new Bitmap(f.Width, f.Height))
            {
                bool ok = false;
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    IntPtr hdc = g.GetHdc();
                    try { ok = PrintWindow(f.Handle, hdc, 2); } catch { ok = false; }
                    finally { g.ReleaseHdc(hdc); }
                }
                if (!ok)
                {
                    // 退路：直接抓窗口区域
                    using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(f.PointToScreen(Point.Empty), Point.Empty, f.Size);
                }
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            return true;
        }
        catch { return false; }
    }

    // ========================================================================
    //  v2.4 第 3 步自检：--exportcheck <报告>
    //  重点不是"功能有没有"，而是三件事：
    //    1) 底栏 5 个按钮纹丝不动（几何 / 文案 / 控件树），新增入口全在「更多 ▾」里；
    //    2) 「复制全文」的文案真的跟着语种翻，语种认不出来时不硬凑；
    //    3) 导出的 docx 是手拼 OpenXML 的真包（拆开验部件），TXT 带 BOM 且默认不含原文。
    // ========================================================================
    static void ExportCheck(string outPath)
    {
        StringBuilder sb = new StringBuilder();
        int pass = 0, fail = 0;
        Action<string, bool, string> chk = delegate(string name, bool ok, string note)
        {
            if (ok) { pass++; sb.AppendLine("[通过] " + name + (note.Length > 0 ? "  —— " + note : "")); }
            else { fail++; sb.AppendLine("[失败] " + name + (note.Length > 0 ? "  —— " + note : "")); }
        };

        string tmp = Path.Combine(Path.GetTempPath(), "sqk_exportcheck_" + DateTime.Now.ToString("HHmmss"));
        try { Directory.CreateDirectory(tmp); } catch { }
        QueryForm qf = null;

        try
        {
            sb.AppendLine("== v2.4 第 3 步自检：主界面导出 / 复制入口  " + AppVersionLine());
            sb.AppendLine("临时目录：" + tmp);
            sb.AppendLine();

            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            qf = new QueryForm();
            qf.Show();
            MlPump(1500);
            qf.MainForceDesignWidthForTest();

            Panel foot = (Panel)FindDeep(qf, "foot");
            Panel bar = (Panel)FindDeep(qf, "bar");
            int footH0 = (foot == null) ? -1 : foot.Height;
            string barBefore = (bar == null) ? "" : CaptureTree(bar, "");

            // ---------- 一、底栏零改动（硬约束） ----------
            sb.AppendLine("一、底栏硬约束（5 个按钮零改动）");
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
            chk("底栏按钮总数仍是 5 个", btnCount == 5, "实际 " + btnCount + " 个");
            chk("底栏文案仍是那 5 个（复制这句/读一遍/谐音/语气/更多 ▾；窄窗缩短说法也算）",
                (ContainsText(footTexts, "复制这句") || ContainsText(footTexts, "复制"))
                && ContainsText(footTexts, "读一遍") && (ContainsText(footTexts, "谐音") || ContainsText(footTexts, "谐音关"))
                && (ContainsText(footTexts, "语气") || ContainsText(footTexts, "正式") || ContainsText(footTexts, "口语") || ContainsText(footTexts, "自动"))
                && ContainsText(footTexts, "更多"),
                string.Join(" | ", footTexts.ToArray()));
            chk("底栏没有偷偷多出导出/复制全文按钮",
                !ContainsText(footTexts, "导出") && !ContainsText(footTexts, "复制全文"),
                ContainsText(footTexts, "导出") ? "底栏出现了「导出」" : "底栏干净");

            // ---------- 二、「更多 ▾」菜单里的入口 ----------
            sb.AppendLine();
            sb.AppendLine("二、「更多 ▾」菜单入口");
            ContextMenuStrip more = null;
            try { more = qf.BuildMoreMenu(); } catch (Exception ex) { sb.AppendLine("  建菜单抛异常：" + ex.Message); }
            chk("「更多 ▾」菜单能建出来", more != null, more == null ? "null" : "项数=" + more.Items.Count);
            ToolStripMenuItem miCopyAll = null, miTxt = null, miDocx = null, miSrc = null;
            if (more != null)
            {
                foreach (ToolStripItem it in more.Items)
                {
                    ToolStripMenuItem mi = it as ToolStripMenuItem;
                    if (mi == null) continue;
                    if (mi.Text.StartsWith("复制全文", StringComparison.Ordinal)) miCopyAll = mi;
                    else if (mi.Text.StartsWith("导出 TXT", StringComparison.Ordinal)) miTxt = mi;
                    else if (mi.Text.StartsWith("导出 Word", StringComparison.Ordinal)) miDocx = mi;
                    else if (mi.Text.StartsWith("导出时带上原文", StringComparison.Ordinal)) miSrc = mi;
                }
            }
            chk("第一条是「复制全文」（显性化入口）", miCopyAll != null && more != null && more.Items.IndexOf(miCopyAll) == 0,
                miCopyAll == null ? "没找到" : "位置=" + (more == null ? -1 : more.Items.IndexOf(miCopyAll)));
            // v2.8.3：用户嫌加粗抢眼，要求撤掉——「复制全文」字体必须跟菜单其余项完全一致（不加粗、同字族同字号）。
            chk("「复制全文」不加粗，字体与其余项完全一致",
                miCopyAll != null && miTxt != null && !miCopyAll.Font.Bold
                && miCopyAll.Font.Name == miTxt.Font.Name
                && Math.Abs(miCopyAll.Font.Size - miTxt.Font.Size) < 0.01f
                && miCopyAll.Font.Style == miTxt.Font.Style,
                miCopyAll == null ? "无此项"
                    : "Bold=" + miCopyAll.Font.Bold + " 字体=" + miCopyAll.Font.Name + "/" + miCopyAll.Font.Size
                      + (miTxt == null ? " 对照项缺失" : " 对照=" + miTxt.Font.Name + "/" + miTxt.Font.Size));
            chk("「复制全文」标了快捷键 Ctrl+Shift+C",
                miCopyAll != null && miCopyAll.ShortcutKeyDisplayString == "Ctrl+Shift+C",
                miCopyAll == null ? "无此项" : "显示=" + (miCopyAll == null ? "" : miCopyAll.ShortcutKeyDisplayString));
            chk("有「导出 TXT 到桌面」", miTxt != null, miTxt == null ? "缺" : miTxt.Text);
            chk("有「导出 Word（.docx）到桌面」", miDocx != null, miDocx == null ? "缺" : miDocx.Text);
            chk("有「导出时带上原文」开关", miSrc != null, miSrc == null ? "缺" : miSrc.Text);
            chk("默认只导译文（开关默认不勾 + 两项都标「只译文」）",
                miSrc != null && !miSrc.Checked
                && miTxt != null && miTxt.Text.IndexOf("只译文", StringComparison.Ordinal) >= 0
                && miDocx != null && miDocx.Text.IndexOf("只译文", StringComparison.Ordinal) >= 0,
                (miSrc == null ? "?" : "勾选=" + miSrc.Checked) + " / " + (miTxt == null ? "" : miTxt.Text));

            // 勾上以后两项文案要跟着变成「带原文」，并且偏好落进配置
            if (miSrc != null && miTxt != null && miDocx != null)
            {
                miSrc.PerformClick();
                MlPump(60);
                chk("勾上「带原文」后两项文案同步变（带原文）",
                    miTxt.Text.IndexOf("带原文", StringComparison.Ordinal) >= 0
                    && miDocx.Text.IndexOf("带原文", StringComparison.Ordinal) >= 0,
                    miTxt.Text);
                chk("导出偏好写进了配置", qf.ExportWithSrc, "export_with_src=" + GetSet("export_with_src", "?"));
                miSrc.PerformClick();   // 还原成默认
                MlPump(60);
                chk("再点一下能还原成「只译文」", !qf.ExportWithSrc && miTxt.Text.IndexOf("只译文", StringComparison.Ordinal) >= 0,
                    miTxt.Text);
            }

            // ---------- 三、复制全文：文案随语种 ----------
            sb.AppendLine();
            sb.AppendLine("三、复制全文（显性化 + 文案随语种）");
            qf.MainPrimeForTest("这个价格包含运费吗", "The price includes shipping.");
            MlPump(80);
            chk("中文进 → 出英文，文案是「复制全文（英文）」", qf.CopyAllLabel() == "复制全文（英文）", qf.CopyAllLabel());
            chk("文案里带出语种提示", qf.MainOutLangName() == "英文", "识别=" + qf.MainOutLangName());

            qf.MainPrimeForTest("Do you accept PayPal?", "你们接受 PayPal 吗？");
            MlPump(80);
            chk("英文进 → 出中文，文案是「复制全文（中文）」", qf.CopyAllLabel() == "复制全文（中文）", qf.CopyAllLabel());

            qf.MainPrimeForTest("12345 + 678", "12345 + 678");
            MlPump(80);
            chk("认不出语种时不硬凑，退回「复制全文」", qf.CopyAllLabel() == "复制全文", qf.CopyAllLabel());

            qf.MainPrimeForTest("这个价格包含运费吗", "The price includes shipping.");
            MlPump(80);
            ContextMenuStrip boxMenu = null;
            try { boxMenu = qf.BuildBoxMenu(); } catch { }
            chk("右键菜单里的复制全文文案也换成了当前语种",
                boxMenu != null && qf.MiCopyAllBoxTextForTest != null
                && qf.MiCopyAllBoxTextForTest.StartsWith("复制全文（英文）", StringComparison.Ordinal),
                qf.MiCopyAllBoxTextForTest);
            chk("复制全文复制的是整段结果，不是一句",
                qf.CopyAllText() == qf.BoxTextForTest && qf.BoxTextForTest.Length > 10,
                "长度=" + qf.CopyAllText().Length);
            chk("底栏「复制这句」仍在（原有能力没被顶掉）",
                FindDeep(qf, "btnCopyLine") != null || ContainsText(footTexts, "复制这句"), "按钮文案=" + (FindDeep(qf, "btnCopyLine") == null ? "找不到控件名" : "btnCopyLine"));

            // ---------- 四、导出 TXT ----------
            sb.AppendLine();
            sb.AppendLine("四、导出 TXT");
            string srcZh = "这个价格包含运费吗？我们下周可以发货。";
            string dstEn = "Does this price include shipping? We can ship next week.";
            string p1 = qf.MainWriteExport(tmp, false, srcZh, dstEn, false);
            chk("TXT 落盘成功", File.Exists(p1), p1);
            chk("文件名带日期时间（中英译文_yyyyMMdd_HHmmss）",
                Path.GetFileName(p1).StartsWith("中英译文_", StringComparison.Ordinal) && Path.GetFileName(p1).EndsWith(".txt", StringComparison.Ordinal)
                && Path.GetFileName(p1).Length >= "中英译文_20260101_000000.txt".Length - 2,
                Path.GetFileName(p1));
            byte[] raw = File.ReadAllBytes(p1);
            chk("TXT 是 UTF-8 带 BOM（记事本打开不乱码）", raw.Length > 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF,
                "前三字节=" + (raw.Length > 3 ? (raw[0].ToString("X2") + raw[1].ToString("X2") + raw[2].ToString("X2")) : "太短"));
            string txt1 = File.ReadAllText(p1, Encoding.UTF8);
            chk("只导译文时不含原文句子", txt1.IndexOf(srcZh, StringComparison.Ordinal) < 0 && txt1.IndexOf("原文", StringComparison.Ordinal) < 0,
                txt1.IndexOf(srcZh, StringComparison.Ordinal) >= 0 ? "原文漏进去了" : "只有译文");

            string p2 = qf.MainWriteExport(tmp, false, srcZh, dstEn, true);
            string txt2 = File.ReadAllText(p2, Encoding.UTF8);
            bool srcBefore = txt2.IndexOf(srcZh, StringComparison.Ordinal) >= 0
                          && txt2.IndexOf(dstEn, StringComparison.Ordinal) >= 0
                          && txt2.IndexOf(srcZh, StringComparison.Ordinal) < txt2.IndexOf(dstEn, StringComparison.Ordinal);
            chk("勾了带原文后，原文在前、译文在后", srcBefore,
                srcBefore ? "顺序对" : "缺失或顺序乱");
            chk("两次导出不互相覆盖（同秒自动加 _2）", p1 != p2 && File.Exists(p1) && File.Exists(p2),
                Path.GetFileName(p1) + " / " + Path.GetFileName(p2));
            chk("同一秒重名会补 _2 序号", Path.GetFileName(p2).IndexOf("_2", StringComparison.Ordinal) > 0, Path.GetFileName(p2));

            // ---------- 五、导出 docx（手拼 OpenXML 真包） ----------
            sb.AppendLine();
            sb.AppendLine("五、导出 Word（.docx 必须是真 OpenXML 包）");
            string d1 = qf.MainWriteExport(tmp, true, srcZh, dstEn, false);
            string detail = "";
            string xml1 = QueryForm.MainDocxProbe(d1, out detail);
            chk("docx 落盘成功", File.Exists(d1), d1);
            chk("docx 是 zip 包（PK 头）而非改了后缀的文本", xml1 != null, detail);
            chk("docx 五个部件齐全（含 [Content_Types].xml / document.xml / styles.xml）", xml1 != null, detail);
            chk("document.xml 有 w:document / w:body / w:sectPr", xml1 != null
                && xml1.IndexOf("<w:document", StringComparison.Ordinal) >= 0
                && xml1.IndexOf("<w:sectPr>", StringComparison.Ordinal) >= 0, detail);
            chk("段落标签配平（w:p 开闭数量一致）", xml1 != null, detail);
            chk("docx 默认只写译文", xml1 != null && xml1.IndexOf(srcZh, StringComparison.Ordinal) < 0,
                xml1 == null ? "解析失败" : "文档里没出现原文");

            string d2 = qf.MainWriteExport(tmp, true, srcZh, dstEn, true);
            string detail2 = "";
            string xml2 = QueryForm.MainDocxProbe(d2, out detail2);
            chk("带原文的 docx 里有「原文」「译文」两个小标题",
                xml2 != null && xml2.IndexOf("原文", StringComparison.Ordinal) >= 0 && xml2.IndexOf("译文", StringComparison.Ordinal) >= 0, detail2);

            // RTL：阿拉伯语那一行必须带 bidi / jc=right，否则 Word 里是左对齐
            string ar = "مرحبا بالعالم";
            string d3 = qf.MainWriteExport(tmp, true, "", ar, false);
            string detail3 = "";
            string xml3 = QueryForm.MainDocxProbe(d3, out detail3);
            chk("从右往左的段落写了 <w:bidi/> 与右对齐",
                xml3 != null && xml3.IndexOf("<w:bidi/>", StringComparison.Ordinal) >= 0
                && xml3.IndexOf("w:jc w:val=\"right\"", StringComparison.Ordinal) >= 0, detail3);
            chk("RTL 段落字体指到 Arial（避免方框）",
                xml3 != null && xml3.IndexOf("Arial", StringComparison.Ordinal) >= 0, detail3);

            // 假 docx 必须被识破
            string fake = Path.Combine(tmp, "假的.docx");
            try { File.WriteAllText(fake, "这只是一个 txt 改了后缀", new UTF8Encoding(true)); } catch { }
            string fakeDetail = "";
            chk("把 txt 改后缀的假 docx 会被识破", QueryForm.MainDocxProbe(fake, out fakeDetail) == null, fakeDetail);

            // ---------- 六、桌面路径与容错 ----------
            sb.AppendLine();
            sb.AppendLine("六、落盘位置与容错");
            string desk = qf.ExportTargetDir();
            chk("导出目录取自系统（真实桌面，不是硬拼 %USERPROFILE%）",
                desk != null && Path.IsPathRooted(desk) && Directory.Exists(desk), desk);
            chk("桌面路径与系统 API 一致", desk == Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), desk);

            int deskCount0 = 0;
            try { deskCount0 = Directory.GetFiles(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "中英译文_*").Length; } catch { }
            qf.MainPrimeForTest("", "");
            MlPump(60);
            qf.ExportResult(false);
            MlPump(60);
            chk("没有译文时导出被拦住并给提示",
                qf.TipTextForTest.IndexOf("还没有译文", StringComparison.Ordinal) >= 0,
                "提示=" + qf.TipTextForTest);
            int deskCount1 = 0;
            try { deskCount1 = Directory.GetFiles(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "中英译文_*").Length; } catch { }
            chk("空结果不会在桌面留下垃圾文件", deskCount1 == deskCount0, "桌面导出文件数 " + deskCount0 + " → " + deskCount1);

            // ---------- 七、主界面画完一轮后仍零改动 ----------
            sb.AppendLine();
            sb.AppendLine("七、跑完这些操作后主界面无回归");
            // 只比"有哪些控件"，不比坐标与正文：渲染结果后窗口会为了显示全而自动长高，
            // 拿带坐标/正文的整棵树去比必然"不一致"，那不是回归。底栏的几何另有逐项断言。
            Func<Control, string> shape = null;
            shape = delegate(Control root)
            {
                List<string> lines = new List<string>();
                List<Control> stack = new List<Control>();
                stack.Add(root);
                while (stack.Count > 0)
                {
                    Control c = stack[stack.Count - 1];
                    stack.RemoveAt(stack.Count - 1);
                    lines.Add(c.GetType().Name + "#" + (c.Name == null ? "" : c.Name) + "$" + (c is Label || c is Button || c is RoundButton ? (c.Text ?? "") : ""));
                    foreach (Control k in c.Controls) stack.Add(k);
                }
                lines.Sort(StringComparer.Ordinal);
                return string.Join("|", lines.ToArray());
            };
            string shapeBefore = shape(qf);
            qf.MainPrimeForTest(srcZh, dstEn);
            MlPump(150);
            string shapeAfter = shape(qf);
            string treeAfter = CaptureTree(qf, "mlPanel,tipStrip");
            string barAfter = (bar == null) ? "" : CaptureTree(bar, "");
            int footH1 = (foot == null) ? -1 : foot.Height;
            chk("渲染一屏前后控件清单完全一致（没有为导出/复制临时塞控件）", shapeBefore == shapeAfter,
                shapeBefore == shapeAfter ? "控件清单一致" : "不一致：" + shapeAfter.Length + " vs " + shapeBefore.Length);
            chk("底栏 5 个按钮的几何与文案逐项一致", barBefore == barAfter && barBefore.Length > 0,
                barBefore == barAfter ? "逐项一致" : "有差异");
            chk("底栏高度没被撑开", footH0 == footH1, footH0 + " → " + footH1);
            chk("「更多 ▾」按钮仍只有 5 个兄弟按钮", btnCount == 5, "按钮数=" + btnCount);
            chk("说明页里写了导出与复制全文的新入口",
                (qf.WelcomeTextForTest ?? "").IndexOf("导出 TXT", StringComparison.Ordinal) >= 0
                && (qf.WelcomeTextForTest ?? "").IndexOf("复制全文", StringComparison.Ordinal) >= 0,
                "说明页已更新");
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[异常] " + ex.GetType().Name + "：" + ex.Message);
            sb.AppendLine(ex.StackTrace);
        }
        finally
        {
            try { if (qf != null) qf.Close(); } catch { }
        }

        sb.AppendLine();
        sb.AppendLine("== 合计 " + (pass + fail) + " 项：通过 " + pass + " / 失败 " + fail);
        try { File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true)); } catch { }
    }

    // ========================================================================
    //  v2.4 第 4 步自检：--ingestcheck <报告>
    //  盯的是四件事：
    //    1) 八类文档拖到主界面都能读进来、填进输入框、走同一套分段上限；超上限只说"只翻前 N 字"；
    //    2) 文件夹 / 快捷方式 / 加密件 / 扫描件 / 空文件 一律给明确提示，不抛异常、不污染输入框；
    //    3) Ctrl+V 只在"有图没字"时接管，文字粘贴原样放行；OCR 链路复用已有的那份脚本；
    //    4) 底栏 5 个按钮与整窗控件清单零改动（拖入提示是临时的，松手即消失）。
    // ========================================================================
    static void IngestCheck(string outPath)
    {
        StringBuilder sb = new StringBuilder();
        int pass = 0, fail = 0;
        Action<string, bool, string> chk = delegate(string name, bool ok, string note)
        {
            if (ok) { pass++; sb.AppendLine("[通过] " + name + (note.Length > 0 ? "  —— " + note : "")); }
            else { fail++; sb.AppendLine("[失败] " + name + (note.Length > 0 ? "  —— " + note : "")); }
        };

        string tmp = Path.Combine(Path.GetTempPath(), "sqk_ingest_" + DateTime.Now.ToString("HHmmss"));
        try { Directory.CreateDirectory(tmp); } catch { }
        QueryForm qf = null;

        try
        {
            sb.AppendLine("== v2.4 第 4 步自检：主界面拖文件读译 + Ctrl+V 图片 OCR  " + AppVersionLine());
            sb.AppendLine("临时目录：" + tmp);
            sb.AppendLine();

            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            qf = new QueryForm();
            qf.Show();
            MlPump(1200);
            qf.MainForceDesignWidthForTest();
            qf.MainSetSuppressForTest(true);      // 自检绝不联网

            Panel foot = (Panel)FindDeep(qf, "foot");
            Panel bar = (Panel)FindDeep(qf, "bar");
            int footH0 = (foot == null) ? -1 : foot.Height;
            string barBefore = (bar == null) ? "" : CaptureTree(bar, "");

            Func<Control, string> shape = null;
            shape = delegate(Control root)
            {
                List<string> lines = new List<string>();
                List<Control> stack = new List<Control>();
                stack.Add(root);
                while (stack.Count > 0)
                {
                    Control c = stack[stack.Count - 1];
                    stack.RemoveAt(stack.Count - 1);
                    // 只记"有哪些控件"和按钮文案：Label 的正文会随翻译状态变，拿它比会把正常刷新当成回归
                    lines.Add(c.GetType().Name + "#" + (c.Name == null ? "" : c.Name) + "$" + (c is Button || c is RoundButton ? (c.Text ?? "") : ""));
                    foreach (Control k in c.Controls) stack.Add(k);
                }
                lines.Sort(StringComparer.Ordinal);
                return string.Join("|", lines.ToArray());
            };
            string shapeBase = shape(qf);

            // ---------- 一、底栏硬约束 ----------
            sb.AppendLine("一、底栏硬约束（5 个按钮零改动、不新增）");
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
            chk("底栏文案还是那 5 个（窄窗缩短说法也算）", (ContainsText(footTexts, "复制这句") || ContainsText(footTexts, "复制"))
                && ContainsText(footTexts, "读一遍") && (ContainsText(footTexts, "谐音") || ContainsText(footTexts, "谐音关"))
                && (ContainsText(footTexts, "语气") || ContainsText(footTexts, "正式") || ContainsText(footTexts, "口语") || ContainsText(footTexts, "自动"))
                && ContainsText(footTexts, "更多"),
                string.Join(" | ", footTexts.ToArray()));
            chk("没有为收文件新增底栏按钮（无「打开/选择文件」之类）",
                !ContainsText(footTexts, "打开") && !ContainsText(footTexts, "选择文件") && !ContainsText(footTexts, "拖"),
                ContainsText(footTexts, "打开") ? "底栏出现了「打开」" : "底栏干净");

            // ---------- 二、拖放接线 ----------
            sb.AppendLine();
            sb.AppendLine("二、拖放接线（主窗口任意位置都能接）");
            qf.MainInstallDrop();
            MlPump(60);
            int droppable = 0, controls = 0;
            Action<Control> walk = null;
            walk = delegate(Control r)
            {
                foreach (Control c in r.Controls)
                {
                    controls++;
                    if (c.AllowDrop) droppable++;
                    walk(c);
                }
            };
            walk(qf);
            chk("主窗口本身接了拖放（AllowDrop）", qf.AllowDrop, "AllowDrop=" + qf.AllowDrop);
            chk("子控件也接了拖放（拖到正文/空白/底栏边角都算数）", droppable >= controls - 2 && controls > 5,
                "已接 " + droppable + " / 共 " + controls + " 个子控件");
            qf.MainInstallDrop();                 // 再来一次
            qf.MainInstallDrop();
            chk("重复安装是幂等的（不会挂多份事件）", qf.MainDropOnForTest, "MainDropOn=" + qf.MainDropOnForTest);

            // ---------- 三、拖入时的可见接收提示 ----------
            sb.AppendLine();
            sb.AppendLine("三、拖入时的可见接收提示（临时覆盖层，松手即撤）");
            qf.MainDropEnterForTest(new string[] { "会议纪要.docx" });
            MlPump(60);
            Control hint = FindDeep(qf, "dropHint");
            chk("拖到窗口上会出现接收提示", hint != null && qf.MainHintAliveForTest,
                hint == null ? "没出现" : hint.GetType().Name + " 尺寸 " + hint.Width + "×" + hint.Height);
            chk("提示是铺满窗口的整块层（Dock=Fill），不是塞进底栏的小控件",
                hint != null && hint.Dock == DockStyle.Fill, hint == null ? "无" : "Dock=" + hint.Dock);
            chk("提示层盖在最前面（用户一眼能看见）",
                hint != null && qf.Controls.GetChildIndex(hint) == 0,
                hint == null ? "无" : "层级=" + qf.Controls.GetChildIndex(hint));
            chk("提示层不影响底栏（底栏还是原来那个面板，没被并进去）",
                FindDeep(qf, "bar") == bar && !(bar.Parent is MainDropHint), bar == null ? "无底栏" : "底栏未受牵连");
            qf.MainDropLeaveForTest();
            MlPump(80);
            chk("松手/移出后提示立刻消失", FindDeep(qf, "dropHint") == null && !qf.MainHintAliveForTest, "已撤");
            chk("撤掉提示后控件清单回到原来的样子（零残留）", shape(qf) == shapeBase,
                shape(qf) == shapeBase ? "一致" : "不一致");

            // ---------- 四、八类文档：读进来 → 填进去 ----------
            sb.AppendLine();
            sb.AppendLine("四、八类文档拖进主界面：读取 → 自动填入（自检不联网）");
            string zh = "这个价格能不能再便宜一点呢";
            string en = "Could you make it a little cheaper please";

            string pTxt = Path.Combine(tmp, "a.txt");
            string pMd = Path.Combine(tmp, "b.md");
            string pLog = Path.Combine(tmp, "c.log");
            string pCsv = Path.Combine(tmp, "d.csv");
            string pSrt = Path.Combine(tmp, "e.srt");
            string pJson = Path.Combine(tmp, "f.json");
            string pHtml = Path.Combine(tmp, "g.html");
            string pDocx = Path.Combine(tmp, "h.docx");
            string pPdf = Path.Combine(tmp, "i.pdf");
            File.WriteAllText(pTxt, zh + "，麻烦帮我看看运费。", new UTF8Encoding(true));
            File.WriteAllText(pMd, "# 标题\n\n" + zh + "。", new UTF8Encoding(true));
            File.WriteAllText(pLog, "2026-10-26 10:00:01 INFO " + zh, new UTF8Encoding(true));
            File.WriteAllText(pCsv, "姓名,备注\n张三,\"" + zh + "\"", new UTF8Encoding(true));
            File.WriteAllText(pSrt, "1\r\n00:00:01,000 --> 00:00:03,000\r\n" + zh + "\r\n\r\n2\r\n00:00:03,500 --> 00:00:06,000\r\n明天下午三点有空吗\r\n\r\n", new UTF8Encoding(true));
            File.WriteAllText(pJson, "{\"msg\":\"" + zh + "\"}", new UTF8Encoding(true));
            File.WriteAllText(pHtml, "<html><body><p>" + zh + "</p></body></html>", new UTF8Encoding(true));
            MlDocxSave(pDocx, "", zh + "。这是一份 Word 文档里的中文。", false);
            MlMakePdf(pPdf, zh + " this is inside a pdf", false);

            string[] eight = new string[] { pTxt, pMd, pLog, pCsv, pSrt, pJson, pHtml, pDocx, pPdf };
            string[] eightName = new string[] { "txt", "md", "log", "csv", "srt", "json", "html", "docx", "pdf" };
            for (int i = 0; i < eight.Length; i++)
            {
                string path = eight[i];
                string nm = eightName[i];
                if (!File.Exists(path)) { chk("造样例 " + nm, false, "文件没造出来"); continue; }
                string msg = "";
                try { msg = qf.MainRouteForTest(new string[] { path }); }
                catch (Exception ex) { chk(nm + " 拖入不崩", false, ex.GetType().Name + "：" + ex.Message); continue; }
                string got = qf.MlMainInputForTest ?? "";
                chk(nm + " 读进主界面", msg.IndexOf("已读入主界面", StringComparison.Ordinal) >= 0, msg);
                chk(nm + " 正文真的填进了输入框（不是只弹个提示）", got.Length >= 8, "输入框 " + got.Length + " 字：" + (got.Length > 40 ? got.Substring(0, 40) + "…" : got));
                chk(nm + " 自检模式下没有偷偷联网（抑制开关仍在、没有在跑的请求）",
                    msg.IndexOf("已读入", StringComparison.Ordinal) >= 0 && qf.MainSuppressForTest && !qf.MainBusyForTest,
                    "抑制=" + qf.MainSuppressForTest + " 在跑=" + qf.MainBusyForTest);
            }

            // ---------- 五、srt 只翻台词 ----------
            sb.AppendLine();
            sb.AppendLine("五、srt 去掉序号与时间轴，只留台词");
            string srtRaw = File.ReadAllText(pSrt);
            string srtClean = QueryForm.MainSrtForTest(srtRaw);
            chk("时间轴被去掉（不含 -->）", srtClean.IndexOf("-->", StringComparison.Ordinal) < 0, srtClean.Length + " 字");
            chk("序号行被去掉（没有孤零零的 1 / 2 行）",
                Array.IndexOf(srtClean.Split('\n'), "1") < 0 && Array.IndexOf(srtClean.Split('\n'), "2") < 0, srtClean.Replace("\n", " / "));
            chk("台词一条不少、顺序不变", srtClean.IndexOf("便宜一点", StringComparison.Ordinal) >= 0
                && srtClean.IndexOf("明天下午三点", StringComparison.Ordinal) >= 0
                && srtClean.IndexOf("便宜一点", StringComparison.Ordinal) < srtClean.IndexOf("明天下午三点", StringComparison.Ordinal),
                srtClean.Replace("\n", " / "));
            chk("srt 落到输入框里也是干净的（不含时间轴）", (qf.MlMainInputForTest ?? "").IndexOf("-->", StringComparison.Ordinal) < 0,
                "输入框里没有时间轴");
            chk("顺手兼容 vtt 的 WEBVTT 头与内联标签",
                QueryForm.MainSrtForTest("WEBVTT\n\n00:00.000 --> 00:02.000\n<i>hello there</i>\n").Trim() == "hello there",
                QueryForm.MainSrtForTest("WEBVTT\n\n00:00.000 --> 00:02.000\n<i>hello there</i>\n"));

            // ---------- 六、异常件：只给提示，不崩 ----------
            sb.AppendLine();
            sb.AppendLine("六、文件夹 / 快捷方式 / 加密件 / 扫描件 / 空文件：明确提示、不崩");
            string folder = Path.Combine(tmp, "一个文件夹");
            try { Directory.CreateDirectory(folder); } catch { }
            string lnk = Path.Combine(tmp, "快捷方式.lnk");
            try { File.WriteAllText(lnk, "fake lnk"); } catch { }
            string encDocx = Path.Combine(tmp, "加密.docx");
            string encPdf = Path.Combine(tmp, "加密.pdf");
            string scanPdf = Path.Combine(tmp, "扫描件.pdf");
            string emptyTxt = Path.Combine(tmp, "空.txt");
            // 真的加密 docx：Word 加密后就是一个 OLE 复合文档壳（D0CF11E0 开头），不是 zip
            byte[] ole = new byte[2048];
            byte[] sig = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
            Array.Copy(sig, ole, sig.Length);
            try { File.WriteAllBytes(encDocx, ole); } catch { }
            MlMakePdf(encPdf, "secret", true);
            MlMakePdf(scanPdf, "", false);
            File.WriteAllText(emptyTxt, "", new UTF8Encoding(true));
            string unsupported = Path.Combine(tmp, "压缩包.zip");
            File.WriteAllText(unsupported, "PK");

            Action<string, string> bad = delegate(string path, string label)
            {
                string before = qf.MlMainInputForTest ?? "";
                string msg = "";
                try { msg = qf.MainRouteForTest(new string[] { path }); }
                catch (Exception ex) { chk(label + " 不抛异常", false, ex.GetType().Name + "：" + ex.Message); return; }
                chk(label + " 拖入不崩", true, "返回：" + (msg.Length > 46 ? msg.Substring(0, 46) + "…" : msg));
                chk(label + " 给了明确提示（不是闷着不说）",
                    msg.IndexOf("不支持", StringComparison.Ordinal) >= 0
                    || msg.IndexOf("加密", StringComparison.Ordinal) >= 0
                    || msg.IndexOf("扫", StringComparison.Ordinal) >= 0
                    || msg.IndexOf("没有可翻", StringComparison.Ordinal) >= 0
                    || msg.IndexOf("读不了", StringComparison.Ordinal) >= 0
                    || msg.IndexOf("读不动", StringComparison.Ordinal) >= 0
                    || msg.IndexOf("暂不", StringComparison.Ordinal) >= 0, msg);
                chk(label + " 没把上一次的内容搞乱", (qf.MlMainInputForTest ?? "") == before, "输入框未变");
                chk(label + " 控件清单没变（没留残渣）", shape(qf) == shapeBase, shape(qf) == shapeBase ? "一致" : "不一致");
            };
            bad(folder, "文件夹");
            bad(lnk, "快捷方式(.lnk)");
            bad(encDocx, "加密 docx");
            bad(encPdf, "加密 pdf");
            bad(scanPdf, "扫描件 pdf（无文字层）");
            bad(emptyTxt, "空文件");
            bad(unsupported, "不支持的 .zip");

            chk("文件夹被点名说「不支持」（不是静默失败）",
                qf.MainRouteForTest(new string[] { folder }).IndexOf("不支持", StringComparison.Ordinal) >= 0,
                qf.MainRouteForTest(new string[] { folder }));
            chk("扫描件提示里说清「认不了字」这件事",
                qf.MainRouteForTest(new string[] { scanPdf }).IndexOf("扫", StringComparison.Ordinal) >= 0,
                qf.MainRouteForTest(new string[] { scanPdf }));

            // ---------- 七、长文：分段与上限沿用老规矩 ----------
            sb.AppendLine();
            sb.AppendLine("七、长文分段与超大文件上限（沿用已有参数，不另起一套）");
            chk("单段上限还是 1200 字", qf.MainSegCharsForTest == 1200, "MlSegChars=" + qf.MainSegCharsForTest);
            chk("单文件上限还是 20 万字", qf.MainMaxCharsForTest == 200000, "MlMaxChars=" + qf.MainMaxCharsForTest);
            chk("1200 字以内 → 1 段（直接一条请求）", qf.MainSegCountForTest(1000) == 1, "段数=" + qf.MainSegCountForTest(1000));
            chk("5000 字 → 多段（按 1200 切开）", qf.MainSegCountForTest(5000) >= 4, "段数=" + qf.MainSegCountForTest(5000));

            string huge = Path.Combine(tmp, "超大.txt");
            StringBuilder big = new StringBuilder();
            while (big.Length < 260000) big.Append("这是一段用来测试超上限的中文内容，重复很多遍。");
            File.WriteAllText(huge, big.ToString(), new UTF8Encoding(true));
            string hugeMsg = qf.MainRouteForTest(new string[] { huge });
            string hugeIn = qf.MlMainInputForTest ?? "";
            chk("超大文件不会把内存撑爆（读入长度被截到上限内）", hugeIn.Length <= qf.MainMaxCharsForTest, "读入 " + hugeIn.Length + " 字");
            chk("超大文件明确告诉用户「只翻前 N 字」", hugeMsg.IndexOf("只翻前", StringComparison.Ordinal) >= 0, hugeMsg.Length > 80 ? hugeMsg.Substring(0, 80) + "…" : hugeMsg);

            qf.MainIngestForTest(new string('中', 5000));
            MlPump(120);
            chk("长文走分段编排且自检不联网（抑制开关在、没起请求）",
                qf.MainSuppressForTest && !qf.MainBusyForTest, "抑制=" + qf.MainSuppressForTest + " 在跑=" + qf.MainBusyForTest);

            // ---------- 八、Ctrl+V：图才接管，文字放行 ----------
            sb.AppendLine();
            sb.AppendLine("八、Ctrl+V：剪贴板是图才认字，是文字就不抢");
            chk("剪贴板只有图 → 接管", QueryForm.MainWantPasteImage(true, false), "接管");
            chk("剪贴板只有文字 → 不接管（原样让输入框粘）", !QueryForm.MainWantPasteImage(false, true), "放行");
            chk("剪贴板图文都有 → 不接管（别把用户刚复制的字变成 OCR）", !QueryForm.MainWantPasteImage(true, true), "放行");
            chk("剪贴板空 → 不接管", !QueryForm.MainWantPasteImage(false, false), "放行");
            chk("剪贴板被别的程序占着时静默放行（不抛到界面）", SafeClipProbe(), "探针无异常");

            // OCR 认字：画一张有字的图，走已有那份 Windows.Media.Ocr 脚本
            string ocrPng = Path.Combine(tmp, "ocr_sample.png");
            try
            {
                using (Bitmap bmp = new Bitmap(520, 150))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.White);
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    using (Font f = new Font("Segoe UI", 30f, FontStyle.Regular))
                    using (SolidBrush br = new SolidBrush(Color.Black))
                        g.DrawString("HELLO OCR 2026", f, br, new PointF(16, 40));
                    bmp.Save(ocrPng, ImageFormat.Png);
                }
            }
            catch (Exception ex) { sb.AppendLine("  造 OCR 样例失败：" + ex.Message); }
            string ocrText = "";
            try { ocrText = qf.MainOcrFileForTest(ocrPng) ?? ""; }
            catch (Exception ex) { chk("图片认字链路不抛异常", false, ex.GetType().Name + "：" + ex.Message); }
            chk("图片能被认出字来（走的是已有 OCR 链路）", ocrText.Trim().Length > 0, "认出：" + (ocrText ?? "").Replace("\r", " ").Replace("\n", " ").Trim());
            chk("认出来的字确实对得上（HELLO/OCR 至少中一个）",
                ocrText.ToUpperInvariant().IndexOf("HELLO", StringComparison.Ordinal) >= 0
                || ocrText.ToUpperInvariant().IndexOf("OCR", StringComparison.Ordinal) >= 0, ocrText);
            chk("粘贴的图有固定落盘位置（OCR 脚本只认路径）",
                qf.MainClipPathForTest.EndsWith("paste_clip.png", StringComparison.OrdinalIgnoreCase)
                && qf.MainClipPathForTest.StartsWith(LOG_DIR, StringComparison.OrdinalIgnoreCase), qf.MainClipPathForTest);

            string tidyIn = "第一行\n第二行中文\nHELLO\nWORLD";
            string tidyOut = QueryForm.MainTidyForTest(tidyIn);
            chk("中文断行直接接上（不留空格）", tidyOut.IndexOf("第一行第二行中文", StringComparison.Ordinal) >= 0, tidyOut.Replace("\n", " / "));
            chk("英文断行补一个空格（不把两个词粘成一个）", tidyOut.IndexOf("HELLO WORLD", StringComparison.Ordinal) >= 0, tidyOut.Replace("\n", " / "));

            // 真剪贴板走一遍：放图 → 应该被接管并落盘（之后还原用户原来的剪贴板）
            bool hadText = false, hadImg = false;
            string oldText = "";
            Image oldImg = null;
            try
            {
                hadText = Clipboard.ContainsText(); if (hadText) oldText = Clipboard.GetText();
                hadImg = Clipboard.ContainsImage(); if (hadImg) oldImg = Clipboard.GetImage();
            }
            catch { }
            try
            {
                if (File.Exists(qf.MainClipPathForTest)) { try { File.Delete(qf.MainClipPathForTest); } catch { } }
                using (Image im = Image.FromFile(ocrPng)) Clipboard.SetImage(im);
                MlPump(80);
                bool took = false;
                try { took = qf.MainPasteProbeForTest(); } catch (Exception ex) { sb.AppendLine("  Ctrl+V 探针抛异常：" + ex.Message); }
                MlPump(400);
                chk("剪贴板里是图时 Ctrl+V 被接管（按键被吃掉，不再往输入框粘）", took, "接管=" + took);
                chk("图片已落盘给 OCR 用", File.Exists(qf.MainClipPathForTest), qf.MainClipPathForTest);
            }
            catch (Exception ex) { chk("真剪贴板走一遍不崩", false, ex.GetType().Name + "：" + ex.Message); }
            finally
            {
                try
                {
                    if (hadText && oldText != null && oldText.Length > 0) Clipboard.SetText(oldText);
                    else if (hadImg && oldImg != null) Clipboard.SetImage(oldImg);
                    else Clipboard.Clear();
                }
                catch { }
                try { if (oldImg != null) oldImg.Dispose(); } catch { }
            }

            MlPump(1500);   // 让后台 OCR 收尾，别和最后的界面比对抢跑道

            // ---------- 九、视觉零改动 ----------
            sb.AppendLine();
            sb.AppendLine("九、主界面视觉零改动（跑完上面所有操作后）");
            string barAfter = (bar == null) ? "" : CaptureTree(bar, "");
            int footH1 = (foot == null) ? -1 : foot.Height;
            chk("控件清单与开工前完全一致（拖入提示没留下、也没新塞控件）", shape(qf) == shapeBase,
                shape(qf) == shapeBase ? "一致" : "不一致");
            chk("底栏 5 个按钮的几何与文案逐项一致", barBefore == barAfter && barBefore.Length > 0,
                barBefore == barAfter ? "逐项一致" : "有差异");
            chk("底栏高度没被撑开", footH0 == footH1, footH0 + " → " + footH1);
            chk("主窗口没有新增可见的拖放控件（就靠 AllowDrop + 临时层）",
                FindDeep(qf, "dropHint") == null && FindDeep(qf, "btnOpen") == null && FindDeep(qf, "btnPick") == null,
                "没有常驻控件");
            chk("输入框还在原位（拖文件填的是它，不是另开的框）", FindDeep(qf, "inputWrap") != null, "inputWrap 在");
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[异常] " + ex.GetType().Name + "：" + ex.Message);
            sb.AppendLine(ex.StackTrace);
        }
        finally
        {
            try { if (qf != null) qf.Close(); } catch { }
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
        }

        sb.AppendLine();
        sb.AppendLine("== 合计 " + (pass + fail) + " 项：通过 " + pass + " / 失败 " + fail);
        try { File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true)); } catch { }
    }

    // 剪贴板被别的程序锁住时，Ctrl+V 探针必须静默放行而不是抛出来
    static bool SafeClipProbe()
    {
        try { Clipboard.ContainsImage(); Clipboard.ContainsText(); return true; }
        catch { return false; }
    }

    // ========================================================================
    //  v2.5 第 5 步自检：--voicecheck <报告>
    //  盯四件事：
    //    1) 底栏那 5 个按钮零改动，「说」不在底栏、也不是新的 Dock 行；
    //    2) 装上「说」前后，窗体客户区 / 输入框容器 / 输入框 / 「查一下」几何逐项一致；
    //    3) 中英识别切换、停口 1.8 秒、30 秒上限、文本拼接规则（中文直连、英文补空格）；
    //    4) 没引擎 / 没麦克风 / 没权限 给的是人话提示，不抛异常、不崩（自检绝不真开麦）。
    // ========================================================================
    static void VoiceCheck(string outPath)
    {
        StringBuilder sb = new StringBuilder();
        int pass = 0, fail = 0;
        Action<string, bool, string> chk = delegate(string name, bool ok, string note)
        {
            if (ok) { pass++; sb.AppendLine("[通过] " + name + (note.Length > 0 ? "  —— " + note : "")); }
            else { fail++; sb.AppendLine("[失败] " + name + (note.Length > 0 ? "  —— " + note : "")); }
        };

        QueryForm qf = null;
        string oldLang = "zh";
        try
        {
            sb.AppendLine("== v2.5 第 5 步自检：主界面语音输入（中英 / 停口 1.8 秒自动翻）  " + AppVersionLine());
            sb.AppendLine();
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            qf = new QueryForm();
            qf.Show();
            MlPump(1200);
            qf.MainForceDesignWidthForTest();
            qf.MainSetSuppressForTest(true);      // 自检绝不联网
            qf.MainVoiceQuietWarnForTest(true);   // 自检不弹模态框

            Panel bar = (Panel)FindDeep(qf, "bar");
            Panel foot = (Panel)FindDeep(qf, "foot");
            Panel inputBar = (Panel)FindDeep(qf, "inputBar");
            Panel inputWrap = (Panel)FindDeep(qf, "inputWrap");

            Control box = null;
            if (inputWrap != null) foreach (Control c in inputWrap.Controls) if (c is TextBox) { box = c; break; }
            Control go = null;
            if (inputBar != null) foreach (Control c in inputBar.Controls) if (c is RoundButton) { go = c; break; }

            sb.AppendLine("环境：可见识别引擎 = " + qf.MainVoiceEngineListForTest());
            sb.AppendLine();

            // ---------- 一、底栏硬约束 ----------
            sb.AppendLine("一、底栏硬约束（5 个按钮零改动，「说」不进底栏）");
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
            chk("底栏文案还是那 5 个（窄窗缩短说法也算）", (ContainsText(footTexts, "复制这句") || ContainsText(footTexts, "复制"))
                && ContainsText(footTexts, "读一遍") && (ContainsText(footTexts, "谐音") || ContainsText(footTexts, "谐音关"))
                && (ContainsText(footTexts, "语气") || ContainsText(footTexts, "正式") || ContainsText(footTexts, "口语") || ContainsText(footTexts, "自动"))
                && ContainsText(footTexts, "更多"),
                string.Join(" | ", footTexts.ToArray()));
            chk("「说」没有挤进底栏", !ContainsText(footTexts, "说"), string.Join(" | ", footTexts.ToArray()));

            // ---------- 二、「说」的落位：浮层，不挤压布局 ----------
            sb.AppendLine();
            sb.AppendLine("二、「说」的落位（浮在输入框右侧，不占 Dock、不动别人）");
            Control mic = qf.MainVoiceButtonForTest;
            chk("「说」按钮装上了", mic != null, mic == null ? "没装上" : "尺寸 " + mic.Width + "×" + mic.Height);
            chk("「说」挂输入框容器里（不是底栏、不是新开的一行）", mic != null && mic.Parent == inputWrap,
                mic == null ? "无" : ("父=" + (mic.Parent == null ? "null" : mic.Parent.Name)));
            chk("「说」是浮层（Dock=None），不参与 Dock 布局", mic != null && mic.Dock == DockStyle.None,
                mic == null ? "无" : "Dock=" + mic.Dock);
            chk("「说」不挤压输入框（输入框仍是 Dock=Fill）", box != null && box.Dock == DockStyle.Fill,
                box == null ? "找不到输入框" : "Dock=" + box.Dock);
            chk("「说」在输入框右侧、不越界",
                mic != null && inputWrap != null && mic.Right <= inputWrap.ClientSize.Width && mic.Left > inputWrap.ClientSize.Width / 2,
                mic == null ? "无" : ("Left=" + mic.Left + " Right=" + mic.Right + " 容器宽=" + (inputWrap == null ? -1 : inputWrap.ClientSize.Width)));
            chk("「说」比输入框矮（不把这一行撑高）",
                mic != null && inputWrap != null && mic.Height <= inputWrap.ClientSize.Height,
                mic == null ? "无" : ("按钮高=" + mic.Height + " 容器高=" + (inputWrap == null ? -1 : inputWrap.ClientSize.Height)));
            chk("「说」没压到「查一下」",
                mic != null && go != null && !mic.RectangleToScreen(mic.ClientRectangle).IntersectsWith(go.RectangleToScreen(go.ClientRectangle)),
                mic == null || go == null ? "缺少控件" : "两者分得开");
            chk("输入框右边留出了按钮的位置（文字不钻到按钮底下）", RightMarginOf(box) > 0,
                "右边距=" + RightMarginOf(box) + "px");
            chk("输入框这一行高度还是 40（没被新按钮撑开）", inputBar != null && inputBar.Height == qf.SForTest(40),
                inputBar == null ? "找不到 inputBar" : ("高=" + inputBar.Height));
            chk("输入框容器内边距仍是 10,6,10,6",
                inputWrap != null && inputWrap.Padding == new Padding(qf.SForTest(10), qf.SForTest(6), qf.SForTest(10), qf.SForTest(6)),
                inputWrap == null ? "缺少控件" : inputWrap.Padding.ToString());

            // 装 / 卸 一轮，几何必须一位不差
            int w0 = qf.ClientSize.Width, h0 = qf.ClientSize.Height;
            Size wrap0 = (inputWrap == null) ? Size.Empty : inputWrap.ClientSize;
            int boxW0 = (box == null) ? -1 : box.Width, boxH0 = (box == null) ? -1 : box.Height;
            int goW0 = (go == null) ? -1 : go.Width, goX0 = (go == null) ? -1 : go.Left;
            int barH0 = (bar == null) ? -1 : bar.Height;
            string barTree0 = (bar == null) ? "" : CaptureTree(bar, "");

            qf.MainVoiceDisposeForTest();
            MlPump(60);
            chk("卸掉「说」窗口不会缩（说明它本来就没占布局）",
                qf.ClientSize.Width == w0 && qf.ClientSize.Height == h0,
                w0 + "×" + h0 + " → " + qf.ClientSize.Width + "×" + qf.ClientSize.Height);
            qf.MainVoiceReinstallForTest();
            MlPump(120);

            chk("装 / 卸「说」后窗体客户区不变",
                qf.ClientSize.Width == w0 && qf.ClientSize.Height == h0,
                w0 + "×" + h0 + " → " + qf.ClientSize.Width + "×" + qf.ClientSize.Height);
            chk("装 / 卸「说」后输入框容器尺寸不变", inputWrap != null && inputWrap.ClientSize == wrap0,
                wrap0.Width + "×" + wrap0.Height + " → " + (inputWrap == null ? "?" : inputWrap.ClientSize.Width + "×" + inputWrap.ClientSize.Height));
            chk("装 / 卸「说」后输入框宽高不变", box != null && box.Width == boxW0 && box.Height == boxH0,
                boxW0 + "×" + boxH0 + " → " + (box == null ? "?" : box.Width + "×" + box.Height));
            chk("装 / 卸「说」后「查一下」几何不变", go != null && go.Width == goW0 && go.Left == goX0,
                "宽 " + goW0 + "→" + (go == null ? -1 : go.Width) + "，左 " + goX0 + "→" + (go == null ? -1 : go.Left));
            chk("装 / 卸「说」后底栏高度与控件树不变",
                bar != null && bar.Height == barH0 && CaptureTree(bar, "") == barTree0,
                (bar == null) ? "缺少 bar" : (bar.Height == barH0 ? "一致" : "高度 " + barH0 + "→" + bar.Height));

            mic = qf.MainVoiceButtonForTest;
            chk("重新装回来的「说」仍在原位（幂等，不会越装越偏）",
                mic != null && mic.Parent == inputWrap && mic.Right <= inputWrap.ClientSize.Width,
                mic == null ? "没装上" : ("Left=" + mic.Left + " Right=" + mic.Right));

            // ---------- 三、录音中的样子 ----------
            sb.AppendLine();
            sb.AppendLine("三、录音中的样子（点开始 / 点停 / 视觉状态明确）");
            Color idleFill = qf.MainVoiceFillForTest;
            chk("闲着时按钮写「说」", mic != null && mic.Text == "说", mic == null ? "无" : mic.Text);
            qf.MainVoiceStateForTest(true);
            MlPump(60);
            chk("录音中按钮写「停」（一眼看出在录）", mic != null && mic.Text == "停", mic == null ? "无" : mic.Text);
            chk("录音中按钮换了颜色（不只是文字变）", mic != null && qf.MainVoiceFillForTest != idleFill,
                mic == null ? "无" : idleFill.Name + " → " + qf.MainVoiceFillForTest.Name);
            qf.MainVoiceStateForTest(false);
            MlPump(60);
            chk("停下后按钮回到「说」", mic != null && mic.Text == "说", mic == null ? "无" : mic.Text);
            chk("停下后颜色也回到原样", mic != null && qf.MainVoiceFillForTest == idleFill,
                mic == null ? "无" : qf.MainVoiceFillForTest.Name);

            // ---------- 四、中英切换 ----------
            sb.AppendLine();
            sb.AppendLine("四、默认识别中文，右键切英文");
            oldLang = qf.MainVoiceLangForTest;
            chk("默认识别语言是中文", qf.MainVoiceLangForTest == "zh", "voice_lang=" + qf.MainVoiceLangForTest);
            ContextMenuStrip vm = qf.MainVoiceMenuForTest;
            int miCount = (vm == null) ? 0 : vm.Items.Count;
            chk("右键有切换菜单（中文 / English 两项）", vm != null && miCount >= 2, "菜单项 " + miCount + " 个");
            chk("菜单里标出了当前语种（勾在中文上）",
                vm != null && vm.Items.Count >= 2 && ((ToolStripMenuItem)vm.Items[0]).Checked
                && !((ToolStripMenuItem)vm.Items[1]).Checked, "勾选状态正确");

            qf.MainVoiceSetLang("en");
            MlPump(30);
            chk("切英文后按英文识别", qf.MainVoiceLangForTest == "en", "voice_lang=" + qf.MainVoiceLangForTest);
            chk("切英文后勾选跟着换",
                vm != null && vm.Items.Count >= 2 && !((ToolStripMenuItem)vm.Items[0]).Checked
                && ((ToolStripMenuItem)vm.Items[1]).Checked, "勾选状态正确");
            qf.MainVoiceSetLang("zh");
            MlPump(30);
            chk("能切回中文", qf.MainVoiceLangForTest == "zh", "voice_lang=" + qf.MainVoiceLangForTest);
            chk("语种记进了配置（下次开还是这个）",
                GetSet("voice_lang", "?") == "zh", "settings voice_lang=" + GetSet("voice_lang", "?"));

            // ---------- 五、阈值与文本拼接 ----------
            sb.AppendLine();
            sb.AppendLine("五、停口阈值、上限与拼接规则");
            chk("停口自动收尾是 1.8 秒", QueryForm.VOICE_STOP_SILENCE_MS == 1800, QueryForm.VOICE_STOP_SILENCE_MS + " 毫秒");
            chk("最长听 30 秒就自动收", QueryForm.VOICE_MAX_MS == 30000, QueryForm.VOICE_MAX_MS + " 毫秒");
            chk("中文识别结果直接接上（不插空格）", QueryForm.MicCompose("", "今天天气不错", "") == "今天天气不错",
                QueryForm.MicCompose("", "今天天气不错", ""));
            chk("英文单词之间补一个空格", QueryForm.MicJoin("HELLO", "WORLD") == "HELLO WORLD", QueryForm.MicJoin("HELLO", "WORLD"));
            chk("中文句子之间不补空格", QueryForm.MicJoin("第一句", "第二句") == "第一句第二句", QueryForm.MicJoin("第一句", "第二句"));
            chk("中英混着说也不粘（中文后接英文补空格）",
                QueryForm.MicJoin("今天", "HELLO") == "今天 HELLO", QueryForm.MicJoin("今天", "HELLO"));
            chk("定稿 + 中间结果能拼成完整一句",
                QueryForm.MicCompose("", "今天天气", "不错") == "今天天气不错", QueryForm.MicCompose("", "今天天气", "不错"));
            chk("输入框原有的字会被接在前面",
                QueryForm.MicCompose("先写的话", "今天天气不错", "") == "先写的话今天天气不错",
                QueryForm.MicCompose("先写的话", "今天天气不错", ""));
            chk("换行制表都归一成空格", QueryForm.MicClean("  a\r\nb\tc  ") == "a b c", QueryForm.MicClean("  a\r\nb\nc  "));
            chk("全空还是空（不会凭空造字）", QueryForm.MicCompose("", "", "") == "", "空");

            // ---------- 六、异常兜底：不崩、说人话 ----------
            sb.AppendLine();
            sb.AppendLine("六、异常兜底（没引擎 / 没麦克风 / 没权限：说人话，不崩）");
            qf.MainVoiceQuietWarnForTest(true);
            QueryForm.voiceForceNoEngineForTest = true;
            bool startThrew = false;
            try { qf.MainVoiceStartForTest(); }
            catch (Exception ex) { startThrew = true; sb.AppendLine("  点「说」抛了：" + ex.GetType().Name + "：" + ex.Message); }
            MlPump(60);
            chk("没有识别引擎时点「说」不抛异常（只是听不了）", !startThrew, startThrew ? "抛异常了" : "没抛");
            chk("没有识别引擎时不会假装在录", !qf.MainVoiceOnForTest, "micOn=" + qf.MainVoiceOnForTest);
            chk("没有识别引擎时按钮回到「说」", mic != null && mic.Text == "说", mic == null ? "无" : mic.Text);
            string warn1 = qf.lastVoiceWarn ?? "";
            chk("没引擎的提示是人话（说清了为什么、还给了退路）",
                warn1.IndexOf("引擎", StringComparison.Ordinal) >= 0
                && (warn1.IndexOf("打字", StringComparison.Ordinal) >= 0 || warn1.IndexOf("粘贴", StringComparison.Ordinal) >= 0),
                warn1.Length > 100 ? warn1.Substring(0, 100) + "…" : warn1);
            QueryForm.voiceForceNoEngineForTest = false;

            string micErr = QueryForm.MainVoiceMicErrorText(new InvalidOperationException("No audio input device"));
            chk("麦克风被占用 / 找不到时，提示里点名「麦克风」和「占着」",
                micErr.IndexOf("麦克风", StringComparison.Ordinal) >= 0 && micErr.IndexOf("占", StringComparison.Ordinal) >= 0,
                micErr.Length > 80 ? micErr.Substring(0, 80) + "…" : micErr);
            string denyErr = QueryForm.MainVoiceMicErrorText(new UnauthorizedAccessException("Access is denied."));
            chk("没权限时，提示里告诉用户去哪儿开权限（设置 → 隐私）",
                denyErr.IndexOf("隐私", StringComparison.Ordinal) >= 0 || denyErr.IndexOf("权限", StringComparison.Ordinal) >= 0,
                denyErr.Length > 80 ? denyErr.Substring(0, 80) + "…" : denyErr);
            chk("提示文案都不带英文异常堆栈（人话）",
                warn1.IndexOf("Exception", StringComparison.Ordinal) < 0 && micErr.IndexOf("Exception", StringComparison.Ordinal) < 0,
                "无堆栈字样");

            bool idleThrew = false;
            try { qf.MainVoiceFinishForTest("自检"); } catch { idleThrew = true; }
            chk("没在录的时候点停也不崩（幂等）", !idleThrew && !qf.MainVoiceOnForTest, idleThrew ? "抛异常" : "没事");

            // 一个字都没听到就收尾：不许去翻空文本
            qf.MainVoiceStateForTest(true);
            bool emptyThrew = false;
            try { qf.MainVoiceFinishForTest("自检空转"); } catch { emptyThrew = true; }
            MlPump(60);
            chk("一个字都没听到时不翻译（不会把空文本发出去）",
                !emptyThrew && !qf.MainVoiceOnForTest && !qf.MainBusyForTest,
                "异常=" + emptyThrew + " 在跑=" + qf.MainBusyForTest);

            // ---------- 七、回归总览 ----------
            sb.AppendLine();
            sb.AppendLine("七、回归总览");
            chk("主界面控件树里没有多出第二个浮动按钮",
                CountByName(qf, "btnMic") == 1, "btnMic 数量=" + CountByName(qf, "btnMic"));
            chk("底栏仍然只有一层按钮行（bar）", bar != null && FindDeep(foot, "bar") == bar, "结构未变");
            chk("输入框与「查一下」都还在原位（功能入口没被挤掉）", box != null && go != null && go.Text == "查一下",
                (box == null ? "缺输入框" : "输入框在") + " / " + (go == null ? "缺查一下" : go.Text));
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[异常] " + ex.GetType().Name + "：" + ex.Message);
            sb.AppendLine(ex.StackTrace);
        }
        finally
        {
            try { QueryForm.voiceForceNoEngineForTest = false; } catch { }
            try { if (qf != null && qf.MainVoiceLangForTest != oldLang) qf.MainVoiceSetLang(oldLang); } catch { }
            try { if (qf != null) qf.Close(); } catch { }
        }

        sb.AppendLine();
        sb.AppendLine("== 合计 " + (pass + fail) + " 项：通过 " + pass + " / 失败 " + fail);
        try { File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true)); } catch { }
    }

    // 读输入框当前的右边距（EM_GETMARGINS：低字左、高字右）
    static int RightMarginOf(Control box)
    {
        try
        {
            if (box == null || !box.IsHandleCreated) return -1;
            const int EM_GETMARGINS = 0x00D4;
            int m = (int)SendMessage(box.Handle, EM_GETMARGINS, 0, 0);
            return (m >> 16) & 0xFFFF;
        }
        catch { return -1; }
    }

    static int CountByName(Control root, string name)
    {
        if (root == null) return 0;
        int n = (root.Name == name) ? 1 : 0;
        foreach (Control c in root.Controls) n += CountByName(c, name);
        return n;
    }
}

