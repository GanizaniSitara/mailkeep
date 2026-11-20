using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Mailkeep.WinForms;

/// <summary>The body of a message being written, as a page: bold, lists, a forwarded letter kept as it was
/// laid out. The same runtime as the reading pane, with the page made editable and the formatting commands
/// the browser already knows driven from the toolbar.
///
/// Nothing in the page may run: a forwarded letter is somebody else's HTML, so the document's own policy
/// forbids scripts, and the commands sent from here arrive through the runtime's own channel, which that
/// policy does not govern. Pictures are allowed, since a letter being forwarded should look like the letter.</summary>
internal sealed partial class HtmlEditor : WebView2
{
    private bool _ready;
    private string? _pending;
    private string? _documentUri;
    private string? _documentFile;
    private string? _folder;

    public bool Failed { get; private set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<Keys, Action?>? ShortcutAction { get; set; }

    public HtmlEditor()
    {
        Dock = DockStyle.Fill;
        DefaultBackgroundColor = Color.White;
        AccessibleName = "Message body editor";
        CoreWebView2InitializationCompleted += Ready;
    }

    public async Task StartAsync(string dataFolder)
    {
        try
        {
            _folder = dataFolder;
            var environment = await CoreWebView2Environment.CreateAsync(null, dataFolder);
            await EnsureCoreWebView2Async(environment);
        }
        catch (Exception) { Failed = true; }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WebDocuments.Forget(ref _documentFile);
        base.Dispose(disposing);
    }

    /// <summary>Shows a page for editing. Held until the runtime is up, as the first page always is.</summary>
    public void Load(string? bodyHtml)
    {
        if (Failed) return;
        var document = Document(bodyHtml ?? "");
        if (!_ready) { _pending = document; return; }
        Navigate(document);
    }

    /// <summary>The page as written, as the HTML that will go: the body's content, wrapped so a reader's
    /// client draws it in the font it was written in.</summary>
    public async Task<string> GetHtmlAsync()
    {
        if (Failed || !_ready || CoreWebView2 is null) return "";
        var inner = await CoreWebView2.ExecuteScriptAsync("document.body.innerHTML");
        var html = System.Text.Json.JsonSerializer.Deserialize<string>(inner) ?? "";
        return "<html><head><meta charset=\"utf-8\"></head><body style=\"font-family:Arial,sans-serif;font-size:10pt\">" + html + "</body></html>";
    }

    /// <summary>Whether anything has been typed: a page with nothing but the quoted letter counts as untouched.</summary>
    public async Task<bool> IsEmptyAsync()
    {
        if (Failed || !_ready || CoreWebView2 is null) return true;
        var text = await CoreWebView2.ExecuteScriptAsync("document.body.innerText.trim().length");
        return text == "0";
    }

    public void Command(string command, string? value = null)
    {
        if (Failed || !_ready || CoreWebView2 is null) return;
        var argument = value is null ? "null" : System.Text.Json.JsonSerializer.Serialize(value);
        _ = CoreWebView2.ExecuteScriptAsync($"document.execCommand({System.Text.Json.JsonSerializer.Serialize(command)}, false, {argument})");
    }

    // Keep each text run separate so checking never joins words across paragraphs or markup.
    private const string TextNodes = "const walk=document.createTreeWalker(document.body,NodeFilter.SHOW_TEXT);const nodes=[];while(walk.nextNode()){if(!walk.currentNode.parentElement.closest('style,script,template,noscript'))nodes.push(walk.currentNode);}const text=nodes.map(n=>n.data).join('\\n');";

    public async Task<string> GetTextAsync()
    {
        if (CoreWebView2 is null) throw new InvalidOperationException("The message editor is not ready.");
        var result = await CoreWebView2.ExecuteScriptAsync("(()=>{" + TextNodes + "return text;})()");
        return System.Text.Json.JsonSerializer.Deserialize<string>(result) ?? "";
    }

    public async Task ApplyCorrectionsAsync(string snapshot, IReadOnlyList<SpellingCorrection> corrections)
    {
        if (CoreWebView2 is null) throw new InvalidOperationException("The message editor is not ready.");
        var expected = System.Text.Json.JsonSerializer.Serialize(snapshot);
        var changes = System.Text.Json.JsonSerializer.Serialize(corrections.OrderByDescending(c => c.Start));
        var result = await CoreWebView2.ExecuteScriptAsync("(()=>{" + TextNodes + "if(text!==" + expected + ")return false;"
            + "const changes=" + changes + ";const mapped=[];for(const c of changes){let offset=0;let found=false;for(const n of nodes){if(c.Start>=offset&&c.Start+c.Length<=offset+n.length){mapped.push([n,c.Start-offset,c.Length,c.Replacement]);found=true;break;}offset+=n.length+1;}if(!found)return false;}for(const [n,s,l,r] of mapped)n.replaceData(s,l,r);return true;})()");
        if (result != "true") throw new InvalidOperationException("The message changed while checking spelling. Please check it again.");
    }

    public void FocusBody()
    {
        if (Failed || !_ready || CoreWebView2 is null) return;
        Focus();
        // A delayed browser callback must not steal focus back after the user moves to an address field.
        _ = CoreWebView2.ExecuteScriptAsync("if(document.hasFocus())document.body.focus()");
    }

    /// <summary>A letter's HTML with its wrapper removed, so it can sit inside another page: the doctype,
    /// the html and body tags go, the head's styles stay, and anything that could run is stripped.</summary>
    public static string Fragment(string html)
    {
        var body = MainForm.StripActiveContent(html);
        body = EventAttribute().Replace(body, "");
        body = Regex.Replace(body, @"<!doctype[^>]*>", "", RegexOptions.IgnoreCase);
        body = Regex.Replace(body, @"</?(html|body|head)\b[^>]*>", "", RegexOptions.IgnoreCase);
        body = Regex.Replace(body, @"<meta\b[^>]*>", "", RegexOptions.IgnoreCase);
        body = Regex.Replace(body, @"<title\b[^>]*>.*?</title>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return body;
    }

    private static string Document(string bodyHtml)
    {
        const string csp = "default-src 'none'; img-src data: https: http:; style-src 'unsafe-inline'; font-src data:";
        return "<!doctype html><html><head><meta charset=\"utf-8\"><meta http-equiv=\"Content-Security-Policy\" content=\"" + csp + "\">"
            + "<style>body{font-family:Arial,sans-serif;font-size:10pt;margin:8px;outline:none}</style></head>"
            + "<body contenteditable=\"true\" spellcheck=\"true\">" + bodyHtml + "</body></html>";
    }

    private void Navigate(string document)
    {
        _documentUri = WebDocuments.UriFor(document, _folder, ref _documentFile);
        CoreWebView2.Navigate(_documentUri);
    }

    private void Ready(object? sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (!e.IsSuccess) { Failed = true; return; }
        _ready = true;
        var core = CoreWebView2;
        WebShortcuts.Attach(this, keys => ShortcutAction?.Invoke(keys));
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        // The page is the one being written and no other: a link clicked while editing opens nowhere.
        core.NavigationStarting += (_, args) => { if (!WebDocuments.IsSame(_documentUri, args.Uri)) args.Cancel = true; };
        core.NewWindowRequested += (_, args) => args.Handled = true;
        if (_pending is { } document) { _pending = null; Navigate(document); }
    }

    [GeneratedRegex(@"\s+on[a-z]+\s*=\s*(?:""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EventAttribute();
}
