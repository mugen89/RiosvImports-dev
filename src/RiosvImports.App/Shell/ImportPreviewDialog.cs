using RiosvImports.App.Controls;
using RiosvImports.Core.Ledger;
using RiosvImports.Core.Localization;
using RiosvImports.Core.Values;

namespace RiosvImports.App.Shell;

/// <summary>
/// Какво носи чуждият файл — ПРЕДИ да влезе.
///
/// Дневникът на „Деметра“ се изнася цял: движенията на площадката за месец, а
/// понякога и за три. В него редовно има редове, които тук нямат работа —
/// приемане, което е сторнирано, курс, който е бил на друго дружество, или
/// просто грешка на оператора отсреща. Досега те влизаха в таблицата и се
/// махаха оттам — но вече са се смесили с останалите и се търсят по същия
/// начин, по който се търси игла.
///
/// Затова прозорчето стои МЕЖДУ файла и таблицата: тук редовете се подреждат,
/// пресяват и махат, а нататък тръгва само каквото е останало. Маркерът обикно-
/// вено е един и се вижда от пръв поглед — дата извън месеца, кръгло число,
/// непознат контрагент — и затова има и подредба по заглавие, и търсене, и
/// сито по вид и по месец.
///
/// <b>Нищо не се записва оттук.</b> Прозорчето само отсява; записът си остава на
/// таблицата, както и досега.
/// </summary>
internal sealed class ImportPreviewDialog : Dialog
{
    private const string LineColumn = "__line";
    private const string KindColumn = "__kind";
    private const string DateColumn = "__date";
    private const string CodeColumn = "__code";
    private const string AmountColumn = "__amount";
    private const string PartnerColumn = "__partner";
    private const string DocumentColumn = "__document";
    private const string SiteColumn = "__site";

    private readonly List<ImportedMove> _all;
    private readonly HashSet<int> _dropped = [];
    private readonly MoveKind _wanted;
    private readonly int _problems;

    private readonly DataGridView _grid = new();
    private readonly Label _summary = new();
    private readonly SearchBox _search = new(Lang.T("Код, контрагент или документ", "Code, partner or document"));
    private readonly ComboBox _kind = new();
    private readonly ComboBox _month = new();
    private readonly AppButton _drop;

    private string _sort = DateColumn;
    private bool _down;

    public ImportPreviewDialog(
        string fileName, IReadOnlyList<ImportedMove> moves, MoveKind wanted, int problems = 0)
    {
        _all = [.. moves];
        _wanted = wanted;
        _problems = problems;

        Text = Lang.T("Какво носи файлът", "What the file holds");
        Font = Theme.Base;
        BackColor = Theme.Canvas;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;

        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;

        ClientSize = new Size(
            Math.Clamp(screen.Width - 160,
                Math.Min(Theme.Scaled(1060), screen.Width),
                Math.Max(Theme.Scaled(1360), Theme.Scaled(1060))),
            Math.Clamp(screen.Height - 160,
                Math.Min(Theme.Scaled(560), screen.Height),
                Theme.Scaled(820)));

        MinimumSize = new Size(
            Math.Min(Theme.Scaled(980), screen.Width),
            Math.Min(Theme.Scaled(560), screen.Height));

        KeyPreview = true;

        _drop = Theme.DangerButton(Lang.T("Махни избраните", "Drop the selected"), Theme.Glyph.Delete);

        Controls.Add(BuildBody());
        Controls.Add(BuildFilters());
        Controls.Add(BuildTitle(fileName));
        Controls.Add(BuildButtons());

        Fill();

        Theme.Apply(this);
    }

    /// <summary>Каквото е останало след отсяването — това тръгва нататък.</summary>
    public IReadOnlyList<ImportedMove> Kept =>
    [
        .. _all.Where(m => !_dropped.Contains(m.Line))
    ];

    // ------------------------------------------------------------- устройство

    private Control BuildTitle(string fileName)
    {
        var host = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(100),
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, Theme.Gap, Theme.GapLarge, 0)
        };

        // Пълнещият влиза пръв (виж АРХИТЕКТУРА.md).
        host.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = Lang.T($"Какво носи {fileName}", $"What {fileName} holds"),
            Font = Theme.Title,
            ForeColor = Theme.Ink
        });

        host.Controls.Add(new Label
        {
            Dock = DockStyle.Bottom,
            Height = Theme.Scaled(48),
            Text = Lang.T(
                "Редовете още не са влезли никъде. Подредете по заглавие, пресейте и махнете " +
                $"онова, което няма работа тук — нататък тръгва само останалото. В „{Moves.Title(_wanted)}“ " +
                "влизат само редовете от този вид; останалите се внасят от своите таблици.",
                "Nothing has been entered anywhere yet. Sort by a header, filter, and drop whatever " +
                $"does not belong — only the rest goes on. Only “{Moves.Title(_wanted)}” rows enter " +
                "this table; the others are imported from their own."),
            Font = Theme.Base,
            ForeColor = Theme.InkMuted
        });

        return host;
    }

    /// <summary>
    /// Ситото: вид, месец и търсене.
    ///
    /// Пренасящ се ред, както в склада: три контроли с постоянни ширини на тесен
    /// прозорец не се събират, а филтър, който го няма, не е филтър.
    /// </summary>
    private Control BuildFilters()
    {
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, 0, Theme.GapLarge, Theme.Gap)
        };

        _kind.DropDownStyle = ComboBoxStyle.DropDownList;
        _kind.Items.Add(Lang.T("Всички видове", "All kinds"));

        foreach (var kind in Moves.All) _kind.Items.Add(Moves.Title(kind));

        // Отваря се на вида на таблицата: точно тези редове ще влязат в нея, а
        // другите се внасят отделно.
        _kind.SelectedIndex = Moves.All.ToList().IndexOf(_wanted) + 1;
        _kind.SelectedIndexChanged += (_, _) => Fill();
        Theme.StyleInput(_kind);

        _month.DropDownStyle = ComboBoxStyle.DropDownList;
        _month.SelectedIndexChanged += (_, _) => Fill();
        Theme.StyleInput(_month);

        FillMonths();

        _search.Height = Theme.Scaled(34);
        _search.TextChanged += (_, _) => Fill();

        bar.Controls.Add(Cell(_kind, Lang.T("Вид движение", "Movement kind"), 220));
        bar.Controls.Add(Cell(_month, Lang.T("Месец", "Month"), 200));
        bar.Controls.Add(Cell(_search, Lang.T("Търсене", "Search"), 300));

        _drop.Height = Theme.Scaled(34);
        _drop.Dock = DockStyle.Top;
        _drop.Enabled = false;
        _drop.Click += (_, _) => Drop();

        bar.Controls.Add(Cell(_drop, string.Empty, 200));

        var back = Theme.GhostButton(Lang.T("Върни махнатите", "Put the dropped back"));
        back.Height = Theme.Scaled(34);
        back.Dock = DockStyle.Top;
        back.Click += (_, _) => Restore();

        bar.Controls.Add(Cell(back, string.Empty, 190));

        return bar;
    }

    private static Control Cell(Control input, string caption, int width)
    {
        var host = new Panel
        {
            Width = Theme.Scaled(width),
            Height = Theme.Small.Height + 6 + Theme.Scaled(34),
            Margin = new Padding(0, 0, Theme.GapSmall, Theme.GapSmall),
            BackColor = Theme.Canvas
        };

        input.Dock = DockStyle.Top;

        host.Controls.Add(input);
        host.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            Height = Theme.Small.Height + 6,
            Text = caption,
            Font = Theme.SmallBold,
            ForeColor = Theme.InkFaint
        });

        return host;
    }

    private Control BuildBody()
    {
        var host = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, 0, Theme.GapLarge, Theme.GapSmall)
        };

        BuildGrid();

        var card = new Card
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(1),
            Fill = Theme.Canvas,
            Outline = Theme.Border
        };

        _summary.Dock = DockStyle.Bottom;
        _summary.Height = Theme.Base.Height + Theme.Scaled(10);
        _summary.Font = Theme.Bold;
        _summary.ForeColor = Theme.InkMuted;

        _grid.Dock = DockStyle.Fill;
        card.Controls.Add(_grid);

        host.Controls.Add(card);
        host.Controls.Add(_summary);

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
        _grid.MultiSelect = true;
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
        _grid.RowTemplate.Height = line + 12;
        _grid.ColumnHeadersHeight = line + 20;

        Column(LineColumn, Lang.T("Ред", "Line"), 70, right: true);
        Column(KindColumn, Lang.T("Движение", "Movement"), 130);
        Column(DateColumn, Lang.T("Дата", "Date"), 110);
        Column(CodeColumn, Lang.T("Код", "Code"), 110);
        Column(AmountColumn, Lang.T("Количество, т", "Quantity, t"), 130, right: true);
        Column(PartnerColumn, Lang.T("Контрагент", "Partner"), 280, fill: true);
        Column(DocumentColumn, Lang.T("Документ", "Document"), 190);
        Column(SiteColumn, Lang.T("Площадка", "Site"), 110);

        _grid.ColumnHeaderMouseClick += (_, e) => Sort(e.ColumnIndex);
        _grid.SelectionChanged += (_, _) => Allow();
    }

    private void Column(string name, string header, int width, bool fill = false, bool right = false)
    {
        var column = new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            Width = Theme.Scaled(width),
            SortMode = DataGridViewColumnSortMode.Programmatic
        };

        if (fill)
        {
            column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            column.MinimumWidth = Theme.Scaled(180);
        }

        if (right)
        {
            column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
        }

        _grid.Columns.Add(column);
    }

    private Control BuildButtons()
    {
        var host = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = Theme.Base.Height + Theme.Scaled(30),
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, 0, Theme.GapLarge, Theme.Gap)
        };

        var take = Theme.PrimaryButton(Lang.T("Вземи останалото", "Take what is left"));
        take.Dock = DockStyle.Right;
        take.Height = Theme.Base.Height + Theme.Scaled(16);
        take.FitWidth(16);
        take.Click += (_, _) => DialogResult = DialogResult.OK;

        var cancel = Theme.SecondaryButton(Lang.T("Откажи", "Cancel"));
        cancel.Dock = DockStyle.Right;
        cancel.Height = Theme.Base.Height + Theme.Scaled(16);
        cancel.Margin = new Padding(0, 0, Theme.GapSmall, 0);
        cancel.FitWidth(16);
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;

        // Закачените отдясно се редят в обратен ред на добавяне.
        host.Controls.Add(take);
        host.Controls.Add(cancel);

        AcceptButton = take;
        CancelButton = cancel;

        return host;
    }

    // ------------------------------------------------------------- пълнене

    private void FillMonths()
    {
        _month.Items.Clear();
        _month.Items.Add(Lang.T("Всички месеци", "All months"));

        foreach (var period in _all.Select(m => LedgerPeriod.Of(m.Date)).Distinct().OrderBy(p => p))
            _month.Items.Add(period.Title());

        _month.SelectedIndex = 0;
    }

    /// <summary>Каквото остава след ситото — редът на екрана.</summary>
    private IEnumerable<ImportedMove> Shown()
    {
        var kind = _kind.SelectedIndex > 0 ? Moves.All[_kind.SelectedIndex - 1] : (MoveKind?)null;
        var month = _month.SelectedIndex > 0 ? _month.SelectedItem?.ToString() : null;
        var text = _search.Text.Trim();

        return _all.Where(move =>
            (kind is null || move.Kind == kind) &&
            (month is null || LedgerPeriod.Of(move.Date).Title() == month) &&
            (text.Length == 0 || Hits(move, text)));
    }

    /// <summary>
    /// Търси се в няколко полета наведнъж.
    ///
    /// Човек помни думата, не полето: „Метал“ хваща контрагента, „1602“ —
    /// кода, „114“ — документа. Разделени на три търсения, те биха искали от
    /// него да знае предварително кое търси.
    /// </summary>
    private static bool Hits(ImportedMove move, string text) =>
        move.Ewc.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        move.Partner.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        move.Eik.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        move.Document.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        SplitMath.Amount(move.Amount).Contains(text, StringComparison.Ordinal);

    private IEnumerable<ImportedMove> Arrange(IEnumerable<ImportedMove> moves)
    {
        var order = _sort switch
        {
            KindColumn => moves.OrderBy(m => Moves.Title(m.Kind), StringComparer.CurrentCulture),
            CodeColumn => moves.OrderBy(m => m.Ewc, StringComparer.Ordinal),
            AmountColumn => moves.OrderBy(m => m.Amount),
            PartnerColumn => moves.OrderBy(m => m.Partner, StringComparer.CurrentCulture),
            DocumentColumn => moves.OrderBy(m => m.Document, StringComparer.CurrentCulture),
            SiteColumn => moves.OrderBy(m => m.Stagenum, StringComparer.Ordinal),
            LineColumn => moves.OrderBy(m => m.Line),
            _ => moves.OrderBy(m => m.Date).ThenBy(m => m.Line)
        };

        return _down ? order.Reverse() : order;
    }

    /// <summary>
    /// Задрасканият шрифт — един за всички махнати редове.
    ///
    /// Дотук всеки махнат ред получаваше свой нов шрифт при всяко пълнене, а
    /// таблицата се пълни наново при всеки знак в търсенето: стотици шрифтове,
    /// които никой не освобождава.
    /// </summary>
    private readonly Font _struck = new(Theme.Base, FontStyle.Strikeout);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _struck.Dispose();

        base.Dispose(disposing);
    }

    private void Fill()
    {
        // Наведнъж, а не ред по ред: юлският износ е петстотин и осемдесет реда и
        // таблицата се пълни наново при всяко търсене и подреждане. Номерата на
        // стълбовете се вземат веднъж: ред, още невързан за таблицата, не познава
        // клетките си по име.
        int At(string column) => _grid.Columns[column]!.Index;

        var (line, kind, date, code, amount, partner, document, site) =
            (At(LineColumn), At(KindColumn), At(DateColumn), At(CodeColumn),
             At(AmountColumn), At(PartnerColumn), At(DocumentColumn), At(SiteColumn));

        var rows = new List<DataGridViewRow>();

        foreach (var move in Arrange(Shown()))
        {
            var row = (DataGridViewRow)_grid.RowTemplate.Clone();

            row.CreateCells(_grid);

            row.Cells[line].Value = move.Line.ToString();
            row.Cells[kind].Value = Moves.Title(move.Kind);
            row.Cells[date].Value = move.Date.ToString(ValueConverter.BulgarianDateFormat);
            row.Cells[code].Value = move.Ewc;
            row.Cells[amount].Value = SplitMath.Amount(move.Amount);

            row.Cells[partner].Value = move.Partner.Length > 0 && move.Eik.Length > 0
                ? $"{move.Partner} — {move.Eik}"
                : move.Partner.Length > 0 ? move.Partner : move.Eik;

            row.Cells[document].Value = move.Document;
            row.Cells[site].Value = move.Stagenum;

            // Махнатото не изчезва от списъка: то се задрасква. Изчезнал ред не
            // може да бъде върнат, а и не се вижда, че изобщо го е имало.
            if (_dropped.Contains(move.Line))
            {
                row.DefaultCellStyle.ForeColor = Theme.InkFaint;
                row.DefaultCellStyle.Font = _struck;
            }

            row.Tag = move;

            rows.Add(row);
        }

        _grid.Rows.Clear();
        _grid.Rows.AddRange([.. rows]);

        Glyph();
        Weigh();
        Allow();
    }

    private void Glyph()
    {
        foreach (DataGridViewColumn column in _grid.Columns)
            column.HeaderCell.SortGlyphDirection = column.Name == _sort
                ? _down ? SortOrder.Descending : SortOrder.Ascending
                : SortOrder.None;
    }

    private void Sort(int index)
    {
        if (index < 0 || index >= _grid.Columns.Count) return;

        var name = _grid.Columns[index].Name;

        // Числата тръгват от голямото надолу: натиснал „Количество“, човек пита
        // кое е най-многото.
        _down = _sort == name ? !_down : name is AmountColumn or DateColumn;
        _sort = name;

        Fill();
    }

    /// <summary>Редът отдолу: колко влизат, колко са махнати и колко тона са.</summary>
    private void Weigh()
    {
        var kept = Kept;
        var mine = kept.Where(m => m.Kind == _wanted).ToList();

        var said = Lang.T(
            $"Във файла: {_all.Count} реда · в „{Moves.Title(_wanted)}“ влизат {mine.Count} " +
            $"· {SplitMath.Amount(mine.Sum(m => m.Amount))} т",
            $"In the file: {_all.Count} rows · {mine.Count} enter “{Moves.Title(_wanted)}” " +
            $"· {SplitMath.Amount(mine.Sum(m => m.Amount))} t");

        if (_dropped.Count > 0)
            said += Lang.T($" · махнати {_dropped.Count}", $" · {_dropped.Count} dropped");

        if (_problems > 0)
            said += Lang.T($" · {_problems} неразчетени", $" · {_problems} unreadable");

        _summary.Text = said;
        _summary.ForeColor = _dropped.Count > 0 ? Theme.Warn : Theme.InkMuted;
    }

    private void Allow() =>
        _drop.Enabled = _grid.SelectedRows.Count > 0;

    // ------------------------------------------------------------- действия

    /// <summary>Задрасква избраните — те не тръгват нататък.</summary>
    private void Drop()
    {
        foreach (DataGridViewRow row in _grid.SelectedRows)
            if (row.Tag is ImportedMove move)
                _dropped.Add(move.Line);

        Fill();
    }

    /// <summary>Връща всички махнати: сгрешеното махане не бива да иска нов файл.</summary>
    private void Restore()
    {
        if (_dropped.Count == 0) return;

        _dropped.Clear();
        Fill();
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keys)
    {
        if (keys == Keys.Delete && _grid.Focused && _grid.SelectedRows.Count > 0)
        {
            Drop();
            return true;
        }

        return base.ProcessCmdKey(ref message, keys);
    }

    // ------------------------------------------------------------- за теста

    internal int RowCount => _grid.Rows.Count;

    internal int DroppedCount => _dropped.Count;

    internal string SummaryText => _summary.Text;

    internal string CellAt(int row, string column) =>
        _grid.Rows[row].Cells[column].Value?.ToString() ?? string.Empty;

    internal string CodeAt(int row) => CellAt(row, CodeColumn);

    internal string AmountAt(int row) => CellAt(row, AmountColumn);

    internal string DateAt(int row) => CellAt(row, DateColumn);

    /// <summary>Щраква заглавие на стълб, както би щракнал човек.</summary>
    internal void SortBy(string column)
    {
        for (var i = 0; i < _grid.Columns.Count; i++)
            if (_grid.Columns[i].Name == column)
            {
                Sort(i);
                return;
            }
    }

    internal static string AmountColumnName => AmountColumn;

    internal static string DateColumnName => DateColumn;

    /// <summary>Избира редове и ги маха, както копчето.</summary>
    internal void DropRows(params int[] rows)
    {
        _grid.ClearSelection();

        foreach (var row in rows)
            if (row >= 0 && row < _grid.Rows.Count)
                _grid.Rows[row].Selected = true;

        Drop();
    }

    internal void RestoreAll() => Restore();

    internal void UseSearch(string text)
    {
        _search.Text = text;
        Fill();
    }

    internal void UseKind(MoveKind? kind)
    {
        _kind.SelectedIndex = kind is null ? 0 : Moves.All.ToList().IndexOf(kind.Value) + 1;
        Fill();
    }

    internal void UseMonth(LedgerPeriod? period)
    {
        if (period is null)
        {
            _month.SelectedIndex = 0;
        }
        else
        {
            var title = period.Value.Title();

            for (var i = 0; i < _month.Items.Count; i++)
                if (_month.Items[i]?.ToString() == title)
                {
                    _month.SelectedIndex = i;
                    break;
                }
        }

        Fill();
    }
}
