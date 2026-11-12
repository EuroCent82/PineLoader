using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace PineLoader;

static class Brand
{
    public static readonly Color Ink = Color.FromArgb(4, 34, 26);
    public static readonly Color Gold = Color.FromArgb(213, 180, 26);
    public static readonly Color Text = Color.FromArgb(244, 247, 242);
    public static readonly Color Muted = Color.FromArgb(197, 217, 204);
    public static readonly Color Line = Color.FromArgb(36, 92, 72);
    public static readonly Color Raised = Color.FromArgb(12, 56, 42);
    public static readonly Color Hover = Color.FromArgb(20, 84, 64);

    static readonly PrivateFontCollection Fonts = new();
    static FontFamily? DisplayFamily;
    static FontFamily? TextFamily;

    public static void Load(string root)
    {
        DisplayFamily = Family(root, "ChakraPetch-SemiBold.ttf", "Chakra");
        TextFamily = Family(root, "WorkSans-Regular.ttf", "Work Sans");
    }

    public static Font Display(float size) => New(DisplayFamily, size, FontStyle.Bold);

    public static Font Body(float size) => New(TextFamily, size, FontStyle.Regular);

    public static Button Button(string text, Rectangle bounds, bool primary)
    {
        var button = new Button
        {
            Text = text,
            Font = Display(13f),
            Bounds = bounds,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            ForeColor = primary ? Ink : Text,
            BackColor = primary ? Gold : Raised,
            AccessibleName = text
        };
        button.FlatAppearance.BorderSize = 2;
        button.FlatAppearance.BorderColor = primary ? Ink : Line;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(228, 198, 58) : Hover;
        button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(184, 154, 22) : Line;
        button.GotFocus += (_, _) => button.FlatAppearance.BorderColor = Text;
        button.LostFocus += (_, _) => button.FlatAppearance.BorderColor = primary ? Ink : Line;
        return button;
    }

    public static GraphicsPath Round(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    static FontFamily? Family(string root, string file, string familyName)
    {
        var path = Path.Combine(root, "fonts", file);
        if (!File.Exists(path)) return null;
        Fonts.AddFontFile(path);
        return Fonts.Families.FirstOrDefault(item => item.Name.Contains(familyName, StringComparison.OrdinalIgnoreCase));
    }

    static Font New(FontFamily? family, float size, FontStyle fallback)
    {
        if (family != null) return new Font(family, size, FontStyle.Regular, GraphicsUnit.Point);
        return new Font("Segoe UI", size, fallback, GraphicsUnit.Point);
    }
}
