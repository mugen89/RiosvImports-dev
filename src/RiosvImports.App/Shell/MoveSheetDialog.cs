using System.Globalization;
using RiosvImports.App.Controls;
using RiosvImports.App.Editor;
using RiosvImports.Core;
using RiosvImports.Core.Excel;
using RiosvImports.Core.Ledger;
using RiosvImports.Core.Localization;
using RiosvImports.Core.Values;
using RiosvImports.Data;

namespace RiosvImports.App.Shell;

/// <summary>
/// Откъде е отворена таблицата — от това зависи дали се внася файл.
///
/// Файл се внася само от „Склад“ → „Внеси…“. Дотук същото внасяне стоеше и в
/// „Месец“, и тестващите питаха кое от двете е правилното — а два пътя до едно
/// внасяне са и два начина един файл да се внесе два пъти.
/// </summary>
internal enum SheetImport
{
    /// <summary>Без внасяне от файл — „Месец“: там се пише и се залепва.</summary>
    None,

    /// <summary>„Внеси…“ стои в лентата, ако движението е отключено за внасяне.</summary>
    Offered,

    /// <summary>Отваря направо менюто за внасяне — „Склад“ → „Внеси…“.</summary>
    Start
}

/// <summary>
/// Въвеждане на цял месец наведнъж — в таблица.
///
/// Прозорчето за един ред е добро за един ред. Приемането обаче не идва по
/// едно: в истински месец те са петстотин и вече съществуват някъде — в
/// счетоводна програма, в кантарен дневник, в лист на Excel. Тогава единственото
/// разумно е да се залепят наведнъж.
///
/// Затова таблицата тук е СЪЩАТА, с която се попълват отчетите
/// (<see cref="SheetGrid"/>): колоните ѝ идват от възела на схемата, залепянето
/// на блок, повтарянето на клетка надолу, пресяването по колона и падащите
/// менюта вече работят и са изпитани. Нова таблица само за склада щеше да е
/// втора таблица, която да догонва първата.
///
/// Разпределението по партиди не се показва тук. При петстотин реда то не се
/// преглежда ред по ред — смята се по FIFO при записа, а когато трябва да се
/// погледне, редът се отваря поединично.
/// </summary>
internal sealed class MoveSheetDialog : Dialog
{
    /// <summary>Скрита колона с номера на движението — по нея редът се познава.</summary>
    private const string IdColumn = "__move";

    /// <summary>
    /// Скрита колона с кой файл е донесъл реда.
    ///
    /// Пътува с реда, а не се пази отстрани, защото между зареждането и записа
    /// човек размества, залепя и трие: списък „това са редовете от файла“ би се
    /// разминал с таблицата още на първото разместване. В самия ред белегът
    /// оцелява каквото и да става с него.
    /// </summary>
    private const string SourceColumn = "__import";

    private readonly Workspace _workspace;
    private readonly long _companyId;
    private readonly string _stagenum;
    private readonly MoveKind _kind;
    private readonly LedgerPeriod _period;

    /// <summary>Видима служебна колона: предаване направо от полученото.</summary>
    private const string DirectColumn = "__direct";

    /// <summary>Видима служебна колона: кодът, с който третираното влиза в книгата.</summary>
    private const string OutColumn = "__ewc_out";

    /// <summary>Видима служебна колона: договорът на предаването.</summary>
    private const string ContractColumn = "__contract";

    /// <summary>Видима служебна колона: бележката на предаването.</summary>
    private const string NoteColumn = "__note";

    private readonly SheetGrid? _grid;
    private readonly SheetPlan? _plan;
    private readonly Label _hint = new();

    /// <summary>Копчето „Внеси…“ — менюто излиза под него, не при мишката.</summary>
    private AppButton? _importButton;

    /// <summary>Файловете, внесени в тази таблица — по белега, който носят редовете им.</summary>
    private readonly Dictionary<string, (string Source, string Path)> _files = new(StringComparer.Ordinal);

    /// <summary>Внася ли се файл оттук — само когато таблицата е отворена от склада.</summary>
    private readonly SheetImport _import;

    /// <summary>
    /// <paramref name="import"/> казва дали оттук се внася файл.
    ///
    /// <see cref="SheetImport.Start"/> отваря направо менюто за внасяне. Ползва се
    /// от „Склад“: там човек е натиснал „Внеси…“ и таблицата е стъпката по пътя, а
    /// не целта. Празна таблица с едно скрито меню е точно толкова далеч от файла,
    /// колкото и преди.
    /// </summary>
    public MoveSheetDialog(
        ImportService service,
        Workspace workspace,
        long companyId,
        string stagenum,
        MoveKind kind,
        LedgerPeriod period,
        SheetImport import = SheetImport.None)
    {
        _workspace = workspace;
        _companyId = companyId;
        _stagenum = stagenum;
        _kind = kind;
        _period = period;
        _import = import;

        var schema = service.GetSchema("book");
        var report = schema.FindReport(BookNodes.Report);

        var plan = report is null
            ? null
            : WorkbookLayout.Build(schema, [report])
                .Sheets.FirstOrDefault(s => s.Node.ElementName == Moves.Node(kind));

        Text = Lang.T($"{Moves.Title(kind)} — {period.Title()}",
                      $"{Moves.Title(kind)} — {period.Title()}");

        FormBorderStyle = FormBorderStyle.Sizable;
        ClientSize = new Size(Theme.Scaled(1180), Theme.Scaled(680));
        MinimumSize = new Size(Theme.Scaled(900), Theme.Scaled(520));
        BackColor = Theme.Canvas;
        Font = Theme.Base;

        _plan = plan;

        if (plan is not null)
        {
            _grid = new SheetGrid(plan, Load(), Extras(kind),
                _workspace.Lists.LimitsFor(_companyId, "book"))
            {
                Dock = DockStyle.Fill
            };

            // Доставчикът се подсказва от списъка, докато се пише: едно име,
            // писано на ръка по трийсет пъти, иначе става три. Допълването на
            // липсващия ЕИК става при записа (LedgerWriter).
            if (kind == MoveKind.Received)
            {
                var suppliers = _workspace.Originators(companyId);

                IReadOnlyList<string> names = [.. suppliers.Select(p => p.Name).Distinct()];
                IReadOnlyList<string> pins = [.. suppliers.Select(p => p.Eik).Where(e => e.Length > 0).Distinct()];

                _grid.Suggest = column => column switch
                {
                    LedgerWriter.NameField => names,
                    LedgerWriter.EikField => pins,
                    _ => []
                };
            }

            // Договорът — същият списък като в прозореца на склада. Новото се
            // научава при записа (LedgerWriter), както там.
            if (Moves.HasContract(kind))
            {
                IReadOnlyList<string> contracts =
                [
                    .. _workspace.Partners.List(companyId, PartnerRole.Contract).Select(p => p.Name)
                ];

                _grid.Suggest = column => column == ContractColumn ? contracts : [];
            }
        }

        Controls.Add(BuildBody());
        Controls.Add(BuildButtons());
        Controls.Add(BuildTitle());

        Theme.Apply(this);

        // След показването, а не в конструктора: меню, отворено върху прозорец,
        // който още го няма, се появява на чужд екран.
        if (import == SheetImport.Start && _importButton is not null) Shown += (_, _) => ImportMenu();
    }

    /// <summary>Дали нещо е записано — страницата се обновява само тогава.</summary>
    public bool Saved { get; private set; }

    /// <summary>Защо записът е спрян — празно, ако не е.</summary>
    internal string Refusal { get; private set; } = string.Empty;

    /// <summary>
    /// Служебните колони на таблицата.
    ///
    /// Номерът на движението е скрит — той е за програмата. „Пряко“ обаче се
    /// вижда и се пипа: на площадка за събиране почти всяко предаване е пряко,
    /// а скрито поле, което мени от кой склад се вади количеството, е точно
    /// това, което човек трябва да види.
    /// </summary>
    private static IReadOnlyList<LocalColumn> Extras(MoveKind kind)
    {
        var extras = new List<LocalColumn>
        {
            new(IdColumn, string.Empty, LocalColumnKind.Text, 0) { Hidden = true },
            new(SourceColumn, string.Empty, LocalColumnKind.Text, 0) { Hidden = true }
        };

        if (Moves.CanBeDirect(kind))
            extras.Add(new LocalColumn(DirectColumn, Lang.T("Пряко", "Direct"), LocalColumnKind.Text, 80,
                Lang.T("„да“ — предава се направо от полученото, без третиране.",
                       "“да” — handed over straight from received stock, without treatment.")));

        if (Moves.HasContract(kind))
            extras.Add(new LocalColumn(ContractColumn, Moves.ContractTitle, LocalColumnKind.Text, 180,
                Lang.T("По какъв договор се предава — по избор. В отчета не влиза.",
                       "Which contract this hand-over is under — optional. It does not enter the report."))
            {
                Editable = true
            });

        // Бележката на предаването се пресява в склад „Предаден“ и трябва да се
        // пише и оттук. При другите видове таблицата я пази, без да я показва.
        if (Moves.HasContract(kind))
            extras.Add(new LocalColumn(NoteColumn, Lang.T("Бележка", "Note"), LocalColumnKind.Text, 180,
                Lang.T("Свободна бележка за пресяване в „Предаден“. В отчета не влиза.",
                       "A free note for filtering in “Handed over”. It does not enter the report."))
            {
                Editable = true
            });

        // Кодът горе е приетият — по него се вади складът. Ако в книгата
        // третираното трябва да влезе с друг код, той се пише тук. Празно значи
        // „същият“.
        if (kind == MoveKind.Treated)
            extras.Add(new LocalColumn(OutColumn, Lang.T("Код за отчета", "Report code"),
                LocalColumnKind.Text, 120,
                Lang.T("Кодът, с който третираното влиза в отчета. Празно — същият като приетия.",
                       "The code the treatment enters the report with. Blank — the same as received.")));

        return extras;
    }

    /// <summary>Служебните колони, както ги иска изнасянето.</summary>
    private IReadOnlyList<(string Name, string Header)> ExportExtras() =>
    [
        .. Extras(_kind).Where(c => !c.Hidden).Select(c => (c.Name, c.Header))
    ];

    // ------------------------------------------------------------- устройство

    private Control BuildTitle()
    {
        var host = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(76),
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, Theme.Gap, Theme.GapLarge, 0)
        };

        host.Controls.Add(new Label
        {
            Dock = DockStyle.Bottom,
            Height = Theme.Scaled(24),
            Text = Lang.T(
                "Залепете редове от Excel с Ctrl+V. Дата, код и количество са задължителни. " +
                "Ред с дата извън месеца се записва в своя месец.",
                "Paste rows from Excel with Ctrl+V. Date, code and quantity are required. " +
                "A row dated outside this month is saved into its own month."),
            Font = Theme.Base,
            ForeColor = Theme.InkMuted
        });

        host.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = Lang.T($"{Moves.Title(_kind)} — {_period.Title()}",
                          $"{Moves.Title(_kind)} — {_period.Title()}"),
            Font = Theme.Title,
            ForeColor = Theme.Ink
        });

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

        if (_grid is not null) host.Controls.Add(_grid);

        return host;
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

        var save = Theme.PrimaryButton(Lang.T("Записване", "Save"));
        save.Height = buttonHeight;
        save.Margin = Padding.Empty;
        save.Click += (_, _) => Accept();

        var cancel = Theme.GhostButton(Lang.T("Отказ", "Cancel"));
        cancel.Height = buttonHeight;
        cancel.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;

        tools.Controls.Add(save.FitWidth());
        tools.Controls.Add(cancel.FitWidth());

        // Внасянето и изнасянето стоят отляво, далеч от „Записване“: те работят
        // върху таблицата, а не върху склада, и объркването им със записа би
        // било скъпо.
        var sources = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Canvas
        };

        // Внасянето стои само там, където е отключено — а по подразбиране това е
        // единствено приемането. Третирането, образуването и предаването се
        // РАЖДАТ тук: внесени отвън, те носят количество, но не изчерпват
        // партида и не се виждат в пътя ѝ (виж ImportPolicy).
        //
        // Копчето се маха, а не се угасява: угасено копче кара човека да търси
        // какво му липсва, за да го включи. Тук липсва решение, а то се взима на
        // друг екран — и надписът отдолу казва къде.
        if (_import != SheetImport.None && _workspace.Periods.CanImport(_companyId, _kind))
        {
            _importButton = Action(Lang.T("Внеси…", "Import…"), Theme.Glyph.Import, ImportMenu);
            sources.Controls.Add(_importButton);
        }
        sources.Controls.Add(Action(Lang.T("Изнеси…", "Export…"), Theme.Glyph.Excel, () => Export()));

        // Без внасяне оттук се казва къде е: копче, което го няма, иначе се търси.
        if (_import == SheetImport.None && _workspace.Periods.CanImport(_companyId, _kind))
            _hint.Text = Lang.T(
                "Файл от „Деметра“ или от наш шаблон се внася от „Склад“ → „Внеси…“.",
                "A Demetra file or one of our templates is imported from “Store” → “Import…”.");

        _hint.Dock = DockStyle.Fill;
        _hint.AutoSize = false;
        _hint.Font = Theme.Small;
        _hint.ForeColor = Theme.InkMuted;
        _hint.TextAlign = ContentAlignment.MiddleLeft;
        _hint.Padding = new Padding(Theme.Gap, 0, 0, 0);

        bar.Controls.Add(_hint);
        bar.Controls.Add(tools);
        bar.Controls.Add(sources);

        CancelButton = cancel;

        return bar;
    }

    // ------------------------------------------------------------- данни

    /// <summary>
    /// Движенията на месеца като редове за таблицата.
    ///
    /// Номерът на движението пътува в скрита колона: по него редът се познава
    /// при записа и поправеното си остава поправено, вместо целият месец да се
    /// изтрива и да се записва наново с нови номера — което би скъсало
    /// дяловете, а с тях и произхода.
    /// </summary>
    private List<Dictionary<string, string>> Load()
    {
        var rows = new List<Dictionary<string, string>>();

        var parties = Moves.HasContract(_kind)
            ? _workspace.Ledger.Parties(_companyId, _stagenum)
            : new Dictionary<long, HandParty>();

        var moves = _workspace.Ledger.Read(_companyId, _stagenum)
            .Where(m => m.Kind == _kind && !m.Opening && _period.Covers(m.Date))
            .OrderBy(m => m.Date)
            .ThenBy(m => m.Id);

        foreach (var move in moves)
        {
            var row = new Dictionary<string, string>(_workspace.Ledger.Fields(move.Id), StringComparer.Ordinal)
            {
                ["date_record"] = move.Date.ToString(ValueConverter.BulgarianDateFormat),
                ["ewc"] = move.Ewc,
                ["amount_ton"] = SplitMath.Amount(move.Amount),
                [WorkbookLayout.StagenumLink] = move.Stagenum,
                [IdColumn] = move.Id.ToString()
            };

            // Служебните стойности се връщат в таблицата, иначе записът им ги
            // губи: онова, което не се е показало, се записва като непопълнено.
            if (_kind == MoveKind.Treated && move.EwcOut.Length > 0) row[OutColumn] = move.EwcOut;
            if (Moves.CanBeDirect(_kind) && move.Direct) row[DirectColumn] = Lang.T("да", "yes");
            if (parties.GetValueOrDefault(move.Id)?.Contract is { Length: > 0 } contract) row[ContractColumn] = contract;
            if (parties.GetValueOrDefault(move.Id)?.Note is { Length: > 0 } note) row[NoteColumn] = note;

            // Клиентът се пази в колоните на схемата, а при приемане идва от
            // партидата: тя е мястото, където той наистина живее.
            if (_kind == MoveKind.Received &&
                _workspace.Ledger.Draws(move.Id).FirstOrDefault() is { } draw &&
                _workspace.Batches.Find(draw.BatchId) is { } batch)
            {
                row["pin_originator"] = batch.Eik;
                row["name_originator"] = batch.PartnerName;
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// Записва таблицата.
    ///
    /// Редовете се подреждат по дата преди записа: разпределението по FIFO
    /// гледа наличността към датата на реда, а тя зависи от вече записаните
    /// преди него. Записан наопаки, месецът би излязъл на недостиг там, където
    /// няма такъв.
    /// </summary>
    private void Accept()
    {
        if (_grid is null)
        {
            DialogResult = DialogResult.Cancel;
            return;
        }

        var problems = new List<string>();
        var parsed = new List<(int Line, MoveRequest Request, string Mark)>();
        var line = 0;

        var stored = _workspace.Ledger.Read(_companyId, _stagenum).ToDictionary(m => m.Id);

        foreach (var row in _grid.Snapshot())
        {
            line++;

            if (Read(row, line, problems, stored) is { } request)
                parsed.Add((line, request, row.GetValueOrDefault(SourceColumn, string.Empty)));
        }

        // Затворен месец не приема нов ред. Проверява се тук, а не по реда:
        // едва сега се знаят всички дати наведнъж, а и отговорът трябва да е
        // за месеца, не за реда.
        foreach (var month in parsed
                     .Select(p => LedgerPeriod.Of(p.Request.Date))
                     .Distinct()
                     .Where(m => _workspace.Periods.IsClosed(_companyId, _stagenum, m))
                     .OrderBy(m => m))
            problems.Add(Lang.T($"{month.Title()} е затворен и не приема записи.",
                                $"{month.Title()} is closed and accepts no entries."));

        if (problems.Count > 0)
        {
            MessageBox.Show(this,
                string.Join(Environment.NewLine, problems.Take(10)),
                Lang.T("Има недовършени редове", "Some rows are unfinished"),
                MessageBoxButtons.OK, MessageBoxIcon.Warning);

            return;
        }

        var kept = parsed.Select(p => p.Request.MoveId).OfType<long>().ToHashSet();

        var gone = _workspace.Ledger.Read(_companyId, _stagenum)
            .Where(m => m.Kind == _kind && !m.Opening && _period.Covers(m.Date))
            .Where(m => !kept.Contains(m.Id))
            .ToList();

        // Махнатият ред, от който вече е черпено, спира записа — както в
        // дневника и при Delete в „Месец“ (дефект 9). Иначе едни и същи тонове
        // остават в два склада, а таблицата казва „записано“.
        if (gone.Count > 0 &&
            LedgerPage.Refused(
                _workspace.Ledger.Read(_companyId, _stagenum),
                _workspace.Ledger.Links(_companyId, _stagenum),
                [.. gone.Select(m => m.Id)]) is { } why)
        {
            Refusal = why;

            if (Visible)
                MessageBox.Show(this, why, Lang.T("От него вече е взето", "It has already been drawn on"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);

            return;
        }

        // Махнатите от таблицата си отиват и от склада.
        foreach (var move in gone)
            _workspace.Writer.Remove(_companyId, move.Id);

        // Кой файл какво е родил — събира се при самия запис, защото само тук се
        // знае номерът, който всеки ред е получил.
        var written = new Dictionary<string, List<long>>(StringComparer.Ordinal);

        foreach (var (_, request, mark) in parsed.OrderBy(p => p.Request.Date).ThenBy(p => p.Line))
        {
            var moveId = _workspace.Writer.Write(_companyId, request);

            if (mark.Length == 0 || request.MoveId is not null) continue;

            if (!written.TryGetValue(mark, out var ids)) written[mark] = ids = [];

            ids.Add(moveId);
        }

        Remember(written);

        Saved = true;
        DialogResult = DialogResult.OK;
    }

    /// <summary>
    /// Отбелязва нов внесен файл и връща белега, който редовете му ще носят.
    ///
    /// Белегът е пореден в рамките на прозореца, а не име на файл: един и същи
    /// файл може да се внесе два пъти, а второто внасяне е отделно събитие и се
    /// маха отделно.
    /// </summary>
    private string Mark(string source, string path)
    {
        var mark = (_files.Count + 1).ToString(CultureInfo.InvariantCulture);
        _files[mark] = (source, path);

        return mark;
    }

    /// <summary>
    /// Запазва следата на внесеното: кой файл кои движения е родил.
    ///
    /// Записва се СЛЕД движенията, защото номерата им се знаят чак тогава.
    /// Прекъснат по средата запис оставя движения без следа — тогава те са
    /// просто обикновени редове, каквито са били и преди дневникът да го има.
    ///
    /// Партидите вървят с приемането: то ги завежда, файлът ги е донесъл и с
    /// него си отиват. При другите три вида дяловете сочат към чужди партиди —
    /// заведени от друго приемане — и те не са на този файл, за да ги маха.
    /// </summary>
    private void Remember(IReadOnlyDictionary<string, List<long>> written)
    {
        foreach (var (mark, moves) in written)
        {
            if (moves.Count == 0 || !_files.TryGetValue(mark, out var file)) continue;

            var importId = _workspace.Imports.Begin(_companyId, file.Source, file.Path,
                Lang.T($"{Moves.Title(_kind)} — {_period.Title()}",
                       $"{Moves.Title(_kind)} — {_period.Title()}"));

            _workspace.Imports.Track(importId, ImportTarget.Move, moves);

            if (_kind != MoveKind.Received) continue;

            _workspace.Imports.Track(importId, ImportTarget.Batch,
                moves.SelectMany(id => _workspace.Ledger.Draws(id)).Select(d => d.BatchId));
        }
    }

    /// <summary>
    /// Един ред от таблицата — или null, ако е празен или недовършен.
    ///
    /// Записът пренаписва всеки ред, а таблицата показва само част от движението.
    /// Каквото тя не носи, трябва да се пренесе изрично, иначе се губи мълчаливо:
    /// бележката, а преди всичко дяловете. Предадено от „Образуван“ не намира
    /// партида по FIFO — партидата помни кода, с който отпадъкът е дошъл — и с
    /// едно „Записване“ всички предавания на месеца оставаха без произход, а
    /// количествата им изскачаха обратно в склада.
    ///
    /// Пази се посоченото, докато редът е какъвто е бил. Променени дата, код или
    /// количество са ново решение и разпределението се смята наново.
    /// </summary>
    private MoveRequest? Read(
        Dictionary<string, string> row, int line, List<string> problems, IReadOnlyDictionary<long, Move> stored)
    {
        var date = row.GetValueOrDefault("date_record", string.Empty).Trim();
        var code = ValueConverter.NormalizeCode(row.GetValueOrDefault("ewc"));
        var amountText = row.GetValueOrDefault("amount_ton", string.Empty).Trim();

        // Съвсем празен ред е място за писане, а не пропуск.
        if (date.Length == 0 && code.Length == 0 && amountText.Length == 0) return null;

        var missing = new List<string>();

        DateOnly? day = null;

        if (ValueConverter.TryNormalizeDate(date, out var normalized) &&
            DateOnly.TryParseExact(normalized, ValueConverter.BulgarianDateFormat, out var parsed))
            day = parsed;
        else
            missing.Add(Lang.T("дата", "date"));

        if (code.Length == 0) missing.Add(Lang.T("код", "code"));

        var amount = ValueConverter.TryAmount(amountText) ?? 0m;
        if (amount <= 0) missing.Add(Lang.T("количество", "quantity"));

        if (missing.Count > 0 || day is not { } when_)
        {
            problems.Add(Lang.T($"Ред {line}: липсва {string.Join(", ", missing)}.",
                                $"Row {line}: missing {string.Join(", ", missing)}."));
            return null;
        }

        // Дата извън отворения месец НЕ е грешка.
        //
        // Месецът тук е изглед, а не заграждение: периодът на движението се
        // извежда от датата му при записа, тъй че редът просто отива в своя
        // месец. Обратното — да се отхвърля — правеше внасянето на юлски
        // дневник през септември невъзможно и караше човек да отваря месец
        // само за да може да залепи в него.
        //
        // Затворен месец е друго: там наистина не се пише, и това се проверява
        // при записа, където се знаят всички редове наведнъж.
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (name, value) in row)
        {
            if (name.StartsWith("__", StringComparison.Ordinal)) continue;
            if (BookNodes.Owned.Contains(name)) continue;
            if (value.Trim().Length == 0) continue;

            fields[name] = value.Trim();
        }

        long? moveId = long.TryParse(row.GetValueOrDefault(IdColumn), out var id) ? id : null;

        var direct = Yes(row.GetValueOrDefault(DirectColumn));
        var rounded = SplitMath.Round(amount);

        var was = moveId is { } known ? stored.GetValueOrDefault(known) : null;

        var same = was is not null &&
                   was.Date == when_ &&
                   was.Amount == rounded &&
                   was.Direct == direct &&
                   string.Equals(ValueConverter.NormalizeCode(was.Ewc), code, StringComparison.Ordinal);

        return new MoveRequest(
            _kind,
            when_,
            code,
            rounded,
            _stagenum,
            fields.GetValueOrDefault("pin_originator", string.Empty),
            fields.GetValueOrDefault("name_originator", string.Empty),
            fields.GetValueOrDefault("reason", string.Empty),
            Note: Moves.HasContract(_kind)
                ? row.GetValueOrDefault(NoteColumn, string.Empty)
                : was is null ? string.Empty : _workspace.Ledger.Note(was.Id),
            Direct: direct,
            Fields: fields,
            MoveId: moveId,
            Draws: same ? _workspace.Ledger.Draws(was!.Id) : null,
            EwcOut: ValueConverter.NormalizeCode(row.GetValueOrDefault(OutColumn)),
            Contract: row.GetValueOrDefault(ContractColumn, string.Empty));
    }

    /// <summary>
    /// Как се чете отметка в клетка.
    ///
    /// Пише се и на двата езика, и с една буква: таблицата се пълни и от Excel,
    /// а там човек пише каквото му е под ръка.
    /// </summary>
    private static bool Yes(string? text)
    {
        var value = text?.Trim().ToLowerInvariant() ?? string.Empty;

        return value is "да" or "д" or "yes" or "y" or "1" or "true" or "x";
    }

    // ------------------------------------------------------------- внасяне

    private static AppButton Action(string text, string glyph, Action handler)
    {
        var button = Theme.GhostButton(text, glyph);
        button.Height = Theme.Scaled(40);
        button.Margin = new Padding(0, 0, Theme.GapSmall, 0);
        button.Click += (_, _) => handler();

        return button.FitWidth();
    }

    /// <summary>Откъде да дойдат редовете.</summary>
    private void ImportMenu()
    {
        var menu = new ContextMenuStrip { Font = Theme.Base };

        menu.Items.Add(Lang.T("От наш шаблон…", "From our template…"), null, (_, _) => ImportTemplate());
        menu.Items.Add(Lang.T("От „Деметра“…", "From Demetra…"), null, (_, _) => ImportDemetra());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Lang.T("Изнеси празен шаблон…", "Export a blank template…"), null,
            (_, _) => Export(blank: true));

        // Под копчето, когато го има: менюто може да е отворено и без мишка —
        // от „Склад“, където човек вече е натиснал „Внеси…“ на друг екран.
        if (_importButton is { IsHandleCreated: true } button)
            menu.Show(button, new Point(0, button.Height));
        else
            menu.Show(MousePosition);
    }

    /// <summary>
    /// Внася от наш шаблон.
    ///
    /// Колоните му са същите като на таблицата, затова прочетеното влиза
    /// направо — без съответствие и без тълкуване.
    /// </summary>
    private void ImportTemplate()
    {
        if (_grid is null || _plan is null) return;
        if (Ask(Lang.T("Внасяне от шаблон", "Import from a template")) is not { } path) return;

        try
        {
            // Excel файлът се чете секунда-две: часовникът казва, че нещо става,
            // вместо прозорецът да изглежда замръзнал.
            UseWaitCursor = true;

            int added;
            IReadOnlyList<string> problems;

            try
            {
                added = LoadTemplate(path, out problems);
            }
            finally
            {
                UseWaitCursor = false;
            }

            Say(problems.Count > 0
                ? string.Join(" ", problems.Take(2))
                : Lang.T($"Заредени са {added} реда. Прегледайте ги и запишете.",
                         $"{added} rows loaded. Review them and save."));
        }
        catch (Exception ex)
        {
            Failed(ex);
        }
    }

    /// <summary>
    /// Слага редовете от наш шаблон в таблицата и връща колко са.
    ///
    /// Отделено от избора на файл, за да може да се провери с тест: прозорче за
    /// избор на файл не се отваря на тестова нишка.
    /// </summary>
    internal int LoadTemplate(string path, out IReadOnlyList<string> problems)
    {
        problems = [];

        if (_grid is null || _plan is null) return 0;

        var file = LedgerWorkbook.Read(path, _plan, ExportExtras());
        problems = file.Problems;

        var mark = Mark(ImportSource.Template, path);

        // Внесеният ред е нов, дори да идва от изнесен: номерът на движението
        // не се пренася, за да не залепне за чужд запис.
        return _grid.AppendRows(file.Rows.Select(row =>
        {
            var values = new Dictionary<string, string>(row, StringComparer.Ordinal);
            values.Remove(IdColumn);
            values[SourceColumn] = mark;

            return (IReadOnlyDictionary<string, string>)values;
        }));
    }

    /// <summary>
    /// Внася от дневник на „Деметра“.
    ///
    /// Файлът носи и четирите вида движение наведнъж, а таблицата е за един —
    /// затова се зареждат само редовете на този вид и се казва колко са
    /// останалите. Извън месеца също не се зареждат: те не биха се записали и
    /// щяха да спрат целия запис заради чужда дата.
    /// </summary>
    private void ImportDemetra()
    {
        if (_grid is null) return;
        if (Ask(Lang.T("Внасяне от „Деметра“", "Import from Demetra")) is not { } path) return;

        try
        {
            var loaded = LoadDemetra(path);

            Say(loaded.Text);

            // Редовете са за друг месец: това не е пречка, но трябва да се
            // каже — иначе човек записва и после не намира нищо в месеца, който
            // гледа.
            if (loaded.Rows > 0 && !loaded.Months.Contains(_period))
                MessageBox.Show(this,
                    Lang.T(
                        $"Заредени са {loaded.Rows} реда за {loaded.MonthList}." +
                        $"{Environment.NewLine}{Environment.NewLine}" +
                        $"Отворен е {_period.Title()}. Редовете ще се запишат в своя месец, " +
                        "не в отворения — изберете го горе, за да ги видите след записа.",
                        $"{loaded.Rows} rows loaded for {loaded.MonthList}." +
                        $"{Environment.NewLine}{Environment.NewLine}" +
                        $"{_period.Title()} is open. The rows will be saved into their own month, " +
                        "not the open one — pick it above to see them after saving."),
                    Lang.T("Друг период", "Another period"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);

            else if (loaded.Rows == 0 && loaded.Others > 0)
                MessageBox.Show(this,
                    Lang.T(
                        $"Във файла няма редове от вид „{Moves.Title(_kind)}“. " +
                        $"Има {loaded.Others} реда от други видове движение — внесете ги от техните таблици.",
                        $"The file has no “{Moves.Title(_kind)}” rows. " +
                        $"It holds {loaded.Others} rows of other movement kinds — import those from their own tables."),
                    Lang.T("Друг вид движение", "Another movement kind"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Failed(ex);
        }
    }

    /// <summary>
    /// Показва какво носи файлът и връща онова, което човек е оставил.
    ///
    /// Null значи „отказано“ — тогава нищо не влиза. Отделено в свой метод,
    /// защото тестът минава по същия път без прозорче (правило 4): подменя се
    /// само тази стъпка.
    /// </summary>
    private IReadOnlyList<ImportedMove>? Preview(string fileName, ImportOutcome outcome)
    {
        if (Previewer is { } instead) return instead(outcome);

        // Прозорче — само на показан прозорец. Модален прозорец върху нишка без
        // съобщителен цикъл виси завинаги; тестът минава по същия път и взима
        // всичко, освен ако сам не е подменил прегледа.
        if (!Visible) return outcome.Moves;

        using var dialog = new ImportPreviewDialog(
            fileName, outcome.Moves, _kind, outcome.Problems.Count);

        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.Kept : null;
    }

    /// <summary>
    /// Кой показва прегледа — подменя се от теста.
    ///
    /// Модален прозорец върху нишка без съобщителен цикъл виси завинаги, а
    /// пътят до внасянето трябва да се проверява целият.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<ImportOutcome, IReadOnlyList<ImportedMove>?>? Previewer { get; set; }

    /// <summary>Какво е влязло от чуждия дневник и какво е останало навън.</summary>
    internal sealed record Loaded(
        int Rows,
        int Others,
        int Unread,
        IReadOnlyList<LedgerPeriod> Months)
    {
        /// <summary>Месеците, които редовете покриват — изписани за човек.</summary>
        public string MonthList => string.Join(", ", Months.Select(m => m.Title()));

        public string Text
        {
            get
            {
                var said = Lang.T($"Заредени са {Rows} реда.", $"{Rows} rows loaded.");

                if (Months.Count > 0)
                    said += Lang.T($" Ще се запишат в {MonthList}.",
                                   $" They will be saved into {MonthList}.");

                if (Others > 0)
                    said += Lang.T($" {Others} реда са от друг вид движение.",
                                   $" {Others} rows belong to other movement kinds.");

                if (Unread > 0)
                    said += Lang.T($" {Unread} реда не се разчетоха.",
                                   $" {Unread} rows could not be read.");

                return said;
            }
        }
    }

    /// <summary>
    /// Слага редовете от дневник на „Деметра“ в таблицата.
    ///
    /// Отделено от избора на файл по същата причина, по която и четенето на наш
    /// шаблон: така пътят се проверява с тест срещу истински износ.
    /// </summary>
    internal Loaded LoadDemetra(string path)
    {
        if (_grid is null) return new Loaded(0, 0, 0, []);

        ImportOutcome outcome;

        // Четенето на чуждия дневник е бавната част — часовникът стои до
        // прегледа, но не и в него: там човек работи.
        UseWaitCursor = true;

        try
        {
            var file = DemetraWorkbook.Read(path);

            var plan = new ImportPlan(
                DemetraWorkbook.SuggestedKinds,
                file.Sites.ToDictionary(site => site, _ => _stagenum, StringComparer.OrdinalIgnoreCase),
                DemetraWorkbook.SuggestedDirect);

            outcome = LedgerImport.Apply(file, plan);
        }
        finally
        {
            UseWaitCursor = false;
        }

        // ПРЕГЛЕД ПРЕДИ ВЛИЗАНЕ.
        //
        // Чуждият дневник се изнася цял и в него редовно има редове, които тук
        // нямат работа: сторнирано приемане, курс на друго дружество, грешка на
        // оператора отсреща. Влязат ли в таблицата, те вече са се смесили с
        // останалите и се търсят като игла. Затова прозорчето стои МЕЖДУ файла
        // и таблицата и нататък тръгва само каквото е останало.
        var picked = Preview(Path.GetFileName(path), outcome);

        if (picked is null) return new Loaded(0, 0, 0, []);

        // Всички редове от този вид, независимо от месеца им: месецът горе е
        // изглед, а всеки ред отива в своя при записа. Иначе внасянето на
        // юлски дневник изискваше първо да се отвори юли — а той се появяваше
        // в списъка чак след като има какво да покаже.
        var mine = picked.Where(m => m.Kind == _kind).ToList();

        var mark = Mark(ImportSource.Demetra, path);

        _grid.AppendRows(mine.Select(move =>
        {
            var row = Row(move);
            row[SourceColumn] = mark;

            return (IReadOnlyDictionary<string, string>)row;
        }));

        var months = mine
            .Select(m => LedgerPeriod.Of(m.Date))
            .Distinct()
            .OrderBy(m => m)
            .ToList();

        return new Loaded(mine.Count, picked.Count - mine.Count, outcome.Problems.Count, months);
    }

    /// <summary>Внесено движение като ред за таблицата.</summary>
    private Dictionary<string, string> Row(ImportedMove move)
    {
        var row = new Dictionary<string, string>(move.Fields, StringComparer.Ordinal)
        {
            ["date_record"] = move.Date.ToString(ValueConverter.BulgarianDateFormat),
            ["ewc"] = move.Ewc,
            ["amount_ton"] = SplitMath.Amount(move.Amount),
            [WorkbookLayout.StagenumLink] = move.Stagenum
        };

        // Клиентът се казва различно от двете страни на пътя.
        if (_kind == MoveKind.Received)
        {
            if (move.Partner.Length > 0) row["name_originator"] = move.Partner;
            if (move.Eik.Length > 0) row["pin_originator"] = move.Eik;
        }
        else if (_kind == MoveKind.Submitted)
        {
            if (move.Partner.Length > 0) row["name_receiver"] = move.Partner;
            if (move.Eik.Length > 0) row["pin_receiver"] = move.Eik;
            if (move.Direct) row[DirectColumn] = Lang.T("да", "yes");
        }

        if (move.Document.Length > 0) row["reason"] = move.Document;

        return row;
    }

    /// <summary>Изнася таблицата в наш шаблон — за поправяне в Excel и връщане.</summary>
    private void Export(bool blank = false)
    {
        if (_grid is null || _plan is null) return;

        using var dialog = new SaveFileDialog
        {
            Filter = Lang.T("Работна книга на Excel (*.xlsx)|*.xlsx", "Excel workbook (*.xlsx)|*.xlsx"),
            FileName = $"{Moves.Title(_kind)} {_period}.xlsx",
            InitialDirectory = _workspace.Settings.Get(SettingsStore.Keys.LastFolder, string.Empty)
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var written = SaveTemplate(dialog.FileName, blank);

            _workspace.Settings.Set(SettingsStore.Keys.LastFolder,
                Path.GetDirectoryName(dialog.FileName) ?? string.Empty);

            Say(blank
                ? Lang.T("Празният шаблон е записан.", "The blank template is saved.")
                : Lang.T($"Изнесени са {written} реда.", $"{written} rows exported."));
        }
        catch (Exception ex)
        {
            Failed(ex);
        }
    }

    /// <summary>Записва таблицата в наш шаблон и връща колко реда са влезли.</summary>
    internal int SaveTemplate(string path, bool blank = false)
    {
        if (_grid is null || _plan is null) return 0;

        var rows = blank
            ? new List<IReadOnlyDictionary<string, string>>()
            : [.. _grid.Snapshot().Select(r => (IReadOnlyDictionary<string, string>)r)];

        LedgerWorkbook.Save(path, _plan, ExportExtras(), rows);

        return rows.Count;
    }

    private string? Ask(string title)
    {
        using var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = Lang.T("Работна книга на Excel (*.xlsx)|*.xlsx", "Excel workbook (*.xlsx)|*.xlsx"),
            InitialDirectory = _workspace.Settings.Get(SettingsStore.Keys.LastFolder, string.Empty)
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return null;

        _workspace.Settings.Set(SettingsStore.Keys.LastFolder,
            Path.GetDirectoryName(dialog.FileName) ?? string.Empty);

        return dialog.FileName;
    }

    private void Say(string text)
    {
        _hint.Text = text;
        _hint.ForeColor = Theme.InkMuted;
    }

    private void Failed(Exception ex)
    {
        _hint.Text = ex.Message;
        _hint.ForeColor = Theme.Error;
    }

    // ------------------------------------------------------------- за теста

    internal SheetGrid? Grid => _grid;

    /// <summary>Има ли копче „Внеси…“ — само когато таблицата е отворена от склада.</summary>
    internal bool ImportOffered => _importButton is not null;

    /// <summary>Какво казва долният ред — пътят до внасянето, когато го няма тук.</summary>
    internal string HintText => _hint.Text;

    internal int RowCount => _grid?.DataRowCount ?? 0;

    internal void AcceptForTest() => Accept();
}
