# HomeWatch v0.5.9b — Reconfigure server connection without resetting enrolment

This is a Windows-agent hotfix on top of v0.5.9a. The HomeWatch server itself is an HTTP Uvicorn process: `0.0.0.0:8000` (mapped to host port `8090` in example Docker Compose). **It does not natively serve HTTPS in this release.** HTTPS requires a trusted TLS-terminating reverse proxy (for example Nginx Proxy Manager, Caddy, or nginx).

## Change a provisioned client's server address

1. Configure an HTTPS reverse proxy for the SAME existing HomeWatch server/database. Its upstream is `http://<docker-host>:8090` or, on the same Docker network, `http://app:8000`.
2. Ensure your Windows clients trust the proxy's TLS certificate, and the proxy allows HomeWatch's `/agent/` API paths without additional interactive authentication.
3. On a Windows client open HomeWatch Launcher > Performance settings (admin) > **Server connection...**.
4. Enter the HTTPS origin, choose **Test and save**, and approve the change. HomeWatch tests `GET /agent/catalogue` with the existing device ID/token **before writing**. A failure leaves the old URL unchanged.
5. The running tray agent detects the change within approximately five seconds, reloads its network client and preserves the enrolment and wellbeing timer state. Any open launcher window closes and can be reopened against the new address.
6. Use **Installer broker setup...** again and approve the displayed HTTPS origin. Re-enrolment replaces the broker's old protected endpoint only after authenticated confirmation.

**Do not delete** `C:\ProgramData\HomeWatch\config.json`. Updating the URL preserves `InstallationId`, `EnrollmentSecret`, `DeviceId`, and `DeviceToken`. An HTTP URL remains available for ordinary HomeWatch functions, but **will not** be accepted for the privileged installer broker.

## Deploy

Push to `main`, check GitHub Actions, then tag **`v0.5.9.2`**. This hotfix can be delivered through the existing 3-EXE signed update ZIP. The separate installed broker executable itself remains on the last administrator-installed version and already supports secure HTTPS enrolment, so **no new installer run is needed** for this UI/connection change as long as the broker service is installed.

No server or PostgreSQL changes. Windows compilation and a real HTTPS-proxy switch still require on-device testing.
