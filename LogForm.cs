using System.Drawing.Drawing2D;

namespace PineLoader;

sealed class LogForm : Form
{
    const int Corner = 22;

    readonly Image? wordmark;

    public LogForm(string title, string path, Image? wordmark, Icon? icon)
    {
        this.wordmark = wordmark;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Brand.Ink;
        ForeColor = Brand.Text;
        ClientSize = new Size(720, 520);
        Text = title;
        if (icon != null) Icon = (Icon)icon.Clone();
        KeyPreview = true;
        AccessibilityObject.Name = title;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        UpdateStyles();

        var markHeight = wordmark == null ? 0 : 72;

        var heading = new Label
        {
            Text = title,
            ForeColor = Brand.Text,
            BackColor = Brand.Ink,
            Font = Brand.Body(12f),
            AutoSize = false,
            Bounds = new Rectangle(28, 48 + markHeight + 8, ClientSize.Width - 120, 28),
            AccessibleName = title
        };
        var body = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            BackColor = Brand.Raised,
            ForeColor = Brand.Text,
            Font = Brand.Body(11f),
            Text = ReadLog(path),
            Bounds = new Rectangle(28, heading.Bottom + 8, ClientSize.Width - 56, ClientSize.Height - heading.Bottom - 36),
            TabIndex = 0,
            AccessibleName = title + ", Inhalt"
        };
        var close = Brand.Button("Schließen", new Rectangle(ClientSize.Width - 132, 12, 104, 36), primary: false);
        close.AccessibleName = "Schließen";
        close.Click += (_, _) => Close();
        CancelButton = close;
        Controls.Add(heading);
        Controls.Add(body);
        Controls.Add(close);
        AcceptButton = close;
        Shown += (_, _) =>
        {
            ApplyShape();
            body.SelectionStart = 0;
            body.SelectionLength = 0;
            close.Focus();
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Brand.Ink);
        using var gold = new SolidBrush(Brand.Gold);
        using var line = new Pen(Brand.Line);
        g.FillRectangle(gold, 0, 0, ClientSize.Width, 3);
        using var outline = Brand.Round(new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1), Corner);
        g.DrawPath(line, outline);
        if (wordmark != null)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            var markHeight = 72;
            var markWidth = markHeight * wordmark.Width / wordmark.Height;
            g.DrawImage(wordmark, new Rectangle((ClientSize.Width - markWidth) / 2, 48, markWidth, markHeight));
        }
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

    void ApplyShape()
    {
        Region?.Dispose();
        Region = new Region(Brand.Round(ClientRectangle, Corner));
    }

    static string ReadLog(string path)
    {
        try
        {
            if (!File.Exists(path)) return "Keine Einträge.";
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length == 0) return "Keine Einträge.";
            if (stream.Length > 262144) stream.Position = stream.Length - 262144;
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd().Trim();
            return text.Length == 0 ? "Keine Einträge." : text;
        }
        catch (Exception error)
        {
            return error.Message;
        }
    }
}
