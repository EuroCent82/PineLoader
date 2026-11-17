using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace PineLoader;

sealed class MainForm : Form
{
    const int Corner = 22;
    readonly bool direct;
    readonly string root;
    readonly Image? logo;
    readonly Image? wordmark;
    readonly Font statusFont;
    readonly Font bodyFont;
    readonly TaskCompletionSource<bool> startChoice = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Button? startButton;
    Button? closeButton;
    string heading = "Ordner werden geprüft";
    string detail = "";
    string? problem;
    int foundCount;
    int loadedCount;
    bool allowClose;
    bool choosing;
    bool countsReady;

    public MainForm(bool direct)
    {
        this.direct = direct;
        root = ModHost.FindRoot();
        Brand.Load(root);
        logo = LoadPng(Path.Combine(root, "brand", "NEW", "pineloader-logo.png"));
        wordmark = LoadPng(Path.Combine(root, "brand", "NEW", "pineloader-wordmark.png"));
        statusFont = Brand.Display(18f);
        bodyFont = Brand.Body(13f);
        var iconPath = Path.Combine(root, "brand", "pine.ico");
        if (File.Exists(iconPath)) Icon = new Icon(iconPath);
        AutoScaleMode = AutoScaleMode.Font;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        TopMost = true;
        BackColor = Brand.Ink;
        ForeColor = Brand.Text;
        Text = "PineLoader";
        KeyPreview = true;
        var height = WindowHeight();
        var work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 780, height);
        if (height > work.Height - 24) height = Math.Max(480, work.Height - 24);
        ClientSize = new Size(Math.Min(780, Math.Max(480, work.Width - 24)), height);
        AccessibilityObject.Name = "PineLoader";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        UpdateStyles();
    }

    public void ShowStep(string title, string line)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => ShowStep(title, line));
            return;
        }
        if (title == "Fehler") problem = line;
        else if (direct) return;
        heading = title;
        detail = line;
        if (!Visible)
        {
            ShowInTaskbar = true;
            TopMost = true;
            Show();
        }
        Invalidate();
    }

    public Task<bool> OfferStart(int found, int loaded, IReadOnlyList<string> modLines)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => ShowStartOffer(found, loaded, modLines));
            return startChoice.Task;
        }
        ShowStartOffer(found, loaded, modLines);
        return startChoice.Task;
    }

    public void StaySilent()
    {
        if (InvokeRequired)
        {
            BeginInvoke(StaySilent);
            return;
        }
        TopMost = false;
        ShowInTaskbar = false;
        Hide();
    }

    public void Finish()
    {
        allowClose = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (choosing)
        {
            Choose(false);
            allowClose = true;
        }
        else if (!allowClose) e.Cancel = true;
        base.OnFormClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (choosing && e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyShape();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.Clear(Brand.Ink);
        using var gold = new SolidBrush(Brand.Gold);
        using var text = new SolidBrush(Brand.Text);
        using var muted = new SolidBrush(Brand.Muted);
        using var line = new Pen(Brand.Line);
        g.FillRectangle(gold, 0, 0, ClientSize.Width, 3);
        using var outline = Brand.Round(new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1), Corner);
        g.DrawPath(line, outline);
        var mark = LogoRect();
        if (logo != null) g.DrawImage(logo, mark);
        var top = mark.Bottom + 16f;
        if (direct)
        {
            g.DrawString("Bereit", statusFont, text, 28, top);
            if (problem != null)
            {
                g.DrawString(problem, bodyFont, gold, new RectangleF(28, top + 34, ClientSize.Width - 56, 48));
            }
            else if (countsReady)
            {
                var skipped = Math.Max(0, foundCount - loadedCount);
                g.DrawString("Gefunden: " + foundCount, bodyFont, muted, 28, top + 34);
                g.DrawString("Geladen: " + loadedCount, bodyFont, muted, 28, top + 58);
                g.DrawString("Nicht geladen: " + skipped, bodyFont, muted, 28, top + 82);
            }
            return;
        }
        g.DrawString(heading, bodyFont, text, 28, top);
        g.DrawString(detail, bodyFont, muted, new RectangleF(28, top + 24, ClientSize.Width - 56, 40));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            statusFont.Dispose();
            bodyFont.Dispose();
            logo?.Dispose();
            wordmark?.Dispose();
        }
        base.Dispose(disposing);
    }

    void ShowStartOffer(int found, int loaded, IReadOnlyList<string> modLines)
    {
        choosing = true;
        foundCount = found;
        loadedCount = loaded;
        countsReady = true;
        var contentTop = LogoRect().Bottom + 16 + 34 + 22 * 3 + 12;
        var work = Screen.FromControl(this).WorkingArea;
        var needed = contentTop + 120 + 12 + 44 + 10 + 32 + 4 + 32 + 10 + 44 + 12 + 48 + 24;
        var height = Math.Min(Math.Max(ClientSize.Height, needed), Math.Max(520, work.Height - 16));
        if (ClientSize.Height != height)
        {
            ClientSize = new Size(ClientSize.Width, height);
            ApplyShape();
        }
        var startTop = ClientSize.Height - 24 - 48;
        var restoreTop = startTop - 12 - 44;
        var debugTop = restoreTop - 10 - 32;
        var autoTop = debugTop - 4 - 32;
        var logsTop = autoTop - 10 - 44;
        var listHeight = Math.Max(36, logsTop - 12 - contentTop);
        var width = (ClientSize.Width - 56 - 12) / 2;
        var list = new ListBox
        {
            Bounds = new Rectangle(28, contentTop, ClientSize.Width - 56, listHeight),
            IntegralHeight = false,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Brand.Ink,
            ForeColor = Brand.Text,
            Font = Brand.Body(13f),
            TabIndex = 0,
            AccessibleName = "Mod-Ordner"
        };
        foreach (var line in modLines) list.Items.Add(line);
        var logs = Brand.Button("PineLoader-Log", new Rectangle(28, logsTop, width, 44), primary: false);
        var errors = Brand.Button("Fehler-Log", new Rectangle(40 + width, logsTop, width, 44), primary: false);
        var autostart = new CheckBox
        {
            Text = "Ab der Anmeldung auf eine neue Spieldatei achten",
            Bounds = new Rectangle(28, autoTop, ClientSize.Width - 56, 32),
            ForeColor = Brand.Text,
            BackColor = Brand.Ink,
            Font = Brand.Body(13f),
            FlatStyle = FlatStyle.Flat,
            Checked = ModHost.AutostartOn(root),
            TabIndex = 3,
            AccessibleName = "Ab der Anmeldung auf eine neue Spieldatei achten"
        };
        autostart.CheckedChanged += (_, _) =>
        {
            ModHost.SaveAutostart(root, autostart.Checked);
            new ModHost(root).RememberStartup();
        };
        var debug = new CheckBox
        {
            Text = "Debug-Fenster beim Spielstart",
            Bounds = new Rectangle(28, debugTop, ClientSize.Width - 56, 32),
            ForeColor = Brand.Text,
            BackColor = Brand.Ink,
            Font = Brand.Body(13f),
            FlatStyle = FlatStyle.Flat,
            Checked = DebugForm.OpenAtStart(root),
            TabIndex = 4,
            AccessibleName = "Debug-Fenster beim Spielstart"
        };
        debug.CheckedChanged += (_, _) => DebugForm.Save(root, debug.Checked);
        var restore = Brand.Button("Spiel wiederherstellen", new Rectangle(28, restoreTop, ClientSize.Width - 56, 44), primary: false);
        restore.TabIndex = 5;
        restore.Click += (_, _) => ConfirmRestore();
        var start = Brand.Button("Spiel starten", new Rectangle(28, startTop, ClientSize.Width - 56, 48), primary: true);
        var close = Brand.Button("×", new Rectangle(ClientSize.Width - 52, 12, 36, 36), primary: false);
        close.AccessibleName = "Schließen";
        logs.TabIndex = 1;
        errors.TabIndex = 2;
        start.TabIndex = 6;
        close.TabIndex = 7;
        logs.Click += (_, _) => ShowLog("PineLoader-Log", Path.Combine(root, "Logs", "pineloader.txt"));
        errors.Click += (_, _) => ShowLog("Fehler-Log", Path.Combine(root, "Logs", "err.txt"));
        start.Click += (_, _) =>
        {
            if (!choosing) return;
            Choose(true);
        };
        close.Click += (_, _) => Close();
        AcceptButton = start;
        Controls.Add(list);
        Controls.Add(logs);
        Controls.Add(errors);
        Controls.Add(autostart);
        Controls.Add(debug);
        Controls.Add(restore);
        Controls.Add(start);
        Controls.Add(close);
        startButton = start;
        closeButton = close;
        Invalidate();
        start.Focus();
    }

    void ConfirmRestore()
    {
        var host = new ModHost(root);
        var name = host.ShippingFileName();
        var answer = MessageBox.Show(
            this,
            name + " wird wieder die originale Spieldatei. PineLoader startet danach nicht mehr vor dem Spiel.",
            "Spiel wiederherstellen",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;
        try
        {
            host.RestoreGame();
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Spiel wiederherstellen", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void ShowLog(string title, string path)
    {
        using var window = new LogForm(title, path, wordmark, Icon);
        window.ShowDialog(this);
    }

    void Choose(bool start)
    {
        choosing = false;
        if (startButton != null) startButton.Enabled = false;
        if (closeButton != null) closeButton.Enabled = false;
        foreach (Control control in Controls) control.Enabled = false;
        startChoice.TrySetResult(start);
    }

    Rectangle LogoRect()
    {
        var width = ClientSize.Width - 56;
        var height = logo == null ? 80 : Math.Max(1, width * logo.Height / logo.Width);
        return new Rectangle(28, 48, width, height);
    }

    int WindowHeight()
    {
        var width = 780 - 56;
        var logoHeight = logo == null ? 80 : Math.Max(1, width * logo.Height / logo.Width);
        if (!direct) return 48 + logoHeight + 16 + 64 + 24;
        return 48 + logoHeight + 16 + 34 + 22 * 3 + 12 + 120 + 12 + 44 + 10 + 32 + 4 + 32 + 10 + 44 + 12 + 48 + 24;
    }

    void ApplyShape()
    {
        Region?.Dispose();
        Region = new Region(Brand.Round(ClientRectangle, Corner));
    }

    static Image? LoadPng(string path)
    {
        if (!File.Exists(path)) return null;
        using var file = new Bitmap(path);
        var copy = new Bitmap(file.Width, file.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(copy);
        graphics.Clear(Color.Transparent);
        graphics.DrawImage(file, 0, 0, file.Width, file.Height);
        return copy;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ClassStyle |= 0x00020000;
            return parameters;
        }
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var round = 2;
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }
}
