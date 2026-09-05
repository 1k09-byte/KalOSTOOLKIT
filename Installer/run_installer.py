from __future__ import annotations

import subprocess
import sys
import time
from pathlib import Path

# Pick the freshly published single-file installer (publish-standalone.ps1
# writes it into Installer/output under the versioned name).
out_dir = Path(r"C:\KaliteKit\Installer\output")
exes = sorted(out_dir.glob("KaliteKit*.exe")) if out_dir.is_dir() else []
EXE = exes[0] if exes else Path(r"C:\KaliteKit\Installer\output\KaliteKit.Setup.exe")

if not EXE.is_file():
    print(f"Installer missing: {EXE}")
    sys.exit(2)

p = subprocess.Popen([str(EXE)], creationflags=subprocess.CREATE_NEW_CONSOLE)
print(f"Launched {EXE.name} as PID {p.pid} (new console window)")
try:
    rc = p.wait(timeout=600)
    print(f"Installer exited with code {rc}")
except subprocess.TimeoutExpired:
    print("Installer still running after 10min — killing it")
    p.kill()
    p.wait(timeout=10)
    print(f"Final exit code: {p.returncode}")
