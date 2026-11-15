using RiosvImports.Core;
using RiosvImports.App.Controls;
using RiosvImports.Core.Excel;
using RiosvImports.Core.Ledger;
using RiosvImports.Core.Localization;
using RiosvImports.Core.Values;
using RiosvImports.Data;

namespace RiosvImports.App.Shell;

/// <summary>
/// Завареното: какво е стояло на площадката в мига, преди програмата да поеме
/// склада.
///
/// <b>Два склада са физически купчини</b> и само те имат заварено количество:
/// „Получен“ — суровината, както е дошла, и „Образуван“ — каквото е излязло от
/// третирането и чака да бъде предадено. Затова и прозорецът е на два
/// раздела: всеки със своя списък и своя сбор, вместо една таблица с колона
/// „склад“, която се чете ред по ред.
///
/// Другите два не са купчини и затова ги няма тук: „Третиран“ е временен
/// държател в рамките на периода, а „Предаден“ — регистър на излязлото.
///
/// <b>Началният месец стои горе и се мести със стрелки.</b> Завареното е
/// „каквото е стояло в деня преди него“; сменен месец мести и завареното
/// (<see cref="LedgerSetup.MoveStart"/>). Дотук месецът нямаше къде да се
/// избере и прозорецът само казваше, че липсва.
///
/// Много редове се внасят от Excel: шаблонът е с два листа, по един на склад,
/// и носи вече обявеното — тегли се, поправя се, връща се.
///
/// <b>Без клиент и без документ.</b> Завареното е купчина материал, чиято
/// история не се знае — затова се обявява само склад, код и колко. Измислен
/// произход е по-лош от липсващия: той изглежда като отговор.
/// </summary>
internal sealed class StartStockDialog : Dialog
{
    private const string CodeColumn = "__code";
    private const string AmountColumn = "__amount";
    private const string UsedColumn = "__used";

    private readonly Workspace _workspace;
    private readonly long _companyId;
    private readonly string _stagenum;
    private readonly Dictionary<string, string> _names;

    private readonly DataGridView _grid = new();
    private readonly Label _summary = new();
    private readonly Label _says = new();

    private readonly TabStrip _tabs = new();

    private readonly ComboBox _code = new();
    private readonly TextBox _amount = new();

    private readonly AppButton _add;
    private readonly AppButton _remove;

    private readonly AppButton _earlier = Theme.GhostButton(string.Empty, Theme.Glyph.ChevronBack);
    private readonly AppButton _later = Theme.GhostButton(string.Empty, Theme.Glyph.Chevron);
    private readonly Label _monthText = new();
    private readonly Label _eve = new();

    /// <summary>
    /// Показаният начален месец. Обявен ли е, той е обявеният; не е ли —
    /// предложение, което се записва едва с първото обявяване или внасяне.
    /// </summary>
    private LedgerPeriod _month;

    private List<OpeningStock> _openings = [];

    public StartStockDialog(
        ImportService service, Workspace workspace, long companyId, string stagenum)
    {
        _workspace = workspace;
        _companyId = companyId;
        _stagenum = stagenum;
        _names = EwcNames(service.GetSchema("book"));

        _month = workspace.Setup.Start(companyId) ?? workspace.Setup.Proposal(companyId);

        Text = Lang.T("Начални количества", "Opening quantities");
        Font = Theme.Base;
        BackColor = Theme.Canvas;
        FormBorderStyle = FormBorderStyle.Sizable;

        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;

        ClientSize = new Size(
            Math.Clamp(screen.Width - 160,
                Math.Min(Theme.Scaled(1000), screen.Width),
                Math.Max(Theme.Scaled(1240), Theme.Scaled(1000))),
            Math.Clamp(screen.Height - 160,
                Math.Min(Theme.Scaled(640), screen.Height),
                Theme.Scaled(820)));

        MinimumSize = new Size(
            Math.Min(Theme.Scaled(1000), screen.Width),
            Math.Min(Theme.Scaled(640), screen.Height));

        _add = Theme.PrimaryButton(Lang.T("Обяви", "Declare"));
        _remove = Theme.DangerButton(Lang.T("Махни", "Remove"), Theme.Glyph.Delete);

        // Пълнещият влиза пръв; от закачените отгоре последният застава
        // най-горе, от долните — най-долу.
        Controls.Add(BuildBody());
        Controls.Add(BuildEntry());
        Controls.Add(BuildTabs());
        Controls.Add(BuildMonth());
        Controls.Add(BuildTitle());
        Controls.Add(BuildButtons());

        Fill();

        Theme.Apply(this);
    }

    /// <summary>Дали нещо е обявено, махнато или преместено — складът се чете наново само тогава.</summary>
    public bool Changed { get; private set; }

    /// <summary>Складът на избрания раздел.</summary>
    private StoreKind Store => _tabs.SelectedIndex == 1 ? StoreKind.Formed : StoreKind.Received;

    private bool Saved => _workspace.Setup.Start(_companyId) is not null;

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

        host.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = Lang.T("Начални количества", "Opening quantities"),
            Font = Theme.Title,
            ForeColor = Theme.Ink
        });

        host.Controls.Add(new Label
        {
            Dock = DockStyle.Bottom,
            Height = Theme.Scaled(48),
            Text = Lang.T(
                "Какво е стояло на площадката в деня преди началния месец. Месецът се мести със " +
                "стрелките и завареното върви с него. Много редове се внасят от Excel — шаблонът " +
                "носи вече обявеното.",
                "What stood on the site the day before the starting month. The month moves with the " +
                "arrows and the opening stock moves with it. Many rows are imported from Excel — the " +
                "template carries what is already declared."),
            Font = Theme.Base,
            ForeColor = Theme.InkMuted
        });

        return host;
    }

    /// <summary>
    /// Началният месец — със стрелки, както месецът в „Месец“.
    ///
    /// В синя карта, както датата в работните прозорци: сгрешеният месец не
    /// се вижда от само себе си — изглежда точно като верен.
    /// </summary>
    private Control BuildMonth()
    {
        var host = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(64),
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, 0, Theme.GapLarge, Theme.GapSmall)
        };

        var card = new Card
        {
            Dock = DockStyle.Fill,
            Fill = Theme.AccentLight,
            Outline = Theme.AccentSoft,
            Stripe = Theme.Accent,
            Padding = new Padding(Theme.Gap + 4, 8, Theme.Gap, 8)
        };

        var caption = new Label
        {
            Dock = DockStyle.Left,
            Width = Theme.Scaled(140),
            Text = Lang.T("Начален месец", "Starting month"),
            Font = Theme.Bold,
            ForeColor = Theme.Accent,
            BackColor = Theme.AccentLight,
            TextAlign = ContentAlignment.MiddleLeft
        };

        foreach (var (button, back) in new[] { (_earlier, true), (_later, false) })
        {
            button.Dock = DockStyle.Left;
            button.Width = Theme.Scaled(40);
            button.AccessibleName = back
                ? Lang.T("Предишен месец", "Previous month")
                : Lang.T("Следващ месец", "Next month");

            button.Click += (_, _) => Step(back ? -1 : 1);

            new ToolTip().SetToolTip(button, back
                ? Lang.T("Началото месец по-рано — завареното се мести с него.",
                         "Start a month earlier — the opening stock moves with it.")
                : Lang.T("Началото месец по-късно — завареното се мести с него.",
                         "Start a month later — the opening stock moves with it."));
        }

        _monthText.Dock = DockStyle.Left;
        _monthText.Width = Theme.Scaled(190);
        _monthText.Font = Theme.Title;
        _monthText.ForeColor = Theme.Ink;
        _monthText.BackColor = Theme.AccentLight;
        _monthText.TextAlign = ContentAlignment.MiddleCenter;

        _eve.Dock = DockStyle.Fill;
        _eve.Font = Theme.Small;
        _eve.ForeColor = Theme.InkMuted;
        _eve.BackColor = Theme.AccentLight;
        _eve.TextAlign = ContentAlignment.MiddleLeft;
        _eve.Padding = new Padding(Theme.Gap, 0, 0, 0);

        // Пълнещият влиза пръв; закачените отляво се редят в обратен ред.
        card.Controls.Add(_eve);
        card.Controls.Add(_later);
        card.Controls.Add(_monthText);
        card.Controls.Add(_earlier);
        card.Controls.Add(caption);

        host.Controls.Add(card);

        return host;
    }

    /// <summary>Двата склада — по раздел. Значето на всеки казва колко реда има.</summary>
    private Control BuildTabs()
    {
        var host = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(40),
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, 0, Theme.GapLarge, 0)
        };

        // Разделите са само заглавия: списъкът под тях е един и се пълни по
        // избрания склад, за да не живеят две еднакви таблици поотделно.
        _tabs.Dock = DockStyle.Fill;
        _tabs.TabPages.Add(new TabPage(Moves.Short(StoreKind.Received)));
        _tabs.TabPages.Add(new TabPage(Moves.Short(StoreKind.Formed)));
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            ShowStore();
            Say(string.Empty, Theme.InkMuted);
        };

        host.Controls.Add(_tabs);

        return host;
    }

    private Control BuildEntry()
    {
        var host = new Panel
        {
            Dock = DockStyle.Bottom,
            BackColor = Theme.Canvas,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(Theme.GapLarge, Theme.GapSmall, Theme.GapLarge, 0)
        };

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = Theme.Canvas
        };

        _code.DropDownStyle = ComboBoxStyle.DropDown;
        _code.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _code.AutoCompleteSource = AutoCompleteSource.ListItems;

        foreach (var (code, name) in _names.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            _code.Items.Add($"{code} — {name}");

        Theme.StyleInput(_code);

        _amount.TextAlign = HorizontalAlignment.Right;
        Theme.StyleInput(_amount);

        // Enter в количеството обявява: при двайсет кода ръката не бива да
        // ходи до копчето след всеки.
        _amount.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;

            e.Handled = true;
            e.SuppressKeyPress = true;
            Add();
        };

        // Складът е разделът: обявеното отива там, където човек гледа.
        bar.Controls.Add(Cell(_code, Lang.T("Код на отпадъка", "Waste code"), 420));
        bar.Controls.Add(Cell(_amount, Lang.T("Количество, т", "Quantity, t"), 160));

        _add.Height = Theme.Scaled(34);
        _add.Dock = DockStyle.Top;
        _add.Click += (_, _) => Add();

        bar.Controls.Add(Cell(_add, string.Empty, 130));

        _says.Dock = DockStyle.Top;
        _says.Height = Theme.Base.Height + Theme.Scaled(8);
        _says.Font = Theme.Small;
        _says.ForeColor = Theme.InkMuted;
        _says.AutoEllipsis = true;

        host.Controls.Add(_says);
        host.Controls.Add(bar);

        return host;
    }

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

        _summary.Dock = DockStyle.Bottom;
        _summary.Height = Theme.Base.Height + Theme.Scaled(10);
        _summary.Font = Theme.Bold;
        _summary.ForeColor = Theme.InkMuted;

        _grid.Dock = DockStyle.Fill;
        card.Controls.Add(_grid);

        host.Controls.Add(card);
        host.Controls.Add(_summary);

        return host;
    }

    private void BuildGrid()
    {
        _grid.Font = Theme.Base;
        _grid.BackgroundColor = Theme.Canvas;
        _grid.BorderStyle = BorderStyle.None;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
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
        _grid.DefaultCellStyle.SelectionBackColor = Theme.AccentLight;
        _grid.DefaultCellStyle.SelectionForeColor = Theme.Ink;
        _grid.DefaultCellStyle.Padding = new Padding(6, 4, 6, 4);
        _grid.AlternatingRowsDefaultCellStyle.BackColor = Theme.Surface;
        _grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = Theme.AccentLight;
        _grid.AlternatingRowsDefaultCellStyle.SelectionForeColor = Theme.Ink;
        _grid.GridColor = Theme.Border;

        var line = Math.Max(1, _grid.Font.Height);
        _grid.RowTemplate.Height = line + 12;
        _grid.ColumnHeadersHeight = line + 20;

        Column(CodeColumn, Lang.T("Код", "Code"), 300, fill: true);
        Column(AmountColumn, Lang.T("Обявено, т", "Declared, t"), 130, right: true);
        Column(UsedColumn, Lang.T("Останало, т", "Left, t"), 130, right: true);

        _grid.SelectionChanged += (_, _) => Allow();
    }

    private void Column(string name, string header, int width, bool fill = false, bool right = false)
    {
        var column = new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            Width = Theme.Scaled(width),
            SortMode = DataGridViewColumnSortMode.NotSortable
        };

        if (fill)
        {
            column.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            column.MinimumWidth = Theme.Scaled(200);
        }

        if (right)
        {
            column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
        }

        _grid.Columns.Add(column);
    }

    private Control BuildButtons()
    {
        var height = Theme.Base.Height + Theme.Scaled(16);

        var host = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = Theme.Base.Height + Theme.Scaled(30),
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, 0, Theme.GapLarge, Theme.Gap)
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

        var close = Theme.SecondaryButton(Lang.T("Затвори", "Close"));
        close.Height = height;
        close.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        close.Click += (_, _) => DialogResult = DialogResult.OK;

        var import = Theme.GhostButton(Lang.T("Внеси от Excel", "Import from Excel"), Theme.Glyph.Import);
        import.Height = height;
        import.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        import.Click += (_, _) => ImportFile();

        var template = Theme.GhostButton(Lang.T("Шаблон за Excel", "Excel template"), Theme.Glyph.Excel);
        template.Height = height;
        template.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        template.Click += (_, _) => SaveTemplate();

        tools.Controls.Add(close.FitWidth(16));
        tools.Controls.Add(import.FitWidth(16));
        tools.Controls.Add(template.FitWidth(16));

        _remove.Dock = DockStyle.Left;
        _remove.Height = height;
        _remove.FitWidth(16);
        _remove.Enabled = false;
        _remove.Click += (_, _) => Remove();

        host.Controls.Add(tools);
        host.Controls.Add(_remove);

        CancelButton = close;

        return host;
    }

    // ------------------------------------------------------------- пълнене

    /// <summary>Чете обявеното наново и показва раздела и месеца.</summary>
    private void Fill()
    {
        _openings = [.. _workspace.Setup.Openings(_companyId, _stagenum)];

        _tabs.Counts[0] = _openings.Count(o => o.Store == StoreKind.Received);
        _tabs.Counts[1] = _openings.Count(o => o.Store == StoreKind.Formed);
        _tabs.Invalidate();

        ShowMonth();
        ShowStore();
    }

    /// <summary>Пълни таблицата с обявеното в избрания склад.</summary>
    private void ShowStore()
    {
        _grid.Rows.Clear();

        foreach (var opening in _openings.Where(o => o.Store == Store))
        {
            var index = _grid.Rows.Add();
            var row = _grid.Rows[index];

            row.Cells[CodeColumn].Value = Describe(opening.Batch.Ewc);
            row.Cells[AmountColumn].Value = SplitMath.Amount(opening.Amount);
            row.Cells[UsedColumn].Value = SplitMath.Amount(opening.Available);

            // Изчерпаното не се маха безнаказано и това си личи предварително:
            // от него вече е тръгнала истинска работа.
            if (opening.InUse) row.Cells[UsedColumn].Style.ForeColor = Theme.Warn;

            row.Tag = opening;
        }

        Weigh();
        Allow();
    }

    /// <summary>Месецът и какво значи той: в кой ден стои завареното.</summary>
    private void ShowMonth()
    {
        _monthText.Text = _month.Title();

        var eve = LedgerSetup.Eve(_month).ToString(ValueConverter.BulgarianDateFormat);

        if (Saved)
        {
            _eve.Text = Lang.T($"Завареното стои към {eve}.", $"The opening stock stands as of {eve}.");
            _eve.ForeColor = Theme.InkMuted;
            _monthText.ForeColor = Theme.Ink;
        }
        else
        {
            _eve.Text = Lang.T(
                $"Още не е обявен — предложен е. Записва се с първото обявяване или внасяне (към {eve}).",
                $"Not declared yet — this is a proposal. It is saved with the first declaration or import (as of {eve}).");
            _eve.ForeColor = Theme.Warn;
            _monthText.ForeColor = Theme.InkMuted;
        }
    }

    private void Weigh()
    {
        var mine = _openings.Where(o => o.Store == Store).ToList();
        var name = Moves.Short(Store);

        if (mine.Count == 0)
        {
            _summary.Text = Lang.T(
                $"В „{name}“ нищо не е обявено. Ако към {LedgerSetup.Eve(_month).ToString(ValueConverter.BulgarianDateFormat)} " +
                "е имало наличност, тя ще липсва в сметките.",
                $"Nothing is declared in “{name}”. If anything stood there on " +
                $"{LedgerSetup.Eve(_month).ToString(ValueConverter.BulgarianDateFormat)}, it will be missing.");

            _summary.ForeColor = Theme.InkFaint;
            return;
        }

        var other = _openings.Where(o => o.Store != Store).Sum(o => o.Amount);
        var otherName = Moves.Short(Store == StoreKind.Formed ? StoreKind.Received : StoreKind.Formed);

        _summary.Text = Lang.T(
            $"„{name}“: {SplitMath.Amount(mine.Sum(o => o.Amount))} т в {mine.Count} реда · " +
            $"„{otherName}“: {SplitMath.Amount(other)} т",
            $"“{name}”: {SplitMath.Amount(mine.Sum(o => o.Amount))} t in {mine.Count} rows · " +
            $"“{otherName}”: {SplitMath.Amount(other)} t");

        _summary.ForeColor = Theme.Ink;
    }

    private OpeningStock? Selected() => _grid.CurrentRow?.Tag as OpeningStock;

    private void Allow() => _remove.Enabled = Selected() is not null;

    private void Say(string text, Color colour)
    {
        _says.Text = text;
        _says.ForeColor = colour;
    }

    // ------------------------------------------------------------- месец

    /// <summary>
    /// Мести началния месец с една стъпка.
    ///
    /// Докато месецът не е обявен, стрелките местят само предложението: нищо
    /// още не стои на него. Обявен ли е, местят и завареното — освен ако по
    /// пътя има истинско движение, което би останало преди него.
    /// </summary>
    private void Step(int by)
    {
        var to = by < 0 ? _month.Previous : _month.Next;

        if (!Saved)
        {
            _month = to;
            ShowMonth();
            Weigh();

            return;
        }

        if (!_workspace.Setup.MoveStart(_companyId, to))
        {
            var first = _workspace.Setup.Before(_companyId, to);

            Say(Lang.T(
                    $"Има движение от {first?.ToString(ValueConverter.BulgarianDateFormat)} — началото не може да " +
                    $"е след него, иначе то би черпило от склад, който тогава още не е обявен.",
                    $"There is a movement dated {first?.ToString(ValueConverter.BulgarianDateFormat)} — the start " +
                    $"cannot be after it, or it would draw from stock not yet declared."),
                Theme.Warn);

            return;
        }

        _month = to;
        Changed = true;

        Say(Lang.T($"Началото е {to.Title()}; завареното е преместено към " +
                   $"{LedgerSetup.Eve(to).ToString(ValueConverter.BulgarianDateFormat)}.",
                   $"The start is {to.Title()}; the opening stock moved to " +
                   $"{LedgerSetup.Eve(to).ToString(ValueConverter.BulgarianDateFormat)}."),
            Theme.Ok);

        Fill();
    }

    /// <summary>
    /// Записва показания месец, ако още не е обявен — първото обявяване или
    /// внасяне го потвърждава. Лъжа, ако преди него вече има движения.
    /// </summary>
    private bool Settle()
    {
        if (Saved) return true;

        if (_workspace.Setup.Before(_companyId, _month) is { } first)
        {
            Say(Lang.T(
                    $"Има движение от {first.ToString(ValueConverter.BulgarianDateFormat)} — изберете начален " +
                    "месец не по-късно от него.",
                    $"There is a movement dated {first.ToString(ValueConverter.BulgarianDateFormat)} — pick a " +
                    "starting month no later than that."),
                Theme.Warn);

            return false;
        }

        _workspace.Setup.SetStart(_companyId, _month);

        return true;
    }

    // ------------------------------------------------------------- действия

    /// <summary>Обявява едно заварено количество в склада на избрания раздел.</summary>
    private void Add()
    {
        var code = Code(_code.Text);

        if (code.Length == 0)
        {
            Say(Lang.T("Изберете код на отпадъка.", "Pick a waste code."), Theme.Warn);
            return;
        }

        if (!SplitMath.TryRead(_amount.Text, out var amount) || amount <= 0)
        {
            Say(Lang.T("Количеството трябва да е положително число.",
                       "The quantity must be a positive number."), Theme.Warn);

            return;
        }

        if (!Settle()) return;

        var store = Store;

        _workspace.Setup.AddOpening(_companyId, _month, new OpeningEntry(
            store, code, SplitMath.Round(amount), Stagenum: _stagenum));

        Changed = true;

        Say(Lang.T(
                $"Обявени {SplitMath.Amount(amount)} т по {code} в склад „{Moves.Short(store)}“ " +
                $"към {LedgerSetup.Eve(_month).ToString(ValueConverter.BulgarianDateFormat)}.",
                $"{SplitMath.Amount(amount)} t of {code} declared in the “{Moves.Short(store)}” store " +
                $"as of {LedgerSetup.Eve(_month).ToString(ValueConverter.BulgarianDateFormat)}."),
            Theme.Ok);

        _amount.Text = string.Empty;
        _code.Focus();

        Fill();
    }

    /// <summary>
    /// Маха обявено количество.
    ///
    /// Правилото кога изобщо може живее в <see cref="LedgerSetup"/>: от което
    /// вече е черпено, не се маха — махането щеше да отнесе произхода на
    /// истинска работа. Тук се казва само какво е станало.
    /// </summary>
    private void Remove()
    {
        if (Selected() is not { } opening) return;

        var said = Lang.T(
            $"{SplitMath.Amount(opening.Amount)} т по {opening.Batch.Ewc} в склад " +
            $"„{Moves.Short(opening.Store)}“.\n\nДа се махне ли обявеното количество?",
            $"{SplitMath.Amount(opening.Amount)} t of {opening.Batch.Ewc} in the " +
            $"“{Moves.Short(opening.Store)}” store.\n\nRemove the declared quantity?");

        if (Visible && MessageBox.Show(this, said, Lang.T("Махане", "Removal"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        if (!_workspace.Setup.RemoveOpening(_companyId, opening.Batch.Id))
        {
            Say(Lang.T(
                    "От това количество вече е черпено — то е начало на верига, а не число в " +
                    "настройка. Поправете движението, което го е изразходвало.",
                    "This quantity has already been drawn from — it is the start of a chain, not a " +
                    "number in a setting. Fix the movement that consumed it."),
                Theme.Warn);

            return;
        }

        Changed = true;

        Say(Lang.T("Обявеното количество е махнато.", "The declared quantity is gone."), Theme.Ok);

        Fill();
    }

    // ------------------------------------------------------------- Excel

    /// <summary>Записва шаблона с вече обявеното в него — по лист на склад.</summary>
    private void SaveTemplate()
    {
        using var save = new SaveFileDialog
        {
            Filter = Lang.T("Работна книга на Excel (*.xlsx)|*.xlsx", "Excel workbook (*.xlsx)|*.xlsx"),
            FileName = Lang.T($"Начални количества {_month}.xlsx", $"Opening stock {_month}.xlsx"),
            InitialDirectory = _workspace.Settings.Get(SettingsStore.Keys.LastFolder, string.Empty)
        };

        if (save.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            WriteTemplate(save.FileName);

            _workspace.Settings.Set(SettingsStore.Keys.LastFolder, Path.GetDirectoryName(save.FileName)!);

            if (MessageBox.Show(this,
                    Lang.T("Шаблонът е готов. Да се отвори ли сега?", "The template is ready. Open it now?"),
                    Lang.T("Готово", "Done"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(save.FileName) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message,
                Lang.T("Шаблонът не беше създаден", "The template was not created"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Самият запис на шаблона — без питане, за да минава и тестът.</summary>
    internal void WriteTemplate(string path) =>
        StockOpeningWorkbook.Save(
            path,
            LedgerSetup.Eve(_month),
            [.. _names.Keys.OrderBy(k => k, StringComparer.Ordinal)],
            _openings.Select(o => new StockOpeningRow(0, o.Store, o.Batch.Ewc, o.Amount)));

    /// <summary>Пита за файл, казва какво ще стане и внася.</summary>
    private void ImportFile()
    {
        using var open = new OpenFileDialog
        {
            Filter = Lang.T("Работна книга на Excel (*.xlsx)|*.xlsx", "Excel workbook (*.xlsx)|*.xlsx"),
            InitialDirectory = _workspace.Settings.Get(SettingsStore.Keys.LastFolder, string.Empty)
        };

        if (open.ShowDialog(this) != DialogResult.OK) return;

        StockOpeningFile file;

        try
        {
            // Само кодове от номенклатурата: сбъркана цифра дава съществуващ на
            // вид код, който НИСО после отхвърля.
            file = StockOpeningWorkbook.Read(open.FileName, _names.Keys.ToHashSet(StringComparer.Ordinal));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message,
                Lang.T("Файлът не беше прочетен", "The file was not read"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _workspace.Settings.Set(SettingsStore.Keys.LastFolder, Path.GetDirectoryName(open.FileName)!);

        if (file.Rows.Count == 0)
        {
            MessageBox.Show(this,
                Lang.T("Във файла няма нито един годен ред.", "The file has no usable row.") +
                Skipped(file.Problems),
                Lang.T("Нищо за внасяне", "Nothing to import"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var received = file.Rows.Where(r => r.Store == StoreKind.Received).ToList();
        var formed = file.Rows.Where(r => r.Store == StoreKind.Formed).ToList();

        var ask = Lang.T(
            $"Внасянето ЗАМЕСТВА обявеното досега с файла:\n\n" +
            $"„{Moves.Short(StoreKind.Received)}“: {received.Count} реда, {SplitMath.Amount(received.Sum(r => r.Amount))} т\n" +
            $"„{Moves.Short(StoreKind.Formed)}“: {formed.Count} реда, {SplitMath.Amount(formed.Sum(r => r.Amount))} т\n\n" +
            $"Към {LedgerSetup.Eve(_month).ToString(ValueConverter.BulgarianDateFormat)}. Продължаване?",
            $"The import REPLACES what was declared so far with the file:\n\n" +
            $"“{Moves.Short(StoreKind.Received)}”: {received.Count} rows, {SplitMath.Amount(received.Sum(r => r.Amount))} t\n" +
            $"“{Moves.Short(StoreKind.Formed)}”: {formed.Count} rows, {SplitMath.Amount(formed.Sum(r => r.Amount))} t\n\n" +
            $"As of {LedgerSetup.Eve(_month).ToString(ValueConverter.BulgarianDateFormat)}. Continue?");

        if (MessageBox.Show(this, ask, Lang.T("Внасяне", "Import"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        Import(file);

        if (file.Problems.Count > 0)
            MessageBox.Show(this, _says.Text + Skipped(file.Problems),
                Lang.T("Внесено, с пропуснати редове", "Imported, with rows skipped"),
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private static string Skipped(IReadOnlyList<string> problems) =>
        problems.Count == 0
            ? string.Empty
            : Environment.NewLine + Environment.NewLine +
              Lang.T($"Пропуснати редове: {problems.Count}", $"Rows skipped: {problems.Count}") +
              Environment.NewLine + string.Join(Environment.NewLine, problems.Take(10)) +
              (problems.Count > 10 ? Environment.NewLine + "…" : string.Empty);

    /// <summary>
    /// Внася прочетения файл: заменя обявеното в двата склада.
    ///
    /// От което вече е черпено, остава — то не е настройка, а начало на
    /// верига (<see cref="LedgerSetup.ReplaceOpenings"/>). Същият ред във
    /// файла — а шаблонът го носи, защото носи всичко обявено — не се обявява
    /// втори път: иначе всяко връщане на шаблона би го удвоявало.
    ///
    /// Връща колко реда са обявени.
    /// </summary>
    internal int Import(StockOpeningFile file)
    {
        if (!Settle()) return 0;

        // Два реда с един и същ код в един склад са една купчина.
        var entries = file.Rows
            .GroupBy(r => (r.Store, Ewc: ValueConverter.NormalizeCode(r.Ewc)))
            .Select(g => new OpeningEntry(g.Key.Store, g.Key.Ewc, SplitMath.Round(g.Sum(r => r.Amount)),
                Stagenum: _stagenum))
            .ToList();

        // Наново от базата: докато прозорецът стои отворен, от обявеното може
        // да е черпено другаде, а показаният списък помни отпреди това.
        foreach (var kept in _workspace.Setup.Openings(_companyId, _stagenum).Where(o => o.InUse))
        {
            var twin = entries.FindIndex(e =>
                e.Store == kept.Store &&
                string.Equals(ValueConverter.NormalizeCode(e.Ewc), ValueConverter.NormalizeCode(kept.Batch.Ewc),
                    StringComparison.Ordinal) &&
                e.Amount == kept.Amount);

            if (twin >= 0) entries.RemoveAt(twin);
        }

        var left = _workspace.Setup.ReplaceOpenings(_companyId, _month, entries, _stagenum);

        Changed = true;

        var said = Lang.T($"Внесени са {entries.Count} реда.", $"{entries.Count} rows imported.");

        if (left.Count > 0)
            said += Lang.T(
                $" {left.Count} реда, от които вече е черпено, остават както са — поправят се ръчно.",
                $" {left.Count} rows already drawn from stay as they are — fix them by hand.");

        if (file.Problems.Count > 0)
            said += Lang.T($" Пропуснати: {file.Problems.Count}.", $" Skipped: {file.Problems.Count}.");

        Say(said, file.Problems.Count > 0 || left.Count > 0 ? Theme.Warn : Theme.Ok);

        Fill();

        return entries.Count;
    }

    // ------------------------------------------------------------- помощни

    private string Describe(string ewc) =>
        _names.TryGetValue(ewc, out var name) ? $"{ewc} — {name}" : ewc;

    private static string Code(string? display)
    {
        var text = display?.Trim() ?? string.Empty;
        var dash = text.IndexOf(" — ", StringComparison.Ordinal);

        return ValueConverter.NormalizeCode(dash > 0 ? text[..dash] : text);
    }

    /// <summary>Номенклатурата на кодовете — от схемата, не от списък в кода.</summary>
    private static Dictionary<string, string> EwcNames(Core.Schema.SchemaDefinition schema)
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

    // ------------------------------------------------------------- за теста

    /// <summary>Всички обявени редове — и в двата склада.</summary>
    internal int RowCount => _openings.Count;

    /// <summary>Редовете в показания раздел.</summary>
    internal int ShownCount => _grid.Rows.Count;

    internal string SummaryText => _summary.Text;

    internal string SaysText => _says.Text;

    internal string MonthText => _monthText.Text;

    internal string EveText => _eve.Text;

    internal LedgerPeriod Month => _month;

    internal string AmountAt(int row) => _grid.Rows[row].Cells[AmountColumn].Value?.ToString() ?? string.Empty;

    /// <summary>Отваря раздела на склада, както щракване по него.</summary>
    internal void UseTab(StoreKind store) => _tabs.SelectedIndex = store == StoreKind.Formed ? 1 : 0;

    /// <summary>Натиска стрелката за месеца.</summary>
    internal void StepMonth(int by) => Step(by);

    /// <summary>Обявява количество в склада си, както копчето в неговия раздел.</summary>
    internal void Declare(StoreKind store, string ewc, decimal amount)
    {
        UseTab(store);

        _code.Text = ewc;
        _amount.Text = SplitMath.Amount(amount);

        Add();
    }

    internal void Pick(int row)
    {
        if (row < 0 || row >= _grid.Rows.Count) return;

        _grid.CurrentCell = _grid.Rows[row].Cells[0];
        Allow();
    }

    /// <summary>Маха избраното, както копчето — питането се прескача на скрит прозорец.</summary>
    internal void RemoveNow() => Remove();

    internal bool CanRemove => _remove.Enabled;
}
