using System.Globalization;
using RiosvImports.App.Controls;
using RiosvImports.Core.Localization;
using RiosvImports.Core.Schema;
using RiosvImports.Core.Values;
using RiosvImports.Data;

namespace RiosvImports.App.Shell;

/// <summary>
/// Всички начални количества на едно място — само те, без движенията от
/// отчетите.
///
/// В баланса началното количество стои в една колона до дванайсет други и се
/// губи. Тук се вижда само то: кой код, за коя фирма, за коя година и колко.
/// Точно този изглед отговаря на въпроса „нали не съм въвел нещо, без да
/// забележа“ — а той се задава, защото количествата се внасят от файл и никой
/// не помни какво е имало в него преди три месеца.
///
/// Колоната „Движение“ е второто половин изречение на същия въпрос: начално
/// количество по код, по който през цялата година не е мръднало нищо, или е
/// залежал отпадък, или е сгрешен ред. И двете си струва да се видят.
///
/// Прозорецът пипа само таблицата с началните количества. Отчетите не се
/// докосват — оттам идват единствено числата в справочната колона.
/// </summary>
internal sealed class OpeningsDialog : Dialog
{
    private const string CompanyColumn = "company";
    private const string YearColumn = "year";
    private const string CodeColumn = "ewc";
    private const string NameColumn = "name";
    private const string AmountColumn = "amount";
    private const string MovedColumn = "moved";

    private readonly DataGridView _grid = new();
    private readonly Label _summary = new();

    private readonly IReadOnlyList<Company> _companies;
    private readonly IReadOnlyDictionary<string, string> _names;

    /// <summary>
    /// Движението по код за дадена фирма и година — попълва справочната колона.
    ///
    /// null значи, че за тази фирма и година изобщо няма проверен отчет. Това
    /// не е същото като „нищо не е мръднало“: годината просто още не е
    /// работена и няма какво да се подозира.
    /// </summary>
    private readonly Func<long, int, IReadOnlyDictionary<string, decimal>?> _movement;

    private readonly Dictionary<(long, int), IReadOnlyDictionary<string, decimal>?> _cache = [];

    public OpeningsDialog(
        IReadOnlyList<Opening> openings,
        IReadOnlyList<Company> companies,
        IReadOnlyDictionary<string, string> names,
        Func<long, int, IReadOnlyDictionary<string, decimal>?> movement)
    {
        _companies = companies;
        _names = names;
        _movement = movement;

        Text = Lang.T("Начални количества", "Opening quantities");
        Font = Theme.Base;
        BackColor = Theme.Canvas;
        MinimumSize = new Size(Theme.Scaled(880), Theme.Scaled(500));
        ClientSize = new Size(Theme.Scaled(1040), Theme.Scaled(620));
        KeyPreview = true;

        Controls.Add(BuildGrid());
        Controls.Add(BuildSummary());
        Controls.Add(BuildTitle());
        Controls.Add(BuildButtons());

        Fill(openings);
        Describe();

        Theme.Apply(this);
    }

    /// <summary>Иска се шаблон за начални количества.</summary>
    public event EventHandler? TemplateRequested;

    /// <summary>Иска се внасяне от Excel. Прозорецът се затваря — списъкът вече е друг.</summary>
    public event EventHandler? ImportRequested;

    /// <summary>Каквото е останало в таблицата след „Запиши“.</summary>
    public IReadOnlyList<Opening> Result { get; private set; } = [];

    // ------------------------------------------------------------- устройство

    private Control BuildTitle()
    {
        var host = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(64),
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, Theme.Gap, Theme.GapLarge, 0)
        };

        host.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = Lang.T(
                "Само началните количества, без движенията от отчетите. Количество 0 маха реда.",
                "Only the opening quantities, without the movements from the reports. A quantity of 0 removes the row."),
            Font = Theme.Base,
            ForeColor = Theme.InkMuted
        });

        host.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(34),
            Text = Lang.T("Начални количества", "Opening quantities"),
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
            Height = Theme.Base.Height + 18,
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, 0, Theme.GapLarge, Theme.GapSmall)
        };

        _summary.Dock = DockStyle.Fill;
        _summary.Font = Theme.Bold;
        _summary.ForeColor = Theme.InkMuted;

        host.Controls.Add(_summary);

        return host;
    }

    private Control BuildGrid()
    {
        var host = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, 0, Theme.GapLarge, Theme.Gap)
        };

        var frame = new Card
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(1),
            Fill = Theme.Canvas,
            Outline = Theme.Border
        };

        _grid.Dock = DockStyle.Fill;
        _grid.Font = Theme.Base;
        _grid.BackgroundColor = Theme.Canvas;
        _grid.BorderStyle = BorderStyle.None;
        _grid.EnableHeadersVisualStyles = false;
        _grid.AllowUserToAddRows = true;
        _grid.AllowUserToDeleteRows = true;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersWidth = Theme.Scaled(32);
        _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
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
        _grid.RowHeadersDefaultCellStyle.BackColor = Theme.Surface;
        _grid.RowHeadersDefaultCellStyle.ForeColor = Theme.InkMuted;
        _grid.GridColor = Theme.Border;

        var line = Math.Max(1, _grid.Font.Height);
        _grid.RowTemplate.Height = line + 14;
        _grid.ColumnHeadersHeight = line + 22;

        var company = new DataGridViewComboBoxColumn
        {
            Name = CompanyColumn,
            HeaderText = Lang.T("Фирма", "Company"),
            Width = Theme.Scaled(230),
            FlatStyle = FlatStyle.Flat,
            SortMode = DataGridViewColumnSortMode.NotSortable
        };

        company.Items.AddRange([.. _companies.Select(c => (object)c.Display)]);

        _grid.Columns.Add(company);

        Column(YearColumn, Lang.T("Година", "Year"), 84, right: true);
        Column(CodeColumn, Lang.T("Код", "Code"), 96);
        Column(NameColumn, Lang.T("Наименование", "Description"), 260, locked: true, fill: true);
        Column(AmountColumn, Lang.T("Начално, тон", "Opening, t"), 118, right: true);
        Column(MovedColumn, Lang.T("Движение за годината", "Movement that year"), 196, locked: true, right: true);

        _grid.CellEndEdit += (_, e) => Touched(e.RowIndex);
        _grid.RowsRemoved += (_, _) => Describe();

        // Падащият списък не търпи стойност извън себе си, а таблицата се пълни
        // и от файл. Вместо да пада, клетката остава празна и се вижда празна.
        _grid.DataError += (_, e) => e.ThrowException = false;

        frame.Controls.Add(_grid);
        host.Controls.Add(frame);

        return host;
    }

    private void Column(string name, string header, int width, bool locked = false, bool right = false, bool fill = false)
    {
        var column = new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            Width = width,
            ReadOnly = locked,
            SortMode = DataGridViewColumnSortMode.NotSortable
        };

        if (locked) column.DefaultCellStyle.ForeColor = Theme.InkMuted;

        if (right)
        {
            column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
        }

        if (fill)
        {
            column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            column.MinimumWidth = Theme.Scaled(180);
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

        var tools = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Theme.Canvas
        };

        var save = Theme.PrimaryButton(Lang.T("Запиши", "Save"));
        save.Height = Theme.Base.Height + 16;
        save.Click += (_, _) => _ = Accept();

        var cancel = Theme.SecondaryButton(Lang.T("Отказ", "Cancel"));
        cancel.Height = save.Height;
        cancel.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;

        tools.Controls.Add(save.FitWidth(16));
        tools.Controls.Add(cancel.FitWidth(16));

        var files = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Canvas
        };

        files.Controls.Add(Small(Lang.T("Шаблон", "Template"), () =>
        {
            TemplateRequested?.Invoke(this, EventArgs.Empty);
        }));

        files.Controls.Add(Small(Lang.T("Внеси от Excel", "Import from Excel"), () =>
        {
            // Внасянето замества целия списък — задържан отворен, прозорецът
            // би показвал вчерашното.
            DialogResult = DialogResult.Cancel;
            ImportRequested?.Invoke(this, EventArgs.Empty);
        }));

        host.Controls.Add(tools);
        host.Controls.Add(files);

        AcceptButton = save;
        CancelButton = cancel;

        return host;
    }

    private static Control Small(string text, Action handler)
    {
        var button = Theme.GhostButton(text, Theme.Glyph.Excel);
        button.Height = Theme.Base.Height + 16;
        button.Margin = new Padding(0, 0, Theme.GapSmall, 0);
        button.Click += (_, _) => handler();

        return button.FitWidth(12);
    }

    // ------------------------------------------------------------- пълнене

    private void Fill(IReadOnlyList<Opening> openings)
    {
        foreach (var opening in openings
            .OrderBy(o => o.Year)
            .ThenBy(o => Name(o.CompanyId), StringComparer.Ordinal)
            .ThenBy(o => o.Ewc, StringComparer.Ordinal))
        {
            var index = _grid.Rows.Add();
            var row = _grid.Rows[index];

            row.Cells[CompanyColumn].Value = Name(opening.CompanyId);
            row.Cells[YearColumn].Value = opening.Year.ToString(CultureInfo.InvariantCulture);
            row.Cells[CodeColumn].Value = opening.Ewc;
            row.Cells[AmountColumn].Value = Amounts.Text(opening.Amount);

            Touched(index);
        }

        if (_grid.Rows.Count > 0)
        {
            _grid.CurrentCell = null;
            _grid.ClearSelection();
        }
    }

    /// <summary>Попълва изведените колони на реда и го оцветява, ако стои сам.</summary>
    private void Touched(int index)
    {
        if (index < 0 || index >= _grid.Rows.Count) return;

        var row = _grid.Rows[index];
        if (row.IsNewRow) return;

        var code = ValueConverter.NormalizeCode(Cell(row, CodeColumn));

        row.Cells[NameColumn].Value = code.Length == 0
            ? string.Empty
            : _names.TryGetValue(code, out var name) && name.Length > 0
                ? name
                : Lang.T("непознат код", "unknown code");

        row.Cells[NameColumn].Style.ForeColor =
            code.Length > 0 && !_names.ContainsKey(code) ? Theme.Error : Theme.InkMuted;

        var moved = Movement(row, code);

        row.Cells[MovedColumn].Value = moved is null ? "—" : Amounts.Text(moved.Value);

        // Начално количество по код, по който през цялата година не е мръднало
        // нищо: или залежал отпадък, или ред, въведен без да е забелязан.
        var alone = moved == 0m;

        row.Cells[MovedColumn].Style.BackColor = alone ? Theme.WarnSoft : Color.Empty;
        row.Cells[MovedColumn].Style.ForeColor = alone ? Theme.Warn : Theme.InkMuted;

        Describe();
    }

    private decimal? Movement(DataGridViewRow row, string code)
    {
        if (code.Length == 0) return null;
        if (Company(row) is not { } company) return null;
        if (!int.TryParse(Cell(row, YearColumn), out var year)) return null;

        if (!_cache.TryGetValue((company, year), out var moved))
        {
            moved = _movement(company, year);
            _cache[(company, year)] = moved;
        }

        return moved?.GetValueOrDefault(code);
    }

    private void Describe()
    {
        var rows = Rows(out _);
        var alone = 0;

        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.IsNewRow) continue;
            if (row.Cells[MovedColumn].Value?.ToString() == Amounts.Text(0m)) alone++;
        }

        _summary.ForeColor = alone > 0 ? Theme.Warn : Theme.InkMuted;

        _summary.Text = alone == 0
            ? Lang.T($"{rows.Count} начални количества, общо {Amounts.Text(rows.Sum(r => r.Amount))} т.",
                     $"{rows.Count} opening quantities, {Amounts.Text(rows.Sum(r => r.Amount))} t in total.")
            : Lang.T($"{rows.Count} начални количества, общо {Amounts.Text(rows.Sum(r => r.Amount))} т. " +
                     $"{alone} от тях {(alone == 1 ? "стои" : "стоят")} без нито едно движение през годината си.",
                     $"{rows.Count} opening quantities, {Amounts.Text(rows.Sum(r => r.Amount))} t in total. " +
                     $"{alone} of them {(alone == 1 ? "has" : "have")} no movement at all in their year.");
    }

    // ------------------------------------------------------------- четене

    private string Cell(DataGridViewRow row, string column) =>
        row.Cells[column].Value?.ToString()?.Trim() ?? string.Empty;

    private long? Company(DataGridViewRow row)
    {
        var display = Cell(row, CompanyColumn);

        return _companies.FirstOrDefault(c => c.Display == display)?.Id;
    }

    private string Name(long id) => _companies.FirstOrDefault(c => c.Id == id)?.Display ?? id.ToString();

    /// <summary>Каквото се чете от таблицата. Празните редове се прескачат.</summary>
    private List<Opening> Rows(out List<string> problems)
    {
        problems = [];

        var result = new List<Opening>();
        var seen = new HashSet<(long, int, string)>();

        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.IsNewRow) continue;

            var company = Company(row);
            var code = ValueConverter.NormalizeCode(Cell(row, CodeColumn));
            var yearText = Cell(row, YearColumn);
            var amountText = Cell(row, AmountColumn);

            if (company is null && code.Length == 0 && yearText.Length == 0 && amountText.Length == 0) continue;

            var where = Lang.T($"ред {row.Index + 1}: ", $"row {row.Index + 1}: ");

            if (company is null)
            {
                problems.Add(where + Lang.T("не е избрана фирма.", "no company is chosen."));
                continue;
            }

            if (code.Length == 0)
            {
                problems.Add(where + Lang.T("липсва код.", "the code is missing."));
                continue;
            }

            if (!int.TryParse(yearText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) ||
                year is < 1990 or > 2100)
            {
                problems.Add(where + Lang.T($"„{yearText}“ не е година.", $"“{yearText}” is not a year."));
                continue;
            }

            if (ValueConverter.TryAmount(amountText) is not { } amount)
            {
                problems.Add(where + Lang.T($"„{amountText}“ не е число.", $"“{amountText}” is not a number."));
                continue;
            }

            if (amount < 0)
            {
                problems.Add(where + Lang.T("количеството е отрицателно.", "the quantity is negative."));
                continue;
            }

            if (!seen.Add((company.Value, year, code)))
            {
                problems.Add(where + Lang.T($"кодът {code} вече го има за същата фирма и година.",
                                            $"code {code} is already there for the same company and year."));
                continue;
            }

            result.Add(new Opening(company.Value, year, code, amount));
        }

        return result;
    }

    /// <summary>
    /// Прочита таблицата и затваря прозореца, ако всичко се чете.
    ///
    /// Връща какво е станало, за да го ползват и тестовете: сбъркан ред спира
    /// целия запис, вместо половината да отиде в базата.
    /// </summary>
    internal bool Accept()
    {
        var rows = Rows(out var problems);

        if (problems.Count > 0)
        {
            if (Visible)
            {
                MessageBox.Show(this,
                    string.Join(Environment.NewLine, problems.Take(8)),
                    Lang.T("Има какво да се поправи", "There is something to fix"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            return false;
        }

        Result = rows;

        if (Visible) DialogResult = DialogResult.OK;

        return true;
    }

    // ------------------------------------------------------------- тестове

    /// <summary>Колко реда с данни има — ползва се от тестовете.</summary>
    internal int RowCount => Rows(out _).Count;

    /// <summary>Сборът на въведеното — ползва се от тестовете.</summary>
    internal decimal Total => Rows(out _).Sum(o => o.Amount);

    /// <summary>Колко реда стоят без движение през годината си — за тестовете.</summary>
    internal int Alone => _grid.Rows.Cast<DataGridViewRow>()
        .Count(row => !row.IsNewRow && row.Cells[MovedColumn].Value?.ToString() == Amounts.Text(0m));

    /// <summary>Движението, показано срещу даден код — ползва се от тестовете.</summary>
    internal decimal? MovementOf(string code)
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.IsNewRow) continue;
            if (ValueConverter.NormalizeCode(Cell(row, CodeColumn)) != code) continue;

            return ValueConverter.TryAmount(Cell(row, MovedColumn));
        }

        return null;
    }

    /// <summary>Слага количество на реда с този код — ползва се от тестовете.</summary>
    internal bool PutAmount(string code, string amount)
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.IsNewRow) continue;
            if (ValueConverter.NormalizeCode(Cell(row, CodeColumn)) != code) continue;

            row.Cells[AmountColumn].Value = amount;
            Touched(row.Index);

            return true;
        }

        return false;
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keys)
    {
        if (keys == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            return true;
        }

        return base.ProcessCmdKey(ref message, keys);
    }
}
