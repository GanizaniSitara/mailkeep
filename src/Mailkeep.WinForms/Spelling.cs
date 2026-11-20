using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Mailkeep.WinForms;

internal sealed record SpellingCorrection(int Start, int Length, string Replacement);
internal sealed record SpellingIssue(int Start, int Length, IReadOnlyList<string> Suggestions);

// Windows' installed spell providers; no network service or persistent dictionary writes.
internal sealed class WindowsSpelling : IDisposable
{
    private readonly ISpellCheckerFactory _factory = (ISpellCheckerFactory)Activator.CreateInstance(
        Type.GetTypeFromCLSID(new Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC"), true)!)!;
    private ISpellChecker? _checker;
    public IReadOnlyList<string> Languages
    {
        get { _factory.SupportedLanguages(out var languages); return Strings(languages); }
    }
    public IReadOnlyList<SpellingIssue> Check(string text, string language)
    {
        if (_checker is not null) Marshal.ReleaseComObject(_checker);
        _factory.CreateSpellChecker(language, out _checker);
        _checker.Check(text, out var errors);
        var result = new List<SpellingIssue>();
        try
        {
            while (errors.Next(out var error) == 0 && error is not null)
            {
                try
                {
                    error.StartIndex(out var start); error.Length(out var length); error.Action(out var action);
                    if (action == 0) continue;
                    IReadOnlyList<string> suggestions;
                    if (action == 3) suggestions = [""];
                    else if (action == 2) { error.Replacement(out var replacement); suggestions = [replacement]; }
                    else { _checker.Suggest(text.Substring((int)start, (int)length), out var words); suggestions = Strings(words); }
                    result.Add(new((int)start, (int)length, suggestions));
                }
                finally { Marshal.ReleaseComObject(error); }
            }
        }
        finally { Marshal.ReleaseComObject(errors); }
        return result;
    }
    private static string[] Strings(IEnumString values)
    {
        try { var result = new List<string>(); var next = new string[1]; while (values.Next(1, next, IntPtr.Zero) == 0) result.Add(next[0]); return result.ToArray(); }
        finally { Marshal.ReleaseComObject(values); }
    }
    public void Dispose() { if (_checker is not null) Marshal.ReleaseComObject(_checker); Marshal.ReleaseComObject(_factory); }

    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellCheckerFactory
    {
        void SupportedLanguages(out IEnumString value);
        void IsSupported([MarshalAs(UnmanagedType.LPWStr)] string language, [MarshalAs(UnmanagedType.Bool)] out bool value);
        void CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string language, out ISpellChecker value);
    }
    [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellChecker
    {
        void LanguageTag([MarshalAs(UnmanagedType.LPWStr)] out string value);
        void Check([MarshalAs(UnmanagedType.LPWStr)] string text, out IEnumSpellingError errors);
        void Suggest([MarshalAs(UnmanagedType.LPWStr)] string word, out IEnumString suggestions);
    }
    [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumSpellingError { [PreserveSig] int Next(out ISpellingError? error); }
    [ComImport, Guid("B7C82D61-FBE8-4B47-9B27-6C0D2E0DE0A3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellingError
    {
        void StartIndex(out uint value);
        void Length(out uint value);
        void Action(out int value);
        void Replacement([MarshalAs(UnmanagedType.LPWStr)] out string value);
    }
}

internal sealed class SpellingForm : Form
{
    private readonly string _text;
    private readonly WindowsSpelling _spelling = new();
    private IReadOnlyList<SpellingIssue> _issues = [];
    private int _index;
    private readonly List<SpellingCorrection> _corrections = [];
    private readonly Label _word = new() { Left = 12, Top = 66, Width = 400, AccessibleName = "Spelling word" };
    private readonly TextBox _replacement = new() { Left = 12, Top = 112, Width = 290, AccessibleName = "Spelling replacement" };
    private readonly ListBox _suggestions = new() { Left = 12, Top = 144, Width = 290, Height = 126, AccessibleName = "Spelling suggestions" };
    private readonly Button _change = new() { Left = 316, Top = 110, Width = 96, Text = "Change" };
    private readonly Button _ignore = new() { Left = 316, Top = 150, Width = 96, Text = "Ignore" };
    private readonly Button _finish = new() { Left = 316, Top = 190, Width = 96, Text = "Finish", DialogResult = DialogResult.OK };
    public IReadOnlyList<SpellingCorrection> Corrections => _corrections;
    public string CorrectedText
    {
        get { var text = _text; foreach (var change in _corrections.OrderByDescending(c => c.Start)) text = text.Remove(change.Start, change.Length).Insert(change.Start, change.Replacement); return text; }
    }
    public SpellingForm(string text)
    {
        _text = text;
        Text = "Spelling"; ClientSize = new Size(430, 310); StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        var languages = _spelling.Languages.ToArray();
        if (languages.Length == 0) { _spelling.Dispose(); throw new InvalidOperationException("No Windows spelling dictionary is installed."); }
        var language = new ComboBox { Left = 12, Top = 28, Width = 290, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Spelling language" };
        language.Items.AddRange(languages);
        _suggestions.SelectedIndexChanged += (_, _) => { if (_suggestions.SelectedItem is string value) _replacement.Text = value; };
        _change.Click += (_, _) => { var issue = _issues[_index]; _corrections.Add(new(issue.Start, issue.Length, _replacement.Text)); _index++; ShowIssue(); };
        _ignore.Click += (_, _) => { _index++; ShowIssue(); };
        language.SelectedIndexChanged += (_, _) => { _corrections.Clear(); _index = 0; _issues = _spelling.Check(_text, (string)language.SelectedItem!); ShowIssue(); };
        Controls.AddRange([new Label { Left = 12, Top = 8, Text = "Dictionary:", AutoSize = true }, language, _word,
            new Label { Left = 12, Top = 92, Text = "Change to:", AutoSize = true }, _replacement, _suggestions, _change, _ignore, _finish]);
        var cancel = new Button { Left = 316, Top = 270, Width = 96, Text = "Cancel", DialogResult = DialogResult.Cancel };
        Controls.Add(cancel); CancelButton = cancel; AcceptButton = _change;
        var preferred = new[] { CultureInfo.CurrentUICulture.Name, "en-GB", "en-US" }.FirstOrDefault(languages.Contains);
        language.SelectedItem = preferred ?? languages[0];
    }
    private void ShowIssue()
    {
        var done = _index >= _issues.Count;
        _word.Text = done ? "Spelling check complete." : "Not in dictionary: " + _text.Substring(_issues[_index].Start, _issues[_index].Length);
        _change.Enabled = _ignore.Enabled = _replacement.Enabled = _suggestions.Enabled = !done;
        _finish.Enabled = done;
        _suggestions.Items.Clear();
        _replacement.Text = done ? "" : _text.Substring(_issues[_index].Start, _issues[_index].Length);
        if (!done) { _suggestions.Items.AddRange(_issues[_index].Suggestions.Cast<object>().ToArray()); if (_suggestions.Items.Count > 0) _suggestions.SelectedIndex = 0; }
        AcceptButton = done ? _finish : _change;
    }
    protected override void Dispose(bool disposing) { if (disposing) _spelling.Dispose(); base.Dispose(disposing); }
}
