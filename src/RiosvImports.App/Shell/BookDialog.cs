using RiosvImports.App.Controls;
using RiosvImports.Core.Ledger;
using RiosvImports.Core.Localization;
using RiosvImports.Core.Values;

namespace RiosvImports.App.Shell;

/// <summary>
/// Какво ще влезе в отчета за един период — и какво би го спряло.
///
/// <paramref name="Waiting"/> са месеците, в чийто край нещо чака в „Третиран“:
/// само те спират съставянето. <paramref name="Problems"/> са най-честите грешки,
/// които проверката по схемата ще намери в съставения отчет — те не спират, но
/// казани тук, се поправят в „Месец“, преди отчетът да съществува, а не в него.
/// </summary>
internal sealed record BookWeight(
    int Rows,
    int Missing,
    IReadOnlyList<WaitingMonth> Waiting,
    IReadOnlyList<string> Problems,
    int ProblemCount)
{
    /// <summary>Дали съставянето е спряно.</summary>
    public bool Blocked => Waiting.Count > 0;

    public static BookWeight Empty => new(0, 0, [], [], 0);
}

/// <summary>
/// Какъв период да покрие съставеният отчет.
///
/// Изборът е между месеца, който се гледа, и тримесечието, в което той пада.
/// Двете съществуват, защото служат на различни неща: <b>тримесечието се
/// подава</b> — схемата иска <c>quarter</c> и НИСО приема на тримесечие, —
/// а <b>месецът се проверява</b>, преди да се затвори.
///
/// Затова по подразбиране е тримесечието: то е това, което накрая тръгва
/// нанякъде.
///
/// Когато отчет не може да се състави, прозорецът казва ЗАЩО — в кой месец,
/// колко и кои купчини, и къде се поправя — и копчето „Състави“ угасва. Дотук
/// копчето работеше и чак след натискането излизаше съобщение, а самият
/// прозорец казваше само „чакат 1,250 т“, без месец.
/// </summary>
internal sealed class BookDialog : Dialog
{
    private readonly SegmentedControl _scope = new();
    private readonly Label _preview = new();
    private readonly LedgerPeriod _period;
    private readonly Func<LedgerPeriod, LedgerPeriod, BookWeight> _weigh;

    /// <summary>Претегленото по период: превключването напред-назад не смята наново.</summary>
    private readonly Dictionary<(LedgerPeriod, LedgerPeriod), BookWeight> _weighed = [];

    private AppButton? _create;

    public BookDialog(LedgerPeriod period, Func<LedgerPeriod, LedgerPeriod, BookWeight> weigh)
    {
        _period = period;
        _weigh = weigh;

        Text = Lang.T("Съставяне на отчет", "Compose a report");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        // По-висок от преди: прозорецът казва и защо не излиза, и какво ще намери
        // проверката. На 1366×700 четирите реда грешки не се побираха в 460.
        ClientSize = new Size(Theme.Scaled(720), Theme.Scaled(500));
        BackColor = Theme.Canvas;
        Font = Theme.Base;

        Controls.Add(BuildBody());
        Controls.Add(BuildButtons());
        Controls.Add(BuildTitle());

        // Тримесечието по подразбиране — освен ако то е спряно, а месецът не:
        // тогава се отваря онова, което може да излезе, и се казва защо.
        _scope.SelectedIndex = 1;

        if (Weight(Quarter().From, Quarter().To).Blocked && !Weight(_period, _period).Blocked)
            _scope.SelectedIndex = 0;

        Describe();

        Theme.Apply(this);
    }

    /// <summary>Първият месец от избрания период.</summary>
    public LedgerPeriod From => _scope.SelectedIndex == 1 ? Quarter().From : _period;

    /// <summary>Последният месец от избрания период.</summary>
    public LedgerPeriod To => _scope.SelectedIndex == 1 ? Quarter().To : _period;

    private (LedgerPeriod From, LedgerPeriod To) Quarter()
    {
        var first = (_period.Quarter - 1) * 3 + 1;

        return (new LedgerPeriod(_period.Year, first), new LedgerPeriod(_period.Year, first + 2));
    }

    private BookWeight Weight(LedgerPeriod from, LedgerPeriod to)
    {
        if (_weighed.TryGetValue((from, to), out var known)) return known;

        BookWeight weight;

        try
        {
            weight = _weigh(from, to);
        }
        catch (Exception ex)
        {
            weight = BookWeight.Empty with
            {
                Problems = [Lang.T("Проверката не можа да мине: ", "The check could not run: ") + ex.Message],
                ProblemCount = 1
            };
        }

        _weighed[(from, to)] = weight;

        return weight;
    }

    private Control BuildTitle()
    {
        var host = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(64),
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, Theme.Gap, Theme.GapLarge, 0)
        };

        host.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = Lang.T("Отчетна книга за оползотворяване", "Recovery and disposal book"),
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

        _scope.SetItems([_period.Title(), Lang.T("Тримесечие", "Quarter")]);
        _scope.Dock = DockStyle.Top;
        _scope.Height = Theme.Scaled(40);
        _scope.SelectedIndexChanged += (_, _) => Describe();

        _preview.Dock = DockStyle.Fill;
        _preview.Font = Theme.Base;
        _preview.ForeColor = Theme.InkMuted;
        _preview.Padding = new Padding(0, Theme.Gap, 0, 0);

        host.Controls.Add(_preview);
        host.Controls.Add(_scope);

        return host;
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

        var create = Theme.PrimaryButton(Lang.T("Състави", "Compose"));
        create.Height = buttonHeight;
        create.Margin = Padding.Empty;
        create.Click += (_, _) =>
        {
            if (!Weight(From, To).Blocked) DialogResult = DialogResult.OK;
        };
        _create = create;

        var cancel = Theme.GhostButton(Lang.T("Отказ", "Cancel"));
        cancel.Height = buttonHeight;
        cancel.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;

        tools.Controls.Add(create.FitWidth());
        tools.Controls.Add(cancel.FitWidth());

        bar.Controls.Add(tools);
        AcceptButton = create;
        CancelButton = cancel;

        return bar;
    }

    /// <summary>
    /// Казва какво ще влезе — преди да е влязло.
    ///
    /// Броят редове е половината отговор. Другата половина е колко от тях са
    /// без посочен произход: те ще минат проверката на НИСО, но няма да могат
    /// да отговорят на чий е бил отпадъкът, а точно затова се води склад.
    /// </summary>
    private void Describe()
    {
        var weight = Weight(From, To);

        var period = From == To
            ? From.Title()
            : Lang.T($"{From.Title()} — {To.Title()}", $"{From.Title()} — {To.Title()}");

        var text = Lang.T(
            $"Период: {period}{Environment.NewLine}" +
            $"Движения за подаване: {weight.Rows}",
            $"Period: {period}{Environment.NewLine}" +
            $"Movements to submit: {weight.Rows}");

        var gap = Environment.NewLine + Environment.NewLine;

        if (weight.Blocked)
        {
            // Чакащото в склад „Третиран“ спира съставянето, а не само го
            // отбелязва: отчет, който твърди, че отпадъкът е минал през
            // площадката, докато той още стои на нея, е неверен отчет.
            text += gap + Lang.T(
                "Отчет не може да се състави — в склад „Третиран“ чака количество:",
                "No report can be composed — a quantity waits in the “Treated” store:");

            foreach (var month in weight.Waiting)
                text += Environment.NewLine + Lang.T(
                    $"· {month.Month.Title()} — {SplitMath.Amount(month.Amount)} т: {month.Named(2)}",
                    $"· {month.Month.Title()} — {SplitMath.Amount(month.Amount)} t: {month.Named(2)}");

            text += gap + new LedgerIssue(LedgerProblem.Waiting, string.Empty, 0m).Fix;

            // Тримесечието е спряно, а месецът — не: това е изходът, и той се казва.
            if (From != To && !Weight(_period, _period).Blocked)
                text += gap + Lang.T(
                    $"Отчет само за {_period.Title()} може да се състави — изберете го горе.",
                    $"A report for {_period.Title()} alone can be composed — pick it above.");
        }
        else if (weight.Rows == 0)
        {
            text += gap + Lang.T(
                "През този период няма движения. Съставеният отчет ще е празен.",
                "There are no movements in this period. The composed report will be empty.");
        }
        else
        {
            if (weight.Missing > 0)
                text += gap + Lang.T(
                    $"{weight.Missing} от тях са без посочен произход. Отчетът ще мине, но за тези " +
                    "количества складът няма да може да каже от кого са.",
                    $"{weight.Missing} of them have no stated origin. The report will pass, but the store " +
                    "will not be able to say who those quantities came from.");

            // Грешките по схемата не спират съставянето — отчетът се поправя и в
            // редактора. Казани тук, те се поправят при източника, в „Месец“, и
            // не се връщат при следващото съставяне.
            if (weight.ProblemCount > 0)
            {
                text += gap + Lang.T(
                    $"Проверката по схемата ще намери {weight.ProblemCount} грешки. Отчетът се съставя, " +
                    "но не става файл, докато не се поправят:",
                    $"The schema check will find {weight.ProblemCount} errors. The report is composed, " +
                    "but cannot become a file until they are fixed:");

                foreach (var problem in weight.Problems) text += Environment.NewLine + "· " + problem;
            }
        }

        _preview.Text = text;

        _preview.ForeColor = weight.Blocked
            ? Theme.Error
            : (weight.Missing > 0 || weight.ProblemCount > 0) && weight.Rows > 0 ? Theme.Warn : Theme.InkMuted;

        if (_create is not null) _create.Enabled = !weight.Blocked;
    }

    /// <summary>Заглавието, което съставеният отчет получава.</summary>
    public string SuggestedTitle(string company) => Lang.T(
        $"Оползотворяване {Title()} — {company}",
        $"Recovery {Title()} — {company}");

    private string Title() => From == To
        ? $"{LedgerPeriod.Short(From.Month)} {From.Year}"
        : $"{From.Quarter}-во тримесечие {From.Year}";

    /// <summary>Периодът като дати — заглавната част на отчета иска тях.</summary>
    public string PeriodStart => From.First.ToString(ValueConverter.BulgarianDateFormat);

    public string PeriodEnd => To.Last.ToString(ValueConverter.BulgarianDateFormat);

    // ------------------------------------------------------------- за теста

    /// <summary>Какво казва прозорецът за избрания период.</summary>
    internal string PreviewText => _preview.Text;

    /// <summary>Може ли да се натисне „Състави“.</summary>
    internal bool ComposeOffered => _create?.Enabled ?? false;

    /// <summary>0 — месецът, 1 — тримесечието.</summary>
    internal int ScopeIndex => _scope.SelectedIndex;

    internal void UseScope(int index)
    {
        _scope.SelectedIndex = index;
        Describe();
    }
}
