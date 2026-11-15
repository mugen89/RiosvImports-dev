using RiosvImports.App.Controls;
using RiosvImports.App.Editor;
using RiosvImports.Core;
using RiosvImports.Core.Localization;
using RiosvImports.Data;

namespace RiosvImports.App.Shell;

/// <summary>
/// Списъкът с отчети — това, с което се работи всеки ден.
///
/// Отчетът е нещо, което се започва, оставя и продължава след седмица, затова
/// тук той е запис в базата, а не файл някъде по диска. Пътят през Excel
/// остава наличен: за човек с готов файл от предишна година той е по-бързият.
/// </summary>
internal sealed class WorkPage : ContentPage
{
    private readonly ImportService _service;
    private readonly Workspace _workspace;
    private readonly CompanyScope _scope;
    private readonly DocumentList _list = new();
    private readonly Card _empty = new();

    private readonly SearchBox _search = new(Lang.T("Търсене по име или вид отчет", "Search by report name or type"));
    private readonly SegmentedControl _status = new();
    private readonly ComboBox _origin = new();
    private readonly ComboBox _sort = new();
    private readonly Label _count = new();

    /// <summary>Заглавието на екрана — носи и избраната фирма.</summary>
    private readonly Label _pageTitle = new();

    /// <summary>Всичко от базата; на екрана стига само това, което мине през ситото.</summary>
    private List<DocumentEntry> _all = [];

    /// <summary>
    /// Фирмата идва отвън — от лентата отляво. Подаден null значи „направи си
    /// свой обхват“: така страницата се сглобява и сама, което ползва тестът.
    /// </summary>
    public WorkPage(ImportService service, Workspace workspace, CompanyScope? scope = null) : base(1460)
    {
        _service = service;
        _workspace = workspace;
        _scope = scope ?? new CompanyScope(workspace);

        _scope.Changed += (_, _) => Reload();


        Controls.Add(BuildList());
        Controls.Add(BuildFilters());
        Controls.Add(BuildToolbar());
        Controls.Add(BuildTitle());

        Reload();
    }

    public event EventHandler? DocumentsChanged;

    // ------------------------------------------------------------- устройство

    private Control BuildTitle()
    {
        var host = new Panel { Dock = DockStyle.Top, Height = Theme.Scaled(76), BackColor = Theme.Canvas };

        // Закачените отгоре се редят наопаки: последният добавен застава най-
        // горе. Затова подзаглавието влиза първо, а заглавието — след него.
        host.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(26),
            Text = Lang.T("Започнати и готови отчети. Двоен клик отваря, Enter също.", "Started and finished reports. Double-click opens, so does Enter."),
            Font = Theme.Base,
            ForeColor = Theme.InkMuted
        });

        _pageTitle.Dock = DockStyle.Top;
        _pageTitle.Height = Theme.Scaled(44);
        _pageTitle.Font = Theme.Display;
        _pageTitle.ForeColor = Theme.Ink;
        _pageTitle.Text = Lang.T("Отчети", "Reports");

        host.Controls.Add(_pageTitle);

        return host;
    }

    private Control BuildToolbar()
    {
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(62),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Canvas,
            Padding = new Padding(0, 0, 0, Theme.Gap)
        };

        var create = Theme.PrimaryButton(Lang.T("Нов отчет", "New report"));
        create.Glyph = Theme.Glyph.Add;
        create.Width = Theme.Scaled(160);
        bar.Controls.Add(create);
        create.Click += (_, _) => StartNewReport();

        // Разделител: действията върху избрания отчет стоят отделно от
        // създаването и от пътя през Excel.
        bar.Controls.Add(Action(Lang.T("Отвори", "Open"), Theme.Glyph.Open, OpenSelected));
        bar.Controls.Add(Action(Lang.T("Копирай", "Duplicate"), Theme.Glyph.Copy, Duplicate));
        bar.Controls.Add(Action(Lang.T("Преименувай", "Rename"), Theme.Glyph.Rename, Rename));
        bar.Controls.Add(Separator());
        bar.Controls.Add(Action(Lang.T("Шаблон", "Template"), Theme.Glyph.Excel, CreateTemplate));
        bar.Controls.Add(Action(Lang.T("Внеси", "Import"), Theme.Glyph.Import, ImportFromExcel));
        bar.Controls.Add(Separator());
        bar.Controls.Add(Danger(Lang.T("Изтрий", "Delete"), Theme.Glyph.Delete, Delete));

        return bar;
    }

    private static AppButton Action(string text, string glyph, Action handler)
    {
        var button = Theme.GhostButton(text, glyph);
        button.Height = Theme.Scaled(40);
        button.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        button.Click += (_, _) => handler();

        return button.FitWidth();
    }

    private static AppButton Danger(string text, string glyph, Action handler)
    {
        var button = Theme.DangerButton(text, glyph);
        button.Height = Theme.Scaled(40);
        button.Margin = new Padding(Theme.GapSmall, 0, 0, 0);
        button.Click += (_, _) => handler();

        return button.FitWidth();
    }

    private static Control Separator() => new Panel
    {
        Width = 1,
        Height = Theme.Scaled(24),
        Margin = new Padding(Theme.Gap, 8, 4, 0),
        BackColor = Theme.Border
    };

    /// <summary>
    /// Търсене, пресяване по състояние и подредба.
    ///
    /// Списъкът расте бързо: един задължен по осем отчета за три години прави
    /// двайсет и четири записа със сходни имена. Затова трите неща стоят на
    /// един ред — какво търся, какво ме интересува и в какъв ред.
    ///
    /// Пресяването е само на екрана. Нищо не се трие и не се пипа в базата.
    /// </summary>
    private Control BuildFilters()
    {
        var bar = new Panel
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(56),
            BackColor = Theme.Canvas,
            Padding = new Padding(0, 0, 0, Theme.Gap)
        };

        _search.Width = Theme.Scaled(280);
        _search.Dock = DockStyle.Left;
        _search.TextChanged += (_, _) => ApplyFilters();

        _status.SetItems([Lang.T("Всички", "All"), Lang.T("Чернови", "Drafts"), Lang.T("Проверени", "Checked"), Lang.T("Подписани", "Signed")]);
        _status.Dock = DockStyle.Left;
        _status.Width = Math.Max(Theme.Scaled(360), _status.Needed);
        _status.Height = Theme.Scaled(40);
        _status.Margin = new Padding(Theme.Gap, 0, 0, 0);
        _status.SelectedIndexChanged += (_, _) => ApplyFilters();

        // Произходът е падащ списък, а не още един превключвател: гледа се
        // по-рядко от състоянието, а списъкът ще расте с всеки нов начин за
        // съставяне на отчет.
        _origin.Dock = DockStyle.Left;
        _origin.Width = Theme.Scaled(210);
        _origin.Font = Theme.Base;
        _origin.DropDownStyle = ComboBoxStyle.DropDownList;
        _origin.Items.AddRange([Lang.T("Всякакъв произход", "Any origin"), Lang.T("Създадени тук", "Made here"), Lang.T("От Excel", "From Excel"), Lang.T("От друга програма", "From another program")]);
        _origin.SelectedIndex = 0;
        _origin.SelectedIndexChanged += (_, _) => ApplyFilters();
        Theme.StyleInput(_origin);

        _sort.Dock = DockStyle.Right;
        _sort.Width = Theme.Scaled(210);
        _sort.Font = Theme.Base;
        _sort.DropDownStyle = ComboBoxStyle.DropDownList;
        _sort.Items.AddRange([Lang.T("Последно променени", "Last changed"), Lang.T("По име", "By name"), Lang.T("По вид отчет", "By report type"), Lang.T("По състояние", "By status")]);
        _sort.SelectedIndex = 0;
        _sort.SelectedIndexChanged += (_, _) => ApplyFilters();
        Theme.StyleInput(_sort);

        _count.Dock = DockStyle.Right;
        _count.Width = Theme.Scaled(150);
        _count.Font = Theme.Small;
        _count.ForeColor = Theme.InkFaint;
        _count.TextAlign = ContentAlignment.MiddleRight;
        _count.Padding = new Padding(0, 0, Theme.Gap, 0);

        // Разстоянията между групите се правят с празни панели: подредбата по
        // закачане няма отстояния, а вложен FlowLayoutPanel тук би добавил
        // цяло ниво само заради два процепа.
        bar.Controls.Add(_count);
        bar.Controls.Add(_sort);
        bar.Controls.Add(_origin);
        bar.Controls.Add(new Panel { Dock = DockStyle.Left, Width = Theme.GapSmall, BackColor = Theme.Canvas });
        bar.Controls.Add(_status);
        bar.Controls.Add(new Panel { Dock = DockStyle.Left, Width = Theme.GapSmall, BackColor = Theme.Canvas });
        bar.Controls.Add(_search);

        // Подредбата и броят стоят на реда с търсенето, докато се побират до
        // произхода. На лаптоп не се побираха: подредбата лягаше под произхода
        // и от „Последно променени“ оставаше „…менени“, а броят — върху
        // превключвателя. Тогава двете слизат на втори ред, отдясно.
        var below = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = Theme.Scaled(44),
            BackColor = Theme.Canvas,
            Visible = false
        };

        var high = bar.Height;
        var placing = false;

        bar.Controls.Add(below);

        void Place()
        {
            if (placing) return;

            var down = _sort.Width + _count.Width > bar.ClientSize.Width - _origin.Right;

            if (down == (_sort.Parent == below)) return;

            placing = true;
            bar.SuspendLayout();

            if (down)
            {
                // Подредбата най-вдясно, броят — до нея, както горе.
                below.Controls.Add(_count);
                below.Controls.Add(_sort);
            }
            else
            {
                bar.Controls.Add(_sort);
                bar.Controls.SetChildIndex(_sort, 0);
                bar.Controls.Add(_count);
                bar.Controls.SetChildIndex(_count, 0);
            }

            below.Visible = down;
            bar.Height = high + (down ? below.Height : 0);
            bar.ResumeLayout(true);
            placing = false;
        }

        bar.Resize += (_, _) => Place();

        return bar;
    }

    private Control BuildList()
    {
        var frame = new Card
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(1),
            Fill = Theme.Canvas,
            Outline = Theme.Border
        };

        _list.Dock = DockStyle.Fill;
        _list.DoubleClick += (_, _) => OpenSelected();
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { OpenSelected(); e.Handled = true; }
            if (e.KeyCode == Keys.Delete) { Delete(); e.Handled = true; }
            if (e.KeyCode == Keys.F2) { Rename(); e.Handled = true; }
        };

        _empty.Dock = DockStyle.Fill;
        _empty.Fill = Theme.Canvas;
        _empty.Outline = Color.Transparent;
        _empty.Visible = false;

        _empty.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = Lang.T("Още няма отчети.", "No reports yet.") +
                   Environment.NewLine + Environment.NewLine +
                   Lang.T("Започнете нов или внесете готов файл от Excel.",
                          "Start one, or import a finished file from Excel."),
            Font = Theme.Big,
            ForeColor = Theme.InkFaint,
            TextAlign = ContentAlignment.MiddleCenter
        });

        frame.Controls.Add(_empty);
        frame.Controls.Add(_list);

        return frame;
    }

    // ------------------------------------------------------------- списък

    /// <summary>Чете наново от базата и прилага ситото.</summary>
    public void Reload()
    {
        _all = _workspace.Documents.List()
            .Select(document =>
            {
                var report = _service.Schemas
                    .FirstOrDefault(s => s.Key == document.SchemaKey)?
                    .FindReport(document.Report);

                return new DocumentEntry(document, report is null ? "—" : ReportTitles.Short(report));
            })
            .ToList();

        ApplyFilters();
    }

    /// <summary>
    /// Пресява и подрежда вече прочетеното. Не пипа базата — затова писането в
    /// търсачката е мигновено дори при стотици отчета.
    /// </summary>
    private void ApplyFilters()
    {
        _pageTitle.Text = _scope.Titled(Lang.T("Отчети", "Reports"));

        var query = _search.Text.Trim();

        var shown = _all.Where(entry =>
        {
            var matchesStatus = _status.SelectedIndex switch
            {
                1 => entry.Document.Status == DocumentStatus.Draft,
                2 => entry.Document.Status == DocumentStatus.Checked,
                3 => entry.Document.Status == DocumentStatus.Signed,
                _ => true
            };

            var matchesOrigin = _origin.SelectedIndex switch
            {
                1 => entry.Document.Origin == DocumentOrigin.Manual,
                2 => entry.Document.Origin == DocumentOrigin.Excel,
                3 => entry.Document.Origin == DocumentOrigin.External,
                _ => true
            };

            // Фирмата вече не е сито тук — тя се избира веднъж, в лентата
            // отляво. Отчетите БЕЗ фирма обаче остават видими: те идват от
            // внасяне с непознат ЕИК и ако се скрият зад избора, човек няма
            // откъде да им я посочи.
            var matchesCompany = entry.Document.CompanyId is null
                || entry.Document.CompanyId == _scope.Current;

            if (!matchesStatus || !matchesOrigin || !matchesCompany) return false;
            if (query.Length == 0) return true;

            // Търси се и по име, и по вид: хората помнят ту едното, ту другото.
            return entry.Document.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || entry.Kind.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        });

        shown = _sort.SelectedIndex switch
        {
            1 => shown.OrderBy(e => e.Document.Title, StringComparer.CurrentCulture),
            2 => shown.OrderBy(e => e.Kind, StringComparer.CurrentCulture)
                      .ThenByDescending(e => e.Document.UpdatedUtc),
            // Подредбата по състояние върви от недовършеното към готовото:
            // черновите са това, което иска внимание.
            3 => shown.OrderBy(e => e.Document.Status)
                      .ThenByDescending(e => e.Document.UpdatedUtc),
            _ => shown.OrderByDescending(e => e.Document.UpdatedUtc)
        };

        var entries = shown.ToList();
        _list.Fill(entries);

        _list.Visible = entries.Count > 0;
        _empty.Visible = entries.Count == 0;

        _count.Text = entries.Count == _all.Count
            ? Plural(_all.Count)
            : Lang.T($"{entries.Count} от {_all.Count}", $"{entries.Count} of {_all.Count}");

        SetEmptyText(_all.Count == 0
            ? Lang.T("Още няма отчети.", "No reports yet.") +
              Environment.NewLine + Environment.NewLine +
              Lang.T("Започнете нов или внесете готов файл от Excel.",
                     "Start one, or import a finished file from Excel.")
            : Lang.T("Нищо не отговаря на търсенето.", "Nothing matches your search.") +
              Environment.NewLine + Environment.NewLine +
              Lang.T("Изчистете търсачката или изберете „Всички“.",
                     "Clear the search box or choose “All”."));
    }

    private static string Plural(int count) => count switch
    {
        0 => Lang.T("няма отчети", "no reports"),
        1 => Lang.T("1 отчет", "1 report"),
        _ => Lang.T($"{count} отчета", $"{count} reports")
    };

    private void SetEmptyText(string text)
    {
        if (_empty.Controls.Count > 0) _empty.Controls[0].Text = text;
    }

    private DocumentSummary? Selected => _list.Selected?.Document;

    /// <summary>Заглавието на екрана — ползва се от тестовете.</summary>
    internal string TitleText => _pageTitle.Text;

    /// <summary>Имената на видимите отчети — ползва се от тестовете.</summary>
    internal IReadOnlyList<string> ShownTitles =>
    [
        .. _list.Items.Cast<DocumentEntry>().Select(e => e.Document.Title)
    ];

    // ------------------------------------------------------------- действия

    public void StartNewReport()
    {
        using var dialog = new NewReportDialog(_service);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Report is null) return;

        var year = DateTime.Now.Month <= 3 ? DateTime.Now.Year - 1 : DateTime.Now.Year;
        var title = ReportTitles.SuggestDocumentName(dialog.Report, year);

        var header = new Dictionary<string, string> { ["year"] = year.ToString() };

        // ЕИК се пише веднъж в настройките и оттам нататък се предлага само.
        if (_workspace.Settings.Get(SettingsStore.Keys.DefaultEik) is { Length: > 0 } eik)
            header["eik"] = eik;

        var id = _workspace.Documents.Create(
            dialog.Schema!.Key, dialog.Report.ElementName, title, header, DocumentOrigin.Manual);

        Reload();
        DocumentsChanged?.Invoke(this, EventArgs.Empty);
        Open(id);
    }

    private void OpenSelected()
    {
        if (Selected is { } document) Open(document.Id);
    }

    /// <summary>
    /// Отваря отчет по номер — от „Продължете оттам…“ на началния екран. Отчет,
    /// изтрит междувременно, не се отваря: списъкът показва какво е останало.
    /// </summary>
    internal void OpenDocument(long id)
    {
        if (_workspace.Documents.Find(id) is null)
        {
            Reload();
            return;
        }

        Open(id);
    }

    private void Open(long id)
    {
        try
        {
            using var editor = new EditorForm(_service, _workspace, id);
            editor.ShowDialog(this);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Lang.T("Отчетът не можа да се отвори", "The report could not be opened"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        Reload();
        DocumentsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Копие на минал отчет е най-бързият начин за следващия период: числата
    /// се менят, а редовете, получателите и кодовете обикновено не.
    /// </summary>
    private void Duplicate()
    {
        if (Selected is not { } document) return;

        var id = _workspace.Documents.Duplicate(document.Id, document.Title + Lang.T(" — копие", " — copy"));

        Reload();
        DocumentsChanged?.Invoke(this, EventArgs.Empty);
        Open(id);
    }

    private void Rename()
    {
        if (Selected is not { } document) return;

        var name = Prompt(Lang.T("Ново име на отчета:", "New name for the report:"), document.Title);
        if (name is null) return;

        _workspace.Documents.Rename(document.Id, name);
        Reload();
        DocumentsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Delete()
    {
        if (Selected is not { } document) return;

        var answer = MessageBox.Show(this,
            Lang.T($"Да се изтрие ли „{document.Title}“?\r\n\r\n" +
                   "Записаните редове се губят безвъзвратно.",
                   $"Delete “{document.Title}”?\r\n\r\n" +
                   "The saved rows are lost for good."),
            Lang.T("Изтриване", "Deleting"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.Yes) return;

        _workspace.Documents.Delete(document.Id);
        Reload();
        DocumentsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------- Excel

    private void CreateTemplate()
    {
        using var dialog = new NewReportDialog(_service) { Text = Lang.T("Excel шаблон", "Excel template") };
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Report is null) return;

        using var save = new SaveFileDialog
        {
            Filter = Lang.T("Работна книга на Excel (*.xlsx)|*.xlsx", "Excel workbook (*.xlsx)|*.xlsx"),
            FileName = ReportTitles.Short(dialog.Report) + ".xlsx",
            InitialDirectory = _workspace.Settings.Get(SettingsStore.Keys.LastFolder, string.Empty)
        };

        if (save.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            _service.CreateTemplate(dialog.Schema!, [dialog.Report], save.FileName);
            _workspace.Settings.Set(SettingsStore.Keys.LastFolder, Path.GetDirectoryName(save.FileName)!);

            if (MessageBox.Show(this,
                    Lang.T("Шаблонът е готов. Да се отвори ли сега?",
                           "The template is ready. Open it now?"),
                    Lang.T("Готово", "Done"),
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(save.FileName) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Lang.T("Шаблонът не беше създаден", "The template was not created"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Казва защо фирмата е празна. Внасянето не се спира — редовете са
    /// свършената работа и не бива да се губят заради един ЕИК, — но човекът
    /// трябва да знае, преди да седне да подписва.
    /// </summary>
    private void WarnAboutUnknownEik(string eik)
    {
        var known = _workspace.Companies.List();

        var opening = Lang.T("Файлът е внесен, но фирмата остава празна.\r\n\r\n",
                             "The file was imported, but the company is left blank.\r\n\r\n");

        var text = known.Count == 0
            ? opening +
              Lang.T($"ЕИК {eik} не е заведен тук, а и още няма нито една фирма.\r\n" +
                     "Добавете я от Настройки → Фирми и я изберете в отчета.",
                     $"UIC {eik} is not registered here, and there are no companies at all yet.\r\n" +
                     "Add it under Settings → Companies and pick it in the report.")
            : opening +
              Lang.T($"ЕИК {eik} от файла не е сред заведените фирми:\r\n",
                     $"UIC {eik} from the file is not among the registered companies:\r\n") +
              string.Join("\r\n", known.Select(c => "    • " + c.Display)) +
              Lang.T("\r\n\r\nИзберете фирма в отчета или я добавете от Настройки → Фирми.",
                     "\r\n\r\nPick a company in the report, or add it under Settings → Companies.");

        MessageBox.Show(this, text, Lang.T("Непозната фирма", "Unknown company"),
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ImportFromExcel()
    {
        using var open = new OpenFileDialog
        {
            Filter = Lang.T("Работна книга на Excel (*.xlsx)|*.xlsx", "Excel workbook (*.xlsx)|*.xlsx"),
            InitialDirectory = _workspace.Settings.Get(SettingsStore.Keys.LastFolder, string.Empty)
        };

        if (open.ShowDialog(this) != DialogResult.OK) return;

        OpenFromFile(open.FileName);
    }

    /// <summary>
    /// Прочита попълнен шаблон и го превръща в отчет в базата. Оттам нататък
    /// той не се различава по нищо от отчет, започнат вътре в програмата.
    /// </summary>
    public void OpenFromFile(string path)
    {
        try
        {
            var workbook = _service.ReadWorkbook(path);
            var reports = workbook.Reports.ToList();

            if (reports.Count == 0)
            {
                MessageBox.Show(this,
                    Lang.T("Във файла няма разпознат отчет.", "No report was recognised in the file."),
                    Lang.T("Внасяне", "Import"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            long? first = null;

            // Фирмата се разпознава по ЕИК-а от файла; непознат ЕИК се маха.
            // Правилото живее в CompanyStore, за да се провери с тест.
            ImportedEik? matched = null;

            // Един файл ражда толкова отчета, колкото вида носи — и трите се
            // махат заедно, защото заедно са дошли. Затова внасянето е едно, а
            // отчетите му са редовете в него.
            var imported = new List<long>();

            foreach (var report in reports)
            {
                var title = ReportTitles.SuggestDocumentName(report, DateTime.Now.Year) + Lang.T(" (от Excel)", " (from Excel)");

                var header = workbook.Config
                    .Where(p => !p.Value.IsEmpty)
                    .ToDictionary(p => p.Key, p => p.Value.Text, StringComparer.Ordinal);

                matched = _workspace.Companies.MatchImportedEik(header);

                var id = _workspace.Documents.Create(
                    workbook.Schema.Key, report.ElementName, title, header,
                    DocumentOrigin.Excel, matched.Company?.Id);

                first ??= id;
                imported.Add(id);

                foreach (var sheet in workbook.Sheets.Where(s => ReferenceEquals(s.Plan.Report, report)))
                {
                    var rows = sheet.Rows
                        .Where(r => !r.IsBlank)
                        .Select(r => r.Cells
                            .Where(c => !c.Value.IsEmpty)
                            .ToDictionary(c => c.Key, c => c.Value.Text, StringComparer.Ordinal))
                        .ToList();

                    _workspace.Documents.SaveRows(id, sheet.Plan.Node.ElementName, rows);
                }
            }

            // Следата се пази чак сега: дотук всичко може да се спъне в файла, а
            // внасяне без нито един отчет зад себе си е ред в списъка, чийто
            // бутон няма какво да свърши.
            if (imported.Count > 0)
            {
                var importId = _workspace.Imports.Begin(
                    matched?.Company?.Id, ImportSource.Report, path);

                _workspace.Imports.Track(importId, ImportTarget.Document, imported);
            }

            _workspace.Settings.Set(SettingsStore.Keys.LastFolder, Path.GetDirectoryName(path)!);

            Reload();
            DocumentsChanged?.Invoke(this, EventArgs.Empty);

            if (matched is { Cleared: true }) WarnAboutUnknownEik(matched.FileEik);

            if (first is { } opened) Open(opened);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Lang.T("Файлът не беше прочетен", "The file could not be read"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Ctrl+F от главния прозорец — курсорът отива в търсенето.</summary>
    public override bool FocusSearch()
    {
        _search.Focus();
        return true;
    }

    // ------------------------------------------------------------- помощни

    private string? Prompt(string question, string current)
    {
        // Прозорче като всяко друго, макар да е сглобено на място: основата
        // носи копчето на лентата, смаляването и мястото (виж Dialog).
        using var form = new Dialog
        {
            Text = Lang.T("Преименуване", "Rename"),
            Font = Theme.Base,
            BackColor = Theme.Canvas,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            ClientSize = new Size(Theme.Scaled(460), Theme.Scaled(150)),
            MaximizeBox = false
        };

        var label = new Label { Dock = DockStyle.Top, Height = Theme.Scaled(28), Text = question };
        var input = new TextBox
        {
            Dock = DockStyle.Top,
            Font = Theme.Big,
            BorderStyle = BorderStyle.FixedSingle,
            Text = current
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = Theme.Scaled(52)
        };

        var ok = Theme.SecondaryButton(Lang.T("Готово", "Done"));
        ok.Width = Theme.Scaled(110);
        ok.DialogResult = DialogResult.OK;

        var cancel = Theme.SecondaryButton(Lang.T("Отказ", "Cancel"));
        cancel.Width = Theme.Scaled(110);
        cancel.DialogResult = DialogResult.Cancel;

        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18) };
        host.Controls.Add(input);
        host.Controls.Add(label);
        host.Controls.Add(buttons);

        form.Controls.Add(host);
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        input.SelectAll();

        return form.ShowDialog(this) == DialogResult.OK && input.Text.Trim().Length > 0
            ? input.Text.Trim()
            : null;
    }
}
