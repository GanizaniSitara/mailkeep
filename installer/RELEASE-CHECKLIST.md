# Private beta release checks

This is a checklist, not approval to publish.

## Packaging evidence (12 November 2026)

- Private 0.1.0 and 0.1.1 MSI candidates passed independent Windows 10 Beta VM
  install, launch, upgrade, downgrade rejection, repair, uninstall and reinstall.
- Three invented MIME messages retained exact raw bytes, HTML, attachments and
  folder locations. Encrypted fake account bytes and preferences also survived.
- The visible wizard, Start menu shortcut and HTML reading pane worked.
- Both candidates had identical application payloads: cross-schema application
  upgrade remains unproven.
- Extracted payload hashes matched the manifests. No mailbox or credential-store
  files were packaged. Product-assembly scans for known owner addresses and profile
  paths found none; this was a limited scan, not a complete security audit.
- The notices-inclusive 0.1.2 candidate then passed a separate lifecycle run:
  upgrade from 0.1.0, downgrade rejection, repair, uninstall and reinstall, with
  retained fixture data checked after each step. All 519 extracted file hashes
  match; the original 495 payload files are unchanged and 24 notice files were
  added. Full-payload checks found no known configured email addresses or PEM
  private-key markers; encoded secrets and unknown identifiers remain outside
  that scan's scope.

Candidate hashes and acceptance limits are recorded in private release evidence.

On 13 November, private MSI 0.1.3 bundled the deployed concise send-error GUI
and dependency notices. The independent Beta VM passed an actual 0.1.2-to-0.1.3
upgrade with different application binaries, explicit upgraded-GUI startup,
downgrade rejection, repair, uninstall and reinstall. The three invented messages
retained exact MIME bytes, HTML, attachments, folder locations, encrypted account
bytes and settings. Its 519 extracted payload files, installer conditions and
known-address/private-key-marker scan also passed. This does not exercise a new
database schema.

Private MSI 0.1.4 passed an actual 0.1.3-to-0.1.4 upgrade, responsive baseline
and upgraded application startup, downgrade rejection, repair, uninstall and
reinstall. Exact fixture MIME, HTML, attachments, folders, encrypted fake account
and settings survived each check. All 519 installed files matched the candidate
manifest; final fixture cleanup passed. The nine prerequisite/installer-condition
cases also passed for this exact package. This is not schema-migration or
real-provider acceptance evidence.

## Outstanding release conditions

Private MSI 0.1.6 includes the subsequent draft-filing, search-reader navigation,
mixed-folder sorting and five-second Inbox-arrival fixes. Its actual 0.1.5-to-0.1.6
upgrade and all 18 lifecycle steps passed in the independent Windows 10 VM.
The three invented messages, HTML, attachments, account and preferences survived.
All 519 extracted and installed payload files matched; nine installer-condition
cases and 379 backend tests passed. Known-address/profile/private-key literal
checks found no matches. These are bounded checks, not comprehensive privacy
clearance, provider acceptance or permission to publish.

- Publish the Yahoo, AOL, iCloud and Fastmail presets as untested in the release notes and account setup. Continue provider testing after release. Gmail and Outlook.com have live usage.
- Verify the GitHub issue tracker is publicly accessible before publishing.
- OAuth registration: using Thunderbird's desktop client IDs for public beta
  by owner decision (14 November 2026); login prompt displays Thunderbird.
- Signing: shipping unsigned public beta by owner decision (14 November 2026);
  users may see a Windows SmartScreen prompt.
- Artwork rights: cleared by owner decision (14 November 2026).
- Finish dependency/asset notices review. Included texts do not constitute legal
  clearance for every component or establish ownership of Mailkeep branding.
- Rerun acceptance against the exact final package, including the supported
  OS/prerequisite matrix and an upgrade from an actual previous application build.

Proton support is deferred by owner decision and must not be advertised as tested.
No public release or push is authorized by this checklist.
