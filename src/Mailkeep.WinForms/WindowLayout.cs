using System.Text.Json;

namespace Mailkeep.WinForms;

internal sealed record WindowLayout(Rectangle Bounds, bool Maximized, int Dpi, int FolderWidth,
    double LocalFraction, double MessageFraction, int[]? MessageColumns, int[]? MessageColumnOrder = null,
    string[]? ExpandedFolders = null, int MessageSplit = 0, int LocalSplit = 0,
    string? SelectedAccount = null, string? SelectedFolder = null)
{
    public const string Setting = "window.layout";

    public static WindowLayout? Read(string? json)
    {
        if (json is null) return null;
        try
        {
            // This small, stable setting is read before first show. Reflection-based contract discovery
            // cost more startup CPU than reading it; keep writing the existing JSON format below.
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Null) return null;
            var bounds = new Rectangle();
            if (root.TryGetProperty("Bounds", out var rectangle))
                foreach (var property in rectangle.EnumerateObject())
                    switch (property.Name)
                    {
                        // Older serializers include both Location/Size and X/Y/Width/Height. Apply
                        // setters in document order, as the historical Rectangle reader did.
                        case "Location": bounds.Location = new Point(Int(property.Value, "X"), Int(property.Value, "Y")); break;
                        case "Size": bounds.Size = new Size(Int(property.Value, "Width"), Int(property.Value, "Height")); break;
                        case "X": bounds.X = property.Value.GetInt32(); break;
                        case "Y": bounds.Y = property.Value.GetInt32(); break;
                        case "Width": bounds.Width = property.Value.GetInt32(); break;
                        case "Height": bounds.Height = property.Value.GetInt32(); break;
                    }
            return new(bounds, root.TryGetProperty("Maximized", out var maximized) && maximized.GetBoolean(),
                Int(root, "Dpi"), Int(root, "FolderWidth"), Fraction(root, "LocalFraction"), Fraction(root, "MessageFraction"),
                Columns(root, "MessageColumns"), Columns(root, "MessageColumnOrder"),
                root.TryGetProperty("ExpandedFolders", out var expanded) && expanded.ValueKind != JsonValueKind.Null
                    ? expanded.EnumerateArray().Select(value => value.GetString()!).ToArray() : null,
                Int(root, "MessageSplit"), Int(root, "LocalSplit"), Text(root, "SelectedAccount"), Text(root, "SelectedFolder"));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            return null;
        }

        static int Int(JsonElement value, string name) => value.TryGetProperty(name, out var property) ? property.GetInt32() : 0;
        static double Fraction(JsonElement value, string name) => value.TryGetProperty(name, out var property) ? property.GetDouble() : 0;
        static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var property) ? property.GetString() : null;
        static int[]? Columns(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.EnumerateArray().Select(item => item.GetInt32()).ToArray() : null;
    }

    /// <summary>The window's size, place and maximised state. Put on the window before it is first shown, it
    /// opens where it was; put on afterwards, it opened at the default size, grew, then maximised.</summary>
    public void RestoreWindow(Form form)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var area = Screen.FromRectangle(Bounds).WorkingArea;
        var width = Math.Max(form.MinimumSize.Width, Math.Min(Bounds.Width, area.Width));
        var height = Math.Max(form.MinimumSize.Height, Math.Min(Bounds.Height, area.Height));
        form.StartPosition = FormStartPosition.Manual;
        form.Bounds = new Rectangle(Math.Clamp(Bounds.X, area.Left, Math.Max(area.Left, area.Right - width)),
            Math.Clamp(Bounds.Y, area.Top, Math.Max(area.Top, area.Bottom - height)), width, height);
        if (Maximized) form.WindowState = FormWindowState.Maximized;
    }

    public void Restore(Form form, SplitContainer folders, SplitContainer accounts, SplitContainer messages,
        ListView messageList, ISet<string> expandedFolders, bool restoreWindow = true)
    {
        foreach (var key in ExpandedFolders ?? []) expandedFolders.Add(key);
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        if (restoreWindow) RestoreWindow(form);
        var scale = Dpi > 0 ? (double)form.DeviceDpi / Dpi : 1;
        RestoreColumns(messageList, MessageColumns, scale);
        RestoreColumnOrder(messageList, MessageColumnOrder);
        // The splitters are set once the window has its final size: maximising above takes effect only
        // when the message loop gets to it, and a distance worked out against the smaller window before
        // that landed the reading pane somewhere else every launch.
        form.BeginInvoke(() =>
        {
            SetDistance(folders, FolderWidth * scale);
            SetDistance(accounts, LocalSplit > 0 ? LocalSplit * scale : LocalFraction * (accounts.Height - accounts.SplitterWidth));
            // The list keeps its height in pixels, as it did the last time, whatever the window does.
            SetDistance(messages, MessageSplit > 0 ? MessageSplit * scale : MessageFraction * (messages.Height - messages.SplitterWidth));
        });
    }

    public static WindowLayout Capture(Form form, bool maximized, SplitContainer folders, SplitContainer accounts,
        SplitContainer messages, ListView messageList, IEnumerable<string> expandedFolders,
        string? selectedAccount = null, string? selectedFolder = null)
        => new(form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds, maximized,
            form.DeviceDpi, folders.SplitterDistance,
            (double)accounts.SplitterDistance / Math.Max(1, accounts.Height - accounts.SplitterWidth),
            (double)messages.SplitterDistance / Math.Max(1, messages.Height - messages.SplitterWidth),
            messageList.Columns.Cast<ColumnHeader>().Select(column => column.Width).ToArray(),
            messageList.Columns.Cast<ColumnHeader>().Select(column => column.DisplayIndex).ToArray(),
            expandedFolders.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            messages.SplitterDistance, accounts.SplitterDistance, selectedAccount, selectedFolder);

    public string ToJson() => JsonSerializer.Serialize(this);

    private static void SetDistance(SplitContainer split, double distance)
    {
        var length = split.Orientation == Orientation.Vertical ? split.Width : split.Height;
        var maximum = length - split.SplitterWidth - split.Panel2MinSize;
        if (!double.IsFinite(distance) || distance <= 0 || maximum < split.Panel1MinSize) return;
        split.SplitterDistance = (int)Math.Clamp(distance, split.Panel1MinSize, maximum);
    }

    private static void RestoreColumns(ListView list, int[]? widths, double scale)
    {
        if (widths is null) return;
        for (var index = 0; index < Math.Min(widths.Length, list.Columns.Count); index++)
            if (widths[index] > 0) list.Columns[index].Width = (int)Math.Clamp(widths[index] * scale, 20, 4000);
    }

    /// <summary>Each column's saved display position, by column index. Only a complete permutation is trusted;
    /// anything else, including a layout saved before columns could move, leaves the default order.</summary>
    private static void RestoreColumnOrder(ListView list, int[]? order)
    {
        var count = list.Columns.Count;
        if (order is null || order.Length != count || order.Distinct().Count() != count || order.Any(position => position < 0 || position >= count)) return;
        for (var position = 0; position < count; position++)
            list.Columns[Array.IndexOf(order, position)].DisplayIndex = position;
    }
}
