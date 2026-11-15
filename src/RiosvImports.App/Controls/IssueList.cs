using System.Drawing.Drawing2D;
using RiosvImports.Core.Import;
using RiosvImports.Core.Localization;

namespace RiosvImports.App.Controls;

/// <summary>
/// Един ред в списъка с проблеми.
///
/// <paramref name="Label"/> заменя думата за тежестта, когато „грешка“ не казва
/// достатъчно: в проверката на месеца важното е КАКВО спира — отчетът или само
/// затварянето. <paramref name="Tag"/> носи онова, зад което стои редът, за да
/// може двойното щракване да води дотам.
/// </summary>
internal sealed record IssueEntry(
    IssueSeverity Severity,
    string Location,
    string Message,
    string Label = "",
    object? Tag = null)
{
    public static IssueEntry From(Issue issue) => new(issue.Severity, issue.Location, issue.Message);

    public static IssueEntry Schema(string message) =>
        new(IssueSeverity.Error, Lang.T("Схема", "Schema"), message);

    /// <summary>Думата отляво — подадената или тази на тежестта.</summary>
    public string Word => Label.Length > 0
        ? Label
        : Severity == IssueSeverity.Error ? Lang.T("Грешка", "Error") : Lang.T("Бележка", "Note");
}

/// <summary>
/// Списъкът с намерените проблеми.
///
/// Беше обикновен ListView и това си личеше: заглавията на колоните му се
/// рисуват от Windows и остават светли, каквото и да им зададеш. При тъмна
/// тема цялата долна част на екрана светваше в бяло.
///
/// Тук всичко се рисува на ръка, както в останалите списъци: цветна отметка
/// за тежестта, мястото приглушено и самото съобщение — това, което се чете.
/// </summary>
internal sealed class IssueList : ListBox
{
    /// <summary>Колко място взема думата отляво — мери се по най-дългата.</summary>
    private int _wordWidth = Theme.Scaled(70);

    public IssueList()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = Theme.Scaled(34);
        BorderStyle = BorderStyle.None;
        BackColor = Theme.Canvas;
        ForeColor = Theme.Ink;
        Font = Theme.Base;
        IntegralHeight = false;
        HorizontalScrollbar = false;
    }

    public IssueEntry? Selected => SelectedItem as IssueEntry;

    public void Fill(IEnumerable<IssueEntry> entries)
    {
        var list = entries.ToList();

        // По най-дългата дума, но не по-тясно от досегашното: „Грешка“ и
        // „Бележка“ се побираха в седемдесет, „Спира отчета“ — не.
        _wordWidth = Math.Max(Theme.Scaled(70), list.Count == 0
            ? 0
            : list.Max(e => TextRenderer.MeasureText(e.Word, Theme.SmallBold).Width) + Theme.Scaled(6));

        BeginUpdate();
        Items.Clear();

        foreach (var entry in list) Items.Add(entry);

        EndUpdate();
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || Items[e.Index] is not IssueEntry entry) return;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        var bounds = e.Bounds;

        using (var background = new SolidBrush(selected ? Theme.AccentLight : Theme.Canvas))
            g.FillRectangle(background, bounds);

        var error = entry.Severity == IssueSeverity.Error;
        var accent = error ? Theme.Error : Theme.Warn;

        // Цветна ивица отляво вместо иконка: вижда се с крайчеца на окото и
        // не изисква място, което трябва на съобщението.
        using (var stripe = new SolidBrush(accent))
            g.FillRectangle(stripe, bounds.Left + Theme.Scaled(6), bounds.Top + Theme.Scaled(7), Theme.Scaled(3), bounds.Height - Theme.Scaled(14));

        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;

        var word = bounds.Left + Theme.Scaled(18);
        var place = word + _wordWidth + Theme.Scaled(6);
        var message = place + Theme.Scaled(198);

        TextRenderer.DrawText(g, entry.Word, Theme.SmallBold,
            new Rectangle(word, bounds.Top, _wordWidth, bounds.Height), accent, flags);

        TextRenderer.DrawText(g, entry.Location, Theme.Small,
            new Rectangle(place, bounds.Top, Theme.Scaled(190), bounds.Height), Theme.InkFaint, flags);

        TextRenderer.DrawText(g, entry.Message, Theme.Base,
            new Rectangle(message, bounds.Top, bounds.Width - (message - bounds.Left) - Theme.Scaled(8), bounds.Height),
            selected ? Theme.Ink : Theme.InkStrong, flags);
    }
}
