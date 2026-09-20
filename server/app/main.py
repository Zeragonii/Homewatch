from __future__ import annotations

import hashlib
import hmac
import os
import secrets
import uuid
import json
import urllib.request
from datetime import date, datetime, timedelta, timezone, time as dt_time
from pathlib import Path
from typing import Optional
from zoneinfo import ZoneInfo

from fastapi import Depends, FastAPI, File, Form, Header, HTTPException, Request, UploadFile
from fastapi.responses import FileResponse, HTMLResponse, JSONResponse
from fastapi.staticfiles import StaticFiles
from itsdangerous import BadSignature, URLSafeSerializer
from pydantic import BaseModel
from sqlalchemy import Boolean, Date, DateTime, ForeignKey, Integer, String, Text, UniqueConstraint, create_engine, inspect, select, text
from sqlalchemy.orm import DeclarativeBase, Mapped, Session, mapped_column, relationship, sessionmaker

APP_TITLE = os.getenv("APP_TITLE", "HomeWatch")
AGENT_RELEASE_REPO = os.getenv("AGENT_RELEASE_REPO", "Zeragonii/Homewatch").strip()
GITHUB_TOKEN = os.getenv("GITHUB_TOKEN", "").strip()
DATABASE_URL = os.getenv("DATABASE_URL", "sqlite:///./homewatch.db")
ADMIN_USERNAME = os.getenv("ADMIN_USERNAME", "admin")
ADMIN_PASSWORD = os.getenv("ADMIN_PASSWORD", "change-me")
SESSION_SECRET = os.getenv("SESSION_SECRET", "dev-only-change-me")
SCREENSHOT_DIR = Path(os.getenv("SCREENSHOT_DIR", "./screenshots"))
FAMILY_TIMEZONE = os.getenv("FAMILY_TIMEZONE", "Europe/London")
try:
    FAMILY_TZ = ZoneInfo(FAMILY_TIMEZONE)
except Exception:
    FAMILY_TZ = timezone.utc
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


class ChildPolicy(Base):
    __tablename__ = "child_policies"
    id: Mapped[int] = mapped_column(primary_key=True)
    child_id: Mapped[int] = mapped_column(ForeignKey("children.id"), unique=True)
    enabled: Mapped[bool] = mapped_column(Boolean, default=False)
    weekday_start: Mapped[str] = mapped_column(String(5), default="07:00")
    weekday_end: Mapped[str] = mapped_column(String(5), default="21:00")
    weekend_start: Mapped[str] = mapped_column(String(5), default="07:00")
    weekend_end: Mapped[str] = mapped_column(String(5), default="22:00")
    weekday_minutes: Mapped[int] = mapped_column(Integer, default=0)
    weekend_minutes: Mapped[int] = mapped_column(Integer, default=0)
    warning_minutes: Mapped[int] = mapped_column(Integer, default=10)
    grace_minutes: Mapped[int] = mapped_column(Integer, default=0)
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=lambda: datetime.now(timezone.utc))


class DailyExtension(Base):
    __tablename__ = "daily_extensions"
    __table_args__ = (UniqueConstraint("child_id", "day", name="uq_child_extension_day"),)
    id: Mapped[int] = mapped_column(primary_key=True)
    child_id: Mapped[int] = mapped_column(ForeignKey("children.id"))
    day: Mapped[date] = mapped_column(Date)
    minutes: Mapped[int] = mapped_column(Integer, default=0)


class AppLimit(Base):
    __tablename__ = "app_limits"
    __table_args__ = (UniqueConstraint("child_id", "process_name", name="uq_child_app_limit"),)
    id: Mapped[int] = mapped_column(primary_key=True)
    child_id: Mapped[int] = mapped_column(ForeignKey("children.id"))
    process_name: Mapped[str] = mapped_column(String(255))
    weekday_minutes: Mapped[int] = mapped_column(Integer, default=0)
    weekend_minutes: Mapped[int] = mapped_column(Integer, default=0)
    enabled: Mapped[bool] = mapped_column(Boolean, default=True)


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
    logged_in_user: Mapped[str] = mapped_column(String(255), default="")
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

# Lightweight bootstrap migration for existing v0.1 databases.
# create_all() does not add columns to an existing table.
with engine.begin() as conn:
    device_columns = {c["name"] for c in inspect(conn).get_columns("devices")}
    if "logged_in_user" not in device_columns:
        conn.execute(text("ALTER TABLE devices ADD COLUMN logged_in_user VARCHAR(255) NOT NULL DEFAULT ''"))

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
    logged_in_user: str = ""


class ActivityBody(BaseModel):
    usage: dict[str, int]


class CommandBody(BaseModel):
    kind: str
    payload: str = ""


class DeviceAssignmentBody(BaseModel):
    child_id: int


class CommandResultBody(BaseModel):
    result: str = ""


class ReleaseBody(BaseModel):
    version: str
    url: str
    sha256: str
    channel: str = "stable"
    promoted: bool = False


class PolicyBody(BaseModel):
    enabled: bool = False
    weekday_start: str = "07:00"
    weekday_end: str = "21:00"
    weekend_start: str = "07:00"
    weekend_end: str = "22:00"
    weekday_minutes: int = 0
    weekend_minutes: int = 0
    warning_minutes: int = 10
    grace_minutes: int = 0


class ExtensionBody(BaseModel):
    minutes: int


class AppLimitBody(BaseModel):
    process_name: str
    weekday_minutes: int = 0
    weekend_minutes: int = 0
    enabled: bool = True


def family_now() -> datetime:
    return datetime.now(timezone.utc).astimezone(FAMILY_TZ)


def family_today() -> date:
    return family_now().date()


def parse_hhmm(value: str) -> dt_time:
    try:
        hh, mm = value.split(":", 1)
        h, m = int(hh), int(mm)
        if not (0 <= h <= 23 and 0 <= m <= 59):
            raise ValueError
        return dt_time(h, m)
    except Exception:
        raise HTTPException(400, f"Invalid time: {value}")


def in_window(now_t: dt_time, start_s: str, end_s: str) -> bool:
    start, end = parse_hhmm(start_s), parse_hhmm(end_s)
    if start == end:
        return True
    if start < end:
        return start <= now_t < end
    return now_t >= start or now_t < end


def policy_for(db: Session, child_id: int) -> ChildPolicy:
    policy = db.scalar(select(ChildPolicy).where(ChildPolicy.child_id == child_id))
    if not policy:
        policy = ChildPolicy(child_id=child_id)
        db.add(policy); db.commit(); db.refresh(policy)
    return policy


def child_usage_seconds(db: Session, child_id: int, day: date, process_name: Optional[str] = None) -> int:
    device_ids = db.scalars(select(Device.id).where(Device.child_id == child_id)).all()
    if not device_ids:
        return 0
    stmt = select(ActivityDaily).where(ActivityDaily.device_id.in_(device_ids), ActivityDaily.day == day)
    rows = db.scalars(stmt).all()
    if process_name is not None:
        return sum(r.seconds for r in rows if r.process_name.lower() == process_name.lower())
    return sum(r.seconds for r in rows)


def child_policy_state(db: Session, child_id: int, current_app: str = "") -> dict:
    child = db.get(Child, child_id)
    if not child:
        raise HTTPException(404, "Child not found")
    policy = policy_for(db, child_id)
    now = family_now(); today = now.date(); weekend = now.weekday() >= 5
    start = policy.weekend_start if weekend else policy.weekday_start
    end = policy.weekend_end if weekend else policy.weekday_end
    allowance_min = policy.weekend_minutes if weekend else policy.weekday_minutes
    extension = db.scalar(select(DailyExtension).where(DailyExtension.child_id == child_id, DailyExtension.day == today))
    extension_min = extension.minutes if extension else 0
    used = child_usage_seconds(db, child_id, today)
    allowed_by_window = in_window(now.timetz().replace(tzinfo=None), start, end)
    base_seconds = max(0, allowance_min + extension_min) * 60
    grace_seconds = max(0, policy.grace_minutes) * 60
    hard_limit = base_seconds + grace_seconds if allowance_min > 0 else 0
    remaining = max(0, base_seconds - used) if allowance_min > 0 else None
    status = "disabled" if not policy.enabled else "allowed"
    reason = ""
    blocked = False
    if policy.enabled and not allowed_by_window:
        blocked, status, reason = True, "blocked", f"Outside allowed hours ({start}–{end})"
    elif policy.enabled and allowance_min > 0 and used >= hard_limit:
        blocked, status, reason = True, "blocked", "Daily screen-time allowance exhausted"
    elif policy.enabled and allowance_min > 0 and used >= base_seconds:
        status, reason = "grace", "Daily allowance exhausted; grace period active"
    elif policy.enabled and allowance_min > 0 and remaining is not None and remaining <= max(0, policy.warning_minutes) * 60:
        status, reason = "warning", "Daily allowance nearly exhausted"

    app_state = None
    if current_app:
        limits = db.scalars(select(AppLimit).where(AppLimit.child_id == child_id, AppLimit.enabled == True)).all()
        limit = next((x for x in limits if x.process_name.lower() == current_app.lower()), None)
        if limit:
            minutes = limit.weekend_minutes if weekend else limit.weekday_minutes
            app_used = child_usage_seconds(db, child_id, today, limit.process_name)
            app_state = {"process_name": limit.process_name, "used_seconds": app_used, "limit_seconds": minutes * 60 if minutes > 0 else 0, "blocked": bool(minutes > 0 and app_used >= minutes * 60)}

    return {
        "enabled": policy.enabled, "status": status, "blocked": blocked, "reason": reason,
        "timezone": FAMILY_TIMEZONE, "local_time": now.isoformat(), "window_start": start, "window_end": end,
        "used_seconds": used, "allowance_seconds": allowance_min * 60 if allowance_min > 0 else 0,
        "extension_minutes": extension_min, "grace_minutes": policy.grace_minutes, "warning_minutes": policy.warning_minutes,
        "remaining_seconds": remaining, "app": app_state,
    }


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
    today = family_today()
    activity_rows = db.scalars(select(ActivityDaily).where(ActivityDaily.day == today)).all()
    activity = {}
    for row in activity_rows:
        activity.setdefault(row.device_id, []).append({"process": row.process_name, "seconds": row.seconds})
    command_rows = db.scalars(select(Command).order_by(Command.created_at.desc()).limit(250)).all()
    command_history = {}
    for row in command_rows:
        bucket = command_history.setdefault(row.device_id, [])
        if len(bucket) < 8:
            bucket.append({
                "id": row.id,
                "kind": row.kind,
                "payload": row.payload,
                "status": row.status,
                "result": row.result,
                "created_at": row.created_at.isoformat(),
                "completed_at": row.completed_at.isoformat() if row.completed_at else None,
            })
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
                "logged_in_user": d.logged_in_user,
                "last_seen": d.last_seen.isoformat() if d.last_seen else None,
                "online": bool(d.last_seen and (now - d.last_seen) < timedelta(seconds=45)),
                "activity": sorted(activity.get(d.id, []), key=lambda x: x["seconds"], reverse=True)[:8],
                "commands": command_history.get(d.id, []),
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


@app.get("/api/screentime", dependencies=[Depends(require_admin)])
def screentime(db: Session = Depends(db_session)):
    children = db.scalars(select(Child).order_by(Child.name)).all()
    out=[]
    today=family_today()
    for child in children:
        policy=policy_for(db, child.id)
        ext=db.scalar(select(DailyExtension).where(DailyExtension.child_id==child.id, DailyExtension.day==today))
        limits=db.scalars(select(AppLimit).where(AppLimit.child_id==child.id).order_by(AppLimit.process_name)).all()
        state=child_policy_state(db, child.id)
        out.append({"id":child.id,"name":child.name,"policy":{
            "enabled":policy.enabled,"weekday_start":policy.weekday_start,"weekday_end":policy.weekday_end,
            "weekend_start":policy.weekend_start,"weekend_end":policy.weekend_end,"weekday_minutes":policy.weekday_minutes,
            "weekend_minutes":policy.weekend_minutes,"warning_minutes":policy.warning_minutes,"grace_minutes":policy.grace_minutes},
            "today":{"used_seconds":state["used_seconds"],"extension_minutes":ext.minutes if ext else 0,"status":state["status"],"reason":state["reason"]},
            "app_limits":[{"id":x.id,"process_name":x.process_name,"weekday_minutes":x.weekday_minutes,"weekend_minutes":x.weekend_minutes,"enabled":x.enabled} for x in limits]})
    return {"timezone":FAMILY_TIMEZONE,"children":out}


@app.put("/api/children/{child_id}/policy", dependencies=[Depends(require_admin)])
def update_policy(child_id:int, body:PolicyBody, db:Session=Depends(db_session)):
    if not db.get(Child,child_id): raise HTTPException(404,"Child not found")
    for value in (body.weekday_start,body.weekday_end,body.weekend_start,body.weekend_end): parse_hhmm(value)
    for value in (body.weekday_minutes,body.weekend_minutes,body.warning_minutes,body.grace_minutes):
        if value < 0 or value > 1440: raise HTTPException(400,"Minute values must be between 0 and 1440")
    p=policy_for(db,child_id)
    p.enabled=body.enabled;p.weekday_start=body.weekday_start;p.weekday_end=body.weekday_end;p.weekend_start=body.weekend_start;p.weekend_end=body.weekend_end
    p.weekday_minutes=body.weekday_minutes;p.weekend_minutes=body.weekend_minutes;p.warning_minutes=body.warning_minutes;p.grace_minutes=body.grace_minutes;p.updated_at=datetime.now(timezone.utc)
    db.commit();return {"ok":True}


@app.post("/api/children/{child_id}/extension", dependencies=[Depends(require_admin)])
def add_extension(child_id:int, body:ExtensionBody, db:Session=Depends(db_session)):
    if not db.get(Child,child_id): raise HTTPException(404,"Child not found")
    if body.minutes < -1440 or body.minutes > 1440: raise HTTPException(400,"Invalid extension")
    today=family_today(); row=db.scalar(select(DailyExtension).where(DailyExtension.child_id==child_id,DailyExtension.day==today))
    if not row: row=DailyExtension(child_id=child_id,day=today,minutes=0);db.add(row)
    row.minutes=max(0,row.minutes+body.minutes);db.commit();return {"ok":True,"minutes":row.minutes}


@app.post("/api/children/{child_id}/app-limits", dependencies=[Depends(require_admin)])
def upsert_app_limit(child_id:int, body:AppLimitBody, db:Session=Depends(db_session)):
    if not db.get(Child,child_id): raise HTTPException(404,"Child not found")
    process=body.process_name.strip()[:255]
    if not process: raise HTTPException(400,"Process name required")
    if not process.lower().endswith('.exe'): process += '.exe'
    if body.weekday_minutes<0 or body.weekend_minutes<0: raise HTTPException(400,"Minutes cannot be negative")
    row=db.scalar(select(AppLimit).where(AppLimit.child_id==child_id,AppLimit.process_name==process))
    if not row: row=AppLimit(child_id=child_id,process_name=process);db.add(row)
    row.weekday_minutes=min(body.weekday_minutes,1440);row.weekend_minutes=min(body.weekend_minutes,1440);row.enabled=body.enabled
    db.commit();db.refresh(row);return {"id":row.id}


@app.delete("/api/app-limits/{limit_id}", dependencies=[Depends(require_admin)])
def delete_app_limit(limit_id:int, db:Session=Depends(db_session)):
    row=db.get(AppLimit,limit_id)
    if not row: raise HTTPException(404,"App limit not found")
    db.delete(row);db.commit();return {"ok":True}


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




@app.put("/api/devices/{device_id}/child", dependencies=[Depends(require_admin)])
def reassign_device_child(device_id: str, body: DeviceAssignmentBody, db: Session = Depends(db_session)):
    device = db.get(Device, device_id)
    if not device:
        raise HTTPException(404, "Device not found")
    child = db.get(Child, body.child_id)
    if not child:
        raise HTTPException(404, "Child not found")
    device.child_id = child.id
    db.commit()
    return {"ok": True, "device_id": device.id, "child_id": child.id, "child": child.name}

@app.post("/api/devices/{device_id}/commands", dependencies=[Depends(require_admin)])
def create_command(device_id: str, body: CommandBody, db: Session = Depends(db_session)):
    if body.kind not in {"message", "screenshot", "check_update", "lock", "logoff", "restart", "shutdown", "close_app"}:
        raise HTTPException(400, "Unsupported command")
    if body.kind == "message":
        if len(body.payload) > 5000:
            raise HTTPException(400, "Message payload too large")
        try:
            message = json.loads(body.payload)
            message_type = str(message.get("type", "notify")).lower()
            message_text = str(message.get("text", "")).strip()
            if message_type not in {"notify", "question", "alert"}:
                raise ValueError
            if not message_text or len(message_text) > 2000:
                raise ValueError
        except Exception:
            raise HTTPException(400, "Message payload must contain a valid type and non-empty text")
    if body.kind == "close_app":
        target = body.payload.strip()
        if not target or len(target) > 255:
            raise HTTPException(400, "A process name is required")
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
    managed.logged_in_user = body.logged_in_user[:255]
    managed.last_seen = datetime.now(timezone.utc)
    db.commit()
    return {"ok": True}


@app.post("/agent/activity")
def activity(body: ActivityBody, device: Device = Depends(agent_auth), db: Session = Depends(db_session)):
    today = family_today()
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


@app.get("/agent/policy")
def agent_policy(device: Device = Depends(agent_auth), db: Session = Depends(db_session)):
    return child_policy_state(db, device.child_id, device.current_app)


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



_github_release_cache: dict[str, object] = {"checked_at": None, "manifest": None}

def github_release_manifest(force: bool = False) -> Optional[dict]:
    """Resolve the latest non-prerelease GitHub release into an agent update manifest.

    The release workflow publishes a ZIP and a matching .sha256 asset. This keeps the
    server as the agent-facing authority while avoiding manual release registration.
    """
    if not AGENT_RELEASE_REPO:
        return None
    now = datetime.now(timezone.utc)
    checked = _github_release_cache.get("checked_at")
    if not force and isinstance(checked, datetime) and now - checked < timedelta(minutes=5):
        return _github_release_cache.get("manifest")  # type: ignore[return-value]
    _github_release_cache["checked_at"] = now
    headers = {"Accept": "application/vnd.github+json", "User-Agent": "HomeWatch"}
    if GITHUB_TOKEN:
        headers["Authorization"] = f"Bearer {GITHUB_TOKEN}"
    try:
        req = urllib.request.Request(f"https://api.github.com/repos/{AGENT_RELEASE_REPO}/releases/latest", headers=headers)
        with urllib.request.urlopen(req, timeout=10) as response:
            release = json.load(response)
        assets = {a.get("name"): a.get("browser_download_url") for a in release.get("assets", [])}
        zip_url = assets.get("HomeWatch-Agent-win-x64.zip")
        sha_url = assets.get("HomeWatch-Agent-win-x64.sha256")
        if not zip_url or not sha_url:
            _github_release_cache["manifest"] = None
            return None
        sha_req = urllib.request.Request(sha_url, headers=headers)
        with urllib.request.urlopen(sha_req, timeout=10) as response:
            digest = response.read(256).decode("ascii", errors="ignore").strip().split()[0].lower()
        if len(digest) != 64 or any(c not in "0123456789abcdef" for c in digest):
            _github_release_cache["manifest"] = None
            return None
        version = str(release.get("tag_name", "")).lstrip("vV")
        if not version:
            _github_release_cache["manifest"] = None
            return None
        manifest = {"available": True, "version": version, "url": zip_url, "sha256": digest}
        _github_release_cache["manifest"] = manifest
        return manifest
    except Exception:
        # A temporary GitHub outage must never break the agent heartbeat/update loop.
        return _github_release_cache.get("manifest")  # type: ignore[return-value]

@app.get("/agent/update")
def update_manifest(refresh: bool = False, device: Device = Depends(agent_auth), db: Session = Depends(db_session)):
    # A manually promoted server release wins. Otherwise automatically follow the
    # latest normal GitHub Release from AGENT_RELEASE_REPO.
    release = db.scalar(select(AgentRelease).where(AgentRelease.channel == "stable", AgentRelease.promoted == True).order_by(AgentRelease.id.desc()))
    if release:
        return {"available": True, "version": release.version, "url": release.url, "sha256": release.sha256}
    return github_release_manifest(force=refresh) or {"available": False}
