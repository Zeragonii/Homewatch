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
    private bool installing;

    public AppPortalForm(string serverUrl, string deviceId, string token)
    {
        http = new HttpClient { BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(50) };
        http.DefaultRequestHeaders.Add("X-Device-ID", deviceId);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Directory.CreateDirectory(cacheDirectory);

        Text = "HomeWatch Launcher";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 650);
        Size = new Size(1120, 750);
        BackColor = Background;
        ForeColor = TextColor;
        Font = new Font("Segoe UI", 10);
        Icon = AgentContext.LoadBrandIcon();

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
        heading.Controls.Add(new Label { Text = "HOMEWATCH  /  LAUNCHER", ForeColor = Green, Dock = DockStyle.Fill, Font = new Font("Segoe UI Semibold", 9, FontStyle.Bold) }, 0, 0);
        heading.Controls.Add(new Label { Text = "HomeWatch Launcher", ForeColor = TextColor, Dock = DockStyle.Fill, Font = new Font("Segoe UI Semibold", 25, FontStyle.Bold) }, 0, 1);
        heading.Controls.Add(new Label { Text = "Pick something you love. Request new games and apps whenever you need them.", ForeColor = Muted, Dock = DockStyle.Fill, AutoEllipsis = true }, 0, 2);
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
        if (loading || installing || IsDisposed) return;
        loading = true;
        try
        {
            using var response = await http.GetAsync("agent/applications");
            await RequireSuccess(response);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var list = document.RootElement.GetProperty("applications").Deserialize<List<PortalApplication>>() ?? new();
            // Shared household apps are published by the parent. They are installers,
            // deliberately distinct from installed/executable approvals.
            using (var catalogueResponse = await http.GetAsync("agent/catalogue"))
            {
                if (catalogueResponse.IsSuccessStatusCode)
                {
                    using var catalogue = JsonDocument.Parse(await catalogueResponse.Content.ReadAsStringAsync());
                    var items = catalogue.RootElement.GetProperty("items").Deserialize<List<PortalCatalogueItem>>() ?? new();
                    foreach (var item in items)
                    {
                        list.Add(new PortalApplication
                        {
                            Id = -item.Id, CatalogueId = item.Id, IsCatalogue = true,
                            FileName = Path.GetFileName(item.InstallerPath),
                            FilePath = item.InstallerPath,
                            Sha256 = item.InstallerSha256,
                            DisplayName = item.Name, Description = item.Description,
                            ArtworkRevision = item.ArtworkRevision,
                            Publisher = "HomeWatch family library", Status = "available",
                            PackageStatus = item.Installation?.Status ?? "not_installed",
                            InstalledPath = item.Installation?.ExecutablePath ?? "",
                            InstalledRequestId = item.Installation?.RequestId ?? 0,
                            InstalledApproved = item.Installation?.Approved ?? false,
                            PackageSize = item.TotalBytes
                        });
                    }
                }
            }
            // Avoid tearing down the grid, images or user's selection every heartbeat.
            var snapshot = JsonSerializer.Serialize(list);
            if (IsDisposed || (!force && previousSnapshot == snapshot)) return;
            previousSnapshot = snapshot;
            var installedGamePaths = new HashSet<string>(list.Where(a => a.IsCatalogue && !string.IsNullOrEmpty(a.InstalledPath))
                .Select(a => a.InstalledPath), StringComparer.OrdinalIgnoreCase);
            // The game already appears as a household tile; don't duplicate its approval tile.
            apps = list.Where(a => a.IsCatalogue || !installedGamePaths.Contains(a.FilePath))
                .GroupBy(a => a.FilePath, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
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
        var status = new Label { Text = app.IsCatalogue ? (app.InstalledApproved ? "✓ Ready to play" : app.InstalledRequestId > 0 ? "◷ Awaiting approval" : app.PackageStatus == "installing" ? "Installing" : "↓ Available to install") : approved ? "✓ Ready to play" : app.Status == "pending" ? "◷ Waiting for approval" : "Not available",
            ForeColor = approved || app.InstalledApproved ? Green : app.IsCatalogue ? Accent : app.Status == "pending" ? Amber : Muted,
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
            if (!app.IsCatalogue)
            {
                using var icon = Icon.ExtractAssociatedIcon(app.FilePath);
                if (icon != null) graphics.DrawIcon(icon, new Rectangle(55, 65, 65, 65));
            }
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
            var approved = app?.Status == "approved" || app?.InstalledApproved == true;
            var statusText = app is null ? "Select a tile to see its details" : app.IsCatalogue ? (app.InstalledApproved ? "✓ Installed — ready to play" : app.InstalledRequestId > 0 ? "◷ Installed — waiting for parent approval" : "↓ In the family library") : approved ? "✓ Approved and ready" :
                app.Status == "pending" ? "◷ Waiting for parent approval" : "Not approved";
            content.Controls.Add(new Label { Text = statusText, ForeColor = approved ? Green : Amber, Dock = DockStyle.Fill,
                Font = new Font("Segoe UI Semibold", 10, FontStyle.Bold) }, 0, 2);
            content.Controls.Add(new Label { Text = app is not null && !string.IsNullOrWhiteSpace(app.Publisher) ? $"Publisher: {app.Publisher}" : "Publisher unavailable",
                ForeColor = Muted, AutoEllipsis = true, Dock = DockStyle.Fill }, 0, 3);
            content.Controls.Add(new Label { Text = app is null ? "A nice place to keep your favourite games!" :
                app.IsCatalogue ? $"{app.Description}\n\nInstaller: {app.FileName}\nSize: {app.PackageSize / (1024.0 * 1024 * 1024):F2} GB\n" +
                (string.IsNullOrEmpty(app.InstalledPath) ? "Available to everyone in your family." : $"Installed here: {app.InstalledPath}") :
                $"File: {app.FileName}\n{app.FilePath}\n\nOnly applications approved by your parent can be opened here.",
                ForeColor = Muted, Dock = DockStyle.Fill, AutoEllipsis = true }, 0, 4);
            var canUse = approved || app?.IsCatalogue == true;
            var button = ButtonFor(app?.IsCatalogue == true ? app.InstalledApproved ? "▶  PLAY" : app.InstalledRequestId > 0 ? "WAITING FOR PARENT APPROVAL" : app.PackageStatus == "detecting" ? "LOCATE INSTALLED GAME" : "↓  INSTALL FROM FAMILY LIBRARY" : approved ? "▶  PLAY / OPEN" : "Unavailable",
                canUse ? Accent : Color.FromArgb(63, 78, 100), 100, 50);
            button.Dock = DockStyle.Fill; button.Enabled = canUse && !(app?.IsCatalogue == true && app.InstalledRequestId > 0 && !app.InstalledApproved);
            if (app != null)
                button.Click += async (_, _) => { if (app.IsCatalogue && app.InstalledApproved) await LaunchInstalledAsync(app, button);
                    else if (app.IsCatalogue && app.PackageStatus == "detecting") await ChooseInstalledAsync(app, button);
                    else if (app.IsCatalogue) await InstallCatalogueAsync(app, button); else await LaunchAsync(app, button); };
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

    private async Task InstallationStateAsync(int id, string status, string installed = "", string executable = "",
        string digest = "", int? requestId = null)
    {
        using var response = await http.PutAsJsonAsync($"agent/catalogue/{id}/installation",
            new { status, installed_path = installed, executable_path = executable,
                executable_sha256 = digest, request_id = requestId });
        await RequireSuccess(response);
    }

    private async Task LaunchInstalledAsync(PortalApplication app, Button button)
    {
        button.Enabled = false;
        try
        {
            var candidate = await ApplicationCandidate.ScanAsync(app.InstalledPath);
            using var response = await http.PostAsJsonAsync($"agent/applications/{app.InstalledRequestId}/verify", candidate);
            await RequireSuccess(response);
            using var process = Process.Start(new ProcessStartInfo(app.InstalledPath)
                { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(app.InstalledPath) ?? "" });
            if (process is null) throw new IOException("Unable to start the installed game");
            await ReportLaunchAsync(app.InstalledRequestId, "started", "Opened from HomeWatch family library");
            footer.Text = "Have fun! 🎮";
        }
        catch (Exception e)
        {
            footer.Text = "Game launch was blocked: " + e.Message;
            await ReportLaunchAsync(app.InstalledRequestId, "failed", e.Message);
        }
        finally { if (!IsDisposed) button.Enabled = true; }
    }

    private async Task ChooseInstalledAsync(PortalApplication app, Button button)
    {
        using var picker = new OpenFileDialog { Title = "Locate the installed game's executable", Filter = "Applications (*.exe)|*.exe", CheckFileExists = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        button.Enabled = false;
        try
        {
            using var source = await http.GetAsync($"agent/catalogue/{app.CatalogueId}");
            await RequireSuccess(source);
            var current = await source.Content.ReadFromJsonAsync<PortalCatalogueItem>();
            if (current is null || !current.Enabled) throw new IOException("This game is no longer published");
            await RegisterInstalledGameAsync(current, picker.FileName);
            footer.Text = "Game found — waiting for your parent's launch approval.";
        }
        catch (Exception e) { footer.Text = "Could not register the game: " + e.Message; }
        finally { if (!IsDisposed) { button.Enabled = true; await LoadAppsAsync(force: true); } }
    }

    private async Task RegisterInstalledGameAsync(PortalCatalogueItem item, string selected)
    {
        // An existing game EXE must not point into HomeWatch's own installer cache.
        var stageRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HomeWatch", "Packages");
        if (Path.GetFullPath(selected).StartsWith(Path.GetFullPath(stageRoot) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase))
            throw new IOException("Choose the game executable, not a cached setup file");
        var candidate = await ApplicationCandidate.ScanAsync(selected);
        candidate.Reason = "Installed from household library: " + item.Name;
        using var requested = await http.PostAsJsonAsync("agent/applications/requests", candidate);
        await RequireSuccess(requested);
        using var doc = JsonDocument.Parse(await requested.Content.ReadAsStringAsync());
        var requestId = doc.RootElement.GetProperty("id").GetInt32();
        await InstallationStateAsync(item.Id, "awaiting_approval", Path.GetDirectoryName(selected) ?? "",
            selected, candidate.Sha256, requestId);
    }

    private async Task InstallCatalogueAsync(PortalApplication app, Button button)
    {
        // Retain v0.4.5 support for a package with only an EXE fingerprint.
        PortalCatalogueItem item;
        try
        {
            using var read = await http.GetAsync($"agent/catalogue/{app.CatalogueId}");
            await RequireSuccess(read);
            item = await read.Content.ReadFromJsonAsync<PortalCatalogueItem>()
                ?? throw new IOException("Package no longer exists");
        }
        catch (Exception e) { footer.Text = "Unable to open the installer: " + e.Message; return; }
        if (item.Manifest == null) { await InstallLegacyCatalogueAsync(app, button); return; }
        if (MessageBox.Show(this, $"Install {app.DisplayName}?\n\n" +
            $"Package size: {item.TotalBytes / (1024.0 * 1024 * 1024):F2} GB\n" +
            "HomeWatch will copy and verify the complete NAS package. " +
            "The installer may still request an administrator password.",
            "Install game", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        installing = true;
        button.Enabled = false;
        string auditStatus = "failed", auditDetail = "";
        using var cancel = new CancellationTokenSource();
        using var progressDialog = new Form
        {
            Text = "Preparing " + app.DisplayName, Size = new Size(550, 160),
            StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false, ControlBox = false
        };
        var progressText = new Label { Dock = DockStyle.Top, Height = 52, Padding = new Padding(12),
            Text = "Preparing approved installer…", AutoEllipsis = true };
        var cancelButton = new Button { Dock = DockStyle.Bottom, Height = 36, Text = "Cancel download" };
        cancelButton.Click += (_, _) => { cancel.Cancel(); cancelButton.Enabled = false; };
        progressDialog.Controls.Add(progressText);
        progressDialog.Controls.Add(cancelButton);
        progressDialog.Show(this);
        try
        {
            await InstallationStateAsync(item.Id, "copying");
            var exe = await HouseholdPackageInstaller.InstallAsync(item, message =>
            {
                if (!progressDialog.IsDisposed) progressText.Text = message;
                if (!IsDisposed) footer.Text = message;
            }, cancel.Token);
            progressDialog.Hide();
            // Recheck publication immediately before executing the approved setup.
            using (var latest = await http.GetAsync($"agent/catalogue/{item.Id}"))
            {
                await RequireSuccess(latest);
                var fresh = await latest.Content.ReadFromJsonAsync<PortalCatalogueItem>();
                if (fresh == null || !fresh.Enabled || fresh.InstallerSha256 != item.InstallerSha256)
                    throw new IOException("Parent has changed or withdrawn this package");
            }
            await InstallationStateAsync(item.Id, "installing");
            var before = HouseholdPackageInstaller.UninstallLocations();
            footer.Text = "Installer open — follow the normal Windows setup wizard.";
            using var process = Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) ?? ""
            });
            if (process is null) throw new IOException("Windows could not launch the installer");
            auditStatus = "started"; auditDetail = "Interactive installer started as logged-in user";
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new IOException("Installer exited with code " + process.ExitCode);
            await InstallationStateAsync(item.Id, "detecting");
            footer.Text = "Setup closed. Looking for the new game…";
            var detected = HouseholdPackageInstaller.FindInstalledGames(before, item.Name, item.GameScanRoots);
            string? selected = null;
            if (detected.Count == 1 && File.Exists(detected[0]))
            {
                if (MessageBox.Show(this, $"Did this game install here?\n{detected[0]}",
                    "Confirm game location", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    selected = detected[0];
            }
            if (selected is null)
            {
                using var picker = new OpenFileDialog
                {
                    Title = "Choose the installed game's EXE (not setup.exe)",
                    Filter = "Applications (*.exe)|*.exe", CheckFileExists = true,
                    InitialDirectory = detected.Count > 0 ? Path.GetDirectoryName(detected[0]) : null
                };
                if (picker.ShowDialog(this) == DialogResult.OK) selected = picker.FileName;
            }
            if (selected is null)
            {
                footer.Text = "Installer closed. Choose the game EXE later by installing again or asking your parent.";
                auditStatus = "needs_selection"; auditDetail = "Game location not confirmed";
                await InstallationStateAsync(item.Id, "detecting");
                return;
            }
            await RegisterInstalledGameAsync(item, selected);
            auditStatus = "completed"; auditDetail = "Game executable located; launch approval requested";
            footer.Text = "Game installed! Your parent can now approve its Play button.";
            if (MessageBox.Show(this, "Remove the copied installer files to free up disk space?\n\n" +
                "The installed game will not be affected.", "Clean up installation files",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                try { Directory.Delete(Path.GetDirectoryName(exe)!, recursive: true); }
                catch (Exception cleanupError) { footer.Text = "Installed; installer cache cleanup failed: " + cleanupError.Message; }
            }
        }
        catch (OperationCanceledException)
        {
            footer.Text = "Package copy cancelled.";
            auditStatus = "cancelled"; auditDetail = "Child cancelled the download";
            await InstallationStateAsync(item.Id, "failed");
        }
        catch (Exception e)
        {
            footer.Text = "Installation issue: " + e.Message;
            auditDetail = e.Message;
            try { await InstallationStateAsync(item.Id, "failed"); } catch { }
        }
        finally
        {
            try
            {
                using var audit = await http.PostAsJsonAsync($"agent/catalogue/{item.Id}/attempts",
                    new { status = auditStatus == "completed" ? "started" : auditStatus == "cancelled" ? "failed" : auditStatus == "needs_selection" ? "failed" : auditStatus,
                          detail = auditDetail.Length > 250 ? auditDetail[..250] : auditDetail });
                await RequireSuccess(audit);
            }
            catch { }
            progressDialog.Hide();
            installing = false;
            if (!IsDisposed) { button.Enabled = true; await LoadAppsAsync(force: true); }
        }
    }

    // Installers always run as the current Windows user; the privileged service
    // NEVER receives arbitrary paths or executes downloaded packages.
    private async Task InstallLegacyCatalogueAsync(PortalApplication app, Button button)
    {
        if (app.CatalogueId <= 0 || !app.IsCatalogue) return;
        if (MessageBox.Show(this,
            $"Get {app.DisplayName} from your family's installer library?\n\n" +
            "HomeWatch will verify the download before opening the installer. " +
            "Windows may still ask for an administrator password if the setup program requires it.",
            "Install from family library", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        button.Enabled = false;
        string status = "failed", detail = "";
        try
        {
            // Check the current published metadata immediately before opening the source.
            using var response = await http.GetAsync($"agent/catalogue/{app.CatalogueId}");
            await RequireSuccess(response);
            var current = await response.Content.ReadFromJsonAsync<PortalCatalogueItem>();
            if (current == null || !current.Enabled || current.InstallerSha256 != app.Sha256)
                throw new IOException("This installer has changed. Please refresh the family library.");
            var source = current.InstallerPath;
            if (!System.Text.RegularExpressions.Regex.IsMatch(current.InstallerSha256, "^[a-f0-9]{64}$"))
                throw new IOException("Invalid installer fingerprint.");
            var httpSource = Uri.TryCreate(source, UriKind.Absolute, out var url) &&
                (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp);
            if (!httpSource && !source.StartsWith(@"\\", StringComparison.Ordinal))
                throw new IOException("Use an approved UNC network share or HTTP(S) installer URL.");
            var fileName = httpSource ? Path.GetFileName(url!.AbsolutePath) : Path.GetFileName(source);
            if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Only .exe installers are supported.");
            // Use the digest in the path to prevent old installers clashing with new versions.
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HomeWatch", "installers", current.InstallerSha256);
            Directory.CreateDirectory(root);
            var target = Path.Combine(root, "HomeWatch-Installer.exe");
            if (!File.Exists(target) || !await HashMatchesAsync(target, current.InstallerSha256))
            {
                var temp = Path.Combine(root, "download-" + Guid.NewGuid().ToString("N") + ".tmp");
                footer.Text = $"Copying {app.DisplayName} from the family library…";
                try
                {
                    const long maxLength = 8L * 1024 * 1024 * 1024;
                    await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        if (httpSource)
                        {
                            // Never forward device bearer credentials to the NAS URL.
                            using var downloader = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
                            { Timeout = TimeSpan.FromMinutes(45) };
                            using var download = await downloader.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                            if (!download.IsSuccessStatusCode || (int)download.StatusCode >= 300)
                                throw new IOException("NAS HTTP download failed: " + (int)download.StatusCode);
                            if (download.Content.Headers.ContentLength > maxLength)
                                throw new IOException("Installer is larger than the 8 GB limit.");
                            await using var input = await download.Content.ReadAsStreamAsync();
                            await CopyLimitedAsync(input, output, maxLength);
                        }
                        else
                        {
                            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                            if (input.Length > maxLength) throw new IOException("Installer is larger than the 8 GB limit.");
                            await CopyLimitedAsync(input, output, maxLength);
                        }
                    }
                    if (!await HashMatchesAsync(temp, current.InstallerSha256))
                    {
                        status = "hash_mismatch";
                        throw new IOException("Installer fingerprint mismatch. Nothing was launched. Ask your parent to verify the NAS file and its SHA-256.");
                    }
                    File.Move(temp, target, true);
                }
                finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
            }
            // Recheck the exact local bytes and the parent's publication status
            // right before launching (the catalogue may have been revoked).
            if (!await HashMatchesAsync(target, current.InstallerSha256))
                throw new IOException("Installer changed after verification. Launch blocked.");
            using (var publishCheck = await http.GetAsync($"agent/catalogue/{app.CatalogueId}"))
            {
                await RequireSuccess(publishCheck);
                var latest = await publishCheck.Content.ReadFromJsonAsync<PortalCatalogueItem>();
                if (latest == null || !latest.Enabled || latest.InstallerSha256 != current.InstallerSha256)
                    throw new IOException("This installer is no longer published by your parent.");
            }
            footer.Text = $"Opening the installer for {app.DisplayName}…";
            using var process = Process.Start(new ProcessStartInfo(target)
            { UseShellExecute = true, WorkingDirectory = root });
            if (process is null) throw new IOException("Windows could not open the installer.");
            status = "started";
            detail = "Installer opened as the current Windows user";
            footer.Text = "Installer started! Follow its instructions. ✨";
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            if (!IsDisposed) footer.Text = "Could not install: " + ex.Message;
        }
        finally
        {
            try
            {
                using var audit = await http.PostAsJsonAsync($"agent/catalogue/{app.CatalogueId}/attempts",
                    new { status, detail });
                await RequireSuccess(audit);
            }
            catch { /* Audit transport failure must not stop the UI. */ }
            if (!IsDisposed) button.Enabled = true;
        }
    }

    private static async Task CopyLimitedAsync(Stream input, Stream output, long maxLength)
    {
        var buffer = new byte[128 * 1024];
        long copied = 0;
        int size;
        while ((size = await input.ReadAsync(buffer)) > 0)
        {
            copied += size;
            if (copied > maxLength) throw new IOException("Installer is larger than the 8 GB limit.");
            await output.WriteAsync(buffer.AsMemory(0, size));
        }
    }

    private static async Task<bool> HashMatchesAsync(string filename, string expected)
    {
        await using var input = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read);
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(input)).ToLowerInvariant();
        return string.Equals(digest, expected, StringComparison.Ordinal);
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

public sealed class PortalCatalogueItem
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("installer_path")] public string InstallerPath { get; set; } = "";
    [JsonPropertyName("installer_sha256")] public string InstallerSha256 { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("artwork_revision")] public int ArtworkRevision { get; set; }
    [JsonPropertyName("package_root")] public string PackageRoot { get; set; } = "";
    [JsonPropertyName("package_entrypoint")] public string PackageEntrypoint { get; set; } = "";
    [JsonPropertyName("manifest")] public PackageManifest? Manifest { get; set; }
    [JsonPropertyName("total_bytes")] public long TotalBytes { get; set; }
    [JsonPropertyName("game_scan_roots")] public List<string> GameScanRoots { get; set; } = new();
    [JsonPropertyName("installation")] public PackageInstallation? Installation { get; set; }
}

public sealed class PackageInstallation
{
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("executable_path")] public string ExecutablePath { get; set; } = "";
    [JsonPropertyName("request_id")] public int? RequestId { get; set; }
    [JsonPropertyName("approved")] public bool Approved { get; set; }
}

public sealed class PackageManifest
{
    [JsonPropertyName("entrypoint")] public string Entrypoint { get; set; } = "";
    [JsonPropertyName("total_bytes")] public long TotalBytes { get; set; }
    [JsonPropertyName("files")] public List<PackageFile> Files { get; set; } = new();
}

public sealed class PackageFile
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}

public sealed class PortalApplication
{
    [JsonPropertyName("id")] public int Id { get; set; }
    public int CatalogueId { get; set; }
    public bool IsCatalogue { get; set; }
    public string Description { get; set; } = "";
    public string PackageStatus { get; set; } = "";
    public string InstalledPath { get; set; } = "";
    public int InstalledRequestId { get; set; }
    public bool InstalledApproved { get; set; }
    public long PackageSize { get; set; }
    [JsonPropertyName("file_name")] public string FileName { get; set; } = "";
    [JsonPropertyName("file_path")] public string FilePath { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("approval_scope")] public string ApprovalScope { get; set; } = "";
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("artwork_revision")] public int ArtworkRevision { get; set; }
    [JsonPropertyName("publisher")] public string Publisher { get; set; } = "";
}
