# post-update-tweaks.ps1
# Ships inside the KalOS update zip (next to KalOS.exe) and is executed by the
# app's "Apply changes" button, per os-changes.json. Runs elevated (the app is
# requireAdministrator), silently (no console), and its exit code decides
# success/failure in the apply log.
#
# Add your tweaks below. Keep each one idempotent so re-running after a
# partial failure is safe. Scripts are one-shot — the app does NOT roll back
# script effects.

Write-Output "post-update-tweaks: no tweaks configured."
exit 0
