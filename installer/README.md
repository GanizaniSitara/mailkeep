# Mailkeep Beta MSI

Private beta packaging, not a published release. The MSI installs for the current
Windows user under `%LOCALAPPDATA%\Programs\Mailkeep`, without elevation. The GUI
and CLI carry separate .NET runtimes; the CLI is in the `cli` subdirectory.

The package adds a Start menu shortcut and offers Mailkeep as a Windows mail
handler. It does not change the protected default-app selection. Mail, settings
and encrypted accounts remain under `%LOCALAPPDATA%\Mailkeep`; the installer
neither packages nor removes that directory. Uninstall removes owned application
files and registry values, using remove-if-empty directory rules.

64-bit Windows 10 22H2 or later and Microsoft WebView2 Evergreen Runtime are
required. The MSI reads the OS build from the registry because Windows Installer
can report VersionNT=603 on Windows 10. The MSI detects machine and
per-user runtime registrations and stops with a clear message when absent. It
does not download a runtime or silently install another product.

Build with `mailkeep-build-msi.py` using fresh self-contained GUI and
CLI publish directories. The script uses the already-provisioned WiX toolchain,
excludes symbols, rejects unexpected payload types, emits stable component GUIDs,
and records every payload SHA256. `mailkeep-msi-inspect.py` independently extracts
and hashes all MSI files and inspects registration, upgrade and removal tables.
Packaging and verification helper scripts are maintained separately from this source tree.

The guest-only `mailkeep-msi-lifecycle.ps1` tests install, launch, upgrade,
downgrade rejection, repair, uninstall and reinstall. Its persistent fixture has
invented MIME mail, HTML, attachments, encrypted account bytes and preferences.
It refuses existing mail/accounts/settings (preserving any diagnostics-only
directory) and requires the independent Beta VM UUID.

The independent Windows 10 Beta VM passed install, launch, upgrade, downgrade
rejection, repair, uninstall and reinstall with byte-identical fixture mail and
retained settings. The visible wizard, Start menu launch and HTML rendering also
passed. Later lifecycle runs also cover upgrades between different application
builds. These prove installer retention, not an application schema upgrade.

Public release still requires real-provider evidence, exact-package acceptance,
dependency notices review and explicit publication approval. See RELEASE-CHECKLIST.md
for the recorded beta decisions on unsigned distribution, artwork and OAuth.
The current Google and Microsoft registrations belong to Thunderbird; their consent
screens can display that name. Packaging does not change existing local credentials.
