# Driver Store Manager — Validation Notes (spec §12)

Implementation: `Services/DriverStoreNative.cs` (the only file allowed to call
`drvstore.dll`/`setupapi.dll`/`newdev.dll`), `Services/DriverStoreProvider.cs`
(`IDriverStoreProvider` + native + pnputil providers),
`Services/SmartCleanupClassifier.cs`, `Services/RestorePointService.cs`,
`ViewModels/DriverStoreViewModel.cs`, `Views/DriverStorePage.xaml(.cs)`,
`Models/DriverStoreModels.cs`, tests in `tests/Services/DriverStoreTests.cs`.

Architecture decision (spec §3): native `drvstore.dll` + SetupAPI primary
(RAPR parity — boot-critical flags, file lists, install dates, offline
support), `pnputil.exe` shell-out as the built-in fallback behind the same
interface. DISM was not implemented; pnputil was chosen over DISM for the
fallback because it is a shipped, supported OS component the app already
shells out to, and it covers every operation the page needs online.

## Risk-register items VALIDATED during implementation

- **8.1 (undocumented API compatibility) — mitigated by design, failure path
  tested indirectly.** All drvstore P/Invokes are isolated in one file; the
  ViewModel catches `DllNotFoundException`/`EntryPointNotFoundException` and
  offers the pnputil fallback in the UI. Not yet exercised against a real
  machine where drvstore exports are missing — the fallback path itself is
  wired but its live activation is design-only.
- **10.3 (boot-critical gate cannot be bypassed) — TESTED.**
  `Cleanup_NeverSelectsBootCritical_RegardlessOfState` asserts no classifier
  output ever contains a `BootCritical` package. The classifier is a pure
  function; the review UI renders only its output and there is no other
  path that builds the candidate list.
- **8.4 partial (smart-cleanup false positives) — TESTED.** In-use packages
  are never candidates (`Cleanup_NeverSelectsInUse_EvenWhenOlder`); the
  newest member of a group is always kept even when unused;
  disconnected-device associations are surfaced in the per-candidate
  reasoning (`Cleanup_DisconnectedDeviceMentionedInReason`) rather than
  presented identically to confirmed-superseded packages. Printers/USB are
  NOT excluded from candidacy as a class — they are distinguished via the
  "associated with currently-disconnected device(s)" reasoning text only.
- **10.1 partial (enumeration accuracy) — parser tested, live
  cross-check not yet done.** The pnputil parser is unit-tested against a
  representative `pnputil /enum-drivers` transcript (field order, version
 /date, inbox detection, garbage input). A live cross-check of the native
  enumeration against pnputil on real hardware has NOT been performed.
- **8.6 partial (string marshaling) — struct layouts ported verbatim from
  RAPR** (MAX_PATH=260, LOCALE_NAME_MAX_LENGTH=85, 256-char enum callback
  string), and the property-get path uses a 2048-byte buffer like the
  reference. No long-path/long-provider-name package has been tested.
- **7.3 (restore point) — implemented with fail-safe semantics:** delete
  flows ABORT when restore-point creation fails; the restore-point
  appearance itself has not been verified via `vssadmin list shadows`.

## Risk-register items DESIGN-ONLY / NOT YET VERIFIED

- **8.2 (boot-critical removal)** — UI friction implemented (distinct
  dialog, typed confirmation phrase naming the INF, spec 7.1), but never
  exercised against a real boot-critical package.
- **8.3 (force-delete of in-use driver)** — flow implemented (device
  disclosure by name, explicit per-item acknowledgment, never silently
  batched; `DiUninstallDriverW` + `SetupUninstallOEMInf SUOI_FORCEDELETE`
  per RAPR). The spec §10.4 recovery-path test (force-delete on a test VM,
  confirm "no driver" state, reinstall, confirm recovery) has NOT been run.
- **8.5 / §10.5 (offline-image isolation)** — offline support implemented
  via the store's own device-node enumeration and `DriverStoreDelete
  UNCONFIGURE`, with per-session path re-confirmation; no mounted-VHD test
  comparing online-store snapshots before/after has been run.
- **§10.2 (export round-trip)** — export uses `DriverStoreCopyW` (native)
  or `pnputil /export-driver` (fallback); no automated file-list round-trip
  verification exists yet. `GetDriverFiles` provides the reference list an
  automated check would compare against.
- **§7.5 live behavior / §10.6 long-string test** — untested on real images.
- **Non-elevated failures (§8.7)** — low risk in this app: the process runs
  `requireAdministrator` (app.manifest), so no separate elevation path was
  needed; a denied store open surfaces as a Win32Exception message.

## Known limitations

- The pnputil fallback cannot report boot-critical status, install dates,
  extension IDs, or binary file lists; the UI surfaces this via the
  fallback explanation card. Inbox classification in the fallback is
  signer/provider heuristic (`Microsoft Windows*` prefix, excluding
  "Hardware Compatibility Publisher" signers, which are OEM-published).
- Smart Cleanup groups by (class + original INF name). Packages whose
  vendor reuses one INF name across unrelated classes stay in separate
  groups; vendors that rename their INF every release will not group.
- The restore-point opt-out setting (spec 7.3 "if the user has opted out")
  is not yet exposed in Settings; restore points are currently always
  required, and creation failure aborts the delete.
