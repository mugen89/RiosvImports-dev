using System.Globalization;
using RiosvImports.App.Controls;
using RiosvImports.Core.Ledger;
using RiosvImports.Core.Localization;
using RiosvImports.Data;

namespace RiosvImports.App.Shell;

/// <summary>
/// Затварянето и отварянето на месеци — на едно място, далеч от всекидневната
/// работа.
///
/// Затварянето е решение, което се взима веднъж на месец: „този месец е
/// прегледан и приет такъв“. Копчето му обаче стоеше в „Месец“, до онова, което
/// се пипа по двайсет пъти на ден, и се натискаше по невнимание — а затворен
/// месец после отказва всеки запис.
///
/// Затова е тук: в настройките, където се влиза нарочно. Кое е затворено
/// продължава да се вижда в „Месец“ — там то е отговор на въпроса „защо не
/// приема“, а не копче.
/// </summary>
internal sealed class PeriodsDialog : Dialog
{
    private readonly Workspace _workspace;
    private readonly CompanyScope _scope;

    private readonly DataGridView _grid = new();
    private readonly Label _summary = new();
    private readonly AppButton _lock;

    private List<Row> _rows = [];

    /// <summary>Един месец на една площадка — както се показва и затваря.</summary>
    private sealed record Row(string Stage, LedgerPeriod Period, bool Closed, DateTime? On, string Note);

    public PeriodsDialog(Workspace workspace, CompanyScope scope)
    {
        _workspace = workspace;
        _scope = scope;

        Text = Lang.T("Затворени месеци", "Closed months");
        Font = Theme.Base;
        BackColor = Theme.Canvas;
        MinimumSize = new Size(Theme.Scaled(820), Theme.Scaled(460));
        ClientSize = new Size(Theme.Scaled(1000), Theme.Scaled(560));
        MaximizeBox = false;
        KeyPreview = true;

        _lock = Theme.SecondaryButton(Lang.T("Затвори месеца", "Close the month"));

        Controls.Add(BuildBody());
        Controls.Add(BuildSummary());
        Controls.Add(BuildTitle());
        Controls.Add(BuildButtons());

        Fill();

        Theme.Apply(this);
    }

    /// <summary>Дали нещо е затворено или отворено — страниците се обновяват само тогава.</summary>
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
                "Затворен месец не приема нови записи и не се поправя. Отваря се наново по всяко време.",
                "A closed month accepts no new entries and cannot be edited. It can be reopened at any time."),
            Font = Theme.Base,
            ForeColor = Theme.InkMuted
        });

        host.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(34),
            Text = _scope.Titled(Lang.T("Затворени месеци", "Closed months")),
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

        Column("stage", Lang.T("Площадка", "Site"), 110);
        Column("month", Lang.T("Месец", "Month"), 180);
        Column("state", Lang.T("Състояние", "State"), 140);
        Column("when", Lang.T("Затворен на", "Closed on"), 150);
        Column("note", Lang.T("Прието с бележки", "Accepted with notes"), 300, fill: true);

        _grid.SelectionChanged += (_, _) => Refresh(Selected());
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0) Switch();
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

        var close = Theme.SecondaryButton(Lang.T("Затвори прозореца", "Close the window"));
        close.Dock = DockStyle.Right;
        close.Height = Theme.Base.Height + 16;
        close.FitWidth(16);
        close.Click += (_, _) => DialogResult = DialogResult.OK;

        _lock.Dock = DockStyle.Left;
        _lock.Height = Theme.Base.Height + 16;
        _lock.FitWidth(16);
        _lock.Enabled = false;
        _lock.Click += (_, _) => Switch();

        host.Controls.Add(close);
        host.Controls.Add(_lock);

        CancelButton = close;

        return host;
    }

    // ------------------------------------------------------------- пълнене

    /// <summary>
    /// Месеците на фирмата — по площадки.
    ///
    /// Показват се тези с движение и вече затворените: месец без нито един ред
    /// няма какво да пази, а затворен без движение е решение и си стои.
    /// </summary>
    private void Fill()
    {
        _rows = [];

        if (_scope.Current is { } companyId)
        {
            foreach (var stage in _workspace.Ledger.Stages(companyId))
            {
                var closed = _workspace.Periods.Closed(companyId, stage)
                    .ToDictionary(c => c.Period, c => c);

                var months = new HashSet<LedgerPeriod>(_workspace.Ledger.Periods(companyId, stage));

                foreach (var period in closed.Keys) months.Add(period);

                foreach (var period in months.OrderByDescending(m => m))
                    _rows.Add(closed.TryGetValue(period, out var mark)
                        ? new Row(stage, period, true, mark.ClosedUtc, mark.Note)
                        : new Row(stage, period, false, null, string.Empty));
            }
        }

        _grid.Rows.Clear();

        foreach (var row in _rows)
        {
            var index = _grid.Rows.Add();
            var line = _grid.Rows[index];

            line.Cells["stage"].Value = row.Stage;
            line.Cells["month"].Value = row.Period.Title();
            line.Cells["state"].Value = row.Closed
                ? Lang.T("Затворен", "Closed")
                : Lang.T("Отворен", "Open");

            line.Cells["when"].Value = row.On is { } on
                ? on.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)
                : string.Empty;

            line.Cells["note"].Value = row.Note;

            if (row.Closed) line.Cells["state"].Style.ForeColor = Theme.Warn;
        }

        _grid.ClearSelection();
        _grid.CurrentCell = null;

        Refresh(null);
    }

    private Row? Selected()
    {
        if (_grid.CurrentRow is not { Index: >= 0 and var index } || index >= _rows.Count) return null;

        return _rows[index];
    }

    private void Refresh(Row? row)
    {
        _lock.Enabled = row is not null;

        _lock.Text = row is { Closed: true }
            ? Lang.T("Отвори месеца", "Reopen the month")
            : Lang.T("Затвори месеца", "Close the month");

        _lock.FitWidth(16);

        if (_scope.Current is null)
        {
            _summary.Text = Lang.T("Заведете фирма в „Настройки“, за да има какво да се затваря.",
                                   "Add a company under Settings first.");
            return;
        }

        var closed = _rows.Count(r => r.Closed);

        _summary.Text = _rows.Count == 0
            ? Lang.T("Още няма месец с движение.", "No month has any movement yet.")
            : Lang.T($"{_rows.Count} месеца · {closed} затворени",
                     $"{_rows.Count} months · {closed} closed");
    }

    // ------------------------------------------------------------- решението

    /// <summary>
    /// Затваря или отваря избрания месец.
    ///
    /// Затварянето минава през същите контроли, които пазят и отчета: спиращото
    /// не се прескача. Отварянето не пита нищо освен потвърждение — грешка се
    /// намира и след затварянето, а единственият друг изход би бил да се поправя
    /// със записи в следващия месец.
    /// </summary>
    private void Switch()
    {
        if (Selected() is not { } row) return;
        if (_scope.Current is not { } companyId) return;

        if (row.Closed)
        {
            var answer = MessageBox.Show(this,
                Lang.T($"Да се отвори ли наново {row.Period.Title()}? Движенията в него отново ще се пипат.",
                       $"Reopen {row.Period.Title()}? Its movements will become editable again."),
                Lang.T("Отваряне на месец", "Reopen a month"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (answer != DialogResult.Yes) return;

            _workspace.Periods.Reopen(companyId, row.Stage, row.Period);

            Changed = true;
            Fill();

            return;
        }

        var issues = LedgerChecks.For(_workspace, companyId, row.Stage, row.Period);
        var blocking = issues.Where(i => i.Blocks).ToList();

        if (blocking.Count > 0)
        {
            MessageBox.Show(this,
                Lang.T($"{row.Period.Title()} не може да се затвори, докато това стои:",
                       $"{row.Period.Title()} cannot be closed while this stands:") +
                Environment.NewLine + Environment.NewLine +
                string.Join(Environment.NewLine, blocking.Take(8).Select(i => "· " + i.Text)),
                Lang.T("Месецът не е готов", "The month is not ready"),
                MessageBoxButtons.OK, MessageBoxIcon.Warning);

            return;
        }

        var notes = issues.Select(i => i.Text).ToList();

        var question = Lang.T($"Да се затвори ли {row.Period.Title()}?", $"Close {row.Period.Title()}?");

        if (notes.Count > 0)
            question += Environment.NewLine + Environment.NewLine +
                        Lang.T($"Приема се с {notes.Count} бележки:", $"Accepted with {notes.Count} notes:") +
                        Environment.NewLine +
                        string.Join(Environment.NewLine, notes.Take(5).Select(n => "· " + n));

        if (MessageBox.Show(this, question,
                Lang.T("Затваряне на месец", "Close a month"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        _workspace.Periods.Close(companyId, row.Stage, row.Period,
            string.Join(" · ", notes.Take(5)));

        Changed = true;
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

    // ------------------------------------------------------------- за теста

    internal int RowCount => _rows.Count;

    internal string SummaryText => _summary.Text;

    /// <summary>Избира ред, както би го избрал човек.</summary>
    internal void Pick(int row)
    {
        if (row < 0 || row >= _grid.Rows.Count) return;

        _grid.CurrentCell = _grid.Rows[row].Cells[0];
        Refresh(Selected());
    }

    /// <summary>Избира реда на този месец. Връща дали го има.</summary>
    internal bool Pick(string stage, LedgerPeriod period)
    {
        var at = _rows.FindIndex(r => r.Stage == stage && r.Period == period);

        if (at < 0) return false;

        Pick(at);
        return true;
    }

    /// <summary>Какво предлага копчето за избрания ред.</summary>
    internal string LockText => _lock.Text;

    internal bool LockOffered => _lock.Enabled;

    /// <summary>Затваря месеца на избрания ред, без да пита — ползва се от теста.</summary>
    internal bool CloseSelected()
    {
        if (Selected() is not { Closed: false } row) return false;
        if (_scope.Current is not { } companyId) return false;

        var issues = LedgerChecks.For(_workspace, companyId, row.Stage, row.Period);
        if (issues.Any(i => i.Blocks)) return false;

        _workspace.Periods.Close(companyId, row.Stage, row.Period,
            string.Join(" · ", issues.Select(i => i.Text).Take(5)));

        Changed = true;
        Fill();

        return true;
    }

    /// <summary>Отваря месеца на избрания ред, без да пита — ползва се от теста.</summary>
    internal bool ReopenSelected()
    {
        if (Selected() is not { Closed: true } row) return false;
        if (_scope.Current is not { } companyId) return false;

        _workspace.Periods.Reopen(companyId, row.Stage, row.Period);

        Changed = true;
        Fill();

        return true;
    }
}
