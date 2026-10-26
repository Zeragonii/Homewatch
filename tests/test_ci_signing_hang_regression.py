from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
WORKFLOW = (ROOT / '.github/workflows/release.yml').read_text(encoding='utf-8')


def test_windows_signing_does_not_import_self_signed_root_on_unattended_runner():
    signing = WORKFLOW.split('      - name: Sign Windows binaries when a trusted publisher certificate is configured', 1)[1].split('      - name: Package exact updater binaries',1)[0]
    assert 'CertStoreLocation' not in signing
    assert 'Import-Certificate' not in signing
    assert "timeout-minutes: 8" in signing
    assert "Get-AuthenticodeSignature -LiteralPath $_" in signing
    assert "'Valid', 'NotTrusted'" in signing
    assert 'SHA256]::HashData($check.SignerCertificate.RawData)' in signing
    assert 'Incorrect signing certificate' in signing


def test_installer_checks_same_self_signed_signer_pin():
    signing = WORKFLOW.split('      - name: Build Windows installer',1)[1].split('      - uses: actions/upload-artifact',1)[0]
    assert "Get-AuthenticodeSignature -LiteralPath HomeWatch-Agent-Setup-win-x64.exe" in signing
    assert "'Valid', 'NotTrusted'" in signing
    assert "$actual -ne $env:HOMEWATCH_SIGNER_SHA256" in signing


def test_client_side_signed_update_enforcement_stays_intact():
    service=(ROOT/'agent/HomeWatchService/Program.cs').read_text()
    verifier=(ROOT/'agent/HomeWatchService/SignaturePolicy.cs').read_text()
    assert service.count('SignaturePolicy.VerifyAll(') >= 2
    assert 'WinVerifyTrust' in verifier
    assert 'trusted-publisher.sha256' in verifier
