using System.ComponentModel;
using System.Drawing;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;

namespace HomeWatchAgent;

// An explicitly local, interactive administration tool. No network endpoint,
// agent command, or SYSTEM updater-service action can launch this operation.
internal static class DefenderSetup
{
    internal const string AdminArgument = "--local-defender-setup";
    private const string ResourceName = "HomeWatch.Manage-DefenderExclusions.ps1";

    internal static void OpenWithUac()
    {
        var consent = MessageBox.Show(
            "This opens a local administrator-only tool for optional Microsoft Defender folder exclusions.\n\n" +
            "These exclusions reduce malware scanning of HomeWatch package caches and game directories. " +
            "Only continue on a PC you administer. No changes will be made until you choose Add or Remove in the elevated window.\n\n" +
            "Continue to Windows administrator approval?",
            "HomeWatch — Performance settings", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (consent != DialogResult.Yes) return;

        try
        {
            // UAC is required even when this launcher was started by a child user.
            // No credentials are requested, stored, forwarded or handled by HomeWatch.
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? Application.ExecutablePath,
                Arguments = AdminArgument,
                Verb = "runas",
                UseShellExecute = true
            });
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            // Administrator declined the UAC prompt. Nothing was modified.
        }
        catch (Exception e)
        {
            MessageBox.Show("Unable to open administrator settings: " + e.Message,
                "HomeWatch", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    internal static void ShowElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity is null || !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            MessageBox.Show("Administrator elevation is required. Open this tool from HomeWatch Launcher and approve Windows UAC.",
                "HomeWatch", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        using var dialog = new DefenderSetupForm();
        dialog.ShowDialog();
    }

    internal static async Task<(int ExitCode, string Output)> ApplyAsync(string action, string? gameFolder)
    {
        if (action is not ("Add" or "Remove")) throw new ArgumentException("Unknown action", nameof(action));
        // Read bundled, build-time-controlled script, not a file controlled by a child
        // or a server-provided script/command. Extract only in the elevated context.
        await using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new FileNotFoundException("Bundled exclusions script is missing from the executable.");
        string script = Path.Combine(Path.GetTempPath(), "homewatch-defender-" + Guid.NewGuid().ToString("N") + ".ps1");
        try
        {
            await using (var output = new FileStream(script, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await resource.CopyToAsync(output);

            var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var start = new ProcessStartInfo
            {
                FileName = powershell,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            // RemoteSigned allows a locally-created script; machine Group Policy
            // can still prevent execution. Never use Bypass or hidden server commands.
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "RemoteSigned",
                                        "-File", script, "-Action", action, "-ConfirmedInLocalUi" })
                start.ArgumentList.Add(arg);
            if (!string.IsNullOrWhiteSpace(gameFolder))
            {
                start.ArgumentList.Add("-GameFolders");
                start.ArgumentList.Add(gameFolder.Trim());
            }
            using var process = Process.Start(start) ?? throw new IOException("Could not launch Windows PowerShell.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, (await stdout) + Environment.NewLine + (await stderr));
        }
        finally
        {
            try { File.Delete(script); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

internal sealed class DefenderSetupForm : Form
{
    private readonly TextBox folder = new() { Dock = DockStyle.Fill, PlaceholderText = "Optional, e.g. D:\\Games" };
    private readonly TextBox result = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill, Font = new Font("Consolas", 9), BackColor = Color.FromArgb(19, 27, 40),
        ForeColor = Color.White
    };
    private readonly Button add = new() { Text = "Add exclusions", Width = 145, Height = 34 };
    private readonly Button remove = new() { Text = "Remove exclusions", Width = 145, Height = 34 };

    internal DefenderSetupForm()
    {
        Text = "HomeWatch — Administrator performance settings";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(735, 500);
        MinimumSize = new Size(640, 420);
        BackColor = Color.FromArgb(12, 20, 35);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10);
        Icon = AgentContext.LoadBrandIcon();

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 5 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 75));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 37));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill, ForeColor = Color.FromArgb(208, 220, 241),
            Text = "LOCAL ADMINISTRATOR ONLY\n" +
                   "Excludes package staging caches for existing Windows profiles and optionally ONE game directory. " +
                   "Exclusions reduce Defender scanning; files placed in these folders will not get normal real-time scanning.",
            AutoSize = false
        }, 0, 0);
        root.Controls.Add(new Label { Text = "Optional local game directory (leave empty for caches only):", Dock = DockStyle.Fill }, 0, 1);
        var folderRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        folderRow.Controls.Add(folder, 0, 0);
        var browse = new Button { Text = "Browse...", Dock = DockStyle.Fill };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Choose the game installation folder to exclude" };
            if (dialog.ShowDialog(this) == DialogResult.OK) folder.Text = dialog.SelectedPath;
        };
        folderRow.Controls.Add(browse, 1, 0);
        root.Controls.Add(folderRow, 0, 2);
        result.Text = "No changes have been made. Choose an action below to review the selected folders.";
        root.Controls.Add(result, 0, 3);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        add.Click += async (_, _) => await Apply("Add");
        remove.Click += async (_, _) => await Apply("Remove");
        actions.Controls.Add(add);
        actions.Controls.Add(remove);
        var close = new Button { Text = "Close", Width = 95, Height = 34 };
        close.Click += (_, _) => Close();
        actions.Controls.Add(close);
        root.Controls.Add(actions, 0, 4);
        Controls.Add(root);
    }

    private async Task Apply(string action)
    {
        string path = folder.Text.Trim();
        if (!string.IsNullOrEmpty(path) && !Directory.Exists(path))
        {
            MessageBox.Show("Please choose an existing local game directory, or leave it empty to manage caches only.",
                "HomeWatch", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var confirm = MessageBox.Show(
            $"{action} Defender real-time scanning exclusions for the HomeWatch package caches of existing user profiles" +
            (path.Length > 0 ? $" and:\n{path}" : "") + "?\n\nThis will change Windows Defender settings on THIS PC only.",
            "Confirm local administrator action", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;
        add.Enabled = false;
        remove.Enabled = false;
        folder.Enabled = false;
        result.Text = "Applying changes...";
        try
        {
            var outcome = await DefenderSetup.ApplyAsync(action, path);
            result.Text = outcome.Output.Trim();
            if (outcome.ExitCode != 0)
                MessageBox.Show("Defender settings did not complete successfully. Review the log and Windows Security settings.",
                    "HomeWatch", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception e)
        {
            result.Text = e.ToString();
            MessageBox.Show("Unable to change Defender settings. No successful change is assumed.",
                "HomeWatch", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            add.Enabled = true;
            remove.Enabled = true;
            folder.Enabled = true;
        }
    }
}
