using RiosvImports.App.Controls;
using RiosvImports.Core;
using RiosvImports.Core.Ledger;
using RiosvImports.Core.Localization;
using RiosvImports.Core.Schema;
using RiosvImports.Core.Values;
using RiosvImports.Data;

namespace RiosvImports.App.Shell;

/// <summary>Каквото прозорчето връща: движението, документът и дяловете му.</summary>
internal sealed record MoveEntry(
    MoveKind Kind,
    DateOnly Date,
    string Ewc,
    decimal Amount,
    Dictionary<string, string> Fields,
    string Document,
    string Note,
    IReadOnlyList<Draw> Draws,
    string EwcOut = "",
    string Contract = "");

/// <summary>
/// Въвеждането на едно движение.
///
/// Едно прозорче за четирите вида, защото трите неща, които човек пише всеки
/// път — дата, код, количество — са едни и същи, а различното идва от схемата
/// и се сглобява само. Четири почти еднакви прозореца щяха да се разминат на
/// петата поправка.
///
/// Редът на попълване е нарочен: <b>дата, после код, после количество.</b>
/// Наличността е към дата — преди тя да е избрана, не се знае от какво изобщо
/// може да се черпи, и предложеното разпределение би било по вчерашния склад.
///
/// Полетата извън тези три не се изброяват тук. Те се четат от възела на
/// схемата (правило 1): добавена от НИСО колона се появява сама, а написана на
/// ръка би остаряла мълчаливо.
/// </summary>
internal sealed class MoveDialog : Dialog
{
    private const string TakenColumn = "__taken";
    private const string BatchColumn = "__batch";
    private const string StockColumn = "__stock";

    private readonly Workspace _workspace;
    private readonly long _companyId;
    private readonly string _stagenum;
    private readonly MoveKind _kind;
    private readonly long? _moveId;

    private readonly ReportNode? _node;
    private readonly Dictionary<string, Control> _inputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _names;

    private readonly DateTimePicker _date = new();
    private readonly ComboBox _code = new();
    private readonly ComboBox _outCode = new();
    private readonly TextBox _amount = new();
    private readonly TextBox _document = new();
    private readonly TextBox _note = new();
    private readonly ComboBox _contract = new();

    private readonly DataGridView _draws = new();
    private readonly Label _split = new();
    private readonly Card _allocation = new();

    private List<BatchStock> _stock = [];
    private AppButton? _save;
    private bool _filling;

    public MoveDialog(
        ImportService service,
        Workspace workspace,
        long companyId,
        string stagenum,
        MoveKind kind,
        LedgerPeriod period,
        long? moveId = null)
    {
        _workspace = workspace;
        _companyId = companyId;
        _stagenum = stagenum;
        _kind = kind;
        _moveId = moveId;

        var schema = service.GetSchema("book");
        _node = schema.FindReport(BookNodes.Report)?
            .SelfAndDescendants()
            .FirstOrDefault(n => n.ElementName == Moves.Node(kind));

        _names = EwcNames(schema);

        Text = Title(kind);
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.Sizable;
        ClientSize = new Size(Theme.Scaled(780), Theme.Scaled(640));
        MinimumSize = new Size(Theme.Scaled(700), Theme.Scaled(520));
        BackColor = Theme.Canvas;
        Font = Theme.Base;

        // Полетата са тези, които растат: те идват от схемата и при приемане са
        // десетина. Затова ТЕ поемат остатъка и се превъртат, а копчетата и
        // разпределението са закачени за ръбовете и не могат да бъдат избутани
        // извън прозореца.
        //
        // Първата подредба беше обратната — полетата с AutoSize отгоре — и при
        // приемане израстваха на 600 пиксела в прозорец от 540: копчето
        // „Записване“ оставаше под долния ръб и прозорецът изглеждаше без изход.
        Controls.Add(BuildFields());
        Controls.Add(BuildAllocation());
        Controls.Add(BuildButtons());
        Controls.Add(BuildTitle());

        Seed(period);
        Theme.Apply(this);
    }

    /// <summary>Готовото движение. Има смисъл само при <see cref="DialogResult.OK"/>.</summary>
    public MoveEntry? Result { get; private set; }

    /// <summary>
    /// Дали копчето за записване се побира цяло в прозореца.
    ///
    /// Проверява се, защото веднъж не се побираше: полетата идват от схемата и
    /// при приемане пораснаха над височината на прозореца, а закаченото за
    /// долния ръб копче остана под него. Такъв прозорец изглежда като прозорец
    /// без изход и човек го затваря, вместо да записва.
    ///
    /// Мери се в координати на прозореца, а не на екрана, и НЕ пита за
    /// <see cref="Control.Visible"/>: непоказан прозорец обявява всичките си
    /// деца за невидими и стои на случайно място по екрана. Тестът тук не
    /// показва прозорци — виж бележката в АРХИТЕКТУРА.md — така че единственото
    /// смислено мерило е подредбата, а тя е налична веднага след сглобяването.
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

    /// <summary>
    /// Дали нещо се рисува ВЪРХУ това място.
    ///
    /// Точно това беше счупеното, а не положението: копчето си стоеше където
    /// трябва, но полетата отгоре бяха с AutoSize и пораснаха на 600 пиксела в
    /// прозорец от 540. Закачената отгоре контрола не се реже до отреденото ѝ
    /// място — тя просто продължава надолу, а понеже е добавена по-рано, се
    /// рисува ОТГОРЕ и покрива лентата с копчетата.
    ///
    /// Проверка само за положението би минала и при счупеното. Затова се пита и
    /// за покриването — иначе тестът мери с това, което проверява (правило 6).
    /// </summary>
    private bool Covered(Rectangle place)
    {
        var bar = _save?.Parent;

        while (bar is not null && bar.Parent != this) bar = bar.Parent;
        if (bar is null) return false;

        var above = Controls.IndexOf(bar);

        // По-малък индекс значи по-горе в реда на рисуване.
        for (var i = 0; i < above; i++)
        {
            var sibling = Controls[i];
            if (sibling.Bounds.IntersectsWith(place)) return true;
        }

        return false;
    }

    /// <summary>Къде стои контрола спрямо самия прозорец.</summary>
    private Rectangle Place(Control control)
    {
        var bounds = control.Bounds;

        for (var parent = control.Parent; parent is not null && parent != this; parent = parent.Parent)
            bounds.Offset(parent.Bounds.Location);

        return bounds;
    }

    /// <summary>Къде точно стои копчето — за съобщението, когато проверката падне.</summary>
    internal string SaveTrace => _save is null
        ? "няма копче"
        : $"копче={Place(_save)} прозорец={ClientRectangle}";

    /// <summary>Кодът за отчета, както стои в полето — за теста.</summary>
    internal string OutCodeText => _outCode.Text;

    /// <summary>Колко договора предлага полето — извън предаването никакви.</summary>
    internal int ContractsOffered => _contract.Items.Count;

    internal string ContractText => _contract.Text;

    /// <summary>Дали видът черпи от склад — тогава има какво да се разпределя.</summary>
    private bool Draws => Moves.Draws(_kind);

    /// <summary>Дали произходът се наследява от третиранията на месеца.</summary>
    private bool Inherits => _kind == MoveKind.Generated;

    // ------------------------------------------------------------- устройство

    private Control BuildTitle()
    {
        var host = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(62),
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, Theme.Gap, Theme.GapLarge, 0)
        };

        host.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = Title(_kind),
            Font = Theme.Title,
            ForeColor = Theme.Ink
        });

        return host;
    }

    private Control BuildFields()
    {
        // Превъртаща се рамка около самите полета: при приемане те са десетина
        // и на нисък екран не се побират наведнъж.
        var frame = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Theme.Canvas
        };

        var host = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, 0, Theme.GapLarge, Theme.Gap)
        };

        _date.Format = DateTimePickerFormat.Short;
        _date.ValueChanged += (_, _) => Recalculate();

        _code.DropDownStyle = ComboBoxStyle.DropDown;
        _code.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _code.AutoCompleteSource = AutoCompleteSource.ListItems;
        _code.SelectedIndexChanged += (_, _) => Recalculate();
        _code.Leave += (_, _) => Recalculate();

        _amount.TextAlign = HorizontalAlignment.Right;
        _amount.TextChanged += (_, _) => Recalculate();

        host.Controls.Add(Row(Lang.T("Дата", "Date"), _date));
        host.Controls.Add(Row(Lang.T("Код на отпадъка", "Waste code"), _code));
        host.Controls.Add(Row(Lang.T("Количество, т", "Quantity, t"), _amount));

        // Документът е наш, не на схемата: протоколът, с който партидата е
        // дошла, е това, по което човек я намира в папката.
        if (_kind == MoveKind.Received)
            host.Controls.Add(Row(Lang.T("Документ", "Document"), _document));

        // При третиране кодът горе е този, по който се вади складът — приетият.
        // Кодът за книгата може да е друг и тогава се пише тук. Празно значи
        // „същият“: полето съществува заради петте приети кода, които влизат в
        // едно производство, а не заради всекидневния случай.
        if (_kind == MoveKind.Treated)
        {
            _outCode.DropDownStyle = ComboBoxStyle.DropDown;
            _outCode.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            _outCode.AutoCompleteSource = AutoCompleteSource.ListItems;

            host.Controls.Add(Row(
                Lang.T("Код за отчета (празно — същият)", "Code for the report (blank — the same)"),
                _outCode));
        }

        foreach (var column in Extras())
            host.Controls.Add(Row(ReportTitles.ColumnShort(column), Input(column)));

        // Договорът е на предаването и предлага същия списък като прозореца на
        // склада: написаното тук се научава при записа, както там.
        if (Moves.HasContract(_kind))
        {
            _contract.DropDownStyle = ComboBoxStyle.DropDown;
            _contract.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            _contract.AutoCompleteSource = AutoCompleteSource.ListItems;

            foreach (var contract in _workspace.Partners.List(_companyId, PartnerRole.Contract))
                _contract.Items.Add(contract.Name);

            host.Controls.Add(Row(Moves.ContractTitle, _contract));
        }

        host.Controls.Add(Row(Lang.T("Бележка", "Note"), _note));

        frame.Controls.Add(host);

        // Редовете следват ширината на прозореца — до 700 пиксела в мащаба. При
        // 150 % прозорецът се свива до екрана, а редовете от 1050 пиксела не
        // се свиваха с него и десният им край се режеше.
        host.SizeChanged += (_, _) =>
        {
            var width = Math.Min(Theme.Scaled(700), host.ClientSize.Width - host.Padding.Horizontal);

            if (width <= 0) return;

            foreach (Control row in host.Controls)
                if (row.Width != width) row.Width = width;
        };

        return frame;
    }

    /// <summary>
    /// Колоните на възела без трите, които складът води сам.
    ///
    /// Датата, кодът и количеството имат свои полета горе; площадката идва от
    /// страницата. Останалото е на схемата.
    /// </summary>
    private IEnumerable<ColumnDef> Extras() =>
        _node?.Attributes.Where(c => !BookNodes.Owned.Contains(c.Name)) ?? [];

    private Control Input(ColumnDef column)
    {
        Control input;

        if (column.Domain.IsEnum && !column.Domain.IsLargeEnum)
        {
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
            combo.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            combo.AutoCompleteSource = AutoCompleteSource.ListItems;

            BookLimits.Offer(combo, BookLimits.Of(_workspace, _companyId), _kind, column);

            input = combo;
        }
        else if (_kind == MoveKind.Received && column.Name == LedgerWriter.NameField)
        {
            input = Supplier();
        }
        else if (_kind == MoveKind.Received && column.Name == LedgerWriter.EikField)
        {
            var box = new TextBox();

            // Познат ЕИК попълва празното име — написаното не се затрива.
            box.Leave += (_, _) =>
            {
                if (!_inputs.TryGetValue(LedgerWriter.NameField, out var who) || who.Text.Trim().Length > 0) return;

                var name = _workspace.Partners.NameOf(_companyId, box.Text, PartnerRole.Originator);
                if (name.Length > 0) who.Text = name;
            };

            input = box;
        }
        else
        {
            input = new TextBox();
        }

        _inputs[column.Name] = input;
        return input;
    }

    /// <summary>
    /// Доставчикът — меню от познатите, в което се и пише.
    ///
    /// Менюто показва „Метал ООД — 203344556“: двата номера различават две
    /// дружества с близки имена. В полето остава само името, както го иска
    /// книгата, а ЕИК-ът слиза в своето поле, ако то е празно.
    /// </summary>
    private ComboBox Supplier()
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
        combo.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        combo.AutoCompleteSource = AutoCompleteSource.ListItems;

        foreach (var partner in _workspace.Originators(_companyId))
            combo.Items.Add(partner.Display);

        void Split()
        {
            if (_filling) return;

            var name = PartnerStore.NameOf(combo.Text);
            if (name.Length == 0) return;

            var known = _workspace.Partners.Complete(_companyId, name, string.Empty, PartnerRole.Originator);

            _filling = true;

            try
            {
                combo.Text = known.Name;
            }
            finally
            {
                _filling = false;
            }

            if (!_inputs.TryGetValue(LedgerWriter.EikField, out var target)) return;

            // Написаното от човек не се затрива — пипа се само празното поле.
            if (target.Text.Trim().Length == 0 && known.Eik.Length > 0) target.Text = known.Eik;
        }

        combo.SelectedIndexChanged += (_, _) => Split();
        combo.Leave += (_, _) => Split();

        return combo;
    }

    private Control BuildAllocation()
    {
        _allocation.Dock = DockStyle.Fill;
        _allocation.Margin = new Padding(Theme.GapLarge, 0, Theme.GapLarge, 0);
        _allocation.Fill = Theme.Canvas;
        _allocation.Outline = Theme.Border;
        _allocation.Padding = new Padding(Theme.Gap);
        _allocation.Visible = Draws || Inherits;

        // Закачено за долния ръб, а не Fill: разпределението е с постоянна
        // височина и не бива да се свива, когато полетата отгоре пораснат.
        var host = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = Theme.Scaled(250),
            Visible = Draws || Inherits,
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, 0, Theme.GapLarge, Theme.Gap)
        };

        _split.Dock = DockStyle.Bottom;
        _split.Height = Theme.Scaled(40);
        _split.Font = Theme.Small;
        _split.ForeColor = Theme.InkMuted;

        _draws.Dock = DockStyle.Fill;
        _draws.Font = Theme.Base;
        _draws.BackgroundColor = Theme.Canvas;
        _draws.BorderStyle = BorderStyle.None;
        _draws.EnableHeadersVisualStyles = false;
        _draws.AllowUserToAddRows = false;
        _draws.AllowUserToDeleteRows = false;
        _draws.AllowUserToResizeRows = false;
        _draws.RowHeadersVisible = false;
        _draws.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _draws.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _draws.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _draws.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;

        _draws.ColumnHeadersDefaultCellStyle.BackColor = Theme.Surface;
        _draws.ColumnHeadersDefaultCellStyle.ForeColor = Theme.InkStrong;
        _draws.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.Surface;
        _draws.ColumnHeadersDefaultCellStyle.SelectionForeColor = Theme.InkStrong;
        _draws.ColumnHeadersDefaultCellStyle.Font = Theme.Bold;
        _draws.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 6, 6, 6);
        _draws.DefaultCellStyle.BackColor = Theme.Canvas;
        _draws.DefaultCellStyle.ForeColor = Theme.Ink;
        _draws.DefaultCellStyle.Padding = new Padding(6, 4, 6, 4);
        _draws.GridColor = Theme.Border;

        var line = Math.Max(1, _draws.Font.Height);
        _draws.RowTemplate.Height = line + 12;
        _draws.ColumnHeadersHeight = line + 20;

        _draws.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = BatchColumn,
            HeaderText = Lang.T("Партида", "Batch"),
            ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 200,
            MinimumWidth = Theme.Scaled(200),
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        _draws.Columns.Add(Number(StockColumn, Lang.T("Налично, т", "Available, t"), true));
        _draws.Columns.Add(Number(TakenColumn, Lang.T("Взето, т", "Taken, t"), Inherits));

        _draws.CellEndEdit += (_, _) => Describe();

        _allocation.Controls.Add(_draws);
        _allocation.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(24),
            Text = Inherits
                ? Lang.T("Произход — наследен от третиранията през месеца",
                         "Origin — inherited from this month’s treatments")
                : Lang.T("Разпределение по партиди — най-старата първа",
                         "Split across batches — oldest first"),
            Font = Theme.SmallBold,
            ForeColor = Theme.InkFaint
        });

        host.Controls.Add(_allocation);
        host.Controls.Add(_split);

        return host;
    }

    private static DataGridViewTextBoxColumn Number(string name, string header, bool readOnly)
    {
        var column = new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            Width = Theme.Scaled(140),
            ReadOnly = readOnly,
            SortMode = DataGridViewColumnSortMode.NotSortable
        };

        column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        column.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;

        return column;
    }

    private Control BuildButtons()
    {
        // Височината се смята от копчето, а не се избира: 40 пиксела копче в
        // лента от 68 с по 16 отстояние горе и долу остава 4 пиксела извън нея —
        // а лентата е долепена до долния ръб, тоест тези 4 пиксела са извън
        // прозореца и копчето изглежда липсващо.
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
        _save = save;

        var cancel = Theme.GhostButton(Lang.T("Отказ", "Cancel"));
        cancel.Height = buttonHeight;
        cancel.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;

        tools.Controls.Add(save.FitWidth());
        tools.Controls.Add(cancel.FitWidth());

        bar.Controls.Add(tools);
        CancelButton = cancel;

        return bar;
    }

    private Control Row(string caption, Control input)
    {
        var host = new Panel
        {
            Width = Theme.Scaled(700),
            Height = Theme.Scaled(52),
            BackColor = Theme.Canvas,
            Margin = new Padding(0, 0, 0, Theme.GapSmall)
        };

        input.Dock = DockStyle.Top;
        input.Width = Theme.Scaled(700);
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

    /// <summary>Слага началните стойности — или тези на движението, което се поправя.</summary>
    private void Seed(LedgerPeriod period)
    {
        _filling = true;

        try
        {
            // Датата се слага ПРЕДИ менюто с кодовете: при черпене от склад
            // менюто изброява наличното, а наличното е към дата.
            var today = DateOnly.FromDateTime(DateTime.Today);
            _date.Value = period.Covers(today)
                ? today.ToDateTime(TimeOnly.MinValue)
                : period.Last.ToDateTime(TimeOnly.MinValue);

            if (_moveId is { } id) Load(id);

            FillCodes();
        }
        finally
        {
            _filling = false;
        }

        Recalculate();
    }

    /// <summary>
    /// Кодовете в менюто.
    ///
    /// При черпене от склад се предлагат само кодовете, които наистина има:
    /// списък от осемстотин, от които стават двайсет, е списък, в който се
    /// греши. При приемане и образуване менюто е пълното — тогава кодът идва
    /// отвън и складът няма мнение.
    /// </summary>
    private void FillCodes()
    {
        // Изчистването на списъка изпразва и написаното. При поправка то е
        // самото движение и не бива да изчезне, защото менюто се е пренаредило.
        var chosen = _code.Text;

        _code.Items.Clear();

        if (Draws)
        {
            foreach (var code in StockMath.Available(Stock())
                         .Select(s => s.Batch.Ewc)
                         .Distinct(StringComparer.Ordinal)
                         .Order(StringComparer.Ordinal))
                _code.Items.Add(Describe(code));
        }
        else if (_node?.Attributes.FirstOrDefault(c => c.Name == "ewc") is { } column)
        {
            var limits = BookLimits.Of(_workspace, _companyId);

            foreach (var option in limits.Offer(BookNodes.Report, Moves.Node(_kind), column))
                _code.Items.Add(option.Display);

            // Празното поле тръгва с останалия един код; вече избраното не се
            // пипа — менютата се пълнят наново и при смяна на вида.
            if (chosen.Trim().Length == 0) chosen = BookLimits.Sole(limits, _kind, column);
        }

        _code.Text = chosen;

        // Менюто на изходния код е пълното и когато горното е сведено до склада:
        // там се избира какво ИЗЛИЗА, а то не е ограничено от това, което стои.
        if (_kind == MoveKind.Treated &&
            _node?.Attributes.FirstOrDefault(c => c.Name == "ewc") is { } outgoing)
        {
            var chosenOut = _outCode.Text;

            _outCode.Items.Clear();

            foreach (var option in _workspace.Lists.LimitsFor(_companyId, "book")
                         .Offer(BookNodes.Report, Moves.Node(_kind), outgoing))
                _outCode.Items.Add(option.Display);

            _outCode.Text = chosenOut;
        }
    }

    private void Load(long id)
    {
        var move = _workspace.Ledger.Read(_companyId, _stagenum).FirstOrDefault(m => m.Id == id);
        if (move is null) return;

        _date.Value = move.Date.ToDateTime(TimeOnly.MinValue);
        _code.Text = Describe(move.Ewc);
        _outCode.Text = move.EwcOut.Length > 0 ? Describe(move.EwcOut) : string.Empty;
        _amount.Text = SplitMath.Amount(move.Amount);
        _note.Text = _workspace.Ledger.Note(id);
        _contract.Text = _workspace.Ledger.Contract(id);

        foreach (var (name, value) in _workspace.Ledger.Fields(id))
        {
            if (!_inputs.TryGetValue(name, out var input)) continue;

            input.Text = input is ComboBox combo ? Match(combo, value) : value;
        }
    }

    /// <summary>
    /// Наличното към избраната дата.
    ///
    /// Когато се поправя вече записано движение, самото то се изважда от
    /// сметката: иначе собствените му дялове биха се броили за чужди и складът
    /// щеше да изглежда по-празен, отколкото е.
    /// </summary>
    private IReadOnlyList<BatchStock> Stock()
    {
        if (Moves.From(_kind) is not { } store) return [];

        var moves = _workspace.Ledger.Read(_companyId, _stagenum)
            .Where(m => m.Id != _moveId)
            .ToList();

        var links = _workspace.Ledger.Links(_companyId, _stagenum)
            .Where(l => l.MoveId != _moveId)
            .ToList();

        var on = DateOnly.FromDateTime(_date.Value);

        return StockMath.Batches(_workspace.Batches.List(_companyId, _stagenum), moves, links, store, on);
    }

    /// <summary>Пресмята предложеното разпределение наново и го показва.</summary>
    private void Recalculate()
    {
        if (_filling || IsDisposed) return;

        _filling = true;

        try
        {
            if (Draws) Propose();
            else if (Inherits) Trace();
        }
        finally
        {
            _filling = false;
        }

        Describe();
    }

    private void Propose()
    {
        var code = Code(_code.Text);

        _stock = [.. StockMath.Available(Stock(), code)];
        _draws.Rows.Clear();

        SplitMath.TryRead(_amount.Text, out var amount);
        var proposed = Allocation.Fifo(_stock, amount)
            .Draws.ToDictionary(d => d.BatchId, d => d.Amount);

        foreach (var item in _stock)
        {
            var index = _draws.Rows.Add();
            var row = _draws.Rows[index];

            row.Cells[BatchColumn].Value = $"{item.Batch.Display} · {item.Batch.ReceivedOn:dd.MM.yyyy}";
            row.Cells[StockColumn].Value = SplitMath.Amount(item.Available);
            row.Cells[TakenColumn].Value = proposed.TryGetValue(item.Batch.Id, out var taken) && taken > 0
                ? SplitMath.Amount(taken)
                : string.Empty;

            row.Tag = item.Batch.Id;
        }
    }

    /// <summary>
    /// Показва наследения произход.
    ///
    /// Образуваното не се посочва на ръка: то идва от третиранията през месеца и
    /// носи техните дялове в същото съотношение. Друго съотношение не се знае —
    /// претенцията е пропорционална, не измерена.
    /// </summary>
    private void Trace()
    {
        _draws.Rows.Clear();

        SplitMath.TryRead(_amount.Text, out var amount);

        // Над чакащото няма произход: остатъкът е собствен отпадък.
        var pool = Treatments();
        var take = Math.Min(amount, SplitMath.Round(pool.Sum(d => d.Amount)));

        var draws = Allocation.Inherit(pool, take);
        var batches = _workspace.Batches.List(_companyId, _stagenum).ToDictionary(b => b.Id);

        foreach (var origin in Allocation.Origins(draws, batches))
        {
            var index = _draws.Rows.Add();
            var row = _draws.Rows[index];

            row.Cells[BatchColumn].Value = origin.Partner;
            row.Cells[StockColumn].Value = string.Empty;
            row.Cells[TakenColumn].Value = SplitMath.Amount(origin.Amount);
            row.Tag = origin.BatchId;
        }
    }

    /// <summary>
    /// Какво чака в склад „Третиран“ към избраната дата.
    ///
    /// Оттам черпи образуваното — не от „третиранията през този месец“, както
    /// беше досега. Третирано на 30 септември и образувано на 2 октомври са едно
    /// и също количество; календарът няма нищо общо с това.
    /// </summary>
    private IReadOnlyList<Draw> Treatments()
    {
        var moves = _workspace.Ledger.Read(_companyId, _stagenum)
            .Where(m => m.Id != _moveId)
            .ToList();

        var links = _workspace.Ledger.Links(_companyId, _stagenum)
            .Where(l => l.MoveId != _moveId)
            .ToList();

        return StockMath.Waiting(
            _workspace.Batches.List(_companyId, _stagenum),
            moves,
            links,
            DateOnly.FromDateTime(_date.Value)).Draws;
    }

    /// <summary>Казва как се схожда разпределението с количеството.</summary>
    private void Describe()
    {
        if (!Draws && !Inherits)
        {
            _split.Text = string.Empty;
            return;
        }

        SplitMath.TryRead(_amount.Text, out var amount);
        var taken = Taken().Sum(d => d.Amount);
        var missing = SplitMath.Round(amount - taken);

        if (amount <= 0)
        {
            _split.Text = Lang.T("Въведете количество.", "Enter a quantity.");
            _split.ForeColor = Theme.InkMuted;
            return;
        }

        if (missing == 0)
        {
            _split.Text = Inherits
                ? Lang.T($"Произходът покрива {SplitMath.Amount(amount)} т.",
                         $"The origin covers {SplitMath.Amount(amount)} t.")
                : Lang.T($"Разпределени са всичките {SplitMath.Amount(amount)} т.",
                         $"All {SplitMath.Amount(amount)} t are allocated.");
            _split.ForeColor = Theme.Ok;
            return;
        }

        _split.Text = missing > 0
            ? Lang.T($"Непокрити остават {SplitMath.Amount(missing)} т от {SplitMath.Amount(amount)} т.",
                     $"{SplitMath.Amount(missing)} t of {SplitMath.Amount(amount)} t remain uncovered.")
            : Lang.T($"Разпределено е с {SplitMath.Amount(-missing)} т повече от количеството.",
                     $"{SplitMath.Amount(-missing)} t more is allocated than the quantity.");

        _split.ForeColor = Theme.Warn;
    }

    /// <summary>Дяловете, както стоят в таблицата в момента.</summary>
    private List<Draw> Taken()
    {
        var draws = new List<Draw>();

        foreach (DataGridViewRow row in _draws.Rows)
        {
            if (row.Tag is not long batchId) continue;
            if (!SplitMath.TryRead(row.Cells[TakenColumn].Value?.ToString(), out var amount)) continue;
            if (amount == 0) continue;

            draws.Add(new Draw(batchId, amount));
        }

        return draws;
    }

    // ------------------------------------------------------------- записване

    /// <summary>
    /// Проверява и затваря.
    ///
    /// Недостигът НЕ спира записа: половин въведен месец трябва да може да се
    /// запише и да се поправи после. Спира само това, което няма как да се
    /// поправи после — липсващо количество и липсващ код.
    /// </summary>
    private void Accept()
    {
        var code = Code(_code.Text);
        var problems = new List<string>();

        if (code.Length == 0)
            problems.Add(Lang.T("Кодът на отпадъка е задължителен.", "The waste code is required."));

        if (!SplitMath.TryRead(_amount.Text, out var amount) || amount <= 0)
            problems.Add(Lang.T("Количеството трябва да е положително число.",
                                "The quantity must be a positive number."));

        foreach (var column in Extras())
        {
            if (!column.Required) continue;
            if (Value(column).Length > 0) continue;

            problems.Add(Lang.T($"„{ReportTitles.ColumnShort(column)}“ е задължително.",
                                $"“{ReportTitles.ColumnShort(column)}” is required."));
        }

        var draws = Taken();

        if (Draws && draws.Count > 0)
            problems.AddRange(Allocation.Check(draws, _stock, amount, code)
                .Where(i => i.Kind != DrawProblem.Mismatch)
                .Select(i => i.Text));

        if (problems.Count > 0)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, problems.Take(8)),
                Lang.T("Липсва нещо", "Something is missing"),
                MessageBoxButtons.OK, MessageBoxIcon.Warning);

            return;
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var column in Extras())
        {
            var value = Value(column);
            if (value.Length > 0) fields[column.Name] = value;
        }

        Result = new MoveEntry(
            _kind,
            DateOnly.FromDateTime(_date.Value),
            code,
            amount,
            fields,
            _document.Text.Trim(),
            _note.Text.Trim(),
            draws,
            Code(_outCode.Text),
            PartnerStore.Tidy(_contract.Text));

        DialogResult = DialogResult.OK;
    }

    private string Value(ColumnDef column) =>
        _inputs.TryGetValue(column.Name, out var input) ? Code(input.Text) : string.Empty;

    // ------------------------------------------------------------- помощни

    /// <summary>„161004 — Отпадъци от…“ обратно до „161004“.</summary>
    private static string Code(string? display)
    {
        var text = display?.Trim() ?? string.Empty;
        var dash = text.IndexOf(" — ", StringComparison.Ordinal);

        return dash > 0 ? text[..dash].Trim() : text;
    }

    /// <summary>Намира записаната стойност в менюто, за да се покаже с описанието си.</summary>
    private static string Match(ComboBox combo, string value)
    {
        foreach (var item in combo.Items)
            if (item is string text && Code(text) == value)
                return text;

        return value;
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

    private static string Title(MoveKind kind) => kind switch
    {
        MoveKind.Received => Lang.T("Приемане", "Receipt"),
        MoveKind.Treated => Lang.T("Третиране", "Treatment"),
        MoveKind.Generated => Lang.T("Образуване", "Generation"),
        _ => Lang.T("Предаване", "Handover")
    };
}
