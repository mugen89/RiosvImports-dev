using System.Globalization;
using System.Text;
using RiosvImports.Core;
using RiosvImports.Core.Balance;
using RiosvImports.Core.Schema;
using RiosvImports.Data;

// Пълни базата с измислени отчетни книги за оползотворяване.
//
// Съществува заради баланса: празен екран не показва нищо, а един отчет не
// показва повече. За да се види как изглежда таблото при истинска работа —
// няколко кода, няколко месеца, една незавършена книга и една предходна
// година — трябват данни, а истински данни не се раздават.
//
// Всичко, което тази програма прави, носи белег: заглавията започват с
// „ПРОБА“, а фирмите са с ЕИК 9000000xx. „--clear“ маха точно това и нищо
// друго — затова белегът е и единственият начин да се разпознае.
//
//     dotnet run --project tools\RiosvImports.Seed
//     dotnet run --project tools\RiosvImports.Seed -- --clear
//     dotnet run --project tools\RiosvImports.Seed -- --db C:\път\до\workspace.db
//
// Без „--db“ се пълни базата на програмата, пусната от Visual Studio: от 1.3.1
// тя стои до програмата, в „Data“ (src\RiosvImports.App\bin\Debug\…\Data).

Console.OutputEncoding = Encoding.UTF8;

const string Mark = "ПРОБА";

// Фирмата, на която се прави нарочното преразходване. Поле, защото се решава
// при създаването на фирмите, а се ползва при писането на месеците.
long Overdrawn = 0;

var clear = args.Contains("--clear");
var path = Value("--db") ?? DefaultDatabase();

Console.WriteLine($"база: {path}");

using var db = Workspace.Open(path);

if (clear)
{
    Clear();
    return 0;
}

Seed();

return 0;

// Базата на програмата от Visual Studio — до нея, в „Data“. Извън хранилището
// (инструментът е копиран другаде) — старото място в профила.
string DefaultDatabase()
{
    for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
    {
        if (!File.Exists(Path.Combine(folder.FullName, "RiosvImports.sln"))) continue;

        return Path.Combine(folder.FullName, "src", "RiosvImports.App", "bin", "Debug", "net9.0-windows",
            "Data", "workspace.db");
    }

    return Workspace.DefaultPath();
}

string? Value(string name)
{
    var at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}

// ------------------------------------------------------------------ чистене

void Clear()
{
    var removed = 0;

    foreach (var document in db.Documents.List().Where(d => d.Title.StartsWith(Mark, StringComparison.Ordinal)))
    {
        db.Documents.Delete(document.Id);
        removed++;
    }

    var companies = 0;

    foreach (var company in db.Companies.List().Where(c => c.Eik.StartsWith("9000000", StringComparison.Ordinal)))
    {
        // Началните количества падат заедно с фирмата: външният ключ е с
        // „ON DELETE CASCADE“, а базата се отваря с включени външни ключове.
        db.Companies.Remove(company.Id);
        companies++;
    }

    Console.WriteLine($"махнати: {removed} отчета, {companies} фирми");
}

// ------------------------------------------------------------------ пълнене

void Seed()
{
    var service = new ImportService(ImportService.LocateSchemaFolder());
    var book = service.GetSchema("book");
    var report = book.FindReport(BalanceSheets.Report)
        ?? throw new InvalidOperationException("схемата не описва отчетната книга за оползотворяване");

    var codes = Codes(report);

    Console.WriteLine("кодове: " + string.Join(", ", codes));

    var alpha = Company("900000011", $"{Mark} Рециклиране ЕООД");
    var beta = Company("900000022", $"{Mark} Метал ООД");

    Overdrawn = alpha;

    // Едно и също зърно всеки път: две пускания дават една и съща база, а
    // разлика в число тогава значи разлика в кода.
    var dice = new Random(20260903);

    // Предходната година — една книга за декември, за да има какво да покаже
    // изборът „край на предходната година“.
    Book(report, alpha, 2025, 12, DocumentStatus.Checked, codes, dice, heavy: false);

    for (var month = 1; month <= 8; month++)
    {
        // Последната е незавършена: тя е поводът изобщо да има избор за
        // временно включване на чернова.
        var status = month switch
        {
            8 => DocumentStatus.Draft,
            <= 3 => DocumentStatus.Signed,
            _ => DocumentStatus.Checked
        };

        Book(report, alpha, 2026, month, status, codes, dice, heavy: month == 3);
    }

    // Втора фирма, две книги: сборният изглед на няколко фирми иска поне две.
    Book(report, beta, 2026, 2, DocumentStatus.Checked, codes, dice, heavy: false);
    Book(report, beta, 2026, 5, DocumentStatus.Checked, codes, dice, heavy: false);

    // Въведени начални количества — за да не е празен и другият избор.
    //
    // Последните два кода са нарочни: единият се предава, без да е образуван
    // (излиза на червено), а другият стои цяла година непокътнат.
    db.Openings.Replace(
    [
        new Opening(alpha, 2026, codes[0], 4.5m),
        new Opening(alpha, 2026, codes[3], 1.25m),
        new Opening(alpha, 2026, codes[8], 3m),
        new Opening(alpha, 2026, codes[9], 6.75m),
        new Opening(beta, 2026, codes[6], 8m)
    ]);

    Console.WriteLine();
    Console.WriteLine("готово. „Баланс“ показва 2026 г.; за предходната изберете 2025.");
    Console.WriteLine($"махане: dotnet run --project tools{Path.DirectorySeparatorChar}RiosvImports.Seed -- --clear");
}

long Company(string eik, string name)
{
    if (db.Companies.FindByEik(eik) is { } existing) return existing.Id;

    var id = db.Companies.Add(eik, name);
    Console.WriteLine($"фирма: {name} — {eik}");

    return id;
}

/// <summary>Десет кода по ЕКО от самата схема — измислен код не се приема при проверка.</summary>
List<string> Codes(ReportNode report)
{
    var domain = Node(report, BalanceSheet.Received)
        .Attributes.First(a => a.Name == "ewc").Domain;

    string[] wanted =
    [
        "160604", "191202", "150101", "170405", "160103",
        "200121", "160213", "191001", "120101", "150110"
    ];

    var found = wanted.Where(code => domain.Options.Any(o => o.Value == code)).ToList();

    // Схемата се мени без наше участие: ако някой от кодовете вече го няма,
    // списъкът се допълва от самата номенклатура, вместо да се къса.
    foreach (var option in domain.Options)
    {
        if (found.Count >= 10) break;
        if (!found.Contains(option.Value)) found.Add(option.Value);
    }

    return found;
}

static ReportNode Node(ReportNode report, BalanceSheet sheet) =>
    report.SelfAndDescendants().First(n => n.ElementName == BalanceSheets.Node(sheet));

// ------------------------------------------------------------------ една книга

void Book(
    ReportNode report,
    long company,
    int year,
    int month,
    DocumentStatus status,
    List<string> codes,
    Random dice,
    bool heavy)
{
    var eik = db.Companies.Find(company)?.Eik ?? string.Empty;
    var who = db.Companies.Find(company)?.Name.Replace(Mark + " ", string.Empty) ?? string.Empty;

    var title = $"{Mark} · Оползотворяване {month:00}.{year} — {who}";

    // Обявеният период на книгата. Два месеца са нарочно сбъркани, за да има
    // какво да намери проверката за покритие: април тръгва два дни по-късно
    // (пропуск), а юни започва още в края на май (застъпване).
    var first = new DateTime(year, month, 1);
    var last = new DateTime(year, month, DateTime.DaysInMonth(year, month));

    if (year == 2026 && month == 4) first = first.AddDays(2);
    if (year == 2026 && month == 6) first = first.AddDays(-7);

    var id = db.Documents.Create("book", BalanceSheets.Report, title,
        new Dictionary<string, string>
        {
            ["year"] = year.ToString(CultureInfo.InvariantCulture),
            ["eik"] = eik,
            ["period_start"] = first.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
            ["period_end"] = last.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
        },
        DocumentOrigin.Manual, company);

    // Самият отчет: един работен лист, към който сочат всички редове.
    db.Documents.SaveRows(id, BalanceSheets.Report,
        [new Dictionary<string, string> { ["stagenum"] = "1" }]);

    var received = Node(report, BalanceSheet.Received);
    var treated = Node(report, BalanceSheet.Treated);
    var generated = Node(report, BalanceSheet.Generated);
    var submitted = Node(report, BalanceSheet.Submitted);

    var operation = treated.Attributes.First(a => a.Name == "op_id_code").Domain.Options[0];
    var sender = received.Attributes.First(a => a.Name == "type_originator_code").Domain.Options[0].Value;
    var receiver = submitted.Attributes.First(a => a.Name == "type_receiver_code").Domain.Options[0].Value;

    var reuse = submitted.Attributes.First(a => a.Name == "is_for_reuse").Domain;
    var noReuse = reuse.Options.FirstOrDefault(o => o.Value == "0")?.Value ?? reuse.Options[0].Value;

    // Кодовете за месеца: пет-седем от първите осем, различни всеки месец.
    // Последните два са запазени за двата случая, които балансът трябва да
    // умее да покаже, а случайността не гарантира: код, който само се предава
    // (отрицателно салдо), и код, който стои непокътнат цяла година.
    var pool = codes.Take(8).OrderBy(_ => dice.Next()).Take(dice.Next(5, 8)).ToList();

    var scale = heavy ? 3m : 1m;

    List<Dictionary<string, string>> Rows(int count, Func<int, Dictionary<string, string>> make) =>
        [.. Enumerable.Range(0, count).Select(make)];

    string Day(int day) => $"{Math.Min(day, DateTime.DaysInMonth(year, month)):00}.{month:00}.{year}";
    string Tons(decimal low, decimal high) =>
        Math.Round(low + (decimal)dice.NextDouble() * (high - low), 2).ToString("0.##", CultureInfo.GetCultureInfo("bg-BG"));

    // Получен и обработен вървят по двойки: каквото е дошло, се третира.
    var intake = Rows(Math.Min(3, pool.Count), i => new Dictionary<string, string>
    {
        ["stagenum"] = "1",
        ["date_record"] = Day(4 + i * 6),
        ["ewc"] = pool[i],
        ["type_originator_code"] = sender,
        ["name_originator"] = $"{Mark} Доставчик {i + 1} ООД",
        ["amount_ton"] = Tons(5m * scale, 25m * scale)
    });

    db.Documents.SaveRows(id, BalanceSheets.Node(BalanceSheet.Received), intake);

    db.Documents.SaveRows(id, BalanceSheets.Node(BalanceSheet.Treated),
    [
        .. intake.Select(row => new Dictionary<string, string>
        {
            ["stagenum"] = "1",
            ["date_record"] = row["date_record"],
            ["ewc"] = row["ewc"],
            ["op_id_code"] = operation.Value,
            ["op_descr"] = operation.Description ?? operation.Value,
            ["amount_ton"] = row["amount_ton"]
        })
    ]);

    // Образуваният е това, което излиза от обработката — оттам се пълни
    // площадката.
    var madeCount = Math.Min(pool.Count, dice.Next(3, 6));

    db.Documents.SaveRows(id, BalanceSheets.Node(BalanceSheet.Generated),
        Rows(madeCount, i => new Dictionary<string, string>
        {
            ["stagenum"] = "1",
            ["date_record"] = Day(10 + i * 4),
            ["ewc"] = pool[i],
            ["waste_desc"] = Describe(received, pool[i]),
            ["amount_ton"] = Tons(2m * scale, 18m * scale)
        }));

    // Предаденият сваля салдото. В един месец нарочно се предава повече от
    // наличното: отрицателното салдо трябва да се вижда, а не да се вярва, че
    // не се случва.
    var sentCount = Math.Min(pool.Count, dice.Next(2, 5));
    var greedy = month == 6 && year == 2026;

    db.Documents.SaveRows(id, BalanceSheets.Node(BalanceSheet.Submitted),
        Rows(sentCount, i => new Dictionary<string, string>
        {
            ["stagenum"] = "1",
            ["date_record"] = Day(18 + i * 3),
            ["ewc"] = pool[i],
            ["type_receiver_code"] = receiver,
            ["name_receiver"] = $"{Mark} Получател {i + 1} АД",
            ["op_id_code"] = operation.Value,
            ["op_descr"] = operation.Description ?? operation.Value,
            ["is_for_reuse"] = noReuse,
            ["amount_ton"] = greedy && i == 0 ? Tons(40m, 70m) : Tons(1m * scale, 14m * scale)
        }));

    // Кодът, от който има начално количество, но нищо не се образува: в юли се
    // предава повече, отколкото го има, и салдото му излиза на червено.
    if (year == 2026 && month == 7 && company == Overdrawn)
    {
        var rows = db.Documents.ReadRows(id, BalanceSheets.Node(BalanceSheet.Submitted))
            .Select(r => r.Values).ToList();

        rows.Add(new Dictionary<string, string>
        {
            ["stagenum"] = "1",
            ["date_record"] = Day(24),
            ["ewc"] = codes[8],
            ["type_receiver_code"] = receiver,
            ["name_receiver"] = $"{Mark} Получател АД",
            ["op_id_code"] = operation.Value,
            ["op_descr"] = operation.Description ?? operation.Value,
            ["is_for_reuse"] = noReuse,
            ["amount_ton"] = "12,4"
        });

        db.Documents.SaveRows(id, BalanceSheets.Node(BalanceSheet.Submitted), rows);
    }

    if (status != DocumentStatus.Draft) db.Documents.SetStatus(id, status);

    Console.WriteLine($"  {title}  ·  {status}  ·  {pool.Count} кода");
}

string Describe(ReportNode node, string code) =>
    node.Attributes.First(a => a.Name == "ewc").Domain.Options
        .FirstOrDefault(o => o.Value == code)?.Description ?? code;
