# post-update-tweaks.ps1
# Ships inside the KalOS update zip (next to KalOS.exe) and is executed by the
# app's "Apply changes" button, per os-changes.json. Runs elevated (the app is
# requireAdministrator), silently (no console), and its exit code decides
# success/failure in the apply log.
#
# ══════════════════════════════════════════════════════════════════════════
#  HOW TO ADD A TWEAK — the easy way
#  1. Write your tweak below using the helpers (or plain PowerShell).
#  2. Bump "version" in os-changes.json to the new release tag
#  3. Publish. Users get the "Apply changes" button once, and this runs.
#
#  That's it — you never edit the JSON, only this file plus one number.
# ══════════════════════════════════════════════════════════════════════════
#
# Everything here is idempotent: re-running after a partial failure is safe.
# Scripts are one-shot — the app does NOT roll back script effects. For
# rollback-able registry tweaks you can also put them directly into
# os-changes.json as "type": "registry" entries instead.

# ── Helpers (log everything so the apply log shows what happened) ──────────

function Set-KalRegDWord {
    param([string]$Key, [string]$Name, [uint32]$Value)
    try {
        $k = Get-Item -LiteralPath ("Registry::" + $Key) -ErrorAction SilentlyContinue
        if ($null -eq $k) { New-Item -Path ("Registry::" + $Key) -Force | Out-Null }
        New-ItemProperty -Path ("Registry::" + $Key) -Name $Name -PropertyType DWord -Value $Value -Force | Out-Null
        Write-Output ("[reg-dword] {0} \ {1} = {2}" -f $Key, $Name, $Value)
    } catch {
        Write-Output ("[reg-dword] FAILED {0} \ {1}: {2}" -f $Key, $Name, $_.Exception.Message)
        throw
    }
}

function Set-KalRegString {
    param([string]$Key, [string]$Name, [string]$Value)
    try {
        $k = Get-Item -LiteralPath ("Registry::" + $Key) -ErrorAction SilentlyContinue
        if ($null -eq $k) { New-Item -Path ("Registry::" + $Key) -Force | Out-Null }
        New-ItemProperty -Path ("Registry::" + $Key) -Name $Name -PropertyType String -Value $Value -Force | Out-Null
        Write-Output ("[reg-string] {0} \ {1} = {2}" -f $Key, $Name, $Value)
    } catch {
        Write-Output ("[reg-string] FAILED {0} \ {1}: {2}" -f $Key, $Name, $_.Exception.Message)
        throw
    }
}

function Set-KalRegExpandString {
    # REG_EXPAND_SZ: writes the literal value — env vars like %SystemRoot% stay
    # unexpanded in the registry and get expanded by whoever reads it, exactly
    # like `reg add ... /t REG_EXPAND_SZ`.
    param([string]$Key, [string]$Name, [string]$Value)
    try {
        $k = Get-Item -LiteralPath ("Registry::" + $Key) -ErrorAction SilentlyContinue
        if ($null -eq $k) { New-Item -Path ("Registry::" + $Key) -Force | Out-Null }
        New-ItemProperty -Path ("Registry::" + $Key) -Name $Name -PropertyType ExpandString -Value $Value -Force | Out-Null
        Write-Output ("[reg-expandstr] {0} \ {1} = {2}" -f $Key, $Name, $Value)
    } catch {
        Write-Output ("[reg-expandstr] FAILED {0} \ {1}: {2}" -f $Key, $Name, $_.Exception.Message)
        throw
    }
}

function Set-KalService {
    param([string]$Name, [ValidateSet('auto','delayed','demand','manual','disabled')][string]$Startup)
    $map = @{ auto = 'auto'; delayed = 'delayed-auto'; demand = 'demand'; manual = 'demand'; disabled = 'disabled' }
    $sc = & sc.exe config $Name start= $map[$Startup] 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Output ("[service] {0} -> {1}" -f $Name, $Startup)
    } else {
        Write-Output ("[service] FAILED {0} -> {1}: {2}" -f $Name, $Startup, ($sc -join ' '))
        throw "sc.exe config $Name failed (exit $LASTEXITCODE)"
    }
}

# ══════════════════════════════════════════════════════════════════════════
#  YOUR TWEAKS GO HERE — nothing is configured right now.
# ══════════════════════════════════════════════════════════════════════════

Write-Output "post-update-tweaks: no tweaks configured."
exit 0
