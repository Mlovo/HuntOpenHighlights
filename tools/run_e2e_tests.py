#!/usr/bin/env python3
"""Automated runner for HuntReplayStudio Interactive Video Trimming E2E Test Suite (Tiers 1-4)."""
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def main():
    print(f"=== HuntReplayStudio E2E Test Runner ===")
    print(f"Working Directory: {ROOT}")
    cmd = ["dotnet", "run", "--project", str(ROOT / "tests" / "HuntReplay.E2ETests")]
    if len(sys.argv) > 1:
        cmd.extend(sys.argv[1:])
    
    result = subprocess.run(cmd, cwd=str(ROOT))
    sys.exit(result.returncode)

if __name__ == "__main__":
    main()
