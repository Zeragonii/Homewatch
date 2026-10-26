using System.Diagnostics;
using System.Drawing;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HomeWatchAgent;

// Runs in the same interactive, unprivileged process as the tray agent.
// It never requests execution from the SYSTEM updater service.
public sealed class AppPortalForm : Form
{
    private static readonly Color BackgroundColor = Color.FromArgb(13, 23, 41);
    private static readonly Color SurfaceColor = Color.FromArgb(27, 41, 66);
    private static readonly Color ForegroundColor = Color.FromArgb(237, 244, 255);
    private static readonly Color MutedColor = Color.FromArgb(163, 185, 212);
    private static readonly Color MintColor = Color.FromArgb(97, 213, 164);
    private static readonly Color BlueColor = Color.FromArgb(84, 137, 245);
    private static readonly Color GoldColor = Color.FromArgb(249, 190, 93);

    private readonly HttpClient http;
    private readonly string deviceId;
    private readonly string token;
    private readonly FlowLayoutPanel appList;
    private readonly Label feedback;
    private readonly Label chosenFile;
    private readonly TextBox reason;
    private readonly Button requestButton;
    private readonly System.Windows.Forms.Timer refreshTimer;
    private ApplicationCandidate? selected;
    private bool loading;
    private bool scanning;

    public AppPortalForm(string serverUrl, string deviceId, string token)
    {
        this.deviceId = deviceId;
        this.token = token;
        http = new HttpClient { BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.Add("X-Device-ID", this.deviceId);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", this.token);

        Text = "HomeWatch • My apps";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(650, 600);
        Size = new Size(750, 730);
        BackColor = BackgroundColor;
        ForeColor = ForegroundColor;
        Font = new Font("Segoe UI", 10);
        Icon = SystemIcons.Shield;

        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(22), BackColor = BackgroundColor };
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 184));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new Panel { Dock = DockStyle.Fill };
        header.Controls.Add(new Label { Text = "MY APP SPACE", ForeColor = MintColor, Font = new Font("Segoe UI Semibold", 9, FontStyle.Bold), Location = new Point(2, 2), AutoSize = true });
        header.Controls.Add(new Label { Text = "Hey there! 👋", ForeColor = ForegroundColor, Font = new Font("Segoe UI Semibold", 22, FontStyle.Bold), Location = new Point(0, 26), AutoSize = true });
        header.Controls.Add(new Label { Text = "Ask to play something new, or open your approved apps.", ForeColor = MutedColor, Location = new Point(2, 73), AutoSize = true });
        shell.Controls.Add(header, 0, 0);

        var requestPanel = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = SurfaceColor, Padding = new Padding(17, 14, 17, 13), RowCount = 4, ColumnCount = 2 };
        requestPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        requestPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        requestPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        requestPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
        requestPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        requestPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        requestPanel.Controls.Add(new Label { Text = "REQUEST A NEW APP", ForeColor = ForegroundColor, Dock = DockStyle.Fill, Font = new Font("Segoe UI Semibold", 10, FontStyle.Bold) }, 0, 0);
        chosenFile = new Label { Text = "No app selected yet", ForeColor = MutedColor, AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        requestPanel.Controls.Add(chosenFile, 0, 1);
        var chooseButton = MakeButton("Choose app…", BlueColor);
        chooseButton.Click += async (_, _) => await ChooseAsync();
        requestPanel.Controls.Add(chooseButton, 1, 1);
        reason = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "Why would you like this app? (optional)", BackColor = BackgroundColor, ForeColor = ForegroundColor, BorderStyle = BorderStyle.FixedSingle, MaxLength = 400, Margin = new Padding(0, 6, 8, 6) };
        requestPanel.Controls.Add(reason, 0, 2);
        requestButton = MakeButton("Ask parent", MintColor);
        requestButton.ForeColor = BackgroundColor;
        requestButton.Enabled = false;
        requestButton.Click += async (_, _) => await RequestAsync();
        requestPanel.Controls.Add(requestButton, 1, 2);
        feedback = new Label { Text = "Pick the .exe file for the app you want.", ForeColor = MutedColor, Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        requestPanel.Controls.Add(feedback, 0, 3);
        requestPanel.SetColumnSpan(feedback, 2);
        shell.Controls.Add(requestPanel, 0, 1);

        var listPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(0, 16, 0, 0) };
        listPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        listPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var listHeading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        listHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        listHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
        listHeading.Controls.Add(new Label { Text = "Your applications", Font = new Font("Segoe UI Semibold", 15, FontStyle.Bold), ForeColor = ForegroundColor, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        var refreshButton = MakeButton("Refresh", SurfaceColor);
        refreshButton.Click += async (_, _) => await LoadAppsAsync();
        listHeading.Controls.Add(refreshButton, 1, 0);
        listPanel.Controls.Add(listHeading, 0, 0);
        appList = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false, FlowDirection = FlowDirection.TopDown, Padding = new Padding(0, 5, 0, 0) };
        appList.SizeChanged += (_, _) => ResizeCards();
        listPanel.Controls.Add(appList, 0, 1);
        shell.Controls.Add(listPanel, 0, 2);
        Controls.Add(shell);

        refreshTimer = new System.Windows.Forms.Timer { Interval = 12000 };
        refreshTimer.Tick += async (_, _) => await LoadAppsAsync();
        Shown += async (_, _) => { refreshTimer.Start(); await LoadAppsAsync(); };
        FormClosed += (_, _) => { refreshTimer.Stop(); refreshTimer.Dispose(); http.Dispose(); };
    }

    private static Button MakeButton(string text, Color background) => new()
    {
        Text = text, BackColor = background, ForeColor = Color.White,
        FlatStyle = FlatStyle.Flat, Dock = DockStyle.Fill, Cursor = Cursors.Hand,
        Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold), Margin = new Padding(6, 2, 0, 2)
    };

    private void ResizeCards()
    {
        foreach (Control c in appList.Controls) c.Width = Math.Max(250, appList.ClientSize.Width - 26);
    }

    private async Task ChooseAsync()
    {
        using var fileDialog = new OpenFileDialog { Title = "Choose an application", Filter = "Windows apps (*.exe)|*.exe", CheckFileExists = true, Multiselect = false };
        if (fileDialog.ShowDialog(this) != DialogResult.OK) return;
        scanning = true;
        requestButton.Enabled = false;
        feedback.Text = "Checking the app's fingerprint and publisher…";
        try
        {
            selected = await ApplicationCandidate.ScanAsync(fileDialog.FileName);
            chosenFile.Text = $"{selected.FileName}  •  {selected.SignatureStatus} signature";
            feedback.Text = selected.SignatureStatus == "valid"
                ? $"Signed by {selected.Publisher}. Ready to request."
                : "Publisher signature could not be verified; your parent can review the request.";
        }
        catch (Exception ex)
        {
            selected = null;
            chosenFile.Text = "Please choose another application";
            feedback.Text = "Couldn't inspect that file: " + ex.Message;
        }
        finally { scanning = false; requestButton.Enabled = selected is not null; }
    }

    private async Task RequestAsync()
    {
        if (selected is null || scanning) return;
        requestButton.Enabled = false;
        feedback.Text = "Sending your request…";
        try
        {
            // Rescan immediately before sending to avoid a stale fingerprint.
            var candidate = await ApplicationCandidate.ScanAsync(selected.FilePath);
            candidate.Reason = reason.Text.Trim();
            using var response = await http.PostAsJsonAsync("agent/applications/requests", candidate);
            await RequireSuccess(response);
            var app = await response.Content.ReadFromJsonAsync<PortalApplication>();
            feedback.Text = app?.Status == "approved" ? "Good news! This app is already approved. 🎉" : "Request sent! Your parent can review it now. ✨";
            selected = null; reason.Clear(); chosenFile.Text = "No app selected yet";
            await LoadAppsAsync();
        }
        catch (Exception ex) { feedback.Text = "Couldn't send request: " + ex.Message; }
        finally { requestButton.Enabled = selected is not null; }
    }

    private async Task LoadAppsAsync()
    {
        if (loading || IsDisposed) return;
        loading = true;
        try
        {
            using var response = await http.GetAsync("agent/applications");
            await RequireSuccess(response);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var apps = doc.RootElement.GetProperty("applications").Deserialize<List<PortalApplication>>() ?? new();
            if (IsDisposed) return;
            appList.SuspendLayout();
            try
            {
                appList.Controls.Clear();
                if (apps.Count == 0)
                {
                    appList.Controls.Add(new Label { Text = "Nothing here yet. Choose an app above and ask your parent!", ForeColor = MutedColor, AutoSize = true, Padding = new Padding(10, 18, 0, 0) });
                }
                foreach (var app in apps) appList.Controls.Add(CreateCard(app));
                ResizeCards();
            }
            finally { appList.ResumeLayout(); }
        }
        catch (Exception ex) { if (!IsDisposed) feedback.Text = "Couldn't refresh apps: " + ex.Message; }
        finally { loading = false; }
    }

    private Panel CreateCard(PortalApplication app)
    {
        var approved = app.Status == "approved";
        var card = new Panel { Height = 88, BackColor = SurfaceColor, Margin = new Padding(0, 0, 0, 9) };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = SurfaceColor,
            Padding = new Padding(14, 10, 14, 10), ColumnCount = 2, RowCount = 2
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 143));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 53));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 47));
        var name = new Label { Text = app.FileName, Font = new Font("Segoe UI Semibold", 11, FontStyle.Bold), ForeColor = ForegroundColor, AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty };
        var statusLabel = approved ? "✓ Approved" : app.Status switch { "pending" => "⏳ Waiting for approval", "denied" => "Not approved", _ => "Approval removed" };
        var statusColor = approved ? MintColor : app.Status == "pending" ? GoldColor : MutedColor;
        var status = new Label { Text = statusLabel + (approved && app.ApprovalScope != "" ? $"  •  {app.ApprovalScope}" : ""), ForeColor = statusColor, AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty };
        var launch = MakeButton(approved ? "Play / Open" : app.Status switch
        {
            "pending" => "Waiting…", "denied" => "Not approved", _ => "Unavailable"
        }, approved ? BlueColor : Color.FromArgb(60, 77, 100));
        launch.Enabled = approved;
        launch.Margin = new Padding(8, 5, 0, 5);
        launch.Click += async (_, _) => await LaunchAsync(app, launch);
        layout.Controls.Add(name, 0, 0);
        layout.Controls.Add(status, 0, 1);
        layout.Controls.Add(launch, 1, 0);
        layout.SetRowSpan(launch, 2);
        card.Controls.Add(layout);
        return card;
    }

    private async Task LaunchAsync(PortalApplication app, Button button)
    {
        button.Enabled = false;
        try
        {
            var candidate = await ApplicationCandidate.ScanAsync(app.FilePath);
            using var check = await http.PostAsJsonAsync($"agent/applications/{app.Id}/verify", candidate);
            await RequireSuccess(check);
            // Normal logged-in user launch. No UAC bypass or SYSTEM privileges.
            using var process = Process.Start(new ProcessStartInfo(app.FilePath)
            {
                UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(app.FilePath) ?? ""
            });
            feedback.Text = "Enjoy! 🎮";
            await ReportLaunchAsync(app.Id, "started", "Started as the current Windows user");
        }
        catch (Exception ex)
        {
            feedback.Text = "Can't open that app: " + ex.Message;
            await ReportLaunchAsync(app.Id, "failed", ex.Message);
        }
        finally { if (!IsDisposed) button.Enabled = true; }
    }

    private async Task ReportLaunchAsync(int id, string status, string detail)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("agent/applications/launched", new { request_id = id, status, detail });
            await RequireSuccess(response);
        }
        catch { /* A launch must not crash the client when audit delivery is unavailable. */ }
    }

    private static async Task RequireSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Server returned {(int)response.StatusCode}: {body[..Math.Min(body.Length, 180)]}");
        }
    }
}

public sealed class ApplicationCandidate
{
    [JsonPropertyName("file_name")] public string FileName { get; set; } = "";
    [JsonPropertyName("file_path")] public string FilePath { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("file_size")] public long FileSize { get; set; }
    [JsonPropertyName("signature_status")] public string SignatureStatus { get; set; } = "unknown";
    [JsonPropertyName("publisher")] public string Publisher { get; set; } = "";
    [JsonPropertyName("signer_thumbprint")] public string SignerThumbprint { get; set; } = "";
    [JsonPropertyName("signer_subject")] public string SignerSubject { get; set; } = "";
    [JsonPropertyName("signer_issuer")] public string SignerIssuer { get; set; } = "";
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";

    public static async Task<ApplicationCandidate> ScanAsync(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        if (!fullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            throw new InvalidOperationException("Choose an existing .exe file.");
        if (fullPath.StartsWith(@"\\"))
            throw new InvalidOperationException("Network paths aren't supported in this version.");
        var info = new FileInfo(fullPath);
        string hash;
        await using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            hash = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
        var result = new ApplicationCandidate { FileName = info.Name, FilePath = fullPath, FileSize = info.Length, Sha256 = hash };
        await result.ReadSignatureAsync();
        return result;
    }

    private async Task ReadSignatureAsync()
    {
        // A fixed, read-only PowerShell script. The untrusted file path travels via
        // an environment variable, NOT string interpolation in a PowerShell command.
        const string script = @"
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$s = Get-AuthenticodeSignature -LiteralPath $env:HW_SIGNATURE_FILE
$c = $s.SignerCertificate
$publisher = ''
$thumbprint = ''
$subject = ''
$issuer = ''
if ($null -ne $c) {
  $publisher = $c.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
  $thumbprint = $c.Thumbprint
  $subject = $c.Subject
  $issuer = $c.Issuer
}
[pscustomobject]@{ status = [string]$s.Status; publisher = $publisher; thumbprint = $thumbprint; subject = $subject; issuer = $issuer } | ConvertTo-Json -Compress
";
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        psi.Environment["HW_SIGNATURE_FILE"] = FilePath;
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);
        using var process = Process.Start(psi) ?? throw new IOException("Unable to inspect the application signature");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(timeout.Token);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0) throw new IOException("Signature inspection failed: " + error[..Math.Min(error.Length, 160)]);
        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        var raw = root.GetProperty("status").GetString() ?? "Unknown";
        SignatureStatus = raw == "Valid" ? "valid" : raw == "NotSigned" ? "unsigned" : "invalid";
        Publisher = root.GetProperty("publisher").GetString() ?? "";
        SignerThumbprint = (root.GetProperty("thumbprint").GetString() ?? "").ToLowerInvariant();
        SignerSubject = root.GetProperty("subject").GetString() ?? "";
        SignerIssuer = root.GetProperty("issuer").GetString() ?? "";
    }
}

public sealed class PortalApplication
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("file_name")] public string FileName { get; set; } = "";
    [JsonPropertyName("file_path")] public string FilePath { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("approval_scope")] public string ApprovalScope { get; set; } = "";
}
