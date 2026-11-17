using System.Text;

namespace PineLoader;

sealed class DebugForm : Form
{
    readonly string root;
    readonly TextBox log;
    readonly Label agents;
    readonly System.Windows.Forms.Timer timer;
    readonly Dictionary<string, long> offsets = new(StringComparer.OrdinalIgnoreCase);

    public DebugForm(string root)
    {
        this.root = root;
        Brand.Load(root);
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = true;
        TopMost = false;
        MinimizeBox = true;
        MaximizeBox = true;
        BackColor = Brand.Ink;
        ForeColor = Brand.Text;
        Font = Brand.Body(12f);
        Text = "PineLoader Debug";
        MinimumSize = new Size(640, 420);
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        var width = Math.Min(960, Math.Max(640, area.Width - 48));
        var height = Math.Min(680, Math.Max(420, area.Height - 80));
        ClientSize = new Size(width, height);
        Location = new Point(area.Right - width - 16, area.Top + 16);
        var iconPath = Path.Combine(root, "brand", "pine.ico");
        if (File.Exists(iconPath)) Icon = new Icon(iconPath);
        KeyPreview = true;
        AccessibilityObject.Name = "PineLoader Debug";

        var bar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 92,
            BackColor = Brand.Ink
        };
        agents = new Label
        {
            Text = "Spielserver wird gelesen",
            ForeColor = Brand.Text,
            BackColor = Brand.Ink,
            Font = Brand.Body(12f),
            AutoSize = false,
            Bounds = new Rectangle(16, 10, width - 150, 28),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            AccessibleName = "Java-Agents am Spielserver"
        };
        var keep = new CheckBox
        {
            Text = "Beim nächsten Spielstart wieder öffnen",
            ForeColor = Brand.Text,
            BackColor = Brand.Ink,
            Font = Brand.Body(12f),
            FlatStyle = FlatStyle.Flat,
            AutoSize = false,
            Bounds = new Rectangle(16, 44, width - 150, 32),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Checked = OpenAtStart(root),
            TabIndex = 1,
            AccessibleName = "Beim nächsten Spielstart wieder öffnen"
        };
        keep.CheckedChanged += (_, _) => Save(root, keep.Checked);
        var close = Brand.Button("Schließen", new Rectangle(width - 128, 28, 112, 36), primary: false);
        close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        close.AccessibleName = "Debug-Fenster schließen";
        close.TabIndex = 2;
        close.Click += (_, _) => Close();
        bar.Controls.Add(agents);
        bar.Controls.Add(keep);
        bar.Controls.Add(close);

        log = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            BorderStyle = BorderStyle.None,
            BackColor = Brand.Raised,
            ForeColor = Brand.Text,
            Font = new Font(FontFamily.GenericMonospace, 10f),
            Dock = DockStyle.Fill,
            TabIndex = 0,
            AccessibleName = "Mod-Protokoll"
        };
        log.Text = "Warte auf die Mods." + Environment.NewLine;

        Controls.Add(log);
        Controls.Add(bar);
        CancelButton = close;
        AcceptButton = close;

        timer = new System.Windows.Forms.Timer { Interval = 400 };
        timer.Tick += (_, _) => RefreshView();
        Shown += (_, _) =>
        {
            RefreshView();
            timer.Start();
        };
        FormClosed += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            log.Font.Dispose();
        };
    }

    protected override bool ShowWithoutActivation => true;

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

    public static bool OpenAtStart(string root)
    {
        var path = IniPath(root);
        if (!File.Exists(path)) return true;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (!line.StartsWith("Debug=", StringComparison.OrdinalIgnoreCase)) continue;
            return line.Substring("Debug=".Length).Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }

    public static void Save(string root, bool open)
    {
        var path = IniPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : ["[PineLoader]"];
        var value = "Debug=" + (open ? "true" : "false");
        var index = lines.FindIndex(line => line.TrimStart().StartsWith("Debug=", StringComparison.OrdinalIgnoreCase));
        if (index >= 0) lines[index] = value;
        else lines.Add(value);
        File.WriteAllLines(path, lines);
    }

    static string IniPath(string root) => Path.Combine(root, "Config", "PineLoader.ini");

    void RefreshView()
    {
        if (IsDisposed) return;
        agents.Text = AgentLine();
        var added = ReadNewLines();
        if (added.Length == 0) return;
        var follow = log.SelectionStart >= Math.Max(0, log.TextLength - 2);
        log.AppendText(added);
        if (follow)
        {
            log.SelectionStart = log.TextLength;
            log.ScrollToCaret();
        }
    }

    string AgentLine()
    {
        var path = Path.Combine(root, "Cache", "java-agent.arg");
        if (!File.Exists(path)) return "Spielserver: keine Java-Agents";
        var names = new List<string>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (!line.StartsWith("-javaagent:", StringComparison.OrdinalIgnoreCase)) continue;
            var payload = line.Substring("-javaagent:".Length).Trim().Trim('"');
            var split = payload.IndexOf('=');
            var jar = split >= 0 ? payload.Substring(0, split) : payload;
            var name = Path.GetFileName(jar);
            if (name.Length > 0) names.Add(name);
        }
        return names.Count == 0 ? "Spielserver: keine Java-Agents" : "Spielserver: " + string.Join(", ", names);
    }

    string ReadNewLines()
    {
        var dir = Path.Combine(root, "Logs");
        if (!Directory.Exists(dir)) return "";
        var added = new StringBuilder();
        foreach (var file in Directory.GetFiles(dir, "*.txt").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Equals("standort", StringComparison.OrdinalIgnoreCase) || name.Equals("place", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (!offsets.TryGetValue(file, out var offset)) offset = Math.Max(0, stream.Length - 12000);
                if (offset > stream.Length) offset = 0;
                if (offset > 0 && !offsets.ContainsKey(file))
                {
                    stream.Position = offset;
                    var skip = stream.ReadByte();
                    while (skip >= 0 && skip != '\n')
                    {
                        offset = stream.Position;
                        skip = stream.ReadByte();
                    }
                    if (skip == '\n') offset = stream.Position;
                }
                if (stream.Length == offset)
                {
                    offsets[file] = offset;
                    continue;
                }
                stream.Position = offset;
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var text = reader.ReadToEnd();
                offsets[file] = stream.Length;
                foreach (var raw in text.Split('\n'))
                {
                    var line = raw.TrimEnd('\r');
                    if (line.Length == 0) continue;
                    added.Append('[').Append(name).Append("] ").AppendLine(line);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return added.ToString();
    }
}
