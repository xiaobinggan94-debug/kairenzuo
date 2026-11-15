using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

// 纯预览用的临时程序：把改好的菜单/小窗拉出来截个图，不碰真实数据、不注册热键
class Shot
{
    static string OUT = "";

    [STAThread]
    static void Main(string[] args)
    {
        OUT = args.Length > 0 ? args[0] : AppDomain.CurrentDomain.BaseDirectory;
        Directory.CreateDirectory(OUT);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            ShootMenu("menu_tray", true);
            ShootMenu("menu_more", false);
            ShootListForm("win_history", "HistoryForm", HistoryData());
            ShootWordBook();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(OUT, "error.txt"), ex.ToString());
        }
    }

    static MethodInfo StyleMenuMethod()
    {
        return typeof(QueryForm).GetMethod("StyleMenu", BindingFlags.NonPublic | BindingFlags.Static);
    }

    static ContextMenuStrip BuildMenu(bool tray)
    {
        ContextMenuStrip m = new ContextMenuStrip();
        if (tray)
        {
            m.Items.Add("查一下（Ctrl+空格）");
            ToolStripMenuItem miAuto = new ToolStripMenuItem("鼠标划完自动查（划词弹窗）");
            miAuto.Checked = true;
            m.Items.Add(miAuto);
            m.Items.Add("截图查图里的英文");
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("历史记录");
            m.Items.Add("生词本");
            m.Items.Add("语音朗读看结果");
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("字号 大一点");
            m.Items.Add("字号 小一点");
            m.Items.Add("清空查询缓存");
            m.Items.Add("再看一遍使用说明");
            m.Items.Add("退出");
        }
        else
        {
            m.Items.Add("查一下（Ctrl+空格）");
            m.Items.Add("截图查英文");
            m.Items.Add("历史记录");
            m.Items.Add("生词本");
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("语音设置");
        }
        StyleMenuMethod().Invoke(null, new object[] { m });
        return m;
    }

    static void ShootMenu(string name, bool tray)
    {
        ContextMenuStrip m = BuildMenu(tray);
        m.Show(new Point(120, 80));
        Application.DoEvents();
        Thread.Sleep(220);
        // 让第 5 项处于悬停态，验证浅蓝底 + 深蓝字
        m.Items[4].Select();
        Application.DoEvents();
        Thread.Sleep(260);
        Rectangle r = m.Bounds;
        r.Inflate(10, 10);
        Capture(r, name);
        m.Close();
        Application.DoEvents();
    }

    static void ShootListForm(string name, string nested, List<string[]> data)
    {
        Type t = typeof(QueryForm).GetNestedType(nested, BindingFlags.NonPublic | BindingFlags.Public);
        Form f = (Form)Activator.CreateInstance(t, new object[] { data, null });
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(120, 80);
        f.TopMost = true;
        f.Show();
        Application.DoEvents();
        Thread.Sleep(300);
        FieldInfo lb = t.GetField("lb", BindingFlags.NonPublic | BindingFlags.Instance);
        if (lb != null)
        {
            ListBox box = lb.GetValue(f) as ListBox;
            if (box != null && box.Items.Count > 1) box.SelectedIndex = 1;
        }
        Application.DoEvents();
        Thread.Sleep(320);
        Capture(f.Bounds, name);
        f.Close();
        Application.DoEvents();
    }

    static void ShootWordBook()
    {
        Type t = typeof(QueryForm).GetNestedType("WordBookForm", BindingFlags.NonPublic | BindingFlags.Public);
        Form f = (Form)Activator.CreateInstance(t, new object[] { null });
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(120, 80);
        f.TopMost = true;
        f.Show();
        Application.DoEvents();
        Thread.Sleep(400);
        Capture(f.Bounds, "win_wordbook");
        f.Close();
        Application.DoEvents();
    }

    static void Capture(Rectangle r, string name)
    {
        using (Bitmap bmp = new Bitmap(r.Width, r.Height))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(r.Location, Point.Empty, r.Size);
            bmp.Save(Path.Combine(OUT, name + ".png"), ImageFormat.Png);
        }
    }

    static List<string[]> HistoryData()
    {
        List<string[]> list = new List<string[]>();
        list.Add(new string[] { "10-25 18:40", "the refund will be processed within three business days" });
        list.Add(new string[] { "10-25 17:12", "Please hold on a moment while I check the inventory" });
        list.Add(new string[] { "10-25 15:03", "I apologize for the inconvenience caused by the delay" });
        list.Add(new string[] { "10-24 21:55", "Your package has been dispatched from our warehouse" });
        list.Add(new string[] { "10-24 20:31", "Could you please provide your order number" });
        return list;
    }
}
