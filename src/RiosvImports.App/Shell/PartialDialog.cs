using RiosvImports.App.Controls;
using RiosvImports.Core.Ledger;
using RiosvImports.Core.Localization;
using RiosvImports.Core.Values;

namespace RiosvImports.App.Shell;

/// <summary>
/// Частично взимане: колко точно от всяка избрана партида.
///
/// В големия прозорец стоят всички партиди — на площадка със седемдесет
/// доставки списъкът е дълъг и писането по него е писане в движеща се цел:
/// щракваш в клетка, превърташ, губиш реда. А работата е друга: „от тези
/// шестте вземам по толкова“.
///
/// Затова тук влизат САМО избраните. Нищо друго не се вижда, нищо друго не
/// може да се пипне, а сборът стои отдолу и се мени с всяко число.
///
/// Прозорчето не пише в базата и не решава нищо: връща по колко се взима от
/// всяка партида. Какво става после е работа на големия прозорец.
///
/// Затова и вид на движението му е нужен само за надписите: третиране и
/// предаване искат едно и също число, а го наричат различно. Едно прозорче за
/// двете, а не две почти еднакви, които се разминават на първата поправка.
///
/// С едно изключение: при предаването тук стои и кантарът. Човек, който е
/// оставил само избраните пред себе си, решава количествата точно срещу
/// измереното — да се връща за това в големия прозорец значи да разпределя на
/// едно място, а да сверява на друго.
/// </summary>
internal sealed class PartialDialog : Dialog
{
    private const string BatchColumn = "__batch";
    private const string PartnerColumn = "__partner";
    private const string ReceivedColumn = "__received";
    private const string StockColumn = "__stock";
    private const string TakeColumn = "__take";

    private readonly IReadOnlyList<StockShare> _shares;

    /// <summary>Кое движение се готви — от него идват надписите и нищо друго.</summary>
    private readonly MoveKind _kind;

    private readonly DataGridView _grid = new();
    private readonly Label _sum = new();
    private readonly Label _heading = new();

    /// <summary>Пресяването — десен бутон по заглавието, както навсякъде.</summary>
    private readonly GridFilter _filter;

    /// <summary>Кантарът — само при предаване.</summary>
    private readonly TextBox _target = new();

    /// <summary>Как стои взетото спрямо кантара и какво ще направи изравняването.</summary>
    private readonly Label _gap = new();

    /// <summary>
    /// Взетото от партиди, които не са тук — по други кодове в същото предаване.
    ///
    /// Кантарът мери целия камион, а не само показаното. Без това число
    /// разпределеното тук би се сверявало срещу цялото и винаги би „не
    /// достигало“ с чуждото.
    /// </summary>
    private readonly decimal _outside;

    /// <summary>По колко се взима от всяка партида — това връща прозорчето.</summary>
    private readonly Dictionary<long, decimal> _takes = [];

    public PartialDialog(
        IReadOnlyList<StockShare> shares,
        IReadOnlyDictionary<long, decimal> taken,
        MoveKind kind = MoveKind.Treated,
        decimal target = 0m,
        decimal outside = 0m)
    {
        _shares = [.. shares.OrderBy(s => s.Batch.ReceivedOn).ThenBy(s => s.Batch.Id)];
        _kind = kind;
        _outside = SplitMath.Round(Math.Max(0m, outside));
        _filter = new GridFilter(_grid);

        if (target > 0) _target.Text = SplitMath.Amount(target);

        foreach (var share in _shares)
            if (taken.TryGetValue(share.Batch.Id, out var amount) && amount > 0)
                _takes[share.Batch.Id] = Math.Min(amount, share.Amount);

        Text = Title;
        Font = Theme.Base;
        BackColor = Theme.Canvas;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimumSize = new Size(Theme.Scaled(880), Theme.Scaled(Weighed ? 520 : 420));
        ClientSize = new Size(
            Theme.Scaled(1020),
            Math.Min(Theme.Scaled(Weighed ? 860 : 760),
                Theme.Scaled(Weighed ? 420 : 320) + (Theme.Base.Height + 10) * Math.Max(1, _shares.Count)));
        KeyPreview = true;

        // Пълнещият влиза пръв; от закачените отгоре последният застава най-горе.
        Controls.Add(BuildBody());
        if (Weighed) Controls.Add(BuildScale());
        Controls.Add(BuildTitle());
        Controls.Add(BuildButtons());

        Fill();

        Theme.Apply(this);
    }

    /// <summary>По колко се взима от всяка партида. Празно значи „нищо оттам“.</summary>
    public IReadOnlyDictionary<long, decimal> Takes => _takes;

    /// <summary>Заявеното на кантара. Нула значи „не е казано“.</summary>
    public decimal Target =>
        SplitMath.TryRead(_target.Text, out var amount) && amount > 0 ? SplitMath.Round(amount) : 0m;

    /// <summary>Дали прозорчето носи кантар — само предаването го има.</summary>
    private bool Weighed => _kind == MoveKind.Submitted;

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
                "Напишете колко се взима от всяка; празно значи „нищо оттам“. Работи като в Excel: " +
                "Ctrl+C копира избраните клетки, Ctrl+V залепва колона (едно число върху избрана " +
                "област го слага навсякъде), Ctrl+D повтаря числото отгоре, Delete изчиства. " +
                "Заглавие: щракване подрежда, десен бутон пресява.",
                "Type how much is taken from each; blank means “nothing from there”. It works like " +
                "Excel: Ctrl+C copies the selected cells, Ctrl+V pastes a column (one value over a " +
                "selection fills it), Ctrl+D repeats the value above, Delete clears. " +
                "Header: click sorts, right-click filters."),
            Font = Theme.Base,
            ForeColor = Theme.InkMuted
        });

        _heading.Dock = DockStyle.Top;
        _heading.Height = Theme.Scaled(34);
        _heading.Text = Heading;
        _heading.Font = Theme.Title;
        _heading.ForeColor = Theme.Ink;
        host.Controls.Add(_heading);

        return host;
    }

    private string Heading => Lang.T($"{Title} — {_shares.Count} партиди",
                                     $"{Title} — {_shares.Count} batches");

    /// <summary>
    /// Кантарът: колко е предадено общо, и двете копчета, които го изравняват.
    ///
    /// Същото, което стои и в големия прозорец, и по същите правила
    /// (<see cref="Handover"/>): „Изравни“ слага разликата в реда, на който
    /// стоите, „Пропорционално“ я разнася по всички взети.
    /// </summary>
    private Control BuildScale()
    {
        var host = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(96),
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, 0, Theme.GapLarge, Theme.GapSmall)
        };

        var card = new Card
        {
            Dock = DockStyle.Fill,
            Fill = Theme.Surface,
            Outline = Theme.Border,
            Stripe = Theme.Accent,
            Padding = new Padding(Theme.Gap, 6, Theme.Gap, 6)
        };

        var line = new Panel { Dock = DockStyle.Top, Height = Theme.Scaled(38), BackColor = Theme.Surface };

        var caption = new Label
        {
            Dock = DockStyle.Left,
            Width = Theme.Scaled(170),
            Text = Lang.T("Предадено общо, т", "Handed over in total, t"),
            Font = Theme.Bold,
            ForeColor = Theme.InkStrong,
            BackColor = Theme.Surface,
            TextAlign = ContentAlignment.MiddleLeft
        };

        _target.Dock = DockStyle.Left;
        _target.Width = Theme.Scaled(136);
        _target.Font = Theme.Title;
        _target.TextAlign = HorizontalAlignment.Right;
        Theme.StyleInput(_target);
        _target.TextChanged += (_, _) => Weigh();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Surface,
            Padding = new Padding(Theme.Gap, 0, 0, 0)
        };

        var level = Theme.SecondaryButton(Lang.T("Изравни", "Balance"));
        level.Height = Theme.Scaled(32);
        level.Margin = new Padding(0, 0, Theme.GapSmall, 0);
        level.Click += (_, _) => Level();

        new ToolTip().SetToolTip(level, Lang.T(
            "Слага разликата до заявеното в реда, на който стоите.",
            "Puts the difference from the stated total into the row you are standing on."));

        var spread = Theme.GhostButton(Lang.T("Пропорционално", "Proportionally"));
        spread.Height = Theme.Scaled(32);
        spread.Click += (_, _) => Proportional();

        new ToolTip().SetToolTip(spread, Lang.T(
            "Разнася заявеното по взетите редове, в тяхното съотношение. Нищо ли не е " +
            "взето — по видимите, според наличното.",
            "Spreads the stated total across the rows taken, in their own proportion. If nothing " +
            "is taken yet — across the visible rows, by what is available."));

        buttons.Controls.Add(level.FitWidth());
        buttons.Controls.Add(spread.FitWidth());

        // Закачените отляво се редят в обратен ред на добавяне.
        line.Controls.Add(buttons);
        line.Controls.Add(_target);
        line.Controls.Add(caption);

        _gap.Dock = DockStyle.Fill;
        _gap.Font = Theme.SmallBold;
        _gap.ForeColor = Theme.InkMuted;
        _gap.BackColor = Theme.Surface;
        _gap.TextAlign = ContentAlignment.MiddleLeft;

        // Пълнещият влиза пръв (виж АРХИТЕКТУРА.md).
        card.Controls.Add(_gap);
        card.Controls.Add(line);

        host.Controls.Add(card);

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
        // Избор по КЛЕТКИ, а не по редове.
        //
        // Тук се работи с една колона: маркира се област в нея, копира се,
        // залепва се, изчиства се. Изборът по цял ред би влачил и трите
        // описателни колони във всяко копиране и би правил от „тези четири
        // количества“ четири реда текст.
        _grid.MultiSelect = true;
        _grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;

        // Без заглавия: копираното оттук се залепва обратно в лист като числа,
        // а не като таблица с шапка.
        _grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
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

        // Ширините са сметнати да се съберат: колоната, в която се пише, не
        // бива да остава зад воден плъзгач — тя е причината прозорчето да го има.
        Column(BatchColumn, Lang.T("Партида", "Batch"), 120);
        Column(PartnerColumn, Lang.T("Клиент", "Client"), 260, fill: true);
        Column(ReceivedColumn, Lang.T("Приета на · документ", "Received · document"), 200);
        Column(StockColumn, Lang.T("Налично, т", "Available, t"), 130, right: true);
        Column(TakeColumn, TakeTitle, 170, right: true, write: true);

        _grid.CellEndEdit += (_, e) => Wrote(e.RowIndex);

        // Ляв бутон подрежда, десен пресява — както в редактора на отчета.
        _grid.SortCompare += Compare;
        _grid.ColumnHeaderMouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Right) _filter.Open(e.ColumnIndex);
        };

        _filter.Changed += Weigh;

        // Курсорът е редът, който поема разликата — казва се веднага.
        _grid.CurrentCellChanged += (_, _) =>
        {
            if (Weighed) ShowGap();
        };
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
        const int height = 40;

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
            Dock = DockStyle.Left,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Canvas
        };

        quick.Controls.Add(Quick(Lang.T("Всичко", "Everything"), whole: true));
        quick.Controls.Add(Quick(Lang.T("Нищо", "Nothing"), whole: false));

        _sum.AutoSize = false;
        _sum.Width = Theme.Scaled(420);
        _sum.Height = height;
        _sum.TextAlign = ContentAlignment.MiddleLeft;
        _sum.Font = Theme.Bold;
        _sum.ForeColor = Theme.Ink;
        _sum.Margin = new Padding(Theme.Gap, 0, 0, 0);

        quick.Controls.Add(_sum);

        // Пълнещият влиза пръв; закачените отдясно взимат своето.
        bar.Controls.Add(quick);
        bar.Controls.Add(tools);

        AcceptButton = ok;
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
                : Lang.T("(клиент не е посочен)", "(no client stated)");

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

        Weigh();
    }

    /// <summary>Изписва едно количество и казва с цвят дали редът участва.</summary>
    private static void Show(DataGridViewRow row, decimal take)
    {
        row.Cells[TakeColumn].Value = take > 0 ? SplitMath.Amount(take) : string.Empty;
        row.Cells[TakeColumn].Style.ForeColor = take > 0 ? Theme.Accent : Theme.InkFaint;
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
            Show(_grid.Rows[index], 0m);
        }
        else
        {
            var take = Math.Min(SplitMath.Round(amount), share.Amount);

            _takes[share.Batch.Id] = take;
            Show(_grid.Rows[index], take);
        }

        Weigh();
    }

    /// <summary>Пълни всичко или изчиства всичко — при двайсет реда е по-кратко.</summary>
    private void SetAll(bool whole)
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            // Пресятото не се пипа: „Всичко“ значи всичко, което се вижда.
            if (row.Tag is not StockShare share || !row.Visible) continue;

            if (whole) _takes[share.Batch.Id] = share.Amount;
            else _takes.Remove(share.Batch.Id);

            Show(row, whole ? share.Amount : 0m);
        }

        Weigh();
    }

    /// <summary>Сборът отдолу: колко се взима и от колко налично.</summary>
    private void Weigh()
    {
        var take = SplitMath.Round(_takes.Values.Sum());
        var stock = SplitMath.Round(_shares.Sum(s => s.Amount));

        _sum.Text = Lang.T(
            $"{TakeWord}: {SplitMath.Amount(take)} т от {SplitMath.Amount(stock)} т налични",
            $"{TakeWord}: {SplitMath.Amount(take)} t out of {SplitMath.Amount(stock)} t available");

        _sum.ForeColor = take > 0 ? Theme.Ink : Theme.InkMuted;

        _grid.Columns[TakeColumn].HeaderText = LedgerPage.Summed(TakeTitle, take);

        _heading.Text = _filter.Active ? $"{Heading} · {_filter.Note()}" : Heading;

        if (Weighed) ShowGap();
    }

    /// <summary>Сборът, който кантарът сверява: взетото тук и взетото извън тук.</summary>
    private Weighing Weighing() =>
        new(SplitMath.Round(_takes.Values.Sum() + _outside), Target);

    /// <summary>Редът, на който стои курсорът — той поема разликата при „Изравни“.</summary>
    private DataGridViewRow? Anchor() =>
        _grid.CurrentRow is { Visible: true, Tag: StockShare } row ? row : null;

    /// <summary>
    /// Казва как стои взетото спрямо кантара и какво ще направи „Изравни“ —
    /// преди да бъде натиснато (същото правило като в големия прозорец).
    /// </summary>
    private void ShowGap()
    {
        var weighing = Weighing();

        var outside = _outside > 0
            ? Lang.T($" (от тях {SplitMath.Amount(_outside)} т от партиди извън този списък)",
                     $" (of which {SplitMath.Amount(_outside)} t from batches not listed here)")
            : string.Empty;

        if (!weighing.Stated)
        {
            _gap.Text = Lang.T(
                "Не е заявено общо количество — предава се точно написаното.",
                "No total is stated — exactly what is typed gets handed over.");
            _gap.ForeColor = Theme.InkFaint;
            return;
        }

        if (weighing.Matches)
        {
            _gap.Text = Lang.T(
                $"Взетото е точно заявеното: {SplitMath.Amount(weighing.Target)} т{outside}.",
                $"What is taken is exactly what is stated: {SplitMath.Amount(weighing.Target)} t{outside}.");
            _gap.ForeColor = Theme.Ok;
            return;
        }

        var size = SplitMath.Amount(Math.Abs(weighing.Gap));

        _gap.Text = weighing.Short
            ? Lang.T($"Взето {SplitMath.Amount(weighing.Picked)} т{outside} · не достигат {size} т.",
                     $"Taken {SplitMath.Amount(weighing.Picked)} t{outside} · {size} t are missing.")
            : Lang.T($"Взето {SplitMath.Amount(weighing.Picked)} т{outside} · {size} т в повече.",
                     $"Taken {SplitMath.Amount(weighing.Picked)} t{outside} · {size} t too many.");

        if (Anchor() is { Tag: StockShare share })
            _gap.Text += Lang.T($" „Изравни“ ги слага в #{share.Batch.Code}.",
                                $" “Balance” puts them into #{share.Batch.Code}.");

        _gap.ForeColor = Theme.Warn;
    }

    /// <summary>„Изравни“: разликата сяда в реда, на който стои курсорът.</summary>
    private void Level()
    {
        var weighing = Weighing();

        if (!weighing.Stated || weighing.Matches) return;
        if (Anchor() is not { Tag: StockShare share } row) return;

        var (amount, _) = Handover.Onto(_takes.GetValueOrDefault(share.Batch.Id), share.Amount, weighing.Gap);

        if (amount > 0) _takes[share.Batch.Id] = amount;
        else _takes.Remove(share.Batch.Id);

        Show(row, amount);
        Weigh();
    }

    /// <summary>
    /// „Пропорционално“: заявеното се разнася по взетите редове.
    ///
    /// Нищо ли не е взето, основа са видимите редове с цялото им налично — това
    /// е „от тези, според колкото има от всяка“, най-честият случай, когато
    /// кантарът е дошъл преди разпределението.
    /// </summary>
    private void Proportional()
    {
        var wanted = SplitMath.Round(Target - _outside);
        if (wanted <= 0) return;

        var rows = _grid.Rows.Cast<DataGridViewRow>()
            .Where(r => r.Tag is StockShare)
            .ToList();

        var basis = _takes.Count > 0
            ? rows.Select(r => (StockShare)r.Tag!)
                .Where(s => _takes.ContainsKey(s.Batch.Id))
                .Select(s => new TreatPick(s, _takes[s.Batch.Id]))
                .ToList()
            : rows.Where(r => r.Visible)
                .Select(r => (StockShare)r.Tag!)
                .Select(s => new TreatPick(s, s.Amount))
                .ToList();

        if (basis.Count == 0) return;

        var scaled = Handover.Scale(basis, wanted).ToDictionary(p => p.Share.Batch.Id, p => p.Amount);

        foreach (var pick in basis) _takes.Remove(pick.Share.Batch.Id);
        foreach (var (id, amount) in scaled) _takes[id] = amount;

        foreach (var row in rows)
            Show(row, _takes.GetValueOrDefault(((StockShare)row.Tag!).Batch.Id));

        Weigh();
    }

    // ------------------------------------------------------------- залепване

    /// <summary>
    /// Залепва като в Excel.
    ///
    /// Едно число върху избрана област го слага навсякъде в нея — точно както
    /// прави и таблицата с данни в редактора. Колона числа се залепва НАДОЛУ от
    /// текущия ред, по едно на ред.
    ///
    /// Празният ред маха количеството, нечетимото се пропуска, а над наличното
    /// се свежда до него. Връща колко реда са поети.
    /// </summary>
    internal int Paste(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var values = lines
            .Select(line => line.Split('\t')[0].Trim())
            .ToList();

        // Последният празен ред на залепването не е стойност: Excel слага нов
        // ред след последната клетка.
        while (values.Count > 1 && values[^1].Length == 0) values.RemoveAt(values.Count - 1);

        if (values.Count == 0) return 0;

        var chosen = Chosen();

        // Едно число върху няколко избрани клетки: слага се във всички.
        if (values.Count == 1 && chosen.Count > 1)
        {
            foreach (var row in chosen) Write(row, values[0]);

            return chosen.Count;
        }

        var at = _grid.CurrentCell?.RowIndex ?? 0;
        if (at < 0) at = 0;

        var taken = 0;

        foreach (var value in values)
        {
            // Скритите от пресяването се прескачат, както в Excel.
            while (at < _grid.Rows.Count && !_grid.Rows[at].Visible) at++;

            if (at >= _grid.Rows.Count) break;

            Write(at, value);

            at++;
            taken++;
        }

        return taken;
    }

    /// <summary>Редовете, чиито клетки са избрани — по ред, без повторения.</summary>
    private List<int> Chosen() =>
    [
        .. _grid.SelectedCells
            .Cast<DataGridViewCell>()
            .Where(c => _grid.Columns[c.ColumnIndex].Name == TakeColumn)
            .Select(c => c.RowIndex)
            .Distinct()
            .OrderBy(i => i)
    ];

    /// <summary>Слага стойност в един ред и я прекарва през същата проверка.</summary>
    private void Write(int row, string value)
    {
        if (row < 0 || row >= _grid.Rows.Count) return;

        _grid.Rows[row].Cells[TakeColumn].Value = value;
        Wrote(row);
    }

    /// <summary>
    /// Delete изчиства избраните клетки, без да ги трие.
    ///
    /// Редът остава — той е партида, а не запис, който човек е добавил. Празно
    /// значи „нищо оттам“.
    /// </summary>
    private void Clear()
    {
        foreach (var row in Chosen()) Write(row, string.Empty);
    }

    /// <summary>
    /// Ctrl+D повтаря стойността от реда отгоре.
    ///
    /// Същият клавиш върши същото и в таблицата с данни: при десет еднакви
    /// количества той е по-кратък от десет писания.
    /// </summary>
    private void Down()
    {
        var chosen = Chosen();

        if (chosen.Count == 0 && _grid.CurrentCell is { RowIndex: > 0 } cell)
            chosen = [cell.RowIndex];

        foreach (var row in chosen)
        {
            var above = row - 1;
            while (above >= 0 && !_grid.Rows[above].Visible) above--;

            if (above >= 0)
                Write(row, _grid.Rows[above].Cells[TakeColumn].Value?.ToString() ?? string.Empty);
        }
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keys)
    {
        // Копирането го върши самата таблица: Ctrl+C взима избраните клетки.
        switch (keys)
        {
            case Keys.Control | Keys.V:
                try
                {
                    if (Clipboard.ContainsText()) Paste(Clipboard.GetText());
                }
                catch (System.Runtime.InteropServices.ExternalException)
                {
                    // Буферът е зает от друга програма — залепването не става.
                }

                return true;

            case Keys.Delete when !_grid.IsCurrentCellInEditMode:
                Clear();
                return true;

            case Keys.Control | Keys.D:
                Down();
                return true;

            case Keys.Escape:
                DialogResult = DialogResult.Cancel;
                return true;
        }

        return base.ProcessCmdKey(ref message, keys);
    }

    // ------------------------------------------------------------- за теста

    internal int RowCount => _grid.Rows.Count;

    internal string SumText => _sum.Text;

    internal string TakeAt(int row) => _grid.Rows[row].Cells[TakeColumn].Value?.ToString() ?? string.Empty;

    /// <summary>Пише количество по ред — както човек в клетката.</summary>
    internal void UseTake(int row, decimal amount)
    {
        if (row < 0 || row >= _grid.Rows.Count) return;

        _grid.Rows[row].Cells[TakeColumn].Value = SplitMath.Amount(amount);
        Wrote(row);
    }

    /// <summary>Натиска „Всичко“ или „Нищо“.</summary>
    internal void UseAll(bool whole) => SetAll(whole);

    /// <summary>Пише в кантара, както човек.</summary>
    internal void UseTarget(decimal amount) =>
        _target.Text = amount > 0 ? SplitMath.Amount(amount) : string.Empty;

    /// <summary>Застава на ред и натиска „Изравни“.</summary>
    internal void UseLevel(int row)
    {
        if (row >= 0 && row < _grid.Rows.Count)
            _grid.CurrentCell = _grid.Rows[row].Cells[TakeColumn];

        Level();
    }

    /// <summary>Натиска „Пропорционално“.</summary>
    internal void UseProportional() => Proportional();

    /// <summary>Какво казва редът под кантара.</summary>
    internal string GapText => _gap.Text;

    /// <summary>Пресява по колона — както с десния бутон.</summary>
    internal void FilterForTest(string column, IEnumerable<string>? allowed) => _filter.Set(column, allowed);

    /// <summary>Колко реда се виждат.</summary>
    internal int VisibleCount => _filter.Visible;

    /// <summary>Подрежда по колона — както с щракване по заглавието.</summary>
    internal void SortForTest(string column, bool down) =>
        _grid.Sort(_grid.Columns[column],
            down ? System.ComponentModel.ListSortDirection.Descending
                 : System.ComponentModel.ListSortDirection.Ascending);

    /// <summary>Наличното по ред — за проверка на подредбата.</summary>
    internal decimal StockAt(int row) => ((StockShare)_grid.Rows[row].Tag!).Amount;

    /// <summary>
    /// Залепва след щракване в един ред — без да се пипа буферът на машината.
    ///
    /// Щракването свива избора до тази клетка; точно затова оттук се залепва
    /// колона, а не се разлива едно число.
    /// </summary>
    internal int PasteFrom(int row, string text)
    {
        if (row >= 0 && row < _grid.Rows.Count)
        {
            _grid.CurrentCell = _grid.Rows[row].Cells[TakeColumn];
            _grid.ClearSelection();
            _grid.Rows[row].Cells[TakeColumn].Selected = true;
        }

        return Paste(text);
    }

    /// <summary>Залепва върху вече избраното — както Ctrl+V след маркиране.</summary>
    internal int PasteHere(string text) => Paste(text);

    /// <summary>
    /// Избира клетки в колоната с количествата — както с мишката.
    ///
    /// Текущата клетка се слага ПЪРВА и чак после се трупа изборът: присвояване
    /// на текуща клетка свива избора до нея и куката би показвала една избрана
    /// клетка там, където човек е маркирал пет. Веднъж вече мина зелен тест по
    /// този начин — на неотворен прозорец, където присвояването не прави нищо.
    /// </summary>
    internal void PickCells(params int[] rows)
    {
        if (rows.Length == 0)
        {
            _grid.ClearSelection();
            return;
        }

        if (rows[0] >= 0 && rows[0] < _grid.Rows.Count)
            _grid.CurrentCell = _grid.Rows[rows[0]].Cells[TakeColumn];

        _grid.ClearSelection();

        foreach (var row in rows)
            if (row >= 0 && row < _grid.Rows.Count)
                _grid.Rows[row].Cells[TakeColumn].Selected = true;
    }

    /// <summary>Колко клетки в колоната с количествата са избрани.</summary>
    internal int ChosenCount => Chosen().Count;

    /// <summary>Delete върху избраните клетки.</summary>
    internal void ClearCells() => Clear();

    /// <summary>Ctrl+D върху избраните клетки.</summary>
    internal void RepeatAbove() => Down();

    /// <summary>Какво би отишло в буфера при Ctrl+C.</summary>
    internal string Copied() => _grid.GetClipboardContent()?.GetText() ?? string.Empty;

    /// <summary>
    /// Как се казва прозорчето — по движението, което се готви.
    ///
    /// Свойство, а не поле: статичното поле се пълни веднъж, с езика, който е
    /// бил в сила тогава (правило 8).
    /// </summary>
    private string Title => _kind == MoveKind.Submitted
        ? Lang.T("Частично предаване", "Partial hand-over")
        : Lang.T("Частично третиране", "Partial treatment");

    /// <summary>Как се казва колоната, в която се пише количеството.</summary>
    private string TakeTitle => _kind == MoveKind.Submitted
        ? Lang.T("За предаване, т", "To hand over, t")
        : Lang.T("За третиране, т", "To treat, t");

    /// <summary>Същото, но без мерната единица — за реда със сбора.</summary>
    private string TakeWord => _kind == MoveKind.Submitted
        ? Lang.T("За предаване", "To hand over")
        : Lang.T("За третиране", "To treat");
}
