using System.Globalization;
using RiosvImports.App.Controls;
using RiosvImports.Core.Localization;
using RiosvImports.Data;

namespace RiosvImports.App.Shell;

/// <summary>
/// Внесените файлове и махането на цяло внасяне.
///
/// Внасянето е единственото действие, при което в базата влизат стотици редове
/// наведнъж, и единственото, чиято грешка не се поправя ред по ред. Същият
/// файл, внесен два пъти, не личи по нито един ред — всеки от тях изглежда
/// правилен и само сборът излиза двоен; файл, внесен в чужда фирма, е цял
/// правилен, но не на това място. И в двата случая поправката е една и съща:
/// махни всичко, дошло с този файл.
///
/// Затова списъкът е по ФАЙЛОВЕ, а не по редове. Редовете се поправят другаде.
/// </summary>
internal sealed class ImportsDialog : Dialog
{
    private readonly Workspace _workspace;
    private readonly DataGridView _grid = new();
    private readonly Label _summary = new();
    private readonly AppButton _remove;

    private List<ImportRecord> _imports = [];

    public ImportsDialog(Workspace workspace)
    {
        _workspace = workspace;

        Text = Lang.T("Внесени файлове", "Imported files");
        Font = Theme.Base;
        BackColor = Theme.Canvas;
        MinimumSize = new Size(Theme.Scaled(880), Theme.Scaled(460));
        ClientSize = new Size(Theme.Scaled(1080), Theme.Scaled(560));
        MaximizeBox = false;
        KeyPreview = true;

        _remove = Theme.DangerButton(Lang.T("Махни внесеното", "Remove the import"), Theme.Glyph.Delete);

        Controls.Add(BuildBody());
        Controls.Add(BuildSummary());
        Controls.Add(BuildTitle());
        Controls.Add(BuildButtons());

        Fill();

        Theme.Apply(this);
    }

    /// <summary>Дали нещо е махнато — страниците се обновяват само тогава.</summary>
    public bool Changed { get; private set; }

    // ------------------------------------------------------------- устройство

    private Control BuildTitle()
    {
        var host = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(68),
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, Theme.Gap, Theme.GapLarge, 0)
        };

        host.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = Lang.T(
                "Махането отнася всичко, дошло с файла — движенията, партидите от тях и отчетите. " +
                "Записаното на ръка не се пипа.",
                "Removing an import takes away everything that came with the file — the movements, " +
                "their batches and the reports. Anything entered by hand is left alone."),
            Font = Theme.Base,
            ForeColor = Theme.InkMuted
        });

        host.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(34),
            Text = Lang.T("Внесени файлове", "Imported files"),
            Font = Theme.Title,
            ForeColor = Theme.Ink
        });

        return host;
    }

    private Control BuildSummary()
    {
        var host = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Base.Height + 16,
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, 0, Theme.GapLarge, Theme.GapSmall)
        };

        _summary.Dock = DockStyle.Fill;
        _summary.Font = Theme.Bold;
        _summary.ForeColor = Theme.InkMuted;

        host.Controls.Add(_summary);

        return host;
    }

    private Control BuildBody()
    {
        var host = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, 0, Theme.GapLarge, Theme.Gap)
        };

        BuildGrid();

        var card = new Card
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(1),
            Fill = Theme.Canvas,
            Outline = Theme.Border
        };

        _grid.Dock = DockStyle.Fill;
        card.Controls.Add(_grid);
        host.Controls.Add(card);

        return host;
    }

    private void BuildGrid()
    {
        _grid.Font = Theme.Base;
        _grid.BackgroundColor = Theme.Canvas;
        _grid.BorderStyle = BorderStyle.None;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;

        _grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.Surface;
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.InkStrong;
        _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.Surface;
        _grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Theme.InkStrong;
        _grid.ColumnHeadersDefaultCellStyle.Font = Theme.Bold;
        _grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 6, 6, 6);

        _grid.DefaultCellStyle.BackColor = Theme.Canvas;
        _grid.DefaultCellStyle.ForeColor = Theme.Ink;
        _grid.DefaultCellStyle.SelectionBackColor = Theme.AccentLight;
        _grid.DefaultCellStyle.SelectionForeColor = Theme.Ink;
        _grid.DefaultCellStyle.Padding = new Padding(6, 4, 6, 4);
        _grid.AlternatingRowsDefaultCellStyle.BackColor = Theme.Surface;
        _grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = Theme.AccentLight;
        _grid.AlternatingRowsDefaultCellStyle.SelectionForeColor = Theme.Ink;
        _grid.GridColor = Theme.Border;

        var line = Math.Max(1, _grid.Font.Height);
        _grid.RowTemplate.Height = line + 14;
        _grid.ColumnHeadersHeight = line + 22;

        Column("when", Lang.T("Кога", "When"), 132);
        Column("file", Lang.T("Файл", "File"), 260, fill: true);
        Column("source", Lang.T("Откъде", "Source"), 150);
        Column("company", Lang.T("Фирма", "Company"), 190);
        Column("what", Lang.T("Какво носи", "What it holds"), 230);

        _grid.SelectionChanged += (_, _) => Refresh(Selected());
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0) Remove();
        };
    }

    private void Column(string name, string header, int width, bool fill = false)
    {
        var column = new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            Width = width,
            SortMode = DataGridViewColumnSortMode.NotSortable
        };

        if (fill)
        {
            column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            column.MinimumWidth = Theme.Scaled(200);
        }

        _grid.Columns.Add(column);
    }

    private Control BuildButtons()
    {
        var host = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = Theme.Base.Height + 30,
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, 0, Theme.GapLarge, Theme.Gap)
        };

        var close = Theme.SecondaryButton(Lang.T("Затвори", "Close"));
        close.Dock = DockStyle.Right;
        close.Height = Theme.Base.Height + 16;
        close.FitWidth(16);
        close.Click += (_, _) => DialogResult = DialogResult.OK;

        _remove.Dock = DockStyle.Left;
        _remove.Height = Theme.Base.Height + 16;
        _remove.FitWidth(16);
        _remove.Enabled = false;
        _remove.Click += (_, _) => Remove();

        host.Controls.Add(close);
        host.Controls.Add(_remove);

        CancelButton = close;

        return host;
    }

    // ------------------------------------------------------------- пълнене

    private void Fill()
    {
        _imports = [.. _workspace.History.List()];

        var companies = _workspace.Companies.List().ToDictionary(c => c.Id, c => c.Name);

        _grid.Rows.Clear();

        foreach (var import in _imports)
        {
            var index = _grid.Rows.Add();
            var row = _grid.Rows[index];

            row.Cells["when"].Value = import.CreatedUtc.ToLocalTime()
                .ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

            row.Cells["file"].Value = import.FileName.Length > 0
                ? import.FileName
                : Lang.T("(без име)", "(no name)");

            row.Cells["source"].Value = import.SourceLabel;

            row.Cells["company"].Value = import.CompanyId is { } id && companies.TryGetValue(id, out var name)
                ? name
                : Lang.T("—", "—");

            row.Cells["what"].Value = import.Note.Length > 0
                ? import.Summary + " · " + import.Note
                : import.Summary;

            // Изчерпано внасяне остава в списъка, защото казва, че файлът е
            // минавал оттук — но не обещава махане, каквото няма да стане.
            if (import.Empty) row.DefaultCellStyle.ForeColor = Theme.InkMuted;
        }

        _grid.ClearSelection();
        _grid.CurrentCell = null;

        Refresh(null);
    }

    private ImportRecord? Selected()
    {
        if (_grid.CurrentRow is not { Index: >= 0 and var index } || index >= _imports.Count) return null;

        return _imports[index];
    }

    private void Refresh(ImportRecord? import)
    {
        // И изчерпаното внасяне се маха — но от списъка, не от склада. Ред,
        // чийто бутон не прави нищо, кара човека да го натисне втори път.
        _remove.Enabled = import is not null;

        _remove.Text = import is { Empty: true }
            ? Lang.T("Махни от списъка", "Remove from the list")
            : Lang.T("Махни внесеното", "Remove the import");

        if (_imports.Count == 0)
        {
            _summary.Text = Lang.T(
                "Още няма внесени файлове. Внасянето от Excel се появява тук само̀.",
                "No files imported yet. Imports from Excel show up here on their own.");

            return;
        }

        if (import is null)
        {
            _summary.Text = Lang.T($"{_imports.Count} внасяния. Изберете едно, за да го махнете.",
                                   $"{_imports.Count} imports. Pick one to remove it.");

            return;
        }

        if (import.Empty)
        {
            _summary.Text = Lang.T("От това внасяне вече не е останало нищо.",
                                   "Nothing is left from this import.");

            return;
        }

        _summary.Text = Lang.T($"Ще се махнат: {import.Summary}.",
                               $"To be removed: {import.Summary}.");
    }

    // ------------------------------------------------------------- махане

    /// <summary>
    /// Пита и маха.
    ///
    /// Питането изрежда какво си отива и какво не може — и двете идват от
    /// <see cref="ImportHistory.Check"/>, а не се съчиняват тук: същият отговор
    /// ще трябва и на второ място, а две негови копия щяха да се разминат.
    /// </summary>
    private void Remove()
    {
        if (Selected() is not { } import) return;

        if (import.Empty)
        {
            Forget(import);
            return;
        }

        if (_workspace.History.Check(import.Id) is not { } check) return;

        if (!check.Allowed)
        {
            MessageBox.Show(this,
                string.Join(Environment.NewLine, check.Blocked),
                Lang.T("Внесеното не може да се махне", "The import cannot be removed"),
                MessageBoxButtons.OK, MessageBoxIcon.Warning);

            return;
        }

        var question = Lang.T(
            $"Да се махне ли всичко, дошло с „{import.FileName}“?",
            $"Remove everything that came with “{import.FileName}”?") +
            Environment.NewLine + Environment.NewLine +
            Lang.T($"Ще си отидат: {import.Summary}.", $"Going away: {import.Summary}.");

        if (check.Warnings.Count > 0)
            question += Environment.NewLine + Environment.NewLine +
                        string.Join(Environment.NewLine, check.Warnings);

        var answer = MessageBox.Show(this, question,
            Lang.T("Махане на внесеното", "Removing an import"),
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes) return;

        var undone = _workspace.History.Undo(import.Id);

        if (!undone.Done)
        {
            MessageBox.Show(this,
                string.Join(Environment.NewLine, undone.Blocked),
                Lang.T("Внесеното не може да се махне", "The import cannot be removed"),
                MessageBoxButtons.OK, MessageBoxIcon.Warning);

            return;
        }

        Changed = true;
        Fill();

        var said = Lang.T($"Махнато: {undone.Summary}.", $"Removed: {undone.Summary}.");

        if (undone.Kept.Count > 0)
            said += Environment.NewLine + Environment.NewLine +
                    string.Join(Environment.NewLine, undone.Kept.Take(5));

        MessageBox.Show(this, said, Lang.T("Готово", "Done"),
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// Маха реда на внасяне, зад което вече не е останало нищо.
    ///
    /// Само следата: записите им ги няма отдавна — махнати са на ръка, с
    /// фирмата си или с предишно махане.
    /// </summary>
    private void Forget(ImportRecord import)
    {
        var answer = MessageBox.Show(this,
            Lang.T(
                $"От „{import.FileName}“ вече не е останало нищо в базата. " +
                "Да се махне ли редът от списъка?",
                $"Nothing is left in the database from “{import.FileName}”. " +
                "Remove the row from the list?"),
            Lang.T("Махане от списъка", "Removing from the list"),
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes) return;

        _workspace.History.Forget(import.Id);
        Fill();
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keys)
    {
        if (keys == Keys.Escape)
        {
            DialogResult = DialogResult.OK;
            return true;
        }

        return base.ProcessCmdKey(ref message, keys);
    }
}
