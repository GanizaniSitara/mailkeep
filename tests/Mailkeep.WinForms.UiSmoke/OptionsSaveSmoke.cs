using System.Diagnostics;
using System.Reflection;
using Forms = System.Windows.Forms;

internal static class OptionsSaveSmoke
{
    // Exercise the actual OK action, including asynchronous persistence, rather than synthesizing FormClosing.
    internal static void Save(Forms.Form form)
    {
        SynchronizationContext.SetSynchronizationContext(new Forms.WindowsFormsSynchronizationContext());
        var button = ComposeToolsSmoke.Controls(form).OfType<Forms.Button>().Single(control => control.Text == "OK");
        typeof(Forms.Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, [EventArgs.Empty]);
        var clock = Stopwatch.StartNew();
        while (form.DialogResult != Forms.DialogResult.OK)
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Options OK did not persist preferences");
            Forms.Application.DoEvents(); Thread.Sleep(10);
        }
    }
}
