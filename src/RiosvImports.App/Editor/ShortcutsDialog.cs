using RiosvImports.App.Controls;
using RiosvImports.Core.Localization;

namespace RiosvImports.App.Editor;

/// <summary>
/// Списъкът с клавишните комбинации.
///
/// Отделен прозорец от упътването нарочно: упътването отговаря на „какво се
/// попълва тук“ и се чете веднъж, а този списък отговаря на „как да го направя
/// по-бързо“ и се отваря пак и пак, докато комбинациите влязат в пръстите.
///
/// Съдържанието е написано на ръка и това е рискът му: клавишите живеят в
/// ProcessCmdKey, а тук стои описанието им. Тестът сверява двете.
/// </summary>
internal sealed class ShortcutsDialog : Dialog
{
    /// <summary>
    /// Всичко, което програмата разбира. Групите следват екраните, а вътре в
    /// групата — от най-често използваното надолу.
    ///
    /// Свойство, а не статично поле: полето се пълни веднъж, при първото
    /// обръщение, с езика, който е бил в сила тогава. След смяна на езика
    /// списъкът щеше да остане на стария.
    /// </summary>
    internal static (string Group, string Keys, string What)[] Shortcuts =>
    [
        (Lang.T("Главният прозорец", "The main window"), "Ctrl+Q", Lang.T("Свива и отваря лентата отляво — с лявата ръка, без мишката", "Collapses and opens the left rail — left hand only, no mouse")),
        (Lang.T("Главният прозорец", "The main window"), "Ctrl+1 … Ctrl+8", Lang.T("Поредният екран от лентата: Ctrl+1 — „Начало“, Ctrl+2 — „Месец“, Ctrl+3 — „Склад“…", "The screen at that place in the rail: Ctrl+1 — “Home”, Ctrl+2 — “Month”, Ctrl+3 — “Store”…")),
        (Lang.T("Главният прозорец", "The main window"), "F6", Lang.T("В „Баланс“: свива и отваря панела с настройките отдясно", "In “Balance”: collapses and opens the settings panel on the right")),
        (Lang.T("Главният прозорец", "The main window"), "Ctrl+F", Lang.T("Търсенето на екрана: „Месец“, „Склад“, „Отчети“, „Списъци“", "The screen's search: “Month”, “Store”, “Reports”, “Lists”")),
        (Lang.T("Главният прозорец", "The main window"), "Ctrl+PageUp", Lang.T("В „Месец“: предишният месец", "In “Month”: the previous month")),
        (Lang.T("Главният прозорец", "The main window"), "Ctrl+PageDown", Lang.T("В „Месец“: следващият месец", "In “Month”: the next month")),
        (Lang.T("Главният прозорец", "The main window"), "F4", Lang.T("В „Третиране от склада“: отваря календара за датата", "In “Treatment from the store”: opens the date calendar")),
        (Lang.T("Главният прозорец", "The main window"), "Ctrl+V", Lang.T("В третирането: залепва колона количества от Excel; едно число върху избрана област го слага навсякъде", "In treatment: pastes a column of quantities from Excel; one value over a selection fills it")),
        (Lang.T("Главният прозорец", "The main window"), "Ctrl+D", Lang.T("В „Частично третиране“: повтаря количеството от реда отгоре", "In “Partial treatment”: repeats the quantity from the row above")),
        (Lang.T("Главният прозорец", "The main window"), "Delete", Lang.T("В „Частично третиране“: изчиства избраните клетки, редовете остават", "In “Partial treatment”: clears the selected cells, the rows stay")),
        (Lang.T("Главният прозорец", "The main window"), "Ctrl+F1", Lang.T("Този списък", "This list")),

        (Lang.T("Таблицата с данни", "The data table"), "F4", Lang.T("Отваря търсачка за дългите списъци — код по ЕКО, вид дейност", "Opens a search box for the long lists — EWC code, activity type")),
        (Lang.T("Таблицата с данни", "The data table"), "Ctrl+V", Lang.T("Поставя от Excel. Една стойност върху избрана област я слага навсякъде", "Pastes from Excel. One value over a selected area fills all of it")),
        (Lang.T("Таблицата с данни", "The data table"), "Ctrl+C", Lang.T("Копира избраното", "Copies the selection")),
        (Lang.T("Таблицата с данни", "The data table"), "Ctrl+D", Lang.T("Повтаря стойността от реда отгоре", "Repeats the value from the row above")),
        (Lang.T("Таблицата с данни", "The data table"), "Ctrl+Shift+D", Lang.T("Разлива първата избрана стойност надолу по селекцията", "Spreads the first selected value down the selection")),
        (Lang.T("Таблицата с данни", "The data table"), "Ctrl+Enter", Lang.T("Прави копие на реда точно под него", "Makes a copy of the row right below it")),
        (Lang.T("Таблицата с данни", "The data table"), "Ctrl+Shift+N", Lang.T("Номерира колоната — 1, 2, 3…", "Numbers the column — 1, 2, 3…")),
        (Lang.T("Таблицата с данни", "The data table"), "Delete", Lang.T("Изчиства избраните клетки, но пази редовете", "Clears the selected cells but keeps the rows")),
        (Lang.T("Таблицата с данни", "The data table"), "Ctrl+Delete", Lang.T("Изтрива избраните редове изцяло", "Deletes the selected rows entirely")),
        (Lang.T("Таблицата с данни", "The data table"), "Ctrl+-", Lang.T("Същото — както се трие ред в Excel", "The same — the way a row is deleted in Excel")),
        (Lang.T("Таблицата с данни", "The data table"), "Tab", Lang.T("Придвижва се надясно в реда, вместо да излиза от таблицата", "Moves right along the row instead of leaving the table")),

        (Lang.T("Прозорецът с отчета", "The report window"), "F1", Lang.T("Как се попълва този отчет", "How this report is filled in")),
        (Lang.T("Прозорецът с отчета", "The report window"), "F5", Lang.T("Проверява отчета", "Checks the report")),
        (Lang.T("Прозорецът с отчета", "The report window"), "Ctrl+S", Lang.T("Записва веднага — иначе записът е автоматичен", "Saves right away — otherwise the save is automatic")),
        (Lang.T("Прозорецът с отчета", "The report window"), "F6", Lang.T("Отваря и затваря помощните полета отстрани", "Opens and closes the helper fields on the side")),
        (Lang.T("Прозорецът с отчета", "The report window"), "Ctrl+F1", Lang.T("Този списък", "This list")),
        (Lang.T("Прозорецът с отчета", "The report window"), "Ctrl+Tab", Lang.T("Следваща таблица от отчета", "Next table of the report")),

        (Lang.T("Списъкът с отчети", "The report list"), "Enter", Lang.T("Отваря избрания отчет", "Opens the selected report")),
        (Lang.T("Списъкът с отчети", "The report list"), "F2", Lang.T("Преименува", "Renames")),
        (Lang.T("Списъкът с отчети", "The report list"), "Delete", Lang.T("Изтрива", "Deletes")),

        (Lang.T("Прозорците за избор", "The picker windows"), Lang.T("букви", "letters"), Lang.T("Пише направо в търсачката, без да се щрака в нея", "Types straight into the search box, without clicking in it")),
        (Lang.T("Прозорците за избор", "The picker windows"), Lang.T("Стрелки", "Arrows"), Lang.T("Движи се из списъка, докато курсорът стои в търсачката", "Moves through the list while the cursor stays in the search box")),
        (Lang.T("Прозорците за избор", "The picker windows"), "Enter", Lang.T("Избира", "Chooses")),
        (Lang.T("Прозорците за избор", "The picker windows"), "Esc", Lang.T("Затваря без промяна", "Closes without changing anything"))
    ];

    private readonly FlowLayoutPanel _stack = new();

    public ShortcutsDialog()
    {
        Text = Lang.T("Клавишни комбинации", "Keyboard shortcuts");
        Font = Theme.Base;
        BackColor = Theme.Canvas;
        ClientSize = new Size(Theme.Scaled(760), Theme.Scaled(720));
        MinimumSize = new Size(Theme.Scaled(620), Theme.Scaled(480));
        KeyPreview = true;

        _stack.Dock = DockStyle.Fill;
        _stack.FlowDirection = FlowDirection.TopDown;
        _stack.WrapContents = false;
        _stack.AutoScroll = true;
        _stack.BackColor = Theme.Canvas;
        _stack.Padding = new Padding(Theme.GapLarge, Theme.Gap, Theme.GapLarge, Theme.GapLarge);

        Controls.Add(_stack);
        Controls.Add(BuildHeader());
        Controls.Add(BuildFooter());

        Build();

        Theme.Apply(this);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);

        var width = Math.Max(320, _stack.ClientSize.Width - _stack.Padding.Horizontal - 4);

        foreach (Control child in _stack.Controls)
            if (child.Width != width) child.Width = width;
    }

    private Control BuildHeader()
    {
        var host = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(72),
            BackColor = Theme.Surface,
            Padding = new Padding(Theme.GapLarge, Theme.Gap, Theme.GapLarge, Theme.Gap)
        };

        host.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Font = Theme.Title,
            ForeColor = Theme.Ink,
            Text = Lang.T("Клавишни комбинации", "Keyboard shortcuts")
        });

        return host;
    }

    private Control BuildFooter()
    {
        var host = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = Theme.Scaled(70),
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, Theme.Gap, Theme.GapLarge, Theme.Gap)
        };

        var close = Theme.PrimaryButton(Lang.T("Затвори", "Close"));
        close.Dock = DockStyle.Right;
        close.Click += (_, _) => Close();
        close.FitWidth(40);

        host.Controls.Add(close);

        AcceptButton = close;
        CancelButton = close;

        return host;
    }

    private void Build()
    {
        foreach (var group in Shortcuts.GroupBy(s => s.Group))
        {
            _stack.Controls.Add(new Label
            {
                Text = group.Key,
                Font = Theme.Heading,
                ForeColor = Theme.Accent,
                Height = Theme.Scaled(34),
                Margin = new Padding(0, Theme.Gap, 0, 4)
            });

            foreach (var (_, keys, what) in group)
                _stack.Controls.Add(new ShortcutRow(keys, what) { Margin = new Padding(0, 0, 0, 2) });
        }
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }

        return base.ProcessCmdKey(ref message, keyData);
    }
}
