using System.Reflection;
using System.Windows.Forms;
using Mailkeep.Core;
using Mailkeep.Infrastructure;

// Exercises the actual archive control hierarchy without showing or activating any window.
internal static class ArchiveTreeSmoke
{
    public static int Run(string app, string root)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var paths = new AppPaths(root);
                var location = Path.Combine(root, "fixture-archive");
                foreach (var path in new[] { "Company@Example/2023/Team/A", "Company@Example/2024/Team/A", "Company@Example/2024/Empty" })
                {
                    using var bundle = FolderBundle.Create(Path.Combine(location, path, FolderBundle.FileName), path.Split('/')[^1], "local");
                }
                var store = new ArchiveStore(paths);
                store.Add(location);
                var assembly = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(app))!, "Mailkeep.WinForms.dll"));
                var mainType = assembly.GetType("Mailkeep.WinForms.MainForm", true)!;
                var localRoot = new TreeNode("All Local Mail");
                TreeNode Local(string key, string name, string kind, string origin = "native") => new(name)
                {
                    Tag = new LocalFolder(key, null, name, kind, 100, origin)
                };
                var archiveBranch = Local("archive-parent", "Archive_2023", LocalFolderKinds.User);
                var importedType = mainType.GetNestedType("MailFolder", BindingFlags.NonPublic)!;
                var imported = new TreeNode("MyStuff_2023  (272)")
                {
                    Tag = Activator.CreateInstance(importedType, ["imported-2023", "OE", "Archive_2023/MyStuff_2023", "MyStuff_2023"])
                };
                archiveBranch.Nodes.Add(imported);
                archiveBranch.Nodes.Add(Local("restored", "MyStuff_2022", LocalFolderKinds.User, "restored-archive"));
                archiveBranch.Nodes.Add(Local("earlier", "MyStuff_2019", LocalFolderKinds.User, "archive"));
                localRoot.Nodes.Add(archiveBranch);
                localRoot.Nodes.Add(Local("deleted", "Deleted Items", LocalFolderKinds.Deleted));
                localRoot.Nodes.Add(Local("inbox", "Inbox", LocalFolderKinds.Inbox));
                localRoot.Nodes.Add(Local("custom", "_Pinned", LocalFolderKinds.User));
                mainType.GetMethod("SortLocalFolderNodes", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [localRoot, true]);
                if (!localRoot.Nodes.Cast<TreeNode>().Select(node => node.Text)
                    .SequenceEqual(new[] { "Inbox", "Deleted Items", "_Pinned", "Archive_2023" })
                    || !archiveBranch.Nodes.Cast<TreeNode>().Select(node => node.Text)
                    .SequenceEqual(new[] { "MyStuff_2019", "MyStuff_2022", "MyStuff_2023  (272)" }))
                    throw new InvalidOperationException("Mixed imported/restored folders were not alphabetized with standard folders kept first.");
                Console.WriteLine("PASS local-folder ordering: mixed imported/restored siblings, standard folders, punctuation, counts");
                var type = assembly.GetType("Mailkeep.WinForms.ArchiveWindow", true)!;
                using var window = (Form)Activator.CreateInstance(type, [store, (Action<long>)(_ => { }), (Action)(() => { }), null])!;
                type.GetMethod("Reload")!.Invoke(window, null);
                var tree = (TreeView)type.GetField("_tree", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var loaded = System.Diagnostics.Stopwatch.StartNew();
                while (tree.Nodes.Count == 0)
                {
                    if (loaded.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Archive folders did not load");
                    Application.DoEvents(); Thread.Sleep(10);
                }
                var company = tree.Nodes[0].Nodes[0];
                if (company.Text != "Company@Example" || company.Nodes.Count != 2
                    || company.Nodes[0].Text != "2023" || company.Nodes[1].Text != "2024"
                    || company.Nodes[0].Nodes[0].Nodes[0].Text != "A"
                    || company.Nodes[1].Nodes[0].Text != "Empty")
                    throw new InvalidOperationException("Archive control flattened or lost part of the folder hierarchy.");
                if (window.Visible) throw new InvalidOperationException("Fixture window must remain hidden.");
                var chooserType = assembly.GetType("Mailkeep.WinForms.FolderChoiceForm", true)!;
                foreach (var copying in new[] { false, true })
                {
                    using var chooser = (Form)Activator.CreateInstance(chooserType,
                        [Array.Empty<LocalFolder>(), null, true, false, true, copying])!;
                    _ = chooser.Handle;
                    var setState = typeof(Control).GetMethod("SetState", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var visible = Enum.Parse(setState.GetParameters()[0].ParameterType, "Visible");
                    setState.Invoke(chooser, [visible, true]);
                    void CreateChildren(Control parent)
                    {
                        foreach (Control child in parent.Controls) { child.CreateControl(); CreateChildren(child); }
                    }
                    CreateChildren(chooser);
                    chooser.PerformLayout();
                    using var bitmap = new System.Drawing.Bitmap(chooser.Width, chooser.Height);
                    chooser.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, chooser.Width, chooser.Height));
                    bitmap.Save(Path.Combine(root, (copying ? "archive-copy" : "archive-restore") + "-picker.png"));
                    foreach (var (name, area) in new[] {
                        ("landscape", new System.Drawing.Rectangle(0, 0, 1280, 720)),
                        ("portrait", new System.Drawing.Rectangle(-800, 0, 800, 1280)),
                        ("secondary-offset", new System.Drawing.Rectangle(1280, -200, 1920, 1080)) })
                    {
                        using var screen = new System.Drawing.Bitmap(area.Width, area.Height);
                        using var graphics = System.Drawing.Graphics.FromImage(screen);
                        graphics.Clear(System.Drawing.SystemColors.AppWorkspace);
                        graphics.DrawImageUnscaled(bitmap, (area.Width - bitmap.Width) / 2, (area.Height - bitmap.Height) / 2);
                        screen.Save(Path.Combine(root, (copying ? "archive-copy-" : "archive-restore-") + name + ".png"));
                    }
                    setState.Invoke(chooser, [visible, false]);
                }
                Console.WriteLine("PASS archive-tree actual-control nested-years duplicate-leaves empty-folder @-name no-window-shown");
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
        return 0;
    }
}
