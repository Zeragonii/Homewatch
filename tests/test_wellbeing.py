"""HomeWatch v0.5.7: child-scoped wellbeing policies and auditable client events."""
import uuid
from pathlib import Path

import pytest
from tests.test_smoke import client, _make_app_device
from server.app.main import SessionLocal, WellbeingEvent, WellbeingRule


@pytest.fixture(autouse=True)
def logged_in():
    assert client.post('/api/login', json={'username':'admin','password':'testpass'}).status_code == 200


def make_rule(**overrides):
    payload = {
        'kind': 'hydration', 'title': 'Hydration Break!',
        'message': 'Time for a drink of water.', 'mode': 'active',
        'interval_minutes': 60, 'scheduled_times': [],
        'idle_threshold_seconds': 180, 'allow_snooze': True,
        'snooze_minutes': 5, 'enabled': True,
    }
    payload.update(overrides)
    return payload


def test_admin_create_update_delete_and_child_scoping():
    child_a = client.post('/api/children', json={'name':'Wellbeing A '+uuid.uuid4().hex[:7]}).json()['id']
    child_b = client.post('/api/children', json={'name':'Wellbeing B '+uuid.uuid4().hex[:7]}).json()['id']
    device_a, headers_a = _make_app_device(child_a)
    device_b, headers_b = _make_app_device(child_b)
    assert client.post(f'/api/children/{child_a}/wellbeing', json=make_rule()).status_code == 200
    created = client.post(f'/api/children/{child_a}/wellbeing', json=make_rule(kind='toilet', title='Toilet Break!'))
    assert created.status_code == 200, created.text
    rid = created.json()['id']
    admin = client.get('/api/wellbeing').json()
    assert any(x['id'] == rid and x['title'] == 'Toilet Break!' for x in admin['rules'])
    rules_a = client.get('/agent/wellbeing', headers=headers_a).json()['rules']
    rules_b = client.get('/agent/wellbeing', headers=headers_b).json()['rules']
    assert any(x['id'] == rid for x in rules_a)
    assert not any(x['id'] == rid for x in rules_b)

    changed = make_rule(kind='toilet', title='Visit the loo!', mode='scheduled',
                        scheduled_times=['15:00', '11:30', '15:00'], interval_minutes=60)
    update = client.put(f'/api/wellbeing/{rid}', json=changed)
    assert update.status_code == 200, update.text
    assert update.json()['scheduled_times'] == ['11:30', '15:00']
    assert client.get('/agent/wellbeing', headers=headers_a).json()['rules'][-1]['title'] == 'Visit the loo!'
    assert client.delete(f'/api/wellbeing/{rid}').status_code == 200
    assert not any(x['id'] == rid for x in client.get('/api/wellbeing').json()['rules'])
    assert not any(x['id'] == rid for x in client.get('/agent/wellbeing', headers=headers_a).json()['rules'])
    with SessionLocal() as db:
        assert db.get(WellbeingRule, rid).deleted is True


def test_client_events_are_child_authorised_and_idempotent():
    child = client.post('/api/children', json={'name':'Wellbeing Log '+uuid.uuid4().hex[:7]}).json()['id']
    other = client.post('/api/children', json={'name':'Wellbeing Other '+uuid.uuid4().hex[:7]}).json()['id']
    _, headers = _make_app_device(child)
    _, wrong_headers = _make_app_device(other)
    rid = client.post(f'/api/children/{child}/wellbeing', json=make_rule()).json()['id']
    event_id = str(uuid.uuid4())
    event = {'event_id': event_id, 'rule_id': rid, 'event':'shown'}
    assert client.post('/agent/wellbeing/events', headers=wrong_headers, json=event).status_code == 404
    assert client.post('/agent/wellbeing/events', headers=headers, json=event).status_code == 200
    assert client.post('/agent/wellbeing/events', headers=headers, json=event).status_code == 200
    assert client.post('/agent/wellbeing/events', headers=headers, json={**event,'event':'acknowledged'}).status_code == 409
    for kind in ('snoozed','acknowledged'):
        assert client.post('/agent/wellbeing/events', headers=headers, json={**event,'event_id':str(uuid.uuid4()),'event':kind}).status_code == 200
    assert client.post('/agent/wellbeing/events', headers=headers, json={**event,'event_id':'not-a-uuid'}).status_code == 400
    assert client.post('/agent/wellbeing/events', headers=headers, json={**event,'event':'dangerous'}).status_code == 400
    history = client.get('/api/wellbeing/events', params={'child_id':child}).json()['events']
    assert {e['event'] for e in history if e['title']=='Hydration Break!'} >= {'shown','snoozed','acknowledged'}
    with SessionLocal() as db:
        assert db.query(WellbeingEvent).filter(WellbeingEvent.event_id == event_id).count() == 1


def test_wellbeing_rule_validation_and_admin_protection():
    child = client.post('/api/children', json={'name':'Wellbeing Valid '+uuid.uuid4().hex[:7]}).json()['id']
    bad = [make_rule(interval_minutes=0), make_rule(idle_threshold_seconds=20),
           make_rule(mode='scheduled',scheduled_times=[]),
           make_rule(mode='scheduled',scheduled_times=['25:99']),
           make_rule(mode='powershell'), make_rule(kind='unknown'),
           make_rule(snooze_minutes=31), make_rule(title='')]
    for b in bad:
        response = client.post(f'/api/children/{child}/wellbeing',json=b)
        assert response.status_code == 400, (b,response.text)
    client.cookies.clear()
    assert client.get('/api/wellbeing').status_code == 401
    assert client.post(f'/api/children/{child}/wellbeing',json=make_rule()).status_code == 401
    assert client.get('/agent/wellbeing').status_code in (401,422)
    assert client.post('/api/login',json={'username':'admin','password':'testpass'}).status_code == 200


def test_windows_agent_scheduler_is_standalone_and_release_version():
    root = Path(__file__).resolve().parents[1]
    scheduler = (root/'agent/HomeWatchAgent/WellbeingManager.cs').read_text()
    agent = (root/'agent/HomeWatchAgent/Program.cs').read_text()
    assert 'new WellbeingManager(' in agent
    assert 'Application.Run(form)' in scheduler
    assert 'agent/wellbeing' in scheduler
    assert 'wellbeing-state.json' in scheduler
    assert 'SessionSwitchReason.SessionLock' in scheduler
    assert 'Math.Clamp((now - lastTick).TotalSeconds, 0, 5)' in scheduler
    assert 'FiredScheduledSlots' in scheduler
    assert 'snoozed' in scheduler and 'acknowledged' in scheduler
    assert (root/'agent/HomeWatchAgent/HomeWatchAgent.csproj').read_text().find('<Version>0.6.4.1</Version>') >= 0
    assert "$version = '0.6.4.1'" in (root/'.github/workflows/release.yml').read_text()
