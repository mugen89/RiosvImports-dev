using RiosvImports.Core.Ledger;
using RiosvImports.Core.Localization;
using RiosvImports.Core.Values;

namespace RiosvImports.App.Shell;

/// <summary>Какъв е тонът на присъдата — оттам и цветът ѝ.</summary>
internal enum VerdictTone
{
    /// <summary>Няма какво да се каже — празен месец.</summary>
    Quiet,

    /// <summary>Отчетът излиза и няма бележки.</summary>
    Ok,

    /// <summary>Отчетът излиза, но нещо трябва да се погледне.</summary>
    Note,

    /// <summary>Нещо спира — отчета или затварянето на месеца.</summary>
    Stop
}

/// <summary>
/// Присъдата за един месец: може ли отчет, а ако не — защо и какво да се направи.
///
/// На „Месец“ дотук стоеше първата бележка от списъка: „3 спиращи · Код 170405:
/// на 12.07…“. Тя не казваше дали спира отчетът или само затварянето, нито къде
/// се поправя — а отчетът не излизаше и човекът не знаеше защо. Тук отговорът е
/// подреден като въпроса: първо „може ли“, после „защо не“, накрая „какво да
/// направя“ — с мястото, където се прави.
///
/// Правилото е отделено от страницата (правило 4): проверява се с тест, без
/// нито един прозорец.
/// </summary>
internal sealed record MonthVerdict(string Text, string Fix, VerdictTone Tone)
{
    /// <param name="period">Месецът, който се гледа.</param>
    /// <param name="moves">Колко движения има в него.</param>
    /// <param name="closed">Затворен ли е.</param>
    /// <param name="issues">Какво намира проверката (<see cref="Reconcile.Check"/>).</param>
    /// <param name="quarter">
    /// Кои месеци от тримесечието чакат в „Третиран“. Съставянето предлага
    /// тримесечието по подразбиране, и готов месец в неготово тримесечие е
    /// точно случаят, в който „защо не излиза“ няма отговор на екрана.
    /// </param>
    public static MonthVerdict For(
        LedgerPeriod period,
        int moves,
        bool closed,
        IReadOnlyList<LedgerIssue> issues,
        IReadOnlyList<WaitingMonth> quarter)
    {
        var month = period.Title();
        var waiting = quarter.FirstOrDefault(w => w.Month == period);
        var stopping = issues.Where(i => i.Blocks && !i.StopsReport).ToList();
        var notes = issues.Where(i => !i.Blocks).ToList();

        if (closed)
            return new MonthVerdict(
                Lang.T($"{Capital(month)} е затворен — не приема нови записи.",
                       $"{Capital(month)} is closed — it accepts no new entries."),
                Lang.T("Отваря се наново от „Настройки“ → „Затворени месеци…“.",
                       "It is reopened from “Settings” → “Closed months…”."),
                VerdictTone.Ok);

        if (waiting is not null)
            return new MonthVerdict(
                Lang.T($"Отчет за {month} не може да се състави: в склад „Третиран“ чакат {SplitMath.Amount(waiting.Amount)} т.",
                       $"No report for {month} can be composed: {SplitMath.Amount(waiting.Amount)} t wait in the “Treated” store."),
                Lang.T($"Чакат {waiting.Named(2)}. ", $"Waiting: {waiting.Named(2)}. ") +
                new LedgerIssue(LedgerProblem.Waiting, string.Empty, waiting.Amount).Fix,
                VerdictTone.Stop);

        if (stopping.Count > 0)
        {
            var first = stopping[0];

            return new MonthVerdict(
                stopping.Count == 1
                    ? Lang.T($"Месецът не може да се затвори: {first.Text}",
                             $"The month cannot be closed: {first.Text}")
                    : Lang.T($"Месецът не може да се затвори — {stopping.Count} спиращи. Първото: {first.Text}",
                             $"The month cannot be closed — {stopping.Count} blocking. The first: {first.Text}"),
                first.Fix + Lang.T(" Отчетът се съставя и така, но с тези количества.",
                                   " A report can still be composed, but with these quantities."),
                VerdictTone.Stop);
        }

        // Месецът е наред, но съставянето предлага тримесечието — и то може да не е.
        var elsewhere = quarter.Where(w => w.Month != period).ToList();

        var later = elsewhere.Count == 0
            ? string.Empty
            : Lang.T(
                $" Тримесечието още не е готово: {string.Join(", ", elsewhere.Select(w => $"{w.Month.Title()} — чакат {SplitMath.Amount(w.Amount)} т в „Третиран“"))}. " +
                $"Отчет само за {month} излиза.",
                $" The quarter is not ready yet: {string.Join(", ", elsewhere.Select(w => $"{w.Month.Title()} — {SplitMath.Amount(w.Amount)} t waiting in “Treated”"))}. " +
                $"A report for {month} alone can be composed.");

        if (moves == 0 && notes.Count == 0)
            return new MonthVerdict(
                Lang.T($"През {month} няма движения.", $"There are no movements in {month}."),
                Lang.T("Въвеждат се с копчетата горе. Файл от „Деметра“ или от наш шаблон се внася от „Склад“ → „Внеси…“.",
                       "Enter them with the buttons above. A Demetra file or one of our templates is imported from “Store” → “Import…”.") + later,
                VerdictTone.Quiet);

        if (notes.Count > 0)
            return new MonthVerdict(
                notes.Count == 1
                    ? Lang.T($"Отчет за {month} може да се състави · една бележка: {notes[0].Text}",
                             $"A report for {month} can be composed · one note: {notes[0].Text}")
                    : Lang.T($"Отчет за {month} може да се състави · {notes.Count} бележки. Първата: {notes[0].Text}",
                             $"A report for {month} can be composed · {notes.Count} notes. The first: {notes[0].Text}"),
                notes[0].Fix + later,
                VerdictTone.Note);

        return new MonthVerdict(
            Lang.T($"Отчет за {month} може да се състави. Няма намерени несъответствия.",
                   $"A report for {month} can be composed. No discrepancies found."),
            later.Length > 0
                ? later.TrimStart()
                : Lang.T("„Състави отчет…“ прави книгата за месеца или за тримесечието.",
                         "“Compose report…” makes the book for the month or for the quarter."),
            elsewhere.Count > 0 ? VerdictTone.Note : VerdictTone.Ok);
    }

    /// <summary>Месецът в началото на изречение — „Юли 2026“, не „юли 2026“.</summary>
    private static string Capital(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0], System.Globalization.CultureInfo.CurrentCulture) + text[1..];

    /// <summary>Цветът на присъдата — от ролята, не от името (правило 7).</summary>
    public Color Colour => Tone switch
    {
        VerdictTone.Stop => Theme.Error,
        VerdictTone.Note => Theme.Warn,
        VerdictTone.Ok => Theme.Ok,
        _ => Theme.InkMuted
    };
}
