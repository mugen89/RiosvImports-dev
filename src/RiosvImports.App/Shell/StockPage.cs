using RiosvImports.App.Controls;
using RiosvImports.Core;
using RiosvImports.Core.Ledger;
using RiosvImports.Core.Localization;
using RiosvImports.Core.Values;
using RiosvImports.Data;

namespace RiosvImports.App.Shell;

/// <summary>
/// Складът: какво стои на площадката и чие е.
///
/// Двата склада се гледат поотделно, защото са две различни неща: в „Получен“
/// стои чужд отпадък, приет за третиране; в „Образуван“ — вече третираното,
/// което чака да бъде предадено.
///
/// Горната таблица отговаря на „какво има“, долната — на „чие е“. Двете са
/// разделени, а не слети в дърво, защото вторият въпрос се задава за един ред,
/// а не за всички наведнъж: списък от четиристотин партиди под двайсет кода е
/// същото като никакъв списък.
///
/// Изгледът „По клиент“ обръща същите числа наопаки — от кого колко още стои —
/// и това е изгледът, заради който партидите съществуват: договорът е с
/// клиента, не с кода.
/// </summary>
internal sealed class StockPage : ContentPage
{
    private const string KeyColumn = "__key";
    private const string NameColumn = "__name";
    private const string AmountColumn = "__amount";
    private const string CountColumn = "__count";
    private const string AgeColumn = "__age";

    private const string BatchColumn = "__batch";
    private const string PartnerColumn = "__partner";
    private const string TakenColumn = "__taken";
    private const string DocumentColumn = "__document";

    /// <summary>На кого е излязло — само в летописа.</summary>
    private const string ReceiverColumn = "__receiver";

    /// <summary>Кога е излязло. В летописа един ред е едно предаване.</summary>
    private const string HandedColumn = "__handed";

    /// <summary>По какъв договор е излязло — само в „Предаден“.</summary>
    private const string ContractColumn = "__contract";

    /// <summary>Бележката на предаването — пак само в „Предаден“.</summary>
    private const string NoteColumn = "__note";

    /// <summary>Копчето „Третирай“ на самия ред — по едно в двете таблици.</summary>
    private const string TreatColumn = "__treat";

    private readonly ImportService _service;
    private readonly Workspace _workspace;
    private readonly CompanyScope _scope;

    private readonly ComboBox _stage = new();
    private readonly SegmentedControl _store = new();
    private readonly SegmentedControl _view = new();
    private readonly DateTimePicker _date = new();

    /// <summary>Един месец назад и един напред — по краищата на месеците.</summary>
    private readonly AppButton _earlier = Theme.GhostButton(string.Empty, Theme.Glyph.ChevronBack);
    private readonly AppButton _later = Theme.GhostButton(string.Empty, Theme.Glyph.Chevron);
    private readonly SearchBox _search = new(Lang.T("Търсене по код или клиент", "Search by code or client"));

    // Филтрите: временният склад се гледа през тях. „Кое стои по тези кодове“,
    // „кое е на този клиент“, „кое се задържа“ и „кое си струва да се пипне“ са
    // четири различни въпроса и всеки от тях свежда списъка до различни редове.
    //
    // Кодът и клиентът са копчета, а не падащи списъци: падащият избира ЕДНО, а
    // въпросът рядко е за едно. „Колко имам по трите кода за опаковки“ и „какво
    // ми стои от тези двама клиенти“ са същата работа като в Excel — отметки,
    // а не един избор.
    private readonly AppButton _codes = new(string.Empty, ButtonVariant.Secondary) { Glyph = Theme.Glyph.Filter };
    private readonly AppButton _partners = new(string.Empty, ButtonVariant.Secondary) { Glyph = Theme.Glyph.Filter };

    /// <summary>
    /// „Само този месец“: числото спира да е наличност и става движение.
    ///
    /// Изключена — редът казва какво СТОИ към избраната дата. Включена — какво
    /// се е случило през месеца на тази дата, и тогава може да е и
    /// отрицателно: код, от който през септември е излязло повече, отколкото е
    /// влязло, си е точно това.
    ///
    /// Затова и заглавието на стълба се мени: едно и също число под едно и също
    /// име би значело две различни неща.
    /// </summary>
    private readonly CheckBox _monthOnly = new();

    /// <summary>Ситото по получател — само в летописа, където има получатели.</summary>
    private readonly AppButton _receivers = new(string.Empty, ButtonVariant.Secondary) { Glyph = Theme.Glyph.Filter };

    private Control? _receiverHost;

    /// <summary>Клетките на другите две сита — ширината им следва склада.</summary>
    private Control? _codeHost;
    private Control? _partnerHost;

    /// <summary>
    /// Нагласени ли са колоните горе за „Поотделно“. Пази се, за да се пипат само
    /// при смяна на разреза: иначе всяко преизчисляване би връщало ширина, която
    /// човек е дръпнал на ръка.
    /// </summary>
    private bool _spreadForMoves;

    /// <summary>Същото за долната таблица: в „Предаден“ стълбовете там са осем.</summary>
    private bool _spreadForHanded;

    /// <summary>
    /// Поправяното в момента действие. Докато го има, записаното е то, а не ново:
    /// същият белег, същото време в дневника.
    /// </summary>
    private AmendSeed? _amending;

    /// <summary>Ситото по договор — там, където е и получателят.</summary>
    private readonly AppButton _contracts = new(string.Empty, ButtonVariant.Secondary) { Glyph = Theme.Glyph.Filter };

    private Control? _contractHost;

    /// <summary>Ситото по бележка — до договора, по същата причина.</summary>
    private readonly AppButton _notes = new(string.Empty, ButtonVariant.Secondary) { Glyph = Theme.Glyph.Filter };

    private Control? _noteHost;

    /// <summary>
    /// Долната половина с партидите и мястото под лентата. В „Поотделно“ редът
    /// горе вече е най-дребното и долу няма какво да се покаже.
    /// </summary>
    private Control? _bottom;

    private Panel? _body;
    private int _sumsHeight;

    /// <summary>Пренасящата се лента с филтрите — пази се, за да се провери.</summary>
    private FlowLayoutPanel? _filters;
    private readonly TextBox _days = new();
    private readonly TextBox _least = new();

    private readonly DataGridView _master = new();
    private readonly DataGridView _detail = new();
    private readonly Label _summary = new();
    private readonly Label _chosen = new();

    /// <summary>Заглавието на екрана — носи и избраната фирма.</summary>
    private readonly Label _pageTitle = new();
    private readonly Label _caption = new();

    private readonly List<string> _partnerIds = [];

    /// <summary>
    /// Отметнатите кодове и клиенти. Null значи „всички“.
    ///
    /// Null, а не пълен списък: пълният трябва да се поддържа при всяка смяна на
    /// склад и дата, а разликата между „всичко е отметнато“ и „няма филтър“ се
    /// вижда чак когато нещо ново се появи в склада — и тогава е късно.
    /// </summary>
    private HashSet<string>? _codeFilter;
    private HashSet<string>? _partnerFilter;

    /// <summary>Отметнатите получатели. Null значи „всички“ — както при другите два.</summary>
    private HashSet<string>? _receiverFilter;

    /// <summary>
    /// Отметнатите договори. Null значи „всички“; празният низ сред тях значи
    /// „без договор“ — и него може да се търси: кое още не е вързано за договор.
    /// </summary>
    private HashSet<string>? _contractFilter;

    /// <summary>Отметнатите бележки — устроено като договора.</summary>
    private HashSet<string>? _noteFilter;

    private readonly List<string> _receiverIds = [];
    private readonly Dictionary<string, string> _names;

    /// <summary>
    /// Редовете на склада, всеки с получателя си, ако вече е излязъл.
    ///
    /// Не дялове (<see cref="StockShare"/>), а редове: в „Предаден“ един и същи
    /// код от една и съща партида може да е излязъл към трима оползотворители и
    /// дялът ги събира в едно число. Останалите три склада нямат получател и
    /// редът е дялът, обвит.
    /// </summary>
    private IReadOnlyList<StockLine> _shares = [];

    /// <summary>Каквото складът знае, но не по партиди — показва се отделно.</summary>
    private decimal _loose;

    private bool _filling;

    /// <summary>
    /// Едно копче за трите склада.
    ///
    /// Действието следва раздела, защото на всеки склад му се прави точно едно
    /// нещо: полученото се третира, третираното се образува, образуваното се
    /// предава. Три копчета, от които две винаги са сиви, заемат ширина и карат
    /// човека да избира между неща, които не са избор.
    /// </summary>
    private AppButton? _action;

    /// <summary>
    /// Прякото предаване — второ копче, и само в „Получен“.
    ///
    /// Иначе екранът държи по едно действие на склад нарочно: три копчета, от
    /// които две винаги са сиви, заемат ширина и карат човека да избира между
    /// неща, които не са избор. Тук обаче двете действия са на ЕДИН склад и
    /// двете важат: полученото или се третира, или си отива както е дошло.
    /// Второто не е по-рядко от първото на площадка за събиране, а скрито в
    /// меню, то просто нямаше да бъде намерено.
    /// </summary>
    private AppButton? _handOver;

    private AppButton? _import;

    /// <summary>Копчето към дневника — надписът му носи и колко реда чакат.</summary>
    private AppButton? _log;

    /// <summary>
    /// Предлага ли се действие на този склад.
    ///
    /// Пази се отделно от копчето: <c>Control.Visible</c> отговаря и за
    /// родителите си и е „не“, докато екранът още не е показан — а тестът пита
    /// за нагласеното, не за нарисуваното.
    /// </summary>
    private bool _actionShown = true;

    // Подредбата на всяка от двете таблици: по коя колона и в коя посока.
    // Празно значи „както идва“ — подредбата по подразбиране на изгледа.
    private string? _masterSort;
    private bool _masterDown;

    private string? _detailSort;
    private bool _detailDown;

    /// <summary>
    /// Коя от двете таблици е питана за избора.
    ///
    /// Двете носят различни неща — горе кодове, долу партиди — и „третирай
    /// избраното“ значи различно във всяка. Пази се коя е пипана последна, а не
    /// се гадае по това дали някъде има избрани редове: избраното горе не
    /// изчезва, когато човек слезе долу, и обратното.
    /// </summary>
    private DataGridView? _active;

    /// <summary>
    /// Фирмата идва отвън — от лентата отляво. Подаден null значи „направи си
    /// свой обхват“: така страницата се сглобява и сама, което ползва тестът.
    /// </summary>
    public StockPage(ImportService service, Workspace workspace, CompanyScope? scope = null) : base(1460)
    {
        _service = service;
        _workspace = workspace;
        _scope = scope ?? new CompanyScope(workspace);
        _names = EwcNames(service);

        _scope.Changed += (_, _) => Refresh();

        Controls.Add(BuildContent());
        Controls.Add(BuildFilters());
        Controls.Add(BuildToolbar());
        Controls.Add(BuildTitle());

        Refresh();
    }

    /// <summary>Кой от трите склада се гледа.</summary>
    private StoreKind Store =>
        _store.SelectedIndex >= 0 && _store.SelectedIndex < Moves.Stores.Count
            ? Moves.Stores[_store.SelectedIndex]
            : StoreKind.Received;

    /// <summary>Дали редовете се групират по клиент вместо по код.</summary>
    private bool ByPartner => _view.SelectedIndex == 1;

    /// <summary>
    /// Гледа ли се откъм ПОЛУЧАТЕЛЯ — на кого е излязло.
    ///
    /// Само в летописа: в трите склада с наличност нищо не е излязло и въпросът
    /// няма отговор. Затова и третият разрез се появява само там.
    /// </summary>
    private bool ByReceiver => CanSeeReceivers && _view.SelectedIndex == 2;

    /// <summary>Има ли изобщо какво да се каже за получатели на този склад.</summary>
    private bool CanSeeReceivers => Store == StoreKind.Submitted;

    /// <summary>
    /// Гледа ли се всяко най-дребно движение поотделно — без никакво събиране.
    ///
    /// Трите разреза отговарят на „колко“ и затова събират: по код, по клиент,
    /// по получател. Този е пълният списък на излязлото: ред по дял на партида в
    /// предаване, с всичко, което се знае за него. Предаване от двайсет партиди
    /// са двайсет реда — и точно затова няма долна таблица: няма какво да се
    /// разгъне, а сито и сбор важат за самия ред, а не за купчина зад него.
    /// </summary>
    private bool ByMove => CanSeeReceivers && _view.SelectedIndex == 3;

    // ------------------------------------------------------------- устройство

    private Control BuildTitle()
    {
        var host = new Panel { Dock = DockStyle.Top, Height = Theme.Scaled(76), BackColor = Theme.Canvas };

        // Под заглавието стои сборът на ВИДИМОТО — с датата, кодовете и
        // клиентите зад него. Мени се с всеки филтър, защото отговаря на „колко
        // е това, което гледам“, а не „какво е този екран“.
        _summary.Dock = DockStyle.Top;
        _summary.Height = Theme.Scaled(26);
        _summary.Font = Theme.Base;
        _summary.ForeColor = Theme.InkMuted;

        host.Controls.Add(_summary);

        _pageTitle.Dock = DockStyle.Top;
        _pageTitle.Height = Theme.Scaled(44);
        _pageTitle.Font = Theme.Display;
        _pageTitle.ForeColor = Theme.Ink;
        _pageTitle.Text = Lang.T("Склад", "Store");

        host.Controls.Add(_pageTitle);

        return host;
    }

    private Control BuildToolbar()
    {
        // Два реда, а не един: с четвъртия склад превключвателят порасна и
        // онова, което стоеше до него, се озова под копчетата отдясно. На
        // лаптоп това значеше „Внеси…“ под „Третирай…“.
        //
        // Горе стои КАКВО се гледа — складът и разрезът; долу — обхватът му и
        // какво се прави с него.
        var bar = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(76 + 48),
            BackColor = Theme.Canvas,
            Padding = new Padding(0, 0, 0, Theme.Gap)
        };

        foreach (var input in new[] { _stage })
        {
            input.DropDownStyle = ComboBoxStyle.DropDownList;
            input.Font = Theme.Base;
            Theme.StyleInput(input);
        }

        _stage.SelectedIndexChanged += (_, _) => Rebuild();

        _store.SetItems([.. Moves.Stores.Select(Moves.Short)]);
        _store.SelectedIndex = 0;
        _store.Dock = DockStyle.Left;

        // По надписите, а не кръгло число: с петия склад (и при 150 %) старите
        // 470 пиксела режеха „Рециклиран“ с многоточие.
        _store.Width = Math.Max(Theme.Scaled(470), _store.Needed);
        _store.Height = Theme.Scaled(40);
        _store.SelectedIndexChanged += (_, _) => Rebuild();

        _view.SetItems([Lang.T("По код", "By code"), Lang.T("По клиент", "By client")]);
        _view.SelectedIndex = 0;
        _view.Dock = DockStyle.Left;
        _view.Width = Theme.Scaled(200);
        _view.Height = Theme.Scaled(40);
        _view.Margin = new Padding(Theme.Gap, 0, 0, 0);
        _view.SelectedIndexChanged += (_, _) => Rebuild();

        _date.Format = DateTimePickerFormat.Short;
        _date.Value = DateTime.Today;
        _date.ValueChanged += (_, _) => Rebuild();
        Theme.StyleInput(_date);

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

        // Само икона: надписът „Обнови“ стоеше до действие, което се ползва
        // всеки ден, и му вземаше ширина. Какво прави, казва подсказката.
        var refresh = Theme.GhostButton(string.Empty, Theme.Glyph.Refresh);
        refresh.Height = Theme.Scaled(40);
        refresh.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        refresh.AccessibleName = Lang.T("Обнови", "Refresh");
        refresh.Click += (_, _) => Refresh();

        new ToolTip().SetToolTip(refresh, refresh.AccessibleName);

        var action = Theme.PrimaryButton(Lang.T("Третирай…", "Treat…"));
        action.Glyph = Theme.Glyph.Play;
        action.Height = Theme.Scaled(40);
        action.Click += (_, _) => Act();
        _action = action;

        // Внасянето стои тук, а не само в „Месец“: файлът от чужда програма се
        // сеща точно когато складът излезе различен от очакваното, а дотогава
        // пътят дотам минаваше през друг екран и през избор на месец.
        //
        // Внася се в движението, което ПЪЛНИ гледания склад: в „Получен“ това е
        // приемането, в „Предаден“ — предаването. Така копчето не пита нищо
        // излишно — складът горе вече е отговорил.
        var handOver = Theme.SecondaryButton(Lang.T("Предай направо…", "Hand over directly…"));
        handOver.Height = Theme.Scaled(40);
        handOver.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        handOver.Click += (_, _) => SubmitDirectSelected();
        _handOver = handOver;

        new ToolTip().SetToolTip(handOver, Lang.T(
            "Предава избраното направо от склад „Получен“, без третиране — както е дошло.",
            "Hands the selection over straight from the “Received” store, untreated — as it came."));

        var import = Theme.GhostButton(Lang.T("Внеси…", "Import…"), Theme.Glyph.Import);
        import.Height = Theme.Scaled(40);
        import.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        import.Click += (_, _) => Import();
        _import = import;

        // Дневникът на въведеното стои до внасянето, защото отговаря на съседен
        // въпрос: „какво влезе оттук и как да го върна“. Числото на копчето е
        // самото подсещане — празен дневник значи, че няма какво да се проверява.
        var log = Theme.GhostButton(Lang.T("Дневник", "Log"), Theme.Glyph.Documents);
        log.Height = Theme.Scaled(40);
        log.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        log.Click += (_, _) => OpenLog();
        _log = log;

        new ToolTip().SetToolTip(log, Lang.T(
            "Какво е въведено оттук и в кой месец е паднало — оттам се намира сбъркана дата.",
            "What was entered here and which month it landed in — a mistyped date is found there."));

        // Завареното стои при склада, а не в „Настройки“: то Е склад — какво е
        // имало в двете купчини в деня преди първия воден месец.
        var start = Theme.GhostButton(Lang.T("Начални…", "Opening…"));
        start.Height = Theme.Scaled(40);
        start.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        start.Click += (_, _) => OpenStart();

        new ToolTip().SetToolTip(start, Lang.T(
            "Какво е стояло в „Получен“ и „Образуван“, преди програмата да поеме склада.",
            "What stood in “Received” and “Formed” before the program took over the store."));

        tools.Controls.Add(action.FitWidth());
        tools.Controls.Add(handOver.FitWidth());
        tools.Controls.Add(import.FitWidth());
        tools.Controls.Add(start.FitWidth());
        tools.Controls.Add(log.FitWidth());
        tools.Controls.Add(refresh.FitWidth());

        var scope = new Panel { Dock = DockStyle.Bottom, Height = Theme.Scaled(62), BackColor = Theme.Canvas };

        _monthOnly.Text = Lang.T("Само този месец", "This month only");
        _monthOnly.Dock = DockStyle.Left;
        _monthOnly.Width = Theme.Scaled(190);
        _monthOnly.Height = Theme.Scaled(40);
        _monthOnly.Margin = new Padding(Theme.Gap, 0, 0, 0);
        _monthOnly.ForeColor = Theme.Ink;
        _monthOnly.BackColor = Color.Transparent;
        _monthOnly.CheckedChanged += (_, _) => Rebuild();

        new ToolTip().SetToolTip(_monthOnly, Lang.T(
            "Показва какво се е случило през месеца на избраната дата, вместо какво стои към нея.",
            "Shows what happened during the month of the chosen date, instead of what stands as of it."));

        var monthHost = new Panel
        {
            Dock = DockStyle.Left,
            Width = Theme.Scaled(206),
            BackColor = Theme.Canvas,
            Padding = new Padding(0, Theme.Small.Height + 6, 0, 0)
        };

        monthHost.Controls.Add(_monthOnly);

        scope.Controls.Add(tools);
        scope.Controls.Add(monthHost);
        scope.Controls.Add(BuildDatePicker());
        scope.Controls.Add(Spacer());
        scope.Controls.Add(Labelled(_stage, Lang.T("Площадка", "Site"), 100));

        var views = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas };

        views.Controls.Add(_view);
        views.Controls.Add(_store);

        // Пълнещият влиза пръв: закачените се редят наопаки и последният
        // получава своя край, а остатъкът остава за първия.
        bar.Controls.Add(views);
        bar.Controls.Add(scope);

        // Копчетата стоят на реда с датата, докато се побират до нея. В
        // „Получен“ са шест и на лаптоп (1366 пиксела или 150 %) не се побират:
        // закачената отдясно лента пазеше цялата си ширина и лягаше върху „Само
        // този месец“ и стрелката за следващия ден. Тогава слиза на свой ред —
        // пренасяне, а не стискане.
        var below = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = Theme.Scaled(48),
            BackColor = Theme.Canvas,
            Visible = false
        };

        var high = bar.Height;
        var placing = false;

        // Последен сред закачените долу — значи най-отдолу.
        bar.Controls.Add(below);

        void Place()
        {
            if (placing) return;

            var down = tools.PreferredSize.Width > scope.ClientSize.Width - monthHost.Right;

            if (down == (tools.Parent == below)) return;

            placing = true;
            bar.SuspendLayout();

            if (down)
            {
                below.Controls.Add(tools);
            }
            else
            {
                // Обратно на мястото си: закачва се последна, в остатъка вдясно.
                scope.Controls.Add(tools);
                scope.Controls.SetChildIndex(tools, 0);
            }

            below.Visible = down;
            bar.Height = high + (down ? below.Height : 0);
            bar.ResumeLayout(true);
            placing = false;
        }

        scope.Resize += (_, _) => Place();
        tools.SizeChanged += (_, _) => Place();

        return bar;
    }

    /// <summary>
    /// Датата с двете стрелки.
    ///
    /// Складът се гледа по месеци: „какво имах в края на август“ е въпросът,
    /// който отчетът задава, а не „какво имах на 14-и“. Затова стрелките се
    /// движат по КРАИЩАТА на месеците — назад към края на предходния, напред
    /// към края на текущия, а щом се стигне той, към края на следващия.
    ///
    /// Самата дата остава: за всеки друг ден тя е по-краткият път от дванайсет
    /// натискания.
    /// </summary>
    private Control BuildDatePicker()
    {
        // Шрифтът се слага ПРЕДИ мерене: наследен по-късно от страницата, той
        // прави полето по-високо от реда, мерен по подразбиращия се шрифт —
        // при 150 % долният му ръб и стрелките се режеха. Редът е и поне
        // колкото стрелките, които никога не са по-ниски от надписа си.
        _date.Font = Theme.Base;
        var height = Math.Max(_date.PreferredHeight, Theme.Base.Height + Theme.Scaled(6));

        var row = new Panel { Dock = DockStyle.Top, Height = height, BackColor = Theme.Canvas };

        foreach (var (button, back) in new[] { (_earlier, true), (_later, false) })
        {
            button.Height = height;
            button.Width = height + Theme.Gap;
            button.Dock = back ? DockStyle.Left : DockStyle.Right;
            button.AccessibleName = back
                ? Lang.T("Края на предходния месец", "End of the previous month")
                : Lang.T("Края на следващия месец", "End of the next month");

            button.Click += (_, _) => Roll(back ? -1 : 1);

            new ToolTip().SetToolTip(button, button.AccessibleName);
        }

        // Пълнещият влиза пръв: закачените се редят наопаки и последният
        // получава своя край.
        _date.Dock = DockStyle.Fill;
        row.Controls.Add(_date);
        row.Controls.Add(_earlier);
        row.Controls.Add(_later);

        var host = new Panel { Dock = DockStyle.Left, Width = Theme.Scaled(210), BackColor = Theme.Canvas };

        host.Controls.Add(row);
        host.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            Height = Theme.Small.Height + 6,
            Text = Lang.T("Към дата", "As of"),
            Font = Theme.SmallBold,
            ForeColor = Theme.InkFaint
        });

        return host;
    }

    /// <summary>
    /// Мести датата на край на месец — назад или напред.
    ///
    /// Напред от средата на месеца се стига първо до неговия край, а не до
    /// следващия: човек, който гледа 14 септември и натиска напред, пита „а в
    /// края на септември“.
    /// </summary>
    private void Roll(int step)
    {
        var on = DateOnly.FromDateTime(_date.Value);
        var month = LedgerPeriod.Of(on);

        var target = step < 0
            ? month.Previous.Last
            : on < month.Last ? month.Last : month.Next.Last;

        _date.Value = target.ToDateTime(TimeOnly.MinValue);
    }

    /// <summary>
    /// Вторият ред: филтрите.
    ///
    /// Отделен ред, а не още четири полета горе: горният ред казва КОЕ се гледа
    /// (фирма, площадка, склад, дата), а този — колко от него да остане. Двете
    /// се пипат в различни мигове и смесени в една лента се търсят по-дълго,
    /// отколкото се ползват.
    ///
    /// Празно поле значи „без филтър“. Затова няма отметки „включи филтъра“:
    /// празното вече е изключеното.
    /// </summary>
    private Control BuildFilters()
    {
        // ЛЕНТА, КОЯТО СЕ ПРЕНАСЯ, а не се реже.
        //
        // Дотук филтрите бяха закачени отдясно наляво в един ред с постоянни
        // ширини. Съберат ли се, изглежда добре; не се ли съберат — а на
        // лаптоп и при мащаб 125 % не се събират — последните закачени получават
        // нула ширина и „Изчисти“ се рисува ВЪРХУ търсенето. Контрола, която я
        // няма, не е филтър: човек я търси и не я намира.
        //
        // Пренасящ се ред решава това веднъж: тесен прозорец дава два реда,
        // широк — един, и нито едно поле не изчезва.
        var bar = _filters = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = Theme.Canvas,
            Padding = new Padding(0, 0, 0, Theme.Gap)
        };

        foreach (var (button, sieve) in new[]
                 {
                     (_codes, Sieve.Code),
                     (_partners, Sieve.Partner),
                     (_receivers, Sieve.Receiver),
                     (_contracts, Sieve.Contract),
                     (_notes, Sieve.Note)
                 })
        {
            button.Height = Theme.Scaled(34);
            button.Dock = DockStyle.Top;
            button.Click += (_, _) => Pick(sieve);
        }

        foreach (var input in new[] { _days, _least })
        {
            input.Font = Theme.Base;
            input.TextAlign = HorizontalAlignment.Right;
            input.TextChanged += (_, _) => Rebuild();
            Theme.StyleInput(input);
        }

        _search.Height = Theme.Scaled(34);
        _search.TextChanged += (_, _) => Rebuild();

        // По-тесни от преди: седем контроли на един ред не се събират другояче,
        // а надписите им се четат и така.
        _codeHost = Cell(_codes, Lang.T("Код", "Code"), 244);
        bar.Controls.Add(_codeHost);

        _partnerHost = Cell(_partners, Lang.T("Клиент", "Client"), 244);
        bar.Controls.Add(_partnerHost);

        _receiverHost = Cell(_receivers, Lang.T("Получател", "Receiver"), 244);
        bar.Controls.Add(_receiverHost);

        _contractHost = Cell(_contracts, Moves.ContractTitle, 244);
        bar.Controls.Add(_contractHost);

        _noteHost = Cell(_notes, Lang.T("Бележка", "Note"), 244);
        bar.Controls.Add(_noteHost);

        bar.Controls.Add(Cell(_days, Lang.T("Престой над, дни", "Held longer than, days"), 130));
        bar.Controls.Add(Cell(_least, Lang.T("Количество над, т", "Quantity over, t"), 130));
        bar.Controls.Add(Cell(_search, Lang.T("Търсене", "Search"), 210));

        // „Изчисти“ е последно и се пренася заедно с останалите. Празният
        // надпис го подравнява с полетата: те носят свой отгоре.
        var clear = Theme.GhostButton(Lang.T("Изчисти", "Clear"));
        clear.Height = Theme.Scaled(34);
        clear.Dock = DockStyle.Top;
        clear.Click += (_, _) => ClearFilters();

        bar.Controls.Add(Cell(clear, string.Empty, 104));

        return bar;
    }

    /// <summary>Маха всички филтри наведнъж — четири празни полета са четири щраквания.</summary>
    private void ClearFilters()
    {
        _filling = true;

        try
        {
            _codeFilter = null;
            _partnerFilter = null;
            _receiverFilter = null;
            _contractFilter = null;
            _noteFilter = null;

            _days.Text = string.Empty;
            _least.Text = string.Empty;
            _search.Text = string.Empty;
        }
        finally
        {
            _filling = false;
        }

        Rebuild();
    }

    private Control BuildContent()
    {
        var host = _body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas };

        // Долната таблица е с постоянна височина: тя отговаря на въпрос, зададен
        // за един ред, и не бива да расте за сметка на списъка, който се преглежда.
        var bottom = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = Theme.Scaled(250),
            BackColor = Theme.Canvas,
            Padding = new Padding(0, Theme.Gap, 0, 0)
        };

        _caption.Dock = DockStyle.Top;
        // Два реда: името на кода по ЕКО е дълго и надписът минава на втори —
        // с място за един, краят му (колко реда и тона) се режеше.
        _caption.Font = Theme.SmallBold;
        _caption.Height = Math.Max(Theme.Scaled(24), 2 * Theme.SmallBold.Height + Theme.Scaled(4));
        _caption.ForeColor = Theme.InkFaint;

        bottom.Controls.Add(Frame(_detail));
        bottom.Controls.Add(_caption);

        // Двата сбора стоят един до друг: отляво какво се вижда, отдясно какво
        // е избрано от него. Второто е числото, с което човек натиска копчето,
        // и то трябва да се вижда, преди да го натисне.
        // Сборът на видимото се качи при заглавието; тук остава само избраното
        // — числото, с което човек натиска копчето.
        var sums = new Panel { Dock = DockStyle.Bottom, Height = Theme.Scaled(30), BackColor = Theme.Canvas };

        _chosen.Dock = DockStyle.Right;
        _chosen.Width = Theme.Scaled(380);
        _chosen.Font = Theme.Bold;
        _chosen.ForeColor = Theme.Ink;
        _chosen.TextAlign = ContentAlignment.MiddleRight;

        sums.Controls.Add(_chosen);

        host.Controls.Add(Frame(_master));
        host.Controls.Add(bottom);
        host.Controls.Add(sums);

        // На нисък екран — лаптоп при 150 % — долната таблица отстъпва, а не
        // горната: горе е списъкът, който се преглежда и от който се избира, и
        // той пази място поне за заглавието и няколко реда. С постоянните 250
        // пиксела (в мащаба — 375) горната таблица изчезваше изцяло.
        host.Resize += (_, _) =>
        {
            var room = host.ClientSize.Height - sums.Height - Theme.Scaled(220);
            var height = Math.Clamp(room, Theme.Scaled(140), Theme.Scaled(250));

            if (bottom.Height != height) bottom.Height = height;
        };

        // Ако и така не стига — лаптоп при 150 %, където лентата отгоре е на
        // три реда, — двете таблици пазят своето, а мястото под лентата се
        // превърта, вместо горната да се свие до заглавието си. Както
        // работната част в прозорчетата на склада (Dialog.KeepWork).
        host.AutoScroll = true;
        host.AutoScrollMinSize = new Size(0, sums.Height + Theme.Scaled(220 + 140));

        _bottom = bottom;
        _sumsHeight = sums.Height;

        BuildMaster();
        BuildDetail();

        return host;
    }

    private void BuildMaster()
    {
        Style(_master, multi: true);
        _master.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        // Докато таблицата се пълни наново, изборът в нея скача по няколко пъти —
        // изчистване, първи ред, възстановен ред — и всеки скок пълнеше долната
        // таблица изцяло. Пресмятането я пълни веднъж, накрая.
        _master.SelectionChanged += (_, _) =>
        {
            if (_rebuilding) return;

            ShowDetail();
            Weigh();
        };
        _master.ColumnHeaderMouseClick += (_, e) =>
        {
            // Ляв бутон подрежда, десен пресява — същото разделение, което
            // таблицата с данни в редактора вече прави. Пресява се по колоната,
            // която държи реда: другите две са число и дата, а списък с отметки
            // за тях е по-дълъг път от полето „над колко“ отдолу.
            if (e.Button == MouseButtons.Right)
            {
                if (SieveOf(_master.Columns[e.ColumnIndex].Name) is { } sieve) Pick(sieve);
                return;
            }

            SortBy(_master, e.ColumnIndex);
        };
        _master.Enter += (_, _) => { _active = _master; Weigh(); };

        AddColumn(_master, KeyColumn, Lang.T("Код", "Code"), 140);
        AddColumn(_master, NameColumn, Lang.T("Наименование", "Description"), 420, fill: true);
        AddColumn(_master, AmountColumn, Lang.T("Налично, т", "Available, t"), 140, right: true);
        AddColumn(_master, CountColumn, Lang.T("Партиди", "Batches"), 100, right: true);
        AddColumn(_master, AgeColumn, Lang.T("Най-стара", "Oldest"), 120);
        AddTreatColumn(_master, Lang.T("Третира целия ред — всички партиди зад него.",
                                       "Treats the whole row — every batch behind it."));

        // Тези стоят само в „Поотделно“. Добавени са накрая, а редът им на екрана
        // се казва изрично: първата клетка на реда трябва да си остане видима във
        // всеки изглед — по нея се слага курсорът.
        //
        // Текстовите делят мястото помежду си: десет стълба трябва да се съберат
        // на лаптоп, а на голям монитор да използват ширината му. Пълният текст
        // на отрязана клетка излиза под мишката.
        void Spare(string name, string header, int weight, int least)
        {
            AddColumn(_master, name, header, weight, fill: true);
            _master.Columns[name].MinimumWidth = Theme.Scaled(least);
        }

        AddColumn(_master, HandedColumn, Lang.T("Предадено на", "Handed over"), 106);
        AddColumn(_master, BatchColumn, Lang.T("Партида", "Batch"), 100);
        Spare(PartnerColumn, Lang.T("Клиент", "Client"), 170, 110);
        Spare(DocumentColumn, Lang.T("Приета на / документ", "Received / document"), 130, 90);
        Spare(ReceiverColumn, Lang.T("Получател", "Receiver"), 170, 110);
        Spare(ContractColumn, Moves.ContractTitle, 130, 90);
        Spare(NoteColumn, Lang.T("Бележка", "Note"), 120, 80);

        Reorder(_master, HandedColumn, KeyColumn, NameColumn, BatchColumn, PartnerColumn, DocumentColumn,
            ReceiverColumn, ContractColumn, NoteColumn, AmountColumn, CountColumn, AgeColumn, TreatColumn);

        // В „Поотделно“ долна таблица няма: партидата се отваря направо от реда.
        _master.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && ByMove) OpenLine(e.RowIndex);
        };

        _master.CellContentClick += (_, e) =>
        {
            if (!Pressed(_master, e)) return;

            _active = _master;
            ActOn(_master, [_master.Rows[e.RowIndex]]);
        };

        Aim(_master);

        var menu = Menu(_master,
            (Lang.T("Третирай избраното…", "Treat the selection…"), () =>
            {
                _active = _master;

                if (CanPickRows) ActOn(_master, Chosen(_master));
                else Act();
            }),
            (Lang.T("Предай направо избраното…", "Hand the selection over directly…"), () =>
            {
                _active = _master;
                SubmitShares(MasterShares(Chosen(_master)), direct: true);
            }),
            (Lang.T("Отвори партидата…", "Open the batch…"), () =>
            {
                if (_master.CurrentRow is { } row) OpenLine(row.Index);
            }));

        // Надписът следва склада по същата причина, по която и копчето горе.
        menu.Opening += (_, e) =>
        {
            menu.Items[0].Text = Store switch
            {
                StoreKind.Received => Lang.T("Третирай избраното…", "Treat the selection…"),
                StoreKind.Treated => Lang.T("Образувай от избраното…", "Generate from the selection…"),
                StoreKind.Recycled => Lang.T("Върни избраното в „Третиран“…", "Return the selection to “Treated”…"),
                _ => ActionTitle()
            };

            menu.Items[0].Visible = CanPickRows;
            menu.Items[1].Visible = CanSubmitDirect;
            menu.Items[2].Visible = ByMove;

            // От „Предаден“ в трите разреза не се прави нищо: празно меню не се отваря.
            if (!menu.Items.Cast<ToolStripItem>().Any(item => item.Available)) e.Cancel = true;
        };

        _master.ContextMenuStrip = menu;
    }

    private void BuildDetail()
    {
        Style(_detail, multi: true);
        _detail.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _detail.SelectionChanged += (_, _) => Weigh();
        _detail.ColumnHeaderMouseClick += (_, e) =>
        {
            // Същото разделение като горе: ляв бутон подрежда, десен пресява.
            if (e.Button == MouseButtons.Right)
            {
                if (SieveOf(_detail.Columns[e.ColumnIndex].Name) is { } sieve) Pick(sieve);
                return;
            }

            SortBy(_detail, e.ColumnIndex);
        };
        _detail.Enter += (_, _) => { _active = _detail; Weigh(); };

        AddColumn(_detail, BatchColumn, Lang.T("Партида", "Batch"), 140);
        AddColumn(_detail, PartnerColumn, Lang.T("Клиент", "Client"), 320, fill: true);
        AddColumn(_detail, DocumentColumn, Lang.T("Приета на / документ", "Received / document"), 220);
        // Кога и на кого. Стоят само в летописите — в трите склада с наличност
        // нищо не е излязло и стълбовете биха били празни на всеки ред. В
        // „Рециклиран“ стои само датата: получател там няма.
        //
        // Датата е първата от двете: в един месец към един и същ получател
        // тръгват три курса и „кога“ е това, което ги различава.
        AddColumn(_detail, HandedColumn, Lang.T("Предадено на", "Handed over"), 140);
        AddColumn(_detail, ReceiverColumn, Lang.T("Получател", "Receiver"), 170, fill: true);
        _detail.Columns[ReceiverColumn].MinimumWidth = Theme.Scaled(110);
        AddColumn(_detail, TakenColumn, Lang.T("Налично, т", "Available, t"), 140, right: true);
        AddTreatColumn(_detail, Lang.T("Третира само тази партида.", "Treats this batch alone."));

        // До получателя: четат се заедно — на кого и по какъв договор. Делят мястото
        // с останалия текст: иначе количеството излиза зад десния ръб на лаптоп.
        AddColumn(_detail, ContractColumn, Moves.ContractTitle, 130, fill: true);
        _detail.Columns[ContractColumn].MinimumWidth = Theme.Scaled(90);

        AddColumn(_detail, NoteColumn, Lang.T("Бележка", "Note"), 120, fill: true);
        _detail.Columns[NoteColumn].MinimumWidth = Theme.Scaled(80);

        Reorder(_detail, BatchColumn, PartnerColumn, DocumentColumn, HandedColumn, ReceiverColumn,
            ContractColumn, NoteColumn, TakenColumn, TreatColumn);

        _detail.CellContentClick += (_, e) =>
        {
            if (!Pressed(_detail, e)) return;

            _active = _detail;
            ActOn(_detail, [_detail.Rows[e.RowIndex]]);
        };

        // Двойното щракване отваря партидата, а не третира: отварянето е
        // безобидно, третирането — не, и двете не бива да си делят един жест.
        _detail.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && !Pressed(_detail, e)) Open(e.RowIndex);
        };

        Aim(_detail);

        var origin = Menu(_detail,
            (Lang.T("Третирай избраните партиди…", "Treat the selected batches…"), () =>
            {
                _active = _detail;
                ActOn(_detail, Chosen(_detail));
            }),
            (Lang.T("Предай направо избраните партиди…", "Hand the selected batches over directly…"),
                () =>
                {
                    _active = _detail;
                    SubmitShares(DetailShares(Chosen(_detail)), direct: true);
                }),
            (Lang.T("Отвори партидата…", "Open the batch…"), () =>
            {
                if (_detail.CurrentRow is { } row) Open(row.Index);
            }));

        var act = origin.Items[0];
        var direct = origin.Items[1];

        // Надписът следва склада: партида се третира от „Получен“, образува се
        // от „Третиран“, предава се от „Образуван“, а рециклираното се връща от
        // „Рециклиран“. Прякото предаване е второ действие и то важи само в
        // „Получен“.
        origin.Opening += (_, _) =>
        {
            act.Visible = CanPickRows;
            act.Text = Store switch
            {
                StoreKind.Treated =>
                    Lang.T("Образувай от избраните партиди…", "Generate from the selected batches…"),
                StoreKind.Formed =>
                    Lang.T("Предай избраните партиди…", "Hand the selected batches over…"),
                StoreKind.Recycled =>
                    Lang.T("Върни избраните редове в „Третиран“…", "Return the selected rows to “Treated”…"),
                _ => Lang.T("Третирай избраните партиди…", "Treat the selected batches…")
            };

            direct.Visible = CanSubmitDirect;
        };

        _detail.ContextMenuStrip = origin;
    }

    /// <summary>
    /// Редът на колоните на екрана — независимо от реда на добавянето им.
    ///
    /// Колоните се познават и по номер, затова новата се добавя накрая, а мястото
    /// ѝ се казва тук.
    /// </summary>
    private static void Reorder(DataGridView grid, params string[] columns)
    {
        for (var i = 0; i < columns.Length; i++) grid.Columns[columns[i]].DisplayIndex = i;
    }

    /// <summary>
    /// По кое сито пресява тази колона. Празно значи, че не пресява.
    ///
    /// Едно място за двете таблици: колоната с код или клиент се сменя с разреза,
    /// а десният бутон трябва да пресява онова, което заглавието казва.
    /// </summary>
    private Sieve? SieveOf(string column) => column switch
    {
        KeyColumn or NameColumn => ByReceiver ? Sieve.Receiver : ByPartner ? Sieve.Partner : Sieve.Code,

        // Долу стои онова, което горе го няма: кодът по клиент и по получател,
        // клиентът иначе.
        PartnerColumn => ByPartner || ByReceiver ? Sieve.Code : Sieve.Partner,

        ReceiverColumn => Sieve.Receiver,
        ContractColumn => Sieve.Contract,
        NoteColumn => Sieve.Note,
        _ => null
    };

    /// <summary>
    /// Копчето „Третирай“ на самия ред.
    ///
    /// Колона, а не само меню: това е същият жест, с който се пренася ред в
    /// отчета, и е единственият, който се вижда, без да се търси. Менюто и
    /// лентата остават за избраното наведнъж.
    /// </summary>
    private void AddTreatColumn(DataGridView grid, string tip)
    {
        var column = new DataGridViewButtonColumn
        {
            Name = TreatColumn,
            HeaderText = string.Empty,
            Text = Lang.T("Третирай", "Treat"),
            UseColumnTextForButtonValue = true,
            Width = Theme.Scaled(110),
            FlatStyle = FlatStyle.Flat,
            ToolTipText = tip,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            Resizable = DataGridViewTriState.False
        };

        column.DefaultCellStyle.BackColor = Theme.Surface;
        column.DefaultCellStyle.ForeColor = Theme.Ink;
        column.DefaultCellStyle.SelectionBackColor = Theme.AccentLight;
        column.DefaultCellStyle.SelectionForeColor = Theme.Ink;

        grid.Columns.Add(column);
    }

    /// <summary>
    /// Подредба по щракнатото заглавие.
    ///
    /// Второ щракване по същата колона обръща посоката — единственият жест,
    /// който хората пробват сами. Първото щракване подрежда по големина за
    /// числата (най-голямото първо: то е интересното) и по азбучен ред за
    /// текста; датата тръгва от най-старото, защото точно то е въпросът на
    /// временния склад.
    ///
    /// Подрежда се самата наличност, а не клетките. В таблицата „12,400“ и
    /// „5,000“ са текст и подредени като текст, петте тона застават след
    /// дванайсетте — а колоната казва „тонове“.
    /// </summary>
    private void SortBy(DataGridView grid, int column)
    {
        if (column < 0 || column >= grid.Columns.Count) return;

        var name = grid.Columns[column].Name;
        if (name == TreatColumn) return;

        var master = grid == _master;
        var chosen = master ? _masterSort : _detailSort;
        var down = master ? _masterDown : _detailDown;

        if (chosen == name) down = !down;
        else down = Numeric(name);

        if (master)
        {
            _masterSort = name;
            _masterDown = down;
        }
        else
        {
            _detailSort = name;
            _detailDown = down;
        }

        if (master) Rebuild();
        else ShowDetail();
    }

    /// <summary>Кои колони тръгват от най-голямото — числата.</summary>
    private static bool Numeric(string column) =>
        column is AmountColumn or CountColumn or TakenColumn;

    /// <summary>Подрежда по ключ в избраната посока.</summary>
    private static IEnumerable<T> By<T, TKey>(
        IEnumerable<T> rows, Func<T, TKey> key, bool down, IComparer<TKey>? order = null) =>
        down ? rows.OrderByDescending(key, order) : rows.OrderBy(key, order);

    /// <summary>
    /// Слага стрелката върху подредената колона и я маха от останалите.
    ///
    /// Колоните са <see cref="DataGridViewColumnSortMode.Programmatic"/>: самата
    /// таблица не подрежда нищо, но рисува стрелката, когато ѝ се каже. Без нея
    /// подредбата се вижда само по редовете и човек не знае по какво гледа.
    /// </summary>
    private static void Mark(DataGridView grid, string? column, bool down)
    {
        foreach (DataGridViewColumn each in grid.Columns)
            each.HeaderCell.SortGlyphDirection = each.Name == column
                ? down ? SortOrder.Descending : SortOrder.Ascending
                : SortOrder.None;
    }

    /// <summary>Дали е пипнато копчето на реда, а не обикновена клетка.</summary>
    private static bool Pressed(DataGridView grid, DataGridViewCellEventArgs e) =>
        e.RowIndex >= 0 && e.ColumnIndex >= 0 && grid.Columns[e.ColumnIndex].Name == TreatColumn;

    /// <summary>
    /// Десният бутон мести избора само когато щракнатият ред е извън него.
    ///
    /// Иначе менюто би важало за друг ред — а при избрани двайсет реда
    /// щракването върху тях би ги изгубило точно преди действието, което се
    /// отнася за тях.
    /// </summary>
    private static void Aim(DataGridView grid)
    {
        grid.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;
            if (grid.Rows[e.RowIndex].Selected) return;

            grid.CurrentCell = grid.Rows[e.RowIndex].Cells[0];
        };
    }

    /// <summary>Контекстно меню, което се показва само когато има ред под него.</summary>
    private static ContextMenuStrip Menu(DataGridView grid, params (string Text, Action Do)[] items)
    {
        var menu = new ContextMenuStrip { Font = Theme.Base };

        foreach (var (text, action) in items) menu.Items.Add(text, null, (_, _) => action());

        menu.Opening += (_, e) => e.Cancel = grid.CurrentRow is null;

        return menu;
    }

    private static Control Frame(Control child)
    {
        var frame = new Card
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(1),
            Fill = Theme.Canvas,
            Outline = Theme.Border
        };

        child.Dock = DockStyle.Fill;
        frame.Controls.Add(child);

        return frame;
    }

    private static void Style(DataGridView grid, bool multi = false)
    {
        grid.Font = Theme.Base;
        grid.BackgroundColor = Theme.Canvas;
        grid.BorderStyle = BorderStyle.None;
        grid.EnableHeadersVisualStyles = false;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.AllowUserToOrderColumns = false;
        grid.RowHeadersVisible = false;
        grid.MultiSelect = multi;
        grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;

        grid.ColumnHeadersDefaultCellStyle.BackColor = Theme.Surface;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Theme.InkStrong;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.Surface;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Theme.InkStrong;
        grid.ColumnHeadersDefaultCellStyle.Font = Theme.Bold;
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 6, 6, 6);

        grid.DefaultCellStyle.BackColor = Theme.Canvas;
        grid.DefaultCellStyle.ForeColor = Theme.Ink;
        grid.DefaultCellStyle.SelectionBackColor = Theme.AccentLight;
        grid.DefaultCellStyle.SelectionForeColor = Theme.Ink;
        grid.DefaultCellStyle.Padding = new Padding(6, 4, 6, 4);
        grid.AlternatingRowsDefaultCellStyle.BackColor = Theme.Surface;
        grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = Theme.AccentLight;
        grid.AlternatingRowsDefaultCellStyle.SelectionForeColor = Theme.Ink;
        grid.GridColor = Theme.Border;

        var line = Math.Max(1, grid.Font.Height);
        grid.RowTemplate.Height = line + 14;
        // Две реда високо: под името на колоната застава сборът ѝ.
        grid.ColumnHeadersHeight = 2 * line + 18;
    }

    private static void AddColumn(
        DataGridView grid, string name, string header, int width, bool right = false, bool fill = false)
    {
        var column = new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            Width = Theme.Scaled(width),
            SortMode = DataGridViewColumnSortMode.Programmatic,
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
            column.MinimumWidth = Theme.Scaled(200);
        }

        grid.Columns.Add(column);
    }

    // ------------------------------------------------------------- пълнене

    public new void Refresh()
    {
        _filling = true;

        try
        {
            FillStages();
        }
        finally
        {
            _filling = false;
        }

        Rebuild();
        base.Refresh();
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

    /// <summary>Тече ли пресмятане — тогава изборът в горната таблица не пълни долната.</summary>
    private bool _rebuilding;

    /// <summary>Пресмята наличността и я показва в избрания разрез.</summary>
    private void Rebuild()
    {
        if (_filling || IsDisposed) return;

        _rebuilding = true;

        try
        {
            Recompute();
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private void Recompute()
    {
        _pageTitle.Text = _scope.Titled(Lang.T("Склад", "Store"));

        _master.Rows.Clear();
        _detail.Rows.Clear();
        _shares = [];

        Allow();

        if (Company() is not { } companyId)
        {
            _summary.Text = Lang.T("Заведете фирма в „Настройки“, за да започнете.",
                                   "Add a company in Settings to begin.");
            return;
        }

        var stage = Stage();
        var on = DateOnly.FromDateTime(_date.Value);

        var batches = _workspace.Batches.List(companyId, stage);
        var moves = _workspace.Ledger.Read(companyId, stage);
        var links = _workspace.Ledger.Links(companyId, stage);

        // Склад „Третиран“ се смята по своя сметка: влизащото и излизащото в
        // него носят различни кодове и разликата по код не значи нищо.
        if (Store == StoreKind.Treated)
        {
            var waiting = StockMath.Waiting(batches, moves, links, on);

            _shares = [.. waiting.Shares.Select(StockLine.Of)];
            _loose = waiting.Loose;
        }
        else
        {
            // В летописа редовете носят и получателя: един и същ код от една и
            // съща партида може да е излязъл към трима оползотворители, а дялът
            // ги събира в едно число. Получателите се четат с ЕДНО питане.
            var parties = CanSeeReceivers ? _workspace.Ledger.Parties(companyId, stage) : null;
            DateOnly? from = MonthOnly ? LedgerPeriod.Of(on).First : null;

            _shares = StockMath.Lines(batches, moves, links, Store, on, parties, from);

            // „Поотделно“ обещава ВСЯКО движение, а редовете горе тръгват от
            // дяловете: предаване без посочена партида иначе би липсвало.
            if (ByMove) _shares = [.. _shares, .. StockMath.Unlinked(moves, links, Store, on, parties, from)];

            // Невързаното за партида се брои и тук: предаденото от склад
            // „Образуван“ рядко намира партида — тя помни кода, с който
            // отпадъкът е дошъл, а движението носи кода, с който е излязъл.
            // Без този ред изгледът би обявил за по-малко излязло, отколкото е.
            //
            // В „Поотделно“ то вече стои със свой ред и второ казване би го
            // броило два пъти.
            _loose = MonthOnly || ByMove ? 0m : StockMath.Loose(moves, links, Store, on);
        }

        // Менютата се пълнят от цялата наличност, а не от вече пресятата:
        // иначе избраният код изпразва списъка с клиенти и двата филтъра
        // започват да се изключват взаимно.
        FillFilters();

        var kept = Kept();

        if (ByMove) FillByMove(kept);
        else if (ByReceiver) FillByReceiver(kept);
        else if (ByPartner) FillByPartner(kept);
        else FillByCode(kept);

        Describe(kept, on);
        Mark(_master, _masterSort, _masterDown);
        ShowDetail();
        Weigh();
    }

    /// <summary>
    /// Каквото остава след филтрите.
    ///
    /// Едно място за всички: таблицата, сборовете, произходът отдолу и
    /// избраното за третиране гледат едно и също. Пресято на четири места, то
    /// щеше да се разминава — и точно там, където човек натиска копче.
    ///
    /// Филтърът за количество е последен и е различен по устройство: той пита за
    /// РЕДА, не за дяла. „Над един тон“ значи „купчина над един тон“; приложен
    /// на всеки дял поотделно, той би скрил код от пет тона, събран от десет
    /// доставки по половин.
    /// </summary>
    private IReadOnlyList<StockLine> Kept()
    {
        var kept = _shares.Where(Matches).ToList();

        if (Least() is not { } least || least <= 0) return kept;

        var keys = kept
            .GroupBy(Key, StringComparer.Ordinal)
            .Where(g => SplitMath.Round(g.Sum(x => x.Amount)) >= least)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

        return [.. kept.Where(x => keys.Contains(Key(x)))];
    }

    /// <summary>Пресича ли нещо в момента — по него празната таблица се обяснява.</summary>
    private bool Filtered =>
        _codeFilter is { Count: > 0 } ||
        _partnerFilter is { Count: > 0 } ||
        _receiverFilter is { Count: > 0 } ||
        _contractFilter is { Count: > 0 } ||
        _noteFilter is { Count: > 0 } ||
        _days.Text.Trim().Length > 0 ||
        _least.Text.Trim().Length > 0 ||
        _search.Text.Trim().Length > 0;

    /// <summary>
    /// По какво се групира в избрания разрез — код, клиент, получател, или самият
    /// ред, когато нищо не се събира: дял на една партида в едно предаване.
    /// </summary>
    private string Key(StockLine share) =>
        ByMove ? $"{share.MoveId}:{share.Batch.Id}"
        : ByReceiver ? share.Party.Key
        : ByPartner ? share.Batch.Eik
        : share.Ewc;

    /// <summary>
    /// Изписва какво пропускат филтрите.
    ///
    /// Копчето казва състоянието си само: „(всички)“, самата стойност, когато е
    /// една, или колко са. Филтър, който не си личи, се забравя включен и после
    /// празната таблица изглежда като загубени данни.
    /// </summary>
    private void FillFilters()
    {
        _partnerIds.Clear();

        foreach (var eik in _shares.Select(x => x.Batch.Eik).Distinct(StringComparer.Ordinal))
            _partnerIds.Add(eik);

        // Копчето е с ширината на полето до него, затова надписът се скъсява,
        // вместо да се реже: „160216 — Компоненти, отстранени от…“ не се събира
        // никъде, а първите му думи стигат.
        _receiverIds.Clear();

        foreach (var key in _shares.Select(x => x.Party.Key).Distinct(StringComparer.Ordinal))
            _receiverIds.Add(key);

        // Пет сита на един ред са по-тесни от две (виж Allow): и надписът им се
        // скъсява по-рано.
        var most = CanSeeReceivers ? 18 : 28;

        _codes.Text = Shorten(Caption(_codeFilter, Describe), most);
        _partners.Text = Shorten(Caption(_partnerFilter, PartnerName), most);
        _receivers.Text = Shorten(Caption(_receiverFilter, Receiver), most);
        _contracts.Text = Shorten(Caption(_contractFilter, ContractName), most);
        _notes.Text = Shorten(Caption(_noteFilter, ContractName), most);
    }

    /// <summary>Дълъг надпис върху копче — скъсен с многоточие.</summary>
    private static string Shorten(string text, int most) =>
        text.Length <= most ? text : text[..(most - 1)].TrimEnd() + "…";

    /// <summary>Как се изписва един филтър върху копчето си.</summary>
    private static string Caption(HashSet<string>? chosen, Func<string, string> describe) => chosen switch
    {
        null or { Count: 0 } => Lang.T("(всички)", "(all)"),
        { Count: 1 } one => describe(one.First()),
        { Count: var many } => Lang.T($"{many} избрани", $"{many} chosen")
    };

    /// <summary>Клиентът с ЕИК-а — както се чете в списъка за отметки.</summary>
    private string PartnerName(string eik)
    {
        var name = _shares
            .Where(x => string.Equals(x.Batch.Eik, eik, StringComparison.Ordinal))
            .Select(x => x.Batch.PartnerName)
            .FirstOrDefault(n => n.Length > 0);

        if (name is { Length: > 0 }) return eik.Length > 0 ? $"{name} — {eik}" : name;

        return eik.Length > 0 ? eik : Lang.T("(клиент не е посочен)", "(no client stated)");
    }

    /// <summary>
    /// Отметките за код или за клиент — същият прозорец, който пресява и в
    /// редактора.
    ///
    /// Стойностите идват от това, което наистина стои в склада, а не от цялата
    /// номенклатура: осемстотин кода по ЕКО са възможни, но в един склад стоят
    /// пет.
    /// </summary>
    private void Pick(bool byPartner) => Pick(byPartner ? Sieve.Partner : Sieve.Code);

    /// <summary>По кое се пресява. Едно сито, няколко въпроса.</summary>
    private enum Sieve
    {
        Code,
        Partner,
        Receiver,
        Contract,
        Note
    }

    private void Pick(Sieve sieve)
    {
        var keys = _shares
            .Select(x => Of(x, sieve))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (keys.Count == 0) return;

        // Показва се четимото, връща се ключът: човек отмята „Метал ООД“, а
        // филтърът пази ЕИК-а.
        var shown = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var key in keys)
        {
            var text = sieve switch
            {
                Sieve.Partner => PartnerName(key),
                Sieve.Receiver => Receiver(key),
                Sieve.Contract or Sieve.Note => ContractName(key),
                _ => Describe(key)
            };

            // Две различни стойности с еднакъв надпис не бива да се слеят.
            while (shown.ContainsKey(text)) text += " ";

            shown[text] = key;
        }

        var values = shown.Keys
            .OrderBy(x => x, sieve == Sieve.Code ? StringComparer.Ordinal : StringComparer.CurrentCulture)
            .ToList();

        var chosen = sieve switch
        {
            Sieve.Partner => _partnerFilter,
            Sieve.Receiver => _receiverFilter,
            Sieve.Contract => _contractFilter,
            Sieve.Note => _noteFilter,
            _ => _codeFilter
        };

        var allowed = chosen is null
            ? null
            : values.Where(v => chosen.Contains(shown[v])).ToHashSet(StringComparer.Ordinal);

        using var dialog = new Editor.ColumnFilter(Title(sieve), values, allowed);

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var picked = dialog.ShowsEverything
            ? null
            : dialog.Chosen.Select(v => shown[v]).ToHashSet(StringComparer.Ordinal);

        switch (sieve)
        {
            case Sieve.Partner: _partnerFilter = picked; break;
            case Sieve.Receiver: _receiverFilter = picked; break;
            case Sieve.Contract: _contractFilter = picked; break;
            case Sieve.Note: _noteFilter = picked; break;
            default: _codeFilter = picked; break;
        }

        Rebuild();
    }

    /// <summary>Каква стойност гледа това сито на един ред.</summary>
    private static string Of(StockLine line, Sieve sieve) => sieve switch
    {
        Sieve.Partner => line.Batch.Eik,
        Sieve.Receiver => line.Party.Key,
        Sieve.Contract => line.Contract,
        Sieve.Note => line.Note,
        _ => line.Ewc
    };

    private static string Title(Sieve sieve) => sieve switch
    {
        Sieve.Partner => Lang.T("Клиент", "Client"),
        Sieve.Receiver => Lang.T("Получател", "Receiver"),
        Sieve.Contract => Moves.ContractTitle,
        Sieve.Note => Lang.T("Бележка", "Note"),
        _ => Lang.T("Код", "Code")
    };

    /// <summary>
    /// Едно търсене за двата разреза.
    ///
    /// Човек пише „Метал“ или „1602“ и не му се обяснява кое поле търси —
    /// пробват се кодът, наименованието му, клиентът, ЕИК-ът и номерът на
    /// партидата.
    ///
    /// Полетата нарочно се застъпват. Дума като „метал“ хваща и клиента
    /// „Метал ООД“, и кода „черни метали“ — и двете остават. Разделянето им на
    /// две търсения би искало от човека да знае предварително кое търси, а той
    /// най-често помни само думата. За еднозначно търсене има ЕИК и номер на
    /// партида.
    /// </summary>
    private bool Matches(StockLine share)
    {
        if (_codeFilter is { Count: > 0 } codes && !codes.Contains(share.Ewc)) return false;

        if (_partnerFilter is { Count: > 0 } partners && !partners.Contains(share.Batch.Eik)) return false;

        if (_receiverFilter is { Count: > 0 } receivers && !receivers.Contains(share.Party.Key))
            return false;

        if (_contractFilter is { Count: > 0 } contracts && !contracts.Contains(share.Contract))
            return false;

        if (_noteFilter is { Count: > 0 } notes && !notes.Contains(share.Note))
            return false;

        // Престоят се мери към датата, към която се гледа складът, а не към
        // днес: „кое стоеше повече от 90 дни на 30 септември“ е въпрос, който се
        // задава през октомври.
        if (Days() is { } days && days > 0)
        {
            var on = DateOnly.FromDateTime(_date.Value);
            if (on.DayNumber - share.Batch.ReceivedOn.DayNumber < days) return false;
        }

        var needle = _search.Text.Trim();
        if (needle.Length == 0) return true;

        return Contains(share.Ewc, needle)
            || Contains(Describe(share.Ewc), needle)
            || Contains(share.Batch.PartnerName, needle)
            || Contains(share.Batch.Eik, needle)
            || Contains(share.Batch.Code, needle)
            || Contains(share.Contract, needle)
            || Contains(share.Note, needle);
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>Празно поле значи „без филтър“, а не нула.</summary>
    private int? Days() => int.TryParse(_days.Text.Trim(), out var days) ? days : null;

    private decimal? Least() => SplitMath.TryRead(_least.Text, out var least) ? least : null;

    private void FillByCode(IReadOnlyList<StockLine> shares)
    {
        // Сборът на видимото стои в заглавието на колоната — там, където се
        // гледа самото число. Мени се с всеки филтър.
        _master.Columns[AmountColumn].HeaderText =
            LedgerPage.Summed(AmountHeader(), shares.Sum(s => s.Amount));

        _master.Columns[KeyColumn].HeaderText = Lang.T("Код", "Code");
        _master.Columns[NameColumn].HeaderText = Lang.T("Наименование", "Description");
        _master.Columns[CountColumn].HeaderText = Lang.T("Партиди", "Batches");

        var rows = shares
            .GroupBy(s => s.Ewc, StringComparer.Ordinal)
            .Select(g => new Pile(
                g.Key,
                _names.GetValueOrDefault(g.Key, string.Empty),
                SplitMath.Round(g.Sum(s => s.Amount)),
                g.Select(s => s.Batch.Id).Distinct().Count(),
                g.Min(s => s.Batch.ReceivedOn)))
            .ToList();

        Add(Arrange(rows, byCode: true));
    }

    /// <summary>Един ред от горната таблица, сведен до това, по което се подрежда.</summary>
    private sealed record Pile(string Key, string Name, decimal Amount, int Count, DateOnly Oldest);

    /// <summary>
    /// Подредбата на горната таблица.
    ///
    /// Без избрана колона всеки изглед си има своя: по код — по самия код,
    /// защото така стои и в номенклатурата; по клиент — по количество надолу,
    /// защото въпросът там е „кой има най-много при нас“.
    /// </summary>
    private IEnumerable<Pile> Arrange(IReadOnlyList<Pile> rows, bool byCode) => _masterSort switch
    {
        KeyColumn => By(rows, p => p.Key, _masterDown, StringComparer.Ordinal),
        NameColumn => By(rows, p => p.Name, _masterDown, StringComparer.CurrentCulture),
        AmountColumn => By(rows, p => p.Amount, _masterDown),
        CountColumn => By(rows, p => p.Count, _masterDown),
        AgeColumn => By(rows, p => p.Oldest, _masterDown),
        _ => byCode
            ? rows.OrderBy(p => p.Key, StringComparer.Ordinal)
            : rows.OrderByDescending(p => p.Amount)
    };

    /// <summary>
    /// Слага редовете в горната таблица — наведнъж, както в „Поотделно“: по
    /// клиент редовете са колкото доставчиците, а те са стотици.
    /// </summary>
    private void Add(IEnumerable<Pile> piles)
    {
        int At(string column) => _master.Columns[column]!.Index;

        var (key, name, amount, count, age) =
            (At(KeyColumn), At(NameColumn), At(AmountColumn), At(CountColumn), At(AgeColumn));

        var unnamed = ByReceiver ? Lang.T("(получател не е посочен)", "(no receiver stated)")
            : ByPartner ? Lang.T("(клиент не е посочен)", "(no client stated)")
            : string.Empty;

        var rows = new List<DataGridViewRow>();

        foreach (var pile in piles)
        {
            var row = (DataGridViewRow)_master.RowTemplate.Clone();

            row.CreateCells(_master);

            row.Cells[key].Value = pile.Key.Length > 0 ? pile.Key : Lang.T("—", "—");
            row.Cells[name].Value = pile.Name.Length > 0 ? pile.Name : unnamed;
            row.Cells[amount].Value = SplitMath.Amount(pile.Amount);
            row.Cells[count].Value = pile.Count.ToString();
            row.Cells[age].Value = pile.Oldest.ToString(ValueConverter.BulgarianDateFormat);

            row.Tag = pile.Key;

            rows.Add(row);
        }

        _master.Rows.AddRange([.. rows]);
    }

    private void FillByPartner(IReadOnlyList<StockLine> shares)
    {
        _master.Columns[AmountColumn].HeaderText =
            LedgerPage.Summed(AmountHeader(), shares.Sum(s => s.Amount));

        _master.Columns[KeyColumn].HeaderText = Lang.T("ЕИК", "UIC");
        _master.Columns[NameColumn].HeaderText = Lang.T("Клиент", "Client");
        _master.Columns[CountColumn].HeaderText = Lang.T("Кодове", "Codes");

        var rows = shares
            .GroupBy(s => s.Batch.Eik, StringComparer.Ordinal)
            .Select(g => new Pile(
                g.Key,
                g.Select(s => s.Batch.PartnerName).FirstOrDefault(n => n.Length > 0) ?? string.Empty,
                SplitMath.Round(g.Sum(s => s.Amount)),
                g.Select(s => s.Ewc).Distinct(StringComparer.Ordinal).Count(),
                g.Min(s => s.Batch.ReceivedOn)))
            .ToList();

        Add(Arrange(rows, byCode: false));
    }

    /// <summary>
    /// Горната таблица, гледана откъм ПОЛУЧАТЕЛЯ.
    ///
    /// Обръща същите числа за трети път: „по код“ казва какво е излязло, „по
    /// клиент“ — чие е било, „по получател“ — на кого е отишло. Третият е
    /// въпросът, който идва отвън: оползотворителят се обажда и пита колко сме
    /// му предали през годината.
    /// </summary>
    private void FillByReceiver(IReadOnlyList<StockLine> shares)
    {
        _master.Columns[AmountColumn].HeaderText =
            LedgerPage.Summed(AmountHeader(), shares.Sum(s => s.Amount));

        _master.Columns[KeyColumn].HeaderText = Lang.T("ЕИК / ДДС", "VAT id");
        _master.Columns[NameColumn].HeaderText = Lang.T("Получател", "Receiver");
        _master.Columns[CountColumn].HeaderText = Lang.T("Кодове", "Codes");

        var rows = shares
            .GroupBy(s => s.Party.Key, StringComparer.Ordinal)
            .Select(g => new Pile(
                g.Key,
                g.Select(s => s.Party.Name).FirstOrDefault(n => n.Length > 0) ?? string.Empty,
                SplitMath.Round(g.Sum(s => s.Amount)),
                g.Select(s => s.Ewc).Distinct(StringComparer.Ordinal).Count(),
                g.Min(s => s.Batch.ReceivedOn)))
            .ToList();

        Add(Arrange(rows, byCode: false));
    }

    /// <summary>
    /// Горната таблица без никакво събиране: всеки ред е дял на една партида в едно
    /// предаване — най-дребното, което е излязло, с всичко, което се знае за него.
    ///
    /// Без избрана колона отгоре стои най-новото — както и долу в летописа:
    /// питането е „какво излезе последно“.
    /// </summary>
    private void FillByMove(IReadOnlyList<StockLine> shares)
    {
        _master.Columns[AmountColumn].HeaderText =
            LedgerPage.Summed(AmountHeader(), shares.Sum(s => s.Amount));

        _master.Columns[KeyColumn].HeaderText = Lang.T("Код", "Code");

        // Стълбът е тесен и пълното заглавие би се рязало.
        _master.Columns[NameColumn].HeaderText = Lang.T("Отпадък", "Waste");

        IEnumerable<StockLine> ordered = _masterSort switch
        {
            HandedColumn => By(shares, s => (s.Moved ?? default, s.MoveId), _masterDown),
            KeyColumn => By(shares, s => s.Ewc, _masterDown, StringComparer.Ordinal),
            NameColumn => By(shares, s => _names.GetValueOrDefault(s.Ewc, string.Empty), _masterDown,
                StringComparer.CurrentCulture),
            BatchColumn => By(shares, s => s.Batch.Code, _masterDown, StringComparer.Ordinal),
            PartnerColumn => By(shares, s => Partner(s.Batch), _masterDown, StringComparer.CurrentCulture),
            DocumentColumn => By(shares, s => s.Batch.ReceivedOn, _masterDown),
            ReceiverColumn => By(shares, s => s.Party.Display, _masterDown, StringComparer.CurrentCulture),
            ContractColumn => By(shares, s => s.Contract, _masterDown, StringComparer.CurrentCulture),
            NoteColumn => By(shares, s => s.Note, _masterDown, StringComparer.CurrentCulture),
            AmountColumn => By(shares, s => s.Amount, _masterDown),
            _ => shares
                .OrderByDescending(s => s.Moved ?? default)
                .ThenByDescending(s => s.MoveId)
                .ThenBy(s => s.Batch.ReceivedOn)
                .ThenBy(s => s.Batch.Id)
        };

        // Наведнъж, а не ред по ред. Тук редовете са хиляди — заета площадка трупа
        // по няколкостотин дяла на месец — а добавяне по един струваше две секунди
        // на всяко натискане в търсенето. Номерата на стълбовете се вземат веднъж:
        // ред, още невързан за таблицата, не познава клетките си по име.
        int At(string column) => _master.Columns[column]!.Index;

        var (date, code, name, batch, client, document, receiver, contract, note, amount) =
            (At(HandedColumn), At(KeyColumn), At(NameColumn), At(BatchColumn), At(PartnerColumn),
             At(DocumentColumn), At(ReceiverColumn), At(ContractColumn), At(NoteColumn), At(AmountColumn));

        var noBatch = Lang.T("(без посочена партида)", "(no batch stated)");
        var noReceiver = Lang.T("(не е посочен)", "(not stated)");

        var rows = new List<DataGridViewRow>(shares.Count);

        foreach (var line in ordered)
        {
            // От образеца на таблицата: той носи височината на реда.
            var row = (DataGridViewRow)_master.RowTemplate.Clone();

            row.CreateCells(_master);

            row.Cells[date].Value = line.Moved?.ToString(ValueConverter.BulgarianDateFormat) ?? string.Empty;
            row.Cells[code].Value = line.Ewc;
            row.Cells[name].Value = _names.GetValueOrDefault(line.Ewc, string.Empty);
            row.Cells[batch].Value = BatchText(line);
            row.Cells[client].Value = line.Batch.IsNone ? noBatch : Partner(line.Batch);
            row.Cells[document].Value = DocumentText(line);
            row.Cells[receiver].Value = line.Party.Known ? line.Party.Display : noReceiver;
            row.Cells[contract].Value = line.Contract;
            row.Cells[note].Value = line.Note;
            row.Cells[amount].Value = SplitMath.Amount(line.Amount);

            row.Tag = Key(line);

            rows.Add(row);
        }

        _master.Rows.AddRange([.. rows]);

        // Кодът се мери по истинските кодове: на Windows 10 няма „Segoe UI
        // Variable“ и по-широкият „Segoe UI“ режеше звездичката на опасните
        // кодове при 100 %. Различните кодове са малко — мерят се за миг.
        var codeWidth = shares.Select(s => s.Ewc).Distinct()
            .Select(ewc => TextRenderer.MeasureText(ewc, _master.Font).Width + CellPadding)
            .DefaultIfEmpty(0).Max();

        var key = _master.Columns[KeyColumn];
        var wanted = Math.Max(Theme.Scaled(66), codeWidth);
        if (key.Width != wanted) key.Width = wanted;
    }

    /// <summary>Номерът на партидата, както се чете в таблица.</summary>
    private static string BatchText(StockLine line) =>
        line.Batch.IsNone ? "—" : "#" + line.Batch.Code;

    /// <summary>Кога и с какво е приета партидата зад реда.</summary>
    private static string DocumentText(StockLine line)
    {
        if (line.Batch.IsNone) return string.Empty;

        var day = line.Batch.ReceivedOn.ToString(ValueConverter.BulgarianDateFormat);
        var text = line.Batch.Document.Length > 0 ? $"{day} · {line.Batch.Document}" : day;

        return line.Batch.Opening ? text + Lang.T(" · заварено", " · carried in") : text;
    }

    /// <summary>Долната таблица: какво стои зад избрания ред.</summary>
    private void ShowDetail()
    {
        _detail.Rows.Clear();

        // В „Поотделно“ редът горе вече Е най-дребното: под него няма какво да се
        // разгъне и долната половина я няма.
        if (ByMove) return;

        if (_master.CurrentRow?.Tag is not string key)
        {
            _caption.Text = Lang.T("Изберете ред, за да видите произхода му.",
                                   "Pick a row to see where it came from.");
            return;
        }

        var chosen = Kept().Where(s => Key(s) == key);

        // Без избрана колона долу стои редът, по който партидите ще бъдат
        // изчерпани: най-старата първа.
        var rows = (_detailSort switch
        {
            BatchColumn => By(chosen, s => s.Batch.Code, _detailDown, StringComparer.Ordinal),
            PartnerColumn => By(chosen,
                s => ByPartner ? s.Ewc : Partner(s.Batch), _detailDown, StringComparer.CurrentCulture),
            DocumentColumn => By(chosen, s => s.Batch.ReceivedOn, _detailDown),
            HandedColumn => By(chosen, s => s.Moved ?? default, _detailDown),
            ReceiverColumn => By(chosen, s => s.Party.Display, _detailDown, StringComparer.CurrentCulture),
            ContractColumn => By(chosen, s => s.Contract, _detailDown, StringComparer.CurrentCulture),
            NoteColumn => By(chosen, s => s.Note, _detailDown, StringComparer.CurrentCulture),
            TakenColumn => By(chosen, s => s.Amount, _detailDown),

            // В летописа без избрана колона отгоре стои НАЙ-НОВОТО предаване:
            // питането е „какво излезе последно“, а не „кое е най-старо“. В
            // трите склада с наличност си остава редът, по който ще се изчерпи.
            _ => Chronicle
                ? chosen.OrderByDescending(s => s.Moved ?? default).ThenByDescending(s => s.MoveId)
                : chosen.OrderBy(s => s.Batch.ReceivedOn).ThenBy(s => s.Batch.Id)
        }).ToList();

        // Наведнъж, а не ред по ред — както горе в „Поотделно“. Тук редовете се
        // пълнят наново при всяко щракване по горната таблица, а в „Получен“ зад
        // един код стоят стотици партиди: по един ред това беше четвърт секунда
        // на щракване. Номерата на стълбовете се вземат веднъж: ред, още невързан
        // за таблицата, не познава клетките си по име.
        int At(string column) => _detail.Columns[column]!.Index;

        var (batch, partner, handedAt, receiver, contract, note, document, taken) =
            (At(BatchColumn), At(PartnerColumn), At(HandedColumn), At(ReceiverColumn),
             At(ContractColumn), At(NoteColumn), At(DocumentColumn), At(TakenColumn));

        var notStated = Lang.T("(не е посочен)", "(not stated)");
        var built = new DataGridViewRow[rows.Count];

        for (var i = 0; i < rows.Count; i++)
        {
            var share = rows[i];
            var row = (DataGridViewRow)_detail.RowTemplate.Clone();

            row.CreateCells(_detail);

            row.Cells[batch].Value = BatchText(share);

            // Долу стои онова, което ГОРЕ го няма: по код — клиентът, по
            // клиент и по получател — кодът. Повторено, то не казва нищо ново.
            row.Cells[partner].Value = ByPartner || ByReceiver
                ? Describe(share.Ewc)
                : Partner(share.Batch);

            // Заради тези двата редовете в летописа не се събират: един ред е
            // едно предаване. Извън летописа стълбовете ги няма.
            if (Chronicle)
                row.Cells[handedAt].Value = share.Moved is { } moved
                    ? moved.ToString(ValueConverter.BulgarianDateFormat)
                    : string.Empty;

            if (CanSeeReceivers)
            {
                row.Cells[receiver].Value = share.Party.Known ? share.Party.Display : notStated;
                row.Cells[contract].Value = share.Contract;
                row.Cells[note].Value = share.Note;
            }

            row.Cells[document].Value = DocumentText(share);

            row.Cells[taken].Value = SplitMath.Amount(share.Amount);
            row.Tag = share;

            built[i] = row;
        }

        _detail.Rows.AddRange(built);

        Mark(_detail, _detailSort, _detailDown);

        var sum = SplitMath.Amount(rows.Sum(s => s.Amount));

        // И долу: сборът на партидите зад избрания ред.
        _detail.Columns[TakenColumn].HeaderText =
            LedgerPage.Summed(AmountHeader(), rows.Sum(s => s.Amount));

        // В летописа редовете се броят като ПРЕДАВАНИЯ: един ред е един курс, а
        // „3 реда“ не казва това, което „3 предавания“ казва.
        var handed = Lang.T(
            $"{rows.Count} " + (rows.Count == 1 ? "предаване" : "предавания"),
            $"{rows.Count} " + (rows.Count == 1 ? "hand-over" : "hand-overs"));

        _caption.Text = ByReceiver
            ? Lang.T($"Предадено на {Receiver(key)} — {handed} · {sum} т",
                     $"Handed over to {Receiver(key)} — {handed} · {sum} t")
            : CanSeeReceivers
                ? Lang.T($"Предавания на {Describe(key)} — {handed} · {sum} т",
                         $"Hand-overs of {Describe(key)} — {handed} · {sum} t")
            : Store == StoreKind.Recycled && !ByPartner
                ? Lang.T($"Рециклирано като {Describe(key)} — {rows.Count} реда · {sum} т · " +
                         "„Върни“ го връща в „Третиран“",
                         $"Recycled as {Describe(key)} — {rows.Count} rows · {sum} t · " +
                         "“Return” sends it back to “Treated”")
            : ByPartner
                ? Lang.T($"Кодове зад {key} — {rows.Count} реда · {sum} т",
                         $"Codes behind {key} — {rows.Count} rows · {sum} t")
                : Lang.T($"Произход на {Describe(key)} — {rows.Count} партиди · {sum} т",
                         $"Origin of {Describe(key)} — {rows.Count} batches · {sum} t");
    }

    private void Describe(IReadOnlyList<StockLine> shares, DateOnly on)
    {
        var total = shares.Sum(s => s.Amount);
        var codes = shares.Select(s => s.Ewc).Distinct(StringComparer.Ordinal).Count();

        // Редът без партида не е клиент: той е липсата на такъв.
        var partners = shares
            .Where(s => !s.Batch.IsNone)
            .Select(s => s.Batch.Eik)
            .Distinct(StringComparer.Ordinal)
            .Count();

        var day = on.ToString(ValueConverter.BulgarianDateFormat);

        var rows = shares.Select(x => Key(x)).Distinct(StringComparer.Ordinal).Count();

        var loose = _loose > 0
            ? Lang.T($" · и още {SplitMath.Amount(_loose)} т без посочена партида",
                     $" · plus {SplitMath.Amount(_loose)} t with no batch named")
            : string.Empty;

        // Летописът се чете иначе: не „има толкова“, а „излязло е толкова“ —
        // и то от началото, а не към някаква наличност.
        if (shares.Count == 0 && _loose <= 0 && Filtered)
        {
            _summary.Text = Lang.T(
                "Нищо не отговаря на филтрите. „Изчисти“ ги маха наведнъж.",
                "Nothing matches the filters. “Clear” removes them all at once.");

            return;
        }

        // Ограничен до месеца, редът казва ПРЕЗ кой месец, а не ДО коя дата:
        // „до 30.09“ до число, което брои само септември, е второто, което
        // човек чете, и първото, което го подвежда.
        var month = LedgerPeriod.Of(on).Title();

        var when = MonthOnly
            ? Lang.T($"през {month}", $"during {month}")
            : Lang.T($"до {day}", $"up to {day}");

        if (shares.Count == 0 && _loose <= 0)
        {
            _summary.Text = Moves.IsStock(Store)
                ? MonthOnly
                    ? Lang.T($"Няма движение в {Moves.Title(Store).ToLowerInvariant()} през {month}.",
                             $"No movement in the {Moves.Title(Store).ToLowerInvariant()} during {month}.")
                    : Lang.T($"Няма наличност в {Moves.Title(Store).ToLowerInvariant()} към {day}.",
                             $"Nothing in the {Moves.Title(Store).ToLowerInvariant()} as of {day}.")
                : Store == StoreKind.Recycled
                    ? Lang.T($"Няма рециклирано {when}.", $"Nothing recycled {when}.")
                    : Lang.T($"Няма предадено {when}.", $"Nothing handed over {when}.");

            return;
        }

        // Двата летописа се четат еднакво — „излязло е толкова“ — но всеки със
        // своята дума. Рециклираното казва и че е извън отчета: иначе числото
        // стои като образувано, което някой е забравил да обяви.
        if (Store == StoreKind.Recycled)
        {
            _summary.Text = Lang.T(
                $"Рециклирани {SplitMath.Amount(total)} т {when} · {rows} реда · " +
                $"{codes} кода · {partners} клиента · извън отчета" + loose,
                $"{SplitMath.Amount(total)} t recycled {when} · {rows} rows · " +
                $"{codes} codes · {partners} clients · outside the report" + loose);

            return;
        }

        _summary.Text = Moves.IsStock(Store)
            ? MonthOnly
                ? Lang.T(
                    $"Движение {SplitMath.Amount(total)} т през {month} · {rows} реда · " +
                    $"{codes} кода · {partners} клиента",
                    $"{SplitMath.Amount(total)} t moved during {month} · {rows} rows · " +
                    $"{codes} codes · {partners} clients")
                : Lang.T(
                    $"Общо {SplitMath.Amount(total)} т към {day} · {rows} реда · " +
                    $"{codes} кода · {partners} клиента" + loose,
                    $"{SplitMath.Amount(total)} t in total as of {day} · {rows} rows · " +
                    $"{codes} codes · {partners} clients" + loose)
            : Lang.T(
                $"Предадени {SplitMath.Amount(total)} т {when} · {rows} реда · " +
                $"{codes} кода · {partners} клиента" + loose,
                $"{SplitMath.Amount(total)} t handed over {when} · {rows} rows · " +
                $"{codes} codes · {partners} clients" + loose);
    }

    // ------------------------------------------------------------- третиране

    /// <summary>
    /// Третира се само каквото стои в склад „Получен“.
    ///
    /// Третирането вади оттам по устройство (<see cref="Moves.Store"/>).
    /// Образуваното се предава, а не се третира, и копчето там не е сиво
    /// случайно.
    /// </summary>
    private bool CanTreat => Store == StoreKind.Received;

    /// <summary>
    /// Образува се само каквото чака в склад „Третиран“.
    ///
    /// Същата сметка отдолу (<see cref="Moves.From"/>), но обърната на другата
    /// стъпка: образуваното вади оттам и влиза в „Образуван“.
    /// </summary>
    private bool CanGenerate => Store == StoreKind.Treated;

    /// <summary>
    /// Предава се каквото стои в склад „Образуван“.
    ///
    /// Прякото предаване — онова, което излиза от „Получен“, без да е минало
    /// през третиране — е друго движение и продължава да се въвежда от „Месец“:
    /// то не тръгва от избрани редове в този склад.
    /// </summary>
    private bool CanSubmit => Store == StoreKind.Formed;

    /// <summary>
    /// Пряко се предава само от склад „Получен“.
    ///
    /// Не всичко се третира: площадка за събиране приема опаковки от стотина
    /// контрагента и ги предава почти както са дошли. Движението е същото
    /// „предаден“, само че вади от другия склад — <see cref="Moves.From"/> го
    /// знае по флага, а <see cref="Move.Direct"/> го помни.
    /// </summary>
    private bool CanSubmitDirect => Store == StoreKind.Received;

    /// <summary>
    /// Рециклираното се връща — в склад „Рециклиран“ действието е „Върни“.
    ///
    /// Рециклирането изважда от отчета нещо, което иначе би влязло в него, и
    /// сгрешеното трябва да се поправя там, където се вижда, а не през дневника.
    /// Върнатото отново чака в „Третиран“ (<see cref="Recycling.Revert"/>).
    /// „Предаден“ остава без действие: предаденото наистина си е отишло.
    /// </summary>
    private bool CanRevert => Store == StoreKind.Recycled;

    /// <summary>
    /// Прави ли се нещо с избраните редове на този склад.
    ///
    /// В „Получен“ това е третиране, в „Третиран“ — образуване (и оттам
    /// рециклиране), в „Образуван“ — предаване, в „Рециклиран“ — връщане. От
    /// „Предаден“ не се прави нищо: предаденото си е отишло.
    /// </summary>
    private bool CanPickRows => CanTreat || CanGenerate || CanSubmit || CanRevert;

    /// <summary>Как се казва действието върху един ред — то следва склада.</summary>
    private string RowAction() => Store switch
    {
        StoreKind.Treated => Lang.T("Образувай", "Generate"),
        StoreKind.Formed => Lang.T("Предай", "Hand over"),
        StoreKind.Recycled => Lang.T("Върни", "Return"),
        _ => Lang.T("Третирай", "Treat")
    };

    /// <summary>Как се казва действието на този склад.</summary>
    private string ActionTitle() => Store switch
    {
        StoreKind.Received => Lang.T("Третирай…", "Treat…"),
        StoreKind.Treated => Lang.T("Образувай…", "Generate…"),
        StoreKind.Recycled => Lang.T("Върни в „Третиран“…", "Return to “Treated”…"),
        _ => Lang.T("Предай…", "Hand over…")
    };

    /// <summary>
    /// Кое движение пълни този склад.
    ///
    /// По него се внася: складът горе вече е казал какво се гледа, а внасянето
    /// го допълва отдолу. За „Предаден“ това е самото предаване — единственото,
    /// което влиза в него.
    /// </summary>
    private static MoveKind Feeds(StoreKind store) => store switch
    {
        StoreKind.Received => MoveKind.Received,
        StoreKind.Treated => MoveKind.Treated,
        StoreKind.Formed => MoveKind.Generated,
        StoreKind.Recycled => MoveKind.Recycled,
        _ => MoveKind.Submitted
    };

    /// <summary>
    /// Внася в движението, което пълни гледания склад.
    ///
    /// Само там, където внасянето е отключено: по подразбиране това е единствено
    /// приемането. За останалите се казва ЗАЩО, вместо копчето да мълчи.
    /// </summary>
    private void Import()
    {
        if (Company() is not { } companyId) return;

        var kind = Feeds(Store);

        if (!_workspace.Periods.CanImport(companyId, kind))
        {
            MessageBox.Show(this, ImportPolicy.Why(kind),
                Lang.T("Внасянето е заключено", "Importing is locked"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);

            return;
        }

        OpenMonth(kind, SheetImport.Start);
    }

    /// <summary>
    /// Как се казва колоната със сбора.
    ///
    /// В трите склада числото е наличност — какво СТОИ. В „Предаден“ същото
    /// число значи друго: какво е излязло. Една и съща дума за двете би карала
    /// човек да чете летописа като наличност.
    /// </summary>
    private string AmountHeader() => MonthOnly && Moves.IsStock(Store)
        // Само за месеца числото е ДВИЖЕНИЕ, не наличност, и може да е
        // отрицателно. Едно и също име за двете би карало човек да чете
        // движението като наличност.
        ? Lang.T("Движение, т", "Movement, t")
        : Moves.IsStock(Store)
            ? Lang.T("Налично, т", "Available, t")
            : Store == StoreKind.Recycled
                ? Lang.T("Рециклирано, т", "Recycled, t")
                : Lang.T("Предадено, т", "Handed over, t");

    /// <summary>
    /// Гледа ли се само месецът на избраната дата.
    ///
    /// Склад „Третиран“ не може: там наличността се смята по ЛОТОВЕ — кое
    /// третиране още чака своето образуване — а лот не се реже по месеци.
    /// Затова отметката там е угасена, а не мълчаливо пренебрегната.
    /// </summary>
    private bool MonthOnly => _monthOnly.Checked && Store != StoreKind.Treated;

    /// <summary>Прави онова, което се прави на този склад.</summary>
    private void Act()
    {
        switch (Store)
        {
            case StoreKind.Received:
                TreatSelected();
                break;

            case StoreKind.Treated:
                GenerateSelected();
                break;

            case StoreKind.Formed:
                SubmitSelected();
                break;

            case StoreKind.Recycled:
                Revert(SelectedLines());
                break;

            // „Предаден“ е летопис: от него не се прави нищо, затова копчето го
            // няма. Тук се стига само ако някой го е натиснал по друг път.
            default:
                break;
        }
    }

    /// <summary>
    /// Прави с ТЕЗИ редове онова, което се прави на този склад.
    ///
    /// Едно място за копчето на реда и за менюто: те се различават само по това
    /// кои редове подават. Кой прозорец се отваря, решава складът — и това е
    /// написано веднъж.
    /// </summary>
    private void Act(IReadOnlyList<StockShare> shares)
    {
        if (CanGenerate) GenerateShares(shares);
        else if (CanSubmit) SubmitShares(shares);
        else TreatShares(shares);
    }

    /// <summary>
    /// Копчето на реда и менюто — с ТЕЗИ редове от тази таблица.
    ///
    /// В „Рециклиран“ действието иска редовете цели, с рециклирането зад всеки
    /// (<see cref="StockLine.MoveId"/>): връща се точно избраното, а не всичко
    /// по партидата. Другаде действието иска дяловете.
    /// </summary>
    private void ActOn(DataGridView grid, IReadOnlyList<DataGridViewRow> rows)
    {
        if (CanRevert)
        {
            Revert(grid == _master ? MasterLines(rows) : DetailLines(rows));
            return;
        }

        Act(grid == _master ? MasterShares(rows) : DetailShares(rows));
    }

    /// <summary>Нагажда копчетата към склада, който се гледа.</summary>
    private void Allow()
    {
        // От летописа не се прави нищо: предаденото си е отишло. Копчето се
        // скрива, вместо да стои угасено — угасено копче кара човека да търси
        // какво му липсва, за да го включи.
        // Разрезите „По получател“ и „Поотделно“ се появяват само в „Предаден“:
        // в трите склада с наличност нищо не е излязло и „на кого“ няма отговор.
        // Изборът се връща на първия, когато разрезът изчезне под него.
        var wanted = CanSeeReceivers ? 4 : 2;

        // С икони или с надписи решава отметката в „Настройки“. Чете се тук, защото
        // страницата се обновява при всяко показване: връщане в склада стига.
        var icons = _workspace.Settings.GetBool(SettingsStore.Keys.StoreViewIcons, true);

        if (_view.Items.Count != wanted || _view.ShowsIcons != icons)
        {
            var was = _view.SelectedIndex;

            _filling = true;

            try
            {
                _view.SetItems(wanted == 4
                    ?
                    [
                        Lang.T("По код", "By code"),
                        Lang.T("По клиент", "By client"),
                        Lang.T("По получател", "By receiver"),
                        Lang.T("Поотделно", "Individually")
                    ]
                    : [Lang.T("По код", "By code"), Lang.T("По клиент", "By client")]);

                _view.ShowIcons(icons ? [.. ViewPainters.Take(wanted)] : null);

                _view.SelectedIndex = was < wanted ? was : 0;

                // По надписите, а не кръгло число: както превключвателят на
                // складовете, и по същата причина. Тясно, защото редът е
                // споделен с него и на лаптоп не остава повече.
                _view.Width = icons
                    ? _view.Needed
                    : wanted == 4 ? Math.Max(Theme.Scaled(344), _view.Snug) : Theme.Scaled(200);
            }
            finally
            {
                _filling = false;
            }
        }

        // Получателят се вижда само в летописа — и ситото, и стълбът долу. В
        // трите склада с наличност нищо не е излязло: стълбът би стоял празен на
        // всеки ред, а ситото би пресявало по стойност, каквато няма.
        if (_detail.Columns[ReceiverColumn] is { } shown) shown.Visible = CanSeeReceivers;

        foreach (var name in new[] { ContractColumn, NoteColumn })
            _detail.Columns[name].Visible = CanSeeReceivers;

        foreach (var name in new[]
                 {
                     HandedColumn, BatchColumn, PartnerColumn, DocumentColumn,
                     ReceiverColumn, ContractColumn, NoteColumn
                 })
            _master.Columns[name].Visible = ByMove;

        _master.Columns[CountColumn].Visible = !ByMove;
        _master.Columns[AgeColumn].Visible = !ByMove;

        // Десет стълба в ширината, в която иначе стоят пет. Кодът е шест цифри, а
        // името му отстъпва първо: то е единственото, което кодът вече казва.
        if (_spreadForMoves != ByMove)
        {
            _spreadForMoves = ByMove;

            _master.Columns[KeyColumn].Width = Theme.Scaled(ByMove ? 66 : 140);

            // По-тесен от това и „Предадено, т“ минава на втори ред, а сборът под
            // него излиза от заглавието — точно числото, заради което е там.
            _master.Columns[AmountColumn].Width = Theme.Scaled(ByMove ? 126 : 140);

            var name = _master.Columns[NameColumn];

            name.MinimumWidth = Theme.Scaled(ByMove ? 84 : 200);
            name.FillWeight = ByMove ? 100 : 420;
        }

        // Долу в „Предаден“ стълбовете са осем в ширината на пет: числата пазят
        // мястото си, а текстът го дели — иначе количеството, заради което
        // таблицата е там, остава зад десния ръб. В другите складове е каквото е
        // било: там наличността и дългите имена на клиентите имат цялата ширина.
        if (_spreadForHanded != CanSeeReceivers)
        {
            _spreadForHanded = CanSeeReceivers;

            _detail.Columns[BatchColumn].Width = Theme.Scaled(CanSeeReceivers ? 100 : 140);
            _detail.Columns[HandedColumn].Width = Theme.Scaled(CanSeeReceivers ? 106 : 140);
            _detail.Columns[TakenColumn].Width = Theme.Scaled(CanSeeReceivers ? 126 : 140);

            var partner = _detail.Columns[PartnerColumn];

            partner.MinimumWidth = Theme.Scaled(CanSeeReceivers ? 110 : 200);
            partner.FillWeight = CanSeeReceivers ? 170 : 320;

            var document = _detail.Columns[DocumentColumn];

            if (CanSeeReceivers)
            {
                document.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                document.FillWeight = 130;
                document.MinimumWidth = Theme.Scaled(90);
            }
            else
            {
                document.AutoSizeMode = DataGridViewAutoSizeColumnMode.NotSet;
                document.MinimumWidth = 5;
                document.Width = Theme.Scaled(220);
            }
        }

        // Без долна таблица: списъкът получава цялата височина, а изборът — и
        // сборът му — е само горе.
        if (_bottom is not null) _bottom.Visible = !ByMove;

        if (_body is not null)
            _body.AutoScrollMinSize = new Size(0, _sumsHeight + Theme.Scaled(ByMove ? 220 : 220 + 140));

        if (ByMove) _active = _master;

        // Датата стои и в двата летописа: „кога излезе“ и „кога е рециклирано“
        // различават редовете, които иначе си приличат.
        if (_detail.Columns[HandedColumn] is { } handed)
        {
            handed.Visible = Chronicle;
            handed.HeaderText = Store == StoreKind.Recycled
                ? Lang.T("Рециклирано на", "Recycled on")
                : Lang.T("Предадено на", "Handed over");
        }

        // В „Третиран“ месецът не се реже: наличността там се смята по лотове.
        _monthOnly.Enabled = Store != StoreKind.Treated;

        // Внасянето се вижда само там, където е отключено. По подразбиране това
        // е само приемането — останалите три се раждат в програмата.
        if (_import is not null)
            _import.Visible = Company() is { } who && _workspace.Periods.CanImport(who, Feeds(Store));

        if (_receiverHost is not null) _receiverHost.Visible = CanSeeReceivers;
        if (_contractHost is not null) _contractHost.Visible = CanSeeReceivers;
        if (_noteHost is not null) _noteHost.Visible = CanSeeReceivers;

        // С договора и бележката ситата в „Предаден“ стават пет и на цялата ширина
        // на съдържанието не биха се събрали на един ред. Само там: другите
        // складове имат две и пазят мястото си за по-дългите надписи.
        foreach (var cell in new[] { _codeHost, _partnerHost, _receiverHost, _contractHost, _noteHost })
            if (cell is not null) cell.Width = Theme.Scaled(CanSeeReceivers ? 160 : 244);

        // Скрито сито не бива да пресява: сменил склада, човек не вижда защо
        // таблицата е празна.
        if (!CanSeeReceivers)
        {
            _receiverFilter = null;
            _contractFilter = null;
            _noteFilter = null;
        }

        // В „Рециклиран“ копчето има работа — то връща сгрешеното.
        _actionShown = Moves.IsStock(Store) || CanRevert;

        if (_action is not null)
        {
            _action.Visible = _actionShown;
            _action.Text = ActionTitle();
            _action.FitWidth();
        }

        // Второто копче се вижда само там, където има какво да прави. Скрито, а
        // не угасено: угасено копче кара човека да търси какво му липсва, за да
        // го включи.
        if (_handOver is not null) _handOver.Visible = CanSubmitDirect;

        // Копчето на реда следва склада и по надпис, и по видимост: то прави
        // онова, което на този склад изобщо се прави с един ред.
        foreach (var grid in new[] { _master, _detail })
            if (grid.Columns[TreatColumn] is DataGridViewButtonColumn column)
            {
                column.Visible = CanPickRows;
                column.Text = RowAction();
                column.ToolTipText = RowTip(grid == _master);
            }
    }

    /// <summary>Иконите на изгледите — в реда на надписите им.</summary>
    private static readonly IReadOnlyList<Action<Graphics, Rectangle, Color>> ViewPainters =
    [
        ViewIcons.Code, ViewIcons.Client, ViewIcons.Receiver, ViewIcons.Each
    ];

    /// <summary>Какво казва подсказката на копчето — действието и обхватът му.</summary>
    private string RowTip(bool whole) => (Store, whole) switch
    {
        (StoreKind.Treated, true) => Lang.T("Образува от целия ред — всички партиди зад него.",
                                            "Generates from the whole row — every batch behind it."),
        (StoreKind.Treated, false) => Lang.T("Образува само от тази партида.",
                                             "Generates from this batch alone."),
        (StoreKind.Formed, true) => Lang.T("Предава целия ред — всички партиди зад него.",
                                           "Hands over the whole row — every batch behind it."),
        (StoreKind.Formed, false) => Lang.T("Предава само от тази партида.",
                                            "Hands over from this batch alone."),
        (StoreKind.Recycled, true) => Lang.T("Връща в „Третиран“ всичко рециклирано по този ред.",
                                             "Returns everything recycled on this row to “Treated”."),
        (StoreKind.Recycled, false) => Lang.T("Връща в „Третиран“ само това рециклиране на партидата.",
                                              "Returns only this recycling of the batch to “Treated”."),
        (_, true) => Lang.T("Третира целия ред — всички партиди зад него.",
                            "Treats the whole row — every batch behind it."),
        _ => Lang.T("Третира само тази партида.", "Treats this batch alone.")
    };

    /// <summary>
    /// Сборът на избраното.
    ///
    /// Отделно число от сбора на видимото, защото отговаря на друг въпрос: не
    /// „колко има“, а „колко ще третирам сега“. То стои до копчето и се мени със
    /// всяко щракване, за да не се събира наум.
    /// </summary>
    private void Weigh()
    {
        var chosen = Selection();

        if (chosen.Count == 0)
        {
            _chosen.Text = Lang.T("Нищо не е избрано.", "Nothing is selected.");
            _chosen.ForeColor = Theme.InkFaint;
            return;
        }

        var codes = chosen.Select(s => s.Ewc).Distinct(StringComparer.Ordinal).Count();

        _chosen.Text = Lang.T(
            $"Избрано: {SplitMath.Amount(chosen.Sum(s => s.Amount))} т · " +
            $"{codes} кода · {chosen.Count} дяла",
            $"Selected: {SplitMath.Amount(chosen.Sum(s => s.Amount))} t · " +
            $"{codes} codes · {chosen.Count} shares");

        _chosen.ForeColor = Theme.Ink;
    }

    /// <summary>Копчето в лентата: пита таблицата, която е пипана последна.</summary>
    private void TreatSelected() => TreatShares(SelectedShares());

    /// <summary>Същото копче, когато се гледа склад „Третиран“.</summary>
    private void GenerateSelected() => GenerateShares(SelectedShares());

    /// <summary>
    /// Летопис ли е гледаният склад — „Предаден“ или „Рециклиран“.
    ///
    /// И в двата един ред е ЕДНО излизане, с датата си, и отгоре стои
    /// най-новото. Разликата е само в получателя — той е на предаването.
    /// </summary>
    private bool Chronicle => !Moves.IsStock(Store);

    /// <summary>Редовете зад избраното — от таблицата, която е пипана последна.</summary>
    private IReadOnlyList<StockLine> SelectedLines()
    {
        var grid = _active ?? _master;

        return grid == _detail ? DetailLines(Chosen(_detail)) : MasterLines(Chosen(_master));
    }

    /// <summary>Редовете на склада зад избраните горе — цели, с движението зад всеки.</summary>
    private IReadOnlyList<StockLine> MasterLines(IReadOnlyList<DataGridViewRow> rows)
    {
        var keys = rows.Select(r => r.Tag as string).OfType<string>().ToHashSet(StringComparer.Ordinal);

        return keys.Count == 0 ? [] : [.. Kept().Where(s => keys.Contains(Key(s)))];
    }

    /// <summary>Избраните долу редове — самите редове на склада.</summary>
    private static IReadOnlyList<StockLine> DetailLines(IReadOnlyList<DataGridViewRow> rows) =>
        [.. rows.Select(r => r.Tag).OfType<StockLine>()];

    /// <summary>
    /// Връща избраните редове от „Рециклиран“ обратно в „Третиран“.
    ///
    /// Пита преди това и казва колко се връща: рециклирането мени отчета, а
    /// връщането — пак. Затвореният месец не се пипа, както и при поправката
    /// от дневника.
    ///
    /// Движение, от което се връщат всички дялове, се маха; иначе олеква с
    /// върнатото. Дневникът го казва сам — махнатото стои там като махнато.
    /// </summary>
    internal int Revert(IReadOnlyList<StockLine> lines, bool ask = true)
    {
        if (Company() is not { } companyId) return 0;

        var stage = Stage();
        var moves = _workspace.Ledger.Read(companyId, stage);
        var steps = Recycling.Revert(lines, moves, _workspace.Ledger.Links(companyId, stage));

        if (steps.Count == 0)
        {
            if (ask)
                MessageBox.Show(this,
                    Lang.T("Изберете редове от склад „Рециклиран“, които да се върнат.",
                           "Pick rows of the “Recycled” store to return."),
                    Lang.T("Няма избрано", "Nothing is selected"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);

            return 0;
        }

        foreach (var month in steps.Select(s => s.Move.Period).Distinct())
        {
            if (!_workspace.Periods.IsClosed(companyId, stage, month)) continue;

            if (ask)
                MessageBox.Show(this,
                    Lang.T($"{month.Title()} е затворен. Отворете месеца от „Настройки“, ако рециклирането " +
                           "наистина трябва да се върне.",
                           $"{month.Title()} is closed. Reopen it from “Settings” if the recycling really " +
                           "has to be returned."),
                    Lang.T("Затворен месец", "Closed month"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);

            return 0;
        }

        var back = SplitMath.Round(steps.Sum(s => s.Returned));

        if (ask && MessageBox.Show(this,
                Lang.T($"Да се върнат ли {SplitMath.Amount(back)} т в склад „Третиран“?\n\n" +
                       "Рециклирането им се маха и количеството отново чака там — за да бъде " +
                       "образувано или рециклирано наново.",
                       $"Return {SplitMath.Amount(back)} t to the “Treated” store?\n\n" +
                       "Their recycling is removed and the quantity waits there again — to be " +
                       "generated or recycled anew."),
                Lang.T("Връщане от рециклиране", "Returning from recycling"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return 0;

        foreach (var step in steps)
        {
            var move = step.Move;

            if (step.Gone)
            {
                _workspace.Ledger.Remove(move.Id);
                continue;
            }

            _workspace.Ledger.Update(move.Id, new MoveDraft(
                move.Kind, move.Date, move.Ewc, step.Amount, move.Stagenum,
                Note: _workspace.Ledger.Note(move.Id),
                Fields: _workspace.Ledger.Fields(move.Id),
                Draws: step.Keep));
        }

        Refresh();
        LedgerChanged?.Invoke(this, EventArgs.Empty);

        _summary.Text = Lang.T($"Върнати са {SplitMath.Amount(back)} т в „Третиран“. ",
                               $"{SplitMath.Amount(back)} t returned to “Treated”. ") + _summary.Text;

        return steps.Count;
    }

    /// <summary>Същото копче, когато се гледа склад „Образуван“.</summary>
    private void SubmitSelected() => SubmitShares(SelectedShares());

    /// <summary>„Предай направо…“ — от „Получен“, без третиране.</summary>
    private void SubmitDirectSelected() => SubmitShares(SelectedShares(), direct: true);

    /// <summary>
    /// Дяловете зад избраното — от таблицата, която е пипана последна.
    ///
    /// Едно място за двете действия: горе редът е код и се разгъва до партидите
    /// си, долу редът Е дялът. Разликата между третиране и образуване е какво
    /// става после, а не кое е избрано.
    /// </summary>
    private IReadOnlyList<StockShare> SelectedShares()
    {
        var grid = _active ?? _master;

        return grid == _detail
            ? DetailShares(Chosen(_detail))
            : MasterShares(Chosen(_master));
    }

    /// <summary>Редовете, които човек е избрал — или този, на който стои.</summary>
    private static IReadOnlyList<DataGridViewRow> Chosen(DataGridView grid)
    {
        var rows = grid.SelectedRows.Cast<DataGridViewRow>().OrderBy(r => r.Index).ToList();

        if (rows.Count > 0) return rows;

        return grid.CurrentRow is { } current ? [current] : [];
    }

    /// <summary>
    /// Какво стои зад избраните горе редове.
    ///
    /// Горният ред е код (или клиент), а третира се дял — затова редът се
    /// разгъва до партидите си. Пресяването важи и тук: избраното е това, което
    /// се вижда, а не всичко, което съвпада по код.
    /// </summary>
    private IReadOnlyList<StockShare> MasterShares(IReadOnlyList<DataGridViewRow> rows)
    {
        var keys = rows.Select(r => r.Tag as string)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        if (keys.Count == 0) return [];

        return
        [
            // Прозорчетата на действието искат ДЯЛОВЕ: получателят е въпрос на
            // летописа, а от него не се третира и не се предава.
            .. Kept()
                .Where(s => keys.Contains(Key(s)))
                .OrderBy(s => s.Ewc, StringComparer.Ordinal)
                .ThenBy(s => s.Batch.ReceivedOn)
                .ThenBy(s => s.Batch.Id)
                .Select(s => s.Share)
        ];
    }

    /// <summary>Избраните долу редове са самите дялове.</summary>
    private static IReadOnlyList<StockShare> DetailShares(IReadOnlyList<DataGridViewRow> rows) =>
    [
        // Редът долу носи ЛИНИЯ (с получателя си); действието иска дял.
        .. rows.Select(r => r.Tag).OfType<StockLine>().Select(l => l.Share)
    ];

    /// <summary>
    /// Отваря прозорчето и записва каквото то е върнало.
    ///
    /// Прозорчето само сглобява движенията; записът минава през
    /// <see cref="LedgerWriter"/>, както всяко друго движение — правилата „какво
    /// става при третиране“ живеят там и на едно място.
    /// </summary>
    private void TreatShares(IReadOnlyList<StockShare> shares)
    {
        if (Company() is not { } companyId) return;

        if (!CanTreat)
        {
            MessageBox.Show(this,
                Lang.T("От склад „Образуван“ се предава, а не се третира. " +
                       "Изберете склад „Получен“, за да третирате.",
                       "The “Formed” store is handed over, not treated. " +
                       "Switch to the “Received” store to treat."),
                Lang.T("Друг склад", "Another store"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);

            return;
        }

        if (shares.Count == 0)
        {
            MessageBox.Show(this,
                Lang.T("Изберете редове, които да се третират.",
                       "Pick the rows to be treated."),
                Lang.T("Няма избрано", "Nothing is selected"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);

            return;
        }

        var stage = Stage();

        using var dialog = new TreatDialog(
            _service, _workspace, companyId, stage, shares, DateOnly.FromDateTime(_date.Value));

        var written = 0;

        // „Третирай и продължи“ записва, без да затваря — а записва пак оттук,
        // за да остане правилото на едно място.
        dialog.Write = (moves, date, fields, note) =>
        {
            var made = Apply(moves, date, fields, note, dialog.Window);
            written += made;

            return made;
        };

        var closed = dialog.ShowDialog(this);

        if (closed == DialogResult.OK && dialog.Result.Count > 0)
            written += Apply(dialog.Result, dialog.Date, dialog.Fields, dialog.Note, dialog.Window);

        if (written == 0) return;

        Refresh();
        LedgerChanged?.Invoke(this, EventArgs.Empty);

        var said = written == 1
            ? Lang.T("Записано е 1 третиране.", "1 treatment saved.")
            : Lang.T($"Записани са {written} третирания.", $"{written} treatments saved.");

        _summary.Text = said + " " + _summary.Text;
    }

    /// <summary>
    /// Записва сглобените третирания и връща колко са.
    ///
    /// Отделено от прозорчето, за да може да се провери с тест: прозорче не се
    /// показва на тестова нишка (правило 4).
    /// </summary>
    internal int Apply(
        IReadOnlyList<TreatMove> moves,
        DateOnly date,
        IReadOnlyDictionary<string, string>? fields = null,
        string note = "",
        ActionWindow? window = null)
    {
        if (Company() is not { } companyId) return 0;

        var stage = Stage();
        var written = 0;

        // Един белег за всичко, записано наведнъж: поправката отваря действието
        // цяло, а не ред по ред.
        var action = Mark();

        foreach (var move in moves)
        {
            var id = _workspace.Writer.Write(companyId, new MoveRequest(
                MoveKind.Treated,
                date,
                move.Ewc,
                move.Amount,
                stage,
                Note: note,
                Fields: fields,
                Draws: move.Draws,
                EwcOut: move.EwcOut));

            Remember(companyId, stage, MoveKind.Treated, id, date, move.Ewc, move.Amount, note, action);

            written++;
        }

        if (window is not null) _workspace.Actions.Keep(action, window);

        return written;
    }

    /// <summary>
    /// Отваря прозорчето за образуване и записва каквото то е върнало.
    ///
    /// Устроено като третирането и нарочно: двете са една и съща работа на две
    /// съседни стъпки. Разликата е вътре в прозорчето — от една купчина излизат
    /// няколко кода — а тук тя не се вижда.
    ///
    /// <paramref name="kind"/> е краят: образуван отпадък (в отчета и в
    /// „Образуван“) или рециклиран (извън отчета, в „Рециклиран“). Пътят дотам
    /// е един — същият прозорец, същото записване.
    /// </summary>
    private void GenerateShares(IReadOnlyList<StockShare> shares)
    {
        if (Company() is not { } companyId) return;

        if (!CanGenerate)
        {
            MessageBox.Show(this,
                Lang.T("Образува се от склад „Третиран“. Изберете него, за да обявите " +
                       "какво е излязло от третираното.",
                       "Generation starts from the “Treated” store. Switch to it to state " +
                       "what came out of the treated waste."),
                Lang.T("Друг склад", "Another store"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);

            return;
        }

        if (shares.Count == 0)
        {
            MessageBox.Show(this,
                Lang.T("Изберете редове, от които да се образува.", "Pick the rows to generate from."),
                Lang.T("Няма избрано", "Nothing is selected"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);

            return;
        }

        var written = 0;
        var recycled = 0;

        using var dialog = FormDialog(shares,
            made => written += made,
            made => recycled += made);

        var closed = dialog.ShowDialog(this);

        if (closed == DialogResult.OK && dialog.Result.Count > 0)
            written += ApplyGenerated(dialog.Result, dialog.Date, dialog.Fields, dialog.Note, dialog.Window);

        if (written == 0 && recycled == 0) return;

        Refresh();
        LedgerChanged?.Invoke(this, EventArgs.Empty);

        var said = (written, recycled) switch
        {
            (> 0, > 0) => Lang.T($"Записани са {written} образувания и {recycled} рециклирания.",
                                 $"{written} generations and {recycled} recyclings saved."),
            (0, 1) => Lang.T("Записано е 1 рециклиране.", "1 recycling saved."),
            (0, _) => Lang.T($"Записани са {recycled} рециклирания.", $"{recycled} recyclings saved."),
            (1, _) => Lang.T("Записано е 1 образуване.", "1 generation saved."),
            _ => Lang.T($"Записани са {written} образувания.", $"{written} generations saved.")
        };

        _summary.Text = said + " " + _summary.Text;
    }

    /// <summary>
    /// Прозорецът на образуването с двата му изхода, вързани за записа.
    ///
    /// Образуваното и рециклираното се записват оттук, от страницата — правилото
    /// как действие става движение живее на едно място. Отделно от показването,
    /// за да може тестът да мине по същия път, без прозорецът да се показва.
    /// </summary>
    internal GenerateDialog FormDialog(
        IReadOnlyList<StockShare> shares, Action<int>? formed = null, Action<int>? recycled = null)
    {
        var dialog = new GenerateDialog(
            _service, _workspace, Company() ?? 0, Stage(), shares, DateOnly.FromDateTime(_date.Value));

        dialog.Write = (moves, date, fields, note) =>
        {
            var made = ApplyGenerated(moves, date, fields, note, dialog.Window);
            formed?.Invoke(made);

            return made;
        };

        dialog.WriteRecycled = (moves, date, fields, note) =>
        {
            var made = ApplyRecycled(moves, date, fields, note, dialog.Window);
            recycled?.Invoke(made);

            return made;
        };

        return dialog;
    }

    /// <summary>
    /// Записва сглобеното образуване и връща колко реда са станали.
    ///
    /// „Описание на отпадъка“ се попълва тук, а не в прозорчето: то е името на
    /// кода по ЕКО и се различава от ред до ред, а схемата го иска задължително.
    /// Написаното от човека — ако утре има такова поле — не се пипа.
    /// </summary>
    internal int ApplyGenerated(
        IReadOnlyList<GenMove> moves,
        DateOnly date,
        IReadOnlyDictionary<string, string>? fields = null,
        string note = "",
        ActionWindow? window = null) =>
        ApplyMade(moves, date, fields, note, MoveKind.Generated, window);

    /// <summary>
    /// Записва сглобеното рециклиране и връща колко реда са станали.
    ///
    /// Същото записване като на образуваното — същите дялове по партиди, същият
    /// белег в дневника, — само че движението е „рециклиран“ и затова изчерпва
    /// „Третиран“, без да влиза в отчета.
    /// </summary>
    internal int ApplyRecycled(
        IReadOnlyList<GenMove> moves,
        DateOnly date,
        IReadOnlyDictionary<string, string>? fields = null,
        string note = "",
        ActionWindow? window = null) =>
        ApplyMade(moves, date, fields, note, MoveKind.Recycled, window);

    private int ApplyMade(
        IReadOnlyList<GenMove> moves,
        DateOnly date,
        IReadOnlyDictionary<string, string>? fields,
        string note,
        MoveKind kind,
        ActionWindow? window = null)
    {
        if (Company() is not { } companyId) return 0;

        var stage = Stage();
        var written = 0;

        // Един белег за всичко, записано наведнъж: поправката отваря действието
        // цяло, а не ред по ред.
        var action = Mark();

        foreach (var move in moves)
        {
            var row = new Dictionary<string, string>(fields ?? new Dictionary<string, string>(),
                StringComparer.Ordinal);

            // Описанието е колона на книгата — рециклираното в книгата не отива.
            if (kind == MoveKind.Generated &&
                (!row.TryGetValue("waste_desc", out var described) || described.Trim().Length == 0))
                row["waste_desc"] = _names.GetValueOrDefault(move.Ewc, move.Ewc);

            var id = _workspace.Writer.Write(companyId, new MoveRequest(
                kind,
                date,
                move.Ewc,
                move.Amount,
                stage,
                Note: note,
                Fields: row,
                Draws: move.Draws));

            Remember(companyId, stage, kind, id, date, move.Ewc, move.Amount, note, action);

            written++;
        }

        if (window is not null) _workspace.Actions.Keep(action, window);

        return written;
    }

    /// <summary>
    /// Отваря прозорчето за предаване и записва каквото то е върнало.
    ///
    /// Устроено като третирането и образуването, и пак нарочно: трите са една и
    /// съща работа на три съседни стъпки. Разликата е вътре в прозорчето —
    /// предаването тръгва от кантара, а не от склада — а тук тя не се вижда.
    /// </summary>
    private void SubmitShares(IReadOnlyList<StockShare> shares, bool direct = false)
    {
        if (Company() is not { } companyId) return;

        if (direct ? !CanSubmitDirect : !CanSubmit)
        {
            MessageBox.Show(this,
                direct
                    ? Lang.T("Пряко се предава от склад „Получен“ — каквото си отива, както е " +
                             "дошло. Изберете него.",
                             "A direct hand-over starts from the “Received” store — what leaves " +
                             "as it came. Switch to it.")
                    : Lang.T("Предава се от склад „Образуван“. Изберете него, за да обявите " +
                             "какво е излязло от площадката.",
                             "Hand-over starts from the “Formed” store. Switch to it to state " +
                             "what left the site."),
                Lang.T("Друг склад", "Another store"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);

            return;
        }

        if (shares.Count == 0)
        {
            MessageBox.Show(this,
                Lang.T("Изберете редове, които да се предадат.",
                       "Pick the rows to be handed over."),
                Lang.T("Няма избрано", "Nothing is selected"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);

            return;
        }

        var stage = Stage();

        using var dialog = new SubmitDialog(
            _service, _workspace, companyId, stage, shares,
            DateOnly.FromDateTime(_date.Value), direct);

        var written = 0;

        dialog.Write = (moves, date, fields, note) =>
        {
            var made = ApplySubmitted(moves, date, fields, note, direct, dialog.Contract, dialog.Window);
            written += made;

            return made;
        };

        var closed = dialog.ShowDialog(this);

        if (closed == DialogResult.OK && dialog.Result.Count > 0)
            written += ApplySubmitted(
                dialog.Result, dialog.Date, dialog.Fields, dialog.Note, direct, dialog.Contract, dialog.Window);

        if (written == 0) return;

        Refresh();
        LedgerChanged?.Invoke(this, EventArgs.Empty);

        var said = written == 1
            ? Lang.T("Записано е 1 предаване.", "1 hand-over saved.")
            : Lang.T($"Записани са {written} предавания.", $"{written} hand-overs saved.");

        _summary.Text = said + " " + _summary.Text;
    }

    /// <summary>
    /// Записва сглобеното предаване и връща колко реда са станали.
    ///
    /// Дяловете се подават изрично и точно това е смисълът на пътя оттук.
    /// Записът сам би ги сметнал по FIFO, но той търси партида по кода на
    /// ДВИЖЕНИЕТО, а партидата помни кода, с който отпадъкът е ДОШЪЛ; при
    /// предаване на образуван отпадък двата не съвпадат никога и движението
    /// оставаше без партида. Оттук то си я носи и веригата на произхода стига
    /// до последната стъпка.
    ///
    /// <paramref name="direct"/> казва от кой склад си отива количеството.
    /// Флагът пътува до самото движение и там остава: наличността се смята от
    /// него (<see cref="Moves.Sign"/>), а не от това кой екран е записал реда.
    /// </summary>
    internal int ApplySubmitted(
        IReadOnlyList<HandMove> moves,
        DateOnly date,
        IReadOnlyDictionary<string, string>? fields = null,
        string note = "",
        bool direct = false,
        string contract = "",
        ActionWindow? window = null)
    {
        if (Company() is not { } companyId) return 0;

        var stage = Stage();
        var written = 0;

        // Един белег за всичко, записано наведнъж: поправката отваря действието
        // цяло, а не ред по ред.
        var action = Mark();

        // Получателят се научава при ЗАПИСА, а не докато се пише в полето:
        // недовършено име, изтрито след миг, не бива да остане в списъка
        // завинаги. Записано предаване обаче е решение.
        //
        // Тук, а не в прозорчето, защото проверката „какво спира записа“ трябва
        // да остане само въпрос: заявка, която пише в базата, рано или късно се
        // вика от място, което не е искало да пише.
        if (fields is not null)
            _workspace.Partners.Remember(
                companyId,
                fields.GetValueOrDefault("name_receiver", string.Empty),
                fields.GetValueOrDefault("pin_receiver", string.Empty));

        foreach (var move in moves)
        {
            var id = _workspace.Writer.Write(companyId, new MoveRequest(
                MoveKind.Submitted,
                date,
                move.Ewc,
                move.Amount,
                stage,
                Note: note,
                Direct: direct,
                Fields: fields,
                Draws: move.Draws,
                EwcOut: move.EwcOut,
                Contract: contract));

            Remember(companyId, stage, MoveKind.Submitted, id, date, move.Ewc, move.Amount, note, action);

            written++;
        }

        if (window is not null) _workspace.Actions.Keep(action, window);

        return written;
    }

    /// <summary>
    /// Отваря дневника на въведеното.
    ///
    /// Върне ли месец, страницата го подава нататък: намирането свършва там,
    /// където се поправя — в „Месец“, при самия ред.
    /// </summary>
    private void OpenLog()
    {
        var companyId = Company();

        using var dialog = new ActionLogDialog(_workspace, companyId, Stage());

        // Поправката става в прозореца, от който действието е дошло.
        dialog.Amend = AmendEntry;

        dialog.ShowDialog(this);

        ShowLog();

        if (dialog.Changed)
        {
            Refresh();
            LedgerChanged?.Invoke(this, EventArgs.Empty);
        }

        if (dialog.Chosen is not { } entry) return;

        // Рециклирането няма таблица на месеца — тя е раздел от книгата, а то
        // в книгата няма раздел. Намирането свършва там, където то стои: в
        // склад „Рециклиран“, само за този месец.
        if (entry.Kind == MoveKind.Recycled) ShowRecycled(entry.Period);
        else OpenMonth(entry.Kind, entry.Period);
    }

    /// <summary>Показва рециклираното през един месец — там, където то стои.</summary>
    private void ShowRecycled(LedgerPeriod period)
    {
        _filling = true;

        try
        {
            _store.SelectedIndex = Moves.Stores.ToList().IndexOf(StoreKind.Recycled);
            _date.Value = period.Last.ToDateTime(TimeOnly.MinValue);
            _monthOnly.Checked = true;
        }
        finally
        {
            _filling = false;
        }

        Rebuild();
    }

    // ------------------------------------------------------------- поправка

    /// <summary>
    /// Сглобява поправката на едно записано действие — или казва защо не става.
    ///
    /// Действието е всичко, записано наведнъж (едно третиране по три кода са
    /// три реда в дневника и една грешка). Складът за прозореца се смята БЕЗ
    /// него: неговото количество е „върнато“, докато се поправя, и така може
    /// да бъде преместено на друга дата.
    ///
    /// Прозорецът се възстановява, а не само записаното в него: каквото е
    /// предлагал тогава, а за действие отпреди да се пази — цялата купчина на
    /// кодовете му. Точно това позволява грешно взета партида да се смени с вярната:
    /// показвана само взетата, вярната просто я нямаше.
    /// </summary>
    internal AmendSeed? Prepare(ActionEntry entry, out IReadOnlyList<StockShare> shares, out string why)
    {
        shares = [];
        why = string.Empty;

        if (entry.CompanyId is not { } companyId) return null;

        if (entry.Kind == MoveKind.Received)
        {
            why = Lang.T("Приемането се поправя в „Месец“ — то не идва от тези прозорци.",
                         "A receipt is corrected in “Month” — it does not come from these windows.");
            return null;
        }

        var stage = entry.Stagenum;
        var group = _workspace.Actions.Group(entry);
        var ids = group.Select(e => e.MoveId).ToHashSet();

        var moves = _workspace.Ledger.Read(companyId, stage);
        var links = _workspace.Ledger.Links(companyId, stage);
        var batches = _workspace.Batches.List(companyId, stage);

        var old = moves.Where(m => ids.Contains(m.Id)).OrderBy(m => m.Id).ToList();

        if (old.Count == 0)
        {
            why = Lang.T("Движението вече го няма — няма какво да се поправи.",
                         "The movement is gone — there is nothing to correct.");
            return null;
        }

        foreach (var period in old.Select(m => LedgerPeriod.Of(m.Date)).Distinct())
        {
            if (!_workspace.Periods.IsClosed(companyId, stage, period)) continue;

            why = Lang.T($"{period.Title()} е затворен. Отворете месеца от „Настройки“, ако наистина " +
                         "трябва да се поправи.",
                         $"{period.Title()} is closed. Reopen it from “Settings” if it really has to be corrected.");
            return null;
        }

        var gone = old.Select(m => m.Id).ToHashSet();
        var mine = links.Where(l => gone.Contains(l.MoveId)).ToList();

        var fields = new Dictionary<string, string>(_workspace.Ledger.Fields(old[0].Id), StringComparer.Ordinal);

        // Описанието при образуване е името на изходния код — по ред. Взето
        // от първия, то би легнало върху всички.
        if (entry.Kind is MoveKind.Generated or MoveKind.Recycled) fields.Remove("waste_desc");

        var (kept, keptLinks) = Amend.Without(moves, links, gone);

        var day = old[0].Date;
        var made = entry.Kind is MoveKind.Generated or MoveKind.Recycled;

        // Каквото е стояло в склада на тази дата, без поправяното. Рециклираното
        // черпи от същото място като образуваното.
        var stood = made
            ? StockMath.Waiting(batches, kept, keptLinks, day).Shares
            : StockMath.Shares(batches, kept, keptLinks, Moves.From(entry.Kind, old[0].Direct) ?? StoreKind.Formed, day);

        var batchIds = mine.Select(l => l.BatchId).ToHashSet();

        var own = Amend.Own(
            batches, old, mine, Moves.From(entry.Kind, old[0].Direct) ?? StoreKind.Formed);

        // Кодовете на действието. При образуване движението носи кода, в който Е
        // ИЗЛЯЗЛО — откъде е взето, казват партидите.
        var codes = (made
                ? stood.Where(s => batchIds.Contains(s.Batch.Id)).Select(s => s.Ewc)
                : old.Select(m => m.Ewc))
            .Select(ValueConverter.NormalizeCode)
            .ToHashSet(StringComparer.Ordinal);

        var window = _workspace.Actions.Window(entry.Action);

        var scope = (window is { Scope.Count: > 0 } known
                ? known.Scope.Select(s => (s.Ewc, s.Batch))
                : stood.Where(s => codes.Contains(ValueConverter.NormalizeCode(s.Ewc)))
                    .Select(s => (ValueConverter.NormalizeCode(s.Ewc), s.Batch.Id)))
            .ToHashSet();

        // Взетото стои в обхвата винаги, каквото и да е запомнено за него.
        scope.UnionWith(own.Select(s => (ValueConverter.NormalizeCode(s.Ewc), s.Batch.Id)));

        // „Отделен ред“ не е в движенията, но личи от тях: два реда с един код не
        // идват по друг начин. Иначе поправката тихо ги събира в един.
        var split = window?.Split ?? old
            .GroupBy(m => ValueConverter.NormalizeCode(m.Ewc), StringComparer.Ordinal)
            .Any(g => g.Count() > 1);

        var seed = new AmendSeed(
            group, old, mine, fields, _workspace.Ledger.Note(old[0].Id), _workspace.Ledger.Contract(old[0].Id),
            scope, window?.Target ?? 0m, split, Undo.Takers(moves, links, gone), Own: own);

        var offered = stood.Where(s => scope.Contains((ValueConverter.NormalizeCode(s.Ewc), s.Batch.Id))).ToList();

        shares = seed.Capped(offered, kept, keptLinks, day);

        seed = seed with { Held = Amend.Held(offered, shares) };

        if (shares.Count == 0)
        {
            why = Lang.T("Партидите зад действието не се намират в склада.",
                         "The batches behind the action are not found in the store.");
            return null;
        }

        return seed;
    }

    /// <summary>
    /// Какво поправката би развалила СЛЕД новата си дата.
    ///
    /// Самият прозорец пази новата дата; тук се пита за останалото — движение,
    /// което е черпило от старото (<see cref="Amend.Breaks"/>).
    /// </summary>
    internal IReadOnlyList<string> Check(AmendSeed seed, IReadOnlyList<Planned> made)
    {
        if (seed.Entries[0].CompanyId is not { } companyId) return [];

        var stage = seed.Entries[0].Stagenum;

        var said = Amend.Breaks(
                _workspace.Batches.List(companyId, stage),
                _workspace.Ledger.Read(companyId, stage),
                _workspace.Ledger.Links(companyId, stage),
                seed.Gone,
                made)
            .Select(i => i.Text)
            .ToList();

        // Прозорецът показва осем реда. Третиране от седемдесет партиди дава седемдесет,
        // а отрязаното без дума изглежда като всичко, което има.
        return said.Count <= ShownBreaks
            ? said
            :
            [
                .. said.Take(ShownBreaks - 1),
                Lang.T($"…и още {said.Count - ShownBreaks + 1}.", $"…and {said.Count - ShownBreaks + 1} more.")
            ];
    }

    /// <summary>Колко реда побира съобщението за развалено по-късно движение.</summary>
    private const int ShownBreaks = 8;

    /// <summary>
    /// Заменя старото действие с новото — цялото или никак.
    ///
    /// Проверено е преди това — от прозореца и от <see cref="Check"/> — затова
    /// тук само се маха и записва. Дотук махането и записът бяха две отделни неща:
    /// прекъснат по средата, записът оставяше нито старото, нито новото.
    ///
    /// Поправеното си остава същото действие: същият белег, същото време и място в
    /// дневника, с белег кога е поправяно и какво е било. Дневникът казва къде е
    /// паднало СЕГА, но не забравя, че го е имало.
    /// </summary>
    internal int Replace(AmendSeed seed, Func<int> write)
    {
        var written = 0;
        var window = _workspace.Actions.Window(seed.Action);

        _amending = seed;

        try
        {
            _workspace.Atomically(() =>
            {
                _workspace.Ledger.Remove(seed.Moves.Select(m => m.Id));

                foreach (var entry in seed.Entries) _workspace.Actions.Forget(entry.Id);

                _workspace.Ledger.Reuse(seed.Moves.Select(m => m.Id));

                written = write();

                // Махнато и незаменено е загубен запис, а не поправка: връща се всичко.
                if (written == 0)
                    throw new InvalidOperationException(Lang.T(
                        "Поправката не записа нищо — старото действие е оставено непокътнато.",
                        "The correction wrote nothing — the old action is left untouched."));

                // Прозорецът на поправката заменя стария. Ако поправката не носи
                // свой, старият остава: той пак казва истината за обхвата.
                if (window is not null && _workspace.Actions.Window(seed.Action) is null)
                    _workspace.Actions.Keep(seed.Action, window);
            });
        }
        finally
        {
            _amending = null;
        }

        return written;
    }

    /// <summary>Белегът на записваното: новото получава свой, поправката пази стария.</summary>
    private string Mark() =>
        _amending is { Action.Length: > 0 } seed ? seed.Action : Guid.NewGuid().ToString("N");

    /// <summary>
    /// Отваря записано действие в прозореца, от който е дошло, и записва
    /// поправката. Връща дали нещо е записано.
    /// </summary>
    private bool AmendEntry(IWin32Window owner, ActionEntry entry)
    {
        if (Prepare(entry, out var shares, out var why) is not { } seed)
        {
            MessageBox.Show(owner, why, Lang.T("Не може да се поправи", "Cannot be corrected"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);

            return false;
        }

        if (entry.CompanyId is not { } companyId) return false;

        var stage = entry.Stagenum;
        var written = 0;

        switch (seed.Kind)
        {
            case MoveKind.Treated:
            {
                using var dialog = new TreatDialog(_service, _workspace, companyId, stage, shares, seed.Date, seed);
                dialog.Guard = (_, made) => Check(seed, made);

                if (dialog.ShowDialog(owner) != DialogResult.OK || dialog.Result.Count == 0) return false;

                written = Replace(seed, () => Apply(dialog.Result, dialog.Date, dialog.Fields, dialog.Note, dialog.Window));
                break;
            }

            // Рециклирането се поправя в прозореца, от който е дошло — същия,
            // в който се образува, отворен с другия край.
            case MoveKind.Generated or MoveKind.Recycled:
            {
                var kind = seed.Kind;

                using var dialog = new GenerateDialog(
                    _service, _workspace, companyId, stage, shares, seed.Date, seed, kind);
                dialog.Guard = (_, made) => Check(seed, made);

                if (dialog.ShowDialog(owner) != DialogResult.OK || dialog.Result.Count == 0) return false;

                written = Replace(seed, () => ApplyMade(
                    dialog.Result, dialog.Date, dialog.Fields, dialog.Note, kind, dialog.Window));
                break;
            }

            default:
            {
                using var dialog = new SubmitDialog(
                    _service, _workspace, companyId, stage, shares, seed.Date, seed.Direct, seed);
                dialog.Guard = (_, made) => Check(seed, made);

                if (dialog.ShowDialog(owner) != DialogResult.OK || dialog.Result.Count == 0) return false;

                written = Replace(seed, () => ApplySubmitted(
                    dialog.Result, dialog.Date, dialog.Fields, dialog.Note, seed.Direct, dialog.Contract, dialog.Window));
                break;
            }
        }

        Refresh();
        LedgerChanged?.Invoke(this, EventArgs.Empty);

        return written > 0;
    }

    /// <summary>
    /// Отваря завареното: двете физически купчини преди първия воден месец.
    ///
    /// Оттук, а не от „Настройки“: това не е предпочитание, а самият склад —
    /// какво е стояло в него, преди програмата да поеме воденето.
    /// </summary>
    private void OpenStart()
    {
        if (Company() is not { } companyId) return;

        using var dialog = new StartStockDialog(_service, _workspace, companyId, Stage());

        dialog.ShowDialog(this);

        if (!dialog.Changed) return;

        Refresh();
        LedgerChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Изписва колко реда чакат в дневника — или нищо, ако е празен.</summary>
    private void ShowLog()
    {
        if (_log is null) return;

        var waiting = Company() is { } companyId
            ? _workspace.Actions.Count(companyId, Stage())
            : 0;

        _log.Text = waiting > 0
            ? Lang.T($"Дневник ({waiting})", $"Log ({waiting})")
            : Lang.T("Дневник", "Log");

        _log.FitWidth();
    }

    /// <summary>
    /// Отбелязва въведеното в дневника.
    ///
    /// Едно място за трите действия, защото дневникът не различава третиране от
    /// предаване — той отговаря на един въпрос: „кое въведох току-що и къде
    /// падна“. Записва се СЛЕД самото движение и с неговия номер: ред в
    /// дневника, който сочи никъде, е по-лош от липсващ ред.
    ///
    /// Внесеното от файл не минава оттук: то си има свой дневник
    /// (<see cref="ImportHistory"/>) и се маха наведнъж, цяло.
    /// </summary>
    private void Remember(
        long companyId,
        string stage,
        MoveKind kind,
        long moveId,
        DateOnly date,
        string ewc,
        decimal amount,
        string note,
        string action = "") =>
        _workspace.Actions.Add(companyId, stage, kind, moveId, date, ewc, amount, note, action,
            _amending?.Since, _amending?.Was ?? string.Empty);

    /// <summary>
    /// Отваря таблицата на месеца за този вид движение.
    ///
    /// Оттук минава внасянето, а за прякото предаване — и самото въвеждане: то
    /// излиза от склад „Получен“, без да е минало през третиране, и не тръгва от
    /// избрани редове в гледания склад.
    /// </summary>
    private void OpenMonth(MoveKind kind, SheetImport import = SheetImport.Offered) =>
        OpenMonth(kind, LedgerPeriod.Of(DateOnly.FromDateTime(_date.Value)), import);

    /// <summary>
    /// Същото, но за ИЗРИЧНО посочен месец.
    ///
    /// Дневникът стига дотук: той знае в кой месец е паднал сбърканият ред, а
    /// месецът на страницата е онзи, в който човек гледа — двата се различават
    /// точно когато дневникът върши работа.
    /// </summary>
    private void OpenMonth(MoveKind kind, LedgerPeriod period, SheetImport import = SheetImport.Offered)
    {
        if (Company() is not { } companyId) return;

        // Оттук внасянето остава: складът е единственото място, откъдето се
        // внася файл (виж SheetImport).
        using var dialog = new MoveSheetDialog(
            _service, _workspace, companyId, Stage(), kind, period, import);

        dialog.ShowDialog(this);

        if (!dialog.Saved) return;

        Refresh();
        LedgerChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Отваря партидата зад долния ред — целият ѝ път.</summary>
    private void Open(int row)
    {
        if (row < 0 || row >= _detail.Rows.Count) return;
        if (_detail.Rows[row].Tag is StockLine line) Open(line);
    }

    /// <summary>Отваря партидата зад горния ред — в „Поотделно“ редът е дялът ѝ.</summary>
    private void OpenLine(int row)
    {
        if (row < 0 || row >= _master.Rows.Count) return;
        if (_master.Rows[row].Tag is not string key) return;

        if (Kept().FirstOrDefault(s => Key(s) == key) is { } line) Open(line);
    }

    private void Open(StockLine line)
    {
        if (Company() is not { } companyId) return;

        // Редът без партида няма какво да отвори.
        if (line.Batch.IsNone) return;

        using var dialog = new BatchDialog(_workspace, companyId, Stage(), line.Batch, _names);

        dialog.ShowDialog(this);
    }

    /// <summary>Записаното третиране е ново движение — „Месец“ трябва да научи.</summary>
    public event EventHandler? LedgerChanged;

    /// <summary>Ctrl+F от главния прозорец — курсорът отива в търсенето.</summary>
    public override bool FocusSearch()
    {
        _search.Focus();
        return true;
    }

    // ------------------------------------------------------------- за теста

    /// <summary>Заглавието на екрана — ползва се от тестовете.</summary>
    internal string TitleText => _pageTitle.Text;

    internal int RowCount => _master.Rows.Count;

    internal int DetailCount => _detail.Rows.Count;

    internal string SummaryText => _summary.Text;

    internal string CaptionText => _caption.Text;

    /// <summary>Предлага ли се действие — в летописа го няма.</summary>
    internal bool ActionShown => _actionShown;

    /// <summary>В кое движение внася копчето при този склад.</summary>
    internal MoveKind ImportKind => Feeds(Store);

    /// <summary>Как е озаглавена колоната със сбора — с името и сбора ѝ.</summary>
    internal string AmountHeaderText => _master.Columns[AmountColumn].HeaderText;

    /// <summary>Същото за долната таблица с партидите.</summary>
    internal string DetailAmountHeaderText => _detail.Columns[TakenColumn].HeaderText;

    /// <summary>
    /// Показва склада към дата — с едно пресмятане, не с две.
    ///
    /// Оттук влиза „Отвори „Третиран““ от проверката на месеца: човекът иска да
    /// види какво чака в края на месеца, в който отчетът е спрял. Филтрите се
    /// махат — забравен филтър би скрил точно купчината, заради която е дошъл.
    /// </summary>
    public void Open(StoreKind store, DateOnly on)
    {
        _filling = true;

        try
        {
            _codeFilter = null;
            _partnerFilter = null;
            _receiverFilter = null;
            _contractFilter = null;
            _noteFilter = null;

            _days.Text = string.Empty;
            _least.Text = string.Empty;
            _search.Text = string.Empty;
            _monthOnly.Checked = false;

            _store.SelectedIndex = Math.Max(0, Moves.Stores.ToList().IndexOf(store));
            _date.Value = on.ToDateTime(TimeOnly.MinValue);
        }
        finally
        {
            _filling = false;
        }

        Rebuild();
    }

    internal void UseStore(StoreKind store)
    {
        _store.SelectedIndex = Math.Max(0, Moves.Stores.ToList().IndexOf(store));
        Rebuild();
    }

    internal void UseView(bool byPartner)
    {
        _view.SelectedIndex = byPartner ? 1 : 0;
        Rebuild();
    }

    internal void UseDate(DateOnly date)
    {
        _date.Value = date.ToDateTime(TimeOnly.MinValue);
        Rebuild();
    }

    internal void UseSearch(string text)
    {
        _search.Text = text;
        Rebuild();
    }

    internal void Pick(int row)
    {
        _master.CurrentCell = _master.Rows[row].Cells[0];
        ShowDetail();
    }

    internal void PickDetail(int row) => _detail.CurrentCell = _detail.Rows[row].Cells[0];

    /// <summary>Избира горе, както би избрал човек с Ctrl.</summary>
    internal void SelectMaster(params int[] rows) => Select(_master, rows);

    /// <summary>Избира долу — редовете са самите партиди.</summary>
    internal void SelectDetail(params int[] rows) => Select(_detail, rows);

    private void Select(DataGridView grid, IReadOnlyList<int> rows)
    {
        grid.ClearSelection();

        foreach (var row in rows)
            if (row >= 0 && row < grid.Rows.Count)
                grid.Rows[row].Selected = true;

        _active = grid;
    }

    /// <summary>Дяловете зад избраното — това, което третирането би поело.</summary>
    internal IReadOnlyList<StockShare> Selection()
    {
        var grid = _active ?? _master;

        return grid == _detail ? DetailShares(Chosen(_detail)) : MasterShares(Chosen(_master));
    }

    /// <summary>Надписът на единственото копче за действие.</summary>
    internal string ActionText => _action?.Text ?? string.Empty;

    internal bool TreatOffered => CanTreat;

    /// <summary>Предлага ли се образуване от този склад.</summary>
    internal bool GenerateOffered => CanGenerate;

    /// <summary>Предлага ли се предаване от този склад.</summary>
    internal bool SubmitOffered => CanSubmit;

    /// <summary>Предлага ли се пряко предаване — без третиране.</summary>
    internal bool DirectOffered => CanSubmitDirect;

    /// <summary>Вижда ли се копчето „Предай направо…“.</summary>
    internal bool DirectButtonShown => _handOver?.Visible ?? false;

    /// <summary>Предлага ли се връщане на рециклирано — само в „Рециклиран“.</summary>
    internal bool RevertOffered => CanRevert;

    /// <summary>Редовете зад избраното в момента — цели, с движението зад всеки.</summary>
    internal IReadOnlyList<StockLine> LineSelection() => SelectedLines();

    /// <summary>Заглавието на стълба с датата долу.</summary>
    internal string DetailDateHeader => _detail.Columns[HandedColumn].HeaderText;

    /// <summary>Вижда ли се стълбът с датата долу.</summary>
    internal bool DetailDateShown => _detail.Columns[HandedColumn].Visible;

    /// <summary>Складовете в превключвателя — по ред.</summary>
    internal IReadOnlyList<string> StoreChoices => _store.Items;

    /// <summary>Чете ли се всеки склад в превключвателя цял — без многоточие.</summary>
    internal bool StoreFits => _store.Fits;

    /// <summary>Отваря намереното от дневника рециклиране, както копчето „Отвори месеца…“.</summary>
    internal void ShowRecycledMonth(LedgerPeriod period) => ShowRecycled(period);

    /// <summary>Рециклира тези дялове през прозореца — без да го показва; ползва се от тестовете.</summary>
    internal GenerateDialog RecycleDialog(IReadOnlyList<StockShare> shares) =>
        new(_service, _workspace, Company() ?? 0, Stage(), shares, CurrentDate, kind: MoveKind.Recycled);

    /// <summary>Надписът на копчето върху самия ред — следва склада.</summary>
    internal string RowActionText =>
        _master.Columns[TreatColumn] is DataGridViewButtonColumn column
            ? column.Text ?? string.Empty
            : string.Empty;

    /// <summary>Вижда ли се копчето на реда изобщо.</summary>
    internal bool RowActionShown =>
        _master.Columns[TreatColumn] is { } column && column.Visible;

    internal decimal LooseAmount => _loose;

    internal string ChosenText => _chosen.Text;

    /// <summary>Щраква заглавието на колона, както би щракнал човек.</summary>
    internal void SortMaster(int column) => SortBy(_master, column);

    internal void SortDetail(int column) => SortBy(_detail, column);

    /// <summary>Коя колона е подредена и накъде сочи стрелката ѝ.</summary>
    internal string MasterGlyph(int column) =>
        _master.Columns[column].HeaderCell.SortGlyphDirection.ToString();

    /// <summary>Отмята кодове във филтъра; без стойности (или само празна) го маха.</summary>
    internal bool UseCodeFilter(params string[] codes)
    {
        if (Clears(codes))
        {
            _codeFilter = null;
            Rebuild();

            return true;
        }

        var known = _shares.Select(x => x.Ewc).ToHashSet(StringComparer.Ordinal);

        if (codes.Any(c => !known.Contains(c))) return false;

        _codeFilter = [.. codes];
        Rebuild();

        return true;
    }

    /// <summary>Отмята клиенти по ЕИК; без стойности (или само празна) го маха.</summary>
    internal bool UsePartnerFilter(params string[] eiks)
    {
        if (Clears(eiks))
        {
            _partnerFilter = null;
            Rebuild();

            return true;
        }

        var known = _shares.Select(x => x.Batch.Eik).ToHashSet(StringComparer.Ordinal);

        if (eiks.Any(e => !known.Contains(e))) return false;

        _partnerFilter = [.. eiks];
        Rebuild();

        return true;
    }

    private static bool Clears(string[] values) =>
        values.Length == 0 || (values.Length == 1 && values[0].Length == 0);

    /// <summary>Датата, към която се гледа складът — ползва се от тестовете.</summary>
    internal DateOnly CurrentDate => DateOnly.FromDateTime(_date.Value);

    /// <summary>Един месец назад (-1) или напред (1) — както го правят стрелките.</summary>
    internal void RollDate(int step) => Roll(step);

    /// <summary>Какво пише на копчетата на филтрите — ползва се от тестовете.</summary>
    internal string CodeFilterText => _codes.Text;

    internal string PartnerFilterText => _partners.Text;

    internal string ReceiverFilterText => _receivers.Text;

    /// <summary>Гледа ли се само месецът на датата.</summary>
    internal bool MonthOnlyOn => _monthOnly.Checked;

    /// <summary>Предлага ли се изобщо — в „Третиран“ не се предлага.</summary>
    internal bool MonthOnlyOffered => _monthOnly.Enabled;

    /// <summary>Вдига отметката „Само този месец“, както би я вдигнал човек.</summary>
    internal void UseMonthOnly(bool only)
    {
        _monthOnly.Checked = only;
        Rebuild();
    }


    /// <summary>
    /// Всяка контрола от лентата ли се вижда цяла.
    ///
    /// Проверява се, защото точно това се счупи веднъж: закачени отдясно наляво
    /// с постоянни ширини, филтрите не се събраха и „Изчисти“ се нарисува ВЪРХУ
    /// търсенето. Контрола, която я няма, не е филтър.
    /// </summary>
    internal bool FiltersFit =>
        _filters is not null &&
        Cells().All(cell =>
            cell.Right <= _filters.ClientSize.Width &&
            cell.Bottom <= _filters.ClientSize.Height);

    /// <summary>На колко реда се е пренесла лентата.</summary>
    internal int FilterRows => Cells().Select(c => c.Top).Distinct().Count();

    /// <summary>Колко филтъра стоят в лентата — нито един не бива да изчезне.</summary>
    internal int FilterCount => Cells().Count();

    /// <summary>
    /// Клетките, които лентата наистина показва.
    ///
    /// Не се пита <c>Control.Visible</c>: то отговаря и за родителите си и е
    /// „не“, докато страницата още не е показана (виж АРХИТЕКТУРА.md). Тук се
    /// чете НАГЛАСЕНОТО — скриваните клетки са на получателя, договора и бележката.
    /// </summary>
    private IEnumerable<Control> Cells() =>
        _filters is null
            ? []
            : _filters.Controls
                .Cast<Control>()
                .Where(c => CanSeeReceivers ||
                            (!ReferenceEquals(c, _receiverHost) && !ReferenceEquals(c, _contractHost) &&
                             !ReferenceEquals(c, _noteHost)));

    /// <summary>Предлага ли се изобщо разрез по получател на този склад.</summary>
    internal bool ReceiversOffered => CanSeeReceivers;

    /// <summary>Колко разреза предлага лентата в момента.</summary>
    internal int ViewCount => _view.Items.Count;

    /// <summary>Гледа се откъм получателя.</summary>
    internal void UseReceiverView()
    {
        if (!CanSeeReceivers) return;

        _view.SelectedIndex = 2;
        Rebuild();
    }

    /// <summary>Пресява по получатели, както отметките биха го направили.</summary>
    internal void FilterReceivers(params string[] keys)
    {
        _receiverFilter = keys.Length == 0 ? null : [.. keys];
        Rebuild();
    }

    /// <summary>Гледа се всяко движение поотделно.</summary>
    internal void UseMoveView()
    {
        if (!CanSeeReceivers) return;

        _view.SelectedIndex = 3;
        Rebuild();
    }

    /// <summary>Гледа ли се всяко движение поотделно.</summary>
    internal bool MoveViewOn => ByMove;

    /// <summary>Вижда ли се ситото по договор.</summary>
    internal bool ContractFilterOffered => CanSeeReceivers;

    /// <summary>Пресява по договори, както отметките биха го направили.</summary>
    internal void FilterContracts(params string[] contracts)
    {
        _contractFilter = contracts.Length == 0 ? null : [.. contracts];
        Rebuild();
    }

    internal string ContractFilterText => _contracts.Text;

    /// <summary>Пресява по бележки, както отметките биха го направили.</summary>
    internal void FilterNotes(params string[] notes)
    {
        _noteFilter = notes.Length == 0 ? null : [.. notes];
        Rebuild();
    }

    internal string NoteFilterText => _notes.Text;

    /// <summary>Пресява ли десният бутон по заглавията „Договор“ и „Бележка“ — и в двете таблици.</summary>
    internal bool ContractColumnFilters =>
        SieveOf(ContractColumn) == Sieve.Contract && SieveOf(NoteColumn) == Sieve.Note &&
        _detail.Columns.Contains(ContractColumn) && _detail.Columns.Contains(NoteColumn);

    /// <summary>С икони ли са изгледите и колко място заемат.</summary>
    internal bool ViewIconsOn => _view.ShowsIcons;

    internal int ViewWidth => _view.Width;

    /// <summary>Името на изгледа — то е и подсказката на иконата.</summary>
    internal string ViewNameAt(int index) => _view.Items[index];

    internal string MasterContractAt(int row) =>
        _master.Rows[row].Cells[ContractColumn].Value?.ToString() ?? string.Empty;

    internal string MasterReceiverAt(int row) =>
        _master.Rows[row].Cells[ReceiverColumn].Value?.ToString() ?? string.Empty;

    internal string MasterDateAt(int row) =>
        _master.Rows[row].Cells[HandedColumn].Value?.ToString() ?? string.Empty;

    internal string MasterBatchAt(int row) =>
        _master.Rows[row].Cells[BatchColumn].Value?.ToString() ?? string.Empty;

    internal string MasterPartnerAt(int row) =>
        _master.Rows[row].Cells[PartnerColumn].Value?.ToString() ?? string.Empty;

    internal string MasterDocumentAt(int row) =>
        _master.Rows[row].Cells[DocumentColumn].Value?.ToString() ?? string.Empty;

    internal string MasterNoteAt(int row) =>
        _master.Rows[row].Cells[NoteColumn].Value?.ToString() ?? string.Empty;

    internal bool MasterNoteShown => _master.Columns[NoteColumn].Visible;

    internal bool MasterContractShown => _master.Columns[ContractColumn].Visible;

    internal string DetailContractAt(int row) =>
        _detail.Rows[row].Cells[ContractColumn].Value?.ToString() ?? string.Empty;

    internal bool DetailContractShown => _detail.Columns[ContractColumn].Visible;

    internal string DetailNoteAt(int row) =>
        _detail.Rows[row].Cells[NoteColumn].Value?.ToString() ?? string.Empty;

    internal bool DetailNoteShown => _detail.Columns[NoteColumn].Visible;

    /// <summary>Щраква заглавието „Договор“ горе, както би щракнал човек.</summary>
    internal void SortMasterByContract() => SortBy(_master, _master.Columns[ContractColumn].Index);

    /// <summary>Чете ли се всеки разрез цял — без многоточие.</summary>
    internal bool ViewFits => _view.Fits;

    /// <summary>Побират ли се видимите стълбове горе в ширината на таблицата.</summary>
    internal bool ColumnsFit => Fit(_master);

    /// <summary>
    /// Същото долу. Таблицата превърта и отвътре нищо не изглежда изрязано, а най-
    /// десният стълб там е количеството.
    /// </summary>
    internal bool DetailColumnsFit => Fit(_detail);

    private static bool Fit(DataGridView grid)
    {
        var shown = grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).ToList();

        // Пълнещият стълб расте и се свива сам: брои се най-тясното му.
        var need = shown.Sum(c => c.AutoSizeMode == DataGridViewAutoSizeColumnMode.Fill ? c.MinimumWidth : c.Width);

        return need <= grid.ClientSize.Width - SystemInformation.VerticalScrollBarWidth;
    }

    /// <summary>Отстъпът на клетка отляво и отдясно — той не расте с мащаба.</summary>
    private const int CellPadding = 12;

    /// <summary>
    /// Чете ли се всяко заглавие горе цяло: нито дума, по-широка от стълба си, нито
    /// редове, повече от височината на заглавието.
    ///
    /// Второто е сборът: „Предадено, т“ в тесен стълб минава на втори ред и избутва
    /// числото под него извън заглавието, без нищо да изглежда счупено.
    /// </summary>
    internal IReadOnlyList<string> HeadersCut =>
    [
        .. _master.Columns.Cast<DataGridViewColumn>()
            .Where(c => c.Visible && c.HeaderText.Length > 0)
            .Where(column =>
            {
                var font = _master.ColumnHeadersDefaultCellStyle.Font ?? _master.Font;
                var room = column.Width - CellPadding;

                var words = column.HeaderText.Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                var high = TextRenderer.MeasureText(
                    column.HeaderText, font, new Size(room, int.MaxValue), TextFormatFlags.WordBreak).Height;

                return high > _master.ColumnHeadersHeight ||
                       words.Any(word => TextRenderer.MeasureText(word, font).Width > room);
            })
            .Select(c => $"{c.HeaderText.Replace(Environment.NewLine, " ")} ({c.Width} px)")
    ];

    /// <summary>
    /// Четат ли се целите късите стълбове в „Поотделно“. Дата, код, партида и
    /// количество не отстъпват: отрязани, те вече не значат нищо. Текстовите
    /// отстъпват нарочно и пълният им текст излиза под мишката.
    ///
    /// Гледа се и височината на самите редове: те се сглобяват наведнъж, отстрани,
    /// и ред без образеца на таблицата при 150 % реже текста си.
    /// </summary>
    internal bool FiguresFit =>
        _master.Rows.Cast<DataGridViewRow>().All(row => row.Height == _master.RowTemplate.Height) &&
        new[] { HandedColumn, KeyColumn, BatchColumn, AmountColumn }
            .Select(name => _master.Columns[name])
            .All(column => _master.Rows.Cast<DataGridViewRow>().All(row =>
                TextRenderer.MeasureText(row.Cells[column.Index].Value?.ToString() ?? string.Empty, _master.Font).Width
                + CellPadding <= column.Width));

    /// <summary>Вижда ли се долната половина — истинската видимост, за показан екран.</summary>
    internal bool DetailPaneShown => _bottom?.Visible ?? false;

    /// <summary>Кога е предадено — по ред от долната таблица.</summary>
    internal string DetailHandedAt(int row) =>
        row >= 0 && row < _detail.Rows.Count
            ? _detail.Rows[row].Cells[HandedColumn].Value?.ToString() ?? string.Empty
            : string.Empty;

    /// <summary>Какво пише в стълба за получател на ред от долната таблица.</summary>
    internal string DetailReceiverAt(int row) =>
        row >= 0 && row < _detail.Rows.Count
            ? _detail.Rows[row].Cells[ReceiverColumn].Value?.ToString() ?? string.Empty
            : string.Empty;

    /// <summary>Вижда ли се стълбът за получател.</summary>
    internal bool DetailReceiverShown =>
        _detail.Columns[ReceiverColumn] is { } column && column.Visible;

    internal void UseDays(string days)
    {
        _days.Text = days;
        Rebuild();
    }

    internal void UseLeast(string tons)
    {
        _least.Text = tons;
        Rebuild();
    }

    internal void ClearAllFilters() => ClearFilters();

    internal string SummaryLine => _summary.Text;

    internal string KeyAt(int row) => _master.Rows[row].Cells[KeyColumn].Value?.ToString() ?? string.Empty;

    internal string AmountAt(int row) => _master.Rows[row].Cells[AmountColumn].Value?.ToString() ?? string.Empty;

    internal string DetailPartnerAt(int row) =>
        _detail.Rows[row].Cells[PartnerColumn].Value?.ToString() ?? string.Empty;

    internal string DetailAmountAt(int row) =>
        _detail.Rows[row].Cells[TakenColumn].Value?.ToString() ?? string.Empty;

    // ------------------------------------------------------------- помощни

    private long? Company() => _scope.Current;

    private string Stage() => _stage.SelectedItem as string ?? "1";

    /// <summary>Как се казва получателят зад ключа — за надписа под таблицата.</summary>
    private string Receiver(string key) =>
        _shares.FirstOrDefault(s => s.Party.Key == key)?.Party.Display is { Length: > 0 } name
            ? name
            : Lang.T("(не е посочен)", "(not stated)");

    /// <summary>Как се чете договорът — празното си има име: и него може да се търси.</summary>
    private static string ContractName(string contract) =>
        contract.Length > 0 ? contract : Lang.T("(не е посочен)", "(not stated)");

    private static string Partner(Batch batch) =>
        batch.PartnerName.Length > 0
            ? batch.Partner
            : batch.Eik.Length > 0 ? batch.Eik : Lang.T("(клиент не е посочен)", "(no client stated)");

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

    /// <summary>
    /// Една клетка от пренасящата се лента: надпис отгоре, контрола отдолу.
    ///
    /// Собствена височина, а не разтеглена: в пренасящ се ред всички клетки
    /// трябва да са еднакво високи, иначе вторият ред започва накриво.
    /// </summary>
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
}
