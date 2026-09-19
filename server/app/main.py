from __future__ import annotations

import hashlib
import hmac
import os
import secrets
import uuid
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from typing import Optional

from fastapi import Depends, FastAPI, File, Form, Header, HTTPException, Request, UploadFile
from fastapi.responses import FileResponse, HTMLResponse, JSONResponse
from fastapi.staticfiles import StaticFiles
from itsdangerous import BadSignature, URLSafeSerializer
from pydantic import BaseModel
from sqlalchemy import Boolean, Date, DateTime, ForeignKey, Integer, String, Text, UniqueConstraint, create_engine, select
from sqlalchemy.orm import DeclarativeBase, Mapped, Session, mapped_column, relationship, sessionmaker

APP_TITLE = os.getenv("APP_TITLE", "HomeWatch")
DATABASE_URL = os.getenv("DATABASE_URL", "sqlite:///./homewatch.db")
ADMIN_USERNAME = os.getenv("ADMIN_USERNAME", "admin")
ADMIN_PASSWORD = os.getenv("ADMIN_PASSWORD", "change-me")
SESSION_SECRET = os.getenv("SESSION_SECRET", "dev-only-change-me")
SCREENSHOT_DIR = Path(os.getenv("SCREENSHOT_DIR", "./screenshots"))
SCREENSHOT_DIR.mkdir(parents=True, exist_ok=True)

connect_args = {"check_same_thread": False} if DATABASE_URL.startswith("sqlite") else {}
engine = create_engine(DATABASE_URL, future=True, pool_pre_ping=True, connect_args=connect_args)
SessionLocal = sessionmaker(bind=engine, expire_on_commit=False)
serializer = URLSafeSerializer(SESSION_SECRET, salt="homewatch-admin-session")


class Base(DeclarativeBase):
    pass


class Child(Base):
    __tablename__ = "children"
    id: Mapped[int] = mapped_column(primary_key=True)
    name: Mapped[str] = mapped_column(String(100), unique=True)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=lambda: datetime.now(timezone.utc))
    devices: Mapped[list["Device"]] = relationship(back_populates="child")


class Device(Base):
    __tablename__ = "devices"
    id: Mapped[str] = mapped_column(String(36), primary_key=True)
    child_id: Mapped[int] = mapped_column(ForeignKey("children.id"))
    name: Mapped[str] = mapped_column(String(120))
    hostname: Mapped[str] = mapped_column(String(255), default="")
    os_version: Mapped[str] = mapped_column(String(255), default="")
    agent_version: Mapped[str] = mapped_column(String(40), default="")
    installation_id: Mapped[Optional[str]] = mapped_column(String(36), nullable=True, unique=True)
    token_hash: Mapped[Optional[str]] = mapped_column(String(64), nullable=True)
    current_app: Mapped[str] = mapped_column(String(255), default="")
    last_seen: Mapped[Optional[datetime]] = mapped_column(DateTime(timezone=True), nullable=True)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=lambda: datetime.now(timezone.utc))
    child: Mapped[Child] = relationship(back_populates="devices")


class PendingAgent(Base):
    __tablename__ = "pending_agents"
    installation_id: Mapped[str] = mapped_column(String(36), primary_key=True)
    secret_hash: Mapped[str] = mapped_column(String(64))
    hostname: Mapped[str] = mapped_column(String(255))
    os_version: Mapped[str] = mapped_column(String(255))
    agent_version: Mapped[str] = mapped_column(String(40))
    status: Mapped[str] = mapped_column(String(20), default="pending")
    approved_device_id: Mapped[Optional[str]] = mapped_column(String(36), nullable=True)
    delivery_token: Mapped[Optional[str]] = mapped_column(Text, nullable=True)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=lambda: datetime.now(timezone.utc))
    last_seen: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=lambda: datetime.now(timezone.utc))


class ActivityDaily(Base):
    __tablename__ = "activity_daily"
    __table_args__ = (UniqueConstraint("device_id", "day", "process_name", name="uq_activity_daily"),)
    id: Mapped[int] = mapped_column(primary_key=True)
    device_id: Mapped[str] = mapped_column(ForeignKey("devices.id"))
    day: Mapped[date] = mapped_column(Date)
    process_name: Mapped[str] = mapped_column(String(255))
    seconds: Mapped[int] = mapped_column(Integer, default=0)


class Command(Base):
    __tablename__ = "commands"
    id: Mapped[int] = mapped_column(primary_key=True)
    device_id: Mapped[str] = mapped_column(ForeignKey("devices.id"))
    kind: Mapped[str] = mapped_column(String(40))
    payload: Mapped[str] = mapped_column(Text, default="")
    status: Mapped[str] = mapped_column(String(20), default="pending")
    result: Mapped[str] = mapped_column(Text, default="")
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=lambda: datetime.now(timezone.utc))
    completed_at: Mapped[Optional[datetime]] = mapped_column(DateTime(timezone=True), nullable=True)


class Screenshot(Base):
    __tablename__ = "screenshots"
    id: Mapped[int] = mapped_column(primary_key=True)
    device_id: Mapped[str] = mapped_column(ForeignKey("devices.id"))
    command_id: Mapped[Optional[int]] = mapped_column(ForeignKey("commands.id"), nullable=True)
    filename: Mapped[str] = mapped_column(String(255))
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=lambda: datetime.now(timezone.utc))


class AgentRelease(Base):
    __tablename__ = "agent_releases"
    id: Mapped[int] = mapped_column(primary_key=True)
    version: Mapped[str] = mapped_column(String(40), unique=True)
    url: Mapped[str] = mapped_column(Text)
    sha256: Mapped[str] = mapped_column(String(64))
    channel: Mapped[str] = mapped_column(String(20), default="stable")
    promoted: Mapped[bool] = mapped_column(Boolean, default=False)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=lambda: datetime.now(timezone.utc))


Base.metadata.create_all(engine)

app = FastAPI(title=APP_TITLE)
static_dir = Path(__file__).parent / "static"
app.mount("/static", StaticFiles(directory=static_dir), name="static")


def db_session():
    db = SessionLocal()
    try:
        yield db
    finally:
        db.close()


def sha256_text(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def require_admin(request: Request):
    cookie = request.cookies.get("hw_session")
    if not cookie:
        raise HTTPException(401, "Authentication required")
    try:
        value = serializer.loads(cookie)
    except BadSignature:
        raise HTTPException(401, "Invalid session")
    if value != ADMIN_USERNAME:
        raise HTTPException(401, "Invalid session")


def agent_auth(
    x_device_id: str = Header(...), authorization: str = Header(...), db: Session = Depends(db_session)
) -> Device:
    if not authorization.startswith("Bearer "):
        raise HTTPException(401, "Missing bearer token")
    token = authorization[7:]
    device = db.get(Device, x_device_id)
    if not device or not device.token_hash or not hmac.compare_digest(device.token_hash, sha256_text(token)):
        raise HTTPException(401, "Invalid device credentials")
    return device


class LoginBody(BaseModel):
    username: str
    password: str


class ChildBody(BaseModel):
    name: str


class EnrolRequest(BaseModel):
    installation_id: str
    enrollment_secret: str
    hostname: str
    os_version: str
    agent_version: str


class EnrolClaim(BaseModel):
    installation_id: str
    enrollment_secret: str


class ApproveBody(BaseModel):
    child_id: Optional[int] = None
    device_name: Optional[str] = None
    replace_device_id: Optional[str] = None


class HeartbeatBody(BaseModel):
    hostname: str
    os_version: str
    agent_version: str
    current_app: str = ""


class ActivityBody(BaseModel):
    usage: dict[str, int]


class CommandBody(BaseModel):
    kind: str
    payload: str = ""


class CommandResultBody(BaseModel):
    result: str = ""


class ReleaseBody(BaseModel):
    version: str
    url: str
    sha256: str
    channel: str = "stable"
    promoted: bool = False


@app.get("/", response_class=HTMLResponse)
def index():
    return (static_dir / "index.html").read_text(encoding="utf-8")


@app.post("/api/login")
def login(body: LoginBody):
    if not (hmac.compare_digest(body.username, ADMIN_USERNAME) and hmac.compare_digest(body.password, ADMIN_PASSWORD)):
        raise HTTPException(401, "Invalid credentials")
    response = JSONResponse({"ok": True})
    response.set_cookie("hw_session", serializer.dumps(ADMIN_USERNAME), httponly=True, samesite="strict", secure=False)
    return response


@app.post("/api/logout")
def logout():
    response = JSONResponse({"ok": True})
    response.delete_cookie("hw_session")
    return response


@app.get("/api/dashboard", dependencies=[Depends(require_admin)])
def dashboard(db: Session = Depends(db_session)):
    children = db.scalars(select(Child).order_by(Child.name)).all()
    pending = db.scalars(select(PendingAgent).where(PendingAgent.status == "pending").order_by(PendingAgent.created_at)).all()
    devices = db.scalars(select(Device).order_by(Device.name)).all()
    today = datetime.now(timezone.utc).date()
    activity_rows = db.scalars(select(ActivityDaily).where(ActivityDaily.day == today)).all()
    activity = {}
    for row in activity_rows:
        activity.setdefault(row.device_id, []).append({"process": row.process_name, "seconds": row.seconds})
    now = datetime.now(timezone.utc)
    return {
        "children": [{"id": c.id, "name": c.name} for c in children],
        "pending": [
            {
                "installation_id": p.installation_id,
                "hostname": p.hostname,
                "os_version": p.os_version,
                "agent_version": p.agent_version,
                "first_seen": p.created_at.isoformat(),
            } for p in pending
        ],
        "devices": [
            {
                "id": d.id,
                "child_id": d.child_id,
                "child": d.child.name if d.child else "",
                "name": d.name,
                "hostname": d.hostname,
                "agent_version": d.agent_version,
                "current_app": d.current_app,
                "last_seen": d.last_seen.isoformat() if d.last_seen else None,
                "online": bool(d.last_seen and (now - d.last_seen) < timedelta(seconds=45)),
                "activity": sorted(activity.get(d.id, []), key=lambda x: x["seconds"], reverse=True)[:8],
            } for d in devices
        ],
    }


@app.post("/api/children", dependencies=[Depends(require_admin)])
def create_child(body: ChildBody, db: Session = Depends(db_session)):
    name = body.name.strip()
    if not name:
        raise HTTPException(400, "Name required")
    if db.scalar(select(Child).where(Child.name == name)):
        raise HTTPException(409, "Child already exists")
    child = Child(name=name)
    db.add(child); db.commit(); db.refresh(child)
    return {"id": child.id, "name": child.name}


@app.post("/api/pending/{installation_id}/approve", dependencies=[Depends(require_admin)])
def approve_pending(installation_id: str, body: ApproveBody, db: Session = Depends(db_session)):
    pending = db.get(PendingAgent, installation_id)
    if not pending or pending.status != "pending":
        raise HTTPException(404, "Pending device not found")
    token = secrets.token_urlsafe(48)
    if body.replace_device_id:
        device = db.get(Device, body.replace_device_id)
        if not device:
            raise HTTPException(404, "Existing device not found")
        device.installation_id = pending.installation_id
        device.token_hash = sha256_text(token)
        device.hostname = pending.hostname
        device.os_version = pending.os_version
        device.agent_version = pending.agent_version
        device.last_seen = None
    else:
        if not body.child_id or not body.device_name:
            raise HTTPException(400, "child_id and device_name are required for a new device")
        if not db.get(Child, body.child_id):
            raise HTTPException(404, "Child not found")
        device = Device(
            id=str(uuid.uuid4()), child_id=body.child_id, name=body.device_name.strip(),
            hostname=pending.hostname, os_version=pending.os_version, agent_version=pending.agent_version,
            installation_id=pending.installation_id, token_hash=sha256_text(token),
        )
        db.add(device)
    pending.status = "approved"
    pending.approved_device_id = device.id
    pending.delivery_token = token
    db.commit()
    return {"ok": True, "device_id": device.id}


@app.post("/api/devices/{device_id}/commands", dependencies=[Depends(require_admin)])
def create_command(device_id: str, body: CommandBody, db: Session = Depends(db_session)):
    if body.kind not in {"message", "screenshot"}:
        raise HTTPException(400, "Unsupported v0.1 command")
    if not db.get(Device, device_id):
        raise HTTPException(404, "Device not found")
    cmd = Command(device_id=device_id, kind=body.kind, payload=body.payload)
    db.add(cmd); db.commit(); db.refresh(cmd)
    return {"id": cmd.id, "status": cmd.status}


@app.get("/api/devices/{device_id}/screenshot", dependencies=[Depends(require_admin)])
def latest_screenshot(device_id: str, db: Session = Depends(db_session)):
    shot = db.scalar(select(Screenshot).where(Screenshot.device_id == device_id).order_by(Screenshot.created_at.desc()))
    if not shot:
        raise HTTPException(404, "No screenshot available")
    path = SCREENSHOT_DIR / shot.filename
    if not path.exists():
        raise HTTPException(404, "Screenshot file missing")
    return FileResponse(path, media_type="image/png")


@app.post("/api/releases", dependencies=[Depends(require_admin)])
def create_release(body: ReleaseBody, db: Session = Depends(db_session)):
    digest = body.sha256.lower().strip()
    if len(digest) != 64 or any(c not in "0123456789abcdef" for c in digest):
        raise HTTPException(400, "sha256 must be 64 hex characters")
    existing = db.scalar(select(AgentRelease).where(AgentRelease.version == body.version))
    if existing:
        existing.url, existing.sha256, existing.channel, existing.promoted = body.url, digest, body.channel, body.promoted
        release = existing
    else:
        release = AgentRelease(version=body.version, url=body.url, sha256=digest, channel=body.channel, promoted=body.promoted)
        db.add(release)
    if body.promoted:
        for r in db.scalars(select(AgentRelease).where(AgentRelease.channel == body.channel)).all():
            if r is not release:
                r.promoted = False
    db.commit(); db.refresh(release)
    return {"id": release.id, "version": release.version, "promoted": release.promoted}


@app.post("/agent/enrol/request")
def agent_enrol_request(body: EnrolRequest, db: Session = Depends(db_session)):
    try:
        uuid.UUID(body.installation_id)
    except ValueError:
        raise HTTPException(400, "Invalid installation_id")
    pending = db.get(PendingAgent, body.installation_id)
    secret_hash = sha256_text(body.enrollment_secret)
    if pending and not hmac.compare_digest(pending.secret_hash, secret_hash):
        raise HTTPException(401, "Installation secret mismatch")
    if not pending:
        pending = PendingAgent(installation_id=body.installation_id, secret_hash=secret_hash,
            hostname=body.hostname, os_version=body.os_version, agent_version=body.agent_version)
        db.add(pending)
    else:
        pending.hostname, pending.os_version, pending.agent_version = body.hostname, body.os_version, body.agent_version
        pending.last_seen = datetime.now(timezone.utc)
    db.commit()
    return {"status": pending.status}


@app.post("/agent/enrol/claim")
def agent_enrol_claim(body: EnrolClaim, db: Session = Depends(db_session)):
    pending = db.get(PendingAgent, body.installation_id)
    if not pending or not hmac.compare_digest(pending.secret_hash, sha256_text(body.enrollment_secret)):
        raise HTTPException(401, "Invalid enrolment credentials")
    if pending.status != "approved":
        return {"status": pending.status}
    if not pending.delivery_token or not pending.approved_device_id:
        return {"status": "claimed"}
    token = pending.delivery_token
    pending.delivery_token = None
    pending.status = "claimed"
    db.commit()
    return {"status": "approved", "device_id": pending.approved_device_id, "device_token": token}


@app.post("/agent/heartbeat")
def heartbeat(body: HeartbeatBody, device: Device = Depends(agent_auth), db: Session = Depends(db_session)):
    managed = db.get(Device, device.id)
    managed.hostname, managed.os_version, managed.agent_version = body.hostname, body.os_version, body.agent_version
    managed.current_app = body.current_app[:255]
    managed.last_seen = datetime.now(timezone.utc)
    db.commit()
    return {"ok": True}


@app.post("/agent/activity")
def activity(body: ActivityBody, device: Device = Depends(agent_auth), db: Session = Depends(db_session)):
    today = datetime.now(timezone.utc).date()
    for process_name, seconds in body.usage.items():
        seconds = max(0, min(int(seconds), 3600))
        if seconds == 0:
            continue
        name = process_name[:255]
        row = db.scalar(select(ActivityDaily).where(
            ActivityDaily.device_id == device.id, ActivityDaily.day == today, ActivityDaily.process_name == name))
        if row:
            row.seconds += seconds
        else:
            db.add(ActivityDaily(device_id=device.id, day=today, process_name=name, seconds=seconds))
    db.commit()
    return {"ok": True}


@app.get("/agent/commands")
def commands(device: Device = Depends(agent_auth), db: Session = Depends(db_session)):
    rows = db.scalars(select(Command).where(Command.device_id == device.id, Command.status == "pending").order_by(Command.id).limit(10)).all()
    for row in rows:
        row.status = "dispatched"
    db.commit()
    return [{"id": r.id, "kind": r.kind, "payload": r.payload} for r in rows]


@app.post("/agent/commands/{command_id}/complete")
def command_complete(command_id: int, body: CommandResultBody, device: Device = Depends(agent_auth), db: Session = Depends(db_session)):
    cmd = db.get(Command, command_id)
    if not cmd or cmd.device_id != device.id:
        raise HTTPException(404, "Command not found")
    cmd.status = "complete"; cmd.result = body.result[:2000]; cmd.completed_at = datetime.now(timezone.utc)
    db.commit()
    return {"ok": True}


@app.post("/agent/commands/{command_id}/screenshot")
def upload_screenshot(command_id: int, image: UploadFile = File(...), device: Device = Depends(agent_auth), db: Session = Depends(db_session)):
    cmd = db.get(Command, command_id)
    if not cmd or cmd.device_id != device.id or cmd.kind != "screenshot":
        raise HTTPException(404, "Screenshot command not found")
    data = image.file.read(15 * 1024 * 1024 + 1)
    if len(data) > 15 * 1024 * 1024:
        raise HTTPException(413, "Screenshot too large")
    if not data.startswith(b"\x89PNG\r\n\x1a\n"):
        raise HTTPException(400, "Only PNG screenshots are accepted")
    filename = f"{device.id}-{datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%f')}.png"
    (SCREENSHOT_DIR / filename).write_bytes(data)
    db.add(Screenshot(device_id=device.id, command_id=command_id, filename=filename))
    cmd.status = "complete"; cmd.result = "screenshot uploaded"; cmd.completed_at = datetime.now(timezone.utc)
    db.commit()
    return {"ok": True}


@app.get("/agent/update")
def update_manifest(device: Device = Depends(agent_auth), db: Session = Depends(db_session)):
    release = db.scalar(select(AgentRelease).where(AgentRelease.channel == "stable", AgentRelease.promoted == True).order_by(AgentRelease.id.desc()))
    if not release:
        return {"available": False}
    return {"available": True, "version": release.version, "url": release.url, "sha256": release.sha256}
