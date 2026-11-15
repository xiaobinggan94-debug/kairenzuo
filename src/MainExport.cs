// ============================================================================
//  翻译器 · MainExport.cs（MainExport）
//  v2.4 第 3 步：主界面「导出 TXT / 导出 docx」+ 复制入口显性化
// ----------------------------------------------------------------------------
//  这一版只动"更多 ▾"菜单和右键菜单，底栏那 5 个按钮的位置/大小/文案/样式
//  一个字都不动（硬约束）：
//      · 导出 TXT / 导出 Word 只挂在「更多 ▾」里，另有 Ctrl+Shift+S / Ctrl+Shift+D
//      · 「复制全文」进「更多 ▾」第一条（加粗）+ 右键菜单，文案跟着当前语种变
//      · 「复制这句」原样保留，底栏按钮数量仍为 5
//  几个刻意的做法，别"顺手优化"掉：
//   1) 桌面目录一律问系统要（SHGetKnownFolderPath），不拼 %USERPROFILE%\Desktop，
//      因为桌面被 OneDrive 接管后那个路径是空的；
//   2) docx 是手拼 OpenXML 的 zip（stored 无压缩），Word / WPS 都能直接打开，
//      不是把 txt 改个后缀；
//   3) 默认只导译文，勾了「导出时带上原文」才把原文一起写进去；
//   4) 重名不覆盖，同一秒导两次自动加 _2、_3。
//  这是 partial class QueryForm 的其中一瓣，必须和另外 7 个 .cs 一起编译：
//      csc /target:winexe /codepage:65001 *.cs
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

partial class QueryForm
{
    // 上一次导出落到哪儿了（自检和提示要用；不落进配置，避免多一份状态）
    internal string lastExportPath = "";

    // ==================== ① 导出偏好：默认只导译文 ====================

    // 配置里存 0/1，缺省 0（只译文）。放成属性是为了不去碰构造函数里的初始化顺序
    internal bool ExportWithSrc
    {
        get { return GetSet("export_with_src", "0") == "1"; }
        set { SetSet("export_with_src", value ? "1" : "0"); }
    }

    internal string ExportTargetDir()
    {
        return MlDesktopDir();          // 系统 API 取真实桌面（OneDrive 重定向也对）
    }

    // ==================== ② 复制全文：文案跟着当前语种走 ====================

    // 现在这一屏结果是什么语种：中文进去 → 出英文，英文进去 → 出中文。
    // 认不出来（比如只查了个符号）就返回空串，文案退回不带语种的「复制全文」。
    internal string MainOutLangName()
    {
        string s = lastInput;
        if (s == null || s.Trim().Length == 0) s = (box == null || box.Text == null) ? "" : box.Text;
        if (s == null || s.Trim().Length == 0) return "";
        string lang = MlLangOfText(s);
        if (lang == "zh") return "英文";
        if (lang == "en") return "中文";
        return "";
    }

    internal string CopyAllLabel()
    {
        string n = MainOutLangName();
        return "复制全文" + (n.Length > 0 ? "（" + n + "）" : "");
    }

    // 复制全文真正要复制的东西：整段结果（不是归零着色后的选区，也不含 tip）
    internal string CopyAllText()
    {
        if (box == null || box.Text == null) return "";
        return box.Text;
    }

    // 菜单里那两个"复制全文"入口的文案在每次出结果后对齐一次语种
    internal void RefreshCopyLabels()
    {
        try
        {
            string lb = CopyAllLabel();
            if (miCopyAllBox != null) miCopyAllBox.Text = lb + "（Ctrl+Shift+C）";
            if (footTip != null && btnMore != null)
                footTip.SetToolTip(btnMore, "更多 ▾：" + lb + "（Ctrl+Shift+C）、导出 TXT / Word…");
        }
        catch { }
    }

    // ==================== ③ 导出落盘：TXT ====================

    internal static string MainTxtBody(string src, string dst, bool withSrc)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine(AppTitle + " · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine();
        if (withSrc && src != null && src.Trim().Length > 0)
        {
            sb.AppendLine("===== 原文 =====");
            sb.AppendLine(src.Trim());
            sb.AppendLine();
            sb.AppendLine("===== 译文 =====");
        }
        sb.AppendLine(dst == null ? "" : dst.Trim());
        return sb.ToString();
    }

    // ==================== ④ 导出落盘：手拼 OpenXML 的真 docx ====================

    internal static string MainDocxDocumentXml(string src, string dst, bool withSrc)
    {
        StringBuilder body = new StringBuilder();
        body.Append(MlDocxParaXml(AppTitle + " · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), false, true, null));
        bool hasSrc = withSrc && src != null && src.Trim().Length > 0;
        if (hasSrc)
        {
            body.Append(MlDocxParaXml("原文", false, true, null));
            foreach (string line in MainSplitLines(src))
                body.Append(MlDocxParaXml(line, MlRtlDominant(line), false, null));
            body.Append(MlDocxParaXml("译文", false, true, null));
        }
        foreach (string line in MainSplitLines(dst))
            body.Append(MlDocxParaXml(line, MlRtlDominant(line), false, null));

        return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
             + "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">"
             + "<w:body>"
             + body.ToString()
             + "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/>"
             + "<w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\"/>"
             + "</w:sectPr>"
             + "</w:body></w:document>";
    }

    static string[] MainSplitLines(string s)
    {
        if (s == null) return new string[0];
        return s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }

    internal static void MainDocxSave(string path, string src, string dst, bool withSrc)
    {
        List<string> names = new List<string>();
        List<byte[]> datas = new List<byte[]>();
        names.Add("[Content_Types].xml"); datas.Add(Encoding.UTF8.GetBytes(ML_DOCX_CT));
        names.Add("_rels/.rels"); datas.Add(Encoding.UTF8.GetBytes(ML_DOCX_RELS));
        names.Add("word/document.xml"); datas.Add(Encoding.UTF8.GetBytes(MainDocxDocumentXml(src, dst, withSrc)));
        names.Add("word/styles.xml"); datas.Add(Encoding.UTF8.GetBytes(ML_DOCX_STYLES));
        names.Add("word/_rels/document.xml.rels"); datas.Add(Encoding.UTF8.GetBytes(ML_DOCX_DOCRELS));
        MlZipWrite(path, names, datas);
    }

    // 自检用：把 docx 当 zip 拆开，确认它真是 OpenXML 包（而不是改了后缀的文本）。
    // 我们自己写的包是 stored（不压缩）的，所以挨个读本地文件头就够了，
    // 不用额外引 System.IO.Compression.dll。
    internal static string MainDocxProbe(string path, out string detail)
    {
        detail = "";
        byte[] all;
        try { all = File.ReadAllBytes(path); }
        catch (Exception ex) { detail = "读不进来：" + ex.Message; return null; }
        if (all.Length < 300) { detail = "文件只有 " + all.Length + " 字节"; return null; }
        if (all[0] != 0x50 || all[1] != 0x4B) { detail = "开头不是 PK，不是 zip"; return null; }

        Dictionary<string, byte[]> parts = new Dictionary<string, byte[]>();
        int i = 0;
        while (i + 30 <= all.Length
            && all[i] == 0x50 && all[i + 1] == 0x4B && all[i + 2] == 0x03 && all[i + 3] == 0x04)
        {
            int compSize = BitConverter.ToInt32(all, i + 18);
            int nameLen = BitConverter.ToUInt16(all, i + 26);
            int extraLen = BitConverter.ToUInt16(all, i + 28);
            if (compSize < 0) { detail = "条目长度非法"; return null; }
            if (i + 30 + nameLen + extraLen + compSize > all.Length) { detail = "条目越界（包被截断）"; return null; }
            string name = Encoding.UTF8.GetString(all, i + 30, nameLen);
            byte[] data = new byte[compSize];
            Array.Copy(all, i + 30 + nameLen + extraLen, data, 0, compSize);
            parts[name] = data;
            i = i + 30 + nameLen + extraLen + compSize;
        }

        string[] need = new string[] {
            "[Content_Types].xml", "_rels/.rels",
            "word/document.xml", "word/styles.xml", "word/_rels/document.xml.rels"
        };
        foreach (string n in need)
        {
            if (!parts.ContainsKey(n)) { detail = "缺部件 " + n + "（现有 " + parts.Count + " 个）"; return null; }
        }
        bool eocd = false;
        for (int k = Math.Max(0, all.Length - 64); k + 4 <= all.Length; k++)
        {
            if (all[k] == 0x50 && all[k + 1] == 0x4B && all[k + 2] == 0x05 && all[k + 3] == 0x06) { eocd = true; break; }
        }
        if (!eocd) { detail = "没有中央目录结尾（Word 会判为损坏）"; return null; }

        string xml = Encoding.UTF8.GetString(parts["word/document.xml"]);
        if (xml.IndexOf("<w:document", StringComparison.Ordinal) < 0) { detail = "document.xml 里没有 w:document"; return null; }
        if (xml.IndexOf("<w:body>", StringComparison.Ordinal) < 0) { detail = "document.xml 里没有 w:body"; return null; }
        if (xml.IndexOf("<w:sectPr>", StringComparison.Ordinal) < 0) { detail = "document.xml 里没有 sectPr"; return null; }
        int open = 0, close = 0;
        int a = 0;
        while ((a = xml.IndexOf("<w:p>", a, StringComparison.Ordinal)) >= 0) { open++; a += 5; }
        a = 0;
        while ((a = xml.IndexOf("</w:p>", a, StringComparison.Ordinal)) >= 0) { close++; a += 6; }
        if (open == 0 || open != close) { detail = "段落标签不配平：" + open + " 开 / " + close + " 闭"; return null; }

        detail = "部件 " + parts.Count + " 个，段落 " + open + " 段，共 " + all.Length + " 字节";
        return xml;
    }

    // ==================== ⑤ 真正导出（菜单与快捷键都走这里） ====================

    // dir 交给调用方是为了自检能导到临时目录，正式运行时传的就是真实桌面
    internal string MainWriteExport(string dir, bool docx, string src, string dst, bool withSrc)
    {
        if (dir == null || dir.Trim().Length == 0) throw new Exception("找不到桌面目录");
        if (dst == null) dst = "";
        Directory.CreateDirectory(dir);

        string baseName = "中英译文_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string ext = docx ? ".docx" : ".txt";
        string path = Path.Combine(dir, baseName + ext);
        int dup = 2;
        while (File.Exists(path)) { path = Path.Combine(dir, baseName + "_" + dup + ext); dup++; }

        if (docx) MainDocxSave(path, src, dst, withSrc);
        else File.WriteAllText(path, MainTxtBody(src, dst, withSrc), new UTF8Encoding(true));   // 带 BOM，记事本/Word 打开不乱码
        return path;
    }

    internal void ExportResult(bool docx)
    {
        string dst = (lastFull != null && lastFull.Trim().Length > 0)
            ? lastFull.Trim()
            : ((box == null || box.Text == null) ? "" : box.Text.Trim());
        // 导出默认不带【语法】：语法讲解是「看的时候帮你懂」，导出去多半是要对外发送或存档，
        // 多一块讲解反而碍事。想看语法就照屏幕上那份看。
        dst = StripGrammarSection(dst).Trim();
        if (dst.Length == 0) { tip.Text = "还没有译文，先查一句再导出"; return; }

        bool withSrc = ExportWithSrc;
        string src = ((lastInput == null) ? "" : lastInput.Trim());
        if (!withSrc) src = "";

        string dir = ExportTargetDir();
        if (dir == null || dir.Trim().Length == 0) { tip.Text = "找不到桌面目录，导出没做成"; return; }

        try
        {
            string p = MainWriteExport(dir, docx, src, dst, withSrc);
            lastExportPath = p;
            tip.Text = "已导出到桌面：" + Path.GetFileName(p) + (withSrc ? "（带原文）" : "（只有译文）");
            Log("主界面导出 " + (docx ? "docx" : "txt") + " → " + p + " 带原文=" + withSrc + " 译文字数=" + dst.Length);
        }
        catch (Exception ex)
        {
            tip.Text = "导出失败：" + MlOneLine(ex.Message, 60);
            Log("主界面导出失败：" + ex.Message);
        }
    }

    // ==================== ⑥ 自检用的几把小钥匙 ====================

    // 造一屏「查完了」的现场：原文进 lastInput、结果进结果框（与真实链路一致）
    internal void MainPrimeForTest(string src, string dst)
    {
        lastInput = (src == null) ? "" : src;
        RenderResult((dst == null) ? "" : dst);
    }

    internal string TipTextForTest
    {
        get { return (tip == null || tip.Text == null) ? "" : tip.Text; }
    }

    internal string MiCopyAllBoxTextForTest
    {
        get { return (miCopyAllBox == null || miCopyAllBox.Text == null) ? "" : miCopyAllBox.Text; }
    }

    internal string BoxTextForTest
    {
        get { return (box == null || box.Text == null) ? "" : box.Text; }
    }

    internal string LastExportPathForTest
    {
        get { return lastExportPath; }
    }

    internal string WelcomeTextForTest
    {
        get { return lastWelcomeText; }
    }
}
