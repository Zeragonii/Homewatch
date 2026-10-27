using System.Drawing;
using System.Net.Http.Headers;
using System.Text.Json;

namespace HomeWatchAgent;

// Only opened inside the administrator-approved performance settings process.
// Changes the EXISTING enrolled device's server origin; never resets its identity.
internal sealed class ServerConnectionForm : Form
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HomeWatch", "config.json");
    private readonly TextBox address = new() { Dock = DockStyle.Fill };
    private readonly TextBox feedback = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical,
        BackColor = Color.FromArgb(22, 33, 51), ForeColor = Color.White,
        Font = new Font("Segoe UI", 10)
    };
    private readonly Button testAndSave = new() { Text = "Test and save", Width = 155, Height = 36 };

    internal ServerConnectionForm()
    {
        Text = "HomeWatch — Server connection";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(600, 365);
        Size = new Size(690, 415);
        BackColor = Color.FromArgb(12, 20, 35);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10);
        Icon = AgentContext.LoadBrandIcon();
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text =
            "Change the HomeWatch server address without resetting this PC's device identity or approvals. " +
            "Use your HTTPS reverse-proxy address for broker provisioning. " +
            "The FastAPI/Portainer endpoint itself serves HTTP. The broker will NOT accept an unencrypted HTTP URL." }, 0, 0);
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Server URL (e.g. https://homewatch.yourdomain.example):" }, 0, 1);
        layout.Controls.Add(address, 0, 2);
        feedback.Text = "Enter the new address and select Test and save. HomeWatch will check that the existing device token works at that URL BEFORE changing anything.\r\n\r\nIf switching from HTTP to HTTPS, configure TLS on a reverse proxy first; changing the prefix alone cannot enable HTTPS.";
        layout.Controls.Add(feedback, 0, 3);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        testAndSave.Click += async (_, _) => await SaveAsync();
        buttons.Controls.Add(testAndSave);
        var close = new Button { Text = "Close", Width = 95, Height = 36 };
        close.Click += (_, _) => Close();
        buttons.Controls.Add(close);
        layout.Controls.Add(buttons, 0, 4);
        Controls.Add(layout);
        try { address.Text = LoadConfig().ServerUrl; }
        catch (Exception ex) { feedback.Text = "Unable to read existing device configuration: " + ex.Message; testAndSave.Enabled = false; }
    }

    private static AgentConfig LoadConfig() =>
        JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(ConfigPath))
        ?? throw new InvalidDataException("HomeWatch configuration is invalid");

    private async Task SaveAsync()
    {
        var newUrl = address.Text.Trim().TrimEnd('/');
        if (!Uri.TryCreate(newUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            feedback.Text = "Enter a valid HTTP or HTTPS server origin without credentials, query or fragment.";
            return;
        }
        // Do not silently fall back from HTTPS to HTTP. This is an explicit local-admin choice.
        if (uri.Scheme == "http" && MessageBox.Show(this,
            "This address uses unencrypted HTTP. Ordinary HomeWatch monitoring may work, but installer-broker provisioning REQUIRES HTTPS. Continue using HTTP for the ordinary agent?",
            "Unencrypted connection", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        testAndSave.Enabled = false;
        feedback.Text = "Testing the existing enrolled device against " + newUrl + " …";
        try
        {
            var original = LoadConfig();
            if (string.IsNullOrWhiteSpace(original.DeviceId) || string.IsNullOrWhiteSpace(original.DeviceToken))
                throw new InvalidOperationException("Enrol this PC in HomeWatch before changing the connection.");
            using var http = new HttpClient { BaseAddress = new Uri(newUrl + "/"), Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.Add("X-Device-ID", original.DeviceId);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", original.DeviceToken);
            using var response = await http.GetAsync("agent/catalogue", HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
                throw new IOException("Device authentication failed (HTTP " + (int)response.StatusCode +
                    "). Confirm that the reverse proxy reaches the SAME HomeWatch server and does not require an additional login.");
            // Re-read after the network request: never overwrite changed credentials with a stale snapshot.
            var current = LoadConfig();
            if (current.InstallationId != original.InstallationId || current.DeviceId != original.DeviceId ||
                current.DeviceToken != original.DeviceToken || current.EnrollmentSecret != original.EnrollmentSecret)
                throw new IOException("The enrolment changed during testing. Retry to avoid overwriting newer credentials.");
            if (string.Equals(current.ServerUrl.TrimEnd('/'), newUrl, StringComparison.OrdinalIgnoreCase))
            {
                feedback.Text = "Connection verified. This address is already configured.";
                return;
            }
            if (MessageBox.Show(this, "The HomeWatch device is authorised at:\n" + newUrl +
                "\n\nKeep device identity, saved approval data and enrolment, and change ONLY its server URL?",
                "Confirm HomeWatch server address", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                feedback.Text = "No changes made.";
                return;
            }
            current.ServerUrl = newUrl;
            // Save via a same-directory temporary file and replacement, retaining the original
            // enrolment fields and preserving the existing file ACL on Windows.
            string temp = ConfigPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(current, new JsonSerializerOptions { WriteIndented = true }));
                File.Replace(temp, ConfigPath, null);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            feedback.Text = "Saved and verified: " + newUrl +
                "\r\n\r\nExisting device identity preserved. The HomeWatch tray agent will pick up the connection change shortly. " +
                (uri.Scheme == "https" ?
                    "You can now return to Installer broker setup and approve the HTTPS origin." :
                    "Installer broker setup is unavailable until you configure HTTPS.");
        }
        catch (Exception ex) { feedback.Text = "Server address was NOT changed: " + ex.Message; }
        finally { testAndSave.Enabled = true; }
    }
}
