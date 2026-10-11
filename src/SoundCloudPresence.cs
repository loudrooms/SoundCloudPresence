// SoundCloud Presence - the tray app.
// Only starts/stops/monitors SoundCloudPresence.ps1 and shows what it saw.
// Build with build.ps1, or: csc /target:winexe /reference:System.Windows.Forms.dll
//   /reference:System.Drawing.dll /reference:Microsoft.CSharp.dll src\SoundCloudPresence.cs

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("SoundCloud Presence")]
[assembly: AssemblyProduct("SoundCloud Presence")]
[assembly: AssemblyVersion("2.0.0")]
[assembly: AssemblyFileVersion("2.0.0")]

namespace SoundCloudPresence
{
    // ---------------------------------------------------------------- theme --

    static class Theme
    {
        public static readonly Color Bg       = Color.FromArgb(22, 23, 26);
        public static readonly Color Card     = Color.FromArgb(38, 40, 45);
        public static readonly Color CardHi   = Color.FromArgb(50, 53, 59);
        public static readonly Color Input    = Color.FromArgb(30, 32, 36);
        public static readonly Color Border   = Color.FromArgb(64, 68, 75);
        public static readonly Color Txt      = Color.FromArgb(235, 237, 240);
        public static readonly Color Muted    = Color.FromArgb(142, 148, 157);
        public static readonly Color Orange   = Color.FromArgb(255, 85, 0);
        public static readonly Color OrangeHi = Color.FromArgb(255, 112, 34);
        public static readonly Color Green    = Color.FromArgb(58, 175, 99);
        public static readonly Color Red      = Color.FromArgb(214, 71, 71);
        public static readonly Color Disabled = Color.FromArgb(54, 57, 62);
        public static readonly Color DisabledTxt = Color.FromArgb(102, 107, 114);

        public static GraphicsPath Rounded(Rectangle r, int radius)
        {
            GraphicsPath gp = new GraphicsPath();
            int d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            gp.AddArc(r.X, r.Y, d, d, 180, 90);
            gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        public static Icon BuildIcon()
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float[] heights = { 0.34f, 0.66f, 0.95f, 0.72f, 0.95f, 0.45f };
                float barW = 3.4f, gap = 1.5f;
                float total = heights.Length * barW + (heights.Length - 1) * gap;
                float x = (32 - total) / 2f, topMax = 26, bottom = 28;
                using (LinearGradientBrush br = new LinearGradientBrush(
                    new RectangleF(x, 2, total, 28), Orange, Color.FromArgb(255, 138, 63), 90f))
                {
                    for (int i = 0; i < heights.Length; i++)
                    {
                        float h = topMax * heights[i];
                        RectangleF bar = new RectangleF(x + i * (barW + gap), bottom - h, barW, h);
                        using (GraphicsPath gp = Rounded(Rectangle.Round(bar), 2))
                            g.FillPath(br, gp);
                    }
                }
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    // ------------------------------------------------------------- controls --
    // custom painted, rounded. no Region clipping - region edges are aliased
    // and show up as a dark fringe.

    class RoundButton : Button
    {
        Color _hover;
        Color _border = Color.Empty;
        bool  _hot;
        int   _radius = 10;

        public RoundButton(string text, Color back, Color fore, Color hover)
        {
            Text = text;
            ForeColor = fore;
            BackColor = back;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            TabStop = false;
            _hover = hover;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public Color BorderColor { get { return _border; } set { _border = value; Invalidate(); } }

        protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : BackColor);

            Color back, fore;
            if (!Enabled) { back = Theme.Disabled; fore = Theme.DisabledTxt; }
            else if (_hot) { back = _hover; fore = ForeColor; }
            else { back = BackColor; fore = ForeColor; }

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath gp = Theme.Rounded(r, _radius))
            using (SolidBrush sb = new SolidBrush(back))
            {
                g.FillPath(sb, gp);
                if (_border != Color.Empty)
                    using (Pen p = new Pen(_border, 1f)) g.DrawPath(p, gp);
            }

            TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                    TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fore, flags);
        }
    }

    class Pill : Control
    {
        Color _dot = Theme.Muted;

        public Pill(string text, Color dot)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = new Font("Segoe UI", 8.25f);
            Height = 26;
            Set(text, dot);
        }

        public void Set(string text, Color dot)
        {
            _dot = dot;
            Text = text;
            Size needed = TextRenderer.MeasureText(text, Font, new Size(400, 26),
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            Width = Math.Max(72, needed.Width + 30);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Color.Black);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath gp = Theme.Rounded(r, Height / 2))
            using (SolidBrush sb = new SolidBrush(Theme.Card))
                g.FillPath(sb, gp);

            using (SolidBrush dot = new SolidBrush(_dot))
                g.FillEllipse(dot, 10, Height / 2 - 4, 8, 8);

            Rectangle textArea = new Rectangle(24, 0, Width - 26, Height);
            TextRenderer.DrawText(g, Text, Font, textArea, Theme.Txt,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }

    class RoundedPanel : Panel
    {
        int _radius = 14;

        public RoundedPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Parent != null ? Parent.BackColor : Color.Black);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath gp = Theme.Rounded(r, _radius))
            using (SolidBrush sb = new SolidBrush(BackColor))
                e.Graphics.FillPath(sb, gp);
        }
    }

    // album art tile, placeholder note when there is no cover
    class CoverTile : Control
    {
        Bitmap _image;

        public CoverTile()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public void SetImage(Bitmap img)
        {
            if (_image != img)
            {
                Bitmap old = _image;
                _image = img;
                if (old != null) old.Dispose();
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);

            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath gp = Theme.Rounded(r, 10))
            {
                using (SolidBrush sb = new SolidBrush(Theme.CardHi))
                    g.FillPath(sb, gp);

                if (_image != null)
                {
                    g.SetClip(gp);
                    int side = Math.Min(_image.Width, _image.Height);
                    using (ImageAttributes ia = new ImageAttributes())
                        g.DrawImage(_image,
                            new Rectangle(0, 0, Width, Height),
                            (_image.Width - side) / 2, (_image.Height - side) / 2, side, side,
                            GraphicsUnit.Pixel, ia);
                    g.ResetClip();
                }
                else
                {
                    using (Font f = new Font("Segoe UI Symbol", 20f))
                    using (SolidBrush tb = new SolidBrush(Theme.Muted))
                    {
                        StringFormat sf = new StringFormat();
                        sf.Alignment = StringAlignment.Center;
                        sf.LineAlignment = StringAlignment.Center;
                        g.DrawString("\u266B", f, tb, new RectangleF(0, 0, Width, Height), sf);
                    }
                }
            }
        }
    }

    // dark checkbox, the system one is bright
    class CheckToggle : CheckBox
    {
        bool _hot;

        public CheckToggle(string text)
        {
            Text = text;
            Font = new Font("Segoe UI", 9f);
            ForeColor = Theme.Txt;
            Cursor = Cursors.Hand;
            AutoSize = false;
            Height = 24;
            TabStop = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Card);

            Rectangle box = new Rectangle(0, (Height - 18) / 2, 18, 18);
            using (GraphicsPath gp = Theme.Rounded(box, 5))
            {
                if (Checked)
                {
                    using (SolidBrush sb = new SolidBrush(_hot ? Theme.OrangeHi : Theme.Orange))
                        g.FillPath(sb, gp);
                    using (Font f = new Font("Segoe UI Symbol", 11f, FontStyle.Bold))
                    using (SolidBrush wb = new SolidBrush(Color.White))
                    {
                        StringFormat sf = new StringFormat();
                        sf.Alignment = StringAlignment.Center;
                        sf.LineAlignment = StringAlignment.Center;
                        g.DrawString("\u2713", f, wb, new RectangleF(box.X, box.Y - 1, box.Width, box.Height), sf);
                    }
                }
                else
                {
                    using (SolidBrush sb = new SolidBrush(_hot ? Theme.Border : Theme.Input))
                        g.FillPath(sb, gp);
                    using (Pen p = new Pen(Theme.Border, 1f)) g.DrawPath(p, gp);
                }
            }

            Rectangle text = new Rectangle(26, 0, Width - 26, Height);
            TextRenderer.DrawText(g, Text, Font, text, Checked ? Theme.Txt : Theme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }

    // ------------------------------------------------------------- main app --

    public class MainForm : Form
    {
        // nothing bundled, comes from config.txt (see README)
        const string DefaultClientId = "";

        static readonly string Dir        = Application.StartupPath;
        static readonly string Service    = Path.Combine(Dir, "SoundCloudPresence.ps1");
        static readonly string ConfigPath = Path.Combine(Dir, "config.txt");
        static readonly string PidFile    = Path.Combine(Path.GetTempPath(), "SoundCloudPresence.pid");
        static readonly string StateFile  = Path.Combine(Path.GetTempPath(), "SoundCloudPresence.state");
        static readonly string LogPath    = Path.Combine(Path.GetTempPath(), "SoundCloudPresence.log");
        static readonly string StartupLnk = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup), "SoundCloudPresence.lnk");

        static readonly Font TitleFont      = new Font("Segoe UI", 12.5f, FontStyle.Bold);
        static readonly Font TitleFontIdle  = new Font("Segoe UI", 10.5f, FontStyle.Bold);

        Pill  pillService, pillDiscord;
        RoundedPanel card;
        CoverTile cover;
        Label lblTitle, lblArtist, lblSub;
        RoundButton btnStart, btnStop, btnSettings, btnLog, btnAuto;
        NotifyIcon tray;
        System.Windows.Forms.Timer timer;

        // settings view
        RoundedPanel panelSettings;
        TextBox txtClientId, txtPoll, txtCover, txtAsset;
        CheckToggle chkBrowser, chkNoCover, chkHidePaused, chkVerbose;
        RoundButton btnActivity, btnSave, btnBack;
        Label lblSave;
        static readonly string[] ActivityNames = { "Listening", "Playing", "Watching" };
        static readonly int[] ActivityValues = { 2, 0, 3 };
        int _actIdx;
        DateTime _feedbackUntil = DateTime.MinValue;

        string _coverFor = "";
        bool   _coverKnown;
        bool   _demoMode;
        bool   _exiting, _allowVisible = true;

        public MainForm(bool startHidden)
        {
            Text = "SoundCloud Presence";
            ClientSize = new Size(410, 420);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            BackColor = Theme.Bg;
            ForeColor = Theme.Txt;
            Icon = Theme.BuildIcon();
            if (startHidden) _allowVisible = false;

            PictureBox logo = new PictureBox();
            logo.Size = new Size(34, 34);
            logo.Location = new Point(20, 18);
            logo.SizeMode = PictureBoxSizeMode.Zoom;
            logo.BackColor = Color.Transparent;
            logo.Image = Icon.ToBitmap();
            Controls.Add(logo);

            Label header = new Label();
            header.Text = "SoundCloud Presence";
            header.Font = new Font("Segoe UI", 12.5f, FontStyle.Bold);
            header.ForeColor = Theme.Txt;
            header.BackColor = Color.Transparent;
            header.AutoSize = true;
            header.Location = new Point(62, 16);
            Controls.Add(header);

            Label sub = new Label();
            sub.Text = "shows what you are listening to on Discord";
            sub.Font = new Font("Segoe UI", 8.25f);
            sub.ForeColor = Theme.Muted;
            sub.BackColor = Color.Transparent;
            sub.AutoSize = true;
            sub.Location = new Point(63, 38);
            Controls.Add(sub);

            pillService = new Pill("Service stopped", Theme.Muted);
            pillService.Location = new Point(20, 66);
            Controls.Add(pillService);

            pillDiscord = new Pill("Discord unknown", Theme.Muted);
            pillDiscord.Location = new Point(170, 66);
            Controls.Add(pillDiscord);

            BuildMainView();
            BuildSettingsView();

            tray = new NotifyIcon();
            tray.Icon = Icon;
            tray.Text = "SoundCloud Presence";
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowFromTray(); };
            ContextMenu menu = new ContextMenu();
            menu.MenuItems.Add("Show", delegate { ShowFromTray(); });
            menu.MenuItems.Add("Start", delegate { StartService(); });
            menu.MenuItems.Add("Stop", delegate { StopService(); });
            menu.MenuItems.Add("-");
            menu.MenuItems.Add("Exit", delegate { _exiting = true; Close(); });
            tray.ContextMenu = menu;

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 500;
            timer.Tick += delegate { RefreshUi(); };
            timer.Start();

            RefreshUi();
        }

        void BuildMainView()
        {
            card = new RoundedPanel();
            card.BackColor = Theme.Card;
            card.Location = new Point(20, 104);
            card.Size = new Size(370, 152);
            Controls.Add(card);

            cover = new CoverTile();
            cover.Size = new Size(96, 96);
            cover.Location = new Point(18, 28);
            card.Controls.Add(cover);

            lblTitle = new Label();
            lblTitle.Text = "Nothing is playing";
            lblTitle.Font = TitleFontIdle;
            lblTitle.ForeColor = Theme.Muted;
            lblTitle.BackColor = Color.Transparent;
            lblTitle.AutoEllipsis = true;
            lblTitle.Location = new Point(132, 30);
            lblTitle.Size = new Size(222, 26);
            card.Controls.Add(lblTitle);

            lblArtist = new Label();
            lblArtist.Font = new Font("Segoe UI", 10.5f);
            lblArtist.ForeColor = Theme.Muted;
            lblArtist.BackColor = Color.Transparent;
            lblArtist.AutoEllipsis = true;
            lblArtist.Location = new Point(132, 58);
            lblArtist.Size = new Size(222, 22);
            card.Controls.Add(lblArtist);

            lblSub = new Label();
            lblSub.Font = new Font("Segoe UI", 9f);
            lblSub.ForeColor = Theme.Muted;
            lblSub.BackColor = Color.Transparent;
            lblSub.AutoEllipsis = true;
            lblSub.Location = new Point(132, 86);
            lblSub.Size = new Size(222, 20);
            card.Controls.Add(lblSub);

            btnStart    = new RoundButton("Start", Theme.Orange, Color.White, Theme.OrangeHi);
            btnStop     = new RoundButton("Stop", Theme.Bg, Theme.Txt, Theme.CardHi);
            btnSettings = new RoundButton("Settings", Theme.Bg, Theme.Muted, Theme.CardHi);
            btnLog      = new RoundButton("Open log", Theme.Bg, Theme.Muted, Theme.CardHi);
            btnAuto     = new RoundButton("Start with Windows: Off", Theme.Bg, Theme.Muted, Theme.CardHi);
            btnStop.BorderColor = Theme.Border;
            btnSettings.BorderColor = Theme.Border;
            btnLog.BorderColor = Theme.Border;
            btnAuto.BorderColor = Theme.Border;

            btnStart.Location    = new Point(20, 272);  btnStart.Size    = new Size(176, 44);
            btnStop.Location     = new Point(214, 272); btnStop.Size     = new Size(176, 44);
            btnSettings.Location = new Point(20, 326);  btnSettings.Size = new Size(176, 34);
            btnLog.Location      = new Point(214, 326); btnLog.Size      = new Size(176, 34);
            btnAuto.Location     = new Point(20, 368);  btnAuto.Size     = new Size(370, 34);

            btnStart.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
            btnStop.Font = new Font("Segoe UI", 9.5f);
            btnSettings.Font = btnLog.Font = btnAuto.Font = new Font("Segoe UI", 8.75f);

            btnStart.Click    += delegate { StartService(); };
            btnStop.Click     += delegate { StopService(); };
            btnSettings.Click += delegate { ShowSettings(); };
            btnLog.Click      += delegate { OpenInNotepad(LogPath); };
            btnAuto.Click     += delegate { ToggleAutostart(); };

            Controls.Add(btnStart);
            Controls.Add(btnStop);
            Controls.Add(btnSettings);
            Controls.Add(btnLog);
            Controls.Add(btnAuto);
        }

        void BuildSettingsView()
        {
            panelSettings = new RoundedPanel();
            panelSettings.BackColor = Theme.Card;
            panelSettings.Location = new Point(20, 104);
            panelSettings.Size = new Size(370, 298);
            panelSettings.Visible = false;
            Controls.Add(panelSettings);

            Label title = new Label();
            title.Text = "Settings";
            title.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
            title.ForeColor = Theme.Txt;
            title.BackColor = Color.Transparent;
            title.AutoSize = true;
            title.Location = new Point(16, 12);
            panelSettings.Controls.Add(title);

            Label capId = Caption("Discord application id  -  free at discord.com/developers/applications");
            capId.Location = new Point(16, 44);
            panelSettings.Controls.Add(capId);

            txtClientId = Input();
            txtClientId.Location = new Point(16, 60);
            txtClientId.Size = new Size(338, 23);
            panelSettings.Controls.Add(txtClientId);

            btnActivity = new RoundButton("Activity: Listening", Theme.Input, Theme.Txt, Theme.CardHi);
            btnActivity.BorderColor = Theme.Border;
            btnActivity.Font = new Font("Segoe UI", 8.75f);
            btnActivity.Location = new Point(16, 96);
            btnActivity.Size = new Size(160, 30);
            btnActivity.Click += delegate
            {
                _actIdx = (_actIdx + 1) % ActivityNames.Length;
                UpdateActivityButton();
            };
            panelSettings.Controls.Add(btnActivity);

            Label capPoll = Caption("Poll interval, seconds");
            capPoll.Location = new Point(194, 84);
            panelSettings.Controls.Add(capPoll);

            txtPoll = Input();
            txtPoll.Location = new Point(194, 100);
            txtPoll.Size = new Size(160, 23);
            txtPoll.TextAlign = HorizontalAlignment.Center;
            panelSettings.Controls.Add(txtPoll);

            Label capCover = Caption("Cover max size, px");
            capCover.Location = new Point(16, 136);
            panelSettings.Controls.Add(capCover);

            txtCover = Input();
            txtCover.Location = new Point(16, 152);
            txtCover.Size = new Size(160, 23);
            txtCover.TextAlign = HorizontalAlignment.Center;
            panelSettings.Controls.Add(txtCover);

            Label capAsset = Caption("Asset key (optional)");
            capAsset.Location = new Point(194, 136);
            panelSettings.Controls.Add(capAsset);

            txtAsset = Input();
            txtAsset.Location = new Point(194, 152);
            txtAsset.Size = new Size(160, 23);
            panelSettings.Controls.Add(txtAsset);

            chkBrowser = new CheckToggle("Browser only");
            chkBrowser.Location = new Point(16, 190);
            chkBrowser.Size = new Size(160, 24);
            panelSettings.Controls.Add(chkBrowser);

            chkNoCover = new CheckToggle("No cover art");
            chkNoCover.Location = new Point(194, 190);
            chkNoCover.Size = new Size(160, 24);
            panelSettings.Controls.Add(chkNoCover);

            chkHidePaused = new CheckToggle("Hide when paused");
            chkHidePaused.Location = new Point(16, 216);
            chkHidePaused.Size = new Size(170, 24);
            panelSettings.Controls.Add(chkHidePaused);

            chkVerbose = new CheckToggle("Verbose log");
            chkVerbose.Location = new Point(194, 216);
            chkVerbose.Size = new Size(150, 24);
            panelSettings.Controls.Add(chkVerbose);

            lblSave = new Label();
            lblSave.Font = new Font("Segoe UI", 8.25f);
            lblSave.ForeColor = Theme.Muted;
            lblSave.BackColor = Color.Transparent;
            lblSave.AutoSize = false;
            lblSave.TextAlign = ContentAlignment.MiddleCenter;
            lblSave.Location = new Point(16, 244);
            lblSave.Size = new Size(338, 16);
            panelSettings.Controls.Add(lblSave);

            btnSave = new RoundButton("Save & apply", Theme.Orange, Color.White, Theme.OrangeHi);
            btnSave.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            btnSave.Location = new Point(16, 262);
            btnSave.Size = new Size(160, 30);
            btnSave.Click += delegate { SaveSettings(); };
            panelSettings.Controls.Add(btnSave);

            btnBack = new RoundButton("Back", Theme.Input, Theme.Muted, Theme.CardHi);
            btnBack.BorderColor = Theme.Border;
            btnBack.Font = new Font("Segoe UI", 9.5f);
            btnBack.Location = new Point(194, 262);
            btnBack.Size = new Size(160, 30);
            btnBack.Click += delegate { ShowMain(); };
            panelSettings.Controls.Add(btnBack);
        }

        static Label Caption(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = new Font("Segoe UI", 8f);
            l.ForeColor = Theme.Muted;
            l.BackColor = Color.Transparent;
            l.AutoSize = true;
            return l;
        }

        static TextBox Input()
        {
            TextBox t = new TextBox();
            t.Font = new Font("Segoe UI", 9f);
            t.ForeColor = Theme.Txt;
            t.BackColor = Theme.Input;
            t.BorderStyle = BorderStyle.FixedSingle;
            return t;
        }

        void UpdateActivityButton()
        {
            btnActivity.Text = "Activity: " + ActivityNames[_actIdx];
            btnActivity.Invalidate();
        }

        void ShowSettings()
        {
            LoadSettings();
            lblSave.Text = "";
            card.Visible = false;
            btnStart.Visible = btnStop.Visible = btnSettings.Visible = btnLog.Visible = btnAuto.Visible = false;
            panelSettings.Visible = true;
        }

        void ShowMain()
        {
            panelSettings.Visible = false;
            card.Visible = true;
            btnStart.Visible = btnStop.Visible = btnSettings.Visible = btnLog.Visible = btnAuto.Visible = true;
            RefreshUi();
        }

        // ---------------------------------------------------- settings i/o ---

        static Dictionary<string, string> ReadConfig()
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(ConfigPath))
                {
                    foreach (string line in File.ReadAllLines(ConfigPath))
                    {
                        string t = line.Trim();
                        if (t.Length == 0 || t.StartsWith("#")) continue;
                        int i = t.IndexOf('=');
                        if (i <= 0) continue;
                        d[t.Substring(0, i).Trim().ToUpperInvariant()] = t.Substring(i + 1).Trim().Trim('"');
                    }
                }
            }
            catch { }
            return d;
        }

        static bool ReadBool(Dictionary<string, string> d, string key, bool def)
        {
            string v;
            if (!d.TryGetValue(key, out v)) return def;
            v = (v ?? "").ToLowerInvariant();
            return v == "1" || v == "true" || v == "yes" || v == "on";
        }

        void LoadSettings()
        {
            Dictionary<string, string> d = ReadConfig();
            string v;
            d.TryGetValue("CLIENT_ID", out v);
            txtClientId.Text = (string.IsNullOrEmpty(v) || v == "PASTE_YOUR_APP_ID") ? "" : v;
            d.TryGetValue("POLL_SECONDS", out v);
            txtPoll.Text = string.IsNullOrEmpty(v) ? "2" : v;
            d.TryGetValue("COVER_MAX_SIZE", out v);
            txtCover.Text = string.IsNullOrEmpty(v) ? "256" : v;
            d.TryGetValue("ASSET_KEY", out v);
            txtAsset.Text = v ?? "";
            chkBrowser.Checked    = ReadBool(d, "BROWSER_ONLY", false);
            chkNoCover.Checked    = ReadBool(d, "NO_COVER", false);
            chkHidePaused.Checked = ReadBool(d, "HIDE_WHEN_PAUSED", false);
            chkVerbose.Checked    = ReadBool(d, "VERBOSE", false);
            _actIdx = 0;
            if (d.TryGetValue("ACTIVITY_TYPE", out v))
            {
                int at;
                if (int.TryParse(v, out at))
                    for (int i = 0; i < ActivityValues.Length; i++)
                        if (ActivityValues[i] == at) { _actIdx = i; break; }
            }
            UpdateActivityButton();
        }

        void SaveSettings()
        {
            double poll;
            if (!double.TryParse(txtPoll.Text.Trim(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out poll) || poll <= 0 || poll > 60)
            { Fail("Poll interval must be a number between 0.1 and 60"); return; }

            int coverSize;
            if (!int.TryParse(txtCover.Text.Trim(), out coverSize) || coverSize < 64 || coverSize > 1024)
            { Fail("Cover size must be between 64 and 1024"); return; }

            string clientId = txtClientId.Text.Trim();
            if (clientId.Length > 0 && (clientId.Length < 10 ||
                clientId.IndexOfAny("0123456789".ToCharArray()) < 0))
            { Fail("That does not look like a Discord application id"); return; }

            List<string> l = new List<string>();
            l.Add("# SoundCloud Presence - settings");
            l.Add("# Edited from the app. Delete this file to reset everything.");
            l.Add("CLIENT_ID=" + clientId);
            l.Add("BROWSER_ONLY=" + (chkBrowser.Checked ? "true" : "false"));
            l.Add("NO_COVER=" + (chkNoCover.Checked ? "true" : "false"));
            l.Add("HIDE_WHEN_PAUSED=" + (chkHidePaused.Checked ? "true" : "false"));
            l.Add("ACTIVITY_TYPE=" + ActivityValues[_actIdx].ToString(CultureInfo.InvariantCulture));
            l.Add("POLL_SECONDS=" + poll.ToString(CultureInfo.InvariantCulture));
            l.Add("ASSET_KEY=" + txtAsset.Text.Trim());
            l.Add("COVER_MAX_SIZE=" + coverSize.ToString(CultureInfo.InvariantCulture));
            l.Add("VERBOSE=" + (chkVerbose.Checked ? "true" : "false"));
            try
            {
                File.WriteAllLines(ConfigPath, l.ToArray());
            }
            catch (Exception ex) { Fail("Could not save: " + ex.Message); return; }

            bool restart = IsRunning();
            if (restart)
            {
                StopService();
                if (clientId.Length > 0) StartService();
            }
            lblSave.ForeColor = Theme.Green;
            lblSave.Text = restart
                ? (clientId.Length > 0 ? "Saved - service restarted" : "Saved - service stopped (no application id)")
                : "Saved";
            _feedbackUntil = DateTime.Now.AddSeconds(4);
        }

        void Fail(string message)
        {
            lblSave.ForeColor = Theme.Red;
            lblSave.Text = message;
            _feedbackUntil = DateTime.Now.AddSeconds(6);
        }

        // ---------------------------------------------------------------- ui --

        void RefreshUi()
        {
            bool running = IsRunning();
            string title = "", artist = "", playing = "", appId = "", discord = "", coverPath = "";
            int clients = 0;

            if (running && File.Exists(StateFile))
            {
                try
                {
                    foreach (string line in File.ReadAllLines(StateFile))
                    {
                        int i = line.IndexOf('=');
                        if (i <= 0) continue;
                        string k = line.Substring(0, i);
                        string v = line.Substring(i + 1);
                        if (k == "Title") title = v;
                        else if (k == "Artist") artist = v;
                        else if (k == "Playing") playing = v;
                        else if (k == "AppId") appId = v;
                        else if (k == "Discord") discord = v;
                        else if (k == "Cover") coverPath = v;
                        else if (k == "Clients") int.TryParse(v, out clients);
                    }
                }
                catch { }
            }

            if (_demoMode)
            {
                running = true;
                title = ""; artist = ""; playing = ""; appId = "";
                discord = "connected";
                clients = 2;
                coverPath = "";
            }

            pillService.Set(running ? "Service running" : "Service stopped", running ? Theme.Green : Theme.Muted);
            Color dCol = Theme.Muted;
            string dTxt = "Discord unknown";
            if (running)
            {
                if (discord == "connected")
                {
                    dCol = Theme.Green;
                    dTxt = clients > 1 ? "Discord connected \u00B7 " + clients + " clients" : "Discord connected";
                }
                else { dCol = Theme.Red; dTxt = "Discord offline"; }
            }
            pillDiscord.Set(dTxt, dCol);
            pillDiscord.Location = new Point(pillService.Location.X + pillService.Width + 8, pillService.Location.Y);

            bool hasTrack = running && title.Length > 0;
            if (hasTrack)
            {
                lblTitle.ForeColor = Theme.Txt;
                lblTitle.Font = TitleFont;
                lblTitle.Text = title;
                lblArtist.Text = artist;
                string via = FriendlyApp(appId);
                lblSub.Text = (playing == "False" ? "Paused" : "Playing") + (via.Length > 0 ? "  \u00B7  via " + via : "");
            }
            else
            {
                lblTitle.ForeColor = Theme.Muted;
                lblTitle.Font = TitleFontIdle;
                lblTitle.Text = running ? "Waiting for media playback" : "Nothing is playing";
                lblArtist.Text = "";
                lblSub.Text = "";
            }

            string coverKey = hasTrack ? title + "|" + artist : "";
            if (coverKey != _coverFor || (_coverKnown && !hasTrack))
            {
                _coverFor = coverKey;
                _coverKnown = hasTrack;
                cover.SetImage(hasTrack ? LoadImage(coverPath) : null);
            }

            btnStart.Enabled = !running;
            btnStop.Enabled = running;
            btnAuto.Text = "Start with Windows: " + (File.Exists(StartupLnk) ? "On" : "Off");

            if (_feedbackUntil != DateTime.MinValue && DateTime.Now > _feedbackUntil)
            {
                lblSave.Text = "";
                _feedbackUntil = DateTime.MinValue;
            }

            // tray tooltip hard limit is 63 chars
            string tip = "SoundCloud Presence";
            if (hasTrack)
            {
                tip = "SoundCloud Presence \u2013 " + artist + " \u2013 " + title;
                if (tip.Length > 62) tip = tip.Substring(0, 59) + "...";
            }
            tray.Text = tip;
        }

        static Bitmap LoadImage(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                byte[] data = File.ReadAllBytes(path);
                using (MemoryStream ms = new MemoryStream(data))
                using (Image img = Image.FromStream(ms))
                    return new Bitmap(img);
            }
            catch { return null; }
        }

        static string FriendlyApp(string appId)
        {
            if (string.IsNullOrEmpty(appId)) return "";
            string a = appId.ToLowerInvariant();
            int bang = a.IndexOf('!');
            if (bang > 0) a = a.Substring(0, bang);
            if (a.Contains("chrome")) return "Chrome";
            if (a.Contains("msedge")) return "Edge";
            if (a.Contains("firefox")) return "Firefox";
            if (a.Contains("brave")) return "Brave";
            if (a.Contains("opera")) return "Opera";
            if (a.Contains("vivaldi")) return "Vivaldi";
            if (a.Contains("chromium")) return "Chromium";
            if (a.Contains("waterfox")) return "Waterfox";
            if (a.Contains("librewolf")) return "LibreWolf";
            if (a.Contains("spotify")) return "Spotify";
            if (a.Contains("tidal")) return "TIDAL";
            if (a.Contains("deezer")) return "Deezer";
            if (a.Contains("vlc")) return "VLC";
            if (a.Contains("mpv")) return "mpv";
            if (a.Contains("foobar")) return "foobar2000";
            if (a.Contains("musicapp") || a.Contains("itunes")) return "Apple Music";
            if (a.Contains("amazon")) return "Amazon Music";
            a = a.Replace(".exe", "");
            int dot = a.LastIndexOf('.');
            if (dot > 0 && a.Length - dot <= 5) a = a.Substring(dot + 1);
            int us = a.LastIndexOf('_');
            if (us > 0 && a.Length - us - 1 == 13) a = a.Substring(0, us);
            if (a.Length == 0) return "";
            return char.ToUpperInvariant(a[0]) + a.Substring(1);
        }

        // -------------------------------------------------------- process ---

        static bool IsRunning()
        {
            try
            {
                if (!File.Exists(PidFile)) return false;
                int pid;
                if (!int.TryParse(File.ReadAllText(PidFile).Trim(), out pid) || pid <= 0) return false;
                Process p = Process.GetProcessById(pid);
                return !p.HasExited;
            }
            catch { return false; }
        }

        static string ResolveClientId()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    foreach (string line in File.ReadAllLines(ConfigPath))
                    {
                        string t = line.Trim();
                        if (t.Length == 0 || t.StartsWith("#")) continue;
                        int i = t.IndexOf('=');
                        if (i <= 0) continue;
                        if (t.Substring(0, i).Trim().ToUpperInvariant() == "CLIENT_ID")
                        {
                            string v = t.Substring(i + 1).Trim().Trim('"');
                            if (v.Length > 0 && v != "PASTE_YOUR_APP_ID") return v;
                        }
                    }
                }
            }
            catch { }
            return DefaultClientId;
        }

        public void StartService()
        {
            if (!File.Exists(Service))
            {
                MessageBox.Show("SoundCloudPresence.ps1 was not found next to the app.", "SoundCloud Presence");
                return;
            }
            string clientId = ResolveClientId();
            if (clientId.Length == 0)
            {
                MessageBox.Show(
                    "Before it can start, the app needs your own Discord application id (free, one minute):\r\n\r\n" +
                    "1.  open  discord.com/developers/applications\r\n" +
                    "2.  click \"New Application\" and name it anything you like\r\n" +
                    "3.  copy the \"Application ID\" shown on the page\r\n" +
                    "4.  paste it into Settings here and press Save & apply.",
                    "SoundCloud Presence - first run", MessageBoxButtons.OK, MessageBoxIcon.Information);
                ShowSettings();
                return;
            }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "powershell.exe";
                psi.WorkingDirectory = Dir;
                psi.Arguments = "-WindowStyle Hidden -NoProfile -ExecutionPolicy Bypass -File \"" + Service +
                                "\" -ClientId \"" + clientId + "\" -StopExisting";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process.Start(psi);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "SoundCloud Presence"); }
            timer.Interval = 200;   // catch up quickly after a state change
            RefreshUi();
            timer.Interval = 500;
        }

        void StopService()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "powershell.exe";
                psi.WorkingDirectory = Dir;
                psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + Service + "\" -Stop";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process.Start(psi).WaitForExit(8000);
            }
            catch { }
            RefreshUi();
        }

        void ToggleAutostart()
        {
            try
            {
                if (File.Exists(StartupLnk))
                {
                    File.Delete(StartupLnk);
                }
                else
                {
                    Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                    dynamic shell = Activator.CreateInstance(shellType);
                    dynamic shortcut = shell.CreateShortcut(StartupLnk);
                    shortcut.TargetPath = Application.ExecutablePath;
                    shortcut.Arguments = "--start";
                    shortcut.WorkingDirectory = Dir;
                    shortcut.WindowStyle = 7;
                    shortcut.Description = "SoundCloud Presence";
                    shortcut.Save();
                }
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "SoundCloud Presence"); }
            RefreshUi();
        }

        static void OpenInNotepad(string path)
        {
            try
            {
                if (!File.Exists(path)) File.WriteAllText(path, "");
                Process.Start("notepad.exe", "\"" + path + "\"");
            }
            catch { }
        }

        void ShowFromTray()
        {
            _allowVisible = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        public void HideToTray()
        {
            _allowVisible = false;
            Hide();
            ShowInTaskbar = false;
        }

        // build.ps1 uses these two poses for the README images
        public void PoseForScreenshot(string path, bool settingsView)
        {
            _demoMode = true;
            timer.Enabled = false;
            _allowVisible = true;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            Show();
            Application.DoEvents();
            Thread.Sleep(120);
            Application.DoEvents();
            RefreshUi();
            if (settingsView)
            {
                ShowSettings();
                Application.DoEvents();
                Thread.Sleep(60);
                Application.DoEvents();
            }
            using (Bitmap bmp = new Bitmap(ClientSize.Width, ClientSize.Height))
            {
                DrawToBitmap(bmp, new Rectangle(Point.Empty, ClientSize));
                bmp.Save(path, ImageFormat.Png);
            }
            _exiting = true;
            Close();
        }

        protected override void SetVisibleCore(bool value)
        {
            if (!_allowVisible)
            {
                value = false;
                if (!IsHandleCreated) CreateHandle();
            }
            base.SetVisibleCore(value);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_exiting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            tray.Visible = false;
            base.OnFormClosing(e);
        }

        // ------------------------------------------------------------ entry --

        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            Mutex single = new Mutex(true, "SoundCloudPresenceApp", out created);
            if (!created) return;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool startHidden = false;
            string shotPath = null;
            bool shotSettings = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--start", StringComparison.OrdinalIgnoreCase)) startHidden = true;
                if (string.Equals(args[i], "--shot-settings", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                { shotPath = args[i + 1]; shotSettings = true; }
                else if (string.Equals(args[i], "--shot", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    shotPath = args[i + 1];
            }

            try
            {
                MainForm form = new MainForm(startHidden);

                if (shotPath != null)
                {
                    form.PoseForScreenshot(shotPath, shotSettings);
                    return;
                }

                if (startHidden)
                {
                    if (ResolveClientId().Length > 0) form.StartService();
                    form.HideToTray();
                }
                Application.Run(form);
            }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText(
                        Path.Combine(Path.GetTempPath(), "SoundCloudPresence.startup-error.txt"),
                        DateTime.Now.ToString("u") + "  " + ex + Environment.NewLine);
                }
                catch { }
                throw;
            }
        }
    }
}
