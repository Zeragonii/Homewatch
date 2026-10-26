from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def test_web_uses_uploaded_logo_and_favicon():
    html = (ROOT / "server/app/static/index.html").read_text()
    assert '/static/homewatch-logo.png' in html
    assert 'rel="icon"' in html


def test_launcher_shortcuts_are_renamed():
    iss = (ROOT / "agent/installer/HomeWatchAgent.iss").read_text()
    assert 'HomeWatch Launcher' in iss
    assert 'HomeWatch My Apps' not in iss


def test_executable_embeds_application_icon():
    csproj = (ROOT / "agent/HomeWatchAgent/HomeWatchAgent.csproj").read_text()
    assert '<ApplicationIcon>HomeWatch.ico</ApplicationIcon>' in csproj
    assert (ROOT / "agent/HomeWatchAgent/HomeWatch.ico").exists()


def test_portal_uses_its_defined_icon_helper():
    form = (ROOT / "agent/HomeWatchAgent/AppPortalForm.cs").read_text()
    agent = (ROOT / "agent/HomeWatchAgent/Program.cs").read_text()
    assert 'Icon = AgentContext.LoadBrandIcon();' in form
    assert 'Icon = Program.LoadBrandIcon();' not in form
    assert 'internal static Icon LoadBrandIcon()' in agent
