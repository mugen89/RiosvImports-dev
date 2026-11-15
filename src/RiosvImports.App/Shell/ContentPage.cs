namespace RiosvImports.App.Shell;

/// <summary>
/// Обща основа на трите страници: държи съдържанието в средата и с разумна
/// ширина.
///
/// На широк монитор разтегнатият на цялата ширина текст е неудобен за четене,
/// а таблица с двайсет колони, залепена вляво, изглежда недовършена. Затова
/// съдържанието има таван на ширината и стои по средата — при тесен прозорец
/// таванът просто не се стига и нищо не се променя.
/// </summary>
internal abstract class ContentPage : UserControl
{
    private int _side = -1;

    protected ContentPage(int maxContentWidth = 1180)
    {
        // През мащаба: таванът е мерен при 100 %, а при 125 % буквите в лентите
        // растат и същото число почва да ги пренася на втори ред.
        MaxContentWidth = Theme.Scaled(maxContentWidth);

        BackColor = Theme.Canvas;
        AutoScroll = true;
        Padding = new Padding(Theme.PageMargin, Theme.GapLarge, Theme.PageMargin, Theme.Gap);
    }

    protected int MaxContentWidth { get; }

    /// <summary>Ширината, която съдържанието наистина получава.</summary>
    protected int ContentWidth => Math.Max(320, Math.Min(MaxContentWidth, Width - 2 * Theme.PageMargin));

    /// <summary>
    /// Слага курсора в търсенето на страницата — ако тя има такова.
    ///
    /// Ctrl+F се поема от главния прозорец, за да работи и когато курсорът
    /// стои в лентата отляво или никъде. Страница без търсене връща „не“.
    /// </summary>
    public virtual bool FocusSearch() => false;

    protected override void OnLayout(LayoutEventArgs e)
    {
        var side = Math.Max(Theme.PageMargin, (Width - MaxContentWidth) / 2);

        // Присвояването на Padding отключва ново подреждане; без тази проверка
        // двете се викат взаимно до безкрай.
        if (side != _side)
        {
            _side = side;
            Padding = new Padding(side, Theme.GapLarge, side, Theme.Gap);
        }

        base.OnLayout(e);
    }
}
