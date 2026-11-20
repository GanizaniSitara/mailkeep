# Mailkeep development

Preserve unrelated work in the shared checkout.

## UI responsiveness

Keep database scans, mailbox/file writes and synchronous task waits out of interactive
UI callbacks. Capture UI input before sending work to a worker; check disposal and
discard superseded reads before applying results. Renderers must take preloaded data
rather than implicitly opening the store or recounting folders. Coalesce repeated
preference changes instead of queueing a write for every resize/drag position; preserve
the last values at shutdown. Final-close persistence is a separate, explicit drain.

When changing these paths, test a delayed or held disposable store in Beta and verify
that UI events continue, current content stays visible, and the eventual result is
correct. Fast warm-store tests alone do not establish responsiveness.

## Desktop regression tests

Run disruptive UI, screenshot and flicker regressions in an isolated Windows 10
test VM rather than on the main desktop. Build and unit tests can run locally;
avoid running foreground fixture UI tests on the main desktop unless explicitly
requested.

Automate repeatable workflow checks in the isolated VM. Extend the existing
UiSmoke scenarios, verify storage invariants independently, and retrieve and
inspect rendered evidence.

Use disposable stores and loopback SMTP/HTTP fixtures only. Never copy live
accounts, credentials or a real Mailkeep store into a test package. Checks
against an installed client should be read-only and should not move, activate,
resize or capture its windows. Never close an unsaved compose window to deploy.

For dialog layout changes, retrieve and visually inspect the rendered screenshots,
including landscape and portrait sizes and secondary-monitor offsets. Geometry
assertions alone are insufficient. State when layouts use simulated work areas
in the VM rather than physical multi-monitor/DPI testing.

After GUI changes, redeploy the tested build and verify the running published DLL
matches it. If an open dialog prevents a safe restart, preserve edits and report
that the newer build is staged rather than live.

VM access and test bundle notes: [docs/vm-ui-regressions.md](docs/vm-ui-regressions.md).
Do not install or update guest runtimes or change VM networking without explicit
approval. Test VMs use shared folders with their NIC disconnected.
