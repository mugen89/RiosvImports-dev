using RiosvImports.App.Controls;
using RiosvImports.Core.Ledger;
using RiosvImports.Core.Localization;
using RiosvImports.Core.Values;
using RiosvImports.Data;

namespace RiosvImports.App.Shell;

/// <summary>
/// Дневникът на въведеното: кое движение е записано оттук и къде е паднало.
///
/// Роден от едно оплакване, което няма нищо общо с изчисления: „ако съм сбъркал
/// датата, не знам в кой месец да го търся“. Въведеното отива направо в своя
/// месец и оттам нататък не се различава от нищо друго — а сгрешената дата
/// значи именно, че месецът не е онзи, в който човек ще погледне. Дванайсет
/// месеца по няколкостотин реда не се обхождат на ръка.
///
/// Прозорчето е нарочно същото като „Внесени файлове“: таблица, ред за
/// състоянието, едно опасно копче отляво и „Затвори“ отдясно. Двете отговарят
/// на един въпрос — „какво влезе и как да го върна“ — и не бива да се учат
/// поотделно.
///
/// <b>Дневникът е за изхвърляне.</b> Той не е отчет и не е следа за
/// проверяващия: докато в него има редове, има какво да се провери; изчистен,
/// той не оставя нищо по склада. Затова „Изчисти“ не пипа нито едно движение, а
/// „Махни движението“ маха него, но не и реда — редът остава и казва „това вече
/// го няма“.
///
/// <b>Изтритото оттук не е имало.</b> Дневникът съществува, за да пази от
/// собствените грешки: изтрито движение връща количеството си в склада, от който
/// е взето, и не оставя следа — нито в склада, нито тук. Затова изтриването пита
/// преди това дали нещо след него вече е черпило от него
/// (<see cref="Undo.Takers"/>) и спира, ако е така: иначе едни и същи тонове биха
/// стояли едновременно в два склада.
/// </summary>
internal sealed class ActionLogDialog : Dialog
{
    private readonly Workspace _workspace;
    private readonly long? _companyId;
    private readonly string? _stagenum;

    private readonly DataGridView _grid = new();
    private readonly Label _summary = new();

    private readonly AppButton _open;
    private readonly AppButton _fix;
    private readonly AppButton _remove;
    private readonly AppButton _clear;
    private readonly AppButton _restore;
    private readonly AppButton _delete;

    /// <summary>
    /// Изтрива всичко, записано наведнъж с избрания ред.
    ///
    /// Отделно копче, а не въпрос след „Изтрий“: въпрос с „Да“ за цялото и „Не“ за
    /// реда би изтрил ред точно когато човек се е отказал.
    /// </summary>
    private readonly AppButton _deleteAll;

    /// <summary>
    /// Предлага ли се цялото действие. Пази се отделно от копчето: Control.Visible
    /// е „не“, докато прозорецът не е показан, а тестът пита за нагласеното.
    /// </summary>
    private bool _wholeShown;

    /// <summary>
    /// Двата раздела: „Въведено“ — работният, чист до подаването на отчета, и
    /// „Отложено“ — сложеното настрана, което оттам се трие истински.
    /// </summary>
    private readonly TabStrip _tabs = new();

    /// <summary>Всички редове — и от двата раздела.</summary>
    private List<ActionEntry> _all = [];

    /// <summary>Редовете на показания раздел.</summary>
    private List<ActionEntry> _entries = [];

    public ActionLogDialog(Workspace workspace, long? companyId, string? stagenum)
    {
        _workspace = workspace;
        _companyId = companyId;
        _stagenum = stagenum;

        Text = Lang.T("Дневник на въведеното", "Log of what was entered");
        Font = Theme.Base;
        BackColor = Theme.Canvas;
        MinimumSize = new Size(Theme.Scaled(900), Theme.Scaled(460));
        ClientSize = new Size(Theme.Scaled(1080), Theme.Scaled(560));
        MaximizeBox = false;
        KeyPreview = true;

        _open = Theme.SecondaryButton(Lang.T("Отвори месеца…", "Open the month…"));
        _fix = Theme.PrimaryButton(Lang.T("Поправи…", "Correct…"));
        _remove = Theme.SecondaryButton(Lang.T("Отложи", "Set aside"));
        _clear = Theme.SecondaryButton(Lang.T("Изчисти дневника", "Clear the log"));
        _restore = Theme.SecondaryButton(Lang.T("Върни", "Put back"));
        _delete = Theme.DangerButton(Lang.T("Изтрий движението", "Delete the movement"), Theme.Glyph.Delete);
        _deleteAll = Theme.DangerButton(Lang.T("Изтрий цялото действие", "Delete the whole action"), Theme.Glyph.Delete);

        // Пълнещият влиза пръв; от закачените отгоре последният застава
        // най-горе: заглавие, раздели, ред за състоянието.
        Controls.Add(BuildBody());
        Controls.Add(BuildSummary());
        Controls.Add(BuildTabs());
        Controls.Add(BuildTitle());
        Controls.Add(BuildButtons());

        Fill();

        Theme.Apply(this);
    }

    /// <summary>Дали нещо е махнато — страницата отзад се обновява само тогава.</summary>
    public bool Changed { get; private set; }

    /// <summary>
    /// Кой ред човек е поискал да отвори. Празно значи „никой“.
    ///
    /// Целият ред, а не само месецът: за да се отвори вярната таблица, трябва
    /// да се знае и ВИДЪТ на движението — иначе човек попада в месеца, но в
    /// чужд раздел и пак търси.
    /// </summary>
    public ActionEntry? Chosen { get; private set; }

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
            Text = Lang.T("Дневник на въведеното", "Log of what was entered"),
            Font = Theme.Title,
            ForeColor = Theme.Ink
        });

        host.Controls.Add(new Label
        {
            Dock = DockStyle.Bottom,
            Height = Theme.Scaled(38),
            Text = Lang.T(
                "Какво е записано от прозорчетата на склада и в кой МЕСЕЦ е паднало. Сгрешеното се " +
                "отлага; изтрито от „Отложено“, то връща количеството в склада, все едно никога не " +
                "се е случвало. Изчистването маха дневника, не записаното.",
                "What the store windows wrote and which MONTH it landed in. What is wrong is set aside; " +
                "deleted from “Set aside”, it returns its quantity to the store as if it had never " +
                "happened. Clearing removes the log, not what was written."),
            Font = Theme.Base,
            ForeColor = Theme.InkMuted
        });

        return host;
    }

    /// <summary>Двата раздела — само заглавия; таблицата под тях е една.</summary>
    private Control BuildTabs()
    {
        var host = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(40),
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, 0, Theme.GapLarge, 0)
        };

        _tabs.Dock = DockStyle.Fill;
        _tabs.TabPages.Add(new TabPage(Lang.T("Въведено", "Entered")));
        _tabs.TabPages.Add(new TabPage(Lang.T("Отложено", "Set aside")));
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            _bin = _tabs.SelectedIndex == 1;
            ShowTab(_bin);
        };

        host.Controls.Add(_tabs);

        return host;
    }

    /// <summary>
    /// Дали е отворен разделът „Отложено“. Пази се отделно от разделите:
    /// непоказан прозорец не вдига събитието за смяна и те не бива да решават
    /// кой списък стои отдолу.
    /// </summary>
    private bool _bin;

    private bool Bin => _bin;

    /// <summary>Дали изтриването оттук е заключено от „Настройки“.</summary>
    private bool Locked => _workspace.Settings.GetBool(SettingsStore.Keys.LogDeleteLocked, false);

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

    private Control BuildBody()
    {
        var host = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Canvas,
            Padding = new Padding(Theme.GapLarge, 0, Theme.GapLarge, Theme.Gap)
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

        Column("when", Lang.T("Въведено", "Entered"), 140);
        Column("kind", Lang.T("Вид", "Kind"), 130);
        Column("date", Lang.T("Дата на движението", "Date of the movement"), 170);

        // Месецът е самата причина дневникът да го има: заради него се търси.
        Column("period", Lang.T("Падна в месец", "Landed in month"), 170);

        Column("what", Lang.T("Какво", "What"), 240, fill: true);
        Column("state", Lang.T("Състояние", "State"), 180);

        _grid.SelectionChanged += (_, _) => Allow();
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0) Open();
        };
    }

    private void Column(string name, string header, int width, bool fill = false)
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
            column.MinimumWidth = Theme.Scaled(180);
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

        // Отляво — трите, с които се поправя: първо се ОТВАРЯ месецът (най-
        // безобидното и най-често нужното), после се маха движението, и чак
        // накрая се изхвърля дневникът.
        var tools = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Canvas
        };

        foreach (var button in new[] { _fix, _open, _remove, _clear, _restore, _delete, _deleteAll })
        {
            button.Height = Theme.Base.Height + Theme.Scaled(16);
            button.Margin = new Padding(0, 0, Theme.GapSmall, 0);
            button.FitWidth(16);
        }

        _open.Click += (_, _) => Open();
        _fix.Click += (_, _) => Fix();

        new ToolTip().SetToolTip(_fix, Lang.T(
            "Отваря действието в прозореца, от който е дошло — със същите партиди и количества. " +
            "Там се сменя датата или каквото е сгрешено; записът заменя старото.",
            "Opens the action in the window it came from — with the same batches and amounts. " +
            "Change the date or whatever is wrong there; saving replaces the old one."));
        _remove.Click += (_, _) => Trash();
        _clear.Click += (_, _) => Clear();
        _restore.Click += (_, _) => Restore();
        _delete.Click += (_, _) => Delete();
        _deleteAll.Click += (_, _) => Delete(whole: true);

        var tips = new ToolTip();

        tips.SetToolTip(_delete, Lang.T(
            "Връща количеството на този ред в склада, от който е взето — все едно никога не се е случвало.",
            "Returns this row's quantity to the store it was taken from — as if the movement had never happened."));

        tips.SetToolTip(_deleteAll, Lang.T(
            "Същото за всичко, записано наведнъж с този ред — и за редовете, които още стоят във „Въведено“.",
            "The same for everything saved together with this row — including rows still under “Entered”."));

        tools.Controls.Add(_fix);
        tools.Controls.Add(_open);
        tools.Controls.Add(_remove);
        tools.Controls.Add(_clear);
        tools.Controls.Add(_restore);
        tools.Controls.Add(_delete);
        tools.Controls.Add(_deleteAll);

        host.Controls.Add(close);
        host.Controls.Add(tools);

        CancelButton = close;

        return host;
    }

    // ------------------------------------------------------------- пълнене

    private void Fill()
    {
        _all = [.. _workspace.Actions.List(_companyId, _stagenum)];

        _tabs.Counts[0] = _all.Count(e => !e.Trashed);
        _tabs.Counts[1] = _all.Count(e => e.Trashed);
        _tabs.Invalidate();

        ShowTab(Bin);
    }

    /// <summary>Пълни таблицата с редовете на единия раздел.</summary>
    private void ShowTab(bool bin)
    {
        _entries = [.. _all.Where(e => e.Trashed == bin)];

        _grid.Rows.Clear();

        foreach (var entry in _entries)
        {
            var index = _grid.Rows.Add();
            var row = _grid.Rows[index];

            row.Cells["when"].Value = entry.CreatedUtc == DateTime.MinValue
                ? string.Empty
                : entry.CreatedUtc.ToString("dd.MM HH:mm");

            row.Cells["kind"].Value = Moves.Title(entry.Kind);
            row.Cells["date"].Value = entry.Date.ToString(ValueConverter.BulgarianDateFormat);
            row.Cells["period"].Value = entry.Where;
            row.Cells["what"].Value = $"{entry.Ewc} · {SplitMath.Amount(entry.Amount)} " + Lang.T("т", "t");

            row.Cells["state"].Value = !entry.Alive
                ? Lang.T("махнато", "removed")
                : entry.Amended is { } fixedAt
                    ? Lang.T($"поправено {fixedAt:dd.MM HH:mm}", $"corrected {fixedAt:dd.MM HH:mm}")
                    : Lang.T("записано", "written");

            // Какво е било стои в самия ред, а не само в подсказка: поправката заменя
            // записаното, но не бива да заличава, че го е имало.
            if (entry.Was.Length > 0)
            {
                row.Cells["what"].Value += Lang.T($" · беше {entry.Was}", $" · was {entry.Was}");
                row.Cells["what"].ToolTipText = Lang.T($"Първоначално: {entry.Was}", $"Originally: {entry.Was}");
                row.Cells["state"].ToolTipText = row.Cells["what"].ToolTipText;
            }

            // Махнатото се чете по-бледо: то не иска работа, а само обяснява
            // какво е станало с реда.
            if (!entry.Alive) row.DefaultCellStyle.ForeColor = Theme.InkFaint;

            row.Tag = entry;
        }

        Weigh();
        Allow();
    }

    /// <summary>Редът отдолу: колко чакат проверка и в колко месеца са паднали.</summary>
    private void Weigh()
    {
        if (Bin)
        {
            WeighBin();
            return;
        }

        if (_entries.Count == 0)
        {
            _summary.Text = Lang.T(
                "Дневникът е празен. Всяко въвеждане от прозорчетата на склада ще се появи тук.",
                "The log is empty. Everything the store windows write will show up here.");

            _summary.ForeColor = Theme.InkFaint;
            return;
        }

        var months = _entries.Select(e => e.Period).Distinct().Count();
        var gone = _entries.Count(e => !e.Alive);

        var text = Lang.T(
            $"{_entries.Count} въведени · в {months} " + (months == 1 ? "месец" : "месеца"),
            $"{_entries.Count} entered · across {months} " + (months == 1 ? "month" : "months"));

        if (gone > 0)
            text += Lang.T($" · {gone} вече ги няма", $" · {gone} are gone already");

        _summary.Text = text;
        _summary.ForeColor = Theme.Ink;
    }

    /// <summary>Редът отдолу в „Отложено“ — и дали изтриването е заключено.</summary>
    private void WeighBin()
    {
        if (_entries.Count == 0)
        {
            _summary.Text = Lang.T(
                "Нищо не е отложено. Сгрешеното се отлага тук от „Въведено“.",
                "Nothing is set aside. What is wrong is set aside here from “Entered”.");
            _summary.ForeColor = Theme.InkFaint;
            return;
        }

        var alive = _entries.Count(e => e.Alive);

        _summary.Text = Lang.T(
            $"{_entries.Count} отложени · {alive} движения още стоят в склада",
            $"{_entries.Count} set aside · {alive} movements still stand in the store");

        // Затвореният месец не се пипа: отложеното от него чака, докато месецът
        // не бъде отворен.
        var closed = _entries.Count(Frozen);

        if (closed > 0)
            _summary.Text += Lang.T($" · {closed} са в затворен месец и не се трият",
                                    $" · {closed} are in a closed month and cannot be deleted");

        if (Locked)
            _summary.Text += Lang.T(" · изтриването е заключено от „Настройки“",
                                    " · deleting is locked in “Settings”");

        _summary.ForeColor = Locked ? Theme.Warn : Theme.Ink;
    }

    private ActionEntry? Selected() =>
        _grid.CurrentRow?.Tag as ActionEntry;

    private void Allow()
    {
        var entry = Selected();

        var bin = Bin;

        // Всеки раздел — своите копчета: в работния се поправя и се слага
        // настрана, в „Отложено“ се връща или се трие истински.
        _fix.Visible = !bin;
        _remove.Visible = !bin;
        _clear.Visible = !bin;
        _restore.Visible = bin;
        _delete.Visible = bin;

        // Второто копче — само когато редът наистина има съседи: при единичен
        // ред то би казало същото като първото.
        _wholeShown = bin && entry is { Action.Length: > 0 } &&
                      _all.Count(e => e.Action == entry.Action) > 1;

        _deleteAll.Visible = _wholeShown;

        _open.Enabled = entry is not null;
        _fix.Enabled = Amend is not null && entry is { Alive: true } && entry.Kind != MoveKind.Received;
        _remove.Enabled = !bin && entry is not null;
        _clear.Enabled = !bin && _entries.Count > 0;
        _restore.Enabled = bin && entry is not null;
        _delete.Enabled = bin && entry is not null && !Locked && !Frozen(entry);
        _deleteAll.Enabled = _delete.Enabled;
    }

    // ------------------------------------------------------------- действия

    /// <summary>
    /// Кой поправя — страницата на склада: тя знае прозорците и как се
    /// записва от тях. Празно значи, че поправка оттук не се предлага.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<IWin32Window, ActionEntry, bool>? Amend { get; set; }

    /// <summary>
    /// Поправя действието в прозореца, от който е дошло.
    ///
    /// Най-честата грешка е датата — а тя не се поправя като число в клетка:
    /// на друга дата в склада стои друго. Прозорецът на действието смята точно
    /// това, затова поправката минава през него, а не през таблицата на месеца.
    /// </summary>
    private void Fix()
    {
        if (Amend is null || Selected() is not { Alive: true } entry) return;

        if (!Amend(this, entry)) return;

        Changed = true;
        Fill();
    }

    /// <summary>Отваря месеца, в който редът е паднал — това е самото намиране.</summary>
    private void Open()
    {
        if (Selected() is not { } entry) return;

        Chosen = entry;
        DialogResult = DialogResult.OK;
    }

    /// <summary>
    /// Отлага реда. Движението остава в склада — то се трие
    /// едва оттам, изрично. Работният раздел остава чист за проверката на
    /// отчета, а сгрешеното не се губи от поглед.
    /// </summary>
    private void Trash()
    {
        if (Bin || Selected() is not { } entry) return;

        _workspace.Actions.Trash(entry.Id);

        Fill();
    }

    /// <summary>Връща реда в „Въведено“ — сложен е настрана по погрешка.</summary>
    private void Restore()
    {
        if (!Bin || Selected() is not { } entry) return;

        _workspace.Actions.Trash(entry.Id, trashed: false);

        Fill();
    }

    /// <summary>
    /// Трие самото движение — оттук, и само оттук — все едно никога не се е случвало.
    ///
    /// През <see cref="LedgerWriter"/>, както всяко друго махане: правилата
    /// какво влачи след себе си едно движение живеят там. Затворен месец спира
    /// изтриването, заключено от „Настройки“ — също; дневникът не заобикаля
    /// нито едното. Движение, което вече го няма, си отива само от дневника.
    ///
    /// <paramref name="whole"/> взима и всичко, записано наведнъж с реда. Или
    /// всичко, или нищо: и проверките, и самото махане са за цялото действие.
    /// </summary>
    private void Delete(bool whole = false)
    {
        if (!Bin || Selected() is not { } entry) return;
        if (entry.CompanyId is not { } companyId) return;

        Refusal = string.Empty;

        if (Locked)
        {
            Tell(Lang.T("Изтриването от дневника е заключено от „Настройки“. Отключете го там, ако " +
                        "наистина трябва да се изтрие.",
                        "Deleting from the log is locked in “Settings”. Unlock it there if it really " +
                        "has to be deleted."),
                Lang.T("Заключено", "Locked"));

            return;
        }

        var rows = whole ? _workspace.Actions.Whole(entry) : [entry];

        if (rows.FirstOrDefault(Frozen) is { } closed)
        {
            Tell(Lang.T($"{closed.Period.Title()} е затворен. Отворете месеца от „Настройки“, " +
                        "ако наистина трябва да се пипне.",
                        $"{closed.Period.Title()} is closed. Reopen it from “Settings” if it really " +
                        "has to be touched."),
                Lang.T("Затворен месец", "A closed month"));

            return;
        }

        var moves = _workspace.Ledger.Read(companyId, entry.Stagenum);
        var gone = rows.Where(r => r.Alive).Select(r => r.MoveId).ToHashSet();

        var takers = Undo.Takers(moves, _workspace.Ledger.Links(companyId, entry.Stagenum), gone);

        if (takers.Count > 0)
        {
            Refusal = Blocked(takers);
            Tell(Refusal, Lang.T("От него вече е взето", "It has already been drawn on"));

            return;
        }

        var mine = moves.Where(m => gone.Contains(m.Id)).ToList();

        if (Visible && MessageBox.Show(this, Asked(entry, rows, mine), Lang.T("Изтриване", "Deletion"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        if (gone.Count > 0) _workspace.Writer.Remove(companyId, gone);

        foreach (var row in rows) _workspace.Actions.Forget(row.Id);

        Changed = true;

        Fill();

        var back = Returned(mine);

        if (back.Length > 0) _summary.Text = back + " " + _summary.Text;
    }

    /// <summary>
    /// Защо не се изтрива, и то с думите на човек: кое стои на пътя и какво да се
    /// направи — изброени са истинските движения, защото тях човек може да намери.
    ///
    /// Не всяко е в дневника: той се чисти след всеки отчет, а въведеното от
    /// „Месец“ никога не е минавало оттук. Тогава се казва къде е, иначе човек
    /// търси ред, който го няма.
    /// </summary>
    private string Blocked(IReadOnlyList<Move> takers)
    {
        const int most = 8;

        var lines = takers.Take(most).Select(m =>
            $"•  {Moves.Title(m.Kind)} · {m.Ewc} · {SplitMath.Amount(m.Amount)} " + Lang.T("т", "t") +
            $" · {m.Date.ToString(ValueConverter.BulgarianDateFormat)}").ToList();

        if (takers.Count > most)
            lines.Add(Lang.T($"…и още {takers.Count - most}", $"…and {takers.Count - most} more"));

        var list = string.Join(Environment.NewLine, lines);

        var logged = _all.Select(e => e.MoveId).ToHashSet();

        var elsewhere = takers.Any(m => !logged.Contains(m.Id))
            ? Lang.T(" Които ги няма в дневника, се махат от „Месец“.",
                     " Those not in the log are removed from “Month”.")
            : string.Empty;

        return Lang.T(
            $"Това не може да се изтрие — от него вече е взето:{Environment.NewLine}{Environment.NewLine}{list}" +
            $"{Environment.NewLine}{Environment.NewLine}Изтрийте първо тях, от последното към първото.{elsewhere} " +
            "Иначе едни и същи тонове биха останали в два склада.",
            $"This cannot be deleted — it has already been drawn on:{Environment.NewLine}{Environment.NewLine}{list}" +
            $"{Environment.NewLine}{Environment.NewLine}Delete those first, from the last to the first.{elsewhere} " +
            "Otherwise the same tonnes would stay in two stores.");
    }

    /// <summary>Какво се пита преди изтриване — и какво точно ще стане.</summary>
    private static string Asked(ActionEntry entry, IReadOnlyList<ActionEntry> rows, IReadOnlyList<Move> mine)
    {
        var day = entry.Date.ToString(ValueConverter.BulgarianDateFormat);

        var what = rows.Count > 1
            ? Lang.T($"Цялото действие — {rows.Count} реда, {SplitMath.Amount(mine.Sum(m => m.Amount))} т.",
                     $"The whole action — {rows.Count} rows, {SplitMath.Amount(mine.Sum(m => m.Amount))} t.")
            : entry.Summary;

        var left = rows.Count(r => !r.Trashed);

        var others = left > 0
            ? Lang.T($"\n{left} от редовете още стоят във „Въведено“ и си отиват с него.",
                     $"\n{left} of the rows are still under “Entered” and go with it.")
            : string.Empty;

        var back = mine.Select(m => Moves.From(m.Kind, m.Direct)).FirstOrDefault() is { } store
            ? Lang.T($"Количеството се връща в „{Moves.Short(store)}“, все едно никога не се е случвало.",
                     $"The quantity returns to “{Moves.Short(store)}”, as if the movement had never happened.")
            : string.Empty;

        return Lang.T(
            $"{what}\nЗаписано на {day}, в {entry.Where}.{others}\n\n{back}\n\nДа се изтрие ли? Това не се връща.",
            $"{what}\nWritten on {day}, in {entry.Where}.{others}\n\n{back}\n\nDelete it? This cannot be undone.");
    }

    /// <summary>Какво се е върнало и къде — казва се след записа, на реда за състоянието.</summary>
    private static string Returned(IReadOnlyList<Move> mine)
    {
        if (mine.Count == 0) return string.Empty;

        var amount = SplitMath.Amount(mine.Sum(m => m.Amount));

        return Moves.From(mine[0].Kind, mine[0].Direct) is { } store
            ? Lang.T($"{amount} т са обратно в „{Moves.Short(store)}“ ·",
                     $"{amount} t are back in “{Moves.Short(store)}” ·")
            : string.Empty;
    }

    /// <summary>
    /// Дали движението на реда е в затворен месец. Тогава не се трие оттук:
    /// затвореният месец е подаден или се подава, и изтриване от него би
    /// разминало отчета със склада. Движение, което вече го няма, не пази нищо.
    /// </summary>
    private bool Frozen(ActionEntry entry) =>
        entry.Alive && entry.CompanyId is { } companyId &&
        _workspace.Periods.IsClosed(companyId, entry.Stagenum, entry.Period);

    /// <summary>Съобщение — само на показан прозорец (тестът минава по същия път).</summary>
    private void Tell(string text, string title)
    {
        if (Visible)
            MessageBox.Show(this, text, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// Изхвърля дневника. Записаното остава.
    ///
    /// Пита се, защото това е единственото действие тук, което не се връща — а
    /// и защото „изчисти“ до „махни движението“ се чете като „махни всичко“.
    /// </summary>
    private void Clear()
    {
        if (_entries.Count == 0) return;

        var said = Lang.T(
            $"Дневникът ще се изпразни — {_entries.Count} реда.\n\n" +
            "Записаното в склада ОСТАВА: това е тефтерче за проверка, не отчет.",
            $"The log will be emptied — {_entries.Count} rows.\n\n" +
            "What is written in the store STAYS: this is a checklist, not a report.");

        if (MessageBox.Show(this, said, Lang.T("Изчистване", "Clearing"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        _workspace.Actions.Clear(_companyId, _stagenum);

        Fill();
    }

    // ------------------------------------------------------------- за теста

    internal int RowCount => _grid.Rows.Count;

    internal string SummaryText => _summary.Text;

    internal bool CanRemove => _remove.Enabled;

    internal bool CanDelete => _delete.Enabled;

    /// <summary>Предлага ли се изтриване на цялото действие.</summary>
    internal bool WholeOffered => _wholeShown;

    /// <summary>Защо изтриването е спряно — празно значи, че не е.</summary>
    internal string Refusal { get; private set; } = string.Empty;

    internal bool CanRestore => _restore.Enabled;

    /// <summary>Отваря раздела, както щракване по него.</summary>
    internal void UseBin(bool bin)
    {
        _tabs.SelectedIndex = bin ? 1 : 0;
        _bin = bin;
        ShowTab(bin);
    }

    /// <summary>Отлага избрания, както копчето.</summary>
    internal void TrashNow() => Trash();

    /// <summary>Връща избрания в „Въведено“.</summary>
    internal void RestoreNow() => Restore();

    /// <summary>Трие избрания — питането се прескача на скрит прозорец.</summary>
    internal void DeleteNow() => Delete();

    /// <summary>Трие цялото действие на избрания ред — същото, със същите проверки.</summary>
    internal void DeleteWholeNow() => Delete(whole: true);

    internal bool CanClear => _clear.Enabled;

    internal void Pick(int row)
    {
        if (row < 0 || row >= _grid.Rows.Count) return;

        _grid.CurrentCell = _grid.Rows[row].Cells[0];
        Allow();
    }

    internal string PeriodAt(int row) =>
        _grid.Rows[row].Cells["period"].Value?.ToString() ?? string.Empty;

    internal string StateAt(int row) =>
        _grid.Rows[row].Cells["state"].Value?.ToString() ?? string.Empty;

    internal string StateTipAt(int row) => _grid.Rows[row].Cells["state"].ToolTipText;

    internal string WhatAt(int row) => _grid.Rows[row].Cells["what"].Value?.ToString() ?? string.Empty;

    /// <summary>
    /// Чете ли се всяка клетка цяла. Прегледът на екраните не вижда в таблица, а
    /// „поправено …“ и „беше …“ са най-дългото в нея.
    /// </summary>
    internal bool CellsFit => _grid.Rows.Cast<DataGridViewRow>()
        .SelectMany(row => row.Cells.Cast<DataGridViewCell>())
        .All(cell => TextRenderer.MeasureText(cell.Value?.ToString() ?? string.Empty, _grid.Font).Width + 12 <=
                     _grid.Columns[cell.ColumnIndex].Width);

    /// <summary>Изчиства дневника, както копчето — но без питането.</summary>
    internal void ClearNow()
    {
        _workspace.Actions.Clear(_companyId, _stagenum);
        Fill();
    }
}
