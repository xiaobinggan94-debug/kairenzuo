// 饼饼专属中英互译 · WordReview.cs（生词复习小窗）
// 卡片式背单词：正面只给单词 + 音标，心里想过意思了按空格翻面，
// 翻面后按 → 记住了 / ← 还得练。一轮默认 20 个，练完给一句小结。
//
// 数据边界（很重要）：
//   读 —— 生词本数据.tsv（日期 单词 音标 意思 近音），只读不改
//   写 —— 生词本复习进度.tsv（单词 熟练度 上次复习 复习次数），本文件独占
// 也就是说复习时怎么点，都不会动到 生词本.txt / 生词本数据.tsv 一个字节。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Speech.Synthesis;
using System.Text;
using System.Windows.Forms;

partial class QueryForm
{
    // ====== 生词复习小窗 ======
    internal class WordReviewForm : Form
    {
        // 一张卡。熟练度 0=刚见 1=有点印象 2=基本会 3=熟了
        class Card
        {
            public string Word = "";
            public string Phon = "";
            public string Mean = "";
            public string Approx = "";
            public int Level = 0;
            public string Last = "";
            public int Times = 0;
        }

        const int ROUND_SIZE = 20;          // 一轮多少个（太多会累，20 个刚好一口气）
        const int MAX_LEVEL = 3;            // 连对三次就算熟了，之后轮次里排后面少打扰

        static string ProgressFile { get { return Path.Combine(LOG_DIR, "生词本复习进度.tsv"); } }

        List<Card> deck = new List<Card>();  // 全部生词（已排序）
        int cur = -1;                        // 当前第几张
        bool flipped = false;
        bool finished = false;
        int roundTotal = 0;
        int nRemember = 0;
        int nAgain = 0;
        int nSkip = 0;

        Label stat;
        Panel cardPanel;
        Panel bottom;
        RoundButton flipBtn;
        RoundButton againBtn;
        RoundButton rememberBtn;
        RoundButton closeBtn;
        SpeechSynthesizer voice;

        public WordReviewForm(IWin32Window owner)
        {
            Text = "生词复习（卡片背单词）";
            ClientSize = new Size(520, 430);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Microsoft YaHei UI", 9.5f);
            BackColor = Pal.Surface;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            KeyPreview = true;

            stat = new Label();
            stat.Dock = DockStyle.Top;
            stat.Height = 26;
            stat.Padding = new Padding(12, 0, 0, 0);
            stat.TextAlign = ContentAlignment.MiddleLeft;
            stat.ForeColor = Pal.InkHint;
            stat.Text = "正在数生词…";
            Controls.Add(stat);

            bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 54;
            HairlineTop(bottom);
            Controls.Add(bottom);

            flipBtn = MakeDialogButton("看中文意思（空格）", 160, true);
            againBtn = MakeDialogButton("还得练 ←", 100, false);
            rememberBtn = MakeDialogButton("记住了 →", 100, true);
            closeBtn = MakeDialogButton("关掉", 70, false);
            RoundButton[] all = new RoundButton[] { flipBtn, againBtn, rememberBtn, closeBtn };
            foreach (RoundButton b in all)
            {
                b.Top = 13;
                b.TabStop = false;          // 焦点别停在按钮上，空格键才不会变成"再点一次按钮"
                bottom.Controls.Add(b);
            }
            flipBtn.Click += delegate { Flip(); };
            againBtn.Click += delegate { Answer(false); };
            rememberBtn.Click += delegate { Answer(true); };
            closeBtn.DialogResult = DialogResult.Cancel;
            bottom.Resize += delegate { LayoutButtons(); };

            cardPanel = new Panel();
            cardPanel.Dock = DockStyle.Fill;
            cardPanel.BackColor = Pal.Surface;
            cardPanel.Cursor = Cursors.Hand;
            cardPanel.Paint += delegate (object s, PaintEventArgs e) { DrawCard(e.Graphics, cardPanel.ClientRectangle); };
            cardPanel.Click += delegate { Flip(); };
            Controls.Add(cardPanel);
            cardPanel.BringToFront();

            KeyDown += delegate (object s, KeyEventArgs e) {
                if (e.KeyCode == Keys.Escape) { Close(); return; }
                if (e.KeyCode == Keys.Space) { Flip(); e.SuppressKeyPress = true; return; }
                if (e.KeyCode == Keys.Right || e.KeyCode == Keys.D) { Answer(true); e.SuppressKeyPress = true; return; }
                if (e.KeyCode == Keys.Left || e.KeyCode == Keys.A) { Answer(false); e.SuppressKeyPress = true; return; }
                if (e.KeyCode == Keys.F2) { Speak(); e.SuppressKeyPress = true; return; }
                if (e.KeyCode == Keys.S) { Skip(); e.SuppressKeyPress = true; return; }
            };

            BuildRound();
        }

        // ---------- 抽卡 ----------

        // 排序思路：越不熟的越先出来；一样不熟时，越久没碰过的先出来。
        // 这样每天打开都是"最该复习的那批"，不会老是碰到刚背过的词。
        void BuildRound()
        {
            deck.Clear();
            finished = false;
            flipped = false;
            cur = -1;
            roundTotal = 0;
            nRemember = 0;
            nAgain = 0;
            nSkip = 0;

            Dictionary<string, string[]> prog = LoadProgress();
            List<string[]> rows = ReadWordsTsV();
            Dictionary<string, Card> map = new Dictionary<string, Card>();
            for (int i = 0; i < rows.Count; i++)
            {
                string[] r = rows[i];
                if (r == null || r.Length < 2) continue;
                string word = r[1].Trim();
                if (word.Length == 0) continue;
                string key = word.ToLower();
                Card c = new Card();
                c.Word = word;
                c.Phon = r.Length > 2 ? r[2].Trim() : "";
                c.Mean = r.Length > 3 ? r[3].Trim() : "";
                c.Approx = r.Length > 4 ? r[4].Trim() : "";
                c.Last = r[0].Trim();                 // 没复习过时，用"记进生词本那天"当初次排序依据
                map[key] = c;                         // 同一个词查过多次，后加的覆盖前面的
                if (prog.ContainsKey(key))
                {
                    string[] p = prog[key];
                    int lv = 0;
                    if (p.Length > 0) int.TryParse(p[0], out lv);
                    c.Level = Math.Max(0, Math.Min(MAX_LEVEL, lv));
                    if (p.Length > 1 && p[1].Trim().Length > 0) c.Last = p[1].Trim();
                    if (p.Length > 2) int.TryParse(p[2], out c.Times);
                }
            }

            foreach (Card c in map.Values) deck.Add(c);
            deck.Sort(CompareCard);

            roundTotal = Math.Min(ROUND_SIZE, deck.Count);
            if (deck.Count == 0)
            {
                cur = -1;
                stat.Text = "生词本还是空的";
                SetButtons();
                cardPanel.Invalidate();
                return;
            }
            cur = 0;
            stat.Text = "第 1 / " + roundTotal + " 个　·　生词本共 " + deck.Count + " 个";
            SetButtons();
            cardPanel.Invalidate();
        }

        static int CompareCard(Card a, Card b)
        {
            if (a.Level != b.Level) return a.Level.CompareTo(b.Level);
            int x = string.Compare(a.Last, b.Last, StringComparison.Ordinal);
            if (x != 0) return x;
            if (a.Times != b.Times) return a.Times.CompareTo(b.Times);
            return string.Compare(a.Word, b.Word, StringComparison.OrdinalIgnoreCase);
        }

        Card Current
        {
            get
            {
                if (cur < 0 || cur >= deck.Count) return null;
                if (cur >= roundTotal) return null;   // 一轮只练前 ROUND_SIZE 个
                return deck[cur];
            }
        }

        // ---------- 交互 ----------

        void Flip()
        {
            if (finished) { BuildRound(); return; }
            if (Current == null) return;
            flipped = true;
            SetButtons();
            cardPanel.Invalidate();
        }

        void Answer(bool ok)
        {
            if (finished || Current == null) return;
            if (!flipped)
            {
                // 还没看答案就按"记住了"，当作先看答案，不算作答，免得误点把熟练度刷上去
                Flip();
                return;
            }
            Card c = Current;
            if (ok)
            {
                c.Level = Math.Min(MAX_LEVEL, c.Level + 1);
                c.Times++;
                nRemember++;
            }
            else
            {
                c.Level = Math.Max(0, c.Level - 1);
                c.Times++;
                nAgain++;
            }
            c.Last = DateTime.Now.ToString("yyyy-MM-dd");
            SaveProgress();
            Next();
        }

        void Skip()
        {
            if (finished || Current == null) return;
            nSkip++;
            Next();
        }

        void Next()
        {
            flipped = false;
            cur++;
            if (cur >= roundTotal)
            {
                finished = true;
                cur = -1;
                stat.Text = "这一轮 " + roundTotal + " 个：记住 " + nRemember + " 个 · 还得练 " + nAgain + " 个 · 跳过 " + nSkip + " 个";
                SetButtons();
                cardPanel.Invalidate();
                return;
            }
            stat.Text = "第 " + (cur + 1) + " / " + roundTotal + " 个　·　生词本共 " + deck.Count + " 个";
            SetButtons();
            cardPanel.Invalidate();
        }

        void Speak()
        {
            Card c = Current;
            if (c == null) return;
            try
            {
                if (voice == null) { voice = new SpeechSynthesizer(); voice.Rate = 0; }
                voice.SpeakAsyncCancelAll();
                voice.SpeakAsync(c.Word);
            }
            catch { }
        }

        // 按钮随状态换：正面只有"看中文意思"，翻面后才有两个作答键
        // 注意：窗口还没 Show 出来时控件的 Visible 一律返回 false，
        // 所以排布不能读 Visible，得用这两个布尔量来算宽度，否则按钮会叠在一起
        bool btnFlipOn = true;
        bool btnAgainOn = false;

        void SetButtons()
        {
            bool has = Current != null;
            btnFlipOn = !flipped || !has;
            btnAgainOn = has && flipped;
            flipBtn.Visible = btnFlipOn;
            againBtn.Visible = btnAgainOn;
            rememberBtn.Visible = btnAgainOn;
            closeBtn.Visible = true;
            flipBtn.Text = finished ? "再来一轮（空格）" : "看中文意思（空格）";
            flipBtn.Width = finished ? 150 : 160;
            LayoutButtons();
        }

        void LayoutButtons()
        {
            int x = bottom.ClientSize.Width - 14;
            x = Put(closeBtn, x);
            if (btnAgainOn) x = Put(againBtn, x);
            if (btnAgainOn) x = Put(rememberBtn, x);
            if (btnFlipOn) x = Put(flipBtn, x);
        }

        static int Put(Control c, int x)
        {
            x -= c.Width;
            c.Left = x;
            return x - 8;
        }

        // ---------- 绘制 ----------

        void DrawCard(Graphics g, Rectangle bounds)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(bounds.Left + 18, bounds.Top + 10, bounds.Width - 36, bounds.Height - 20);
            if (r.Width < 40 || r.Height < 40) return;
            using (GraphicsPath path = RoundRectPath(r, 14))
            {
                using (SolidBrush bg = new SolidBrush(Color.White)) g.FillPath(bg, path);
                using (Pen p = new Pen(Pal.BorderFaint)) g.DrawPath(p, path);
            }

            if (finished)
            {
                DrawCenterText(g, r, "这一轮练完啦", new Font("Microsoft YaHei UI", 18f, FontStyle.Bold), Pal.Ink, -46);
                DrawCenterText(g, r, "记住 " + nRemember + " 个　还得练 " + nAgain + " 个　跳过 " + nSkip + " 个",
                    new Font("Microsoft YaHei UI", 11.5f), Pal.InkSecondary, -6);
                DrawCenterText(g, r, "按空格或点卡片 = 再来一轮　·　Esc = 关掉",
                    new Font("Microsoft YaHei UI", 9.5f), Pal.InkHint, 34);
                return;
            }

            Card c = Current;
            if (c == null)
            {
                DrawCenterText(g, r, "生词本还是空的", new Font("Microsoft YaHei UI", 15f, FontStyle.Bold), Pal.Ink, -20);
                DrawCenterText(g, r, "查几句带【生词】的内容，词会自动进生词本，回来就能练",
                    new Font("Microsoft YaHei UI", 10f), Pal.InkHint, 18);
                return;
            }

            if (!flipped)
            {
                DrawCenterText(g, r, c.Word, new Font("Segoe UI", 30f, FontStyle.Bold), Pal.Ink, -34);
                if (c.Phon.Length > 0)
                    DrawCenterText(g, r, c.Phon, new Font("Microsoft YaHei UI", 12f), Pal.InkHint, 16);
                DrawCenterText(g, r, "想一下中文意思，按空格看答案　·　F2 读一遍",
                    new Font("Microsoft YaHei UI", 9.5f), Pal.InkMuted4, r.Height / 2 - 42);
            }
            else
            {
                DrawCenterText(g, r, c.Word, new Font("Segoe UI", 20f, FontStyle.Bold), Pal.Ink, -80);
                if (c.Phon.Length > 0)
                    DrawCenterText(g, r, c.Phon, new Font("Microsoft YaHei UI", 10.5f), Pal.InkHint, -46);

                int lineY = r.Top + r.Height / 2 - 14;
                using (Pen p = new Pen(Pal.Hair))
                    g.DrawLine(p, r.Left + 46, lineY, r.Right - 46, lineY);

                string mean = c.Mean.Length > 0 ? c.Mean : "（这个词当时没记下意思）";
                DrawCenterText(g, r, mean, new Font("Microsoft YaHei UI", 13f), Pal.Ink, 4);
                if (c.Approx.Length > 0)
                    DrawCenterText(g, r, "近音记法：" + c.Approx, new Font("Microsoft YaHei UI", 10f), Pal.InkMuted4, 46);

                DrawCenterText(g, r, "← 还得练　→ 记住了", new Font("Microsoft YaHei UI", 9.5f), Pal.InkHint, r.Height / 2 - 26);
            }
        }

        // dy 是相对卡片竖直中线的偏移，正数往下
        static void DrawCenterText(Graphics g, Rectangle card, string text, Font f, Color color, int dy)
        {
            int h = 44;
            int cy = card.Top + card.Height / 2 + dy;
            Rectangle box = new Rectangle(card.Left + 24, cy - h / 2, card.Width - 48, h);
            TextRenderer.DrawText(g, text, f, box, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        }

        static GraphicsPath RoundRectPath(Rectangle r, int radius)
        {
            int d = radius * 2;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ---------- 进度文件 ----------

        // 进度一行一个词：单词 \t 熟练度 \t 上次复习日期 \t 复习次数
        static Dictionary<string, string[]> LoadProgress()
        {
            Dictionary<string, string[]> d = new Dictionary<string, string[]>();
            try
            {
                if (!File.Exists(ProgressFile)) return d;
                string all = "";
                using (StreamReader sr = new StreamReader(ProgressFile, Encoding.UTF8, true))
                    all = sr.ReadToEnd();
                foreach (string raw in all.Replace("\r\n", "\n").Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0) continue;
                    string[] p = line.Split('\t');
                    if (p.Length < 2 || p[0].Trim().Length == 0) continue;
                    d[p[0].Trim().ToLower()] = p;
                }
            }
            catch { }
            return d;
        }

        void SaveProgress()
        {
            if (dryRun) return;      // 自检模式：只走状态，不落盘
            try
            {
                Dictionary<string, string[]> prog = LoadProgress();
                foreach (Card c in deck)
                {
                    if (c.Times <= 0) continue;      // 没复习过的词不必占一行
                    prog[c.Word.ToLower()] = new string[] {
                        c.Level.ToString(), c.Last, c.Times.ToString() };
                }
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, string[]> kv in prog)
                {
                    string[] v = kv.Value;
                    sb.Append(kv.Key);
                    sb.Append('\t');
                    sb.Append(v.Length > 0 ? v[0] : "0");
                    sb.Append('\t');
                    sb.Append(v.Length > 1 ? v[1] : "");
                    sb.Append('\t');
                    sb.Append(v.Length > 2 ? v[2] : "0");
                    sb.AppendLine();
                }
                Directory.CreateDirectory(LOG_DIR);
                File.WriteAllText(ProgressFile, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { if (voice != null) { voice.Dispose(); voice = null; } } catch { }
            base.OnFormClosed(e);
        }

        // ---------- 自检（只在 --audit 里跑）----------
        // 把一整轮从头点到尾：翻面 → 记住 → 翻面 → 还得练 …… 确认状态机不会错位、计数不打架。
        // dryRun = true 保证一个字都不写进进度文件，自检不会污染饼饼真实的复习记录。
        internal bool dryRun;

        internal void SelfTest(StringBuilder sb)
        {
            dryRun = true;
            try
            {
                sb.AppendLine("  复习卡：生词 " + deck.Count + " 个，这一轮 " + roundTotal + " 个，起始熟练度 " + (Current != null ? Current.Level : -1));
                if (roundTotal == 0)
                {
                    sb.AppendLine("  复习卡：生词本为空，只验证了空状态，没跑流程");
                    return;
                }
                int guard = 0;
                bool levelOk = true;
                while (Current != null && guard < 500)
                {
                    guard++;
                    Card c = Current;
                    int lvBefore = c.Level;
                    bool wantRemember = (guard % 3) != 0;      // 三分之二记住，三分之一重练
                    if (!flipped) Flip();
                    if (!flipped) levelOk = false;
                    Answer(wantRemember);
                    if (wantRemember && c.Level < MAX_LEVEL && c.Level != lvBefore + 1) levelOk = false;
                    if (!wantRemember && lvBefore > 0 && c.Level != lvBefore - 1) levelOk = false;
                    if (c.Times <= 0) levelOk = false;
                }
                sb.AppendLine("  复习卡：点了 " + guard + " 次，记住 " + nRemember + " · 重练 " + nAgain
                    + " · 结束态 " + finished + " · 熟练度增减 " + (levelOk ? "正常" : "异常"));
                sb.AppendLine("  复习卡：结束语 " + stat.Text);
                if (!finished || guard == 0 || (nRemember + nAgain) != roundTotal)
                    sb.AppendLine("  复习卡：!! 流程对不上（应为 " + roundTotal + " 个全部作答）");
            }
            catch (Exception ex)
            {
                sb.AppendLine("  复习卡：!! 自检抛异常 " + ex.GetType().Name + " " + ex.Message);
            }
            finally { dryRun = false; }
        }
    }
}
