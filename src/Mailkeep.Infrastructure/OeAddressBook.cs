using System.Globalization;
using Mailkeep.Core;
using Microsoft.Data.Sqlite;

namespace Mailkeep.Infrastructure;

/// <summary>Reads OE Classic's address book so its contacts can be brought across. OE Classic keeps it as a SQLite
/// file of its own, _AddressBook.db under its user folder, one row per contact with up to three addresses. The
/// file is never opened where it lies: it is copied first and the copy read, so nothing here can write to it,
/// lock it, or leave a journal beside it while OE Classic is running.</summary>
public static class OeAddressBook
{
    public const string Origin = "oe-classic";

    /// <summary>Where OE Classic keeps it; MAILKEEP_OE_ADDRESSBOOK points a test run at a fixture instead.</summary>
    public static string DefaultPath => Environment.GetEnvironmentVariable("MAILKEEP_OE_ADDRESSBOOK") ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OEClassic", "User", "_AddressBook.db");

    public static IReadOnlyList<Contact> Read(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("OE Classic's address book was not found.", path);
        var copy = Path.Combine(Path.GetTempPath(), "mailkeep-oe-addressbook-" + Guid.NewGuid().ToString("N") + ".db");
        File.Copy(path, copy);
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = copy, Mode = SqliteOpenMode.ReadOnly, Pooling = false
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id,name_first,name_middle,name_last,name_nick,name_display,
                       email_personal,email_work,email_other,email_primary,notes
                FROM addressbook ORDER BY id
                """;
            using var reader = command.ExecuteReader();
            var contacts = new List<Contact>();
            while (reader.Read())
            {
                string Text(int column) => reader.IsDBNull(column) ? "" : reader.GetValue(column)?.ToString()?.Trim() ?? "";
                var addresses = new[] { Text(6), Text(7), Text(8) };
                // The address marked primary first, then whichever of the others is filled in.
                var primary = reader.IsDBNull(9) ? 0 : Convert.ToInt32(reader.GetValue(9), CultureInfo.InvariantCulture);
                var email = (primary is >= 0 and < 3 && addresses[primary].Length > 0 ? addresses[primary] : null)
                    ?? addresses.FirstOrDefault(value => value.Length > 0);
                if (email is null) continue;
                var name = Text(5);
                if (name.Length == 0) name = string.Join(" ", new[] { Text(1), Text(2), Text(3) }.Where(part => part.Length > 0));
                if (name.Length == 0) name = Text(4);
                contacts.Add(new Contact(0, name, email, Text(10), Origin, Text(0)));
            }
            return contacts;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(copy); } catch (IOException) { }
        }
    }
}
