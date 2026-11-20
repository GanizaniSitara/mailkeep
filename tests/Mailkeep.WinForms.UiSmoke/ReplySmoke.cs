using System.Reflection;
using Mailkeep.Core;
using Mailkeep.Infrastructure;
using Forms = System.Windows.Forms;

internal static class ReplySmoke
{
    public static void Run(string app, MailkeepServices services)
    {
        var defaultIdentity = services.Identities.Find("fixture-3")!;
        var receivingIdentity = defaultIdentity with { Id = "receiving-identity", Email = "local@example.invalid", IsDefault = false };
        services.Identities.Upsert(receivingIdentity);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var assembly = Assembly.LoadFrom(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(app))!, "Mailkeep.WinForms.dll"));
                var mainType = assembly.GetType("Mailkeep.WinForms.MainForm", true)!;
                using var main = (Forms.Form)Activator.CreateInstance(mainType, [services.Paths.Root])!;
                var create = mainType.GetMethod("CreateReplySeed", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var composeType = assembly.GetType("Mailkeep.WinForms.ComposeForm", true)!;
                var summary = services.Database.ListMessages().Single(value => value.Account == "local-fixture");
                var content = services.Database.GetMessage(summary.Id)!;
                content = content with { Summary = summary with { Subject = "Re: Reply fixture", Sender = "Sender <sender@example.invalid>",
                    Recipients = ["Local <LOCAL@example.invalid>", "Other own <one@example.invalid>",
                        "sender@example.invalid", "First <other@example.invalid>", "OTHER@example.invalid", "last@example.invalid"] } };
                var identities = new[] { defaultIdentity, receivingIdentity };
                foreach (var all in new[] { false, true })
                {
                    var seed = create.Invoke(main, [content, all])!;
                    using var compose = (Forms.Form)Activator.CreateInstance(composeType, [identities, seed])!;
                    string Value(string name) => (string)composeType.GetProperty(name)!.GetValue(compose)!;
                    if (Value("IdentityId") != receivingIdentity.Id) throw new InvalidOperationException("Reply selected the default instead of the receiving identity.");
                    if (Value("Subject") != "Re: Reply fixture") throw new InvalidOperationException("Reply duplicated its subject prefix.");
                    var to = (IReadOnlyList<string>)composeType.GetProperty("To")!.GetValue(compose)!;
                    var cc = (IReadOnlyList<string>)composeType.GetProperty("Cc")!.GetValue(compose)!;
                    if (to.Count != 1 || MimeKit.MailboxAddress.Parse(to[0]).Address != "sender@example.invalid") throw new InvalidOperationException("Reply lost its sender.");
                    var addresses = cc.Select(value => MimeKit.MailboxAddress.Parse(value).Address).ToArray();
                    if (!addresses.SequenceEqual(all ? new[] { "other@example.invalid", "last@example.invalid" } : []))
                        throw new InvalidOperationException("Reply-all retained own addresses, duplicates or lost another recipient.");
                }
                var directed = content with { ReplyTo = ["Reply desk <reply@example.invalid>", "REPLY@example.invalid", "second@example.invalid"],
                    Summary = content.Summary with { Recipients = ["reply@example.invalid", "second@example.invalid", "other@example.invalid"] } };
                var directedSeed = create.Invoke(main, [directed, true])!;
                using var directedCompose = (Forms.Form)Activator.CreateInstance(composeType, [identities, directedSeed])!;
                var directedTo = (IReadOnlyList<string>)composeType.GetProperty("To")!.GetValue(directedCompose)!;
                var directedCc = (IReadOnlyList<string>)composeType.GetProperty("Cc")!.GetValue(directedCompose)!;
                if (!directedTo.Select(value => MimeKit.MailboxAddress.Parse(value).Address).SequenceEqual(new[] { "reply@example.invalid", "second@example.invalid" })
                    || !directedCc.SequenceEqual(new[] { "other@example.invalid" }))
                    throw new InvalidOperationException("Reply-To did not replace From or was duplicated in Cc.");
                // When the original receiving identity is unavailable, normal composer default remains usable.
                var unknown = content with { Summary = content.Summary with { Account = "unknown-account" } };
                var fallback = create.Invoke(main, [unknown, false])!;
                using var fallbackCompose = (Forms.Form)Activator.CreateInstance(composeType, [identities, fallback])!;
                if ((string)composeType.GetProperty("IdentityId")!.GetValue(fallbackCompose)! != defaultIdentity.Id)
                    throw new InvalidOperationException("Unknown-account reply did not fall back to the ready default.");
                Console.WriteLine("PASS reply identity=receiving-account fallback=default own-addresses=excluded duplicates=removed reply-all=other-recipients Reply-To=honored no-messages-sent");
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15))) throw new TimeoutException("Reply checks did not finish.");
        if (failure is not null) throw failure;
    }
}
