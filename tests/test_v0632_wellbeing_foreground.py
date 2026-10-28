"""Regression contracts for v0.6.3.2 wellbeing acknowledgement and foregrounding."""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
WELLBEING = (ROOT / "agent/HomeWatchAgent/WellbeingManager.cs").read_text(encoding="utf-8")
AGENT = (ROOT / "agent/HomeWatchAgent/Program.cs").read_text(encoding="utf-8")


def _popup():
    return WELLBEING.split("internal sealed class WellbeingPopup : Form", 1)[1]


def test_reminders_use_same_win32_foreground_helpers_as_alerts():
    popup = _popup()
    for call in (
        "NativeMethods.PinTopmost(Handle)",
        "NativeMethods.ShowWindow(Handle, NativeMethods.SW_RESTORE)",
        "NativeMethods.SetForegroundWindow(Handle)",
        "Activate();",
        "BringToFront();",
    ):
        assert call in popup
        assert call in AGENT
    assert 'TopMost = true;' in popup
    assert 'ShowInTaskbar = true;' in popup
    assert 'foregroundRetry = new() { Interval = 350 }' in popup
    assert 'foregroundRetry.Start();' in popup
    assert 'foregroundRetry.Stop();' in popup
    assert 'Activated += (_, _)' in popup


def test_reminder_requires_explicit_action_before_closing():
    popup = _popup()
    assert 'FormClosing += (_, e) => { if (!completed) e.Cancel = true; };' in popup
    assert 'choice = "acknowledged"; completed = true; Close();' in popup
    assert 'choice = "snoozed"; completed = true; Close();' in popup
    assert 'AcceptButton = done;' in popup
    assert 'if (rule.AllowSnooze)' in popup
    assert 'ControlBox = false;' in popup


def test_unexpected_popup_error_does_not_log_acknowledgement():
    scheduler = WELLBEING.split('private async Task RunReminderAsync(WellbeingRule rule)', 1)[1].split('private async Task RefreshRulesAsync()', 1)[0]
    assert 'string result = "failed"' in scheduler
    assert 'else if (result == "acknowledged")' in scheduler
    assert 'EnqueueEvent(rule.Id, "acknowledged")' in scheduler
    assert 'state.SnoozedUntil = DateTime.UtcNow.AddSeconds(30);' in scheduler
    assert 'finally { showing = false; }' in scheduler
    assert 'EnqueueEvent(rule.Id, result == "snoozed" ? "snoozed" : "acknowledged")' not in scheduler


def test_release_and_existing_signed_updater_are_in_sync():
    assert "<Version>0.6.4</Version>" in (ROOT / 'agent/HomeWatchAgent/HomeWatchAgent.csproj').read_text()
    assert "$version = '0.6.4'" in (ROOT / '.github/workflows/release.yml').read_text()
    assert '#define MyAppVersion "0.6.4"' in (ROOT / 'agent/installer/HomeWatchAgent.iss').read_text()
    workflow = (ROOT / '.github/workflows/release.yml').read_text()
    assert "@('HomeWatchAgent.exe', 'HomeWatchService.exe', 'HomeWatchUpdater.exe')" in workflow
    assert 'HomeWatchInstallerBroker-win-x64.exe' in workflow
    assert (ROOT/'RELEASE-v0.6.3.2.md').is_file()
