using RiosvImports.App.Controls;
using RiosvImports.Core.Ledger;
using RiosvImports.Core.Localization;
using RiosvImports.Data;

namespace RiosvImports.App.Shell;

/// <summary>
/// Получателите на една фирма: на кого предава тя.
///
/// Списъкът се пълни САМ — всяко записано предаване вписва получателя си. Този
/// прозорец съществува за другите две неща: да се впише получател предварително
/// (преди първото предаване, за да не се пише на ръка тогава) и да се поправи
/// сгрешен ЕИК, който вече се предлага по трийсет пъти месечно.
///
/// <b>Списъкът предлага, не ограничава.</b> Махнат оттук получател не изчезва
/// от нито едно записано предаване: името му е в самото движение, както го иска
/// НИСО. Оттук си отива само предложението (правило 13).
///
/// Устроено като „Внесени файлове“ и „Дневник на въведеното“: таблица, ред за
/// състоянието, действия отляво, „Затвори“ отдясно.
///
/// Един прозорец и за трите списъка — получателите, доставчиците и договорите:
/// те са различни списъци, но работата по тях е една и съща, и втори прозорец
/// би бил втори начин за едно и също нещо. Договорът няма ЕИК и полето за него
/// там просто го няма.
/// </summary>
internal sealed class ReceiversDialog : Dialog
{
    private readonly Workspace _workspace;
    private readonly long _companyId;

    /// <summary>Кой списък е отворен — получатели, доставчици или договори.</summary>
    private readonly string _role;

    private bool Suppliers => _role == PartnerRole.Originator;

    private bool Contracts => _role == PartnerRole.Contract;

    private readonly DataGridView _grid = new();
    private readonly Label _summary = new();

    private readonly TextBox _name = new();
    private readonly TextBox _eik = new();

    private readonly AppButton _add;
    private readonly AppButton _save;
    private readonly AppButton _remove;

    private List<Partner> _partners = [];

    public ReceiversDialog(Workspace workspace, long companyId, string role = PartnerRole.Receiver)
    {
        _workspace = workspace;
        _companyId = companyId;
        _role = role;

        Text = Heading;
        Font = Theme.Base;
        BackColor = Theme.Canvas;
        MinimumSize = new Size(Theme.Scaled(720), Theme.Scaled(440));
        ClientSize = new Size(Theme.Scaled(860), Theme.Scaled(560));
        MaximizeBox = false;
        KeyPreview = true;

        _add = Theme.PrimaryButton(Lang.T("Впиши", "Add"));
        _save = Theme.SecondaryButton(Lang.T("Поправи избрания", "Fix the selected one"));
        _remove = Theme.DangerButton(Lang.T("Махни", "Remove"), Theme.Glyph.Delete);

        Controls.Add(BuildBody());
        Controls.Add(BuildEntry());
        Controls.Add(BuildSummary());
        Controls.Add(BuildTitle());
        Controls.Add(BuildButtons());

        Fill();

        Theme.Apply(this);
    }

    /// <summary>Дали списъкът е пипан — прозорчетата го четат наново само тогава.</summary>
    public bool Changed { get; private set; }

    // ------------------------------------------------------------- устройство

    private Control BuildTitle()
    {
        var host = new Panel
        {
            Dock = DockStyle.Top,
            // Заглавието плюс бележката под него — с 76 пиксела заглавието
            // губеше долния си край дори при 100 %.
            Height = Math.Max(Theme.Scaled(76), Theme.Gap + Theme.Title.Height + Theme.Scaled(42)),
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, Theme.Gap, Theme.GapLarge, 0)
        };

        // Пълнещият влиза пръв (виж АРХИТЕКТУРА.md).
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
            Height = Theme.Scaled(38),
            Text = Contracts
                ? Lang.T(
                    "Списъкът се пълни сам — всяко записано предаване вписва договора си. Предлага се, " +
                    "докато се пише, за да не се пише един договор по три начина. Изтеклият се маха " +
                    "оттук; записаните предавания не се пипат.",
                    "The list fills itself — every saved hand-over adds its contract. It is offered while " +
                    "typing, so one contract is not written three ways. An expired one is removed here; " +
                    "saved hand-overs are not touched.")
                : Suppliers
                ? Lang.T(
                    "Списъкът се пълни сам — всяко записано приемане, на ръка или от внесен дневник, " +
                    "вписва доставчика си. Предлага се, докато се пише, за да не се пише едно име по " +
                    "три начина. Махнатото оттук не пипа нито едно записано приемане.",
                    "The list fills itself — every saved receipt, typed or imported, adds its supplier. " +
                    "It is offered while typing, so one name is not written three ways. Removing from " +
                    "here touches no saved receipt.")
                : Lang.T(
                    "Списъкът се пълни сам — всяко записано предаване вписва получателя си. " +
                    "Тук се впи́сва предварително и се поправя сгрешен ЕИК. Махнатото оттук не " +
                    "пипа нито едно записано предаване.",
                    "The list fills itself — every saved hand-over adds its receiver. Here you add one " +
                    "in advance and fix a wrong VAT id. Removing from here touches no saved hand-over."),
            Font = Theme.Base,
            ForeColor = Theme.InkMuted
        });

        return host;
    }

    private Control BuildSummary()
    {
        var host = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Base.Height + Theme.Scaled(16),
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, 0, Theme.GapLarge, Theme.GapSmall)
        };

        _summary.Dock = DockStyle.Fill;
        _summary.Font = Theme.Bold;
        _summary.ForeColor = Theme.InkMuted;

        host.Controls.Add(_summary);

        return host;
    }

    /// <summary>Редът за вписване — име и ЕИК, най-отдолу над копчетата.</summary>
    private Control BuildEntry()
    {
        var host = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = Theme.Scaled(76),
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, Theme.GapSmall, Theme.GapLarge, 0)
        };

        var line = new Panel { Dock = DockStyle.Top, Height = Theme.Scaled(44), BackColor = Theme.Canvas };

        _eik.Dock = DockStyle.Right;
        _eik.Width = Theme.Scaled(180);
        _eik.Visible = !Contracts;
        Theme.StyleInput(_eik);

        _name.Dock = DockStyle.Fill;
        Theme.StyleInput(_name);

        // Пълнещият влиза пръв.
        line.Controls.Add(_name);
        line.Controls.Add(_eik);

        var captions = new Panel { Dock = DockStyle.Top, Height = Theme.Small.Height + 6, BackColor = Theme.Canvas };

        captions.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = Contracts
                ? Moves.ContractTitle
                : Suppliers
                    ? Lang.T("Име на доставчик", "Supplier name")
                    : Lang.T("Име на получател", "Receiver name"),
            Font = Theme.SmallBold,
            ForeColor = Theme.InkFaint
        });

        captions.Controls.Add(new Label
        {
            Dock = DockStyle.Right,
            Width = Theme.Scaled(180),
            Text = Lang.T("ЕИК / ДДС номер", "VAT id"),
            Font = Theme.SmallBold,
            ForeColor = Theme.InkFaint,
            Visible = !Contracts
        });

        host.Controls.Add(line);
        host.Controls.Add(captions);

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

        _grid.Dock = DockStyle.Fill;
        card.Controls.Add(_grid);
        host.Controls.Add(card);

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
        _grid.RowTemplate.Height = line + 14;
        _grid.ColumnHeadersHeight = line + 22;

        Column("name",
            Contracts
                ? Moves.ContractTitle
                : Suppliers ? Lang.T("Доставчик", "Supplier") : Lang.T("Получател", "Receiver"),
            300, fill: true);
        Column("eik", Lang.T("ЕИК / ДДС номер", "VAT id"), 190);
        Column("used",
            // Договорът се брои по движение: едно предаване по три кода са три.
            Contracts
                ? Lang.T("Движения", "Movements")
                : Suppliers ? Lang.T("Приемания", "Receipts") : Lang.T("Предавания", "Hand-overs"),
            140, right: true);

        _grid.Columns["eik"]!.Visible = !Contracts;

        _grid.SelectionChanged += (_, _) => Picked();
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
        var host = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = Theme.Base.Height + Theme.Scaled(30),
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, 0, Theme.GapLarge, Theme.Gap)
        };

        var close = Theme.SecondaryButton(Lang.T("Затвори", "Close"));
        close.Dock = DockStyle.Right;
        close.Height = Theme.Base.Height + Theme.Scaled(16);
        close.FitWidth(16);
        close.Click += (_, _) => DialogResult = DialogResult.OK;

        var tools = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Canvas
        };

        foreach (var button in new[] { _add, _save, _remove })
        {
            button.Height = Theme.Base.Height + Theme.Scaled(16);
            button.Margin = new Padding(0, 0, Theme.GapSmall, 0);
            button.FitWidth(16);
        }

        _add.Click += (_, _) => Add();
        _save.Click += (_, _) => Fix();
        _remove.Click += (_, _) => Remove();

        tools.Controls.Add(_add);
        tools.Controls.Add(_save);
        tools.Controls.Add(_remove);

        host.Controls.Add(close);
        host.Controls.Add(tools);

        CancelButton = close;

        return host;
    }

    // ------------------------------------------------------------- пълнене

    private void Fill()
    {
        _partners = Suppliers
            ? [.. _workspace.Originators(_companyId)]
            : [.. _workspace.Partners.List(_companyId, _role)];

        _grid.Rows.Clear();

        foreach (var partner in _partners)
        {
            var index = _grid.Rows.Add();
            var row = _grid.Rows[index];

            row.Cells["name"].Value = partner.Name;
            row.Cells["eik"].Value = partner.Eik;

            // Вписаният, но още неползван, се чете по-бледо: той е намерение, а
            // не история.
            row.Cells["used"].Value = partner.Fresh
                ? Lang.T("още не", "not yet")
                : partner.Used.ToString();

            if (partner.Fresh) row.DefaultCellStyle.ForeColor = Theme.InkFaint;

            row.Tag = partner;
        }

        _summary.Text = PartnerStore.Counted(_partners.Count, _role);
        _summary.ForeColor = _partners.Count == 0 ? Theme.InkFaint : Theme.Ink;

        Allow();
    }

    private Partner? Selected() => _grid.CurrentRow?.Tag as Partner;

    /// <summary>Избраният слиза в полетата — поправката е писане, не втори екран.</summary>
    private void Picked()
    {
        if (Selected() is not { } partner) return;

        _name.Text = partner.Name;
        _eik.Text = partner.Eik;

        Allow();
    }

    private void Allow()
    {
        var typed = _name.Text.Trim().Length > 0;

        _add.Enabled = typed;
        _save.Enabled = typed && Selected() is not null;
        _remove.Enabled = Selected() is not null;
    }

    // ------------------------------------------------------------- действия

    /// <summary>
    /// Написаното в полето за име. Договорът се пази без излишни интервали, както
    /// и при записа на предаване: иначе вписаното тук не би съвпаднало с него.
    /// </summary>
    private string Typed => Contracts ? PartnerStore.Tidy(_name.Text) : _name.Text;

    private void Add()
    {
        if (_name.Text.Trim().Length == 0) return;

        if (!_workspace.Partners.Add(_companyId, Typed, _eik.Text, _role))
        {
            Already();
            return;
        }

        Changed = true;

        _name.Text = string.Empty;
        _eik.Text = string.Empty;

        Fill();
    }

    /// <summary>Казва, че написаното вече го има — на показан прозорец.</summary>
    private void Already()
    {
        if (!Visible) return;

        MessageBox.Show(this,
            Contracts
                ? Lang.T("Този договор вече е в списъка.", "That contract is already on the list.")
                : Suppliers
                    ? Lang.T("Този доставчик вече е в списъка.", "That supplier is already on the list.")
                    : Lang.T("Този получател вече е в списъка.", "That receiver is already on the list."),
            Lang.T("Вече го има", "Already there"),
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void Fix()
    {
        if (Selected() is not { } partner) return;
        if (_name.Text.Trim().Length == 0) return;

        // Поправена печатна грешка често става точно съседът ѝ, а ключът на
        // таблицата не търпи двойник: казва се, вместо да гръмне. Смяна само на
        // главните букви на самия ред минава.
        var typed = Typed.Trim();
        var pin = _eik.Text.Trim();

        if (_partners.Any(p => p.Id != partner.Id &&
                               string.Equals(p.Name, typed, StringComparison.CurrentCultureIgnoreCase) &&
                               string.Equals(p.Eik, pin, StringComparison.OrdinalIgnoreCase)))
        {
            Already();
            return;
        }

        _workspace.Partners.Update(partner.Id, Typed, _eik.Text);

        Changed = true;

        Fill();
    }

    private void Remove()
    {
        if (Selected() is not { } partner) return;

        var said = Lang.T(
            $"„{partner.Display}“ ще спре да се предлага.\n\n" +
            "Записаните движения НЕ се пипат: името им стои в самото движение.",
            $"“{partner.Display}” will stop being offered.\n\n" +
            "Saved movements are NOT touched: their name is in the movement itself.");

        if (MessageBox.Show(this, said, Lang.T("Махане", "Removal"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        _workspace.Partners.Remove(partner.Id);

        Changed = true;

        Fill();
    }

    private string Heading => Contracts
        ? Lang.T("Договори", "Contracts")
        : Suppliers
            ? Lang.T("Доставчици", "Suppliers")
            : Lang.T("Получатели", "Receivers");

    // ------------------------------------------------------------- за теста

    internal int RowCount => _grid.Rows.Count;

    internal string SummaryText => _summary.Text;

    internal string NameAt(int row) => _grid.Rows[row].Cells["name"].Value?.ToString() ?? string.Empty;

    internal string EikAt(int row) => _grid.Rows[row].Cells["eik"].Value?.ToString() ?? string.Empty;

    internal string UsedAt(int row) => _grid.Rows[row].Cells["used"].Value?.ToString() ?? string.Empty;

    /// <summary>Вижда ли се полето за ЕИК — договорът няма такова.</summary>
    internal bool EikShown => _grid.Columns["eik"]!.Visible;

    /// <summary>Заглавието на прозореца.</summary>
    internal string HeadingText => Heading;

    /// <summary>Маха избрания — питането се прескача на скрит прозорец.</summary>
    internal void RemoveNow()
    {
        if (Selected() is not { } partner) return;

        _workspace.Partners.Remove(partner.Id);

        Changed = true;

        Fill();
    }

    /// <summary>Вписва получател, както копчето — но без прозорчето за грешка.</summary>
    internal bool AddNow(string name, string eik)
    {
        var added = _workspace.Partners.Add(_companyId, Contracts ? PartnerStore.Tidy(name) : name, eik, _role);

        if (added) Changed = true;

        Fill();

        return added;
    }

    internal void Pick(int row)
    {
        if (row < 0 || row >= _grid.Rows.Count) return;

        _grid.CurrentCell = _grid.Rows[row].Cells[0];
        Picked();
    }

    /// <summary>Поправя избрания, както копчето.</summary>
    internal void FixNow(string name, string eik)
    {
        _name.Text = name;
        _eik.Text = eik;

        Fix();
    }
}
