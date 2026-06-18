using ForzaTelemetrySplitter.Core;
using ForzaTelemetrySplitter.Resources;

namespace ForzaTelemetrySplitter.UI;

/// <summary>
/// Add/edit dialog for a single companion app: a display name, the path to an .exe or .bat (chosen
/// via a file picker), optional arguments, and an enabled toggle. Mirrors
/// <see cref="DestinationDialog"/>. A note appears for .bat paths because batch files can't be
/// de-duplicated against running processes and may relaunch on each splitter start.
/// </summary>
public sealed class CompanionDialog : Form
{
    private readonly TextBox _name = new();
    private readonly TextBox _path = new();
    private readonly TextBox _arguments = new();
    private readonly CheckBox _enabled = new();
    private readonly Label _batNote = new();

    /// <summary>The edited companion, valid only when DialogResult == OK.</summary>
    public Companion Result { get; private set; } = new();

    public CompanionDialog(Companion? existing = null)
    {
        Text = existing is null ? Strings.Comp_AddTitle : Strings.Comp_EditTitle;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(440, 230);
        Font = new Font("Segoe UI", 9f);

        int labelX = 14, fieldX = 96, fieldW = 330, y = 16, rowH = 34;

        AddLabel(Strings.Comp_Name, labelX, y);
        _name.SetBounds(fieldX, y - 3, fieldW, 24);
        Controls.Add(_name);
        y += rowH;

        AddLabel(Strings.Comp_Path, labelX, y);
        _path.SetBounds(fieldX, y - 3, fieldW - 90, 24);
        _path.TextChanged += (_, _) => UpdateBatNote();
        Controls.Add(_path);
        var browse = new Button { Text = Strings.Comp_Browse };
        browse.SetBounds(fieldX + fieldW - 84, y - 4, 84, 26);
        browse.Click += OnBrowse;
        Controls.Add(browse);
        y += rowH;

        AddLabel(Strings.Comp_Arguments, labelX, y);
        _arguments.SetBounds(fieldX, y - 3, fieldW, 24);
        Controls.Add(_arguments);
        y += rowH;

        _enabled.Text = Strings.Comp_Enabled;
        _enabled.SetBounds(fieldX, y, 120, 24);
        Controls.Add(_enabled);
        y += rowH;

        _batNote.SetBounds(labelX, y, ClientSize.Width - 28, 36);
        _batNote.ForeColor = Color.DimGray;
        _batNote.Font = new Font("Segoe UI", 8f, FontStyle.Italic);
        _batNote.Visible = false;
        Controls.Add(_batNote);

        var ok = new Button { Text = Strings.Dest_Ok, DialogResult = DialogResult.None };
        ok.SetBounds(ClientSize.Width - 180, ClientSize.Height - 36, 80, 26);
        ok.Click += OnOk;
        Controls.Add(ok);

        var cancel = new Button { Text = Strings.Dest_Cancel, DialogResult = DialogResult.Cancel };
        cancel.SetBounds(ClientSize.Width - 92, ClientSize.Height - 36, 80, 26);
        Controls.Add(cancel);

        AcceptButton = ok;
        CancelButton = cancel;

        if (existing is not null)
        {
            _name.Text = existing.Name;
            _path.Text = existing.Path;
            _arguments.Text = existing.Arguments;
            _enabled.Checked = existing.Enabled;
        }
        else
        {
            _enabled.Checked = true;
        }
        UpdateBatNote();
    }

    private void AddLabel(string text, int x, int y)
    {
        var l = new Label { Text = text, AutoSize = true };
        l.SetBounds(x, y, 80, 20);
        Controls.Add(l);
    }

    private void OnBrowse(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title = Strings.Comp_AddTitle,
            Filter = Strings.Comp_FileFilter,
            CheckFileExists = true,
        };
        // Seed the dialog from the current path if it points at a real folder.
        try
        {
            var dir = Path.GetDirectoryName(_path.Text.Trim());
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
        }
        catch { /* invalid path text — ignore and open the default location */ }

        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _path.Text = dlg.FileName;
        if (string.IsNullOrWhiteSpace(_name.Text))
            _name.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
    }

    private void UpdateBatNote()
    {
        bool isBat = _path.Text.Trim().EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        _batNote.Text = isBat ? Strings.Comp_BatNote : "";
        _batNote.Visible = isBat;
    }

    private void OnOk(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            MessageBox.Show(this, Strings.Comp_MissingName, Strings.Main_Title,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(_path.Text))
        {
            MessageBox.Show(this, Strings.Comp_MissingPath, Strings.Main_Title,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Existence is intentionally NOT checked here: a tool may be installed later, and a missing
        // path surfaces as a clear, non-fatal balloon at launch time instead.
        Result = new Companion
        {
            Name = _name.Text.Trim(),
            Path = _path.Text.Trim(),
            Arguments = _arguments.Text.Trim(),
            Enabled = _enabled.Checked,
        };
        DialogResult = DialogResult.OK;
        Close();
    }
}
