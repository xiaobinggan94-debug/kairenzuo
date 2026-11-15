// 软件名：谁下载谁自己起名，不用改代码
//   取名字的优先级：settings.ini 里的「软件名」→ 编译时内置的默认名（APP_TITLE）
//   首启那张卡片会让用户填一次；想改随时从托盘「更多」菜单把那张卡再叫出来
//   只影响"显示出来的名字"，绝不碰单实例 Mutex、窗口消息名、数据目录（改了会串数据）
using System;
using System.Text;

partial class QueryForm
{
    internal const string APP_NAME_KEY  = "软件名";
    internal const string APP_NAME_HINT = "中英互译速查";   // 首启卡片输入框预填的中性名
    internal const int    APP_NAME_MAX  = 16;

    // 当前生效的名字：settings 里有就用它，没有就用编译默认
    internal static string AppTitle
    {
        get
        {
            string n = CleanAppName(GetSet(APP_NAME_KEY, ""));
            return n.Length > 0 ? n : APP_TITLE;
        }
    }

    // 洗一遍：换行/制表符/控制字符一律去掉，首尾空白砍掉，超长截断
    internal static string CleanAppName(string raw)
    {
        if (raw == null) return "";
        StringBuilder sb = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c < ' ') continue;              // 换行、制表符、其它控制字符都别混进来
            sb.Append(c);
        }
        string s = sb.ToString().Trim();
        if (s.Length > APP_NAME_MAX) s = s.Substring(0, APP_NAME_MAX);
        return s;
    }

    // 保存名字（空 = 恢复内置默认名）；自检模式只改内存，不碰真实 settings.ini
    internal static void SaveAppName(string raw)
    {
        string n = CleanAppName(raw);
        if (selfTestMode) SetSetQuiet(APP_NAME_KEY, n);
        else SetSet(APP_NAME_KEY, n);
    }

    // 名字改了之后，把显示名字的地方一次性刷新（不用重启程序）
    internal void ApplyAppName()
    {
        string n = AppTitle;
        try { Text = n; } catch { }
        try { if (headPanel != null) { headPanel.TitleText = n; headPanel.Invalidate(); } } catch { }
        try { if (tray != null) tray.Text = n + "：选中文字按 " + HotkeyLabel; } catch { }
    }
}
