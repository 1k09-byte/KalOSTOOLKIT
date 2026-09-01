#!/usr/bin/env python3
"""
Generate Services/TweakCatalog.g.cs from privacy.sexy batch scripts.

The three .bat files (dataC.bat / cleanup.bat / removeapps.bat) are parsed into
typed C# tweak definitions so the installer can run them natively (registry via
Microsoft.Win32, files via System.IO, DISM/vssadmin/slmgr/wevtutil via the
built-in tools, Store apps via PowerShell) instead of shipping batch scripts.

Usage:
    python tools/generate_tweaks.py [path-to-dataC.bat] [path-to-cleanup.bat] [path-to-removeapps.bat]

OneDrive and Edge sections are intentionally *not* generated — they are
hand-implemented composites in TweaksService (RemoveOneDriveAction /
RemoveEdgeAction) because they are heterogeneous multi-step sequences.
"""

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

# ── action containers ────────────────────────────────────────────────────────

class Action:
    def __init__(self, kind, **kw):
        self.kind = kind
        self.kw = kw

    def key(self):
        return (self.kind, tuple(sorted(self.kw.items())))

    def __repr__(self):
        return f"Action({self.kind}, {self.kw})"


SKIP_SECTION = re.compile(r"(?i)onedrive|edge")
REVERT_RE = re.compile(r"\(?revert\b", re.IGNORECASE)

# comment-line extractors (the apply direction; revert lines are filtered out)
RE_CLEAR_DIR = re.compile(r'^:: Clear directory contents(?: \([^)]*\))?\s*:\s*"(.+)"\s*$')
RE_DELETE_DIR = re.compile(r'^:: Delete directory(?: \([^)]*\))?\s*:\s*"(.+)"\s*$')
RE_DELETE_FILES = re.compile(r'^:: Delete files matching pattern:\s*"(.+)"\s*$')
RE_DELETE_FILE = re.compile(r'^:: Delete file\s*:\s*"(.+)"\s*$')
RE_DELETE_VALUE = re.compile(r'^:: Delete the registry value "([^"]+)" from the key "([^"]+)"')
RE_CLEAR_VALUES = re.compile(r'^:: Clear registry values from "([^"]+)"(?: \((recursively|recursive)\))?')
RE_DELETE_KEY = re.compile(r'^:: Remove the registry key "([^"]+)"')
RE_CREATE_KEY = re.compile(r'^:: Create "([^"]+)" registry key')
RE_UNINSTALL_APP = re.compile(r"^:: Uninstall '([^']+)' Store app")

# PowerShell-line extractors
RE_REG_ADD = re.compile(
    r"\$data\s*=\s*'([^']*)'\s*;.*?reg add '([^']+)' /v '([^']+)' /t '([^']+)'")
RE_FEATURE = re.compile(r"\$featureName\s*=\s*'([^']+)'")
RE_CAPABILITY = re.compile(
    r"Get-WindowsCapability -Online -Name '([^']+)' \| Remove-WindowsCapability")

# Capabilities whose DISM removal is known to hang for many minutes (or look
# stuck near 100%) on common installs — excluded so the tweaks step never
# stalls on them.
EXCLUDED_CAPABILITIES = {
    "Rsat.StorageReplica.Tools*",
}
RE_SERVICE = re.compile(r"\$service(?:Query|Name)\s*=\s*'([^']+)'")

# Services whose disable breaks connectivity itself, not just telemetry:
# NlaSvc off → network identification fails and Wi-Fi drops/fails to
# reconnect; netprofm (Network List Service) depends on NlaSvc, compounding
# it. WlanSvc excluded defensively — disabling it literally turns Wi-Fi off.
EXCLUDED_SERVICES = {
    "NlaSvc",
    "netprofm",
    "WlanSvc",
    "Wcmsvc",
}
RE_TASK = re.compile(r"\$taskPathPattern='([^']*)';\s*\$taskNamePattern='([^']*)'")
RE_HOSTS = re.compile(r"^:: Add hosts entries for (\S+)\s*$")


def unescape(line: str) -> str:
    """cmd caret-escaping in the generated scripts: ^" is a literal quote."""
    return line.replace('^"', '"')


def classify_section(title: str) -> bool:
    """True when a section should be skipped (hand-implemented composites)."""
    return bool(SKIP_SECTION.search(title))


def path_group(path: str) -> str:
    """History vs Logs for file deletions."""
    if any(k in path for k in ("Recent", "ComDlg32", "History", "WebCache")):
        return "History"
    return "Logs"


def parse_file(path: Path, catalog: dict, order: list):
    text = path.read_text(encoding="utf-8", errors="replace")
    lines = text.splitlines()
    section = ""
    hosts: dict[str, list] = {}  # section title -> domains, flushed on section change

    def flush_hosts(sec: str):
        doms = hosts.pop(sec, None)
        if doms:
            add(catalog, order, Action("HostsBlock", Domains=tuple(doms)), sec, "Privacy")

    for raw in lines:
        line = raw.rstrip()
        m = re.match(r"^echo --- (.+)$", line)
        if m:
            if section:
                flush_hosts(section)
            section = m.group(1)
            continue
        m = RE_HOSTS.match(line)
        if m:
            doms = hosts.setdefault(section, [])
            if m.group(1) not in doms:
                doms.append(m.group(1))
            continue
        if not section or classify_section(section):
            continue
        if REVERT_RE.search(line):
            continue
        # comments carry the clean payload for file / registry ops
        m = RE_CLEAR_DIR.match(line)
        if m:
            add(catalog, order, Action("DeletePath", Path=m.group(1), ContentsOnly=True),
                section, path_group(m.group(1)))
            continue
        m = RE_DELETE_DIR.match(line)
        if m:
            add(catalog, order, Action("DeletePath", Path=m.group(1), ContentsOnly=False),
                section, path_group(m.group(1)))
            continue
        m = RE_DELETE_FILES.match(line)
        if m:
            add(catalog, order, Action("DeletePath", Path=m.group(1), ContentsOnly=False),
                section, path_group(m.group(1)))
            continue
        m = RE_DELETE_FILE.match(line)
        if m:
            add(catalog, order, Action("DeletePath", Path=m.group(1), ContentsOnly=False),
                section, path_group(m.group(1)))
            continue
        m = RE_DELETE_VALUE.match(line)
        if m:
            add(catalog, order,
                Action("RegistryValueDelete", Key=m.group(2), Value=m.group(1)),
                section, "History")
            continue
        m = RE_CLEAR_VALUES.match(line)
        if m:
            add(catalog, order,
                Action("RegistryValuesClear", Key=m.group(1), Recursive=bool(m.group(2))),
                section, "History")
            continue
        m = RE_DELETE_KEY.match(line)
        if m:
            add(catalog, order, Action("RegistryKeyDelete", Key=m.group(1)), section, "History")
            continue
        m = RE_CREATE_KEY.match(line)
        if m:
            grp = "Apps" if "AppxAllUserStore" in m.group(1) else "Privacy"
            add(catalog, order, Action("RegistryKeyCreate", Key=m.group(1)), section, grp)
            continue
        m = RE_UNINSTALL_APP.match(line)
        if m:
            add(catalog, order, Action("AppxRemove", Package=m.group(1)), section, "Apps")
            continue

        # PowerShell command lines
        cmd = unescape(line)
        m = RE_REG_ADD.search(cmd)
        if m:
            data, key, value, kind = m.groups()
            add(catalog, order,
                Action("RegistrySet", Key=key, Value=value, Kind=kind, Data=data),
                section, "Privacy")
            continue
        m = RE_FEATURE.search(cmd)
        if m:
            add(catalog, order, Action("DisableFeature", Feature=m.group(1)), section, "Features")
            continue
        m = RE_CAPABILITY.search(cmd)
        if m:
            if m.group(1) in EXCLUDED_CAPABILITIES:
                continue
            add(catalog, order, Action("RemoveCapability", Capability=m.group(1)),
                section, "Capabilities")
            continue
        m = RE_TASK.search(cmd)
        if m:
            add(catalog, order, Action("DisableTask", Path=m.group(1), Name=m.group(2)),
                section, "Tasks")
            continue
        m = RE_SERVICE.search(cmd)
        if m and re.search(r"(?i)disable", section):
            if m.group(1) in EXCLUDED_SERVICES:
                continue
            add(catalog, order, Action("DisableService", Service=m.group(1)), section, "Services")
            continue
        if "wevtutil.exe el" in cmd or "wevtutil sl" in cmd:
            add(catalog, order, Action("ClearEventLogs"), section, "Logs")
            continue
        if "vssadmin delete shadows" in cmd:
            add(catalog, order,
                Action("RunTool", File="vssadmin.exe", Args="delete shadows /all /quiet",
                       Desc="Clear volume backups (shadow copies)"),
                section, "Logs")
            continue
        if "slmgr.vbs" in cmd and "/cpky" in cmd:
            add(catalog, order,
                Action("RunTool", File="cscript.exe",
                       Args='//nologo "%SYSTEMROOT%\\System32\\slmgr.vbs" /cpky',
                       Desc="Remove Windows product key from registry"),
                section, "Logs")
            continue
        if "Remove-DefaultAppAssociations" in cmd:
            add(catalog, order,
                Action("RunTool", File="dism.exe", Args="/online /Remove-DefaultAppAssociations",
                       Desc="Remove associations of default apps"),
                section, "Logs")
            continue


    if section:
        flush_hosts(section)


def add(catalog: dict, order: list, action: Action, section: str, group: str):
    """Dedupe identical actions across the three (heavily overlapping) scripts."""
    k = action.key()
    if k not in catalog:
        catalog[k] = (action, section, group)
        order.append(k)


def cs_escape(s: str) -> str:
    """Escape a string as a C# verbatim string (@\"...\")."""
    return '@"' + s.replace('"', '""') + '"'


def emit(catalog: dict, order: list) -> str:
    lines = []
    lines.append("// <auto-generated>")
    lines.append("// Generated by tools/generate_tweaks.py from the privacy.sexy")
    lines.append("// scripts (dataC.bat, cleanup.bat, removeapps.bat). Do not edit")
    lines.append("// by hand — re-run the generator to refresh the catalog.")
    lines.append("// </auto-generated>")
    lines.append("#nullable enable")
    lines.append("using System.Collections.Generic;")
    lines.append("using KalOS.Models;")
    lines.append("")
    lines.append("namespace KalOS.Services")
    lines.append("{")
    lines.append("    /// <summary>The native tweak catalog (generated).</summary>")
    lines.append("    public static partial class TweakCatalog")
    lines.append("    {")
    lines.append("        /// <summary>Every tweak the installer can run, deduplicated across the source scripts.</summary>")
    lines.append("        public static readonly IReadOnlyList<TweakDef> All = new TweakDef[]")
    lines.append("        {")
    for i, k in enumerate(order):
        action, section, group = catalog[k]
        expr = emit_action(action)
        lines.append(f'            new({cs_escape(section)}, TweakGroup.{group}, {expr}),')
    lines.append("        };")
    lines.append("    }")
    lines.append("}")
    return "\n".join(lines)


def emit_action(a: Action) -> str:
    k = a.kw
    if a.kind == "RegistrySet":
        kind = {"REG_DWORD": "TweakValueKind.Dword",
                "REG_SZ": "TweakValueKind.String",
                "REG_MULTI_SZ": "TweakValueKind.MultiString"}.get(k["Kind"])
        if kind is None:
            raise ValueError(f"unknown registry kind {k['Kind']}")
        return (f"new RegistrySetAction({cs_escape(k['Key'])}, {cs_escape(k['Value'])}, "
                f"{kind}, {cs_escape(k['Data'])})")
    if a.kind == "RegistryValueDelete":
        return f"new RegistryValueDeleteAction({cs_escape(k['Key'])}, {cs_escape(k['Value'])})"
    if a.kind == "RegistryValuesClear":
        return (f"new RegistryValuesClearAction({cs_escape(k['Key'])}, "
                f"{'true' if k['Recursive'] else 'false'})")
    if a.kind == "RegistryKeyCreate":
        return f"new RegistryKeyCreateAction({cs_escape(k['Key'])})"
    if a.kind == "RegistryKeyDelete":
        return f"new RegistryKeyDeleteAction({cs_escape(k['Key'])})"
    if a.kind == "DeletePath":
        return (f"new DeletePathAction({cs_escape(k['Path'])}, "
                f"{'true' if k['ContentsOnly'] else 'false'})")
    if a.kind == "AppxRemove":
        # The Deprovisioned marker key is generated separately (as a
        # RegistryKeyCreateAction) by the same section, so the action itself
        # does not need to carry it.
        return f"new AppxRemoveAction({cs_escape(k['Package'])}, null)"
    if a.kind == "DisableFeature":
        return f"new DisableFeatureAction({cs_escape(k['Feature'])})"
    if a.kind == "RemoveCapability":
        return f"new RemoveCapabilityAction({cs_escape(k['Capability'])})"
    if a.kind == "DisableService":
        return f"new DisableServiceAction({cs_escape(k['Service'])})"
    if a.kind == "DisableTask":
        return f"new DisableTaskAction({cs_escape(k['Path'])}, {cs_escape(k['Name'])})"
    if a.kind == "HostsBlock":
        domains = ", ".join(cs_escape(d) for d in k["Domains"])
        return f"new HostsBlockAction(new[] {{ {domains} }})"
    if a.kind == "ClearEventLogs":
        return "new ClearEventLogsAction()"
    if a.kind == "RunTool":
        return (f"new RunToolAction({cs_escape(k['File'])}, {cs_escape(k['Args'])}, "
                f"{cs_escape(k['Desc'])})")
    raise ValueError(f"unhandled action {a.kind}")


def main():
    args = sys.argv[1:]
    if len(args) < 3:
        print("usage: generate_tweaks.py dataC.bat cleanup.bat removeapps.bat")
        return 1
    catalog = {}
    order = []
    for p in args:
        parse_file(Path(p), catalog, order)

    out = ROOT / "Services" / "TweakCatalog.g.cs"
    out.write_text(emit(catalog, order), encoding="utf-8")

    counts = {}
    for _, (_, _, grp) in catalog.items():
        counts[grp] = counts.get(grp, 0) + 1
    print(f"wrote {out} — {len(catalog)} tweaks")
    for grp in sorted(counts):
        print(f"  {grp:14s} {counts[grp]}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
