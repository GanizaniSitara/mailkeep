using Microsoft.Web.WebView2.Core;

namespace Mailkeep.WinForms;

/// <summary>The reading pane's view of mail written as a page. The text of an HTML letter is not the letter:
/// the tables it is laid out in, the headings, the pictures it was written around — a client that shows only
/// the words is handing over somebody's post with the pictures torn out, which is why the client this one is
/// modelled on has always drawn the page.
///
/// The document arrives through SafeHtml, whose default content policy allows only data: images and inline
/// style. A reader can explicitly allow selected image origins for a rendering, while remote stylesheets,
/// scripts and frames remain blocked. Clicking a link hands it to the browser rather than navigating the pane,
/// so the pane only ever holds the letter it was given.</summary>
internal sealed class HtmlView : Control
{
    private CoreWebView2Controller? _controller;
    private Form? _positionOwner;
    public CoreWebView2? CoreWebView2 => _controller?.CoreWebView2;
    private readonly Action<string?> _openLink;
    private bool _ready;
    private string? _documentUri;
    private string? _documentFile;
    private string? _folder;
    private string? _pending;
    private string? _pendingPictures;
    private ulong? _navigationId;
    private bool _messageDocument;
    public bool DocumentLoaded { get; private set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<Keys, Action?>? ShortcutAction { get; set; }
    public event EventHandler? DocumentLoadCompleted;

    public HtmlView(Action<string?> openLink)
    {
        _openLink = openLink;
        Dock = DockStyle.Fill;
        // Keep visible until initialization creates the native window. Ready hides the idle view;
        // the caller then switches between this control and the text pane.
        BackColor = Color.White;
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        AccessibleName = "Message body";
    }

    /// <summary>Whether the view can be used at all. The runtime is part of Windows, but a machine without it
    /// must still be able to read its mail, so the caller falls back to the text pane.</summary>
    public bool Failed { get; private set; }
    internal bool IsReady => _ready;
    internal bool RoutePaneKeys;
    private bool _readingContent;

    protected override bool ProcessCmdKey(ref Message message, Keys keys)
    {
        if (RoutePaneKeys && !_readingContent && keys == Keys.Down && _controller is not null)
        {
            _controller.MoveFocus(CoreWebView2MoveFocusReason.Next);
            return true;
        }
        if (RoutePaneKeys && keys is Keys.Tab or (Keys.Shift | Keys.Tab)
            && ShortcutAction?.Invoke(keys) is { } action)
        {
            BeginInvoke(() => { if (!IsDisposed) action(); });
            return true;
        }
        return base.ProcessCmdKey(ref message, keys);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        if (!RoutePaneKeys) _controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (_controller is not null) _controller.Bounds = ClientRectangle;
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (_controller is not null) _controller.IsVisible = Visible;
    }

    private void ParentWindowMoved(object? sender, EventArgs e) => _controller?.NotifyParentWindowPositionChanged();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_positionOwner is not null) _positionOwner.LocationChanged -= ParentWindowMoved;
            _controller?.Close();
            _controller = null;
        }
        base.Dispose(disposing);
    }

    /// <summary>Starts the runtime in its own folder: the default is beside the executable, which is under
    /// Program Files for an installed copy and not writable.</summary>
    private static bool _firstMessageLoadedLogged;

    public async Task StartAsync(string dataFolder)
    {
        try
        {
            _folder = dataFolder;
            await Task.Run(() => WebDocuments.Sweep(dataFolder));
            if (IsDisposed || Disposing) return;
            var environment = await CoreWebView2Environment.CreateAsync(null, dataFolder);
            // Own the supported controller keyboard event instead of relying on the
            // WinForms wrapper's newer, optional input-processing interface.
            var controller = await environment.CreateCoreWebView2ControllerAsync(Handle);
            if (IsDisposed || Disposing) { controller.Close(); return; }
            _controller = controller;
            controller.Bounds = ClientRectangle;
            controller.IsVisible = Visible;
            controller.DefaultBackgroundColor = Color.White;
            controller.GotFocus += (_, _) => _readingContent = true;
            controller.LostFocus += (_, _) => _readingContent = false;
            controller.AcceleratorKeyPressed += (_, args) =>
            {
                if (args.KeyEventKind is not (CoreWebView2KeyEventKind.KeyDown or CoreWebView2KeyEventKind.SystemKeyDown)) return;
                var keys = (Keys)args.VirtualKey | ModifierKeys;
                if (RoutePaneKeys && keys == Keys.Escape)
                {
                    args.Handled = true;
                    BeginInvoke(() => { if (!IsDisposed) { _readingContent = false; Focus(); } });
                    return;
                }
                // Within the document, Tab follows its ordinary links. Pane navigation
                // resumes on Escape or when the browser reaches the document boundary.
                if (keys is Keys.Tab or (Keys.Shift | Keys.Tab)) return;
                if (ShortcutAction?.Invoke(keys) is not { } action) return;
                args.Handled = true;
                if (args.PhysicalKeyStatus.WasKeyDown == 0)
                    BeginInvoke(() => { if (!IsDisposed) action(); });
            };
            controller.MoveFocusRequested += (_, args) =>
            {
                if (!RoutePaneKeys) return;
                var keys = args.Reason == CoreWebView2MoveFocusReason.Next ? Keys.Tab : Keys.Shift | Keys.Tab;
                if (ShortcutAction?.Invoke(keys) is not { } action) return;
                args.Handled = true;
                BeginInvoke(() => { if (!IsDisposed) action(); });
            };
            _positionOwner = FindForm();
            if (_positionOwner is not null) _positionOwner.LocationChanged += ParentWindowMoved;
            Ready();
        }
        catch (Exception error)
        {
            // No runtime, or it refused to start. Mail is still readable as text.
            TimingLog.Mark($"HTML reader initialization failed: {error.GetType().Name} (0x{error.HResult:X8})");
            Failed = true;
            Visible = false;
        }
    }

    /// <summary>Shows a document. Held until the runtime is up, because a message can be opened before it
    /// has finished starting and that first message is the one being waited for.</summary>
    public void Show(string document)
    {
        if (Failed) return;
        if (!_ready) { _pending = document; return; }
        Navigate(document);
    }

    /// <summary>Fill pictures without replacing the document, moving its scroll position or repainting blank.</summary>
    public void UpdatePictures(string document)
    {
        if (Failed) return;
        if (!_ready) { _pending = document; return; }
        _pendingPictures = document;
        if (DocumentLoaded) ApplyPictures();
    }

    private async void ApplyPictures()
    {
        if (_pendingPictures is not { } document || _documentUri is not { } uri) return;
        _pendingPictures = null;
        try { await CoreWebView2!.ExecuteScriptAsync(PictureUpdateScript(document, uri)); }
        catch (Exception) { } // The readable letter remains if the view closes or rejects the update.
    }

    internal static string PictureUpdateScript(string document, string uri) => $$"""
        (() => {
          if (location.href !== new URL({{System.Text.Json.JsonSerializer.Serialize(uri)}}).href) return;
          const updated = new DOMParser().parseFromString({{System.Text.Json.JsonSerializer.Serialize(document)}}, 'text/html');
          const selector = 'img, source, [background], [style], style';
          const current = document.querySelectorAll(selector);
          const replacement = updated.querySelectorAll(selector);
          if (current.length !== replacement.length) return;
          current.forEach((element, index) => {
            const next = replacement[index];
            for (const name of ['src', 'srcset', 'background', 'style']) {
              const value = next.getAttribute(name);
              if (value !== null && value !== element.getAttribute(name)) element.setAttribute(name, value);
            }
            if (element.tagName === 'STYLE' && element.textContent !== next.textContent) element.textContent = next.textContent;
          });
        })();
        """;

    /// <summary>Identify the exact document being requested. Navigation events are asynchronous: a single
    /// boolean can be consumed by an earlier Clear, causing the following message to be refused.</summary>
    private void Navigate(string document, bool messageDocument = true)
    {
        _navigationStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        if (RoutePaneKeys && _readingContent) { _readingContent = false; Focus(); }
        _pendingPictures = null;
        DocumentLoaded = false;
        _navigationId = null;
        _messageDocument = messageDocument;
        _documentUri = WebDocuments.UriFor(document, _folder, ref _documentFile);
        CoreWebView2!.Navigate(_documentUri);
    }

    private long _navigationStarted;

    public void Clear()
    {
        _pending = null;
        if (_ready && !Failed) Navigate("<!doctype html><body style=\"background:#fff\">", messageDocument: false);
    }

    /// <summary>Writes a data: picture to a file the reader chooses.</summary>
    private void SavePicture(string dataUri)
    {
        try
        {
            var comma = dataUri.IndexOf(',');
            var header = dataUri[5..comma];
            var type = header.Split(';')[0].ToLowerInvariant();
            var bytes = header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(dataUri[(comma + 1)..])
                : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(dataUri[(comma + 1)..]));
            var extension = type switch
            {
                "image/jpeg" or "image/jpg" => ".jpg", "image/png" => ".png", "image/gif" => ".gif", "image/bmp" => ".bmp",
                "image/webp" => ".webp", "image/svg+xml" => ".svg", _ => ".img"
            };
            using var dialog = new SaveFileDialog
            {
                Title = "Save Picture As", FileName = "picture" + extension, DefaultExt = extension,
                Filter = $"{extension.TrimStart('.').ToUpperInvariant()} picture|*{extension}|All files|*.*"
            };
            if (dialog.ShowDialog(FindForm()) == DialogResult.OK) File.WriteAllBytes(dialog.FileName, bytes);
        }
        catch (Exception error)
        {
            MessageBox.Show(FindForm(), error.Message, "Could not save picture", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>The browser menu items a reader keeps, by their WebView2 names.</summary>
    internal static readonly string[] ReaderMenuItems = ["copy", "selectAll", "copyImage", "saveImageAs", "copyLinkLocation"];

    /// <summary>Strips the browser's menu down to <see cref="ReaderMenuItems"/>, tidying separators so none is
    /// left at either end or doubled.</summary>
    internal static void KeepReaderItems(IList<CoreWebView2ContextMenuItem> items)
    {
        for (var i = items.Count - 1; i >= 0; i--)
            if (items[i].Kind != CoreWebView2ContextMenuItemKind.Separator && !ReaderMenuItems.Contains(items[i].Name))
                items.RemoveAt(i);
        for (var i = items.Count - 1; i >= 0; i--)
            if (items[i].Kind == CoreWebView2ContextMenuItemKind.Separator
                && (i == 0 || i == items.Count - 1 || items[i - 1].Kind == CoreWebView2ContextMenuItemKind.Separator))
                items.RemoveAt(i);
    }

    private void Ready()
    {
        _ready = true;
        var core = CoreWebView2!;
        // Right-click offers only what a reader needs from a letter: copy text, and copy or save a picture or a
        // link. Everything else the browser would add (back, print, inspect, share) stays out.
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.ContextMenuRequested += (_, args) =>
        {
            KeepReaderItems(args.MenuItems);
            // The browser's own "Save image as" goes through its download machinery, which does not reliably
            // open a dialog here. Pictures in a letter are data: images (SafeHtml), so Mailkeep saves the bytes
            // itself through an ordinary Save dialog.
            var target = args.ContextMenuTarget;
            if (target.Kind != CoreWebView2ContextMenuTargetKind.Image || string.IsNullOrEmpty(target.SourceUri)
                || !target.SourceUri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
            var source = target.SourceUri;
            var position = 0;
            for (var i = args.MenuItems.Count - 1; i >= 0; i--)
                if (args.MenuItems[i].Name == "saveImageAs") { args.MenuItems.RemoveAt(i); position = i; }
            var save = core.Environment.CreateContextMenuItem("Save Picture As…", null, CoreWebView2ContextMenuItemKind.Command);
            save.CustomItemSelected += (_, _) => BeginInvoke(() => SavePicture(source));
            args.MenuItems.Insert(position, save);
        };
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = true;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsScriptEnabled = false;
        // A letter is read where it is. Anything that would take the pane somewhere else — a click, a
        // redirect, a window the page wants to open — goes to the browser instead, and the pane keeps the
        // message it was given.
        core.NavigationStarting += (_, args) =>
        {
            if (WebDocuments.IsSame(_documentUri, args.Uri))
            {
                _navigationId = args.NavigationId;
                StartupDiagnostics.Mark($"html_navigation_start id={args.NavigationId} message={_messageDocument}");
                return;
            }
            args.Cancel = true;
            // Superseded data/file documents are refreshes, not links to open elsewhere.
            if (args.IsUserInitiated && (args.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || args.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase))) _openLink(args.Uri);
        };
        core.NavigationCompleted += (_, args) =>
        {
            if (args.NavigationId != _navigationId) return;
            DocumentLoaded = args.IsSuccess;
            TimingLog.MarkBackground($"preview: navigation success={args.IsSuccess} error={args.WebErrorStatus} elapsed_ms={System.Diagnostics.Stopwatch.GetElapsedTime(_navigationStarted).TotalMilliseconds:F1}");
            if (DocumentLoaded) ApplyPictures();
            StartupDiagnostics.Mark($"html_navigation_complete id={args.NavigationId} message={_messageDocument} success={args.IsSuccess}");
            if (!_firstMessageLoadedLogged && _messageDocument && DocumentLoaded)
            {
                _firstMessageLoadedLogged = true;
                TimingLog.Mark("first message HTML navigation completed (not presentation)");
            }
            DocumentLoadCompleted?.Invoke(this, EventArgs.Empty);
        };
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            if (args.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || args.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) _openLink(args.Uri);
        };
        if (_pending is { } document) { _pending = null; Navigate(document); }
        else Visible = false;
    }
}
