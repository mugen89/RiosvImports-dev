using System.Diagnostics.CodeAnalysis;
using RiosvImports.App.Controls;
using RiosvImports.App.Updates;
using RiosvImports.Core;
using RiosvImports.Core.Localization;
using RiosvImports.Data;

namespace RiosvImports.App.Shell;

/// <summary>
/// Главният прозорец: лента отляво и една страница отдясно.
///
/// Лента вместо раздели, защото разделите на Windows Forms изглеждат на
/// възрастта си, а тук всеки ред има място за икона, име и брояч. Страниците
/// се правят веднъж и се крият — превключването между тях трябва да е
/// мигновено.
/// </summary>
internal sealed class ShellForm : Form
{
    private readonly Panel _host = new() { Dock = DockStyle.Fill, BackColor = Theme.Canvas };
    private readonly Panel _rail = new();
    private readonly List<(NavButton Button, Control Page)> _pages = [];

    /// <summary>
    /// Бутоните на лентата, в реда на добавяне. Правят се веднъж и преживяват
    /// пресъздаването на страниците — виж <see cref="Add"/>.
    /// </summary>
    private readonly List<NavButton> _nav = [];

    /// <summary>Редът под името на програмата. Сменя се с езика.</summary>
    private readonly Label _brandLine = new();

    /// <summary>Името на програмата — крие се, когато лентата е свита.</summary>
    private readonly Label _brandName = new();

    /// <summary>Фирмата, по която се работи — една за всички екрани.</summary>
    private readonly CompanyScope _scope;

    /// <summary>Изборът ѝ в лентата, с надписа над него. Крие се при свиване.</summary>
    private readonly Panel _scopeBox = new();
    private readonly Label _scopeCaption = new();
    private readonly ComboBox _scopePicker = new();

    /// <summary>Номерата на фирмите по реда на списъка.</summary>
    private readonly List<long> _scopeIds = [];

    /// <summary>Пълни се списъкът — избраното тогава не е избор на човека.</summary>
    private bool _filling;

    /// <summary>
    /// Показва ли се изборът на фирма.
    ///
    /// Пази се отделно от самата контрола: <c>Control.Visible</c> отговаря и за
    /// родителите си и е „не“, докато прозорецът още не е показан — а тестът
    /// пита за нагласеното, не за нарисуваното.
    /// </summary>
    private bool _scopeShown = true;

    /// <summary>Копчето, което свива лентата. Не е NavButton нарочно — виж BuildRail.</summary>
    private readonly Button _railToggle = new();

    /// <summary>Подсказките на бутоните в свитата лента: там надпис няма.</summary>
    private readonly ToolTip _railTips = new();

    /// <summary>
    /// Ширината на лентата отворена и свита — мерена при 100 %.
    ///
    /// Минава през мащаба при всяко ползване: при 150 % надписите в нея са
    /// един път и половина по-широки и 226 пиксела побираха само „RIOSV“.
    /// </summary>
    private static int RailWide => Theme.Scaled(226);

    private static int RailNarrow => Theme.Scaled(64);

    private readonly Workspace _workspace;
    private readonly ImportService _service;

    /// <summary>
    /// Изданието най-долу в лентата — или, когато има по-ново, покана към него.
    /// Видимо от всеки екран: известието на началния екран се вижда само там.
    /// </summary>
    private readonly Label _railVersion = new();

    /// <summary>
    /// Обновяването — едно за цялата програма. Началният екран и настройките
    /// го показват; главният прозорец го пуска и затваря програмата за него.
    /// </summary>
    internal UpdateService Updates { get; }

    /// <summary>
    /// Програмата се затваря, за да се обнови. Program.Main пуска инсталатора,
    /// след като базата е затворена.
    /// </summary>
    internal bool RestartForUpdate { get; private set; }

    private HomePage _home;
    private LedgerPage _ledger;
    private StockPage _stock;
    private WorkPage _work;
    private BalancePage _balance;
    private ListsPage _lists;
    private AppearancePage _appearance;
    private SettingsPage _settings;

    public ShellForm(ImportService service, Workspace workspace, string? startupFile = null, UpdateService? updates = null)
    {
        _workspace = workspace;
        _service = service;
        _scope = new CompanyScope(workspace);

        // Преди страниците: и началният екран, и настройките го показват.
        Updates = updates ?? new UpdateService(
            prerelease => UpdateBackends.Create(prerelease, AppServices.Web),
            workspace.Settings.GetBool(SettingsStore.Keys.UpdatePrerelease, false));

        Updates.Changed += OnUpdateChanged;

        // Отворена е чужда база — най-често пробната, дошла с изданието за
        // преглед. Пише го на заглавната лента: числата в програмата тогава не
        // са ничия истинска работа и това не бива да се разбира по-късно.
        //
        // Изданието стои там също: „кое издание ползвате“ е първият въпрос на
        // всяка поддръжка, а заглавната лента се вижда и на снимка на екрана.
        var title = $"RIOSV Imports {Updates.CurrentVersion}";

        Text = AppServices.IsOwn(workspace)
            ? title
            : title + Lang.T(" — пробна база", " — test database");
        Font = Theme.Base;
        BackColor = Theme.Canvas;
        // Най-малкото, на което програмата още се ползва — но никога повече от
        // екрана: на лаптоп 1366×768 работната площ е 728 пиксела висока и
        // по-голям минимум би изтласкал долния край на прозореца под лентата.
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        MinimumSize = new Size(Math.Min(1120, screen.Width), Math.Min(720, screen.Height));
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;

        BuildRail();

        Controls.Add(_host);
        Controls.Add(_rail);

        BuildPages();

        Show(_home);
        UpdateBadges();

        // След страниците: свиването пипа бутоните, а те се правят в Add.
        RailCollapsed = workspace.Settings.GetBool(SettingsStore.Keys.RailCollapsed, false);

        // Каквото не е оцветено нарочно, получава цвят от темата.
        Theme.Apply(this);

        Theme.Changed += OnThemeChanged;
        Lang.Changed += OnThemeChanged;

        if (startupFile is not null) _work.OpenFromFile(startupFile);
    }

    /// <summary>
    /// Сменена тема означава нови екрани.
    ///
    /// Нарисуваните на ръка контроли се обновяват сами, но на десетки надписи
    /// и панели цветът е зададен веднъж при сглобяване. Да се обхожда дървото
    /// и да се гадае коя роля има всеки надпис е по-крехко, отколкото просто
    /// да се сглоби наново — страниците са евтини, а работата стои в базата.
    ///
    /// Отлага се, защото събитието идва от бутон, който сам е част от екрана,
    /// който предстои да бъде премахнат.
    /// </summary>
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;

        BeginInvoke(RebuildPages);
    }

    private void RebuildPages()
    {
        if (IsDisposed) return;

        var activeIndex = _pages.FindIndex(entry => entry.Page.Visible);

        SuspendLayout();

        foreach (var (_, page) in _pages)
        {
            _host.Controls.Remove(page);
            page.Dispose();
        }

        _pages.Clear();

        BuildPages();

        BackColor = Theme.Canvas;
        _host.BackColor = Theme.Canvas;
        _rail.BackColor = Theme.Surface;
        _brandLine.Text = Lang.T("Отчети към НИСО", "NWIS reports");

        // Свиването се прилага наново: то носи надписите на подсказките, а те
        // са на езика, който е бил в сила при последното превключване.
        ApplyRail(RailCollapsed);

        foreach (Control child in _rail.Controls) Repaint(child);

        // След пребоядисването: то не знае, че поканата за ново издание е в
        // основния цвят, а не в бледия.
        ShowRailVersion();

        Show(_pages[Math.Max(0, activeIndex)].Page);

        Theme.Apply(this);

        ResumeLayout(performLayout: true);
    }

    /// <summary>Пребоядисва лентата отляво — надписите там не се пресъздават.</summary>
    private static void Repaint(Control control)
    {
        control.BackColor = Theme.Surface;

        if (control is Label label)
            label.ForeColor = label.Font == Theme.Small ? Theme.InkFaint : Theme.Accent;

        foreach (Control child in control.Controls) Repaint(child);

        control.Invalidate(invalidateChildren: true);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Theme.Changed -= OnThemeChanged;
        Lang.Changed -= OnThemeChanged;
        Updates.Changed -= OnUpdateChanged;
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        // И при прозорец, който никога не е показван — тестовете сглобяват
        // такива стотици пъти, а службата за обновяване ги надживява.
        if (disposing) Updates.Changed -= OnUpdateChanged;
        base.Dispose(disposing);
    }

    /// <summary>Сглобява страниците и ги закача за лентата.</summary>
    [MemberNotNull(nameof(_home), nameof(_ledger), nameof(_stock),
        nameof(_work), nameof(_balance), nameof(_lists), nameof(_appearance), nameof(_settings))]
    private void BuildPages()
    {
        _home = new HomePage(_workspace, Updates);
        _ledger = new LedgerPage(_service, _workspace, _scope);
        _stock = new StockPage(_service, _workspace, _scope);
        _work = new WorkPage(_service, _workspace, _scope);
        _balance = new BalancePage(_service, _workspace, _scope);
        _lists = new ListsPage(_service, _workspace, _scope);
        _appearance = new AppearancePage(_workspace);
        _settings = new SettingsPage(_workspace, _scope, Updates);

        _home.GoToWork += (_, _) => Show(_work);
        _home.NewReport += (_, _) => { Show(_work); _work.StartNewReport(); };

        // Отчетът от „Продължете оттам…“ се отваря направо. „Отчети“ остава
        // отдолу: затвореният редактор връща там, където човек би го търсил.
        _home.OpenReport += (_, id) => { Show(_work); _work.OpenDocument(id); };

        _home.CheckRequested += (_, _) => CheckOnline();
        _home.RestartRequested += (_, _) => RestartToUpdate();
        _home.NoticesChanged += (_, _) => UpdateBadges();
        _settings.CheckRequested += (_, _) => CheckOnline();
        _settings.RestartRequested += (_, _) => RestartToUpdate();

        // Пресмята се само страницата пред очите. Скритите се пресмятат така или
        // иначе при показване (Show) — а пресметнати и сега, те бяха по една
        // секунда чакане след всяко „Записване“ в склада при истински месец.
        _work.DocumentsChanged += (_, _) => { Touch(_home, _balance); UpdateBadges(); };

        // Съставеният от склада отчет е нов отчет: списъкът и таблото трябва
        // да го видят, без да се чака превключване на страници.
        _ledger.DocumentsChanged += (_, _) => { Touch(_work, _home); UpdateBadges(); };

        // Третирането от склада е движение като всяко друго: „Месец“ трябва да
        // го види веднага, а не при следващото превключване.
        _stock.LedgerChanged += (_, _) => Touch(_ledger);

        // Проверката на месеца води до склада, в който се образува — към края
        // на месеца, в който чака.
        _ledger.StoreRequested += (_, request) =>
        {
            Show(_stock);
            _stock.Open(request.Store, request.On);
        };

        // Махнатото внасяне отнася наведнъж движения, партиди и отчети — тоест
        // почти всичко, което другите страници показват. Затова се обновяват
        // всички, вместо да се гадае коя точно е пипната.
        _settings.DataChanged += (_, _) =>
        {
            // В „Настройки“ се завеждат и премахват фирми: списъкът в лентата
            // трябва да го научи, преди страниците да се обновят по него.
            _scope.Reload();
            FillScope();

            Touch(_work, _ledger, _stock, _balance, _home);
            UpdateBadges();
        };

        Add(Lang.T("Начало", "Home"), Theme.Glyph.Home, _home);

        // Складът е екранът, на който се работи всеки ден: отчетът вече се
        // съставя от него, а не обратното. Затова стои преди „Отчети“.
        Add(Lang.T("Месец", "Month"), Theme.Glyph.Panel, _ledger);

        // Складът стои до месеца: едното е какво се е случило, другото — какво
        // е останало от него, и двете се гледат в един и същ дъх.
        Add(Lang.T("Склад", "Store"), Theme.Glyph.Open, _stock);

        Add(Lang.T("Отчети", "Reports"), Theme.Glyph.Documents, _work);
        Add(Lang.T("Баланс", "Balance"), Theme.Glyph.Balance, _balance);

        // Между работата и настройването: списъците се пипат рядко, но когато
        // се пипат, е заради отчет, който тъкмо се попълва.
        Add(Lang.T("Списъци", "Lists"), Theme.Glyph.Filter, _lists);

        Add(Lang.T("Изглед", "Appearance"), Theme.Glyph.Palette, _appearance);
        Add(Lang.T("Настройки", "Settings"), Theme.Glyph.Settings, _settings);
    }

    private void BuildRail()
    {
        _rail.Dock = DockStyle.Left;
        _rail.Width = RailWide;
        _rail.BackColor = Theme.Surface;
        _rail.Padding = new Padding(0, 0, 0, Theme.Gap);

        // Кантът отдясно е единственото, което дели лентата от съдържанието.
        _rail.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, _rail.Width - 1, 0, _rail.Width - 1, _rail.Height);
        };

        // Височините следват шрифтовете: при 100 % излизат заварените 92 и 22,
        // при 150 % — колкото буквите искат, вместо да режат името наполовина.
        var line = Theme.Small.Height + Theme.Scaled(4);

        var brand = new Panel
        {
            Dock = DockStyle.Top,
            Height = Math.Max(Theme.Scaled(92), Theme.Scaled(34) + Theme.Heading.Height + line + Theme.Scaled(8)),
            BackColor = Theme.Surface
        };

        _brandName.Dock = DockStyle.Fill;
        _brandName.Text = "RIOSV Imports";
        _brandName.Font = Theme.Heading;
        _brandName.ForeColor = Theme.Accent;
        _brandName.Padding = new Padding(Theme.Scaled(22), Theme.Scaled(34), 0, 0);

        brand.Controls.Add(_brandName);

        // Лентата се сглобява веднъж, затова този ред се пази в поле и се
        // обновява при смяна на езика — иначе остава на езика, на който
        // програмата е стартирала.
        _brandLine.Dock = DockStyle.Bottom;
        _brandLine.Height = Math.Max(Theme.Scaled(22), line);
        _brandLine.Font = Theme.Small;
        _brandLine.ForeColor = Theme.InkFaint;
        _brandLine.Padding = new Padding(Theme.Scaled(23), 0, 0, 0);
        _brandLine.Text = Lang.T("Отчети към НИСО", "NWIS reports");

        brand.Controls.Add(_brandLine);
        brand.Controls.Add(BuildRailToggle());

        // Изборът на фирма влиза ПРЕДИ името: закачените отгоре се редят
        // наопаки, а бутоните после се изнасят най-отпред. Така отгоре надолу
        // излиза име → фирма → бутони.
        _rail.Controls.Add(BuildScope());
        _rail.Controls.Add(brand);

        // Изданието — закачено отдолу, не мести бутоните. Многоточие вместо
        // рязане: при 150 % „Ново издание 1.10.2“ е на ръба на ширината.
        _railVersion.Dock = DockStyle.Bottom;
        _railVersion.Height = Theme.Small.Height + Theme.Scaled(14);
        _railVersion.Font = Theme.Small;
        _railVersion.ForeColor = Theme.InkFaint;
        _railVersion.BackColor = Theme.Surface;
        _railVersion.AutoEllipsis = true;
        _railVersion.TextAlign = ContentAlignment.MiddleLeft;
        _railVersion.Click += (_, _) =>
        {
            if (Updates.State.HasNewer) Show(_home);
        };

        _rail.Controls.Add(_railVersion);
        _rail.SizeChanged += (_, _) => FitRailFooter();
    }

    /// <summary>
    /// Изданието най-долу в лентата — или покана към по-новото.
    ///
    /// Поканата е видима от всеки екран: проверката при пускане отговаря,
    /// докато човек вече е отишъл в „Склад“, а известието на началния екран
    /// се вижда само на началния екран.
    /// </summary>
    private void ShowRailVersion()
    {
        var state = Updates.State;
        var newer = state.HasNewer && state.Version is not null;
        var collapsed = RailCollapsed;

        _railVersion.Text = newer
            ? collapsed ? "●" : Lang.T($"● Ново издание {state.Version}", $"● New version {state.Version}")
            : collapsed ? string.Empty : Lang.T($"Издание {Updates.CurrentVersion}", $"Version {Updates.CurrentVersion}");

        _railVersion.Font = newer ? Theme.SmallBold : Theme.Small;
        _railVersion.ForeColor = newer ? Theme.Accent : Theme.InkFaint;
        _railVersion.BackColor = Theme.Surface;
        _railVersion.Cursor = newer ? Cursors.Hand : Cursors.Default;
        _railVersion.TextAlign = collapsed ? ContentAlignment.MiddleCenter : ContentAlignment.MiddleLeft;
        _railVersion.Padding = collapsed ? Padding.Empty : new Padding(Theme.Scaled(23), 0, Theme.Scaled(8), 0);

        _railTips.SetToolTip(_railVersion, newer
            ? Lang.T($"Издание {state.Version} — към известието на началния екран",
                     $"Version {state.Version} — to the notification on Home")
            : string.Empty);

        FitRailFooter();
    }

    /// <summary>
    /// На нисък екран бутоните са по-важни от номера: долният край на лентата
    /// се дава на тях. Мери се по височините, а не по местата — местата се
    /// нареждат след събитието, а височините са известни отпреди.
    /// </summary>
    private void FitRailFooter()
    {
        var used = _rail.Padding.Vertical + _rail.Controls.Cast<Control>()
            .Where(c => c.Dock == DockStyle.Top && c.Visible)
            .Sum(c => c.Height);

        var show = _railVersion.Text.Length > 0 && used + _railVersion.Height <= _rail.ClientSize.Height;

        if (_railVersion.Visible != show) _railVersion.Visible = show;
    }

    /// <summary>Какво пише най-долу в лентата — ползва се от тестовете.</summary>
    internal string RailVersionText => _railVersion.Text;

    /// <summary>Броячът на „Начало“ в лентата — ползва се от тестовете.</summary>
    internal int HomeBadge => _pages.Count > 0 ? _pages[0].Button.Badge : 0;

    /// <summary>
    /// Вика се веднъж, щом прозорецът се покаже — от Program.Main, а не оттук:
    /// тестовете показват главния прозорец стотици пъти и не бива да питат
    /// GitHub нито веднъж.
    /// </summary>
    internal void OnLaunched()
    {
        // „Обновено до …“ — веднъж, на първото пускане на новото издание.
        if (NewsFeed.NoteUpgrade(_workspace, Updates.CurrentVersion))
        {
            if (_home.Visible) _home.Refresh();
            UpdateBadges();
        }

        if (_workspace.Settings.GetBool(SettingsStore.Keys.UpdateAuto, true)) CheckOnline();
    }

    /// <summary>Пита GitHub и за издание, и за новини — едното не чака другото.</summary>
    internal void CheckOnline()
    {
        _ = Updates.CheckAsync();
        _ = RefreshNewsAsync();
    }

    private bool _newsRunning;

    private async Task RefreshNewsAsync()
    {
        if (_newsRunning) return;
        _newsRunning = true;

        try
        {
            var added = await NewsFeed.RefreshAsync(_workspace, AppServices.Web, Updates.CurrentVersion);

            if (IsDisposed || added is null) return;

            // Преди началния екран: той отбелязва видяното като прочетено, а
            // спешното трябва да изскочи, преди да е отбелязано.
            ShowCritical();

            if (_home.Visible) _home.Refresh();
            UpdateBadges();
        }
        finally
        {
            _newsRunning = false;
        }
    }

    /// <summary>
    /// Сгрешено издание, спрян подпис — това не чака човек да мине през
    /// началния екран. Изскача веднъж; после стои в известията като всяко друго.
    /// </summary>
    private void ShowCritical()
    {
        var urgent = _workspace.News.List(20)
            .Where(item => item is { Seen: false, Severity: NewsSeverity.Critical })
            .Take(2)
            .ToList();

        foreach (var item in urgent)
        {
            _workspace.News.MarkSeen(item.Id);

            MessageBox.Show(this, item.Body.Length > 0 ? item.Body : item.Title, item.Title,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnUpdateChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;

        if (IsHandleCreated && InvokeRequired)
        {
            BeginInvoke(() => OnUpdateChanged(sender, e));
            return;
        }

        var state = Updates.State;

        // Кога е питано — за празното известие и за настройките след рестарт.
        if (state.Stage is UpdateStage.Current or UpdateStage.Available && state.CheckedAt is { } at)
            _workspace.Settings.Set(SettingsStore.Keys.UpdateChecked,
                at.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

        // Бележките на намереното се пазят за след рестарта: тогава те казват
        // „какво е новото“, а проверка още няма.
        if (state.Stage == UpdateStage.Available && state.Release is { Notes.Length: > 0 } release)
            _workspace.Settings.Set(SettingsStore.Keys.UpdateNotes, release.Version + "\n" + release.Notes);

        // Само видимият начален екран се пипа: той отбелязва показаното като
        // прочетено, а невидим не е показал нищо. Щом се отвори, се пълни сам.
        if (_home.Visible) _home.ShowUpdates();

        _settings.ShowUpdates();
        ShowRailVersion();
        UpdateBadges();
    }

    /// <summary>
    /// Програмата се затваря, за да се обнови. Инсталаторът се пуска от
    /// Program.Main, след като базата е затворена.
    /// </summary>
    private void RestartToUpdate()
    {
        if (!Updates.CanApply || Updates.State.Stage != UpdateStage.Ready) return;

        RestartForUpdate = true;
        Close();
    }

    /// <summary>Има ли нещо за гледане в обновяването — едно в брояча на „Начало“.</summary>
    private bool UpdateWaiting => Updates.State switch
    {
        { Stage: UpdateStage.Available, Version: { } version } =>
            version != _workspace.Settings.Get(SettingsStore.Keys.UpdateSkipped),
        { Stage: UpdateStage.Downloading or UpdateStage.Ready } => true,
        _ => false
    };

    /// <summary>
    /// Фирмата, по която се работи — един избор за цялата програма.
    ///
    /// Стои в лентата, а не по екраните, защото е едно и също навсякъде: човек
    /// работи по една фирма, докато свърши с нея. Дотогава същият избор стоеше
    /// в четири ленти по четири екрана и ядеше по двеста пиксела от всяка —
    /// точно тези, които на лаптоп не достигат.
    /// </summary>
    private Control BuildScope()
    {
        _scopeBox.Dock = DockStyle.Top;
        _scopeBox.BackColor = Theme.Surface;
        _scopeBox.Padding = new Padding(Theme.Scaled(18), Theme.GapSmall, Theme.Scaled(18), Theme.Gap);

        _scopePicker.Dock = DockStyle.Bottom;
        _scopePicker.DropDownStyle = ComboBoxStyle.DropDownList;
        _scopePicker.Font = Theme.Base;
        _scopePicker.SelectedIndexChanged += (_, _) => UseScope();
        Theme.StyleInput(_scopePicker);

        _scopeCaption.Dock = DockStyle.Top;
        _scopeCaption.Height = Theme.Small.Height + 8;

        // Надписът плюс самото поле плюс отстъпите — а не 74 пиксела: при 150 %
        // полето с фирмата е по-високо и се режеше отгоре.
        _scopeBox.Height = Math.Max(74,
            _scopeBox.Padding.Vertical + _scopeCaption.Height + _scopePicker.PreferredHeight);
        _scopeCaption.Font = Theme.SmallBold;
        _scopeCaption.ForeColor = Theme.InkFaint;
        _scopeCaption.Text = Lang.T("Фирма", "Company");

        _scopeBox.Controls.Add(_scopePicker);
        _scopeBox.Controls.Add(_scopeCaption);

        FillScope();

        return _scopeBox;
    }

    /// <summary>
    /// Пълни списъка с фирми.
    ///
    /// Без нито една заведена списъкът казва точно това: празен избор без
    /// обяснение изглежда като повреда, а отговорът е в „Настройки“.
    /// </summary>
    private void FillScope()
    {
        _filling = true;

        try
        {
            _scopePicker.Items.Clear();
            _scopeIds.Clear();

            // Само името: лентата е двеста пиксела, а ЕИК-ът след името
            // изяжда точно тях. Той стои в подсказката — там се проверява,
            // когато две дружества се казват почти еднакво.
            foreach (var company in _scope.Companies)
            {
                _scopePicker.Items.Add(company.Name);
                _scopeIds.Add(company.Id);
            }

            if (_scopeIds.Count == 0)
            {
                _scopePicker.Items.Add(Lang.T("— заведете фирма —", "— add a company —"));
                _scopePicker.SelectedIndex = 0;
                _scopePicker.Enabled = false;

                return;
            }

            _scopePicker.Enabled = true;

            var index = _scope.Current is { } id ? _scopeIds.IndexOf(id) : -1;
            _scopePicker.SelectedIndex = index >= 0 ? index : 0;

            _railTips.SetToolTip(_scopePicker, _scope.Company?.Display ?? string.Empty);
        }
        finally
        {
            _filling = false;
        }
    }

    /// <summary>Избраното в лентата става фирмата за всички екрани.</summary>
    private void UseScope()
    {
        if (_filling) return;
        if (_scopePicker.SelectedIndex < 0 || _scopePicker.SelectedIndex >= _scopeIds.Count) return;

        _scope.Use(_scopeIds[_scopePicker.SelectedIndex]);

        _railTips.SetToolTip(_scopePicker, _scope.Company?.Display ?? string.Empty);

        // Страниците се обновяват сами по събитието на обхвата; тук остават
        // брояците, които не са на страница.
        UpdateBadges();
    }

    /// <summary>
    /// Копчето, което свива лентата.
    ///
    /// Обикновено копче, а не <see cref="NavButton"/>: бутоните в лентата се
    /// намират по РЕД и се броят — и от програмата, и от теста. Копче, което не
    /// отваря страница, няма работа в този брой.
    ///
    /// Стои горе, до името на програмата. Първият му вариант беше най-долу — и
    /// не се намери: там окото не търси нищо, а долният край на лентата е
    /// мястото, на което човек поглежда последно. Ctrl+Q остава за тези, които
    /// не пипат мишката.
    /// </summary>
    private Control BuildRailToggle()
    {
        _railToggle.Dock = DockStyle.Right;
        _railToggle.Width = Theme.Scaled(44);
        _railToggle.FlatStyle = FlatStyle.Flat;
        _railToggle.FlatAppearance.BorderSize = 0;
        _railToggle.FlatAppearance.MouseOverBackColor = Theme.AccentSoft;
        _railToggle.FlatAppearance.MouseDownBackColor = Theme.AccentLight;
        _railToggle.BackColor = Theme.Surface;
        _railToggle.ForeColor = Theme.InkMuted;
        _railToggle.Font = Theme.Icon;
        _railToggle.Cursor = Cursors.Hand;
        _railToggle.TabStop = false;
        _railToggle.Click += (_, _) => RailCollapsed = !RailCollapsed;

        return _railToggle;
    }

    /// <summary>
    /// Свита ли е лентата отляво.
    ///
    /// Свита, тя е ивица с икони: на лаптоп двеста пиксела са три колони от
    /// баланса, а на човек, който работи в програмата всеки ден, надписите
    /// отдавна не му трябват. Изборът се помни — иначе всяко пускане го връща.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool RailCollapsed
    {
        get => _rail.Width == RailNarrow;
        set
        {
            ApplyRail(value);
            _workspace.Settings.Set(SettingsStore.Keys.RailCollapsed, value);
        }
    }

    /// <summary>
    /// Привежда лентата към даденото състояние.
    ///
    /// Отделно от свойството, защото се вика и при смяна на езика: надписите на
    /// подсказките са на езика, който е бил в сила при последното превключване,
    /// а записът в настройките тогава е излишен.
    /// </summary>
    private void ApplyRail(bool collapsed)
    {
        _rail.Width = collapsed ? RailNarrow : RailWide;

        _brandName.Visible = !collapsed;
        _brandLine.Visible = !collapsed;

        // Свитата лента е ивица с икони: падащ списък с име на фирма няма
        // къде да се събере в нея. Коя е фирмата се вижда, щом лентата се
        // отвори — а тя се отваря с едно натискане.
        _scopeBox.Visible = !collapsed;
        _scopeShown = !collapsed;

        foreach (var button in _nav)
        {
            button.Compact = collapsed;
            button.Width = _rail.Width;
            button.Invalidate();

            _railTips.SetToolTip(button, collapsed ? button.Text : string.Empty);
        }

        _railToggle.Dock = collapsed ? DockStyle.Fill : DockStyle.Right;
        _railToggle.Text = collapsed ? Theme.Glyph.Chevron : Theme.Glyph.ChevronBack;

        _railTips.SetToolTip(_railToggle, collapsed
            ? Lang.T("Отвори лентата (Ctrl+Q)", "Open the rail (Ctrl+Q)")
            : Lang.T("Свий лентата (Ctrl+Q)", "Collapse the rail (Ctrl+Q)"));

        // Свитата лента няма място за номер — остава само точката за ново издание.
        ShowRailVersion();
    }

    /// <summary>
    /// Ctrl+Q свива и отваря лентата.
    ///
    /// С лявата ръка, без да се пуска мишката: това е клавиш, който се натиска
    /// между две други неща, а не действие, заради което човек сяда.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message message, Keys keys)
    {
        if (keys == (Keys.Control | Keys.Q))
        {
            RailCollapsed = !RailCollapsed;
            return true;
        }

        // Ctrl+1 … Ctrl+8 — поредният екран от лентата, както разделите в
        // браузъра. Между „Месец“ и „Склад“ се скача по сто пъти на ден.
        if (PageByKey(keys) is { } index && index < _pages.Count)
        {
            Show(_pages[index].Page);
            return true;
        }

        // Търсенето на видимия екран — откъдето и да е курсорът. Страница без
        // търсене („Начало“, „Баланс“) не поема клавиша.
        if (keys == (Keys.Control | Keys.F) &&
            _pages.FirstOrDefault(entry => entry.Button.Active).Page is ContentPage { } page &&
            page.FocusSearch())
            return true;

        // Списъкът с клавишите се отваряше само от редактора, а сега в него
        // има и клавиши, които важат тук.
        if (keys == (Keys.Control | Keys.F1))
        {
            using var dialog = new Editor.ShortcutsDialog();
            dialog.ShowDialog(this);

            return true;
        }

        return base.ProcessCmdKey(ref message, keys);
    }

    /// <summary>Кой поред екран иска съчетанието — Ctrl с цифра от реда или от цифровата част.</summary>
    internal static int? PageByKey(Keys keys)
    {
        if ((keys & Keys.Modifiers) != Keys.Control) return null;

        return (keys & Keys.KeyCode) switch
        {
            >= Keys.D1 and <= Keys.D9 and var digit => digit - Keys.D1,
            >= Keys.NumPad1 and <= Keys.NumPad9 and var pad => pad - Keys.NumPad1,
            _ => null
        };
    }

    /// <summary>Натиска съчетание, както човек — ползва се от тестовете.</summary>
    internal bool Press(Keys keys)
    {
        var message = new Message();
        return ProcessCmdKey(ref message, keys);
    }

    /// <summary>Кой екран е отворен — ползва се от тестовете.</summary>
    /// <remarks>
    /// По бутона, а не по страницата: страница в непоказан прозорец казва „невидима“
    /// и когато е избрана.
    /// </remarks>
    internal string ActivePage => _pages.FirstOrDefault(entry => entry.Button.Active).Page?.GetType().Name ?? string.Empty;

    private void Add(string title, string glyph, Control page)
    {
        // Бутоните в лентата се правят веднъж и преживяват пресъздаването на
        // страниците. Намират се по РЕД, не по надпис: при смяна на езика
        // надписът вече е друг, съществуващият бутон не се разпознава и до
        // лентата се долепя втори комплект на новия език.
        //
        // Редът е стабилен, защото BuildPages вика Add един и същ брой пъти в
        // един и същ ред, а _pages е изчистен точно преди това.
        var index = _pages.Count;
        var button = index < _nav.Count ? _nav[index] : null;

        if (button is null)
        {
            button = new NavButton(title, glyph) { Dock = DockStyle.Top, Width = _rail.Width };

            // Абонаментът е еднократен, а целта се сменя през Tag. Ако вместо
            // това се закачаше нов обработчик при всяко пресъздаване, старите
            // щяха да останат и да сочат към вече премахнати страници.
            button.Click += (sender, _) =>
            {
                if (sender is NavButton { Tag: Control target }) Show(target);
            };

            _rail.Controls.Add(button);
            button.BringToFront();
            _nav.Add(button);
        }

        // Надписът се задава и на заварения бутон: само така смяната на езика
        // стига до лентата.
        button.Text = title;
        button.Tag = page;
        button.Invalidate();

        page.Dock = DockStyle.Fill;
        page.Visible = false;
        _host.Controls.Add(page);

        _pages.Add((button, page));
    }

    /// <summary>
    /// Надписите на бутоните в лентата — ползва се от тестовете.
    ///
    /// Чете се от самата лента, а не от <see cref="_nav"/>: тъкмо разликата
    /// между двете беше грешката — бутоните се трупаха в лентата, докато
    /// списъкът изглеждаше наред.
    /// </summary>
    internal List<string> NavLabels() =>
        _rail.Controls.OfType<NavButton>().Select(b => b.Text).ToList();

    /// <summary>Редът под името на програмата — ползва се от тестовете.</summary>
    internal string BrandLine => _brandLine.Text;

    /// <summary>Фирмите в лентата — ползва се от тестовете.</summary>
    internal IReadOnlyList<string> CompanyChoices => [.. _scopePicker.Items.Cast<string>()];

    /// <summary>Коя фирма е избрана в лентата — ползва се от тестовете.</summary>
    internal long? CurrentCompany => _scope.Current;

    /// <summary>Избира фирма от лентата, както го прави човек.</summary>
    internal void UseCompany(int index)
    {
        if (index < 0 || index >= _scopePicker.Items.Count) return;

        _scopePicker.SelectedIndex = index;
    }

    /// <summary>Вижда ли се изборът на фирма — при свита лента го няма.</summary>
    internal bool CompanyPickerShown => _scopeShown;

    /// <summary>Ширината на лентата отляво — ползва се от тестовете.</summary>
    internal int RailWidth => _rail.Width;

    /// <summary>
    /// Казва на страниците, че данните зад тях са се сменили.
    ///
    /// Пресмята се само видимата. Останалите се пресмятат при показване — Show
    /// го прави за всяка — и пресмятането им сега е чакане, което никой не вижда.
    /// Видима е тази, чийто бутон е избран, а не тази с Visible: в непоказан
    /// прозорец Visible е „не“ за всичко (виж капаните).
    /// </summary>
    private void Touch(params Control[] pages)
    {
        var active = _pages.FirstOrDefault(entry => entry.Button.Active).Page;

        foreach (var page in pages)
        {
            if (!ReferenceEquals(page, active)) continue;

            switch (page)
            {
                case HomePage home: home.Refresh(); break;
                case WorkPage work: work.Reload(); break;
                case BalancePage balance: balance.Refresh(); break;
                case LedgerPage ledger: ledger.Refresh(); break;
                case StockPage stock: stock.Refresh(); break;
            }
        }
    }

    private void Show(Control page)
    {
        foreach (var (button, candidate) in _pages)
        {
            var active = ReferenceEquals(candidate, page);

            candidate.Visible = active;
            button.Active = active;
        }

        if (page is HomePage home) home.Refresh();
        if (page is WorkPage work) work.Reload();
        if (page is BalancePage balance) balance.Refresh();
        if (page is LedgerPage ledger) ledger.Refresh();
        if (page is StockPage stock) stock.Refresh();
        if (page is ListsPage lists) lists.Refresh();

        UpdateBadges();
    }

    private void UpdateBadges()
    {
        foreach (var (button, page) in _pages)
        {
            button.Badge = page switch
            {
                HomePage => _workspace.News.UnseenCount() + (UpdateWaiting ? 1 : 0),
                WorkPage => _workspace.Documents.List().Count(d => d.Status != DocumentStatus.Signed),
                _ => 0
            };

            button.Invalidate();
        }
    }
}
