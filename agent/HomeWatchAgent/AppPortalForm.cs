using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HomeWatchAgent;

// Runs in the logged-in user's session. No privileged operations are exposed here.
public sealed class AppPortalForm : Form
{
    private static readonly Color Background = Color.FromArgb(12, 20, 35);
    private static readonly Color Surface = Color.FromArgb(24, 36, 57);
    private static readonly Color TextColor = Color.FromArgb(240, 246, 255);
    private static readonly Color Muted = Color.FromArgb(162, 180, 204);
    private static readonly Color Accent = Color.FromArgb(92, 157, 249);
    private static readonly Color Green = Color.FromArgb(104, 218, 165);
    private static readonly Color Amber = Color.FromArgb(255, 201, 112);

    private readonly HttpClient http;
    private readonly FlowLayoutPanel posterGrid;
    private readonly Panel detailPane;
    private readonly Label footer;
    private readonly System.Windows.Forms.Timer timer;
    private readonly string cacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HomeWatch", "artwork");
    private List<PortalApplication> apps = new();
    private string previousSnapshot = "";
    private int? selectedId;
    private bool loading;

    public AppPortalForm(string serverUrl, string deviceId, string token)
    {
        http = new HttpClient { BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(50) };
        http.DefaultRequestHeaders.Add("X-Device-ID", deviceId);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Directory.CreateDirectory(cacheDirectory);

        Text = "HomeWatch | My Games";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 650);
        Size = new Size(1120, 750);
        BackColor = Background;
        ForeColor = TextColor;
        Font = new Font("Segoe UI", 10);
        Icon = SystemIcons.Shield;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(25, 18, 25, 16), ColumnCount = 1, RowCount = 3, BackColor = Background };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 47));
        heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        heading.Controls.Add(new Label { Text = "HOMEWATCH  /  MY SPACE", ForeColor = Green, Dock = DockStyle.Fill, Font = new Font("Segoe UI Semibold", 9, FontStyle.Bold) }, 0, 0);
        heading.Controls.Add(new Label { Text = "My games & apps", ForeColor = TextColor, Dock = DockStyle.Fill, Font = new Font("Segoe UI Semibold", 25, FontStyle.Bold) }, 0, 1);
        heading.Controls.Add(new Label { Text = "Pick something you love. We'll keep your collection ready.", ForeColor = Muted, Dock = DockStyle.Fill, AutoEllipsis = true }, 0, 2);
        header.Controls.Add(heading, 0, 0);
        var headerActions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(0, 1, 0, 0) };
        var request = ButtonFor("+ Request a new app", Accent, 215, 42);
        request.Click += async (_, _) => await NewRequestAsync();
        var refresh = ButtonFor("Refresh library", Surface, 215, 35);
        refresh.Click += async (_, _) => await LoadAppsAsync(force: true);
        headerActions.Controls.Add(request);
        headerActions.Controls.Add(refresh);
        header.Controls.Add(headerActions, 1, 0);
        root.Controls.Add(header, 0, 0);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 68));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32));
        posterGrid = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight, BackColor = Background, Padding = new Padding(0, 5, 10, 5) };
        detailPane = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Padding = new Padding(16), Margin = new Padding(12, 3, 0, 3) };
        body.Controls.Add(posterGrid, 0, 0);
        body.Controls.Add(detailPane, 1, 0);
        root.Controls.Add(body, 0, 1);
        footer = new Label { Text = "Your library will show up here. ✨", Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = Muted, AutoEllipsis = true };
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);
        ShowDetails(null);

        timer = new System.Windows.Forms.Timer { Interval = 12000 };
        timer.Tick += async (_, _) => await LoadAppsAsync();
        Shown += async (_, _) => { timer.Start(); await LoadAppsAsync(); };
        FormClosed += (_, _) => { timer.Stop(); timer.Dispose(); http.Dispose(); DisposePosterImages(); };
    }

    private static Button ButtonFor(string title, Color color, int width, int height)
    {
        var b = new Button { Text = title, Size = new Size(width, height), BackColor = color,
            ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            Font = new Font("Segoe UI Semibold", 10, FontStyle.Bold), Margin = new Padding(0, 1, 0, 6) };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    private async Task NewRequestAsync()
    {
        using var dialog = new NewAppRequestForm(http);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            footer.Text = "Request sent to your parent! ✨";
            await LoadAppsAsync(force: true);
        }
    }

    private async Task LoadAppsAsync(bool force = false)
    {
        if (loading || IsDisposed) return;
        loading = true;
        try
        {
            using var response = await http.GetAsync("agent/applications");
            await RequireSuccess(response);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var list = document.RootElement.GetProperty("applications").Deserialize<List<PortalApplication>>() ?? new();
            // Avoid tearing down the grid, images or user's selection every heartbeat.
            var snapshot = JsonSerializer.Serialize(list);
            if (IsDisposed || (!force && previousSnapshot == snapshot)) return;
            previousSnapshot = snapshot;
            apps = list.GroupBy(a => a.FilePath, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            DisposePosterImages();
            posterGrid.SuspendLayout();
            try
            {
                while (posterGrid.Controls.Count > 0)
                {
                    var oldControl = posterGrid.Controls[0];
                    posterGrid.Controls.RemoveAt(0);
                    oldControl.Dispose();
                }
                if (apps.Count == 0)
                    posterGrid.Controls.Add(new Label { ForeColor = Muted, AutoSize = true,
                        Text = "No games here yet. Tap '+ Request a new app' to get started!", Padding = new Padding(10, 20, 5, 5) });
                foreach (var app in apps) posterGrid.Controls.Add(CreateTile(app));
            }
            finally { posterGrid.ResumeLayout(); }
            var selected = apps.FirstOrDefault(a => a.Id == selectedId);
            ShowDetails(selected);
            // Painting happens first; artwork downloads and caches asynchronously afterward.
            foreach (var app in apps)
                if (app.ArtworkRevision > 0)
                    _ = PopulatePosterAsync(app);
        }
        catch (Exception e) { if (!IsDisposed) footer.Text = "Unable to refresh your apps: " + e.Message; }
        finally { loading = false; }
    }

    private Control CreateTile(PortalApplication app)
    {
        bool approved = app.Status == "approved";
        var tile = new Panel { Size = new Size(175, 272), BackColor = Surface,
            Margin = new Padding(0, 0, 14, 14), Cursor = Cursors.Hand, Tag = app.Id };
        var poster = new PictureBox { Name = "poster", Size = new Size(175, 205), Location = Point.Empty,
            BackColor = Color.FromArgb(42, 63, 95), SizeMode = PictureBoxSizeMode.Zoom,
            Tag = app.Sha256 + "-" + app.ArtworkRevision };
        poster.Image = MakePlaceholderArt(app);
        tile.Controls.Add(poster);
        var name = new Label { Text = app.DisplayName, Location = new Point(9, 211), Size = new Size(157, 29),
            Font = new Font("Segoe UI Semibold", 10, FontStyle.Bold), AutoEllipsis = true, ForeColor = TextColor };
        tile.Controls.Add(name);
        var status = new Label { Text = approved ? "✓ Ready to play" : app.Status == "pending" ? "◷ Waiting for approval" : "Not available",
            ForeColor = approved ? Green : app.Status == "pending" ? Amber : Muted,
            Location = new Point(9, 241), Size = new Size(157, 23), AutoEllipsis = true,
            Font = new Font("Segoe UI", 9) };
        tile.Controls.Add(status);
        foreach (Control clickable in new Control[] { tile, poster, name, status })
            clickable.Click += (_, _) => { selectedId = app.Id; ShowDetails(app); };
        return tile;
    }

    private static Bitmap MakePlaceholderArt(PortalApplication app)
    {
        var bitmap = new Bitmap(175, 205);
        using var graphics = Graphics.FromImage(bitmap);
        using var background = new LinearGradientBrush(new Rectangle(0, 0, 175, 205),
            Color.FromArgb(55, 82, 129), Color.FromArgb(23, 40, 66), LinearGradientMode.Vertical);
        graphics.FillRectangle(background, 0, 0, 175, 205);
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(app.FilePath);
            if (icon != null) graphics.DrawIcon(icon, new Rectangle(55, 65, 65, 65));
        }
        catch { /* Missing executable / inaccessible icon: use the branded gradient. */ }
        return bitmap;
    }

    private async Task PopulatePosterAsync(PortalApplication app)
    {
        try
        {
            var image = await LoadPosterAsync(app);
            if (IsDisposed) { image?.Dispose(); return; }
            var tile = posterGrid.Controls.OfType<Panel>().FirstOrDefault(x => x.Tag is int id && id == app.Id);
            var poster = tile?.Controls.OfType<PictureBox>().FirstOrDefault(x => x.Name == "poster");
            if (poster == null || poster.IsDisposed || image == null ||
                !string.Equals(poster.Tag?.ToString(), app.Sha256 + "-" + app.ArtworkRevision,
                               StringComparison.Ordinal)) { image?.Dispose(); return; }
            var old = poster.Image; poster.Image = image; old?.Dispose();
        }
        catch { /* Missing artwork should never prevent requesting or opening an app. */ }
    }

    private async Task<Image?> LoadPosterAsync(PortalApplication app)
    {
        if (app.ArtworkRevision < 1 || !System.Text.RegularExpressions.Regex.IsMatch(app.Sha256, "^[0-9a-f]{64}$")) return null;
        var file = Path.Combine(cacheDirectory, app.Sha256 + "-" + app.ArtworkRevision + ".jpg");
        if (!File.Exists(file))
        {
            using var response = await http.GetAsync($"agent/applications/artwork/{app.Sha256}/{app.ArtworkRevision}");
            await RequireSuccess(response);
            var data = await response.Content.ReadAsByteArrayAsync();
            if (data.Length > 3_000_000 || data.Length < 100) throw new IOException("Unexpected artwork size");
            var temp = file + ".tmp";
            await File.WriteAllBytesAsync(temp, data);
            File.Move(temp, file, true);
        }
        // Clone the image so the file is never locked while the launcher runs.
        var bytes = await File.ReadAllBytesAsync(file);
        using var stream = new MemoryStream(bytes);
        using var img = Image.FromStream(stream);
        return new Bitmap(img);
    }

    private void DisposePosterImages()
    {
        foreach (var tile in posterGrid.Controls.OfType<Panel>())
            foreach (var image in tile.Controls.OfType<PictureBox>())
            { image.Image?.Dispose(); image.Image = null; }
    }

    private void ShowDetails(PortalApplication? app)
    {
        detailPane.SuspendLayout();
        try
        {
            while (detailPane.Controls.Count > 0)
            {
                var oldControl = detailPane.Controls[0];
                detailPane.Controls.RemoveAt(0);
                oldControl.Dispose();
            }
            var content = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Surface,
                RowCount = 6, ColumnCount = 1, Padding = new Padding(12, 14, 12, 14) };
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 61));
            content.Controls.Add(new Label { Text = "ABOUT THIS APP", ForeColor = Accent, Dock = DockStyle.Fill,
                Font = new Font("Segoe UI Semibold", 9, FontStyle.Bold) }, 0, 0);
            content.Controls.Add(new Label { Text = app?.DisplayName ?? "Choose a game", ForeColor = TextColor,
                Dock = DockStyle.Fill, AutoEllipsis = true,
                Font = new Font("Segoe UI Semibold", 20, FontStyle.Bold) }, 0, 1);
            var approved = app?.Status == "approved";
            var statusText = app is null ? "Select a tile to see its details" : approved ? "✓ Approved and ready" :
                app.Status == "pending" ? "◷ Waiting for parent approval" : "Not approved";
            content.Controls.Add(new Label { Text = statusText, ForeColor = approved ? Green : Amber, Dock = DockStyle.Fill,
                Font = new Font("Segoe UI Semibold", 10, FontStyle.Bold) }, 0, 2);
            content.Controls.Add(new Label { Text = app is not null && !string.IsNullOrWhiteSpace(app.Publisher) ? $"Publisher: {app.Publisher}" : "Publisher unavailable",
                ForeColor = Muted, AutoEllipsis = true, Dock = DockStyle.Fill }, 0, 3);
            content.Controls.Add(new Label { Text = app is null ? "A nice place to keep your favourite games!" :
                $"File: {app.FileName}\n{app.FilePath}\n\nOnly applications approved by your parent can be opened here.",
                ForeColor = Muted, Dock = DockStyle.Fill, AutoEllipsis = true }, 0, 4);
            var button = ButtonFor(approved ? "▶  PLAY / OPEN" : "Unavailable", approved ? Accent : Color.FromArgb(63, 78, 100), 100, 50);
            button.Dock = DockStyle.Fill; button.Enabled = approved;
            if (app != null) button.Click += async (_, _) => await LaunchAsync(app, button);
            content.Controls.Add(button, 0, 5);
            detailPane.Controls.Add(content);
        }
        finally { detailPane.ResumeLayout(); }
    }

    private async Task LaunchAsync(PortalApplication app, Button button)
    {
        button.Enabled = false;
        try
        {
            var candidate = await ApplicationCandidate.ScanAsync(app.FilePath);
            using var check = await http.PostAsJsonAsync($"agent/applications/{app.Id}/verify", candidate);
            await RequireSuccess(check);
            using var process = Process.Start(new ProcessStartInfo(app.FilePath)
            {
                UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(app.FilePath) ?? ""
            });
            footer.Text = "Have fun! 🎮";
            await ReportLaunchAsync(app.Id, "started", "Started as the current Windows user");
        }
        catch (Exception e)
        {
            footer.Text = "Could not open the app: " + e.Message;
            await ReportLaunchAsync(app.Id, "failed", e.Message);
        }
        finally { if (!IsDisposed) button.Enabled = true; }
    }

    private async Task ReportLaunchAsync(int id, string status, string detail)
    {
        try
        {
            using var r = await http.PostAsJsonAsync("agent/applications/launched", new { request_id = id, status, detail });
            await RequireSuccess(r);
        }
        catch { /* Audit transport failures must not crash the launcher. */ }
    }

    internal static async Task RequireSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var content = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException($"Server returned {(int)response.StatusCode}: {content[..Math.Min(170, content.Length)]}");
    }
}

// Request creation stays in its own child-friendly window, not a permanent form.
public sealed class NewAppRequestForm : Form
{
    private readonly HttpClient http;
    private readonly Label chosen;
    private readonly Label feedback;
    private readonly TextBox reason;
    private readonly Button send;
    private ApplicationCandidate? selected;
    private bool busy;

    public NewAppRequestForm(HttpClient http)
    {
        this.http = http;
        Text = "Ask for an app";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(570, 390);
        MinimumSize = Size;
        BackColor = Color.FromArgb(24, 36, 57);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 11);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), RowCount = 6, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(new Label { Text = "Ask to add something new ✨", Dock = DockStyle.Fill,
            Font = new Font("Segoe UI Semibold", 19, FontStyle.Bold) }, 0, 0);
        var choose = new Button { Text = "Choose the game's .exe file…", Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(66, 101, 158), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        choose.Click += async (_, _) => await ChooseAsync();
        root.Controls.Add(choose, 0, 1);
        chosen = new Label { Text = "Nothing selected yet", ForeColor = Color.FromArgb(171, 192, 217), Dock = DockStyle.Fill, AutoEllipsis = true };
        root.Controls.Add(chosen, 0, 2);
        reason = new TextBox { Dock = DockStyle.Fill, MaxLength = 400, Multiline = true,
            PlaceholderText = "Why would you like this app? (optional)", BackColor = Color.FromArgb(13, 23, 41), ForeColor = Color.White };
        root.Controls.Add(reason, 0, 3);
        send = new Button { Text = "Send request to parent", Dock = DockStyle.Fill, Enabled = false,
            BackColor = Color.FromArgb(52, 145, 124), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        send.Click += async (_, _) => await SubmitAsync();
        root.Controls.Add(send, 0, 4);
        feedback = new Label { Text = "Your parent will review the app before you can open it.",
            ForeColor = Color.FromArgb(171, 192, 217), Dock = DockStyle.Fill };
        root.Controls.Add(feedback, 0, 5);
        Controls.Add(root);
    }

    private async Task ChooseAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "Windows applications (*.exe)|*.exe", Title = "Choose an application", CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        busy = true; send.Enabled = false; feedback.Text = "Checking the app…";
        try
        {
            selected = await ApplicationCandidate.ScanAsync(dialog.FileName);
            chosen.Text = selected.FileName + "  •  " + selected.SignatureStatus + " signature";
            feedback.Text = "Looks good! Ready to ask your parent.";
        }
        catch (Exception ex) { selected = null; feedback.Text = "Could not inspect that app: " + ex.Message; }
        finally { busy = false; send.Enabled = selected != null; }
    }

    private async Task SubmitAsync()
    {
        if (busy || selected == null) return;
        busy = true; send.Enabled = false;
        try
        {
            var candidate = await ApplicationCandidate.ScanAsync(selected.FilePath);
            candidate.Reason = reason.Text.Trim();
            using var response = await http.PostAsJsonAsync("agent/applications/requests", candidate);
            await AppPortalForm.RequireSuccess(response);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception e) { feedback.Text = "Could not submit request: " + e.Message; }
        finally { busy = false; if (!IsDisposed) send.Enabled = selected != null; }
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
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("artwork_revision")] public int ArtworkRevision { get; set; }
    [JsonPropertyName("publisher")] public string Publisher { get; set; } = "";
}
