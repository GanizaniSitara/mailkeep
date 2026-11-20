namespace Mailkeep.WinForms;

/// <summary>Icon sheets bundled with Mailkeep. Missing or unreadable sheets fall back to drawn icons.
///
/// The sheets are plain grids — eight icons to a row, one file per size — so an icon is a crop.</summary>
internal static class OeIcons
{
    private const int Columns = 8;

    private static readonly Lazy<string?> Directory = new(() =>
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons");
        return File.Exists(Path.Combine(candidate, "icons16.png")) ? candidate : null;
    });

    private static readonly Lazy<Bitmap?> Small = new(() => Sheet(16));
    private static readonly Lazy<Bitmap?> Large = new(() => Sheet(32));

    public static bool Installed => Directory.Value is not null;

    /// <summary>One 16-pixel icon, for a folder tree or a message row.</summary>
    public static Bitmap? At16(int index) => Crop(Small.Value, 16, index, 16);

    /// <summary>One icon for the toolbar, taken from the 32-pixel sheet and brought down to the toolbar's own
    /// size: the 24-pixel sheet holds six status icons and none of the commands.</summary>
    public static Bitmap? At24(int index) => Crop(Large.Value, 32, index, 24);

    public static Bitmap? At32(int index) => Crop(Large.Value, 32, index, 32);

    private static Bitmap? Sheet(int cell)
    {
        if (Directory.Value is not { } directory) return null;
        try
        {
            // Copied into a bitmap of our own so the file is not held open for the life of the program.
            using var stream = File.OpenRead(Path.Combine(directory, $"icons{cell}.png"));
            using var loaded = new Bitmap(stream);
            return new Bitmap(loaded);
        }
        catch (Exception) { return null; }
    }

    private static Bitmap? Crop(Bitmap? sheet, int cell, int index, int size)
    {
        if (sheet is null || index < 0) return null;
        var left = index % Columns * cell;
        var top = index / Columns * cell;
        if (left + cell > sheet.Width || top + cell > sheet.Height) return null;
        try
        {
            var image = new Bitmap(size, size);
            using var graphics = Graphics.FromImage(image);
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            graphics.DrawImage(sheet, new Rectangle(0, 0, size, size), new Rectangle(left, top, cell, cell), GraphicsUnit.Pixel);
            return image;
        }
        catch (Exception)
        {
            // Drawing can fail for reasons that have nothing to do with this program — GDI+ reports most of them
            // as one unhelpful sentence. An icon is not worth refusing to start over: fall back to the drawn one.
            return null;
        }
    }
}
