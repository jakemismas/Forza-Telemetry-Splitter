using ForzaTelemetrySplitter.Config;
using ForzaTelemetrySplitter.Resources;

namespace ForzaTelemetrySplitter.UI;

/// <summary>
/// Owns the tray icon and its context menu, and acts as the app's lifetime root
/// (an ApplicationContext). The main window hides to tray; the app only exits via Exit here.
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private readonly AppConfig _config;
    private readonly MainForm _mainForm;
    private readonly OverlayForm _overlay;
    private readonly NotifyIcon _tray;

    private readonly ToolStripMenuItem _showOverlayItem;
    private readonly ToolStripMenuItem _startStopItem;

    public TrayContext(AppConfig config, MainForm mainForm, OverlayForm overlay)
    {
        _config = config;
        _mainForm = mainForm;
        _overlay = overlay;

        _showOverlayItem = new ToolStripMenuItem(Strings.Tray_ShowOverlay, null, OnToggleOverlay) { Checked = config.ShowOverlay };
        _startStopItem = new ToolStripMenuItem(Strings.Tray_StartSplitting, null, OnToggleSplitting);

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem(Strings.Tray_Open, null, (_, _) => ShowMainWindow()));
        menu.Items.Add(_startStopItem);
        menu.Items.Add(_showOverlayItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(Strings.Tray_SetupGuide, null, OnSetupGuide));
        menu.Items.Add(new ToolStripMenuItem(Strings.Tray_CheckUpdates, null, OnCheckForUpdates));
        menu.Items.Add(new ToolStripMenuItem(Strings.Tray_Exit, null, OnExit));

        _tray = new NotifyIcon
        {
            Text = "Forza Telemetry Splitter",
            Icon = Resources.AppIcon.Load(),
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowMainWindow();

        _mainForm.RunStateChanged += RefreshRunState;
        _mainForm.AutoSplitNotice += OnAutoSplitNotice;
        _mainForm.Engine.ErrorOccurred += OnEngineErrorBalloon;
        _mainForm.Engine.PortInUse += port => OnEngineErrorBalloon(Strings.Error_PortInUse(port));

        // Apply persisted overlay visibility.
        ApplyOverlayVisibility();
        RefreshRunState();
    }

    private void ShowMainWindow()
    {
        _mainForm.Show();
        _mainForm.WindowState = FormWindowState.Normal;
        _mainForm.Activate();
        _mainForm.BringToFront();
    }

    private void OnToggleOverlay(object? sender, EventArgs e)
    {
        _config.ShowOverlay = !_config.ShowOverlay;
        _showOverlayItem.Checked = _config.ShowOverlay;
        ApplyOverlayVisibility();
        ConfigStore.Save(_config);
    }

    private void ApplyOverlayVisibility()
    {
        if (_config.ShowOverlay)
        {
            _overlay.Reposition();
            _overlay.Show();
        }
        else
        {
            _overlay.Hide();
        }
    }

    private void OnToggleSplitting(object? sender, EventArgs e) => _mainForm.ToggleRunning();

    /// <summary>Reopen the first-run welcome / setup guide on demand.</summary>
    private void OnSetupGuide(object? sender, EventArgs e)
    {
        using var welcome = new WelcomeForm(_config);
        welcome.ShowDialog();
        // If they tick "don't show again" here, respect it; never un-set the flag from this entry.
        if (welcome.DontShowAgain && !_config.FirstRunComplete)
        {
            _config.FirstRunComplete = true;
            ConfigStore.Save(_config);
        }
    }

    /// <summary>
    /// Lightweight manual-update path: open the GitHub Releases page in the default browser.
    /// (No background auto-update — this is a small, stable utility; users grab new builds when
    /// they want them.)
    /// </summary>
    private void OnCheckForUpdates(object? sender, EventArgs e)
    {
        const string releasesUrl = "https://github.com/jakemismas/Forza-Telemetry-Splitter/releases";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = releasesUrl,
                UseShellExecute = true,
            });
        }
        catch
        {
            MessageBox.Show($"Latest releases:\n{releasesUrl}", "Forza Telemetry Splitter",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private void RefreshRunState()
    {
        _startStopItem.Text = _mainForm.Engine.Running ? Strings.Tray_StopSplitting : Strings.Tray_StartSplitting;
    }

    private void OnEngineErrorBalloon(string message)
    {
        try
        {
            _tray.BalloonTipTitle = "Forza Telemetry Splitter";
            _tray.BalloonTipText = message.Length > 200 ? message[..200] + "…" : message;
            _tray.ShowBalloonTip(5000);
        }
        catch { /* balloon is best-effort */ }
    }

    /// <summary>
    /// The watcher auto-started (game != null, naming it) or auto-stopped (game == null) the splitter.
    /// Surface a brief, friendly balloon so the user sees the zero-click lifecycle working.
    /// </summary>
    private void OnAutoSplitNotice(string? game)
    {
        try
        {
            _tray.BalloonTipTitle = "Forza Telemetry Splitter";
            _tray.BalloonTipText = game is null ? Strings.Tray_GameClosed : Strings.Tray_GameDetected(game);
            _tray.ShowBalloonTip(4000);
        }
        catch { /* balloon is best-effort */ }
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _mainForm.ReallyExit = true;
        _mainForm.Engine.Stop();
        ConfigStore.Save(_config);

        _tray.Visible = false;
        _tray.Dispose();
        _overlay.Close();
        _mainForm.Close();

        ExitThread();
    }
}
