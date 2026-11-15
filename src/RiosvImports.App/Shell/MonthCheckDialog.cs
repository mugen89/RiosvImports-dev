using RiosvImports.App.Controls;
using RiosvImports.Core.Import;
using RiosvImports.Core.Ledger;
using RiosvImports.Core.Localization;
using RiosvImports.Core.Values;

namespace RiosvImports.App.Shell;

/// <summary>Какво поиска човекът от проверката, когато я затвори.</summary>
internal enum CheckAction
{
    None,

    /// <summary>Да види реда в таблицата на месеца.</summary>
    ShowMove,

    /// <summary>Да отвори склад „Третиран“ към края на месеца — там се образува.</summary>
    OpenTreated
}

/// <summary>
/// Всичко, което проверката намира в един месец — и какво да се направи за всяко.
///
/// Присъдата на „Месец“ е един ред и казва най-важното. Тук стои целият списък,
/// подреден по тежест: първо онова, което спира отчета, после онова, което спира
/// затварянето, накрая бележките. Списъкът е същият, с който редакторът показва
/// грешките на отчета (<see cref="IssueList"/>) — две подредби на едно и също би
/// било втори начин да се чете едно и също нещо.
///
/// Под списъка стои избраното с думи и с мястото, където се поправя. Оттам се
/// стига и до самото място: двойното щракване води до реда, а „Отвори
/// „Третиран““ — до склада, в който се образува.
/// </summary>
internal sealed class MonthCheckDialog : Dialog
{
    private readonly IssueList _list = new();
    private readonly Label _detail = new();
    private readonly AppButton _show;
    private readonly AppButton _treated;
    private readonly WaitingMonth? _waiting;

    public MonthCheckDialog(
        string title,
        MonthVerdict verdict,
        IReadOnlyList<LedgerIssue> issues,
        WaitingMonth? waiting)
    {
        _waiting = waiting;

        Text = title;
        Font = Theme.Base;
        BackColor = Theme.Canvas;
        MinimumSize = new Size(Theme.Scaled(760), Theme.Scaled(460));
        ClientSize = new Size(Theme.Scaled(1000), Theme.Scaled(600));
        MaximizeBox = false;
        KeyPreview = true;

        _show = Theme.SecondaryButton(Lang.T("Покажи реда", "Show the row"));
        _treated = Theme.SecondaryButton(Lang.T("Отвори „Третиран“", "Open “Treated”"));

        Controls.Add(BuildBody());
        Controls.Add(BuildDetail());
        Controls.Add(BuildButtons());
        Controls.Add(BuildTitle(title, verdict));

        _list.Fill(Entries(issues));
        _list.SelectedIndexChanged += (_, _) => Explain();
        _list.DoubleClick += (_, _) => Go();

        if (_list.Items.Count > 0) _list.SelectedIndex = 0;

        Explain();

        Theme.Apply(this);
    }

    /// <summary>Какво поиска човекът — страницата го изпълнява, след като прозорецът се затвори.</summary>
    public CheckAction Action { get; private set; }

    /// <summary>Движението, до което да се стигне, когато <see cref="Action"/> е <see cref="CheckAction.ShowMove"/>.</summary>
    public long? MoveId { get; private set; }

    // ------------------------------------------------------------- устройство

    private static Control BuildTitle(string title, MonthVerdict verdict)
    {
        // Височината е сборът на трите реда и отстъпите — заглавие, присъда на два
        // реда и поправка на три. Изчислена „на око“, поправката оставаше с един
        // ред и се режеше точно там, където казва къде се поправя.
        var host = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Title.Height + Theme.Scaled(8) +
                     2 * Theme.Bold.Height + Theme.Scaled(6) +
                     3 * Theme.Base.Height + Theme.Scaled(8) +
                     Theme.Gap + Theme.GapSmall,
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, Theme.Gap, Theme.GapLarge, Theme.GapSmall)
        };

        // Закачените отгоре се редят наопаки: заглавието влиза последно.
        host.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = verdict.Fix,
            Font = Theme.Base,
            ForeColor = Theme.InkMuted,
            AutoEllipsis = true
        });

        host.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            Height = 2 * Theme.Bold.Height + Theme.Scaled(6),
            Text = verdict.Text,
            Font = Theme.Bold,
            ForeColor = verdict.Colour,
            AutoEllipsis = true
        });

        host.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            Height = Theme.Title.Height + Theme.Scaled(8),
            Text = title,
            Font = Theme.Title,
            ForeColor = Theme.Ink,
            AutoEllipsis = true
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

        var card = new Card
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(1),
            Fill = Theme.Canvas,
            Outline = Theme.Border
        };

        _list.Dock = DockStyle.Fill;
        card.Controls.Add(_list);
        host.Controls.Add(card);

        return host;
    }

    private Control BuildDetail()
    {
        // Шест реда: бележката, купчините зад нея и какво да се направи — всяко на
        // свой ред. Свити в четири, последните редове се сливаха в един.
        var host = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 6 * Theme.Base.Height + Theme.Scaled(16),
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, Theme.GapSmall, Theme.GapLarge, 0)
        };

        _detail.Dock = DockStyle.Fill;
        _detail.Font = Theme.Base;
        _detail.ForeColor = Theme.Ink;
        _detail.AutoEllipsis = true;

        host.Controls.Add(_detail);

        return host;
    }

    private Control BuildButtons()
    {
        var height = Theme.Scaled(38);

        var host = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = height + 2 * Theme.Gap,
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, Theme.Gap, Theme.GapLarge, Theme.Gap)
        };

        var close = Theme.PrimaryButton(Lang.T("Затвори", "Close"));
        close.Dock = DockStyle.Right;
        close.Height = height;
        close.FitWidth(16);
        close.Click += (_, _) => DialogResult = DialogResult.Cancel;

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Canvas
        };

        _show.Height = height;
        _show.Margin = new Padding(0, 0, Theme.GapSmall, 0);
        _show.Click += (_, _) => Go();

        _treated.Height = height;
        _treated.Margin = new Padding(0, 0, Theme.GapSmall, 0);
        _treated.Visible = _waiting is not null;
        _treated.Click += (_, _) =>
        {
            Action = CheckAction.OpenTreated;
            DialogResult = DialogResult.OK;
        };

        actions.Controls.Add(_treated.FitWidth(16));
        actions.Controls.Add(_show.FitWidth(16));

        host.Controls.Add(actions);
        host.Controls.Add(close);

        CancelButton = close;

        return host;
    }

    // ------------------------------------------------------------- пълнене

    /// <summary>
    /// Редовете на списъка — по тежест, както ги връща проверката.
    ///
    /// Думата отляво казва КАКВО спира, а не само „грешка“: чакащото в
    /// „Третиран“ спира отчета, недостигът — затварянето, а бележката — нищо.
    /// </summary>
    private static IEnumerable<IssueEntry> Entries(IReadOnlyList<LedgerIssue> issues)
    {
        if (issues.Count == 0)
        {
            yield return new IssueEntry(IssueSeverity.Info, string.Empty,
                Lang.T("Няма намерени несъответствия.", "No discrepancies found."),
                Lang.T("Наред", "Fine"));

            yield break;
        }

        foreach (var issue in issues
                     .OrderByDescending(i => i.StopsReport)
                     .ThenByDescending(i => i.Blocks)
                     .ThenBy(i => i.On))
        {
            var label = issue.StopsReport
                ? Lang.T("Спира отчета", "Stops the report")
                : issue.Blocks
                    ? Lang.T("Спира месеца", "Stops the month")
                    : Lang.T("Бележка", "Note");

            var place = string.Join(" · ", new[]
            {
                issue.On?.ToString(ValueConverter.BulgarianDateFormat) ?? string.Empty,
                issue.Ewc,
                issue.KindText
            }.Where(p => p.Length > 0));

            yield return new IssueEntry(
                issue.Blocks ? IssueSeverity.Error : IssueSeverity.Warning,
                place,
                issue.Text,
                label,
                issue);
        }
    }

    /// <summary>Избраното с думи — какво е и какво да се направи.</summary>
    private void Explain()
    {
        var issue = _list.Selected?.Tag as LedgerIssue;

        _show.Enabled = issue?.MoveId is not null;

        if (issue is null)
        {
            _detail.Text = _list.Selected?.Message ?? string.Empty;
            return;
        }

        var text = issue.Text;

        if (issue.Kind == LedgerProblem.Waiting && _waiting is not null)
            text += Environment.NewLine + Lang.T("Чакат: ", "Waiting: ") + _waiting.Named(4);

        _detail.Text = text + Environment.NewLine + Environment.NewLine +
                       Lang.T("Какво да се направи: ", "What to do: ") + issue.Fix;
    }

    /// <summary>Към избраното: реда в месеца — или склада, ако бележката е за него.</summary>
    private void Go()
    {
        if (_list.Selected?.Tag is not LedgerIssue issue) return;

        if (issue.MoveId is { } id)
        {
            Action = CheckAction.ShowMove;
            MoveId = id;
            DialogResult = DialogResult.OK;
            return;
        }

        if (issue.Kind == LedgerProblem.Waiting)
        {
            Action = CheckAction.OpenTreated;
            DialogResult = DialogResult.OK;
        }
    }

    // ------------------------------------------------------------- за теста

    internal int EntryCount => _list.Items.Count;

    internal IReadOnlyList<string> Words => [.. _list.Items.OfType<IssueEntry>().Select(e => e.Word)];

    internal string DetailText => _detail.Text;

    internal bool ShowOffered => _show.Enabled;

    /// <summary>Не от Visible: в непоказан прозорец то е „не“ за всичко (виж капаните).</summary>
    internal bool TreatedOffered => _waiting is not null;

    internal void Pick(int index)
    {
        _list.SelectedIndex = index;
        Explain();
    }

    /// <summary>Двойното щракване по избрания ред — без мишка.</summary>
    internal void Press() => Go();
}
