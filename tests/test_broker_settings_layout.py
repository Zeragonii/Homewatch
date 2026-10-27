from pathlib import Path
ROOT = Path(__file__).resolve().parents[1]


def test_two_visible_action_rows():
    source = (ROOT / 'agent/HomeWatchAgent/DefenderSetup.cs').read_text()
    assert 'RowStyle(SizeType.Absolute, 86)' in source
    assert 'actions.Controls.Add(securityActions, 0, 0)' in source
    assert 'actions.Controls.Add(brokerActions, 0, 1)' in source
    assert 'brokerActions.Controls.Add(broker)' in source
    assert 'securityActions.Controls.Add(signing)' in source
    assert 'WrapContents = false' in source
    assert 'RowStyle(SizeType.Absolute, 38)' not in source


def test_broker_is_local_admin_entrypoint_not_remote():
    source = (ROOT / 'agent/HomeWatchAgent/DefenderSetup.cs').read_text()
    assert 'InstallerBrokerClient.ConfigureWithAdministrator()' in source
    assert 'OpenWithUac()' in source
    assert (ROOT / 'agent/HomeWatchInstallerBroker/HomeWatchInstallerBroker.csproj').exists()


def test_numeric_patch_version():
    assert '<Version>0.6.0</Version>' in (ROOT / 'agent/HomeWatchAgent/HomeWatchAgent.csproj').read_text()
    assert "$version = '0.6.0'" in (ROOT / '.github/workflows/release.yml').read_text()
