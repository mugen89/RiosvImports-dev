using RiosvImports.App.Controls;
using RiosvImports.App.Updates;
using RiosvImports.Data;

using RiosvImports.Core.Ledger;
using RiosvImports.Core.Localization;

namespace RiosvImports.App.Shell;

/// <summary>
/// Настройки — стойностите, които се въвеждат веднъж и после само се предлагат.
///
/// Разделено на рамки по теми. Без тях страницата беше един непрекъснат стълб
/// от надписи и полета, в който окото не разбира къде свършва едното и започва
/// другото.
///
/// Изгледът си има собствен раздел: той се пробва — сменя се, поглежда се,
/// връща се — а не се настройва веднъж като ЕИК.
/// </summary>
internal sealed class SettingsPage : ContentPage
{
    private readonly Workspace _workspace;

    /// <summary>Фирмата от лентата — затварянето на месеци е нейно.</summary>
    private readonly CompanyScope _scope;

    private readonly CompanyList _companies = new();
    private readonly Label _companyCount = new();
    private readonly CheckBox _confirmDelete = new();

    /// <summary>Заключва истинското изтриване от дневника — докато отчетът се подава.</summary>
    private readonly CheckBox _lockLogDelete = new();

    /// <summary>Изгледите на склада — с икони или с надписи.</summary>
    private readonly CheckBox _viewIcons = new();
    private readonly Label _account = new();
    private readonly Label _database = new();

    private AppButton _addCompany = null!;

    /// <summary>
    /// Махнато внасяне — страниците вече показват друго.
    ///
    /// Настройките обикновено не пипат данни, затова събитието е едно и е тук,
    /// а не по страници: махането отнася наведнъж движения, партиди и отчети, а
    /// кой екран какво показва от тях знае обвивката.
    /// </summary>
    public event EventHandler? DataChanged;

    // Работният период: самоконтролът на човека за това КОГА пише.
    private readonly CheckBox _periodOn = new();
    private readonly DateTimePicker _periodFrom = new();
    private readonly DateTimePicker _periodTo = new();
    private readonly Label _periodSays = new();

    /// <summary>По една отметка на движение — приемането е винаги вдигнато.</summary>
    private readonly Dictionary<MoveKind, CheckBox> _unlock = [];

    private readonly Label _unlockSays = new();

    /// <summary>Докато се пълни от базата, писането обратно в нея се спира.</summary>
    private bool _loading;

    /// <summary>Обновяването — същото, което показва и началният екран.</summary>
    private readonly UpdateService _updates;

    private readonly Label _version = new();
    private readonly Label _updateSays = new();
    private readonly CheckBox _autoCheck = new();
    private readonly CheckBox _prerelease = new();
    private readonly Label _supportSays = new();

    /// <summary>Под пътя до базата: какво е това място и какво става с него.</summary>
    private readonly Label _databaseSays = new();

    /// <summary>Какво прави изчистването — и после какво е станало.</summary>
    private readonly Label _testingSays = new();

    private AppButton _checkNow = null!;
    private AppButton _updateNow = null!;

    /// <summary>Стълбът с рамките — превърта се до избраната от лентата горе.</summary>
    private readonly FlowLayoutPanel _stack;

    /// <summary>„Провери сега“ — обвивката пита и за изданието, и за новините.</summary>
    public event EventHandler? CheckRequested;

    /// <summary>„Рестартирай и обнови“ — обвивката затваря програмата и пуска инсталатора.</summary>
    public event EventHandler? RestartRequested;

    /// <summary>
    /// Фирмата идва отвън — от лентата отляво. Подаден null значи „направи си
    /// свой обхват“: така страницата се сглобява и сама, което ползва тестът.
    /// Същото и с обновяването.
    /// </summary>
    public SettingsPage(Workspace workspace, CompanyScope? scope = null, UpdateService? updates = null) : base(1020)
    {
        _workspace = workspace;
        _scope = scope ?? new CompanyScope(workspace);
        _updates = updates ?? new UpdateService(prerelease => UpdateBackends.Create(prerelease, AppServices.Web));

        _stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true
        };

        _stack.Controls.Add(Title(Lang.T("Настройки", "Settings")));
        _stack.Controls.Add(Subtitle(Lang.T("Данните, които се въвеждат веднъж и после само се предлагат.", "The details you enter once and are then only offered back to you.")));

        // Осем рамки са три екрана на лаптоп. Лентата горе води до всяка с едно
        // щракване, вместо да се търси с колелцето.
        (string Jump, Control Frame)[] frames =
        [
            (Lang.T("Фирми", "Companies"), BuildCompanies()),
            (Lang.T("Работен период", "Working period"), BuildPeriod()),
            (Lang.T("Внасяне", "Importing"), BuildImports()),
            (Lang.T("Работа", "Working"), BuildWork()),
            (Lang.T("Профил", "Account"), BuildAccount()),
            (Lang.T("Обновяване", "Updates"), BuildUpdates()),
            (Lang.T("База", "Database"), BuildDatabase()),
            (Lang.T("Поддръжка", "Support"), BuildSupport()),
            (Lang.T("За тестване", "For testing"), BuildTesting())
        ];

        _stack.Controls.Add(BuildJumps(frames));

        foreach (var (_, frame) in frames) _stack.Controls.Add(frame);

        Controls.Add(_stack);

        Load();
    }

    /// <summary>
    /// Лентата с имената на рамките. Пренася се на втори ред, когато не се
    /// побира — при 150 % осемте имена не влизат в един.
    /// </summary>
    private Control BuildJumps(IReadOnlyList<(string Jump, Control Frame)> frames)
    {
        var bar = new FlowLayoutPanel
        {
            Width = Theme.Scaled(940),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, Theme.GapSmall)
        };

        foreach (var (jump, frame) in frames)
        {
            var button = Theme.GhostButton(jump);
            button.Height = Theme.Scaled(32);
            button.Margin = new Padding(0, 0, Theme.Scaled(4), Theme.Scaled(4));
            button.FitWidth(8);
            button.Click += (_, _) => JumpTo(frame);

            bar.Controls.Add(button);
        }

        bar.Height = WrappedHeight(bar);

        return bar;
    }

    /// <summary>Колко висока става лента с пренасяне при тази ширина — броят се редовете.</summary>
    internal static int WrappedHeight(FlowLayoutPanel bar)
    {
        var x = 0;
        var rows = 1;
        var row = 0;

        foreach (Control control in bar.Controls)
        {
            var width = control.Width + control.Margin.Horizontal;

            if (x > 0 && x + width > bar.Width - bar.Padding.Horizontal)
            {
                rows++;
                x = 0;
            }

            x += width;
            row = Math.Max(row, control.Height + control.Margin.Vertical);
        }

        return rows * row + bar.Padding.Vertical;
    }

    /// <summary>Рамката застава горе, а не „някъде в полезрението“ — там окото не гледа.</summary>
    private void JumpTo(Control frame)
    {
        var top = frame.Top - _stack.AutoScrollPosition.Y;
        _stack.AutoScrollPosition = new Point(0, Math.Max(0, top - Theme.GapSmall));
    }

    // ------------------------------------------------------------- общи части

    internal static Label Title(string text) => new()
    {
        Text = text,
        Font = Theme.Display,
        ForeColor = Theme.Ink,
        Width = Theme.Scaled(940),
        Height = Theme.Scaled(44)
    };

    internal static Label Subtitle(string text) => new()
    {
        Text = text,
        Font = Theme.Base,
        ForeColor = Theme.InkMuted,
        Width = Theme.Scaled(940),
        Height = Theme.Scaled(30),
        Margin = new Padding(0, 0, 0, Theme.GapSmall)
    };

    /// <summary>
    /// Рамка със заглавие. Връща картата и мястото за съдържанието — така всяка
    /// страница си подрежда вътрешността, без да преповтаря кутията.
    /// </summary>
    internal static Card Frame(string heading, out Panel body)
    {
        var card = new Card
        {
            Width = Theme.Scaled(940),
            Height = Theme.Scaled(120),
            Margin = new Padding(0, 0, 0, Theme.Gap),
            Padding = new Padding(Theme.Gap, Theme.GapSmall, Theme.Gap, Theme.Gap),
            Fill = Theme.SurfaceAlt,
            Outline = Theme.Border
        };

        body = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };

        card.Controls.Add(body);
        card.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(32),
            Text = heading,
            Font = Theme.Heading,
            ForeColor = Theme.Accent
        });

        return card;
    }

    private static FlowLayoutPanel ButtonBar() => new()
    {
        Dock = DockStyle.Bottom,
        Height = Theme.Scaled(48),
        FlowDirection = FlowDirection.LeftToRight,
        WrapContents = false,
        BackColor = Color.Transparent
    };

    // ------------------------------------------------------------- фирми

    private Control BuildCompanies()
    {
        var card = Frame(Lang.T("Фирми", "Companies"), out var body);
        card.Height = Theme.Scaled(340);

        _companyCount.Dock = DockStyle.Bottom;
        _companyCount.Height = Theme.Scaled(26);
        _companyCount.Font = Theme.Small;
        _companyCount.ForeColor = Theme.InkFaint;

        _addCompany = Theme.SecondaryButton(Lang.T("Добави фирма…", "Add company…"));
        // Без отстъпа по подразбиране: иначе първото копче в реда стои с три пиксела по-ниско от съседите си.
        _addCompany.Margin = Padding.Empty;
        _addCompany.Height = Theme.Scaled(38);
        _addCompany.Click += (_, _) => AddCompany();

        var edit = Theme.SecondaryButton(Lang.T("Поправи", "Edit"));
        edit.Height = Theme.Scaled(38);
        edit.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        edit.Click += (_, _) => EditCompany();

        var remove = Theme.DangerButton(Lang.T("Премахни", "Remove"), Theme.Glyph.Delete);
        remove.Height = Theme.Scaled(38);
        remove.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        remove.Click += (_, _) => RemoveCompany();

        var bar = ButtonBar();
        bar.Controls.Add(_addCompany.FitWidth(24));
        bar.Controls.Add(edit.FitWidth(24));
        bar.Controls.Add(remove.FitWidth(24));

        _companies.Dock = DockStyle.Fill;
        _companies.DoubleClick += (_, _) => EditCompany();

        body.Controls.Add(_companies);
        body.Controls.Add(bar);
        body.Controls.Add(_companyCount);

        return card;
    }

    private void AddCompany()
    {
        var limit = AppServices.Account.CompanyLimit;

        using var dialog = new CompanyDialog(_workspace.Companies, limit);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _workspace.Companies.Add(dialog.EnteredEik, dialog.EnteredName);
        LoadCompanies();
    }

    private void EditCompany()
    {
        if (_companies.Selected is not { } company) return;

        using var dialog = new CompanyDialog(
            _workspace.Companies, AppServices.Account.CompanyLimit, company);

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _workspace.Companies.Update(company.Id, dialog.EnteredEik, dialog.EnteredName);
        LoadCompanies();
    }

    private void RemoveCompany()
    {
        if (_companies.Selected is not { } company) return;

        var used = _workspace.Companies.DocumentCount(company.Id);

        // Отчетите не си отиват с фирмата: подписаният отчет е документ и се
        // пази независимо дали фирмата още се води в програмата.
        var question = used == 0
            ? Lang.T($"Да се премахне ли „{company.Name}“?", $"Remove “{company.Name}”?")
            : Lang.T($"Да се премахне ли „{company.Name}“?\r\n\r\n" +
                     $"{used} отчета сочат към нея. Те остават, но ще бъдат без фирма.",
                     $"Remove “{company.Name}”?\r\n\r\n" +
                     $"{used} reports point to it. They stay, but will be left without a company.");

        var answer = MessageBox.Show(this, question, Lang.T("Премахване на фирма", "Removing a company"),
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes) return;

        _workspace.Companies.Remove(company.Id);
        LoadCompanies();
    }

    private void LoadCompanies()
    {
        var all = _workspace.Companies.List();
        var limit = AppServices.Account.CompanyLimit;

        _companies.Fill(all);

        _companyCount.Text = all.Count == 0
            ? Lang.T($"Няма добавени фирми. Абонаментът позволява {limit}.", $"No companies added. Your subscription allows {limit}.")
            : Lang.T($"{all.Count} от {limit} по абонамент.", $"{all.Count} of {limit} under your subscription.");

        _addCompany.Enabled = all.Count < limit;
        _addCompany.Invalidate();
    }

    // ------------------------------------------------------------- останалото

    /// <summary>
    /// Работният период: между кои дати се пише сега.
    ///
    /// Самоконтрол, а не заключалка. Затвореният месец е заключалка — отчетът е
    /// подаден и не се пипа от никого. Тук човек сам казва „тази седмица
    /// въвеждам септември“ и програмата го държи за думата, но прозорчетата на
    /// склада имат отметка, с която той я оттегля. Правилото е негово.
    ///
    /// Заради какво съществува: грешките се случват в първите дни на новия
    /// месец, докато старият още се дописва. Датата стои на днешната, човек
    /// въвежда вчерашното количество и то отива в новия месец — а сгрешената
    /// дата изглежда точно като дата и излиза чак когато отчетът не излезе.
    ///
    /// Период с две дати, а не месец: един въвежда веднъж месечно, друг — всеки
    /// петък за изминалата седмица. Месецът е най-честият случай и затова има
    /// копче, а не собствен вид.
    /// </summary>
    private Control BuildPeriod()
    {
        var card = Frame(Lang.T("Работен период", "Working period"), out var body);
        card.Height = Theme.Scaled(196);

        _periodOn.Text = Lang.T(
            "Пази ме да не пиша извън периода",
            "Keep me from writing outside the period");

        _periodOn.Dock = DockStyle.Top;
        _periodOn.Height = Theme.Scaled(30);
        _periodOn.CheckedChanged += (_, _) => { AllowPeriod(); SavePeriod(); };
        Theme.StyleInput(_periodOn);

        var line = new Panel { Dock = DockStyle.Top, Height = Theme.Scaled(44), BackColor = Color.Transparent };

        foreach (var (picker, caption) in new[]
                 {
                     (_periodTo, Lang.T("до", "to")),
                     (_periodFrom, Lang.T("от", "from"))
                 })
        {
            picker.Format = DateTimePickerFormat.Short;
            picker.Width = Theme.Scaled(150);
            picker.Dock = DockStyle.Left;
            picker.Margin = new Padding(0, 0, Theme.Gap, 0);
            picker.ValueChanged += (_, _) => SavePeriod();
            Theme.StyleInput(picker);

            var label = new Label
            {
                Dock = DockStyle.Left,
                Width = Theme.Scaled(36),
                Text = caption,
                Font = Theme.Bold,
                ForeColor = Theme.InkMuted,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.Transparent
            };

            // Закачените отляво се редят в обратен ред на добавяне.
            line.Controls.Add(picker);
            line.Controls.Add(label);
        }

        _periodSays.Dock = DockStyle.Top;
        _periodSays.Height = Theme.Scaled(26);
        _periodSays.Font = Theme.Small;
        _periodSays.ForeColor = Theme.InkMuted;
        _periodSays.BackColor = Color.Transparent;

        // Трите най-чести периода. Копче, а не вид: месецът е просто период,
        // който се пада най-често, и не заслужава своя сметка.
        var bar = ButtonBar();

        bar.Controls.Add(Quick(Lang.T("Този месец", "This month"),
            () => WorkPeriod.Of(LedgerPeriod.Of(Today))));

        bar.Controls.Add(Quick(Lang.T("Предходния месец", "Previous month"),
            () => WorkPeriod.Of(LedgerPeriod.Of(Today).Previous)));

        bar.Controls.Add(Quick(Lang.T("Тази седмица", "This week"),
            () => WorkPeriod.Week(Today)));

        // Пълнещият влиза пръв; закаченото се реди наопаки.
        body.Controls.Add(bar);
        body.Controls.Add(_periodSays);
        body.Controls.Add(line);
        body.Controls.Add(_periodOn);

        return card;
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);

    private AppButton Quick(string text, Func<WorkPeriod> period)
    {
        var button = Theme.SecondaryButton(text);
        button.Height = Theme.Scaled(38);
        button.Margin = new Padding(0, 0, Theme.GapSmall, 0);

        button.Click += (_, _) =>
        {
            var chosen = period();

            _periodOn.Checked = true;
            _periodFrom.Value = chosen.From.ToDateTime(TimeOnly.MinValue);
            _periodTo.Value = chosen.To.ToDateTime(TimeOnly.MinValue);

            AllowPeriod();
            SavePeriod();
        };

        return button.FitWidth(24);
    }

    /// <summary>Датите се пипат само когато периодът пази нещо.</summary>
    private void AllowPeriod()
    {
        _periodFrom.Enabled = _periodOn.Checked;
        _periodTo.Enabled = _periodOn.Checked;
    }

    /// <summary>Периодът на ИЗБРАНАТА фирма — двете дружества се водят различно.</summary>
    private void SavePeriod()
    {
        if (_loading) return;
        if (_scope.Current is not { } companyId) return;

        var period = new WorkPeriod(
            DateOnly.FromDateTime(_periodFrom.Value),
            DateOnly.FromDateTime(_periodTo.Value),
            _periodOn.Checked);

        _workspace.Periods.SetWindow(companyId, period);

        ShowPeriod(period);

        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Казва с думи какво значи избраното — числата не се четат сами.</summary>
    private void ShowPeriod(WorkPeriod period)
    {
        if (!period.On)
        {
            _periodSays.Text = Lang.T(
                "Изключено — всяка дата минава. Прозорчетата на склада не питат нищо.",
                "Off — any date goes through. The store windows ask nothing.");

            _periodSays.ForeColor = Theme.InkFaint;
            return;
        }

        if (!period.Valid)
        {
            _periodSays.Text = Lang.T("Краят е преди началото — така периодът не пази нищо.",
                                      "The end is before the start — the period guards nothing.");
            _periodSays.ForeColor = Theme.Warn;
            return;
        }

        var days = period.Days;

        _periodSays.Text = Lang.T(
            $"Пише се в {period.Title} — {days} " + (days == 1 ? "ден" : "дни") +
            ". Извън него прозорчетата питат, преди да запишат.",
            $"Writing into {period.Title} — {days} " + (days == 1 ? "day" : "days") +
            ". Outside it the store windows ask before they write.");

        _periodSays.ForeColor = Theme.Ok;
    }

    private void LoadPeriod()
    {
        var period = _scope.Current is { } companyId
            ? _workspace.Periods.Window(companyId)
            : WorkPeriod.Off;

        var from = period.On ? period.From : LedgerPeriod.Of(Today).First;
        var to = period.On ? period.To : LedgerPeriod.Of(Today).Last;

        _periodOn.Checked = period.On;
        _periodFrom.Value = from.ToDateTime(TimeOnly.MinValue);
        _periodTo.Value = to.ToDateTime(TimeOnly.MinValue);

        AllowPeriod();
        ShowPeriod(period);
    }

    /// <summary>
    /// Кои движения може да се внасят отвън.
    ///
    /// Приемането — винаги; то е вратата на площадката и идва отвън по
    /// устройство. Останалите три се РАЖДАТ тук: от избрани купчини, с посочени
    /// дялове по партиди, с произход, който минава през цялата верига.
    ///
    /// Внесено отвън третиране носи количество и код, но не изчерпва партида,
    /// не наследява произход и не се вижда в пътя ѝ. Оттам нататък двете сметки
    /// — тази на програмата и тази на файла — вървят една до друга и се
    /// разминават тихо, а разликата излиза чак в отчета.
    ///
    /// Затова те се отключват ПООТДЕЛНО: има случаи, в които се налага —
    /// пренасяне на стара година, площадка, водена другаде до вчера — но това е
    /// изключение, а не начин на работа.
    /// </summary>
    private Control BuildImports()
    {
        var card = Frame(Lang.T("Внасяне на движения", "Importing movements"), out var body);
        card.Height = Theme.Scaled(210);

        var line = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(38),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoSize = false,
            BackColor = Color.Transparent
        };

        foreach (var kind in Moves.All)
        {
            var box = new CheckBox
            {
                Text = Moves.Title(kind),
                Width = Theme.Scaled(210),
                Height = Theme.Scaled(30),
                ForeColor = Theme.Ink,
                BackColor = Color.Transparent
            };

            // Приемането стои вдигнато и не се пипа: заключено, програмата няма
            // откъде да научи какво е влязло на площадката.
            if (ImportPolicy.Always(kind))
            {
                box.Checked = true;
                box.Enabled = false;
            }
            else
            {
                box.CheckedChanged += (_, _) => SaveImports();
            }

            Theme.StyleInput(box);

            _unlock[kind] = box;
            line.Controls.Add(box);
        }

        _unlockSays.Dock = DockStyle.Top;
        _unlockSays.Height = Theme.Scaled(46);
        _unlockSays.Font = Theme.Small;
        _unlockSays.ForeColor = Theme.InkMuted;
        _unlockSays.BackColor = Color.Transparent;

        // Пълнещият влиза пръв; закаченото се реди наопаки.
        body.Controls.Add(_unlockSays);
        body.Controls.Add(line);

        return card;
    }

    private void SaveImports()
    {
        if (_loading) return;
        if (_scope.Current is not { } companyId) return;

        var unlocked = _unlock
            .Where(pair => pair.Value.Checked)
            .Select(pair => pair.Key)
            .ToList();

        _workspace.Periods.SetUnlocked(companyId, unlocked);

        ShowUnlocked();

        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Казва с думи какво значи отключеното — отметка сама не обяснява.</summary>
    private void ShowUnlocked()
    {
        var extra = _unlock
            .Where(pair => pair.Value.Checked && !ImportPolicy.Always(pair.Key))
            .Select(pair => Moves.Title(pair.Key))
            .ToList();

        if (extra.Count == 0)
        {
            _unlockSays.Text = Lang.T(
                "Внася се само приемане. Останалите три се раждат в програмата — " +
                "от избрани купчини и с произход по партиди.",
                "Only receipts are imported. The other three are born inside the program — " +
                "from picked piles, with an origin traced through batches.");

            _unlockSays.ForeColor = Theme.InkMuted;
            return;
        }

        _unlockSays.Text = Lang.T(
            $"Отключено е и внасяне на: {string.Join(", ", extra)}. Внесените редове носят " +
            "количество, но не изчерпват партида и не се виждат в пътя ѝ.",
            $"Also unlocked for import: {string.Join(", ", extra)}. Imported rows carry a " +
            "quantity but draw down no batch and never show in its trail.");

        _unlockSays.ForeColor = Theme.Warn;
    }

    private void LoadImports()
    {
        var unlocked = _scope.Current is { } companyId
            ? _workspace.Periods.Unlocked(companyId)
            : ImportPolicy.Default;

        foreach (var (kind, box) in _unlock)
            box.Checked = ImportPolicy.Allows(unlocked, kind);

        ShowUnlocked();
    }

    private Control BuildWork()
    {
        var card = Frame(Lang.T("Работа", "Working"), out var body);
        card.Height = Theme.Scaled(212);

        _confirmDelete.Text = Lang.T("Питай преди изтриване на ред", "Ask before deleting a row");
        _confirmDelete.Dock = DockStyle.Top;
        _confirmDelete.Height = Theme.Scaled(30);
        _confirmDelete.CheckedChanged += (_, _) => Save();
        Theme.StyleInput(_confirmDelete);

        _lockLogDelete.Text = Lang.T(
            "Заключи изтриването на движения от дневника („Отложено“)",
            "Lock deleting movements from the log (“Set aside”)");
        _lockLogDelete.Dock = DockStyle.Top;
        _lockLogDelete.Height = Theme.Scaled(30);
        _lockLogDelete.CheckedChanged += (_, _) => Save();
        Theme.StyleInput(_lockLogDelete);

        // Складът чете отметката при всяко показване: връщане там е достатъчно.
        _viewIcons.Text = Lang.T(
            "Изгледите на склада като икони (без надпис — името излиза под мишката)",
            "Store views as icons (no label — the name shows under the mouse)");
        _viewIcons.Dock = DockStyle.Top;
        _viewIcons.Height = Theme.Scaled(30);
        _viewIcons.CheckedChanged += (_, _) => Save();
        Theme.StyleInput(_viewIcons);

        var shortcuts = Theme.SecondaryButton(Lang.T("Клавишни комбинации…", "Keyboard shortcuts…"));
        shortcuts.Margin = Padding.Empty;
        shortcuts.Height = Theme.Scaled(38);
        shortcuts.Click += (_, _) =>
        {
            using var dialog = new Editor.ShortcutsDialog();
            dialog.ShowDialog(this);
        };

        // Получателите стоят при работата, а не при фирмите: те са на КОГО
        // предава тази фирма, не кой е тя.
        var receivers = Theme.SecondaryButton(Lang.T("Получатели…", "Receivers…"));
        receivers.Height = Theme.Scaled(38);
        receivers.Margin = new Padding(0, 0, Theme.GapSmall, 0);
        receivers.Click += (_, _) => ShowReceivers();

        // Доставчиците — от кого приема. Друг списък, същата работа.
        var suppliers = Theme.SecondaryButton(Lang.T("Доставчици…", "Suppliers…"));
        suppliers.Height = Theme.Scaled(38);
        suppliers.Margin = new Padding(0, 0, Theme.GapSmall, 0);
        suppliers.Click += (_, _) => ShowReceivers(PartnerRole.Originator);

        // Договорите — по какво предава. Трети списък, пак същата работа.
        var contracts = Theme.SecondaryButton(Lang.T("Договори…", "Contracts…"));
        contracts.Height = Theme.Scaled(38);
        contracts.Margin = new Padding(0, 0, Theme.GapSmall, 0);
        contracts.Click += (_, _) => ShowReceivers(PartnerRole.Contract);

        var bar = ButtonBar();
        bar.Controls.Add(receivers.FitWidth(24));
        bar.Controls.Add(suppliers.FitWidth(24));
        bar.Controls.Add(contracts.FitWidth(24));
        bar.Controls.Add(shortcuts.FitWidth(24));

        body.Controls.Add(bar);
        body.Controls.Add(_viewIcons);
        body.Controls.Add(_lockLogDelete);
        body.Controls.Add(_confirmDelete);

        return card;
    }

    private Control BuildAccount()
    {
        var card = Frame(Lang.T("Профил", "Account"), out var body);
        card.Height = Theme.Scaled(150);

        _account.Dock = DockStyle.Top;
        _account.Height = Theme.Scaled(28);
        _account.ForeColor = Theme.InkMuted;

        var signIn = Theme.SecondaryButton(Lang.T("Вход в профила…", "Sign in…"));
        signIn.Margin = Padding.Empty;
        signIn.Height = Theme.Scaled(38);
        signIn.Click += (_, _) => MessageBox.Show(this,
            Lang.T(
                "Услугата още не е пусната.\r\n\r\n" +
                "До нейното пускане програмата работи с временен местен профил и " +
                $"допуска {AppServices.Account.CompanyLimit} фирми.",
                "The service is not live yet.\r\n\r\n" +
                "Until it is, the program runs on a temporary local account and " +
                $"allows {AppServices.Account.CompanyLimit} companies."),
            Lang.T("Профил", "Account"), MessageBoxButtons.OK, MessageBoxIcon.Information);

        var bar = ButtonBar();
        bar.Controls.Add(signIn.FitWidth(24));

        body.Controls.Add(bar);
        body.Controls.Add(_account);

        return card;
    }

    private Control BuildDatabase()
    {
        var card = Frame(Lang.T("Къде се пази работата", "Where your work is kept"), out var body);
        card.Height = Theme.Scaled(196);

        _database.Dock = DockStyle.Top;
        _database.Height = Theme.Scaled(28);
        _database.Font = Theme.Mono;
        _database.ForeColor = Theme.InkFaint;
        _database.AutoEllipsis = true;

        _databaseSays.Dock = DockStyle.Top;
        _databaseSays.Height = 2 * Theme.Small.Height + Theme.Scaled(8);
        _databaseSays.Font = Theme.Small;
        _databaseSays.ForeColor = Theme.InkMuted;
        _databaseSays.BackColor = Color.Transparent;

        var open = Theme.SecondaryButton(Lang.T("Отвори папката", "Open the folder"));
        open.Margin = Padding.Empty;
        open.Height = Theme.Scaled(38);
        open.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            Path.GetDirectoryName(_workspace.Path)!) { UseShellExecute = true });

        var backup = Theme.SecondaryButton(Lang.T("Направи резервно копие…", "Make a backup…"));
        backup.Height = Theme.Scaled(38);
        backup.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        backup.Click += (_, _) => Backup();

        // Внесените файлове стоят до резервното копие, защото са от същия
        // разговор: и двете отговарят на „как се връщам от сбъркано“.
        var imports = Theme.SecondaryButton(Lang.T("Внесени файлове…", "Imported files…"));
        imports.Height = Theme.Scaled(38);
        imports.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        imports.Click += (_, _) => ShowImports();

        // Затварянето на месец е от същия ред неща: решение, което се взима
        // рядко и нарочно, и което после спира всеки запис.
        var periods = Theme.SecondaryButton(Lang.T("Затворени месеци…", "Closed months…"));
        periods.Height = Theme.Scaled(38);
        periods.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        periods.Click += (_, _) => ShowPeriods();

        var bar = ButtonBar();
        bar.Controls.Add(open.FitWidth(24));
        bar.Controls.Add(backup.FitWidth(24));
        bar.Controls.Add(imports.FitWidth(24));
        bar.Controls.Add(periods.FitWidth(24));

        // Закачените отгоре се редят наопаки: пътят влиза последен, за да стои горе.
        body.Controls.Add(bar);
        body.Controls.Add(_databaseSays);
        body.Controls.Add(_database);

        return card;
    }

    /// <summary>
    /// Какво е мястото на базата — с думи, защото пътят сам не казва.
    ///
    /// От 1.3.1 работата стои до програмата и това трябва да се знае: при
    /// обновяване тя остава, а при деинсталиране се пренася в профила.
    /// </summary>
    private string DatabaseText()
    {
        if (!AppServices.IsOwn(_workspace))
            return Lang.T("Отворена е друга база — подадена при пускането („--db“). Работата ви не се пипа.",
                          "Another database is open — given at start (“--db”). Your own work is not touched.");

        if (!DataLocation.BesideProgram)
            return Lang.T("До програмата не може да се пише, затова работата стои в профила на потребителя — както преди 1.3.1.",
                          "The program's folder cannot be written to, so your work stays in your user profile — as before 1.3.1.");

        var moved = AppServices.Adopted == AdoptOutcome.Moved
            ? Lang.T(" Пренесена е от профила при това пускане; старото копие стои там преименувано.",
                     " It was moved from your profile on this start; the old copy stays there, renamed.")
            : string.Empty;

        return Lang.T(
            $"В папка „{DataLocation.FolderName}“ до програмата. При обновяване остава; при деинсталиране се пренася в профила.",
            $"In the “{DataLocation.FolderName}” folder next to the program. It stays when updating; uninstalling moves it to your profile.") + moved;
    }

    // ------------------------------------------------------------- за тестване

    /// <summary>
    /// Изчистване на склада и на внесеното — за този, който тества.
    ///
    /// Тестващият внася един и същ дневник отново и отново и всеки път трябва да
    /// започне от празен склад. Дотук това значеше да се махне всяко внасяне
    /// поотделно и после всяко ръчно движение — или да се изтрие базата и да се
    /// заведат фирмите наново. Резервното копие се прави само, преди всичко.
    /// </summary>
    private Control BuildTesting()
    {
        var card = Frame(Lang.T("За тестване", "For testing"), out var body);
        card.Height = Theme.Scaled(178);

        _testingSays.Dock = DockStyle.Top;
        _testingSays.Height = 3 * Theme.Small.Height + Theme.Scaled(8);
        _testingSays.Font = Theme.Small;
        _testingSays.ForeColor = Theme.InkMuted;
        _testingSays.BackColor = Color.Transparent;
        _testingSays.Text = TestingText();

        var clear = Theme.DangerButton(Lang.T("Изчисти внасянията и склада…", "Clear imports and stock…"));
        clear.Margin = Padding.Empty;
        clear.Height = Theme.Scaled(38);
        clear.Click += (_, _) => ClearStock();

        var bar = ButtonBar();
        bar.Controls.Add(clear.FitWidth(24));

        body.Controls.Add(bar);
        body.Controls.Add(_testingSays);

        return card;
    }

    private static string TestingText() => Lang.T(
        "Изтрива за всички фирми движенията, партидите, началните количества, затворените месеци, дневника и " +
        "списъка с внесените файлове. Фирмите, отчетите, списъците и настройките остават. Преди това се прави " +
        "резервно копие до базата.",
        "Deletes, for every company, the movements, batches, opening quantities, closed months, the log and the " +
        "list of imported files. Companies, reports, lists and settings stay. A backup is made next to the " +
        "database first.");

    private void ClearStock()
    {
        var counts = _workspace.CountStock();

        if (counts.IsEmpty)
        {
            MessageBox.Show(this,
                Lang.T("Складът вече е празен — няма движения, партиди и внесени файлове.",
                       "The store is already empty — no movements, batches or imported files."),
                Lang.T("Няма какво да се изчисти", "Nothing to clear"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);

            return;
        }

        var nl = Environment.NewLine;

        var question = Lang.T(
            $"Ще се изтрият за ВСИЧКИ фирми:{nl}{nl}" +
            $"·  {counts.Moves} движения и {counts.Openings} начални количества{nl}" +
            $"·  {counts.Batches} партиди{nl}" +
            $"·  {counts.Imports} внесени файла (следата им — отчетите остават){nl}" +
            $"·  {counts.Logged} реда от дневника{nl}" +
            $"·  {counts.Closed} затворени месеца и началото на воденето{nl}{nl}" +
            $"Остават фирмите, отчетите, списъците и настройките. Преди изчистването се прави резервно копие.{nl}{nl}" +
            "Да се изчисти ли?",
            $"This deletes, for EVERY company:{nl}{nl}" +
            $"·  {counts.Moves} movements and {counts.Openings} opening quantities{nl}" +
            $"·  {counts.Batches} batches{nl}" +
            $"·  {counts.Imports} imported files (their trail — the reports stay){nl}" +
            $"·  {counts.Logged} log rows{nl}" +
            $"·  {counts.Closed} closed months and the start of the store{nl}{nl}" +
            $"Companies, reports, lists and settings stay. A backup is made before clearing.{nl}{nl}" +
            "Clear?");

        // „Не“ е отговорът по подразбиране: Enter не бива да изтрива склада.
        if (MessageBox.Show(this, question, Lang.T("Изчистване на склада", "Clearing the store"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        string backup;

        try
        {
            UseWaitCursor = true;
            backup = ClearWithBackup();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Lang.T("Складът не беше изчистен", "The store was not cleared"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);

            return;
        }
        finally
        {
            UseWaitCursor = false;
        }

        DataChanged?.Invoke(this, EventArgs.Empty);

        _testingSays.Text = Lang.T($"Изчистено. Резервното копие е: {backup}", $"Cleared. The backup is: {backup}");
        _testingSays.ForeColor = Theme.Ok;

        MessageBox.Show(this,
            Lang.T($"Складът и внесеното са изчистени.{nl}{nl}Резервното копие е:{nl}{backup}{nl}{nl}" +
                   "За да се върне: затворете програмата и сложете копието на мястото на workspace.db.",
                   $"The store and the imports are cleared.{nl}{nl}The backup is:{nl}{backup}{nl}{nl}" +
                   "To bring it back: close the program and put the copy in place of workspace.db."),
            Lang.T("Готово", "Done"), MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// Резервно копие и изчистване — без питане. Копието е в „Backups“ до базата;
    /// без него изчистването не започва.
    /// </summary>
    internal string ClearWithBackup()
    {
        var folder = Path.Combine(Path.GetDirectoryName(_workspace.Path)!, "Backups");
        Directory.CreateDirectory(folder);

        var backup = Path.Combine(folder, $"before-clear-{DateTime.Now:yyyy-MM-dd-HHmmss}.db");

        _workspace.BackupTo(backup);
        _workspace.ClearStock();

        return backup;
    }

    // ------------------------------------------------------------- обновяване

    /// <summary>
    /// Кое издание е пуснато, има ли по-ново и копчетата за двете.
    ///
    /// Проверката тук и известието на началния екран са едно и също: копчето
    /// пита, отговорът излиза и на двете места. Самото слагане е винаги с
    /// копче — програмата не се сменя сама.
    /// </summary>
    private Control BuildUpdates()
    {
        var card = Frame(Lang.T("Обновяване", "Updates"), out var body);
        card.Height = Theme.Scaled(250);

        _version.Dock = DockStyle.Top;
        _version.Height = Theme.Scaled(28);
        _version.Font = Theme.Bold;
        _version.ForeColor = Theme.Ink;
        _version.BackColor = Color.Transparent;

        _updateSays.Dock = DockStyle.Top;
        _updateSays.Height = Theme.Scaled(44);
        _updateSays.Font = Theme.Small;
        _updateSays.ForeColor = Theme.InkMuted;
        _updateSays.BackColor = Color.Transparent;

        _autoCheck.Text = Lang.T(
            "Проверявай за ново издание и новини при пускане",
            "Check for a new version and news on start");
        _autoCheck.Dock = DockStyle.Top;
        _autoCheck.Height = Theme.Scaled(30);
        _autoCheck.CheckedChanged += (_, _) => SaveUpdates();
        Theme.StyleInput(_autoCheck);

        _prerelease.Text = Lang.T(
            "Предлагай и пробните издания — за този, който тества",
            "Offer test versions too — for whoever is testing");
        _prerelease.Dock = DockStyle.Top;
        _prerelease.Height = Theme.Scaled(30);
        _prerelease.CheckedChanged += (_, _) => SaveUpdates();
        Theme.StyleInput(_prerelease);

        _checkNow = Theme.SecondaryButton(Lang.T("Провери сега", "Check now"));
        _checkNow.Margin = Padding.Empty;
        _checkNow.Glyph = Theme.Glyph.Sync;
        _checkNow.Height = Theme.Scaled(38);
        _checkNow.Click += (_, _) => CheckRequested?.Invoke(this, EventArgs.Empty);

        _updateNow = Theme.SecondaryButton(string.Empty);
        _updateNow.Height = Theme.Scaled(38);
        _updateNow.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        _updateNow.Click += (_, _) => UpdateNow();

        var releases = Theme.SecondaryButton(Lang.T("Всички издания…", "All versions…"));
        releases.Glyph = Theme.Glyph.External;
        releases.Height = Theme.Scaled(38);
        releases.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        releases.Click += (_, _) => Browser.Open(UpdateFeed.ReleasesPage, FindForm());

        var bar = ButtonBar();
        bar.Controls.Add(_checkNow.FitWidth(24));
        bar.Controls.Add(_updateNow);
        bar.Controls.Add(releases.FitWidth(24));

        body.Controls.Add(bar);
        body.Controls.Add(_prerelease);
        body.Controls.Add(_autoCheck);
        body.Controls.Add(_updateSays);
        body.Controls.Add(_version);

        return card;
    }

    /// <summary>Едното копче за четирите положения: свали, изтегли, рестартирай, опитай пак.</summary>
    private void UpdateNow()
    {
        var state = _updates.State;

        switch (state.Stage)
        {
            case UpdateStage.Ready:
                RestartRequested?.Invoke(this, EventArgs.Empty);
                break;

            case UpdateStage.Available or UpdateStage.Failed when _updates.CanApply:
                _ = _updates.DownloadAsync();
                break;

            case UpdateStage.Available when state.Release is { } release:
                Browser.Open(release.Page, FindForm());
                break;
        }
    }

    private void SaveUpdates()
    {
        if (_loading) return;

        _workspace.Settings.Set(SettingsStore.Keys.UpdateAuto, _autoCheck.Checked);
        _workspace.Settings.Set(SettingsStore.Keys.UpdatePrerelease, _prerelease.Checked);

        _updates.UsePrerelease(_prerelease.Checked);

        ShowUpdates();
    }

    /// <summary>Казва с думи докъде е стигнало обновяването — и нагажда копчетата по това.</summary>
    public void ShowUpdates()
    {
        var state = _updates.State;
        var version = state.Version;

        _version.Text = $"RIOSV Imports {_updates.CurrentVersion} — " + _updates.Kind switch
        {
            InstallKind.Installed => Lang.T("инсталирана в профила", "installed in your profile"),
            InstallKind.Portable => Lang.T("преносима", "portable"),
            _ => Lang.T("пусната от папка; сама се обновява само инсталираната",
                        "run from a folder; only the installed copy updates itself")
        };

        var checkedAt = state.CheckedAt ?? LastChecked();
        var when = checkedAt is { } at ? Lang.T($" Проверено: {at:dd.MM.yyyy, HH:mm}.", $" Checked: {at:dd.MM.yyyy, HH:mm}.") : string.Empty;

        (_updateSays.Text, _updateSays.ForeColor) = state.Stage switch
        {
            UpdateStage.Checking => (Lang.T("Проверява се в GitHub…", "Checking GitHub…"), Theme.InkMuted),

            UpdateStage.Current => (Lang.T("Това е последното издание.", "This is the latest version.") + when, Theme.Ok),

            UpdateStage.Available when _updates.CanApply => (Lang.T(
                $"Налично е издание {version}. Сваля се само разликата, а работата продължава, докато трае.",
                $"Version {version} is available. Only the difference is downloaded, and work carries on meanwhile."), Theme.Accent),

            UpdateStage.Available => (Lang.T(
                $"Има издание {version}. Тази програма не се обновява сама — инсталаторът е в страницата на изданието.",
                $"Version {version} is out. This copy does not update itself — the installer is on the release page."), Theme.Accent),

            UpdateStage.Downloading => (Lang.T(
                $"Сваля се издание {version} — {state.Progress} %.",
                $"Downloading version {version} — {state.Progress} %."), Theme.Accent),

            UpdateStage.Ready => (Lang.T(
                $"Издание {version} е свалено. „Рестартирай и обнови“ затваря програмата и я отваря наново.",
                $"Version {version} is downloaded. “Restart and update” closes the program and opens it again."), Theme.Ok),

            UpdateStage.Failed => (state.Problem ?? Lang.T("Проверката не стана.", "The check did not work."), Theme.Warn),

            _ => (Lang.T("В това пускане още не е проверявано.", "Not checked yet in this session.") + when, Theme.InkMuted)
        };

        _checkNow.Enabled = !_updates.Busy;
        _checkNow.Text = state.Stage == UpdateStage.Checking ? Lang.T("Проверява се…", "Checking…") : Lang.T("Провери сега", "Check now");
        _checkNow.FitWidth(24);

        var (text, glyph, enabled) = state.Stage switch
        {
            UpdateStage.Ready => (Lang.T("Рестартирай и обнови", "Restart and update"), Theme.Glyph.Upgrade, true),
            UpdateStage.Downloading => (Lang.T($"Сваля се… {state.Progress} %", $"Downloading… {state.Progress} %"), Theme.Glyph.Download, false),
            UpdateStage.Available when _updates.CanApply => (Lang.T($"Обнови до {version}", $"Update to {version}"), Theme.Glyph.Download, true),
            UpdateStage.Available => (Lang.T($"Изтегли {version}…", $"Download {version}…"), Theme.Glyph.External, true),
            UpdateStage.Failed when _updates.CanApply && state.Release is not null => (Lang.T("Опитай пак", "Try again"), Theme.Glyph.Refresh, true),
            _ => (string.Empty, null, false)
        };

        _updateNowShown = text.Length > 0;
        _updateNow.Visible = _updateNowShown;
        _updateNow.Text = text;
        _updateNow.Glyph = glyph;
        _updateNow.Enabled = enabled;
        if (text.Length > 0) _updateNow.FitWidth(24);

        _updateNow.Invalidate();
        _checkNow.Invalidate();
    }

    private DateTime? LastChecked() =>
        DateTime.TryParse(_workspace.Settings.Get(SettingsStore.Keys.UpdateChecked),
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var at)
            ? at
            : null;

    // ------------------------------------------------------------- поддръжка

    /// <summary>
    /// Какво се праща, когато нещо се обърка: дневникът на грешките и кратко
    /// описание на машината. Без нито ред от работата — ЕИК-ове, отчети и
    /// количества не напускат базата.
    /// </summary>
    private Control BuildSupport()
    {
        var card = Frame(Lang.T("Поддръжка", "Support"), out var body);
        card.Height = Theme.Scaled(160);

        _supportSays.Dock = DockStyle.Top;
        _supportSays.Height = Theme.Scaled(46);
        _supportSays.Font = Theme.Small;
        _supportSays.ForeColor = Theme.InkMuted;
        _supportSays.BackColor = Color.Transparent;
        _supportSays.Text = SupportText();

        var log = Theme.SecondaryButton(Lang.T("Дневник на грешките", "Error log"));
        log.Margin = Padding.Empty;
        log.Glyph = Theme.Glyph.Documents;
        log.Height = Theme.Scaled(38);
        log.Click += (_, _) => OpenErrorLog();

        var copy = Theme.SecondaryButton(Lang.T("Копирай данните за поддръжка", "Copy support details"));
        copy.Glyph = Theme.Glyph.Copy;
        copy.Height = Theme.Scaled(38);
        copy.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        copy.Click += (_, _) => CopyDiagnostics();

        var bar = ButtonBar();
        bar.Controls.Add(log.FitWidth(24));
        bar.Controls.Add(copy.FitWidth(24));

        body.Controls.Add(bar);
        body.Controls.Add(_supportSays);

        return card;
    }

    private static string SupportText() => Lang.T(
        "Неочакваните грешки се записват в дневник. „Копирай данните“ слага в клипборда изданието, " +
        "Windows и мястото на базата — без нищо от работата ви.",
        "Unexpected errors are written to a log. “Copy details” puts the version, Windows and the " +
        "database location on the clipboard — nothing from your work.");

    private void OpenErrorLog()
    {
        var path = Program.CrashLogPath;

        if (File.Exists(path))
        {
            Browser.Reveal(path);
            return;
        }

        MessageBox.Show(this,
            Lang.T("Няма записани грешки.\r\n\r\nДневникът ще бъде тук, ако някога се появи:\r\n",
                   "No errors have been written.\r\n\r\nThe log will be here if one ever appears:\r\n") + path,
            Lang.T("Дневник на грешките", "Error log"), MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void CopyDiagnostics()
    {
        try
        {
            // Със сто милисекунди между опитите: клипбордът е зает, докато друга
            // програма го държи — виж капаните в АРХИТЕКТУРА.md.
            Clipboard.SetDataObject(Diagnostics(_workspace, _updates), copy: true, retryTimes: 5, retryDelay: 100);

            _supportSays.Text = Lang.T("Копирано в клипборда. Поставете го в писмото до поддръжката.",
                                       "Copied to the clipboard. Paste it into your message to support.");
            _supportSays.ForeColor = Theme.Ok;
        }
        catch (Exception ex)
        {
            _supportSays.Text = Lang.T("Клипбордът е зает от друга програма: ", "Another program holds the clipboard: ") + ex.Message;
            _supportSays.ForeColor = Theme.Warn;
        }
    }

    /// <summary>
    /// Данните за поддръжка. Профилът на потребителя се заменя с
    /// %LOCALAPPDATA% — името му в Windows не е нужно на никого.
    /// </summary>
    internal static string Diagnostics(Workspace workspace, UpdateService updates)
    {
        static string Hide(string path) => HideProfile(path);

        static string Size(string path)
        {
            try
            {
                return File.Exists(path) ? $"{new FileInfo(path).Length / 1024.0 / 1024.0:0.0} MB" : "—";
            }
            catch
            {
                return "—";
            }
        }

        var state = updates.State;
        var log = Program.CrashLogPath;

        return string.Join(Environment.NewLine,
            $"RIOSV Imports {updates.CurrentVersion} ({AppVersion.BuildFull}), {updates.Kind}",
            $"Windows {Environment.OSVersion.Version}, {(Environment.Is64BitProcess ? "x64" : "x86")}, .NET {Environment.Version}",
            $"UI: {Lang.Key}, zoom {Theme.Zoom * 100:0} %",
            $"DB: {Hide(workspace.Path)} ({Size(workspace.Path)})",
            $"Update: {state.Stage}{(state.Version is { } v ? " " + v : string.Empty)}" +
            (state.Problem is { } problem ? $" — {problem}" : string.Empty),
            $"Log: {Hide(log)} ({(File.Exists(log) ? File.GetLastWriteTime(log).ToString("dd.MM.yyyy HH:mm") : "—")})");
    }

    /// <summary>
    /// Пътят без името на потребителя в Windows — %LOCALAPPDATA% или
    /// %USERPROFILE% вместо профила.
    ///
    /// От 1.3.1 базата стои до програмата, а преносимото копие често е на
    /// работния плот или в „Изтегляния“ — тоест в профила, но не в
    /// %LOCALAPPDATA%. Името на човека не бива да тръгне с данните за поддръжка
    /// и оттам.
    /// </summary>
    internal static string HideProfile(string path)
    {
        foreach (var (folder, name) in new[]
                 {
                     (Environment.SpecialFolder.LocalApplicationData, "%LOCALAPPDATA%"),
                     (Environment.SpecialFolder.UserProfile, "%USERPROFILE%")
                 })
        {
            var root = Environment.GetFolderPath(folder);

            if (root.Length > 0 && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return name + path[root.Length..];
        }

        return path;
    }

    /// <summary>
    /// Показва ли се копчето за обновяване. Пази се отделно: Visible е „не“ за
    /// всичко в непоказан прозорец, а тестът пита за нагласеното.
    /// </summary>
    private bool _updateNowShown;

    /// <summary>Надписът на копчето за обновяване, ако се вижда — ползва се от тестовете.</summary>
    internal string UpdateButtonText => _updateNowShown ? _updateNow.Text : string.Empty;

    /// <summary>Какво казва рамката за обновяването — ползва се от тестовете.</summary>
    internal string UpdateSays => _updateSays.Text;

    /// <summary>Натиска копчето за обновяване — ползва се от тестовете.</summary>
    internal void ClickUpdate() => UpdateNow();

    // ------------------------------------------------------------- данни

    private new void Load()
    {
        _loading = true;

        try
        {
            _confirmDelete.Checked = _workspace.Settings.GetBool(SettingsStore.Keys.ConfirmOnDelete, true);
            _lockLogDelete.Checked = _workspace.Settings.GetBool(SettingsStore.Keys.LogDeleteLocked, false);
            _viewIcons.Checked = _workspace.Settings.GetBool(SettingsStore.Keys.StoreViewIcons, true);
            _account.Text = AppServices.Account.Describe();
            _database.Text = _workspace.Path;
            _databaseSays.Text = DatabaseText();
            _autoCheck.Checked = _workspace.Settings.GetBool(SettingsStore.Keys.UpdateAuto, true);
            _prerelease.Checked = _workspace.Settings.GetBool(SettingsStore.Keys.UpdatePrerelease, false);

            LoadPeriod();
            LoadImports();
        }
        finally
        {
            _loading = false;
        }

        LoadCompanies();
        ShowUpdates();
    }

    private void Save()
    {
        if (_loading) return;

        _workspace.Settings.Set(SettingsStore.Keys.ConfirmOnDelete, _confirmDelete.Checked);
        _workspace.Settings.Set(SettingsStore.Keys.LogDeleteLocked, _lockLogDelete.Checked);
        _workspace.Settings.Set(SettingsStore.Keys.StoreViewIcons, _viewIcons.Checked);
    }

    /// <summary>Вдига или сваля отметката за иконите, както щракване по нея.</summary>
    internal void UseViewIcons(bool on) => _viewIcons.Checked = on;

    /// <summary>
    /// Кой получава от тази фирма — или от кого приема тя.
    ///
    /// Списъците се пълнят сами, от записаните движения; оттук се впи́сва
    /// предварително и се поправя сгрешен ЕИК. Иска фирма — контрагентите са
    /// нейни, а не общи.
    /// </summary>
    private void ShowReceivers(string role = PartnerRole.Receiver)
    {
        if (_scope.Current is not { } companyId)
        {
            MessageBox.Show(this,
                Lang.T("Изберете фирма — тези списъци се водят по фирма.",
                       "Pick a company — these lists are kept per company."),
                Lang.T("Няма избрана фирма", "No company selected"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);

            return;
        }

        using var dialog = new ReceiversDialog(_workspace, companyId, role);
        dialog.ShowDialog(this);

        if (dialog.Changed) DataChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Дневникът на внесените файлове.
    ///
    /// Оттук се маха цяло внасяне — същият файл, внесен два пъти, или правилният
    /// файл, внесен в чужда фирма. Махнатото пипа склада и отчетите, затова
    /// страниците се обновяват след него.
    /// </summary>
    private void ShowImports()
    {
        using var dialog = new ImportsDialog(_workspace);
        dialog.ShowDialog(this);

        if (dialog.Changed) DataChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Затварянето и отварянето на месеци.
    ///
    /// Тук, а не в „Месец“: решение веднъж на месец няма работа до копчетата,
    /// които се натискат по цял ден.
    /// </summary>
    private void ShowPeriods()
    {
        using var dialog = new PeriodsDialog(_workspace, _scope);
        dialog.ShowDialog(this);

        if (dialog.Changed) DataChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Копие на целия файл. При WAL живите записи стоят и встрани от него,
    /// затова копието се прави със средствата на SQLite, а не с File.Copy.
    /// </summary>
    private void Backup()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = Lang.T("Резервно копие (*.db)|*.db", "Backup (*.db)|*.db"),
            FileName = $"RIOSV Imports — {DateTime.Now:yyyy-MM-dd}.db"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            _workspace.BackupTo(dialog.FileName);

            MessageBox.Show(this,
                Lang.T("Резервното копие е готово:\r\n\r\n", "The backup is ready:\r\n\r\n") + dialog.FileName,
                Lang.T("Готово", "Done"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Lang.T("Копието не беше направено", "The backup was not made"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
