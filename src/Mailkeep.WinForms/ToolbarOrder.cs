using System.Text.Json;
using Mailkeep.Infrastructure;

namespace Mailkeep.WinForms;

internal static class ToolbarOrder
{
    private const string Setting = "toolbar.order";

    public static void Attach(ToolStrip toolbar, MailDatabase database)
    {
        var defaults = toolbar.Items.Cast<ToolStripItem>().ToArray();
        for (var i = 0; i < defaults.Length; i++)
            defaults[i].Name = defaults[i] is ToolStripSeparator ? "separator-" + i : defaults[i].Text;
        var changing = false;
        void Apply(IEnumerable<string?> names)
        {
            changing = true;
            try
            {
                var ordered = names.Select(name => defaults.FirstOrDefault(item => item.Name == name))
                    .OfType<ToolStripItem>().Distinct().ToArray();
                toolbar.Items.Clear();
                toolbar.Items.AddRange(ordered.Concat(defaults.Except(ordered)).ToArray());
            }
            finally { changing = false; }
        }
        void Save() => _ = SettingsWrites.Save(database, Setting,
            JsonSerializer.Serialize(toolbar.Items.Cast<ToolStripItem>().Select(item => item.Name).ToArray()));
        try
        {
            if (JsonSerializer.Deserialize<string[]>(database.GetSetting(Setting) ?? "null") is { } order) Apply(order);
        }
        catch (JsonException) { }
        toolbar.AllowItemReorder = true; // Native Alt+drag, including the split button and separators.
        toolbar.ItemAdded += (_, _) =>
        {
            if (!changing && toolbar.IsHandleCreated)
                toolbar.BeginInvoke(new Action(() => { if (!toolbar.IsDisposed) Save(); }));
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Customize Toolbar…", null, (_, _) =>
        {
            using var dialog = new Form { Text = "Customize Toolbar", ClientSize = new Size(340, 380),
                StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
                MinimizeBox = false, MaximizeBox = false };
            var list = new ListBox { Left = 12, Top = 34, Width = 226, Height = 290, AccessibleName = "Toolbar button order" };
            var items = toolbar.Items.Cast<ToolStripItem>().ToList();
            void Fill(int selected)
            {
                list.Items.Clear();
                foreach (var item in items) list.Items.Add(item is ToolStripSeparator ? "— separator —" : item.Text ?? "");
                list.SelectedIndex = selected;
            }
            Fill(0);
            void Move(int delta)
            {
                var index = list.SelectedIndex;
                var next = index + delta;
                if (index < 0 || next < 0 || next >= items.Count) return;
                (items[index], items[next]) = (items[next], items[index]);
                Fill(next);
            }
            var up = new Button { Text = "Move Up", Left = 246, Top = 34, Width = 82 };
            var down = new Button { Text = "Move Down", Left = 246, Top = 68, Width = 82 };
            up.Click += (_, _) => Move(-1);
            down.Click += (_, _) => Move(1);
            var ok = new Button { Text = "OK", Left = 162, Top = 340, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", Left = 246, Top = 340, DialogResult = DialogResult.Cancel };
            dialog.Controls.AddRange([new Label { Text = "Choose a button and move it up or down.", Left = 12, Top = 12, AutoSize = true }, list, up, down, ok, cancel]);
            dialog.AcceptButton = ok;
            dialog.CancelButton = cancel;
            if (dialog.ShowDialog(toolbar.FindForm()) != DialogResult.OK) return;
            Apply(items.Select(item => item.Name));
            Save();
        });
        menu.Items.Add("Hold Alt and drag a button to reposition it").Enabled = false;
        menu.Items.Add("Reset Toolbar Order", null, (_, _) => { Apply(defaults.Select(item => item.Name)); Save(); });
        toolbar.ContextMenuStrip = menu;
        toolbar.Disposed += (_, _) => menu.Dispose();
    }
}
