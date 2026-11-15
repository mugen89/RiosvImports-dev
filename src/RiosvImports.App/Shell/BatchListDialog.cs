using RiosvImports.App.Controls;
using RiosvImports.Core.Ledger;
using RiosvImports.Core.Localization;
using RiosvImports.Core.Values;

namespace RiosvImports.App.Shell;

/// <summary>
/// Партидите зад един код — на цял прозорец.
///
/// В работните прозорчета партидите стоят в дясната половина, а при петдесет
/// клиента зад един код тя е тясна: клиентът се реже, а редът, който човек
/// търси, е три превъртания надолу. Тук същият списък има целия екран, търсене
/// и същото пресяване и подреждане по заглавие.
///
/// Изборът работи точно като вдясно: избраното Е изборът. Щракване, Ctrl и
/// Shift избират партиди, а избраната влиза с цялото си налично — или с
/// написаното, ако човек е писал в реда. Прозорчето не пише в базата и не
/// решава нищо: връща по колко се взима от всяка партида.
///
/// Видът е копие на <see cref="PartialDialog"/> — същите колони, рамка и копчета
/// — за да не се учи втори начин за едно и също нещо.
/// </summary>
internal sealed class BatchListDialog : Dialog
{
    private const string BatchColumn = "__batch";
    private const string PartnerColumn = "__partner";
    private const string ReceivedColumn = "__received";
    private const string StockColumn = "__stock";
    private const string TakeColumn = "__take";

    private readonly IReadOnlyList<StockShare> _shares;
    private readonly string _title;
    private readonly string _takeTitle;

    private readonly DataGridView _grid = new();
    private readonly Label _sum = new();
    private readonly Label _heading = new();

    private readonly SearchBox _search = new(Lang.T(
        "Търсете по партида, клиент или документ", "Search by batch, client or document"));

    /// <summary>Пресяването — десен бутон по заглавието, както навсякъде.</summary>
    private readonly GridFilter _filter;

    /// <summary>По колко се взима от всяка партида — това връща прозорчето.</summary>
    private readonly Dictionary<long, decimal> _takes = [];

    /// <summary>
    /// Каквото човекът е писал по партида — за да не се губи при пресичане на избора.
    ///
    /// Извадена и върната обратно, партидата трябва да се върне с написаното,
    /// а не с цялото налично.
    /// </summary>
    private readonly Dictionary<long, decimal> _typed = [];

    private bool _filling;

    /// <summary>„Вземи отгоре до …“ — само когато прозорецът го предлага.</summary>
    private readonly FromTopBar? _fromTop;

    public BatchListDialog(
        string title,
        string takeTitle,
        IReadOnlyList<StockShare> shares,
        IReadOnlyDictionary<long, decimal> taken,
        decimal? fromTop = null)
    {
        _shares = [.. shares.OrderBy(s => s.Batch.ReceivedOn).ThenBy(s => s.Batch.Id)];
        _title = title;
        _takeTitle = takeTitle;
        _filter = new GridFilter(_grid) { Also = Found };

        foreach (var share in _shares)
            if (taken.TryGetValue(share.Batch.Id, out var amount) && amount > 0)
            {
                _takes[share.Batch.Id] = Math.Min(amount, share.Amount);

                // Непълно взетата е писана — връща се с числото си.
                if (amount < share.Amount) _typed[share.Batch.Id] = amount;
            }

        Text = title;
        Font = Theme.Base;
        BackColor = Theme.Canvas;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        KeyPreview = true;

        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;

        MinimumSize = new Size(Math.Min(Theme.Scaled(880), screen.Width), Math.Min(Theme.Scaled(480), screen.Height));
        ClientSize = new Size(
            Math.Min(Theme.Scaled(1240), screen.Width - 80),
            Math.Min(Theme.Scaled(860), screen.Height - 80));

        // Пълнещият влиза пръв; от закачените отгоре последният застава най-горе.
        Controls.Add(BuildBody());

        // „Вземи отгоре“ — само при предаването: там се пълни камион до тонаж.
        // Колко — идва от „Предадено общо“ в прозореца отзад, без взетото по
        // другите кодове. Тук свое поле няма: второ място за същото число.
        if (fromTop is { } top)
        {
            _fromTop = new FromTopBar { Padding = new Padding(Theme.GapLarge, 2, Theme.GapLarge, Theme.GapSmall) };

            _fromTop.Hint(top > 0
                ? Lang.T($"Ще вземе до {SplitMath.Amount(top)} т — според „Предадено общо“.",
                         $"Will take up to {SplitMath.Amount(top)} t — from the “Handed over in total”.")
                : Lang.T("Няма какво да се вземе: „Предадено общо“ не е написано или вече е покрито.",
                         "Nothing to take: the “Handed over in total” is not typed or is already covered."));

            _fromTop.Requested += () =>
            {
                if (top > 0) FromTop(top);
            };

            Controls.Add(_fromTop);
        }

        Controls.Add(BuildSearch());
        Controls.Add(BuildTitle());
        Controls.Add(BuildButtons());

        Fill();

        Theme.Apply(this);
    }

    /// <summary>По колко се взима от всяка партида. Липсващата значи „нищо оттам“.</summary>
    public IReadOnlyDictionary<long, decimal> Takes => _takes;

    /// <summary>
    /// Редът над партидите в работните прозорчета: надписът и иконата, която
    /// отваря списъка на цял прозорец.
    ///
    /// Едно място за трите прозорчета (третиране, образуване, предаване): там
    /// партидите стоят по един и същ начин и иконата трябва да стои еднакво.
    /// </summary>
    internal static Control Header(Label behind, Action open)
    {
        // Два реда по шрифта на надписа: името на кода по ЕКО е дълго и редът
        // минава на втори — с място за един, краят (броят и тоновете) се режеше.
        var height = Math.Max(Theme.Scaled(30), 2 * behind.Font.Height + Theme.Scaled(4));

        var head = new Panel { Dock = DockStyle.Top, Height = height, BackColor = Theme.Canvas };

        var tip = Lang.T("Отвори партидите на цял прозорец", "Open the batches in a full window");

        var button = Theme.GhostButton(string.Empty, Theme.Glyph.Expand);
        button.Dock = DockStyle.Right;
        button.Width = height + 6;
        button.AccessibleName = tip;
        button.TabStop = false;
        button.Click += (_, _) => open();

        new ToolTip().SetToolTip(button, tip);

        behind.Dock = DockStyle.Fill;

        // Пълнещият влиза пръв (виж АРХИТЕКТУРА.md).
        head.Controls.Add(behind);
        head.Controls.Add(button);

        return head;
    }

    // ------------------------------------------------------------- устройство

    private Control BuildTitle()
    {
        var host = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(92),
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, Theme.Gap, Theme.GapLarge, 0)
        };

        host.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = Lang.T(
                "Избраното е това, което се взима: щракване, Ctrl или Shift за няколко. Избраната " +
                "партида влиза цяла, освен ако в реда не е написано друго. " +
                "Заглавие: щракване подрежда, десен бутон пресява. Ctrl+F — търсене.",
                "What is selected is what gets taken: click, Ctrl or Shift for several. A selected " +
                "batch goes in whole unless another amount is typed in its row. " +
                "Header: click sorts, right-click filters. Ctrl+F — search."),
            Font = Theme.Base,
            ForeColor = Theme.InkMuted
        });

        _heading.Dock = DockStyle.Top;
        _heading.Height = Theme.Scaled(34);
        _heading.Font = Theme.Title;
        _heading.ForeColor = Theme.Ink;
        _heading.AutoEllipsis = true;
        host.Controls.Add(_heading);

        return host;
    }

    private Control BuildSearch()
    {
        var host = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(34) + Theme.Gap,
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, 0, Theme.GapLarge, Theme.Gap)
        };

        _search.Dock = DockStyle.Left;
        _search.Width = Theme.Scaled(420);
        _search.Height = Theme.Scaled(34);
        _search.TextChanged += (_, _) => Searched();

        host.Controls.Add(_search);

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
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;

        // Избор по цял ред — тук се избират партиди, както вдясно.
        _grid.MultiSelect = true;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;

        _grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText;
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
        _grid.DefaultCellStyle.SelectionBackColor = Theme.Accent;
        _grid.DefaultCellStyle.SelectionForeColor = Theme.OnAccent;
        _grid.DefaultCellStyle.Padding = new Padding(6, 4, 6, 4);
        _grid.AlternatingRowsDefaultCellStyle.BackColor = Theme.Surface;
        _grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = Theme.Accent;
        _grid.AlternatingRowsDefaultCellStyle.SelectionForeColor = Theme.OnAccent;
        _grid.GridColor = Theme.Border;

        var line = Math.Max(1, _grid.Font.Height);
        _grid.RowTemplate.Height = line + 14;
        _grid.ColumnHeadersHeight = 2 * line + 18;

        Column(BatchColumn, Lang.T("Партида", "Batch"), 120);
        Column(PartnerColumn, Lang.T("Клиент", "Client"), 300, fill: true);
        Column(ReceivedColumn, Lang.T("Приета на · документ", "Received · document"), 240);
        Column(StockColumn, Lang.T("Налично, т", "Available, t"), 130, right: true);
        Column(TakeColumn, _takeTitle, 170, right: true, write: true);

        // Ляв бутон подрежда, десен пресява — както в редактора на отчета.
        _grid.SortCompare += Compare;
        _grid.ColumnHeaderMouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Right) _filter.Open(e.ColumnIndex);
        };

        // Подреждането и пресяването местят и крият редове — изборът се
        // рисува наново от числата, не от това, което таблицата е запомнила.
        _grid.Sorted += (_, _) => ShowChoice();

        _filter.Changed += () =>
        {
            ShowChoice();
            Weigh();
        };

        // Щракване в колоната за количество не е избор, а писане: то не бива
        // да изважда останалите партиди изпод ръката на човека.
        _grid.MouseUp += (_, e) =>
        {
            var at = _grid.HitTest(e.X, e.Y);

            if (at.Type != DataGridViewHitTestType.Cell) return;

            if (_grid.Columns[at.ColumnIndex].Name == TakeColumn) ShowChoice();
            else Picked();
        };

        // Стрелките и Ctrl+A избират точно колкото и мишката.
        _grid.KeyUp += (_, e) =>
        {
            if (e.KeyCode is not (Keys.Up or Keys.Down or Keys.Home or Keys.End
                or Keys.PageUp or Keys.PageDown or Keys.Space or Keys.A)) return;

            if (_grid.IsCurrentCellInEditMode) return;

            Picked();
        };

        _grid.CellEndEdit += (_, e) => Wrote(e.RowIndex);
    }

    /// <summary>
    /// Подрежда по истинските стойности, а не по изписаното.
    ///
    /// Изписаното е „10,514“ и „01.07.2026 · Сертификат“: като текст десет идва
    /// преди две, а юли на една година — преди януари на следващата.
    /// </summary>
    private void Compare(object? sender, DataGridViewSortCompareEventArgs e)
    {
        if (_grid.Rows[e.RowIndex1].Tag is not StockShare a ||
            _grid.Rows[e.RowIndex2].Tag is not StockShare b) return;

        e.SortResult = e.Column.Name switch
        {
            StockColumn => a.Amount.CompareTo(b.Amount),
            TakeColumn => _takes.GetValueOrDefault(a.Batch.Id).CompareTo(_takes.GetValueOrDefault(b.Batch.Id)),
            ReceivedColumn => a.Batch.ReceivedOn.CompareTo(b.Batch.ReceivedOn),
            _ => string.Compare(e.CellValue1?.ToString(), e.CellValue2?.ToString(),
                StringComparison.CurrentCulture)
        };

        if (e.SortResult == 0) e.SortResult = a.Batch.Id.CompareTo(b.Batch.Id);

        e.Handled = true;
    }

    private void Column(string name, string header, int width, bool fill = false,
        bool right = false, bool write = false)
    {
        var column = new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,

            // Ширината е мерена при 100 %: при 125 % и 150 % буквите в стълба
            // растат и същото число почва да ги реже.
            Width = Theme.Scaled(width),
            ReadOnly = !write,
            SortMode = DataGridViewColumnSortMode.Automatic
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

        // Клетката, в която се пише, изглежда като поле, а не като число.
        if (write)
        {
            column.DefaultCellStyle.BackColor = Theme.Surface;
            column.DefaultCellStyle.Font = Theme.Bold;
        }

        _grid.Columns.Add(column);
    }

    private Control BuildButtons()
    {
        var height = Theme.Scaled(40);

        var bar = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = height + 2 * Theme.Gap,
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, Theme.Gap, Theme.GapLarge, Theme.Gap)
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

        var ok = Theme.PrimaryButton(Lang.T("Готово", "Done"));
        ok.Height = height;
        ok.Click += (_, _) => DialogResult = DialogResult.OK;

        var cancel = Theme.GhostButton(Lang.T("Отказ", "Cancel"));
        cancel.Height = height;
        cancel.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;

        tools.Controls.Add(ok.FitWidth());
        tools.Controls.Add(cancel.FitWidth());

        var quick = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Canvas
        };

        quick.Controls.Add(Quick(Lang.T("Всичко", "Everything"), whole: true));
        quick.Controls.Add(Quick(Lang.T("Нищо", "Nothing"), whole: false));

        _sum.AutoSize = true;
        _sum.MinimumSize = new Size(0, height);
        _sum.TextAlign = ContentAlignment.MiddleLeft;
        _sum.Font = Theme.Bold;
        _sum.ForeColor = Theme.Ink;
        _sum.Margin = new Padding(Theme.Gap, 0, 0, 0);

        quick.Controls.Add(_sum);

        // Пълнещият влиза пръв; закачените отдясно взимат своето.
        bar.Controls.Add(quick);
        bar.Controls.Add(tools);

        // Без AcceptButton: Enter в таблицата приключва писането в клетка, а
        // не затваря прозорчето с половин избор.
        CancelButton = cancel;

        return bar;
    }

    private AppButton Quick(string text, bool whole)
    {
        var button = Theme.GhostButton(text);
        button.Height = Theme.Scaled(40);
        button.Margin = new Padding(0, 0, Theme.GapSmall, 0);
        button.Click += (_, _) => SetAll(whole);

        return button.FitWidth();
    }

    // ------------------------------------------------------------- пълнене

    private void Fill()
    {
        _grid.Rows.Clear();

        foreach (var share in _shares)
        {
            var index = _grid.Rows.Add();
            var row = _grid.Rows[index];

            row.Cells[BatchColumn].Value = "#" + share.Batch.Code;

            row.Cells[PartnerColumn].Value = share.Batch.PartnerName.Length > 0
                ? $"{share.Batch.PartnerName} — {share.Batch.Eik}"
                : share.Batch.Eik.Length > 0 ? share.Batch.Eik : Lang.T("(клиент не е посочен)", "(no client stated)");

            var document = share.Batch.Document.Length > 0
                ? $"{share.Batch.ReceivedOn.ToString(ValueConverter.BulgarianDateFormat)} · {share.Batch.Document}"
                : share.Batch.ReceivedOn.ToString(ValueConverter.BulgarianDateFormat);

            row.Cells[ReceivedColumn].Value = share.Batch.Opening
                ? document + Lang.T(" · заварено", " · carried in")
                : document;

            row.Cells[StockColumn].Value = SplitMath.Amount(share.Amount);
            row.Tag = share;

            Show(row, _takes.GetValueOrDefault(share.Batch.Id));
        }

        _filter.Apply();
        ShowChoice();
        Weigh();
    }

    /// <summary>
    /// Таблицата при първото показване избира първия ред сама и изтрива
    /// нагласения избор — затова той се рисува наново, щом прозорецът се види.
    /// </summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        ShowChoice();
        Weigh();
        _grid.Focus();
    }

    /// <summary>Изписва едно количество и казва с цвят дали редът участва.</summary>
    private static void Show(DataGridViewRow row, decimal take)
    {
        row.Cells[TakeColumn].Value = take > 0 ? SplitMath.Amount(take) : string.Empty;
        row.Cells[TakeColumn].Style.ForeColor = take > 0 ? Theme.Accent : Theme.InkFaint;
    }

    /// <summary>Избира в таблицата точно партидите, от които се взима.</summary>
    private void ShowChoice()
    {
        _filling = true;

        try
        {
            foreach (DataGridViewRow row in _grid.Rows)
                if (row.Tag is StockShare share)
                    row.Selected = row.Visible && _takes.GetValueOrDefault(share.Batch.Id) > 0;
        }
        finally
        {
            _filling = false;
        }
    }

    /// <summary>
    /// Избраните редове стават това, което се взима.
    ///
    /// Скритите от търсенето или пресяването не се пипат: те не могат да се
    /// щракнат, а щракването по видимите не бива да ги изважда.
    /// </summary>
    private void Picked()
    {
        if (_filling) return;

        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is not StockShare share || !row.Visible) continue;

            if (row.Selected)
                _takes[share.Batch.Id] = _typed.TryGetValue(share.Batch.Id, out var typed)
                    ? Math.Min(typed, share.Amount)
                    : share.Amount;
            else
                _takes.Remove(share.Batch.Id);

            Show(row, _takes.GetValueOrDefault(share.Batch.Id));
        }

        Weigh();
    }

    /// <summary>Написаното по един ред. Над наличното се свежда до него.</summary>
    private void Wrote(int index)
    {
        if (index < 0 || index >= _grid.Rows.Count) return;
        if (_grid.Rows[index].Tag is not StockShare share) return;

        var text = _grid.Rows[index].Cells[TakeColumn].Value?.ToString();

        if (!SplitMath.TryRead(text, out var amount) || amount <= 0)
        {
            _takes.Remove(share.Batch.Id);
            _typed.Remove(share.Batch.Id);
            Show(_grid.Rows[index], 0m);
        }
        else
        {
            var take = Math.Min(SplitMath.Round(amount), share.Amount);

            _takes[share.Batch.Id] = take;
            _typed[share.Batch.Id] = take;
            Show(_grid.Rows[index], take);
        }

        // Писането е свило избора до един ред — връща се истинският.
        BeginInvokeIfShown(ShowChoice);
        Weigh();
    }

    /// <summary>
    /// Изборът след писане се връща чак след като таблицата приключи своето:
    /// тя мени избора СЛЕД събитието за край на писането.
    /// </summary>
    private void BeginInvokeIfShown(Action act)
    {
        if (IsHandleCreated) BeginInvoke(act);
        else act();
    }

    /// <summary>Избира или изчиства всичко, което се вижда.</summary>
    private void SetAll(bool whole)
    {
        if (whole) _grid.SelectAll();
        else _grid.ClearSelection();

        Picked();
    }

    /// <summary>
    /// Взима отгоре надолу, в реда на таблицата, до написаното. Скритото от
    /// търсенето и пресяването излиза: целта е точно толкова от видимото.
    /// </summary>
    private void FromTop(decimal amount)
    {
        var ordered = _grid.Rows.Cast<DataGridViewRow>()
            .Where(r => r.Visible)
            .Select(r => r.Tag)
            .OfType<StockShare>()
            .ToList();

        var picks = Handover.FromTop(ordered, amount);

        _takes.Clear();
        _typed.Clear();

        foreach (var pick in picks)
        {
            _takes[pick.Share.Batch.Id] = pick.Amount;

            // Изравнената партида е „писана“: извадена и върната, тя се връща
            // с изравненото, а не цяла.
            if (pick.Amount < pick.Share.Amount) _typed[pick.Share.Batch.Id] = pick.Amount;
        }

        foreach (DataGridViewRow row in _grid.Rows)
            if (row.Tag is StockShare share)
                Show(row, _takes.GetValueOrDefault(share.Batch.Id));

        ShowChoice();
        Weigh();

        _fromTop?.Report(picks, amount);
    }

    // ------------------------------------------------------------- търсене

    /// <summary>Думите от търсенето — всяка трябва да се намери някъде в реда.</summary>
    private string[] Words() =>
        _search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Дали редът отговаря на търсенето.
    ///
    /// По думи, а не по целия низ: „метал 0412“ намира партидата на Метал ООД
    /// с протокол 0412, без да се пита в коя колона стои всяко.
    /// </summary>
    private bool Found(DataGridViewRow row)
    {
        var words = Words();
        if (words.Length == 0) return true;

        var text = string.Join(" ", row.Cells.Cast<DataGridViewCell>().Select(c => c.Value?.ToString()));

        return words.All(w => text.Contains(w, StringComparison.CurrentCultureIgnoreCase));
    }

    private void Searched()
    {
        _filter.Apply();
        ShowChoice();
        Weigh();
    }

    // ------------------------------------------------------------- сбор

    /// <summary>Сборът отдолу: колко партиди и колко тона от колко налично.</summary>
    private void Weigh()
    {
        var take = SplitMath.Round(_takes.Values.Sum());
        var stock = SplitMath.Round(_shares.Sum(s => s.Amount));

        _sum.Text = Lang.T(
            $"Избрани {_takes.Count} от {_shares.Count} партиди · {SplitMath.Amount(take)} т от {SplitMath.Amount(stock)} т",
            $"{_takes.Count} of {_shares.Count} batches selected · {SplitMath.Amount(take)} t of {SplitMath.Amount(stock)} t");

        _sum.ForeColor = take > 0 ? Theme.Ink : Theme.InkMuted;

        _grid.Columns[TakeColumn].HeaderText = LedgerPage.Summed(_takeTitle, take);

        var shown = _grid.Rows.Cast<DataGridViewRow>().Count(r => r.Visible);

        _heading.Text = shown < _grid.Rows.Count
            ? Lang.T($"{_title} · показани {shown} от {_grid.Rows.Count}",
                     $"{_title} · {shown} of {_grid.Rows.Count} shown")
            : _title;
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keys)
    {
        if (keys == (Keys.Control | Keys.F))
        {
            _search.Focus();
            return true;
        }

        return base.ProcessCmdKey(ref message, keys);
    }

    // ------------------------------------------------------------- за теста

    internal int RowCount => _grid.Rows.Count;

    internal int VisibleCount => _grid.Rows.Cast<DataGridViewRow>().Count(r => r.Visible);

    internal string SumText => _sum.Text;

    /// <summary>Пише в търсенето, както човек.</summary>
    internal void UseSearch(string text) => _search.Text = text;

    /// <summary>Пресява по колона — както с десния бутон.</summary>
    internal void FilterForTest(string column, IEnumerable<string>? allowed) => _filter.Set(column, allowed);

    /// <summary>Подрежда по колона — както с щракване по заглавието.</summary>
    internal void SortForTest(string column, bool down) =>
        _grid.Sort(_grid.Columns[column],
            down ? System.ComponentModel.ListSortDirection.Descending
                 : System.ComponentModel.ListSortDirection.Ascending);

    /// <summary>Наличното по ред — за проверка на подредбата.</summary>
    internal decimal StockAt(int row) => ((StockShare)_grid.Rows[row].Tag!).Amount;

    /// <summary>Избира редове, както човек с Ctrl — и прилага избора.</summary>
    internal void PickRows(params int[] rows)
    {
        _filling = true;

        try
        {
            _grid.ClearSelection();

            foreach (var row in rows)
                if (row >= 0 && row < _grid.Rows.Count)
                    _grid.Rows[row].Selected = true;
        }
        finally
        {
            _filling = false;
        }

        Picked();
    }

    /// <summary>Пише количество по ред — както човек в клетката.</summary>
    internal void UseTake(int row, decimal amount)
    {
        if (row < 0 || row >= _grid.Rows.Count) return;

        _grid.Rows[row].Cells[TakeColumn].Value = SplitMath.Amount(amount);
        Wrote(row);
    }

    /// <summary>Пише количество и натиска „Вземи“, както човек.</summary>
    internal void UseFromTop(decimal amount) => FromTop(amount);

    /// <summary>Натиска „Всичко“ или „Нищо“.</summary>
    internal void UseAll(bool whole) => SetAll(whole);
}
