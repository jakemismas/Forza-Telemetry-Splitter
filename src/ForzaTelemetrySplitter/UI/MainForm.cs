using System.ComponentModel;
using ForzaTelemetrySplitter.Config;
using ForzaTelemetrySplitter.Core;
using ForzaTelemetrySplitter.Resources;

namespace ForzaTelemetrySplitter.UI;

/// <summary>
/// The main configuration window: the destination grid, add/edit/remove/toggle, a Start/Stop
/// button, and a live status strip. Closing the window hides it to the tray rather than exiting.
/// </summary>
public sealed class MainForm : Form
{
    private readonly AppConfig _config;
    private readonly SplitterEngine _engine;

    private readonly DataGridView _grid = new();
    private readonly Button _startStop = new();
    private readonly Label _status = new();
    private readonly Label _listenInfo = new();
    private readonly ComboBox _units = new();
    private readonly ComboBox _language = new();
    private readonly CheckBox _startWithWindows = new();
    private readonly TabControl _tabs = new();
    private readonly TabPage _statusPage = new();
    private readonly TabPage _activityPage = new();
    private readonly TabPage _overlayPage = new();
    private readonly TabPage _settingsPage = new();
    private ActivityChart? _chart;
    private readonly Button _logToggle = new();      // Start/Stop logging (Activity tab)
    private readonly Label _logFolderLabel = new();
    private readonly Panel _statusDot = new();       // reliably-rendered colored status indicator
    private Color _statusDotColor = Color.Gray;
    private readonly Dictionary<Destination, long> _prevForwarded = new();
    private readonly Dictionary<Destination, DestinationStatus> _statusCache = new();
    private readonly System.Windows.Forms.Timer _uiTimer = new();

    private readonly ForzaProcessWatcher _gameWatcher = new();
    private CheckBox? _autoSplit;
    /// <summary>Display name of the Forza title the watcher currently sees, or null if none.</summary>
    private string? _autoDetectedGame;
    /// <summary>True while the engine was started by the watcher (vs. a manual Start).</summary>
    private bool _autoStarted;
    /// <summary>
    /// Set when the user presses Stop while a game is still detected, so the watcher won't immediately
    /// restart for that session. Cleared when the game process exits (session over) or on a manual Start.
    /// </summary>
    private bool _manualStopThisSession;

    // Index order of the language dropdown items.
    private static readonly AppLanguage[] _languageOrder =
    {
        AppLanguage.Auto, AppLanguage.English, AppLanguage.Japanese,
        AppLanguage.French, AppLanguage.German, AppLanguage.Spanish,
    };

    private readonly OverlayForm _overlay;

    /// <summary>True once the app is genuinely exiting (vs. just hiding to tray).</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ReallyExit { get; set; }

    /// <summary>Raised when the engine's running state changes, so the tray can refresh its menu.</summary>
    public event Action? RunStateChanged;

    /// <summary>
    /// Raised when the process watcher auto-starts (string = detected game name) or auto-stops
    /// (string = null) the splitter, so the tray can show a balloon naming the game.
    /// </summary>
    public event Action<string?>? AutoSplitNotice;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public SplitterEngine Engine => _engine;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public OverlayForm Overlay => _overlay;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public AppConfig Config => _config;

    public MainForm(AppConfig config, SplitterEngine engine, OverlayForm overlay)
    {
        _config = config;
        _engine = engine;
        _overlay = overlay;

        Text = Strings.Main_Title;
        Icon = AppIcon.Load();
        ClientSize = new Size(640, 480);
        MinimumSize = new Size(560, 440);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        _overlay.ApplySettings(_config);
        _overlay.Moved += p => { _config.OverlayX = p.X; _config.OverlayY = p.Y; ConfigStore.Save(_config); };

        BuildLayout();
        RefreshGrid();

        _engine.ErrorOccurred += OnEngineError;
        _engine.PortInUse += OnPortInUse;

        _uiTimer.Interval = 250; // ~4 Hz
        _uiTimer.Tick += (_, _) => UpdateStatus();
        _uiTimer.Start();

        // Watch for a running Forza game and auto-start/stop splitting around its lifetime.
        // Timer-based, so these handlers run on the UI thread.
        _gameWatcher.GameDetected += OnGameDetected;
        _gameWatcher.GameClosed += OnGameClosed;
        _gameWatcher.Start();
    }

    private void BuildLayout()
    {
        // Tabs: Status (destinations), Activity (throughput + logging), Overlay (overlay settings),
        // Settings (preferences).
        _tabs.Dock = DockStyle.Fill;
        _statusPage.Text = Strings.Tab_Status;
        _activityPage.Text = Strings.Tab_Activity;
        _overlayPage.Text = Strings.Tab_Overlay;
        _settingsPage.Text = Strings.Tab_Settings;
        _tabs.TabPages.Add(_statusPage);
        _tabs.TabPages.Add(_activityPage);
        _tabs.TabPages.Add(_overlayPage);
        _tabs.TabPages.Add(_settingsPage);
        Controls.Add(_tabs);

        BuildStatusTab();
        BuildActivityTab();
        BuildOverlayTab();
        BuildSettingsTab();

        UpdateStartStopButton();
    }

    private void BuildStatusTab()
    {
        var host = _statusPage;
        int hostW = host.ClientSize.Width;
        int hostH = host.ClientSize.Height;

        _listenInfo.SetBounds(12, 10, hostW - 24, 20);
        _listenInfo.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _listenInfo.ForeColor = Color.DimGray;
        _listenInfo.Text = Strings.Main_ListenInfo(_config.ListenIp, _config.ListenPort);
        host.Controls.Add(_listenInfo);

        // Destination grid (height computed below once the bottom rows are placed).
        _grid.SetBounds(12, 34, hostW - 24, hostH - 170);
        _grid.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.ShowCellToolTips = true;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.EditMode = DataGridViewEditMode.EditProgrammatically;
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = Strings.Col_On, Name = "Enabled", FillWeight = 10 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = Strings.Col_Status, Name = "Status", FillWeight = 24, ReadOnly = true });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = Strings.Col_Name, Name = "Name", FillWeight = 28 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = Strings.Col_Ip, Name = "Ip", FillWeight = 26 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = Strings.Col_Port, Name = "Port", FillWeight = 12 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = Strings.Col_Forwarded, Name = "Count", FillWeight = 18 });
        _grid.CellContentClick += OnGridCellClick;
        _grid.CellDoubleClick += (_, e) =>
        {
            // Don't treat a double-click on the Enabled/Status cells as "edit" — toggling the
            // checkbox must just enable/disable, never open the edit dialog.
            if (e.RowIndex >= 0 && _grid.Columns[e.ColumnIndex].Name is not ("Enabled" or "Status"))
                EditSelected();
        };
        _grid.CellPainting += OnStatusCellPainting;
        host.Controls.Add(_grid);

        // --- Bottom region, laid out bottom-up so nothing overlaps or clips ---
        int H = host.ClientSize.Height;
        int W = host.ClientSize.Width;

        // (1) Italic "you can close this window" note, at the very bottom.
        var bgNote = new Label
        {
            Text = Strings.Main_BackgroundNote,
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = Color.DimGray,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        bgNote.SetBounds(12, H - 26, W - 24, 20);
        bgNote.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        host.Controls.Add(bgNote);

        // (2) Status strip above the note: a reliably-painted colored dot + text.
        _statusDot.SetBounds(12, H - 52, 14, 14);
        _statusDot.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _statusDot.Paint += (_, pe) =>
        {
            pe.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var b = new SolidBrush(_statusDotColor);
            pe.Graphics.FillEllipse(b, 0, 0, _statusDot.Width - 1, _statusDot.Height - 1);
        };
        host.Controls.Add(_statusDot);

        _status.SetBounds(32, H - 56, W - 44, 26);
        _status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _status.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        host.Controls.Add(_status);

        // (3) Action row: Add / Edit / Remove (left), Start/Stop (right).
        int row = H - 92;
        var add = MakeButton(host, Strings.Main_Add, 12, row, AnchorStyles.Bottom | AnchorStyles.Left);
        add.Click += (_, _) => AddDestination();
        var edit = MakeButton(host, Strings.Main_Edit, 96, row, AnchorStyles.Bottom | AnchorStyles.Left);
        edit.Click += (_, _) => EditSelected();
        var remove = MakeButton(host, Strings.Main_Remove, 180, row, AnchorStyles.Bottom | AnchorStyles.Left);
        remove.Click += (_, _) => RemoveSelected();

        _startStop.SetBounds(W - 132, row, 120, 28);
        _startStop.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _startStop.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        _startStop.Click += (_, _) => ToggleRunning();
        host.Controls.Add(_startStop);

        // Size the grid to end just above the action row.
        _grid.Size = new Size(_grid.Width, row - 34 - 6);
    }

    private void BuildActivityTab()
    {
        var host = _activityPage;
        int W = host.ClientSize.Width, H = host.ClientSize.Height;

        // Chart fills the top; two control rows sit below it.
        _chart = new ActivityChart(_engine.History)
        {
            Bounds = new Rectangle(8, 8, W - 16, H - 86),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };
        host.Controls.Add(_chart);

        // Row 1 (just under the chart): zoom + logging controls.
        int row1 = H - 70;
        var zoomIn = new Button { Text = "+", Font = new Font("Segoe UI", 11f, FontStyle.Bold) };
        zoomIn.SetBounds(8, row1, 36, 28);
        zoomIn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        zoomIn.Click += (_, _) => _chart?.ZoomIn();
        new ToolTip().SetToolTip(zoomIn, Strings.Zoom_In);
        host.Controls.Add(zoomIn);

        var zoomOut = new Button { Text = "−", Font = new Font("Segoe UI", 11f, FontStyle.Bold) };
        zoomOut.SetBounds(48, row1, 36, 28);
        zoomOut.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        zoomOut.Click += (_, _) => _chart?.ZoomOut();
        new ToolTip().SetToolTip(zoomOut, Strings.Zoom_Out);
        host.Controls.Add(zoomOut);

        // Logging controls on the right of row 1.
        _logToggle.SetBounds(W - 320, row1, 110, 28);
        _logToggle.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _logToggle.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        _logToggle.Click += (_, _) => ToggleLogging();
        host.Controls.Add(_logToggle);
        UpdateLogButton();

        var changeFolder = new Button { Text = Strings.Log_ChangeFolder };
        changeFolder.SetBounds(W - 204, row1, 96, 28);
        changeFolder.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        changeFolder.Click += (_, _) => ChangeLogFolder();
        host.Controls.Add(changeFolder);

        var openFolder = new Button { Text = Strings.Log_OpenFolder };
        openFolder.SetBounds(W - 104, row1, 96, 28);
        openFolder.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        openFolder.Click += (_, _) => OpenLogFolder();
        host.Controls.Add(openFolder);

        // Row 2: the current log folder path.
        _logFolderLabel.Text = Strings.Log_FolderLabel(LogDir);
        _logFolderLabel.Font = new Font("Segoe UI", 8f);
        _logFolderLabel.ForeColor = Color.DimGray;
        _logFolderLabel.AutoEllipsis = true;
        _logFolderLabel.SetBounds(8, H - 32, W - 16, 22);
        _logFolderLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        host.Controls.Add(_logFolderLabel);
    }

    private void BuildOverlayTab()
    {
        var host = _overlayPage;
        int x = 18, y = 18, rowH = 36;

        var show = new CheckBox { Text = Strings.Ov_Show, AutoSize = true, Checked = _config.ShowOverlay };
        show.SetBounds(x, y, 280, 24);
        show.CheckedChanged += (_, _) =>
        {
            _config.ShowOverlay = show.Checked;
            ConfigStore.Save(_config);
            if (_config.ShowOverlay) { _overlay.ApplySettings(_config); _overlay.Show(); } else _overlay.Hide();
        };
        host.Controls.Add(show);
        y += rowH;

        var transparent = new CheckBox { Text = Strings.Ov_TransparentBg, AutoSize = true, Checked = _config.OverlayTransparentBg };
        host.Controls.Add(transparent);
        transparent.SetBounds(x, y, 280, 24);
        y += rowH;

        var textColor = new Button { Text = Strings.Ov_TextColor };
        textColor.SetBounds(x, y, 150, 28);
        textColor.Click += (_, _) =>
        {
            using var dlg = new ColorDialog { Color = Color.FromArgb(_config.OverlayTextColorArgb), FullOpen = true };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _config.OverlayTextColorArgb = dlg.Color.ToArgb();
            ConfigStore.Save(_config); _overlay.ApplySettings(_config);
        };
        host.Controls.Add(textColor);
        y += rowH;

        var bgColor = new Button { Text = Strings.Ov_BgColor };
        bgColor.SetBounds(x, y, 150, 28);
        bgColor.Click += (_, _) =>
        {
            using var dlg = new ColorDialog { Color = Color.FromArgb(_config.OverlayBgColorArgb), FullOpen = true };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _config.OverlayBgColorArgb = unchecked((int)0xFF000000) | (dlg.Color.ToArgb() & 0x00FFFFFF);
            ConfigStore.Save(_config); _overlay.ApplySettings(_config);
        };
        host.Controls.Add(bgColor);
        y += rowH;

        var opacityLabel = new Label { Text = Strings.Ov_Opacity, AutoSize = true };
        opacityLabel.SetBounds(x, y + 4, 160, 20);
        host.Controls.Add(opacityLabel);
        var opacity = new TrackBar { Minimum = 10, Maximum = 100, Value = Math.Clamp(_config.OverlayOpacity, 10, 100), TickFrequency = 10 };
        opacity.SetBounds(x + 160, y, 220, 30);
        opacity.ValueChanged += (_, _) => { _config.OverlayOpacity = opacity.Value; ConfigStore.Save(_config); _overlay.ApplySettings(_config); };
        host.Controls.Add(opacity);
        y += rowH + 8;

        // Background color + opacity only matter when NOT transparent; enable/disable accordingly.
        void SyncBgEnabled() { bgColor.Enabled = opacity.Enabled = opacityLabel.Enabled = !transparent.Checked; }
        transparent.CheckedChanged += (_, _) =>
        {
            _config.OverlayTransparentBg = transparent.Checked;
            ConfigStore.Save(_config); _overlay.ApplySettings(_config); SyncBgEnabled();
        };
        SyncBgEnabled();

        // Move overlay toggle + hint.
        var moveBtn = new Button { Text = Strings.Ov_Move };
        moveBtn.SetBounds(x, y, 150, 30);
        host.Controls.Add(moveBtn);
        var moveHint = new Label { Text = Strings.Ov_MoveHint, AutoSize = false, ForeColor = Color.DimGray, Visible = false };
        moveHint.SetBounds(x + 160, y + 4, host.ClientSize.Width - x - 170, 40);
        host.Controls.Add(moveHint);
        bool moving = false;
        moveBtn.Click += (_, _) =>
        {
            moving = !moving;
            if (moving && !_config.ShowOverlay) { _config.ShowOverlay = true; ConfigStore.Save(_config); show.Checked = true; _overlay.Show(); }
            _overlay.SetMovable(moving);
            moveBtn.Text = moving ? Strings.Ov_DoneMoving : Strings.Ov_Move;
            moveHint.Visible = moving;
        };
    }

    private void BuildSettingsTab()
    {
        var host = _settingsPage;
        int x = 18, y = 18, gap = 8;

        // Start with Windows.
        _startWithWindows.Text = Strings.Main_StartWithWindows;
        _startWithWindows.AutoSize = true;
        _startWithWindows.SetBounds(x, y, 320, 24);
        _startWithWindows.Checked = StartupRegistration.IsEnabled();
        _startWithWindows.CheckedChanged += OnStartWithWindowsChanged;
        host.Controls.Add(_startWithWindows);
        var startupDesc = MakeDesc(Strings.Settings_StartupDesc, x + 4, y + 24, host.ClientSize.Width - x - 24);
        host.Controls.Add(startupDesc);
        y = startupDesc.Bottom + gap + 12;

        // Auto-start splitting while a Forza game is running (additive to Start with Windows).
        _autoSplit = new CheckBox
        {
            Text = Strings.Settings_AutoSplit,
            AutoSize = true,
            Checked = _config.AutoSplitOnGameDetected,
        };
        _autoSplit.SetBounds(x, y, 420, 24);
        _autoSplit.CheckedChanged += (_, _) =>
        {
            _config.AutoSplitOnGameDetected = _autoSplit.Checked;
            ConfigStore.Save(_config);
        };
        host.Controls.Add(_autoSplit);
        var autoSplitDesc = MakeDesc(Strings.Settings_AutoSplitDesc, x + 4, y + 24, host.ClientSize.Width - x - 24);
        host.Controls.Add(autoSplitDesc);
        y = autoSplitDesc.Bottom + gap + 12;

        // Speed unit.
        var unitLabel = new Label { Text = Strings.Settings_UnitDesc, AutoSize = false, Font = new Font("Segoe UI", 9f) };
        unitLabel.SetBounds(x, y + 3, 340, 20);
        host.Controls.Add(unitLabel);
        _units.DropDownStyle = ComboBoxStyle.DropDownList;
        _units.Items.AddRange(new object[] { "mph", "kph" });
        _units.SelectedIndex = _config.SpeedUnit == SpeedUnit.Kph ? 1 : 0;
        _units.SetBounds(x + 350, y, 70, 24);
        _units.SelectedIndexChanged += (_, _) =>
        {
            _config.SpeedUnit = _units.SelectedIndex == 1 ? SpeedUnit.Kph : SpeedUnit.Mph;
            ConfigStore.Save(_config);
        };
        host.Controls.Add(_units);
        y += 36 + gap;

        // Language.
        var langLabel = new Label { Text = Strings.Settings_LangDesc, AutoSize = false, Font = new Font("Segoe UI", 9f) };
        langLabel.SetBounds(x, y + 3, 340, 20);
        host.Controls.Add(langLabel);
        _language.DropDownStyle = ComboBoxStyle.DropDownList;
        _language.Items.AddRange(new object[] { Strings.Lang_Auto, "English", "日本語", "Français", "Deutsch", "Español" });
        _language.SelectedIndex = Array.IndexOf(_languageOrder, _config.Language) is var li && li >= 0 ? li : 0;
        _language.SetBounds(x + 350, y, 130, 24);
        _language.SelectedIndexChanged += OnLanguageChanged;
        host.Controls.Add(_language);
    }

    private static Label MakeDesc(string text, int x, int y, int width)
    {
        var l = new Label
        {
            Text = text,
            AutoSize = false,
            ForeColor = Color.DimGray,
            Font = new Font("Segoe UI", 8.5f),
        };
        l.SetBounds(x, y, width, 32);
        return l;
    }

    private Button MakeButton(Control host, string text, int x, int y, AnchorStyles anchor)
    {
        var b = new Button { Text = text };
        b.SetBounds(x, y, 78, 28);
        b.Anchor = anchor;
        host.Controls.Add(b);
        return b;
    }

    private void RefreshGrid()
    {
        _grid.Rows.Clear();
        foreach (var d in _config.Destinations)
        {
            int idx = _grid.Rows.Add();
            var row = _grid.Rows[idx];
            row.Cells["Enabled"].Value = d.Enabled;
            row.Cells["Name"].Value = d.Name;
            row.Cells["Ip"].Value = d.Ip;
            row.Cells["Port"].Value = d.Port;
            row.Cells["Count"].Value = Interlocked.Read(ref d.ForwardedCount);
            row.Cells["Status"].Value = ""; // text drawn by OnStatusCellPainting; value used for tooltip
            row.Tag = d;
        }
    }

    /// <summary>Map a destination's status to (color, label) for the dot cell.</summary>
    private static (Color color, string label) StatusVisual(DestinationStatus s) => s switch
    {
        DestinationStatus.Forwarding => (Color.FromArgb(46, 204, 113), Strings.Dot_Forwarding),
        DestinationStatus.Idle       => (Color.FromArgb(241, 196, 15), Strings.Dot_Idle),
        DestinationStatus.Error      => (Color.FromArgb(231, 76, 60),  Strings.Dot_Error),
        _                            => (Color.Gray,                   Strings.Dot_Disabled),
    };

    /// <summary>Owner-draw the Status cell: a shaped dot + text label (color is never the only cue).</summary>
    private void OnStatusCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "Status") return;
        if (_grid.Rows[e.RowIndex].Tag is not Destination d) return;

        var status = _statusCache.TryGetValue(d, out var cs) ? cs
            : (d.Enabled ? DestinationStatus.Idle : DestinationStatus.Disabled);
        var (color, label) = StatusVisual(status);

        e.PaintBackground(e.CellBounds, true);
        var b = e.CellBounds;
        int cy = b.Top + b.Height / 2;
        int dotX = b.Left + 6, dotSize = 10;
        using (var brush = new SolidBrush(color))
        {
            e.Graphics!.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            // Distinct shapes per state (not color alone): filled circle / hollow ring / triangle.
            switch (status)
            {
                case DestinationStatus.Forwarding:
                    e.Graphics.FillEllipse(brush, dotX, cy - dotSize / 2, dotSize, dotSize);
                    break;
                case DestinationStatus.Idle:
                    using (var pen = new Pen(color, 2))
                        e.Graphics.DrawEllipse(pen, dotX, cy - dotSize / 2, dotSize, dotSize);
                    break;
                case DestinationStatus.Error:
                    var tri = new[] {
                        new Point(dotX + dotSize / 2, cy - dotSize / 2),
                        new Point(dotX, cy + dotSize / 2),
                        new Point(dotX + dotSize, cy + dotSize / 2) };
                    e.Graphics.FillPolygon(brush, tri);
                    break;
                default: // Disabled — hollow grey ring
                    using (var pen = new Pen(color, 1.5f))
                        e.Graphics.DrawEllipse(pen, dotX, cy - dotSize / 2, dotSize, dotSize);
                    break;
            }
        }
        TextRenderer.DrawText(e.Graphics!, label, _grid.Font,
            new Rectangle(dotX + dotSize + 6, b.Top, b.Width - dotSize - 12, b.Height),
            _grid.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        e.Handled = true;
    }


    private Destination? Selected =>
        _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].Tag as Destination : null;

    private void OnGridCellClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0) return;
        // Toggle the Enabled checkbox column inline.
        if (_grid.Columns[e.ColumnIndex].Name == "Enabled" &&
            _grid.Rows[e.RowIndex].Tag is Destination d)
        {
            d.Enabled = !d.Enabled;
            _grid.Rows[e.RowIndex].Cells["Enabled"].Value = d.Enabled;
            ApplyDestinations();
        }
    }

    private void AddDestination()
    {
        using var dlg = new DestinationDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (HasPortConflict(dlg.Result, null)) return;
        _config.Destinations.Add(dlg.Result);
        ApplyDestinations();
        RefreshGrid();
    }

    private void EditSelected()
    {
        var d = Selected;
        if (d is null) return;
        using var dlg = new DestinationDialog(d);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        if (HasPortConflict(dlg.Result, d)) return;
        d.Name = dlg.Result.Name;
        d.Ip = dlg.Result.Ip;
        d.Port = dlg.Result.Port;
        d.Enabled = dlg.Result.Enabled;
        ApplyDestinations();
        RefreshGrid();
    }

    private void RemoveSelected()
    {
        var d = Selected;
        if (d is null) return;
        if (MessageBox.Show(this, Strings.Remove_Confirm(d.Name), Strings.Remove_Title,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        _config.Destinations.Remove(d);
        ApplyDestinations();
        RefreshGrid();
    }

    /// <summary>
    /// Warn if two destinations share the same IP:port (a likely mistake), or if a destination
    /// collides with the splitter's own listen port.
    /// </summary>
    private bool HasPortConflict(Destination candidate, Destination? excluding)
    {
        if (string.Equals(candidate.Ip, _config.ListenIp, StringComparison.OrdinalIgnoreCase) &&
            candidate.Port == _config.ListenPort)
        {
            MessageBox.Show(this,
                $"{candidate.Ip}:{candidate.Port} is the splitter's own listen port.\n\n" +
                "A destination must be a DIFFERENT port that your tool listens on.",
                "Port conflict", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return true;
        }

        foreach (var d in _config.Destinations)
        {
            if (ReferenceEquals(d, excluding)) continue;
            if (string.Equals(d.Ip, candidate.Ip, StringComparison.OrdinalIgnoreCase) &&
                d.Port == candidate.Port)
            {
                MessageBox.Show(this,
                    $"Another destination (\"{d.Name}\") already uses {candidate.Ip}:{candidate.Port}.",
                    "Duplicate destination", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return true;
            }
        }
        return false;
    }

    private void ApplyDestinations()
    {
        _engine.SetDestinations(_config.Destinations);
        ConfigStore.Save(_config);
    }

    public void ToggleRunning()
    {
        if (_engine.Running)
        {
            _engine.Stop();
            _autoStarted = false;
            // If the user stops while a game is still detected, honor that for the rest of this game
            // session — the watcher must not immediately auto-restart. Cleared when the game exits.
            if (_autoDetectedGame is not null) _manualStopThisSession = true;
        }
        else
        {
            // A manual Start clears any prior manual-stop hold.
            _manualStopThisSession = false;
            _engine.SetDestinations(_config.Destinations);
            _engine.Start(_config.ListenIp, _config.ListenPort);
        }
        UpdateStartStopButton();
        RunStateChanged?.Invoke();
    }

    private void OnGameDetected(string game)
    {
        _autoDetectedGame = game;
        if (!_config.AutoSplitOnGameDetected) return;
        if (_engine.Running) return;          // already splitting (manual or prior auto)
        if (_manualStopThisSession) return;    // user opted out for this session

        _engine.SetDestinations(_config.Destinations);
        if (_engine.Start(_config.ListenIp, _config.ListenPort))
        {
            _autoStarted = true;
            UpdateStartStopButton();
            RunStateChanged?.Invoke();
            AutoSplitNotice?.Invoke(game);
        }
        // A failed Start (e.g. port in use) already surfaced via the engine's PortInUse/ErrorOccurred
        // events; don't claim an auto-start that didn't happen.
    }

    private void OnGameClosed()
    {
        _autoDetectedGame = null;
        // The game session is over: a fresh launch should be free to auto-start again.
        _manualStopThisSession = false;

        if (!_autoStarted) return; // we didn't start it, so we don't stop it
        _engine.Stop();
        _autoStarted = false;
        UpdateStartStopButton();
        RunStateChanged?.Invoke();
        AutoSplitNotice?.Invoke(null);
    }

    private void UpdateStartStopButton()
    {
        _startStop.Text = _engine.Running ? Strings.Tray_StopSplitting : Strings.Tray_StartSplitting;
    }

    private void OnStartWithWindowsChanged(object? sender, EventArgs e)
    {
        bool ok = _startWithWindows.Checked ? StartupRegistration.Enable() : StartupRegistration.Disable();
        if (!ok)
        {
            // Registry write failed — revert the checkbox to the real state without re-firing.
            _startWithWindows.CheckedChanged -= OnStartWithWindowsChanged;
            _startWithWindows.Checked = StartupRegistration.IsEnabled();
            _startWithWindows.CheckedChanged += OnStartWithWindowsChanged;
        }
    }

    /// <summary>
    /// The folder where logs are written: the user's choice, or a stable per-user default
    /// (%APPDATA%\ForzaTelemetrySplitter\logs). We do NOT use AppContext.BaseDirectory because, for a
    /// single-file published exe, that resolves to a temporary extraction folder Windows can delete —
    /// which would lose the user's logs.
    /// </summary>
    private string LogDir =>
        string.IsNullOrWhiteSpace(_config.LogDirectory)
            ? Path.Combine(ConfigStore.ConfigDirectory, "logs")
            : _config.LogDirectory;

    private void ToggleLogging()
    {
        if (_engine.IsRecording)
        {
            _engine.StopRecording();
        }
        else
        {
            try { Directory.CreateDirectory(LogDir); } catch { }
            string path = Path.Combine(LogDir, $"forza-session-{DateTime.Now:yyyyMMdd-HHmmss}.fts");
            _engine.StartRecording(path);
        }
        UpdateLogButton();
    }

    private void UpdateLogButton()
    {
        _logToggle.Text = _engine.IsRecording ? Strings.Log_Stop : Strings.Log_Start;
        _logToggle.ForeColor = _engine.IsRecording ? Color.Firebrick : SystemColors.ControlText;
    }

    private void ChangeLogFolder()
    {
        using var dlg = new FolderBrowserDialog { SelectedPath = LogDir };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _config.LogDirectory = dlg.SelectedPath;
        ConfigStore.Save(_config);
        _logFolderLabel.Text = Strings.Log_FolderLabel(LogDir);
    }

    private void OpenLogFolder()
    {
        try { Directory.CreateDirectory(LogDir); } catch { }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            { FileName = LogDir, UseShellExecute = true });
        }
        catch { /* ignore */ }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        int idx = _language.SelectedIndex;
        if (idx < 0 || idx >= _languageOrder.Length) return;

        _config.Language = _languageOrder[idx];
        ConfigStore.Save(_config);
        Program.ApplyCulture(_config.Language);

        // Strings already shown on built controls don't retranslate live; a relaunch applies the
        // language everywhere. Tell the user (in the newly-selected language).
        MessageBox.Show(this, Strings.Lang_RestartNote, Strings.Main_Title,
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void UpdateStatus()
    {
        var s = _engine.GetStatus();

        // Refresh per-destination counts + status dots. CurrentStatus() reads _prevForwarded, so
        // invalidate the dot first, THEN update _prevForwarded for the next tick.
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is not Destination d) continue;
            long count = Interlocked.Read(ref d.ForwardedCount);
            bool increased = _prevForwarded.TryGetValue(d, out var prev) && count > prev;
            _statusCache[d] = DestinationStatusLogic.Derive(d.Enabled, s.Receiving, increased, d.LastSendFailed);
            _prevForwarded[d] = count;

            row.Cells["Count"].Value = count;
            row.Cells["Status"].ToolTipText = Strings.Dot_Tooltip(d.Ip, d.Port);
            _grid.InvalidateCell(row.Cells["Status"]);   // repaint the dot from the cached status
        }

        // Activity chart: only repaint when its tab is visible and the window isn't minimized.
        _chart?.SetRunning(s.Running);
        if (_tabs.SelectedTab == _activityPage && WindowState != FormWindowState.Minimized)
            _chart?.Invalidate();

        // Live gear + speed readout, shown when telemetry is flowing.
        string readout = Strings.Readout_Gear(
            SpeedUnitExtensions.FormatGear(s.Gear),
            SpeedUnitExtensions.FormatSpeed(s.SpeedMps, _config.SpeedUnit));

        string text;
        Color dotColor;
        if (!s.Running)
        {
            text = Strings.Status_Stopped;
            dotColor = Color.Gray;
        }
        else if (s.Receiving)
        {
            text = Strings.Status_Connected(
                ForzaPacket.GameName(s.Format),
                s.PacketsPerSecond,
                s.IsRaceOn ? Strings.Status_RaceOn : Strings.Status_RaceOff,
                readout);
            dotColor = Color.FromArgb(46, 204, 113); // green: receiving from the game
        }
        else if (_autoStarted && _autoDetectedGame is { } game)
        {
            // The watcher started us and named the game, but no telemetry has arrived yet. Tell the
            // user the game is seen and we're waiting (commonly: Data Out not turned on in-game),
            // rather than claiming we're splitting when nothing is flowing.
            text = Strings.Status_GameDetectedWaiting(game);
            dotColor = Color.FromArgb(241, 196, 15);  // amber: running, waiting for telemetry
        }
        else
        {
            text = Strings.Status_WaitingForForza(_config.ListenIp, _config.ListenPort);
            dotColor = Color.FromArgb(241, 196, 15);  // amber: running, waiting for the game
        }
        _status.Text = text;
        if (_statusDotColor != dotColor) { _statusDotColor = dotColor; _statusDot.Invalidate(); }
        UpdateLogButton();

        _overlay.SetStatus(s.Receiving, readout);
    }

    private void OnEngineError(string message)
    {
        // Marshal to UI thread.
        if (InvokeRequired) { BeginInvoke(new Action<string>(OnEngineError), message); return; }

        UpdateStartStopButton();
        RunStateChanged?.Invoke();
        MessageBox.Show(this, message, Strings.Main_Title,
            MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private void OnPortInUse(int port)
    {
        if (InvokeRequired) { BeginInvoke(new Action<int>(OnPortInUse), port); return; }

        UpdateStartStopButton();
        RunStateChanged?.Invoke();
        MessageBox.Show(this, Strings.Error_PortInUse(port), Strings.Main_Title,
            MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing via the X hides to tray instead of exiting.
        if (!ReallyExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _uiTimer.Stop();
        _gameWatcher.Dispose();
        base.OnFormClosing(e);
    }
}
