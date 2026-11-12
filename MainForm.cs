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
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        TopMost = true;
        BackColor = Brand.Ink;
        ForeColor = Brand.Text;
        Text = "PineLoader";
        KeyPreview = true;
        ClientSize = new Size(780, WindowHeight());
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

    public Task<bool> OfferStart(int found, int loaded)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => ShowStartOffer(found, loaded));
            return startChoice.Task;
        }
        ShowStartOffer(found, loaded);
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

    void ShowStartOffer(int found, int loaded)
    {
        choosing = true;
        foundCount = found;
        loadedCount = loaded;
        countsReady = true;
        var top = LogoRect().Bottom + 16 + 34 + 22 * 3 + 16;
        var width = (ClientSize.Width - 56 - 12) / 2;
        var logs = Brand.Button("PineLoader-Log", new Rectangle(28, top, width, 44), primary: false);
        var errors = Brand.Button("Fehler-Log", new Rectangle(40 + width, top, width, 44), primary: false);
        var start = Brand.Button("Spiel starten", new Rectangle(28, top + 56, ClientSize.Width - 56, 48), primary: true);
        var close = Brand.Button("×", new Rectangle(ClientSize.Width - 52, 12, 36, 36), primary: false);
        close.AccessibleName = "Schließen";
        logs.TabIndex = 0;
        errors.TabIndex = 1;
        start.TabIndex = 2;
        close.TabIndex = 3;
        logs.Click += (_, _) => ShowLog("PineLoader-Log", Path.Combine(root, "Logs", "pineloader.txt"));
        errors.Click += (_, _) => ShowLog("Fehler-Log", Path.Combine(root, "Logs", "err.txt"));
        start.Click += (_, _) =>
        {
            if (!choosing) return;
            Choose(true);
        };
        close.Click += (_, _) => Close();
        AcceptButton = start;
        Controls.Add(logs);
        Controls.Add(errors);
        Controls.Add(start);
        Controls.Add(close);
        startButton = start;
        closeButton = close;
        Invalidate();
        start.Focus();
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
        return 48 + logoHeight + 16 + 34 + 22 * 3 + 16 + 44 + 12 + 48 + 24;
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
