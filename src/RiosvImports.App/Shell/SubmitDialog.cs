using RiosvImports.App.Controls;
using RiosvImports.Core;
using RiosvImports.Core.Ledger;
using RiosvImports.Core.Localization;
using RiosvImports.Core.Schema;
using RiosvImports.Core.Values;
using RiosvImports.Data;

namespace RiosvImports.App.Shell;

/// <summary>
/// Предаване на избраното от склад „Образуван“.
///
/// Нарочно същото прозорче като „Третиране от склада“: същите две таблици,
/// същият избор с Ctrl и Shift, същата колона, в която се пише количество,
/// същите „Всичко“ и „Нищо“, същото „и продължи“. Човек, който е научил едното,
/// не бива да учи другото — а и двете правят една и съща работа: избира се
/// какво излиза от склада и се разписва по партидите му.
///
/// <b>Тук обаче се тръгва отзад напред.</b> Третирането почва от склада:
/// „взимам осемнайсет тона кабел“. Предаването почва от кантара: камионът е
/// напълнен, теглен е и е върнал трийсет тона, а чак после се пита кои редове
/// правят точно тези трийсет. Досега тази сметка се е правела на ръка — коя
/// доставка, от коя дата, чия е била — и заради нея съществува програмата.
/// Затова прозорчето има поле за заявеното общо количество и изравняване към
/// него (<see cref="Handover.Onto"/>, <see cref="Handover.Scale"/>); всичко
/// останало е третирането.
///
/// Изходен код има, но по изключение: предаването не преработва нищо и
/// отпадъкът почти винаги си отива под кода, с който стои. Почти — затова
/// „Използвай същия код“ стои вдигнато и общият код се пита само когато някой
/// го свали.
///
/// <b>Прозорчето е едно за двата вида предаване.</b> Обичайното вади от склад
/// „Образуван“: прието, третирано, обявено, предадено. Прякото вади направо от
/// „Получен“ — не всичко се третира. Площадка за събиране приема опаковки от
/// стотина контрагента и ги предава почти както са дошли, а изискването да
/// минат през третиране, за да излязат, би било измислено от програмата, не от
/// работата.
///
/// Разликата е ЕДИН флаг и той вече съществува: <see cref="Moves.From"/> знае
/// от кой склад вади предаването, а <see cref="Move.Direct"/> го помни. Затова
/// тук няма второ прозорче и второ правило — има един въпрос, зададен на друга
/// купчина.
///
/// Полетата горе не са изброени в кода: те идват от възела на схемата, както
/// навсякъде другаде (правило 1). Днес това са получателят с вида и ЕИК-а му,
/// основанието, дейността R/D с описанието ѝ, двете полета за НУБА и „за
/// повторна употреба“; утре НИСО може да добави още едно.
/// </summary>
internal sealed class SubmitDialog : Dialog
{
    private const string CodeColumn = "__code";
    private const string BatchColumn = "__batches";
    private const string AgeColumn = "__age";
    private const string StockColumn = "__stock";
    private const string TakeColumn = "__take";

    private const string OriginBatch = "__obatch";
    private const string OriginPartner = "__opartner";
    private const string OriginDocument = "__odocument";
    private const string OriginStock = "__ostock";
    private const string OriginTake = "__otake";

    private readonly Workspace _workspace;
    private readonly long _companyId;
    private readonly string _stagenum;
    /// <summary>
    /// Купчините, които се предават сега.
    ///
    /// Не са окончателни: смяната на датата ги смята наново, защото на друга
    /// дата в склада стои друго. Затова не са readonly.
    /// </summary>
    private IReadOnlyList<TreatPile> _piles;

    /// <summary>
    /// Кое е избрано отзад — по двойки „код и партида“.
    ///
    /// Прозорчето се пресмята наново при всяка смяна на датата, но обхватът му
    /// остава този: човекът е избрал тези клиенти и тези кодове, а датата само
    /// казва колко от тях е стояло тогава.
    /// </summary>
    private readonly HashSet<(string Ewc, long Batch)> _chosen;

    private readonly DateOnly _on;

    /// <summary>
    /// От кой склад вади това предаване.
    ///
    /// Не се решава тук: <see cref="Moves.From"/> е единственото място, което
    /// знае кое движение кой склад пипа (правило 12а). Пази се в поле, защото
    /// смяната на датата смята купчините наново и трябва да пита същия склад.
    /// </summary>
    private readonly StoreKind _store;

    /// <summary>Пряко ли е предаването — направо от „Получен“, без третиране.</summary>
    private readonly bool _direct;

    private readonly ReportNode? _node;
    private readonly Dictionary<string, Control> _inputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _names;

    private readonly DateTimePicker _date = new();

    /// <summary>Редът до датата: в кой месец влиза записът и дали той приема.</summary>
    private readonly Label _when = new();

    /// <summary>
    /// Работният период — самоконтролът на човека, прочетен при отваряне.
    ///
    /// Чете се веднъж: той се мени от „Настройки“, не оттук, а прозорчето живее
    /// минути. Пресмятане при всяка смяна на датата би питало базата стотици
    /// пъти за отговор, който не се мени.
    /// </summary>
    private readonly WorkPeriod _period;

    /// <summary>Отметката, с която човек оттегля собственото си правило.</summary>
    private readonly CheckBox _outside = new();

    /// <summary>Рамката на датата — расте, когато отметката се появи.</summary>
    private Panel? _dateHost;

    /// <summary>
    /// Предлага ли се изобщо оттегляне на периода.
    ///
    /// Пази се отделно от отметката: <c>Control.Visible</c> отговаря и за
    /// родителите си и е „не“, докато прозорчето още не е показано — а тестът
    /// пита за нагласеното, не за нарисуваното (същото като при копчето на
    /// склада).
    /// </summary>
    private bool _outsideShown;
    private readonly TextBox _note = new();

    /// <summary>
    /// Договорът, по който се предава — по избор.
    ///
    /// Устроено като получателя и нарочно: предлага вече писаното, приема ново и
    /// то се научава при записа. Разликата е, че тук написаното се изравнява със
    /// списъка (<see cref="LedgerWriter"/>): по договора се пресява и един
    /// договор, изписан по два начина, са два реда в ситото.
    /// </summary>
    private readonly ComboBox _contract = new()
    {
        DropDownStyle = ComboBoxStyle.DropDown,
        AutoCompleteMode = AutoCompleteMode.SuggestAppend,
        AutoCompleteSource = AutoCompleteSource.ListItems
    };

    private readonly CheckBox _sameCode = new();
    private readonly ComboBox _outCode = new();

    /// <summary>Рамката на полетата и самите полета — заради <see cref="FieldsFit"/>.</summary>
    private Panel? _fieldsFrame;
    private FlowLayoutPanel? _fieldsHost;

    /// <summary>Заявеното общо количество — кантарът, ако има такъв.</summary>
    private readonly TextBox _target = new();

    /// <summary>Разликата между избраното и заявеното, с думи.</summary>
    private readonly Label _gap = new();

    /// <summary>Кой ред ще поеме разликата — казва го преди да е натиснато.</summary>
    private readonly Label _anchor = new();

    /// <summary>Кой ред да поеме разликата — посочен изрично.</summary>
    private readonly ComboBox _into = new();

    /// <summary>
    /// Посоченият ред, ако е посочен.
    ///
    /// Празно значи „редът, на който стоя“ — както беше и преди да го има
    /// менюто. Партида 0 значи цял код: горният ред, чиято разлика се разписва
    /// по партидите му.
    /// </summary>
    private (string Ewc, long Batch)? _pinned;

    /// <summary>По какво се познава, че менюто трябва да се пренапише.</summary>
    private string _intoShape = string.Empty;



    /// <summary>Рамката на кантара — ивицата ѝ носи състоянието на едно място.</summary>
    private Card? _scale;

    private AppButton? _level;
    private AppButton? _spread;

    private readonly CheckBox _perBatch = new();
    private readonly DataGridView _rows = new();
    private readonly DataGridView _origin = new();

    /// <summary>
    /// Проверката на поправката — подава я страницата, само когато действие се
    /// поправя.
    ///
    /// Прозорецът пази новата дата сам: той не дава да се вземе повече от
    /// стоялото тогава. Не вижда обаче какво е станало СЛЕД нея — движение,
    /// което е черпило от старото. Страницата го вижда и казва кое би се
    /// развалило; празно значи, че поправката се държи.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<DateOnly, IReadOnlyList<Planned>, IReadOnlyList<string>>? Guard { get; set; }

    /// <summary>Поправяното действие — празно, когато се въвежда ново.</summary>
    private readonly AmendSeed? _amend;

    /// <summary>
    /// Движенията, които поправката заменя. Складът се смята без тях: тяхното
    /// количество е „върнато“, докато се поправя, иначе действието не би могло
    /// да се премести дори с един ден.
    /// </summary>
    private readonly HashSet<long> _gone = [];

    /// <summary>Пресяването на двете таблици — десен бутон по заглавието.</summary>
    private readonly GridFilter _rowsFilter;
    private readonly GridFilter _originFilter;

    /// <summary>Търсенето над двете таблици — по код, клиент, партида или документ.</summary>
    private readonly StockSearch _search = new();

    /// <summary>„Вземи отгоре до …“ над партидите — в реда, в който се виждат.</summary>
    private readonly FromTopBar _fromTop = new();

    /// <summary>Надписът над кодовете — казва и какво е пресято.</summary>
    private readonly Label _what = new();
    private readonly Label _split = new();
    private readonly Label _totals = new();
    private readonly Label _behind = new();

    /// <summary>Сборът на маркираното — отговаря на „това колко е“.</summary>
    private readonly Label _picked = new();

    /// <summary>Коя таблица е пипана последна — по нея се брои маркираното.</summary>
    private DataGridView? _last;

    private AppButton? _save;
    private AppButton? _more;

    /// <summary>Какво е записано, докато прозорчето стои отворено.</summary>
    /// <summary>Какво трябва да се знае при поправка — видимо през цялата ширина.</summary>
    private readonly NoticeStrip _notice = new();

    private readonly Label _done = new();

    /// <summary>Колко предавания са записани, докато прозорчето стои отворено.</summary>
    private int _saved;

    /// <summary>Описанието на дейността, както го е попълнил самият прозорец.</summary>
    private string _written = string.Empty;

    /// <summary>
    /// Каквото човекът е написал по код — за да не се губи при пресичане.
    ///
    /// Изваден и върнат обратно в избора, редът трябва да се появи с числото,
    /// което човек е писал, а не с цялата купчина. Иначе всяко пипване по
    /// избора трие работа.
    /// </summary>
    private readonly Dictionary<string, decimal> _wanted = new(StringComparer.Ordinal);

    /// <summary>Откъде е тръгнало влаченето — по него се познава от щракването.</summary>
    private int _from = -1;

    /// <summary>Избраните редове преди писане на количество — връщат се след него.</summary>
    private List<int> _held = [];
    private List<int> _kept = [];

    /// <summary>По коя колона е подредена всяка таблица и накъде.</summary>
    private string _rowsSort = CodeColumn;
    private bool _rowsDown;
    private string _originSort = OriginDocument;
    private bool _originDown;

    /// <summary>
    /// По колко се взима от всяка партида.
    ///
    /// Тук стои истината за предаването. Горната таблица е сборът ѝ по кодове, а
    /// долната — самите редове; и двете пишат в нея. През нея минава и
    /// изравняването към кантара: то пипа дяловете, а не написаното в клетка.
    /// </summary>
    private readonly Dictionary<(string Ewc, long Batch), decimal> _picks = [];

    /// <summary>
    /// Посоченото на ръка по партиди — по код, отделно от <see cref="_picks"/>.
    ///
    /// Дяловете се сглобяват наново при всяка смяна на датата, а с тях се губеше
    /// и изборът вдясно: посочените партиди ставаха отново „най-старата
    /// нататък“. Тук той оцелява — и за партиди, които на междинна дата ги няма:
    /// датата се пише цифра по цифра и минава през дати, на които не са дошли.
    /// </summary>
    private readonly Dictionary<string, Dictionary<long, decimal>> _hand = new(StringComparer.Ordinal);

    /// <summary>Избраните кодове, които на тази дата ги няма — връщат се избрани.</summary>
    private readonly HashSet<string> _away = new(StringComparer.Ordinal);

    private bool _filling;

    public SubmitDialog(
        ImportService service,
        Workspace workspace,
        long companyId,
        string stagenum,
        IReadOnlyList<StockShare> shares,
        DateOnly on,
        bool direct = false,
        AmendSeed? amend = null)
    {
        _workspace = workspace;
        _companyId = companyId;
        _stagenum = stagenum;
        _on = on;
        _amend = amend;

        if (amend is not null) _gone.UnionWith(amend.Gone);
        _direct = direct;
        _store = Moves.From(MoveKind.Submitted, direct) ?? StoreKind.Formed;
        _piles = Handover.Piles(shares);

        // При поправка обхватът е този на действието, а не наличното в момента: свито
        // до нула на тази дата, то пак трябва да се върне на друга.
        _chosen = amend?.Scope is { } scope
            ? [.. scope]
            : [.. shares.Select(s => (ValueConverter.NormalizeCode(s.Ewc), s.Batch.Id))];

        _period = workspace.Periods.Window(companyId);

        var schema = service.GetSchema("book");

        _node = schema.FindReport(BookNodes.Report)?
            .SelfAndDescendants()
            .FirstOrDefault(n => n.ElementName == Moves.Node(MoveKind.Submitted));

        _names = EwcNames(schema);

        Text = Title;

        // Прозорецът се разтваря на цял екран. Тук се работи, а не се отговаря
        // на един въпрос: при двайсет кода и по пет доставки зад всеки,
        // таблиците искат толкова място, колкото човекът има.
        MaximizeBox = true;
        FormBorderStyle = FormBorderStyle.Sizable;
        WindowState = FormWindowState.Maximized;

        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;

        // По-широк праг от този при третирането, и по сметка: дясната рамка тук
        // носи и кантара и иска 440 пиксела, а разделителят се слага само ако
        // двата минимума се побират (380 + 440 + 6). При 880 те не се побират с
        // два пиксела — разделителят оставаше по средата и лявата таблица се
        // отваряше с два видими стълба от пет.
        ClientSize = new Size(
            // Никога повече, отколкото екранът има: при 150 % желаното в
            // пиксели е един път и половина по-голямо и без този таван
            // прозорчето би поискало ширина, каквато лаптопът няма.
            Math.Clamp(screen.Width - 160,
                Math.Min(Theme.Scaled(1060), screen.Width),
                Math.Max(Theme.Scaled(1480), Theme.Scaled(1060))),
            Math.Min(Theme.Scaled(880), Math.Max(Theme.Scaled(600), screen.Height - 160)));

        MinimumSize = new Size(
            Math.Min(Theme.Scaled(980), screen.Width),
            Math.Min(Theme.Scaled(600), screen.Height));
        BackColor = Theme.Canvas;
        Font = Theme.Base;

        // Редът на добавяне е нарочен и е същият като в MoveDialog: закаченото
        // за ръбовете се добавя СЛЕД това, което пълни средата, иначе пораснало
        // поле се рисува върху лентата с копчетата — виж бележката там.
        _rowsFilter = new GridFilter(_rows);
        _originFilter = new GridFilter(_origin);

        _search.Attach(_rowsFilter, _originFilter, p => Describe(p.Ewc), () => _rows.CurrentRow?.Tag as TreatPile);
        _search.Changed += Searched;

        var rows = BuildRows();
        Controls.Add(rows);

        var fields = BuildFields();
        Controls.Add(fields);

        // Под заглавието, над полетата. Видима е само при поправка, и то ако има какво
        // да каже.
        Controls.Add(_notice);

        Controls.Add(BuildButtons());

        var title = BuildTitle();
        Controls.Add(title);

        // На нисък екран — лаптоп при 150 % — работната част не отстъпва:
        // отстъпват обяснението и полетата (виж Dialog.KeepWork).
        KeepWork(title, fields, rows, Theme.Scaled(500));

        Seed();

        if (_amend is not null) Reopen(_amend);

        Theme.Apply(this);
    }

    /// <summary>Готовите движения. Имат смисъл само при <see cref="DialogResult.OK"/>.</summary>
    public IReadOnlyList<HandMove> Result { get; private set; } = [];

    /// <summary>Датата на предаването — една за всички редове.</summary>
    public DateOnly Date => DateOnly.FromDateTime(_date.Value);

    /// <summary>Колоните от схемата, попълнени веднъж за всички редове.</summary>
    public IReadOnlyDictionary<string, string> Fields { get; private set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Бележката, ако има такава — една за всички редове.</summary>
    public string Note => _note.Text.Trim();

    /// <summary>Договорът, ако е посочен — един за всички редове, както бележката.</summary>
    public string Contract => PartnerStore.Tidy(_contract.Text);

    /// <summary>
    /// Какво трябва да се помни за прозореца, за да се отвори същият при поправка.
    /// Кантарът е тук: той е мярката, спрямо която са подреждани редовете, а в
    /// движенията го няма.
    /// </summary>
    internal ActionWindow Window =>
        new([.. _chosen.Select(c => new WindowShare(c.Ewc, c.Batch))], Target, _perBatch.Checked);

    /// <summary>
    /// Извън работния период ли се пише. Поправка на старата дата не е: действието
    /// е минало през тази проверка при записа си и отметка за него би значила само
    /// още едно щракване. Сменена дата е ново решение и се пита.
    /// </summary>
    private bool OutsidePeriod => !_period.Covers(Date) && _amend?.Date != Date;

    /// <summary>
    /// Кой записва сглобеното предаване.
    ///
    /// Прозорчето не пише в базата само̀: правилото как предаване става движение
    /// живее в страницата отзад и там си остава — иначе същото правило щеше да
    /// има две копия, а второто рано или късно се разминава с първото.
    ///
    /// Нужно е само на „Предай и продължи“: при затваряне страницата взема
    /// сглобеното от <see cref="Result"/>, както досега.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<IReadOnlyList<HandMove>, DateOnly, IReadOnlyDictionary<string, string>, string, int>? Write
    {
        get; set;
    }

    /// <summary>
    /// Дали копчето за записване се побира цяло в прозореца.
    ///
    /// Проверява се по същата причина, по която и в <see cref="MoveDialog"/>:
    /// полетата идват от схемата и утре може да са двойно повече. Тук те стоят
    /// в превъртаща се рамка с определена височина, тъй че не могат да избутат
    /// копчето — но това е решение, което се пази, а не съвпадение.
    /// </summary>
    internal bool SaveReachable
    {
        get
        {
            if (_save is null) return false;

            var place = Place(_save);

            return ClientRectangle.Contains(place) && !Covered(place);
        }
    }

    private bool Covered(Rectangle place)
    {
        var bar = _save?.Parent;

        while (bar is not null && bar.Parent != this) bar = bar.Parent;
        if (bar is null) return false;

        var above = Controls.IndexOf(bar);

        for (var i = 0; i < above; i++)
            if (Controls[i].Bounds.IntersectsWith(place)) return true;

        return false;
    }

    private Rectangle Place(Control control)
    {
        var bounds = control.Bounds;

        for (var parent = control.Parent; parent is not null && parent != this; parent = parent.Parent)
            bounds.Offset(parent.Bounds.Location);

        return bounds;
    }

    // ------------------------------------------------------------- устройство

    private Control BuildTitle()
    {
        var host = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(100),
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, Theme.Gap, Theme.GapLarge, 0)
        };

        // Пълнещият влиза ПРЪВ. Закаченото се подрежда от последното добавено
        // назад, тъй че добавен последен, пълнещият заема цялата височина и
        // редът под заглавието остава без своя — стои под него, но невидим.
        // Точно капанът, описан в АРХИТЕКТУРА.md, и той стоеше тук от самото
        // начало: редът се четеше само в кода.
        host.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = Title,
            Font = Theme.Title,
            ForeColor = Theme.Ink
        });

        host.Controls.Add(new Label
        {
            Dock = DockStyle.Bottom,

            // Два реда, а не един: обяснението е две изречения на два езика и
            // на един ред се реже точно там, където обяснява.
            Height = Theme.Scaled(48),
            Text = Lead,
            Font = Theme.Base,
            ForeColor = Theme.InkMuted
        });

        return host;
    }

    /// <summary>
    /// Полетата, които важат за цялото предаване.
    ///
    /// В превъртаща се рамка с определена височина: те идват от схемата и не се
    /// знае колко ще са. Порасналото поле тук превърта, вместо да се разлее
    /// върху таблицата и копчетата отдолу.
    /// </summary>
    private Control BuildFields()
    {
        // Три реда по четири полета, а кутията за общия код е четвъртото на
        // последния и е висока колкото две. Рамката пак превърта — при по-тесен
        // прозорец редовете стават повече — но при отваряне нищо не остава
        // отрязано.
        var frame = _fieldsFrame = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(224),
            AutoScroll = true,
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, 0, Theme.GapLarge, Theme.Gap)
        };

        var host = _fieldsHost = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = Theme.Canvas
        };

        foreach (var column in Extras())
            host.Controls.Add(Field(Caption(column), Input(column)));

        // Полетата се раждат по реда на схемата, а описанието стои СЛЕД
        // дейността: попълни ли се тя сама, изборът ѝ е минал, преди полето на
        // описанието изобщо да съществува. Затова се дописва накрая.
        Settle();

        FillContracts();

        host.Controls.Add(Field(Moves.ContractTitle, _contract));
        host.Controls.Add(Field(Lang.T("Бележка", "Note"), _note));
        host.Controls.Add(BuildOutCode());

        frame.Controls.Add(host);

        return frame;
    }

    /// <summary>
    /// Датата — над кодовете, в ЛЯВАТА половина.
    ///
    /// Дотук стоеше над дясната половина, до самите числа, и изяждаше от
    /// височината на партидите — а там при петдесет клиента зад един код всеки
    /// ред е нужен. Вляво място има: кодовете рядко са повече от няколко.
    /// Датата е единственото, което не се вижда от само себе си — сгрешена, тя
    /// изглежда точно като дата — затова остава в синя карта, а не в полетата горе.
    ///
    /// Закачена е за лявата рамка, а не сложена на изчислено място: така тя
    /// върви с разделителя, вместо да се разминава с него при първото дръпване.
    /// </summary>
    private Control BuildDate()
    {
        var host = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(74),
            BackColor = Theme.Canvas,
            Padding = new Padding(0, 0, 0, Theme.GapSmall)
        };

        var card = new Card
        {
            Dock = DockStyle.Fill,
            Fill = Theme.AccentLight,
            Outline = Theme.AccentSoft,
            Stripe = Theme.Accent,
            Padding = new Padding(Theme.Gap + 4, 6, Theme.Gap, 6)
        };

        var line = new Panel { Dock = DockStyle.Top, Height = Theme.Scaled(34), BackColor = Theme.AccentLight };

        // По-дребна от преди: тя вече не се бори за внимание с цялата ширина, а
        // стои до числата и ѝ стига да се чете.
        _date.Format = DateTimePickerFormat.Short;
        _date.Font = Theme.Title;
        _date.Dock = DockStyle.Left;
        _date.Width = Theme.Scaled(136);
        Theme.StyleInput(_date);
        _date.ValueChanged += (_, _) => Reload();

        var caption = new Label
        {
            Dock = DockStyle.Left,
            Width = Theme.Scaled(150),
            Text = DateTitle,
            Font = Theme.Bold,
            ForeColor = Theme.Accent,
            BackColor = Theme.AccentLight,
            TextAlign = ContentAlignment.MiddleLeft
        };

        // Закачените отляво се редят в обратен ред на добавяне.
        line.Controls.Add(_date);
        line.Controls.Add(caption);

        _when.Dock = DockStyle.Fill;
        _when.Font = Theme.Small;
        _when.ForeColor = Theme.InkMuted;
        _when.BackColor = Theme.AccentLight;
        _when.TextAlign = ContentAlignment.MiddleLeft;

        // Отметката стои ПРИ датата, а не при копчетата: тя се отнася за нея и
        // само за нея. Мястото ѝ се пази и когато я няма — иначе картата
        // подскача при всяка смяна на датата и окото губи реда под нея.
        _outside.Dock = DockStyle.Bottom;
        _outside.Height = Theme.Scaled(24);
        _outside.Text = Lang.T("Пиши извън работния период", "Write outside the working period");
        _outside.Font = Theme.Small;
        _outside.ForeColor = Theme.Warn;
        _outside.BackColor = Color.Transparent;
        _outside.Visible = false;
        _outside.CheckedChanged += (_, _) => ShowWhen();

        _dateHost = host;

        // Пълнещият влиза пръв (виж АРХИТЕКТУРА.md).
        card.Controls.Add(_when);
        card.Controls.Add(_outside);
        card.Controls.Add(line);

        host.Controls.Add(card);

        return host;
    }

    private static string DateTitle => Lang.T("Дата на предаване", "Date of the hand-over");

    /// <summary>
    /// Как се казва прозорчето — по това от кой склад вади.
    ///
    /// Свойство, а не статично поле: статичното се пълни веднъж, с езика, който
    /// е бил в сила тогава (правило 8).
    /// </summary>
    private string Title => _direct
        ? Lang.T("Пряко предаване от склада", "Direct hand-over from the store")
        : Lang.T("Предаване от склада", "Hand-over from the store");

    /// <summary>
    /// Редът под заглавието — как се работи тук.
    ///
    /// При прякото предаване първото изречение казва и КАКВО става, защото това
    /// е единственият случай, в който количество напуска площадката, без да е
    /// минало през третиране, и човек трябва да е сигурен, че е поискал точно
    /// това.
    /// </summary>
    private string Lead => _direct
        ? Lang.T(
            "Предава се направо от склад „Получен“, без третиране. Горе се избират кодове, " +
            "долу — самите партиди. Кажете колко е предадено общо и „Изравни“ ще сведе " +
            "избраното до него — разликата сяда в реда, чието КОЛИЧЕСТВО сте посочили. " +
            "Заглавие: щракване подрежда, десен бутон пресява.",
            "Handed over straight from the “Received” store, with no treatment. Codes are picked " +
            "above, batches below. State the total handed over and “Balance” brings the selection " +
            "down to it — the difference lands in the row whose QUANTITY you click. " +
            "Header: click sorts, right-click filters.")
        : Lang.T(
            "Предава се избраното. Горе се избират кодове, долу — самите партиди: щракване, " +
            "Ctrl или Shift за няколко. Кажете колко е предадено общо и „Изравни“ ще сведе " +
            "избраното до него — разликата сяда в реда, чието КОЛИЧЕСТВО сте посочили. " +
            "Заглавие: щракване подрежда, десен бутон пресява.",
            "What is selected is what gets handed over. Codes are picked above, batches below: " +
            "click, Ctrl or Shift for several. State the total handed over and “Balance” brings " +
            "the selection down to it — the difference lands in the row whose QUANTITY you click. " +
            "Header: click sorts, right-click filters.");

    /// <summary>
    /// Казва в кой месец влиза записът — и дали той още приема.
    ///
    /// Месецът с име, а не датата втори път: сгрешената дата почти винаги е
    /// сгрешен МЕСЕЦ, а „16.09.2026“ не се чете като „септември“ достатъчно
    /// бързо, за да се хване с крайчеца на окото.
    /// </summary>
    private void ShowWhen()
    {
        var month = LedgerPeriod.Of(Date);

        // Отметката се показва само когато има какво да се оттегля. Стои ли
        // винаги, тя учи човека да я вдига по навик — а тогава правилото му не
        // пази нищо.
        var outside = OutsidePeriod;

        _outsideShown = outside;
        _outside.Visible = outside;
        if (!outside) _outside.Checked = false;

        // Рамката расте само когато има какво да побере. Постоянната празна
        // ивица под датата би яла ред от таблицата всеки ден заради случай,
        // който се пада веднъж месечно — а подскачането се случва точно
        // когато нещо не е наред и погледът трябва да отиде там.
        // Извън периода съобщението носи и двете граници и не се събира на един
        // ред при тесен екран. Ред повече се дава наготово, а не се мери:
        // мерене върху собствената височина би раздувало картата при всяко
        // ново показване.
        if (_dateHost is not null)
            _dateHost.Height = Theme.Scaled(outside ? 102 : 74) + (outside ? Theme.Small.Height : 0);

        if (_workspace.Periods.IsClosed(_companyId, _stagenum, month))
        {
            _when.Text = Lang.T($"{month.Title()} е затворен и не приема записи.",
                                $"{month.Title()} is closed and accepts no entries.");
            _when.ForeColor = Theme.Warn;

            return;
        }

        // Затвореният месец е по-силното от двете и се казва пръв: него никаква
        // отметка не отваря.
        if (outside)
        {
            _when.Text = _outside.Checked
                ? Lang.T($"Извън работния период: {_period.Bounds} — записва се въпреки това.",
                         $"Outside the working period: {_period.Bounds} — saved anyway.")
                : _period.Miss(Date);

            _when.ForeColor = Theme.Warn;

            return;
        }

        _when.Text = Lang.T($"Записва се в {month.Title()}.  ·  F4 отваря календара",
                            $"Saved into {month.Title()}.  ·  F4 opens the calendar");

        _when.ForeColor = Theme.InkMuted;
    }

    /// <summary>
    /// Изходният код: същият или един общ за цялото предаване.
    ///
    /// По подразбиране всеки код си остава своят и питането е излишно —
    /// предаването не преработва нищо. Случва се обаче: отпадък, заведен при нас
    /// под един код, се приема от оползотворителя под друг, и в книгата трябва
    /// да стои неговият. Рядко, затова отметката е вдигната и менюто е угасено,
    /// докато някой не го поиска.
    ///
    /// Складът и в двата случая се вади по кода, под който количеството СТОИ —
    /// иначе наличността не намалява там, откъдето то наистина си отива.
    ///
    /// Кутията е буквално тази от третирането: там същият въпрос вече е решен и
    /// човекът, който я е виждал, не бива да я заварва другояче.
    /// </summary>
    private Control BuildOutCode()
    {
        // Широка колкото едно поле, а не две. С договора полетата станаха
        // единайсет, по четири на ред на лаптоп: двойна кутия не се събираше до
        // третия ред, падаше на четвърти и там рамката ѝ отрязваше менюто —
        // надписът се виждаше, а полето под него не. Менюто само е широко колкото
        // преди: името на кода се чете там, където се избира.
        var host = new Panel
        {
            Width = Theme.Scaled(280),
            Height = Theme.Scaled(80),
            BackColor = Theme.Canvas,
            Margin = new Padding(0, 0, Theme.Gap, Theme.GapSmall)
        };

        _outCode.Dock = DockStyle.Top;
        _outCode.DropDownStyle = ComboBoxStyle.DropDown;
        _outCode.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _outCode.AutoCompleteSource = AutoCompleteSource.ListItems;
        _outCode.Width = Theme.Scaled(280);
        _outCode.DropDownWidth = Theme.Scaled(576);
        _outCode.TextChanged += (_, _) => Weigh();
        Theme.StyleInput(_outCode);

        _sameCode.Dock = DockStyle.Top;
        _sameCode.Height = Theme.Base.Height + 12;
        _sameCode.Text = Lang.T("Използвай същия код", "Use the same code");
        _sameCode.Checked = true;
        _sameCode.ForeColor = Theme.Ink;
        _sameCode.BackColor = Color.Transparent;
        _sameCode.CheckedChanged += (_, _) => ApplyLock();

        // Закачените отгоре се редят в обратен ред на добавяне: последният
        // добавен застава най-горе.
        host.Controls.Add(_outCode);
        host.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            Height = Theme.Small.Height + 6,
            Text = Lang.T("Общ код за всички редове", "One code for every row"),
            Font = Theme.SmallBold,
            ForeColor = Theme.InkFaint
        });
        host.Controls.Add(_sameCode);

        return host;
    }

    /// <summary>Отметката заключва менюто: с нея кодът идва от самия ред.</summary>
    private void ApplyLock()
    {
        _outCode.Enabled = !_sameCode.Checked;

        if (_sameCode.Checked) _outCode.Text = string.Empty;

        Weigh();
    }

    /// <summary>Менюто на изходния код — от схемата, стеснено от кратките списъци.</summary>
    private void FillOutCodes()
    {
        if (_node?.Attributes.FirstOrDefault(c => c.Name == "ewc") is not { } column) return;

        var limits = _workspace.Lists.LimitsFor(_companyId, "book");

        foreach (var option in limits.Offer(BookNodes.Report, Moves.Node(MoveKind.Submitted), column))
            _outCode.Items.Add(option.Display);
    }

    /// <summary>Общият изходен код, ако е избран такъв.</summary>
    private string OutCode() => _sameCode.Checked ? string.Empty : Code(_outCode.Text);

    /// <summary>
    /// Кантарът: колко е предадено ОБЩО.
    ///
    /// Стои под датата, над кодовете — там, където е и сборът по кодове:
    /// разминаването се хваща само ако двете числа са едно до друго, а дясната
    /// височина остава за партидите.
    ///
    /// Полето е по избор — не всяко предаване минава през кантар. Обяви ли се
    /// обаче, то е по-вярно от сметката назад: то е измерено, а избраното е
    /// сглобено. Затова записът спира, докато двете не съвпаднат — точно това
    /// сверяване досега се е правело на ръка, и точно него програмата поема.
    /// </summary>
    private Control BuildTarget()
    {
        var host = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(172),
            BackColor = Theme.Canvas,
            Padding = new Padding(0, 0, 0, Theme.GapSmall)
        };

        // Ивицата отляво носи състоянието: сива, докато не е заявено нищо,
        // зелена при съвпадение, оранжева при разминаване. Тя е онова, което се
        // хваща с крайчеца на окото, докато погледът е в таблицата.
        var card = new Card
        {
            Dock = DockStyle.Fill,
            Fill = Theme.Surface,
            Outline = Theme.Border,
            Stripe = Theme.BorderSoft,
            Padding = new Padding(Theme.Gap, 6, Theme.Gap, 6)
        };

        _scale = card;

        var line = new Panel { Dock = DockStyle.Top, Height = Theme.Scaled(34), BackColor = Theme.Surface };

        _target.Dock = DockStyle.Left;
        _target.Width = Theme.Scaled(136);
        _target.Font = Theme.Title;
        _target.TextAlign = HorizontalAlignment.Right;
        Theme.StyleInput(_target);
        _target.TextChanged += (_, _) => Weigh();

        var caption = new Label
        {
            Dock = DockStyle.Left,
            Width = Theme.Scaled(150),
            Text = TargetTitle,
            Font = Theme.Bold,
            ForeColor = Theme.InkStrong,
            BackColor = Theme.Surface,
            TextAlign = ContentAlignment.MiddleLeft
        };

        // Закачените отляво се редят в обратен ред на добавяне.
        line.Controls.Add(_target);
        line.Controls.Add(caption);

        _gap.Dock = DockStyle.Top;
        _gap.Height = Theme.Scaled(22);
        _gap.Font = Theme.SmallBold;
        _gap.ForeColor = Theme.InkMuted;
        _gap.BackColor = Theme.Surface;
        _gap.TextAlign = ContentAlignment.MiddleLeft;

        // Копчетата казват какво ще стане ПРЕДИ да са натиснати: надписът до тях
        // назовава реда, който ще поеме разликата. Изравняване, което не се
        // вижда къде е седнало, е точно същото като сметка наум — само че
        // направена от програмата и скрита от човека.
        var tools = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(34),
            BackColor = Theme.Surface
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Surface
        };

        var level = Theme.SecondaryButton(Lang.T("Изравни", "Balance"));
        level.Height = Theme.Scaled(30);
        level.Margin = new Padding(0, 0, Theme.GapSmall, 0);
        level.Click += (_, _) => Level();
        _level = level;

        new ToolTip().SetToolTip(level, Lang.T(
            "Слага разликата до заявеното в един ред — този, посочен в менюто вдясно, или " +
            "този, на който стоите. Както тя ляга в насипната купчина, от която е догребвано.",
            "Puts the difference from the stated total into a single row — the one picked in the " +
            "menu, or the one you are standing on. The way it lands in the loose pile the load " +
            "was topped up from."));

        var spread = Theme.GhostButton(Lang.T("Пропорционално", "Proportionally"));
        spread.Height = Theme.Scaled(30);
        spread.Margin = new Padding(0, 0, Theme.GapSmall, 0);
        spread.Click += (_, _) => Proportional();
        _spread = spread;

        new ToolTip().SetToolTip(spread, Lang.T(
            "Разнася разликата по всички избрани редове, в тяхното съотношение — " +
            "когато няма един ред, който да е виновен.",
            "Spreads the difference across every picked row in their own proportion — " +
            "when no single row is to blame."));

        buttons.Controls.Add(level.FitWidth());
        buttons.Controls.Add(spread.FitWidth());

        // Менюто стои в рамката на кантара, а не над таблицата: то е част от
        // изравняването, а не от избора. И на СОБСТВЕН ред, а не в остатъка до
        // копчетата.
        //
        // До тях мястото зависи от ширината на рамката, а тя се свива до
        // няколкостотин пиксела на лаптоп при мащаб 150 %. Меню, в което не се
        // побира „080114 · 15,000 т“, не е меню; ред отгоре струва един ред от
        // таблицата и се плаща веднъж.
        var pick = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(34),
            BackColor = Theme.Surface,
            Padding = new Padding(0, 0, 0, Theme.GapSmall)
        };

        _into.DropDownStyle = ComboBoxStyle.DropDownList;
        _into.Dock = DockStyle.Fill;
        _into.SelectedIndexChanged += (_, _) => Pin();
        Theme.StyleInput(_into);

        var intoCaption = new Label
        {
            Dock = DockStyle.Left,
            Width = Theme.Scaled(130),
            Text = IntoTitle,
            Font = Theme.Small,
            ForeColor = Theme.InkMuted,
            BackColor = Theme.Surface,
            TextAlign = ContentAlignment.MiddleLeft
        };

        // Пълнещият влиза пръв (виж АРХИТЕКТУРА.md).
        pick.Controls.Add(_into);
        pick.Controls.Add(intoCaption);

        new ToolTip().SetToolTip(_into, Lang.T(
            "Кой ред да поеме разликата. Посочването тук НЕ пипа избора в таблиците — " +
            "затова го има: щракване по ред долу изважда останалите от предаването.",
            "Which row is to take the difference. Picking here does NOT touch the selection in " +
            "the tables — that is why it exists: clicking a row below drops the others."));

        tools.Controls.Add(buttons);

        // Редът за поемащия ред е СОБСТВЕН, а не остатъкът до копчетата: той е
        // цяло изречение („2,400 т ще слязат от 191202 · #2026-0003“), а до
        // двете копчета в най-тясната рамка остават сто и няколко пиксела.
        // Отрязано точно там, където обяснява, то не обяснява нищо.
        _anchor.Dock = DockStyle.Top;
        _anchor.Height = Theme.Scaled(20);
        _anchor.AutoSize = false;
        _anchor.Font = Theme.Small;
        _anchor.ForeColor = Theme.InkMuted;
        _anchor.BackColor = Theme.Surface;
        _anchor.TextAlign = ContentAlignment.MiddleLeft;

        // Закачените отгоре се редят в обратен ред на добавяне: последният
        // добавен застава най-горе.
        // Редът на четене е редът на работата: колко се разминава, къде да
        // седне разликата, копчето, и какво ще стане, ако се натисне.
        card.Controls.Add(_anchor);
        card.Controls.Add(tools);
        card.Controls.Add(pick);
        card.Controls.Add(_gap);
        card.Controls.Add(line);

        host.Controls.Add(card);

        return host;
    }

    private static string TargetTitle => Lang.T("Предадено общо, т", "Handed over in total, t");

    /// <summary>Заглавието на менюто за поемащия ред.</summary>
    private static string IntoTitle => Lang.T("Разликата поема", "Difference goes to");

    /// <summary>Първият ред на менюто: старото поведение — курсорът.</summary>
    private static string HandText => Lang.T("Редът, на който стоя", "The row I am standing on");

    /// <summary>Заявеното общо количество. Нула значи „не е казано“.</summary>
    private decimal Target =>
        SplitMath.TryRead(_target.Text, out var amount) && amount > 0 ? SplitMath.Round(amount) : 0m;

    /// <summary>
    /// Колоните на възела без трите, които складът води сам.
    ///
    /// Датата, кодът и количеството са самото движение и имат свое място;
    /// площадката идва от страницата. Останалото е на схемата.
    /// </summary>
    private IEnumerable<ColumnDef> Extras() =>
        _node?.Attributes.Where(c => !BookNodes.Owned.Contains(c.Name)) ?? [];

    private static string Caption(ColumnDef column) => ReportTitles.ColumnShort(column);

    /// <summary>Името на получателя по схемата — полето, което се научава.</summary>
    private const string ReceiverName = "name_receiver";

    /// <summary>ЕИК-ът на получателя — попълва се сам, щом името е познато.</summary>
    private const string ReceiverEik = "pin_receiver";

    private Control Input(ColumnDef column)
    {
        Control input;

        // Получателят е СВОБОДЕН текст по схемата и такъв остава: НИСО не дава
        // номенклатура и програмата не бива да измисля една. Но същото поле се
        // пише на ръка по трийсет пъти месечно и ражда „Претопи ООД“, „Претопи
        // OOD“ и „претопи оод“ — трима получателя вместо един.
        //
        // Затова полето ПРЕДЛАГА вече писаното, без да ограничава: непознато
        // име минава и се научава при записа (правило 13).
        if (column.Name == ReceiverName)
        {
            var who = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDown,
                AutoCompleteMode = AutoCompleteMode.SuggestAppend,
                AutoCompleteSource = AutoCompleteSource.ListItems
            };

            FillReceivers(who);

            // Изборът носи и ЕИК-а: той се пише веднъж, при опознаването, и
            // после не се преписва — точно там се раждат разминаванията.
            who.SelectedIndexChanged += (_, _) => Received(who);
            who.Leave += (_, _) => Received(who);

            _inputs[column.Name] = who;
            return who;
        }

        if (column.Domain.IsEnum && !column.Domain.IsLargeEnum)
        {
            var combo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDown,
                AutoCompleteMode = AutoCompleteMode.SuggestAppend,
                AutoCompleteSource = AutoCompleteSource.ListItems
            };

            // Описанието на дейността е самият текст на дейността. Схемата иска
            // и двете, но те казват едно и също — второто се пише само, както
            // и при пренасянето в отчета.
            if (column.Name == "op_id_code") combo.SelectedIndexChanged += (_, _) => Describe(combo);

            BookLimits.Offer(
                combo, BookLimits.Of(_workspace, _companyId), MoveKind.Submitted, column);

            input = combo;
        }
        else
        {
            input = new TextBox();
        }

        _inputs[column.Name] = input;
        return input;
    }

    /// <summary>Запомнените получатели на тази фирма, най-ползваните отгоре.</summary>
    private void FillReceivers(ComboBox who)
    {
        who.Items.Clear();

        foreach (var partner in _workspace.Partners.List(_companyId))
            who.Items.Add(partner.Display);
    }

    /// <summary>
    /// Запомнените договори на тази фирма, най-ползваните отгоре.
    ///
    /// Написаното в полето оцелява: менюто се пълни наново и след „Предай и
    /// продължи“, за да е новият договор под ръка за следващия курс.
    /// </summary>
    private void FillContracts()
    {
        var typed = _contract.Text;

        _contract.Items.Clear();

        foreach (var contract in _workspace.Partners.List(_companyId, PartnerRole.Contract))
            _contract.Items.Add(contract.Name);

        _contract.Text = typed;
    }

    /// <summary>
    /// Разделя избраното на име и ЕИК.
    ///
    /// Менюто показва „Претопи ООД — 205566778“, защото двата номера си приличат
    /// и името само не различава две дружества. В полето обаче остава само
    /// името: в книгата то отива така, както НИСО го иска.
    /// </summary>
    private void Received(ComboBox who)
    {
        if (_filling) return;

        var name = PartnerStore.NameOf(who.Text);
        if (name.Length == 0) return;

        _filling = true;

        try
        {
            who.Text = name;
        }
        finally
        {
            _filling = false;
        }

        if (!_inputs.TryGetValue(ReceiverEik, out var target)) return;

        var eik = _workspace.Partners.EikOf(_companyId, name);
        if (eik.Length == 0) return;

        // Написаното от човек не се затрива — пипа се само празното поле.
        if (target.Text.Trim().Length == 0) target.Text = eik;
    }

    /// <summary>
    /// Дописва това, което следва вече попълненото.
    ///
    /// Повиква се веднъж, след като всички полета са налице. Безопасно е и при
    /// празна дейност — тогава няма какво да се опише и нищо не се пипа.
    /// </summary>
    private void Settle()
    {
        if (_inputs.TryGetValue("op_id_code", out var input) && input is ComboBox combo)
            Describe(combo);
    }

    /// <summary>Попълва описанието на дейността по избраната дейност.</summary>
    private void Describe(ComboBox operation)
    {
        if (_filling) return;
        if (!_inputs.TryGetValue("op_descr", out var target)) return;

        var chosen = operation.Text.Trim();
        var dash = chosen.IndexOf(" — ", StringComparison.Ordinal);
        var description = dash > 0 ? chosen[(dash + 3)..].Trim() : string.Empty;

        if (description.Length == 0) return;

        // Написаното от човек не се затрива: пипа се само празното поле и това,
        // което самият прозорец е попълнил преди малко.
        if (target.Text.Trim().Length > 0 && target.Text.Trim() != _written) return;

        target.Text = description;
        _written = description;
    }

    private Control BuildRows()
    {
        var host = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, 0, Theme.GapLarge, Theme.Gap)
        };

        var head = new Panel { Dock = DockStyle.Top, Height = Theme.Scaled(30), BackColor = Theme.Canvas };

        // Обединяването мени само колко реда вижда НИСО. Произходът е един и
        // същ и в двата случая — той стои във връзките, не в реда.
        _perBatch.Dock = DockStyle.Right;
        _perBatch.Width = Theme.Scaled(280);
        _perBatch.Text = Lang.T("Отделен ред за всяка партида", "A separate row for every batch");
        _perBatch.ForeColor = Theme.Ink;
        _perBatch.BackColor = Color.Transparent;
        _perBatch.CheckedChanged += (_, _) => Weigh();

        head.Controls.Add(_perBatch);

        _what.Dock = DockStyle.Fill;
        _what.Text = WhatTitle;
        _what.Font = Theme.SmallBold;
        _what.ForeColor = Theme.InkFaint;
        head.Controls.Add(_what);

        // Два реда под таблицата: сборът и какво ще излезе от него. Сборът е
        // по-едър — него човек гледа, докато пише.
        _split.Dock = DockStyle.Bottom;
        _split.Height = Theme.Scaled(24);
        _split.Font = Theme.Small;
        _split.ForeColor = Theme.InkMuted;

        _totals.Dock = DockStyle.Fill;
        _totals.Font = Theme.Bold;
        _totals.ForeColor = Theme.Ink;
        _totals.TextAlign = ContentAlignment.MiddleLeft;

        // Сборът на маркираното стои до общия, отдясно: двете се четат заедно —
        // „това е всичко, а това е онова, което съм отбелязал“.
        _picked.Dock = DockStyle.Right;
        _picked.Width = Theme.Scaled(420);
        _picked.Font = Theme.Bold;
        _picked.ForeColor = Theme.InkFaint;
        _picked.TextAlign = ContentAlignment.MiddleRight;

        Dress(_rows);

        // Влаченето по редовете е самият избор: „тези ги предавам“. Затова
        // таблицата е с избор по цял ред и с много избрани, а не с една клетка.
        _rows.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _rows.MultiSelect = true;

        // Заглавията подреждат. Таблиците ги пълним ние, затова колоните са
        // „програмни“: самата таблица не подрежда нищо, но рисува стрелката,
        // когато ѝ се каже.
        //
        // Десният бутон пресява — както в редактора на отчета.
        _rows.ColumnHeaderMouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Right) _rowsFilter.Open(e.ColumnIndex);
            else SortRows(e.ColumnIndex);
        };

        _rowsFilter.Changed += () =>
        {
            ShowFilters();
            ShowPicked();
        };

        _rows.Columns.Add(Plain(CodeColumn, Lang.T("Код", "Code"), 300, fill: true));
        _rows.Columns.Add(Number(BatchColumn, Lang.T("Партиди", "Batches"), 84, readOnly: true));
        _rows.Columns.Add(Plain(AgeColumn, Lang.T("Най-стара", "Oldest"), 98));
        _rows.Columns.Add(Number(StockColumn, Lang.T("Налично, т", "Available, t"), 108, readOnly: true));
        // Надписът остава кратък: че в колоната се пише, го казват видът ѝ —
        // плътен фон и получер текст — и редът над таблиците. Дълго заглавие се
        // реже точно там, където обяснява.
        _rows.Columns.Add(Number(TakeColumn, Lang.T("За предаване, т", "To hand over, t"), 142, readOnly: false));

        // Писането започва от първата цифра, без влизане в клетката: човек, който
        // иска двайсет от двеста, набира „20“ и натиска Enter.
        _rows.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;

        // Щракването в клетката за количество сваля избора на останалите редове
        // — така прави всяка таблица на Windows. Тук това би значело „извадих ги
        // от предаването“, каквото човек не е искал: той е посегнал да напише
        // число. Затова изборът се запомня преди писането и се връща след него.
        _rows.CellBeginEdit += (_, _) =>
            _held = [.. _rows.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index)];

        _rows.CellEndEdit += (_, _) =>
        {
            Remember();
            Weigh();
            Restore();
            ShowOrigin();
        };

        // Избраното Е изборът: предава се точно то и нищо друго.
        //
        // Дотук простото щракване само местеше погледа, а количествата оставаха
        // по всички редове — човек избираше един код, натискаше „Предай“ и
        // получаваше трите. Сега щракването изважда останалите.
        //
        // Прилага се при ПУСКАНЕ на бутона и на клавиша, а не при самата смяна
        // на избора: DataGridView мени избора и вдига събитията си в ред, който
        // не обещава нищо, а тук редът има значение.
        _rows.SelectionChanged += (_, _) =>
        {
            ShowOrigin();
            ShowPicked();
        };

        _rows.Enter += (_, _) => { _last = _rows; ShowPicked(); };
        _rows.CurrentCellChanged += (_, _) => Follow();

        _rows.CellMouseDown += (_, e) => _from = e.RowIndex;

        _rows.MouseUp += (_, e) =>
        {
            _from = -1;
            Clicked(Hit(e));
        };

        // Стрелките и Ctrl+A избират точно колкото и мишката.
        _rows.KeyUp += (_, e) =>
        {
            if (e.KeyCode is not (Keys.Up or Keys.Down or Keys.Home or Keys.End
                or Keys.PageUp or Keys.PageDown or Keys.Space or Keys.A)) return;

            if (_rows.IsCurrentCellInEditMode) return;

            Apply();
            ShowOrigin();
        };

        // Кой е донесъл това.
        //
        // Количеството се решава по код, но зад кода стоят хора: дванайсет тона
        // кабел са осем от Метал ООД и четири от Скрап ЕООД, приети с различни
        // протоколи. Досега това се виждаше само на екрана отзад — а въпросът
        // „чие взимам“ се задава точно тук, преди да се натисне копчето.
        Dress(_origin);

        // Партидите са избор, а не справка.
        //
        // Дотук долната таблица само показваше кого ще засегне FIFO. Но работата
        // е друга: избират се партиди — тази, онази и онази — и по колко от
        // всяка. Затова тук се избира с Ctrl, Shift и влачене, а количеството се
        // пише в самия ред.
        _origin.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _origin.MultiSelect = true;
        _origin.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;

        _origin.Columns.Add(Plain(OriginBatch, Lang.T("Партида", "Batch"), 104));
        // Двата текстови стълба си делят мястото, за да останат числата на екрана: на
        // лаптоп „Ще се вземе“ излизаше извън таблицата.
        _origin.Columns.Add(Plain(OriginPartner, Lang.T("Клиент", "Client"), 240, fill: true, least: 120));
        _origin.Columns.Add(Plain(OriginDocument, Lang.T("Приета на · документ", "Received · document"), 200, fill: true, least: 120));
        _origin.Columns.Add(Number(OriginStock, Lang.T("Налично, т", "Available, t"), 110, readOnly: true));
        _origin.Columns.Add(Number(OriginTake, Lang.T("Ще се вземе, т", "Will be taken, t"), 140, readOnly: false));

        // Пълненето на таблицата също вдига „смени се изборът“ — затова кой е
        // пипан последно се решава от ДЕЙСТВИЕ (щракване, клавиш, фокус), а не
        // оттук: иначе избран горе код се брои като маркирани долу партиди.
        _origin.SelectionChanged += (_, _) => ShowPicked();

        _origin.Enter += (_, _) => { _last = _origin; ShowPicked(); };
        _origin.CurrentCellChanged += (_, _) => Follow();

        _origin.ColumnHeaderMouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Right) _originFilter.Open(e.ColumnIndex);
            else SortBatches(e.ColumnIndex);
        };

        _originFilter.Changed += () =>
        {
            ShowFilters();
            ShowPicked();
        };

        _origin.CellEndEdit += (_, e) => TookFromBatch(e.RowIndex);

        _origin.CellBeginEdit += (_, _) =>
            _kept = [.. _origin.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index)];

        _origin.MouseUp += (_, e) =>
        {
            if (Column(_origin, e) == OriginTake) return;

            PickedBatches();
        };

        _origin.KeyUp += (_, e) =>
        {
            if (e.KeyCode is not (Keys.Up or Keys.Down or Keys.Home or Keys.End
                or Keys.PageUp or Keys.PageDown or Keys.Space or Keys.A)) return;

            if (_origin.IsCurrentCellInEditMode) return;

            PickedBatches();
        };

        _behind.Dock = DockStyle.Top;
        _behind.Height = Theme.Scaled(30);
        _behind.Font = Theme.SmallBold;
        _behind.ForeColor = Theme.InkFaint;

        var left = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas };
        left.Controls.Add(Frame(_rows));
        left.Controls.Add(head);

        // Закачените отгоре се редят в обратен ред на добавяне: датата е
        // добавена последна и застава най-горе, кантарът — под нея, а кодовете
        // под двете. Стоят вляво, за да остане цялата дясна височина за партидите.
        left.Controls.Add(BuildTarget());
        left.Controls.Add(BuildDate());

        var right = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.Gap, 0, 0, 0)
        };

        _fromTop.Requested += FromTop;

        // Закачените отгоре се редят наопаки: заглавието над реда „Вземи
        // отгоре“, той — над таблицата.
        right.Controls.Add(Frame(_origin));
        right.Controls.Add(_fromTop);
        right.Controls.Add(BatchListDialog.Header(_behind, Expand));

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = Theme.Scaled(6),
            BackColor = Theme.Canvas
        };

        split.Panel1.BackColor = Theme.Canvas;
        split.Panel2.BackColor = Theme.Canvas;

        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(right);

        var sums = new Panel { Dock = DockStyle.Bottom, Height = Theme.Scaled(28), BackColor = Theme.Canvas };

        // Пълнещият влиза пръв: закачените се редят наопаки и последният взима
        // своя край, а остатъкът остава за първия.
        sums.Controls.Add(_totals);
        sums.Controls.Add(_picked);

        host.Controls.Add(split);
        host.Controls.Add(_search.Row());
        host.Controls.Add(_split);
        host.Controls.Add(sums);

        // Разделителят се слага СЛЕД сглобяването и в границите си: при по-тесен
        // прозорец от двата минимума WinForms хвърля, вместо да се пригоди.
        Split(split, 440);

        return host;
    }

    /// <summary>Рамка около таблица — същата, с която са рамкирани и другите.</summary>
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

    /// <summary>Общият вид на двете таблици — един и същ, за да четат еднакво.</summary>
    private static void Dress(DataGridView grid)
    {
        // Копира се като в Excel: Ctrl+C взима избраното със заглавията, за да
        // има какво да се залепи обратно в лист.
        grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText;

        grid.Dock = DockStyle.Fill;
        grid.Font = Theme.Base;
        grid.BackgroundColor = Theme.Canvas;
        grid.BorderStyle = BorderStyle.None;
        grid.EnableHeadersVisualStyles = false;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.AllowUserToOrderColumns = false;
        grid.RowHeadersVisible = false;
        grid.MultiSelect = false;
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
        // Избраното е в пълния цвят, а не в бледия: по него се вижда кой код е
        // отворен вдясно и кои партиди са маркирани — бледосиньото се губеше
        // сред редуващите се ивици.
        grid.DefaultCellStyle.SelectionBackColor = Theme.Accent;
        grid.DefaultCellStyle.SelectionForeColor = Theme.OnAccent;
        grid.DefaultCellStyle.Padding = new Padding(6, 4, 6, 4);
        grid.AlternatingRowsDefaultCellStyle.BackColor = Theme.Surface;
        grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = Theme.Accent;
        grid.AlternatingRowsDefaultCellStyle.SelectionForeColor = Theme.OnAccent;
        grid.GridColor = Theme.Border;

        var line = Math.Max(1, grid.Font.Height);
        grid.RowTemplate.Height = line + 12;
        // Два реда: под името на колоната стои сборът ѝ. С „line + 20“
        // вторият ред — самият сбор — не се виждаше при никой мащаб.
        grid.ColumnHeadersHeight = 2 * line + 18;
    }

    /// <summary>
    /// Показва кой е донесъл количеството зад избрания код.
    ///
    /// „Ще се вземе“ е самото разпределение, което записът ще направи — не
    /// предположение: пита се същото <see cref="Handover.Spread"/>, което после
    /// сглобява движенията. Затова редът, който човек вижда тук, е редът, по
    /// който партидите наистина ще се изчерпят.
    /// </summary>
    private void ShowOrigin()
    {
        if (IsDisposed) return;

        var was = _filling;
        _filling = true;

        try
        {
            _origin.Rows.Clear();
        }
        finally
        {
            _filling = was;
        }

        if (_rows.CurrentRow?.Tag is not TreatPile pile)
        {
            _behind.Text = Lang.T("Изберете код, за да видите кой го е донесъл.",
                                  "Pick a code to see who brought it.");
            return;
        }

        // Наведнъж, а не ред по ред: зад един код в „Получен“ стоят стотици
        // партиди, а таблицата се пълни наново при всяко щракване по код.
        // Номерата на стълбовете се вземат веднъж: ред, още невързан за
        // таблицата, не познава клетките си по име.
        int At(string column) => _origin.Columns[column]!.Index;

        var (batchAt, partnerAt, documentAt, stockAt) =
            (At(OriginBatch), At(OriginPartner), At(OriginDocument), At(OriginStock));

        var carried = Lang.T(" · заварено", " · carried in");
        var built = new List<DataGridViewRow>();

        foreach (var share in Sorted(pile.Shares))
        {
            var row = (DataGridViewRow)_origin.RowTemplate.Clone();

            row.CreateCells(_origin);

            row.Cells[batchAt].Value = "#" + share.Batch.Code;
            row.Cells[partnerAt].Value = Partner(share.Batch);

            var document = share.Batch.Document.Length > 0
                ? $"{share.Batch.ReceivedOn.ToString(ValueConverter.BulgarianDateFormat)} · {share.Batch.Document}"
                : share.Batch.ReceivedOn.ToString(ValueConverter.BulgarianDateFormat);

            row.Cells[documentAt].Value = share.Batch.Opening ? document + carried : document;

            row.Cells[stockAt].Value = SplitMath.Amount(share.Amount);

            row.Tag = share;

            built.Add(row);
        }

        _origin.Rows.AddRange([.. built]);

        // Взетото по партиди се чете от посоченото, а не се смята наново: точно
        // това пазеше избора на човека от FIFO.
        ShowTaken();

        // Пресяването е по стойности — и по „Ще се вземе“ — затова след тях.
        _originFilter.Apply();

        Glyph(_origin, _originSort, _originDown);

        var clients = pile.Shares
            .Select(x => x.Batch.Eik)
            .Distinct(StringComparer.Ordinal)
            .Count();

        _behind.Text = Lang.T(
            $"Зад {Describe(pile.Ewc)} — {pile.Batches} партиди от {clients} клиента · " +
            $"{SplitMath.Amount(pile.Available)} т",
            $"Behind {Describe(pile.Ewc)} — {pile.Batches} batches from {clients} clients · " +
            $"{SplitMath.Amount(pile.Available)} t");

        if (_originFilter.Active) _behind.Text += " · " + _originFilter.Note();

        ShowFromTop();
    }

    private static string WhatTitle => Lang.T("Какво се предава", "What is being handed over");

    /// <summary>Казва над таблиците какво е пресято — скритото иначе се забравя.</summary>
    /// <summary>
    /// Търсенето е сменено: пресяват се кодовете, а с тях и партидите на
    /// текущия. Изборът не се пипа — скритото остава в него.
    /// </summary>
    private void Searched()
    {
        _rowsFilter.Apply();
        ShowFilters();
        ShowPicked();
    }

    private void ShowFilters()
    {
        _what.Text = _rowsFilter.Active ? $"{WhatTitle} · {_rowsFilter.Note()}" : WhatTitle;
        _what.ForeColor = _rowsFilter.Active ? Theme.Accent : Theme.InkFaint;

        ShowOrigin();
    }

    /// <summary>Клиентът, както се чете на един ред.</summary>
    private static string Partner(Batch batch) =>
        batch.PartnerName.Length > 0
            ? batch.Partner
            : batch.Eik.Length > 0 ? batch.Eik : Lang.T("(клиент не е посочен)", "(no client stated)");

    private static DataGridViewTextBoxColumn Plain(
        string name, string header, int width, bool fill = false, int least = 150)
    {
        var column = new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,

            // Ширината е мерена при 100 %: при 125 % и 150 % буквите в стълба
            // растат и същото число почва да ги реже.
            Width = Theme.Scaled(width),
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.Programmatic
        };

        if (fill)
        {
            column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            column.FillWeight = width;
            column.MinimumWidth = Theme.Scaled(least);
        }

        return column;
    }

    private static DataGridViewTextBoxColumn Number(string name, string header, int width, bool readOnly)
    {
        var column = new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,

            // Ширината е мерена при 100 %: при 125 % и 150 % буквите в стълба
            // растат и същото число почва да ги реже.
            Width = Theme.Scaled(width),
            ReadOnly = readOnly,
            SortMode = DataGridViewColumnSortMode.Programmatic
        };

        column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;

        // Клетката, в която се пише, изглежда като поле, а не като число.
        //
        // Рядко се предава цяла купчина: двестате тона излизат с по двайсет на
        // курс. Да се намали количеството обаче беше най-скритото действие в
        // прозорчето — колоната изглеждаше точно като съседната, която не се
        // пипа, и единственият знак беше, че курсорът влиза в нея.
        if (!readOnly)
        {
            column.DefaultCellStyle.BackColor = Theme.Surface;
            column.DefaultCellStyle.Font = Theme.Bold;
        }

        return column;
    }

    private Control BuildButtons()
    {
        var buttonHeight = Theme.Scaled(40);

        var bar = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = buttonHeight + 2 * Theme.Gap,
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

        var save = Theme.PrimaryButton(Lang.T("Предай и затвори", "Hand over and close"));
        save.Height = buttonHeight;
        save.Margin = Padding.Empty;
        save.Click += (_, _) => Accept();
        _save = save;

        // Второто копче е заради работата, която не е една: избрани са десет
        // получателя, предава се на три дати, и досега всяка дата значеше ново
        // отваряне на прозорчето с ново избиране отзад. Записва и остава — от
        // списъка изчезва само това, което вече си е отишло от склада.
        var more = Theme.SecondaryButton(Lang.T("Предай и продължи", "Hand over and continue"));
        more.Height = buttonHeight;
        more.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        more.Click += (_, _) => Continue();
        _more = more;

        var cancel = Theme.GhostButton(Lang.T("Затвори", "Close"));
        cancel.Height = buttonHeight;
        cancel.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        cancel.Click += (_, _) => Leave();

        tools.Controls.Add(save.FitWidth());
        tools.Controls.Add(more.FitWidth());
        tools.Controls.Add(cancel.FitWidth());

        // „Всичко“ и „Нищо“ стоят отляво, далеч от копчето, което записва: те
        // пипат таблицата, а при двайсет избрани реда се ползват често.
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

        // Частичното предаване изнася избраното в отделен прозорец.
        //
        // В голямата таблица писането по седемдесет реда е писане в движеща се
        // цел: щракваш в клетка, превърташ, губиш реда. Изнесени настрани,
        // шестте избрани се попълват, без нищо друго да се вижда и без нищо
        // друго да може да се пипне.
        var partial = Theme.GhostButton(Lang.T("Частично предаване…", "Partial hand-over…"));
        partial.Height = Theme.Scaled(40);
        partial.Margin = new Padding(Theme.Gap, 0, Theme.GapSmall, 0);
        partial.Click += (_, _) => Partial();

        quick.Controls.Add(partial.FitWidth());

        _done.AutoSize = false;
        _done.Width = Theme.Scaled(360);
        _done.Height = Theme.Scaled(40);
        _done.TextAlign = ContentAlignment.MiddleLeft;
        _done.Font = Theme.SmallBold;
        _done.ForeColor = Theme.Ok;
        _done.Margin = new Padding(Theme.Gap, 0, 0, 0);

        quick.Controls.Add(_done);

        bar.Controls.Add(tools);
        bar.Controls.Add(quick);

        CancelButton = cancel;

        return bar;
    }

    /// <summary>
    /// Какво прави едно щракване по ред.
    ///
    /// Отделен метод, а не тяло на събитието, защото през него минава и тестът:
    /// кука, която прави нещо друго, зелено показва работещо прозорче, докато на
    /// екрана то не работи. Веднъж се случи точно това.
    ///
    /// Щракване в колоната за количество не е избор, а писане: то не бива да
    /// изважда останалите редове изпод ръката на човека.
    /// </summary>
    private void Clicked(string column)
    {
        _last = _rows;

        if (column == TakeColumn) return;

        Apply();
        ShowOrigin();
    }

    /// <summary>
    /// Връща избора отпреди писането.
    ///
    /// Само когато е бил повече от един ред: при един няма какво да се поправи,
    /// а излишното пипане по избора мига на екрана.
    /// </summary>
    private void Restore() => Restore(_rows, ref _held);

    private void Restore(DataGridView grid, ref List<int> held)
    {
        if (held.Count <= 1)
        {
            held = [];
            return;
        }

        _filling = true;

        try
        {
            grid.ClearSelection();

            foreach (var index in held)
                if (index >= 0 && index < grid.Rows.Count)
                    grid.Rows[index].Selected = true;
        }
        finally
        {
            _filling = false;
        }

        held = [];
    }

    /// <summary>
    /// Подрежда горната таблица по щракнатото заглавие.
    ///
    /// Числата тръгват от голямото надолу, думите — от началото на азбуката:
    /// човек, който натиска „Налично“, пита кое е най-многото, а натиснал
    /// „Код“ — къде е този код.
    /// </summary>
    private void SortRows(int column)
    {
        if (column < 0 || column >= _rows.Columns.Count) return;

        var name = _rows.Columns[column].Name;

        _rowsDown = _rowsSort == name ? !_rowsDown : Numeric(name);
        _rowsSort = name;

        _filling = true;

        try
        {
            Lay(everything: false);
        }
        finally
        {
            _filling = false;
        }

        Weigh();
        ShowOrigin();
    }

    /// <summary>Подрежда долната таблица по щракнатото заглавие.</summary>
    private void SortBatches(int column)
    {
        if (column < 0 || column >= _origin.Columns.Count) return;

        var name = _origin.Columns[column].Name;

        _originDown = _originSort == name ? !_originDown : Numeric(name);
        _originSort = name;

        ShowOrigin();
    }

    /// <summary>Числовите колони тръгват от голямото — там въпросът е „кое е най-“.</summary>
    private static bool Numeric(string column) =>
        column is StockColumn or TakeColumn or BatchColumn or OriginStock or OriginTake;

    /// <summary>Рисува стрелката на подредената колона.</summary>
    private static void Glyph(DataGridView grid, string column, bool down)
    {
        foreach (DataGridViewColumn each in grid.Columns)
            each.HeaderCell.SortGlyphDirection = each.Name == column
                ? down ? SortOrder.Descending : SortOrder.Ascending
                : SortOrder.None;
    }

    /// <summary>Купчините в реда, по който се показват.</summary>
    private IEnumerable<TreatPile> Sorted(IEnumerable<TreatPile> piles)
    {
        var order = _rowsSort switch
        {
            BatchColumn => piles.OrderBy(p => p.Batches),
            AgeColumn => piles.OrderBy(p => p.Oldest),
            StockColumn => piles.OrderBy(p => p.Available),
            TakeColumn => piles.OrderBy(Taken),
            _ => piles.OrderBy(p => p.Ewc, StringComparer.Ordinal)
        };

        return _rowsDown ? order.Reverse() : order;
    }

    /// <summary>Партидите в реда, по който се показват.</summary>
    private IEnumerable<StockShare> Sorted(IEnumerable<StockShare> shares)
    {
        // Посоченото отгоре, останалото по реда си: обърнат, той би разбъркал точно
        // партидите, между които се търси вярната.
        if (_originSort == OriginTake)
        {
            decimal Picked(StockShare s) => _picks.GetValueOrDefault((Key(s), s.Batch.Id));

            return _originDown ? shares.OrderByDescending(Picked) : shares.OrderBy(Picked);
        }

        var order = _originSort switch
        {
            OriginBatch => shares.OrderBy(s => s.Batch.Code, StringComparer.Ordinal),
            OriginPartner => shares.OrderBy(s => s.Batch.PartnerName, StringComparer.CurrentCulture),
            OriginStock => shares.OrderBy(s => s.Amount),
            _ => shares.OrderBy(s => s.Batch.ReceivedOn).ThenBy(s => s.Batch.Id)
        };

        return _originDown ? order.Reverse() : order;
    }

    /// <summary>В коя колона е щракнато — празно, ако е извън редовете.</summary>
    private string Hit(MouseEventArgs e) => Column(_rows, e);

    private static string Column(DataGridView grid, MouseEventArgs e)
    {
        var at = grid.HitTest(e.X, e.Y).ColumnIndex;

        return at >= 0 && at < grid.Columns.Count ? grid.Columns[at].Name : string.Empty;
    }

    /// <summary>
    /// Избраните долу партиди стават това, което се взима от тях.
    ///
    /// Същото правило, както горе: избраното Е изборът. Избрана партида влиза с
    /// каквото има — или с написаното, ако човек вече е писал по нея — а
    /// неизбраната излиза от предаването.
    /// </summary>
    private void PickedBatches()
    {
        if (_filling) return;
        if (_rows.CurrentRow?.Tag is not TreatPile pile) return;

        _last = _origin;

        foreach (DataGridViewRow row in _origin.Rows)
        {
            if (row.Tag is not StockShare share) continue;

            // Скритата от пресяването партида не може да се щракне — и не бива
            // да излиза от предаването заради щракване по видимите.
            if (!row.Visible) continue;

            var key = (Key(share), share.Batch.Id);

            if (!row.Selected)
            {
                _picks.Remove(key);
                continue;
            }

            if (!_picks.TryGetValue(key, out var taken) || taken <= 0)
                _picks[key] = share.Amount;
        }

        Took(pile);
    }

    /// <summary>Написаното по един ред от долната таблица.</summary>
    private void TookFromBatch(int index)
    {
        if (_filling) return;
        if (index < 0 || index >= _origin.Rows.Count) return;

        _last = _origin;
        if (_origin.Rows[index].Tag is not StockShare share) return;
        if (_rows.CurrentRow?.Tag is not TreatPile pile) return;

        var text = _origin.Rows[index].Cells[OriginTake].Value?.ToString();
        var key = (Key(share), share.Batch.Id);

        if (!SplitMath.TryRead(text, out var amount) || amount <= 0)
        {
            _picks.Remove(key);
        }
        else
        {
            // Повече от стоялото не се взима: партидата няма толкова.
            _picks[key] = Math.Min(SplitMath.Round(amount), share.Amount);
        }

        Restore(_origin, ref _kept);
        Took(pile);
    }

    /// <summary>
    /// Пренася избраното по партиди нагоре — в реда на кода и в сборовете.
    ///
    /// Горната таблица е сборът на долната: код, от който не се взима нищо,
    /// излиза от избора сам, а такъв, от който се взима, влиза.
    /// </summary>
    private void Took(TreatPile pile)
    {
        _filling = true;

        try
        {
            var take = Taken(pile);

            _wanted[pile.Ewc] = take;
            Hold(pile);

            foreach (DataGridViewRow row in _rows.Rows)
            {
                if (row.Tag is not TreatPile mine || !string.Equals(mine.Ewc, pile.Ewc, StringComparison.Ordinal))
                    continue;

                row.Selected = take > 0;
                Mark(row, take);
            }
        }
        finally
        {
            _filling = false;
        }

        Weigh();
        ShowTaken();
    }

    /// <summary>Колко се взима от една купчина — сборът по партидите ѝ.</summary>
    private decimal Taken(TreatPile pile) => SplitMath.Round(
        pile.Shares.Sum(s => _picks.GetValueOrDefault((Key(s), s.Batch.Id))));

    /// <summary>Кодът на един дял — сведен, за да е ключ.</summary>
    private static string Key(StockShare share) => ValueConverter.NormalizeCode(share.Ewc);

    /// <summary>
    /// Сборът на ИЗБРАНОТО — отделно число от сбора на всичко.
    ///
    /// Човек маркира шест реда от седемдесет и пита „това колко е“. Дотук
    /// отговорът се събираше наум или не се събираше изобщо.
    ///
    /// Брои се таблицата, в която стои ръката: горе редовете са кодове, долу —
    /// партиди, а числото и в двата случая е едно и също по смисъл: колко от
    /// маркираното ще влезе в предаването и колко стои зад него.
    /// </summary>
    private void ShowPicked()
    {
        var below = ReferenceEquals(_last, _origin);

        var rows = below
            ? _origin.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<StockShare>().Count()
            : _rows.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<TreatPile>().Count();

        decimal take;
        decimal stock;

        if (below)
        {
            var shares = _origin.SelectedRows
                .Cast<DataGridViewRow>()
                .Select(r => r.Tag)
                .OfType<StockShare>()
                .ToList();

            take = SplitMath.Round(shares.Sum(x => _picks.GetValueOrDefault((Key(x), x.Batch.Id))));
            stock = SplitMath.Round(shares.Sum(x => x.Amount));
        }
        else
        {
            var piles = _rows.SelectedRows
                .Cast<DataGridViewRow>()
                .Select(r => r.Tag)
                .OfType<TreatPile>()
                .ToList();

            take = SplitMath.Round(piles.Sum(Taken));
            stock = SplitMath.Round(piles.Sum(p => p.Available));
        }

        if (rows == 0)
        {
            _picked.Text = Lang.T("Нищо не е маркирано.", "Nothing is marked.");
            _picked.ForeColor = Theme.InkFaint;

            return;
        }

        var what = below
            ? Lang.T($"{rows} партиди", $"{rows} batches")
            : Lang.T($"{rows} кода", $"{rows} codes");

        _picked.Text = Lang.T(
            $"Маркирано: {what} · {SplitMath.Amount(take)} т от {SplitMath.Amount(stock)} т",
            $"Marked: {what} · {SplitMath.Amount(take)} t of {SplitMath.Amount(stock)} t");

        _picked.ForeColor = take > 0 ? Theme.Ink : Theme.InkMuted;
    }

    /// <summary>Изписва взетото по партиди, без да пресмята наново.</summary>
    private void ShowTaken()
    {
        if (_rows.CurrentRow?.Tag is TreatPile pile)
        {
            _origin.Columns[OriginStock].HeaderText =
                LedgerPage.Summed(Lang.T("Налично, т", "Available, t"), pile.Available);

            _origin.Columns[OriginTake].HeaderText =
                LedgerPage.Summed(Lang.T("Ще се вземе, т", "Will be taken, t"), Taken(pile));
        }

        _filling = true;

        try
        {
            foreach (DataGridViewRow row in _origin.Rows)
            {
                if (row.Tag is not StockShare share) continue;

                var take = _picks.GetValueOrDefault((Key(share), share.Batch.Id));

                row.Cells[OriginTake].Value = take > 0 ? SplitMath.Amount(take) : string.Empty;
                row.Cells[OriginTake].Style.ForeColor = take > 0 ? Theme.Accent : Theme.InkFaint;
                row.Selected = take > 0;
            }
        }
        finally
        {
            _filling = false;
        }
    }

    private AppButton Quick(string text, bool whole)
    {
        var button = Theme.GhostButton(text);
        button.Height = Theme.Scaled(40);
        button.Margin = new Padding(0, 0, Theme.GapSmall, 0);
        button.Click += (_, _) => FillTakes(whole);

        return button.FitWidth();
    }

    /// <summary>
    /// Едно поле с надписа си.
    ///
    /// Двайсет пиксела по-тясно от полето при третирането, и не от вкус:
    /// възелът на предаването носи девет колони — получателят с вида и ЕИК-а
    /// му, основанието, дейността с описанието ѝ, двете за НУБА и „за повторна
    /// употреба“ — а с бележката стават десет. По 300 пиксела те се редят по
    /// ТРИ на ред и четвъртият ред оставаше отрязан от рамката: надписът
    /// „Бележка“ се виждаше, а полето под него — не. По 280 се редят по четири
    /// и се побират в три реда.
    ///
    /// Рамката пак превърта — при по-тесен прозорец редовете пак стават четири
    /// — но при отваряне прозорчето вече не изглежда счупено.
    /// </summary>
    private Control Field(string caption, Control input)
    {
        var width = Theme.Scaled(280);

        var host = new Panel
        {
            Width = width,
            Height = Theme.Scaled(52),
            BackColor = Theme.Canvas,
            Margin = new Padding(0, 0, Theme.Gap, Theme.GapSmall)
        };

        input.Dock = DockStyle.Top;
        input.Width = width;
        Theme.StyleInput(input);

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

    // ------------------------------------------------------------- пълнене

    /// <summary>Слага началните стойности: датата, купчините и цялото налично.</summary>
    private void Seed()
    {
        _filling = true;

        try
        {
            // Датата, към която складът е гледан, а не днешната: човек избира
            // „към 30.09“ и предава това, което е стояло тогава.
            var today = DateOnly.FromDateTime(DateTime.Today);
            _date.Value = (_amend?.Date ?? (_on > today ? today : _on)).ToDateTime(TimeOnly.MinValue);

            FillOutCodes();

            _outCode.Enabled = false;

            Lay(everything: true);
            ShowWhen();
        }
        finally
        {
            _filling = false;
        }

        Weigh();
        ShowOrigin();
    }

    /// <summary>
    /// Смята купчините наново към избраната дата.
    ///
    /// Складът не е един и същ през цялата година: партида, приета на 1 юли, не
    /// може да се предаде на 1 май. Дотук прозорчето показваше наличността към
    /// датата, с която е отворено, и оставяше човека да предава количества,
    /// които още не са дошли — грешка, която се вижда чак в отчета.
    ///
    /// Обхватът остава избраният отзад: пресмята се същият набор „код и
    /// партида“, но към новата дата.
    /// </summary>
    private void Reload(bool saved = false)
    {
        ShowWhen();

        if (_filling) return;

        var moves = _workspace.Ledger.Read(_companyId, _stagenum).Where(m => !_gone.Contains(m.Id)).ToList();
        var links = _workspace.Ledger.Links(_companyId, _stagenum).Where(l => !_gone.Contains(l.MoveId)).ToList();
        var batches = _workspace.Batches.List(_companyId, _stagenum);

        var shares = StockMath.Shares(batches, moves, links, _store, Date)
            .Where(s => _chosen.Contains((ValueConverter.NormalizeCode(s.Ewc), s.Batch.Id)))
            .ToList();

        if (_amend is not null)
        {
            var open = _amend.Capped(shares, moves, links, Date);

            _notice.Say(_amend.Notice(Amend.Held(shares, open)));

            shares = [.. open];
        }

        _piles = Handover.Piles(shares);

        _filling = true;

        try
        {
            Lay(everything: false, saved);
        }
        finally
        {
            _filling = false;
        }

        Weigh();
        ShowOrigin();
    }

    /// <summary>
    /// Слага купчините в таблицата.
    ///
    /// Изборът преживява пресмятането: човек, който е извадил два кода и после
    /// е сменил датата, не бива да ги намери върнати. Нито пък избралият три
    /// кода да ги загуби: изчезналите на тази дата се помнят и се връщат избрани,
    /// щом датата пак ги има. Забравят се само след записване — тогава ги няма,
    /// защото вече са взети.
    /// </summary>
    private void Lay(bool everything, bool saved = false)
    {
        var picked = everything
            ? []
            : _rows.Rows.Cast<DataGridViewRow>()
                .Where(r => r.Tag is TreatPile p && (r.Visible ? r.Selected : Taken(p) > 0))
                .Select(r => ((TreatPile)r.Tag!).Ewc)
                .ToHashSet(StringComparer.Ordinal);

        if (!everything && !saved) picked.UnionWith(_away);

        // Коя купчина е под курсора — по нея виси таблицата с партидите.
        var was = (_rows.CurrentRow?.Tag as TreatPile)?.Ewc;

        _rows.Rows.Clear();

        // Дяловете се сглобяват наново заедно с редовете: партидите са други —
        // на друга дата или след записване — и старите ключове вече сочат към
        // количества, каквито няма.
        _picks.Clear();

        foreach (var pile in Sorted(_piles))
        {
            var index = _rows.Rows.Add();
            var row = _rows.Rows[index];

            row.Cells[CodeColumn].Value = Describe(pile.Ewc);
            row.Cells[BatchColumn].Value = pile.Batches.ToString();
            row.Cells[AgeColumn].Value = pile.Oldest.ToString(ValueConverter.BulgarianDateFormat);
            row.Cells[StockColumn].Value = SplitMath.Amount(pile.Available);

            row.Tag = pile;

            // Отваря се с всичко избрано: най-честото е „предавам това, което
            // избрах отзад“, а изваждането на ред е по-рядко от избирането на
            // всички.
            var take = everything || picked.Contains(pile.Ewc);

            row.Selected = take;

            // Написаното се пази по код, но не може да надхвърли наличното към
            // новата дата. Свива се само показаното, не и написаното: иначе
            // една междинна дата би го смалила завинаги.
            var written = _wanted.TryGetValue(pile.Ewc, out var typed)
                ? Math.Min(typed, pile.Available)
                : pile.Available;

            Mark(row, Fill(pile, take, written));
        }

        _away.Clear();

        if (!everything && !saved)
            _away.UnionWith(picked.Where(ewc =>
                !_piles.Any(p => string.Equals(p.Ewc, ewc, StringComparison.Ordinal))));

        // Ако нито един от избраните кодове не е оцелял след записване — а има
        // какво още да се взима — избира се всичко: това е „продължавам с
        // останалото“. Нарочно празният избор („Нищо“) обаче си остава празен,
        // а избраното, което само на тази дата го няма, чака да се върне.
        if (_rows.Rows.Count > 0 && _rows.SelectedRows.Count == 0 &&
            (everything || (picked.Count > 0 && _away.Count == 0)))
            _rows.SelectAll();

        Keep(was);

        _rowsFilter.Apply();

        Glyph(_rows, _rowsSort, _rowsDown);

        Allow();
    }

    /// <summary>
    /// Връща курсора върху същата купчина след пресмятането.
    ///
    /// <c>DataGridView.Rows.Clear()</c> изпуска текущата клетка и не я връща
    /// сам, докато таблицата не бъде пипната. А по текущия ред виси всичко
    /// вдясно: смяната на датата изпразваше десния край на прозореца и оттам
    /// нататък написаното не се прибираше никъде.
    ///
    /// Изборът се пази през присвояването: текущата клетка го свива до своя ред,
    /// а тук се сменя само курсорът, не и какво е избрано.
    /// </summary>
    private void Keep(string? ewc)
    {
        if (_rows.Rows.Count == 0) return;

        var at = 0;

        if (ewc is { Length: > 0 })
            for (var i = 0; i < _rows.Rows.Count; i++)
                if (_rows.Rows[i].Tag is TreatPile pile &&
                    string.Equals(pile.Ewc, ewc, StringComparison.Ordinal))
                {
                    at = i;
                    break;
                }

        var chosen = _rows.Rows.Cast<DataGridViewRow>()
            .Where(r => r.Selected)
            .Select(r => r.Index)
            .ToList();

        _rows.CurrentCell = _rows.Rows[at].Cells[0];

        _rows.ClearSelection();

        foreach (var index in chosen) _rows.Rows[index].Selected = true;
    }

    /// <summary>Копчетата следват това, което има за предаване.</summary>
    private void Allow()
    {
        var something = _rows.Rows.Count > 0;

        if (_save is not null) _save.Enabled = something;
        if (_more is not null) _more.Enabled = something;
    }

    /// <summary>
    /// Избира или изчиства всички редове.
    ///
    /// Същият жест като влаченето, но с копче: при трийсет кода „всичко“ е
    /// по-бързо от влачене през целия списък, а при един изключен ред —
    /// обратното.
    /// </summary>
    private void FillTakes(bool whole)
    {
        if (whole) _rows.SelectAll();
        else _rows.ClearSelection();

        Apply();
        ShowOrigin();
    }

    /// <summary>
    /// Превръща избора в количества.
    ///
    /// Избраният ред взема каквото човек е писал по него, а ако не е писал —
    /// цялата купчина. Неизбраният отива на нула и си остава празен: нула,
    /// изписана като „0,000“ на двайсет реда, е двайсет числа, които не значат
    /// нищо.
    /// </summary>
    private void Apply()
    {
        if (_filling) return;

        _filling = true;

        try
        {
            foreach (DataGridViewRow row in _rows.Rows)
            {
                // Скритият от пресяването код си пази количеството.
                if (row.Tag is not TreatPile pile || !row.Visible) continue;

                var written = _wanted.TryGetValue(pile.Ewc, out var typed) ? typed : pile.Available;

                // Изборът по код се разписва по партидите: те са истината, а
                // редът горе е сборът им. Дотук този ред беше сам по себе си и
                // партидите се смятаха наново при всяко показване — тогава
                // посоченото от човека по партиди се губеше.
                Mark(row, Fill(pile, row.Selected, written));
            }
        }
        finally
        {
            _filling = false;
        }

        Weigh();
    }

    /// <summary>
    /// Разписва количество по партидите на една купчина — най-старата първа.
    ///
    /// Ползва се, когато човек е казал само „толкова от този код“: тогава редът
    /// се избира сам, по същото правило, по което складът се изчерпва навсякъде
    /// другаде.
    /// </summary>
    private void Spread(TreatPile pile, decimal amount)
    {
        foreach (var share in pile.Shares) _picks.Remove((Key(share), share.Batch.Id));

        foreach (var pick in Handover.Spread(pile, amount))
            _picks[(Key(pick.Share), pick.Share.Batch.Id)] = pick.Amount;
    }

    /// <summary>
    /// Слага дяловете на една купчина и казва колко се взима от нея.
    ///
    /// Посоченото на ръка по партиди печели: то е изборът на човека, а
    /// „най-старата нататък“ е само подразбирането, когато той не е посочвал.
    /// </summary>
    private decimal Fill(TreatPile pile, bool take, decimal written)
    {
        if (take && Recall(pile)) return Taken(pile);

        var amount = take ? written : 0m;

        Spread(pile, amount);

        return amount;
    }

    /// <summary>Запомня посоченото по партидите на една купчина.</summary>
    private void Hold(TreatPile pile)
    {
        // Посоченото по партиди, които на тази дата ги няма, остава.
        var held = _hand.TryGetValue(pile.Ewc, out var was)
            ? was.Where(p => pile.Shares.All(s => s.Batch.Id != p.Key)).ToDictionary()
            : [];

        foreach (var share in pile.Shares)
            if (_picks.GetValueOrDefault((Key(share), share.Batch.Id)) is > 0 and var take)
                held[share.Batch.Id] = take;

        if (held.Count > 0) _hand[pile.Ewc] = held;
        else _hand.Remove(pile.Ewc);
    }

    /// <summary>
    /// Връща посоченото на ръка — свито до стоялото на тази дата.
    ///
    /// Лъжа, ако по кода не е посочвано. Посочено ли е, а на тази дата никоя от
    /// партидите я няма, от кода не се взима нищо: чужди партиди не се взимат
    /// вместо посочените.
    /// </summary>
    private bool Recall(TreatPile pile)
    {
        if (!_hand.TryGetValue(pile.Ewc, out var held)) return false;

        foreach (var share in pile.Shares)
        {
            var key = (Key(share), share.Batch.Id);

            if (held.GetValueOrDefault(share.Batch.Id) is > 0 and var take)
                _picks[key] = Math.Min(take, share.Amount);
            else
                _picks.Remove(key);
        }

        return true;
    }

    /// <summary>
    /// Пренася върнатото от целия списък: по колко се взима от всяка партида
    /// на една купчина. Пресятото там също е в числата — скритото не значи
    /// „извадено“.
    /// </summary>
    internal void ApplyList(TreatPile pile, IReadOnlyDictionary<long, decimal> takes)
    {
        foreach (var share in pile.Shares)
        {
            var key = (Key(share), share.Batch.Id);
            var take = takes.GetValueOrDefault(share.Batch.Id);

            if (take <= 0) _picks.Remove(key);
            else _picks[key] = Math.Min(SplitMath.Round(take), share.Amount);
        }

        _last = _origin;

        Took(pile);
        ShowOrigin();
    }

    /// <summary>
    /// Отваря партидите зад текущия код на цял прозорец — същият избор, с
    /// повече място, търсене и пресяване.
    /// </summary>
    private void Expand()
    {
        if (_rows.CurrentRow?.Tag is not TreatPile pile) return;

        var taken = pile.Shares
            .GroupBy(s => s.Batch.Id)
            .ToDictionary(g => g.Key, g => _picks.GetValueOrDefault((Key(g.First()), g.Key)));

        using var dialog = new BatchListDialog(
            Lang.T($"Зад {Describe(pile.Ewc)}", $"Behind {Describe(pile.Ewc)}"),
            Lang.T("Ще се вземе, т", "Will be taken, t"),
            pile.Shares,
            taken,
            fromTop: TopPlan().Left);

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        ApplyList(pile, dialog.Takes);
    }

    /// <summary>
    /// Колко да вземе „Вземи отгоре“ от текущия код: „Предадено общо“ без
    /// взетото по другите кодове.
    ///
    /// Кантарът мери целия камион. Взето ли е вече нещо по друг код, то е в
    /// камиона и тук се допълва само остатъкът — иначе сборът би прескочил
    /// заявеното точно с чуждото.
    /// </summary>
    private (TreatPile? Pile, decimal Others, decimal Left) TopPlan()
    {
        if (_rows.CurrentRow?.Tag is not TreatPile pile) return (null, 0m, 0m);

        var others = SplitMath.Round(_piles
            .Where(p => !string.Equals(p.Ewc, pile.Ewc, StringComparison.Ordinal))
            .Sum(Taken));

        return (pile, others, SplitMath.Round(Target - others));
    }

    /// <summary>Казва над партидите какво ще вземе копчето — преди да е натиснато.</summary>
    private void ShowFromTop()
    {
        var (pile, others, rest) = TopPlan();

        if (pile is null)
            _fromTop.Hint(Lang.T("Изберете код вляво.", "Pick a code on the left."));
        else if (Target <= 0)
            _fromTop.Hint(Lang.T("Напишете „Предадено общо“ — копчето взима отгоре до него.",
                                 "Type the “Handed over in total” — the button takes from the top up to it."));
        else if (rest <= 0)
            _fromTop.Hint(Lang.T(
                $"Другите кодове вече покриват {SplitMath.Amount(Target)} т — оттук няма какво да се вземе.",
                $"The other codes already cover {SplitMath.Amount(Target)} t — nothing is left to take here."));
        else
            _fromTop.Hint(others > 0
                ? Lang.T($"Ще вземе до {SplitMath.Amount(rest)} т: {SplitMath.Amount(Target)} т общо − {SplitMath.Amount(others)} т по други кодове.",
                         $"Will take up to {SplitMath.Amount(rest)} t: {SplitMath.Amount(Target)} t in total − {SplitMath.Amount(others)} t under other codes.")
                : Lang.T($"Ще вземе до {SplitMath.Amount(rest)} т.", $"Will take up to {SplitMath.Amount(rest)} t."));
    }

    /// <summary>
    /// Взима от партидите на текущия код отгоре надолу до „Предадено общо“.
    ///
    /// Редът е този, в който таблицата се вижда — подредена и пресята от
    /// човека. Скритите партиди на този код излизат от предаването: целта е
    /// „точно толкова от това, което гледам“, а скрито взимане би прескочило
    /// числото. Другите кодове не се пипат.
    /// </summary>
    private void FromTop()
    {
        var (pile, _, amount) = TopPlan();

        if (pile is null || Target <= 0 || amount <= 0)
        {
            ShowFromTop();
            _fromTop.Say(_fromTop.SaidText, warn: true);
            return;
        }

        var ordered = _origin.Rows.Cast<DataGridViewRow>()
            .Where(r => r.Visible)
            .Select(r => r.Tag)
            .OfType<StockShare>()
            .ToList();

        var picks = Handover.FromTop(ordered, amount);

        ApplyList(pile, picks.ToDictionary(p => p.Share.Batch.Id, p => p.Amount));

        _fromTop.Report(picks, amount);
    }

    /// <summary>Изписва количеството и казва с цвят дали редът участва.</summary>
    private static void Mark(DataGridViewRow row, decimal take)
    {
        row.Cells[TakeColumn].Value = take > 0 ? SplitMath.Amount(take) : string.Empty;
        row.DefaultCellStyle.ForeColor = take > 0 ? Theme.Ink : Theme.InkFaint;
    }

    /// <summary>Запомня написаното по ред, за да преживее пресичането на избора.</summary>
    private void Remember()
    {
        if (_rows.CurrentRow?.Tag is not TreatPile pile) return;

        SplitMath.TryRead(_rows.CurrentRow.Cells[TakeColumn].Value?.ToString(), out var amount);

        if (amount > 0) _wanted[pile.Ewc] = amount;
        else _wanted.Remove(pile.Ewc);

        // Число по код отменя посоченото по партиди: то пак се разписва от
        // най-старата нататък.
        _hand.Remove(pile.Ewc);

        // Написаното по код се разписва по партидите му веднага: оттам нататък
        // сметката е една и съща за двете таблици.
        Spread(pile, amount);
        ShowTaken();
    }

    /// <summary>
    /// Каквото е избрано в момента — по код, за проверките.
    ///
    /// Сборът на дяловете, а не написаното в реда: редът може да е сбор от пет
    /// посочени партиди, а не число, което някой е напечатал.
    /// </summary>
    private List<HandOrder> Orders()
    {
        var orders = new List<HandOrder>();

        foreach (var pile in _piles) orders.Add(new HandOrder(pile, Taken(pile)));

        return orders;
    }

    /// <summary>Самите дялове — те отиват в движенията такива, каквито са.</summary>
    private List<TreatPick> Picks()
    {
        var picks = new List<TreatPick>();

        foreach (var pile in _piles)
            foreach (var share in pile.Shares)
                if (_picks.GetValueOrDefault((Key(share), share.Batch.Id)) is > 0 and var take)
                    picks.Add(new TreatPick(share, Math.Min(take, share.Amount)));

        return picks;
    }

    /// <summary>
    /// Сборовете и какво ще излезе от тях.
    ///
    /// Два реда, защото са два въпроса: „колко взимам от колко“ се гледа, докато
    /// се пише, а „какво ще стане“ — преди да се натисне копчето.
    /// </summary>
    private void Weigh()
    {
        if (_filling || IsDisposed) return;

        // Кантарът или избраното са сменени — „Вземи отгоре“ ще вземе друго.
        ShowFromTop();

        var orders = Orders();
        var wanted = SplitMath.Round(orders.Sum(o => Math.Max(0m, o.Amount)));
        var available = SplitMath.Round(orders.Sum(o => o.Pile.Available));
        var codes = orders.Count(o => o.Amount > 0);

        // Изписано с думи, а не като „сбор от … · … · …“: числата са три и без
        // думи пред тях не се познава кое какво е. Първото е онова, което ще
        // излезе от площадката, ако сега се натисне копчето.
        _totals.Text = Lang.T(
            $"За предаване: {SplitMath.Amount(wanted)} т · налично: {SplitMath.Amount(available)} т · " +
            $"кодове: {codes} от {orders.Count}",
            $"To hand over: {SplitMath.Amount(wanted)} t · available: {SplitMath.Amount(available)} t · " +
            $"codes: {codes} of {orders.Count}");

        ShowOriginal(wanted);

        // Сборовете стоят и в заглавията на колоните — там, където се гледа
        // самото число.
        _rows.Columns[StockColumn].HeaderText =
            LedgerPage.Summed(Lang.T("Налично, т", "Available, t"), available);

        _rows.Columns[TakeColumn].HeaderText =
            LedgerPage.Summed(Lang.T("За предаване, т", "To hand over, t"), wanted);

        ShowPicked();
        FillInto();
        ShowGap();

        var moves = Handover.From(Picks(), _perBatch.Checked, OutCode());

        if (moves.Count == 0)
        {
            _split.Text = Lang.T("Няма посочено количество за предаване.",
                                 "No quantity is stated for the hand-over.");
            _split.ForeColor = Theme.InkMuted;
            return;
        }

        var over = Handover.Check(orders, Target, _store).FirstOrDefault(i => i.Kind != HandProblem.Nothing);

        if (over is not null)
        {
            _split.Text = over.Text;
            _split.ForeColor = Theme.Warn;
            return;
        }

        var batches = moves.SelectMany(m => m.Draws).Select(d => d.BatchId).Distinct().Count();

        // „1 реда“ е дребно, но се чете всеки път: числото е пред очите, докато
        // човек решава дали да натисне копчето.
        _split.Text = Lang.T(
            $"{moves.Count} {(moves.Count == 1 ? "ред" : "реда")} „Предаден“ · " +
            $"от {batches} {(batches == 1 ? "партида" : "партиди")}",
            $"{moves.Count} “Submitted” {(moves.Count == 1 ? "row" : "rows")} · " +
            $"from {batches} {(batches == 1 ? "batch" : "batches")}");

        if (OutCode() is { Length: > 0 } code)
            _split.Text += Lang.T($" · всички с код {code}", $" · all under code {code}");

        _split.ForeColor = Theme.Ok;
    }

    // --------------------------------------------------------- изравняване

    /// <summary>
    /// Казва как стои избраното спрямо кантара — и кой ред ще поеме разликата.
    ///
    /// Два реда, защото са два въпроса, и вторият е причината изравняването да
    /// не е страшно: „2,400 т в повече“ е състоянието, а „ще слязат от 191202 ·
    /// #2026-0003“ е какво точно ще стане, ако се натисне копчето. Второто се
    /// чете ПРЕДИ натискането — иначе изравняването е сметка, направена вместо
    /// човека и скрита от него.
    /// </summary>
    private void ShowGap()
    {
        var weighing = Handover.Weigh(Picks(), Target);

        if (!weighing.Stated)
        {
            _gap.Text = Lang.T(
                "Не е заявено общо количество — предава се точно избраното.",
                "No total is stated — exactly what is picked gets handed over.");

            _gap.ForeColor = Theme.InkFaint;
            _anchor.Text = string.Empty;

            Offer(false);
            return;
        }

        if (weighing.Matches)
        {
            _gap.Text = Lang.T(
                $"Избраното е точно заявеното: {SplitMath.Amount(weighing.Target)} т.",
                $"What is picked is exactly what is stated: {SplitMath.Amount(weighing.Target)} t.");

            _gap.ForeColor = Theme.Ok;
            _anchor.Text = string.Empty;

            Offer(false, Theme.Ok);
            return;
        }

        var size = SplitMath.Amount(Math.Abs(weighing.Gap));

        _gap.Text = weighing.Short
            ? Lang.T($"Избрано {SplitMath.Amount(weighing.Picked)} т · не достигат {size} т.",
                     $"Picked {SplitMath.Amount(weighing.Picked)} t · {size} t are missing.")
            : Lang.T($"Избрано {SplitMath.Amount(weighing.Picked)} т · {size} т в повече.",
                     $"Picked {SplitMath.Amount(weighing.Picked)} t · {size} t too many.");

        _gap.ForeColor = Theme.Warn;

        Offer(true, Theme.Warn);

        if (Anchor() is not { } anchor)
        {
            _anchor.Text = Lang.T(
                "Посочете ред, който да поеме разликата, или застанете на него.",
                "Pick a row that is to take the difference, or stand on it.");
            return;
        }

        var (_, left) = Handover.Onto(anchor.Take, anchor.Room, weighing.Gap);

        var into = weighing.Short
            ? Lang.T($"{size} т ще се добавят към {anchor.Name}",
                     $"{size} t will be added to {anchor.Name}")
            : Lang.T($"{size} т ще слязат от {anchor.Name}",
                     $"{size} t will come off {anchor.Name}");

        // Каквото редът не побира, се казва тук, а не се премълчава: човек, на
        // когото е обещано изравняване, не бива да открива сам, че е останало
        // половин тон.
        if (left != 0)
            into += Lang.T($" · {SplitMath.Amount(Math.Abs(left))} т ще останат",
                           $" · {SplitMath.Amount(Math.Abs(left))} t will be left over");

        // Казва се и ОТКЪДЕ идва редът: посочен, или онзи, на който стои ръката.
        // Двете се различават само по това дали менюто е пипано, а разликата е
        // цялата разлика между „избрах този“ и „случайно бях там“.
        _anchor.Text = _pinned is null
            ? into + Lang.T(" · редът, на който стоите", " · the row you are standing on")
            : into + Lang.T(" · посочен ред", " · picked row");
    }

    /// <summary>
    /// Копчетата за изравняване работят само когато има какво да равнят, а
    /// ивицата на рамката казва в какво състояние е кантарът.
    ///
    /// Цветът е на едно място, вместо да се задава на трите изхода на
    /// <see cref="ShowGap"/>: три места, които трябва да се пазят съгласувани,
    /// се разминават на първата поправка.
    /// </summary>
    private void Offer(bool something, Color? stripe = null)
    {
        if (_level is not null) _level.Enabled = something;
        if (_spread is not null) _spread.Enabled = something;

        if (_scale is null) return;

        _scale.Stripe = stripe ?? Theme.BorderSoft;
        _scale.Invalidate();
    }

    /// <summary>
    /// Един ред, който може да поеме разликата.
    ///
    /// <see cref="Name"/> влиза в изречението („ще слязат от 080114 · #2026-0771“),
    /// а <see cref="Label"/> — в менюто, където до реда стои и количеството му.
    /// Двете не са едно, защото в изречението вече има число и второ до него не
    /// се чете.
    /// </summary>
    private sealed record Spot(
        string Name, string Label, string Ewc, long Batch, decimal Take, decimal Room);

    /// <summary>
    /// Редовете, които могат да поемат разликата — точно тези, които участват.
    ///
    /// Изравняването пипа само предаваното: ред, от който не се взима нищо, не
    /// може да поеме нищо, а изравняване в него би го вкарало в предаването
    /// изотзад. Затова списъкът е сборът на избраното, в реда на двете таблици:
    /// код, после партидите му.
    /// </summary>
    private List<Spot> Spots()
    {
        var spots = new List<Spot>();

        foreach (var pile in Sorted(_piles))
        {
            var take = Taken(pile);
            if (take <= 0) continue;

            spots.Add(new Spot(
                pile.Ewc,
                $"{pile.Ewc} · {SplitMath.Amount(take)} т",
                pile.Ewc, 0, take, pile.Available));

            foreach (var share in Sorted(pile.Shares))
            {
                var mine = _picks.GetValueOrDefault((Key(share), share.Batch.Id));
                if (mine <= 0) continue;

                spots.Add(new Spot(
                    $"{Key(share)} · #{share.Batch.Code}",
                    $"    #{share.Batch.Code} · {SplitMath.Amount(mine)} т",
                    Key(share), share.Batch.Id, mine, share.Amount));
            }
        }

        return spots;
    }

    /// <summary>
    /// Кой ред поема разликата: посоченият в менюто, иначе този, на който стои
    /// ръката.
    ///
    /// Курсорът беше единственият начин и точно това беше проблемът: той се
    /// мести при всяко щракване, а щракването по който и да е стълб освен
    /// количеството е ИЗБОР — то изважда останалите редове от предаването,
    /// защото избраното Е изборът. Човек, който иска да посочи реда, рискува
    /// избора си, за да го направи.
    ///
    /// Затова менюто: посочването през него не пипа нито един ред. Курсорът
    /// остава като подразбиране — той е бърз, когато редът е един и ръката вече
    /// е на него.
    ///
    /// Долу редът е партида и разликата сяда точно в нея; горе редът е код и
    /// разликата се разписва по партидите му, най-старата първа.
    /// </summary>
    private Spot? Anchor()
    {
        // Посоченото печели над курсора — иначе менюто би било украса.
        if (_pinned is { } pin &&
            Spots().FirstOrDefault(s => s.Batch == pin.Batch &&
                string.Equals(s.Ewc, pin.Ewc, StringComparison.Ordinal)) is { } picked)
            return picked;

        if (ReferenceEquals(_last, _origin) && _origin.CurrentRow?.Tag is StockShare share)
            return new Spot(
                $"{Key(share)} · #{share.Batch.Code}", string.Empty,
                Key(share), share.Batch.Id,
                _picks.GetValueOrDefault((Key(share), share.Batch.Id)),
                share.Amount);

        return _rows.CurrentRow?.Tag is TreatPile pile
            ? new Spot(pile.Ewc, string.Empty, pile.Ewc, 0, Taken(pile), pile.Available)
            : null;
    }

    /// <summary>
    /// Пренаписва менюто с редовете, които участват сега.
    ///
    /// Пренаписва се само когато списъкът наистина се е сменил: <c>Weigh</c> се
    /// вика при всеки натиснат клавиш в кантара, а меню, което се пресъздава под
    /// отворена стрелка, се затваря в ръцете на човека.
    /// </summary>
    private void FillInto()
    {
        var spots = Spots();
        var shape = string.Join("|", spots.Select(s => $"{s.Ewc}:{s.Batch}:{s.Take}"));

        if (shape == _intoShape) return;

        _intoShape = shape;

        var at = _pinned is { } pin
            ? spots.FindIndex(s => s.Batch == pin.Batch &&
                string.Equals(s.Ewc, pin.Ewc, StringComparison.Ordinal))
            : -1;

        // Посочен ред, който е отпаднал от избора, не се пази мълчаливо: той
        // вече не участва в предаването и разликата няма къде да седне в него.
        var lost = _pinned is not null && at < 0;
        if (lost) _pinned = null;

        var was = _filling;
        _filling = true;

        try
        {
            _into.Items.Clear();
            _into.Items.Add(HandText);

            foreach (var spot in spots) _into.Items.Add(spot.Label);

            _into.SelectedIndex = at >= 0 ? at + 1 : 0;
        }
        finally
        {
            _filling = was;
        }

        if (lost)
            Say(Lang.T(
                "Посоченият ред отпадна от избора — разликата пак ляга там, където стои ръката.",
                "The picked row left the selection — the difference lands where the hand is again."));
    }

    /// <summary>
    /// Курсорът се е преместил — изречението под копчетата сочи друг ред.
    ///
    /// Без това то оставаше от последното пипнато число: човек мести курсора,
    /// чете „ще слязат от 191204“ и натиска копчето, а разликата сяда другаде.
    /// Изречението е обещание и трябва да върви с курсора, не след него.
    /// </summary>
    private void Follow()
    {
        // _scale е последното построено в рамката на кантара: няма ли го, още
        // се нареждат прозорчето и таблиците, а те местят курсора сами.
        if (_filling || IsDisposed || _scale is null) return;

        ShowGap();
    }

    /// <summary>Запомня посочения ред. Не пипа нито една таблица — затова го има.</summary>
    private void Pin()
    {
        if (_filling) return;

        var spots = Spots();
        var at = _into.SelectedIndex - 1;

        _pinned = at >= 0 && at < spots.Count ? (spots[at].Ewc, spots[at].Batch) : null;

        ShowGap();
    }

    /// <summary>
    /// Слага количество на посочения ред.
    ///
    /// Минава през дяловете, а не през клетка на таблица: посоченият ред може да
    /// е под друг код и изобщо да не се вижда долу, а курсорът не бива да се
    /// мести заради изравняването — местенето му пресича избора.
    /// </summary>
    private void Put(Spot spot, decimal amount)
    {
        if (_piles.FirstOrDefault(p =>
                string.Equals(p.Ewc, spot.Ewc, StringComparison.Ordinal)) is not { } pile) return;

        if (spot.Batch > 0)
        {
            if (pile.Shares.FirstOrDefault(s => s.Batch.Id == spot.Batch) is not { } share) return;

            var key = (Key(share), share.Batch.Id);

            // Повече от стоялото не се взима: партидата няма толкова.
            if (amount <= 0) _picks.Remove(key);
            else _picks[key] = Math.Min(SplitMath.Round(amount), share.Amount);
        }
        else
        {
            Spread(pile, amount);
        }

        // Пренарежда горния ред, маркира го и пренаписва числата долу.
        Took(pile);
    }

    /// <summary>
    /// „Изравни“: разликата сяда в реда, на който стои ръката.
    ///
    /// Точно жестът, който площадката прави на ръка — камионът е показал трийсет
    /// тона, сметката назад дава трийсет и два, и двата излишни се свалят от
    /// насипната купчина, защото тя е тази, от която е догребвано. Програмата не
    /// познава коя е тя; човекът я познава и застава на нея.
    /// </summary>
    private void Level()
    {
        var weighing = Handover.Weigh(Picks(), Target);

        if (!weighing.Stated || weighing.Matches) return;

        if (Anchor() is not { } anchor)
        {
            Say(Lang.T("Застанете на реда, който да поеме разликата.",
                       "Stand on the row that is to take the difference."));
            return;
        }

        var (amount, _) = Handover.Onto(anchor.Take, anchor.Room, weighing.Gap);

        // Ред, който поема разлика, влиза в предаването — за това се грижи
        // Took: без него написаното число стои, а редът остава неизбран и
        // първото щракване по таблицата го занулява обратно.
        Put(anchor, amount);
    }

    /// <summary>
    /// „Пропорционално“: разликата се разнася по всички избрани дялове.
    ///
    /// Другият честен отговор, когато няма един виновен ред. Пипат се дяловете,
    /// а не написаното в клетка: клетката е сборът им и се преизписва после.
    /// </summary>
    private void Proportional()
    {
        var target = Target;
        if (target <= 0) return;

        var scaled = Handover.Scale(Picks(), target);

        _picks.Clear();

        foreach (var pick in scaled)
            _picks[(Key(pick.Share), pick.Share.Batch.Id)] = pick.Amount;

        _filling = true;

        try
        {
            foreach (DataGridViewRow row in _rows.Rows)
            {
                if (row.Tag is not TreatPile pile) continue;

                var take = Taken(pile);

                _wanted[pile.Ewc] = take;
                Hold(pile);
                row.Selected = take > 0;

                Mark(row, take);
            }
        }
        finally
        {
            _filling = false;
        }

        Weigh();
        ShowTaken();
    }

    /// <summary>Казва нещо до копчетата — там, където се гледа записаното дотук.</summary>
    private void Say(string text)
    {
        _done.Text = text;
        _done.ForeColor = Theme.Warn;
    }

    // ------------------------------------------------------------- записване

    /// <summary>
    /// Проверява и затваря.
    ///
    /// Спира само това, което няма как да се поправи после: искане над
    /// наличното, празно предаване, разминаване с кантара, липсващо задължително
    /// поле и затворен месец. Всичко останало се записва и се поправя от
    /// „Месец“, както всяко друго движение.
    /// </summary>
    private void Accept()
    {
        if (!Ready(out var moves, out var fields)) return;

        // Поправка, която мени количеството, се потвърждава: най-често се
        // мести датата, а по-малкото количество на по-ранна дата е следствие,
        // което лесно минава незабелязано.
        if (_amend is not null && Visible)
        {
            var now = SplitMath.Round(moves.Sum(m => m.Amount));

            if (now != Original &&
                MessageBox.Show(this,
                    Lang.T(
                        $"Количеството се променя: беше {SplitMath.Amount(Original)} т, става {SplitMath.Amount(now)} т.\n\n" +
                        "Да се запише ли поправката така?",
                        $"The quantity changes: it was {SplitMath.Amount(Original)} t, it becomes {SplitMath.Amount(now)} t.\n\n" +
                        "Save the correction like this?"),
                    Lang.T("Променено количество", "Changed quantity"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        }

        Fields = fields;
        Result = moves;

        DialogResult = DialogResult.OK;
    }

    /// <summary>
    /// Проверява и сглобява — едно място за двете копчета.
    ///
    /// Спира само това, което няма как да се поправи после: искане над
    /// наличното, празно предаване, разминаване с кантара, липсващо задължително
    /// поле и затворен месец.
    ///
    /// Разминаването с кантара спира записа нарочно, макар да е поправимо от
    /// „Месец“: то е единственото, което прозорчето изобщо съществува да хване.
    /// Полето е по избор и празно не пита нищо — обяви ли се обаче число,
    /// значението му е „толкова е измерено“, а не „толкова горе-долу“.
    /// </summary>
    private bool Ready(
        out IReadOnlyList<HandMove> moves,
        out IReadOnlyDictionary<string, string> fields)
    {
        moves = [];
        fields = new Dictionary<string, string>(StringComparer.Ordinal);

        var orders = Orders();
        var problems = Handover.Check(orders, Target, _store).Select(i => i.Text).ToList();

        if (!_sameCode.Checked && Code(_outCode.Text).Length == 0)
            problems.Add(Lang.T(
                "Изберете общ код или вдигнете „Използвай същия код“.",
                "Pick one code or tick “Use the same code”."));

        foreach (var column in Extras())
        {
            if (!column.Required) continue;
            if (Value(column).Length > 0) continue;

            problems.Add(Lang.T($"„{Caption(column)}“ е задължително.",
                                $"“{Caption(column)}” is required."));
        }

        var month = LedgerPeriod.Of(Date);

        if (_workspace.Periods.IsClosed(_companyId, _stagenum, month))
            problems.Add(Lang.T($"{month.Title()} е затворен и не приема записи.",
                                $"{month.Title()} is closed and accepts no entries."));

        // Самоконтролът спира записа, но с отметка се отваря: правилото е на
        // човека и той може да го оттегли. Затвореният месец — не.
        if (OutsidePeriod && !_outside.Checked) problems.Add(_period.Miss(Date));

        Problems = problems;

        if (problems.Count > 0)
        {
            // Съобщението — само на показан прозорец. Модален прозорец върху
            // нишка без съобщителен цикъл виси завинаги, а тестът трябва да
            // може да мине по същия път и да прочете какво е спряло.
            if (Visible)
                MessageBox.Show(this, string.Join(Environment.NewLine, problems.Take(8)),
                    Lang.T("Липсва нещо", "Something is missing"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);

            return false;
        }

        var written = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var column in Extras())
        {
            var value = Value(column);
            if (value.Length > 0) written[column.Name] = value;
        }

        fields = written;
        moves = Handover.From(Picks(), _perBatch.Checked, OutCode());

        if (Guard is not null)
        {
            var broken = Guard(Date, [.. moves.Select(m => new Planned(MoveKind.Submitted, Date, m.Ewc, m.Amount, m.Draws, _direct, m.EwcOut))]);

            if (broken.Count > 0)
            {
                Problems = broken;

                if (Visible)
                    MessageBox.Show(this,
                        string.Join(Environment.NewLine, broken.Take(8)),
                        Lang.T("Поправката разваля по-късно движение", "The correction breaks a later movement"),
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);

                moves = [];
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Записва и остава отворено.
    ///
    /// Записването минава през страницата отзад — там живее правилото как
    /// предаване става движение — а прозорчето само подава сглобеното и после
    /// се пресмята наново. Предаденото си отива от склада и затова изчезва от
    /// списъка само̀: не се маха на ръка, а просто вече го няма.
    /// </summary>
    private void Continue()
    {
        if (Write is null) return;
        if (!Ready(out var moves, out var fields)) return;

        var written = Write(moves, Date, fields, Note);

        FillContracts();

        _saved += written;

        _done.Text = _saved == 1
            ? Lang.T("Записано е 1 предаване.", "1 hand-over saved.")
            : Lang.T($"Записани са {_saved} предавания.", $"{_saved} hand-overs saved.");

        _wanted.Clear();
        _hand.Clear();
        Reload(saved: true);

        if (_rows.Rows.Count == 0)
            _done.Text += Lang.T(" Няма какво повече да се предава.", " Nothing is left to hand over.");

        // Следващото предаване почти винаги е същото на друга дата: курсорът се
        // връща там, откъдето продължава работата.
        _date.Focus();
    }

    /// <summary>Затваря — записаното с „продължи“ вече е в базата.</summary>
    private void Leave() =>
        DialogResult = _saved > 0 ? DialogResult.OK : DialogResult.Cancel;

    /// <summary>
    /// Кои партиди влизат в частичното предаване.
    ///
    /// Избраните долу — това е обичайното: човек е посочил точно тях. Ако долу
    /// няма избрано, взимат се партидите на избраните горе кодове: тогава
    /// въпросът е „от този код, по колко от всяка“.
    /// </summary>
    internal IReadOnlyList<StockShare> PartialScope()
    {
        var chosen = _origin.SelectedRows
            .Cast<DataGridViewRow>()
            .Select(r => r.Tag)
            .OfType<StockShare>()
            .ToList();

        if (chosen.Count > 0) return chosen;

        var codes = _rows.SelectedRows
            .Cast<DataGridViewRow>()
            .Select(r => r.Tag)
            .OfType<TreatPile>()
            .Select(p => p.Ewc)
            .ToHashSet(StringComparer.Ordinal);

        if (codes.Count == 0) return [];

        return
        [
            .. _piles.Where(p => codes.Contains(p.Ewc)).SelectMany(p => p.Shares)
        ];
    }

    /// <summary>
    /// Пренася върнатото от прозорчето: по колко се взима от всяка партида.
    ///
    /// Партида, която е била в обхвата, но се връща без количество, излиза от
    /// предаването — човекът я е оставил празна нарочно.
    /// </summary>
    internal void ApplyPartial(
        IReadOnlyList<StockShare> scope, IReadOnlyDictionary<long, decimal> takes)
    {
        foreach (var share in scope)
        {
            var key = (Key(share), share.Batch.Id);
            var take = takes.GetValueOrDefault(share.Batch.Id);

            if (take <= 0) _picks.Remove(key);
            else _picks[key] = Math.Min(take, share.Amount);
        }

        // Посоченото тук е посочено на ръка — то преживява смяната на датата.
        var touched = scope.Select(Key).ToHashSet(StringComparer.Ordinal);

        // Редовете горе са сборът на партидите си — пресмятат се наново.
        _filling = true;

        try
        {
            foreach (DataGridViewRow row in _rows.Rows)
            {
                if (row.Tag is not TreatPile pile) continue;

                var take = Taken(pile);

                _wanted[pile.Ewc] = take;
                if (touched.Contains(pile.Ewc)) Hold(pile);
                row.Selected = take > 0;

                Mark(row, take);
            }
        }
        finally
        {
            _filling = false;
        }

        Weigh();
        ShowTaken();
    }

    /// <summary>Отваря частичното предаване за избраното.</summary>
    private void Partial()
    {
        var scope = PartialScope();

        if (scope.Count == 0)
        {
            MessageBox.Show(this,
                Lang.T("Изберете кодове горе или партиди долу, за да ги предадете частично.",
                       "Pick codes above or batches below to hand them over partially."),
                Lang.T("Няма избрано", "Nothing is selected"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);

            return;
        }

        var taken = scope
            .GroupBy(s => s.Batch.Id)
            .ToDictionary(g => g.Key, g => _picks.GetValueOrDefault((Key(g.First()), g.Key)));

        // Кантарът влиза заедно с партидите: там се решава срещу него. Взетото
        // от партиди извън обхвата се подава отделно — кантарът мери целия
        // камион, а прозорчето вижда само своите редове.
        var inScope = scope.Select(s => s.Batch.Id).ToHashSet();
        var outside = Picks().Where(p => !inScope.Contains(p.Share.Batch.Id)).Sum(p => p.Amount);

        using var dialog = new PartialDialog(scope, taken, MoveKind.Submitted, Target, outside);

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        ApplyPartial(scope, dialog.Takes);
        UseTarget(dialog.Target);
    }

    /// <summary>
    /// Залепва колона числа в колоната с количествата.
    ///
    /// Количествата идват отнякъде: от кантарен лист, от таблица на клиента, от
    /// собствена сметка в Excel. Да се напечатат осемдесет реда на ръка е точно
    /// работата, която програмата трябва да спести — а редът на партидите вече
    /// се нагласява с подреждане по заглавие.
    ///
    /// Залепва се НАДОЛУ от текущия ред, по един ред на всяка линия. Празен ред
    /// маха количеството; нечетимото се пропуска, вместо да спре останалите.
    /// Повече от наличното се свежда до него — партидата няма толкова.
    ///
    /// Връща колко реда са поети.
    /// </summary>
    private int Paste(DataGridView grid, string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var at = grid.CurrentRow?.Index ?? 0;
        if (at < 0) at = 0;

        var taken = 0;

        foreach (var line in lines)
        {
            // Скритите от пресяването редове се прескачат: залепва се в
            // това, което се вижда, както в Excel.
            while (at < grid.Rows.Count && !grid.Rows[at].Visible) at++;

            if (at >= grid.Rows.Count) break;

            // От залепена област се взима първата колона: тук се пълни едно
            // число на ред, а не таблица.
            var cell = line.Split('\t')[0].Trim();

            // Последният празен ред на залепването не е стойност.
            if (cell.Length == 0 && line.Trim().Length == 0 && at == lines.Length - 1) break;

            if (grid == _origin) UseBatchTake(at, Read(cell));
            else UseTake(at, Read(cell));

            at++;
            taken++;
        }

        return taken;
    }

    /// <summary>Прочита едно залепено число. Нечетимото е нула, тоест „нищо“.</summary>
    private static decimal Read(string text) =>
        SplitMath.TryRead(text, out var amount) && amount > 0 ? amount : 0m;

    /// <summary>
    /// F4 отваря календара, откъдето и да е в прозореца.
    ///
    /// При повтарящо се предаване датата е единственото, което се мени, а тя е
    /// в горния ляв ъгъл — далеч от таблицата, в която стои ръката. Клавишът я
    /// докарва при човека, вместо човекът да ходи до нея.
    ///
    /// Самото падане на календара минава през клавиш към контролата: тя няма
    /// начин да се отвори отвън. Ако съобщителният цикъл не е вдигнат — както е
    /// в теста — остава фокусът, който е половината работа.
    /// </summary>
    internal void OpenCalendar()
    {
        _date.Focus();

        try
        {
            SendKeys.Send("%{DOWN}");
        }
        catch (InvalidOperationException)
        {
            // Без съобщителен цикъл: фокусът е сложен, календарът се отваря с F4.
        }
    }

    /// <summary>
    /// Курсорът застава на датата.
    ///
    /// Тя е единственото, което се мени при повтарящо се предаване: всичко
    /// останало вече е попълнено и избрано отзад.
    /// </summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _date.Focus();
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keys)
    {
        if (keys == (Keys.Control | Keys.F))
        {
            _search.Box.Focus();
            return true;
        }

        if (keys == Keys.F4 && !_date.Focused)
        {
            OpenCalendar();
            return true;
        }

        if (keys == (Keys.Control | Keys.V) && Table() is { } grid)
        {
            PasteInto(grid);
            return true;
        }

        return base.ProcessCmdKey(ref message, keys);
    }

    /// <summary>Коя таблица е под ръката — празно, ако курсорът е другаде.</summary>
    private DataGridView? Table()
    {
        if (_origin.Focused || _origin.IsCurrentCellInEditMode) return _origin;
        if (_rows.Focused || _rows.IsCurrentCellInEditMode) return _rows;

        return null;
    }

    /// <summary>Залепва от буфера и казва колко е поето.</summary>
    internal void PasteInto(DataGridView grid)
    {
        var text = string.Empty;

        try
        {
            if (Clipboard.ContainsText()) text = Clipboard.GetText();
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Буферът е зает от друга програма — залепването просто не става.
            return;
        }

        if (text.Length == 0) return;

        var taken = Paste(grid, text);

        if (taken == 0) return;

        _done.Text = taken == 1
            ? Lang.T("Залепен е 1 ред.", "1 row pasted.")
            : Lang.T($"Залепени са {taken} реда.", $"{taken} rows pasted.");
    }

    private string Value(ColumnDef column) =>
        _inputs.TryGetValue(column.Name, out var input)
            // Името на получателя е текст, а не код: „Претопи ООД — 205566778“
            // се свежда до името, а всичко останало минава през разделянето на
            // код и описание.
            ? column.Name == ReceiverName ? PartnerStore.NameOf(input.Text) : Code(input.Text)
            : string.Empty;

    /// <summary>
    /// Нарежда поправяното действие такова, каквото е записано: дата,
    /// полета, бележка и взетото от всяка партида. Оттам човек мени каквото е
    /// сгрешено — най-често датата — и записва поправката на мястото на
    /// старото.
    /// </summary>
    private void Reopen(AmendSeed amend)
    {
        var day = amend.Date.ToString(ValueConverter.BulgarianDateFormat);

        Text = Lang.T($"Поправка на предаване от {day}", $"Correcting the hand-over of {day}");

        // Поправката е едно действие: „и продължи“ би записало второ до нея.
        if (_more is not null) _more.Visible = false;

        if (_save is not null)
        {
            _save.Text = Lang.T("Запиши поправката", "Save the correction");
            _save.FitWidth();
        }

        foreach (var (name, value) in amend.Fields)
            if (_inputs.TryGetValue(name, out var input))
                input.Text = value;

        _note.Text = amend.Note;
        _contract.Text = amend.Contract;

        if (amend.EwcOut.Length > 0)
        {
            _sameCode.Checked = false;
            _outCode.Text = amend.EwcOut;
        }

        _perBatch.Checked = amend.Split;

        foreach (var pile in _piles) ApplyList(pile, amend.Taken(pile));

        // След количествата: кантарът се сверява с тях.
        if (amend.Target > 0) _target.Text = SplitMath.Amount(amend.Target);

        // Взетото — отгоре: обхватът може да е стотици партиди, а поправката е за шепа от тях.
        _originSort = OriginTake;
        _originDown = true;

        ShowOrigin();

        _notice.Say(amend.Notice(amend.Held));
    }

    /// <summary>Колко е било количеството на поправяното действие.</summary>
    private decimal Original => _amend is null ? 0m : SplitMath.Round(_amend.Moves.Sum(m => m.Amount));

    /// <summary>
    /// При поправка сборът носи и първоначалното количество.
    ///
    /// Най-често се мести датата — а на по-ранна дата в склада стои по-малко и
    /// поправката тихо взима по-малко. Първоначалното стои до новото, за да се
    /// види разликата, преди да се запише.
    /// </summary>
    private void ShowOriginal(decimal now)
    {
        if (_amend is null) return;

        var was = Original;
        var gap = SplitMath.Round(now - was);

        if (gap == 0)
        {
            _totals.Text += Lang.T($" · първоначално {SplitMath.Amount(was)} т, без промяна",
                                   $" · originally {SplitMath.Amount(was)} t, unchanged");
            _totals.ForeColor = Theme.Ink;

            return;
        }

        var sign = gap > 0 ? "+" : "−";

        _totals.Text += Lang.T(
            $" · първоначално {SplitMath.Amount(was)} т — промяна {sign}{SplitMath.Amount(Math.Abs(gap))} т",
            $" · originally {SplitMath.Amount(was)} t — change {sign}{SplitMath.Amount(Math.Abs(gap))} t");
        _totals.ForeColor = Theme.Warn;
    }

    // ------------------------------------------------------------- за теста

    /// <summary>Какво е спряло последната проверка — ползва се от тестовете.</summary>
    internal IReadOnlyList<string> Problems { get; private set; } = [];

    internal int RowCount => _rows.Rows.Count;

    /// <summary>Сменя датата, както човек би я сменил — списъкът се смята наново.</summary>
    internal void UseDate(DateOnly date) => _date.Value = date.ToDateTime(TimeOnly.MinValue);

    /// <summary>Натиска „Предай и продължи“.</summary>
    internal void HandAndContinue() => Continue();

    /// <summary>Колко предавания са записани, без прозорчето да се затваря.</summary>
    internal int Saved => _saved;

    /// <summary>Какво пише до копчетата за записаното дотук.</summary>
    internal string DoneText => _done.Text;

    /// <summary>Предлага ли се изобщо предаване — при празен списък няма какво.</summary>
    internal bool CanHand => _save?.Enabled ?? false;

    internal string TakeAt(int row) => _rows.Rows[row].Cells[TakeColumn].Value?.ToString() ?? string.Empty;

    internal string StockAt(int row) => _rows.Rows[row].Cells[StockColumn].Value?.ToString() ?? string.Empty;

    internal string BatchesAt(int row) => _rows.Rows[row].Cells[BatchColumn].Value?.ToString() ?? string.Empty;

    internal string SplitText => _split.Text;

    /// <summary>Какво пише до датата — в кой месец влиза записът.</summary>
    /// <summary>
    /// Побира ли се съобщението под датата.
    ///
    /// Мери се спрямо ширината на самия надпис: <c>MeasureText</c> с нулева
    /// ширина мери на един ред и би отчел за побрано всичко (виж
    /// АРХИТЕКТУРА.md). Ползва се от тестовете.
    /// </summary>
    internal bool WhenFits =>
        _when.ClientSize.Width <= 0 ||
        TextRenderer.MeasureText(
            _when.Text, _when.Font,
            new Size(_when.ClientSize.Width, int.MaxValue),
            TextFormatFlags.WordBreak).Height <= _when.ClientSize.Height;

    internal string WhenText => _when.Text;

    /// <summary>Предлага ли се изобщо оттегляне на работния период.</summary>
    internal bool OutsideOffered => _outsideShown;

    /// <summary>Вдига отметката „пиши извън работния период“, както би я вдигнал човек.</summary>
    internal void UseOutside(bool outside)
    {
        _outside.Checked = outside;
        ShowWhen();
    }

    /// <summary>
    /// Побира ли се датата цяла в рамката, която ѝ е дадена.
    ///
    /// Проверява се, защото надписът се мени с езика, а дясната рамка има
    /// най-малка ширина: отрязана дата се вижда чак на снимка от потребител.
    /// </summary>
    internal bool DateFits =>
        _date.Parent is { } line && _date.Right <= line.ClientSize.Width;

    /// <summary>Как е озаглавена датата.</summary>
    internal static string DateCaption => DateTitle;

    internal string TotalsText => _totals.Text;

    /// <summary>Сборът на маркираното — ползва се от тестовете.</summary>
    internal string PickedText => _picked.Text;

    /// <summary>Заглавието на колоната с количеството горе.</summary>
    internal string TakeHeaderText => _rows.Columns[TakeColumn].HeaderText;

    /// <summary>Заглавието на колоната с количеството долу.</summary>
    internal string BatchTakeHeaderText => _origin.Columns[OriginTake].HeaderText;

    internal void UseTake(int row, decimal amount)
    {
        _rows.CurrentCell = _rows.Rows[row].Cells[TakeColumn];
        _rows.Rows[row].Cells[TakeColumn].Value = SplitMath.Amount(amount);

        Remember();
        Weigh();
        ShowOrigin();
    }

    /// <summary>Влачи по редовете, както би влачил човек — избраното се прилага.</summary>
    internal void DragPiles(params int[] rows)
    {
        _rows.ClearSelection();

        foreach (var row in rows)
            if (row >= 0 && row < _rows.Rows.Count)
                _rows.Rows[row].Selected = true;

        Apply();
        ShowOrigin();
    }

    /// <summary>
    /// Избира код, както би го избрал човек с едно щракване.
    ///
    /// Прилага избора: точно това прави и мишката, откакто избраното Е изборът.
    /// </summary>
    internal void PickPile(int row)
    {
        // Щракването избира САМО този ред — дори когато клетката вече е текуща.
        // Без изричния избор куката се разминава с мишката: присвояването на
        // същата текуща клетка не прави нищо, а човек с мишката пак избира.
        _rows.CurrentCell = _rows.Rows[row].Cells[0];

        _filling = true;

        try
        {
            _rows.ClearSelection();
            _rows.Rows[row].Selected = true;
        }
        finally
        {
            _filling = false;
        }

        // През същия път, по който минава и мишката.
        Clicked(CodeColumn);
    }

    /// <summary>Щракване в клетката за количество — то НЕ мени избора.</summary>
    internal void PickTakeCell(int row)
    {
        _rows.CurrentCell = _rows.Rows[row].Cells[TakeColumn];

        Clicked(TakeColumn);
    }

    /// <summary>Натиска „Всичко“ или „Нищо“, както би ги натиснал човек.</summary>
    internal void UseQuick(bool whole) => FillTakes(whole);

    /// <summary>
    /// Избира партиди долу, както човек с Ctrl — и прилага избора.
    ///
    /// През същия метод, по който минава и мишката: кука, която прави нещо
    /// друго, показва работещо прозорче, докато на екрана то не работи.
    /// </summary>
    internal void PickBatches(params int[] rows)
    {
        _filling = true;

        try
        {
            _origin.ClearSelection();

            foreach (var row in rows)
                if (row >= 0 && row < _origin.Rows.Count)
                    _origin.Rows[row].Selected = true;
        }
        finally
        {
            _filling = false;
        }

        PickedBatches();
    }

    /// <summary>Пише колко да се вземе от една партида.</summary>
    internal void UseBatchTake(int row, decimal amount)
    {
        if (row < 0 || row >= _origin.Rows.Count) return;

        _origin.Rows[row].Cells[OriginTake].Value = SplitMath.Amount(amount);

        TookFromBatch(row);
    }

    /// <summary>Щраква заглавие на колона горе, както би щракнал човек.</summary>
    internal void SortPiles(int column) => SortRows(column);

    /// <summary>Щраква заглавие на колона долу.</summary>
    internal void SortShares(int column) => SortBatches(column);

    /// <summary>Накъде сочи стрелката на колона долу.</summary>
    internal string ShareGlyph(int column) =>
        _origin.Columns[column].HeaderCell.SortGlyphDirection.ToString();

    /// <summary>Кодът на ред горе — за проверка на подредбата.</summary>
    internal string CodeAt(int row) => _rows.Rows[row].Cells[CodeColumn].Value?.ToString() ?? string.Empty;

    /// <summary>Номерът на партидата на ред долу.</summary>
    internal string BatchAt(int row) => _origin.Rows[row].Cells[OriginBatch].Value?.ToString() ?? string.Empty;

    /// <summary>
    /// Залепва текст в колоната с количествата — както Ctrl+V от Excel.
    ///
    /// През същия метод, който минава и от клавиша; буферът не се пипа, защото
    /// на тестова нишка той е на цялата машина, а не на програмата.
    /// </summary>
    internal int PasteInto(bool batches, int from, string text)
    {
        var grid = batches ? _origin : _rows;

        if (from >= 0 && from < grid.Rows.Count) grid.CurrentCell = grid.Rows[from].Cells[0];

        return Paste(grid, text);
    }

    internal int OriginCount => _origin.Rows.Count;

    internal string OriginPartnerAt(int row) =>
        _origin.Rows[row].Cells[OriginPartner].Value?.ToString() ?? string.Empty;

    internal string OriginTakeAt(int row) =>
        _origin.Rows[row].Cells[OriginTake].Value?.ToString() ?? string.Empty;

    internal string BehindText => _behind.Text;

    /// <summary>Пише количество и натиска „Вземи“ над партидите, както човек.</summary>
    internal void UseFromTop(decimal amount)
    {
        _target.Text = SplitMath.Amount(amount);
        FromTop();
    }

    internal string FromTopText => _fromTop.SaidText;

    /// <summary>
    /// Отмахва клиент от партидите — както с десния бутон по „Клиент“, като се
    /// махне отметката от него.
    /// </summary>
    internal void HideBatchesOf(string part) =>
        _originFilter.Set(OriginPartner, _origin.Rows.Cast<DataGridViewRow>()
            .Select(r => r.Cells[OriginPartner].Value?.ToString() ?? string.Empty)
            .Where(v => !v.Contains(part, StringComparison.CurrentCulture))
            .Distinct()
            .ToList());

    /// <summary>Написаното в „Ще се вземе“ по всички редове вдясно, в реда им.</summary>
    internal IReadOnlyList<string> OriginTakes() =>
        [.. _origin.Rows.Cast<DataGridViewRow>().Select(r => r.Cells[OriginTake].Value?.ToString() ?? string.Empty)];

    /// <summary>Пише в търсенето над таблиците, както човек.</summary>
    internal void UseSearch(string text) => _search.Box.Text = text;

    /// <summary>Колко кода се виждат.</summary>
    internal int VisiblePiles => _rows.Rows.Cast<DataGridViewRow>().Count(r => r.Visible);

    internal bool CanGoFullScreen => MaximizeBox && FormBorderStyle == FormBorderStyle.Sizable;

    internal void UsePerBatch(bool perBatch)
    {
        _perBatch.Checked = perBatch;
        Weigh();
    }

    internal void UseField(string name, string value)
    {
        if (_inputs.TryGetValue(name, out var input)) input.Text = value;
    }

    /// <summary>Избира дейност от менюто, както би я избрал човек.</summary>
    internal bool UseOperation(int index)
    {
        if (!_inputs.TryGetValue("op_id_code", out var input) || input is not ComboBox combo) return false;
        if (index < 0 || index >= combo.Items.Count) return false;

        combo.SelectedIndex = index;
        return true;
    }

    internal string FieldText(string name) =>
        _inputs.TryGetValue(name, out var input) ? input.Text : string.Empty;

    /// <summary>
    /// Побират ли се всички полета в рамката си, без да се превърта.
    ///
    /// Проверява се на лаптоп: там те са по четири на ред и всяко ново поле може
    /// да избута последното на ред, който рамката отрязва.
    ///
    /// Мери се при ширината на рамката. Голото <c>PreferredSize</c> на пренасяща
    /// се лента е мерено без граница, на един ред, и казва „побира се“ при всяка
    /// подредба.
    /// </summary>
    internal bool FieldsFit
    {
        get
        {
            if (_fieldsFrame is null || _fieldsHost is null) return false;

            var room = _fieldsFrame.ClientSize;
            var wide = room.Width - _fieldsFrame.Padding.Horizontal;

            return _fieldsHost.GetPreferredSize(new Size(wide, 0)).Height <= room.Height - _fieldsFrame.Padding.Vertical;
        }
    }

    /// <summary>Пише договор в полето, както би го написал човек.</summary>
    internal void UseContract(string text) => _contract.Text = text;

    /// <summary>Колко договора предлага полето.</summary>
    internal int ContractsOffered => _contract.Items.Count;

    /// <summary>Какво предлага полето на този ред от менюто.</summary>
    internal string ContractAt(int index) => _contract.Items[index]?.ToString() ?? string.Empty;

    /// <summary>Какво пише в полето — без изчистването при записа.</summary>
    internal string ContractText => _contract.Text;

    /// <summary>Заявеното от кантара, както се чете в момента.</summary>
    internal decimal TargetNow => Target;

    /// <summary>
    /// Побират ли се стълбовете долу. Таблицата превърта и отвътре нищо не изглежда
    /// отрязано, а десният стълб е взетото от всяка партида — заради него е поправката.
    /// </summary>
    internal bool OriginColumnsFit
    {
        get
        {
            var need = _origin.Columns.Cast<DataGridViewColumn>().Sum(c =>
                c.AutoSizeMode == DataGridViewAutoSizeColumnMode.Fill ? c.MinimumWidth : c.Width);

            return need <= _origin.ClientSize.Width - SystemInformation.VerticalScrollBarWidth;
        }
    }

    /// <summary>Какво казва лентата при поправка.</summary>
    internal string NoticeText => _notice.Text;

    /// <summary>Побира ли се лентата — на лаптоп текстът се пренася на повече редове.</summary>
    internal bool NoticeFits => _notice.Fits;

    /// <summary>Отделен ред ли е за всяка партида.</summary>
    internal bool PerBatchOn => _perBatch.Checked;

    /// <summary>Колко партиди предлага прозорецът.</summary>
    internal int BatchesOffered => _piles.Sum(p => p.Shares.Count);

    /// <summary>Колко получателя предлага полето.</summary>
    internal int ReceiversOffered =>
        _inputs.TryGetValue(ReceiverName, out var input) && input is ComboBox who
            ? who.Items.Count
            : -1;

    /// <summary>Какво предлага полето на този ред от менюто.</summary>
    internal string ReceiverAt(int index) =>
        _inputs.TryGetValue(ReceiverName, out var input) && input is ComboBox who &&
        index >= 0 && index < who.Items.Count
            ? who.Items[index]?.ToString() ?? string.Empty
            : string.Empty;

    /// <summary>Избира получател от менюто, както би го избрал човек.</summary>
    internal bool UseReceiver(int index)
    {
        if (!_inputs.TryGetValue(ReceiverName, out var input) || input is not ComboBox who) return false;
        if (index < 0 || index >= who.Items.Count) return false;

        who.SelectedIndex = index;
        return true;
    }

    /// <summary>Пише получател на ръка — както при непознат контрагент.</summary>
    internal void TypeReceiver(string name)
    {
        if (!_inputs.TryGetValue(ReceiverName, out var input)) return;

        input.Text = name;

        if (input is ComboBox who) Received(who);
    }

    /// <summary>Сглобява движенията, както копчето би ги сглобило.</summary>
    internal IReadOnlyList<HandMove> Planned() =>
        Handover.From(Picks(), _perBatch.Checked, OutCode());

    /// <summary>Празно значи „същия код“; иначе общият код за всички редове.</summary>
    internal void UseOutCode(string ewc)
    {
        _sameCode.Checked = ewc.Length == 0;
        _outCode.Text = ewc;
        Weigh();
    }

    internal bool OutCodeOffered => _outCode.Enabled;

    /// <summary>Пряко ли е предаването — страницата отзад го записва в движението.</summary>
    internal bool Direct => _direct;

    /// <summary>Какво спира записа в този момент — без да се пипа нищо.</summary>
    internal IReadOnlyList<string> Blocking()
    {
        Ready(out _, out _);
        return Problems;
    }

    /// <summary>Пише заявеното общо количество, както човек би го написал.</summary>
    internal void UseTarget(decimal amount)
    {
        _target.Text = amount > 0 ? SplitMath.Amount(amount) : string.Empty;
        Weigh();
    }

    /// <summary>Как е озаглавен кантарът.</summary>
    internal static string TargetCaption => TargetTitle;

    /// <summary>Какво казва редът за разликата до заявеното.</summary>
    internal string GapText => _gap.Text;

    /// <summary>Кой ред ще поеме разликата — както прозорчето го обявява.</summary>
    internal string AnchorText => _anchor.Text;

    /// <summary>Как е озаглавено менюто за поемащия ред.</summary>
    internal static string IntoCaption => IntoTitle;

    /// <summary>Колко реда предлага менюто, заедно с „редът, на който стоя“.</summary>
    internal int IntoCount => _into.Items.Count;

    /// <summary>Какво пише на ред от менюто.</summary>
    internal string IntoAt(int index) =>
        index >= 0 && index < _into.Items.Count ? _into.Items[index]?.ToString() ?? string.Empty : string.Empty;

    /// <summary>Кой ред е посочен в менюто.</summary>
    internal int IntoPicked => _into.SelectedIndex;

    /// <summary>Посочва ред от менюто, както би го посочил човек.</summary>
    internal void UseInto(int index)
    {
        if (index < 0 || index >= _into.Items.Count) return;

        _into.SelectedIndex = index;
    }

    /// <summary>
    /// Стои ли всичко в рамката на кантара вътре в нея.
    ///
    /// Лентата расте на два реда, а рамката — с нея. Забрави ли се второто,
    /// вторият ред ляга върху изречението, което казва кой ред поема разликата,
    /// и точно то спира да се чете.
    /// </summary>
    internal bool ScaleFits =>
        _scale is { } card &&
        card.Controls.Cast<Control>().All(c => c.Bottom <= card.ClientSize.Height);

    /// <summary>Остава ли менюто в рамката и достатъчно широко, за да се чете.</summary>
    internal bool IntoFits =>
        _into.Parent is { } host &&
        _into.Right <= host.ClientSize.Width &&
        _into.Width >= Theme.Scaled(120);

    /// <summary>Предлага ли се изравняване — при съвпаднали числа няма какво.</summary>
    internal bool LevelOffered => _level?.Enabled ?? false;

    /// <summary>Натиска „Изравни“ — разликата сяда в текущия ред.</summary>
    internal void UseLevel() => Level();

    /// <summary>Натиска „Пропорционално“.</summary>
    internal void UseProportional() => Proportional();

    /// <summary>Сборът на посоченото по партиди — истината за предаването.</summary>
    internal decimal PickedTotal => SplitMath.Round(Picks().Sum(p => p.Amount));

    /// <summary>
    /// Побира ли се кантарът цял в рамката, която му е дадена.
    ///
    /// Същата проверка като на датата и по същата причина: надписът се мени с
    /// езика, а дясната рамка има най-малка ширина.
    /// </summary>
    internal bool TargetFits =>
        _target.Parent is { } line && _target.Right <= line.ClientSize.Width;

    /// <summary>
    /// Побират ли се двете копчета за изравняване в реда си.
    ///
    /// „Пропорционално“ е дълга дума и на двата езика, а рамката вдясно е
    /// най-тясната в прозореца: отрязано копче се вижда чак на снимка от
    /// потребител.
    /// </summary>
    internal bool BalanceFits =>
        _spread?.Parent is { } row && _spread.Right <= row.Parent?.ClientSize.Width;

    // ------------------------------------------------------------- помощни

    /// <summary>„161004 — Отпадъци от…“ обратно до „161004“.</summary>
    private static string Code(string? display)
    {
        var text = display?.Trim() ?? string.Empty;
        var dash = text.IndexOf(" — ", StringComparison.Ordinal);

        return dash > 0 ? text[..dash].Trim() : text;
    }

    private string Describe(string ewc) =>
        _names.TryGetValue(ewc, out var name) ? $"{ewc} — {name}" : ewc;

    /// <summary>Номенклатурата на кодовете — от схемата, не от списък в кода.</summary>
    private static Dictionary<string, string> EwcNames(SchemaDefinition schema)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        var column = schema.FindReport(BookNodes.Report)?
            .SelfAndDescendants()
            .FirstOrDefault(n => n.ElementName == Moves.Node(MoveKind.Received))?
            .Attributes.FirstOrDefault(c => c.Name == "ewc");

        foreach (var option in column?.Domain.Options ?? [])
            if (option.Description is { Length: > 0 } description)
                names[option.Value] = description;

        return names;
    }
}
