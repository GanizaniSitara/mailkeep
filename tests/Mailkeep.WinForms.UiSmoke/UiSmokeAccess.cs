using System.Reflection;
using Mailkeep.Infrastructure;
using System.Drawing;
using System.Windows.Forms;

namespace Mailkeep.WinForms;

/// <summary>Reaches the client's internal helpers from the smoke runner, by reflection on the built assembly.</summary>
internal static class UiSmokeAccess
{
    public static bool IsInsidersFixture(string app)
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MailkeepTestRig", "bundle", "app") + Path.DirectorySeparatorChar;
        return Environment.MachineName.Equals("DESKTOP-JPRPJ73", StringComparison.OrdinalIgnoreCase)
            && Path.GetFullPath(app).StartsWith(expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Draws the filing drop-down's rows for a store's folders into an image, as the open list draws
    /// them, without showing a window or saving anything.</summary>
    public static int RenderTree(string app, MailkeepServices services, string output)
    {
        var assembly = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(app))!, "Mailkeep.WinForms.dll"));
        var type = assembly.GetType("Mailkeep.WinForms.FilingShortcuts", true)!;
        var choices = ((System.Collections.IEnumerable)type.GetMethod("Tree")!.Invoke(null, [services.Database.ListLocalFolders()])!).Cast<object>().ToArray();
        using var box = new ComboBox { Font = new Font("Tahoma", 8.25f), Width = 300 };
        type.GetMethod("DrawAsTree")!.Invoke(null, [box]);
        box.Items.AddRange(choices);
        var rows = Math.Min(choices.Length, 60);
        using var image = new Bitmap(300, rows * 18 + 18);
        using var graphics = Graphics.FromImage(image);
        graphics.Clear(SystemColors.Window);
        var raise = typeof(ComboBox).GetMethod("OnDrawItem", BindingFlags.Instance | BindingFlags.NonPublic)!;
        raise.Invoke(box, [new DrawItemEventArgs(graphics, box.Font, new Rectangle(0, 0, 300, 18), 0, DrawItemState.ComboBoxEdit)]);
        for (var index = 0; index < rows; index++)
            raise.Invoke(box, [new DrawItemEventArgs(graphics, box.Font, new Rectangle(0, 18 + index * 18, 300, 18), index, DrawItemState.None)]);
        image.Save(output);
        return choices.Length;
    }
}
