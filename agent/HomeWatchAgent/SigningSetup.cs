using System.Drawing;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace HomeWatchAgent;

// Local UAC-approved trust decision. Nothing on the server can invoke this operation.
// Never install private key material or accept a signer just because the server says it's trusted.
internal sealed class SigningCertificateForm : Form
{
    private readonly TextBox expected = new() { Dock = DockStyle.Fill, PlaceholderText = "Paste the 64-character SHA-256 fingerprint from the HomeWatch web UI" };
    private readonly TextBox details = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill, Font = new Font("Consolas", 9), BackColor = Color.FromArgb(19, 27, 40), ForeColor = Color.White };
    private readonly Button fetch = new() { Text = "Fetch public certificate", Width = 175, Height = 35 };
    private readonly Button install = new() { Text = "Approve && install", Width = 160, Height = 35, Enabled = false };
    private X509Certificate2? candidate;
    private string? candidateSha;
    private static string PinPath => Path.Combine(AppContext.BaseDirectory, ".updates", "trusted-publisher.sha256");

    internal SigningCertificateForm()
    {
        Text = "HomeWatch — Local signing certificate trust";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(790, 475);
        MinimumSize = new Size(700, 440);
        BackColor = Color.FromArgb(12, 20, 35);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10);
        Icon = AgentContext.LoadBrandIcon();

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(17), ColumnCount = 1, RowCount = 5 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.Controls.Add(new Label { Dock = DockStyle.Fill, Text =
            "ADMINISTRATOR TRUST — THIS PC ONLY\n" +
            "Fetch the public code-signing certificate uploaded by your parent administrator. Compare its SHA-256 " +
            "fingerprint with the web dashboard, then approve it here. This adds a Windows trusted root/publisher " +
            "and pins exactly that certificate for HomeWatch updates. Never approve an unfamiliar fingerprint." }, 0, 0);
        root.Controls.Add(new Label { Text = "Expected SHA-256 fingerprint (copy from Applications → Signing certificate):", Dock = DockStyle.Fill }, 0, 1);
        root.Controls.Add(expected, 0, 2);
        details.Text = "No changes have been made.\r\n" +
            "Current update publisher pin: " + (File.Exists(PinPath) ? File.ReadAllText(PinPath).Trim() : "NOT CONFIGURED");
        root.Controls.Add(details, 0, 3);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        fetch.Click += async (_, _) => await FetchAsync();
        install.Click += (_, _) => Install();
        buttons.Controls.Add(fetch);
        buttons.Controls.Add(install);
        var close = new Button { Text = "Close", Width = 95, Height = 35 };
        close.Click += (_, _) => Close();
        buttons.Controls.Add(close);
        root.Controls.Add(buttons, 0, 4);
        Controls.Add(root);
        FormClosed += (_, _) => { candidate?.Dispose(); };
    }

    private async Task FetchAsync()
    {
        candidate?.Dispose(); candidate = null; candidateSha = null;
        install.Enabled = false;
        fetch.Enabled = false;
        details.Text = "Downloading the public certificate…";
        try
        {
            var configFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HomeWatch", "config.json");
            if (!File.Exists(configFile)) throw new IOException("HomeWatch device configuration not found. Enrol this PC first.");
            var config = JsonSerializer.Deserialize<AgentConfig>(await File.ReadAllTextAsync(configFile))
                ?? throw new InvalidDataException("Unable to read device configuration");
            if (string.IsNullOrWhiteSpace(config.ServerUrl) || string.IsNullOrWhiteSpace(config.DeviceId) ||
                string.IsNullOrWhiteSpace(config.DeviceToken))
                throw new InvalidOperationException("This PC has not completed HomeWatch enrolment.");
            var server = new Uri(config.ServerUrl.TrimEnd('/') + "/");
            if (server.Scheme is not ("https" or "http")) throw new InvalidOperationException("Only HTTP(S) HomeWatch servers are supported.");
            using var client = new HttpClient { BaseAddress = server, Timeout = TimeSpan.FromSeconds(25) };
            client.DefaultRequestHeaders.Add("X-Device-ID", config.DeviceId);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.DeviceToken);
            using var response = await client.GetAsync("agent/signing-certificate");
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var bytes = Convert.FromBase64String(root.GetProperty("certificate_base64").GetString() ?? "");
            if (bytes.Length > 16384) throw new InvalidDataException("Certificate exceeds 16 KiB");
            var cert = new X509Certificate2(bytes);
            var sha = Convert.ToHexString(SHA256.HashData(cert.RawData));
            var claimed = root.GetProperty("sha256").GetString();
            if (!sha.Equals(claimed, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Server fingerprint does not match certificate bytes");
            ValidatePublicSigningCertificate(cert);
            candidate = cert; candidateSha = sha;
            details.Text = "Publisher: " + cert.Subject + "\r\n" +
                "SHA-256: " + sha + "\r\n" +
                "Not before: " + cert.NotBefore + "\r\nExpires: " + cert.NotAfter + "\r\n" +
                (server.Scheme == "http" ? "\r\nNOTICE: The server connection is HTTP. Independently compare the SHA-256 fingerprint.\r\n" : "") +
                "\r\nCopy the matching fingerprint from the parent web UI into the field above before installation.";
            install.Enabled = true;
        }
        catch (Exception ex) { details.Text = "Cannot fetch certificate: " + ex.Message; }
        finally { fetch.Enabled = true; }
    }

    private static void ValidatePublicSigningCertificate(X509Certificate2 cert)
    {
        if (cert.HasPrivateKey) throw new InvalidDataException("Never distribute or import a private signing key");
        if (!cert.SubjectName.RawData.AsSpan().SequenceEqual(cert.IssuerName.RawData))
            throw new InvalidDataException("This household setup expects a self-signed code-signing certificate");
        if (DateTime.Now < cert.NotBefore || DateTime.Now >= cert.NotAfter)
            throw new InvalidDataException("Certificate is expired or not yet valid");
        var usage = cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        if (usage is null || !usage.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == "1.3.6.1.5.5.7.3.3"))
            throw new InvalidDataException("Certificate is not authorised for code signing");
        if (cert.Extensions.OfType<X509BasicConstraintsExtension>().Any(x => x.CertificateAuthority))
            throw new InvalidDataException("CA certificates are not accepted");
    }

    private void Install()
    {
        if (candidate is null || candidateSha is null) return;
        var typed = expected.Text.Trim().Replace(" ", "").Replace(":", "").ToUpperInvariant();
        if (typed.Length != 64 || !typed.All(Uri.IsHexDigit) || typed != candidateSha)
        {
            MessageBox.Show("The fingerprint does NOT match. Copy the exact SHA-256 value shown in the parent web UI.",
                "HomeWatch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var current = File.Exists(PinPath) ? File.ReadAllText(PinPath).Trim() : "";
        if (current.Length > 0 && !current.Equals(candidateSha, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("A different signing certificate is already pinned. Certificate rotation requires a deliberate manual recovery; HomeWatch will not silently replace it.",
                "HomeWatch", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (MessageBox.Show("Trust publisher:\n" + candidate.Subject + "\n\nSHA-256:\n" + candidateSha +
            "\n\nThis establishes a Windows machine-wide trusted root and code-signing publisher, and restricts future HomeWatch updates to THIS certificate. Proceed?",
            "Confirm machine trust", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try
        {
            ValidatePublicSigningCertificate(candidate);
            using (var roots = new X509Store(StoreName.Root, StoreLocation.LocalMachine))
            {
                roots.Open(OpenFlags.ReadWrite);
                roots.Add(candidate);
            }
            using (var publishers = new X509Store(StoreName.TrustedPublisher, StoreLocation.LocalMachine))
            {
                publishers.Open(OpenFlags.ReadWrite);
                publishers.Add(candidate);
            }
            // Program Files is administrator-protected. Never use editable ProgramData config to pin update trust.
            Directory.CreateDirectory(Path.GetDirectoryName(PinPath)!);
            var temp = PinPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, candidateSha + Environment.NewLine);
                File.Move(temp, PinPath, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            details.Text += "\r\n\r\nSUCCESS: Certificate trusted in LocalMachine Root and TrustedPublisher, updater signer pinned.";
            install.Enabled = false;
        }
        catch (Exception ex) { MessageBox.Show("Certificate trust setup failed: " + ex.Message,
            "HomeWatch", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
