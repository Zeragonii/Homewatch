using System.Drawing;

namespace HomeWatchAgent;

// Child-facing storage maintenance. No administrator credentials or paths can
// be supplied. The privileged service independently owns all broker cleanup.
internal sealed class StorageCleanupForm : Form
{
    private static readonly Color Background = Color.FromArgb(12, 20, 35);
    private static readonly Color Surface = Color.FromArgb(24, 36, 57);
    private static readonly Color Muted = Color.FromArgb(162, 180, 204);
    private static readonly Color Accent = Color.FromArgb(92, 157, 249);
    private static readonly Color Green = Color.FromArgb(104, 218, 165);
    private readonly Func<bool> installationActive;
    private readonly Label totalLabel;
    private readonly Label downloadLabel;
    private readonly Label brokerLabel;
    private readonly Label eligibleLabel;
    private readonly Label statusLabel;
    private readonly Button cleanButton;
    private readonly Button refreshButton;
    private bool busy;
    private bool brokerReachable;
    private long eligibleBytes;

    internal StorageCleanupForm(Func<bool> installationActive)
    {
        this.installationActive = installationActive;
        Text = "HomeWatch — Storage Cleanup";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(520, 424);
        BackColor = Background;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10);
        Icon = AgentContext.LoadBrandIcon();

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 16, 24, 20),
            ColumnCount = 1, RowCount = 9, BackColor = Background };
        foreach (int height in new[] { 35, 25, 62, 47, 47, 42, 35, 55, 38 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        var heading = Label("Keep your computer tidy ✨", 19, FontStyle.Bold);
        layout.Controls.Add(heading, 0, 0);
        layout.Controls.Add(Label("These are temporary installers, not your installed games.", 9, FontStyle.Regular, Muted), 0, 1);
        totalLabel = Label("Checking temporary files…", 21, FontStyle.Bold, Green);
        layout.Controls.Add(totalLabel, 0, 2);
        downloadLabel = Label("Downloads: checking…", 11, FontStyle.Regular);
        brokerLabel = Label("Protected installer copies: checking…", 11, FontStyle.Regular);
        layout.Controls.Add(downloadLabel, 0, 3);
        layout.Controls.Add(brokerLabel, 0, 4);
        eligibleLabel = Label("Checking what is safe to clean…", 10, FontStyle.Regular, Muted);
        layout.Controls.Add(eligibleLabel, 0, 5);
        statusLabel = Label("Files in use, or recently downloaded, are always left alone.", 9, FontStyle.Regular, Muted);
        statusLabel.AutoEllipsis = true;
        layout.Controls.Add(statusLabel, 0, 6);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, FlowDirection = FlowDirection.LeftToRight };
        cleanButton = Button("Clean up now", Accent, 195);
        refreshButton = Button("Refresh size", Surface, 135);
        actions.Controls.Add(cleanButton);
        actions.Controls.Add(refreshButton);
        layout.Controls.Add(actions, 0, 7);
        var note = Label("Only HomeWatch's own temporary downloads and protected staging are affected.",
            9, FontStyle.Regular, Muted);
        layout.Controls.Add(note, 0, 8);
        Controls.Add(layout);
        cleanButton.Click += async (_, _) => await CleanAsync();
        refreshButton.Click += async (_, _) => await RefreshAsync();
        Shown += async (_, _) => await RefreshAsync();
    }

    private static Label Label(string text, float size, FontStyle style, Color? color = null) => new()
    {
        Text = text, Dock = DockStyle.Fill, ForeColor = color ?? Color.White,
        Font = new Font("Segoe UI", size, style), TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true
    };

    private static Button Button(string text, Color color, int width) => new()
    {
        Text = text, Width = width, Height = 38, Margin = new Padding(0, 2, 12, 0),
        BackColor = color, ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
        Cursor = Cursors.Hand, Font = new Font("Segoe UI Semibold", 10, FontStyle.Bold)
    };

    internal static string SizeText(long bytes)
    {
        if (bytes < 1024) return bytes.ToString() + " B";
        string[] sizes = ["KB", "MB", "GB", "TB"];
        double value = bytes;
        int i = -1;
        do { value /= 1024; i++; } while (value >= 1024 && i < sizes.Length - 1);
        return value.ToString(value >= 10 ? "F1" : "F2") + " " + sizes[i];
    }

    private async Task RefreshAsync()
    {
        if (busy || IsDisposed) return;
        busy = true;
        refreshButton.Enabled = cleanButton.Enabled = false;
        try
        {
            statusLabel.Text = "Checking both storage locations…";
            var cache = await Task.Run(InstallerCacheMaintenance.GetUsage);
            InstallerBrokerClient.BrokerStorage? broker = null;
            brokerReachable = false;
            if (InstallerBrokerClient.IsInstalled)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    broker = await InstallerBrokerClient.GetStorageAsync(timeout.Token);
                    brokerReachable = true;
                }
                catch { /* Show an unknown broker size, not a fabricated zero. */ }
            }
            if (IsDisposed) return;
            long total = cache.TotalBytes + (broker?.TotalBytes ?? 0);
            eligibleBytes = cache.EligibleBytes + (broker?.EligibleBytes ?? 0);
            totalLabel.Text = broker is null && InstallerBrokerClient.IsInstalled
                ? SizeText(cache.TotalBytes) + " + broker (unavailable)"
                : SizeText(total) + " in temporary files";
            downloadLabel.Text = "Downloaded packages: " + SizeText(cache.TotalBytes) +
                $"  ({cache.FolderCount} folders)";
            brokerLabel.Text = broker is null
                ? "Protected broker staging: " + (InstallerBrokerClient.IsInstalled ? "unavailable" : "not installed")
                : "Protected broker staging: " + SizeText(broker.TotalBytes);
            eligibleLabel.Text = "Ready to clear safely: " + SizeText(eligibleBytes);
            statusLabel.Text = installationActive() ? "An installation is running — tidy-up is paused."
                : "New or busy packages are kept until it's safe to remove them.";
        }
        catch (Exception ex)
        {
            if (!IsDisposed) statusLabel.Text = "Could not check storage: " + ex.Message;
            eligibleBytes = 0;
        }
        finally
        {
            busy = false;
            if (!IsDisposed)
            {
                refreshButton.Enabled = true;
                cleanButton.Enabled = eligibleBytes > 0 && !installationActive();
            }
        }
    }

    private async Task CleanAsync()
    {
        if (busy || IsDisposed || installationActive() || eligibleBytes <= 0) return;
        if (MessageBox.Show(this,
            "Remove old, unused installer downloads and broker staging?\n\n" +
            "Installed games, game saves and NAS files will not be removed.",
            "Tidy up HomeWatch", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        busy = true;
        cleanButton.Enabled = refreshButton.Enabled = false;
        statusLabel.Text = "Tidying up unused installer files…";
        long freed = 0;
        string? brokerIssue = null;
        try
        {
            // The local cleaner and downloader use the same per-package
            // exclusive leases; nothing in use can be removed by this path.
            var local = await Task.Run(InstallerCacheMaintenance.CleanNow);
            freed += local.FreedBytes;
            if (InstallerBrokerClient.IsInstalled)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                    freed += await InstallerBrokerClient.CleanStorageAsync(timeout.Token);
                }
                catch (Exception ex) { brokerIssue = ex.Message; }
            }
        }
        catch (Exception ex) { brokerIssue = ex.Message; }
        finally { busy = false; }
        if (IsDisposed) return;
        await RefreshAsync();
        if (!IsDisposed)
            statusLabel.Text = brokerIssue is null
                ? "Nice! Freed approximately " + SizeText(freed) + " of temporary storage. 🎉"
                : "Freed " + SizeText(freed) + ". Broker: " + brokerIssue;
    }
}
