// Theme.cs —— v2.1 第一步：主题 token 化（只搬家，不换皮）
//
// 改造前：颜色散在 MainForm.cs 里 117 处，直接写字面量，想换深色皮肤得全文件翻一遍。
// 改造后：所有颜色统一从 Palette 取，代码里只剩下「角色名」（底、面、正文、强调…）。
//
// ★ 关键约束：本步每个 token 的默认值 = 改造前硬编码的原值，
//   所以外观一个像素都不该变（用 --audit 控件树 + 截图逐像素核对）。
// ★ 下一步只要给 Palette 加第二套（暗色）取值，再递归重设一遍控件颜色，就能整体换皮，
//   不用再碰业务代码。

using System.Drawing;

// 调色板：字段名 = 角色，注释 = 用在哪
class Palette
{
    // —— 底 / 面 ——
    public Color BgPage = C(247, 247, 248);        // 窗口底色、分区底色
    public Color Surface = Color.White;            // 卡片、输入容器、弹出层、次级按钮底
    public Color InputBg = C(250, 250, 251);       // 输入框内底
    public Color Shade = Color.Black;              // 全屏遮罩（截图取景）
    public Color ShadowInk = C(255, 92, 96, 108);  // 伪阴影色（带 alpha）

    // —— 标题栏（深色头）——
    public Color HeadBg = C(52, 55, 64);           // 标题栏底
    public Color HeadFg = C(238, 238, 242);        // 标题栏主文字/关闭按钮
    public Color HeadFgSub = C(196, 201, 212);     // 标题栏次级图标（最大化）
    public Color HeadFgTitle = C(241, 242, 247);   // 标题栏标题文字

    // —— 文字 ——
    public Color Ink = C(28, 28, 30);              // 正文
    public Color InkSecondary = C(52, 52, 56);     // 次级按钮文字
    public Color InkMuted = C(126, 126, 132);      // 弱化说明
    public Color InkMuted2 = C(120, 120, 126);     // 列表副行
    public Color InkMuted3 = C(140, 140, 146);     // 列表时间戳
    public Color InkMuted4 = C(124, 124, 130);     // 生词本释义
    public Color InkFaint = C(152, 152, 158);      // 输入框占位提示
    public Color InkHint = C(110, 110, 116);       // 底部提示、统计
    public Color InkDisabled = C(170, 170, 176);   // 禁用态文字
    public Color InkOnSoft = C(74, 106, 140);      // 强调底色上的次级文字
    public Color OnAccent = Color.White;           // 强调色按钮上的文字

    // —— 强调色（蓝）——
    public Color Accent = C(19, 96, 178);          // 主强调：按钮填充、选中条、链接
    public Color AccentHover = C(30, 112, 198);    // 强调色悬停
    public Color AccentSoft = C(232, 240, 252);    // 强调浅底（选中行、浅按钮）
    public Color AccentSoftHover = C(214, 228, 247); // 强调浅底·悬停/按下
    public Color AccentSoftHoverAlt = C(214, 227, 247); // 强调浅底·悬停（原始值差 1，单列保留以做到像素零变化）
    public Color AccentTint = C(240, 244, 252);    // 次级按钮悬停

    // —— 线 / 边 ——
    public Color Hair = C(232, 233, 238);          // 发丝分隔线
    public Color Hair2 = C(232, 232, 236);         // 底部分隔线
    public Color Edge = C(216, 218, 224);          // 卡片描边
    public Color BorderFaint = C(240, 241, 245);   // 列表项描边
    public Color InputBorder = C(222, 224, 230);   // 输入框描边
    public Color BtnBorder = C(226, 227, 233);     // 描边按钮（常态）
    public Color BtnBorder2 = C(220, 224, 234);    // 描边按钮内圈
    public Color BtnBorderHover = C(202, 210, 226); // 描边按钮·悬停

    // —— 状态 ——
    public Color DisabledFill = C(238, 240, 244);  // 禁用填充
    public Color DisabledBorder = C(236, 238, 242); // 禁用描边
    public Color ToneCasualBg = C(233, 248, 236);  // 语气「随意」选中底
    public Color WarnBg = C(255, 243, 222);        // 「近似」提示底
    public Color WarnEdge = C(230, 182, 116);      // 「近似」提示描边/图标
    public Color Danger = C(196, 32, 32);          // 「注意」红字
    public Color Mark = Color.OrangeRed;           // 取景框角标
    public Color GrayMark = C(210, 210, 214);      // 未激活角标

    // 便捷构造：C(r,g,b) / C(a,r,g,b)
    static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }
    static Color C(int a, int r, int g, int b) { return Color.FromArgb(a, r, g, b); }
}

partial class QueryForm
{
    // 全局唯一的调色板实例；以后换皮肤 = 换掉它 + 递归重设控件颜色
    internal static Palette Pal = new Palette();
}
