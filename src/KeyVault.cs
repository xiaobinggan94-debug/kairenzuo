//
// KeyVault.cs —— apikey.txt 的加密落盘（v1.0.0 第三条）
//
// 改之前：Key 以明文躺在 %APPDATA%\SolivineQuickLook\apikey.txt 里，
//         谁翻到那个文件夹、或者把文件拷走，就等于顺手把你的额度拿走了。
// 改之后：用 Windows 自带的 DPAPI（ProtectedData，CurrentUser 作用域）加密，
//         文件里只剩 "dpapi1:" + 一串 Base64，用记事本打开是看不懂的一坨。
//
// ★ 为什么用 DPAPI 而不是自己写 AES：
//   零第三方依赖、零安装、不碰注册表，微软自带；解密密钥由 Windows 按当前用户保管，
//   我们连"那把钥匙存哪儿"这个问题都不用回答。自己造 AES 反而要多管一个密码，
//   密码最后还得明文放旁边，等于白折腾。
// ★ 它能挡住：别人翻你的 %APPDATA%、拷走 apikey.txt、在共用电脑上换个账户登录。
//   挡不住：同一个 Windows 账户下运行的程序（DPAPI 就是这么设计的，别对外吹成保险箱）。
// ★ 换电脑 / 重装系统 / 换 Windows 账户之后解不开——这是特性不是故障：
//   此时当作"还没配 Key"，让小窗重新填一次就好，绝不因为解不开就崩掉或乱发请求。
// ★ 一切以"能用"优先：万一加密不可用（极罕见的受限账户），退回明文保存，
//   并在提示里说清楚，绝不为了加密把工具变成不能用。
//
// ★ 只碰 apikey.txt 的读写，主界面控件一个都没动（--audit 控件树必须与改前逐字节相同）。

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

internal static class KeyVault
{
    // 密文标记。带版本号，将来换算法（dpapi2:）也能认旧文件。
    internal const string SEAL_PREFIX = "dpapi1:";

    // 附加熵：让密文不是"随便一个 DPAPI 工具就能盲解"的裸数据。
    // 换掉这串字符等于让已存的 Key 全部作废，别乱改。
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SolivineQuickLook/apikey/v1");

    // 文件当前是什么状态，给自检和诊断报告说话用
    internal enum State { Missing, Sealed, Plain, Broken }

    // ====== 明文 -> 落盘文本；加密不可用返回 null（调用方退回明文）======
    internal static string Seal(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        try
        {
            byte[] raw = Encoding.UTF8.GetBytes(plain);
            byte[] enc = ProtectedData.Protect(raw, Entropy, DataProtectionScope.CurrentUser);
            return SEAL_PREFIX + Convert.ToBase64String(enc);
        }
        catch { return null; }
    }

    // ====== 落盘文本 -> 明文；不是密文、或换机后解不开，返回 null ======
    internal static string Unseal(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        string s = text.Replace("\uFEFF", "").Trim();
        if (!StartsWithPrefix(s)) return null;
        try
        {
            byte[] enc = Convert.FromBase64String(s.Substring(SEAL_PREFIX.Length).Trim());
            byte[] raw = ProtectedData.Unprotect(enc, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(raw).Trim();
        }
        catch { return null; }
    }

    static bool StartsWithPrefix(string s)
    {
        return s != null && s.StartsWith(SEAL_PREFIX, StringComparison.OrdinalIgnoreCase);
    }

    // ====== 文件里躺的是不是密文（不看能不能解开）======
    internal static bool LooksSealed(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return StartsWithPrefix(text.Replace("\uFEFF", "").Trim());
    }

    // ====== 只看状态，不动文件 ======
    internal static State Peek(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return State.Missing;
            string t = QueryForm.ReadTextAuto(path);
            if (t == null || t.Trim().Length == 0) return State.Missing;
            if (!LooksSealed(t)) return State.Plain;
            return Unseal(t) != null ? State.Sealed : State.Broken;
        }
        catch { return State.Missing; }
    }

    // ====== 统一读取入口 ======
    // 密文：解开就用；
    // 旧明文：原样交出去（由外面的 CleanKeyText 清洗），allowMigrate 时顺手升级成密文；
    // 解不开（换了电脑/账户）：返回空，等于"还没配 Key"，界面会引导重填。
    internal static string ReadKeyText(string path, bool allowMigrate)
    {
        string t;
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "";
            t = QueryForm.ReadTextAuto(path);
        }
        catch { return ""; }
        if (t == null || t.Trim().Length == 0) return "";
        if (!LooksSealed(t))
        {
            if (allowMigrate) TryUpgradePlain(path, t);
            return t;
        }
        string plain = Unseal(t);
        if (plain == null) return "";
        // 早期体验版升级时会顺手留一份 apikey.txt.plain.bak，那是明文。
        // 既然这里的密文已经验证能解开，就把备份也换成同样的密文——
        // 不删文件（你想翻还有），但明文不许留在盘上。
        try
        {
            string bak = path + ".plain.bak";
            if (File.Exists(bak) && !LooksSealed(File.ReadAllText(bak)))
                File.WriteAllText(bak, t + "\r\n", new UTF8Encoding(false));
        }
        catch { }
        return plain;
    }

    // ====== 旧明文升级成密文（一次性，失败就当没发生）======
    // 只在真读到一串像 Key 的东西时才动手，用户手工在文件里写的中文备注一律不动。
    // ★ 升级不留明文备份：在同一个文件夹再躺一份 .plain.bak，加密就白做了——
    //   谁翻到那个目录，照旧能把 Key 读走。回滚靠内存里的原文，不需要磁盘备份。
    // ★ 写完立刻回读验证：解出来必须还是同一串才算升级成功，验不过就把原文写回去。
    internal static void TryUpgradePlain(string path, string plainText)
    {
        try
        {
            string k = QueryForm.CleanKeyText(plainText);
            if (k.Length == 0) return;
            string sealedText = Seal(k);
            if (sealedText == null) return;
            File.WriteAllText(path, sealedText + "\r\n", new UTF8Encoding(false));
            string back = null;
            try { back = Unseal(QueryForm.ReadTextAuto(path)); } catch { }
            if (back != k)
            {
                try { File.WriteAllText(path, plainText, new UTF8Encoding(false)); } catch { }
            }
            // 老版本/早期体验版可能留下过明文备份，顺手清掉（它存在就等于没加密）
            try { if (File.Exists(path + ".plain.bak")) File.Delete(path + ".plain.bak"); } catch { }
        }
        catch { }
    }

    // ====== 给人看的一句话状态（诊断包/自检用，不含 Key 本身）======
    internal static string Describe(string path)
    {
        switch (Peek(path))
        {
            case State.Sealed: return "已加密保存（当前 Windows 用户可解）";
            case State.Plain: return "仍是明文（下次读取会自动升级为加密）";
            case State.Broken: return "加密状态但解不开（换了电脑或 Windows 账户，需重填 Key）";
            default: return "还没有 apikey.txt";
        }
    }
}
