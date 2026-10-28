from __future__ import annotations

import ntpath

import hashlib
import hmac
import os
import secrets
import uuid
import json
import urllib.request
import urllib.parse
import io
import re
import base64
import threading
from concurrent.futures import ThreadPoolExecutor
from cryptography.fernet import Fernet
from cryptography import x509
from cryptography.hazmat.primitives import serialization
from cryptography.x509.oid import ExtendedKeyUsageOID
from . import nas_library
from datetime import date, datetime, timedelta, timezone, time as dt_time
from pathlib import Path, PureWindowsPath
from typing import Optional
from zoneinfo import ZoneInfo
from PIL import Image, UnidentifiedImageError

from fastapi import Depends, FastAPI, File, Form, Header, HTTPException, Request, UploadFile
from fastapi.responses import FileResponse, HTMLResponse, JSONResponse
from fastapi.staticfiles import StaticFiles
from itsdangerous import BadSignature, URLSafeSerializer
from pydantic import BaseModel
from sqlalchemy import BigInteger, Boolean, Date, DateTime, ForeignKey, Integer, String, Text, UniqueConstraint, create_engine, inspect, select, text
from sqlalchemy.orm import DeclarativeBase, Mapped, Session, mapped_column, relationship, sessionmaker

APP_TITLE = os.getenv("APP_TITLE", "HomeWatch")
AGENT_RELEASE_REPO = os.getenv("AGENT_RELEASE_REPO", "Zeragonii/Homewatch").strip()
GITHUB_TOKEN = os.getenv("GITHUB_TOKEN", "").strip()
DATABASE_URL = os.getenv("DATABASE_URL", "sqlite:///./homewatch.db")
ADMIN_USERNAME = os.getenv("ADMIN_USERNAME", "admin")
ADMIN_PASSWORD = os.getenv("ADMIN_PASSWORD", "change-me")
SESSION_SECRET = os.getenv("SESSION_SECRET", "dev-only-change-me")
SCREENSHOT_DIR = Path(os.getenv("SCREENSHOT_DIR", "./screenshots"))
ARTWORK_DIR = Path(os.getenv("ARTWORK_DIR", str(SCREENSHOT_DIR / "artwork")))
STEAMGRIDDB_API_KEY = os.getenv("STEAMGRIDDB_API_KEY", "").strip()
FAMILY_TIMEZONE = os.getenv("FAMILY_TIMEZONE", "Europe/London")
try:
    FAMILY_TZ = ZoneInfo(FAMILY_TIMEZONE)
except Exception:
    FAMILY_TZ = timezone.utc
SCREENSHOT_DIR.mkdir(parents=True, exist_ok=True)
ARTWORK_DIR.mkdir(parents=True, exist_ok=True)

connect_args = {"check_same_thread": False} if DATABASE_URL.startswith("sqlite") else {}
engine = create_engine(DATABASE_URL, future=True, pool_pre_ping=True, connect_args=connect_args)
SessionLocal = sessionmaker(bind=engine, expire_on_commit=False)
serializer = URLSafeSerializer(SESSION_SECRET, salt="homewatch-admin-session")


class Base(DeclarativeBase):
    pass



class SigningCertificate(Base):
    __tablename__ = "signing_certificate"
    # Only public DER certificate material, never a private key or PFX.
    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    der_base64: Mapped[str] = mapped_column(Text, nullable=False)
    sha256: Mapped[str] = mapped_column(String(64), nullable=False)
    subject: Mapped[str] = mapped_column(String(500), nullable=False)
    expires_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), nullable=False)
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=lambda: datetime.now(timezone.utc))

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
    update_status: Mapped[str] = mapped_column(String(600), default="")
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




# v0.4 application approval. This is a normal-user launcher, NOT an elevation broker.
class ApplicationRequest(Base):
    __tablename__ = "application_requests"
    id: Mapped[int] = mapped_column(primary_key=True)
    device_id: Mapped[str] = mapped_column(ForeignKey("devices.id"), index=True)
    child_id: Mapped[int] = mapped_column(ForeignKey("children.id"), index=True)
    file_name: Mapped[str] = mapped_column(String(255))
    file_path: Mapped[str] = mapped_column(Text)
    sha256: Mapped[str] = mapped_column(String(64), index=True)
    file_size: Mapped[int] = mapped_column(BigInteger)
    signature_status: Mapped[str] = mapped_column(String(20))
    publisher: Mapped[str] = mapped_column(String(255), default="")
    signer_thumbprint: Mapped[str] = mapped_column(String(64), default="")
    signer_subject: Mapped[str] = mapped_column(String(500), default="")
    signer_issuer: Mapped[str] = mapped_column(String(500), default="")
    reason: Mapped[str] = mapped_column(String(500), default="")
    status: Mapped[str] = mapped_column(String(20), default="pending")
    decision_scope: Mapped[str] = mapped_column(String(20), default="")
    reviewed_at: Mapped[Optional[datetime]] = mapped_column(DateTime(timezone=True), nullable=True)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=lambda: datetime.now(timezone.utc))


class ApplicationApproval(Base):
    __tablename__ = "application_approvals"
    id: Mapped[int] = mapped_column(primary_key=True)
    request_id: Mapped[int] = mapped_column(ForeignKey("application_requests.id"))
    # device, child, global; scope_id null for global
    scope: Mapped[str] = mapped_column(String(20))
    device_id: Mapped[Optional[str]] = mapped_column(String(36), nullable=True)
    child_id: Mapped[Optional[int]] = mapped_column(Integer, nullable=True)
    file_name: Mapped[str] = mapped_column(String(255))
    sha256: Mapped[str] = mapped_column(String(64), index=True)
    signature_status: Mapped[str] = mapped_column(String(20))
    signer_thumbprint: Mapped[str] = mapped_column(String(64), default="")
    publisher: Mapped[str] = mapped_column(String(255), default="")
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=lambda: datetime.now(timezone.utc))


class GameElevationGrant(Base):
    """Separate, parent-set exact-binary elevation policy, independent of launch approval."""
    __tablename__ = "game_elevation_grants"
    sha256: Mapped[str] = mapped_column(String(64), primary_key=True)
    file_name: Mapped[str] = mapped_column(String(255))
    enabled: Mapped[bool] = mapped_column(Boolean, default=False, server_default=text("false"))
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=lambda: datetime.now(timezone.utc))


class ApplicationPresentation(Base):
    __tablename__ = "application_presentations"
    sha256: Mapped[str] = mapped_column(String(64), primary_key=True)
    display_name: Mapped[str] = mapped_column(String(120), default="")
    artwork_revision: Mapped[int] = mapped_column(Integer, default=0, server_default=text("0"), nullable=False)
    artwork_file: Mapped[str] = mapped_column(String(130), default="")
    artwork_credit: Mapped[str] = mapped_column(String(255), default="")
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=lambda: datetime.now(timezone.utc))


class HouseholdCatalogueItem(Base):
    """Parent-curated, published packages available for install on every enrolled PC.

    Package availability is NOT a Windows elevation grant or permission to launch
    an arbitrary different executable after installation.
    """
    __tablename__ = "household_catalogue"
    id: Mapped[int] = mapped_column(primary_key=True)
    name: Mapped[str] = mapped_column(String(120))
    installer_path: Mapped[str] = mapped_column(String(2048))
    installer_sha256: Mapped[str] = mapped_column(String(64), index=True)
    description: Mapped[str] = mapped_column(String(600), default="")
    package_root: Mapped[str] = mapped_column(String(2048), default="", server_default=text("''"))
    package_entrypoint: Mapped[str] = mapped_column(String(700), default="", server_default=text("''"))
    manifest_json: Mapped[str] = mapped_column(Text, default="", server_default=text("''"))
    total_bytes: Mapped[int] = mapped_column(BigInteger, default=0, server_default=text("0"))
    file_count: Mapped[int] = mapped_column(Integer, default=0, server_default=text("0"))
    # Parent-approved relative path; never an absolute device path.
    launch_relative_path: Mapped[str] = mapped_column(String(700), default="", server_default=text("''"))
    enabled: Mapped[bool] = mapped_column(Boolean, default=True, server_default=text("true"))
    elevate_installer: Mapped[bool] = mapped_column(Boolean, default=False, server_default=text("false"))
    deployment_type: Mapped[str] = mapped_column(String(20), default="installer", server_default=text("'installer'"))
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=lambda: datetime.now(timezone.utc))
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=lambda: datetime.now(timezone.utc))


class NasLibrarySettings(Base):
    __tablename__ = "nas_library_settings"
    id: Mapped[int] = mapped_column(Integer, primary_key=True)
    root_unc: Mapped[str] = mapped_column(String(2048))
    username: Mapped[str] = mapped_column(String(255), default="")
    encrypted_password: Mapped[str] = mapped_column(Text, default="")
    game_scan_roots: Mapped[str] = mapped_column(Text, default=r"C:\Games;D:\Games", server_default=text(r"'C:\Games;D:\Games'"))


class HouseholdInstallation(Base):
    __tablename__ = "household_installations"
    __table_args__ = (UniqueConstraint("catalogue_id", "device_id", name="uq_household_install_device"),)
    id: Mapped[int] = mapped_column(primary_key=True)
    catalogue_id: Mapped[int] = mapped_column(ForeignKey("household_catalogue.id"), index=True)
    device_id: Mapped[str] = mapped_column(ForeignKey("devices.id"), index=True)
    status: Mapped[str] = mapped_column(String(30), default="not_installed")
    installed_path: Mapped[str] = mapped_column(Text, default="")
    executable_path: Mapped[str] = mapped_column(Text, default="")
    install_root: Mapped[str] = mapped_column(Text, default="", server_default=text("''"))
    installed_manifest_sha256: Mapped[str] = mapped_column(String(64), default="", server_default=text("''"))
    executable_sha256: Mapped[str] = mapped_column(String(64), default="")
    request_id: Mapped[Optional[int]] = mapped_column(Integer, nullable=True)
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=lambda: datetime.now(timezone.utc))


class HouseholdLaunchSuggestion(Base):
    """Untrusted device observations, reviewed by a parent before reuse."""
    __tablename__ = "household_launch_suggestions"
    id: Mapped[int] = mapped_column(primary_key=True)
    catalogue_id: Mapped[int] = mapped_column(ForeignKey("household_catalogue.id"), index=True)
    device_id: Mapped[str] = mapped_column(ForeignKey("devices.id"), index=True)
    relative_path: Mapped[str] = mapped_column(String(700))
    installer_sha256: Mapped[str] = mapped_column(String(64))
    executable_sha256: Mapped[str] = mapped_column(String(64))
    status: Mapped[str] = mapped_column(String(20), default="pending")
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=lambda: datetime.now(timezone.utc))


class HouseholdInstallAttempt(Base):
    __tablename__ = "household_install_attempts"
    id: Mapped[int] = mapped_column(primary_key=True)
    catalogue_id: Mapped[int] = mapped_column(ForeignKey("household_catalogue.id"), index=True)
    device_id: Mapped[str] = mapped_column(ForeignKey("devices.id"), index=True)
    status: Mapped[str] = mapped_column(String(20))
    detail: Mapped[str] = mapped_column(String(300), default="")
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=lambda: datetime.now(timezone.utc))


class ApplicationLaunch(Base):
    __tablename__ = "application_launches"
    id: Mapped[int] = mapped_column(primary_key=True)
    device_id: Mapped[str] = mapped_column(ForeignKey("devices.id"))
    request_id: Mapped[int] = mapped_column(ForeignKey("application_requests.id"))
    approval_id: Mapped[Optional[int]] = mapped_column(Integer, nullable=True)
    status: Mapped[str] = mapped_column(String(20))
    detail: Mapped[str] = mapped_column(String(255), default="")
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=lambda: datetime.now(timezone.utc))



class WellbeingRule(Base):
    __tablename__ = "wellbeing_rules"
    id: Mapped[int] = mapped_column(primary_key=True)
    child_id: Mapped[int] = mapped_column(ForeignKey("children.id"), nullable=False, index=True)
    kind: Mapped[str] = mapped_column(String(24), nullable=False)
    deleted: Mapped[bool] = mapped_column(Boolean, nullable=False, default=False, server_default=text("false"))
    title: Mapped[str] = mapped_column(String(100), nullable=False)
    message: Mapped[str] = mapped_column(String(400), nullable=False)
    mode: Mapped[str] = mapped_column(String(20), nullable=False, default="active")
    interval_minutes: Mapped[int] = mapped_column(Integer, nullable=False, default=60)
    scheduled_times: Mapped[str] = mapped_column(Text, nullable=False, default="[]")
    idle_threshold_seconds: Mapped[int] = mapped_column(Integer, nullable=False, default=180)
    allow_snooze: Mapped[bool] = mapped_column(Boolean, nullable=False, default=True, server_default=text("true"))
    snooze_minutes: Mapped[int] = mapped_column(Integer, nullable=False, default=5)
    enabled: Mapped[bool] = mapped_column(Boolean, nullable=False, default=True, server_default=text("true"))
    updated_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=lambda: datetime.now(timezone.utc))


class WellbeingEvent(Base):
    __tablename__ = "wellbeing_events"
    event_id: Mapped[str] = mapped_column(String(36), primary_key=True)
    rule_id: Mapped[int] = mapped_column(ForeignKey("wellbeing_rules.id"), nullable=False, index=True)
    device_id: Mapped[str] = mapped_column(ForeignKey("devices.id"), nullable=False, index=True)
    event: Mapped[str] = mapped_column(String(20), nullable=False)
    created_at: Mapped[datetime] = mapped_column(DateTime(timezone=True), default=lambda: datetime.now(timezone.utc))

Base.metadata.create_all(engine)

# Lightweight bootstrap migration for existing v0.1 databases.
# create_all() does not add columns to an existing table.
with engine.begin() as conn:
    device_columns = {c["name"] for c in inspect(conn).get_columns("devices")}
    if "logged_in_user" not in device_columns:
        conn.execute(text("ALTER TABLE devices ADD COLUMN logged_in_user VARCHAR(255) NOT NULL DEFAULT ''"))
    if "update_status" not in device_columns:
        conn.execute(text("ALTER TABLE devices ADD COLUMN update_status VARCHAR(600) NOT NULL DEFAULT ''"))
    # Older artwork rows can contain NULL even though current models expect an integer.
    # Keep existing names/artwork intact; normalize only the cache revision.
    if inspect(conn).has_table("nas_library_settings"):
        nas_cols = {c['name'] for c in inspect(conn).get_columns('nas_library_settings')}
        if 'game_scan_roots' not in nas_cols:
            conn.execute(text(r"ALTER TABLE nas_library_settings ADD COLUMN game_scan_roots TEXT NOT NULL DEFAULT 'C:\Games;D:\Games'"))
    if inspect(conn).has_table("household_catalogue"):
        catalog_columns = {c["name"] for c in inspect(conn).get_columns("household_catalogue")}
        for col, ddl in {"package_root": "VARCHAR(2048) NOT NULL DEFAULT ''",
                         "package_entrypoint": "VARCHAR(700) NOT NULL DEFAULT ''",
                         "manifest_json": "TEXT NOT NULL DEFAULT ''",
                         "total_bytes": "BIGINT NOT NULL DEFAULT 0",
                         "file_count": "INTEGER NOT NULL DEFAULT 0",
                         "launch_relative_path": "VARCHAR(700) NOT NULL DEFAULT ''",
                         "elevate_installer": "BOOLEAN NOT NULL DEFAULT false",
                         "deployment_type": "VARCHAR(20) NOT NULL DEFAULT 'installer'"}.items():
            if col not in catalog_columns:
                conn.execute(text(f"ALTER TABLE household_catalogue ADD COLUMN {col} {ddl}"))
    if inspect(conn).has_table("household_installations"):
        install_columns = {c["name"] for c in inspect(conn).get_columns("household_installations")}
        if "install_root" not in install_columns:
            conn.execute(text("ALTER TABLE household_installations ADD COLUMN install_root TEXT NOT NULL DEFAULT ''"))
        if "installed_manifest_sha256" not in install_columns:
            conn.execute(text("ALTER TABLE household_installations ADD COLUMN installed_manifest_sha256 VARCHAR(64) NOT NULL DEFAULT ''"))
    if inspect(conn).has_table("application_presentations"):
        conn.execute(text("UPDATE application_presentations SET artwork_revision = 0 WHERE artwork_revision IS NULL"))

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
    update_status: str = ""


class ActivityBody(BaseModel):
    usage: dict[str, int]


class CommandBody(BaseModel):
    kind: str
    payload: str = ""


class DeviceAssignmentBody(BaseModel):
    child_id: int


class CommandResultBody(BaseModel):
    result: str = ""


class MessageResponseBody(BaseModel):
    response_type: str
    text: str = ""


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



class SigningCertificateUpload(BaseModel):
    certificate_base64: str


def signing_certificate_metadata(record: SigningCertificate) -> dict:
    return {"sha256": record.sha256, "subject": record.subject,
            "expires_at": record.expires_at.isoformat(), "updated_at": record.updated_at.isoformat()}


def check_public_signing_certificate(source: bytes) -> tuple[bytes, x509.Certificate]:
    if len(source) > 16384 or len(source) < 100:
        raise HTTPException(400, "Expected a public .cer certificate of at most 16 KiB")
    try:
        cert = (x509.load_pem_x509_certificate(source) if b"-----BEGIN CERTIFICATE-----" in source
                else x509.load_der_x509_certificate(source))
        der = cert.public_bytes(serialization.Encoding.DER)
        # Only self-signed leaf code-signing certificates for this household setup.
        if cert.issuer != cert.subject:
            raise ValueError("Certificate must be self-signed")
        cert.verify_directly_issued_by(cert)
        if cert.not_valid_before_utc > datetime.now(timezone.utc) or cert.not_valid_after_utc <= datetime.now(timezone.utc):
            raise ValueError("Certificate is not currently valid")
        eku = cert.extensions.get_extension_for_class(x509.ExtendedKeyUsage).value
        if ExtendedKeyUsageOID.CODE_SIGNING not in eku:
            raise ValueError("Certificate does not allow code signing")
        try:
            if cert.extensions.get_extension_for_class(x509.BasicConstraints).value.ca:
                raise ValueError("CA certificates cannot be uploaded")
        except x509.ExtensionNotFound:
            pass
    except (ValueError, TypeError, x509.ExtensionNotFound) as exc:
        raise HTTPException(400, "Invalid public code-signing certificate: " + str(exc)) from exc
    return der, cert


@app.get("/api/signing-certificate", dependencies=[Depends(require_admin)])
def admin_get_signing_certificate(db: Session = Depends(db_session)):
    record = db.get(SigningCertificate, 1)
    return {"configured": bool(record), **(signing_certificate_metadata(record) if record else {})}


@app.put("/api/signing-certificate", dependencies=[Depends(require_admin)])
def admin_put_signing_certificate(body: SigningCertificateUpload, db: Session = Depends(db_session)):
    if len(body.certificate_base64) > 23000:
        raise HTTPException(400, "Certificate is too large")
    try:
        data = base64.b64decode(body.certificate_base64, validate=True)
    except ValueError as exc:
        raise HTTPException(400, "Certificate encoding is invalid") from exc
    der, cert = check_public_signing_certificate(data)
    sha = hashlib.sha256(der).hexdigest().upper()
    record = db.get(SigningCertificate, 1)
    if record is None:
        record = SigningCertificate(id=1)
        db.add(record)
    record.der_base64 = base64.b64encode(der).decode('ascii')
    record.sha256 = sha
    record.subject = cert.subject.rfc4514_string()[:500]
    record.expires_at = cert.not_valid_after_utc
    record.updated_at = datetime.now(timezone.utc)
    db.commit()
    return {"configured": True, **signing_certificate_metadata(record)}


@app.delete("/api/signing-certificate", dependencies=[Depends(require_admin)])
def admin_delete_signing_certificate(db: Session = Depends(db_session)):
    record = db.get(SigningCertificate, 1)
    if record:
        db.delete(record)
        db.commit()
    return {"ok": True}


@app.get("/agent/signing-certificate")
def agent_get_signing_certificate(_device: Device = Depends(agent_auth), db: Session = Depends(db_session)):
    record = db.get(SigningCertificate, 1)
    if record is None:
        raise HTTPException(404, "No HomeWatch public signing certificate has been uploaded")
    return {"certificate_base64": record.der_base64, **signing_certificate_metadata(record)}

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
    def seen_at_utc(value: datetime | None) -> datetime | None:
        if value is None:
            return None
        # SQLite (used in smoke tests) returns naive timestamps even for a
        # timezone-aware column; PostgreSQL returns proper timestamptz values.
        return value.replace(tzinfo=timezone.utc) if value.tzinfo is None else value.astimezone(timezone.utc)
    pending_app_rows = db.scalars(select(ApplicationRequest).where(ApplicationRequest.status == "pending")).all()
    pending_app_count = sum(1 for r in pending_app_rows if not matching_approval(db, db.get(Device, r.device_id), {
        "file_name": r.file_name, "sha256": r.sha256,
        "signature_status": r.signature_status, "signer_thumbprint": r.signer_thumbprint
    }))
    return {
        "pending_application_requests": pending_app_count,
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
                "update_status": d.update_status,
                "last_seen": d.last_seen.isoformat() if d.last_seen else None,
                "online": bool(d.last_seen and (now - seen_at_utc(d.last_seen)) < timedelta(seconds=45)),
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




# v0.5.7: gentle, per-child wellbeing prompts independent of screen-time blocks.
class WellbeingRuleBody(BaseModel):
    kind: str = "custom"
    title: str = ""
    message: str = ""
    mode: str = "active"
    interval_minutes: int = 60
    scheduled_times: list[str] = []
    idle_threshold_seconds: int = 180
    allow_snooze: bool = True
    snooze_minutes: int = 5
    enabled: bool = True


class WellbeingEventBody(BaseModel):
    event_id: str
    rule_id: int
    event: str


def wellbeing_rule_json(row: WellbeingRule) -> dict:
    return {"id": row.id, "child_id": row.child_id, "kind": row.kind,
            "title": row.title, "message": row.message, "mode": row.mode,
            "interval_minutes": row.interval_minutes,
            "scheduled_times": json.loads(row.scheduled_times or "[]"),
            "idle_threshold_seconds": row.idle_threshold_seconds,
            "allow_snooze": row.allow_snooze, "snooze_minutes": row.snooze_minutes,
            "enabled": row.enabled, "updated_at": row.updated_at.isoformat() if row.updated_at else ""}


def validated_wellbeing(body: WellbeingRuleBody) -> dict:
    kind = body.kind.strip().lower()
    title, message = body.title.strip(), body.message.strip()
    if kind not in ("hydration", "toilet", "custom"):
        raise HTTPException(400, "Invalid reminder kind")
    if not (1 <= len(title) <= 100 and 1 <= len(message) <= 400):
        raise HTTPException(400, "Title and message required (up to 100/400 characters)")
    if body.mode not in ("active", "clock", "scheduled"):
        raise HTTPException(400, "Invalid reminder scheduling mode")
    if not 5 <= body.interval_minutes <= 720:
        raise HTTPException(400, "Interval must be 5–720 minutes")
    if not 30 <= body.idle_threshold_seconds <= 900:
        raise HTTPException(400, "Idle threshold must be 30–900 seconds")
    if not 1 <= body.snooze_minutes <= 30:
        raise HTTPException(400, "Snooze must be 1–30 minutes")
    times = sorted(set(body.scheduled_times))
    if len(times) > 12 or any(not re.fullmatch(r'(?:[01]\d|2[0-3]):[0-5]\d', t) for t in times):
        raise HTTPException(400, "Specify at most 12 valid HH:MM reminder times")
    if body.mode == "scheduled" and not times:
        raise HTTPException(400, "Scheduled reminders require at least one HH:MM time")
    return {"kind": kind, "title": title, "message": message,
            "mode": body.mode, "interval_minutes": body.interval_minutes,
            "scheduled_times": json.dumps(times), "idle_threshold_seconds": body.idle_threshold_seconds,
            "allow_snooze": body.allow_snooze, "snooze_minutes": body.snooze_minutes,
            "enabled": body.enabled}


@app.get('/api/wellbeing', dependencies=[Depends(require_admin)])
def admin_wellbeing(db: Session = Depends(db_session)):
    children = db.scalars(select(Child).order_by(Child.name)).all()
    rules = db.scalars(select(WellbeingRule).where(WellbeingRule.deleted.is_(False)).order_by(WellbeingRule.id)).all()
    return {"children": [{"id": c.id, "name": c.name} for c in children],
            "rules": [wellbeing_rule_json(r) for r in rules], "timezone": FAMILY_TIMEZONE}


@app.post('/api/children/{child_id}/wellbeing', dependencies=[Depends(require_admin)])
def add_wellbeing_rule(child_id: int, body: WellbeingRuleBody, db: Session = Depends(db_session)):
    if not db.get(Child, child_id):
        raise HTTPException(404, "Child not found")
    rule = WellbeingRule(child_id=child_id, **validated_wellbeing(body))
    db.add(rule); db.commit(); db.refresh(rule)
    return wellbeing_rule_json(rule)


@app.put('/api/wellbeing/{rule_id}', dependencies=[Depends(require_admin)])
def update_wellbeing_rule(rule_id: int, body: WellbeingRuleBody, db: Session = Depends(db_session)):
    rule = db.get(WellbeingRule, rule_id)
    if not rule or rule.deleted: raise HTTPException(404, "Reminder not found")
    for key, value in validated_wellbeing(body).items(): setattr(rule, key, value)
    rule.updated_at = datetime.now(timezone.utc)
    db.commit(); db.refresh(rule)
    return wellbeing_rule_json(rule)


@app.delete('/api/wellbeing/{rule_id}', dependencies=[Depends(require_admin)])
def delete_wellbeing_rule(rule_id: int, db: Session = Depends(db_session)):
    rule = db.get(WellbeingRule, rule_id)
    if not rule or rule.deleted: raise HTTPException(404, "Reminder not found")
    # Soft delete preserves the audit trail while removing it from all UI/agents.
    rule.deleted = True
    rule.enabled = False
    rule.updated_at = datetime.now(timezone.utc)
    db.commit()
    return {"ok": True}


@app.get('/api/wellbeing/events', dependencies=[Depends(require_admin)])
def admin_wellbeing_events(child_id: int | None = None, db: Session = Depends(db_session)):
    stmt = (select(WellbeingEvent, WellbeingRule, Device, Child)
            .join(WellbeingRule, WellbeingEvent.rule_id == WellbeingRule.id)
            .join(Device, WellbeingEvent.device_id == Device.id)
            .join(Child, WellbeingRule.child_id == Child.id))
    if child_id is not None: stmt = stmt.where(WellbeingRule.child_id == child_id)
    rows = db.execute(stmt.order_by(WellbeingEvent.created_at.desc()).limit(150)).all()
    return {"events": [{"event_id": e.event_id, "kind": r.kind, "title": r.title,
                        "child": c.name, "device": d.name, "event": e.event,
                        "created_at": e.created_at.isoformat()} for e, r, d, c in rows]}


@app.get('/agent/wellbeing')
def agent_wellbeing(device: Device = Depends(agent_auth), db: Session = Depends(db_session)):
    rules = db.scalars(select(WellbeingRule).where(WellbeingRule.child_id == device.child_id,
                                                   WellbeingRule.enabled.is_(True),
                                                   WellbeingRule.deleted.is_(False)).order_by(WellbeingRule.id)).all()
    return {"rules": [wellbeing_rule_json(row) for row in rules]}


@app.post('/agent/wellbeing/events')
def agent_wellbeing_event(body: WellbeingEventBody, device: Device = Depends(agent_auth),
                          db: Session = Depends(db_session)):
    if body.event not in ("shown", "acknowledged", "snoozed"):
        raise HTTPException(400, "Unsupported reminder event")
    try: uuid.UUID(body.event_id)
    except (ValueError, AttributeError): raise HTTPException(400, "Invalid event identifier")
    rule = db.get(WellbeingRule, body.rule_id)
    if not rule or rule.child_id != device.child_id:
        raise HTTPException(404, "Reminder not assigned to this child")
    # Idempotent client retry after connectivity loss.
    event = db.get(WellbeingEvent, body.event_id)
    if event:
        if (event.rule_id, event.device_id, event.event) != (rule.id, device.id, body.event):
            raise HTTPException(409, "Event identifier already used")
        return {"ok": True}
    db.add(WellbeingEvent(event_id=body.event_id, rule_id=rule.id, device_id=device.id,
                          event=body.event))
    db.commit()
    return {"ok": True}


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
    managed.update_status = body.update_status[:600]
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




@app.post("/agent/messages/{command_id}/respond")
def message_respond(command_id: int, body: MessageResponseBody, device: Device = Depends(agent_auth), db: Session = Depends(db_session)):
    cmd = db.get(Command, command_id)
    if not cmd or cmd.device_id != device.id or cmd.kind != "message":
        raise HTTPException(404, "Message command not found")
    try:
        payload = json.loads(cmd.payload)
        message_type = str(payload.get("type", "notify")).lower()
    except Exception:
        message_type = "notify"
    if message_type not in {"question", "alert"}:
        raise HTTPException(400, "Message does not accept a response")
    response_type = body.response_type.strip().lower()
    if message_type == "question":
        if response_type != "reply":
            raise HTTPException(400, "Question requires a reply")
        text = body.text.strip()
        if not text:
            raise HTTPException(400, "Reply cannot be empty")
        result = f"Reply: {text}"[:2000]
    else:
        if response_type != "acknowledge":
            raise HTTPException(400, "Alert requires acknowledgement")
        result = "Acknowledged"
    cmd.status = "complete"
    cmd.result = result
    cmd.completed_at = datetime.now(timezone.utc)
    db.commit()
    return {"ok": True, "result": result}


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



# ----------------------------- Applications v0.4 -----------------------------
# These APIs grant HomeWatch catalogue/launcher access only. They never confer
# administrator rights. Client-supplied metadata is untrusted and must not be
# used as an authorization basis for privileged code execution.
class ApplicationCandidate(BaseModel):
    file_name: str
    file_path: str
    sha256: str
    file_size: int
    signature_status: str = "unknown"
    publisher: str = ""
    signer_thumbprint: str = ""
    signer_subject: str = ""
    signer_issuer: str = ""
    reason: str = ""

class ApplicationDecision(BaseModel):
    decision: str  # approved or denied
    scope: str = "device"  # device, child, global

class ApplicationLaunchResult(BaseModel):
    request_id: int
    status: str  # started / failed
    detail: str = ""


def normalise_app(body: ApplicationCandidate) -> dict:
    file_name = body.file_name.strip()
    file_path = body.file_path.strip()
    digest = body.sha256.strip().lower()
    thumbprint = body.signer_thumbprint.strip().lower()
    sig = body.signature_status.strip().lower()
    if (not file_name or len(file_name) > 255 or not file_name.lower().endswith('.exe') or
        '/' in file_name or '\\' in file_name or '\x00' in file_name):
        raise HTTPException(400, "A Windows executable filename is required")
    if (not file_path or len(file_path) > 2048 or '\x00' in file_path or
        not PureWindowsPath(file_path).is_absolute() or
        PureWindowsPath(file_path).name.casefold() != file_name.casefold()):
        raise HTTPException(400, "Invalid Windows executable path")
    if len(digest) != 64 or any(c not in '0123456789abcdef' for c in digest):
        raise HTTPException(400, "Valid SHA-256 fingerprint required")
    if sig not in ('valid', 'unsigned', 'invalid', 'unknown'):
        raise HTTPException(400, "Invalid signature state")
    if thumbprint and (len(thumbprint) > 64 or any(c not in '0123456789abcdef' for c in thumbprint)):
        raise HTTPException(400, "Invalid signer thumbprint")
    if body.file_size < 0 or body.file_size > 2**63-1:
        raise HTTPException(400, "Invalid file size")
    return dict(file_name=file_name, file_path=file_path, sha256=digest,
                file_size=body.file_size, signature_status=sig,
                publisher=body.publisher.strip()[:255], signer_thumbprint=thumbprint,
                signer_subject=body.signer_subject.strip()[:500],
                signer_issuer=body.signer_issuer.strip()[:500], reason=body.reason.strip()[:500])


def matching_approval(db: Session, device: Device, info: dict) -> Optional[ApplicationApproval]:
    # A signer thumbprint is not authorization on its own: binary hash must match.
    rows = db.scalars(select(ApplicationApproval).where(
        ApplicationApproval.sha256 == info['sha256'],
        ApplicationApproval.file_name == info['file_name'],
        ApplicationApproval.signature_status == info['signature_status'],
        ApplicationApproval.signer_thumbprint == info['signer_thumbprint'])).all()
    for a in rows:
        if a.scope == 'global' or a.scope == 'child' and a.child_id == device.child_id or a.scope == 'device' and a.device_id == device.id:
            return a
    return None


def presentation_json(db: Session, digest: str, fallback: str) -> dict:
    row = db.get(ApplicationPresentation, digest)
    name = row.display_name if row and row.display_name else Path(fallback).stem
    revision = (row.artwork_revision or 0) if row and row.artwork_file else 0
    return {"display_name": name, "artwork_revision": revision,
            "artwork_url": f"/api/applications/artwork/{digest}/{revision}" if revision else "",
            "artwork_credit": row.artwork_credit if row else ""}


def request_json(row: ApplicationRequest, db: Session, device: Device | None = None) -> dict:
    d = device or db.get(Device, row.device_id)
    info = {k: getattr(row, k) for k in ('file_name', 'sha256', 'signature_status', 'signer_thumbprint')}
    approval = matching_approval(db, d, info) if d else None
    status = 'approved' if approval else ('revoked' if row.status == 'approved' else row.status)
    return {
        'id': row.id, 'device_id': row.device_id, 'device_name': d.name if d else '',
        'child_id': row.child_id, 'child_name': db.get(Child,row.child_id).name if db.get(Child,row.child_id) else '',
        'file_name': row.file_name, 'file_path': row.file_path, 'sha256': row.sha256,
        'file_size': row.file_size, 'signature_status': row.signature_status,
        'publisher': row.publisher, 'signer_thumbprint': row.signer_thumbprint,
        'signer_subject': row.signer_subject, 'signer_issuer': row.signer_issuer,
        'reason': row.reason, 'status': status, 'approval_scope': approval.scope if approval else '',
        'elevation_allowed': bool(approval and (grant := db.get(GameElevationGrant, row.sha256)) and grant.enabled and grant.file_name.casefold() == row.file_name.casefold()),
        **presentation_json(db, row.sha256, row.file_name),
        'created_at': row.created_at.isoformat(), 'reviewed_at': row.reviewed_at.isoformat() if row.reviewed_at else None
    }

@app.post('/agent/applications/requests')
def agent_application_request(body: ApplicationCandidate, device: Device = Depends(agent_auth), db: Session = Depends(db_session)):
    info = normalise_app(body)
    previous = db.scalar(select(ApplicationRequest).where(
        ApplicationRequest.device_id == device.id,
        ApplicationRequest.sha256 == info['sha256'],
        ApplicationRequest.file_path == info['file_path'],
        ApplicationRequest.status == 'pending').order_by(ApplicationRequest.id.desc()))
    if previous: return request_json(previous, db, device)
    approval = matching_approval(db, device, info)
    row = ApplicationRequest(device_id=device.id, child_id=device.child_id, **info,
                             status='approved' if approval else 'pending',
                             decision_scope=approval.scope if approval else '')
    db.add(row); db.commit(); db.refresh(row)
    return request_json(row, db, device)

@app.get('/agent/applications')
def agent_applications(device: Device = Depends(agent_auth), db: Session = Depends(db_session)):
    rows = db.scalars(select(ApplicationRequest).where(ApplicationRequest.device_id == device.id)
                      .order_by(ApplicationRequest.id.desc()).limit(100)).all()
    return {'applications': [request_json(row, db, device) for row in rows]}

@app.post('/agent/applications/{request_id}/verify')
def agent_application_verify(request_id: int, body: ApplicationCandidate,
                              device: Device = Depends(agent_auth), db: Session = Depends(db_session)):
    row = db.get(ApplicationRequest, request_id)
    if not row or row.device_id != device.id: raise HTTPException(404, 'Application request not found')
    info = normalise_app(body)
    if info['file_path'] != row.file_path or info['sha256'] != row.sha256 or info['file_name'] != row.file_name:
        raise HTTPException(409, 'Application file changed; submit a new request')
    if info['signature_status'] != row.signature_status or info['signer_thumbprint'] != row.signer_thumbprint:
        raise HTTPException(409, 'Application signature changed; submit a new request')
    approval = matching_approval(db, device, info)
    if not approval: raise HTTPException(403, 'This application is not currently approved')
    grant = db.get(GameElevationGrant, row.sha256)
    return {'approved': True, 'approval_id': approval.id, 'scope': approval.scope,
            'elevated': bool(grant and grant.enabled and grant.file_name.casefold() == row.file_name.casefold())}

@app.post('/agent/applications/launched')
def agent_application_launched(body: ApplicationLaunchResult,
                               device: Device = Depends(agent_auth), db: Session = Depends(db_session)):
    row = db.get(ApplicationRequest, body.request_id)
    if not row or row.device_id != device.id: raise HTTPException(404, 'Application request not found')
    if body.status not in ('started', 'failed'): raise HTTPException(400, 'Invalid launch status')
    if body.status == 'started' and not matching_approval(db, device, {
        'file_name': row.file_name, 'sha256': row.sha256,
        'signature_status': row.signature_status, 'signer_thumbprint': row.signer_thumbprint
    }):
        raise HTTPException(403, 'Application not approved')
    db.add(ApplicationLaunch(device_id=device.id, request_id=row.id,
                             status=body.status[:20], detail=body.detail[:255]))
    db.commit()
    return {'ok': True}

@app.get('/api/applications', dependencies=[Depends(require_admin)])
def admin_applications(db: Session = Depends(db_session)):
    rows = db.scalars(select(ApplicationRequest).order_by(ApplicationRequest.id.desc()).limit(250)).all()
    approvals = db.scalars(select(ApplicationApproval).order_by(ApplicationApproval.id.desc()).limit(250)).all()
    launches = db.scalars(select(ApplicationLaunch).order_by(ApplicationLaunch.id.desc()).limit(30)).all()
    return {
        'requests': [request_json(row,db) for row in rows],
        'approvals': [dict(id=a.id, file_name=a.file_name, sha256=a.sha256, publisher=a.publisher,
                           signature_status=a.signature_status, scope=a.scope, device_id=a.device_id,
                           child_id=a.child_id, created_at=a.created_at.isoformat(),
                           elevation_allowed=bool((grant := db.get(GameElevationGrant, a.sha256)) and grant.enabled and grant.file_name.casefold() == a.file_name.casefold())) for a in approvals],
        'launches': [dict(id=x.id, device_id=x.device_id, request_id=x.request_id,
                          status=x.status, detail=x.detail, created_at=x.created_at.isoformat()) for x in launches]
    }

@app.post('/api/applications/requests/{request_id}/decision', dependencies=[Depends(require_admin)])
def decide_application(request_id: int, body: ApplicationDecision, db: Session = Depends(db_session)):
    row = db.get(ApplicationRequest, request_id)
    if not row: raise HTTPException(404, 'Application request not found')
    if row.status != 'pending': raise HTTPException(409, 'Request already reviewed')
    if body.decision not in ('approved', 'denied'):
        raise HTTPException(400, 'Invalid decision')
    if body.decision == 'approved':
        if db.get(Device, row.device_id).child_id != row.child_id:
            raise HTTPException(409, 'Device has been reassigned; the child must request this app again')
        if body.scope not in ('device', 'child', 'global'): raise HTTPException(400, 'Invalid scope')
        if body.scope == 'global' and (row.signature_status != 'valid' or not row.signer_thumbprint):
            raise HTTPException(400, 'Global approvals require a client-reported valid signature and signer')
        db.add(ApplicationApproval(
            request_id=row.id, scope=body.scope,
            device_id=row.device_id if body.scope == 'device' else None,
            child_id=db.get(Device,row.device_id).child_id if body.scope == 'child' else None,
            file_name=row.file_name, sha256=row.sha256, signature_status=row.signature_status,
            signer_thumbprint=row.signer_thumbprint, publisher=row.publisher))
        row.decision_scope = body.scope
    row.status = body.decision
    row.reviewed_at = datetime.now(timezone.utc)
    db.commit()
    return {'ok': True}

@app.delete('/api/applications/approvals/{approval_id}', dependencies=[Depends(require_admin)])
def revoke_application(approval_id: int, db: Session = Depends(db_session)):
    approval = db.get(ApplicationApproval, approval_id)
    if not approval: raise HTTPException(404, 'Approval not found')
    digest = approval.sha256
    db.delete(approval)
    db.flush()
    # A disabled/removed final launch approval must not leave a dormant
    # elevation grant waiting to re-activate on the next normal approval.
    if not db.scalar(select(ApplicationApproval).where(ApplicationApproval.sha256 == digest)):
        grant = db.get(GameElevationGrant, digest)
        if grant:
            grant.enabled = False
            grant.updated_at = datetime.now(timezone.utc)
    db.commit()
    return {'ok': True}


# ------------------ v0.5.8 parent-selected elevation policy ------------------
# These flags are policy only: never permit the server to submit a command line
# or an arbitrary path for privileged execution.
class ElevationSetting(BaseModel):
    enabled: bool


@app.put('/api/elevation/games/{digest}', dependencies=[Depends(require_admin)])
def set_game_elevation(digest: str, body: ElevationSetting, db: Session = Depends(db_session)):
    key = digest.lower()
    if not re.fullmatch(r'[0-9a-f]{64}', key):
        raise HTTPException(400, 'Invalid executable SHA-256')
    approval = db.scalar(select(ApplicationApproval).where(ApplicationApproval.sha256 == key))
    if not approval:
        raise HTTPException(409, 'Approve the exact game executable before configuring elevation')
    grant = db.get(GameElevationGrant, key)
    if grant is None:
        grant = GameElevationGrant(sha256=key, file_name=approval.file_name)
        db.add(grant)
    grant.file_name = approval.file_name
    grant.enabled = body.enabled
    grant.updated_at = datetime.now(timezone.utc)
    db.commit()
    return {'sha256': key, 'enabled': grant.enabled}


@app.put('/api/catalogue/{catalogue_id}/elevation', dependencies=[Depends(require_admin)])
def set_installer_elevation(catalogue_id: int, body: ElevationSetting, db: Session = Depends(db_session)):
    item = db.get(HouseholdCatalogueItem, catalogue_id)
    if item is None: raise HTTPException(404, 'Catalogue entry not found')
    if item.deployment_type == 'portable' and body.enabled:
        raise HTTPException(400, 'Portable games do not use the privileged installer broker')
    item.elevate_installer = body.enabled
    item.updated_at = datetime.now(timezone.utc)
    db.commit()
    return {'catalogue_id': item.id, 'enabled': item.elevate_installer}


@app.get('/agent/elevation/launch/{request_id}')
def elevation_game_authorisation(request_id: int, device: Device = Depends(agent_auth), db: Session = Depends(db_session)):
    row = db.get(ApplicationRequest, request_id)
    if row is None or row.device_id != device.id:
        raise HTTPException(404, 'Application request not found')
    info = {k: getattr(row, k) for k in ('file_name','sha256','signature_status','signer_thumbprint')}
    if not matching_approval(db, device, info):
        raise HTTPException(403, 'Application launch is not approved')
    grant = db.get(GameElevationGrant, row.sha256)
    if not grant or not grant.enabled or grant.file_name.casefold() != row.file_name.casefold():
        raise HTTPException(403, 'Elevated game launch is not approved')
    return {'request_id': row.id, 'file_name': row.file_name, 'file_path': row.file_path,
            'sha256': row.sha256, 'file_size': row.file_size,
            'signature_status': row.signature_status, 'signer_thumbprint': row.signer_thumbprint}


@app.get('/agent/elevation/installer/{catalogue_id}')
def elevation_installer_authorisation(catalogue_id: int, device: Device = Depends(agent_auth), db: Session = Depends(db_session)):
    item = db.get(HouseholdCatalogueItem, catalogue_id)
    if not item or not item.enabled or item.deployment_type != 'installer' or not item.elevate_installer:
        raise HTTPException(403, 'Elevated installer use is not approved')
    if not item.manifest_json:
        raise HTTPException(403, 'Elevated installation requires a verified package manifest')
    return {'catalogue_id': item.id, 'installer_sha256': item.installer_sha256,
            'package_entrypoint': item.package_entrypoint, 'manifest': json.loads(item.manifest_json)}


# ------------------------ Household software catalogue v0.4.5 ------------------------
# A published package is visible to ALL enrolled clients. The file is never
# fetched/executed by the server or privileged Windows updater service.
class CatalogueBody(BaseModel):
    name: str
    installer_path: str = ''
    installer_sha256: str = ''
    manifest_job_id: str = ''
    deployment_type: str = 'installer'
    description: str = ""
    enabled: bool = True


class InstallAttemptBody(BaseModel):
    status: str
    detail: str = ""


def canonical_installer_path(value: str) -> str:
    """Windows UNC share or absolute HTTP(S) URL; no local paths, credentials or traversal."""
    raw = value.strip()
    if not raw or len(raw) > 2048 or any(ord(c) < 32 for c in raw):
        raise HTTPException(400, "Installer path is missing or invalid")
    parsed = urllib.parse.urlsplit(raw)
    if parsed.scheme.lower() in ("https", "http"):
        if parsed.username or parsed.password or not parsed.hostname or parsed.fragment or parsed.query or parsed.port not in (None, 80, 443, 8080, 8443):
            raise HTTPException(400, "Use a direct installer URL without credentials, query parameters or fragments")
        segments = urllib.parse.unquote(parsed.path).split('/')
        if any(x in ('.', '..') for x in segments) or not parsed.path.lower().endswith('.exe'):
            raise HTTPException(400, "Installer URL must point directly to an .exe")
        return raw
    if '://' in raw:
        raise HTTPException(400, "Only Windows SMB shares and HTTP(S) installer URLs are supported")
    # Permit the convenient 192.168.1.250/installers/Folder/setup.exe notation.
    if not raw.startswith('\\\\') and not raw.startswith('//'):
        if not re.match(r'^[a-zA-Z0-9.-]+[/\\][^/\\]+[/\\]', raw):
            raise HTTPException(400, "Use \\\\SERVER\\share\\folder\\setup.exe or a direct HTTP(S) URL")
        raw = '\\\\' + raw
    path = raw.replace('/', '\\')
    # For safety, require an actual share and at least one file, reject administrative
    # shares and traversal. Do not allow credentials embedded in a path.
    parts = [segment for segment in path[2:].split('\\') if segment]
    if (not path.startswith('\\\\') or len(parts) < 3 or
        any(x in ('.','..') or ':' in x for x in parts) or
        parts[1].endswith('$') or not parts[-1].lower().endswith('.exe') or
        not re.fullmatch(r'[A-Za-z0-9.-]+', parts[0])):
        raise HTTPException(400, "Enter an accessible UNC share path ending in .exe")
    return '\\\\' + '\\'.join(parts)


def validate_catalogue(body: CatalogueBody) -> dict:
    name = body.name.strip()
    desc = body.description.strip()
    digest = body.installer_sha256.strip().lower()
    if not (1 <= len(name) <= 120) or any(ord(c) < 32 for c in name):
        raise HTTPException(400, "Name must contain 1–120 printable characters")
    if len(desc) > 600 or any(ord(c) < 32 and c not in '\n\r\t' for c in desc):
        raise HTTPException(400, "Description is too long or invalid")
    if not re.fullmatch(r'[0-9a-f]{64}', digest):
        raise HTTPException(400, "Enter the SHA-256 fingerprint of the installer on your NAS")
    if body.deployment_type != 'installer':
        raise HTTPException(400, 'Portable games must be published using the verified NAS folder scanner')
    return dict(name=name, installer_path=canonical_installer_path(body.installer_path),
                installer_sha256=digest, description=desc, enabled=body.enabled, deployment_type='installer')


def catalogue_json(db: Session, item: HouseholdCatalogueItem, include_path: bool = True, device_id: str = '') -> dict:
    presentation = presentation_json(db, item.installer_sha256, item.name)
    settings = db.get(NasLibrarySettings, 1)
    game_scan_roots = settings.game_scan_roots.split(';') if settings and settings.game_scan_roots else [r'C:\Games', r'D:\Games']
    state = None
    if device_id:
        found = db.scalar(select(HouseholdInstallation).where(
            HouseholdInstallation.device_id == device_id,
            HouseholdInstallation.catalogue_id == item.id))
        if found:
            approved = False
            if found.request_id:
                req = db.get(ApplicationRequest, found.request_id)
                approved = bool(found.status != 'uninstalled' and req and request_json(req, db, db.get(Device, device_id))['status'] == 'approved')
            state = dict(status=found.status, installed_path=found.installed_path,
                         install_root=found.install_root, executable_path=found.executable_path, executable_sha256=found.executable_sha256,
                         installed_manifest_sha256=found.installed_manifest_sha256,
                         update_available=bool(item.deployment_type == 'portable' and found.status not in ('uninstalled', 'not_installed')
                             and found.installed_manifest_sha256 != item.installer_sha256),
                         request_id=found.request_id, approved=approved)
    manifest = json.loads(item.manifest_json) if item.manifest_json else None
    return dict(id=item.id, name=presentation['display_name'] or item.name,
                installer_path=item.installer_path if include_path else None,
                installer_sha256=item.installer_sha256, description=item.description,
                package_root=item.package_root, package_entrypoint=item.package_entrypoint,
                manifest=manifest, total_bytes=item.total_bytes, file_count=item.file_count,
                launch_relative_path=item.launch_relative_path, elevate_installer=item.elevate_installer,
                deployment_type=item.deployment_type,
                installation=state, game_scan_roots=game_scan_roots,
                enabled=item.enabled, created_at=item.created_at.isoformat(),
                updated_at=item.updated_at.isoformat(),
                **{k: v for k,v in presentation.items() if k != 'display_name'})


def catalogue_input(body: CatalogueBody) -> dict:
    if body.manifest_job_id:
        with nas_job_lock:
            job = nas_jobs.get(body.manifest_job_id)
            if not job or job['status'] != 'ready':
                raise HTTPException(400, 'Approved manifest scan is not available or complete')
            result = job['manifest']
            folder = job['folder']
            share = job['share']
        mode = job.get('deployment_type', 'installer')
        if body.deployment_type != mode:
            raise HTTPException(400, 'The selected package mode changed; scan the NAS folder again')
        # Portable games contain no installer; the entrypoint is their main game EXE.
        if mode == 'portable':
            safe_root = nas_library.resolve(share, folder)
            info = dict(name=body.name.strip(), installer_path=safe_root,
                        installer_sha256=result['digest'], description=body.description.strip(),
                        enabled=body.enabled, deployment_type='portable')
            if not (1 <= len(info['name']) <= 120) or len(info['description']) > 600 or any(ord(c)<32 for c in info['name']) or any(ord(c)<32 and c not in '\n\r\t' for c in info['description']):
                raise HTTPException(400, 'Invalid name or description')
        else:
            canon = CatalogueBody(name=body.name, description=body.description, enabled=body.enabled,
                installer_sha256=result['digest'],
                installer_path=nas_library.resolve(share, folder + ('\\' if folder else '') + result['entrypoint']))
            info = validate_catalogue(canon)
        info.update(package_root=nas_library.resolve(share, folder),
                    package_entrypoint=result['entrypoint'],
                    manifest_json=json.dumps(result, sort_keys=True, separators=(',', ':')),
                    total_bytes=result['total_bytes'], file_count=result['file_count'])
        return info
    # Preserve v0.4.5 manual single-file records.
    return validate_catalogue(body)


@app.get('/api/catalogue', dependencies=[Depends(require_admin)])
def admin_catalogue(db: Session = Depends(db_session)):
    rows = db.scalars(select(HouseholdCatalogueItem).order_by(HouseholdCatalogueItem.id.desc())).all()
    audit = db.scalars(select(HouseholdInstallAttempt).order_by(HouseholdInstallAttempt.id.desc()).limit(50)).all()
    suggestions = db.scalars(select(HouseholdLaunchSuggestion).where(
        HouseholdLaunchSuggestion.status == 'pending').order_by(
        HouseholdLaunchSuggestion.created_at.desc()).limit(80)).all()
    return {'items': [catalogue_json(db, row) for row in rows],
            'launch_suggestions': [dict(id=x.id, catalogue_id=x.catalogue_id, device_id=x.device_id,
                                        relative_path=x.relative_path, installer_sha256=x.installer_sha256,
                                        executable_sha256=x.executable_sha256,
                                        created_at=x.created_at.isoformat()) for x in suggestions],
            'installations': [dict(catalogue_id=x.catalogue_id, device_id=x.device_id, status=x.status,
                                   install_root=x.install_root, installed_path=x.installed_path, executable_path=x.executable_path,
                                   updated_at=x.updated_at.isoformat())
                              for x in db.scalars(select(HouseholdInstallation).order_by(HouseholdInstallation.updated_at.desc()).limit(100)).all()],
            'attempts': [dict(id=a.id, catalogue_id=a.catalogue_id, device_id=a.device_id,
                              status=a.status, detail=a.detail, created_at=a.created_at.isoformat()) for a in audit]}


@app.post('/api/catalogue', dependencies=[Depends(require_admin)])
def add_catalogue_item(body: CatalogueBody, db: Session = Depends(db_session)):
    info = catalogue_input(body)
    row = HouseholdCatalogueItem(**info)
    if row.deployment_type == 'portable':
        row.launch_relative_path = row.package_entrypoint
    db.add(row)
    # Reuse presentation and SteamGridDB artwork mechanism keyed by exact installer digest.
    existing = db.get(ApplicationPresentation, info['installer_sha256'])
    if not existing:
        db.add(ApplicationPresentation(sha256=info['installer_sha256'], display_name=info['name']))
    db.commit(); db.refresh(row)
    return catalogue_json(db, row)


@app.put('/api/catalogue/{catalogue_id}', dependencies=[Depends(require_admin)])
def edit_catalogue_item(catalogue_id: int, body: CatalogueBody, db: Session = Depends(db_session)):
    row = db.get(HouseholdCatalogueItem, catalogue_id)
    if not row: raise HTTPException(404, 'Catalogue entry not found')
    if not body.manifest_job_id and row.deployment_type == 'portable' and body.deployment_type == 'portable' and body.installer_path == row.installer_path and body.installer_sha256 == row.installer_sha256:
        info = dict(name=body.name.strip(), description=body.description.strip(), enabled=body.enabled,
                    installer_path=row.installer_path, installer_sha256=row.installer_sha256,
                    deployment_type='portable', package_root=row.package_root,
                    package_entrypoint=row.package_entrypoint, manifest_json=row.manifest_json,
                    total_bytes=row.total_bytes, file_count=row.file_count)
        if not (1 <= len(info['name']) <= 120) or len(info['description']) > 600:
            raise HTTPException(400, 'Invalid name or description')
    else:
        info = catalogue_input(body)
    if not body.manifest_job_id and row.manifest_json and body.installer_path == row.installer_path and body.installer_sha256 == row.installer_sha256 and body.deployment_type == row.deployment_type:
        info.update(package_root=row.package_root, package_entrypoint=row.package_entrypoint, manifest_json=row.manifest_json, total_bytes=row.total_bytes, file_count=row.file_count, deployment_type=row.deployment_type)
    prior_presentation = db.get(ApplicationPresentation, row.installer_sha256)
    if info["installer_sha256"] != row.installer_sha256:
        row.elevate_installer = False
        row.launch_relative_path = info['package_entrypoint'] if info['deployment_type'] == 'portable' else ''
    if info['deployment_type'] == 'portable':
        row.elevate_installer = False
    for key, value in info.items(): setattr(row, key, value)
    row.updated_at = datetime.now(timezone.utc)
    presentation = db.get(ApplicationPresentation, row.installer_sha256)
    if presentation is None:
        if row.deployment_type == 'portable' and prior_presentation and prior_presentation.artwork_file:
            # Revisions retain family-approved tile art, even though each
            # package's immutable SHA-256 fingerprint changes.
            db.add(ApplicationPresentation(sha256=row.installer_sha256, display_name=row.name,
                artwork_revision=prior_presentation.artwork_revision or 0,
                artwork_file=prior_presentation.artwork_file,
                artwork_credit=prior_presentation.artwork_credit))
        else:
            db.add(ApplicationPresentation(sha256=row.installer_sha256, display_name=row.name))
    else:
        presentation.display_name = row.name
    db.commit(); db.refresh(row)
    return catalogue_json(db, row)


@app.delete('/api/catalogue/{catalogue_id}', dependencies=[Depends(require_admin)])
def remove_catalogue_item(catalogue_id: int, db: Session = Depends(db_session)):
    # Unpublish instead of deleting: keep the install audit trail.
    row = db.get(HouseholdCatalogueItem, catalogue_id)
    if not row: raise HTTPException(404, 'Catalogue entry not found')
    row.enabled = False
    row.updated_at = datetime.now(timezone.utc)
    db.commit()
    return {'ok': True}


@app.get('/agent/catalogue')
def agent_catalogue(device: Device = Depends(agent_auth), db: Session = Depends(db_session)):
    rows = db.scalars(select(HouseholdCatalogueItem).where(
        HouseholdCatalogueItem.enabled == True).order_by(HouseholdCatalogueItem.name)).all()
    return {'items': [catalogue_json(db, row, device_id=device.id) for row in rows]}


@app.get('/agent/catalogue/{catalogue_id}')
def agent_catalogue_item(catalogue_id: int, device: Device = Depends(agent_auth), db: Session = Depends(db_session)):
    row = db.get(HouseholdCatalogueItem, catalogue_id)
    if not row or not row.enabled: raise HTTPException(404, 'Package is no longer published')
    return catalogue_json(db, row, device_id=device.id)


@app.post('/agent/catalogue/{catalogue_id}/attempts')
def agent_install_attempt(catalogue_id: int, body: InstallAttemptBody,
                          device: Device = Depends(agent_auth), db: Session = Depends(db_session)):
    row = db.get(HouseholdCatalogueItem, catalogue_id)
    if not row: raise HTTPException(404, 'Catalogue entry not found')
    if body.status not in ('started','failed','hash_mismatch','removed','remove_failed'):
        raise HTTPException(400, 'Invalid install attempt status')
    db.add(HouseholdInstallAttempt(catalogue_id=catalogue_id, device_id=device.id,
                                  status=body.status, detail=body.detail[:300]))
    db.commit()
    return {'ok': True}


# ------------------- Centrally managed launcher presentation v0.4.2 -------------------
# This artwork feature is metadata only; it does not affect approval matching
# or grant any privileges to the target EXE.

# ------------------------ SMB NAS browser / package hashing v0.5.0 ------------------------
# Passwords never appear in API responses or agent catalogue data. Rotate the
# SESSION_SECRET only after re-entering the NAS password (it encrypts it at rest).
_fernet = Fernet(base64.urlsafe_b64encode(hashlib.sha256((SESSION_SECRET + '/nas-v1').encode()).digest()))
nas_jobs: dict[str, dict] = {}
nas_job_lock = threading.Lock()
nas_pool = ThreadPoolExecutor(max_workers=2, thread_name_prefix='nas-scan')


class NasConfigBody(BaseModel):
    root_unc: str
    username: str = ''
    password: str = ''
    game_scan_roots: str = r'C:\Games;D:\Games'


class NasScanBody(BaseModel):
    folder: str
    entrypoint: str
    deployment_type: str = 'installer'


def get_nas_settings(db: Session):
    settings = db.get(NasLibrarySettings, 1)
    if settings is None:
        raise HTTPException(400, 'Set up the NAS share in Applications first')
    try:
        pwd = _fernet.decrypt(settings.encrypted_password.encode()).decode() if settings.encrypted_password else ''
    except Exception:
        raise HTTPException(500, 'NAS credentials could not be decrypted; re-enter them')
    return settings.root_unc, settings.username, pwd


@app.get('/api/nas/config', dependencies=[Depends(require_admin)])
def get_nas_config(db: Session = Depends(db_session)):
    row = db.get(NasLibrarySettings, 1)
    return {'root_unc': row.root_unc if row else '', 'username': row.username if row else '',
            'has_password': bool(row.encrypted_password) if row else False,
            'game_scan_roots': row.game_scan_roots if row else r'C:\Games;D:\Games'}


@app.put('/api/nas/config', dependencies=[Depends(require_admin)])
def save_nas_config(body: NasConfigBody, db: Session = Depends(db_session)):
    try: root = nas_library.validate_root(body.root_unc)
    except ValueError as e: raise HTTPException(400, str(e))
    if len(body.username) > 255 or len(body.password) > 500:
        raise HTTPException(400, 'Credentials are too long')
    row = db.get(NasLibrarySettings, 1)
    if row is None:
        row = NasLibrarySettings(id=1, root_unc=root)
        db.add(row)
    if root != row.root_unc:
        # Changing the server root does not alter existing approved packages.
        row.encrypted_password = ''
    row.root_unc = root
    roots = [v.strip() for v in body.game_scan_roots.split(';') if v.strip()]
    if len(roots) > 12 or any(len(v) > 255 or not re.fullmatch(r'[A-Za-z]:\\[^:*?"<>|]*', v) or '..' in v.split('\\') for v in roots):
        raise HTTPException(400, 'Game scan folders must be local Windows paths such as C:\\Games;D:\\Games')
    row.game_scan_roots = ';'.join(roots)
    row.username = body.username.strip()
    if body.password:
        row.encrypted_password = _fernet.encrypt(body.password.encode()).decode()
    db.commit()
    return {'root_unc': root, 'username': row.username, 'has_password': bool(row.encrypted_password),
            'game_scan_roots': row.game_scan_roots}


@app.get('/api/nas/browse', dependencies=[Depends(require_admin)])
def browse_nas(path: str = '', offset: int = 0, db: Session = Depends(db_session)):
    root, user, pwd = get_nas_settings(db)
    try:
        relative = nas_library.validate_relative(path)
        page = nas_library.list_directory_page(root, relative, user, pwd, offset=offset)
        return {'root': root, 'path': relative, **page}
    except ValueError as e:
        raise HTTPException(400, str(e))
    except Exception as e:
        raise HTTPException(502, f'Could not browse NAS share: {type(e).__name__}')


def run_nas_scan(job_id, root, folder, entrypoint, username, pwd, deployment_type='installer'):
    def progress(filename, done, total):
        with nas_job_lock:
            job = nas_jobs[job_id]
            job['current_file'] = filename
            job['bytes_hashed'] = done
            job['total_bytes'] = total
    try:
        manifest = nas_library.scan_package(root, folder, entrypoint, username, pwd, progress,
                                            deployment_type=deployment_type)
        with nas_job_lock:
            nas_jobs[job_id].update(status='ready', manifest=manifest, bytes_hashed=manifest['total_bytes'],
                                    total_bytes=manifest['total_bytes'], current_file='')
    except Exception as e:
        with nas_job_lock:
            nas_jobs[job_id].update(status='failed', error=f'{type(e).__name__}: {e}'[:300])


@app.post('/api/nas/scan', dependencies=[Depends(require_admin)])
def start_nas_scan(body: NasScanBody, db: Session = Depends(db_session)):
    root, username, pwd = get_nas_settings(db)
    try:
        folder = nas_library.validate_relative(body.folder)
        entrypoint = nas_library.validate_relative(body.entrypoint, allow_empty=False)
        if body.deployment_type not in ('installer', 'portable'):
            raise ValueError('Choose a supported deployment type')
        if not entrypoint.lower().endswith('.exe') or (body.deployment_type == 'installer' and '\\' in entrypoint):
            raise ValueError('Select an EXE from the package folder')
    except ValueError as e: raise HTTPException(400, str(e))
    with nas_job_lock:
        busy = sum(j['status'] == 'hashing' for j in nas_jobs.values())
        if busy >= 2: raise HTTPException(429, 'Two NAS scans are already in progress')
        # Short-lived in-memory jobs; one worker process recommended for v0.5.0.
        if len(nas_jobs) > 100:
            for k in list(nas_jobs)[:50]:
                if nas_jobs[k]['status'] != 'hashing': nas_jobs.pop(k)
        job_id = uuid.uuid4().hex
        nas_jobs[job_id] = dict(status='hashing', share=root, folder=folder, entrypoint=entrypoint,
                                deployment_type=body.deployment_type, bytes_hashed=0, total_bytes=0, current_file='', error='')
    nas_pool.submit(run_nas_scan, job_id, root, folder, entrypoint, username, pwd, body.deployment_type)
    return {'job_id': job_id}


@app.get('/api/nas/scans/{job_id}', dependencies=[Depends(require_admin)])
def get_nas_scan(job_id: str):
    with nas_job_lock:
        item = nas_jobs.get(job_id)
        if not item: raise HTTPException(404, 'NAS scan not found; start another scan')
        return {k: v for k, v in item.items() if k not in ('share',)}


def valid_launch_relative_path(value: str) -> str:
    """Strict Windows relative EXE inside game root (no drive, UNC or traversal)."""
    if not value or len(value) > 700 or '/' in value or ':' in value or value.startswith('\\'):
        raise HTTPException(400, 'Invalid relative executable path')
    parts = value.split('\\')
    if (any(not p or p in ('.', '..') or p.endswith((' ', '.')) or
            any(ord(c) < 32 or c in '<>:"/\\|?*' for c in p) for p in parts) or
            not parts[-1].lower().endswith('.exe')):
        raise HTTPException(400, 'Invalid relative executable path')
    return value


def relative_from_install_root(install_root: str, exe_path: str) -> str:
    if (len(install_root) > 2048 or len(exe_path) > 2048 or
        not ntpath.isabs(install_root) or not ntpath.isabs(exe_path) or
        install_root.startswith('\\\\') or exe_path.startswith('\\\\')):
        raise HTTPException(400, 'Provide absolute local install and executable paths')
    if not re.match(r'^[A-Za-z]:\\', install_root) or not re.match(r'^[A-Za-z]:\\', exe_path):
        raise HTTPException(400, 'Use local Windows drive paths')
    root = ntpath.normcase(ntpath.normpath(install_root))
    exe = ntpath.normcase(ntpath.normpath(exe_path))
    if not exe.startswith(root.rstrip('\\') + '\\'):
        raise HTTPException(400, 'Executable must be inside the installation root')
    relative = ntpath.relpath(ntpath.normpath(exe_path), ntpath.normpath(install_root))
    return valid_launch_relative_path(relative)


class LaunchHintDecision(BaseModel):
    decision: str


class LaunchPathBody(BaseModel):
    relative_path: str


@app.put('/api/catalogue/{catalogue_id}/launch-path', dependencies=[Depends(require_admin)])
def set_launch_path(catalogue_id: int, body: LaunchPathBody, db: Session = Depends(db_session)):
    item = db.get(HouseholdCatalogueItem, catalogue_id)
    if not item: raise HTTPException(404, 'Package not found')
    item.launch_relative_path = valid_launch_relative_path(body.relative_path)
    item.updated_at = datetime.now(timezone.utc)
    db.commit()
    return {'ok': True, 'launch_relative_path': item.launch_relative_path}


@app.post('/api/catalogue/{catalogue_id}/launch-hints/{hint_id}/review', dependencies=[Depends(require_admin)])
def review_launch_hint(catalogue_id: int, hint_id: int, body: LaunchHintDecision, db: Session = Depends(db_session)):
    if body.decision not in ('approve', 'reject'):
        raise HTTPException(400, 'Select approve or reject')
    item = db.get(HouseholdCatalogueItem, catalogue_id)
    hint = db.get(HouseholdLaunchSuggestion, hint_id)
    if not item or not hint or hint.catalogue_id != catalogue_id or hint.status != 'pending':
        raise HTTPException(404, 'Launch suggestion not found')
    # Check it still matches the current published package and device installation.
    installed = db.scalar(select(HouseholdInstallation).where(
        HouseholdInstallation.catalogue_id == catalogue_id,
        HouseholdInstallation.device_id == hint.device_id))
    if body.decision == 'approve':
        if (not installed or installed.executable_sha256 != hint.executable_sha256 or
            not installed.request_id or hint.installer_sha256 != item.installer_sha256):
            raise HTTPException(409, 'Installation has changed since this suggestion')
        item.launch_relative_path = valid_launch_relative_path(hint.relative_path)
        item.updated_at = datetime.now(timezone.utc)
    hint.status = 'approved' if body.decision == 'approve' else 'rejected'
    db.commit()
    return {'ok': True, 'launch_relative_path': item.launch_relative_path}


@app.delete('/api/catalogue/{catalogue_id}/launch-hint', dependencies=[Depends(require_admin)])
def clear_launch_hint(catalogue_id: int, db: Session = Depends(db_session)):
    item = db.get(HouseholdCatalogueItem, catalogue_id)
    if not item: raise HTTPException(404, 'Package not found')
    item.launch_relative_path = ''
    item.updated_at = datetime.now(timezone.utc)
    db.commit()
    return {'ok': True}


class InstallationBody(BaseModel):
    status: str
    installed_path: str = ''
    executable_path: str = ''
    executable_sha256: str = ''
    request_id: Optional[int] = None
    install_root: str = ''
    installed_manifest_sha256: str = ''


@app.put('/agent/catalogue/{catalogue_id}/installation')
def save_device_installation(catalogue_id: int, body: InstallationBody,
                             device: Device = Depends(agent_auth), db: Session = Depends(db_session)):
    item = db.get(HouseholdCatalogueItem, catalogue_id)
    if not item: raise HTTPException(404, 'Unknown package')
    if body.status not in ('copying', 'verifying', 'installing', 'detecting', 'awaiting_approval', 'installed', 'failed', 'uninstalling', 'uninstalled', 'uninstall_failed', 'updating'):
        raise HTTPException(400, 'Unknown installation state')
    if (len(body.installed_path) > 2048 or len(body.executable_path) > 2048 or
        len(body.install_root) > 2048 or
        len(body.executable_sha256) not in (0, 64)):
        raise HTTPException(400, 'Invalid installed application')
    if body.installed_manifest_sha256 and not re.fullmatch('[a-f0-9]{64}', body.installed_manifest_sha256):
        raise HTTPException(400, 'Invalid installed manifest hash')
    if body.executable_sha256 and not re.fullmatch('[a-f0-9]{64}', body.executable_sha256):
        raise HTTPException(400, 'Invalid executable hash')
    if body.request_id:
        req = db.get(ApplicationRequest, body.request_id)
        if not req or req.device_id != device.id or req.sha256 != body.executable_sha256:
            raise HTTPException(403, 'The application request does not belong to this device and executable')
    row = db.scalar(select(HouseholdInstallation).where(HouseholdInstallation.catalogue_id == catalogue_id,
                 HouseholdInstallation.device_id == device.id))
    if row is None:
        row = HouseholdInstallation(catalogue_id=catalogue_id, device_id=device.id)
        db.add(row)
    # A device can suggest a path, never directly set the household-wide hint.
    if body.status == 'awaiting_approval' and body.request_id and body.install_root:
        relative = relative_from_install_root(body.install_root, body.executable_path)
        if relative.casefold() != item.launch_relative_path.casefold():
            existing = db.scalar(select(HouseholdLaunchSuggestion).where(
                HouseholdLaunchSuggestion.catalogue_id == catalogue_id,
                HouseholdLaunchSuggestion.device_id == device.id,
                HouseholdLaunchSuggestion.status == 'pending'))
            if existing is None:
                existing = HouseholdLaunchSuggestion(catalogue_id=catalogue_id, device_id=device.id,
                    relative_path=relative, installer_sha256=item.installer_sha256,
                    executable_sha256=body.executable_sha256)
                db.add(existing)
            else:
                existing.relative_path = relative
                existing.installer_sha256 = item.installer_sha256
                existing.executable_sha256 = body.executable_sha256
    # Only allow clearing an existing installation record, never report an
    # arbitrary game's removal. Existing approvals remain in the audit history.
    if body.status in ('uninstalling', 'uninstalled', 'uninstall_failed') and not row.executable_path:
        raise HTTPException(409, 'No installed game is recorded on this device')
    row.status = body.status
    if body.status == 'uninstall_failed':
        row.updated_at = datetime.now(timezone.utc)
        db.commit()
        return {'ok': True}
    if body.status == 'uninstalled':
        row.install_root = ''
        row.installed_manifest_sha256 = ''
        row.installed_path = ''
        # Keep the historical executable path and request ID to prevent stale
        # standalone approval tiles reappearing after a game's files are removed.
        row.updated_at = datetime.now(timezone.utc)
        db.commit()
        return {'ok': True}
    if body.install_root:
        row.install_root = body.install_root
    if body.installed_manifest_sha256:
        if item.deployment_type != 'portable' or body.installed_manifest_sha256 != item.installer_sha256:
            raise HTTPException(409, 'Published portable revision has changed')
        row.installed_manifest_sha256 = body.installed_manifest_sha256
    # Status-only copying/updating reports must not erase the previously
    # installed executable, its approval or revision while a transfer runs.
    if body.installed_path:
        row.installed_path = body.installed_path
    if body.executable_path:
        row.executable_path = body.executable_path
    if body.executable_sha256:
        row.executable_sha256 = body.executable_sha256
    if body.request_id is not None:
        row.request_id = body.request_id
    row.updated_at = datetime.now(timezone.utc)
    db.commit()
    return {'ok': True}


class PresentationBody(BaseModel):
    display_name: str


class ArtworkChoice(BaseModel):
    game_id: int
    artwork_id: int


def known_digest(db: Session, digest: str) -> str:
    digest = digest.lower()
    if not re.fullmatch(r"[0-9a-f]{64}", digest):
        raise HTTPException(400, "Invalid SHA-256 fingerprint")
    if not db.scalar(select(ApplicationRequest.id).where(ApplicationRequest.sha256 == digest)) and not db.scalar(
        select(HouseholdCatalogueItem.id).where(HouseholdCatalogueItem.installer_sha256 == digest)
    ):
        raise HTTPException(404, "Application not found")
    return digest


def steamgrid_json(path: str) -> dict:
    if not STEAMGRIDDB_API_KEY:
        raise HTTPException(503, "Set STEAMGRIDDB_API_KEY on the HomeWatch server to search artwork")
    url = "https://www.steamgriddb.com/api/v2/" + path
    request = urllib.request.Request(url, headers={"Authorization": "Bearer " + STEAMGRIDDB_API_KEY,
                                                  "Accept": "application/json", "User-Agent": "HomeWatch/0.4.2"})
    try:
        with urllib.request.urlopen(request, timeout=12) as response:
            raw = response.read(1_000_001)
        if len(raw) > 1_000_000: raise ValueError("SteamGridDB response too large")
        return json.loads(raw)
    except Exception as ex:
        raise HTTPException(502, f"SteamGridDB API unavailable: {type(ex).__name__}") from ex


def grid_choices(game_id: int) -> list[dict]:
    if game_id < 1 or game_id > 2**31: raise HTTPException(400, "Invalid game")
    payload = steamgrid_json(f"grids/game/{game_id}?dimensions=600x900")
    return [{"id": int(a["id"]), "thumb": str(a.get("thumb", "")),
             "width": a.get("width"), "height": a.get("height")}
            for a in payload.get("data", [])[:24] if isinstance(a, dict) and "id" in a]


@app.get('/api/applications/artwork/search', dependencies=[Depends(require_admin)])
def search_game_artwork(q: str):
    q = q.strip()
    if len(q) < 2 or len(q) > 80: raise HTTPException(400, "Search for 2–80 characters")
    data = steamgrid_json("search/autocomplete/" + urllib.parse.quote(q, safe=""))
    return {"games": [{"id": int(x["id"]), "name": str(x["name"])[:120]}
                      for x in data.get("data", [])[:12] if "id" in x and "name" in x]}


@app.get('/api/applications/artwork/games/{game_id}', dependencies=[Depends(require_admin)])
def game_artwork(game_id: int):
    return {"artwork": grid_choices(game_id)}


@app.put('/api/applications/{digest}/presentation', dependencies=[Depends(require_admin)])
def set_app_presentation(digest: str, body: PresentationBody, db: Session = Depends(db_session)):
    digest = known_digest(db, digest)
    name = body.display_name.strip()
    if not (1 <= len(name) <= 120) or any(ord(c) < 32 for c in name):
        raise HTTPException(400, "Enter a friendly name between 1 and 120 characters")
    item = db.get(ApplicationPresentation, digest)
    if not item:
        item = ApplicationPresentation(sha256=digest)
        db.add(item)
    item.display_name = name
    item.updated_at = datetime.now(timezone.utc)
    db.commit()
    return presentation_json(db, digest, name)


class NoArtworkRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise ValueError("External image redirect is not allowed")


def download_artwork(url: str) -> bytes:
    parsed = urllib.parse.urlsplit(url)
    # Only SteamGridDB-owned HTTPS asset hosts, no redirects/credentials/ports.
    if (parsed.scheme != "https" or parsed.username or parsed.password or parsed.port is not None
            or not (parsed.hostname == "steamgriddb.com" or (parsed.hostname or "").endswith(".steamgriddb.com"))):
        raise HTTPException(400, "Artwork URL is not on SteamGridDB")
    opener = urllib.request.build_opener(NoArtworkRedirect())
    try:
        with opener.open(urllib.request.Request(url, headers={"User-Agent": "HomeWatch/0.4.2"}), timeout=15) as response:
            image_bytes = response.read(6_000_001)
        if len(image_bytes) > 6_000_000: raise ValueError("Artwork is larger than 6 MB")
        with Image.open(io.BytesIO(image_bytes)) as img:
            img.verify()
        with Image.open(io.BytesIO(image_bytes)) as img:
            if img.width > 4000 or img.height > 4000 or img.width < 100 or img.height < 100:
                raise ValueError("Unsupported artwork dimensions")
            img.thumbnail((600, 900), Image.Resampling.LANCZOS)
            target = io.BytesIO()
            img.convert("RGB").save(target, format="JPEG", quality=88, optimize=True)
            return target.getvalue()
    except (OSError, ValueError, UnidentifiedImageError) as ex:
        raise HTTPException(422, f"Cannot process artwork: {type(ex).__name__}") from ex


@app.post('/api/applications/{digest}/artwork', dependencies=[Depends(require_admin)])
def set_app_artwork(digest: str, body: ArtworkChoice, db: Session = Depends(db_session)):
    digest = known_digest(db, digest)
    # Resolve the submitted ID against a fresh authenticated SGDB API response.
    if body.game_id < 1 or body.game_id > 2**31:
        raise HTTPException(400, "Invalid game")
    raw = steamgrid_json(f"grids/game/{body.game_id}?dimensions=600x900")
    grid = next((a for a in raw.get('data', [])[:24]
                 if isinstance(a, dict) and a.get('id') == body.artwork_id), None)
    if not grid or not isinstance(grid.get('url'), str):
        raise HTTPException(400, "Selected artwork is unavailable")
    binary = download_artwork(grid['url'])
    item = db.get(ApplicationPresentation, digest)
    if not item:
        item = ApplicationPresentation(sha256=digest)
        db.add(item)
    # ORM defaults only materialize at INSERT/flush; newly constructed rows
    # still have artwork_revision=None at this point.
    revision = (item.artwork_revision or 0) + 1
    filename = f"{digest}-{revision}.jpg"
    temp_path = ARTWORK_DIR / (filename + ".tmp")
    temp_path.write_bytes(binary)
    temp_path.replace(ARTWORK_DIR / filename)
    previous_file = item.artwork_file
    item.artwork_revision = revision
    item.artwork_file = filename
    item.artwork_credit = f"SteamGridDB game {body.game_id}, artwork {body.artwork_id}"
    item.updated_at = datetime.now(timezone.utc)
    db.commit()
    if previous_file and previous_file != filename:
        (ARTWORK_DIR / previous_file).unlink(missing_ok=True)
    fallback = db.scalar(select(ApplicationRequest.file_name).where(ApplicationRequest.sha256 == digest)) or ''
    return presentation_json(db, digest, fallback)


@app.get('/api/applications/artwork/{digest}/{revision}', dependencies=[Depends(require_admin)])
def admin_serve_artwork(digest: str, revision: int, db: Session = Depends(db_session)):
    return serve_artwork(digest, revision, db)


@app.get('/agent/applications/artwork/{digest}/{revision}')
def agent_serve_artwork(digest: str, revision: int, device: Device = Depends(agent_auth),
                        db: Session = Depends(db_session)):
    # Per-device application requests remain private. Published catalogue items
    # may be viewed by any enrolled agent so posters propagate across the family.
    known_digest(db, digest)
    private_request = db.scalar(select(ApplicationRequest.id).where(
        ApplicationRequest.device_id == device.id, ApplicationRequest.sha256 == digest))
    published_catalogue = db.scalar(select(HouseholdCatalogueItem.id).where(
        HouseholdCatalogueItem.enabled == True, HouseholdCatalogueItem.installer_sha256 == digest))
    if not private_request and not published_catalogue:
        raise HTTPException(404, 'Artwork unavailable')
    return serve_artwork(digest, revision, db)


def serve_artwork(digest: str, revision: int, db: Session):
    item = db.get(ApplicationPresentation, digest)
    if not item or not item.artwork_file or item.artwork_revision != revision:
        raise HTTPException(404, "Artwork not found")
    return FileResponse(ARTWORK_DIR / item.artwork_file, media_type="image/jpeg",
                        headers={"Cache-Control": "private, max-age=3600", "X-Content-Type-Options": "nosniff"})
