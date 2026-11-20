using Mailkeep.Core;

namespace Mailkeep.WinForms;

internal sealed partial class MainForm
{
    // Global search must never steal Send, Forward, or a standard editor command from an open compose.
    private static bool SearchShortcutConflicts(string text)
    {
        if (!TryParseHotkey(text, out var modifiers, out var key) || (modifiers & 8) != 0) return false;
        var keys = key | ((modifiers & 2) != 0 ? Keys.Control : Keys.None)
            | ((modifiers & 4) != 0 ? Keys.Shift : Keys.None) | ((modifiers & 1) != 0 ? Keys.Alt : Keys.None);
        var reserved = keys switch
        {
            Keys.F1 or Keys.F3 or Keys.F4 or Keys.F7 => true,
            Keys.Alt | Keys.S or Keys.Alt | Keys.K or Keys.Alt | Keys.Enter
            or Keys.Control | Keys.Enter or Keys.Control | Keys.Shift | Keys.B or Keys.Control | Keys.Shift | Keys.R
            or Keys.Control | Keys.Shift | Keys.S or Keys.Control | Keys.Shift | Keys.U
            or Keys.Control | Keys.Shift | Keys.V or Keys.Shift | Keys.Delete
            or Keys.Control | Keys.Shift | Keys.OemPeriod or Keys.Control | Keys.Shift | Keys.Oemcomma
            or Keys.Control | Keys.OemPeriod or Keys.Control | Keys.Oemcomma => true,
            _ => false
        };
        return reserved || (modifiers == 2 && key is Keys.A or Keys.B or Keys.C or Keys.D or Keys.F or Keys.I or Keys.K
                or Keys.M or Keys.N or Keys.O or Keys.P or Keys.Q or Keys.R or Keys.S or Keys.U or Keys.V or Keys.X or Keys.Y or Keys.Z)
            || (modifiers == 1 && key is >= Keys.D0 and <= Keys.D9);
    }

    private Action? MainShortcut(Keys keys) => keys switch
    {
        Keys.Tab => () => FocusNextPane(forward: true),
        Keys.Shift | Keys.Tab => () => FocusNextPane(forward: false),
        Keys.F1 => () => MailHelp.Show(this),
        Keys.F4 or Keys.F3 when _currentContent is not null || _currentOutgoing is not null => () => MessageTextFind.Show(this, _body, _body.Visible ? null : _html.CoreWebView2),
        Keys.Alt | Keys.Enter when Current() is MessageSummary => () => { _ = ShowMessagePropertiesAsync(); },
        Keys.Control | Keys.N => () => { _ = ComposeNewAsync(); },
        Keys.Control | Keys.R => () => { _ = ReplyAsync(false); },
        Keys.Control | Keys.Shift | Keys.R => () => { _ = ReplyAsync(true); },
        Keys.Control | Keys.F => () => { _ = ForwardAsync(); },
        Keys.Control | Keys.Shift | Keys.F => () => { _ = ShowSearchPromptAsync(); },
        Keys.Control | Keys.Shift | Keys.B => ShowAddressBook,
        Keys.Control | Keys.M or Keys.F5 => () => { _ = SyncAllAsync(); },
        Keys.Control | Keys.P => PrintMessage,
        Keys.Control | Keys.I => GoToInbox,
        Keys.Control | Keys.Y => GoToFolder,
        Keys.Control | Keys.Shift | Keys.V when !EditingText() => MoveMessage,
        Keys.Control | Keys.O => () => { _ = OpenSelectedMessageAsync(); },
        Keys.Enter when _messages.Focused => () => { _ = OpenSelectedMessageAsync(); },
        Keys.Control | Keys.Q or Keys.Control | Keys.Enter => () => { _ = MarkSelectionReadAsync(true); },
        Keys.Control | Keys.Shift | Keys.U => () => { _ = MarkSelectionReadAsync(false); },
        Keys.Control | Keys.U => () => NavigateMessage(1, unread: true),
        Keys.Control | Keys.OemPeriod or Keys.Control | Keys.Shift | Keys.OemPeriod => () => NavigateMessage(1),
        Keys.Control | Keys.Oemcomma or Keys.Control | Keys.Shift | Keys.Oemcomma => () => NavigateMessage(-1),
        Keys.Control | Keys.A when _messages.Focused => () => { _ = SelectAllMessagesAsync(); },
        Keys.Delete or Keys.Control | Keys.D or Keys.Shift | Keys.Delete when _localTree.ContainsFocus && !EditingText()
            => () => { if (SelectedFolder().CanDelete) DeleteFolder(); },
        Keys.Delete or Keys.Control | Keys.D when !EditingText() => () => { _ = DeleteAsync(); },
        Keys.Shift | Keys.Delete when !EditingText() => () => { _ = DeleteMessagesAsync(null, permanently: true); },
        _ => null
    };

    // A reader window always acts on its own message, even after selection changes in the main list.
    private Action? ReadShortcut(MessageContent content, Keys keys, Action? closeReader = null) => keys switch
    {
        Keys.Control | Keys.R => () => { _ = ReplyToAsync(content, false); },
        Keys.Control | Keys.Shift | Keys.R => () => { _ = ReplyToAsync(content, true); },
        Keys.Control | Keys.F => () => { _ = ForwardContentAsync(content); },
        Keys.Control | Keys.P => () => PrintContent(content),
        Keys.Delete or Keys.Control | Keys.D => () => { _ = DeleteMessagesAsync([content.Summary]); },
        Keys.Shift | Keys.Delete => () => { _ = DeleteMessagesAsync([content.Summary], permanently: true); },
        Keys.Control | Keys.Shift | Keys.V => () => MoveMessages([content.Summary]),
        Keys.Control | Keys.U => () => { if (NavigateMessage(1, unread: true, content.Summary.Id, open: true)) closeReader?.Invoke(); },
        Keys.Control | Keys.OemPeriod or Keys.Control | Keys.Shift | Keys.OemPeriod => () => { if (NavigateMessage(1, origin: content.Summary.Id, open: true)) closeReader?.Invoke(); },
        Keys.Control | Keys.Oemcomma or Keys.Control | Keys.Shift | Keys.Oemcomma => () => { if (NavigateMessage(-1, origin: content.Summary.Id, open: true)) closeReader?.Invoke(); },
        Keys.Control | Keys.N or Keys.Control | Keys.Shift | Keys.F or Keys.Control | Keys.Shift | Keys.B
            or Keys.Control | Keys.M or Keys.F5 or Keys.Control | Keys.I or Keys.Control | Keys.Y => MainShortcut(keys),
        _ => null
    };

    private void GoToInbox()
    {
        if (_localTree.Nodes.Find("LocalFolder:" + LocalFolderKinds.Inbox, true).FirstOrDefault() is { } inbox)
        {
            _localTree.SelectedNode = inbox;
            inbox.EnsureVisible();
            _messages.Focus();
        }
    }

    private async Task ShowMessagePropertiesAsync()
    {
        if (Current() is not MessageSummary summary) return;
        try
        {
            var content = await Task.Run(() => _services.Database.GetMessage(summary.Id));
            if (content is not null && !IsDisposed) new MessagePropertiesForm(content).Show(this);
        }
        catch (Exception error) { ShowError("Could not load message properties", error); }
    }

    private void GoToFolder()
    {
        using var dialog = new BufferedForm { Text = "Go to Folder", ClientSize = new Size(360, 420),
            StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false };
        var tree = new BufferedTreeView { Dock = DockStyle.Fill, HideSelection = false, AccessibleName = "Go to folder list" };
        var originals = new Dictionary<TreeNode, TreeNode>();
        TreeNode Copy(TreeNode node)
        {
            var copy = new TreeNode(node.Text);
            originals.Add(copy, node);
            foreach (TreeNode child in node.Nodes) copy.Nodes.Add(Copy(child));
            return copy;
        }
        foreach (TreeNode node in _localTree.Nodes) tree.Nodes.Add(Copy(node));
        tree.ExpandAll();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK };
        buttons.Controls.AddRange([cancel, ok]);
        dialog.Controls.Add(tree);
        dialog.Controls.Add(buttons);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) != DialogResult.OK || tree.SelectedNode is not { } selected) return;
        var target = originals[selected];
        _localTree.SelectedNode = target;
        target.EnsureVisible();
        _messages.Focus();
    }

    private bool NavigateMessage(int direction, bool unread = false, long? origin = null, bool open = false)
    {
        var start = origin is { } id ? _rows.ToList().FindIndex(row => row is MessageSummary item && item.Id == id)
            : _messages.SelectedIndices.Count > 0 ? _messages.SelectedIndices[0] : -1;
        // A window for a message outside the current folder has no next/previous in this list.
        if (origin is not null && start < 0) return false;
        for (var index = start < 0 ? 0 : start + direction; index >= 0 && index < _rows.Count; index += direction)
        {
            if (unread && _rows[index] is not MessageSummary { IsRead: false }) continue;
            ListSelection.ClearSelection(_messages);
            _messages.SelectedIndices.Add(index);
            _messages.Items[index].Focused = true;
            _messages.EnsureVisible(index);
            if (open && _rows[index] is MessageSummary next) _ = OpenMessageAsync(next.Id);
            return true;
        }
        return false;
    }
}
