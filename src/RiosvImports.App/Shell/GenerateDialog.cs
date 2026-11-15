using RiosvImports.App.Controls;
using RiosvImports.App.Editor;
using RiosvImports.Core;
using RiosvImports.Core.Ledger;
using RiosvImports.Core.Localization;
using RiosvImports.Core.Schema;
using RiosvImports.Core.Values;
using RiosvImports.Data;

namespace RiosvImports.App.Shell;

/// <summary>
/// Образуване от склад „Третиран“.
///
/// Същото, което правят копчетата „Обр.“ и „Дели“ в отчета — ред от „Третиран“
/// става един или няколко реда в „Образуван“ — но тръгнало от там, където човек
/// наистина гледа: чакащото на площадката.
///
/// Разликата с третирането е една и е цялата причина за отделен прозорец:
/// <b>от един вход излизат няколко изхода</b>. Петдесет тона стар кабел се
/// разпадат на мед, пластмаса и остатък — три кода по ЕКО, три реда в книгата,
/// и сборът им трябва да е точно петдесет, защото НИСО брои до грам.
///
/// Затова екранът има три таблици, а не две: вляво горе — в какви кодове
/// излиза, вляво долу — от коя купчина и колко, а вдясно, на цялата височина —
/// от кои партиди. Горната вляво е новото; другите две се четат както в
/// „Третиране от склада“ и нарочно изглеждат по същия начин.
///
/// Произходът не се пита. Образуваното наследява дяловете на третираното в
/// тяхното съотношение (<see cref="Allocation.Inherit"/>) — друго съотношение
/// никой не е измервал, а измисленото изглежда като отговор.
///
/// Полетата горе не са изброени в кода: те идват от възела на схемата, както
/// навсякъде другаде (правило 1). „Описание на отпадъка“ е изключението — то е
/// името на кода по ЕКО и се различава от ред до ред, затова не стои горе, а се
/// изписва срещу всеки код.
///
/// <b>Същият прозорец рециклира.</b> Рециклирането е образуване с друг край:
/// същите купчини от „Третиран“, същото разделяне по кодове, същите шаблони и
/// същото наследяване на произхода — само че излязлото отива в склад
/// „Рециклиран“ и не влиза в отчета. Нарочно не е втори прозорец: втори начин
/// за същата работа струва преучване на човека и разминаване в кода. Разликата
/// е в думите и в полетата горе — рециклираното не отива в книгата и полетата
/// на книгата нямат какво да правят тук.
/// </summary>
internal sealed class GenerateDialog : Dialog
{
    private const string CodeColumn = "__code";
    private const string BatchColumn = "__batches";
    private const string AgeColumn = "__age";
    private const string StockColumn = "__stock";
    private const string TakeColumn = "__take";
    private const string OutColumn = "__out";

    private const string OriginBatch = "__obatch";
    private const string OriginPartner = "__opartner";
    private const string OriginDocument = "__odocument";
    private const string OriginStock = "__ostock";
    private const string OriginTake = "__otake";

    private readonly Workspace _workspace;
    private readonly long _companyId;
    private readonly string _stagenum;

    /// <summary>
    /// Какво става от купчините — образуван или рециклиран отпадък.
    ///
    /// Едно поле за цялата разлика между двете: думите, полетата на книгата и
    /// вида на движенията, които прозорецът връща. Сметката отдолу е една.
    /// </summary>
    private readonly MoveKind _kind;

    /// <summary>Дали прозорецът рециклира, а не образува.</summary>
    private bool Recycling => _kind == MoveKind.Recycled;

    /// <summary>
    /// Купчините, които чакат в склад „Третиран“.
    ///
    /// Не са окончателни: смяната на датата ги смята наново, защото на друга
    /// дата в склада чака друго. Затова не са readonly.
    /// </summary>
    private IReadOnlyList<TreatPile> _piles;

    /// <summary>Кое е избрано отзад — по двойки „код и партида“.</summary>
    private readonly HashSet<(string Ewc, long Batch)> _chosen;

    private readonly DateOnly _on;

    private readonly ReportNode? _node;
    private readonly ColumnDef? _ewc;
    private readonly IReadOnlyList<EnumOption> _offered;

    /// <summary>
    /// Кодът, с който тръгва нов ред, когато е останал един — иначе празно.
    ///
    /// Площадка, която образува само един код, го пише на всеки ред цял живот.
    /// Стои като текст, а не като избор: в клетката стойността влиза и с писане,
    /// и от шаблон, и двете трябва да могат да го изместят.
    /// </summary>
    private readonly string _sole;
    private readonly Dictionary<string, Control> _inputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _names;

    private readonly DateTimePicker _date = new();
    private readonly TextBox _note = new();
    private readonly CheckBox _perPile = new();

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

    /// <summary>Надписът над купчините — казва и какво е пресято.</summary>
    private readonly Label _what = new();

    /// <summary>Рамката, в която стоят редовете с изходните кодове.</summary>
    private readonly Panel _partHost = new();

    /// <summary>На колко кода се дели купчината — „Брой“, както в отчета.</summary>
    private readonly NumericUpDown _count = new();

    /// <summary>Самите редове. Живеят, докато прозорчето е отворено.</summary>
    private readonly List<Line> _lines = [];

    private readonly Label _codeCaption = new();
    private readonly Label _amountCaption = new();
    private readonly Label _percentCaption = new();

    /// <summary>Тоновете и процентите се пишат един друг — това спира кръга.</summary>
    private bool _syncing;

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

    private readonly ComboBox _templates = new();
    private readonly Label _recipe = new();
    private readonly Label _behind = new();
    private readonly Label _split = new();
    private readonly Label _totals = new();
    private readonly Label _picked = new();
    /// <summary>Какво трябва да се знае при поправка — видимо през цялата ширина.</summary>
    private readonly NoticeStrip _notice = new();

    private readonly Label _done = new();

    /// <summary>Лентата с шаблоните и копчетата над сбора — мери се от теста.</summary>
    private FlowLayoutPanel? _tools;

    private AppButton? _save;
    private AppButton? _more;
    private AppButton? _spread;

    /// <summary>„Рециклирай“ — вторият изход от образуването.</summary>
    private AppButton? _recycle;

    /// <summary>Колко рециклирания са записани оттук, докато прозорецът стои отворен.</summary>
    private int _recycled;

    /// <summary>Записаните разкомплектования — общи с прозорчето „Дели“ в отчета.</summary>
    private List<SplitTemplate> _stored;

    /// <summary>Колко образувания са записани, докато прозорчето стои отворено.</summary>
    private int _written;

    /// <summary>
    /// По колко се взима от всяка чакаща партида.
    ///
    /// Тук стои истината за изчерпването. Горната таблица е сборът ѝ по кодове,
    /// а долната вдясно — самите редове; и двете пишат в нея.
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

    /// <summary>Каквото човекът е написал по код — за да не се губи при пресичане.</summary>
    private readonly Dictionary<string, decimal> _wanted = new(StringComparer.Ordinal);

    /// <summary>
    /// В какво се образува всяка купчина.
    ///
    /// По код, а не по ред от таблицата: редовете се пресмятат наново при всяка
    /// смяна на датата, а рецептата „кабелът дава мед, пластмаса и остатък“ е
    /// решение на човека и няма защо да се пише пак.
    /// </summary>
    private readonly Dictionary<string, List<GenPart>> _recipes = new(StringComparer.Ordinal);

    /// <summary>
    /// Дяловете в проценти по купчина — истината, от която идват тоновете.
    ///
    /// Дотук се помнеха само тоновете и при всяка смяна на количеството дялът
    /// се смяташе обратно от тях: закръглените до грам тонове връщат дял, който
    /// вече не е написаният, а при нула — никакъв, и всичко падаше в първия
    /// ред. Тук процентите стоят такива, каквито са избрани или написани, и
    /// всяко ново количество се дели по тях.
    /// </summary>
    private readonly Dictionary<string, List<(string Ewc, decimal Percent)>> _ratios = new(StringComparer.Ordinal);

    /// <summary>Кой шаблон е избран за купчината — менюто го показва, щом тя е под курсора.</summary>
    private readonly Dictionary<string, string> _attached = new(StringComparer.Ordinal);

    /// <summary>Избраните редове преди писане на количество — връщат се след него.</summary>
    private List<int> _held = [];
    private List<int> _kept = [];

    private string _rowsSort = CodeColumn;
    private bool _rowsDown;
    private string _originSort = OriginDocument;
    private bool _originDown;

    /// <summary>Коя таблица е пипана последна — по нея се брои маркираното.</summary>
    private DataGridView? _last;

    private bool _filling;

    /// <summary>
    /// Двата разделителя — пазят се, за да бъдат сложени по мярка при показване.
    ///
    /// В конструктора това не става: рамките още нямат размер и
    /// <see cref="SplitContainer.SplitterDistance"/> би се сметнал спрямо
    /// сто и петдесет пиксела, а зададен минимум върху неоразмерена контрола
    /// хвърля („SplitterDistance must be between…“). Затова тук се пази кой е
    /// разделителят, а мястото му се дава, щом прозорецът има ширина.
    /// </summary>
    private SplitContainer? _mainSplit;
    private SplitContainer? _sideSplit;

    /// <summary>Разделителите се слагат веднъж — после мястото им е на човека.</summary>
    private bool _placed;

    public GenerateDialog(
        ImportService service,
        Workspace workspace,
        long companyId,
        string stagenum,
        IReadOnlyList<StockShare> shares,
        DateOnly on,
        AmendSeed? amend = null,
        MoveKind kind = MoveKind.Generated)
    {
        _workspace = workspace;
        _companyId = companyId;
        _stagenum = stagenum;
        _on = on;
        _amend = amend;

        // Само двете движения, които излизат от „Третиран“; всичко друго тук
        // би било образуване.
        _kind = kind == MoveKind.Recycled ? MoveKind.Recycled : MoveKind.Generated;

        if (amend is not null) _gone.UnionWith(amend.Gone);
        _piles = Generation.Piles(shares);

        // При поправка обхватът е този на действието, а не наличното в момента: свито
        // до нула на тази дата, то пак трябва да се върне на друга.
        _chosen = amend?.Scope is { } scope
            ? [.. scope]
            : [.. shares.Select(s => (ValueConverter.NormalizeCode(s.Ewc), s.Batch.Id))];

        _period = workspace.Periods.Window(companyId);

        var schema = service.GetSchema("book");

        _node = schema.FindReport(BookNodes.Report)?
            .SelfAndDescendants()
            .FirstOrDefault(n => n.ElementName == Moves.Node(MoveKind.Generated));

        _ewc = _node?.Attributes.FirstOrDefault(c => c.Name == "ewc");

        var limits = BookLimits.Of(_workspace, _companyId);

        _offered = _ewc is null
            ? []
            : limits.Offer(BookNodes.Report, Moves.Node(MoveKind.Generated), _ewc);

        _sole = BookLimits.Sole(limits, MoveKind.Generated, _ewc);

        _names = EwcNames(schema);

        // Шаблоните са същите, които пази прозорчето „Дели“ в отчета: ключът е
        // възелът на схемата и в двата случая. Човек, записал „Стар кабел“
        // веднъж, го намира и на двете места — иначе би го правил два пъти.
        //
        // Рециклирането чете СЪЩИТЕ шаблони: купчината се разпада на същите
        // кодове, каквото и да стане с тях после.
        _stored = SplitTemplates.Load(_workspace.Settings, Moves.Node(MoveKind.Generated));

        Text = Heading;

        MaximizeBox = true;
        FormBorderStyle = FormBorderStyle.Sizable;
        WindowState = FormWindowState.Maximized;

        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;

        ClientSize = new Size(
            // Никога повече, отколкото екранът има: при 150 % желаното в
            // пиксели е един път и половина по-голямо и без този таван
            // прозорчето би поискало ширина, каквато лаптопът няма.
            Math.Clamp(screen.Width - 160,
                Math.Min(Theme.Scaled(1060), screen.Width),
                Math.Max(Theme.Scaled(1480), Theme.Scaled(1060))),
            Math.Min(Theme.Scaled(900), Math.Max(Theme.Scaled(620), screen.Height - 160)));

        MinimumSize = new Size(
            Math.Min(Theme.Scaled(1040), screen.Width),
            Math.Min(Theme.Scaled(640), screen.Height));
        BackColor = Theme.Canvas;
        Font = Theme.Base;

        // Редът на добавяне е нарочен и е същият като в TreatDialog: закаченото
        // за ръбовете се добавя СЛЕД това, което пълни средата.
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
    public IReadOnlyList<GenMove> Result { get; private set; } = [];

    /// <summary>Датата на образуването — една за всички редове.</summary>
    public DateOnly Date => DateOnly.FromDateTime(_date.Value);

    /// <summary>Колоните от схемата, попълнени веднъж за всички редове.</summary>
    public IReadOnlyDictionary<string, string> Fields { get; private set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Бележката, ако има такава — една за всички редове.</summary>
    public string Note => _note.Text.Trim();

    /// <summary>Какво трябва да се помни за прозореца, за да се отвори същият при поправка.</summary>
    internal ActionWindow Window =>
        new([.. _chosen.Select(c => new WindowShare(c.Ewc, c.Batch))], Split: _perPile.Checked);

    /// <summary>
    /// Извън работния период ли се пише. Поправка на старата дата не е: действието
    /// е минало през тази проверка при записа си и отметка за него би значила само
    /// още едно щракване. Сменена дата е ново решение и се пита.
    /// </summary>
    private bool OutsidePeriod => !_period.Covers(Date) && _amend?.Date != Date;

    /// <summary>
    /// Кой записва сглобеното образуване.
    ///
    /// Прозорчето не пише в базата само̀: правилото как образуване става движение
    /// живее в страницата отзад и там си остава — иначе същото правило щеше да
    /// има две копия, а второто рано или късно се разминава с първото.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<IReadOnlyList<GenMove>, DateOnly, IReadOnlyDictionary<string, string>, string, int>? Write
    {
        get; set;
    }

    /// <summary>
    /// Кой записва избраното като РЕЦИКЛИРАНО — вторият изход от образуването.
    ///
    /// Подава го страницата, както <see cref="Write"/>: правилото как
    /// рециклиране става движение живее там. Подадено ли е, копчето
    /// „Рециклирай“ се показва. При поправка не се показва: поправката е едно
    /// действие и си остава онова, което е било.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<IReadOnlyList<GenMove>, DateOnly, IReadOnlyDictionary<string, string>, string, int>? WriteRecycled
    {
        get => _writeRecycled;
        set
        {
            _writeRecycled = value;

            if (_recycle is null) return;

            _recycle.Visible = RecycleOffered;
        }
    }

    private Func<IReadOnlyList<GenMove>, DateOnly, IReadOnlyDictionary<string, string>, string, int>? _writeRecycled;

    /// <summary>Предлага ли се рециклиране от този прозорец.</summary>
    private bool RecycleOffered => _writeRecycled is not null && !Recycling && _amend is null;

    /// <summary>Дали копчето за записване се побира цяло в прозореца.</summary>
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
            Text = Heading,
            Font = Theme.Title,
            ForeColor = Theme.Ink
        });

        host.Controls.Add(new Label
        {
            Dock = DockStyle.Bottom,

            // Два реда, а не един: обяснението е две изречения на два езика и
            // на един ред се реже точно там, където обяснява.
            Height = Theme.Scaled(48),
            Text = Recycling
                ? Lang.T(
                    "Рециклираното излиза от третираното като образуваното, но НЕ влиза в отчета — отива в склад " +
                    "„Рециклиран“. Вляво горе — в какви кодове, под него — от коя купчина и колко; вдясно — от кои " +
                    "партиди. Сборът на кодовете трябва да е точно колкото се изчерпва. " +
                    "Заглавие: щракване подрежда, десен бутон пресява.",
                    "Recycled waste leaves the treated waste like generated waste, but does NOT enter the report — it " +
                    "goes to the “Recycled” store. Top left, the codes; below, the pile and how much of it; on the " +
                    "right, which batches. The codes must add up to exactly what is drawn. " +
                    "Header: click sorts, right-click filters.")
                : Lang.T(
                    "Обявява се какво е излязло от третираното. Вляво горе — в какви кодове излиза, под него — от коя " +
                    "купчина и колко; вдясно — от кои партиди. Сборът на кодовете трябва да е точно колкото се " +
                    "изчерпва, а произходът се наследява от третираното. " +
                    "Заглавие: щракване подрежда, десен бутон пресява.",
                    "State what came out of the treated waste. Top left, the codes it comes out as; below, the pile and " +
                    "how much of it; on the right, which batches. The codes must add up to exactly what is drawn, " +
                    "and the origin is inherited from the treatment. " +
                    "Header: click sorts, right-click filters."),
            Font = Theme.Base,
            ForeColor = Theme.InkMuted
        });

        return host;
    }

    /// <summary>Заглавието на прозореца — според това какво се прави.</summary>
    private string Heading => Recycling
        ? Lang.T("Рециклиране от склада", "Recycling from the store")
        : Lang.T("Образуване от склада", "Generation from the store");

    /// <summary>
    /// Полетата, които важат за цялото образуване.
    ///
    /// В превъртаща се рамка с определена височина: те идват от схемата и не се
    /// знае колко ще са.
    /// </summary>
    private Control BuildFields()
    {
        var frame = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(84),
            AutoScroll = true,
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, 0, Theme.GapLarge, Theme.Gap)
        };

        var host = new FlowLayoutPanel
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

        host.Controls.Add(Field(Lang.T("Бележка", "Note"), _note));

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
            Width = Math.Max(Theme.Scaled(150), TextRenderer.MeasureText(DateText, Theme.Bold).Width + Theme.Scaled(6)),
            Text = DateText,
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

    private static string DateTitle => Lang.T("Дата на образуване", "Date of generation");

    /// <summary>Надписът на датата в ТОЗИ прозорец — образуване или рециклиране.</summary>
    private string DateText => Recycling
        ? Lang.T("Дата на рециклиране", "Date of recycling")
        : DateTitle;

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
    /// Колоните на възела без онези, които складът води сам, и без описанието.
    ///
    /// Датата, кодът и количеството са самото движение; площадката идва от
    /// страницата. „Описание на отпадъка“ отпада по друга причина: то е името на
    /// кода по ЕКО, а кодовете тук са няколко и всеки носи своето. Едно общо
    /// поле би сложило името на медта и срещу пластмасата.
    ///
    /// При рециклиране няма нито едно: полетата са на книгата, а рециклираното
    /// в книгата не отива. Задължително поле там би спирало запис, който никой
    /// отчет няма да прочете.
    /// </summary>
    private IEnumerable<ColumnDef> Extras() =>
        Recycling
            ? []
            : _node?.Attributes.Where(c => !BookNodes.Owned.Contains(c.Name) && c.Name != "waste_desc") ?? [];

    private static string Caption(ColumnDef column) => ReportTitles.ColumnShort(column);

    private Control Input(ColumnDef column)
    {
        Control input;

        if (column.Domain.IsEnum && !column.Domain.IsLargeEnum)
        {
            var combo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDown,
                AutoCompleteMode = AutoCompleteMode.SuggestAppend,
                AutoCompleteSource = AutoCompleteSource.ListItems
            };

            BookLimits.Offer(
                combo, BookLimits.Of(_workspace, _companyId), MoveKind.Generated, column);

            input = combo;
        }
        else
        {
            input = new TextBox();
        }

        _inputs[column.Name] = input;
        return input;
    }

    private Control BuildRows()
    {
        var host = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, 0, Theme.GapLarge, Theme.Gap)
        };

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = Theme.Scaled(6),
            BackColor = Theme.Canvas
        };

        split.Panel1.BackColor = Theme.Canvas;
        split.Panel2.BackColor = Theme.Canvas;

        split.Panel1.Controls.Add(BuildLeft());
        split.Panel2.Controls.Add(BuildRight());

        var sums = new Panel { Dock = DockStyle.Bottom, Height = Theme.Scaled(28), BackColor = Theme.Canvas };

        _split.Dock = DockStyle.Bottom;
        _split.Height = Theme.Scaled(24);
        _split.Font = Theme.Small;
        _split.ForeColor = Theme.InkMuted;

        _totals.Dock = DockStyle.Fill;
        _totals.Font = Theme.Bold;
        _totals.ForeColor = Theme.Ink;
        _totals.TextAlign = ContentAlignment.MiddleLeft;

        _picked.Dock = DockStyle.Right;
        _picked.Width = Theme.Scaled(420);
        _picked.Font = Theme.Bold;
        _picked.ForeColor = Theme.InkFaint;
        _picked.TextAlign = ContentAlignment.MiddleRight;

        // Пълнещият влиза пръв: закачените се редят наопаки.
        sums.Controls.Add(_totals);
        sums.Controls.Add(_picked);

        host.Controls.Add(split);
        host.Controls.Add(_search.Row());
        host.Controls.Add(_split);
        host.Controls.Add(sums);

        _mainSplit = split;

        // Главният разделител се слага веднага: той сам изчаква истинската
        // ширина и после следва прозореца (виж Dialog.Split). Страничният
        // изчаква показването — той дели по височина и няма своя мярка.
        //
        // Вляво стоят разпределението и кодовете една под друга и лявата рамка
        // иска ширината на кодовете (шест колони); вдясно остава колкото искат
        // партидите (пет). По-малко от това и колоната, в която се пише, остава
        // зад воден плъзгач. На широк екран партидите взимат повечето.
        Split(split, 700, choiceMin: 800);

        return host;
    }

    /// <summary>Вляво: от коя купчина и колко се изчерпва.</summary>
    private Control BuildPiles()
    {
        var host = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Canvas,
            Padding = new Padding(0, Theme.GapSmall, 0, 0)
        };

        var head = new Panel { Dock = DockStyle.Top, Height = Theme.Scaled(30), BackColor = Theme.Canvas };

        _perPile.Dock = DockStyle.Right;
        _perPile.Width = Theme.Scaled(300);
        _perPile.Text = Lang.T("Отделен ред за всяка купчина", "A separate row for every pile");
        _perPile.ForeColor = Theme.Ink;
        _perPile.BackColor = Color.Transparent;
        _perPile.CheckedChanged += (_, _) => Weigh();

        head.Controls.Add(_perPile);

        _what.Dock = DockStyle.Fill;
        _what.Text = WhatTitle;
        _what.Font = Theme.SmallBold;
        _what.ForeColor = Theme.InkFaint;
        head.Controls.Add(_what);

        Dress(_rows);

        _rows.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _rows.MultiSelect = true;
        _rows.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;

        // Левият бутон подрежда, десният пресява — както в редактора на отчета.
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

        _rows.Columns.Add(Plain(CodeColumn, Lang.T("Код", "Code"), 280, fill: true));
        _rows.Columns.Add(Number(BatchColumn, Lang.T("Партиди", "Batches"), 84, readOnly: true));
        _rows.Columns.Add(Plain(AgeColumn, Lang.T("Най-стара", "Oldest"), 96));
        _rows.Columns.Add(Number(StockColumn, Lang.T("Чака, т", "Waiting, t"), 106, readOnly: true));
        _rows.Columns.Add(Number(TakeColumn, TakeTitle, 144, readOnly: false));
        _rows.Columns.Add(Plain(OutColumn, Lang.T("Излиза като", "Comes out as"), 170));

        _rows.CellBeginEdit += (_, _) =>
            _held = [.. _rows.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index)];

        _rows.CellEndEdit += (_, _) =>
        {
            Remember();
            Weigh();
            Restore();
            ShowPile();
        };

        _rows.SelectionChanged += (_, _) =>
        {
            ShowPile();
            ShowPicked();
        };

        _rows.Enter += (_, _) => { _last = _rows; ShowPicked(); };

        _rows.MouseUp += (_, e) => Clicked(Column(_rows, e));

        _rows.KeyUp += (_, e) =>
        {
            if (e.KeyCode is not (Keys.Up or Keys.Down or Keys.Home or Keys.End
                or Keys.PageUp or Keys.PageDown or Keys.Space or Keys.A)) return;

            if (_rows.IsCurrentCellInEditMode) return;

            Apply();
            ShowPile();
        };

        host.Controls.Add(Frame(_rows));
        host.Controls.Add(head);

        return host;
    }

    /// <summary>
    /// Вляво: датата, под нея в какво излиза, а най-долу кодовете.
    ///
    /// Разпределението по кодове е работата, заради която прозорчето е
    /// отделно, и стои горе, до датата — там, където окото тръгва. Кодовете
    /// рядко са повече от няколко и им стига долната част. Партидите са
    /// десетки и взимат цялата дясна половина.
    /// </summary>
    private Control BuildLeft()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas };

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = Theme.Scaled(6),
            BackColor = Theme.Canvas
        };

        split.Panel1.BackColor = Theme.Canvas;
        split.Panel2.BackColor = Theme.Canvas;

        split.Panel1.Controls.Add(BuildParts());
        split.Panel2.Controls.Add(BuildPiles());

        _sideSplit = split;

        // Пълнещият влиза пръв (виж АРХИТЕКТУРА.md).
        host.Controls.Add(split);
        host.Controls.Add(BuildDate());

        return host;
    }

    /// <summary>Вдясно: кои партиди стоят зад текущия код — цялата височина.</summary>
    private Control BuildRight()
    {
        var host = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.Gap, 0, 0, 0)
        };

        host.Controls.Add(BuildOrigin());

        return host;
    }

    /// <summary>
    /// Редакторът, заради който прозорчето е отделно: едно към няколко.
    ///
    /// Устроен е като прозорчето „Дели“ в отчета и нарочно до последната
    /// подробност — там този начин на писане е изкаран докрай и хората вече го
    /// знаят: „Брой“ казва на колко кода се дели, всеки ред има падащо меню с
    /// кодовете, а тоновете и процентите вървят заедно — пише се което е под
    /// ръка, вижда се и другото.
    ///
    /// Редовете НЕ се правят наново при всяка смяна. Всяко падащо меню носи
    /// осемстотин кода и пълното пресъздаване струваше по секунда на ред —
    /// урокът е платен веднъж в <c>SplitDialog</c> и не се плаща втори път.
    /// Тук той важи двойно: купчината се сменя с всяко щракване вляво, а всяко
    /// щракване иначе би преправяло целия списък.
    /// </summary>
    private Control BuildParts()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas };

        var head = new Panel { Dock = DockStyle.Top, Height = Theme.Scaled(34), BackColor = Theme.Canvas };

        // „Брой“ е първото, което се пипа: то казва на колко кода се дели.
        _count.Dock = DockStyle.Right;
        _count.Width = Theme.Scaled(54);
        _count.Minimum = 1;
        _count.Maximum = 20;
        _count.Value = 1;
        _count.Font = Theme.Base;
        _count.TextAlign = HorizontalAlignment.Center;
        Theme.StyleInput(_count);
        _count.ValueChanged += (_, _) => Resize((int)_count.Value);

        // Стрелките на кутийката са две точки на екрана и се уцелват трудно.
        // Един ред повече или по-малко е най-честото движение тук.
        var more = Step("+", 1);
        var less = Step("−", -1);

        var counted = new Label
        {
            Dock = DockStyle.Right,
            Width = Theme.Scaled(52),
            Text = Lang.T("Брой", "How many"),
            Font = Theme.SmallBold,
            ForeColor = Theme.InkFaint,
            TextAlign = ContentAlignment.MiddleRight,
            Padding = new Padding(0, 0, Theme.GapSmall, 0)
        };

        // Закачените отдясно се редят в обратен ред на добавяне.
        head.Controls.Add(more);
        head.Controls.Add(less);
        head.Controls.Add(_count);
        head.Controls.Add(counted);

        head.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = Recycling
                ? Lang.T("В какво се рециклира", "What it is recycled as")
                : Lang.T("В какво се образува", "What it comes out as"),
            Font = Theme.SmallBold,
            ForeColor = Theme.InkFaint
        });

        var tools = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = Theme.Scaled(44),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Canvas,
            Padding = new Padding(0, 6, 0, 0)
        };

        // Шаблонът е първи: изборът му запълва всичко отдолу наведнъж.
        _templates.DropDownStyle = ComboBoxStyle.DropDownList;
        _templates.Width = Theme.Scaled(150);
        _templates.Height = Theme.Scaled(32);
        _templates.Font = Theme.Base;
        Theme.StyleInput(_templates);
        _templates.SelectedIndexChanged += (_, _) => ApplyTemplate();

        tools.Controls.Add(_templates);
        tools.Controls.Add(Tool(Lang.T("Запази…", "Save…"), SaveTemplate));
        tools.Controls.Add(Tool(Lang.T("Изтрий", "Delete"), DeleteTemplate));

        var balance = Tool(Lang.T("Изравни", "Balance"), Balance);

        new ToolTip().SetToolTip(balance, Lang.T(
            "Слага разликата в последния ред, за да стане сборът точно колкото се изчерпва.",
            "Puts the difference in the last row so the sum matches what is drawn."));

        tools.Controls.Add(balance);

        // Едно и също разкомплектоване важи за няколко купчини само когато
        // човек го каже: мълчаливото пренасяне би сложило рецептата на кабела и
        // върху маслото.
        _spread = Tool(Lang.T("Приложи", "Apply"), SpreadRecipe);

        new ToolTip().SetToolTip(_spread, Lang.T(
            "Пренася разпределението върху останалите избрани купчини — в проценти, не в тонове.",
            "Carries the split over to the other selected piles — as percentages, not tonnes."));

        tools.Controls.Add(_spread);

        _tools = tools;

        var columns = new Panel
        {
            Dock = DockStyle.Top,
            Height = 2 * Theme.Small.Height + 12,
            BackColor = Theme.Canvas
        };

        // Заглавията са на два реда: името горе, сборът под него — както под
        // всяко друго заглавие на колона в програмата. Затова са подравнени
        // НАГОРЕ: по средата името би подскачало, щом отдолу се появи число.
        Caption(_codeCaption, ContentAlignment.TopLeft);
        Caption(_amountCaption, ContentAlignment.TopRight);
        Caption(_percentCaption, ContentAlignment.TopRight);

        _codeCaption.Text = CodeTitle;

        columns.Controls.Add(_codeCaption);
        columns.Controls.Add(_amountCaption);
        columns.Controls.Add(_percentCaption);

        _partHost.Dock = DockStyle.Fill;
        _partHost.BackColor = Theme.Canvas;
        _partHost.AutoScroll = true;
        _partHost.Padding = new Padding(Theme.GapSmall, 4, Theme.GapSmall, 4);
        _partHost.Resize += (_, _) => Stack();

        _recipe.Dock = DockStyle.Bottom;
        _recipe.Height = Theme.Scaled(24);
        _recipe.Font = Theme.Bold;
        _recipe.ForeColor = Theme.InkMuted;

        var frame = new Card
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(1),
            Fill = Theme.Canvas,
            Outline = Theme.Border
        };

        frame.Controls.Add(_partHost);

        host.Controls.Add(frame);
        host.Controls.Add(columns);
        host.Controls.Add(_recipe);
        host.Controls.Add(tools);
        host.Controls.Add(head);

        return host;
    }

    /// <summary>Общият вид на трите заглавия над редовете.</summary>
    private static void Caption(Label label, ContentAlignment align)
    {
        label.Font = Theme.SmallBold;
        label.ForeColor = Theme.InkFaint;
        label.AutoSize = false;
        label.BackColor = Color.Transparent;
        label.TextAlign = align;
    }

    private string CodeTitle => Recycling
        ? Lang.T("Код на рециклирания отпадък", "Code of the waste recycled")
        : Lang.T("Код на образувания отпадък", "Code of the waste generated");

    /// <summary>Заглавието на колоната с изчерпаното — в думите на действието.</summary>
    private string TakeTitle => Recycling
        ? Lang.T("За рециклиране, т", "To recycle, t")
        : Lang.T("За образуване, т", "To generate, t");

    private static string AmountTitle => Lang.T("Тона", "Tonnes");

    /// <summary>
    /// Сборовете под имената на колоните.
    ///
    /// Числото, което човек събира наум, докато пише: „дотук съм разпределил
    /// толкова“. Чете се от самите полета, а не от рецептата — така то следва
    /// написаното още докато то се пише, вместо след като е прибрано.
    ///
    /// Броят кодове стои при тях по същата причина, поради която стои и в
    /// сбора отдолу: при осем реда „колко станаха“ не се брои с поглед.
    /// </summary>
    private void ShowColumnSums()
    {
        decimal tonnes = 0;
        decimal share = 0;
        var named = 0;

        foreach (var line in _lines)
        {
            if (SplitMath.TryRead(line.Amount.Text, out var amount)) tonnes += amount;
            if (SplitMath.TryRead(line.Percent.Text, out var percent)) share += percent;

            if (Stored(line.Code.Text).Length > 0) named++;
        }

        _codeCaption.Text = named == 0
            ? CodeTitle
            : CodeTitle + Environment.NewLine + Lang.T($"{named} кода", $"{named} codes");

        _amountCaption.Text = LedgerPage.Summed(AmountTitle, SplitMath.Round(tonnes));
        _percentCaption.Text = "%" + Environment.NewLine + SplitMath.Share(share);
    }

    /// <summary>Копче, което мърда броя редове с един.</summary>
    private AppButton Step(string glyph, int by)
    {
        var button = Theme.SecondaryButton(glyph);
        button.Dock = DockStyle.Right;
        button.Width = Theme.Scaled(30);
        button.Font = Theme.Bold;
        button.Click += (_, _) =>
            _count.Value = Math.Clamp(_count.Value + by, _count.Minimum, _count.Maximum);

        return button;
    }

    // ------------------------------------------------------------- редове

    /// <summary>Един дял: код, количество и процент, вързани един за друг.</summary>
    private sealed class Line
    {
        public required Label Number { get; init; }
        public required ComboBox Code { get; init; }
        public required TextBox Amount { get; init; }
        public required TextBox Percent { get; init; }
    }

    private static int NumberWidth => Theme.Scaled(84);

    /// <summary>
    /// Догражда или маха редове, докато станат колкото трябва.
    ///
    /// Не ги прави наново — виж бележката при <see cref="BuildParts"/>. Пипа се
    /// само разликата, а вече попълненото си остава на място, защото никой не
    /// го е бутал.
    /// </summary>
    private void Resize(int wanted)
    {
        wanted = Math.Clamp(wanted, 1, 20);

        if (wanted != _lines.Count)
        {
            _partHost.SuspendLayout();

            while (_lines.Count > wanted) Drop();
            while (_lines.Count < wanted) Add();

            _partHost.ResumeLayout(true);

            Stack();
        }

        if (_filling) return;

        Harvest();
        MarkCurrent();
        Weigh();
    }

    /// <summary>Маха последния ред и освобождава контролите му.</summary>
    private void Drop()
    {
        var line = _lines[^1];
        _lines.RemoveAt(_lines.Count - 1);

        foreach (var control in (Control[])[line.Number, line.Code, line.Amount, line.Percent])
        {
            _partHost.Controls.Remove(control);
            control.Dispose();
        }
    }

    /// <summary>Добавя един ред в края.</summary>
    private void Add()
    {
        var height = Theme.Big.Height + 12;

        var number = new Label
        {
            Size = new Size(Theme.Scaled(24), height),
            Text = (_lines.Count + 1).ToString(),
            ForeColor = Theme.InkFaint,
            TextAlign = ContentAlignment.MiddleRight,
            BackColor = Color.Transparent
        };

        var code = new ComboBox
        {
            Size = new Size(Theme.Scaled(240), height),
            DropDownStyle = ComboBoxStyle.DropDown,
            MaxDropDownItems = 14,
            Font = Theme.Base
        };

        Codes.Fill(code, _offered);

        // Празният ред тръгва с останалия един код. Шаблонът и писането го
        // изместват после — тук се спестява преписване, не се решава вместо
        // човека.
        if (_sole.Length > 0) code.Text = _sole;

        Theme.StyleInput(code);

        var amount = new TextBox
        {
            Size = new Size(NumberWidth, height),
            BorderStyle = BorderStyle.FixedSingle,
            TextAlign = HorizontalAlignment.Right,
            Font = Theme.Base
        };

        var percent = new TextBox
        {
            Size = new Size(NumberWidth, height),
            BorderStyle = BorderStyle.FixedSingle,
            TextAlign = HorizontalAlignment.Right,
            Font = Theme.Base
        };

        Theme.StyleInput(amount);
        Theme.StyleInput(percent);

        var line = new Line { Number = number, Code = code, Amount = amount, Percent = percent };

        // Стойността влиза и с писане, и от шаблон, не само с избор от менюто.
        code.TextChanged += (_, _) => Typed(line, null);
        amount.TextChanged += (_, _) => Typed(line, byPercent: false);
        percent.TextChanged += (_, _) => Typed(line, byPercent: true);

        code.KeyDown += OnRowKey;
        amount.KeyDown += OnRowKey;
        percent.KeyDown += OnRowKey;

        _lines.Add(line);

        _partHost.Controls.Add(number);
        _partHost.Controls.Add(code);
        _partHost.Controls.Add(amount);
        _partHost.Controls.Add(percent);
    }

    /// <summary>
    /// Слага редовете по ширината, която им е останала.
    ///
    /// Прозорецът се разтваря и разделителят се дърпа, а падащото меню с
    /// кодовете е онова, което трябва да порасне: в него се чете и името на
    /// отпадъка, не само цифрите.
    /// </summary>
    private void Stack()
    {
        if (_lines.Count == 0) return;

        var height = Theme.Big.Height + 12;
        var space = _partHost.ClientSize.Width - _partHost.Padding.Horizontal;

        // Мястото за лентата за превъртане се пази винаги: иначе появата ѝ при
        // осмия ред би отрязала процентите на всички останали.
        var wide = Math.Max(150, space - 24 - 2 * NumberWidth - 4 * Theme.GapSmall - 20);

        _partHost.SuspendLayout();

        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            var top = 4 + i * (height + 6);
            var left = _partHost.Padding.Left;

            line.Number.SetBounds(left, top + 3, 24, height);
            line.Code.SetBounds(left + 24 + Theme.GapSmall, top, wide, height);
            line.Amount.SetBounds(line.Code.Right + Theme.GapSmall, top, NumberWidth, height);
            line.Percent.SetBounds(line.Amount.Right + Theme.GapSmall, top, NumberWidth, height);

            line.Number.Text = (i + 1).ToString();
        }

        _partHost.ResumeLayout(true);

        // Заглавията стоят точно над своите колони — иначе „%“ виси над тоновете.
        var tall = 2 * Theme.Small.Height + 8;

        // Рамката около редовете е с пиксел кант — заглавията му го дължат.
        const int border = 1;

        _codeCaption.SetBounds(_lines[0].Code.Left + border, 1, wide, tall);
        _amountCaption.SetBounds(_lines[0].Amount.Left + border, 1, NumberWidth, tall);
        _percentCaption.SetBounds(_lines[0].Percent.Left + border, 1, NumberWidth, tall);
    }

    /// <summary>
    /// Стрелките нагоре и надолу местят между редовете, а не в самото поле.
    ///
    /// Данните се пишат отгоре надолу по една колона — код, код, код, после
    /// количествата — и посягането към мишката на всеки ред е повече работа от
    /// самото писане. В отвореното падащо меню стрелките си остават негови:
    /// там те избират.
    /// </summary>
    private void OnRowKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode is not (Keys.Up or Keys.Down)) return;
        if (sender is ComboBox { DroppedDown: true }) return;

        var from = _lines.FindIndex(l =>
            ReferenceEquals(l.Code, sender) ||
            ReferenceEquals(l.Amount, sender) ||
            ReferenceEquals(l.Percent, sender));

        if (from < 0) return;

        var to = from + (e.KeyCode == Keys.Down ? 1 : -1);

        e.Handled = true;
        e.SuppressKeyPress = true;

        if (to < 0 || to >= _lines.Count) return;

        Control target =
            ReferenceEquals(_lines[from].Code, sender) ? _lines[to].Code :
            ReferenceEquals(_lines[from].Amount, sender) ? _lines[to].Amount :
            _lines[to].Percent;

        target.Focus();

        if (target is TextBox box) box.SelectAll();
        if (target is ComboBox combo) combo.SelectAll();
    }

    /// <summary>
    /// Написаното в един ред — тонове, процент или код.
    ///
    /// Тоновете и процентите се пишат един друг; кодът не пипа нито едното.
    /// <paramref name="byPercent"/> е празно, когато е пипнат самият код.
    /// </summary>
    private void Typed(Line line, bool? byPercent)
    {
        if (_syncing || _filling) return;

        var total = Current() is { } pile ? Taken(pile) : 0m;

        if (byPercent is { } percent)
        {
            _syncing = true;

            try
            {
                if (percent)
                    line.Amount.Text = SplitMath.TryRead(line.Percent.Text, out var share)
                        ? SplitMath.Amount(SplitMath.Round(total * share / 100m))
                        : string.Empty;
                else
                    line.Percent.Text = SplitMath.TryRead(line.Amount.Text, out var value)
                        ? SplitMath.Share(SplitMath.Percent(value, total))
                        : string.Empty;
            }
            finally
            {
                _syncing = false;
            }
        }

        Harvest();
        MarkCurrent();
        Weigh();
    }

    /// <summary>Купчината, върху която стои курсорът — празно, ако няма такава.</summary>
    private TreatPile? Current() => _rows.CurrentRow?.Tag as TreatPile;

    /// <summary>
    /// Прибира написаното в рецептата на текущата купчина.
    ///
    /// Контролите са истината, докато купчината е под ръка; речникът я помни,
    /// когато човек мине на друга. Затова прибирането става при всяко пипване, а
    /// не при излизане: щракване вляво сменя купчината, без да пита.
    /// </summary>
    private void Harvest()
    {
        if (Current() is not { } pile) return;

        _recipes[pile.Ewc] =
        [
            .. _lines.Select(l => new GenPart(
                Stored(l.Code.Text),
                SplitMath.TryRead(l.Amount.Text, out var amount) && amount > 0
                    ? SplitMath.Round(amount)
                    : 0m))
        ];

        // Без количество процентите не се четат — полетата стоят празни. Тогава
        // помненото остава: иначе изваденият и върнат код би загубил дяловете си.
        if (Taken(pile) <= 0) return;

        var ratio = _lines
            .Select(l => (Ewc: Stored(l.Code.Text),
                          Percent: SplitMath.TryRead(l.Percent.Text, out var share) && share > 0 ? share : 0m))
            .ToList();

        if (ratio.All(r => r.Percent <= 0)) _ratios.Remove(pile.Ewc);
        else _ratios[pile.Ewc] = ratio;

        // Написаното на ръка, което вече не е шаблонът, го откача — менюто не
        // бива да твърди „Стар кабел“ над дялове, които не са неговите.
        if (_attached.TryGetValue(pile.Ewc, out var name) &&
            (_stored.FirstOrDefault(t => t.Name == name) is not { } template || !Same(template, ratio)))
        {
            _attached.Remove(pile.Ewc);
            ShowTemplate(pile);
        }
    }

    /// <summary>Дяловете са тези на шаблона — до стотна от процента.</summary>
    private static bool Same(SplitTemplate template, IReadOnlyList<(string Ewc, decimal Percent)> ratio)
    {
        var mine = ratio.Where(r => r.Percent > 0).ToList();

        return mine.Count == template.Parts.Count &&
               mine.Zip(template.Parts).All(x =>
                   string.Equals(ValueConverter.NormalizeCode(x.First.Ewc),
                       ValueConverter.NormalizeCode(x.Second.Ewc), StringComparison.Ordinal) &&
                   Math.Abs(x.First.Percent - x.Second.Percent) < 0.01m);
    }

    /// <summary>Менюто с шаблоните показва шаблона на купчината под курсора.</summary>
    private void ShowTemplate(TreatPile? pile)
    {
        var index = pile is not null && _attached.TryGetValue(pile.Ewc, out var name)
            ? _stored.FindIndex(t => t.Name == name) + 1
            : 0;

        if (_templates.Items.Count == 0 || _templates.SelectedIndex == index) return;

        var was = _filling;
        _filling = true;

        try
        {
            _templates.SelectedIndex = Math.Clamp(index, 0, _templates.Items.Count - 1);
        }
        finally
        {
            _filling = was;
        }
    }

    /// <summary>Вдясно: кои партиди стоят зад чакащото и по колко се взима.</summary>
    private Control BuildOrigin()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas };

        Dress(_origin);

        _origin.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _origin.MultiSelect = true;
        _origin.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;

        _origin.Columns.Add(Plain(OriginBatch, Lang.T("Партида", "Batch"), 100));
        // Двата текстови стълба си делят мястото, за да останат числата на екрана: на
        // лаптоп „Ще се вземе“ излизаше извън таблицата.
        _origin.Columns.Add(Plain(OriginPartner, Lang.T("Клиент", "Client"), 210, fill: true, least: 120));
        _origin.Columns.Add(Plain(OriginDocument, Lang.T("Приета на · документ", "Received · document"), 176, fill: true, least: 120));
        _origin.Columns.Add(Number(OriginStock, Lang.T("Чака, т", "Waiting, t"), 104, readOnly: true));
        _origin.Columns.Add(Number(OriginTake, Lang.T("Ще се вземе, т", "Will be taken, t"), 132, readOnly: false));

        _origin.SelectionChanged += (_, _) => ShowPicked();
        _origin.Enter += (_, _) => { _last = _origin; ShowPicked(); };
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

        host.Controls.Add(Frame(_origin));
        host.Controls.Add(BatchListDialog.Header(_behind, Expand));

        return host;
    }

    private AppButton Tool(string text, Action act)
    {
        var button = Theme.GhostButton(text, null);
        button.Height = Theme.Scaled(32);
        button.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        button.Click += (_, _) => act();

        return button.FitWidth(8);
    }

    /// <summary>Същото за разделител, който дели на горе и долу.</summary>
    private static void Down(SplitContainer split, double share)
    {
        // Мерени при 100 % — през мащаба, иначе при 150 % не значат нищо.
        var top = Theme.Scaled(200);
        var bottom = Theme.Scaled(140);
        var room = split.Height - split.SplitterWidth;

        if (room < Theme.Scaled(120)) return;

        // Нисък прозорец: двете половини си делят мястото, вместо долната да
        // остане под ръба. Таблицата с купчините е там, където се пише колко —
        // тя не може да липсва.
        if (room < top + bottom)
        {
            top = Math.Max(1, (int)(room * share));
            bottom = Math.Max(1, room - top);
        }

        split.Panel1MinSize = 1;
        split.Panel2MinSize = 1;

        split.SplitterDistance = Math.Clamp((int)(split.Height * share), top, Math.Max(top, room - bottom));
        split.Panel1MinSize = top;
        split.Panel2MinSize = bottom;
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

    /// <summary>Общият вид на трите таблици — един и същ, за да четат еднакво.</summary>
    private static void Dress(DataGridView grid)
    {
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

        var save = Theme.PrimaryButton(Recycling
            ? Lang.T("Рециклирай и затвори", "Recycle and close")
            : Lang.T("Образувай и затвори", "Generate and close"));
        save.Height = buttonHeight;
        save.Margin = Padding.Empty;
        save.Click += (_, _) => Accept();
        _save = save;

        var more = Theme.SecondaryButton(Recycling
            ? Lang.T("Рециклирай и продължи", "Recycle and continue")
            : Lang.T("Образувай и продължи", "Generate and continue"));
        more.Height = buttonHeight;
        more.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        more.Click += (_, _) => Continue();
        _more = more;

        // Вторият изход от образуването: същата купчина, същите количества и
        // кодове — но записани като РЕЦИКЛИРАНИ, извън отчета. Стои до
        // „Затвори“, далеч от главното копче: то е рядкото решение, не обичайното.
        // Показва се, щом страницата подаде как се записва рециклиране.
        var recycle = Theme.SecondaryButton(Lang.T("Рециклирай", "Recycle"));
        recycle.Height = buttonHeight;
        recycle.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        recycle.Visible = false;
        recycle.Click += (_, _) => Recycle();
        _recycle = recycle;

        new ToolTip().SetToolTip(recycle, Lang.T(
            "Записва избраното като РЕЦИКЛИРАНО — то излиза от „Третиран“, но не влиза в отчета — и " +
            "оставя прозореца отворен за останалото. Връща се от склад „Рециклиран“.",
            "Saves the selection as RECYCLED — it leaves “Treated” but does not enter the report — and " +
            "keeps the window open for the rest. It is returned from the “Recycled” store."));

        var cancel = Theme.GhostButton(Lang.T("Затвори", "Close"));
        cancel.Height = buttonHeight;
        cancel.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        cancel.Click += (_, _) => Leave();

        // Отдясно наляво: главното копче най-вдясно, рециклирането — вляво от
        // „Затвори“, както е посочено.
        tools.Controls.Add(save.FitWidth());
        tools.Controls.Add(more.FitWidth());
        tools.Controls.Add(cancel.FitWidth());
        tools.Controls.Add(recycle.FitWidth());

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

        _done.AutoSize = false;
        _done.Width = Theme.Scaled(360);
        _done.Height = buttonHeight;
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

    private AppButton Quick(string text, bool whole)
    {
        var button = Theme.GhostButton(text);
        button.Height = Theme.Scaled(40);
        button.Margin = new Padding(0, 0, Theme.GapSmall, 0);
        button.Click += (_, _) => FillTakes(whole);

        return button.FitWidth();
    }

    private Control Field(string caption, Control input)
    {
        var host = new Panel
        {
            Width = Theme.Scaled(300),
            Height = Theme.Scaled(52),
            BackColor = Theme.Canvas,
            Margin = new Padding(0, 0, Theme.Gap, Theme.GapSmall)
        };

        input.Dock = DockStyle.Top;
        input.Width = Theme.Scaled(300);
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

    /// <summary>Слага началните стойности: датата, купчините и цялото чакащо.</summary>
    private void Seed()
    {
        _filling = true;

        try
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            _date.Value = (_amend?.Date ?? (_on > today ? today : _on)).ToDateTime(TimeOnly.MinValue);

            FillTemplates();
            Lay(everything: true);
            ShowWhen();
        }
        finally
        {
            _filling = false;
        }

        Weigh();
        ShowPile();
    }

    /// <summary>
    /// Смята чакащото наново към избраната дата.
    ///
    /// Складът не е един и същ през цялата година: третиране от 1 юли не може
    /// да се образува на 1 май. Обхватът остава избраният отзад — пресмята се
    /// същият набор „код и партида“, но към новата дата.
    /// </summary>
    private void Reload(bool saved = false)
    {
        ShowWhen();

        if (_filling) return;

        var moves = _workspace.Ledger.Read(_companyId, _stagenum).Where(m => !_gone.Contains(m.Id)).ToList();
        var links = _workspace.Ledger.Links(_companyId, _stagenum).Where(l => !_gone.Contains(l.MoveId)).ToList();
        var batches = _workspace.Batches.List(_companyId, _stagenum);

        var shares = StockMath.Waiting(batches, moves, links, Date).Shares
            .Where(s => _chosen.Contains((ValueConverter.NormalizeCode(s.Ewc), s.Batch.Id)))
            .ToList();

        if (_amend is not null)
        {
            var open = _amend.Capped(shares, moves, links, Date);

            _notice.Say(_amend.Notice(Amend.Held(shares, open)));

            shares = [.. open];
        }

        _piles = Generation.Piles(shares);

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
        ShowPile();
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

        // Коя купчина е под курсора — по нея виси всичко вдясно.
        var was = Current()?.Ewc;

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

    /// <summary>Копчетата следват това, което има за образуване.</summary>
    private void Allow()
    {
        var something = _rows.Rows.Count > 0;

        if (_save is not null) _save.Enabled = something;
        if (_more is not null) _more.Enabled = something;
        if (_spread is not null) _spread.Enabled = something;
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

    // ------------------------------------------------------------- изчерпване

    /// <summary>Какво прави едно щракване по ред от купчините.</summary>
    private void Clicked(string column)
    {
        _last = _rows;

        if (column == TakeColumn) return;

        Apply();
        ShowPile();
    }

    /// <summary>
    /// Превръща избора в количества.
    ///
    /// Избраният ред взема каквото човек е писал по него, а ако не е писал —
    /// цялата купчина. Неизбраният отива на нула и си остава празен.
    /// </summary>
    private void Apply()
    {
        if (_filling) return;

        _filling = true;

        try
        {
            foreach (DataGridViewRow row in _rows.Rows)
            {
                // Скритата от пресяването купчина си пази количеството.
                if (row.Tag is not TreatPile pile || !row.Visible) continue;

                var written = _wanted.TryGetValue(pile.Ewc, out var typed) ? typed : pile.Available;
                Mark(row, Fill(pile, row.Selected, written));
            }
        }
        finally
        {
            _filling = false;
        }

        Weigh();
    }

    /// <summary>Избира или изчиства всички редове.</summary>
    private void FillTakes(bool whole)
    {
        if (whole) _rows.SelectAll();
        else _rows.ClearSelection();

        Apply();
        ShowPile();
    }

    /// <summary>Разписва количество по партидите на една купчина — най-старата първа.</summary>
    private void Spread(TreatPile pile, decimal amount)
    {
        foreach (var share in pile.Shares) _picks.Remove((Key(share), share.Batch.Id));

        foreach (var pick in Treatment.Spread(pile, amount))
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
        if (take && Recall(pile))
        {
            var taken = Taken(pile);
            Fit(pile, taken);

            return taken;
        }

        var amount = take ? written : 0m;

        Spread(pile, amount);
        Fit(pile, amount);

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
            taken);

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        ApplyList(pile, dialog.Takes);
    }

    /// <summary>Изписва количеството и казва с цвят дали редът участва.</summary>
    private void Mark(DataGridViewRow row, decimal take)
    {
        row.Cells[TakeColumn].Value = take > 0 ? SplitMath.Amount(take) : string.Empty;
        row.DefaultCellStyle.ForeColor = take > 0 ? Theme.Ink : Theme.InkFaint;

        row.Cells[OutColumn].Value = row.Tag is TreatPile pile && take > 0 ? Outgoing(pile) : string.Empty;
    }

    /// <summary>Как се чете рецептата на един ред: „191202 + 191204 (2 кода)“.</summary>
    private string Outgoing(TreatPile pile)
    {
        var parts = Recipe(pile).Where(p => ValueConverter.NormalizeCode(p.Ewc).Length > 0).ToList();

        if (parts.Count == 0) return Lang.T("(без код)", "(no code)");
        if (parts.Count == 1) return parts[0].Ewc;

        return Lang.T($"{parts[0].Ewc} + още {parts.Count - 1}",
                      $"{parts[0].Ewc} + {parts.Count - 1} more");
    }

    /// <summary>Запомня написаното по ред, за да преживее пресичането на избора.</summary>
    private void Remember()
    {
        if (_rows.CurrentRow?.Tag is not TreatPile pile) return;

        SplitMath.TryRead(_rows.CurrentRow.Cells[TakeColumn].Value?.ToString(), out var amount);

        amount = Math.Min(SplitMath.Round(amount), pile.Available);

        if (amount > 0) _wanted[pile.Ewc] = amount;
        else _wanted.Remove(pile.Ewc);

        // Число по код отменя посоченото по партиди: то пак се разписва от
        // най-старата нататък.
        _hand.Remove(pile.Ewc);

        Spread(pile, amount);
        Fit(pile, amount);
        ShowTaken();
    }

    /// <summary>
    /// Избраните долу партиди стават това, което се взима от тях.
    ///
    /// Същото правило, както горе: избраното Е изборът.
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
            // да излиза от образуването заради щракване по видимите.
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

    /// <summary>Написаното по един ред от таблицата с партидите.</summary>
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
            _picks[key] = Math.Min(SplitMath.Round(amount), share.Amount);
        }

        Restore(_origin, ref _kept);
        Took(pile);
    }

    /// <summary>Пренася избраното по партиди нагоре — в реда на кода и в сборовете.</summary>
    private void Took(TreatPile pile)
    {
        _filling = true;

        try
        {
            var take = Taken(pile);

            _wanted[pile.Ewc] = take;
            Hold(pile);
            Fit(pile, take);

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
        ShowParts();
    }

    /// <summary>Колко се взима от една купчина — сборът по партидите ѝ.</summary>
    private decimal Taken(TreatPile pile) => SplitMath.Round(
        pile.Shares.Sum(s => _picks.GetValueOrDefault((Key(s), s.Batch.Id))));

    /// <summary>Кодът на един дял — сведен, за да е ключ.</summary>
    private static string Key(StockShare share) => ValueConverter.NormalizeCode(share.Ewc);

    // ------------------------------------------------------------- рецепта

    /// <summary>
    /// В какво излиза тази купчина.
    ///
    /// Без записана рецепта се предлага едно към едно: същият код, цялото
    /// количество. Това е най-честият случай и не иска нито едно щракване —
    /// разкомплектоването е работата, която се пипа, а не обявяването.
    /// </summary>
    private List<GenPart> Recipe(TreatPile pile)
    {
        if (_recipes.TryGetValue(pile.Ewc, out var parts)) return parts;

        parts = [new GenPart(pile.Ewc, Taken(pile))];
        _recipes[pile.Ewc] = parts;

        return parts;
    }

    /// <summary>
    /// Смята рецептата за новото количество.
    ///
    /// Съотношението е решението на човека, тоновете са следствие: сменено
    /// количество не бива да иска списъкът да се пише наново.
    /// </summary>
    private void Fit(TreatPile pile, decimal take)
    {
        if (_ratios.TryGetValue(pile.Ewc, out var ratio))
        {
            _recipes[pile.Ewc] = [.. Generation.Recipe(take, ratio)];
            return;
        }

        var parts = Recipe(pile);

        _recipes[pile.Ewc] = [.. Generation.Rescale(parts, take)];
    }

    /// <summary>Показва рецептата и партидите на реда, върху който стои курсорът.</summary>
    private void ShowPile()
    {
        ShowParts();
        ShowOrigin();
    }

    /// <summary>
    /// Пълни редовете с рецептата на текущата купчина.
    ///
    /// Контролите се ПРЕПИСВАТ, а не се правят наново: щракване вляво сменя
    /// купчината и всяко пресъздаване би плащало осемстотинте кода отново.
    /// </summary>
    private void ShowParts()
    {
        if (IsDisposed) return;

        var was = _filling;
        _filling = true;

        try
        {
            if (Current() is not { } pile)
            {
                Resize(1);
                Clear();

                return;
            }

            var take = Taken(pile);
            var parts = Recipe(pile);

            // Процентите се показват помнените, а не сметнати от тоновете: те
            // са написаното, а тоновете — закръглено следствие от тях.
            _ratios.TryGetValue(pile.Ewc, out var ratio);

            _count.Value = Math.Clamp(parts.Count, _count.Minimum, _count.Maximum);
            Resize(parts.Count);

            for (var i = 0; i < _lines.Count; i++)
            {
                var part = i < parts.Count ? parts[i] : new GenPart(string.Empty, 0m);
                var share = ratio is not null && i < ratio.Count ? ratio[i].Percent : -1m;

                _lines[i].Code.Text = Display(part.Ewc);
                _lines[i].Amount.Text = part.Amount > 0 ? SplitMath.Amount(part.Amount) : string.Empty;
                _lines[i].Percent.Text =
                    share > 0 ? SplitMath.Share(share) :
                    share < 0 && part.Amount > 0 ? SplitMath.Share(SplitMath.Percent(part.Amount, take)) :
                    string.Empty;
            }

            ShowTemplate(pile);
        }
        finally
        {
            _filling = was;
        }

        ShowRecipe();
    }

    /// <summary>Изпразва редовете — няма избрана купчина.</summary>
    private void Clear()
    {
        foreach (var line in _lines)
        {
            line.Code.Text = string.Empty;
            line.Amount.Text = string.Empty;
            line.Percent.Text = string.Empty;
        }

        _recipe.Text = Lang.T("Изберете купчина вляво.", "Pick a pile on the left.");
        _recipe.ForeColor = Theme.InkFaint;
    }

    /// <summary>Долният ред на средната таблица: докъде е стигнало делението.</summary>
    private void ShowRecipe()
    {
        ShowColumnSums();

        if (Current() is not { } pile)
        {
            _recipe.Text = Lang.T("Изберете купчина вляво.", "Pick a pile on the left.");
            _recipe.ForeColor = Theme.InkFaint;
            return;
        }

        var take = Taken(pile);
        var parts = Recipe(pile);
        var sum = SplitMath.Round(parts.Sum(p => p.Amount));
        var left = SplitMath.Round(take - sum);
        var blank = parts.Count(p => p.Amount > 0 && ValueConverter.NormalizeCode(p.Ewc).Length == 0);

        if (take <= 0)
        {
            _recipe.Text = Recycling
                ? Lang.T($"{pile.Ewc} — не се рециклира нищо от тази купчина.",
                         $"{pile.Ewc} — nothing is recycled from this pile.")
                : Lang.T($"{pile.Ewc} — не се образува нищо от тази купчина.",
                         $"{pile.Ewc} — nothing is generated from this pile.");
            _recipe.ForeColor = Theme.InkMuted;
            return;
        }

        var text = Lang.T(
            $"{SplitMath.Amount(sum)} от {SplitMath.Amount(take)} т",
            $"{SplitMath.Amount(sum)} of {SplitMath.Amount(take)} t");

        if (left != 0)
            text += Lang.T(
                $"  ·  остават {SplitMath.Amount(left)} т ({SplitMath.Share(SplitMath.Percent(left, take))} %)",
                $"  ·  {SplitMath.Amount(left)} t left ({SplitMath.Share(SplitMath.Percent(left, take))} %)");

        if (blank > 0)
            text += Lang.T($"  ·  {blank} без код", $"  ·  {blank} without a code");

        _recipe.Text = text;
        _recipe.ForeColor = left == 0 && blank == 0 ? Theme.Ok : Theme.Warn;
    }

    /// <summary>
    /// Слага разликата в последния ред — точно колкото се изчерпва.
    ///
    /// Едно натискане вместо сметка наум: човек пише първите два дяла, защото
    /// ги е измерил, а третият е „каквото остана“.
    /// </summary>
    private void Balance()
    {
        if (Current() is not { } pile) return;
        if (_lines.Count == 0) return;

        var take = Taken(pile);

        decimal used = 0;

        for (var i = 0; i < _lines.Count - 1; i++)
            if (SplitMath.TryRead(_lines[i].Amount.Text, out var value)) used += value;

        var rest = SplitMath.Round(take - used);

        _syncing = true;

        try
        {
            _lines[^1].Amount.Text = SplitMath.Amount(rest);
            _lines[^1].Percent.Text = SplitMath.Share(SplitMath.Percent(rest, take));
        }
        finally
        {
            _syncing = false;
        }

        Harvest();
        MarkCurrent();
        Weigh();
    }

    /// <summary>
    /// Пренася рецептата на текущата купчина върху останалите избрани.
    ///
    /// В проценти, не в тонове: същото разкомплектоване върху друго количество
    /// значи същите дялове, а не същите числа.
    /// </summary>
    private void SpreadRecipe()
    {
        if (Current() is not { } source) return;

        // Пренася се съотношението — същото, което влиза и в шаблон. Тоновете
        // на кабела не значат нищо за маслото; дяловете значат.
        var shares = Generation.Ratio(Recipe(source));

        if (shares.Count == 0)
        {
            Say(Lang.T("Първо разпределете текущата купчина.",
                       "Split the current pile first."));
            return;
        }

        var touched = 0;

        foreach (DataGridViewRow row in _rows.SelectedRows)
        {
            if (row.Tag is not TreatPile pile) continue;
            if (string.Equals(pile.Ewc, source.Ewc, StringComparison.Ordinal)) continue;

            _ratios[pile.Ewc] = [.. shares];
            _recipes[pile.Ewc] = [.. Generation.Recipe(Taken(pile), shares)];

            if (_attached.TryGetValue(source.Ewc, out var name)) _attached[pile.Ewc] = name;
            else _attached.Remove(pile.Ewc);

            touched++;
        }

        _filling = true;

        try
        {
            foreach (DataGridViewRow row in _rows.Rows)
                if (row.Tag is TreatPile pile) Mark(row, Taken(pile));
        }
        finally
        {
            _filling = false;
        }

        Weigh();

        Say(touched == 1
            ? Lang.T("Разпределението важи за още 1 купчина.", "The split now covers 1 more pile.")
            : Lang.T($"Разпределението важи за още {touched} купчини.",
                     $"The split now covers {touched} more piles."));
    }

    /// <summary>Пренаписва реда на текущата купчина в горната таблица.</summary>
    private void MarkCurrent()
    {
        if (_rows.CurrentRow?.Tag is not TreatPile pile) return;

        _filling = true;

        try
        {
            foreach (DataGridViewRow row in _rows.Rows)
                if (row.Tag is TreatPile mine &&
                    string.Equals(mine.Ewc, pile.Ewc, StringComparison.Ordinal))
                    Mark(row, Taken(mine));
        }
        finally
        {
            _filling = false;
        }
    }

    // ------------------------------------------------------------- шаблони

    private void FillTemplates()
    {
        var was = _filling;
        _filling = true;

        try
        {
            _templates.Items.Clear();
            _templates.Items.Add(Lang.T("(без шаблон)", "(no template)"));

            foreach (var template in _stored) _templates.Items.Add(template.Name);

            _templates.SelectedIndex = 0;
        }
        finally
        {
            _filling = was;
        }
    }

    private void ApplyTemplate()
    {
        if (_filling) return;
        if (_rows.CurrentRow?.Tag is not TreatPile pile) return;

        var index = _templates.SelectedIndex - 1;

        // „(без шаблон)“ откача шаблона, но дяловете остават: човек, който
        // иска да ги пипне на ръка, тръгва от тях, а не от празно.
        if (index < 0)
        {
            _attached.Remove(pile.Ewc);
            return;
        }

        if (index >= _stored.Count) return;

        var template = _stored[index];

        // Шаблонът е дялове: те се пазят и всяко ново количество се дели по тях.
        _ratios[pile.Ewc] = [.. template.Parts.Select(p => (p.Ewc, p.Percent))];
        _attached[pile.Ewc] = template.Name;

        _recipes[pile.Ewc] = [.. Generation.Recipe(Taken(pile), _ratios[pile.Ewc])];

        ShowParts();
        MarkCurrent();
        Weigh();
    }

    private void SaveTemplate()
    {
        if (_rows.CurrentRow?.Tag is not TreatPile pile) return;

        // В шаблона отиват ДЯЛОВЕ, не тонове: записани веднъж, тоновете важат за
        // едно-единствено количество, а съотношението важи за всяко.
        var kept = Generation.Ratio(Recipe(pile))
            .Select(part => new SplitPart(part.Ewc, part.Percent))
            .ToList();

        if (kept.Count == 0)
        {
            Say(Lang.T("Шаблонът се записва, след като има избрани кодове.",
                       "A template is saved once there are codes chosen."));
            return;
        }

        var name = NameDialog.Ask(this,
            Lang.T("Име на шаблона", "Template name"),
            _templates.SelectedIndex > 0 ? _templates.Text : string.Empty);

        if (string.IsNullOrWhiteSpace(name)) return;

        _stored = SplitTemplates.Put(
            _workspace.Settings, Moves.Node(MoveKind.Generated), new SplitTemplate(name.Trim(), kept));

        FillTemplates();
        _templates.SelectedIndex = _stored.FindIndex(t => t.Name == name.Trim()) + 1;
    }

    private void DeleteTemplate()
    {
        var index = _templates.SelectedIndex - 1;
        if (index < 0 || index >= _stored.Count) return;

        var name = _stored[index].Name;

        if (Visible)
        {
            var answer = MessageBox.Show(this,
                Lang.T($"Да се изтрие ли шаблонът „{name}“?", $"Delete the template “{name}”?"),
                Lang.T("Изтриване", "Delete"), MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (answer != DialogResult.Yes) return;
        }

        _stored = SplitTemplates.Remove(_workspace.Settings, Moves.Node(MoveKind.Generated), name);
        FillTemplates();
    }

    // ------------------------------------------------------------- произход

    /// <summary>Показва кой е донесъл чакащото зад избрания код.</summary>
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
    }

    private string WhatTitle => Recycling
        ? Lang.T("От какво се рециклира — чака в „Третиран“", "What it comes from — waiting in “Treated”")
        : Lang.T("От какво се образува — чака в „Третиран“", "What it comes from — waiting in “Treated”");

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

        // Купчината под курсора може да е сменена — вдясно виси и рецептата ѝ.
        ShowPile();
    }

    /// <summary>Изписва взетото по партиди, без да пресмята наново.</summary>
    private void ShowTaken()
    {
        if (_rows.CurrentRow?.Tag is TreatPile pile)
        {
            _origin.Columns[OriginStock].HeaderText =
                LedgerPage.Summed(Lang.T("Чака, т", "Waiting, t"), pile.Available);

            _origin.Columns[OriginTake].HeaderText =
                LedgerPage.Summed(Lang.T("Ще се вземе, т", "Will be taken, t"), Taken(pile));
        }

        var was = _filling;
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
            _filling = was;
        }
    }

    private static string Partner(Batch batch) =>
        batch.PartnerName.Length > 0
            ? batch.Partner
            : batch.Eik.Length > 0 ? batch.Eik : Lang.T("(клиент не е посочен)", "(no client stated)");

    // ------------------------------------------------------------- сборове

    /// <summary>Кои купчини участват и с какво — за проверките и за сглобяването.</summary>
    private List<GenOrder> Orders()
    {
        var orders = new List<GenOrder>();

        foreach (var pile in _piles)
        {
            var picks = new List<TreatPick>();

            foreach (var share in pile.Shares)
                if (_picks.GetValueOrDefault((Key(share), share.Batch.Id)) is > 0 and var take)
                    picks.Add(new TreatPick(share, Math.Min(take, share.Amount)));

            orders.Add(new GenOrder(pile, picks, Recipe(pile)));
        }

        return orders;
    }

    /// <summary>Сборовете и какво ще излезе от тях.</summary>
    private void Weigh()
    {
        if (_filling || IsDisposed) return;

        var orders = Orders();
        var wanted = SplitMath.Round(orders.Sum(o => Math.Max(0m, o.Consumed)));
        var waiting = SplitMath.Round(orders.Sum(o => o.Pile.Available));
        var codes = orders.Count(o => o.Consumed > 0);

        _totals.Text = Recycling
            ? Lang.T(
                $"За рециклиране: {SplitMath.Amount(wanted)} т · чака: {SplitMath.Amount(waiting)} т · " +
                $"купчини: {codes} от {orders.Count}",
                $"To recycle: {SplitMath.Amount(wanted)} t · waiting: {SplitMath.Amount(waiting)} t · " +
                $"piles: {codes} of {orders.Count}")
            : Lang.T(
                $"За образуване: {SplitMath.Amount(wanted)} т · чака: {SplitMath.Amount(waiting)} т · " +
                $"купчини: {codes} от {orders.Count}",
                $"To generate: {SplitMath.Amount(wanted)} t · waiting: {SplitMath.Amount(waiting)} t · " +
                $"piles: {codes} of {orders.Count}");

        ShowOriginal(wanted);

        _rows.Columns[StockColumn].HeaderText =
            LedgerPage.Summed(Lang.T("Чака, т", "Waiting, t"), waiting);

        _rows.Columns[TakeColumn].HeaderText = LedgerPage.Summed(TakeTitle, wanted);

        ShowPicked();
        ShowRecipe();

        var problem = Generation.Check(orders).FirstOrDefault();
        var moves = Generation.Plan(orders, _perPile.Checked);

        if (problem is not null)
        {
            _split.Text = problem.Say(_kind);
            _split.ForeColor = problem.Kind == GenProblem.Nothing ? Theme.InkMuted : Theme.Warn;
            return;
        }

        var batches = moves.SelectMany(m => m.Draws).Select(d => d.BatchId).Distinct().Count();

        _split.Text = Lang.T(
            $"{moves.Count} {(moves.Count == 1 ? "ред" : "реда")} „{Moves.Title(_kind)}“ · " +
            $"от {batches} {(batches == 1 ? "партида" : "партиди")}",
            $"{moves.Count} “{Moves.Title(_kind)}” {(moves.Count == 1 ? "row" : "rows")} · " +
            $"from {batches} {(batches == 1 ? "batch" : "batches")}");

        _split.ForeColor = Theme.Ok;
    }

    /// <summary>Сборът на МАРКИРАНОТО — отделно число от сбора на всичко.</summary>
    private void ShowPicked()
    {
        var below = ReferenceEquals(_last, _origin);

        int rows;
        decimal take;
        decimal stock;

        if (below)
        {
            var shares = _origin.SelectedRows
                .Cast<DataGridViewRow>()
                .Select(r => r.Tag)
                .OfType<StockShare>()
                .ToList();

            rows = shares.Count;
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

            rows = piles.Count;
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

    // ------------------------------------------------------------- подредба

    private void SortRows(int column)
    {
        if (column < 0 || column >= _rows.Columns.Count) return;

        var name = _rows.Columns[column].Name;
        if (name == OutColumn) return;

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
        ShowPile();
    }

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

    private static void Glyph(DataGridView grid, string column, bool down)
    {
        foreach (DataGridViewColumn each in grid.Columns)
            each.HeaderCell.SortGlyphDirection = each.Name == column
                ? down ? SortOrder.Descending : SortOrder.Ascending
                : SortOrder.None;
    }

    private static string Column(DataGridView grid, MouseEventArgs e)
    {
        var at = grid.HitTest(e.X, e.Y).ColumnIndex;

        return at >= 0 && at < grid.Columns.Count ? grid.Columns[at].Name : string.Empty;
    }

    /// <summary>Връща избора отпреди писането.</summary>
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

    // ------------------------------------------------------------- записване

    /// <summary>Проверява и затваря.</summary>
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
    /// чакащото, празно образуване, ред без код, разминаване между изчерпаното и
    /// обявеното, липсващо задължително поле и затворен месец.
    /// </summary>
    private bool Ready(
        out IReadOnlyList<GenMove> moves,
        out IReadOnlyDictionary<string, string> fields,
        MoveKind? exit = null)
    {
        moves = [];
        fields = new Dictionary<string, string>(StringComparer.Ordinal);

        // През кой изход се записва: образуване (в отчета) или рециклиране
        // (извън него). Полетата горе са на книгата — рециклираното в книгата
        // не отива и задължително поле там не бива да го спира.
        var kind = exit ?? _kind;
        var book = Moves.InBook(kind);

        var orders = Orders();
        var problems = Generation.Check(orders).Select(i => i.Say(kind)).ToList();

        foreach (var column in Extras())
        {
            if (!book) break;
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
            if (Visible)
                MessageBox.Show(this, string.Join(Environment.NewLine, problems.Take(8)),
                    Lang.T("Липсва нещо", "Something is missing"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);

            return false;
        }

        var written = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var column in Extras())
        {
            if (!book) break;

            var value = Value(column);
            if (value.Length > 0) written[column.Name] = value;
        }

        fields = written;
        moves = Generation.Plan(orders, _perPile.Checked);

        if (Guard is not null)
        {
            var broken = Guard(Date, [.. moves.Select(m => new Planned(kind, Date, m.Ewc, m.Amount, m.Draws))]);

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

    /// <summary>Записва и остава отворено.</summary>
    private void Continue()
    {
        if (Write is null) return;
        if (!Ready(out var moves, out var fields)) return;

        _written += Write(moves, Date, fields, Note);

        Written();
    }

    /// <summary>
    /// „Рециклирай“: записва избраното като рециклирано и остава отворено.
    ///
    /// Остава отворено нарочно: от една купчина част се рециклира, а
    /// останалото се образува — и двете от същия прозорец, без да се избира
    /// наново.
    /// </summary>
    private void Recycle()
    {
        if (!RecycleOffered || WriteRecycled is null) return;
        if (!Ready(out var moves, out var fields, MoveKind.Recycled)) return;

        _recycled += WriteRecycled(moves, Date, fields, Note);

        Written();
    }

    /// <summary>След запис оттук: казва какво е записано и смята чакащото наново.</summary>
    private void Written()
    {
        _done.Text = Tally();

        _wanted.Clear();
        _hand.Clear();
        _recipes.Clear();
        _ratios.Clear();
        _attached.Clear();
        Reload(saved: true);

        if (_rows.Rows.Count == 0)
            _done.Text += Recycling
                ? Lang.T(" Няма какво повече да се рециклира.", " Nothing is left to recycle.")
                : Lang.T(" Няма какво повече да се образува.", " Nothing is left to generate.");

        _date.Focus();
    }

    /// <summary>Какво е записано, докато прозорецът стои отворен — образувания и рециклирания.</summary>
    private string Tally()
    {
        // В прозореца на рециклирането „записаното“ Е рециклиране.
        var formed = Recycling ? 0 : _written;
        var recycled = Recycling ? _written : _recycled;

        string Formed() => formed == 1
            ? Lang.T("1 образуване", "1 generation")
            : Lang.T($"{formed} образувания", $"{formed} generations");

        string Recycled() => recycled == 1
            ? Lang.T("1 рециклиране", "1 recycling")
            : Lang.T($"{recycled} рециклирания", $"{recycled} recyclings");

        return (formed > 0, recycled > 0) switch
        {
            (true, true) => Lang.T($"Записани: {Formed()} и {Recycled()}.", $"Saved: {Formed()} and {Recycled()}."),
            (false, true) => recycled == 1
                ? Lang.T("Записано е 1 рециклиране.", "1 recycling saved.")
                : Lang.T($"Записани са {Recycled()}.", $"{Recycled()} saved."),
            _ => formed == 1
                ? Lang.T("Записано е 1 образуване.", "1 generation saved.")
                : Lang.T($"Записани са {Formed()}.", $"{Formed()} saved.")
        };
    }

    /// <summary>Затваря — записаното с „продължи“ и „Рециклирай“ вече е в базата.</summary>
    private void Leave() =>
        DialogResult = _written > 0 || _recycled > 0 ? DialogResult.OK : DialogResult.Cancel;

    private void Say(string text)
    {
        _done.Text = text;
        _done.ForeColor = Theme.Ok;
    }

    private string Value(ColumnDef column) =>
        _inputs.TryGetValue(column.Name, out var input) ? Stored(input.Text) : string.Empty;

    // ------------------------------------------------------------- клавиши

    /// <summary>
    /// F4 отваря календара, а върху код — търсачката.
    ///
    /// Двете са едно и също движение: „отвори ми списъка на това поле“. В
    /// таблицата с кодовете то значи търсачката, навсякъде другаде — календара.
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

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        Spread();
        _date.Focus();
    }

    /// <summary>
    /// Дава на страничния разделител мястото, което му е отредено.
    ///
    /// Веднъж, при показване: дотогава рамката няма височина, а после мястото е
    /// на човека — той го е дръпнал нарочно и не бива да го намери върнато.
    /// Главният се нарежда сам, още при сглобяването.
    /// </summary>
    private void Spread()
    {
        if (_placed) return;
        _placed = true;

        // Разпределението и кодовете делят лявата страна наполовина: и двете
        // са по няколко реда, а партидите имат своя страна.
        if (_sideSplit is { } side) Down(side, 0.5);
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keys)
    {
        if (keys == (Keys.Control | Keys.F))
        {
            _search.Box.Focus();
            return true;
        }

        // F4 значи „отвори ми списъка на това поле“. Стои ли курсорът в меню с
        // кодове, списъкът е неговият и клавишът си остава негов — календарът
        // се докарва само когато полето под ръката няма какво да отвори.
        if (keys == Keys.F4 && !_date.Focused && !InCode())
        {
            OpenCalendar();
            return true;
        }

        return base.ProcessCmdKey(ref message, keys);
    }

    /// <summary>Стои ли курсорът в някое от менютата с кодове.</summary>
    private bool InCode() => _lines.Any(l => l.Code.Focused);

    /// <summary>
    /// Нарежда поправяното действие такова, каквото е записано: дата,
    /// полета, бележка и взетото от всяка партида. Оттам човек мени каквото е
    /// сгрешено — най-често датата — и записва поправката на мястото на
    /// старото.
    /// </summary>
    private void Reopen(AmendSeed amend)
    {
        var day = amend.Date.ToString(ValueConverter.BulgarianDateFormat);

        Text = Recycling
            ? Lang.T($"Поправка на рециклиране от {day}", $"Correcting the recycling of {day}")
            : Lang.T($"Поправка на образуване от {day}", $"Correcting the generation of {day}");

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

        _perPile.Checked = amend.Split;

        // Рецептата се нарежда преди количествата: взетото се дели по нея.
        foreach (var pile in _piles)
        {
            var parts = amend.Recipe(pile);
            if (parts.Count > 0) _ratios[pile.Ewc] = [.. Generation.Ratio(parts)];
        }

        foreach (var pile in _piles)
        {
            ApplyList(pile, amend.Drawn(pile));

            // Същото количество — същите тонове, без закръгляне през процентите.
            var parts = amend.Recipe(pile);

            if (parts.Count > 0 && SplitMath.Round(parts.Sum(p => p.Amount)) == Taken(pile))
                _recipes[pile.Ewc] = parts;
        }

        ShowPile();

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

    /// <summary>На колко кода се дели купчината в момента.</summary>
    internal int PartCount => _lines.Count;

    /// <summary>Какво пише в кутийката „Брой“.</summary>
    internal int PartCountBox => (int)_count.Value;

    internal int OriginCount => _origin.Rows.Count;

    /// <summary>Колко образувания са записани, без прозорчето да се затваря.</summary>
    internal int Saved => _written;

    /// <summary>Колко рециклирания са записани оттук с „Рециклирай“.</summary>
    internal int RecycledCount => _recycled;

    /// <summary>Предлага ли се копчето „Рециклирай“ — нагласеното, не нарисуваното.</summary>
    internal bool RecycleShown => RecycleOffered;

    /// <summary>Надписът на копчето „Рециклирай“.</summary>
    internal string RecycleText => _recycle?.Text ?? string.Empty;

    /// <summary>Натиска „Рециклирай“.</summary>
    internal void RecycleNow() => Recycle();

    internal string DoneText => _done.Text;

    internal string TotalsText => _totals.Text;

    internal string SplitText => _split.Text;

    internal string RecipeText => _recipe.Text;

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

    /// <summary>Как е озаглавена датата в този прозорец — образуване или рециклиране.</summary>
    internal string DateCaptionText => DateText;

    /// <summary>Какво връща прозорецът — образуван или рециклиран отпадък.</summary>
    internal MoveKind Kind => _kind;

    /// <summary>Заглавието на прозореца.</summary>
    internal string HeadingText => Text;

    /// <summary>Надписът на главното копче.</summary>
    internal string SaveText => _save?.Text ?? string.Empty;

    /// <summary>Колко полета на книгата стоят горе — при рециклиране нито едно.</summary>
    internal int ExtraCount => _inputs.Count;

    internal string PickedText => _picked.Text;

    internal string BehindText => _behind.Text;

    internal bool CanGenerate => _save?.Enabled ?? false;

    /// <summary>Пише в търсенето над таблиците, както човек.</summary>
    internal void UseSearch(string text) => _search.Box.Text = text;

    /// <summary>Колко кода се виждат.</summary>
    internal int VisiblePiles => _rows.Rows.Cast<DataGridViewRow>().Count(r => r.Visible);

    internal bool CanGoFullScreen => MaximizeBox && FormBorderStyle == FormBorderStyle.Sizable;

    /// <summary>
    /// Побира ли се лентата с шаблоните в ширината, която ѝ е дадена.
    ///
    /// Проверява се, защото надписите се менят с езика, а рамката вдясно е
    /// най-тясното място в прозореца: отрязано копче се вижда чак на снимка от
    /// потребител.
    /// </summary>
    internal bool ToolsFit =>
        _tools is { } tools &&
        tools.Controls.Cast<Control>().All(c => c.Right <= tools.ClientSize.Width);

    internal string CodeAt(int row) => _rows.Rows[row].Cells[CodeColumn].Value?.ToString() ?? string.Empty;

    internal string StockAt(int row) => _rows.Rows[row].Cells[StockColumn].Value?.ToString() ?? string.Empty;

    /// <summary>Колко партиди предлага прозорецът.</summary>
    internal int BatchesOffered => _piles.Sum(p => p.Shares.Count);

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

    /// <summary>Отделен ред ли е за всяка купчина.</summary>
    internal bool PerPileOn => _perPile.Checked;

    internal string TakeAt(int row) => _rows.Rows[row].Cells[TakeColumn].Value?.ToString() ?? string.Empty;

    internal string OutAt(int row) => _rows.Rows[row].Cells[OutColumn].Value?.ToString() ?? string.Empty;

    /// <summary>Кодът на ред от рецептата — без описанието му.</summary>
    internal string PartCodeAt(int row) => Stored(_lines[row].Code.Text);

    /// <summary>Описанието, което менюто показва до кода.</summary>
    internal string PartNameAt(int row)
    {
        var text = _lines[row].Code.Text;
        var dash = text.IndexOf(" — ", StringComparison.Ordinal);

        return dash > 0 ? text[(dash + 3)..].Trim() : string.Empty;
    }

    internal string PartAmountAt(int row) => _lines[row].Amount.Text;

    /// <summary>Сборът под заглавието „Тона“ — ползва се от тестовете.</summary>
    internal string PartAmountTotalText => _amountCaption.Text;

    /// <summary>Сборът на процентите под „%“.</summary>
    internal string PartPercentTotalText => _percentCaption.Text;

    /// <summary>Колко кода са набрани — стои под заглавието на колоната.</summary>
    internal string PartCodeTotalText => _codeCaption.Text;

    internal string PartPercentAt(int row) => _lines[row].Percent.Text;

    internal string OriginTakeAt(int row) => _origin.Rows[row].Cells[OriginTake].Value?.ToString() ?? string.Empty;

    /// <summary>Номерът на партидата на ред долу.</summary>
    internal string BatchAt(int row) => _origin.Rows[row].Cells[OriginBatch].Value?.ToString() ?? string.Empty;

    internal string OriginPartnerAt(int row) =>
        _origin.Rows[row].Cells[OriginPartner].Value?.ToString() ?? string.Empty;

    /// <summary>Сменя датата, както човек би я сменил — списъкът се смята наново.</summary>
    internal void UseDate(DateOnly date) => _date.Value = date.ToDateTime(TimeOnly.MinValue);

    /// <summary>Избира купчина, както би я избрал човек с едно щракване.</summary>
    internal void PickPile(int row)
    {
        if (row < 0 || row >= _rows.Rows.Count) return;

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

        Clicked(CodeColumn);
    }

    /// <summary>Влачи по редовете, както би влачил човек — избраното се прилага.</summary>
    internal void DragPiles(params int[] rows)
    {
        _rows.ClearSelection();

        foreach (var row in rows)
            if (row >= 0 && row < _rows.Rows.Count)
                _rows.Rows[row].Selected = true;

        Apply();
        ShowPile();
    }

    /// <summary>Пише колко се изчерпва от една купчина.</summary>
    internal void UseTake(int row, decimal amount)
    {
        if (row < 0 || row >= _rows.Rows.Count) return;

        _rows.CurrentCell = _rows.Rows[row].Cells[TakeColumn];
        _rows.Rows[row].Cells[TakeColumn].Value = SplitMath.Amount(amount);

        Remember();
        Weigh();
        ShowPile();
    }

    /// <summary>Пише колко да се вземе от една партида.</summary>
    internal void UseBatchTake(int row, decimal amount)
    {
        if (row < 0 || row >= _origin.Rows.Count) return;

        _origin.Rows[row].Cells[OriginTake].Value = SplitMath.Amount(amount);

        TookFromBatch(row);
    }

    /// <summary>Избира партиди долу, както човек с Ctrl — и прилага избора.</summary>
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

    /// <summary>Натиска „Всичко“ или „Нищо“.</summary>
    internal void UseQuick(bool whole) => FillTakes(whole);

    /// <summary>Слага „Брой“ — на колко кода се дели купчината.</summary>
    internal void UsePartCount(int count) =>
        _count.Value = Math.Clamp(count, _count.Minimum, _count.Maximum);

    /// <summary>Натиска „+“ или „−“ до кутийката „Брой“.</summary>
    internal void StepParts(int by) =>
        _count.Value = Math.Clamp(_count.Value + by, _count.Minimum, _count.Maximum);

    /// <summary>Избира код от менюто на реда, както би го избрал човек.</summary>
    internal void UsePartCode(int row, string ewc)
    {
        if (row < 0 || row >= _lines.Count) return;

        _lines[row].Code.Text = Display(ewc);
    }

    /// <summary>Пише тонове в ред от рецептата.</summary>
    internal void UsePartAmount(int row, decimal amount)
    {
        if (row < 0 || row >= _lines.Count) return;

        _lines[row].Amount.Text = amount > 0 ? SplitMath.Amount(amount) : string.Empty;
    }

    /// <summary>Пише процент в ред от рецептата.</summary>
    internal void UsePartPercent(int row, decimal percent)
    {
        if (row < 0 || row >= _lines.Count) return;

        _lines[row].Percent.Text = percent > 0 ? SplitMath.Share(percent) : string.Empty;
    }

    /// <summary>Натиска „Изравни“.</summary>
    internal void UseBalance() => Balance();

    /// <summary>Натиска „Приложи на избраните“.</summary>
    internal void UseSpread() => SpreadRecipe();

    /// <summary>Избира шаблон по име — ползва се от тестовете.</summary>
    /// <summary>Какво показва менюто с шаблоните.</summary>
    internal string TemplateText => _templates.SelectedItem?.ToString() ?? string.Empty;

    internal bool UseTemplate(string name)
    {
        var at = _stored.FindIndex(t => t.Name == name);
        if (at < 0) return false;

        _templates.SelectedIndex = at + 1;
        return true;
    }

    /// <summary>Записва рецептата на текущата купчина като шаблон с това име.</summary>
    internal bool KeepTemplate(string name)
    {
        if (_rows.CurrentRow?.Tag is not TreatPile pile) return false;

        var kept = Generation.Ratio(Recipe(pile))
            .Select(part => new SplitPart(part.Ewc, part.Percent))
            .ToList();

        if (kept.Count == 0) return false;

        _stored = SplitTemplates.Put(
            _workspace.Settings, Moves.Node(MoveKind.Generated), new SplitTemplate(name, kept));

        FillTemplates();
        return true;
    }

    internal void UsePerPile(bool perPile)
    {
        _perPile.Checked = perPile;
        Weigh();
    }

    internal void UseField(string name, string value)
    {
        if (_inputs.TryGetValue(name, out var input)) input.Text = value;
    }

    internal string FieldText(string name) =>
        _inputs.TryGetValue(name, out var input) ? input.Text : string.Empty;

    /// <summary>Натиска „Образувай и продължи“.</summary>
    internal void GenerateAndContinue() => Continue();

    /// <summary>Сглобява движенията, както копчето би ги сглобило.</summary>
    internal IReadOnlyList<GenMove> Planned() => Generation.Plan(Orders(), _perPile.Checked);

    /// <summary>Какво спира записа в момента — без да се показва прозорец.</summary>
    internal IReadOnlyList<string> Blocking()
    {
        Ready(out _, out _);
        return Problems;
    }

    // ------------------------------------------------------------- помощни

    /// <summary>„161004 — Отпадъци от…“ обратно до „161004“.</summary>
    private static string Stored(string? display)
    {
        var text = display?.Trim() ?? string.Empty;
        var dash = text.IndexOf(" — ", StringComparison.Ordinal);

        return dash > 0 ? text[..dash].Trim() : text;
    }

    private string Describe(string ewc) =>
        _names.TryGetValue(ewc, out var name) ? $"{ewc} — {name}" : ewc;

    /// <summary>
    /// Кодът така, както го показва менюто — с името до него.
    ///
    /// Пита се самото меню, а не номенклатурата: в него стоят точно
    /// предложените стойности и написаното трябва да съвпада с тях, за да се
    /// познае после. Код извън списъка влиза какъвто е — кратките списъци
    /// стесняват предлаганото, не разбираното (правило 13).
    /// </summary>
    private string Display(string ewc)
    {
        var code = ValueConverter.NormalizeCode(ewc);
        if (code.Length == 0) return string.Empty;

        return _offered.FirstOrDefault(o =>
            string.Equals(o.Value, code, StringComparison.OrdinalIgnoreCase))?.Display ?? Describe(code);
    }

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
