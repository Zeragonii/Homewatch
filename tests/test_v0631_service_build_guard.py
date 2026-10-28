"""Regression for the GitHub Actions 0.6.3.1 C# safety-check failure."""

from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
SERVICE = (ROOT / "agent/HomeWatchService/Program.cs").read_text(encoding="utf-8")
WORKFLOW = (ROOT / ".github/workflows/release.yml").read_text(encoding="utf-8")

def test_service_passes_original_ci_process_start_guard():
    # Exact DOTALL regex used by the PowerShell preflight step.
    pattern = r"Process\.Start\s*\(.*?\)\s*\?\?\s*throw"
    assert "Check service source for invalid standalone null-coalescing throw" in WORKFLOW
    assert not re.search(pattern, SERVICE, re.DOTALL)

def test_sc_process_start_checks_null_explicitly():
    assert 'if (process is null)\n            throw new IOException("Could not start sc.exe");' in SERVICE
    assert "RedirectStandardOutput = true, RedirectStandardError = true" in SERVICE

def test_bridge_stays_three_executables():
    assert "@('HomeWatchAgent.exe', 'HomeWatchService.exe', 'HomeWatchUpdater.exe')" in WORKFLOW
    assert 'HomeWatchInstallerBroker-win-x64.exe' in WORKFLOW
    assert "$version = '0.6.3.1'" in WORKFLOW
