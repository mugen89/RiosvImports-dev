using RiosvImports.App.Controls;
using RiosvImports.Core.Ledger;
using RiosvImports.Core.Localization;
using RiosvImports.Core.Values;
using RiosvImports.Data;

namespace RiosvImports.App.Shell;

/// <summary>
/// Една партида, отворена от склада: целият ѝ път.
///
/// Складът отговаря на „какво стои“. Този прозорец отговаря на другия въпрос,
/// който клиентът задава по телефона: „какво стана с моите дванайсет тона“.
/// Отговорът не се съчинява от дати и количества — той се чете от същите
/// връзки, от които се смята и наличността, и точно затова двете не могат да се
/// разминат.
///
/// Показва се и какво е останало в двата склада: партидата почти никога не
/// свършва наведнъж, а „осем от дванайсет са третирани“ е отговорът, който
/// човекът търси, без да го е формулирал така.
/// </summary>
internal sealed class BatchDialog : Dialog
{
    private const string DateColumn = "__date";
    private const string KindColumn = "__kind";
    private const string CodeColumn = "__code";
    private const string ShareColumn = "__share";
    private const string NoteColumn = "__note";
    private const string ReceiverColumn = "__receiver";
    private const string EikColumn = "__eik";
    private const string ContractColumn = "__contract";

    private readonly DataGridView _steps = new();
    private readonly Label _summary = new();
    private readonly Trail _trail;
    private readonly Dictionary<string, string> _names;

    public BatchDialog(
        Workspace workspace,
        long companyId,
        string stagenum,
        Batch batch,
        IReadOnlyDictionary<string, string> names)
    {
        _names = new Dictionary<string, string>(names, StringComparer.Ordinal);

        _trail = StockMath.Trace(
            batch,
            workspace.Ledger.Read(companyId, stagenum),
            workspace.Ledger.Links(companyId, stagenum));

        Text = Lang.T($"Партида #{batch.Code}", $"Batch #{batch.Code}");
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.Sizable;
        ClientSize = new Size(Theme.Scaled(880), Theme.Scaled(560));
        MinimumSize = new Size(Theme.Scaled(760), Theme.Scaled(460));
        BackColor = Theme.Canvas;
        Font = Theme.Base;

        Controls.Add(BuildSteps());
        Controls.Add(BuildButtons());
        Controls.Add(BuildTitle(batch));

        Fill(workspace);
        Theme.Apply(this);
    }

    // ------------------------------------------------------------- устройство

    private Control BuildTitle(Batch batch)
    {
        var host = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(128),
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, Theme.Gap, Theme.GapLarge, 0)
        };

        _summary.Dock = DockStyle.Bottom;
        _summary.Height = Theme.Scaled(52);
        _summary.Font = Theme.Base;
        _summary.ForeColor = Theme.InkMuted;

        host.Controls.Add(_summary);

        host.Controls.Add(new Label
        {
            Dock = DockStyle.Bottom,
            Height = Theme.Scaled(24),
            Text = Describe(batch),
            Font = Theme.Base,
            ForeColor = Theme.InkMuted,
            AutoEllipsis = true
        });

        host.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = Lang.T($"Партида #{batch.Code}", $"Batch #{batch.Code}"),
            Font = Theme.Title,
            ForeColor = Theme.Ink
        });

        return host;
    }

    /// <summary>Шапката: кой я е донесъл, кога, с какъв документ и какъв код.</summary>
    private string Describe(Batch batch)
    {
        var who = batch.PartnerName.Length > 0
            ? batch.Partner
            : batch.Eik.Length > 0 ? batch.Eik : Lang.T("клиент не е посочен", "no client stated");

        var parts = new List<string>
        {
            who,
            Code(batch.Ewc),
            Lang.T($"приета на {batch.ReceivedOn.ToString(ValueConverter.BulgarianDateFormat)}",
                   $"received on {batch.ReceivedOn.ToString(ValueConverter.BulgarianDateFormat)}")
        };

        if (batch.Document.Length > 0) parts.Add(batch.Document);
        if (batch.Opening) parts.Add(Lang.T("заварено количество", "carried-in quantity"));

        parts.Add(Lang.T($"площадка {batch.Stagenum}", $"site {batch.Stagenum}"));

        return string.Join(" · ", parts);
    }

    private Control BuildSteps()
    {
        var host = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, 0, Theme.GapLarge, Theme.Gap)
        };

        var frame = new Card
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(1),
            Fill = Theme.Canvas,
            Outline = Theme.Border
        };

        _steps.Dock = DockStyle.Fill;
        _steps.Font = Theme.Base;
        _steps.BackgroundColor = Theme.Canvas;
        _steps.BorderStyle = BorderStyle.None;
        _steps.EnableHeadersVisualStyles = false;
        _steps.ReadOnly = true;
        _steps.AllowUserToAddRows = false;
        _steps.AllowUserToDeleteRows = false;
        _steps.AllowUserToResizeRows = false;
        _steps.AllowUserToOrderColumns = false;
        _steps.RowHeadersVisible = false;
        _steps.MultiSelect = false;
        _steps.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _steps.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText;
        _steps.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _steps.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _steps.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;

        _steps.ColumnHeadersDefaultCellStyle.BackColor = Theme.Surface;
        _steps.ColumnHeadersDefaultCellStyle.ForeColor = Theme.InkStrong;
        _steps.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.Surface;
        _steps.ColumnHeadersDefaultCellStyle.SelectionForeColor = Theme.InkStrong;
        _steps.ColumnHeadersDefaultCellStyle.Font = Theme.Bold;
        _steps.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 6, 6, 6);
        _steps.DefaultCellStyle.BackColor = Theme.Canvas;
        _steps.DefaultCellStyle.ForeColor = Theme.Ink;
        _steps.DefaultCellStyle.SelectionBackColor = Theme.AccentLight;
        _steps.DefaultCellStyle.SelectionForeColor = Theme.Ink;
        _steps.DefaultCellStyle.Padding = new Padding(6, 4, 6, 4);
        _steps.AlternatingRowsDefaultCellStyle.BackColor = Theme.Surface;
        _steps.AlternatingRowsDefaultCellStyle.SelectionBackColor = Theme.AccentLight;
        _steps.AlternatingRowsDefaultCellStyle.SelectionForeColor = Theme.Ink;
        _steps.GridColor = Theme.Border;

        var line = Math.Max(1, _steps.Font.Height);
        _steps.RowTemplate.Height = line + 12;
        _steps.ColumnHeadersHeight = line + 20;

        Column(DateColumn, Lang.T("Дата", "Date"), 110);
        Column(KindColumn, Lang.T("Движение", "Movement"), 130);
        Column(CodeColumn, Lang.T("Код", "Code"), 280, fill: true);

        // Заради този стълб се отваря партидата: „моите дванайсет тона — на кого
        // отидоха“. Получателят стои в самото движение (схемата го иска), а
        // дотук не стигаше: пътят казваше КОЛКО е излязло, но не и КЪМ КОГО.
        Column(ReceiverColumn, Lang.T("Получател", "Receiver"), 240);
        Column(EikColumn, Lang.T("ЕИК", "VAT id"), 130);
        Column(ContractColumn, Moves.ContractTitle, 160);

        Column(ShareColumn, Lang.T("Дял, т", "Share, t"), 130, right: true);
        Column(NoteColumn, Lang.T("Бележка", "Note"), 180);

        frame.Controls.Add(_steps);

        host.Controls.Add(frame);
        host.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(26),
            Text = Lang.T("Какво е минало през нея", "What went through it"),
            Font = Theme.SmallBold,
            ForeColor = Theme.InkFaint
        });

        return host;
    }

    private void Column(string name, string header, int width, bool right = false, bool fill = false)
    {
        var column = new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            Width = width,
            SortMode = DataGridViewColumnSortMode.NotSortable,
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
            column.MinimumWidth = Theme.Scaled(180);
        }

        _steps.Columns.Add(column);
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

        var close = Theme.PrimaryButton(Lang.T("Затвори", "Close"));
        close.Height = buttonHeight;
        close.Dock = DockStyle.Right;
        close.Click += (_, _) => DialogResult = DialogResult.OK;

        bar.Controls.Add(close.FitWidth());

        // Прозорецът само показва: Esc и Enter правят едно и също — затварят.
        CancelButton = close;
        AcceptButton = close;

        return bar;
    }

    // ------------------------------------------------------------- пълнене

    private void Fill(Workspace workspace)
    {
        foreach (var step in _trail.Steps)
        {
            var index = _steps.Rows.Add();
            var row = _steps.Rows[index];

            row.Cells[DateColumn].Value = step.Move.Date.ToString(ValueConverter.BulgarianDateFormat);
            row.Cells[KindColumn].Value = Moves.Title(step.Move.Kind);
            row.Cells[CodeColumn].Value = Code(step.Move.Ewc);

            // Само предаването има получател. На останалите три вида стълбът
            // стои празен, вместо да повтаря нещо друго на негово място.
            if (step.Move.Kind == MoveKind.Submitted)
            {
                var fields = workspace.Ledger.Fields(step.Move.Id);

                row.Cells[ReceiverColumn].Value = fields.GetValueOrDefault("name_receiver", string.Empty);
                row.Cells[EikColumn].Value = fields.GetValueOrDefault("pin_receiver", string.Empty);
                row.Cells[ContractColumn].Value = workspace.Ledger.Contract(step.Move.Id);
            }

            row.Cells[ShareColumn].Value = SplitMath.Amount(step.Amount);
            row.Cells[NoteColumn].Value = Mark(workspace, step);
        }

        _summary.Text = Weigh();
    }

    /// <summary>
    /// Какво отличава стъпката: заварено, пряко предаване, или бележката ѝ.
    ///
    /// Кратко нарочно — колоната е последна и тясна, а дългият текст тук само би
    /// изтласкал кода наляво.
    /// </summary>
    private static string Mark(Workspace workspace, TrailStep step)
    {
        if (step.Move.Opening) return Lang.T("заварено", "carried in");

        var note = workspace.Ledger.Note(step.Move.Id);

        if (note.Length > 40) note = note[..38].TrimEnd() + "…";

        if (!step.Move.Direct) return note;

        var direct = Lang.T("пряко предаване", "direct handover");

        return note.Length > 0 ? $"{direct} · {note}" : direct;
    }

    internal int RowCount => _steps.Rows.Count;

    internal string ContractAt(int row) => _steps.Rows[row].Cells[ContractColumn].Value?.ToString() ?? string.Empty;

    /// <summary>Четирите числа и какво още стои по нея.</summary>
    private string Weigh()
    {
        if (_trail.IsEmpty)
            return Lang.T(
                "Към тази партида не сочи нито едно движение — приемането ѝ е махнато.",
                "No movement points at this batch — its receipt has been removed.");

        var received = _trail.Total(MoveKind.Received);
        var treated = _trail.Total(MoveKind.Treated);
        var generated = _trail.Total(MoveKind.Generated);
        var submitted = _trail.Total(MoveKind.Submitted);

        // Само когато го има: повечето партиди никога не минават през него.
        var recycled = _trail.Total(MoveKind.Recycled) is > 0 and var amount
            ? Lang.T($" · рециклирани {SplitMath.Amount(amount)} т", $" · recycled {SplitMath.Amount(amount)} t")
            : string.Empty;

        return Lang.T(
            $"Получени {SplitMath.Amount(received)} т · третирани {SplitMath.Amount(treated)} т · " +
            $"образувани {SplitMath.Amount(generated)} т · предадени {SplitMath.Amount(submitted)} т" + recycled +
            Environment.NewLine +
            $"Още стоят: „Получен“ {SplitMath.Amount(_trail.InStore(StoreKind.Received))} т · " +
            $"„Третиран“ {SplitMath.Amount(_trail.InStore(StoreKind.Treated))} т · " +
            $"„Образуван“ {SplitMath.Amount(_trail.InStore(StoreKind.Formed))} т",

            $"Received {SplitMath.Amount(received)} t · treated {SplitMath.Amount(treated)} t · " +
            $"generated {SplitMath.Amount(generated)} t · handed over {SplitMath.Amount(submitted)} t" + recycled +
            Environment.NewLine +
            $"Still held: “Received” {SplitMath.Amount(_trail.InStore(StoreKind.Received))} t · " +
            $"“Treated” {SplitMath.Amount(_trail.InStore(StoreKind.Treated))} t · " +
            $"“Formed” {SplitMath.Amount(_trail.InStore(StoreKind.Formed))} t");
    }

    private string Code(string ewc) =>
        _names.TryGetValue(ValueConverter.NormalizeCode(ewc), out var name) ? $"{ewc} — {name}" : ewc;

    // ------------------------------------------------------------- за теста

    internal int StepCount => _steps.Rows.Count;

    internal string SummaryText => _summary.Text;

    internal string KindAt(int row) => _steps.Rows[row].Cells[KindColumn].Value?.ToString() ?? string.Empty;

    internal string ShareAt(int row) => _steps.Rows[row].Cells[ShareColumn].Value?.ToString() ?? string.Empty;
}
