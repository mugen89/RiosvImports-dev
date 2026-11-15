using RiosvImports.App.Controls;
using RiosvImports.Core;
using RiosvImports.Core.Localization;
using RiosvImports.Core.Schema;
using RiosvImports.Core.Values;
using RiosvImports.Data;

namespace RiosvImports.App.Shell;

/// <summary>
/// Кратките списъци: какво да се вижда в менютата за всяка фирма.
///
/// Схемата на НИСО е обща за цялата страна и затова предлага всичко —
/// осемстотин и четиридесет ЕКО кода, тринайсет дейности по оползотворяване.
/// Една фирма работи с двайсет от първите и с две-три от вторите. Дългото меню
/// не е богат избор, а разстояние между правилния ред и съседния му.
///
/// Затова тук се записва не какво е позволено по наредба, а какво тази фирма
/// върши: списъкът НЕ проверява нищо и нищо не отхвърля. Стеснява само какво
/// се предлага. Внесен отвън файл, отчет отпреди списъка и кодът, който вече
/// стои в реда, минават непокътнати — иначе екранът щеше мълчаливо да трие
/// чужда работа.
///
/// Страницата има две части. „Списъци“ показва записаното. „Защо липсва“ пита
/// обратното: човек вижда, че една стойност я няма в менюто, въвежда я тук и
/// получава кой точно запис я спира. Без нея отговорът се намира чрез отваряне
/// на записите един по един — работа, която програмата може да свърши сама.
/// </summary>
internal sealed class ListsPage : ContentPage
{
    private const string CompanyColumn = "company";
    private const string KindColumn = "kind";
    private const string ScopeColumn = "scope";
    private const string FieldColumn = "field";
    private const string ValuesColumn = "values";
    private const string StateColumn = "state";

    private const string FoundValueColumn = "value";
    private const string FoundFieldColumn = "foundfield";
    private const string FoundCompanyColumn = "foundcompany";
    private const string FoundScopeColumn = "foundscope";
    private const string FoundStateColumn = "foundstate";

    /// <summary>„Всички фирми“ и „Всички видове“ стоят на нулевото място в менютата.</summary>
    private const int Everything = 0;

    private readonly ImportService _service;
    private readonly Workspace _workspace;
    private readonly CompanyScope _scope;

    private readonly SegmentedControl _tabs = new();
    private readonly Panel _host = new();

    // ------------------------------------------------------------- „Списъци“

    private readonly Panel _listsView = new();
    private readonly DataGridView _grid = new();
    private readonly Label _summary = new();
    private readonly Panel _empty = new();
    private readonly SearchBox _search = new(Lang.T("Търсете по поле или стойност", "Search by field or value"));
    private readonly ComboBox _byKind = new();

    /// <summary>Отметката „попълвай останалата една стойност“ и какво значи тя.</summary>
    private readonly CheckBox _fills = new();

    private readonly Label _fillsSays = new();

    /// <summary>Пълненето на отметката вдига CheckedChanged; тогава то не е избор на човека.</summary>
    private bool _loading;

    /// <summary>Заглавието на екрана — носи и избраната фирма.</summary>
    private readonly Label _pageTitle = SettingsPage.Title(string.Empty);

    private readonly AppButton _edit;
    private readonly AppButton _toggle;
    private readonly AppButton _remove;

    /// <summary>Всичко записано, и подмножеството, което се вижда сега.</summary>
    private List<ValueList> _all = [];
    private List<ValueList> _rows = [];

    /// <summary>Фирмите в реда на менюто за пресяване; нулевото място е „всички“.</summary>
    private List<Company> _companies = [];

    // ------------------------------------------------------------- „Защо липсва“

    private readonly Panel _searchView = new();
    private readonly TextBox _values = new();
    private readonly DataGridView _found = new();
    private readonly Label _foundSummary = new();

    /// <summary>
    /// Фирмата идва отвън — от лентата отляво. Подаден null значи „направи си
    /// свой обхват“: така страницата се сглобява и сама, което ползва тестът.
    /// </summary>
    public ListsPage(ImportService service, Workspace workspace, CompanyScope? scope = null) : base(1180)
    {
        _service = service;
        _workspace = workspace;
        _scope = scope ?? new CompanyScope(workspace);

        _scope.Changed += (_, _) => Refresh();

        _edit = Theme.SecondaryButton(Lang.T("Промени", "Edit"));
        _toggle = Theme.SecondaryButton(Lang.T("Изключи", "Turn off"));
        _remove = Theme.DangerButton(Lang.T("Изтрий", "Delete"), Theme.Glyph.Delete);

        BuildListsView();
        BuildSearchView();

        _host.Dock = DockStyle.Fill;
        _host.BackColor = Theme.Canvas;
        _host.Controls.Add(_listsView);
        _host.Controls.Add(_searchView);

        _tabs.Dock = DockStyle.Top;
        _tabs.Height = Theme.Scaled(44);
        _tabs.SetItems([Lang.T("Списъци", "Lists"), Lang.T("Защо липсва", "Why is it missing")]);
        _tabs.SelectedIndexChanged += (_, _) => ShowTab();

        var stack = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas };

        stack.Controls.Add(_host);
        stack.Controls.Add(new Panel { Dock = DockStyle.Top, Height = Theme.Gap, BackColor = Theme.Canvas });
        stack.Controls.Add(_tabs);
        stack.Controls.Add(SettingsPage.Subtitle(Lang.T(
            "Кратките списъци стесняват само предлаганото. Нищо не се отхвърля и вече попълненото остава.",
            "Short lists narrow only what is offered. Nothing is rejected and what is already filled in stays.")));
        _pageTitle.Text = Lang.T("Списъци", "Lists");
        stack.Controls.Add(_pageTitle);

        Controls.Add(stack);

        Refresh();
        ShowTab();
    }

    private void ShowTab()
    {
        var lists = _tabs.SelectedIndex <= 0;

        _listsView.Visible = lists;
        _searchView.Visible = !lists;

        if (!lists) FillCompanyPickers();
    }

    // ============================================================= „Списъци“

    private void BuildListsView()
    {
        _listsView.Dock = DockStyle.Fill;
        _listsView.BackColor = Theme.Canvas;

        // Закачените отгоре се редят наопаки на реда на добавяне: копчетата
        // застават най-горе, под тях отметката, после ситото и чак тогава
        // таблицата.
        _listsView.Controls.Add(BuildBody());
        _listsView.Controls.Add(BuildSieve());
        _listsView.Controls.Add(BuildFills());
        _listsView.Controls.Add(BuildTools());
    }

    private Control BuildTools()
    {
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(56),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = Theme.Canvas
        };

        var add = Theme.PrimaryButton(Lang.T("Нов списък", "New list"));
        add.Glyph = Theme.Glyph.Add;
        add.Width = Theme.Scaled(170);
        add.Click += (_, _) => Create();

        _edit.Width = Theme.Scaled(130);
        _edit.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        _edit.Click += (_, _) => Edit();

        _toggle.Width = Theme.Scaled(150);
        _toggle.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        _toggle.Click += (_, _) => Toggle();

        _remove.Width = Theme.Scaled(130);
        _remove.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        _remove.Click += (_, _) => Remove();

        bar.Controls.Add(add);
        bar.Controls.Add(_edit);
        bar.Controls.Add(_toggle);
        bar.Controls.Add(_remove);

        return bar;
    }

    /// <summary>
    /// Отметката за останалата една стойност.
    ///
    /// Стои тук, а не в „Настройки“, защото не важи сама за себе си: без кратък
    /// списък не се променя нищо, а точно списъците са на този екран. Стои и
    /// под копчетата, а не до тях — тя е за всички записи наведнъж, не за
    /// избрания ред.
    ///
    /// Отметка на фирма: една площадка предава на един получател с една дейност
    /// и цял живот преписва едно и също, а съседната работи с десет.
    /// </summary>
    private Control BuildFills()
    {
        var bar = new Panel { Dock = DockStyle.Top, Height = Theme.Scaled(62), BackColor = Theme.Canvas };

        _fills.Text = Lang.T(
            "Попълвай сам полето, което списъкът е свел до една стойност",
            "Fill in a field on my behalf when the list has narrowed it to one value");

        _fills.Location = new Point(0, 2);
        _fills.Size = new Size(Theme.Scaled(620), Theme.Base.Height + 10);
        _fills.ForeColor = Theme.Ink;
        _fills.BackColor = Color.Transparent;
        _fills.CheckedChanged += (_, _) => SaveFills();
        Theme.StyleInput(_fills);

        // Два реда: изречението казва и докъде важи, и колко полета засяга сега,
        // а то не се събира на един ред при тясна страница.
        _fillsSays.Location = new Point(0, _fills.Bottom + 2);
        _fillsSays.Size = new Size(Theme.Scaled(900), Theme.Small.Height * 2 + 6);
        _fillsSays.Font = Theme.Small;
        _fillsSays.ForeColor = Theme.InkMuted;

        bar.Controls.Add(_fills);
        bar.Controls.Add(_fillsSays);

        // Колкото отметката и двата реда под нея — иначе долният ред се режеше.
        bar.Height = Math.Max(bar.Height, _fillsSays.Bottom + Theme.Scaled(4));

        return bar;
    }

    private void SaveFills()
    {
        if (_loading) return;
        if (_scope.Current is not { } companyId) return;

        _workspace.Lists.SetFills(companyId, _fills.Checked);

        ShowFills();
    }

    /// <summary>
    /// Казва с думи какво значи отметката — отметка сама не обяснява.
    ///
    /// Броят на полетата е това, което прави изречението проверимо: „важи за 3
    /// полета“ се сверява с таблицата отдолу, а „попълва се само“ — не.
    /// </summary>
    private void ShowFills()
    {
        if (_scope.Current is not { } companyId)
        {
            _fills.Enabled = false;
            _fills.Checked = false;

            _fillsSays.Text = Lang.T(
                "Отметката е на фирма — изберете фирма в лентата отляво.",
                "The tick is per company — choose one in the rail on the left.");

            _fillsSays.ForeColor = Theme.InkFaint;
            return;
        }

        _fills.Enabled = true;

        // Броят се само списъците за отчетните книги: прозорчетата на
        // движенията са единствените, които попълват сами. Редакторът пипа
        // стотици реда наведнъж и там това би значело друго.
        var singles = _all.Count(entry =>
            entry.CompanyId == companyId &&
            entry.Enabled &&
            entry.SchemaKey == "book" &&
            entry.Allowed.Count == 1);

        if (!_fills.Checked)
        {
            _fillsSays.Text = Lang.T(
                "Изключено: менютата се стесняват, но нищо не се попълва само. " +
                $"В момента {Fields(singles)} с една стойност.",
                "Off: menus are narrowed but nothing is filled in on its own. " +
                $"Right now {Fields(singles)} with a single value.");

            _fillsSays.ForeColor = Theme.InkMuted;
            return;
        }

        _fillsSays.Text = Lang.T(
            "Важи за прозорчетата на движенията — приемане, третиране, образуване, предаване. " +
            $"В момента {Fields(singles)} с една стойност и те влизат попълнени.",
            "Applies to the movement windows — receipt, treatment, generation, hand-over. " +
            $"Right now {Fields(singles)} with a single value and they come in filled.");

        _fillsSays.ForeColor = singles > 0 ? Theme.Ok : Theme.InkMuted;
    }

    /// <summary>„няма поле“ / „едно поле е“ / „3 полета са“ — числото се чете на български.</summary>
    private static string Fields(int count) => count switch
    {
        0 => Lang.T("няма поле", "there is no field"),
        1 => Lang.T("едно поле е", "one field is"),
        _ => Lang.T($"{count} полета са", $"{count} fields are")
    };

    private void LoadFills()
    {
        _loading = true;

        try
        {
            _fills.Checked = _scope.Current is { } companyId && _workspace.Lists.Fills(companyId);
        }
        finally
        {
            _loading = false;
        }

        ShowFills();
    }

    /// <summary>
    /// Редът за пресяване.
    ///
    /// Един счетоводител с двайсет фирми стига до стотина записа и тогава
    /// таблицата се чете само с превъртане. Търсенето хваща и стойност — „кой
    /// има 15 01 10* в списъка си“ е въпрос, който се задава.
    /// </summary>
    private Control BuildSieve()
    {
        var bar = new Panel { Dock = DockStyle.Top, Height = Theme.Scaled(52), BackColor = Theme.Canvas };

        _byKind.DropDownStyle = ComboBoxStyle.DropDownList;
        _byKind.Width = Theme.Scaled(190);
        _byKind.Location = new Point(0, 8);
        _byKind.Items.Add(Lang.T("Всички видове", "All kinds"));
        foreach (var schema in _service.Schemas) _byKind.Items.Add(ShortKind(schema.Key));
        _byKind.SelectedIndex = Everything;
        _byKind.SelectedIndexChanged += (_, _) => Sieve();
        Theme.StyleInput(_byKind);

        _search.Location = new Point(_byKind.Right + Theme.Gap, 8);
        _search.Width = Theme.Scaled(420);
        _search.TextChanged += (_, _) => Sieve();

        bar.Controls.Add(_byKind);
        bar.Controls.Add(_search);

        return bar;
    }

    private Control BuildBody()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas };

        var frame = new Card
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(1),
            Fill = Theme.Canvas,
            Outline = Theme.Border
        };

        BuildGrid();

        frame.Controls.Add(_grid);
        frame.Controls.Add(_empty);

        _summary.Dock = DockStyle.Bottom;
        _summary.Height = Theme.Base.Height + 12;
        _summary.Font = Theme.Small;
        _summary.ForeColor = Theme.InkFaint;

        host.Controls.Add(frame);
        host.Controls.Add(_summary);

        return host;
    }

    private void BuildGrid()
    {
        Dress(_grid);

        Column(_grid, CompanyColumn, Lang.T("Фирма", "Company"), 200);
        Column(_grid, KindColumn, Lang.T("Вид", "Kind"), 110);
        Column(_grid, ScopeColumn, Lang.T("Обхват", "Scope"), 260);
        Column(_grid, FieldColumn, Lang.T("Поле", "Field"), 170);
        Column(_grid, ValuesColumn, Lang.T("Стойности", "Values"), 280);
        Column(_grid, StateColumn, Lang.T("Състояние", "State"), 110);

        _grid.SelectionChanged += (_, _) => ShowSelection();
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Edit(); };

        _empty.Dock = DockStyle.Fill;
        _empty.BackColor = Theme.Canvas;
        _empty.Padding = new Padding(Theme.GapLarge * 2, Theme.GapLarge * 2, Theme.GapLarge, Theme.Gap);
        _empty.Visible = false;

        _empty.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            Height = Theme.Base.Height * 6,
            Font = Theme.Base,
            ForeColor = Theme.InkMuted,
            Text = Lang.T(
                "Още няма нито един списък — менютата предлагат всичко от схемата.\r\n\r\n" +
                "„Нов списък“ пита за коя фирма, докъде важи и кое поле, и след това се отмятат\r\n" +
                "стойностите. Свит до двайсетина стойности, ЕКО кодът престава да е търсачка и\r\n" +
                "става обикновено падащо меню.",
                "There is no list yet — the menus offer everything from the schema.\r\n\r\n" +
                "“New list” asks for which company, how far it reaches and which field, and then you\r\n" +
                "tick the values. Cut down to about twenty, the EWC code stops being a search box and\r\n" +
                "becomes an ordinary dropdown.")
        });
    }

    /// <summary>Общият вид на двете таблици — рисуват едно и също нещо.</summary>
    private static void Dress(DataGridView grid)
    {
        grid.Dock = DockStyle.Fill;
        grid.Font = Theme.Base;
        grid.BackgroundColor = Theme.Canvas;
        grid.BorderStyle = BorderStyle.None;
        grid.EnableHeadersVisualStyles = false;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.RowHeadersVisible = false;
        grid.MultiSelect = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;

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
        grid.ColumnHeadersHeight = line + 22;
    }

    private static void Column(DataGridView grid, string name, string header, int width) =>
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            Width = Theme.Scaled(width),
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

    // ------------------------------------------------------------- пълнене

    public new void Refresh()
    {
        _all = [.. _workspace.Lists.List()];

        FillCompanyPickers();
        LoadFills();
        Sieve();
    }

    /// <summary>
    /// Чете фирмите наново.
    ///
    /// Фирмата се избира в лентата отляво, но списъкът ѝ трябва да е пресен и
    /// тук: тя може да е заведена или преименувана в „Настройки“, докато тази
    /// страница е стояла отворена.
    /// </summary>
    private void FillCompanyPickers() => _companies = [.. _workspace.Companies.List()];

    /// <summary>Заглавието на екрана — ползва се от тестовете.</summary>
    internal string TitleText => _pageTitle.Text;

    /// <summary>Колко записа се виждат — ползва се от тестовете.</summary>
    internal int ShownCount => _rows.Count;

    /// <summary>Вдигната ли е отметката за останалата една стойност.</summary>
    internal bool FillsOn => _fills.Checked;

    /// <summary>Предлага ли се изобщо — без избрана фирма не се предлага.</summary>
    internal bool FillsOffered => _fills.Enabled;

    /// <summary>Какво пише под отметката — ползва се от тестовете.</summary>
    internal string FillsText => _fillsSays.Text;

    /// <summary>Вдига или пуска отметката, както би го направил човек.</summary>
    internal void UseFills(bool on) => _fills.Checked = on;

    /// <summary>Пресява записаното по избраното горе и по търсената дума.</summary>
    private void Sieve()
    {
        _pageTitle.Text = _scope.Titled(Lang.T("Списъци", "Lists"));

        var kind = _byKind.SelectedIndex > Everything
            ? _service.Schemas[_byKind.SelectedIndex - 1].Key
            : null;

        // Фирмата вече не е сито тук: тя се избира веднъж, в лентата отляво.
        var company = _scope.Current;

        var query = _search.Text.Trim();

        _rows = [.. _all
            .Where(entry => kind is null || entry.SchemaKey == kind)
            .Where(entry => company is null || entry.CompanyId == company)
            .Where(entry => query.Length == 0 || Matches(entry, query))];

        FillGrid();
    }

    /// <summary>
    /// Търси в това, което човек вижда — и в самите стойности.
    ///
    /// Стойностите не са на екрана целите (показват се първите пет), но точно
    /// по тях се пита: „кой има този код в списъка си“.
    /// </summary>
    private bool Matches(ValueList entry, string query) =>
        CompanyName(entry.CompanyId).Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        FieldName(entry).Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        ScopeName(entry).Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        entry.Allowed.Any(v => v.Contains(query, StringComparison.OrdinalIgnoreCase));

    private void FillGrid()
    {
        _grid.Rows.Clear();

        foreach (var entry in _rows)
        {
            _grid.Rows.Add(
                CompanyName(entry.CompanyId),
                ShortKind(entry.SchemaKey),
                ScopeName(entry),
                FieldName(entry),
                ValuesName(entry),
                entry.Enabled ? Lang.T("Включен", "On") : Lang.T("Изключен", "Off"));

            // Изключеният ред се вижда от другия край на екрана.
            //
            // Отначало беше само по-бледо сив и се разчиташе едва при четене ред
            // по ред. А точно изключеният списък е причината едно меню да
            // предлага повече, отколкото човек очаква — той трябва да се хваща
            // с крайчеца на окото, както се хваща сгрешена клетка в таблицата.
            //
            // Цветът е на реда, а не на клетките: стилът на реда се налага над
            // редуването на фоновете, докато стилът на колоната — не.
            if (!entry.Enabled)
            {
                var style = _grid.Rows[^1].DefaultCellStyle;

                style.BackColor = Theme.ErrorSoft;
                style.ForeColor = Theme.Error;

                // Избраният ред пази общия фон на избраното — иначе редът
                // сменя два цвята наведнъж и изглежда като друго състояние.
                style.SelectionBackColor = Theme.AccentLight;
                style.SelectionForeColor = Theme.Error;
            }
        }

        var any = _rows.Count > 0;

        _grid.Visible = any;

        // Обяснението за празен екран важи само когато няма нищо изобщо. Празно
        // след пресяване е друго състояние и си иска друг надпис.
        _empty.Visible = !any && _all.Count == 0;

        _summary.Text = _all.Count == 0
            ? string.Empty
            : _rows.Count == _all.Count
                ? Lang.T($"{_all.Count} списъка, {_all.Count(e => e.Enabled)} включени.",
                         $"{_all.Count} lists, {_all.Count(e => e.Enabled)} on.")
                : Lang.T($"Показани {_rows.Count} от {_all.Count} списъка.",
                         $"{_rows.Count} of {_all.Count} lists shown.");

        ShowSelection();
    }

    private void ShowSelection()
    {
        var entry = Selected;

        _edit.Enabled = entry is not null;
        _toggle.Enabled = entry is not null;
        _remove.Enabled = entry is not null;

        _toggle.Text = entry is { Enabled: false }
            ? Lang.T("Включи", "Turn on")
            : Lang.T("Изключи", "Turn off");
    }

    private ValueList? Selected =>
        _grid.CurrentRow is { Index: >= 0 and var index } && index < _rows.Count ? _rows[index] : null;

    // ------------------------------------------------------------- действия

    private void Create()
    {
        var companies = _workspace.Companies.List();

        if (companies.Count == 0)
        {
            MessageBox.Show(this,
                Lang.T("Първо заведете фирма в „Настройки“. Списъците се водят по фирми.",
                       "Add a company under “Settings” first. Lists are kept per company."),
                Lang.T("Списъци", "Lists"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new ValueListDialog(_service, companies, preferred: _scope.Current);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        // По един запис на фирма. Оттук нататък всеки живее сам за себе си.
        foreach (var company in dialog.Companies)
            _workspace.Lists.Save(company, dialog.SchemaKey, dialog.Report, dialog.Node, dialog.Column, dialog.Allowed);

        Refresh();
    }

    private void Edit()
    {
        if (Selected is not { } entry) return;

        using var dialog = new ValueListDialog(_service, _workspace.Companies.List(), entry);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _workspace.Lists.Save(
            entry.CompanyId, entry.SchemaKey, entry.Report, entry.Node, entry.Column, dialog.Allowed, entry.Enabled);

        Refresh();
    }

    /// <summary>
    /// Изключването е между промяната и изтриването.
    ///
    /// Понякога трябва да се въведе код извън обичайните — веднъж, заради един
    /// ред. Тогава списъкът се изключва, редът се попълва и се включва обратно,
    /// без отметките да се набират наново.
    /// </summary>
    private void Toggle()
    {
        if (Selected is not { } entry) return;

        var index = _grid.CurrentRow!.Index;

        _workspace.Lists.SetEnabled(entry.Id, !entry.Enabled);
        Refresh();

        if (index < _grid.Rows.Count) _grid.CurrentCell = _grid.Rows[index].Cells[0];
    }

    private void Remove()
    {
        if (Selected is not { } entry) return;

        var answer = MessageBox.Show(this,
            Lang.T($"Да се изтрие ли списъкът за „{FieldName(entry)}“ на {CompanyName(entry.CompanyId)}?\r\n\r\n" +
                   "Менютата отново ще предлагат всичко от схемата. Попълненото не се пипа.",
                   $"Delete the list for “{FieldName(entry)}” of {CompanyName(entry.CompanyId)}?\r\n\r\n" +
                   "The menus will offer everything from the schema again. What is filled in is not touched."),
            Lang.T("Изтриване", "Deleting"), MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes) return;

        _workspace.Lists.Remove(entry.Id);
        Refresh();
    }

    // ============================================================= „Защо липсва“

    private void BuildSearchView()
    {
        _searchView.Dock = DockStyle.Fill;
        _searchView.BackColor = Theme.Canvas;
        _searchView.Visible = false;

        Dress(_found);

        Column(_found, FoundValueColumn, Lang.T("Стойност", "Value"), 130);
        Column(_found, FoundFieldColumn, Lang.T("Поле", "Field"), 190);
        Column(_found, FoundCompanyColumn, Lang.T("Фирма", "Company"), 190);
        Column(_found, FoundScopeColumn, Lang.T("Спира я", "Blocked by"), 300);
        Column(_found, FoundStateColumn, Lang.T("Състояние", "State"), 190);

        var frame = new Card
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(1),
            Fill = Theme.Canvas,
            Outline = Theme.Border
        };

        frame.Controls.Add(_found);

        _foundSummary.Dock = DockStyle.Bottom;
        _foundSummary.Height = Theme.Base.Height + 12;
        _foundSummary.Font = Theme.Small;
        _foundSummary.ForeColor = Theme.InkFaint;

        _searchView.Controls.Add(frame);
        _searchView.Controls.Add(_foundSummary);
        _searchView.Controls.Add(BuildAsk());
    }

    private Control BuildAsk()
    {
        var host = new Panel { Dock = DockStyle.Top, Height = Theme.Scaled(132), BackColor = Theme.Canvas };

        // Многоредово нарочно: стойностите идват наведнъж — цял списък кодове от
        // писмо или от таблица. Проверка една по една е същата работа, разделена
        // на двайсет пъти.
        _values.Multiline = true;
        _values.ScrollBars = ScrollBars.Vertical;
        _values.BorderStyle = BorderStyle.FixedSingle;
        _values.Location = new Point(0, 26);
        _values.Size = new Size(Theme.Scaled(420), Theme.Scaled(76));
        _values.Font = Theme.Big;
        Theme.StyleInput(_values);

        var ask = Theme.PrimaryButton(Lang.T("Провери", "Check"));
        ask.Glyph = Theme.Glyph.Search;
        ask.Width = Theme.Scaled(160);
        ask.Location = new Point(_values.Right + Theme.Gap, 26);
        ask.Click += (_, _) => Explain();

        host.Controls.Add(Caption(Lang.T("Стойности — по една на ред", "Values — one per line"), _values.Left));
        host.Controls.Add(_values);
        host.Controls.Add(ask);

        host.Controls.Add(new Label
        {
            Location = new Point(0, 108),
            Size = new Size(Theme.Scaled(980), Theme.Scaled(22)),
            Font = Theme.Small,
            ForeColor = Theme.InkMuted,
            Text = Lang.T(
                "Въведете стойност, която не намирате в менюто — тук пише кой списък я спира.",
                "Enter a value you cannot find in a menu — this says which list is holding it back.")
        });

        return host;

        static Label Caption(string text, int left) => new()
        {
            Location = new Point(left, 4),
            Size = new Size(Theme.Scaled(400), Theme.Small.Height + 6),
            Font = Theme.Small,
            ForeColor = Theme.InkMuted,
            Text = text
        };
    }

    /// <summary>
    /// Отговаря за всяка въведена стойност — за фирмата, избрана в лентата.
    ///
    /// Редовете, в които стойността просто се предлага, също се показват: „няма
    /// нищо, което да я спира“ е точно толкова полезен отговор, колкото и името
    /// на списъка — човек е дошъл тук със съмнение и трябва да си тръгне без него.
    /// </summary>
    private void Explain()
    {
        var values = ValueSearch.Split(_values.Text);

        _found.Rows.Clear();

        if (values.Count == 0)
        {
            _foundSummary.Text = Lang.T("Въведете поне една стойност.", "Enter at least one value.");
            return;
        }

        List<Company> companies = _companies.FirstOrDefault(c => c.Id == _scope.Current) is { } chosen
            ? [chosen]
            : [];

        if (companies.Count == 0)
        {
            _foundSummary.Text = Lang.T(
                "Няма заведени фирми — без фирма няма и списък.",
                "There are no companies — without a company there is no list.");
            return;
        }

        var blocked = 0;

        foreach (var value in values)
        {
            foreach (var company in companies)
            {
                // По един ValueLimits на фирма и вид отчетност: списъците се
                // водят точно така, а видът е в самия отговор.
                foreach (var schema in _service.Schemas)
                {
                    var limits = _workspace.Lists.LimitsFor(company.Id, schema.Key);

                    foreach (var finding in ValueSearch.Explain([schema], limits, value))
                    {
                        if (finding.Verdict == ValueVerdict.Blocked) blocked++;

                        Add(finding, company.Name);
                    }
                }
            }

            // Непозната стойност не зависи от фирмата — казва се веднъж.
            if (ValueSearch.Explain(_service.Schemas, ValueLimits.None, value) is [{ Verdict: ValueVerdict.Unknown } miss])
                Add(miss, string.Empty);
        }

        _foundSummary.Text = blocked == 0
            ? Lang.T($"{values.Count} проверени — нищо не се спира от списък.",
                     $"{values.Count} checked — nothing is held back by a list.")
            : Lang.T($"{values.Count} проверени, {blocked} спрени от списък.",
                     $"{values.Count} checked, {blocked} held back by a list.");

        void Add(ValueFinding finding, string company)
        {
            var index = _found.Rows.Add(
                finding.Value,
                finding.Verdict == ValueVerdict.Unknown
                    ? Lang.T("(няма такова поле)", "(no such field)")
                    : ReportTitles.ColumnShort(finding.Column),
                company,
                finding.Verdict == ValueVerdict.Blocked ? Where(finding) : string.Empty,
                finding.Verdict switch
                {
                    ValueVerdict.Blocked => Lang.T("Спряна от списък", "Held back by a list"),
                    ValueVerdict.Offered => Lang.T("Предлага се", "Offered"),
                    _ => Lang.T("Няма я в схемата", "Not in the schema")
                });

            _found.Rows[index].DefaultCellStyle.ForeColor = finding.Verdict switch
            {
                ValueVerdict.Blocked => Theme.Error,
                ValueVerdict.Unknown => Theme.InkFaint,
                _ => Theme.Ink
            };
        }
    }

    /// <summary>Обхватът на списъка, който спира стойността — с думи.</summary>
    private string Where(ValueFinding finding)
    {
        if (finding.Report.Length == 0) return Lang.T("Списък за всички отчети", "A list for all reports");

        var schema = Schema(finding.SchemaKey);
        var report = schema?.FindReport(finding.Report);
        var name = report is not null ? ReportTitles.Short(report) : finding.Report;

        if (finding.Node.Length == 0) return name;

        var node = report?.SelfAndDescendants().FirstOrDefault(n => n.ElementName == finding.Node);

        return name + "  ▸  " + (node is not null ? ReportTitles.Table(node) : finding.Node);
    }

    /// <summary>Ctrl+F от главния прозорец — курсорът отива в търсенето.</summary>
    public override bool FocusSearch()
    {
        _search.Focus();
        return true;
    }

    // ------------------------------------------------------------- надписи

    private string CompanyName(long id) =>
        _workspace.Companies.Find(id)?.Name ?? Lang.T("(изтрита фирма)", "(deleted company)");

    private static string ShortKind(string key) => key switch
    {
        "year" => Lang.T("Годишни", "Annual"),
        "quarter" => Lang.T("Тримесечни", "Quarterly"),
        "book" => Lang.T("Отчетни книги", "Log books"),
        _ => key
    };

    /// <summary>
    /// Обхватът с думи. Отчет, който схемата вече не описва, се показва със
    /// собственото си име: измисленият надпис не отговаря на нищо в наредбата.
    /// </summary>
    private string ScopeName(ValueList entry)
    {
        if (entry.Report.Length == 0) return Lang.T("Всички отчети", "All reports");

        var report = Schema(entry.SchemaKey)?.FindReport(entry.Report);
        var name = report is not null ? ReportTitles.Short(report) : entry.Report;

        if (entry.Node.Length == 0) return name;

        var node = report?.SelfAndDescendants().FirstOrDefault(n => n.ElementName == entry.Node);

        return name + "  ▸  " + (node is not null ? ReportTitles.Table(node) : entry.Node);
    }

    private string FieldName(ValueList entry) =>
        Find(entry) is { } column ? ReportTitles.ColumnShort(column) : entry.Column;

    /// <summary>
    /// Първите няколко кода и колко са всичко. Кодовете са това, което човек
    /// помни — по тях се познава кой списък е този, без да се отваря.
    /// </summary>
    private string ValuesName(ValueList entry)
    {
        var total = Find(entry)?.Domain.Options.Count;
        var shown = string.Join(", ", entry.Allowed.Take(5));

        if (entry.Allowed.Count > 5) shown += ", …";

        return total is null
            ? Lang.T($"{entry.Allowed.Count}: {shown}", $"{entry.Allowed.Count}: {shown}")
            : Lang.T($"{entry.Allowed.Count} от {total}: {shown}", $"{entry.Allowed.Count} of {total}: {shown}");
    }

    private SchemaDefinition? Schema(string key) =>
        _service.Schemas.FirstOrDefault(s => s.Key == key);

    /// <summary>Полето зад записа. Може да го няма — схемата се мени без нас.</summary>
    private ColumnDef? Find(ValueList entry)
    {
        var schema = Schema(entry.SchemaKey);
        if (schema is null) return null;

        var nodes = entry.Report.Length > 0
            ? schema.FindReport(entry.Report)?.SelfAndDescendants() ?? []
            : schema.Reports.SelectMany(r => r.SelfAndDescendants());

        return nodes
            .Where(node => entry.Node.Length == 0 || node.ElementName == entry.Node)
            .SelectMany(node => node.Attributes)
            .FirstOrDefault(column => column.Name == entry.Column);
    }
}
