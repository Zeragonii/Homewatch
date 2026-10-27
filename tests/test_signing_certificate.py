"""v0.5.4 public-certificate transport and secure update-policy regressions."""
import os
import tempfile
from datetime import datetime, timedelta, timezone
from pathlib import Path
import base64
import hashlib

_test_database = tempfile.NamedTemporaryFile(prefix='homewatch-signing-', suffix='.db', delete=False)
_test_database.close()
os.environ.setdefault('DATABASE_URL', 'sqlite:///' + _test_database.name)
os.environ.setdefault('ADMIN_USERNAME', 'admin')
os.environ.setdefault('ADMIN_PASSWORD', 'testpass')
os.environ.setdefault('SESSION_SECRET', 'test-secret')

from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.x509.oid import NameOID, ExtendedKeyUsageOID
from fastapi.testclient import TestClient
from server.app.main import app, SessionLocal, Device, sha256_text

ROOT = Path(__file__).resolve().parents[1]
client = TestClient(app)


def _make_cert(code_sign=True, ca=False):
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, 'HomeWatch Household Publisher')])
    now = datetime.now(timezone.utc)
    builder = (x509.CertificateBuilder().subject_name(name).issuer_name(name)
               .public_key(key.public_key()).serial_number(x509.random_serial_number())
               .not_valid_before(now-timedelta(days=1)).not_valid_after(now+timedelta(days=60))
               .add_extension(x509.BasicConstraints(ca=ca, path_length=None), critical=True))
    if code_sign:
        builder = builder.add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.CODE_SIGNING]), critical=False)
    return builder.sign(key, hashes.SHA256()).public_bytes(serialization.Encoding.DER)


def test_public_certificate_roundtrip_requires_authenticated_device():
    import uuid
    original = _make_cert()
    payload = {'certificate_base64': base64.b64encode(original).decode('ascii')}
    assert client.put('/api/signing-certificate', json=payload).status_code == 401
    assert client.get('/agent/signing-certificate').status_code == 422
    assert client.post('/api/login', json={'username':'admin','password':'testpass'}).status_code == 200
    result = client.put('/api/signing-certificate', json=payload)
    assert result.status_code == 200, result.text
    sha = hashlib.sha256(original).hexdigest().upper()
    assert result.json()['sha256'] == sha
    assert client.get('/api/signing-certificate').json()['sha256'] == sha
    assert client.get('/agent/signing-certificate', headers={'X-Device-ID':'other','Authorization':'Bearer wrong'}).status_code == 401
    token = 'signing-cert-token-' + uuid.uuid4().hex
    device_id = str(uuid.uuid4())
    from server.app.main import Child
    with SessionLocal() as db:
        c=Child(name='Signer tester '+uuid.uuid4().hex[:8]);db.add(c);db.flush()
        db.add(Device(id=device_id,child_id=c.id,hostname='TEST-SIGNER',name='Signer client',
                      installation_id=str(uuid.uuid4()),token_hash=sha256_text(token),agent_version='0.5.4'))
        db.commit()
    headers = {'X-Device-ID':device_id,'Authorization':'Bearer '+token}
    fetched=client.get('/agent/signing-certificate',headers=headers)
    assert fetched.status_code==200
    assert base64.b64decode(fetched.json()['certificate_base64'])==original
    assert fetched.json()['sha256']==sha
    # Never store or distribute private keys via the certificate endpoint.
    assert 'private_key' not in fetched.json()
    assert client.delete('/api/signing-certificate').status_code==200
    assert client.get('/agent/signing-certificate',headers=headers).status_code==404


def test_rejects_unsuitable_signing_certificates():
    assert client.post('/api/login',json={'username':'admin','password':'testpass'}).status_code==200
    for bad in [b'not-a-certificate', _make_cert(code_sign=False), _make_cert(ca=True)]:
        r=client.put('/api/signing-certificate',json={'certificate_base64':base64.b64encode(bad).decode('ascii')})
        assert r.status_code==400, r.text
    assert client.get('/api/signing-certificate').json()['configured'] is False


def test_update_service_requires_locally_pinned_authority_at_both_phases():
    source=(ROOT/'agent/HomeWatchService/Program.cs').read_text()
    verifier=(ROOT/'agent/HomeWatchService/SignaturePolicy.cs').read_text()
    assert source.count('SignaturePolicy.VerifyAll(')>=2
    assert 'WinVerifyTrust' in verifier
    assert 'X509Certificate.CreateFromSignedFile(file)' in verifier
    assert 'trusted-publisher.sha256' in verifier
    assert 'No local HomeWatch signing certificate has been approved' in verifier
    assert 'SHA256.HashData(signer.RawData)' in verifier
    assert 'agent/signing-certificate' not in verifier
    assert 'Settings.State' in verifier


def test_elevated_local_approval_requires_fingerprint_match():
    agent=(ROOT/'agent/HomeWatchAgent/SigningSetup.cs').read_text()
    assert 'expected.Text.Trim()' in agent
    assert 'typed != candidateSha' in agent
    assert 'new X509Store(StoreName.Root, StoreLocation.LocalMachine)' in agent
    assert 'new X509Store(StoreName.TrustedPublisher, StoreLocation.LocalMachine)' in agent
    assert 'trusted-publisher.sha256' in agent
    assert 'agent/signing-certificate' in agent
    assert 'Process.Start' not in agent


def test_tagged_actions_release_must_be_signed_and_verified():
    wf=(ROOT/'.github/workflows/release.yml').read_text()
    assert "if ($env:GITHUB_REF -like 'refs/tags/v*')" in wf
    assert 'Tagged HomeWatch releases MUST be signed' in wf
    assert 'Get-AuthenticodeSignature' in wf
    assert 'incorrect signing certificate' in wf.lower()
    assert 'Installer signature verification failed' in wf
