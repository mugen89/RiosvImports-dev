using System.Reflection;
using System.Runtime.CompilerServices;
using RiosvImports.App;
using RiosvImports.App.Controls;
using RiosvImports.App.Editor;
using RiosvImports.App.Shell;
using RiosvImports.App.Updates;
using RiosvImports.Core;
using RiosvImports.Core.Ledger;
using RiosvImports.Core.Localization;
using RiosvImports.Data;

/// <summary>
/// Лаптопът на голям монитор: сглобява екраните при подражаван мащаб и казва
/// кое се реже.
///
/// Програмата се пише на монитор при 100 %, а се проверява на лаптоп при 125 %
/// и 150 % — и разминаването излизаше чак на снимка от проверяващия. Шрифтовете
/// растат с мащаба, а размерите, писани в пиксели, не растат; надписите се режат
/// отгоре и отдолу, редове от списъци влизат един в друг, а цели таблици остават
/// под ръба на прозореца.
///
/// Подражаването става през RIOSV_ZOOM (виж Theme.Emulated) и затова се прави в
/// ОТДЕЛЕН процес: шрифтовете се правят веднъж за процеса. Основната програма
/// на тестовете пуска себе си с „--layout“ и чете какво е намерено.
///
/// Какво се търси:
/// <list type="bullet">
/// <item>контрола, която излиза извън родителя си, без той да може да превърти;</item>
/// <item>надпис, който не се побира във височината си;</item>
/// <item>копче, по-ниско или по-тясно от надписа си; отметка, която реже текста;</item>
/// <item>редове и заглавия на таблици, по-ниски от шрифта им;</item>
/// <item>превключвател, чиито дялове излизат с многоточие.</item>
/// </list>
/// </summary>
internal static class LayoutAudit
{
    private static readonly List<string> Found = [];

    /// <summary>„--layout ширина височина“ — връща нула, ако нищо не се реже.</summary>
    public static int Run(string[] args)
    {
        var width = int.Parse(args[1]);
        var height = int.Parse(args[2]);

        var done = 1;

        var thread = new Thread(() => done = Audit(width, height));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        foreach (var line in Found) Console.WriteLine(line);
        Console.WriteLine($"layout: zoom={Theme.Zoom:0.00} {width}x{height} findings={Found.Count}");

        return done;
    }

    private static int Audit(int width, int height)
    {
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (Theme.Emulated > 1f)
                Application.SetDefaultFont(new Font(SystemFonts.MessageBoxFont!.FontFamily, 9f * Theme.Emulated));

            var service = new ImportService(ImportService.LocateSchemaFolder());
            var path = Path.Combine(Path.GetTempPath(), "RiosvImports.Tests", $"layout_{Guid.NewGuid():N}.db");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            using var db = Workspace.Open(path);
            var companyId = Seed(db);

            Lang.Use("bg");
            Theme.Use("light");

            Screens(service, db, companyId, width, height);

            return 0;
        }
        catch (Exception ex)
        {
            Found.Add("[изключение] " + ex);
            return 2;
        }
    }

    /// <summary>
    /// Всеки склад с по нещо в него — празен екран не показва дали редовете
    /// се побират. Имената са дълги нарочно: дълъг код по ЕКО е обичайното.
    /// </summary>
    private static long Seed(Workspace db)
    {
        var companyId = db.Companies.Add("203344556", "Метал Рециклиране и Търговия ЕООД");
        var day = new DateOnly(2026, 9, 1);

        db.Writer.Write(companyId, new MoveRequest(MoveKind.Received, day, "080111", 12.345m,
            Eik: "203344556", Partner: "Металообработващ завод Изток АД", Document: "Протокол 12-РД-00001330"));
        db.Writer.Write(companyId, new MoveRequest(MoveKind.Received, day.AddDays(1), "080111", 8.2m,
            Eik: "204455667", Partner: "Скрап и Вторични Суровини ЕООД"));
        db.Writer.Write(companyId, new MoveRequest(MoveKind.Received, day.AddDays(2), "150101", 4m,
            Eik: "204455667", Partner: "Скрап и Вторични Суровини ЕООД"));

        db.Writer.Write(companyId, new MoveRequest(MoveKind.Treated, day.AddDays(5), "080111", 15m));
        db.Writer.Write(companyId, new MoveRequest(MoveKind.Generated, day.AddDays(6), "191202", 6m));
        db.Writer.Write(companyId, new MoveRequest(MoveKind.Recycled, day.AddDays(6), "191204", 2m));
        db.Writer.Write(companyId, new MoveRequest(MoveKind.Submitted, day.AddDays(7), "191202", 3m,
            Fields: new Dictionary<string, string> { ["name_receiver"] = "Леярна Юг АД", ["pin_receiver"] = "175000111" },
            Contract: "Рамков договор № 12/2026 с анекс 3"));

        // Действие от два реда в „Отложено“: иначе „Изтрий цялото действие“ не се
        // показва и проверката не го вижда.
        var moved = db.Ledger.Read(companyId, "1").First(m => m.Kind == MoveKind.Submitted);

        foreach (var row in new[] { 1, 2 })
        {
            var id = db.Actions.Add(companyId, "1", MoveKind.Submitted, moved.Id, moved.Date, moved.Ewc, moved.Amount, action: "два-реда");

            db.Actions.Trash(id);
        }

        // Поправеното е най-дългото в дневника: „поправено …“ и „беше …“.
        db.Actions.Add(companyId, "1", MoveKind.Submitted, moved.Id, moved.Date, moved.Ewc, moved.Amount,
            action: "поправено", since: DateTime.UtcNow, was: "12,345 т на 08.09.2026, 3 реда");

        // Новините идват отвън и са дълги колкото ги е писал човекът: едната с
        // връзка, другата — с дълго заглавие, което трябва да се реже, а не да
        // избутва датата и кръстчето.
        db.News.Sync(
        [
            new NewsItem("audit-long", new DateTime(2026, 11, 14), NewsSeverity.Important,
                "Срокът за годишните отчети е 31 март — и тази година НИСО приема само подписани файлове",
                string.Join(" ", Enumerable.Repeat(
                    "Годишните отчети по Приложение 9 се подават в НИСО до 31 март, подписани с КЕП на представляващия.", 4)),
                false, "https://example.org/srok"),
            new NewsItem("audit-short", new DateTime(2026, 11, 10), NewsSeverity.Info,
                "Ново в 1.3.0", "Известия на началния екран.", false)
        ]);

        return companyId;
    }

    /// <summary>Бележки, дълги колкото истинските — с заглавия, списък и връзка.</summary>
    private const string LongNotes = """
        ## Ново
        - Известия на началния екран: ново издание, бележките към него и новините от услугата
        - Обновяване от „Настройки“: „Провери сега“, „Обнови до …“ и пробни издания за този, който тества
        - Ctrl+1 … Ctrl+8 отварят екраните от лентата
        ## Поправки
        - Отчетът от „Продължете оттам…“ се отваря направо, а не списъкът с отчети
        - Таблицата `r_yr_generated_waste` вече показва [надписите](https://example.org) си
        """;

    private static void Screens(ImportService service, Workspace db, long companyId, int width, int height)
    {
        // Като инсталирана програма — само тя показва „Обнови сега“. Без мрежа.
        var release = new ReleaseInfo("1.3.10", LongNotes, "https://example.org/releases/tag/v1.3.10");
        var updates = new UpdateService(_ => new FakeUpdates { Found = release });

        using var form = new ShellForm(service, db, null, updates);

        Place(form, width, height);
        form.Show();
        Settle(form);

        // С избрана фирма, както се работи. Без нея складът крие „Внеси…“ и
        // лентата му има едно копче по-малко — тъкмо колкото да се побере на
        // 1366 пиксела, докато на истинския лаптоп три копчета изчезваха.
        var chosen = form.CompanyChoices.ToList().FindIndex(c => c.Contains("Метал Рециклиране", StringComparison.Ordinal));

        if (chosen >= 0) form.UseCompany(chosen);
        Settle(form);

        var pages = (System.Collections.IList)typeof(ShellForm)
            .GetField("_pages", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;

        var show = typeof(ShellForm).GetMethod("Show", BindingFlags.NonPublic | BindingFlags.Instance,
            null, [typeof(Control)], null)!;

        StockPage? stock = null;

        foreach (var entry in pages)
        {
            var page = (Control)((ITuple)entry!)[1]!;

            show.Invoke(form, [page]);
            Settle(form);
            Inspect(page.GetType().Name, form);

            // Известието и рамката за обновяване — във всяко положение, с дълги бележки
            // и дълга причина. Сменят надписи и копчета, а това е мястото, където се реже.
            if (page is HomePage or SettingsPage)
            {
                var problem = UpdateBackends.Explain(new HttpRequestException("x", null, System.Net.HttpStatusCode.Forbidden));

                foreach (var state in new[]
                         {
                             new UpdateState(UpdateStage.Available, release),
                             new UpdateState(UpdateStage.Downloading, release, 47),
                             new UpdateState(UpdateStage.Ready, release, 100),
                             new UpdateState(UpdateStage.Failed, release, Problem: problem),
                             new UpdateState(UpdateStage.Current, CheckedAt: DateTime.Now)
                         })
                {
                    updates.Show(state);
                    show.Invoke(form, [page]);
                    Settle(form);
                    Inspect($"{page.GetType().Name}/{state.Stage}", form);
                }

                updates.Show(new UpdateState(UpdateStage.Idle));
            }

            if (page is not StockPage store) continue;

            stock = store;
            store.UseDate(new DateOnly(2026, 9, 30));

            foreach (var kind in Moves.Stores)
            {
                store.UseStore(kind);
                Settle(form);
                Inspect($"StockPage/{kind}", form);

                if (!store.DetailColumnsFit)
                    Found.Add($"[StockPage/{kind}] стълбовете долу не се побират в таблицата");
            }

            // „Поотделно“ е четвърти разрез и други колони горе.
            store.UseStore(StoreKind.Submitted);
            store.UseMoveView();
            Settle(form);
            Inspect("StockPage/Поотделно", form);

            // Таблицата превърта и затова отвътре не се вижда като изрязана: така
            // последният ѝ стълб оставаше зад ръба при 150 %, без нищо да излиза
            // от родителя си.
            if (!store.ColumnsFit)
                Found.Add("[StockPage/Поотделно] стълбовете горе не се побират в таблицата");

            foreach (var header in store.HeadersCut)
                Found.Add($"[StockPage/Поотделно] заглавието се реже или избутва сбора си: {header}");

            if (!store.FiguresFit)
                Found.Add("[StockPage/Поотделно] дата, код, партида или количество се режат");

            if (store.DetailPaneShown)
                Found.Add("[StockPage/Поотделно] долната таблица стои празна под списъка");

            // И с надписи: те се връщат от отметката и там мястото е най-малко.
            db.Settings.Set(SettingsStore.Keys.StoreViewIcons, false);
            store.Refresh();
            Settle(form);
            Inspect("StockPage/Поотделно/надписи", form);

            db.Settings.Set(SettingsStore.Keys.StoreViewIcons, true);
            store.Refresh();
            Settle(form);

            // И обратно: щом разрезът се смени, долната таблица трябва да се върне.
            store.UseView(byPartner: false);
            Settle(form);

            if (!store.DetailPaneShown)
                Found.Add("[StockPage/Submitted] долната таблица не се върна след „Поотделно“");
        }

        if (stock is null) return;

        show.Invoke(form, [stock]);

        var on = new DateOnly(2026, 9, 30);

        Dialog("TreatDialog", () =>
        {
            stock.UseStore(StoreKind.Received);
            stock.SelectMaster(0);
            return new TreatDialog(service, db, companyId, "1", stock.Selection(), on);
        }, width, height);

        Dialog("GenerateDialog", () =>
        {
            stock.UseStore(StoreKind.Treated);
            stock.SelectMaster(0);
            return stock.FormDialog(stock.Selection());
        }, width, height);

        Dialog("SubmitDialog", () =>
        {
            stock.UseStore(StoreKind.Formed);
            stock.SelectMaster(0);
            return new SubmitDialog(service, db, companyId, "1", stock.Selection(), on);
        }, width, height);

        // Поправката: същите прозорци, с лентата отгоре. На лаптопа лентата се пренася на
        // повече редове и взема от височината — затова и с най-дългия ѝ текст.
        AmendSeed Long(MoveKind kind, out IReadOnlyList<StockShare> shares)
        {
            var moves = db.Ledger.Read(companyId, "1");
            var move = moves.First(m => m.Kind == kind);

            var entry = new ActionEntry(0, companyId, "1", kind, move.Id, move.Date, move.Period, move.Ewc, move.Amount,
                "", DateTime.UtcNow, true);

            var seed = stock.Prepare(entry, out shares, out var why)
                ?? throw new InvalidOperationException(kind + ": " + why);

            return seed with { Takers = [.. moves.Where(m => m.Kind != kind).Take(3)], Held = 123.456m };
        }

        Dialog("TreatDialog/поправка", () =>
        {
            var seed = Long(MoveKind.Treated, out var shares);
            return new TreatDialog(service, db, companyId, "1", shares, seed.Date, seed);
        }, width, height);

        Dialog("GenerateDialog/поправка", () =>
        {
            var seed = Long(MoveKind.Generated, out var shares);
            return new GenerateDialog(service, db, companyId, "1", shares, seed.Date, seed);
        }, width, height);

        Dialog("SubmitDialog/поправка", () =>
        {
            var seed = Long(MoveKind.Submitted, out var shares);
            return new SubmitDialog(service, db, companyId, "1", shares, seed.Date, seed.Direct, seed);
        }, width, height);

        // От склада — с „Внеси…“; от „Месец“ — без него и с реда, който казва къде е.
        Dialog("MoveSheetDialog", () =>
            new MoveSheetDialog(service, db, companyId, "1", MoveKind.Received, new LedgerPeriod(2026, 9),
                SheetImport.Offered), width, height);

        Dialog("MoveSheetDialog/Месец", () =>
            new MoveSheetDialog(service, db, companyId, "1", MoveKind.Received, new LedgerPeriod(2026, 9)), width, height);

        Dialog("MoveSheetDialog/Предаден", () =>
            new MoveSheetDialog(service, db, companyId, "1", MoveKind.Submitted, new LedgerPeriod(2026, 9)), width, height);

        Dialog("MoveDialog", () =>
            new MoveDialog(service, db, companyId, "1", MoveKind.Received, new LedgerPeriod(2026, 9)), width, height, false);

        Dialog("MoveDialog/Предаден", () =>
            new MoveDialog(service, db, companyId, "1", MoveKind.Submitted, new LedgerPeriod(2026, 9)), width, height, false);

        Dialog("ActionLogDialog", () => new ActionLogDialog(db, companyId, "1"), width, height);

        // И разделът с двете червени копчета — без него той се сглобява, но не се вижда.
        Dialog("ActionLogDialog/Отложено", () =>
        {
            var log = new ActionLogDialog(db, companyId, "1");

            log.UseBin(true);
            log.Pick(0);

            return log;
        }, width, height);

        Dialog("ActionLogDialog/най-тесен", () =>
        {
            var log = new ActionLogDialog(db, companyId, "1");

            log.UseBin(true);
            log.Pick(0);

            return log;
        }, Theme.Scaled(900), Theme.Scaled(460));
        Dialog("StartStockDialog", () => new StartStockDialog(service, db, companyId, "1"), width, height);
        Dialog("NewReportDialog", () => new NewReportDialog(service), width, height, false);
        Dialog("CompanyDialog", () => new CompanyDialog(db.Companies, 5), width, height, false);
        Dialog("PeriodsDialog", () => new PeriodsDialog(db, new CompanyScope(db)), width, height, false);
        Dialog("ImportsDialog", () => new ImportsDialog(db), width, height, false);
        // Съставянето в трите си положения: готово, с грешки по схемата и спряно —
        // най-дългите текстове, които прозорецът казва.
        Dialog("BookDialog", () => new BookDialog(new LedgerPeriod(2026, 9),
            (_, _) => new BookWeight(12, 0, [], [], 0)), width, height, false);

        Dialog("BookDialog/грешки", () => new BookDialog(new LedgerPeriod(2026, 9),
            (_, _) => new BookWeight(412, 37, [],
            [
                "Получен: липсва задължителна стойност за „ЕИК/ЕГН на лицето, от което е получен отпадъкът“ — 412 реда",
                "Предаден: липсва задължителна стойност за „Наименование на лицето, на което е предаден“ — 8 реда",
                "Третиран, ред 3: стойността „R12“ не е от допустимите за „Код на дейността“",
                "…и още 2 вида"
            ], 425)), width, height, false);

        Dialog("BookDialog/спряно", () =>
        {
            var waiting = Reconcile.WaitingByMonth(db.Batches.List(companyId, "1"), db.Ledger.Read(companyId, "1"),
                db.Ledger.Links(companyId, "1"), new LedgerPeriod(2026, 7), new LedgerPeriod(2026, 9));

            return new BookDialog(new LedgerPeriod(2026, 9), (from, to) => new BookWeight(12, 0,
                [.. waiting.Where(w => w.Month >= from && w.Month <= to)], [], 0));
        }, width, height, false);

        // Проверката на месеца — с истинските бележки на пробния септември.
        Dialog("MonthCheckDialog", () =>
        {
            var september = new LedgerPeriod(2026, 9);
            var issues = LedgerChecks.For(db, companyId, "1", september);
            var quarter = Reconcile.WaitingByMonth(db.Batches.List(companyId, "1"), db.Ledger.Read(companyId, "1"),
                db.Ledger.Links(companyId, "1"), new LedgerPeriod(2026, 7), september);

            return new MonthCheckDialog("Проверка — септември 2026 — Метал Рециклиране и Търговия ЕООД",
                MonthVerdict.For(september, 7, false, issues, quarter), issues,
                quarter.FirstOrDefault(w => w.Month == september));
        }, width, height, false);
        Dialog("ReceiversDialog", () => new ReceiversDialog(db, companyId), width, height, false);
        Dialog("ReceiversDialog/Договори", () => new ReceiversDialog(db, companyId, PartnerRole.Contract), width, height, false);
        Dialog("ValueListDialog", () => new ValueListDialog(service, db.Companies.List()), width, height, false);
        Dialog("ShortcutsDialog", () => new ShortcutsDialog(), width, height, false);

        if (db.Batches.List(companyId, "1").FirstOrDefault() is { } batch)
            Dialog("BatchDialog", () => new BatchDialog(db, companyId, "1", batch, new Dictionary<string, string>()),
                width, height, false);

        var documentId = db.Documents.Create("book", BookNodes.Report, "Проба за мащаба",
            new Dictionary<string, string> { ["year"] = "2026", ["quarter"] = "3", ["eik"] = "203344556", ["type"] = "REPLACE" },
            DocumentOrigin.Manual, companyId);

        Dialog("EditorForm", () => new EditorForm(service, db, documentId), width, height);
    }

    private static void Dialog(string name, Func<Form> make, int width, int height, bool maximize = true)
    {
        try
        {
            using var dialog = make();

            dialog.StartPosition = FormStartPosition.Manual;
            dialog.Location = new Point(-6000, -6000);

            if (maximize)
            {
                Place(dialog, width, height);

                // Като отворено на цял екран на лаптопа: размерът е там преди OnShown.
                dialog.Load += (_, _) =>
                {
                    dialog.WindowState = FormWindowState.Normal;
                    dialog.MinimumSize = Size.Empty;
                    dialog.ClientSize = new Size(width, height);
                };
            }

            dialog.Show();
            Settle(dialog);
            Inspect(name, dialog);

            // Лентата е в самия прозорец и се вижда: иначе и проверката долу, и текстът в
            // нея минават, а човек не вижда нищо.
            if (name.EndsWith("/поправка", StringComparison.Ordinal) &&
                !dialog.Controls.OfType<NoticeStrip>().Any(n => n.Visible && n.Text.Length > 0))
                Found.Add($"[{name}] лентата при поправка я няма");

            // Прегледът не вижда в таблица, а десният стълб долу е самото взето количество.
            if (dialog is TreatDialog { OriginColumnsFit: false } or GenerateDialog { OriginColumnsFit: false } or
                SubmitDialog { OriginColumnsFit: false })
                Found.Add($"[{name}] долната таблица не побира стълбовете си");

            // Само на цял екран: стеснен на ръка, дневникът превърта отвътре.
            if (maximize && dialog is ActionLogDialog log && width >= 1000 && !log.CellsFit)
                Found.Add($"[{name}] клетка в дневника не се чете цяла");

            dialog.Close();
        }
        catch (Exception ex)
        {
            // Първото място в програмата, откъдето е дошло — без него „SplitterDistance
            // must be between…“ не казва в кой разделител.
            var at = ex.StackTrace?.Split(Environment.NewLine)
                .FirstOrDefault(l => l.Contains("RiosvImports.App"))?.Trim() ?? "";
            Found.Add($"[{name}] изключение: {ex.GetType().Name}: {ex.Message} {at}");
        }
    }

    private static void Place(Form form, int width, int height)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.WindowState = FormWindowState.Normal;
        form.MinimumSize = Size.Empty;
        form.Location = new Point(-6000, -6000);
        form.ClientSize = new Size(width, height);
    }

    private static void Settle(Control control)
    {
        for (var i = 0; i < 3; i++)
        {
            Application.DoEvents();
            control.PerformLayout();
        }
    }

    // ------------------------------------------------------------------ проверката

    private static void Inspect(string screen, Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (!child.Visible) continue;

            Check(screen, child);
            Inspect(screen, child);
        }

        Overlaps(screen, root);
    }

    /// <summary>
    /// Контрола, която закрива съседна, за която се щраква — без нищо да излиза
    /// от родителя си. Така лентата на склада в „Получен“ се пъхаше под „Само
    /// този месец“ („Дневник“ и „Обнови“ — изцяло, „Начални…“ — до половината),
    /// подредбата в „Отчети“ — под произхода, а превключвателят в „Списъци“ —
    /// под заглавието.
    ///
    /// Застъпване само по себе си не е грешка: заглавие през целия ред с
    /// отметка отдясно върху празния му край е нарочно. Грешка е, когато
    /// ОТДОЛУ остава нещо за щракане; кое е отгоре, казва самият Windows.
    /// </summary>
    private static void Overlaps(string screen, Control root)
    {
        var shown = root.Controls.Cast<Control>()
            .Where(c => c.Visible && c.Width > 0 && c.Height > 0)
            .ToList();

        for (var i = 0; i < shown.Count; i++)
        {
            for (var j = i + 1; j < shown.Count; j++)
            {
                var both = Rectangle.Intersect(shown[i].Bounds, shown[j].Bounds);

                if (both.Width <= 2 || both.Height <= 2) continue;

                var middle = new Point(both.X + both.Width / 2, both.Y + both.Height / 2);
                var top = root.GetChildAtPoint(middle, GetChildAtPointSkip.Invisible);

                if (top != shown[i] && top != shown[j]) continue;

                var under = top == shown[i] ? shown[j] : shown[i];

                if (Clickable(under, both))
                    Found.Add($"[{screen}] {Trail(under)} е закрито с {both.Width}x{both.Height} px от {Trail(top)}");
            }
        }
    }

    /// <summary>
    /// Дали в това място (в координатите на родителя ѝ) контролата има нещо за
    /// щракане — тя самата или някое от децата ѝ. Лентата с разделите в
    /// редактора е изключение: копчетата нарочно стоят върху празния ѝ край.
    /// </summary>
    private static bool Clickable(Control control, Rectangle area)
    {
        var hit = Rectangle.Intersect(control.Bounds, area);

        if (hit.Width <= 2 || hit.Height <= 2 || control.GetType().Name == "TabStrip") return false;

        if (control is ButtonBase or ListControl or TextBoxBase or UpDownBase or DateTimePicker
            or DataGridView or SegmentedControl)
            return true;

        hit.Offset(-control.Left, -control.Top);

        return control.Controls.Cast<Control>().Any(child => child.Visible && Clickable(child, hit));
    }

    private static void Check(string screen, Control c)
    {
        var where = Trail(c);

        if (c.Parent is { } parent && parent is not ScrollableControl { AutoScroll: true } &&
            c.Width > 0 && c.Height > 0)
        {
            var overRight = c.Right - parent.ClientSize.Width;
            var overBottom = c.Bottom - parent.ClientSize.Height;

            if (overRight > 2 || overBottom > 2)
                Found.Add($"[{screen}] излиза навън с {Math.Max(0, overRight)}x{Math.Max(0, overBottom)} px: {where}");

            // И наляво/нагоре. Лента, подредена отдясно наляво, която не се
            // побира, бута копчетата си НАЛЯВО извън себе си: така в „Получен“
            // на 1366 пиксела „Дневник“ и „Обнови“ не се виждаха изобщо, а
            // проверката гледаше само десния и долния ръб.
            if (c.Left < -2 || c.Top < -2)
                Found.Add($"[{screen}] излиза навън наляво/нагоре с {Math.Max(0, -c.Left)}x{Math.Max(0, -c.Top)} px: {where}");
        }

        // Тънките разделители (1–2 пиксела, а другата страна дълга) са нарочни.
        var divider = (c.Width <= 2 && c.Height >= 10) || (c.Height <= 2 && c.Width >= 10);

        if (!divider && (c.Height is > 0 and < 6 || c.Width is > 0 and < 6))
            Found.Add($"[{screen}] смачкано до {c.Width}x{c.Height}: {where}");

        switch (c)
        {
            case Label { AutoSize: false, AutoEllipsis: false } label when label.Text.Length > 0 && label.Width > 0:
            {
                var inner = new Size(Math.Max(1, label.ClientSize.Width - label.Padding.Horizontal), int.MaxValue);
                var need = TextRenderer.MeasureText(label.Text, label.Font, inner, TextFormatFlags.WordBreak).Height;
                var have = label.ClientSize.Height - label.Padding.Vertical;

                if (need > have + 2)
                    Found.Add($"[{screen}] надписът иска {need} px, има {have}: {where} „{Short(label.Text)}“");
                break;
            }

            case AppButton button when button.Text.Length > 0:
            {
                var text = TextRenderer.MeasureText(button.Text, button.Font);

                if (text.Height > button.Height - 2 || text.Width > button.Width - 8)
                    Found.Add($"[{screen}] копчето е {button.Width}x{button.Height} за надпис {text.Width}x{text.Height}: „{button.Text}“");
                break;
            }

            case CheckBox box when box.Text.Length > 0 && !box.AutoSize:
            {
                var text = TextRenderer.MeasureText(box.Text, box.Font);
                var glyph = (int)(16 * Theme.Zoom) + 6;

                if (text.Width + glyph > box.Width + 2 || text.Height > box.Height + 2)
                    Found.Add($"[{screen}] отметката реже текста си: „{Short(box.Text)}“");
                break;
            }

            case DataGridView grid:
            {
                if (grid.RowTemplate.Height < grid.Font.Height + 4)
                    Found.Add($"[{screen}] ред от {grid.RowTemplate.Height} px за шрифт от {grid.Font.Height}: {where}");

                // Таблица, която не побира заглавието си и поне един ред, е
                // скрита, макар да е „видима“: така изчезваше горната таблица
                // на склада при 150 %, без нищо да е по-малко от шест пиксела.
                if (grid.ColumnHeadersVisible && grid.ClientSize.Height < grid.ColumnHeadersHeight + grid.RowTemplate.Height)
                    Found.Add($"[{screen}] таблицата е {grid.ClientSize.Height} px — не побира заглавието и един ред: {where}");

                var font = grid.ColumnHeadersDefaultCellStyle.Font ?? grid.Font;

                foreach (DataGridViewColumn column in grid.Columns)
                {
                    if (!column.Visible || column.HeaderText.Length == 0) continue;

                    var need = TextRenderer.MeasureText(column.HeaderText, font).Height + 8;

                    if (need <= grid.ColumnHeadersHeight + 2) continue;

                    Found.Add($"[{screen}] заглавието иска {need} px, има {grid.ColumnHeadersHeight}: „{Short(column.HeaderText)}“");
                    break;
                }

                break;
            }

            case NoticeStrip notice when !notice.Fits:
                Found.Add($"[{screen}] лентата при поправка реже текста си: „{Short(notice.Text)}“");
                break;

            case NoticeStrip notice when notice.Text.Length > 0 && !notice.Visible:
                Found.Add($"[{screen}] лентата при поправка има какво да каже, но не се вижда");
                break;

            case SegmentedControl segmented when !segmented.Fits:
                Found.Add($"[{screen}] превключвателят реже надписите си: {string.Join(" | ", segmented.Items)}");
                break;
        }
    }

    private static string Trail(Control c)
    {
        var parts = new List<string>();

        for (var x = c; x is not null && x is not Form && parts.Count < 4; x = x.Parent)
            parts.Add(x.Text is { Length: > 0 } t && x is not TextBoxBase && x is not ComboBox && x is not DataGridView
                ? $"{x.GetType().Name}„{Short(t, 18)}“"
                : x.GetType().Name);

        parts.Reverse();
        return string.Join(" > ", parts);
    }

    private static string Short(string text, int max = 50)
    {
        var one = text.Replace("\r", " ").Replace("\n", " ");
        return one.Length <= max ? one : one[..max] + "…";
    }
}
