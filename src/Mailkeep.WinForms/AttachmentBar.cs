using Mailkeep.Infrastructure;
using Mailkeep.Core;

namespace Mailkeep.WinForms;

/// <summary>The paperclip at the top right of a message's header, as the client this is modelled on shows
/// it: there when the message carries files, and a menu of them when clicked — each one opens, and the
/// last entry saves them all. Nothing of it shows for a message without attachments.</summary>
internal sealed class AttachmentBar : UserControl
{
    private readonly Button _clip = new()
    {
        FlatStyle = FlatStyle.Flat, Size = new Size(24, 22), Margin = Padding.Empty, Cursor = Cursors.Hand,
        ImageAlign = ContentAlignment.MiddleCenter, AccessibleName = "Message attachments"
    };
    private readonly Func<long, int, string> _extract;
    private IReadOnlyList<AttachmentInfo> _attachments = [];
    private int[] _indices = [];
    private Func<int, string>? _extractCurrent;

    public AttachmentBar(Func<long, int, string> extract)
    {
        _extract = extract;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = Padding.Empty;
        AccessibleName = "Attachment actions";
        _clip.Image = OeIcons.At16(26) ?? new Bitmap(16, 16);
        _clip.FlatAppearance.BorderSize = 0;
        _clip.FlatAppearance.MouseOverBackColor = Color.FromArgb(229, 239, 251);
        _clip.Click += (_, _) => ShowMenu();
        Controls.Add(_clip);
        SetMessage(null);
    }

    public void SetMessage(MessageContent? content)
        => SetAttachments(content?.Attachments ?? [], content is null ? null : index => _extract(content.Summary.Id, index));

    public void SetAttachments(IReadOnlyList<AttachmentInfo> attachments, Func<int, string>? extract)
    {
        _attachments = attachments;
        _extractCurrent = extract;
        // Real attachments first, then the pictures embedded in the message body, which are saved the same way.
        _indices = attachments.Select((file, index) => (file, index)).OrderBy(item => item.file.Inline).Select(item => item.index).ToArray();
        Visible = _indices.Length > 0;
        var names = Names();
        var toolTip = string.Join(Environment.NewLine, _indices.Select(index => names[index]));
        _clip.AccessibleDescription = toolTip;
    }

    private void ShowMenu()
    {
        if (_indices.Length == 0) return;
        var menu = new ContextMenuStrip { Font = new Font("Tahoma", 8.25f) };
        var pictures = false;
        foreach (var index in _indices)
        {
            var file = _attachments[index];
            if (file.Inline && !pictures)
            {
                pictures = true;
                if (menu.Items.Count > 0) menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(new ToolStripLabel("Pictures in this message") { ForeColor = SystemColors.GrayText });
            }
            var item = new ToolStripMenuItem($"{Names()[index]} ({Size(file.Size)})", OeIcons.At16(26)) { AccessibleName = Names()[index] };
            var chosen = index;
            item.Click += async (_, _) => await OpenAsync(chosen);
            menu.Items.Add(item);
        }
        menu.Items.Add(new ToolStripSeparator());
        var save = new ToolStripMenuItem(pictures && _indices.Any(index => !_attachments[index].Inline) ? "Save All…" : pictures ? "Save Pictures…" : "Save Attachments…")
            { AccessibleName = "Save attachments" };
        save.Click += async (_, _) => await SaveAllAsync();
        menu.Items.Add(save);
        menu.Show(_clip, new Point(0, _clip.Height));
    }

    /// <summary>A file name for every part, unique within the message. Embedded pictures often arrive without
    /// one ("attachment"), so they become picture-1.png and so on, with the extension their type implies; a
    /// repeated name gets " (2)", so saving them all never writes one over another.</summary>
    private string[] Names()
    {
        var names = new string[_attachments.Count];
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pictures = 0;
        foreach (var index in _indices)
        {
            var file = _attachments[index];
            var name = AttachmentSafety.DisplayName(file.FileName);
            if (file.Inline && (name == "attachment" || Path.GetExtension(name).Length == 0))
                name = $"picture-{++pictures}" + Extension(file.ContentType);
            var stem = Path.GetFileNameWithoutExtension(name);
            var extension = Path.GetExtension(name);
            for (var n = 2; !used.Add(name); n++) name = $"{stem} ({n}){extension}";
            names[index] = name;
        }
        return names;
    }

    private static string Extension(string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" or "image/pjpeg" => ".jpg",
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/bmp" => ".bmp",
        "image/webp" => ".webp",
        "image/svg+xml" => ".svg",
        "image/tiff" => ".tif",
        _ => ""
    };

    private static string Size(long bytes) => bytes < 1024 ? $"{bytes} B" : $"{Math.Ceiling(bytes / 1024d):N0} KB";

    private async Task OpenAsync(int index)
    {
        if (_extractCurrent is not { } extract) return;
        try
        {
            var path = await Task.Run(() => extract(index));
            if (!IsDisposed && !Disposing) AttachmentSafety.OpenWithShell(path);
        }
        catch (Exception error)
        {
            if (!IsDisposed) MessageBox.Show(this, error.Message, "Could not open attachment", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task SaveAllAsync()
    {
        if (_extractCurrent is not { } extract) return;
        using var dialog = new FolderBrowserDialog { Description = "Save attachments to", UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var folder = dialog.SelectedPath;
        var files = _indices.Select(index => (Index: index, Name: Names()[index])).ToArray();
        try
        {
            foreach (var file in files)
            {
                await Task.Run(() =>
                {
                    var path = extract(file.Index);
                    File.Copy(path, Path.Combine(folder, file.Name), overwrite: true);
                });
            }
        }
        catch (Exception error)
        {
            if (!IsDisposed) MessageBox.Show(this, error.Message, "Could not save attachments", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
