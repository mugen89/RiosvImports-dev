using RiosvImports.App.Controls;
using RiosvImports.Core;
using RiosvImports.Core.Import;
using RiosvImports.Core.Ledger;
using RiosvImports.Core.Localization;
using RiosvImports.Core.Schema;
using RiosvImports.Core.Values;
using RiosvImports.Data;

namespace RiosvImports.App.Shell;

/// <summary>
/// Месецът: всичко, което е минало през площадката, в реда, в който е минало.
///
/// Това е екранът, на който се работи. Отчетът вече не се попълва — той се
/// съставя от записаното тук, — а наличността не се въвежда, а излиза от
/// движенията.
///
/// Четирите вида движение стоят в една таблица, а не в четири раздела: човек
/// въвежда деня, а денят не е разделен на приемане и третиране. Ситото отгоре
/// свежда до един вид, когато трябва.
/// </summary>
internal sealed class LedgerPage : ContentPage
{
    private const string DateColumn = "__date";
    private const string KindColumn = "__kind";
    private const string CodeColumn = "__code";
    private const string AmountColumn = "__amount";
    private const string OriginColumn = "__origin";
    private const string NoteColumn = "__note";

    /// <summary>На кого е предадено — празен при другите три вида.</summary>
    private const string ReceiverColumn = "__receiver";
    private const string ContractColumn = "__contract";

    /// <summary>Колко месеца назад се предлагат, дори да са празни.</summary>
    private const int MonthsBack = 36;

    /// <summary>
    /// Докъде се стига напред с търкаляне.
    ///
    /// Списъкът сам предлага до края на годината; търкалянето стига година
    /// напред, за да може януари да се отвори и през декември. По-нататък няма
    /// какво да се въвежда — документ с такава дата още не съществува.
    /// </summary>
    private const int MonthsAhead = 12;

    private readonly ImportService _service;
    private readonly Workspace _workspace;
    private readonly CompanyScope _scope;

    private readonly ComboBox _stage = new();
    private readonly ComboBox _month = new();
    private readonly SegmentedControl _kind = new();
    private readonly DataGridView _grid = new();
    private readonly Label _summary = new();

    /// <summary>
    /// Присъдата за месеца — един ред, който казва може ли отчет и защо не.
    ///
    /// Дотук тук стоеше първата бележка от списъка, отрязана на един ред: „3
    /// спиращи · Код 170405: на 12.07…“. Не казваше дали спира отчетът или само
    /// затварянето, нито какво да се направи — а точно това питаха.
    /// </summary>
    private readonly Label _checks = new();

    /// <summary>Под присъдата: какво да се направи, с мястото, където се прави.</summary>
    private readonly Label _fix = new();

    /// <summary>Търсене в месеца — по код, клиент, получател, документ, договор и бележка.</summary>
    private readonly SearchBox _search = new(Lang.T(
        "Търсене по код, клиент, документ или бележка  (Ctrl+F)",
        "Search by code, client, document or note  (Ctrl+F)"));

    /// <summary>Копчето „Състави отчет…“ — мери се от теста.</summary>
    private AppButton? _compose;

    /// <summary>„Проверка…“ — целият списък с бележките и какво да се направи.</summary>
    private AppButton? _review;

    /// <summary>Последно намереното за месеца — „Проверка…“ го показва, без да смята наново.</summary>
    private IReadOnlyList<LedgerIssue> _issues = [];

    /// <summary>Какво чака в „Третиран“ в края на месеца — причината, поради която отчет не излиза.</summary>
    private WaitingMonth? _waiting;

    /// <summary>Последната присъда — „Проверка…“ показва нея, а не смята наново.</summary>
    private MonthVerdict? _verdict;

    /// <summary>Колко реда се виждат сега и колко тона носят — следват ситото.</summary>
    private int _shown;
    private decimal _shownAmount;

    /// <summary>Заглавието на екрана — носи и избраната фирма.</summary>
    private readonly Label _pageTitle = new();

    /// <summary>Един месец назад и един напред — без отваряне на списъка.</summary>
    private readonly AppButton _earlier = Theme.GhostButton(string.Empty, Theme.Glyph.ChevronBack);
    private readonly AppButton _later = Theme.GhostButton(string.Empty, Theme.Glyph.Chevron);

    /// <summary>
    /// Месецът, към който се върви.
    ///
    /// Нужен е, защото търкалянето може да излезе извън списъка — назад преди
    /// първия предложен месец не се стига, но напред януари на следващата
    /// година го няма, докато годината не се смени. Поисканият месец влиза в
    /// списъка при следващото му пълнене и оттам нататък е избран като всеки друг.
    /// </summary>
    private LedgerPeriod? _wanted;

    private readonly List<LedgerPeriod> _periods = [];
    private readonly Dictionary<string, string> _names;

    private bool _filling;

    /// <summary>
    /// Фирмата идва отвън — от лентата отляво.
    ///
    /// Подаден null значи „направи си свой обхват“: така страницата се сглобява
    /// и сама, извън обвивката, и това ползва тестът.
    /// </summary>
    public LedgerPage(ImportService service, Workspace workspace, CompanyScope? scope = null) : base(1460)
    {
        _service = service;
        _workspace = workspace;
        _scope = scope ?? new CompanyScope(workspace);
        _names = EwcNames(service);

        _scope.Changed += (_, _) => Refresh();

        Controls.Add(BuildGrid());
        Controls.Add(BuildSummary());
        Controls.Add(BuildToolbar());
        Controls.Add(BuildTitle());

        Refresh();
    }

    // ------------------------------------------------------------- устройство

    private Control BuildTitle()
    {
        var host = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(92),
            BackColor = Theme.Canvas,
            Padding = new Padding(0, 0, 0, Theme.GapSmall)
        };

        // Под заглавието стоят сборовете, а не обяснение какво е този екран.
        //
        // Обяснението се чете веднъж; числата се четат всеки път. Дотук те бяха
        // под лентата с филтрите — на един поглед разстояние от името на екрана
        // и на два от таблицата, която обясняват.
        //
        // Следват ситото: показаното и сборът му се менят с избрания вид.
        _summary.Dock = DockStyle.Top;
        _summary.Height = Theme.Scaled(36);
        _summary.Font = Theme.Base;
        _summary.ForeColor = Theme.InkMuted;

        host.Controls.Add(_summary);

        _pageTitle.Dock = DockStyle.Top;
        _pageTitle.Height = Theme.Scaled(44);
        _pageTitle.Font = Theme.Display;
        _pageTitle.ForeColor = Theme.Ink;
        _pageTitle.Text = Lang.T("Месец", "Month");

        host.Controls.Add(_pageTitle);

        return host;
    }

    private Control BuildToolbar()
    {
        // Два реда, а не един: на един ред обхватът, ситото по вид и четирите
        // копчета се застъпваха — копчетата, закачени отдясно, лягаха върху
        // месеца и го скриваха.
        //
        // Горе е обхватът с действията, долу — ситото по вид. Фирмата вече не е
        // тук: тя се избира веднъж, в лентата отляво.
        var bar = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(76 + 48),
            BackColor = Theme.Canvas,
            Padding = new Padding(0, 0, 0, Theme.Gap)
        };

        foreach (var input in new[] { _stage, _month })
        {
            input.DropDownStyle = ComboBoxStyle.DropDownList;
            input.Font = Theme.Base;
            Theme.StyleInput(input);
        }

        // Смяната на площадка пренарежда месеците — другата площадка е работила
        // през други месеци. Затова тя не се закача за Rebuild.
        _stage.SelectedIndexChanged += (_, _) => SwitchStage();
        _month.SelectedIndexChanged += (_, _) => Rebuild();

        // Всички движения, с рециклирането: в месеца то се вижда като всяко
        // друго, макар да не влиза в отчета.
        _kind.SetItems([Lang.T("Всички", "All"), .. Moves.Every.Select(Moves.Title)]);
        _kind.SelectedIndex = 0;
        _kind.Dock = DockStyle.Left;

        // По надписите, а не кръгло число — шестият дял иначе се реже.
        _kind.Width = Math.Max(Theme.Scaled(460), _kind.Needed);
        _kind.Height = Theme.Scaled(40);
        _kind.SelectedIndexChanged += (_, _) => Rebuild();

        // Търсенето стои до ситото: двете отговарят на „кое от месеца гледам“.
        // Истински месец е петстотин приемания — ред се намира по дума, не с
        // колелцето.
        _search.Dock = DockStyle.Right;
        _search.Width = Theme.Scaled(330);
        _search.TextChanged += (_, _) => Rebuild();

        var tools = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, 0, 0, 0)
        };

        // Приемането е първото действие на месеца и затова е главното копче:
        // без него останалите три нямат от какво да черпят.
        var receive = Theme.PrimaryButton(Moves.Title(MoveKind.Received));
        receive.Glyph = Theme.Glyph.Add;
        receive.Height = Theme.Scaled(40);
        receive.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        receive.Click += (_, _) => Add(MoveKind.Received);

        tools.Controls.Add(receive.FitWidth());
        tools.Controls.Add(Action(Moves.Title(MoveKind.Treated), () => Add(MoveKind.Treated)));
        tools.Controls.Add(Action(Moves.Title(MoveKind.Generated), () => Add(MoveKind.Generated)));
        tools.Controls.Add(Action(Moves.Title(MoveKind.Submitted), () => Add(MoveKind.Submitted)));

        var scope = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas };

        scope.Controls.Add(tools);
        scope.Controls.Add(BuildMonthPicker());
        scope.Controls.Add(Spacer());
        scope.Controls.Add(Labelled(_stage, Lang.T("Площадка", "Site"), 110));

        var filters = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = Theme.Scaled(48),
            BackColor = Theme.Canvas,
            Padding = new Padding(0, Theme.GapSmall, 0, 0)
        };

        filters.Controls.Add(_search);
        filters.Controls.Add(_kind);

        // Пълнещият влиза пръв: закачените се редят наопаки и последният
        // получава своя край, а остатъкът остава за първия.
        bar.Controls.Add(scope);
        bar.Controls.Add(filters);

        return bar;
    }

    /// <summary>
    /// Месецът с двете стрелки.
    ///
    /// Списъкът остава — с него се скача, — но всекидневната работа е „предишния“
    /// и „следващия“: отчетът за август се прави през септември, а поправката по
    /// него се сеща през октомври. Тогава падащ списък с четирийсет месеца е три
    /// движения с мишката за нещо, което е една крачка.
    /// </summary>
    private Control BuildMonthPicker()
    {
        // Поне колкото стрелките до него — те не са по-ниски от надписа си.
        var height = Math.Max(_month.PreferredHeight, Theme.Base.Height + Theme.Scaled(6));

        var row = new Panel { Dock = DockStyle.Top, Height = height, BackColor = Theme.Canvas };

        foreach (var (button, back) in new[] { (_earlier, true), (_later, false) })
        {
            button.Height = height;
            button.Dock = back ? DockStyle.Left : DockStyle.Right;
            button.AccessibleName = back
                ? Lang.T("Предишен месец", "Previous month")
                : Lang.T("Следващ месец", "Next month");

            button.Width = height + Theme.Gap;
            button.Click += (_, _) => Roll(back ? -1 : 1);

            new ToolTip().SetToolTip(button, back
                ? Lang.T("Предишен месец (Ctrl+PageUp)", "Previous month (Ctrl+PageUp)")
                : Lang.T("Следващ месец (Ctrl+PageDown)", "Next month (Ctrl+PageDown)"));
        }

        // Списъкът влиза пръв, за да заеме останалото: закачените се редят
        // наопаки и последният получава своя край.
        _month.Dock = DockStyle.Fill;
        row.Controls.Add(_month);
        row.Controls.Add(_earlier);
        row.Controls.Add(_later);

        var host = new Panel { Dock = DockStyle.Left, Width = Theme.Scaled(230), BackColor = Theme.Canvas };

        host.Controls.Add(row);
        host.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            Height = Theme.Small.Height + 6,
            Text = Lang.T("Месец", "Month"),
            Font = Theme.SmallBold,
            ForeColor = Theme.InkFaint
        });

        return host;
    }

    private Control BuildSummary()
    {
        // Два реда отляво — присъдата и какво да се направи — и двете копчета
        // отдясно. Височината е от шрифтовете, не кръгло число: при 150 % вторият
        // ред иначе излизаше под копчетата и се режеше.
        var text = Theme.Bold.Height + 2 * Theme.Small.Height + Theme.Scaled(10);
        var buttons = Theme.Scaled(40);

        var host = new Panel
        {
            Dock = DockStyle.Top,
            Height = Math.Max(text, buttons) + Theme.Gap,
            BackColor = Theme.Canvas,
            Padding = new Padding(0, 0, 0, Theme.Gap)
        };

        // Съставянето стои тук, а не в лентата: то е краят на месеца, а не
        // едно от нещата, които се правят по няколко пъти на ден.
        //
        // Затварянето на месеца вече не е тук: то е решение веднъж на месец, а
        // стоеше до онова, което се пипа по двайсет пъти на ден. Живее в
        // „Настройки → Затворени месеци“.
        var compose = Theme.GhostButton(Lang.T("Състави отчет…", "Compose report…"), Theme.Glyph.Documents);
        compose.Height = buttons;
        compose.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        compose.Click += (_, _) => Compose();
        _compose = compose;

        // Проверката стои до съставянето, защото отговаря на въпроса преди него:
        // „ще излезе ли отчет — и ако не, какво да пипна“.
        var review = Theme.GhostButton(Lang.T("Проверка…", "Check…"), Theme.Glyph.Search);
        review.Height = buttons;
        review.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        review.Click += (_, _) => Review();
        _review = review;

        new ToolTip().SetToolTip(review, Lang.T(
            "Всичко, което проверката намира в месеца, и какво да се направи за всяко.",
            "Everything the check finds in the month, and what to do about each."));

        var tools = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, 0, 0, 0)
        };

        tools.Controls.Add(compose.FitWidth());
        tools.Controls.Add(review.FitWidth());

        _checks.Dock = DockStyle.Top;
        _checks.Height = Theme.Bold.Height + Theme.Scaled(4);
        _checks.Font = Theme.Bold;
        _checks.ForeColor = Theme.Warn;
        _checks.AutoEllipsis = true;
        _checks.Cursor = Cursors.Hand;
        _checks.Click += (_, _) => Review();

        // Два реда: поправката е изречение с мястото, където се прави, и на
        // един ред се реже точно в края — там, където е „Образувай…“.
        _fix.Dock = DockStyle.Top;
        _fix.Height = 2 * Theme.Small.Height + Theme.Scaled(4);
        _fix.Font = Theme.Small;
        _fix.ForeColor = Theme.InkMuted;
        _fix.AutoEllipsis = true;

        var words = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas };

        // Закачените отгоре се редят наопаки: присъдата влиза последна, за да
        // стои най-горе.
        words.Controls.Add(_fix);
        words.Controls.Add(_checks);

        // Пълнещият влиза пръв: закачените се редят наопаки и копчетата получават
        // своя край, а остатъкът остава за думите.
        host.Controls.Add(words);
        host.Controls.Add(tools);

        return host;
    }

    private Control BuildGrid()
    {
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
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.AllowUserToOrderColumns = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;

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
        // Две реда високо: под името на колоната застава сборът ѝ. На един ред
        // той не се побира и се реже точно там, където значи нещо.
        _grid.ColumnHeadersHeight = 2 * line + 18;

        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Edit(); };

        // Поправянето и махането стоят в контекстното меню, а не в лентата:
        // тя вече носи четирите вида въвеждане, а те се ползват на всеки ред.
        var menu = new ContextMenuStrip { Font = Theme.Base };

        menu.Items.Add(Lang.T("Поправи…", "Edit…"), null, (_, _) => Edit());
        menu.Items.Add(Lang.T("Махни", "Remove"), null, (_, _) => Remove());
        menu.Opening += (_, e) => e.Cancel = Selected() is null;

        _grid.ContextMenuStrip = menu;

        // Десният бутон не мести избора сам — иначе менюто важи за друг ред.
        _grid.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;

            _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[0];
        };

        AddColumn(DateColumn, Lang.T("Дата", "Date"), 100);
        AddColumn(KindColumn, Lang.T("Движение", "Movement"), 130);
        AddColumn(CodeColumn, Lang.T("Код", "Code"), 320, fill: true);
        AddColumn(AmountColumn, Lang.T("Количество, т", "Quantity, t"), 130, right: true);
        AddColumn(OriginColumn, Lang.T("Произход", "Origin"), 280, fill: true);

        // „Произход“ и „Получател“ са двата края на един и същ въпрос: от кого
        // е дошло и на кого е отишло. Четат се заедно и затова стоят един до
        // друг. Само предаването има получател — на останалите три вида стълбът
        // стои празен, вместо да повтаря нещо друго на негово място.
        AddColumn(ReceiverColumn, Lang.T("Получател", "Receiver"), 220);
        AddColumn(ContractColumn, Moves.ContractTitle, 160);
        AddColumn(NoteColumn, Lang.T("Бележка", "Note"), 160);

        frame.Controls.Add(_grid);

        return frame;
    }

    private void AddColumn(string name, string header, int width, bool right = false, bool fill = false)
    {
        var column = new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            Width = Theme.Scaled(width),
            SortMode = DataGridViewColumnSortMode.NotSortable,
            Resizable = DataGridViewTriState.True
        };

        if (right)
        {
            column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
        }

        if (fill)
        {
            column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            column.FillWeight = width;
            column.MinimumWidth = Theme.Scaled(180);
        }

        _grid.Columns.Add(column);
    }

    // ------------------------------------------------------------- пълнене

    /// <summary>Чете наново от базата: фирми, площадки, месеци — и после таблицата.</summary>
    public new void Refresh()
    {
        _filling = true;

        try
        {
            FillStages();
            FillMonths();
        }
        finally
        {
            _filling = false;
        }

        Rebuild();
        base.Refresh();
    }

    /// <summary>Смяна на площадката: месеците се четат наново.</summary>
    private void SwitchStage()
    {
        if (_filling) return;

        _filling = true;

        try
        {
            FillMonths();
        }
        finally
        {
            _filling = false;
        }

        Rebuild();
    }

    private void FillStages()
    {
        var chosen = _stage.SelectedItem as string;

        _stage.Items.Clear();

        if (Company() is not { } companyId) return;

        foreach (var stage in _workspace.Ledger.Stages(companyId)) _stage.Items.Add(stage);

        var index = chosen is null ? -1 : _stage.Items.IndexOf(chosen);
        _stage.SelectedIndex = index >= 0 ? index : 0;
    }

    /// <summary>
    /// Месеците в списъка.
    ///
    /// Не само тези с движение: месецът, от който се води складът, и текущият
    /// стоят винаги. Нов месец още няма нито ред, а точно него човек отваря,
    /// за да въведе първия.
    /// </summary>
    private void FillMonths()
    {
        // Поисканият с търкаляне е по-силен от отворения: точно него се отива
        // да се види, а той може още да го няма в списъка.
        var chosen = _wanted ?? Period();
        _wanted = null;

        _month.Items.Clear();
        _periods.Clear();

        if (Company() is not { } companyId) return;

        var today = LedgerPeriod.Of(DateOnly.FromDateTime(DateTime.Today));

        var months = new HashSet<LedgerPeriod>(_workspace.Ledger.Periods(companyId, Stage())) { today };

        if (_workspace.Setup.Start(companyId) is { } start) months.Add(start);

        // Не само месеците с движение. Първата подредба беше точно такава и се
        // оказа задънена улица: месец се появяваше в списъка чак когато вече
        // има какво да се покаже в него, а за да има, трябва да се отвори и да
        // се въведе. Внасяне на юлски дневник през септември не даваше нищо и
        // юли не можеше да се избере.
        //
        // Затова назад стои цяла работна история, а напред — до края на
        // годината: отчетите се подават след края на периода и човек редовно
        // отваря минал месец.
        var back = today;

        for (var i = 0; i < MonthsBack; i++)
        {
            back = back.Previous;
            months.Add(back);
        }

        for (var month = today.Month + 1; month <= 12; month++)
            months.Add(new LedgerPeriod(today.Year, month));

        // Отвореният месец стои в списъка дори когато нито едно от горните
        // правила не го дава: иначе търкалянето отвъд края на годината би
        // отваряло месец, който списъкът не може да покаже.
        if (chosen is { } open) months.Add(open);

        foreach (var period in months.OrderByDescending(p => p))
        {
            _month.Items.Add(period.Title());
            _periods.Add(period);
        }

        var index = chosen is { } current ? _periods.IndexOf(current) : -1;

        // Без избран месец се отваря ТЕКУЩИЯТ, а не първият в списъка: списъкът
        // предлага и месеците до края на годината и започва от декември, а
        // работата се води в този, който тече.
        if (index < 0) index = _periods.IndexOf(today);

        _month.SelectedIndex = index >= 0 ? index : 0;
    }

    /// <summary>
    /// Отива един месец назад или напред.
    ///
    /// Списъкът се пълни наново, защото поисканият месец може още да не е в
    /// него. Извън обсега не се мърда — стрелката тогава е угасена и това е
    /// отговорът.
    /// </summary>
    private void Roll(int step)
    {
        if (Company() is not { } companyId || Period() is not { } period) return;

        var target = step < 0 ? period.Previous : period.Next;

        if (!Reachable(companyId, target)) return;

        _wanted = target;
        _filling = true;

        try
        {
            FillMonths();
        }
        finally
        {
            _filling = false;
        }

        Rebuild();
    }

    /// <summary>
    /// Дали този месец се отваря с търкаляне.
    ///
    /// Назад — докъдето стига и списъкът: той вече пази цяла работна история и
    /// началото на воденето. Напред — година след днешния.
    /// </summary>
    private bool Reachable(long companyId, LedgerPeriod target)
    {
        var today = LedgerPeriod.Of(DateOnly.FromDateTime(DateTime.Today));

        var floor = today;

        for (var i = 0; i < MonthsBack; i++) floor = floor.Previous;

        if (_workspace.Setup.Start(companyId) is { } start && start < floor) floor = start;

        if (_periods.Count > 0 && _periods[^1] < floor) floor = _periods[^1];

        var ceiling = today;

        for (var i = 0; i < MonthsAhead; i++) ceiling = ceiling.Next;

        return target >= floor && target <= ceiling;
    }

    /// <summary>Угася стрелката, зад която няма месец.</summary>
    private void RollButtons()
    {
        if (Company() is not { } companyId || Period() is not { } period)
        {
            _earlier.Enabled = false;
            _later.Enabled = false;
            return;
        }

        _earlier.Enabled = Reachable(companyId, period.Previous);
        _later.Enabled = Reachable(companyId, period.Next);
    }

    /// <summary>Пресмята и рисува таблицата наново.</summary>
    private void Rebuild()
    {
        if (_filling || IsDisposed) return;

        RollButtons();

        _pageTitle.Text = _scope.Titled(Lang.T("Месец", "Month"));

        if (Company() is not { } companyId || Period() is not { } period)
        {
            _grid.Rows.Clear();
            _summary.Text = Lang.T("Заведете фирма в „Настройки“, за да започнете.",
                                   "Add a company in Settings to begin.");
            _checks.Text = string.Empty;
            _fix.Text = string.Empty;
            _issues = [];
            _waiting = null;
            _verdict = null;
            return;
        }

        var stage = Stage();
        var moves = _workspace.Ledger.Read(companyId, stage);
        var links = _workspace.Ledger.Links(companyId, stage);
        var batchList = _workspace.Batches.List(companyId, stage);
        var batches = batchList.ToDictionary(b => b.Id);

        // Получателите и бележките с ЕДНО питане, а не по едно на ред: месец с
        // петстотин движения иначе би питал базата петстотин пъти за колона,
        // която не участва в нито една сметка.
        var parties = _workspace.Ledger.Parties(companyId, stage);
        var notes = _workspace.Ledger.Notes(companyId, stage);

        var byMove = links.GroupBy(l => l.MoveId)
            .ToDictionary(g => g.Key, g => Allocation.Origins([.. g.Select(l => new Draw(l.BatchId, l.Amount))], batches));

        var wanted = _kind.SelectedIndex > 0 ? Moves.Every[_kind.SelectedIndex - 1] : (MoveKind?)null;
        var words = SearchWords();

        // Редът, на който е стоял човекът, остава избран и след пресмятането:
        // иначе всяка поправка го връщаше в началото на петстотин реда.
        var kept = Selected();

        _shown = 0;
        _shownAmount = 0m;

        // Номерата на стълбовете се вземат веднъж: ред, още невързан за таблицата,
        // не познава клетките си по име.
        int At(string column) => _grid.Columns[column]!.Index;

        var (date, kind, code, amount, origin, receiver, contract, note) =
            (At(DateColumn), At(KindColumn), At(CodeColumn), At(AmountColumn),
             At(OriginColumn), At(ReceiverColumn), At(ContractColumn), At(NoteColumn));

        var notStated = Lang.T("(не е посочен)", "(not stated)");
        var rows = new List<DataGridViewRow>();
        var current = -1;

        foreach (var move in moves
                     .Where(m => period.Covers(m.Date) && !m.Opening)
                     .Where(m => wanted is null || m.Kind == wanted)
                     .OrderBy(m => m.Date)
                     .ThenBy(m => m.Id))
        {
            var dateText = move.Date.ToString(ValueConverter.BulgarianDateFormat);
            var kindText = Moves.Title(move.Kind);

            // При третиране с обявен изходен код се виждат и двата: по левия
            // се вади складът, десният отива в книгата. Скрит, вторият би менял
            // отчета, без да се вижда никъде.
            var codeText = move.EwcOut.Length > 0
                ? $"{Describe(move.Ewc)} → {Describe(move.EwcOut)}"
                : Describe(move.Ewc);

            var shares = byMove.GetValueOrDefault(move.Id);
            var originText = Origin(shares);

            var receiverText = string.Empty;
            var contractText = string.Empty;

            if (move.Kind == MoveKind.Submitted)
            {
                var party = parties.GetValueOrDefault(move.Id, HandParty.None);

                receiverText = party.Known ? party.Display : notStated;
                contractText = party.Contract;
            }

            var noteText = notes.GetValueOrDefault(move.Id, string.Empty);

            // Търси се в това, което се вижда, плюс документа и ЕИК-а зад
            // партидите: човек помни „протокол 1330“ или „Метал“, а не реда.
            if (words.Length > 0 && !words.All(w =>
                    Has(dateText, w) || Has(kindText, w) || Has(codeText, w) || Has(originText, w) ||
                    Has(receiverText, w) || Has(contractText, w) || Has(noteText, w) ||
                    Behind(shares, batches, w)))
                continue;

            var row = (DataGridViewRow)_grid.RowTemplate.Clone();
            row.CreateCells(_grid);

            row.Cells[date].Value = dateText;
            row.Cells[kind].Value = kindText;
            row.Cells[code].Value = codeText;
            row.Cells[amount].Value = SplitMath.Amount(move.Amount);
            row.Cells[origin].Value = originText;
            row.Cells[receiver].Value = receiverText;
            row.Cells[contract].Value = contractText;
            row.Cells[note].Value = noteText;
            row.Tag = move.Id;

            if (move.Id == kept) current = rows.Count;

            rows.Add(row);

            _shown++;
            _shownAmount += move.Amount;
        }

        // Наведнъж, а не ред по ред: петстотин приемания на юли, добавени едно
        // по едно, бяха половин секунда на всяко щракване по ситото.
        _grid.Rows.Clear();
        _grid.Rows.AddRange([.. rows]);

        if (current >= 0)
        {
            _grid.CurrentCell = _grid.Rows[current].Cells[date];
            Reveal(current);
        }

        // Сборът на видимото стои в заглавието на колоната — там, където се
        // гледа самото число. Мени се с всяко пресяване.
        _grid.Columns[AmountColumn].HeaderText =
            Summed(Lang.T("Количество, т", "Quantity, t"), _shownAmount);

        Describe(companyId, stage, period, moves, links, batchList);
    }

    /// <summary>
    /// Превърта до реда, ако е под ръба — иначе избирането му не се вижда, което
    /// е по-лошо от неизбиране.
    ///
    /// Таблица без място за редове (смален прозорец, страница, която още не е
    /// подредена) хвърля при превъртане. Тогава няма какво да се види и не се
    /// превърта: изборът си остава и се вижда при следващото показване.
    /// </summary>
    private void Reveal(int index)
    {
        if (index < 0 || index >= _grid.Rows.Count || _grid.Rows[index].Displayed) return;
        if (_grid.DisplayRectangle.Height <= _grid.ColumnHeadersHeight) return;

        try
        {
            _grid.FirstDisplayedScrollingRowIndex = Math.Max(0, index - 3);
        }
        catch (InvalidOperationException)
        {
            // „No room is available to display rows“ — виж горе.
        }
    }

    /// <summary>Думите от търсенето — всяка трябва да се намери някъде в реда.</summary>
    private string[] SearchWords() =>
        _search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool Has(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>Документът и ЕИК-ът на партидите зад реда — не се виждат, но се търсят.</summary>
    private static bool Behind(
        IReadOnlyList<OriginShare>? shares, IReadOnlyDictionary<long, Batch> batches, string word)
    {
        if (shares is not { Count: > 0 }) return false;

        foreach (var share in shares)
        {
            if (batches.GetValueOrDefault(share.BatchId) is not { } batch) continue;

            if (Has(batch.Document, word) || Has(batch.Eik, word) || Has(batch.PartnerName, word) ||
                Has("#" + batch.Code, word))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Заглавие на колона със сбора под него.
    ///
    /// Едно и също на двата екрана: числото стои след името, отделено с точка,
    /// за да не се чете като част от него.
    /// </summary>
    internal static string Summed(string caption, decimal sum) =>
        caption + Environment.NewLine + SplitMath.Amount(sum);

    /// <summary>Кой е донесъл това — първите двама и „и още“, ако са повече.</summary>
    private static string Origin(IReadOnlyList<OriginShare>? shares)
    {
        if (shares is not { Count: > 0 }) return Lang.T("— не е посочен", "— not stated");

        var named = shares.Take(2).Select(s => $"{s.Partner} ({SplitMath.Amount(s.Amount)} т)");
        var text = string.Join("; ", named);

        return shares.Count > 2
            ? text + Lang.T($"; и още {shares.Count - 2}", $"; and {shares.Count - 2} more")
            : text;
    }

    /// <summary>Какво казва месецът: сборовете, наличностите и какво не е наред.</summary>
    private void Describe(
        long companyId, string stage, LedgerPeriod period,
        IReadOnlyList<Move> moves, IReadOnlyList<LedgerLink> links, IReadOnlyList<Batch> batches)
    {
        var totals = StockMath.Totals(moves, period);

        var openingReceived = StockMath.Opening(moves, StoreKind.Received, period).Values.Sum();
        var closingReceived = StockMath.Closing(moves, StoreKind.Received, period).Values.Sum();
        var closingFormed = StockMath.Closing(moves, StoreKind.Formed, period).Values.Sum();

        // Тримесечието, не само месецът: съставянето предлага него и готов месец
        // в неготово тримесечие е точно случаят, в който „защо не излиза“ иначе
        // няма отговор на екрана.
        var (first, last) = Quarter(period);
        var quarter = Reconcile.WaitingByMonth(batches, moves, links, first, last);

        _waiting = quarter.FirstOrDefault(w => w.Month == period);

        var waiting = _waiting?.Amount ?? 0m;

        // Показаното е първо: то отговаря на въпроса „какво гледам сега“, а
        // сборовете на месеца стоят зад него непроменени.
        var shown = _kind.SelectedIndex > 0 || SearchWords().Length > 0
            ? Lang.T($"Показани {_shown} реда · {SplitMath.Amount(_shownAmount)} т · ",
                     $"{_shown} rows shown · {SplitMath.Amount(_shownAmount)} t · ")
            : string.Empty;

        // Рециклираното се казва само когато го има: площадка, която не
        // рециклира, не бива да чете всеки месец „рециклирано 0,000 т“. Има ли
        // го, то стои до образуваното — двете заедно са къде е отишло третираното.
        var recycled = totals[MoveKind.Recycled] > 0
            ? Lang.T($" · рециклирано {SplitMath.Amount(totals[MoveKind.Recycled])} т (извън отчета)",
                     $" · recycled {SplitMath.Amount(totals[MoveKind.Recycled])} t (outside the report)")
            : string.Empty;

        _summary.Text = shown + Lang.T(
            $"Получено {SplitMath.Amount(totals[MoveKind.Received])} т · " +
            $"третирано {SplitMath.Amount(totals[MoveKind.Treated])} т · " +
            $"образувано {SplitMath.Amount(totals[MoveKind.Generated])} т · " +
            $"предадено {SplitMath.Amount(totals[MoveKind.Submitted])} т" + recycled +
            Environment.NewLine +
            $"Склад „Получен“: {SplitMath.Amount(openingReceived)} → {SplitMath.Amount(closingReceived)} т · " +
            $"„Третиран“ в края: {SplitMath.Amount(waiting)} т · " +
            $"„Образуван“ в края: {SplitMath.Amount(closingFormed)} т",

            $"Received {SplitMath.Amount(totals[MoveKind.Received])} t · " +
            $"treated {SplitMath.Amount(totals[MoveKind.Treated])} t · " +
            $"generated {SplitMath.Amount(totals[MoveKind.Generated])} t · " +
            $"handed over {SplitMath.Amount(totals[MoveKind.Submitted])} t" + recycled +
            Environment.NewLine +
            $"“Received” store: {SplitMath.Amount(openingReceived)} → {SplitMath.Amount(closingReceived)} t · " +
            $"“Treated” at close: {SplitMath.Amount(waiting)} t · " +
            $"“Formed” at close: {SplitMath.Amount(closingFormed)} t");

        _issues = Reconcile.Check(batches, moves, links, period, _workspace.Setup.Start(companyId));

        var closed = _workspace.Periods.IsClosed(companyId, stage, period);
        var count = moves.Count(m => period.Covers(m.Date) && !m.Opening);

        var verdict = _verdict = MonthVerdict.For(period, count, closed, _issues, quarter);

        _checks.Text = verdict.Text;
        _checks.ForeColor = verdict.Colour;
        _fix.Text = verdict.Fix;

        // Броят стои на копчето: „Проверка (3)“ се вижда и когато присъдата е за
        // друго — и казва, че има какво да се отвори.
        var listed = _issues.Count;

        if (_review is not null)
        {
            _review.Text = listed > 0
                ? Lang.T($"Проверка ({listed})…", $"Check ({listed})…")
                : Lang.T("Проверка…", "Check…");

            _review.FitWidth();
        }
    }

    /// <summary>Тримесечието, в което пада месецът — както го предлага съставянето.</summary>
    private static (LedgerPeriod From, LedgerPeriod To) Quarter(LedgerPeriod period)
    {
        var first = (period.Quarter - 1) * 3 + 1;

        return (new LedgerPeriod(period.Year, first), new LedgerPeriod(period.Year, first + 2));
    }

    // ------------------------------------------------------------- действия

    /// <summary>
    /// Отваря месеца по този вид движение — в таблица.
    ///
    /// Таблица, а не прозорче за един ред: приемането не идва по едно. В
    /// истински месец те са стотици и вече съществуват някъде, откъдето се
    /// залепят наведнъж. Единичният ред се отваря от самата таблица или с
    /// двойно щракване тук — там се вижда и разпределението по партиди.
    /// </summary>
    private void Add(MoveKind kind)
    {
        if (Company() is not { } companyId || Period() is not { } period) return;

        var stage = Stage();

        if (Locked(companyId, stage, period)) return;

        // Без внасяне от файл: то е само в „Склад“ → „Внеси…“. Два пътя до едно
        // и също внасяне бяха два начина да се внесе един файл два пъти — и
        // тестващите питаха кой е правилният. Тук се пише и се залепва.
        using var dialog = new MoveSheetDialog(_service, _workspace, companyId, stage, kind, period, SheetImport.None);

        dialog.ShowDialog(this);

        if (dialog.Saved) Refresh();
    }

    /// <summary>Затвореният месец не се пипа, докато не се отвори наново.</summary>
    private bool Locked(long companyId, string stage, LedgerPeriod period)
    {
        if (!_workspace.Periods.IsClosed(companyId, stage, period)) return false;

        MessageBox.Show(this,
            Lang.T("Месецът е затворен. Отворете го наново, за да въвеждате в него.",
                   "The month is closed. Reopen it to enter movements."),
            Lang.T("Затворен месец", "Closed month"),
            MessageBoxButtons.OK, MessageBoxIcon.Information);

        return true;
    }

    private void Edit()
    {
        if (Selected() is not { } moveId) return;
        if (Company() is not { } companyId || Period() is not { } period) return;

        var stage = Stage();

        var move = _workspace.Ledger.Read(companyId, stage).FirstOrDefault(m => m.Id == moveId);
        if (move is null) return;

        if (Locked(companyId, stage, period)) return;

        // Рециклирането няма раздел в книгата, а прозорчето за един ред се
        // сглобява от раздела. Поправя се в прозореца, от който е дошло.
        if (!Moves.InBook(move.Kind))
        {
            MessageBox.Show(this, RecycledHint,
                Lang.T("Рециклиране", "Recycling"), MessageBoxButtons.OK, MessageBoxIcon.Information);

            return;
        }

        using var dialog = new MoveDialog(_service, _workspace, companyId, stage, move.Kind, period, moveId);

        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is not { } entry) return;

        Write(companyId, stage, entry, moveId);
        Refresh();
    }

    /// <summary>Къде се поправя рециклирането — казва се, вместо двойното щракване да мълчи.</summary>
    internal static string RecycledHint => Lang.T(
        "Рециклирането се въвежда и поправя от склад „Третиран“: „Склад“ → „Дневник“ → „Поправи…“. " +
        "Оттук то може само да се махне (Delete) и да се запише наново.",
        "Recycling is entered and corrected from the “Treated” store: “Store” → “Log” → “Correct…”. " +
        "From here it can only be removed (Delete) and saved again.");

    /// <summary>
    /// Записва движението през общото място за записване.
    ///
    /// Правилата — приемането ражда партида, поправката я обновява вместо да
    /// заведе втора — живеят в <see cref="LedgerWriter"/>, защото движения се
    /// въвеждат от три места и три техни копия щяха да се разминат.
    /// </summary>
    private void Write(long companyId, string stage, MoveEntry entry, long? moveId) =>
        _workspace.Writer.Write(companyId, new MoveRequest(
            entry.Kind,
            entry.Date,
            entry.Ewc,
            entry.Amount,
            stage,
            entry.Fields.GetValueOrDefault("pin_originator", string.Empty),
            entry.Fields.GetValueOrDefault("name_originator", string.Empty),
            entry.Document,
            entry.Note,
            Fields: entry.Fields,
            MoveId: moveId,
            Draws: entry.Draws,
            EwcOut: entry.EwcOut,
            Contract: entry.Contract));

    private void Remove()
    {
        if (Selected() is not { } moveId) return;
        if (Company() is not { } companyId) return;

        var stage = Stage();
        var moves = _workspace.Ledger.Read(companyId, stage);
        var move = moves.FirstOrDefault(m => m.Id == moveId);
        if (move is null) return;

        // Затвореният месец не се пипа — и при махане, не само при поправка.
        // Дотук махането минаваше и през затворен месец.
        if (Locked(companyId, stage, LedgerPeriod.Of(move.Date))) return;

        // Същата проверка като в дневника (дефект 9): движение, от което вече е
        // черпено, не се маха — иначе едни и същи тонове остават в два склада.
        if (Refused(moves, _workspace.Ledger.Links(companyId, stage), moveId) is { } why)
        {
            MessageBox.Show(this, why, Lang.T("От него вече е взето", "It has already been drawn on"),
                MessageBoxButtons.OK, MessageBoxIcon.Warning);

            return;
        }

        var day = move.Date.ToString(ValueConverter.BulgarianDateFormat);

        var question = Lang.T(
            $"Да се махне ли {Moves.Title(move.Kind).ToLowerInvariant()} на {day} за {SplitMath.Amount(move.Amount)} т?",
            $"Remove the {Moves.Title(move.Kind).ToLowerInvariant()} of {SplitMath.Amount(move.Amount)} t on {day}?");

        // Питането остава винаги: това е движение от склада, не ред в таблица, и
        // отметката „Питай преди изтриване на ред“ не е за него. „Не“ е отговорът
        // по подразбиране — Enter не бива да маха.
        var answer = MessageBox.Show(this, question,
            Lang.T("Махане на движение", "Remove a movement"),
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes) return;

        // Партида, към която вече нищо не сочи, си отива с приемането: тя е
        // самото приемане, а не отделно заведено нещо. Правилото живее при
        // записа (LedgerWriter), за да е едно за трите места, откъдето се маха.
        _workspace.Writer.Remove(companyId, moveId);

        Refresh();
    }

    /// <summary>
    /// Защо това не може да се махне — или null, ако може.
    ///
    /// Същото правило като в дневника (<see cref="Undo.Takers"/>): кой е черпил
    /// от махнатото. Изброяват се истинските движения, за да могат да се намерят —
    /// и се казва редът: от последното към първото.
    /// </summary>
    internal static string? Refused(
        IReadOnlyList<Move> moves, IReadOnlyList<LedgerLink> links, params long[] gone)
    {
        var takers = Undo.Takers(moves, links, gone.ToHashSet());

        if (takers.Count == 0) return null;

        const int most = 8;

        var lines = takers.Take(most).Select(m => "•  " + Amend.Describe(m)).ToList();

        if (takers.Count > most)
            lines.Add(Lang.T($"…и още {takers.Count - most}", $"…and {takers.Count - most} more"));

        var list = string.Join(Environment.NewLine, lines);

        return Lang.T(
            $"Това не може да се махне — от него вече е взето:{Environment.NewLine}{Environment.NewLine}{list}" +
            $"{Environment.NewLine}{Environment.NewLine}Махнете първо тях, от последното към първото. " +
            "Иначе едни и същи тонове биха останали в два склада.",
            $"This cannot be removed — it has already been drawn on:{Environment.NewLine}{Environment.NewLine}{list}" +
            $"{Environment.NewLine}{Environment.NewLine}Remove those first, from the last to the first. " +
            "Otherwise the same tonnes would stay in two stores.");
    }

    /// <summary>Контролите за този месец — правилото стои в <see cref="LedgerChecks"/>.</summary>
    private IReadOnlyList<LedgerIssue> Issues(long companyId, string stage, LedgerPeriod period) =>
        LedgerChecks.For(_workspace, companyId, stage, period);

    /// <summary>Колко чака в склад „Третиран“ в края на месеца.</summary>
    internal decimal PendingTreated(LedgerPeriod period)
    {
        if (Company() is not { } companyId) return 0m;

        return SplitMath.Round(Waiting(companyId, Stage(), period, period).Sum(w => w.Amount));
    }

    /// <summary>Дали отчет за този период изобщо може да се състави.</summary>
    internal bool CanCompose(LedgerPeriod from, LedgerPeriod to) =>
        Company() is { } companyId && Waiting(companyId, Stage(), from, to).Count == 0;

    /// <summary>
    /// Съставя отчетната книга от движенията.
    ///
    /// Съставеният отчет е обикновен отчет: оттук нататък се отваря в
    /// редактора, проверява се и се подписва по същия път като всеки друг.
    /// Складът само го попълва вместо човека.
    /// </summary>
    private void Compose()
    {
        if (Company() is not { } companyId || Period() is not { } period) return;
        if (_workspace.Companies.Find(companyId) is not { } company) return;

        var stage = Stage();

        using var dialog = new BookDialog(period, (from, to) => Weigh(company.Eik, companyId, stage, from, to));

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        // Контролата стои ТУК, а не само в прозорчето: прозорчето показва, този
        // ред спира. Отчет, който твърди, че отпадъкът е минал през площадката,
        // докато той още чака в склад „Третиран“, е неверен отчет.
        if (Waiting(companyId, stage, dialog.From, dialog.To) is { Count: > 0 } waiting)
        {
            MessageBox.Show(this,
                Lang.T("Отчет не може да се състави, докато в склад „Третиран“ чака количество:",
                       "No report can be composed while a quantity waits in the “Treated” store:") +
                Environment.NewLine + Environment.NewLine +
                string.Join(Environment.NewLine, waiting.Select(w =>
                    Lang.T($"· {w.Month.Title()} — {SplitMath.Amount(w.Amount)} т: {w.Named(2)}",
                           $"· {w.Month.Title()} — {SplitMath.Amount(w.Amount)} t: {w.Named(2)}"))) +
                Environment.NewLine + Environment.NewLine +
                new LedgerIssue(LedgerProblem.Waiting, string.Empty, 0m).Fix,
                Lang.T("Складът не е празен", "The store is not empty"),
                MessageBoxButtons.OK, MessageBoxIcon.Warning);

            return;
        }

        UseWaitCursor = true;
        Cursor.Current = Cursors.WaitCursor;

        long documentId;

        try
        {
            var rows = Collect(companyId, stage, dialog.From, dialog.To);
            var schema = _service.GetSchema("book");
            var header = BookProjection.Header(company.Eik, dialog.From, dialog.To);

            documentId = _workspace.Documents.Create(
                "book",
                BookNodes.Report,
                dialog.SuggestedTitle(company.Name),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["year"] = header.Year.ToString(),
                    ["quarter"] = header.Quarter.ToString(),
                    ["eik"] = header.Eik,
                    ["type"] = header.Type,
                    ["period_start"] = dialog.PeriodStart,
                    ["period_end"] = dialog.PeriodEnd
                },
                DocumentOrigin.Ledger,
                companyId);

            foreach (var (node, values) in BookProjection.Rows(schema, rows))
                _workspace.Documents.SaveRows(documentId, node, values);
        }
        finally
        {
            UseWaitCursor = false;
            Cursor.Current = Cursors.Default;
        }

        DocumentsChanged?.Invoke(this, EventArgs.Empty);

        using (var editor = new Editor.EditorForm(_service, _workspace, documentId))
            editor.ShowDialog(this);

        DocumentsChanged?.Invoke(this, EventArgs.Empty);
        Refresh();
    }

    /// <summary>Движенията за подаване, с колоните им от схемата.</summary>
    private List<BookRow> Collect(long companyId, string stage, LedgerPeriod from, LedgerPeriod to) =>
    [
        .. BookProjection.Submittable(_workspace.Ledger.Read(companyId, stage), from, to)
            .Select(m => new BookRow(
                m.Kind, m.Date, m.Ewc, m.Amount, m.Stagenum, _workspace.Ledger.Fields(m.Id), m.EwcOut))
    ];

    /// <summary>Редовете, които биха влезли в отчета — същите, които съставянето събира.</summary>
    internal IReadOnlyList<BookRow> ReportRows(LedgerPeriod from, LedgerPeriod to) =>
        Company() is { } companyId ? Collect(companyId, Stage(), from, to) : [];

    /// <summary>
    /// Колко реда ще влязат, колко са без произход, какво чака в „Третиран“ и
    /// какво ще каже проверката по схемата.
    ///
    /// Проверката минава по същия път като съставения отчет
    /// (<see cref="BookProjection.Build"/> → <c>ImportProcessor</c>): тук не се
    /// пишат нови правила, а се пита предварително онова, което редакторът би
    /// казал след съставянето.
    /// </summary>
    internal BookWeight Weigh(string eik, long companyId, string stage, LedgerPeriod from, LedgerPeriod to)
    {
        var all = _workspace.Ledger.Read(companyId, stage);
        var links = _workspace.Ledger.Links(companyId, stage);
        var moves = BookProjection.Submittable(all, from, to).ToList();

        var covered = new Dictionary<long, decimal>();

        foreach (var link in links)
            covered[link.MoveId] = covered.GetValueOrDefault(link.MoveId) + link.Amount;

        var missing = moves.Count(m => SplitMath.Round(m.Amount - covered.GetValueOrDefault(m.Id)) > 0);

        var waiting = Reconcile.WaitingByMonth(_workspace.Batches.List(companyId, stage), all, links, from, to);

        // Спряното не се проверява: отчетът и без това няма да излезе, а
        // грешките по схемата биха изместили единствената причина, която важи.
        if (waiting.Count > 0 || moves.Count == 0) return new BookWeight(moves.Count, missing, waiting, [], 0);

        var (problems, count) = Validate(eik, companyId, stage, from, to);

        return new BookWeight(moves.Count, missing, waiting, problems, count);
    }

    /// <summary>
    /// Най-честите грешки, които проверката по схемата ще намери — събрани по
    /// съобщение: „липсва ЕИК — 412 реда“ казва повече от 412 еднакви реда.
    /// </summary>
    private (IReadOnlyList<string> Problems, int Count) Validate(
        string eik, long companyId, string stage, LedgerPeriod from, LedgerPeriod to)
    {
        var schema = _service.GetSchema("book");
        var header = BookProjection.Header(eik, from, to);
        var result = _service.Process(BookProjection.Build(schema, header, Collect(companyId, stage, from, to)));

        var errors = result.AllIssues.Where(i => i.Severity == IssueSeverity.Error).ToList();

        var grouped = errors
            .GroupBy(i => (i.Where, i.Message))
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key.Where, StringComparer.CurrentCulture)
            .Select(g => g.Count() == 1
                ? $"{g.Key.Where}{Line(g.First())}: {g.Key.Message}"
                : Lang.T($"{g.Key.Where}: {g.Key.Message} — {g.Count()} реда",
                         $"{g.Key.Where}: {g.Key.Message} — {g.Count()} rows"))
            .Concat(result.SchemaErrors.Select(e => Lang.T("Схема: ", "Schema: ") + e))
            .ToList();

        const int most = 4;

        var shown = grouped.Take(most).ToList();

        if (grouped.Count > most)
            shown.Add(Lang.T($"…и още {grouped.Count - most} вида", $"…and {grouped.Count - most} more kinds"));

        return (shown, errors.Count + result.SchemaErrors.Count);

        static string Line(Issue issue) => issue.ExcelRow is { } row ? Lang.T($", ред {row}", $", row {row}") : string.Empty;
    }

    /// <summary>
    /// Кои месеци от периода още не са изпразнили склад „Третиран“ — правилото
    /// е в <see cref="Reconcile.WaitingByMonth"/>.
    /// </summary>
    private IReadOnlyList<WaitingMonth> Waiting(
        long companyId, string stage, LedgerPeriod from, LedgerPeriod to) =>
        Reconcile.WaitingByMonth(
            _workspace.Batches.List(companyId, stage),
            _workspace.Ledger.Read(companyId, stage),
            _workspace.Ledger.Links(companyId, stage),
            from,
            to);

    /// <summary>Съставеният отчет е нов отчет — списъкът с отчети трябва да научи.</summary>
    public event EventHandler? DocumentsChanged;

    /// <summary>
    /// „Отвори „Третиран““ от проверката — обвивката показва склада към тази дата.
    ///
    /// Събитие, а не препратка към склада: страницата не знае за другите
    /// страници, а кой екран е отворен, решава обвивката.
    /// </summary>
    public event EventHandler<StoreRequest>? StoreRequested;

    /// <summary>
    /// Целият списък с бележките за месеца — и пътят до всяка.
    ///
    /// Не смята наново: показва точно онова, на което стъпва присъдата отдолу, за
    /// да не се разминат двете.
    /// </summary>
    private void Review()
    {
        if (Period() is not { } period) return;

        var verdict = _verdict ?? new MonthVerdict(_checks.Text, _fix.Text, VerdictTone.Quiet);

        using var dialog = new MonthCheckDialog(
            _scope.Titled(Lang.T($"Проверка — {period.Title()}", $"Check — {period.Title()}")),
            verdict, _issues, _waiting);

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        switch (dialog.Action)
        {
            case CheckAction.ShowMove when dialog.MoveId is { } id:
                ShowMove(id);
                break;

            case CheckAction.OpenTreated:
                StoreRequested?.Invoke(this, new StoreRequest(StoreKind.Treated, period.Last));
                break;
        }
    }

    /// <summary>
    /// Отива до движението — в неговия месец, без сито и без търсене, които биха
    /// го скрили — и го избира.
    /// </summary>
    internal bool ShowMove(long moveId)
    {
        if (Company() is not { } companyId) return false;

        if (_workspace.Ledger.Read(companyId, Stage()).FirstOrDefault(m => m.Id == moveId) is not { } move)
            return false;

        _filling = true;

        try
        {
            _kind.SelectedIndex = 0;
            _search.Text = string.Empty;
            _wanted = LedgerPeriod.Of(move.Date);
            FillMonths();
        }
        finally
        {
            _filling = false;
        }

        Rebuild();

        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is not long id || id != moveId) continue;

            _grid.CurrentCell = row.Cells[DateColumn];
            Reveal(row.Index);

            _grid.Focus();
            return true;
        }

        return false;
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keys)
    {
        switch (keys)
        {
            case Keys.Delete when _grid.Focused:
                Remove();
                return true;

            case Keys.Enter when _grid.Focused:
                Edit();
                return true;

            // Търсенето — с клавиш, както в склада.
            case Keys.Control | Keys.F:
                _search.Focus();
                return true;

            case Keys.Escape when _search.ContainsFocus && _search.Text.Length > 0:
                _search.Text = string.Empty;
                return true;

            // Месецът е на страницата, а не на таблицата: въртенето му работи и
            // когато курсорът стои в списъка с фирми, и когато не стои никъде.
            case Keys.Control | Keys.PageUp:
                Roll(-1);
                return true;

            case Keys.Control | Keys.PageDown:
                Roll(1);
                return true;
        }

        return base.ProcessCmdKey(ref message, keys);
    }

    /// <summary>Ctrl+F от главния прозорец — курсорът отива в търсенето.</summary>
    public override bool FocusSearch()
    {
        _search.Focus();
        return true;
    }

    // ------------------------------------------------------------- за теста

    /// <summary>Заглавието на екрана — ползва се от тестовете.</summary>
    internal string TitleText => _pageTitle.Text;

    internal int RowCount => _grid.Rows.Count;

    /// <summary>Видовете в ситото над таблицата.</summary>
    internal IReadOnlyList<string> KindChoices => _kind.Items;

    /// <summary>Чете ли се всеки дял на ситото цял — без многоточие.</summary>
    internal bool KindFits => _kind.Fits;

    /// <summary>Заглавието на колоната с количеството — носи и сбора ѝ.</summary>
    internal string AmountHeaderText => _grid.Columns[AmountColumn].HeaderText;

    /// <summary>
    /// Височината на копчето „Състави отчет…“.
    ///
    /// Проверява се, защото веднъж вече беше смачкано до дванайсет пиксела от
    /// съседа си отгоре и се показваше нарязано. Закачането е ред в кода, който
    /// изглежда безобиден и на който никой не гледа.
    /// </summary>
    internal int ComposeHeight => _compose?.Height ?? 0;

    /// <summary>Един месец назад (-1) или напред (1) — както го прави стрелката.</summary>
    internal void RollMonth(int step) => Roll(step);

    /// <summary>Дали стрелката е жива — по нея се вижда докъде се стига.</summary>
    internal bool CanRoll(int step) => step < 0 ? _earlier.Enabled : _later.Enabled;

    internal string SummaryText => _summary.Text;

    internal string ChecksText => _checks.Text;

    /// <summary>Какво да се направи — вторият ред под присъдата.</summary>
    internal string FixText => _fix.Text;

    /// <summary>Цветът на присъдата — от него се чете тежестта.</summary>
    internal Color ChecksColour => _checks.ForeColor;

    /// <summary>Надписът на „Проверка…“ — носи броя на бележките.</summary>
    internal string ReviewText => _review?.Text ?? string.Empty;

    /// <summary>Какво намира проверката сега — същото, което „Проверка…“ показва.</summary>
    internal IReadOnlyList<LedgerIssue> Found => _issues;

    /// <summary>Търси в месеца, както човек пише в полето.</summary>
    internal void UseSearch(string text)
    {
        _search.Text = text;
        Rebuild();
    }

    /// <summary>Номерът на движението на избрания ред — проверява, че изборът оцелява.</summary>
    internal long? SelectedMove => Selected();

    /// <summary>Избира реда, както с мишката.</summary>
    internal void Pick(int row) => _grid.CurrentCell = _grid.Rows[row].Cells[DateColumn];

    /// <summary>Махане без питане — за теста; връща отказа, ако има такъв.</summary>
    internal string? RemoveForTest(long moveId)
    {
        if (Company() is not { } companyId) return "няма фирма";

        var moves = _workspace.Ledger.Read(companyId, Stage());

        if (moves.FirstOrDefault(m => m.Id == moveId) is not { } move) return "няма движение";

        if (_workspace.Periods.IsClosed(companyId, Stage(), LedgerPeriod.Of(move.Date))) return "затворен";

        if (Refused(moves, _workspace.Ledger.Links(companyId, Stage()), moveId) is { } why) return why;

        _workspace.Writer.Remove(companyId, moveId);
        Refresh();

        return null;
    }

    /// <summary>Какво би казало съставянето за този период — без прозорец.</summary>
    internal BookWeight WeighForTest(LedgerPeriod from, LedgerPeriod to) =>
        Company() is { } companyId && _workspace.Companies.Find(companyId) is { } company
            ? Weigh(company.Eik, companyId, Stage(), from, to)
            : BookWeight.Empty;

    internal IReadOnlyList<string> MonthChoices => [.. _month.Items.Cast<string>()];

    /// <summary>Кой месец е отворен — проверява, че изборът наистина се е случил.</summary>
    internal LedgerPeriod? CurrentMonth => Period();

    internal IReadOnlyList<string> StageChoices => [.. _stage.Items.Cast<string>()];

    /// <summary>Избира месец. Непознат месец не се избира — списъкът го няма.</summary>
    internal void UseMonth(LedgerPeriod period)
    {
        var index = _periods.IndexOf(period);
        if (index < 0) return;

        _month.SelectedIndex = index;
        Rebuild();
    }

    /// <summary>Ситото по вид: 0 е „Всички“, после четирите движения.</summary>
    internal void UseKind(int index)
    {
        _kind.SelectedIndex = index;
        Rebuild();
    }

    /// <summary>Кодът, изписан на реда — проверява, че номенклатурата стига дотук.</summary>
    internal string CodeAt(int row) => _grid.Rows[row].Cells[CodeColumn].Value?.ToString() ?? string.Empty;

    internal string OriginAt(int row) => _grid.Rows[row].Cells[OriginColumn].Value?.ToString() ?? string.Empty;

    internal string ContractAt(int row) => _grid.Rows[row].Cells[ContractColumn].Value?.ToString() ?? string.Empty;

    // ------------------------------------------------------------- помощни

    private long? Company() => _scope.Current;

    private string Stage() => _stage.SelectedItem as string ?? "1";

    private LedgerPeriod? Period() =>
        _month.SelectedIndex >= 0 && _month.SelectedIndex < _periods.Count
            ? _periods[_month.SelectedIndex]
            : null;

    private long? Selected() =>
        _grid.CurrentRow?.Tag is long id ? id : null;

    private string Describe(string ewc) =>
        _names.TryGetValue(ewc, out var name) ? $"{ewc} — {name}" : ewc;

    private static Dictionary<string, string> EwcNames(ImportService service)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        var column = service.GetSchema("book").FindReport(BookNodes.Report)?
            .SelfAndDescendants()
            .FirstOrDefault(n => n.ElementName == Moves.Node(MoveKind.Received))?
            .Attributes.FirstOrDefault(c => c.Name == "ewc");

        foreach (var option in column?.Domain.Options ?? [])
            if (option.Description is { Length: > 0 } description)
                names[option.Value] = description;

        return names;
    }

    private static Control Labelled(Control input, string caption, int width)
    {
        var host = new Panel { Dock = DockStyle.Left, Width = Theme.Scaled(width), BackColor = Theme.Canvas };

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

    private static Control Spacer() => new Panel
    {
        Dock = DockStyle.Left,
        Width = Theme.Gap,
        BackColor = Theme.Canvas
    };

    private static AppButton Action(string text, Action handler)
    {
        var button = Theme.GhostButton(text, Theme.Glyph.Add);
        button.Height = Theme.Scaled(40);
        button.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        button.Click += (_, _) => handler();

        return button.FitWidth();
    }
}

/// <summary>
/// Кой склад към коя дата да се покаже — молбата на „Месец“ към обвивката.
///
/// Проверката на месеца казва „в „Третиран“ чакат 1,250 т“; оттук човек отива
/// направо там, където се образува, вместо да търси склада и датата сам.
/// </summary>
internal sealed record StoreRequest(StoreKind Store, DateOnly On);
