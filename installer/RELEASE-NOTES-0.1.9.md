# Mailkeep 0.1.9 public beta

Install the Windows x64 MSI over 0.1.8 to keep your mail and settings. The installer is unsigned and Windows may display a SmartScreen prompt.

- Mailkeep preserves local mail when an IMAP server renumbers messages and collects existing mail on the first receive.
- Interrupted sends now show recovery options when delivery is uncertain. Mailkeep records SMTP acceptance to prevent automatic redelivery.
- Extracted attachments carry Windows' internet-file marker. The reading pane identifies blocked HTML resources.
- Receiving mail checkpoints progress as messages are saved. Credential reads and recovery controls remain responsive during background work.
- The installer includes SQLite 3.54.0 and .NET 10.0.15.

Gmail and Outlook.com/Hotmail have been used with live accounts. Yahoo, AOL, iCloud and Fastmail presets remain untested end to end. Please omit private mail and credentials when reporting issues.
