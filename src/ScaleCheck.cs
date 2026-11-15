// ============================================================================
//  饼饼专属中英互译 · ScaleCheck.cs（第二档 ⑯ 换个屏 / 放大字号也认得路）
//  ---------------------------------------------------------------------------
//  要解决的事：两个「换个环境就出问题」的老毛病——
//    · 插着副屏把窗口拖到副屏上，回家拔掉副屏一开机，窗口停在已经不存在的坐标里，
//      点托盘饼干没反应（看着像程序坏了，其实就是窗口在屏幕外面）。
//    · 把字号往大了调（Ctrl+滚轮 / 菜单），窗口尺寸没跟上，框里的字被切掉一半。
//
//  这一轮不改界面，只做两件事：
//    ① 把「位置还够不够得着」「够不着就拉回哪儿」抽成纯函数，好拿假屏幕反复验；
//    ② 把「各种字号 × 各种窗口尺寸」摆成矩阵，一组一组审计有没有字被切、控件跑出框。
//
//  判定口径跟原来一模一样：留得下一块 80×40 的可见区才算「够得着」——
//  只擦到十来个像素的话标题栏抓不住，用起来等于还是丢了。
// ============================================================================

using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

partial class QueryForm
{
    // 一块「抓得住」的最小可见面积（跟 EnsureOnScreen 原来的口径一致）
    internal const int GrabMinW = 80;
    internal const int GrabMinH = 40;

    // ---------------- 一、纯逻辑：够不够得着 ----------------

    // 当前所有屏幕的工作区（做成函数是为了自检能拿假的屏幕数组来跑）
    internal static Rectangle[] ScreenRects()
    {
        try
        {
            Screen[] all = Screen.AllScreens;
            Rectangle[] r = new Rectangle[all.Length];
            for (int i = 0; i < all.Length; i++) r[i] = all[i].WorkingArea;
            return r;
        }
        catch { return new Rectangle[0]; }
    }

    // 任意一块屏幕上留得下 80×40 就算够得着
    internal static bool FitsOnScreens(Rectangle r, Rectangle[] screens)
    {
        if (screens == null || screens.Length == 0) return true;   // 读不到屏幕信息时别乱动窗口
        foreach (Rectangle wa in screens)
        {
            Rectangle inter = Rectangle.Intersect(r, wa);
            if (inter.Width >= GrabMinW && inter.Height >= GrabMinH) return true;
        }
        return false;
    }

    // 够不着时的落点：拉回主屏，尺寸夹到工作区内（窗口比屏幕高时不能硬塞），位置偏上一点
    internal static Rectangle BringInside(Rectangle want, Rectangle wa)
    {
        int nw = Math.Min(want.Width, wa.Width);
        int nh = Math.Min(want.Height, wa.Height);
        int nx = wa.Left + (wa.Width - nw) / 2;
        int ny = wa.Top + Math.Max(0, (wa.Height - nh) / 3);
        return new Rectangle(nx, ny, nw, nh);
    }

    // ---------------- 二、自检 A：位置矩阵（拿假屏幕跑，不动真实窗口） ----------------

    internal string DpiSelfTest()
    {
        StringBuilder sb = new StringBuilder();
        int pass = 0, fail = 0;
        Action<string, bool, string> chk = delegate (string name, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            sb.AppendLine((ok ? "  [OK]   " : "  [FAIL] ") + name + (string.IsNullOrEmpty(detail) ? "" : "  → " + detail));
        };

        // 假屏幕：一台 2560×1440 的笔记本；双屏时右边再接一块 1920×1080
        Rectangle main = new Rectangle(0, 0, 2560, 1400);      // 工作区（扣掉任务栏）
        Rectangle right = new Rectangle(2560, 0, 1920, 1040);
        Rectangle left = new Rectangle(-1920, 0, 1920, 1040);   // 副屏在左边（负坐标）
        Rectangle below = new Rectangle(0, 1400, 2560, 1040);   // 副屏在下边
        Rectangle[] one = new Rectangle[] { main };
        Rectangle[] twoR = new Rectangle[] { main, right };
        Rectangle[] twoL = new Rectangle[] { main, left };
        Rectangle[] twoB = new Rectangle[] { main, below };

        sb.AppendLine("== 一、窗口位置还够不够得着（口径：留得下 " + GrabMinW + "×" + GrabMinH + " 才算） ==");

        Rectangle r1 = new Rectangle(400, 200, 480, 660);
        chk("正常在屏幕中间 → 不动", FitsOnScreens(r1, one), r1.ToString());

        Rectangle r2 = new Rectangle(-3000, -3000, 480, 660);
        chk("整个跑到屏幕外面 → 判定够不着", !FitsOnScreens(r2, one), r2.ToString());

        Rectangle r3 = new Rectangle(-470, 300, 480, 660);   // 只露 10px 边
        chk("只擦到 10px 边 → 也算够不着（标题栏抓不住）", !FitsOnScreens(r3, one), r3.ToString());

        Rectangle r4 = new Rectangle(-380, 300, 480, 660);   // 露 100px
        chk("露出 100px → 够得着，不动它", FitsOnScreens(r4, one), r4.ToString());

        Rectangle r5 = new Rectangle(2560 + 100, 200, 480, 660);
        chk("在右边副屏上（副屏还在）→ 不动", FitsOnScreens(r5, twoR), r5.ToString());
        chk("同一位置、副屏拔掉了 → 判定够不着", !FitsOnScreens(r5, one), r5.ToString());

        Rectangle r6 = new Rectangle(-1800, 200, 480, 660);
        chk("副屏在左边（负坐标）→ 副屏在时不动", FitsOnScreens(r6, twoL), r6.ToString());
        chk("副屏拔掉后 → 判定够不着", !FitsOnScreens(r6, one), r6.ToString());

        Rectangle r7 = new Rectangle(200, 1500, 480, 660);
        chk("副屏在下边 → 副屏在时不动", FitsOnScreens(r7, twoB), r7.ToString());
        chk("副屏拔掉后 → 判定够不着", !FitsOnScreens(r7, one), r7.ToString());

        chk("读不到屏幕信息时不乱动窗口", FitsOnScreens(r2, new Rectangle[0]), "");

        sb.AppendLine();
        sb.AppendLine("== 二、够不着的时候拉回哪儿 ==");
        Rectangle fix2 = BringInside(r2, main);
        sb.AppendLine("  原位置 " + r2 + " → 拉回后 " + fix2);
        chk("拉回来之后真的落在主屏里", main.Contains(fix2), fix2.ToString());
        chk("横向居中（2560 屏、480 宽 → 左边界 1040）", fix2.Left == 1040, fix2.Left.ToString());
        chk("纵向偏上（不是正中，用起来更顺手）", fix2.Top == (main.Height - fix2.Height) / 3, fix2.Top.ToString());
        chk("尺寸没被改掉", fix2.Size == r2.Size, fix2.Size.ToString());

        Rectangle tall = new Rectangle(-3000, -3000, 480, 2000);   // 窗口比屏幕还高
        Rectangle fixTall = BringInside(tall, main);
        sb.AppendLine("  超高窗口 " + tall + " → 拉回后 " + fixTall);
        chk("窗口比屏幕高时，高度夹到工作区内", fixTall.Height <= main.Height, fixTall.Height + " vs " + main.Height);
        chk("夹完还是落在主屏里", main.Contains(fixTall), fixTall.ToString());

        Rectangle wide = new Rectangle(9000, 9000, 4000, 600);
        Rectangle fixWide = BringInside(wide, main);
        chk("窗口比屏幕宽时，宽度也夹住", fixWide.Width <= main.Width, fixWide.Width + " vs " + main.Width);

        sb.AppendLine();
        sb.AppendLine("== 三、这台机器现在是什么情况 ==");
        try
        {
            Screen[] all = Screen.AllScreens;
            for (int i = 0; i < all.Length; i++)
            {
                Screen s = all[i];
                sb.AppendLine("  屏 " + (i + 1) + "：" + s.DeviceName + "  分辨率 " + s.Bounds.Width + "×" + s.Bounds.Height
                    + "  工作区 " + s.WorkingArea.Width + "×" + s.WorkingArea.Height
                    + (s.Primary ? "（主屏）" : "（副屏）"));
            }
            chk("当前窗口位置够得着（不是在屏幕外）", FitsOnScreens(new Rectangle(Location, Size), ScreenRects()), new Rectangle(Location, Size).ToString());
        }
        catch (Exception ex) { sb.AppendLine("  读屏幕信息失败：" + ex.Message); }

        sb.AppendLine();
        sb.AppendLine("位置部分：通过 " + pass + " 项，失败 " + fail + " 项");
        return sb.ToString();
    }

    // ---------------- 三、自检 B：缩放矩阵用的单步 ----------------

    // 摆成某一组「字号 + 窗口尺寸」，等界面重排完再交回去审计。
    // 自检模式下 SetFontSize 不会写你的 settings.ini。
    internal void ScaleStep(int pt, Size client)
    {
        try { ClientSize = client; } catch { }
        Application.DoEvents();
        SetFontSize(pt);
        Application.DoEvents();
        try { PerformLayout(); } catch { }
        Application.DoEvents();
    }

    internal Size ScaleClientForTest { get { return ClientSize; } }
    internal int ScaleFontForTest { get { return curFontPt; } }
}
