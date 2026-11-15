// ============================================================================
//  翻译器 · Settings.cs（Settings）
//  配置与状态：数据目录、settings.ini 读写、窗口位置记忆、调试开关。
//  所有「要记住的东西」都在这个文件里落盘。
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
    static string LOG_DIR = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SolivineQuickLook");

    // ========================================================================
    //  v2.2 地基①：按内容嗅探编码读取文本（UTF-8 / BOM / GBK 回退）
    //  老系统导出的中文 txt、记事本按 ANSI 存的配置，都是 GBK 字节。一律按 UTF-8 读
    //  就会整篇变成「锟斤拷」，日志也跟着乱。这里的顺序是：
    //      BOM 最可靠 → 严格 UTF-8 试解（解不通就说明不是 UTF-8）→ 回退 GB18030/GBK
    //  GB18030 是 GBK 的超集，GBK 中文日志也能正确解出来。
    // ========================================================================
    static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
    static Encoding gbEncodingCache = null;

    static Encoding GbEncoding()
    {
        if (gbEncodingCache != null) return gbEncodingCache;
        try { gbEncodingCache = Encoding.GetEncoding(54936); }   // GB18030（GBK 超集）
        catch { }
        if (gbEncodingCache == null)
        {
            try { gbEncodingCache = Encoding.GetEncoding(936); } // GBK
            catch { gbEncodingCache = Encoding.Default; }
        }
        return gbEncodingCache;
    }

    internal static string DecodeText(byte[] b)
    {
        if (b == null || b.Length == 0) return "";
        // 1) 有 BOM 就照 BOM 走，不用猜
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
            return Encoding.UTF8.GetString(b, 3, b.Length - 3);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
            return Encoding.Unicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
        // 2) 严格 UTF-8：非法字节序列会抛异常，正好当探测器用
        try { return StrictUtf8.GetString(b); }
        catch { }
        // 3) 回退 GB18030 / GBK
        try { return GbEncoding().GetString(b); }
        catch { return Encoding.Default.GetString(b); }
    }

    internal static string ReadTextAuto(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "";
            return DecodeText(File.ReadAllBytes(path));
        }
        catch { return ""; }
    }

    internal static string[] ReadLinesAuto(string path)
    {
        string t = ReadTextAuto(path);
        if (t.Length == 0) return new string[0];
        return t.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    // 把「不是 UTF-8」的文本文件原地转成 UTF-8（带 BOM），转之前先留一份 .gbk.bak。
    // 用途：老版本或别家工具留下的 GBK 日志，直接往后追加 UTF-8 会变成半篇乱码。
    // 返回 true 表示真的转换过。
    internal static bool NormalizeUtf8File(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            byte[] b = File.ReadAllBytes(path);
            if (b.Length == 0) return false;
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return false;   // 已是 UTF-8 BOM
            bool pureAscii = true;
            for (int i = 0; i < b.Length; i++) if (b[i] >= 0x80) { pureAscii = false; break; }
            if (pureAscii) return false;                       // 纯 ASCII：两种编码字节一致，不用动
            try { StrictUtf8.GetString(b); return false; }     // 能解通 = 无 BOM 的 UTF-8，也不用动
            catch { }
            string text = GbEncoding().GetString(b);
            try { File.Copy(path, path + ".gbk.bak", true); } catch { }
            File.WriteAllText(path, text, new UTF8Encoding(true));
            return true;
        }
        catch { return false; }
    }

    static int logEncChecked = 0;

    static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(LOG_DIR);
            string logPath = Path.Combine(LOG_DIR, "log.txt");
            if (logEncChecked == 0)
            {
                logEncChecked = 1;
                NormalizeUtf8File(logPath);   // 每个进程只查一次，别每写一行都读整个文件
            }
            File.AppendAllText(logPath, DateTime.Now.ToString("MM-dd HH:mm:ss") + "  " + msg + "\r\n", Encoding.UTF8);
        }
        catch { }
    }

    static string SettingsFile { get { return Path.Combine(LOG_DIR, "settings.ini"); } }
    static Dictionary<string, string> settings = new Dictionary<string, string>();

    // ========================================================================
    //  v2.2 地基③：settings.ini 缺字段 / 写坏了都用默认值兜底
    //  老版本的配置里没有新键，新版本读到空值会一路把空字符串传下去。
    //  做法：先铺一份完整默认值，再用文件里真实存在的键覆盖，任何缺项都落回默认。
    // ========================================================================
    static Dictionary<string, string> SettingDefaults()
    {
        Dictionary<string, string> d = new Dictionary<string, string>();
        d["win_w"] = "480";
        d["win_h"] = "660";
        d["win_x"] = int.MinValue.ToString();   // 没存过位置就交给 LoadPos 判断，别硬摆
        d["win_y"] = int.MinValue.ToString();
        d["font_size"] = "11";
        d["voice"] = "Zira";
        d["rate"] = "-1";
        d["show_approx"] = "0";
        d["auto_popup"] = "0";
        d["topmost"] = "0";
        d["tone"] = "auto";
        // v2.3：浮层记住上次用的语言对（源默认自动识别、目标默认中文）
        d["ml_src"] = "auto";
        d["ml_dst"] = "zh";
        // v2.4 第 3 步：导出默认只导译文，勾了才带原文
        d["export_with_src"] = "0";
        // v2.5 第 5 步：「说」默认按中文识别，右键「说」可切英文
        d["voice_lang"] = "zh";
        // 第三批 ②：查询热键可换，默认还是老的 Ctrl+空格，老用户升级上来感觉不到变化
        d["hotkey"] = "ctrl+space";
        return d;
    }

    static void LoadSettings()
    {
        settings.Clear();
        // 先把默认值铺满，文件里没有的键就用这里的
        foreach (KeyValuePair<string, string> kv in SettingDefaults()) settings[kv.Key] = kv.Value;
        try
        {
            if (!Directory.Exists(LOG_DIR)) Directory.CreateDirectory(LOG_DIR);
            if (!File.Exists(SettingsFile)) return;
            // 用户在记事本里按 ANSI 存过就是 GBK，按内容嗅探读，免得中文注释/值整篇乱码
            foreach (string raw in ReadLinesAuto(SettingsFile))
            {
                string line = raw.Trim().TrimStart('\uFEFF');
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                int k = line.IndexOf('=');
                if (k <= 0) continue;
                string key = line.Substring(0, k).Trim();
                if (key.Length == 0) continue;
                settings[key] = line.Substring(k + 1).Trim();   // 坏行直接跳过，不影响其它键
            }
            // 值写坏了（比如 win_h=abc、font_size=大号）也当没写，回落到默认值，
            // 免得 GetInt 拿到非数字后各自用各自调用点写的兜底，结果前后不一致。
            foreach (string ik in IntSettingKeys())
            {
                string cur;
                if (!settings.TryGetValue(ik, out cur)) continue;
                int tmp;
                if (int.TryParse(cur, out tmp)) continue;
                string dft;
                if (SettingDefaults().TryGetValue(ik, out dft)) settings[ik] = dft;
                else settings.Remove(ik);
            }
        }
        catch { }
    }

    // settings.ini 里应当写成整数的键（写坏了就回默认，不往下传）
    static string[] IntSettingKeys()
    {
        return new string[] { "win_w", "win_h", "win_x", "win_y", "font_size", "rate", "show_approx", "auto_popup", "topmost" };
    }

    internal static bool dbgLayout = Environment.GetEnvironmentVariable("SQK_DEBUG") == "1";
    static void Dbg(string msg)
    {
        if (!dbgLayout) return;
        try
        {
            string f = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sqk_layout.log");
            File.AppendAllText(f, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n", Encoding.UTF8);
        }
        catch { }
    }

    static string GetSet(string key, string def)
    {
        string v;
        if (settings.TryGetValue(key, out v) && v != null && v.Length > 0) return v;
        return def;
    }

    static int GetInt(string key, int def)
    {
        int v;
        if (int.TryParse(GetSet(key, ""), out v)) return v;
        return def;
    }

    // 无头自检模式：不读写用户的 settings.ini，免得把用户真实字号/窗口尺寸带偏
    internal static bool selfTestMode = false;

    static void SetSet(string key, string val)
    {
        settings[key] = val;
        FlushSettings();
    }

    // 只改内存不落盘，配合 FlushSettings 批量写一次（拖窗口时绝不能每个像素都写盘）
    static void SetSetQuiet(string key, string val)
    {
        settings[key] = val;
    }

    static void FlushSettings()
    {
        try
        {
            Directory.CreateDirectory(LOG_DIR);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# " + AppTitle + " 设置，随便改，改坏了删掉这个文件就恢复默认");
            foreach (KeyValuePair<string, string> kv in settings)
                sb.AppendLine(kv.Key + "=" + kv.Value);
            File.WriteAllText(SettingsFile, sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }

    // ====== 第一批 ③：拔掉副屏之后窗口别跑丢 ======
    // LoadPos 只在启动时跑一次，所以"程序开着 → 拔掉副屏 → 托盘唤出"这条路上，
    // 窗口会停在已经不存在的坐标里，用户看到的现象就是"点了没反应"。
    // 每次唤出（以及显示器变化）都确认一遍：还有没有一块够得着的面积。
    internal bool EnsureOnScreen()
    {
        try
        {
            // 最大化交给系统自己处理，硬改 Bounds 反而会把最大化状态弄丢
            if (WindowState != FormWindowState.Normal) return false;
            Rectangle r = new Rectangle(Location, Size);
            // 判定标准是"留得下一块能抓的位置"，不是"擦到一点边"——
            // 只擦到十来像素的话标题栏抓不住，用起来等于还是丢了。
            // v2.5.9 ⑯：判定和落点抽到 ScaleCheck.cs，自检能拿假屏幕把这些情况全跑一遍
            if (FitsOnScreens(r, ScreenRects())) return false;
            Screen main = Screen.PrimaryScreen;
            if (main == null && Screen.AllScreens.Length > 0) main = Screen.AllScreens[0];
            if (main == null) return false;
            Rectangle wa = main.WorkingArea;
            Bounds = BringInside(r, wa);
            Dbg("EnsureOnScreen 窗口原本在 " + r + "，所有屏幕上都不够得着，拉回主屏 " + Bounds);
            // 这条要进主日志：用户报"窗口又不见了"时，一看 log.txt 就知道是不是它救回来的
            Log("窗口原本在 " + r + "，屏幕上找不着了，已拉回主屏 " + Bounds);
            return true;
        }
        catch { return false; }
    }

    void LoadPos()
    {
        try
        {
            int w = GetInt("win_w", 480);
            int h = GetInt("win_h", 660);
            // 统一按“客户区”存尺寸，避免每次启动少掉一圈边框像素（越开越小）
            if (w >= 300 && w <= 3000) ClientSize = new Size(w, ClientSize.Height);
            if (h >= 260 && h <= 3000) ClientSize = new Size(ClientSize.Width, h);
            Dbg("LoadPos w=" + w + " h=" + h + " -> client=" + ClientSize + " min=" + MinimumSize);
            int x = GetInt("win_x", int.MinValue);
            int y = GetInt("win_y", int.MinValue);
            if (x == int.MinValue || y == int.MinValue) return;
            Rectangle r = new Rectangle(x, y, Width, Height);
            foreach (Screen s in Screen.AllScreens)
            {
                if (s.WorkingArea.IntersectsWith(r))
                {
                    Rectangle wa2 = s.WorkingArea;
                    int nx = Math.Min(Math.Max(x, wa2.Left), wa2.Right - Width);
                    int ny = Math.Min(Math.Max(y, wa2.Top), wa2.Bottom - Height);
                    Location = new Point(nx, Math.Max(ny, wa2.Top));
                    return;
                }
            }
        }
        catch { }
    }

    void SavePos()
    {
        try
        {
            if (selfTestMode) return;
            if (!Visible || Location.X < -10000 || Location.Y < -10000) return;
            if (WindowState != FormWindowState.Normal) return;   // 最大化时别把满屏尺寸存进去
            if (Width < 300 || Height < 280) return;
            // 拖大之后窗口右下角很容易戳出屏幕，那一截就"看着像透明/丢字"，其实是跑到屏幕外了。
            // 松手存尺寸时顺手把整窗收回工作区，保证四边都还在屏幕上
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            int nx = Location.X, ny = Location.Y, nw = Width, nh = Height;
            if (nw > wa.Width) nw = wa.Width;
            if (nh > wa.Height) nh = wa.Height;
            if (nx + nw > wa.Right) nx = wa.Right - nw;
            if (ny + nh > wa.Bottom) ny = wa.Bottom - nh;
            if (nx < wa.Left) nx = wa.Left;
            if (ny < wa.Top) ny = wa.Top;
            if (nx != Location.X || ny != Location.Y || nw != Width || nh != Height)
                Bounds = new Rectangle(nx, ny, nw, nh);
            SetSetQuiet("win_x", Location.X.ToString());
            SetSetQuiet("win_y", Location.Y.ToString());
            SetSetQuiet("win_w", ClientSize.Width.ToString());
            SetSetQuiet("win_h", ClientSize.Height.ToString());
            FlushSettings();   // 一次落盘，别边拖边写
        }
        catch { }
    }

    // ========================================================================
    //  第三批 ②：查询热键可以换（默认 Ctrl+空格，撞车了就换一组记在 settings.ini）
    //  这里只放「候选表 + 解析 + 显示名」三件轻活，真正的注册动作在 MainForm.cs，
    //  因为那边才有窗口句柄。候选刻意避开截图热键要试的那几组
    //  （Ctrl+Shift+A / Ctrl+Alt+S / Ctrl+Shift+X / Ctrl+Alt+Q / Ctrl+Shift+F），
    //  免得查询键跟截图键自己抢自己。
    // ========================================================================
    internal static readonly string[] HotkeyChoices = new string[]
    {
        "ctrl+space",           // 默认，老用户升级上来感觉不到变化
        "ctrl+shift+space",
        "ctrl+shift+d",
        "ctrl+shift+z",
        "ctrl+alt+space"
    };

    // settings.ini 手改坏了（大小写混着、多打个空格、写个根本不存在的组合）一律落回默认，
    // 不让一处手抖把程序弄成「热键全没了」。返回的永远是候选表里的某一个。
    internal static string NormalizeHotkey(string raw)
    {
        if (raw == null) return "ctrl+space";
        string k = raw.Trim().ToLowerInvariant().Replace(" ", "").Replace('-', '+');
        for (int i = 0; i < HotkeyChoices.Length; i++)
            if (HotkeyChoices[i] == k) return k;
        return "ctrl+space";
    }

    // "ctrl+shift+d" → "Ctrl+Shift+空格"… 给界面和提示条看的人话
    internal static string HotkeyFriendly(string key)
    {
        string k = NormalizeHotkey(key);
        StringBuilder sb = new StringBuilder();
        foreach (string p in k.Split('+'))
        {
            if (sb.Length > 0) sb.Append('+');
            if (p == "ctrl") sb.Append("Ctrl");
            else if (p == "shift") sb.Append("Shift");
            else if (p == "alt") sb.Append("Alt");
            else if (p == "space") sb.Append("空格");
            else if (p.Length == 1) sb.Append(p.ToUpperInvariant());
            else sb.Append(p);
        }
        return sb.ToString();
    }

    // 拆成 RegisterHotKey 要的「修饰键 + 虚拟键码」
    internal static bool HotkeyParts(string key, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        foreach (string p in NormalizeHotkey(key).Split('+'))
        {
            if (p == "ctrl") mods |= MOD_CONTROL;
            else if (p == "shift") mods |= MOD_SHIFT;
            else if (p == "alt") mods |= MOD_ALT;
            else if (p == "space") vk = VK_SPACE;
            else if (p.Length == 1) vk = (uint)char.ToUpperInvariant(p[0]);
        }
        // 必须既有修饰键又有主键：vk=0 等于「没有键」，注册上去永远不会触发
        return mods != 0 && vk != 0;
    }
}
