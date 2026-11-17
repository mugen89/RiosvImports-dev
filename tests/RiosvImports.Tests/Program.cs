using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Text.Json;
using System.Xml;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using RiosvImports.Account;
using RiosvImports.Core;
using RiosvImports.Core.Balance;
using RiosvImports.App;
using RiosvImports.App.Controls;
using RiosvImports.App.Editor;
using RiosvImports.App.Shell;
using RiosvImports.App.Updates;
using RiosvImports.Data;
using RiosvImports.Core.Excel;
using RiosvImports.Core.Help;
using RiosvImports.Core.Ledger;
using RiosvImports.Core.Schema;
using RiosvImports.Core.Values;
using RiosvImports.Core.Xml;
using RiosvImports.Core.Import;
using RiosvImports.Core.Localization;
using RiosvImports.Setup;

Console.OutputEncoding = Encoding.UTF8;

// Българските формати, както ги слага и самата програма в Main. Без това
// проверките минават на машина с български регион и падат на всяка друга —
// включително на машините на GitHub, които са с американски.
var bulgarian = new System.Globalization.CultureInfo("bg-BG");
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = bulgarian;
System.Globalization.CultureInfo.CurrentCulture = bulgarian;

// Дневникът на грешките — във временната папка, не в профила. Проверките
// нарочно предизвикват грешки (неуспяло обновяване), а истинският дневник е
// на човека, който пуска тестовете. Процесите за лаптопа го наследяват.
Environment.SetEnvironmentVariable(RiosvImports.App.Program.CrashLogVariable,
    Path.Combine(Path.GetTempPath(), "RiosvImports.Tests", "error.log"));

// Лаптопът на голям монитор: програмата пуска себе си с „--layout“ и подражаван
// мащаб (виж LayoutAudit). Шрифтовете се правят веднъж за процес — затова отделно.
if (args.Length > 0 && args[0] == "--layout") return LayoutAudit.Run(args);

// Схемите се търсят нагоре по дървото, както прави и самата програма.
var schemaFolder = ImportService.LocateSchemaFolder();
var work = Path.Combine(Path.GetTempPath(), "RiosvImports.Tests");
Directory.CreateDirectory(work);

Console.WriteLine($"схеми : {schemaFolder}");
Console.WriteLine($"работна папка: {work}");

var service = new ImportService(schemaFolder);
var pass = 0;
var fail = 0;

void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine($"  {(ok ? "OK  " : "FAIL")}  {name}{(detail is null ? "" : "  -> " + detail)}");
    if (ok) pass++; else fail++;

    // В GitHub Actions провалът излиза и като бележка към хода: бележките се
    // виждат без вход, а целият дневник — само за влезлия.
    if (!ok && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
        Console.WriteLine("::error::" + $"{name}{(detail is null ? "" : " -> " + detail)}"
            .Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A"));
}

// =============================================== 1. Приложение 9 (годишен)
Console.WriteLine("\n### ТЕСТ 1 - Приложение 9, годишен отчет за образувани отпадъци");
{
    var year = service.GetSchema("year");
    var gw = year.FindReport("r_yr_generated_waste")!;
    var tpl = Path.Combine(work, "t1.xlsx");
    service.CreateTemplate(year, [gw], tpl,
        new Dictionary<string, string> { ["year"] = "2024", ["eik"] = "123456789" });

    using (var wb = new XLWorkbook(tpl))
    {
        Fill(wb, "r_yr_generated_waste", 3, new() { ["stagenum"] = "1", ["origin"] = "Производствена дейност" });
        Fill(wb, "r_yr_generated_waste", 4, new() { ["stagenum"] = "2", ["origin"] = "Ремонт" });
        Fill(wb, "r_yr_generated_waste", 5, new() { ["stagenum"] = "3" });
        Fill(wb, "r_yr_generated_waste_amt", 3, new() { ["stagenum"] = "1", ["amount_ton_gen"] = "10,5", ["amount_ton_end"] = "5,25" });
        Fill(wb, "r_yr_generated_waste_amt", 4, new() { ["stagenum"] = "2", ["amount_ton_gen"] = "20" });
        Fill(wb, "r_yr_generated_waste_amt", 5, new() { ["stagenum"] = "1", ["amount_ton_gen"] = "-5" });
        Fill(wb, "r_yr_generated_waste_amt", 6, new() { ["stagenum"] = "99", ["amount_ton_gen"] = "1" });
        Fill(wb, "r_yr_generated_waste_hand", 3, new() { ["stagenum"] = "1", ["type_receiver_code"] = "04", ["receiver_name"] = "Фирма А ЕООД", ["amount_ton_handed_over"] = "5,25" });
        Fill(wb, "r_yr_generated_waste_hand", 4, new() { ["stagenum"] = "2", ["type_receiver_code"] = "05 — Физическо лице", ["receiver_name"] = "Иван Петров", ["amount_ton_handed_over"] = "3" });
        Fill(wb, "r_yr_generated_waste_hand", 5, new() { ["stagenum"] = "2", ["type_receiver_code"] = "LEGAL", ["receiver_name"] = "Ф", ["amount_ton_handed_over"] = "1" });
        wb.Save();
    }

    var r = service.Process(service.ReadWorkbook(tpl));
    Check("XSD проверка минава", r.SchemaErrors.Count == 0, string.Join(" | ", r.SchemaErrors.Take(2)));
    Check("6 приети реда", r.TotalValidRows == 6, $"{r.TotalValidRows}");
    Check("4 отхвърлени реда", r.TotalInvalidRows == 4, $"{r.TotalInvalidRows}");
    var xml = r.Xml!.ToString();
    Check("десетичната запетая става точка", xml.Contains("\"10.5\""));
    Check("кодът от падащото меню се съкращава", xml.Contains("type_receiver_code=\"05\""));
    Check("правилно пространство от имена", xml.Contains("xmlns=\"http://niso.tlogica.com/year.xsd\""));
    Check("невалидните редове не влизат", !xml.Contains("-5") && !xml.Contains("LEGAL"));
}

// ============================================ 2. Отчетна книга (дати + период)
Console.WriteLine("\n### ТЕСТ 2 - Отчетна книга (дати и период)");
{
    var book = service.GetSchema("book");
    var rep = book.FindReport("r_b_generated_waste")!;
    var handed = book.FindReport("r_b_generated_waste_handed")!;
    var tpl = Path.Combine(work, "t2.xlsx");
    service.CreateTemplate(book, [rep, handed], tpl, new Dictionary<string, string>
    {
        ["year"] = "2024", ["eik"] = "123456789",
        ["quarter"] = "1", ["period_start"] = "01.01.2024", ["period_end"] = "31.03.2024"
    });

    using (var wb = new XLWorkbook(tpl))
    {
        foreach (var name in new[] { "r_b_generated_waste", "r_b_generated_waste_handed" })
        {
            var ws = wb.Worksheet(name);
            var cols = Headers(ws);
            Console.WriteLine($"  ({name}: {string.Join(", ", cols.Where(c => c.Length > 0))})");
            var vals = new Dictionary<string, string> { ["stagenum"] = "1" };
            foreach (var c in cols.Where(c => c.Length > 0))
                if (!vals.ContainsKey(c)) vals[c] = SampleFor(book, name, c);
            Fill(wb, name, 3, vals);
        }
        wb.Save();
    }

    var r = service.Process(service.ReadWorkbook(tpl));
    foreach (var i in r.AllIssues) Console.WriteLine("     " + i);
    foreach (var e in r.SchemaErrors) Console.WriteLine("     XSD: " + e);
    Check("XSD проверка минава", r.SchemaErrors.Count == 0);
    var xml = r.Xml?.ToString() ?? "";
    Check("дата дд.мм.гггг става ISO", xml.Contains("2024-01-15"));
    Check("периодът е в корена", xml.Contains("period_start=\"2024-01-01\"") && xml.Contains("period_end=\"2024-03-31\""));
}

// ============================== 3. Вложен отчет (r_yr_rd_nuba, двойно ниво)
Console.WriteLine("\n### ТЕСТ 3 - Вложена структура (r_yr_rd_nuba)");
{
    var year = service.GetSchema("year");
    var nuba = year.FindReport("r_yr_rd_nuba")!;
    var layout = WorkbookLayout.Build(year, [nuba]);
    foreach (var s in layout.Sheets)
        Console.WriteLine($"  лист {s.SheetName,-30} връзки=[{string.Join(",", s.LinkColumns.Select(c => c.Name))}] id={s.IdentityColumn?.Name ?? "-"} колони={s.Columns.Count}");

    var header = layout.Sheets.FirstOrDefault(s => s.Node.ElementName == "r_yr_rd_nuba_iii_e_header");
    Check("вложеният възел получава собствен идентификатор", header?.IdentityColumn is not null);
    var child = layout.Sheets.FirstOrDefault(s => s.Node.ElementName == "r_yr_rd_nuba_iii_e_origin");
    Check("детето му сочи през два ключа", child is not null && child.LinkColumns.Count == 2,
          child is null ? "липсва лист" : string.Join(",", child.LinkColumns.Select(c => c.Name)));
}

// ===================================== 4. Шаблони за всички видове отчетност
Console.WriteLine("\n### ТЕСТ 4 - Шаблон за всеки вид отчетност");
foreach (var s in service.Schemas)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var p = Path.Combine(work, $"all_{s.Key}.xlsx");
    service.CreateTemplate(s, s.Reports, p);
    sw.Stop();

    var parsed = service.ReadWorkbook(p);
    Check($"{s.Title}: {s.Reports.Count} отчета -> {new FileInfo(p).Length / 1024} KB за {sw.ElapsedMilliseconds} мс",
          parsed.Schema.Key == s.Key && parsed.Sheets.Count > 0,
          $"разпозната като {parsed.Schema.Key}, {parsed.Sheets.Count} листа");
}

// =========================================== 5. Файл без служебния лист _МЕТА
Console.WriteLine("\n### ТЕСТ 5 - Файл, направен на ръка (без _МЕТА)");
{
    var manual = Path.Combine(work, "manual.xlsx");
    using (var wb = new XLWorkbook())
    {
        var cfg = wb.Worksheets.Add("CONFIG");
        cfg.Cell(1, 1).Value = "field"; cfg.Cell(1, 2).Value = "value";
        cfg.Cell(2, 1).Value = "year"; cfg.Cell(2, 2).Value = "2024";
        cfg.Cell(3, 1).Value = "eik"; cfg.Cell(3, 2).Value = "123456789";
        cfg.Cell(4, 1).Value = "type"; cfg.Cell(4, 2).Value = "REPLACE";

        var main = wb.Worksheets.Add("r_yr_generated_waste");
        main.Cell(1, 1).Value = "stagenum"; main.Cell(1, 2).Value = "origin";
        main.Cell(2, 1).Value = "7"; main.Cell(2, 2).Value = "Производство";

        var amt = wb.Worksheets.Add("r_yr_generated_waste_amt");
        amt.Cell(1, 1).Value = "stagenum"; amt.Cell(1, 2).Value = "amount_ton_gen"; amt.Cell(1, 3).Value = "коментар";
        amt.Cell(2, 1).Value = "7"; amt.Cell(2, 2).Value = "1,25"; amt.Cell(2, 3).Value = "нещо мое";
        wb.SaveAs(manual);
    }

    var parsed = service.ReadWorkbook(manual);
    var r = service.Process(parsed);
    Check("схемата се познава по имената на листовете", parsed.Schema.Key == "year");
    Check("редът със заглавия се намира на ред 1", r.TotalValidRows == 2, $"{r.TotalValidRows}");
    Check("XSD проверка минава", r.SchemaErrors.Count == 0, string.Join(" | ", r.SchemaErrors.Take(1)));
    Check("непознатата колона се съобщава", r.AllIssues.Any(i => i.Message.Contains("коментар")));
    Check("има предупреждение за липсващия _МЕТА", r.AllIssues.Any(i => i.Message.Contains("_МЕТА")));
}

// ============================ 6. Няколко вида отчети в един файл (ред по схема)
Console.WriteLine("\n### ТЕСТ 6 - Няколко вида отчети в един файл");
{
    var year = service.GetSchema("year");
    // Подаваме ги нарочно в обратен ред спрямо схемата.
    var wwtp = year.FindReport("r_yr_wwtp")!;
    var gw = year.FindReport("r_yr_generated_waste")!;
    var traders = year.FindReport("r_yr_traders")!;
    var tpl = Path.Combine(work, "t6.xlsx");
    service.CreateTemplate(year, [traders, wwtp, gw], tpl,
        new Dictionary<string, string> { ["year"] = "2024", ["eik"] = "123456789" });

    using (var wb = new XLWorkbook(tpl))
    {
        foreach (var name in new[] { "r_yr_generated_waste", "r_yr_wwtp", "r_yr_traders" })
        {
            var ws = wb.Worksheet(name);
            var cols = Headers(ws);
            var vals = new Dictionary<string, string> { ["stagenum"] = "1" };
            foreach (var c in cols.Where(c => c.Length > 0))
                if (!vals.ContainsKey(c)) vals[c] = SampleFor(year, name, c);
            Fill(wb, name, 3, vals);
        }
        wb.Save();
    }

    var r = service.Process(service.ReadWorkbook(tpl));
    foreach (var e in r.SchemaErrors) Console.WriteLine("     XSD: " + e);
    Check("XSD проверка минава при три различни отчета", r.SchemaErrors.Count == 0);

    var order = r.Xml!.Root!.Elements().Select(e => e.Name.LocalName).ToList();
    Console.WriteLine("  ред на елементите: " + string.Join(", ", order));
    var expected = new[] { "r_yr_generated_waste", "r_yr_wwtp", "r_yr_traders" };
    Check("елементите излизат в реда от схемата, а не в реда на избора",
          order.SequenceEqual(expected), string.Join(", ", order));
    Check("има по три раздела в резултата", r.Reports.Count == 3, $"{r.Reports.Count}");
}

// ==================== 7. Подтаблица с общо име не подвежда чужд отчет
// „r_b_notes“ съществува в шест от отчетните книги. Шаблон само за
// „Оползотворяване“ я записва под простото име, а програмата не бива да я
// приписва на „Образуван отпадък“ и да иска от него основен лист.
Console.WriteLine("\n### ТЕСТ 7 - Общото име „r_b_notes“ остава при своя отчет");
{
    var book = service.GetSchema("book");
    var rd = book.FindReport("r_b_recovery_dispose")!;
    var tpl = Path.Combine(work, "t7.xlsx");
    service.CreateTemplate(book, [rd], tpl, new Dictionary<string, string>
    {
        ["year"] = "2025", ["eik"] = "123456789", ["quarter"] = "3",
        ["type"] = "REPLACE", ["period_start"] = "01.07.2025", ["period_end"] = "30.09.2025"
    });

    using (var wb = new XLWorkbook(tpl))
    {
        Fill(wb, "r_b_recovery_dispose", 3, new() { ["stagenum"] = "1" });
        Fill(wb, "r_b_notes", 3, new()
        {
            ["stagenum"] = "1",
            ["book_type_code"] = "38",
            ["date_record"] = "15.07.2025",
            ["notes"] = "Бележка към площадка 1"
        });
        wb.Save();
    }

    var parsed = service.ReadWorkbook(tpl);
    var notes = parsed.Sheets.FirstOrDefault(x => x.Plan.Node.ElementName == "r_b_notes");
    Check("листът „r_b_notes“ се води към „Оползотворяване“",
          notes?.Plan.Report.ElementName == "r_b_recovery_dispose",
          notes?.Plan.Report.ElementName ?? "липсва");

    var r = service.Process(parsed);
    Check("не се отваря раздел за отчет, който го няма във файла",
          r.Reports.Count == 1, string.Join(", ", r.Reports.Select(o => o.Report.ElementName)));
    Check("няма грешка за липсващ основен лист",
          !r.AllIssues.Any(i => i.Message.Contains("липсва лист с основните данни")),
          string.Join(" | ", r.AllIssues.Select(i => i.Message)));
    Check("XSD проверка минава", r.SchemaErrors.Count == 0, string.Join(" | ", r.SchemaErrors.Take(1)));
    Check("бележката влиза в XML", r.Xml!.ToString().Contains("Бележка към площадка 1"));
}

// ================================== 8. Електронен подпис (dsig:Signature)
// НИСО проверява файла срещу непроменената схема и отхвърля неподписан файл
// още при импорта, затова подписът се полага тук, преди качването.
Console.WriteLine("\n### ТЕСТ 8 - Електронен подпис");
{
    var book = service.GetSchema("book");
    var rd = book.FindReport("r_b_recovery_dispose")!;
    var tpl = Path.Combine(work, "t8.xlsx");
    service.CreateTemplate(book, [rd], tpl, new Dictionary<string, string>
    {
        ["year"] = "2025", ["eik"] = "123456789", ["quarter"] = "3",
        ["type"] = "REPLACE", ["period_start"] = "01.07.2025", ["period_end"] = "30.09.2025"
    });

    using (var wb = new XLWorkbook(tpl))
    {
        Fill(wb, "r_b_recovery_dispose", 3, new() { ["stagenum"] = "1" });
        Fill(wb, "r_b_rd_treated", 3, new()
        {
            ["stagenum"] = "1", ["date_record"] = "15.07.2025", ["ewc"] = "150101",
            ["op_id_code"] = "R03", ["op_descr"] = "Сортиране", ["amount_ton"] = "1,5"
        });
        wb.Save();
    }

    var result = service.Process(service.ReadWorkbook(tpl));

    // Тестов сертификат — на машината за разработка няма КЕП.
    using var rsa = RSA.Create(2048);
    var request = new CertificateRequest("CN=ТЕСТ КЕП", rsa,
        HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    request.CertificateExtensions.Add(new X509KeyUsageExtension(
        X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
    var certificate = request.CreateSelfSigned(
        DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));

    // Неподписаният файл трябва да падне точно както го отхвърля и НИСО.
    var plain = Path.Combine(work, "t8_unsigned.xml");
    var plainFiles = service.Save(result, plain, tpl);
    var plainErrors = service.ValidateSignedFile(plain);
    Check("неподписан файл не минава строгата проверка",
          plainErrors.Count == 1 && plainErrors[0].Contains("Signature"),
          plainErrors.Count == 0 ? "нула грешки" : plainErrors[0][..Math.Min(60, plainErrors[0].Length)]);
    Check("неподписаният файл се отчита като неподписан", !plainFiles.Signed);

    const string xades = "http://uri.etsi.org/01903/v1.3.2#";

    foreach (var profile in new[]
             {
                 new SignatureProfile(SignatureDigest.Sha256, SignatureCanonicalization.Exclusive),
                 new SignatureProfile(SignatureDigest.Sha256, SignatureCanonicalization.Inclusive),
                 new SignatureProfile(SignatureDigest.Sha1, SignatureCanonicalization.Exclusive),
                 new SignatureProfile(SignatureDigest.Sha256, SignatureCanonicalization.Exclusive, Form: SignatureForm.Dsig),
                 new SignatureProfile(SignatureDigest.Sha256, SignatureCanonicalization.Exclusive, IncludeCertificate: false)
             })
    {
        var name = $"{profile.Form}/{profile.Digest}/{profile.Canonicalization}" +
                   (profile.IncludeCertificate ? "" : "/без KeyInfo");
        var path = Path.Combine(work,
            $"t8_{profile.Form}_{profile.Digest}_{profile.Canonicalization}_{profile.IncludeCertificate}.xml");
        var files = service.Save(result, path, tpl, certificate, profile);

        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.Load(path);

        Check($"{name}: файлът се отчита като подписан", files.Signed);
        Check($"{name}: подписът се потвърждава", XmlSigner.Verify(doc, certificate));
        Check($"{name}: подписът е последният елемент",
              doc.DocumentElement!.LastChild!.LocalName == "Signature");
        Check($"{name}: минава схемата без отстъпки",
              service.ValidateSignedFile(path).Count == 0,
              string.Join(" | ", service.ValidateSignedFile(path).Take(1)));

        // Промяна в данните трябва да обезсили подписа.
        var tampered = new XmlDocument { PreserveWhitespace = true };
        tampered.Load(path);
        ((XmlElement)tampered.GetElementsByTagName("item")[0]!).SetAttribute("amount_ton", "999");
        Check($"{name}: подправен файл се хваща", !XmlSigner.Verify(tampered, certificate));

        // --- самите ETSI свойства ---
        var properties = doc.GetElementsByTagName("SignedProperties", xades);

        if (profile.Form == SignatureForm.Dsig)
        {
            Check($"{name}: няма ETSI свойства", properties.Count == 0);
            continue;
        }

        Check($"{name}: има SignedProperties", properties.Count == 1);
        Check($"{name}: има SigningTime", doc.GetElementsByTagName("SigningTime", xades).Count == 1);
        Check($"{name}: има SigningCertificate",
              doc.GetElementsByTagName("SigningCertificate", xades).Count == 1);

        // Препратката към свойствата трябва да носи точно този Type, иначе
        // проверяващият не разпознава подписа като XAdES.
        var referenced = doc.GetElementsByTagName("Reference", SignedXml.XmlDsigNamespaceUrl)
                            .OfType<XmlElement>()
                            .FirstOrDefault(r => r.GetAttribute("Type") == "http://uri.etsi.org/01903#SignedProperties");

        Check($"{name}: втора препратка към свойствата", referenced is not null);

        var propertiesId = ((XmlElement)properties[0]!).GetAttribute("Id");
        Check($"{name}: препратката сочи към SignedProperties",
              referenced?.GetAttribute("URI") == "#" + propertiesId, referenced?.GetAttribute("URI"));

        // Target на QualifyingProperties трябва да сочи към Id-то на подписа.
        var qualifying = (XmlElement)doc.GetElementsByTagName("QualifyingProperties", xades)[0]!;
        var signatureId = ((XmlElement)doc.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl)[0]!)
                          .GetAttribute("Id");

        Check($"{name}: Target сочи към подписа",
              signatureId.Length > 0 && qualifying.GetAttribute("Target") == "#" + signatureId,
              qualifying.GetAttribute("Target"));

        // Отпечатъкът на сертификата трябва да е на този сертификат.
        var expected = Convert.ToBase64String(profile.Digest == SignatureDigest.Sha256
            ? SHA256.HashData(certificate.RawData)
            : SHA1.HashData(certificate.RawData));

        Check($"{name}: отпечатъкът е на подписващия сертификат",
              doc.GetElementsByTagName("DigestValue", SignedXml.XmlDsigNamespaceUrl)
                 .OfType<XmlElement>().Any(d => d.InnerText == expected));
    }
}

// =============================================== Право на ползване
Console.WriteLine("\n### ТЕСТ - подписано право на ползване (ES256)");
{
    using var signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var publicPem = signer.ExportSubjectPublicKeyInfoPem();

    string Token(object payload, string algorithm = "ES256", ECDsa? key = null)
    {
        string Part(object value) => Base64Url(JsonSerializer.SerializeToUtf8Bytes(value));

        var head = Part(new { alg = algorithm, typ = "JWT" });
        var body = Part(payload);
        var signature = (key ?? signer).SignData(
            Encoding.ASCII.GetBytes(head + "." + body), HashAlgorithmName.SHA256);

        return head + "." + body + "." + Base64Url(signature);
    }

    var now = DateTimeOffset.UtcNow;
    var valid = new
    {
        sub = "u-1",
        email = "test@example.com",
        plan = "monthly",
        features = new[] { "sign" },
        signatures_left = -1,
        iat = now.ToUnixTimeSeconds(),
        exp = now.AddDays(14).ToUnixTimeSeconds()
    };

    var parsed = EntitlementVerifier.Verify(Token(valid), publicPem);
    Check("валиден подпис се приема", parsed.Email == "test@example.com" && parsed.Plan == "monthly");
    Check("неограничени подписи при платен план", parsed.IsUnlimitedSignatures);
    Check("активно право", parsed.StateAt(now) == EntitlementState.Active);

    // Подправено съдържание при непокътнат подпис - класическата атака.
    var pieces = Token(valid).Split('.');
    var tampered = pieces[0] + "." +
        Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            valid.sub, valid.email, plan = "yearly", valid.features,
            signatures_left = -1, valid.iat,
            exp = now.AddYears(10).ToUnixTimeSeconds()
        })) + "." + pieces[2];

    Check("подправено съдържание се отхвърля",
        EntitlementVerifier.TryVerify(tampered, publicPem) is null);

    // Чужд ключ - все едно някой сам си издава абонамент.
    using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    Check("чужд ключ се отхвърля",
        EntitlementVerifier.TryVerify(Token(valid, key: stranger), publicPem) is null);

    Check("„alg: none“ се отхвърля",
        EntitlementVerifier.TryVerify(Token(valid, algorithm: "none"), publicPem) is null);

    Check("повреден низ се отхвърля", EntitlementVerifier.TryVerify("не-е-jwt", publicPem) is null);

    // Гратисният период: изтекло, но още работещо.
    var expired = new
    {
        sub = "u-2", email = "t2@example.com", plan = "monthly",
        features = new[] { "sign" }, signatures_left = -1,
        iat = now.AddDays(-20).ToUnixTimeSeconds(),
        exp = now.AddDays(-2).ToUnixTimeSeconds()
    };

    var grace = EntitlementVerifier.Verify(Token(expired), publicPem);
    Check("изтекло право влиза в гратисен период", grace.StateAt(now) == EntitlementState.Grace);
    Check("след гратисния период е заключено",
        grace.StateAt(now.AddDays(10)) == EntitlementState.Expired);
    Check("старо право иска подновяване", grace.ShouldRefreshAt(now));
}

// =============================================== Местна база
Console.WriteLine("\n### ТЕСТ - местна база");
{
    var dbPath = Path.Combine(work, $"workspace_{Guid.NewGuid():N}.db");

    long id;

    using (var db = Workspace.Open(dbPath))
    {
        Check("базата се създава и привежда", File.Exists(dbPath));

        id = db.Documents.Create("year", "r_yr_generated_waste", "Проба 2025",
            new Dictionary<string, string> { ["year"] = "2025", ["eik"] = "123456789" });

        db.Documents.SaveRows(id, "r_yr_generated_waste",
        [
            new() { ["stagenum"] = "1", ["origin"] = "Производство" },
            new() { ["stagenum"] = "2", ["origin"] = "Ремонт" },
            new() { ["stagenum"] = "", ["origin"] = "" }   // празен - не бива да се пази
        ]);

        var rows = db.Documents.ReadRows(id, "r_yr_generated_waste");
        Check("празните редове не се пазят", rows.Count == 2, $"{rows.Count} реда");
        Check("редовете се връщат подредени", rows[0].Values["stagenum"] == "1");
        Check("заглавните стойности се пазят", db.Documents.ReadHeader(id)["eik"] == "123456789");

        var copy = db.Documents.Duplicate(id, "Проба 2026");
        Check("копието носи редовете", db.Documents.ReadRows(copy, "r_yr_generated_waste").Count == 2);
        Check("копието е отделен запис", copy != id);

        // Произходът се пази и се чете обратно.
        var fromExcel = db.Documents.Create("year", "r_yr_generated_waste", "От файл",
            null, DocumentOrigin.Excel);

        Check("произходът по подразбиране е „в програмата“",
            db.Documents.Find(id)!.Origin == DocumentOrigin.Manual);
        Check("внесеният от Excel се помни като такъв",
            db.Documents.Find(fromExcel)!.Origin == DocumentOrigin.Excel);
        Check("копието наследява произхода",
            db.Documents.Find(db.Documents.Duplicate(fromExcel, "Копие"))!.Origin == DocumentOrigin.Excel);

        db.Documents.SetStatus(id, DocumentStatus.Signed, "C:\\изход.xml");
        var signed = db.Documents.Find(id)!;
        Check("състоянието се записва", signed.Status == DocumentStatus.Signed);
        Check("пътят до файла се пази", signed.OutputPath == "C:\\изход.xml");

        db.Settings.Set(SettingsStore.Keys.DefaultEik, "999888777");
        Check("настройка се чете обратно",
            db.Settings.Get(SettingsStore.Keys.DefaultEik) == "999888777");

        db.News.Add(new NewsItem("n1", DateTime.UtcNow, NewsSeverity.Info, "Заглавие", "Текст", false));
        db.News.Add(new NewsItem("n1", DateTime.UtcNow, NewsSeverity.Info, "Друго", "Текст", false));
        Check("съобщение не се дублира", db.News.List().Count == 1);
        Check("непрочетените се броят", db.News.UnseenCount() == 1);

        db.News.MarkSeen("n1");
        Check("прочетеното се запомня", db.News.UnseenCount() == 0);

        var backup = Path.Combine(work, "backup.db");
        db.BackupTo(backup);
        Check("резервното копие се създава", new FileInfo(backup).Length > 0);

        db.Documents.Delete(id);
        Check("изтриването маха и редовете",
            db.Documents.ReadRows(id, "r_yr_generated_waste").Count == 0);
    }

    // Второ отваряне: стъпките не бива да се изпълняват повторно.
    using (var again = Workspace.Open(dbPath))
    {
        // Остават внесеният от Excel и копието му; „Проба 2026“ и изтритият не.
        Check("повторното отваряне работи", again.Documents.List().Count == 3,
            $"{again.Documents.List().Count} записа");

        // Стъпката за произхода е приложена и на вече съществуваща база.
        Check("заварените записи получават произход по подразбиране",
            again.Documents.List().All(d => Enum.IsDefined(d.Origin)));
    }
}

// =============================================== Английски
//
// Преводите стоят на място — Lang.T("bg", "en"), — затова пропуснат превод не
// може да се случи: двойката текст е самият израз. Това, което ПАК може да се
// счупи, е нещо друго и по-коварно: текст, прочетен веднъж в статично поле.
// Полето се пълни при първото обръщение, с езика, който е бил в сила тогава, и
// след смяна остава на стария. Тук се проверява точно това.
Console.WriteLine("\n### ТЕСТ - английски");
{
    Check("подразбиращият се език е българският", Lang.Current == Language.Bulgarian);
    Check("ключът на подразбиращия се език е bg", Lang.Key == "bg");
    Check("предлагат се два езика", Lang.All.Length == 2, $"{Lang.All.Length}");
    Check("всеки език се изписва на себе си",
        Lang.All.Any(l => l.Name == "English") && Lang.All.Any(l => l.Name == "Български"));

    Check("на български се връща българското", Lang.T("да", "yes") == "да");

    Lang.Use("en");
    Check("езикът се сменя", Lang.IsEnglish && Lang.Key == "en");
    Check("на английски се връща английското", Lang.T("да", "yes") == "yes");

    // Статичните полета: имената на темите и списъкът с клавиши се четат наново.
    Check("имената на темите следват езика",
        Palette.All.Any(p => p.Name == "Dark") && Palette.All.All(p => !p.Name.Any(IsCyrillic)),
        string.Join(", ", Palette.All.Select(p => p.Name)));

    var groups = ShortcutsDialog.Shortcuts.Select(sc => sc.Group).Distinct().ToList();

    Check("клавишните комбинации следват езика",
        groups.Count == 5 && groups.All(g => !g.Any(IsCyrillic)),
        string.Join(" | ", groups));

    // Съобщенията от долните слоеве също.
    Check("причините за фирма следват езика",
        !CompanyStore.Explain(CompanyProblem.EmptyEik, 3).Any(IsCyrillic),
        CompanyStore.Explain(CompanyProblem.EmptyEik, 3));

    Check("състоянията на отчет следват езика",
        new DocumentSummary(1, "year", "r", "t", DocumentStatus.Signed, DocumentOrigin.Excel,
            null, DateTime.UtcNow, null) is { StatusLabel: "Signed", OriginLabel: "from Excel" });

    // Имената на отчетите и на полетата НЕ следват езика: те идват от XSD
    // файловете на НИСО и назовават нормативни формуляри. Преведено име не
    // отговаря на нищо, което човек може да намери в наредбата.
    var year = service.GetSchema("year");
    var generated = year.FindReport("r_yr_generated_waste")!;

    Check("името на отчета остава на български",
        ReportTitles.Short(generated).Any(IsCyrillic), ReportTitles.Short(generated));

    var column = generated.SelfAndDescendants().SelectMany(n => n.Attributes)
        .First(a => a.Name == "stagenum");

    Check("името на полето остава на български",
        ReportTitles.ColumnShort(column).Any(IsCyrillic), ReportTitles.ColumnShort(column));

    // Рамката на съобщението се превежда, а името на полето в него — не.
    var missing = ValueConverter.Convert(CellValue.Empty, column);

    Check("рамката на съобщението е на английски",
        missing.Error is not null && missing.Error.Contains("missing required value"),
        missing.Error);
    Check("полето в съобщението остава на български",
        missing.Error is not null && missing.Error.Any(IsCyrillic), missing.Error);

    // Всеки прозорец се сглобява и на двата езика.
    var built = 0;
    string? englishError = null;

    var englishThread = new Thread(() =>
    {
        try
        {
            var uiPath = Path.Combine(work, $"en_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(uiPath);

            var docId = db.Documents.Create("year", "r_yr_generated_waste", "Sample",
                new Dictionary<string, string> { ["year"] = "2025" });

            foreach (var key in new[] { "en", "bg" })
            {
                Lang.Use(key);

                Control[] windows =
                [
                    new ShellForm(service, db),
                    new EditorForm(service, db, docId),
                    new NewReportDialog(service),
                    new ShortcutsDialog(),
                    new CompanyDialog(db.Companies, 3)
                ];

                foreach (var window in windows)
                    using (window) { _ = window.Handle; built++; }
            }
        }
        catch (Exception ex)
        {
            englishError = ex.ToString();
        }
    });

    englishThread.SetApartmentState(ApartmentState.STA);
    englishThread.Start();
    englishThread.Join();

    if (englishError is not null) Console.WriteLine("     " + englishError);

    Check("прозорците се сглобяват и на двата езика", englishError is null);
    Check("сглобени са 10 прозореца", built == 10, $"{built}");

    Lang.Use("bg");
    Check("езикът се връща обратно", !Lang.IsEnglish);
}

// =============================================== Числата на таблото
//
// Таблото брои наум това, което човек иначе брои на ръка. Тук се проверява
// самото броене — без прозорци: разбивка, която брои погрешно, изглежда точно
// като вярна.
Console.WriteLine("\n### ТЕСТ - числата на таблото");
{
    var statsPath = Path.Combine(work, $"stats_{Guid.NewGuid():N}.db");
    using var db = Workspace.Open(statsPath);

    var alpha = db.Companies.Add("123456789", "Алфа ЕООД");
    var beta = db.Companies.Add("987654321", "Бета АД");

    // Три на едната фирма, една на другата и една без фирма.
    var d1 = db.Documents.Create("year", "r_yr_generated_waste", "Алфа 1", null, DocumentOrigin.Manual, alpha);
    var d2 = db.Documents.Create("year", "r_yr_generated_waste", "Алфа 2", null, DocumentOrigin.Manual, alpha);
    var d3 = db.Documents.Create("year", "r_yr_generated_waste", "Алфа 3", null, DocumentOrigin.Manual, alpha);
    var d4 = db.Documents.Create("book", "r_b_generated_waste", "Бета 1", null, DocumentOrigin.Manual, beta);
    var d5 = db.Documents.Create("book", "r_b_generated_waste", "Ничий", null, DocumentOrigin.Manual);

    db.Documents.SetStatus(d1, DocumentStatus.Signed);
    db.Documents.SetStatus(d2, DocumentStatus.Checked);

    var documents = db.Documents.List();
    var companies = db.Companies.List();

    string Name(DocumentSummary d) => service.Schemas
        .FirstOrDefault(sch => sch.Key == d.SchemaKey)?
        .FindReport(d.Report) is { } node ? ReportTitles.Short(node) : string.Empty;

    var now = DateTime.Now;
    var stats = DashboardStats.Build(documents, companies, Name, now);

    Check("общият брой е верен", stats.Total == 5, $"{stats.Total}");
    Check("състоянията се броят поотделно",
        stats.Signed == 1 && stats.Checked == 1 && stats.Draft == 3,
        $"подписани {stats.Signed}, проверени {stats.Checked}, чернови {stats.Draft}");
    Check("делът подписани е закръглен", stats.SignedPercent == 20, $"{stats.SignedPercent}%");

    Check("разбивката по фирми подрежда по брой",
        stats.ByCompany.Count == 3 && stats.ByCompany[0].Label == "Алфа ЕООД" && stats.ByCompany[0].Value == 3,
        string.Join(", ", stats.ByCompany.Select(x => $"{x.Label}={x.Value}")));

    Check("отчет без фирма също се брои",
        stats.ByCompany.Any(x => x.Label == "Без фирма" && x.Value == 1),
        string.Join(", ", stats.ByCompany.Select(x => $"{x.Label}={x.Value}")));

    Check("сборът на разбивката по фирми е броят отчети",
        stats.ByCompany.Sum(x => x.Value) == documents.Count,
        $"{stats.ByCompany.Sum(x => x.Value)}");

    Check("видовете отчет са два и с човешки имена",
        stats.ByReport.Count == 2 && stats.ByReport.All(x => x.Label.Any(IsCyrillic)),
        string.Join(" | ", stats.ByReport.Select(x => $"{x.Label}={x.Value}")));

    Check("месеците са шест и последният е текущият",
        stats.ByMonth.Count == 6 && stats.ByMonth[^1].Label == now.ToString("MM.yyyy"),
        string.Join(", ", stats.ByMonth.Select(x => x.Label)));

    Check("току-що пипаното е в текущия месец",
        stats.ByMonth[^1].Value == 5 && stats.ByMonth.Take(5).All(x => x.Value == 0),
        string.Join(", ", stats.ByMonth.Select(x => $"{x.Label}={x.Value}")));

    Check("прясното не чака внимание", stats.Idle.Count == 0, $"{stats.Idle.Count}");

    // Едно и също броене, но погледнато отдалеч: така се вижда какво излиза
    // като изостанало и че подписаното не се брои за такова.
    var later = now.AddDays(30);
    var aged = DashboardStats.Build(documents, companies, Name, later);

    Check("изостаналите са само недовършените",
        aged.Idle.Count == 4 && aged.Idle.All(x => x.Document.Status != DocumentStatus.Signed),
        string.Join(", ", aged.Idle.Select(x => $"{x.Document.Title}:{x.Days}")));

    Check("дните се броят от последното пипане",
        aged.Idle.All(x => x.Days == 30), string.Join(", ", aged.Idle.Select(x => x.Days)));

    Check("празната база дава празно табло",
        DashboardStats.Build([], [], Name, now) is { Total: 0, ByCompany.Count: 0, ByMonth.Count: 6 });

    _ = (d3, d4, d5);
}

// =============================================== Смяна в движение
//
// Смяната на език пресъздава страниците, но лентата отляво живее през цялото
// време. Първата версия търсеше заварения бутон ПО НАДПИС — на новия език
// надписът вече е друг, бутонът не се разпознаваше и до стария се долепяше
// втори комплект. Осем бутона на два езика.
//
// Затова тестът брои бутоните, а не само чете надписите им.
Console.WriteLine("\n### ТЕСТ - смяна на езика при отворен прозорец");
{
    var seen = new List<string>();
    var labels = new List<List<string>>();
    var brand = new List<string>();
    string? switchError = null;

    var switchThread = new Thread(() =>
    {
        try
        {
            var uiPath = Path.Combine(work, $"switch_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(uiPath);

            Lang.Use("bg");

            using var shell = new ShellForm(service, db);
            _ = shell.Handle;

            // Български, английски и обратно: пропуск при връщането не личи,
            // ако се провери само едната посока.
            foreach (var key in new[] { "bg", "en", "bg" })
            {
                Lang.Use(key);

                // Пресъздаването е отложено (BeginInvoke), защото събитието
                // идва от бутон, който сам е част от екрана за премахване.
                // Без цикъл на съобщения то не би се изпълнило.
                Application.DoEvents();

                seen.Add(key);
                labels.Add(shell.NavLabels());
                brand.Add(shell.BrandLine);
            }
        }
        catch (Exception ex)
        {
            switchError = ex.ToString();
        }
    });

    switchThread.SetApartmentState(ApartmentState.STA);
    switchThread.Start();
    switchThread.Join();

    if (switchError is not null) Console.WriteLine("     " + switchError);

    Check("смяната минава без изключение", switchError is null);
    Check("минато е и в двете посоки", seen.Count == 3, string.Join(" → ", seen));

    Check("лентата пази точно осем бутона",
        labels.Count == 3 && labels.All(l => l.Count == 8),
        string.Join(" | ", labels.Select(l => l.Count.ToString())));

    Check("бутоните не се дублират",
        labels.All(l => l.Distinct().Count() == l.Count),
        string.Join(" | ", labels.Select(l => string.Join(", ", l))));

    Check("лентата минава на английски",
        labels.Count == 3 && labels[1].Contains("Home") && labels[1].Contains("Month")
        && labels[1].Contains("Settings"),
        labels.Count == 3 ? string.Join(", ", labels[1]) : "-");

    Check("лентата се връща на български",
        labels.Count == 3 && labels[2].Contains("Начало") && labels[2].Contains("Месец")
        && labels[2].Contains("Настройки"),
        labels.Count == 3 ? string.Join(", ", labels[2]) : "-");

    Check("редът под името също следва езика",
        brand.Count == 3 && brand[0] == "Отчети към НИСО"
        && brand[1] == "NWIS reports" && brand[2] == "Отчети към НИСО",
        string.Join(" | ", brand));

    Lang.Use("bg");
}

// =============================================== Езикът на съобщенията
//
// Съобщението при проверка сочи място, което човек трябва да намери на екрана.
// „лист „r_b_rd_received“, ред 1: липсва задължителна стойност за „stagenum““
// е вярно и напълно безполезно: нито разделът, нито колоната се казват така
// никъде пред очите му.
Console.WriteLine("\n### ТЕСТ - съобщенията говорят на езика на екрана");
{
    var year = service.GetSchema("year");
    var report = year.FindReport("r_yr_generated_waste")!;
    var tpl = Path.Combine(work, "names.xlsx");

    service.CreateTemplate(year, [report], tpl,
        new Dictionary<string, string> { ["year"] = "2024", ["eik"] = "123456789" });

    using (var wb = new XLWorkbook(tpl))
    {
        // Ред без задължителните стойности, ред с невалидно число и ред с
        // непозната изборна стойност — трите различни пътя за съобщение.
        Fill(wb, "r_yr_generated_waste", 3, new() { ["origin"] = "Производство" });
        Fill(wb, "r_yr_generated_waste_amt", 3, new() { ["stagenum"] = "7", ["amount_ton_gen"] = "не-число" });
        Fill(wb, "r_yr_generated_waste_hand", 3, new() { ["stagenum"] = "7", ["type_receiver_code"] = "НЯМА ТАКОВА" });
        wb.Save();
    }

    var result = service.Process(service.ReadWorkbook(tpl));
    var issues = result.AllIssues.Where(i => i.Severity == IssueSeverity.Error).ToList();

    Check("има съобщения за проверка", issues.Count > 0, $"{issues.Count} съобщения");

    var layout = WorkbookLayout.Build(year, [report]);

    // Само тези, за които изобщо има български надпис. Схемата оставя част от
    // полетата без описание и там суровото име е най-доброто, което има.
    var namedColumns = layout.Sheets
        .SelectMany(sheet => sheet.Columns)
        .Where(column => ReportTitles.ColumnShort(column) != column.Name)
        .Select(column => column.Name)
        .Distinct(StringComparer.Ordinal)
        .ToList();

    var namedSheets = layout.Sheets
        .Where(sheet => ReportTitles.Table(sheet.Node) != sheet.Node.ElementName)
        .Select(sheet => sheet.Node.ElementName)
        .Distinct(StringComparer.Ordinal)
        .ToList();

    Check("има преведени колони, срещу които да се сверява", namedColumns.Count > 0, $"{namedColumns.Count}");
    Check("има преведени таблици, срещу които да се сверява", namedSheets.Count > 0, $"{namedSheets.Count}");

    var leaked = new List<string>();

    foreach (var issue in issues)
    {
        foreach (var name in namedColumns)
            if (issue.Message.Contains(name, StringComparison.Ordinal))
                leaked.Add($"„{name}“ в съобщението: {issue.Message}");

        // Точно име, не подниз: „r_yr_generated_waste“ е част от
        // „r_yr_generated_waste_amt“, а второто е друга таблица — и то без
        // описание в схемата, тоест суровото име е всичко, което имаме.
        if (namedSheets.Contains(issue.Where, StringComparer.Ordinal))
            leaked.Add($"„{issue.Where}“ в мястото: {issue.Location}");
    }

    foreach (var l in leaked.Distinct().Take(6)) Console.WriteLine("     " + l);

    Check("нито едно име от схемата не стига до екрана", leaked.Count == 0,
        $"{leaked.Count} пропуска");

    // И обратното — това е същинската проверка: мястото в съобщението трябва
    // да съвпада с надписа на раздел, който наистина стои на екрана. Иначе
    // съобщението праща човека да търси нещо, което го няма.
    var located = issues.Where(i => i.ExcelRow is not null).ToList();
    var tabs = layout.Sheets.Select(sheet => ReportTitles.Table(sheet.Node)).ToHashSet(StringComparer.Ordinal);

    Check("има съобщения, вързани за ред", located.Count > 0, $"{located.Count}");
    Check("мястото сочи раздел, който го има на екрана",
        located.All(i => tabs.Contains(i.Where)),
        string.Join(" | ", located.Where(i => !tabs.Contains(i.Where)).Select(i => i.Where).Take(3)));
    Check("мястото сочи и реда", located.All(i => i.Location.Contains(", ред ", StringComparison.Ordinal)));

    // Отчетът за проверката обаче пази името на листа: той се чете, когато
    // нещо трябва да се разнищи, и тогава точното име помага.
    var log = result.BuildLog(tpl);

    Check("отчетът за проверката пази името на листа",
        log.Contains("r_yr_generated_waste_amt", StringComparison.Ordinal));
    Check("отчетът за проверката носи и българския надпис",
        located.Any(i => i.ToString().Contains('(', StringComparison.Ordinal)
                         && i.ToString().Contains(i.Where, StringComparison.Ordinal)));

    // Колко подтаблици още нямат български надпис. Схемата на НИСО описва
    // самите отчети, но не и подтаблиците в тях — а измислен надпис в
    // програма за подаване на отчети е по-лош от суровото име.
    //
    // Числото е храповик: стои тук, за да се вижда, и може само да намалява.
    // Ако НИСО опише още, стойността се сваля. Ако някой добави таблица без
    // надпис, тестът се обажда.
    var everySheet = service.Schemas
        .SelectMany(schema => schema.Reports.SelectMany(r => WorkbookLayout.Build(schema, [r]).Sheets))
        .Select(sheet => sheet.Node)
        .DistinctBy(node => node.ElementName)
        .ToList();

    var unnamedTables = everySheet
        .Where(node => ReportTitles.Table(node) == node.ElementName)
        .Select(node => node.ElementName)
        .OrderBy(n => n, StringComparer.Ordinal)
        .ToList();

    const int unnamedTableCeiling = 117;

    Console.WriteLine($"     подтаблици без български надпис ({unnamedTables.Count}): " +
        string.Join(", ", unnamedTables.Take(6)) + (unnamedTables.Count > 6 ? " …" : ""));

    Check("прегледани са таблиците на всички отчети", everySheet.Count > 100, $"{everySheet.Count}");
    Check("непреведените таблици не се увеличават", unnamedTables.Count <= unnamedTableCeiling,
        $"{unnamedTables.Count} при таван {unnamedTableCeiling}");
}

// =============================================== Фирми
//
// Таванът на фирмите е това, което се продава, затова се проверява отвсякъде:
// какво минава, какво не минава и какво става със заварена база.
Console.WriteLine("\n### ТЕСТ - фирми");
{
    var dbPath = Path.Combine(work, $"companies_{Guid.NewGuid():N}.db");

    using (var db = Workspace.Open(dbPath))
    {
        Check("новата база започва без фирми", db.Companies.List().Count == 0);

        // (ЕИК, име, очаквана причина). Броят на случаите се сверява накрая —
        // иначе изпразнена таблица би минала за успешен тест.
        (string Eik, string Name, CompanyProblem Expected)[] cases =
        [
            ("",              "Фирма", CompanyProblem.EmptyEik),
            ("   ",           "Фирма", CompanyProblem.EmptyEik),
            ("12345678",      "Фирма", CompanyProblem.EikNotNineOrThirteen),
            ("1234567890",    "Фирма", CompanyProblem.EikNotNineOrThirteen),
            ("123456789012",  "Фирма", CompanyProblem.EikNotNineOrThirteen),
            ("12345678A",     "Фирма", CompanyProblem.EikNotDigits),
            ("123 456 789",   "Фирма", CompanyProblem.EikNotDigits),
            ("123456789",     "",      CompanyProblem.EmptyName),
            ("123456789",     "   ",   CompanyProblem.EmptyName),
            ("123456789",     "Фирма", CompanyProblem.None),
            ("1234567890123", "Клон",  CompanyProblem.None)
        ];

        var triedCompanies = 0;
        var wrongCompanies = new List<string>();

        foreach (var (eik, name, expected) in cases)
        {
            triedCompanies++;
            var actual = db.Companies.Validate(eik, name, 3);
            if (actual != expected) wrongCompanies.Add($"„{eik}“ дава {actual}, а не {expected}");
        }

        Check("проверката на ЕИК минава през всички случаи", triedCompanies == cases.Length, $"{triedCompanies}");
        Check("проверката на ЕИК отсъжда правилно", wrongCompanies.Count == 0,
            string.Join(" | ", wrongCompanies.Take(3)));

        // Излишните разстояния се махат, а не се отхвърлят.
        var first = db.Companies.Add("  123456789  ", "  Първа ЕООД  ");
        Check("ЕИК се записва без разстояния", db.Companies.Find(first)!.Eik == "123456789");
        Check("името се записва без разстояния", db.Companies.Find(first)!.Name == "Първа ЕООД");
        Check("надписът в списъка съдържа и двете",
            db.Companies.Find(first)!.Display == "Първа ЕООД — 123456789");

        Check("същият ЕИК втори път не минава",
            db.Companies.Validate("123456789", "Друго име", 3) == CompanyProblem.AlreadyExists);
        Check("своят собствен ЕИК при поправка минава",
            db.Companies.Validate("123456789", "Първа ООД", 3, first) == CompanyProblem.None);

        db.Companies.Update(first, "123456789", "Първа ООД");
        Check("поправката се записва", db.Companies.Find(first)!.Name == "Първа ООД");

        db.Companies.Add("222222222", "Втора ЕООД");
        db.Companies.Add("333333333", "Трета ЕООД");

        Check("трите фирми се четат обратно", db.Companies.Count() == 3);
        Check("четвъртата опира в тавана",
            db.Companies.Validate("444444444", "Четвърта", 3) == CompanyProblem.LimitReached);
        Check("таванът не пречи на поправка на заварена фирма",
            db.Companies.Validate("123456789", "Първа ООД", 3, first) == CompanyProblem.None);
        Check("по-висок абонамент пуска четвъртата",
            db.Companies.Validate("444444444", "Четвърта", 10) == CompanyProblem.None);

        // Причината винаги се обяснява с изречение — празен низ би оставил
        // прозореца с бутон, който не работи, и без нито дума защо.
        var unexplained = Enum.GetValues<CompanyProblem>()
            .Where(problem => problem != CompanyProblem.None)
            .Where(problem => string.IsNullOrWhiteSpace(CompanyStore.Explain(problem, 3)))
            .ToList();

        Check("всяка причина си има обяснение", unexplained.Count == 0,
            string.Join(", ", unexplained));
        Check("обяснението за тавана казва числото",
            CompanyStore.Explain(CompanyProblem.LimitReached, 3).Contains('3'));

        // Отчетите преживяват премахването на фирмата.
        var second = db.Companies.List().First(c => c.Eik == "222222222");

        var doc = db.Documents.Create("year", "r_yr_generated_waste", "Отчет на Втора",
            new Dictionary<string, string> { ["year"] = "2025" },
            DocumentOrigin.Manual, second.Id);

        Check("отчетът помни фирмата си", db.Documents.Find(doc)!.CompanyId == second.Id);
        Check("фирмата брои отчетите си", db.Companies.DocumentCount(second.Id) == 1);
        Check("другата фирма не ги брои", db.Companies.DocumentCount(first) == 0);

        db.Companies.Remove(second.Id);

        Check("премахнатата фирма изчезва", db.Companies.Find(second.Id) is null);
        Check("отчетът ѝ остава", db.Documents.Find(doc) is not null);
        Check("отчетът ѝ остава без фирма", db.Documents.Find(doc)!.CompanyId is null);

        db.Documents.SetCompany(doc, first);
        Check("отчетът се прехвърля на друга фирма", db.Documents.Find(doc)!.CompanyId == first);
    }
}

// =============================================== ЕИК от внесен файл
//
// Файлът идва отвън. Приема се само ЕИК на заведена тук фирма; всеки друг се
// маха и отчетът влиза без фирма. Мълчаливо приетият чужд ЕИК се вижда чак
// когато НИСО върне вече подадения отчет.
Console.WriteLine("\n### ТЕСТ - внесеният ЕИК се сверява със заведените фирми");
{
    var dbPath = Path.Combine(work, $"import_{Guid.NewGuid():N}.db");

    using var db = Workspace.Open(dbPath);

    var known = db.Companies.Add("123456789", "Позната ЕООД");

    // (какво пише във файла, разпознава ли се, остава ли ЕИК-ът, вика ли се човекът)
    (string? InFile, bool Recognised, bool Kept, bool Warns)[] cases =
    [
        ("123456789",     true,  true,  false),   // точно съвпадение
        ("  123456789  ", true,  true,  false),   // разстояния от Excel
        ("999999999",     false, false, true),    // чужда фирма
        ("12345678",      false, false, true),    // сгрешен ЕИК
        ("1234567890123", false, false, true),    // клон, който не е заведен
        ("",              false, false, false),   // празно поле - няма за какво да се вика
        (null,            false, false, false)    // липсваща клетка
    ];

    var triedImports = 0;
    var wrongImports = new List<string>();

    foreach (var (inFile, recognised, kept, warns) in cases)
    {
        triedImports++;

        var header = new Dictionary<string, string>(StringComparer.Ordinal) { ["year"] = "2025" };
        if (inFile is not null) header["eik"] = inFile;

        var result = db.Companies.MatchImportedEik(header);

        if ((result.Company is not null) != recognised)
            wrongImports.Add($"„{inFile ?? "-"}“: разпознаване {result.Company is not null}, а не {recognised}");

        if (header.ContainsKey("eik") != kept)
            wrongImports.Add($"„{inFile ?? "-"}“: ЕИК остава {header.ContainsKey("eik")}, а не {kept}");

        if (result.Cleared != warns)
            wrongImports.Add($"„{inFile ?? "-"}“: предупреждение {result.Cleared}, а не {warns}");
    }

    Check("сверяването минава през всички случаи", triedImports == cases.Length, $"{triedImports}");
    Check("познатият ЕИК се приема, непознатият се маха", wrongImports.Count == 0,
        string.Join(" | ", wrongImports.Take(3)));

    // Разпознатият ЕИК носи и самата фирма, за да се закачи отчетът за нея.
    var good = new Dictionary<string, string>(StringComparer.Ordinal) { ["eik"] = "123456789" };
    Check("разпознатата фирма се връща", db.Companies.MatchImportedEik(good).Company?.Id == known);

    // Останалата заглавна част не се пипа: маха се само ЕИК-ът.
    var mixed = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["eik"] = "999999999",
        ["year"] = "2025",
        ["name"] = "Някой"
    };

    var cleared = db.Companies.MatchImportedEik(mixed);

    Check("чуждият ЕИК изчезва от заглавната част", !mixed.ContainsKey("eik"));
    Check("останалите заглавни стойности остават", mixed.Count == 2 && mixed["year"] == "2025");
    Check("файловият ЕИК се помни за съобщението", cleared.FileEik == "999999999");

    // Отчетът наистина влиза - без фирма, но с редовете си.
    var doc = db.Documents.Create("year", "r_yr_generated_waste", "Внесен без фирма",
        mixed, DocumentOrigin.Excel, cleared.Company?.Id);

    db.Documents.SaveRows(doc, "r_yr_generated_waste",
        [new() { ["stagenum"] = "1", ["origin"] = "Производство" }]);

    Check("внасянето не се спира заради ЕИК-а", db.Documents.Find(doc) is not null);
    Check("внесеният отчет е без фирма", db.Documents.Find(doc)!.CompanyId is null);
    Check("редовете му се пазят", db.Documents.ReadRows(doc, "r_yr_generated_waste").Count == 1);
    Check("ЕИК не влиза в заглавната част на записа",
        !db.Documents.ReadHeader(doc).ContainsKey("eik"));

    // Без нито една заведена фирма всеки ЕИК е непознат - това е нарочно:
    // фирмата се избира от списък, иначе чужд ЕИК би влязъл без питане.
    var emptyPath = Path.Combine(work, $"import_empty_{Guid.NewGuid():N}.db");
    using var fresh = Workspace.Open(emptyPath);

    var lonely = new Dictionary<string, string>(StringComparer.Ordinal) { ["eik"] = "123456789" };

    Check("без заведени фирми ЕИК-ът пак се маха",
        fresh.Companies.MatchImportedEik(lonely) is { Company: null, Cleared: true }
        && !lonely.ContainsKey("eik"));
}

// =============================================== Заварена база с един ЕИК
//
// Стъпките са само за добавяне: база от предишно издание трябва да се отвори,
// а единственият ЕИК в настройките да стане първата фирма. Тук базата се
// връща назад на ръка до предишното си състояние и после се отваря наново —
// проверява се самата стъпка, не паметта на тази програма.
Console.WriteLine("\n### ТЕСТ - заварена база получава фирма от настройките");
{
    var dbPath = Path.Combine(work, $"upgrade_{Guid.NewGuid():N}.db");
    long oldDoc;

    using (var db = Workspace.Open(dbPath))
    {
        oldDoc = db.Documents.Create("year", "r_yr_generated_waste", "Стар отчет",
            new Dictionary<string, string> { ["year"] = "2024" });

        db.Settings.Set(SettingsStore.Keys.DefaultEik, "987654321");
        db.Settings.Set("default.company", "Заварена ЕООД");
    }

    // Връщане до състоянието отпреди стъпката за фирмите.
    using (var raw = new SqliteConnection($"Data Source={dbPath}"))
    {
        raw.Open();
        using var back = raw.CreateCommand();
        // Всичко, направено СЛЕД стъпка 3, си отива — иначе следващото
        // отваряне се спъва в таблица, която стъпката ще създаде наново.
        //
        // Списъкът расте с всяка нова стъпка, която създава таблица. Редът е по
        // зависимост: който сочи, пада преди този, към когото сочи.
        // Стъпка 20 добавя колони към таблица от стъпка 1 — те се махат, а не
        // таблицата, иначе привеждането ги добавя втори път и пада.
        back.CommandText = """
            ALTER TABLE news DROP COLUMN hidden_utc;
            ALTER TABLE news DROP COLUMN link;
            DROP TABLE action_windows;
            DROP TABLE partners;
            DROP TABLE action_log;
            DROP TABLE import_items;
            DROP TABLE imports;
            DROP TABLE ledger_links;
            DROP TABLE ledger_periods;
            DROP TABLE ledger_moves;
            DROP TABLE batches;
            DROP TABLE value_lists;
            DROP TABLE opening_balances;
            DROP TABLE companies;
            ALTER TABLE documents DROP COLUMN company_id;
            PRAGMA user_version = 3;
            """;
        back.ExecuteNonQuery();
    }

    using (var db = Workspace.Open(dbPath))
    {
        var all = db.Companies.List();

        Check("заварената база се отваря", db.Documents.Find(oldDoc) is not null);
        Check("ЕИК-ът от настройките става фирма", all.Count == 1, $"{all.Count} фирми");
        Check("ЕИК-ът се пренася", all.Count == 1 && all[0].Eik == "987654321",
            all.Count == 1 ? all[0].Eik : "—");
        Check("името се пренася", all.Count == 1 && all[0].Name == "Заварена ЕООД",
            all.Count == 1 ? all[0].Name : "—");
        Check("заварените отчети остават без фирма", db.Documents.Find(oldDoc)!.CompanyId is null);
    }

    // Трето отваряне: стъпката не бива да се изпълни повторно и да удвои фирмата.
    using (var db = Workspace.Open(dbPath))
        Check("стъпката не се повтаря при следващо отваряне", db.Companies.Count() == 1);
}

// =============================================== Сглобяване на интерфейса
//
// Windows Forms вдига част от събитията си още докато конструкторът тече —
// присвояване на Height например. Ако тогава някое поле още не съществува,
// програмата пада при отваряне на прозореца, а компилаторът мълчи.
//
// Затова тук всяка контрола просто се сглобява. Не се показва нищо; проверява
// се само че конструкторът минава. Точно това не хваща нито проверката при
// компилиране, нито снимка на екрана.
Console.WriteLine("\n### ТЕСТ - сглобяване на контролите");
{
    var uiPath = Path.Combine(work, $"ui_{Guid.NewGuid():N}.db");
    var failures = new List<string>();
    var built = new List<string>();
    string? threadError = null;

    // Броят се и успехите. Само „няма провали“ не стига: ако нишката падне,
    // преди да е стигнала до проверките, списъкът с провали остава празен и
    // тестът минава, без да е проверил нищо. Точно това се случи първия път.
    const int expected = 28;

    void Build(string name, Func<Control> make)
    {
        try
        {
            using var control = make();
            _ = control.Handle;   // принуждава създаването на прозореца
            built.Add(name);
        }
        catch (Exception ex)
        {
            failures.Add($"{name}: {ex.GetType().Name} — {ex.Message}");
        }
    }

    var thread = new Thread(() =>
    {
      try
      {
        using var db = Workspace.Open(uiPath);

        Build("AppButton", () => new AppButton("Провери", ButtonVariant.Primary) { Glyph = Theme.Glyph.Check });
        Build("AppButton.FitWidth", () => new AppButton("Внеси от Excel", ButtonVariant.Ghost)
        { Glyph = Theme.Glyph.Import }.FitWidth());
        Build("Card", () => new Card());
        Build("ClickableCard", () => new ClickableCard());

        // Известието се мери само в конструктора — с текст, копче и кръстче.
        Build("NoticeCard", () =>
        {
            var notice = new NoticeCard("Налично е издание 1.3.1", "сега: 1.3.0", "• По-бързо пускане") { Width = 600 };
            notice.AddAction("Обнови сега", Theme.Glyph.Download, ButtonVariant.Primary, () => { });
            notice.AllowDismiss("Скрий", new ToolTip(), () => { });
            notice.Progress = 40;
            return notice;
        });
        Build("SearchBox", () => new SearchBox("търсене"));
        Build("SegmentedControl", () => { var s = new SegmentedControl(); s.SetItems(["А", "Б", "В"]); return s; });
        Build("NavButton", () => new NavButton("Начало", Theme.Glyph.Home));
        Build("ReportList", () => new ReportList());
        Build("TabStrip", () => new TabStrip());
        Build("DocumentList", () => new DocumentList());
        Build("CompanyList", () => new CompanyList());
        Build("NewReportDialog", () => new NewReportDialog(service));
        Build("ShortcutsDialog", () => new ShortcutsDialog());
        Build("ShellForm", () => new ShellForm(service, db));

        // Балансът чете базата още в конструктора. С ФИРМА, а не на празна
        // база: списъкът с отметки вдига събитие при всяко слагане на отметка,
        // а тогава прозорец още няма. На празна база списъкът остава празен и
        // точно това не личи.
        // Прозорецът с началните количества пълни справочна колона още при
        // сглобяването си.
        Build("OpeningsDialog", () => new OpeningsDialog(
            [new Opening(1, 2026, "160604", 5m)],
            [new Company(1, "111111111", "Алфа ЕООД")],
            new Dictionary<string, string> { ["160604"] = "Алкални батерии" },
            (_, _) => null));

        // Дневникът на внесеното чете базата и пълни таблицата в конструктора си.
        Build("ImportsDialog", () => new ImportsDialog(db));

        // Затворените месеци също четат базата при сглобяване.
        Build("PeriodsDialog", () => new PeriodsDialog(db, new CompanyScope(db)));

        // Частичното взимане пълни таблицата си в конструктора — и на двата вида.
        Build("PartialDialog", () => new PartialDialog(
            [new StockShare("150101", new Batch(1, "2026-0001", "204455667", "Скрап ЕООД",
                "150101", new DateOnly(2026, 9, 3)), 10m)],
            new Dictionary<long, decimal>()));

        Build("PartialDialog (предаване)", () => new PartialDialog(
            [new StockShare("150101", new Batch(1, "2026-0001", "204455667", "Скрап ЕООД",
                "150101", new DateOnly(2026, 9, 3)), 10m)],
            new Dictionary<long, decimal>(), MoveKind.Submitted));

        // Целият списък партиди пълни таблицата си в конструктора.
        Build("BatchListDialog", () => new BatchListDialog("Зад 150101", "Ще се вземе, т",
            [new StockShare("150101", new Batch(1, "2026-0001", "204455667", "Скрап ЕООД",
                "150101", new DateOnly(2026, 9, 3)), 10m)],
            new Dictionary<long, decimal> { [1] = 4m }));

        // Прозорецът с периодите смята покритието още в конструктора.
        Build("BalanceReportsDialog", () => new BalanceReportsDialog(
        [
            new ReportPeriod(1, "Проба", "Алфа", "Проверен", false,
                new DateTime(2026, 1, 1), new DateTime(2026, 6, 30), null, null, 3)
        ], 2026, 1, 12));

        Build("BalancePage", () =>
        {
            if (db.Companies.Count() == 0) db.Companies.Add("111111111", "Алфа ЕООД");

            return new BalancePage(service, db);
        });

        // Списъците се сглобяват и празни, и с един запис: празният екран е
        // отделен изглед и се показва точно когато няма нито един ред.
        Build("ListsPage", () =>
        {
            if (db.Companies.Count() == 0) db.Companies.Add("111111111", "Алфа ЕООД");

            db.Lists.Save(db.Companies.List()[0].Id, "book", string.Empty, string.Empty, "ewc", ["150101"]);

            return new ListsPage(service, db);
        });

        // Прозорчето за списък пълни четири менюта в конструктора си и всяко
        // от тях вдига SelectedIndexChanged, докато следващото още го няма.
        Build("ValueListDialog", () => new ValueListDialog(service, db.Companies.List()));

        // Прозорецът за фирма прави проверка още в конструктора: ако полетата
        // се подредят в грешен ред, пада точно при отваряне.
        Build("CompanyDialog", () => new CompanyDialog(db.Companies, 3));

        // Редакторът е най-сложният екран — сглобява се с истински отчет.
        var docId = db.Documents.Create("year", "r_yr_generated_waste", "Проба",
            new Dictionary<string, string> { ["year"] = "2025" });
        Build("EditorForm", () => new EditorForm(service, db, docId));

        // Прозорчето за разделяне строи редовете си на ръка и с шаблон:
        // грешка в подредбата пада точно тук, а не при първото натискане.
        Build("SplitDialog", () =>
        {
            var book = service.GetSchema("book");
            var node = book.FindReport("r_b_recovery_dispose")!
                .SelfAndDescendants().First(n => n.ElementName == "r_b_rd_generated");

            SplitTemplates.Put(db.Settings, "r_b_rd_generated", new SplitTemplate("Проба",
                [new SplitPart("160117", 60m), new SplitPart("160118", 40m)]));

            var dialog = new SplitDialog(
                TransferRule.From("r_b_recovery_dispose", "r_b_rd_treated")!,
                book, node, db.Settings, 10m,
                new Dictionary<string, string> { ["date_record"] = "01.02.2026" });

            dialog.UseTemplate("Проба");
            return dialog;
        });
      }
      catch (Exception ex)
      {
        threadError = ex.ToString();
      }
    });

    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();

    foreach (var failure in failures) Console.WriteLine("     " + failure);
    if (threadError is not null) Console.WriteLine("     нишката падна: " + threadError);

    Check("нишката стига до края", threadError is null);
    Check($"всичките {expected} контроли са опитани", built.Count + failures.Count == expected,
          $"{built.Count} успешни, {failures.Count} провалени");
    Check("всяка контрола се сглобява без грешка", failures.Count == 0, $"{failures.Count} провала");

    // Надписите на бутоните не бива да се режат: българските са дълги и
    // постоянна ширина рано или късно отрязва дума.
    var narrow = new List<string>();

    var widths = new Thread(() =>
    {
        // Мярката за сравнение идва от друг път — Graphics.MeasureString, а не
        // TextRenderer. Първата версия на тази проверка ползваше същата
        // функция като самата реализация: двете грешаха еднакво и проверката
        // минаваше зелена, докато надписите се режеха на екрана.
        using var canvas = new Bitmap(1, 1);
        using var graphics = Graphics.FromImage(canvas);

        foreach (var text in new[]
                 { "Нов отчет", "Всички отчети", "Преименувай", "Внеси от Excel", "Изтрий", "Шаблон" })
        {
            using var button = new AppButton(text, ButtonVariant.Ghost) { Glyph = Theme.Glyph.Add };
            button.FitWidth();

            var needed = (int)Math.Ceiling(graphics.MeasureString(text, button.Font).Width);

            // Освен текста в бутона има икона и отстояния отляво и отдясно.
            if (button.Width < needed + 30)
                narrow.Add($"„{text}“ -> {button.Width} пиксела при нужни поне {needed + 30}");
        }
    });

    widths.SetApartmentState(ApartmentState.STA);
    widths.Start();
    widths.Join();

    foreach (var n in narrow) Console.WriteLine("     " + n);
    Check("бутоните се разширяват според надписа", narrow.Count == 0);
}

// =============================================== Теми
Console.WriteLine();
Console.WriteLine("### ТЕСТ - теми");
{
    var problems = new List<string>();

    Check("има четири теми", Palette.All.Count == 4, $"{Palette.All.Count}");

    foreach (var palette in Palette.All)
    {
        // Всеки цвят трябва да е плътен: полупрозрачното става черно при
        // Graphics.Clear и чупи рисуването.
        foreach (var (name, colour) in new (string, Color)[]
                 {
                     (nameof(palette.Canvas), palette.Canvas), (nameof(palette.Surface), palette.Surface),
                     (nameof(palette.Ink), palette.Ink), (nameof(palette.Accent), palette.Accent),
                     (nameof(palette.Border), palette.Border), (nameof(palette.Error), palette.Error)
                 })
        {
            if (colour.A != 255) problems.Add($"{palette.Name}: {name} не е плътен");
        }

        // Текстът трябва да се чете върху фона. Прагът е скромен нарочно:
        // проверява се за груба грешка, не се мери по стандарт.
        var contrast = Luminance(palette.Ink) - Luminance(palette.Canvas);
        if (Math.Abs(contrast) < 0.4)
            problems.Add($"{palette.Name}: текстът е твърде близък до фона ({contrast:F2})");

        if (palette.IsDark != Luminance(palette.Canvas) < 0.5)
            problems.Add($"{palette.Name}: IsDark не отговаря на фона");

        // Текстът върху плътния основен цвят също трябва да се чете. В тъмния
        // набор основният цвят е светло синьо и бялото върху него се разтваря.
        Theme.Use(palette.Key);

        var onAccent = Math.Abs(Luminance(Theme.OnAccent) - Luminance(palette.Accent));
        if (onAccent < 0.4)
            problems.Add($"{palette.Name}: надписът върху основния цвят е слаб ({onAccent:F2})");
    }

    foreach (var p in problems) Console.WriteLine("     " + p);
    Check("всяка тема е използваема", problems.Count == 0, $"{problems.Count} проблема");

    Theme.Use("light");

    // Ключът се помни и се връща същата тема.
    Theme.Use("dark");
    Check("темата се сменя", Theme.Current.Key == "dark", Theme.Current.Key);

    Theme.Use("няма такава");
    Check("непозната тема пада към светлата", Theme.Current.Key == "light", Theme.Current.Key);
}

static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

// =============================================== Нищо не остава небоядисано
Console.WriteLine();
Console.WriteLine("### ТЕСТ - всеки прозорец следва темата");
{
    var leftovers = new List<string>();
    string? paintError = null;
    var checkedWindows = 0;

    var paintThread = new Thread(() =>
    {
        try
        {
            var dbPath = Path.Combine(work, $"themed_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(dbPath);

            var docId = db.Documents.Create("book", "r_b_generated_waste", "Проба",
                new Dictionary<string, string> { ["year"] = "2025" });

            var book = service.GetSchema("book");
            var report = book.FindReport("r_b_generated_waste")!;

            // Складът иска фирма и партида, за да има какво да покаже.
            var themedCompany = db.Companies.Add("203344556", "Метал ООД");
            var themedBatch = db.Batches.Add(themedCompany, new BatchDraft(
                "203344556", "Метал ООД", "160216", new DateOnly(2026, 9, 3)));

            db.Ledger.Add(themedCompany, new MoveDraft(
                MoveKind.Received, new DateOnly(2026, 9, 3), "160216", 4m,
                Draws: [new Draw(themedBatch, 4m)]));

            var themedShare = new StockShare("160216", db.Batches.Find(themedBatch)!, 4m);

            // Всяка тема поотделно: пропуск при светла не личи, защото
            // подразбиращото се на Windows също е светло.
            foreach (var palette in Palette.All)
            {
                Theme.Use(palette.Key);

                var windows = new List<Control>
                {
                    new ShellForm(service, db),
                    new EditorForm(service, db, docId),
                    new NewReportDialog(service),
                    new HelpDialog(ReportGuide_Builder.For(book, report)),
                    new ShortcutsDialog(),
                    new CompanyDialog(db.Companies, 3),

                    new BalanceReportsDialog(
                    [
                        new ReportPeriod(1, "Проба", "Алфа", "Проверен", false,
                            new DateTime(2026, 1, 1), new DateTime(2026, 6, 30), null, null, 3)
                    ], 2026, 1, 12),

                    new OpeningsDialog(
                        [new Opening(1, 2026, "160604", 5m)],
                        [new Company(1, "111111111", "Алфа ЕООД")],
                        new Dictionary<string, string> { ["160604"] = "Алкални батерии" },
                        (_, _) => null),

                    new ImportsDialog(db),
                    new PeriodsDialog(db, new CompanyScope(db)),

                    new PartialDialog(
                        [new StockShare("150101", new Batch(1, "2026-0001", "204455667", "Скрап ЕООД",
                            "150101", new DateOnly(2026, 9, 3)), 10m)],
                        new Dictionary<long, decimal>()),

                    new TreatDialog(service, db, themedCompany, "1", [themedShare],
                        new DateOnly(2026, 9, 30)),

                    new GenerateDialog(service, db, themedCompany, "1", [themedShare],
                        new DateOnly(2026, 9, 30)),

                    new SubmitDialog(service, db, themedCompany, "1", [themedShare],
                        new DateOnly(2026, 9, 30)),

                    new ActionLogDialog(db, themedCompany, "1"),

                    new ReceiversDialog(db, themedCompany),

                    new StartStockDialog(service, db, themedCompany, "1"),

                    new ImportPreviewDialog("проба.xlsx",
                    [
                        new ImportedMove(1, MoveKind.Received, new DateOnly(2026, 9, 1), "160216",
                            12m, "1", "203344556", "Метал ООД", "ППП 1", string.Empty, false,
                            new Dictionary<string, string>(StringComparer.Ordinal))
                    ], MoveKind.Received),

                    new BatchDialog(db, themedCompany, "1", themedShare.Batch,
                        new Dictionary<string, string>(StringComparer.Ordinal))
                };

                foreach (var window in windows)
                {
                    using (window)
                    {
                        _ = window.Handle;
                        checkedWindows++;

                        foreach (var leftover in Theme.Unthemed(window))
                            leftovers.Add($"{palette.Name}: {window.GetType().Name} -> {leftover}");
                    }
                }
            }

            Theme.Use("light");
        }
        catch (Exception ex)
        {
            paintError = ex.ToString();
        }
    });

    paintThread.SetApartmentState(ApartmentState.STA);
    paintThread.Start();
    paintThread.Join();

    if (paintError is not null) Console.WriteLine("     " + paintError);
    foreach (var l in leftovers.Distinct().Take(8)) Console.WriteLine("     " + l);

    Check("прозорците се сглобяват при всяка тема", paintError is null);
    Check("проверени са 76 прозореца", checkedWindows == 76, $"{checkedWindows}");
    Check("нито една контрола не остава с цвят по подразбиране",
        leftovers.Count == 0, $"{leftovers.Count} пропуснати");
}

// =============================================== Редове в таблицата
Console.WriteLine();
Console.WriteLine("### ТЕСТ - управление на редовете");
{
    var results = new List<string>();
    string? rowsError = null;

    var rowThread = new Thread(() =>
    {
        try
        {
            var year = service.GetSchema("year");
            var report = year.FindReport("r_yr_generated_waste")!;
            var plan = WorkbookLayout.Build(year, [report]).Sheets[0];

            using var grid = new SheetGrid(plan, []);
            _ = grid.Handle;

            // Обичайният ход: сто реда „за всеки случай“, данни само в част от тях.
            grid.AddRows(100);
            results.Add(grid.DataRowCount == 100 ? "" : $"след добавяне: {grid.DataRowCount}");

            var column = plan.Columns[0].Name;
            for (var i = 0; i < 20; i++) grid.PutValue(i, column, (i + 1).ToString());

            // Свеждане до 20 маха точно празните.
            var blocked = grid.TrimTo(20);
            results.Add(grid.DataRowCount == 20 ? "" : $"след свеждане: {grid.DataRowCount}");
            results.Add(blocked == 0 ? "" : $"неочаквано спрени редове: {blocked}");

            // Ред с данни не се трие заради число в кутийка.
            var stillBlocked = grid.TrimTo(5);
            results.Add(stillBlocked == 15 ? "" : $"спрени редове: {stillBlocked}, а се очакват 15");
            results.Add(grid.DataRowCount == 20 ? "" : $"редовете с данни оцеляха: {grid.DataRowCount}");

            // Празен ред по средата също се маха.
            grid.AddRows(3);
            grid.PutValue(grid.DataRowCount - 1, column, "край");
            var removed = grid.RemoveEmptyRows();
            results.Add(removed == 2 ? "" : $"махнати празни: {removed}, а се очакват 2");
            results.Add(grid.DataRowCount == 21 ? "" : $"останали редове: {grid.DataRowCount}");

            // Свеждане нагоре просто добавя.
            grid.TrimTo(30);
            results.Add(grid.DataRowCount == 30 ? "" : $"след разширяване: {grid.DataRowCount}");

            // Една стойност върху избрана област я слага навсякъде.
            var filled = grid.FillColumn(column, "повторено", 0, 9);
            results.Add(filled == 10 ? "" : $"попълнени клетки: {filled}, а се очакват 10");
            results.Add(grid.ValueAt(4, column) == "повторено" ? "" : $"клетка 5: {grid.ValueAt(4, column)}");
            results.Add(grid.ValueAt(12, column) == "13" ? "" : $"клетка 13 се е променила: {grid.ValueAt(12, column)}");
        }
        catch (Exception ex)
        {
            rowsError = ex.ToString();
        }
    });

    rowThread.SetApartmentState(ApartmentState.STA);
    rowThread.Start();
    rowThread.Join();

    var problems = results.Where(r => r.Length > 0).ToList();
    foreach (var p in problems) Console.WriteLine("     " + p);
    if (rowsError is not null) Console.WriteLine("     " + rowsError);

    Check("работата с редове минава без изключение", rowsError is null);
    Check("всички проверки за редове са направени", results.Count == 11, $"{results.Count} от 11");
    Check("редовете се добавят, свеждат и чистят правилно", problems.Count == 0);
}

// =============================================== Подредба и упътване
Console.WriteLine();
Console.WriteLine();
Console.WriteLine("### ТЕСТ - изтриване на цели редове");
{
    var triedRows = 0;
    var notes = new List<string>();
    string? deleteError = null;

    void Expect(string what, bool ok)
    {
        triedRows++;
        if (!ok) notes.Add(what);
    }

    var deleteThread = new Thread(() =>
    {
        try
        {
            var year = service.GetSchema("year");
            var report = year.FindReport("r_yr_generated_waste")!;
            var plan = WorkbookLayout.Build(year, [report]).Sheets[0];

            using var grid = new SheetGrid(plan, []);
            _ = grid.Handle;

            void Refill()
            {
                grid.RemoveRows(Enumerable.Range(0, grid.DataRowCount));
                for (var i = 0; i < 5; i++) grid.PutValue(i, "stagenum", (i + 1).ToString());
            }

            // Средният ред си отива, а останалите се събират - не остава дупка.
            Refill();
            Expect("пет реда преди изтриването", grid.DataRowCount == 5);
            Expect("един изтрит ред се брои за един", grid.RemoveRows([2]) == 1);
            Expect("остават четири реда", grid.DataRowCount == 4);
            Expect("редът над дупката си остава", grid.ValueAt(1, "stagenum") == "2");
            Expect("редът под дупката се вдига нагоре", grid.ValueAt(2, "stagenum") == "4");

            // Няколко наведнъж: номерата се местят при всяко махане, затова се
            // върви отзад напред. При обхождане отпред тук биха паднали 0, 2, 4.
            Refill();
            Expect("три реда наведнъж се броят за три", grid.RemoveRows([0, 1, 2]) == 3);
            Expect("остават последните два", grid.DataRowCount == 2);
            Expect("остават точно 4 и 5",
                grid.ValueAt(0, "stagenum") == "4" && grid.ValueAt(1, "stagenum") == "5");

            // Разпръснат избор - същото правило.
            Refill();
            Expect("разпръснатите редове се махат", grid.RemoveRows([0, 2, 4]) == 3);
            Expect("остават 2 и 4",
                grid.ValueAt(0, "stagenum") == "2" && grid.ValueAt(1, "stagenum") == "4");

            // Повторен номер не брои два пъти и не яде съседа.
            Refill();
            Expect("повторен номер се брои веднъж", grid.RemoveRows([1, 1, 1]) == 1);
            Expect("съседът остава непокътнат", grid.ValueAt(1, "stagenum") == "3");

            // Извън обхвата се пропуска, вместо да хвърля.
            Refill();
            Expect("несъществуващ ред се пропуска", grid.RemoveRows([99, -1]) == 0);
            Expect("нищо не е пипнато", grid.DataRowCount == 5);

            // Празният списък не пипа нищо - това е и пътят при липса на избор.
            Expect("празен избор не маха нищо", grid.RemoveRows([]) == 0);
            Expect("таблицата е същата", grid.DataRowCount == 5);

            // Ctrl+Delete без избран ред не бива да прави нищо и да не пита.
            grid.RemoveRows(Enumerable.Range(0, grid.DataRowCount));
            Expect("Ctrl+Delete се поема и празна", grid.HandlesKey(Keys.Control | Keys.Delete));

            // Изборът тръгва от клетките, не от заглавията на редовете.
            Refill();
            grid.SelectCellsForTest(1, 3);
            Expect("избраните клетки сочат редовете си",
                grid.SelectedDataRows().SequenceEqual([1, 2, 3]));
        }
        catch (Exception ex)
        {
            deleteError = ex.ToString();
        }
    });

    deleteThread.SetApartmentState(ApartmentState.STA);
    deleteThread.Start();
    deleteThread.Join();

    if (deleteError is not null) Console.WriteLine("     " + deleteError);
    foreach (var n in notes) Console.WriteLine("     " + n);

    const int expectedRowChecks = 18;

    Check("изтриването минава без изключение", deleteError is null);
    Check($"направени са всичките {expectedRowChecks} проверки", triedRows == expectedRowChecks,
        $"{triedRows} от {expectedRowChecks}");
    Check("редовете се махат изцяло и без дупки", notes.Count == 0);
}

Console.WriteLine("### ТЕСТ - подредба и упътване");
{
    var notes = new List<string>();
    string? sortError = null;

    var sortThread = new Thread(() =>
    {
        try
        {
            // Този отчет има и число, и свободен текст в основната си таблица —
            // нужни са и двете: числото за подредбата, текстът за ширината.
            var year = service.GetSchema("year");
            var report = year.FindReport("r_yr_generated_waste")!;
            var plan = WorkbookLayout.Build(year, [report]).Sheets[0];

            var numeric = plan.Columns.First(c => c.Domain.Kind == ValueKind.Integer).Name;

            using var grid = new SheetGrid(plan, []);
            _ = grid.Handle;

            grid.AddRows(6);
            grid.PutValue(0, numeric, "10");
            grid.PutValue(1, numeric, "9");
            grid.PutValue(2, numeric, "100");
            grid.PutValue(3, numeric, "2");
            // редове 4 и 5 остават празни

            grid.SortByColumn(numeric);

            var order = grid.ColumnValues(numeric);
            var filled = order.Where(v => v.Length > 0).ToList();

            // Числата се подреждат като числа, а не по азбучен ред.
            notes.Add(string.Join(",", filled) == "2,9,10,100"
                ? "" : "възходящо: " + string.Join(",", filled));

            // Празните остават най-долу, а не най-горе.
            notes.Add(order[4].Length == 0 && order[5].Length == 0
                ? "" : "празните не са най-долу: " + string.Join("|", order));

            grid.SortByColumn(numeric);
            var back = grid.ColumnValues(numeric).Where(v => v.Length > 0).ToList();
            notes.Add(string.Join(",", back) == "100,10,9,2"
                ? "" : "низходящо: " + string.Join(",", back));

            // Побирането по съдържание: дълга стойност разширява колоната.
            var text = plan.Columns.First(c => c.Domain.Kind == ValueKind.Text).Name;
            var before = grid.ColumnWidth(text);

            grid.PutValue(0, text, new string('Ш', 60));
            grid.FitColumnsToContent();

            var after = grid.ColumnWidth(text);
            notes.Add(after > before ? "" : $"колоната не се разшири: {before} -> {after}");

            // Но не безкрайно: таванът пази останалите колони на екрана.
            notes.Add(after <= 420 ? "" : $"колоната мина тавана: {after}");
        }
        catch (Exception ex)
        {
            sortError = ex.ToString();
        }
    });

    sortThread.SetApartmentState(ApartmentState.STA);
    sortThread.Start();
    sortThread.Join();

    if (sortError is not null) Console.WriteLine("     " + sortError);
    foreach (var n in notes.Where(n => n.Length > 0)) Console.WriteLine("     " + n);

    Check("подредбата минава без изключение", sortError is null);
    Check("петте проверки за подредба и ширина са направени", notes.Count == 5, $"{notes.Count} от 5");
    Check("подредба по число, празни долу, колони по съдържание",
        notes.All(n => n.Length == 0));

    // Упътването се съставя от схемата за всеки отчет, без нито едно изключение.
    var broken = new List<string>();
    var untranslated = new SortedSet<string>(StringComparer.Ordinal);
    var totalFields = 0;

    // Толкова полета нямат български надпис днес — от 1442 описани. Числото е
    // таван, не цел: щом някое се допълни в FieldLabels, таванът се сваля.
    // Порасне ли, някъде е добавено поле, което никой не е кръстил.
    const int knownUntranslated = 18;

    foreach (var schema in service.Schemas)
    {
        foreach (var report in schema.Reports)
        {
            try
            {
                var guide = ReportGuide_Builder.For(schema, report);

                if (guide.Tables.Count == 0) broken.Add($"{report.ElementName}: без таблици");
                if (guide.Rules.Count == 0) broken.Add($"{report.ElementName}: без правила");
                if (guide.Steps.Count == 0) broken.Add($"{report.ElementName}: без стъпки");

                totalFields += guide.Tables.Sum(t => t.Fields.Count);

                // Полета без български надпис се броят отделно: това не е
                // грешка в кода, а празнина в самата схема на НИСО — там
                // коментарът е празен. Списъкът стои по-долу, за да се допълни
                // в FieldLabels от човек, който знае материята. Измислен
                // надпис в програма за отчети пред държавата е по-лош от
                // техническото име.
                foreach (var field in guide.Tables.SelectMany(t => t.Fields))
                    if (field.Label.Contains('_') && field.Label == field.Label.ToLowerInvariant())
                        untranslated.Add(field.Label);
            }
            catch (Exception ex)
            {
                broken.Add($"{report.ElementName}: {ex.GetType().Name}");
            }
        }
    }

    foreach (var b in broken.Take(5)) Console.WriteLine("     " + b);

    Check($"упътване за всичките {service.Schemas.Sum(s => s.Reports.Count)} отчета",
        broken.Count == 0, $"{broken.Count} проблема, {totalFields} описани полета");

    Console.WriteLine($"     без български надпис ({untranslated.Count}): " +
                      string.Join(", ", untranslated));

    // Прозорецът наистина се подрежда, а не се свива на тесни ивици.
    var sizes = new List<Size>();
    string? helpError = null;

    var helpThread = new Thread(() =>
    {
        try
        {
            var book = service.GetSchema("book");
            var report = book.FindReport("r_b_generated_waste")!;

            using var dialog = new HelpDialog(ReportGuide_Builder.For(book, report));
            dialog.Width = 900;
            sizes.AddRange(dialog.CardSizes());
        }
        catch (Exception ex)
        {
            helpError = ex.ToString();
        }
    });

    helpThread.SetApartmentState(ApartmentState.STA);
    helpThread.Start();
    helpThread.Join();

    if (helpError is not null) Console.WriteLine("     " + helpError);

    Check("прозорецът с упътването се подрежда", helpError is null);
    Check("картите заемат ширината на прозореца",
        sizes.Count > 0 && sizes.All(z => z.Width > 600),
        sizes.Count == 0 ? "няма карти" : $"най-тясната е {sizes.Min(z => z.Width)} пиксела");
    Check("картите имат съдържание, а не са сплескани",
        sizes.Count > 0 && sizes.All(z => z.Height > 70),
        sizes.Count == 0 ? "няма карти" : $"най-ниската е {sizes.Min(z => z.Height)} пиксела");

    Check("непреведените полета не се увеличават",
        untranslated.Count <= knownUntranslated,
        $"{untranslated.Count} при таван {knownUntranslated}");
}

// =============================================== Клавишни комбинации
Console.WriteLine();
Console.WriteLine("### ТЕСТ - клавишни комбинации");
{
    // Съчетанията, които минават през ProcessCmdKey на таблицата. Ctrl+C и Tab
    // не са тук: те са вградени в самата мрежа, не се поемат от нас.
    var handled = new[]
    {
        ("F4", Keys.F4),
        ("Ctrl+V", Keys.Control | Keys.V),
        ("Ctrl+D", Keys.Control | Keys.D),
        ("Ctrl+Shift+D", Keys.Control | Keys.Shift | Keys.D),
        ("Ctrl+Enter", Keys.Control | Keys.Enter),
        ("Ctrl+Shift+N", Keys.Control | Keys.Shift | Keys.N),
        ("Delete", Keys.Delete),
        ("Ctrl+Delete", Keys.Control | Keys.Delete),
        ("Ctrl+-", Keys.Control | Keys.OemMinus)
    };

    var missing = new List<string>();
    var undocumented = new List<string>();
    string? keysError = null;

    // Брои се колко са опитани: иначе изключение преди цикъла оставя
    // „няма пропуснати“ вярно, без нищо да е проверено.
    var tried = 0;

    var keyThread = new Thread(() =>
    {
        try
        {
            var year = service.GetSchema("year");
            var report = year.FindReport("r_yr_generated_waste")!;
            var plan = WorkbookLayout.Build(year, [report]).Sheets[0];

            using var grid = new SheetGrid(plan, []);
            _ = grid.Handle;

            foreach (var (name, combination) in handled)
            {
                if (!grid.HandlesKey(combination)) missing.Add($"таблицата не поема {name}");
                tried++;
            }
        }
        catch (Exception ex)
        {
            keysError = ex.ToString();
        }
    });

    keyThread.SetApartmentState(ApartmentState.STA);
    keyThread.Start();
    keyThread.Join();

    // И обратното: всяко поето съчетание трябва да е описано в прозореца.
    var documented = ShortcutsDialog.Shortcuts.Select(s => s.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);

    foreach (var (name, _) in handled)
        if (!documented.Contains(name))
            undocumented.Add($"{name} се поема, но не е описано");

    if (keysError is not null) Console.WriteLine("     " + keysError);
    foreach (var m in missing) Console.WriteLine("     " + m);
    foreach (var u in undocumented) Console.WriteLine("     " + u);

    Check("проверката на клавишите минава", keysError is null);
    Check("опитани са всичките девет съчетания", tried == handled.Length, $"{tried} от {handled.Length}");
    Check("таблицата поема всичките девет съчетания", missing.Count == 0);
    Check("всяко поето съчетание е описано в прозореца", undocumented.Count == 0);
    Check("описаните съчетания са групирани",
        ShortcutsDialog.Shortcuts.Select(s => s.Group).Distinct().Count() == 5,
        $"{ShortcutsDialog.Shortcuts.Select(s => s.Group).Distinct().Count()} групи");
}

// =============================================== Дати с различен разделител
Console.WriteLine();
Console.WriteLine("### ТЕСТ - разбор на дати");
{
    // НИСО не уточнява разделителя, а хората лепят дати отвсякъде.
    var accepted = new (string Input, string Expected)[]
    {
        ("01.06.2026", "01.06.2026"),
        ("1.6.2026",   "01.06.2026"),
        ("01/06/2026", "01.06.2026"),
        ("01-06-2026", "01.06.2026"),
        ("2026-06-01", "01.06.2026"),
        ("2026.06.01", "01.06.2026"),
        ("01.06.26",   "01.06.2026"),
        ("  07.06.2026  ", "07.06.2026"),
    };

    var wrong = new List<string>();

    foreach (var (input, expected) in accepted)
        if (!ValueConverter.TryNormalizeDate(input, out var got) || got != expected)
            wrong.Add($"„{input}“ -> „{got}“, а се очаква „{expected}“");

    foreach (var w in wrong) Console.WriteLine("     " + w);
    Check("всички разделители се разпознават", wrong.Count == 0, $"{wrong.Count} разминавания");

    // Серийното число на Excel също е дата.
    Check("серийно число на Excel се разпознава",
        ValueConverter.TryNormalizeDate("46174", out var serial) && serial.EndsWith(".2026"), serial);

    var rejected = new[] { "", "   ", "не е дата", "32.13.2026", "abc.def.ghij" };
    var slipped = rejected.Where(r => ValueConverter.TryNormalizeDate(r, out _)).ToList();

    foreach (var r in slipped) Console.WriteLine("     прието неправилно: " + r);
    Check("невалидното се отхвърля", slipped.Count == 0);

    // Каквото се приема тук, трябва да мине и към XML — иначе подсказката в
    // мрежата и грешката при генериране си противоречат.
    var dateColumn = service.GetSchema("book").Reports
        .SelectMany(r => r.SelfAndDescendants())
        .SelectMany(n => n.Attributes)
        .FirstOrDefault(c => c.Domain.Kind == ValueKind.Date);

    if (dateColumn is not null)
    {
        var converted = ValueConverter.Convert(CellValue.FromText("01/06/2026"), dateColumn);
        Check("нормализираното минава и към XML",
            converted.Ok && converted.Value == "2026-06-01", converted.Error ?? converted.Value);
    }
}

// ===================================== отсяване на сертификатите за подпис
Console.WriteLine("\n### ТЕСТ - кои сертификати стигат до прозореца за подпис");
{
    // Издател, за да не са всички самоподписани — КЕП винаги идва от доставчик.
    using var caKey = RSA.Create(2048);
    var caRequest = new CertificateRequest("CN=ТЕСТ Доставчик", caKey,
        HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
    caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
        X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.DigitalSignature, true));
    using var ca = caRequest.CreateSelfSigned(
        DateTimeOffset.Now.AddDays(-2), DateTimeOffset.Now.AddYears(5));

    X509Certificate2 Issued(string subject, params X509Extension[] extensions)
    {
        var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        foreach (var extension in extensions) request.CertificateExtensions.Add(extension);

        return request.Create(ca, DateTimeOffset.Now.AddDays(-1),
            DateTimeOffset.Now.AddYears(1), Guid.NewGuid().ToByteArray());
    }

    var kep = Issued("CN=Иван Петров",
        new X509KeyUsageExtension(X509KeyUsageFlags.NonRepudiation, true));
    Check("КЕП с nonRepudiation се предлага пръв", SignDialog.RankOf(kep) == 2,
          SignDialog.RankOf(kep).ToString());

    var qcOnly = Issued("CN=Иван Петров QC",
        new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true),
        new X509Extension(new Oid("1.3.6.1.5.5.7.1.3"), [0x30, 0x00], false));
    Check("QCStatements също нарежда сертификата пръв", SignDialog.RankOf(qcOnly) == 2,
          SignDialog.RankOf(qcOnly).ToString());

    var plain = Issued("CN=Обикновен подпис",
        new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
    Check("издаден сертификат за подпис се показва, но по-надолу",
          SignDialog.RankOf(plain) == 1, SignDialog.RankOf(plain).ToString());

    // Трите находки от машина за разработка — точно те не бива да се предлагат.
    using var noUsage = new CertificateRequest("CN=3127d794-14b1", RSA.Create(2048),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
        .CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(8));
    Check("сертификат без KeyUsage отпада", SignDialog.RankOf(noUsage) == 0,
          SignDialog.RankOf(noUsage).ToString());

    using var localhost = new CertificateRequest("CN=localhost", RSA.Create(2048),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
        {
            CertificateExtensions =
            {
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true)
            }
        }
        .CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(4));
    Check("самоподписаният localhost отпада", SignDialog.RankOf(localhost) == 0,
          SignDialog.RankOf(localhost).ToString());

    var encryptionOnly = Issued("CN=Само шифроване",
        new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment, true));
    Check("сертификат само за шифроване отпада", SignDialog.RankOf(encryptionOnly) == 0,
          SignDialog.RankOf(encryptionOnly).ToString());

    // Прозорецът трябва да се отваря и когато няма какво да предложи.
    using (var dialog = new SignDialog())
    {
        Check("прозорецът за подпис се сглобява без КЕП", dialog.Choice is null);
    }

    kep.Dispose();
    qcOnly.Dispose();
    plain.Dispose();
    encryptionOnly.Dispose();
}
// =============================================== Пренасяне между разделите
Console.WriteLine();
Console.WriteLine("### ТЕСТ - отчетна книга за оползотворяване: подредба и пренасяне");
{
    var notes = new List<string>();
    string? moveError = null;

    var moveThread = new Thread(() =>
    {
        try
        {
            var movePath = Path.Combine(work, $"move_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(movePath);

            var docId = db.Documents.Create("book", "r_b_recovery_dispose", "Проба",
                new Dictionary<string, string> { ["year"] = "2026" });

            using var form = new EditorForm(service, db, docId);
            _ = form.Handle;

            // 1. Подредбата на разделите следва пътя на отпадъка през площадката.
            var order = form.Grids.Select(g => g.Plan.Node.ElementName).ToList();

            string[] wanted =
            [
                "r_b_rd_received", "r_b_rd_treated", "r_b_rd_generated",
                "r_b_rd_submitted", "r_b_recovery_dispose", "r_b_notes"
            ];

            notes.Add(order.SequenceEqual(wanted) ? "" : "подредба: " + string.Join(", ", order));

            var received = form.Grids.First(g => g.Plan.Node.ElementName == "r_b_rd_received");
            var treated = form.Grids.First(g => g.Plan.Node.ElementName == "r_b_rd_treated");

            // 2. Копче има навсякъде, откъдето тръгва правило, и никъде другаде.
            var generated = form.Grids.First(g => g.Plan.Node.ElementName == "r_b_rd_generated");
            var submitted = form.Grids.First(g => g.Plan.Node.ElementName == "r_b_rd_submitted");

            notes.Add(received.ColumnWidth("__move") > 0 ? "" : "липсва копче в „Получен отпадък“");
            notes.Add(received.ColumnWidth("__split") == 0 ? "" : "„Получен отпадък“ не се дели, а има копче за делене");
            notes.Add(treated.ColumnWidth("__move") > 0 ? "" : "липсва копче едно към едно в „Обработен отпадък“");
            notes.Add(treated.ColumnWidth("__split") > 0 ? "" : "липсва копче за делене в „Обработен отпадък“");
            notes.Add(generated.ColumnWidth("__move") == 0 ? "" : "копче се е появило в „Образуван“");
            notes.Add(submitted.ColumnWidth("__move") == 0 ? "" : "копче се е появило в „Предаден отпадък“");

            // 3. Един получен ред, попълнен както го попълва човек.
            received.AddRows(1);
            received.PutValue(0, "stagenum", "7");
            received.PutValue(0, "date_record", "10.01.2026");
            received.PutValue(0, "amount_ton", "12,5");

            var ewc = received.Plan.Columns.First(c => c.Name == "ewc");
            received.PutValue(0, "ewc", ewc.Domain.Options[0].Display);

            // 4. Панелът дава стойностите, които се различават.
            form.OpenSidePanel();

            var target = service.GetSchema("book")
                .FindReport("r_b_recovery_dispose")!
                .SelfAndDescendants().First(n => n.ElementName == "r_b_rd_treated");

            var operation = target.Attributes.First(a => a.Name == "op_id_code").Domain.Options[0];
            var code = target.Attributes.First(a => a.Name == "ewc").Domain.Options[1];

            form.Side.Remember(new Dictionary<string, string>
            {
                ["ewc"] = code.Value,
                ["date_record"] = "31.03.2026",
                ["op_id_code"] = operation.Value
            });

            notes.Add(form.Side.Missing().Count == 0
                ? ""
                : "панелът се оплаква от: " + string.Join(", ", form.Side.Missing()));

            form.Side.AlwaysUse = true;

            // 5. Натискането на копчето създава реда отсреща.
            var before = treated.DataRowCount;
            received.PressRowAction(0, "__move");

            notes.Add(treated.DataRowCount == before + 1
                ? ""
                : $"редове в „Обработен отпадък“: {treated.DataRowCount}, преди: {before}");

            // 6. Каквото се повтаря, е копирано; каквото се различава, е от панела.
            notes.Add(treated.ValueAt(0, "stagenum") == "7" ? "" : "работният лист не е пренесен");
            notes.Add(treated.ValueAt(0, "amount_ton") == "12,5"
                ? "" : "количеството не е пренесено: " + treated.ValueAt(0, "amount_ton"));
            // В клетката стои кодът заедно с номенклатурата — иначе редът се
            // проверява с отделен списък в ръка.
            notes.Add(treated.ValueAt(0, "ewc") == code.Display
                ? "" : "кодът не е от панела: " + treated.ValueAt(0, "ewc"));
            notes.Add(treated.ValueAt(0, "date_record") == "31.03.2026"
                ? "" : "датата не е от панела: " + treated.ValueAt(0, "date_record"));

            // 7. Описанието на дейността се пише само.
            notes.Add(treated.ValueAt(0, "op_descr") == operation.Description
                ? "" : "описанието не следва дейността: " + treated.ValueAt(0, "op_descr"));

            // 8. Копчето носи надпис и на реда „нов запис“ — в празна таблица
            //    той е единственият и празно синьо петно изглежда счупено.
            notes.Add(received.ShownAt(received.DataRowCount, "__move") == "Тр."
                ? "" : "копчето на новия ред е без надпис: „" + received.ShownAt(received.DataRowCount, "__move") + "“");

            // 9. Редът остава отбелязан като пренесен.
            notes.Add(received.LocalValue(0, "__moved").Length > 0 ? "" : "редът не е отбелязан");

            // 10. Отметката се пази в базата, но не влиза в плана на листа.
            var snapshot = received.Snapshot();
            notes.Add(snapshot.Count == 1 && snapshot[0].ContainsKey("__moved")
                ? "" : "отметката не се записва");

            var book = service.GetSchema("book");
            var built = WorkbookLayout.Build(book, [book.FindReport("r_b_recovery_dispose")!]);

            notes.Add(built.Sheets.All(sheet => sheet.Columns.All(c => !c.Name.StartsWith("__")))
                ? "" : "служебна колона е стигнала до плана на листа");
        }
        catch (Exception ex)
        {
            moveError = ex.ToString();
        }
    });

    moveThread.SetApartmentState(ApartmentState.STA);
    moveThread.Start();
    moveThread.Join();

    if (moveError is not null) Console.WriteLine("     " + moveError);

    var problems = notes.Where(n => n.Length > 0).ToList();
    foreach (var problem in problems) Console.WriteLine("     " + problem);

    Check("пренасянето минава без изключение", moveError is null);
    Check($"направени са всичките {notes.Count} проверки", notes.Count == 18, $"{notes.Count}");
    Check("разделите са подредени и редът се пренася цял", problems.Count == 0, $"{problems.Count} проблема");
}

// =============================================== Разделяне на количество
Console.WriteLine();
Console.WriteLine("### ТЕСТ - разделяне на един ред на няколко");
{
    var notes = new List<string>();

    // Сметките стоят отделно от прозореца точно за да се проверят тук.
    var thirds = SplitMath.Shares(50m, [33.33m, 33.33m, 33.34m]);
    notes.Add(thirds.Sum() == 50m ? "" : "сборът на три дяла: " + thirds.Sum());

    var uneven = SplitMath.Shares(50m, [33.333m, 33.333m, 33.334m]);
    notes.Add(uneven.Sum() == 50m ? "" : "сборът при закръгляне: " + uneven.Sum());
    notes.Add(uneven.All(v => decimal.Round(v, 3) == v) ? "" : "дял с повече от три знака");

    notes.Add(SplitMath.Percent(12.5m, 50m) == 25m ? "" : "процентът е сбъркан");
    notes.Add(SplitMath.TryRead("12.5", out var dotted) && dotted == 12.5m ? "" : "точката не се чете");
    notes.Add(SplitMath.TryRead("12,5", out var comma) && comma == 12.5m ? "" : "запетаята не се чете");

    string? splitError = null;

    var splitThread = new Thread(() =>
    {
        try
        {
            var splitPath = Path.Combine(work, $"split_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(splitPath);

            // Шаблонът се записва както го записва прозорчето.
            SplitTemplates.Put(db.Settings, "r_b_rd_generated", new SplitTemplate("Стар кабел",
            [
                new SplitPart("160117", 70m),
                new SplitPart("160118", 25m),
                new SplitPart("160119", 5m)
            ]));

            var docId = db.Documents.Create("book", "r_b_recovery_dispose", "Проба",
                new Dictionary<string, string> { ["year"] = "2026" });

            using var form = new EditorForm(service, db, docId);
            _ = form.Handle;

            var treated = form.Grids.First(g => g.Plan.Node.ElementName == "r_b_rd_treated");
            var generated = form.Grids.First(g => g.Plan.Node.ElementName == "r_b_rd_generated");

            treated.AddRows(1);
            treated.PutValue(0, "stagenum", "4");
            treated.PutValue(0, "amount_ton", "50");

            form.OpenSidePanel();
            form.SelectSheet("r_b_rd_treated");

            notes.Add(form.Side.Rule?.ToNode == "r_b_rd_generated"
                ? "" : "панелът не е на правилото за образуване");

            notes.Add(form.Side.UseTemplate("Стар кабел") ? "" : "шаблонът не се намира в панела");

            form.Side.AlwaysUse = true;

            // Едно натискане на копчето за делене прави три реда.
            treated.PressRowAction(0, "__split");

            notes.Add(generated.DataRowCount == 3
                ? "" : $"редове в „Образуван“: {generated.DataRowCount}");

            decimal sum = 0;
            for (var i = 0; i < generated.DataRowCount; i++)
                if (SplitMath.TryRead(generated.ValueAt(i, "amount_ton"), out var part)) sum += part;

            notes.Add(sum == 50m ? "" : "сборът в „Образуван“: " + sum);

            notes.Add(generated.ValueAt(0, "amount_ton") == "35,000"
                ? "" : "първият дял: " + generated.ValueAt(0, "amount_ton"));

            // Работният лист пътува с редовете, иначе те увисват без родител.
            notes.Add(generated.ValueAt(2, "stagenum") == "4" ? "" : "работният лист не е пренесен");

            // Описанието на отпадъка е името на кода — схемата иска и двете.
            notes.Add(generated.ValueAt(0, "waste_desc").Length > 0
                ? "" : "описанието на отпадъка е празно");

            notes.Add(treated.LocalValue(0, "__moved").Length > 0 ? "" : "редът не е отбелязан");

            // Трите реда са една група и количествата им са заключени.
            var link = generated.LinkOf(0);

            notes.Add(link.Length > 0 ? "" : "новият ред не е в група");
            notes.Add(generated.RowsOf(link).Count == 3
                ? "" : $"в групата има {generated.RowsOf(link).Count} реда");

            notes.Add(generated.IsLockedCell(0, "amount_ton")
                ? "" : "количеството на създадения ред не е заключено");

            notes.Add(treated.IsLockedCell(0, "amount_ton")
                ? "" : "количеството на изходния ред не е заключено");

            notes.Add(treated.IsLockedCell(0, "ewc")
                ? "кодът също е заключен, а не бива" : "");

            // Изходният ред помни коя група е породил.
            notes.Add(treated.RowThatSent(link) == 0
                ? "" : "изходният ред не сочи групата");

            // Разливането и запълването не пипат заключена клетка.
            treated.FillColumn("amount_ton", "99", 0, 0);
            notes.Add(treated.ValueAt(0, "amount_ton") == "50"
                ? "" : "заключеното количество се смени на " + treated.ValueAt(0, "amount_ton"));

            // Второто копче на същия раздел прави един ред с цялото количество.
            treated.AddRows(1);
            treated.PutValue(1, "stagenum", "4");
            treated.PutValue(1, "amount_ton", "8");

            var code = generated.Plan.Columns.First(c => c.Name == "ewc").Domain.Options[3];
            form.Side.Remember(new Dictionary<string, string> { ["ewc"] = code.Value });

            treated.PressRowAction(1, "__move");

            notes.Add(generated.DataRowCount == 4
                ? "" : $"след едно към едно редовете са {generated.DataRowCount}");

            notes.Add(generated.ValueAt(3, "amount_ton") == "8"
                ? "" : "цялото количество не е пренесено: " + generated.ValueAt(3, "amount_ton"));

            notes.Add(generated.ValueAt(3, "ewc") == code.Display
                ? "" : "кодът не е от панела: " + generated.ValueAt(3, "ewc"));

            // Ред, който е сам в групата си, се трие направо — и изходният ред
            // престава да се води пренесен.
            generated.DeleteForTest([3]);

            notes.Add(generated.DataRowCount == 3
                ? "" : $"след триене останаха {generated.DataRowCount} реда");

            notes.Add(treated.LocalValue(1, "__moved").Length == 0
                ? "" : "изходният ред още се води пренесен");

            notes.Add(treated.IsLockedCell(1, "amount_ton")
                ? "количеството остана заключено без връзка" : "");

            // Второ пренасяне на същия ред не прави втора група: копчето
            // намира вече създаденото и отваря него.
            notes.Add(EditorForm.ExistingGroup(treated, 0, generated) == link
                ? "" : "вторият опит не намира вече създадената група");

            notes.Add(treated.SentBy(0).Count == 1
                ? "" : $"изходният ред води {treated.SentBy(0).Count} групи");
        }
        catch (Exception ex)
        {
            splitError = ex.ToString();
        }
    });

    splitThread.SetApartmentState(ApartmentState.STA);
    splitThread.Start();
    splitThread.Join();

    if (splitError is not null) Console.WriteLine("     " + splitError);

    var problems = notes.Where(n => n.Length > 0).ToList();
    foreach (var problem in problems) Console.WriteLine("     " + problem);

    Check("разделянето минава без изключение", splitError is null);
    Check($"направени са всичките {notes.Count} проверки", notes.Count == 29, $"{notes.Count}");
    Check("количеството се дели точно и редовете излизат цели", problems.Count == 0, $"{problems.Count} проблема");
}

// =============================================== Подредба и пресяване
Console.WriteLine();
Console.WriteLine("### ТЕСТ - заглавията на служебните колони и пресяването");
{
    var notes = new List<string>();
    string? viewError = null;

    var viewThread = new Thread(() =>
    {
        try
        {
            var viewPath = Path.Combine(work, $"view_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(viewPath);

            var docId = db.Documents.Create("book", "r_b_recovery_dispose", "Проба",
                new Dictionary<string, string> { ["year"] = "2026" });

            using var form = new EditorForm(service, db, docId);
            _ = form.Handle;

            var received = form.Grids.First(g => g.Plan.Node.ElementName == "r_b_rd_received");
            var ewc = received.Plan.Columns.First(c => c.Name == "ewc");

            received.AddRows(4);
            for (var i = 0; i < 4; i++)
            {
                received.PutValue(i, "stagenum", "1");
                received.PutValue(i, "amount_ton", (i + 1).ToString());
                received.PutValue(i, "ewc", ewc.Domain.Options[i % 2].Display);
            }

            // 1. Заглавие на колона без подредба не сваля програмата.
            //    Стрелката за посока хвърля върху неподреждаема колона.
            foreach (var column in new[] { "__no", "amount_ton", "__moved", "__move" })
            {
                try
                {
                    received.SortByColumn(column);
                }
                catch (Exception ex)
                {
                    notes.Add($"подредба по „{column}“ хвърля: {ex.GetType().Name}");
                }
            }

            notes.Add("");

            // 2. Пресяването скрива редове.
            received.FilterForTest("ewc", [ewc.Domain.Options[0].Display]);

            notes.Add(received.VisibleRowCount == 2
                ? "" : $"видими редове след пресяване: {received.VisibleRowCount}");

            notes.Add(received.HasFilters ? "" : "пресяването не се отчита като включено");

            // 3. Скритото си остава в отчета — пресяването е за окото.
            notes.Add(received.Snapshot().Count == 4
                ? "" : $"в отчета остават {received.Snapshot().Count} реда вместо 4");

            notes.Add(received.DataRowCount == 4
                ? "" : $"броят редове се е променил: {received.DataRowCount}");

            // 4. Нов ред пада извън пресяването, затова то си отива.
            received.AddRows(1);

            notes.Add(received.HasFilters ? "добавянето на ред не махна пресяването" : "");
            notes.Add(received.VisibleRowCount == 5
                ? "" : $"след добавяне се виждат {received.VisibleRowCount} реда");

            // 5. И ръчното махане връща всичко.
            received.FilterForTest("ewc", [ewc.Domain.Options[0].Display]);
            received.ClearFilters();

            notes.Add(received.VisibleRowCount == 5
                ? "" : $"след махане се виждат {received.VisibleRowCount} реда");
        }
        catch (Exception ex)
        {
            viewError = ex.ToString();
        }
    });

    viewThread.SetApartmentState(ApartmentState.STA);
    viewThread.Start();
    viewThread.Join();

    if (viewError is not null) Console.WriteLine("     " + viewError);

    var problems = notes.Where(n => n.Length > 0).ToList();
    foreach (var problem in problems) Console.WriteLine("     " + problem);

    Check("прегледът минава без изключение", viewError is null);
    Check($"направени са всичките {notes.Count} проверки", notes.Count == 8, $"{notes.Count}");
    Check("заглавията не хвърлят, пресяването крие само на екрана", problems.Count == 0, $"{problems.Count} проблема");
}



// =============================================== Балансът: сметката
//
// Отчетната книга казва какво се е случило; балансът — колко е останало.
// Правилото е едно изречение („образуваният се събира, предаденият се вади“)
// и точно затова е лесно да се напише другояче на второто място. Тук се
// проверява самата сметка, без прозорец: влизат движения, излиза таблица.
Console.WriteLine("\n### ТЕСТ - балансът кръстосва образуван и предаден");
{
    BalanceEntry[] entries =
    [
        new(BalanceSheet.Generated, "160604", 1, 10m),
        new(BalanceSheet.Submitted, "160604", 2, 4m),

        // Полученият и обработеният описват едно и също количество на две
        // стъпки от пътя му. В кръстосания изглед не участват.
        new(BalanceSheet.Received, "160604", 1, 99m),
        new(BalanceSheet.Treated, "160604", 1, 99m),

        new(BalanceSheet.Generated, "191202", 3, 2.5m)
    ];

    var openings = new Dictionary<string, decimal> { ["160604"] = 5m };

    var cross = BalanceMath.Build(entries, openings);
    var line = cross.Lines.First(l => l.Ewc == "160604");

    Check("началното количество застава пред януари", line.Opening == 5m, $"{line.Opening}");
    Check("образуваният се събира", line.Movement[0] == 10m, $"{line.Movement[0]}");
    Check("предаденият се вади", line.Movement[1] == -4m, $"{line.Movement[1]}");
    Check("полученият и обработеният не влизат", line.Total == 6m, $"{line.Total}");

    Check("салдото се трупа през месеците",
        line.Running[0] == 15m && line.Running[1] == 11m && line.Closing == 11m,
        $"{line.Running[0]} / {line.Running[1]} / {line.Closing}");

    Check("редовете са колкото са различните кодове", cross.Lines.Count == 2, $"{cross.Lines.Count}");

    // Сборният ред се смята от движенията: сборът на салдата по кодове би
    // събрал едно и също количество по два пъти.
    Check("сборният ред стига до 13,5", cross.Totals.Closing == 13.5m, $"{cross.Totals.Closing}");

    var received = BalanceMath.Build(entries, openings, BalanceSheet.Received);
    var only = received.Lines.Single();

    Check("отделният раздел показва записаното", only.Total == 99m, $"{only.Total}");
    Check("началното количество не важи за отделен раздел", only.Opening == 0m, $"{only.Opening}");

    Check("празната таблица няма редове", BalanceMath.Build([]).Lines.Count == 0);

    // Код, за който има само начално количество, пак е ред: наличността му не
    // изчезва, защото през годината нищо не е мръднало.
    var idle = BalanceMath.Build([], new Dictionary<string, decimal> { ["150101"] = 3m });

    Check("код само с начално количество се вижда",
        idle.Lines.Count == 1 && idle.Lines[0].Closing == 3m, $"{idle.Lines.Count}");
}

// =============================================== Балансът: четене на реда
Console.WriteLine("\n### ТЕСТ - редът от отчета става движение");
{
    Dictionary<string, string> row = new()
    {
        ["ewc"] = "16 06 04", ["date_record"] = "15.03.2026", ["amount_ton"] = "12,5"
    };

    var entry = BalanceMath.Entry(BalanceSheet.Generated, row, 2026);

    Check("кодът се свежда до сравнима форма", entry?.Ewc == "160604", entry?.Ewc);
    Check("месецът идва от датата", entry?.Month == 3, $"{entry?.Month}");
    Check("количеството се чете и със запетая", entry?.Amount == 12.5m, $"{entry?.Amount}");

    Check("датата се приема и по ISO",
        BalanceMath.Entry(BalanceSheet.Generated, new Dictionary<string, string>
        {
            ["ewc"] = "160604", ["date_record"] = "2026-03-15", ["amount_ton"] = "1"
        }, 2026)?.Month == 3);

    Check("друга година не се брои", BalanceMath.Entry(BalanceSheet.Generated, row, 2025) is null);

    Check("нечетима дата не се брои",
        BalanceMath.Entry(BalanceSheet.Generated, new Dictionary<string, string>
        {
            ["ewc"] = "160604", ["date_record"] = "някога", ["amount_ton"] = "1"
        }, 2026) is null);

    Check("ред без количество не се брои",
        BalanceMath.Entry(BalanceSheet.Generated, new Dictionary<string, string>
        {
            ["ewc"] = "160604", ["date_record"] = "15.03.2026"
        }, 2026) is null);
}

// =============================================== Балансът: кои отчети влизат
//
// Недовършеният отчет съдържа половин истина и събран с готовите я разваля.
// Изключението е едно и е поименно: човек, който тъкмо попълва книга, иска да
// види какво прави тя с наличностите ПРЕДИ да я затвори.
Console.WriteLine("\n### ТЕСТ - в баланса влизат само проверените отчети");
{
    var balancePath = Path.Combine(work, $"balance_{Guid.NewGuid():N}.db");
    using var db = Workspace.Open(balancePath);

    var alpha = db.Companies.Add("111111111", "Алфа ЕООД");
    var beta = db.Companies.Add("222222222", "Бета ООД");

    long Book(string title, long company, DocumentStatus status, string date, string amount)
    {
        var id = db.Documents.Create("book", "r_b_recovery_dispose", title,
            new Dictionary<string, string> { ["year"] = "2026" }, DocumentOrigin.Manual, company);

        db.Documents.SaveRows(id, "r_b_rd_generated",
        [
            new Dictionary<string, string>
            {
                ["ewc"] = "160604", ["date_record"] = date, ["amount_ton"] = amount
            }
        ]);

        if (status != DocumentStatus.Draft) db.Documents.SetStatus(id, status);

        return id;
    }

    var ready = Book("Проверен", alpha, DocumentStatus.Checked, "10.01.2026", "10");
    var signed = Book("Подписан", alpha, DocumentStatus.Signed, "10.02.2026", "5");
    var draft = Book("Чернова", alpha, DocumentStatus.Draft, "10.03.2026", "100");
    var foreign = Book("Чужда фирма", beta, DocumentStatus.Checked, "10.01.2026", "7");

    var all = db.Documents.List();
    var source = new BalanceSource(db.Documents);

    var mine = BalanceSource.Eligible(all, [alpha]);

    Check("провереният и подписаният влизат",
        mine.Count == 2 && mine.Any(d => d.Id == ready) && mine.Any(d => d.Id == signed),
        $"{mine.Count}");

    Check("черновата остава навън", mine.All(d => d.Id != draft));
    Check("чуждата фирма остава навън", mine.All(d => d.Id != foreign));

    var withDraft = BalanceSource.Eligible(all, [alpha], draft);

    Check("една чернова може да се добави поименно",
        withDraft.Count == 3 && withDraft.Any(d => d.Id == draft), $"{withDraft.Count}");

    Check("без сито по фирми влизат всички", BalanceSource.Eligible(all).Count == 3,
        $"{BalanceSource.Eligible(all).Count}");

    Check("незавършените се предлагат за избор",
        BalanceSource.Drafts(all, [alpha]).Count == 1, $"{BalanceSource.Drafts(all, [alpha]).Count}");

    var table = BalanceMath.Build(source.Read(mine, 2026));

    Check("сборът е само от проверените", table.Totals.Total == 15m, $"{table.Totals.Total}");

    Check("месеците са там, където са датите",
        table.Lines[0].Movement[0] == 10m && table.Lines[0].Movement[1] == 5m,
        $"{table.Lines[0].Movement[0]} / {table.Lines[0].Movement[1]}");

    Check("добавената чернова личи в числата",
        BalanceMath.Build(source.Read(withDraft, 2026)).Totals.Total == 115m);

    Check("другата година е празна", BalanceMath.Build(source.Read(mine, 2025)).Lines.Count == 0);

    var years = source.Years(all, 2030);

    Check("годините идват от отчетите и от днешния ден",
        years.Contains(2026) && years.Contains(2030), string.Join(", ", years));
}

// =============================================== Начални количества
Console.WriteLine("\n### ТЕСТ - начални количества: файл и база");
{
    var openingPath = Path.Combine(work, $"openings_{Guid.NewGuid():N}.db");
    using var db = Workspace.Open(openingPath);

    var alpha = db.Companies.Add("111111111", "Алфа ЕООД");
    var beta = db.Companies.Add("222222222", "Бета ООД");

    db.Openings.Replace(
    [
        new Opening(alpha, 2026, "160604", 12.5m),

        // Същият код, писан другояче: два реда в един файл се събират.
        new Opening(alpha, 2026, "16 06 04", 2.5m),
        new Opening(beta, 2026, "191202", 4m)
    ]);

    var mine = db.Openings.Totals(2026, [alpha]);

    Check("еднаквите кодове се събират", mine.GetValueOrDefault("160604") == 15m,
        $"{mine.GetValueOrDefault("160604")}");

    Check("чуждата фирма не влиза в сбора", !mine.ContainsKey("191202"));

    var both = db.Openings.Totals(2026);

    Check("сборният изглед събира фирмите",
        both.Count == 2 && both["191202"] == 4m, $"{both.Count} кода");

    db.Openings.Replace([new Opening(alpha, 2026, "160604", 3m)]);

    Check("повторното внасяне замества, не трупа",
        db.Openings.Totals(2026, [alpha]).GetValueOrDefault("160604") == 3m,
        $"{db.Openings.Totals(2026, [alpha]).GetValueOrDefault("160604")}");

    Check("другата фирма не се пипа",
        db.Openings.Totals(2026, [beta]).GetValueOrDefault("191202") == 4m);

    Check("другата година не се пипа", db.Openings.List(2025).Count == 0);

    // Файлът: записан и прочетен наново от същия код.
    var file = Path.Combine(work, $"openings_{Guid.NewGuid():N}.xlsx");

    OpeningWorkbook.Save(file, 2026,
        [new OpeningCompany("111111111", "Алфа ЕООД")],
        ["160604", "020103"],
        [
            new OpeningRow(0, "111111111", "160604", 2026, 12.5m),
            new OpeningRow(0, "111111111", "020103", 2026, 1m)
        ]);

    var read = OpeningWorkbook.Read(file);

    Check("файлът се чете обратно без бележки",
        read.Rows.Count == 2 && read.Problems.Count == 0,
        $"{read.Rows.Count} реда, {read.Problems.Count} бележки: {string.Join(" | ", read.Problems)}");

    Check("количеството оцелява през файла",
        read.Rows.Count == 2 && read.Rows[0].Amount == 12.5m,
        read.Rows.Count == 2 ? $"{read.Rows[0].Amount}" : "-");

    Check("водещата нула на кода оцелява",
        read.Rows.Any(r => r.Ewc == "020103"),
        string.Join(", ", read.Rows.Select(r => r.Ewc)));

    // Празният шаблон идва с подсказан ред. Той не е сгрешен, а недокоснат.
    var blank = Path.Combine(work, $"blank_{Guid.NewGuid():N}.xlsx");

    OpeningWorkbook.Save(blank, 2026, [new OpeningCompany("111111111", "Алфа ЕООД")], ["160604"]);

    var empty = OpeningWorkbook.Read(blank);

    Check("недокоснатият шаблон не се оплаква",
        empty.Rows.Count == 0 && empty.Problems.Count == 0,
        $"{empty.Rows.Count} реда, {empty.Problems.Count} бележки");
}

// =============================================== „Същият код“ при третиране
//
// Отпадъкът влиза с един код и в повечето случаи излиза със същия. Тогава
// изборът в панела е преписване от списък с осемстотин реда — работа, при
// която се греши. Отметката заключва полето и кодът идва от самия ред.
Console.WriteLine("\n### ТЕСТ - „същият код“ при третиране");
{
    var sameNotes = new List<string>();
    string? sameError = null;

    var sameThread = new Thread(() =>
    {
        try
        {
            var samePath = Path.Combine(work, $"same_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(samePath);

            var docId = db.Documents.Create("book", "r_b_recovery_dispose", "Проба",
                new Dictionary<string, string> { ["year"] = "2026" });

            using var form = new EditorForm(service, db, docId);
            _ = form.Handle;

            var received = form.Grids.First(g => g.Plan.Node.ElementName == "r_b_rd_received");
            var treated = form.Grids.First(g => g.Plan.Node.ElementName == "r_b_rd_treated");

            var ewc = received.Plan.Columns.First(c => c.Name == "ewc");
            var code = ewc.Domain.Options[3];

            received.AddRows(1);
            received.PutValue(0, "stagenum", "1");
            received.PutValue(0, "date_record", "10.01.2026");
            received.PutValue(0, "amount_ton", "8");
            received.PutValue(0, "ewc", code.Display);

            form.OpenSidePanel();
            form.Side.SameCode = true;

            var target = service.GetSchema("book").FindReport("r_b_recovery_dispose")!
                .SelfAndDescendants().First(n => n.ElementName == "r_b_rd_treated");

            var operation = target.Attributes.First(a => a.Name == "op_id_code").Domain.Options[0];

            form.Side.Remember(new Dictionary<string, string>
            {
                ["date_record"] = "31.01.2026",
                ["op_id_code"] = operation.Value
            });

            // 1. Заключеното поле не се иска и не дава стойност.
            sameNotes.Add(form.Side.Missing().Count == 0
                ? "" : "панелът още иска: " + string.Join(", ", form.Side.Missing()));

            sameNotes.Add(form.Side.Values().ContainsKey("ewc")
                ? "заключеното поле все още дава стойност" : "");

            // 2. Пренасянето минава без питане и носи кода от реда.
            form.Side.AlwaysUse = true;
            received.PressRowAction(0, "__move");

            sameNotes.Add(treated.DataRowCount == 1
                ? "" : $"редове в „Обработен отпадък“: {treated.DataRowCount}");

            sameNotes.Add(treated.ValueAt(0, "ewc") == code.Display
                ? "" : "кодът не е същият: " + treated.ValueAt(0, "ewc"));

            sameNotes.Add(treated.ValueAt(0, "amount_ton") == "8"
                ? "" : "количеството не е пренесено: " + treated.ValueAt(0, "amount_ton"));

            // 3. Свалената отметка връща полето за попълване.
            form.Side.SameCode = false;

            sameNotes.Add(form.Side.Missing().Count == 1
                ? "" : "след свалянето панелът не иска код: " + string.Join(", ", form.Side.Missing()));
        }
        catch (Exception ex)
        {
            sameError = ex.ToString();
        }
    });

    sameThread.SetApartmentState(ApartmentState.STA);
    sameThread.Start();
    sameThread.Join();

    if (sameError is not null) Console.WriteLine("     " + sameError);

    var sameProblems = sameNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in sameProblems) Console.WriteLine("     " + problem);

    Check("пренасянето минава без изключение", sameError is null);
    Check($"направени са всичките {sameNotes.Count} проверки", sameNotes.Count == 6, $"{sameNotes.Count}");
    Check("кодът се взема от реда, а панелът не го иска", sameProblems.Count == 0,
        $"{sameProblems.Count} проблема");
}


// =============================================== Балансът: изгледите
//
// Сметката вече е проверена сама за себе си. Тук се проверява другото —
// че превключвателят стига до таблицата: кръстосаният изглед показва
// началното количество, отделният раздел не, а салдото преименува
// последната колона, защото в него тя значи друго.
Console.WriteLine("\n### ТЕСТ - изгледите на баланса сменят колоните");
{
    Lang.Use("bg");

    var viewNotes = new List<string>();
    string? balanceViewError = null;

    var balanceThread = new Thread(() =>
    {
        try
        {
            var viewPath = Path.Combine(work, $"balview_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(viewPath);

            var company = db.Companies.Add("111111111", "Алфа ЕООД");

            var id = db.Documents.Create("book", "r_b_recovery_dispose", "Проба",
                new Dictionary<string, string> { ["year"] = "2026" }, DocumentOrigin.Manual, company);

            db.Documents.SaveRows(id, "r_b_rd_generated",
            [
                new Dictionary<string, string>
                {
                    ["ewc"] = "160604", ["date_record"] = "10.01.2026", ["amount_ton"] = "10"
                }
            ]);

            db.Documents.SaveRows(id, "r_b_rd_submitted",
            [
                new Dictionary<string, string>
                {
                    ["ewc"] = "160604", ["date_record"] = "10.02.2026", ["amount_ton"] = "4"
                }
            ]);

            db.Documents.SetStatus(id, DocumentStatus.Checked);
            db.Openings.Replace([new Opening(company, 2026, "160604", 5m)]);

            // Тези проверки са за баланса по отчети; по подразбиране е по склада.
            db.Settings.Set("balance.origin", "reports");

            using var page = new BalancePage(service, db);
            _ = page.Handle;

            // 1. Кръстосан изглед, движение: код, начално, дванайсет месеца, общо.
            page.UseView(4, running: false);

            var headers = page.ColumnHeaders();

            viewNotes.Add(headers.Count == 15 ? "" : $"колони: {headers.Count}");
            viewNotes.Add(headers.Contains("Начално") ? "" : "липсва колоната с началното количество");
            viewNotes.Add(headers[^1] == "Общо" ? "" : $"последната колона е „{headers[^1]}“");

            var rows = page.RowTexts();

            viewNotes.Add(rows.Count == 2 ? "" : $"редове: {rows.Count} (очакват се ред и сбор)");
            viewNotes.Add(rows.Count == 2 && rows[0].Contains("160604") ? "" : "кодът не се вижда в реда");
            viewNotes.Add(rows.Count == 2 && rows[^1].StartsWith("Общо") ? "" : "сборният ред не е последен");

            // 2. Салдо: последната колона значи друго и се казва друго.
            page.UseView(4, running: true);

            viewNotes.Add(page.ColumnHeaders()[^1] == "Край"
                ? "" : "салдото не преименува последната колона");

            // 3. Един раздел: началното количество не важи за него.
            page.UseView(0, running: false);

            viewNotes.Add(page.ColumnHeaders().Contains("Начално")
                ? "колоната с началното количество е останала в отделния раздел" : "");
        }
        catch (Exception ex)
        {
            balanceViewError = ex.ToString();
        }
    });

    balanceThread.SetApartmentState(ApartmentState.STA);
    balanceThread.Start();
    balanceThread.Join();

    if (balanceViewError is not null) Console.WriteLine("     " + balanceViewError);

    var viewProblems = viewNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in viewProblems) Console.WriteLine("     " + problem);

    Check("страницата се сглобява и превключва без изключение", balanceViewError is null);
    Check($"направени са всичките {viewNotes.Count} проверки", viewNotes.Count == 8, $"{viewNotes.Count}");
    Check("изгледът стига до колоните на таблицата", viewProblems.Count == 0,
        $"{viewProblems.Count} проблема" + (viewProblems.Count > 0 ? ": " + string.Join(" | ", viewProblems.Take(5)) : ""));
}


// =============================================== Изнасяне на баланса
//
// Изнася се целият обхват — петте изгледа и в двата им вида, — но само за
// отметнатите фирми. Второто е същината: файлът, който отива при друг човек,
// трябва да съдържа точно това, което е било на екрана, и нищо повече.
Console.WriteLine("\n### ТЕСТ - изнасяне на баланса в Excel");
{
    Lang.Use("bg");

    var exportNotes = new List<string>();
    string? exportError = null;

    BalanceExport? together = null;
    BalanceExport? alone = null;
    BalanceExport? nothing = null;
    var empty = -1;
    var choices = new List<string>();

    var exportThread = new Thread(() =>
    {
        try
        {
            var exportPath = Path.Combine(work, $"export_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(exportPath);

            var alpha = db.Companies.Add("111111111", "Алфа ЕООД");
            var beta = db.Companies.Add("222222222", "Бета ООД");

            void Book(string title, long company, (string Node, string Ewc, string Date, string Amount)[] rows)
            {
                var id = db.Documents.Create("book", "r_b_recovery_dispose", title,
                    new Dictionary<string, string> { ["year"] = "2026" }, DocumentOrigin.Manual, company);

                foreach (var group in rows.GroupBy(r => r.Node))
                {
                    db.Documents.SaveRows(id, group.Key,
                    [
                        .. group.Select(r => new Dictionary<string, string>
                        {
                            ["ewc"] = r.Ewc, ["date_record"] = r.Date, ["amount_ton"] = r.Amount
                        })
                    ]);
                }

                db.Documents.SetStatus(id, DocumentStatus.Checked);
            }

            Book("Алфа — 2026", alpha,
            [
                ("r_b_rd_received", "160604", "05.01.2026", "10"),
                ("r_b_rd_generated", "160604", "10.01.2026", "10"),
                ("r_b_rd_submitted", "160604", "10.02.2026", "4")
            ]);

            Book("Бета — 2026", beta,
            [
                ("r_b_rd_generated", "170405", "15.02.2026", "40")
            ]);

            db.Openings.Replace([new Opening(alpha, 2026, "160604", 5m)]);

            // Тези проверки са за баланса по отчети; по подразбиране е по склада.
            db.Settings.Set("balance.origin", "reports");

            using var page = new BalancePage(service, db);
            _ = page.Handle;

            choices.AddRange(page.CompanyChoices());

            // Двете фирми заедно. Страницата тръгва от фирмата в лентата —
            // балансът събира няколко само когато човек ги отметне.
            page.UseAllCompanies(true);
            together = page.BuildExport();

            // Само първата: свалената отметка трябва да се види в изнесеното.
            page.TickCompany(1, ticked: false);
            alone = page.BuildExport();

            // И нито една: непосочен обхват не значи „всички“.
            page.TickCompany(0, ticked: false);
            nothing = page.BuildExport();
            empty = page.RowTexts().Count;
        }
        catch (Exception ex)
        {
            exportError = ex.ToString();
        }
    });

    exportThread.SetApartmentState(ApartmentState.STA);
    exportThread.Start();
    exportThread.Join();

    if (exportError is not null) Console.WriteLine("     " + exportError);

    Check("страницата сглобява изнасяното без изключение", exportError is null);
    Check("в списъка стоят двете фирми", choices.Count == 2, string.Join(" | ", choices));

    if (together is not null && alone is not null)
    {
        // 1. Обхватът пътува с числата.
        Check("изнася се за отметнатите фирми", together.Companies.Count == 2, $"{together.Companies.Count}");
        Check("свалената отметка стеснява обхвата", alone.Companies.Count == 1,
            string.Join(" | ", alone.Companies));
        Check("отчетите също се стесняват", together.Reports.Count == 2 && alone.Reports.Count == 1,
            $"{together.Reports.Count} → {alone.Reports.Count}");

        // 2. Изнасят се всичките пет изгледа, а началното количество важи само
        //    за последния.
        Check("изнасят се петте изгледа", together.Views.Count == 5,
            string.Join(", ", together.Views.Select(v => v.Title)));
        Check("началното количество е само в баланса",
            together.Views.Count(v => v.Opening) == 1 && together.Views[^1].Opening,
            string.Join(", ", together.Views.Where(v => v.Opening).Select(v => v.Title)));

        // 3. Числата са същите, каквито ги смята сметката.
        var balance = together.Views[^1].Table;
        var line = balance.Lines.FirstOrDefault(l => l.Ewc == "160604");

        Check("балансът носи началното количество", line?.Opening == 5m, $"{line?.Opening}");
        Check("движението е образуван минус предаден",
            line?.Movement[0] == 10m && line?.Movement[1] == -4m,
            $"{line?.Movement[0]} / {line?.Movement[1]}");
        Check("чуждата фирма отпада заедно с отметката",
            alone.Views[^1].Table.Lines.All(l => l.Ewc != "170405"),
            string.Join(", ", alone.Views[^1].Table.Lines.Select(l => l.Ewc)));

        // 4. Без нито една отметка не се изнася нищо: непосочен обхват не е
        //    „всички“, а въпрос без отговор.
        Check("без отметка не влиза нито един отчет",
            nothing is not null && nothing.Reports.Count == 0, $"{nothing?.Reports.Count}");

        Check("без отметка таблицата е празна", empty == 0, $"{empty} реда");

        Check("без отметка изгледите остават празни",
            nothing is not null && nothing.Views.All(v => v.Table.IsEmpty));

        // 5. И накрая файлът: записан и прочетен наново.
        var xlsx = Path.Combine(work, $"balance_{Guid.NewGuid():N}.xlsx");
        BalanceWorkbook.Save(xlsx, together);

        using var wb = new XLWorkbook(xlsx);

        var sheets = wb.Worksheets.Select(w => w.Name).ToList();

        Check("файлът има лист за обхвата и по един за всеки изглед",
            sheets.Count == 6 && sheets[0] == BalanceWorkbook.ScopeSheet && sheets.Contains("Баланс"),
            string.Join(", ", sheets));

        static List<string> Texts(IXLWorksheet ws) =>
        [
            .. ws.CellsUsed().Select(c => c.GetFormattedString().Trim()).Where(t => t.Length > 0)
        ];

        var scope = Texts(wb.Worksheet(BalanceWorkbook.ScopeSheet));

        Check("обхватът казва коя година и кои фирми",
            scope.Any(t => t.Contains("2026")) && scope.Any(t => t.Contains("Алфа")) && scope.Any(t => t.Contains("Бета")),
            string.Join(" | ", scope.Take(3)));

        var cross = Texts(wb.Worksheet("Баланс"));

        Check("в листа стоят и движението, и салдото",
            cross.Contains("Движение по месеци") && cross.Contains("Салдо в края на месеца"));

        Check("балансът има колона за началното количество", cross.Contains("Начално"));
        Check("отделният раздел няма такава колона", !Texts(wb.Worksheet("Получен")).Contains("Начално"));

        Check("кодът и наименованието са в отделни колони",
            cross.Contains("160604") && cross.Any(t => t.Contains("Алкални батерии")),
            string.Join(" | ", cross.Where(t => t.StartsWith("16")).Take(2)));

        // Числото се пази като число, а не като текст: иначе Excel не сумира.
        var januaryHeader = wb.Worksheet("Баланс").CellsUsed()
            .FirstOrDefault(c => c.GetFormattedString().Trim() == "яну");

        var codeCell = wb.Worksheet("Баланс").CellsUsed()
            .FirstOrDefault(c => c.GetFormattedString().Trim() == "160604");

        var january = januaryHeader is null || codeCell is null
            ? null
            : wb.Worksheet("Баланс").Cell(codeCell.Address.RowNumber, januaryHeader.Address.ColumnNumber);

        Check("количеството е число, а не текст",
            january is not null && january.DataType == XLDataType.Number && january.GetDouble() == 10d,
            january is null ? "клетката не се намери" : $"{january.DataType}: {january.GetFormattedString()}");
    }
    else
    {
        Check("изнасянето върна нещо", false, "няма изнесено");
    }
}


// =============================================== Оцветяването
//
// Оцветяването не мени нито едно число, но решава кое ще бъде забелязано.
// Затова правилото кой цвят бие кой стои извън екрана: същите цветове трябва
// да излязат и в изнесения файл, а две мнения по въпроса са едно повече.
Console.WriteLine("\n### ТЕСТ - кой цвят на коя клетка");
{
    BalanceEntry[] entries =
    [
        new(BalanceSheet.Generated, "160604", 1, 10m),
        new(BalanceSheet.Generated, "160604", 3, 2m),
        new(BalanceSheet.Submitted, "160604", 4, 30m),
        new(BalanceSheet.Generated, "191202", 2, 1m)
    ];

    // Кодът с начално количество и без нито едно движение е „залежал“.
    var openings = new Dictionary<string, decimal> { ["150101"] = 4m };

    var table = BalanceMath.Build(entries, openings);
    var line = table.Lines.First(l => l.Ewc == "160604");

    var all = new BalanceHighlight
    {
        Marks = BalanceMark.Draft | BalanceMark.Negative | BalanceMark.Peak | BalanceMark.Idle,
        Draft = new HashSet<(string, int)> { ("160604", 1) }
    };

    var movement = new BalanceMarking(table, all, running: false);

    Check("клетка от черновата се отбелязва",
        movement.Tint(line, 1, out _) == BalanceTint.Draft, $"{movement.Tint(line, 1, out _)}");

    Check("отрицателното бие черновата",
        movement.Tint(line, 4, out _) == BalanceTint.Negative, $"{movement.Tint(line, 4, out _)}");

    // Върхът е най-голямото ПО СТОЙНОСТ движение — тук предаденото в април.
    // То обаче е и отрицателно, а отрицателното бие върха; затова се проверява
    // код, при който двете не съвпадат.
    var other = table.Lines.First(l => l.Ewc == "191202");

    Check("върхът на реда се отбелязва",
        movement.Tint(other, 2, out _) == BalanceTint.Peak, $"{movement.Tint(other, 2, out _)}");

    Check("останалите клетки остават без цвят",
        movement.Tint(line, 6, out _) == BalanceTint.None);

    var idle = table.Lines.First(l => l.Ewc == "150101");

    Check("ред само с начално количество е залежал", movement.Idle(idle));
    Check("ред с движение не е залежал", !movement.Idle(line));

    // В салдото докоснатото се носи напред: щом черновата е сменила януари,
    // всяко следващо салдо е друго.
    var running = new BalanceMarking(table, all, running: true);

    Check("в салдото черновата се носи напред",
        running.Tint(line, 2, out _) == BalanceTint.Draft, $"{running.Tint(line, 2, out _)}");

    Check("в салдото няма връх", running.Tint(other, 2, out _) != BalanceTint.Peak);

    // Изключеното оцветяване не оцветява нищо.
    var off = new BalanceMarking(table, BalanceHighlight.Off, running: false);

    Check("изключеното оцветяване мълчи",
        off.Tint(line, 1, out _) == BalanceTint.None && off.Tint(line, 4, out _) == BalanceTint.None &&
        !off.Idle(idle));

    // Топлината се мери спрямо най-голямото в таблицата.
    var heat = new BalanceMarking(table,
        new BalanceHighlight { Marks = BalanceMark.Heat }, running: false);

    var tint = heat.Tint(line, 4, out var strength);

    Check("топлината е спрямо най-голямото",
        tint == BalanceTint.Heat && Math.Abs(strength - 1) < 0.001, $"{tint} {strength:0.###}");
}

// =============================================== Числата на таблото
Console.WriteLine("\n### ТЕСТ - числата зад таблото");
{
    BalanceEntry[] entries =
    [
        new(BalanceSheet.Received, "160604", 1, 30m),
        new(BalanceSheet.Treated, "160604", 1, 30m),
        new(BalanceSheet.Generated, "160604", 1, 20m),
        new(BalanceSheet.Generated, "191202", 5, 8m),
        new(BalanceSheet.Submitted, "160604", 6, 6m)
    ];

    var openings = new Dictionary<string, decimal> { ["160604"] = 5m, ["150101"] = 2m };

    var cross = BalanceMath.Build(entries, openings);

    var sheets = BalanceSheets.All.ToDictionary(
        sheet => sheet,
        sheet => BalanceMath.Build(entries, null, sheet));

    var names = new Dictionary<string, string>
    {
        ["160604"] = "Алкални батерии", ["191202"] = "Черни метали", ["150101"] = "Хартия"
    };

    var stats = BalanceStats.Build(2026, 3, cross, sheets, names);

    Check("началото е сборът на началните", stats.Opening == 7m, $"{stats.Opening}");
    Check("краят е началото плюс движението", stats.Closing == 29m, $"{stats.Closing}");

    Check("сборовете по раздели са както са записани",
        stats.Sheets[BalanceSheet.Received] == 30m &&
        stats.Sheets[BalanceSheet.Generated] == 28m &&
        stats.Sheets[BalanceSheet.Submitted] == 6m,
        $"{stats.Sheets[BalanceSheet.Received]} / {stats.Sheets[BalanceSheet.Generated]} / {stats.Sheets[BalanceSheet.Submitted]}");

    Check("салдото се носи през месеците",
        stats.Running[0] == 27m && stats.Running[11] == 29m,
        $"{stats.Running[0]} … {stats.Running[11]}");

    Check("образуваното е по месеци",
        stats.Generated[0] == 20m && stats.Generated[4] == 8m,
        $"{stats.Generated[0]} / {stats.Generated[4]}");

    Check("най-натовареният месец е януари", stats.BusiestMonth == 1, $"{stats.BusiestMonth}");

    Check("подредбата е по количество, най-голямото първо",
        stats.ByClosing.Count > 0 && stats.ByClosing[0].Ewc == "160604",
        string.Join(", ", stats.ByClosing.Select(s => s.Ewc)));

    Check("името идва от номенклатурата",
        stats.ByClosing[0].Name == "Алкални батерии", stats.ByClosing[0].Name);

    Check("дяловете се сумират до едно",
        Math.Abs(stats.ByClosing.Sum(s => s.Share) - 1) < 0.001,
        $"{stats.ByClosing.Sum(s => s.Share):0.###}");

    Check("залежалото се изброява",
        stats.Idle.Count == 1 && stats.Idle[0].Ewc == "150101",
        string.Join(", ", stats.Idle.Select(s => s.Ewc)));

    Check("няма отрицателни салда", stats.Negative.Count == 0);
    Check("кодовете се броят", stats.Codes == 3, $"{stats.Codes}");
}

// =============================================== Балансът: панел и табло
//
// Тук се проверява, че изборът от панела стига до трите места, които го
// показват: таблицата, таблото и изнесеният файл.
Console.WriteLine("\n### ТЕСТ - панелът стига до таблицата, таблото и изнесеното");
{
    Lang.Use("bg");
    Theme.Use("light");

    var panelNotes = new List<string>();
    string? panelError = null;

    var panelThread = new Thread(() =>
    {
        try
        {
            var panelPath = Path.Combine(work, $"panel_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(panelPath);

            var company = db.Companies.Add("111111111", "Алфа ЕООД");

            long Book(string title, string year, DocumentStatus status,
                (string Node, string Ewc, string Date, string Amount)[] rows)
            {
                var id = db.Documents.Create("book", "r_b_recovery_dispose", title,
                    new Dictionary<string, string> { ["year"] = year }, DocumentOrigin.Manual, company);

                foreach (var group in rows.GroupBy(r => r.Node))
                {
                    db.Documents.SaveRows(id, group.Key,
                    [
                        .. group.Select(r => new Dictionary<string, string>
                        {
                            ["ewc"] = r.Ewc, ["date_record"] = r.Date, ["amount_ton"] = r.Amount
                        })
                    ]);
                }

                if (status != DocumentStatus.Draft) db.Documents.SetStatus(id, status);

                return id;
            }

            // Предходната година: образувани 20, предадени 5 → остават 15.
            Book("Книга 2025", "2025", DocumentStatus.Checked,
            [
                ("r_b_rd_generated", "160604", "10.03.2025", "20"),
                ("r_b_rd_submitted", "160604", "10.09.2025", "5")
            ]);

            Book("Книга 2026", "2026", DocumentStatus.Checked,
            [
                ("r_b_rd_generated", "160604", "10.01.2026", "10"),
                ("r_b_rd_submitted", "160604", "10.02.2026", "30")
            ]);

            Book("Книга 2026 (в работа)", "2026", DocumentStatus.Draft,
            [
                ("r_b_rd_generated", "160604", "10.05.2026", "7")
            ]);

            // Тези проверки са за баланса по отчети; по подразбиране е по склада.
            db.Settings.Set("balance.origin", "reports");

            using var page = new BalancePage(service, db);
            _ = page.Handle;

            page.UseView(4, running: false);

            // 1. Без начални количества салдото тръгва от нула.
            var plain = page.BuildExport().Views[^1].Table;

            panelNotes.Add(plain.Totals.Opening == 0
                ? "" : $"начално без нужда: {plain.Totals.Opening}");

            // 2. Краят на предходната година застава пред януари.
            page.Options.UsePreviousYear(true);

            var carried = page.BuildExport().Views[^1].Table;

            panelNotes.Add(carried.Totals.Opening == 15m
                ? "" : $"пренесеното начално е {carried.Totals.Opening}, а трябва 15");

            page.Options.UsePreviousYear(false);

            // 3. Оцветяването стига до клетките: черновата боядисва май,
            //    отрицателното — февруари.
            page.UseDraft("Книга 2026 (в работа)");

            panelNotes.Add(page.CellColour(0, 5) == Theme.WarnSoft
                ? "" : $"клетката от черновата е {page.CellColour(0, 5).Name}");

            panelNotes.Add(page.CellColour(0, 2) == Theme.ErrorSoft
                ? "" : $"отрицателната клетка е {page.CellColour(0, 2).Name}");

            // 4. Свалената отметка гаси оцветяването.
            page.Options.UseMark(BalanceMark.Draft, false);

            panelNotes.Add(page.CellColour(0, 5).IsEmpty
                ? "" : $"изгасеното оцветяване още боядисва: {page.CellColour(0, 5).Name}");

            page.Options.UseMark(BalanceMark.Draft, true);

            // 5. Отметката за изнасяне решава дали цветовете да заминат.
            page.Options.UseExportMarks(true);
            panelNotes.Add(page.BuildExport().Views[^1].Highlight is not null
                ? "" : "оцветяването не стигна до изнесеното");

            page.Options.UseExportMarks(false);
            panelNotes.Add(page.BuildExport().Views[^1].Highlight is null
                ? "" : "оцветяването замина, макар да е изключено");

            // 6. Таблото показва плочките, отметнати в панела.
            page.UseTab(dashboard: true);

            panelNotes.Add(page.Dashboard.ShownTiles().Count == BalanceTiles.All.Count
                ? "" : $"видими плочки: {page.Dashboard.ShownTiles().Count}");

            page.Options.UseTile(BalanceTile.Heat, false);

            panelNotes.Add(!page.Dashboard.ShownTiles().Contains(BalanceTiles.Key(BalanceTile.Heat))
                ? "" : "изключената плочка още се вижда");

            panelNotes.Add(page.Dashboard.ShownTiles().Contains(BalanceTiles.Key(BalanceTile.Numbers))
                ? "" : "изключването махна и другите плочки");

            // 7. Панелът се сгъва и остава ивица.
            var wide = page.Options.Width;
            page.Options.Collapsed = true;

            panelNotes.Add(page.Options.Width < wide
                ? "" : $"сгънатият панел е {page.Options.Width} при {wide}");

            page.Options.Collapsed = false;

            panelNotes.Add(page.Options.Width == wide ? "" : "панелът не се отваря обратно");
        }
        catch (Exception ex)
        {
            panelError = ex.ToString();
        }
    });

    panelThread.SetApartmentState(ApartmentState.STA);
    panelThread.Start();
    panelThread.Join();

    if (panelError is not null) Console.WriteLine("     " + panelError);

    var panelProblems = panelNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in panelProblems) Console.WriteLine("     " + problem);

    Check("панелът работи без изключение", panelError is null);
    Check($"направени са всичките {panelNotes.Count} проверки", panelNotes.Count == 12, $"{panelNotes.Count}");
    Check("изборът от панела стига навсякъде", panelProblems.Count == 0, $"{panelProblems.Count} проблема");
}


// =============================================== Свиване на лентата
//
// Лентата отляво заема двеста пиксела, а на лаптоп това са три колони от
// баланса. Свита, тя е ивица с икони; изборът се помни, защото иначе всяко
// пускане го връща.
Console.WriteLine("\n### ТЕСТ - лентата отляво се свива и се помни");
{
    Lang.Use("bg");

    var railNotes = new List<string>();
    string? railError = null;

    var railThread = new Thread(() =>
    {
        try
        {
            var railPath = Path.Combine(work, $"rail_{Guid.NewGuid():N}.db");

            int wide;
            int narrow;

            using (var db = Workspace.Open(railPath))
            {
                using var shell = new ShellForm(service, db);
                _ = shell.Handle;

                wide = shell.RailWidth;

                railNotes.Add(!shell.RailCollapsed ? "" : "лентата тръгва свита");

                shell.RailCollapsed = true;
                narrow = shell.RailWidth;

                railNotes.Add(narrow < wide ? "" : $"свитата лента е {narrow} при {wide}");

                // Бутоните остават шест и не се дублират: свиването пипа вида
                // им, не броя им.
                railNotes.Add(shell.NavLabels().Count == 8
                    ? "" : $"бутони след свиване: {shell.NavLabels().Count}");

                railNotes.Add(shell.NavLabels().Distinct().Count() == 8
                    ? "" : "бутоните се дублират след свиване");

                shell.RailCollapsed = false;

                railNotes.Add(shell.RailWidth == wide
                    ? "" : $"отворената лента е {shell.RailWidth} при {wide}");

                shell.RailCollapsed = true;
            }

            // Второ отваряне на същата база: свитото си остава свито.
            using (var db = Workspace.Open(railPath))
            {
                using var shell = new ShellForm(service, db);
                _ = shell.Handle;

                railNotes.Add(shell.RailCollapsed
                    ? "" : "свиването не се помни между пусканията");

                railNotes.Add(shell.RailWidth == narrow
                    ? "" : $"запомнената ширина е {shell.RailWidth}, а трябва {narrow}");

                railNotes.Add(shell.NavLabels().Count == 8
                    ? "" : $"бутони при отваряне със свита лента: {shell.NavLabels().Count}");
            }
        }
        catch (Exception ex)
        {
            railError = ex.ToString();
        }
    });

    railThread.SetApartmentState(ApartmentState.STA);
    railThread.Start();
    railThread.Join();

    if (railError is not null) Console.WriteLine("     " + railError);

    var railProblems = railNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in railProblems) Console.WriteLine("     " + problem);

    Check("свиването минава без изключение", railError is null);
    Check($"направени са всичките {railNotes.Count} проверки", railNotes.Count == 8, $"{railNotes.Count}");
    Check("лентата се свива, отваря и помни", railProblems.Count == 0, $"{railProblems.Count} проблема");
}


// =============================================== Период и сито по код
//
// „Второто тримесечие“ не е отрязване на колони: началното количество на
// периода е салдото ПРЕДИ първия му месец. Иначе всяко тримесечие тръгва от
// нула и числата в него са верни само сами за себе си.
Console.WriteLine("\n### ТЕСТ - балансът се свежда до период и до един код");
{
    BalanceEntry[] entries =
    [
        new(BalanceSheet.Generated, "160604", 1, 10m),
        new(BalanceSheet.Generated, "160604", 5, 4m),
        new(BalanceSheet.Submitted, "160604", 6, 3m),
        new(BalanceSheet.Generated, "191202", 2, 7m)
    ];

    var openings = new Dictionary<string, decimal> { ["160604"] = 5m };
    var year = BalanceMath.Build(entries, openings);

    Check("цялата година се връща непокътната",
        ReferenceEquals(BalanceMath.Window(year, 1, BalanceMath.Months), year));

    var quarter = BalanceMath.Window(year, 4, 6);
    var line = quarter.Lines.First(l => l.Ewc == "160604");

    // 5 начално + 10 през януари = 15, с които тримесечието тръгва.
    Check("периодът тръгва от салдото преди себе си", line.Opening == 15m, $"{line.Opening}");

    Check("движенията извън периода се занулят",
        line.Movement[0] == 0m && line.Movement[4] == 4m && line.Movement[5] == -3m,
        $"{line.Movement[0]} / {line.Movement[4]} / {line.Movement[5]}");

    Check("салдото в периода се смята от неговото начало",
        line.Running[3] == 15m && line.Running[5] == 16m,
        $"{line.Running[3]} / {line.Running[5]}");

    Check("сборът е само за периода", line.Total == 1m, $"{line.Total}");

    // Код без движение в периода остава заради салдото, което носи в него.
    Check("кодът със салдо, но без движение, остава",
        quarter.Lines.Any(l => l.Ewc == "191202" && l.Opening == 7m && l.Total == 0m));

    // Един месец: април няма нищо, но салдото си стои.
    var april = BalanceMath.Window(year, 4, 4);

    Check("месец без движение показва салдото си",
        april.Totals.Running[3] == 22m, $"{april.Totals.Running[3]}");

    // Ситото по код.
    var one = BalanceMath.Only(year, "160604");

    Check("ситото оставя един ред", one.Lines.Count == 1 && one.Lines[0].Ewc == "160604");
    Check("сборът при един ред е самият ред", one.Totals.Closing == one.Lines[0].Closing);
    Check("непознат код дава празна таблица", BalanceMath.Only(year, "999999").Lines.Count == 0);
    Check("празно сито не пипа нищо", ReferenceEquals(BalanceMath.Only(year, ""), year));
}

// =============================================== Периодът стига до екрана
Console.WriteLine("\n### ТЕСТ - периодът и кодът стигат до таблицата и изнесеното");
{
    Lang.Use("bg");

    var pickNotes = new List<string>();
    string? pickError = null;

    var pickThread = new Thread(() =>
    {
        try
        {
            var pickPath = Path.Combine(work, $"pick_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(pickPath);

            var company = db.Companies.Add("111111111", "Алфа ЕООД");

            var id = db.Documents.Create("book", "r_b_recovery_dispose", "Книга 2026",
                new Dictionary<string, string> { ["year"] = "2026" }, DocumentOrigin.Manual, company);

            db.Documents.SaveRows(id, "r_b_rd_generated",
            [
                new Dictionary<string, string>
                { ["ewc"] = "160604", ["date_record"] = "10.01.2026", ["amount_ton"] = "10" },

                new Dictionary<string, string>
                { ["ewc"] = "191202", ["date_record"] = "10.05.2026", ["amount_ton"] = "6" }
            ]);

            db.Documents.SetStatus(id, DocumentStatus.Checked);

            // Тези проверки са за баланса по отчети; по подразбиране е по склада.
            db.Settings.Set("balance.origin", "reports");

            using var page = new BalancePage(service, db);
            _ = page.Handle;

            page.UseView(4, running: false);

            // 1. Цялата година: колона за кода, начално, дванайсет месеца, сбор.
            pickNotes.Add(page.ColumnHeaders().Count == 15
                ? "" : $"колони за годината: {page.ColumnHeaders().Count}");

            // 2. Тримесечие: остават само неговите месеци.
            page.UsePeriod(4, 6);

            var headers = page.ColumnHeaders();

            pickNotes.Add(headers.Count == 6
                ? "" : $"колони за тримесечие: {headers.Count}");

            pickNotes.Add(headers.Contains("апр") && headers.Contains("юни") && !headers.Contains("яну")
                ? "" : "колоните не са на периода: " + string.Join(", ", headers));

            // Януарското количество не изчезва — то е салдото, с което периодът
            // тръгва, и стои в колоната „Начално“.
            var rows = page.RowTexts();

            pickNotes.Add(rows.Any(r => r.Contains("160604") && r.Contains("10"))
                ? "" : "началното салдо на периода не се вижда: " + string.Join(" / ", rows));

            page.UsePeriod(1, 12);

            // 3. Ситото по код оставя един ред и маха сборния.
            page.UseCode("160604");

            pickNotes.Add(page.Code == "160604" ? "" : "кодът не се запомня");

            pickNotes.Add(page.RowTexts().Count == 1
                ? "" : $"редове при сито по код: {page.RowTexts().Count}");

            // 4. Изнесеното носи същия обхват.
            page.UsePeriod(4, 6);

            var export = page.BuildExport();

            pickNotes.Add(export is { From: 4, To: 6, Code: "160604" }
                ? "" : $"изнесеният обхват е {export.From}–{export.To} „{export.Code}“");

            var file = Path.Combine(work, $"period_{Guid.NewGuid():N}.xlsx");
            BalanceWorkbook.Save(file, export);

            using var wb = new XLWorkbook(file);
            var sheet = wb.Worksheet("Баланс");

            var texts = sheet.CellsUsed().Select(c => c.GetFormattedString().Trim()).ToList();

            pickNotes.Add(texts.Contains("апр") && !texts.Contains("яну")
                ? "" : "изнесеният файл носи чужди месеци");

            pickNotes.Add(wb.Worksheet(BalanceWorkbook.ScopeSheet).CellsUsed()
                .Any(c => c.GetFormattedString().Contains("160604"))
                ? "" : "обхватът не казва по кой код е сведено");

            // 5. Махнатото сито връща останалите редове.
            page.UseCode(string.Empty);
            page.UsePeriod(1, 12);

            pickNotes.Add(page.RowTexts().Count == 3
                ? "" : $"редове след махане на ситото: {page.RowTexts().Count}");

            // 6. Щракнатото по таблото стига до таблицата: код води до реда му,
            //    месец — до период от един месец.
            page.UseTab(dashboard: true);
            page.Dashboard.RaiseCodePicked("191202");

            pickNotes.Add(page.Code == "191202" && page.RowTexts().Count == 1
                ? "" : $"щракнатият код дава {page.RowTexts().Count} реда при „{page.Code}“");

            page.UseCode(string.Empty);
            page.Dashboard.RaiseMonthPicked(5);

            pickNotes.Add(page.ColumnHeaders().Count == 4 && page.ColumnHeaders().Contains("май")
                ? "" : "щракнатият месец не стеснява периода: " + string.Join(", ", page.ColumnHeaders()));

            // Повторното щракване по същия месец връща цялата година.
            page.Dashboard.RaiseMonthPicked(5);

            pickNotes.Add(page.ColumnHeaders().Count == 15
                ? "" : $"повторното щракване не връща годината: {page.ColumnHeaders().Count} колони");
        }
        catch (Exception ex)
        {
            pickError = ex.ToString();
        }
    });

    pickThread.SetApartmentState(ApartmentState.STA);
    pickThread.Start();
    pickThread.Join();

    if (pickError is not null) Console.WriteLine("     " + pickError);

    var pickProblems = pickNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in pickProblems) Console.WriteLine("     " + problem);

    Check("периодът минава без изключение", pickError is null);
    Check($"направени са всичките {pickNotes.Count} проверки", pickNotes.Count == 13, $"{pickNotes.Count}");
    Check("периодът и кодът стигат навсякъде", pickProblems.Count == 0, $"{pickProblems.Count} проблема");
}


// =============================================== Покритие на периода
//
// Балансът събира редове от няколко книги и мълчи, ако две от тях покриват
// един и същ месец: количествата в него се броят два пъти, а числото изглежда
// наред. Затова проверката е отделна — и затова е тук, без прозорец.
Console.WriteLine("\n### ТЕСТ - пропуски и застъпвания между отчетите");
{
    static ReportPeriod Book(long id, string title, string company, string from, string to) =>
        new(id, title, company, "Проверен", false,
            BalanceCoverage.Date(from), BalanceCoverage.Date(to), null, null, 0);

    // Точно покритие: три книги, месец до месец.
    ReportPeriod[] tidy =
    [
        Book(1, "Първо", "Алфа", "01.01.2026", "30.04.2026"),
        Book(2, "Второ", "Алфа", "01.05.2026", "31.08.2026"),
        Book(3, "Трето", "Алфа", "01.09.2026", "31.12.2026")
    ];

    Check("покритата година мълчи", BalanceCoverage.Check(tidy, 2026).Count == 0,
        string.Join(" | ", BalanceCoverage.Check(tidy, 2026).Select(i => i.Text)));

    // Пропуск между втората и третата.
    ReportPeriod[] gapped =
    [
        Book(1, "Първо", "Алфа", "01.01.2026", "31.05.2026"),
        Book(2, "Второ", "Алфа", "10.06.2026", "31.12.2026")
    ];

    var gaps = BalanceCoverage.Check(gapped, 2026);

    Check("пропускът се намира",
        gaps.Count == 1 && gaps[0].Kind == CoverageProblem.Gap, string.Join(" | ", gaps.Select(i => i.Text)));

    Check("пропускът сочи двата отчета около себе си",
        gaps[0].First == 1 && gaps[0].Second == 2);

    Check("пропускът брои дните", gaps[0].Text.Contains("9 дни"), gaps[0].Text);

    // Застъпване.
    ReportPeriod[] doubled =
    [
        Book(1, "Първо", "Алфа", "01.01.2026", "15.07.2026"),
        Book(2, "Второ", "Алфа", "01.07.2026", "31.12.2026")
    ];

    var overlaps = BalanceCoverage.Check(doubled, 2026);

    Check("застъпването се намира",
        overlaps.Count == 1 && overlaps[0].Kind == CoverageProblem.Overlap,
        string.Join(" | ", overlaps.Select(i => i.Text)));

    Check("застъпването брои дните", overlaps[0].Text.Contains("15 дни"), overlaps[0].Text);

    // Непокрито в началото и в края на годината.
    ReportPeriod[] middle = [Book(1, "Само средата", "Алфа", "01.03.2026", "31.10.2026")];
    var edges = BalanceCoverage.Check(middle, 2026);

    Check("непокритото в двата края е пропуск",
        edges.Count == 2 && edges.All(i => i.Kind == CoverageProblem.Gap),
        string.Join(" | ", edges.Select(i => i.Text)));

    // Различните фирми не се застъпват помежду си.
    ReportPeriod[] two =
    [
        Book(1, "Алфа цялата", "Алфа", "01.01.2026", "31.12.2026"),
        Book(2, "Бета цялата", "Бета", "01.01.2026", "31.12.2026")
    ];

    Check("две фирми с еднакъв период не се застъпват",
        BalanceCoverage.Check(two, 2026).Count == 0);

    // Отчет без период и отчет с обърнат период.
    ReportPeriod[] broken =
    [
        new(1, "Без период", "Алфа", "Чернова", true, null, null, null, null, 0),
        Book(2, "Обърнат", "Алфа", "31.12.2026", "01.01.2026")
    ];

    var kinds = BalanceCoverage.Check(broken, 2026).Select(i => i.Kind).ToList();

    Check("липсващият период се отбелязва", kinds.Contains(CoverageProblem.Unknown));
    Check("обърнатият период се отбелязва", kinds.Contains(CoverageProblem.Backwards));

    // Стесненият период мери своето, не цялата година.
    ReportPeriod[] quarter = [Book(1, "Второ тримесечие", "Алфа", "01.04.2026", "30.06.2026")];

    Check("при стеснен период краищата не са пропуск",
        BalanceCoverage.Check(quarter, 2026, 4, 6).Count == 0,
        string.Join(" | ", BalanceCoverage.Check(quarter, 2026, 4, 6).Select(i => i.Text)));

    Check("същите отчети за цялата година дават два пропуска",
        BalanceCoverage.Check(quarter, 2026).Count == 2);
}

// =============================================== Периодите идват от отчета
Console.WriteLine("\n### ТЕСТ - обявеният период и датите в данните се четат заедно");
{
    var coverPath = Path.Combine(work, $"cover_{Guid.NewGuid():N}.db");
    using var db = Workspace.Open(coverPath);

    var company = db.Companies.Add("111111111", "Алфа ЕООД");

    var id = db.Documents.Create("book", "r_b_recovery_dispose", "Книга",
        new Dictionary<string, string>
        {
            ["year"] = "2026",
            ["period_start"] = "01.02.2026",
            ["period_end"] = "28.02.2026"
        },
        DocumentOrigin.Manual, company);

    db.Documents.SaveRows(id, "r_b_rd_generated",
    [
        new Dictionary<string, string>
        { ["ewc"] = "160604", ["date_record"] = "10.02.2026", ["amount_ton"] = "1" },

        // Нарочно извън периода: не е грешка, но трябва да се вижда.
        new Dictionary<string, string>
        { ["ewc"] = "160604", ["date_record"] = "03.03.2026", ["amount_ton"] = "2" }
    ]);

    db.Documents.SetStatus(id, DocumentStatus.Checked);

    var source = new BalanceSource(db.Documents);
    var periods = source.Periods(db.Documents.List(), _ => "Алфа ЕООД");

    Check("периодът се чете от заглавните стойности",
        periods.Count == 1 && periods[0].From == new DateTime(2026, 2, 1) && periods[0].To == new DateTime(2026, 2, 28),
        periods.Count == 1 ? $"{periods[0].From:dd.MM.yyyy} – {periods[0].To:dd.MM.yyyy}" : "-");

    Check("датите в данните се четат отделно",
        periods[0].FirstRow == new DateTime(2026, 2, 10) && periods[0].LastRow == new DateTime(2026, 3, 3),
        $"{periods[0].FirstRow:dd.MM.yyyy} – {periods[0].LastRow:dd.MM.yyyy}");

    Check("редовете извън периода се броят", periods[0].Outside == 1, $"{periods[0].Outside}");
    Check("редовете се броят", periods[0].Rows == 2, $"{periods[0].Rows}");
    Check("отчет с период не е „без период“", !periods[0].Unknown);
}


// =============================================== Прегледът на началните количества
//
// Количествата се внасят от файл и после никой не помни какво е имало в него.
// Затова прозорецът показва САМО тях — и до всяко колко е мръднало по този код
// през годината му: начално количество без нито едно движение е или залежал
// отпадък, или ред, въведен без да е забелязан.
Console.WriteLine("\n### ТЕСТ - началните количества се преглеждат и поправят отделно");
{
    Lang.Use("bg");
    Theme.Use("light");

    var openNotes = new List<string>();
    string? openError = null;

    var openThread = new Thread(() =>
    {
        try
        {
            var openPath = Path.Combine(work, $"openings_ui_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(openPath);

            var alpha = db.Companies.Add("111111111", "Алфа ЕООД");
            var beta = db.Companies.Add("222222222", "Бета ООД");

            var id = db.Documents.Create("book", "r_b_recovery_dispose", "Книга",
                new Dictionary<string, string> { ["year"] = "2026" }, DocumentOrigin.Manual, alpha);

            db.Documents.SaveRows(id, "r_b_rd_generated",
            [
                new Dictionary<string, string>
                { ["ewc"] = "160604", ["date_record"] = "10.02.2026", ["amount_ton"] = "9" }
            ]);

            db.Documents.SetStatus(id, DocumentStatus.Checked);

            db.Openings.Replace(
            [
                new Opening(alpha, 2026, "160604", 5m),

                // Код, по който през годината не е мръднало нищо.
                new Opening(alpha, 2026, "150110", 2m),
                new Opening(beta, 2026, "170405", 8m)
            ]);

            var source = new BalanceSource(db.Documents);

            IReadOnlyDictionary<string, decimal>? Moved(long company, int year)
            {
                var chosen = BalanceSource.Eligible(db.Documents.List(), [company]);
                if (chosen.Count == 0) return null;

                var table = BalanceMath.Build(source.Read(chosen, year));

                return table.Lines.ToDictionary(l => l.Ewc, l => l.Total, StringComparer.Ordinal);
            }

            var names = new Dictionary<string, string>
            {
                ["160604"] = "Алкални батерии", ["150110"] = "Опаковки", ["170405"] = "Чугун"
            };

            using var dialog = new OpeningsDialog(db.Openings.All(), db.Companies.List(), names, Moved);
            _ = dialog.Handle;

            // 1. Показват се само началните количества — по едно на ред.
            openNotes.Add(dialog.RowCount == 3 ? "" : $"редове: {dialog.RowCount}");

            openNotes.Add(dialog.Total == 15m ? "" : $"сборът е {dialog.Total}");

            // 2. Кодът без движение се отбелязва, а другият — не.
            openNotes.Add(dialog.Alone == 1 ? "" : $"без движение: {dialog.Alone}");

            openNotes.Add(dialog.MovementOf("160604") == 9m
                ? "" : $"движението по 160604 е {dialog.MovementOf("160604")}");

            openNotes.Add(dialog.MovementOf("150110") == 0m
                ? "" : $"движението по 150110 е {dialog.MovementOf("150110")}");

            // 3. Поправеното се чете обратно.
            openNotes.Add(dialog.PutAmount("150110", "0") ? "" : "редът не се намери за поправка");

            openNotes.Add(dialog.Accept() ? "" : "записът не мина");

            var left = dialog.Result;

            // Нула маха реда — така се маха количество, въведено по погрешка.
            openNotes.Add(left.Count == 3 && left.Any(o => o.Ewc == "150110" && o.Amount == 0m)
                ? "" : $"след поправката остават {left.Count} реда");

            // 4. Сбъркан ред спира записа, вместо да се запише наполовина.
            using var broken = new OpeningsDialog(db.Openings.All(), db.Companies.List(), names, Moved);
            _ = broken.Handle;

            broken.PutAmount("160604", "не е число");

            openNotes.Add(!broken.Accept() ? "" : "записът мина със сбъркано количество");
        }
        catch (Exception ex)
        {
            openError = ex.ToString();
        }
    });

    openThread.SetApartmentState(ApartmentState.STA);
    openThread.Start();
    openThread.Join();

    if (openError is not null) Console.WriteLine("     " + openError);

    var openProblems = openNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in openProblems) Console.WriteLine("     " + problem);

    Check("прегледът минава без изключение", openError is null);
    Check($"направени са всичките {openNotes.Count} проверки", openNotes.Count == 9, $"{openNotes.Count}");
    Check("началните количества се четат и поправят", openProblems.Count == 0, $"{openProblems.Count} проблема");
}

// =============================================== Отметките на фирмите
Console.WriteLine("\n### ТЕСТ - всички и нито една фирма");
{
    var tickNotes = new List<string>();
    string? tickError = null;

    var tickThread = new Thread(() =>
    {
        try
        {
            var tickPath = Path.Combine(work, $"ticks_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(tickPath);

            db.Companies.Add("111111111", "Алфа ЕООД");
            db.Companies.Add("222222222", "Бета ООД");
            db.Companies.Add("333333333", "Гама АД");

            // Тези проверки са за баланса по отчети; по подразбиране е по склада.
            db.Settings.Set("balance.origin", "reports");

            using var page = new BalancePage(service, db);
            _ = page.Handle;

            tickNotes.Add(page.CompanyChoices().Count == 3
                ? "" : $"фирми в списъка: {page.CompanyChoices().Count}");

            page.UseAllCompanies(false);

            tickNotes.Add(page.TickedCompanies == 0
                ? "" : $"след „нито една“ остават {page.TickedCompanies}");

            page.UseAllCompanies(true);

            tickNotes.Add(page.TickedCompanies == 3
                ? "" : $"след „всички“ са отметнати {page.TickedCompanies}");
        }
        catch (Exception ex)
        {
            tickError = ex.ToString();
        }
    });

    tickThread.SetApartmentState(ApartmentState.STA);
    tickThread.Start();
    tickThread.Join();

    if (tickError is not null) Console.WriteLine("     " + tickError);

    var tickProblems = tickNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in tickProblems) Console.WriteLine("     " + problem);

    Check("отметките минават без изключение", tickError is null);
    Check($"направени са всичките {tickNotes.Count} проверки", tickNotes.Count == 3, $"{tickNotes.Count}");
    Check("двете думи вдигат и свалят всички отметки", tickProblems.Count == 0, $"{tickProblems.Count} проблема");
}

// =============================== кратките списъци
Console.WriteLine("\n### ТЕСТ - кратките списъци стесняват предлаганото, не разбираното");
{
    var listBook = service.GetSchema("book");
    var listReport = listBook.FindReport("r_b_recovery_dispose")!;

    // Едно и също поле в няколко таблици на един отчет — точно затова има ниво „лист“.
    var listSheets = listReport.SelfAndDescendants()
        .Where(n => n.Attributes.Any(a => a.Name == "ewc"))
        .ToList();

    var listFirst = listSheets[0];
    var listSecond = listSheets[1];
    var listColumn = listFirst.Attributes.First(a => a.Name == "ewc");

    var listAll = listColumn.Domain.Options;
    var listThree = listAll.Take(3).Select(o => o.Value).ToList();
    var listTwo = listAll.Skip(10).Take(2).Select(o => o.Value).ToList();

    Check("едно поле стои в поне две таблици на отчета", listSheets.Count >= 2, $"{listSheets.Count}");

    // 1. Без списък менюто е пълното — и е СЪЩИЯТ списък, а не негово копие.
    Check("без списък менюто е пълното",
        ReferenceEquals(ValueLimits.None.Offer(listReport, listFirst, listColumn), listAll));

    // 2. Списък за всички отчети свива менюто.
    var listWide = new ValueLimits([new ValueListEntry(string.Empty, string.Empty, "ewc", listThree)]);
    var listWidened = listWide.Offer(listReport, listFirst, listColumn);

    Check("списък за всички отчети свива менюто", listWidened.Count == 3, $"{listWidened.Count}");
    Check("свитото меню пази реда на схемата",
        listWidened.Select(o => o.Value).SequenceEqual(listThree));

    // 3. По-точното печели и НЕ се слива с по-широкото.
    var listMixed = new ValueLimits([
        new ValueListEntry(string.Empty, string.Empty, "ewc", listThree),
        new ValueListEntry(listReport.ElementName, listFirst.ElementName, "ewc", listTwo)
    ]);

    var listSheetLevel = listMixed.Offer(listReport, listFirst, listColumn);

    Check("листът печели над всички отчети", listSheetLevel.Count == 2, $"{listSheetLevel.Count}");
    Check("нивата не се сливат",
        !listSheetLevel.Any(o => listThree.Contains(o.Value)),
        string.Join(", ", listSheetLevel.Select(o => o.Value)));

    // 4. Съседната таблица няма свой списък и пада на по-широкия.
    Check("друг лист пада на по-широкия списък",
        listMixed.Offer(listReport, listSecond, listColumn).Count == 3);

    // 5. Друго поле не се засяга.
    var listOther = listFirst.Attributes.FirstOrDefault(a => a.Domain.IsEnum && a.Name != "ewc");
    Check("списък за едно поле не пипа другите",
        listOther is null || ReferenceEquals(
            listWide.Offer(listReport, listFirst, listOther), listOther.Domain.Options));

    // 6. Вече попълненото остава в менюто, дори да е извън списъка.
    var listOutsider = listAll[50].Value;
    var listWithUse = listWide.Offer(listReport, listFirst, listColumn, [listOutsider, string.Empty]);

    Check("вече попълнена стойност извън списъка остава",
        listWithUse.Count == 4 && listWithUse.Any(o => o.Value == listOutsider),
        $"{listWithUse.Count}");

    // 7. Списък само с непознати кодове връща пълното меню — по-добре дълго,
    //    отколкото поле, което не може да се попълни.
    var listStale = new ValueLimits([new ValueListEntry(string.Empty, string.Empty, "ewc", ["няма такъв"])]);
    Check("списък само с непознати кодове връща пълното меню",
        ReferenceEquals(listStale.Offer(listReport, listFirst, listColumn), listAll));

    // 8. Празният списък не е ограничение, а недовършена настройка.
    var listEmpty = new ValueLimits([new ValueListEntry(string.Empty, string.Empty, "ewc", [])]);
    Check("празен списък не ограничава", listEmpty.IsEmpty);

    // 9. Един и същ обхват връща един и същ списък: Editor/Codes държи по едно
    //    готово падащо меню за всеки списък и го намира по самия него.
    Check("един и същ обхват връща един и същ списък",
        ReferenceEquals(
            listWide.Offer(listReport, listFirst, listColumn),
            listWide.Offer(listReport, listFirst, listColumn)));

    Check("списъкът се отчита като ограничение", listWide.Restricts(listReport, listFirst, listColumn));
    Check("непипнатото поле не се отчита за ограничено",
        !ValueLimits.None.Restricts(listReport, listFirst, listColumn));
}

Console.WriteLine("\n### ТЕСТ - списъците се пазят по фирма и по вид отчетност");
{
    var storePath = Path.Combine(work, $"lists_{Guid.NewGuid():N}.db");
    using var listDb = Workspace.Open(storePath);

    var alpha = listDb.Companies.Add("900000011", "Алфа ПРОБА");
    var beta = listDb.Companies.Add("900000012", "Бета ПРОБА");

    listDb.Lists.Save(alpha, "book", string.Empty, string.Empty, "ewc", ["150101", "150102"]);
    listDb.Lists.Save(beta, "book", string.Empty, string.Empty, "ewc", ["150101"]);

    Check("записват се по един на фирма", listDb.Lists.List().Count == 2, $"{listDb.Lists.List().Count}");

    // Втори списък за същия обхват замества първия, не се трупа до него.
    listDb.Lists.Save(alpha, "book", string.Empty, string.Empty, "ewc", ["150101", "150102", "150103"]);

    Check("същият обхват замества, не трупа", listDb.Lists.List().Count == 2, $"{listDb.Lists.List().Count}");
    Check("замяната пази новите стойности", listDb.Lists.List(alpha).Single().Allowed.Count == 3);

    Check("без фирма няма ограничение", listDb.Lists.LimitsFor(null, "book").IsEmpty);
    Check("друг вид отчетност не се засяга", listDb.Lists.LimitsFor(alpha, "year").IsEmpty);
    Check("списъкът на фирмата се намира", !listDb.Lists.LimitsFor(alpha, "book").IsEmpty);

    // Изключването е между промяната и изтриването: отметките се пазят.
    var saved = listDb.Lists.List(alpha).Single();
    listDb.Lists.SetEnabled(saved.Id, false);

    Check("изключеният не влиза в ограниченията", listDb.Lists.LimitsFor(alpha, "book").IsEmpty);
    Check("изключеният си пази стойностите", listDb.Lists.Find(saved.Id)!.Allowed.Count == 3);

    listDb.Lists.SetEnabled(saved.Id, true);
    Check("включеният се връща", !listDb.Lists.LimitsFor(alpha, "book").IsEmpty);

    // Изтритата фирма отнася списъците си — те нямат смисъл без нея.
    listDb.Companies.Remove(beta);
    Check("изтритата фирма отнася списъците си", listDb.Lists.List(beta).Count == 0);
    Check("чуждите списъци остават", listDb.Lists.List(alpha).Count == 1);

    Check("обхватът се разчита обратно", listDb.Lists.List(alpha).Single().Scope == ValueListScope.All);

    listDb.Lists.Save(alpha, "book", "r_b_recovery_dispose", "r_b_rd_received", "ewc", ["150101"]);
    Check("нивото лист се разчита обратно",
        listDb.Lists.List(alpha).Any(e => e.Scope == ValueListScope.Sheet));
}

Console.WriteLine("\n### ТЕСТ - краткият списък стига до таблицата в програмата");
{
    var gridNotes = new List<string>();
    string? gridError = null;

    var gridThread = new Thread(() =>
    {
        try
        {
            var gridPath = Path.Combine(work, $"gridlists_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(gridPath);

            var company = db.Companies.Add("900000013", "Гама ПРОБА");

            var docId = db.Documents.Create("book", "r_b_recovery_dispose", "Проба",
                new Dictionary<string, string> { ["year"] = "2026" });
            db.Documents.SetCompany(docId, company);

            var schema = service.GetSchema("book");
            var node = schema.FindReport("r_b_recovery_dispose")!
                .SelfAndDescendants().First(n => n.ElementName == "r_b_rd_received");
            var codes = node.Attributes.First(a => a.Name == "ewc").Domain.Options;

            var few = codes.Take(6).Select(o => o.Value).ToList();
            var outsider = codes[100];

            // 1. Без списък ЕКО кодът е твърде дълъг за меню в клетката.
            using (var before = new EditorForm(service, db, docId))
            {
                _ = before.Handle;

                var sheet = before.Grids.First(g => g.Plan.Node.ElementName == "r_b_rd_received");

                gridNotes.Add(!sheet.IsMenuColumn("ewc")
                    ? "" : "без списък дългият код стои като меню в клетката");
            }

            db.Lists.Save(company, "book", string.Empty, string.Empty, "ewc", few);

            using (var after = new EditorForm(service, db, docId))
            {
                _ = after.Handle;

                var sheet = after.Grids.First(g => g.Plan.Node.ElementName == "r_b_rd_received");

                // 2. Свит под прага, кодът става обикновено падащо меню.
                gridNotes.Add(sheet.OfferedForTest("ewc").Count == 6
                    ? "" : $"менюто предлага {sheet.OfferedForTest("ewc").Count} стойности вместо 6");

                gridNotes.Add(sheet.IsMenuColumn("ewc")
                    ? "" : "свитият код не стана меню в клетката");

                // 3. Числовата колона няма меню — списъкът важи само за изброените.
                gridNotes.Add(sheet.OfferedForTest("amount_ton").Count == 0
                    ? "" : "числова колона получи меню");

                // 4. Вече попълнен код извън списъка остава — иначе екранът
                //    трие чужда или по-стара, но свършена работа.
                sheet.AddRows(1);
                sheet.PutValue(0, "ewc", outsider.Display);

                var offered = sheet.OfferedForTest("ewc");

                gridNotes.Add(offered.Any(o => o.Value == outsider.Value)
                    ? "" : "попълненият извън списъка код изчезна от менюто");

                gridNotes.Add(offered.Count == 7
                    ? "" : $"менюто с попълненото има {offered.Count} стойности вместо 7");

                gridNotes.Add(sheet.Snapshot()[0].GetValueOrDefault("ewc") == outsider.Value
                    ? "" : "попълненият извън списъка код не се пази в отчета");
            }
        }
        catch (Exception ex)
        {
            gridError = ex.ToString();
        }
    });

    gridThread.SetApartmentState(ApartmentState.STA);
    gridThread.Start();
    gridThread.Join();

    if (gridError is not null) Console.WriteLine("     " + gridError);

    var gridProblems = gridNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in gridProblems) Console.WriteLine("     " + problem);

    Check("таблицата се сглобява без изключение", gridError is null);
    Check($"направени са всичките {gridNotes.Count} проверки", gridNotes.Count == 7, $"{gridNotes.Count}");
    Check("краткият списък стига до таблицата", gridProblems.Count == 0, $"{gridProblems.Count} проблема");
}


Console.WriteLine("\n### ТЕСТ - какво спира една стойност и защо липсва");
{
    var askBook = service.GetSchema("book");
    var askReport = askBook.FindReport("r_b_recovery_dispose")!;
    var askSheets = askReport.SelfAndDescendants()
        .Where(n => n.Attributes.Any(a => a.Name == "ewc"))
        .ToList();

    var askNode = askSheets[0];
    var askOther = askSheets[1];
    var askColumn = askNode.Attributes.First(a => a.Name == "ewc");

    var inside = askColumn.Domain.Options[0].Value;
    var outside = askColumn.Domain.Options[50].Value;

    var askLimits = new ValueLimits([
        new ValueListEntry(string.Empty, string.Empty, "ewc", [inside])
    ]);

    // 1. Позволеното минава, непозволеното — не. Празното не е нарушение:
    //    липсващо задължително поле е грешка на проверката, не на списъка.
    Check("стойност от списъка минава", askLimits.Allows(askReport, askNode, askColumn, inside));
    Check("стойност извън списъка не минава", !askLimits.Allows(askReport, askNode, askColumn, outside));
    Check("празната стойност минава", askLimits.Allows(askReport, askNode, askColumn, ""));
    Check("без списък всичко минава", ValueLimits.None.Allows(askReport, askNode, askColumn, outside));

    // 2. Вече попълненото се ПРЕДЛАГА, но не е позволено — двете са различни
    //    въпроса и точно затова редът се отбелязва, вместо да се чисти.
    Check("предлаганото и позволеното са различни неща",
        askLimits.Offer(askReport, askNode, askColumn, [outside]).Any(o => o.Value == outside) &&
        !askLimits.Allows(askReport, askNode, askColumn, outside));

    // 3. Списък само с непознати кодове не обявява всичко за сгрешено.
    var askStale = new ValueLimits([new ValueListEntry(string.Empty, string.Empty, "ewc", ["няма такъв"])]);
    Check("изгнил списък не отбелязва редове", askStale.Allows(askReport, askNode, askColumn, outside));

    // 4. Разчитането на въведеното: редове, запетаи, интервали.
    var parsed = ValueSearch.Split($" {inside} , {outside}\n{inside} ");
    Check("въведените стойности се разчитат и не се повтарят",
        parsed.Count == 2 && parsed.Contains(inside) && parsed.Contains(outside),
        string.Join("|", parsed));
    Check("празно въведено дава празен списък", ValueSearch.Split("   ").Count == 0);

    // 5. Обратното търсене казва кой обхват спира стойността.
    var explained = ValueSearch.Explain([askBook], askLimits, outside);

    Check("спряната стойност се намира",
        explained.Any(f => f.Verdict == ValueVerdict.Blocked), $"{explained.Count} реда");
    Check("спиращият обхват е „всички отчети“",
        explained.Where(f => f.Verdict == ValueVerdict.Blocked)
                 .All(f => f.Report.Length == 0 && f.Node.Length == 0));
    Check("предлаганата стойност не се обявява за спряна",
        ValueSearch.Explain([askBook], askLimits, inside)
                   .All(f => f.Verdict != ValueVerdict.Blocked));

    // 6. Списък само за един лист спира там и никъде другаде.
    var askSheetOnly = new ValueLimits([
        new ValueListEntry(askReport.ElementName, askNode.ElementName, "ewc", [inside])
    ]);

    Check("листът спира само своята таблица",
        !askSheetOnly.Allows(askReport, askNode, askColumn, outside) &&
        askSheetOnly.Allows(askReport, askOther, askColumn, outside));

    var askMixed = ValueSearch.Explain([askBook], askSheetOnly, outside);

    Check("едно поле дава и двата отговора, когато е нагласено така",
        askMixed.Any(f => f.Verdict == ValueVerdict.Blocked) &&
        askMixed.Any(f => f.Verdict == ValueVerdict.Offered),
        string.Join(", ", askMixed.Select(f => f.Verdict.ToString())));

    Check("спиращият обхват сочи точния лист",
        askMixed.Where(f => f.Verdict == ValueVerdict.Blocked)
                .All(f => f.Node == askNode.ElementName));

    // 7. Непозната стойност се казва като непозната, а не като спряна.
    var nonsense = ValueSearch.Explain(service.Schemas, askLimits, "ZZZ999");
    Check("непозната стойност се обявява за непозната",
        nonsense.Count == 1 && nonsense[0].Verdict == ValueVerdict.Unknown);
}

Console.WriteLine("\n### ТЕСТ - смяната на фирмата отбелязва редовете и спира проверката");
{
    var markNotes = new List<string>();
    string? markError = null;

    var markThread = new Thread(() =>
    {
        try
        {
            var markPath = Path.Combine(work, $"marks_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(markPath);

            var loose = db.Companies.Add("900000021", "Широка ПРОБА");
            var strict = db.Companies.Add("900000022", "Тясна ПРОБА");

            var schema = service.GetSchema("book");
            var node = schema.FindReport("r_b_recovery_dispose")!
                .SelfAndDescendants().First(n => n.ElementName == "r_b_rd_received");
            var codes = node.Attributes.First(a => a.Name == "ewc").Domain.Options;

            // Тясната фирма ползва първите три кода. Работата е свършена с
            // четвъртия — законен по схема, но извън нейния списък.
            db.Lists.Save(strict, "book", string.Empty, string.Empty, "ewc",
                [.. codes.Take(3).Select(o => o.Value)]);

            var docId = db.Documents.Create("book", "r_b_recovery_dispose", "Проба",
                new Dictionary<string, string> { ["year"] = "2026" });
            db.Documents.SetCompany(docId, loose);

            using var form = new EditorForm(service, db, docId);
            _ = form.Handle;

            var sheet = form.Grids.First(g => g.Plan.Node.ElementName == "r_b_rd_received");

            sheet.PutValue(0, "ewc", codes[0].Display);
            sheet.PutValue(1, "ewc", codes[10].Display);
            sheet.PutValue(2, "ewc", codes[11].Display);

            markNotes.Add(sheet.BlockedRows == 0
                ? "" : $"без списък има {sheet.BlockedRows} отбелязани реда");

            // Смяната на фирмата: нищо не се трие, но два реда излизат извън
            // списъка на новата.
            db.Documents.SetCompany(docId, strict);
            sheet.ApplyLimits(db.Lists.LimitsFor(strict, "book"));

            markNotes.Add(sheet.BlockedRows == 2
                ? "" : $"отбелязани са {sheet.BlockedRows} реда вместо 2");

            markNotes.Add(sheet.Snapshot().Count == 3
                ? "" : $"след смяната остават {sheet.Snapshot().Count} реда вместо 3");

            markNotes.Add(sheet.Snapshot()[1].GetValueOrDefault("ewc") == codes[10].Value
                ? "" : "отбелязаната стойност е изчистена");

            markNotes.Add(form.BlockedRowCount == 2
                ? "" : $"отчетът брои {form.BlockedRowCount} отбелязани реда вместо 2");

            // Проверката спира и не вдига състоянието на „проверен“.
            markNotes.Add(!form.CheckForTest() ? "" : "проверката мина при отбелязани редове");

            markNotes.Add(db.Documents.Find(docId)!.Status == DocumentStatus.Draft
                ? "" : $"състоянието е {db.Documents.Find(docId)!.Status}");

            // Ситото показва точно тях.
            sheet.ToggleOnlyBlocked();

            markNotes.Add(sheet.VisibleRowCount == 2
                ? "" : $"„само отбелязаните“ показва {sheet.VisibleRowCount} реда");

            sheet.ToggleOnlyBlocked();

            markNotes.Add(sheet.VisibleRowCount == 3
                ? "" : $"след изключване се виждат {sheet.VisibleRowCount} реда");

            // Поправеният ред престава да е отбелязан.
            sheet.PutValue(1, "ewc", codes[1].Display);
            sheet.PutValue(2, "ewc", codes[2].Display);

            markNotes.Add(sheet.BlockedRows == 0
                ? "" : $"след поправяне остават {sheet.BlockedRows} отбелязани реда");
        }
        catch (Exception ex)
        {
            markError = ex.ToString();
        }
    });

    markThread.SetApartmentState(ApartmentState.STA);
    markThread.Start();
    markThread.Join();

    if (markError is not null) Console.WriteLine("     " + markError);

    var markProblems = markNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in markProblems) Console.WriteLine("     " + problem);

    Check("смяната на фирмата минава без изключение", markError is null);
    Check($"направени са всичките {markNotes.Count} проверки", markNotes.Count == 10, $"{markNotes.Count}");
    Check("отбелязването спира проверката и се маха с поправянето", markProblems.Count == 0,
        $"{markProblems.Count} проблема");
}

Console.WriteLine("\n### ТЕСТ - редът „нов запис“ приема стойност отвън");
{
    var newRowNotes = new List<string>();
    string? newRowError = null;

    var newRowThread = new Thread(() =>
    {
        try
        {
            var newRowPath = Path.Combine(work, $"newrow_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(newRowPath);

            var docId = db.Documents.Create("book", "r_b_recovery_dispose", "Проба",
                new Dictionary<string, string> { ["year"] = "2026" });

            using var form = new EditorForm(service, db, docId);
            _ = form.Handle;

            var sheet = form.Grids.First(g => g.Plan.Node.ElementName == "r_b_rd_received");

            // Празен отчет: единственият ред е „нов запис“. Точно него натиска
            // човек пръв — и точно върху него търсачката не се отваряше.
            newRowNotes.Add(sheet.DataRowCount == 0
                ? "" : $"празният отчет тръгва с {sheet.DataRowCount} реда");

            var made = sheet.Materialize(0);

            newRowNotes.Add(made == 0 ? "" : $"новият ред получи номер {made}");
            newRowNotes.Add(sheet.DataRowCount == 1
                ? "" : $"след превръщането редовете са {sheet.DataRowCount}");

            // Редът е истински и приема стойност.
            var codes = form.Grids.First(g => g.Plan.Node.ElementName == "r_b_rd_received")
                .Plan.Columns.First(c => c.Name == "ewc").Domain.Options;

            sheet.PutValue(0, "ewc", codes[0].Display);

            newRowNotes.Add(sheet.Snapshot().Count == 1
                ? "" : $"отчетът пази {sheet.Snapshot().Count} реда");

            newRowNotes.Add(sheet.Snapshot()[0].GetValueOrDefault("ewc") == codes[0].Value
                ? "" : "стойността не стигна до реда");

            // Второ повикване върху истински ред не добавя нищо.
            var again = sheet.Materialize(0);

            newRowNotes.Add(again == 0 && sheet.DataRowCount == 1
                ? "" : $"повторното превръщане направи {sheet.DataRowCount} реда");
        }
        catch (Exception ex)
        {
            newRowError = ex.ToString();
        }
    });

    newRowThread.SetApartmentState(ApartmentState.STA);
    newRowThread.Start();
    newRowThread.Join();

    if (newRowError is not null) Console.WriteLine("     " + newRowError);

    var newRowProblems = newRowNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in newRowProblems) Console.WriteLine("     " + problem);

    Check("редът „нов запис“ минава без изключение", newRowError is null);
    Check($"направени са всичките {newRowNotes.Count} проверки", newRowNotes.Count == 6, $"{newRowNotes.Count}");
    Check("редът „нов запис“ става истински и приема стойност", newRowProblems.Count == 0,
        $"{newRowProblems.Count} проблема");
}

// =============================================== Складът: месецът
Console.WriteLine();
Console.WriteLine("### ТЕСТ - месецът се смята, а не се сглобява на ръка");
{
    var notes = new List<string>();

    // Преходът през декември е мястото, където се греши: „месец + 1“ дава
    // тринайсети месец, ако някой го напише сам.
    notes.Add(new LedgerPeriod(2026, 12).Next == new LedgerPeriod(2027, 1)
        ? "" : "декември не води до януари");
    notes.Add(new LedgerPeriod(2026, 1).Previous == new LedgerPeriod(2025, 12)
        ? "" : "януари не води назад към декември");

    notes.Add(LedgerPeriod.TryParse("2026-09", out var parsed) && parsed == new LedgerPeriod(2026, 9)
        ? "" : "„2026-09“ не се разчита");
    notes.Add(!LedgerPeriod.TryParse("2026-13", out _) ? "" : "тринайсети месец се приема");
    notes.Add(!LedgerPeriod.TryParse("глупост", out _) ? "" : "безсмислица се приема");

    // Записът в базата се пресява с обикновено сравнение на текст — затова
    // месецът трябва да е с водеща нула, иначе „2026-9“ застава след „2026-10“.
    notes.Add(new LedgerPeriod(2026, 9).ToString() == "2026-09"
        ? "" : "месецът се изписва без водеща нула: " + new LedgerPeriod(2026, 9));

    notes.Add(new LedgerPeriod(2026, 9).Quarter == 3 ? "" : "септември не е в трето тримесечие");
    notes.Add(new LedgerPeriod(2026, 9).Covers(new DateOnly(2026, 9, 30)) &&
              !new LedgerPeriod(2026, 9).Covers(new DateOnly(2026, 10, 1))
        ? "" : "месецът покрива грешни дни");
    notes.Add(new LedgerPeriod(2026, 9) < new LedgerPeriod(2026, 10) ? "" : "месеците не се подреждат");
    notes.Add(new LedgerPeriod(2028, 2).Last == new DateOnly(2028, 2, 29)
        ? "" : "високосният февруари е сбъркан");

    var problems = notes.Where(n => n.Length > 0).ToList();
    foreach (var problem in problems) Console.WriteLine("     " + problem);

    Check($"направени са всичките {notes.Count} проверки", notes.Count == 10, $"{notes.Count}");
    Check("месецът се държи в краищата си", problems.Count == 0, $"{problems.Count} проблема");
}

// =============================================== Складът: наличността
Console.WriteLine();
Console.WriteLine("### ТЕСТ - наличността се смята от движенията");
{
    var notes = new List<string>();

    var metal = new Batch(1, "2026-0001", "203344556", "Метал ООД", "160216", new DateOnly(2026, 9, 3));
    var skrap = new Batch(2, "2026-0002", "204455667", "Скрап ЕООД", "160216", new DateOnly(2026, 9, 10));
    var carried = new Batch(3, "2026-0000", "203344556", "Метал ООД", "160216",
        new DateOnly(2026, 8, 31), Opening: true);

    // Началното количество е движение като всяко друго, само че отбелязано:
    // така първият месец се смята със същата сметка като всеки следващ.
    var opening = new Move(10, MoveKind.Received, new DateOnly(2026, 8, 31), "160216", 2.0m,
        Opening: true);

    var received1 = new Move(1, MoveKind.Received, new DateOnly(2026, 9, 3), "160216", 12.4m);
    var received2 = new Move(2, MoveKind.Received, new DateOnly(2026, 9, 10), "160216", 8.0m);
    var treated = new Move(3, MoveKind.Treated, new DateOnly(2026, 9, 10), "160216", 15.0m);
    var generated = new Move(4, MoveKind.Generated, new DateOnly(2026, 9, 12), "191202", 12.0m);
    var submitted = new Move(5, MoveKind.Submitted, new DateOnly(2026, 9, 20), "191202", 5.0m);

    var moves = new List<Move> { opening, received1, received2, treated, generated, submitted };
    var batches = new List<Batch> { metal, skrap, carried };

    var links = new List<LedgerLink>
    {
        new(10, 3, 2.0m),
        new(1, 1, 12.4m),
        new(2, 2, 8.0m),
        new(3, 3, 2.0m), new(3, 1, 12.4m), new(3, 2, 0.6m)
    };

    var september = new LedgerPeriod(2026, 9);

    // Приемане и обработка в един ден: денят, в който отпадъкът е дошъл, не
    // бива да е денят, в който не може да се пипне.
    notes.Add(StockMath.Balance(moves, StoreKind.Received, "160216", new DateOnly(2026, 9, 3)) == 14.4m
        ? "" : "приетото същия ден не се брои");

    notes.Add(StockMath.Balance(moves, StoreKind.Received, "160216", september.Last) == 7.4m
        ? "" : "наличността в края на месеца е сбъркана");

    // Кодът се сравнява сведен: „16 02 16“ и „160216“ са едно и също.
    notes.Add(StockMath.Balance(moves, StoreKind.Received, "16 02 16", september.Last) == 7.4m
        ? "" : "разреденият код не се разпознава");

    var stock = StockMath.Batches(batches, moves, links, StoreKind.Received, september.Last);
    var byBatch = stock.ToDictionary(s => s.Batch.Id);

    notes.Add(byBatch.TryGetValue(1, out var first) && first.IsEmpty
        ? "" : "изчерпаната партида още показва наличност");
    notes.Add(byBatch.TryGetValue(2, out var second) && second.Available == 7.4m
        ? "" : "остатъкът по втората партида е сбъркан");

    // Подредбата е тази, по която ще се изчерпват — най-старата първа.
    notes.Add(stock.Select(s => s.Batch.Id).SequenceEqual([3L, 1L, 2L])
        ? "" : "партидите не са подредени по дата на приемане");

    notes.Add(StockMath.Available(stock).All(s => !s.IsEmpty)
        ? "" : "изчерпана партида се предлага за разпределяне");

    // Каквото затваря септември, отваря октомври — иначе месеците не се връзват.
    var closing = StockMath.Closing(moves, StoreKind.Received, september);
    var next = StockMath.Opening(moves, StoreKind.Received, september.Next);
    notes.Add(closing.Count == next.Count && closing.All(p => next.GetValueOrDefault(p.Key) == p.Value)
        ? "" : "краят на месеца не отваря следващия");

    notes.Add(StockMath.Opening(moves, StoreKind.Received, september).GetValueOrDefault("160216") == 2.0m
        ? "" : "началното количество не стои пред първия месец");

    var movement = StockMath.Movement(moves, StoreKind.Received, september);
    notes.Add(movement.GetValueOrDefault("160216") == 5.4m
        ? "" : "движението за месеца е сбъркано: " + movement.GetValueOrDefault("160216"));

    var totals = StockMath.Totals(moves, september);
    notes.Add(totals[MoveKind.Received] == 20.4m && totals[MoveKind.Treated] == 15.0m &&
              totals[MoveKind.Generated] == 12.0m && totals[MoveKind.Submitted] == 5.0m
        ? "" : "сборовете по вид движение са сбъркани");

    notes.Add(StockMath.Balance(moves, StoreKind.Formed, "191202", september.Last) == 7.0m
        ? "" : "вторият склад не се смята");

    // Началното количество не е движение за човека — то е заварено състояние.
    notes.Add(StockMath.Periods(moves).Contains(september) &&
              !StockMath.Periods(moves).Contains(new LedgerPeriod(2026, 8))
        ? "" : "началното количество се брои за месец с движение");

    var problems = notes.Where(n => n.Length > 0).ToList();
    foreach (var problem in problems) Console.WriteLine("     " + problem);

    Check($"направени са всичките {notes.Count} проверки", notes.Count == 13, $"{notes.Count}");
    Check("наличността излиза от движенията", problems.Count == 0, $"{problems.Count} проблема");
}

// =============================================== Складът: разпределянето
Console.WriteLine();
Console.WriteLine("### ТЕСТ - разпределяне по партиди и произходът след него");
{
    var notes = new List<string>();

    var metal = new Batch(1, "2026-0001", "203344556", "Метал ООД", "160216", new DateOnly(2026, 9, 3));
    var skrap = new Batch(2, "2026-0002", "204455667", "Скрап ЕООД", "160216", new DateOnly(2026, 9, 10));
    var other = new Batch(3, "2026-0003", "205566778", "Друг ЕООД", "191202", new DateOnly(2026, 9, 1));

    var stock = new List<BatchStock>
    {
        new(metal, 12.4m, 0m),
        new(skrap, 8.0m, 0m),
        new(other, 4.0m, 0m)
    };

    var same = StockMath.Available(stock, "160216");

    var fifo = Allocation.Fifo(same, 15.0m);

    notes.Add(fifo.Draws.Count == 2 && fifo.Draws[0].BatchId == 1
        ? "" : "разпределянето не тръгва от най-старата партида");
    notes.Add(fifo.Draws.Count == 2 && fifo.Draws[0].Amount == 12.4m && fifo.Draws[1].Amount == 2.6m
        ? "" : "дяловете при преливане в следваща партида са сбъркани");
    notes.Add(fifo.Taken == 15.0m && fifo.IsComplete ? "" : "взетото не отговаря на поисканото");
    notes.Add(fifo.Draws.Sum(d => d.Amount) == 15.0m ? "" : "сборът на дяловете не е количеството");

    // Недостигът е обичайният случай, не изключение: казва се с число.
    var over = Allocation.Fifo(same, 25.0m);
    notes.Add(!over.IsComplete && over.Missing == 4.6m
        ? "" : "недостигът не се съобщава с число: " + over.Missing);

    // Партида с друг код не участва — иначе произходът щеше да е измислен.
    notes.Add(same.All(s => s.Batch.Id != 3) ? "" : "партида с друг код се предлага");

    // Произходът се пренася пропорционално: 12,4 към 2,6 от петнайсет тона,
    // после същото съотношение върху девет тона мед.
    var inherited = Allocation.Inherit(fifo.Draws, 9.0m);
    notes.Add(inherited.Sum(d => d.Amount) == 9.0m
        ? "" : "наследеното не се схожда с произведеното: " + inherited.Sum(d => d.Amount));
    notes.Add(inherited.Count == 2 && inherited[0].Amount > inherited[1].Amount
        ? "" : "наследените дялове не пазят съотношението");
    notes.Add(inherited.All(d => decimal.Round(d.Amount, 3) == d.Amount)
        ? "" : "наследен дял с повече от три знака");

    // Един дял трябва да получи цялото — иначе делене на едно губи грамове.
    var single = Allocation.Inherit([new Draw(1, 12.4m)], 7.0m);
    notes.Add(single.Count == 1 && single[0].Amount == 7.0m ? "" : "делене на един дял губи количество");

    notes.Add(Allocation.Check(fifo.Draws, same, 15.0m, "160216").Count == 0
        ? "" : "изрядното разпределение се обявява за сбъркано");

    var overBatch = Allocation.Check([new Draw(1, 20.0m)], same, 20.0m, "160216");
    notes.Add(overBatch.Any(i => i.Kind == DrawProblem.OverBatch)
        ? "" : "искането над наличното в партидата не се хваща");

    var mismatch = Allocation.Check([new Draw(1, 5.0m)], same, 15.0m, "160216");
    notes.Add(mismatch.Any(i => i.Kind == DrawProblem.Mismatch)
        ? "" : "несъответният сбор не се хваща");

    var unknown = Allocation.Check([new Draw(99, 15.0m)], same, 15.0m, "160216");
    notes.Add(unknown.Any(i => i.Kind == DrawProblem.UnknownBatch)
        ? "" : "непознатата партида не се хваща");

    var wrongCode = Allocation.Check([new Draw(3, 4.0m)], stock, 4.0m, "160216");
    notes.Add(wrongCode.Any(i => i.Kind == DrawProblem.WrongCode)
        ? "" : "партида с друг код не се хваща");

    // Два дяла към една партида са начин на писане, не грешка.
    var twice = Allocation.Check([new Draw(1, 6.0m), new Draw(1, 6.4m)], same, 12.4m, "160216");
    notes.Add(twice.Count == 0 ? "" : "два дяла към една партида се броят за грешка");

    var known = new Dictionary<long, Batch> { [1] = metal, [2] = skrap };
    var origins = Allocation.Origins(inherited, known);

    notes.Add(origins.Count == 2 && origins[0].Amount >= origins[1].Amount
        ? "" : "произходът не е подреден по големина");
    notes.Add(origins.Sum(o => o.Amount) == 9.0m ? "" : "произходът не покрива цялото количество");
    notes.Add(origins[0].Eik == "203344556" && origins[0].PartnerName == "Метал ООД"
        ? "" : "произходът не носи клиента");

    var problems = notes.Where(n => n.Length > 0).ToList();
    foreach (var problem in problems) Console.WriteLine("     " + problem);

    Check($"направени са всичките {notes.Count} проверки", notes.Count == 19, $"{notes.Count}");
    Check("разпределянето пази произхода през обработката", problems.Count == 0,
        $"{problems.Count} проблема");
}

// =============================================== Складът: контролите
Console.WriteLine();
Console.WriteLine("### ТЕСТ - контролите преди затваряне на месеца");
{
    var notes = new List<string>();

    var september = new LedgerPeriod(2026, 9);

    // Обработено повече от полученото: складът не го покрива.
    var shortfall = new List<Move>
    {
        new(1, MoveKind.Received, new DateOnly(2026, 9, 3), "160216", 12.4m),
        new(2, MoveKind.Treated, new DateOnly(2026, 9, 5), "160216", 15.0m)
    };

    var negative = Reconcile.Negative(shortfall, StoreKind.Received);

    notes.Add(negative.Count == 1 ? "" : $"недостигът дава {negative.Count} реда вместо един");
    notes.Add(negative.Count == 1 && negative[0].Amount == 2.6m
        ? "" : "недостигът е с грешно число");
    notes.Add(negative.Count == 1 && negative[0].On == new DateOnly(2026, 9, 5)
        ? "" : "недостигът сочи грешен ден");
    notes.Add(negative.Count == 1 && negative[0].Blocks ? "" : "недостигът не спира затварянето");

    // Същият ден: приемането се брои преди обработката.
    var sameDay = new List<Move>
    {
        new(1, MoveKind.Received, new DateOnly(2026, 9, 3), "160216", 12.4m),
        new(2, MoveKind.Treated, new DateOnly(2026, 9, 3), "160216", 12.4m)
    };
    notes.Add(Reconcile.Negative(sameDay, StoreKind.Received).Count == 0
        ? "" : "обработката в деня на приемането се обявява за недостиг");

    // Второто пропадане е следствие на първото и не се съобщава пак.
    var twice = new List<Move>
    {
        new(1, MoveKind.Treated, new DateOnly(2026, 9, 5), "160216", 3.0m),
        new(2, MoveKind.Treated, new DateOnly(2026, 9, 7), "160216", 4.0m)
    };
    notes.Add(Reconcile.Negative(twice, StoreKind.Received).Count == 1
        ? "" : "съобщава се всяко пропадане, а не първото");

    // Разликата в масата: обработено 15, образувано 12.
    var lossy = new List<Move>
    {
        new(1, MoveKind.Received, new DateOnly(2026, 9, 1), "160216", 20.0m),
        new(2, MoveKind.Treated, new DateOnly(2026, 9, 10), "160216", 15.0m),
        new(3, MoveKind.Generated, new DateOnly(2026, 9, 12), "191202", 12.0m)
    };

    var unbalanced = Reconcile.Unbalanced(lossy, september);
    notes.Add(unbalanced.Count == 1 && unbalanced[0].Amount == 3.0m
        ? "" : "разликата в масата е сбъркана");
    notes.Add(unbalanced.Count == 1 && !unbalanced[0].Blocks
        ? "" : "разликата в масата спира затварянето, а не бива");
    notes.Add(Reconcile.Unbalanced(lossy, september, 3.0m).Count == 0
        ? "" : "допускът не поглъща разликата");

    // Неразпределеното: движение за 15 тона с дялове само за 12,4.
    var links = new List<LedgerLink> { new(2, 1, 12.4m) };
    var unallocated = Reconcile.Unallocated(lossy, links, september);
    notes.Add(unallocated.Any(i => i.Amount == 2.6m)
        ? "" : "непокритото количество не се съобщава");
    notes.Add(unallocated.All(i => !i.Blocks) ? "" : "неразпределеното спира затварянето, а не бива");

    // Движение преди началото на воденето — освен началните количества.
    var early = new List<Move>
    {
        new(1, MoveKind.Received, new DateOnly(2026, 8, 31), "160216", 2.0m, Opening: true),
        new(2, MoveKind.Received, new DateOnly(2026, 8, 20), "160216", 5.0m)
    };

    var before = Reconcile.BeforeStart(early, september);
    notes.Add(before.Count == 1 && before[0].Amount == 5.0m
        ? "" : "движението преди началото не се хваща");
    notes.Add(before.Count == 1 && before[0].Blocks ? "" : "движението преди началото не спира");
    notes.Add(before.All(i => i.Amount != 2.0m)
        ? "" : "началното количество се брои за движение преди началото");

    // Сборната проверка: спиращото стои отгоре.
    var all = Reconcile.Check([], shortfall, [], september);
    notes.Add(all.Count > 0 && all[0].Blocks ? "" : "спиращото не е подредено първо");
    notes.Add(!Reconcile.CanClose(all) ? "" : "месец с недостиг се затваря");

    // Месец, в който третираното си е намерило образуваното, се затваря — дори
    // когато има бележки. Полученото е с непълен произход, а това е бележка.
    var one = new Batch(1, "2026-0001", "203344556", "Метал ООД", "160216", new DateOnly(2026, 9, 1));

    var settled = new List<Move>
    {
        new(1, MoveKind.Received, new DateOnly(2026, 9, 1), "160216", 20.0m),
        new(2, MoveKind.Treated, new DateOnly(2026, 9, 10), "160216", 12.4m),
        new(3, MoveKind.Generated, new DateOnly(2026, 9, 12), "191202", 12.4m)
    };

    var settledLinks = new List<LedgerLink>
    {
        new(1, 1, 18.0m), new(2, 1, 12.4m), new(3, 1, 12.4m)
    };

    var closable = Reconcile.Check([one], settled, settledLinks, september);

    notes.Add(closable.Any(i => i.Kind == LedgerProblem.Unallocated)
        ? "" : "непълният произход не се съобщава");
    notes.Add(Reconcile.CanClose(closable) ? "" : "месец само с бележки не се затваря");

    var problems = notes.Where(n => n.Length > 0).ToList();
    foreach (var problem in problems) Console.WriteLine("     " + problem);

    Check($"направени са всичките {notes.Count} проверки", notes.Count == 18, $"{notes.Count}");
    Check("контролите различават спиращото от бележката", problems.Count == 0,
        $"{problems.Count} проблема");
}

// =============================================== Складът: базата
Console.WriteLine();
Console.WriteLine("### ТЕСТ - складът се появява в заварена база без загуба");
{
    var notes = new List<string>();
    var storePath = Path.Combine(work, $"ledger_{Guid.NewGuid():N}.db");

    long companyId;
    long documentId;

    using (var db = Workspace.Open(storePath))
    {
        companyId = db.Companies.Add("203344556", "Метал ООД");
        documentId = db.Documents.Create("book", "r_b_recovery_dispose", "Книга 2026");
    }

    static int Version(string path)
    {
        using var raw = new SqliteConnection($"Data Source={path}");
        raw.Open();
        using var read = raw.CreateCommand();
        read.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(read.ExecuteScalar());
    }

    notes.Add(Version(storePath) == 20 ? "" : $"новата база е на версия {Version(storePath)}, а не 20");

    // Заварена база: махат се новите таблици и версията се връща на шест —
    // точно състоянието на машина, на която програмата е работила досега.
    using (var raw = new SqliteConnection($"Data Source={storePath}"))
    {
        raw.Open();
        using var back = raw.CreateCommand();
        back.CommandText = """
            ALTER TABLE news DROP COLUMN hidden_utc;
            ALTER TABLE news DROP COLUMN link;
            DROP TABLE action_windows;
            DROP TABLE partners;
            DROP TABLE action_log;
            DROP TABLE import_items;
            DROP TABLE imports;
            DROP TABLE ledger_links;
            DROP TABLE ledger_periods;
            DROP TABLE ledger_moves;
            DROP TABLE batches;
            PRAGMA user_version = 6;
            """;
        back.ExecuteNonQuery();
    }

    notes.Add(Version(storePath) == 6 ? "" : "връщането до версия 6 не стана");

    using (var db = Workspace.Open(storePath))
    {
        notes.Add(Version(storePath) == 20 ? "" : "заварената база не се привежда до 20");

        // Работата на човека не бива да се загуби при привеждането.
        notes.Add(db.Companies.Find(companyId)?.Name == "Метал ООД"
            ? "" : "фирмата не преживя привеждането");
        notes.Add(db.Documents.Find(documentId)?.Title == "Книга 2026"
            ? "" : "отчетът не преживя привеждането");

        var revived = db.Batches.Add(companyId, new BatchDraft(
            "203344556", "Метал ООД", "160216", new DateOnly(2026, 9, 3)));
        notes.Add(revived > 0 && db.Batches.Find(revived) is not null
            ? "" : "новите таблици не работят след привеждането");
    }

    using (var db = Workspace.Open(storePath))
    {
        // Номерът е пореден в рамките на фирмата и годината.
        notes.Add(db.Batches.NextCode(companyId, 2026) == "2026-0002"
            ? "" : "следващият номер е " + db.Batches.NextCode(companyId, 2026));
        notes.Add(db.Batches.NextCode(companyId, 2027) == "2027-0001"
            ? "" : "новата година не тръгва отначало");

        var id = db.Batches.Add(companyId, new BatchDraft(
            "204455667", "Скрап ЕООД", "16 02 16", new DateOnly(2026, 9, 10),
            Document: "протокол 118",
            Fields: new Dictionary<string, string> { ["cat_nuba"] = "3", ["reason"] = "договор 4" }));

        var batch = db.Batches.Find(id);

        notes.Add(batch is not null && batch.Eik == "204455667" && batch.PartnerName == "Скрап ЕООД" &&
                  batch.ReceivedOn == new DateOnly(2026, 9, 10) && batch.Document == "протокол 118"
            ? "" : "партидата не се чете такава, каквато е записана");

        // Кодът се свежда на влизане: „16 02 16“ и „160216“ са едно и също.
        notes.Add(batch?.Ewc == "160216" ? "" : "разреденият код не се свежда при запис");

        var fields = db.Batches.Fields(id);
        notes.Add(fields.GetValueOrDefault("cat_nuba") == "3" && fields.GetValueOrDefault("reason") == "договор 4"
            ? "" : "останалите колони не се пазят");

        notes.Add(batch is { Opening: false } ? "" : "обикновена партида се води начална");

        // Празна площадка значи първата — схемата иска число, не празно.
        var noStage = db.Batches.Add(companyId, new BatchDraft(
            "205566778", "Трети ООД", "191202", new DateOnly(2026, 9, 12), Stagenum: "  "));
        notes.Add(db.Batches.Find(noStage)?.Stagenum == "1" ? "" : "празната площадка не става първа");

        notes.Add(db.Batches.Partners(companyId).Count == 3
            ? "" : $"клиентите са {db.Batches.Partners(companyId).Count} вместо 3");
    }

    var problems = notes.Where(n => n.Length > 0).ToList();
    foreach (var problem in problems) Console.WriteLine("     " + problem);

    Check($"направени са всичките {notes.Count} проверки", notes.Count == 14, $"{notes.Count}");
    Check("складът се добавя към заварена база без загуба", problems.Count == 0,
        $"{problems.Count} проблема");
}

// =============================================== Складът: записът и сметката
Console.WriteLine();
Console.WriteLine("### ТЕСТ - записаният месец се чете и се смята еднакво");
{
    var notes = new List<string>();
    var movePath = Path.Combine(work, $"moves_{Guid.NewGuid():N}.db");

    using var db = Workspace.Open(movePath);

    var companyId = db.Companies.Add("203344556", "Метал ООД");
    var september = new LedgerPeriod(2026, 9);

    var metal = db.Batches.Add(companyId, new BatchDraft(
        "203344556", "Метал ООД", "160216", new DateOnly(2026, 9, 3)));
    var skrap = db.Batches.Add(companyId, new BatchDraft(
        "204455667", "Скрап ЕООД", "160216", new DateOnly(2026, 9, 10)));

    db.Ledger.Add(companyId, new MoveDraft(
        MoveKind.Received, new DateOnly(2026, 9, 3), "160216", 12.4m,
        Draws: [new Draw(metal, 12.4m)]));

    db.Ledger.Add(companyId, new MoveDraft(
        MoveKind.Received, new DateOnly(2026, 9, 10), "160216", 8.0m,
        Draws: [new Draw(skrap, 8.0m)]));

    var treatedId = db.Ledger.Add(companyId, new MoveDraft(
        MoveKind.Treated, new DateOnly(2026, 9, 10), "160216", 15.0m,
        Note: "смяна А",
        Fields: new Dictionary<string, string> { ["op_id_code"] = "R12" },
        Draws: [new Draw(metal, 12.4m), new Draw(skrap, 2.6m)]));

    // Количество с три знака трябва да преживее обиколката до базата и обратно:
    // текстът е точен, докато double не е.
    var oddId = db.Ledger.Add(companyId, new MoveDraft(
        MoveKind.Generated, new DateOnly(2026, 9, 12), "191202", 12.345m,
        Draws: Allocation.Inherit([new Draw(metal, 12.4m), new Draw(skrap, 2.6m)], 12.345m)));

    // Друга площадка — тя не бива да се смесва с първата.
    db.Ledger.Add(companyId, new MoveDraft(
        MoveKind.Received, new DateOnly(2026, 9, 5), "160216", 3.0m, Stagenum: "2"));

    // Начално количество: движение, но не месец с движение.
    db.Ledger.Add(companyId, new MoveDraft(
        MoveKind.Received, new DateOnly(2026, 8, 31), "160216", 2.0m, Opening: true));

    var moves = db.Ledger.Read(companyId, "1");
    var links = db.Ledger.Links(companyId, "1");

    notes.Add(moves.FirstOrDefault(m => m.Id == oddId)?.Amount == 12.345m
        ? "" : "количеството с три знака не преживя базата");
    notes.Add(moves.FirstOrDefault(m => m.Id == treatedId)?.Kind == MoveKind.Treated
        ? "" : "видът на движението не се чете");
    notes.Add(db.Ledger.Note(treatedId) == "смяна А" ? "" : "бележката не се пази");
    notes.Add(db.Ledger.Fields(treatedId).GetValueOrDefault("op_id_code") == "R12"
        ? "" : "останалите колони на движението не се пазят");

    notes.Add(db.Ledger.Draws(treatedId).Sum(d => d.Amount) == 15.0m
        ? "" : "дяловете на обработката не се четат цели");
    notes.Add(links.Count(l => l.MoveId == treatedId) == 2
        ? "" : "дяловете по партиди не са два");

    // Площадката е част от ключа: втората не влиза в първата.
    notes.Add(moves.All(m => m.Stagenum == "1") ? "" : "чужда площадка влиза в обхвата");
    notes.Add(db.Ledger.Read(companyId).Count == moves.Count + 1
        ? "" : "без площадка не се четат всички движения");
    notes.Add(db.Ledger.Stages(companyId).SequenceEqual(["1", "2"])
        ? "" : "площадките не се изброяват");

    notes.Add(db.Ledger.InPeriod(companyId, "1", september).All(m => september.Covers(m.Date))
        ? "" : "месецът пропуска чужди дати");
    notes.Add(db.Ledger.Periods(companyId, "1").SequenceEqual([september])
        ? "" : "началното количество се брои за месец с движение");

    // Сметката в „Ядро“ трябва да дава същото върху прочетеното от базата.
    notes.Add(StockMath.Balance(moves, StoreKind.Received, "160216", september.Last) == 7.4m
        ? "" : "наличността от базата е сбъркана");

    var batches = db.Batches.List(companyId, "1");
    var stock = StockMath.Batches(batches, moves, links, StoreKind.Received, september.Last)
        .ToDictionary(s => s.Batch.Id);

    notes.Add(stock.TryGetValue(metal, out var first) && first.IsEmpty
        ? "" : "изчерпаната партида още показва наличност");
    notes.Add(stock.TryGetValue(skrap, out var second) && second.Available == 5.4m
        ? "" : "остатъкът по втората партида е сбъркан");

    // Пренаписването заменя дяловете, а не ги добавя.
    db.Ledger.Update(treatedId, new MoveDraft(
        MoveKind.Treated, new DateOnly(2026, 9, 10), "160216", 12.4m,
        Draws: [new Draw(metal, 12.4m)]));

    notes.Add(db.Ledger.Draws(treatedId).Count == 1 && db.Ledger.Draws(treatedId)[0].Amount == 12.4m
        ? "" : "пренаписването не заменя дяловете");

    // Махнатата партида отнася дяловете си, но не и движението: по-добре видимо
    // неразпределено, отколкото тихо изтрито движение.
    db.Batches.Remove(skrap);

    notes.Add(db.Ledger.Read(companyId, "1").Any(m => m.Id == oddId)
        ? "" : "махнатата партида отнесе и движението");

    var orphaned = Reconcile.Unallocated(
        db.Ledger.Read(companyId, "1"), db.Ledger.Links(companyId, "1"), september);
    notes.Add(orphaned.Count > 0 ? "" : "движението без партида не се обявява за неразпределено");

    // Махнатото движение отнася дяловете си.
    db.Ledger.Remove(oddId);
    notes.Add(db.Ledger.Links(companyId, "1").All(l => l.MoveId != oddId)
        ? "" : "махнатото движение остави дялове след себе си");

    // Месецът, от който се води складът, е настройка на фирма.
    notes.Add(db.Periods.Start(companyId) is null ? "" : "необявеното начало не е празно");

    db.Periods.SetStart(companyId, september);
    notes.Add(db.Periods.Start(companyId) == september ? "" : "началният месец не се пази");

    var second_company = db.Companies.Add("204455667", "Скрап ЕООД");
    notes.Add(db.Periods.Start(second_company) is null ? "" : "двете фирми делят начален месец");

    notes.Add(!db.Periods.IsClosed(companyId, "1", september) ? "" : "неотвореният месец е затворен");

    db.Periods.Close(companyId, "1", september, "приет с разлика 0,300 т");
    notes.Add(db.Periods.IsClosed(companyId, "1", september) ? "" : "затварянето не се пази");
    notes.Add(db.Periods.Latest(companyId, "1") == september ? "" : "последният затворен месец е сбъркан");
    notes.Add(db.Periods.Closed(companyId, "1").FirstOrDefault()?.Note == "приет с разлика 0,300 т"
        ? "" : "бележката при затварянето не се пази");
    notes.Add(!db.Periods.IsClosed(companyId, "2", september)
        ? "" : "затварянето на едната площадка затваря и другата");

    db.Periods.Reopen(companyId, "1", september);
    notes.Add(!db.Periods.IsClosed(companyId, "1", september) ? "" : "месецът не се отваря наново");

    var problems = notes.Where(n => n.Length > 0).ToList();
    foreach (var problem in problems) Console.WriteLine("     " + problem);

    Check($"направени са всичките {notes.Count} проверки", notes.Count == 27, $"{notes.Count}");
    Check("записаното и смятаното си съответстват", problems.Count == 0, $"{problems.Count} проблема");
}

// =============================================== Складът: началото на воденето
Console.WriteLine();
Console.WriteLine("### ТЕСТ - заварената наличност стои пред първия месец");
{
    var notes = new List<string>();
    var startPath = Path.Combine(work, $"start_{Guid.NewGuid():N}.db");

    using var db = Workspace.Open(startPath);

    var companyId = db.Companies.Add("203344556", "Метал ООД");
    var september = new LedgerPeriod(2026, 9);

    var carried = db.Setup.AddOpening(companyId, september,
        new OpeningEntry(StoreKind.Received, "160216", 4.2m, "204455667", "Скрап ЕООД"));

    var unknown = db.Setup.AddOpening(companyId, september,
        new OpeningEntry(StoreKind.Received, "191202", 1.0m));

    db.Setup.AddOpening(companyId, september,
        new OpeningEntry(StoreKind.Formed, "191202", 3.0m));

    db.Setup.SetStart(companyId, september);

    var moves = db.Ledger.Read(companyId);
    var links = db.Ledger.Links(companyId);

    // Заварената наличност стои в деня ПРЕДИ първия работен ден — така първият
    // месец се смята със същата сметка като всеки следващ.
    notes.Add(moves.All(m => m.Date == new DateOnly(2026, 8, 31))
        ? "" : "заварената наличност не стои пред първия месец");
    notes.Add(moves.All(m => m.Opening) ? "" : "заварената наличност не е отбелязана като такава");

    notes.Add(StockMath.Opening(moves, StoreKind.Received, september).GetValueOrDefault("160216") == 4.2m
        ? "" : "полученото не отваря септември");
    notes.Add(StockMath.Opening(moves, StoreKind.Formed, september).GetValueOrDefault("191202") == 3.0m
        ? "" : "образуваното не отваря септември");

    // Двата склада не се смесват, макар кодът да е един и същ.
    notes.Add(StockMath.Opening(moves, StoreKind.Received, september).GetValueOrDefault("191202") == 1.0m
        ? "" : "двата склада се смесват по един код");

    // Началното количество не е месец с движение.
    notes.Add(db.Ledger.Periods(companyId).Count == 0
        ? "" : "заварената наличност се брои за месец с движение");

    // Партида се създава дори без клиент — тя е носителят, от който се черпи.
    notes.Add(db.Batches.Find(unknown) is { Opening: true, Eik: "" }
        ? "" : "заварената наличност без клиент не получава партида");
    notes.Add(db.Batches.Find(carried)?.PartnerName == "Скрап ЕООД"
        ? "" : "клиентът на заварената партида не се пази");

    var openings = db.Setup.Openings(companyId);
    notes.Add(openings.Count == 3 ? "" : $"обявените са {openings.Count} вместо 3");
    notes.Add(openings.All(o => !o.InUse) ? "" : "необработена наличност се води изразходвана");
    notes.Add(openings.Sum(o => o.Amount) == 8.2m ? "" : "сборът на обявеното е сбъркан");

    // Докато не е пипната, обявената наличност се маха свободно.
    notes.Add(db.Setup.RemoveOpening(companyId, unknown) ? "" : "непипнатата наличност не се маха");
    notes.Add(db.Setup.Openings(companyId).Count == 2 ? "" : "махнатото още се брои");
    notes.Add(db.Ledger.Read(companyId).Count == 2 ? "" : "махнатата партида остави движението си");

    // Обработка от заварената партида — оттук нататък тя вече не е настройка.
    db.Ledger.Add(companyId, new MoveDraft(
        MoveKind.Treated, new DateOnly(2026, 9, 4), "160216", 2.0m,
        Draws: [new Draw(carried, 2.0m)]));

    var used = db.Setup.Openings(companyId).FirstOrDefault(o => o.Batch.Id == carried);
    notes.Add(used is { InUse: true } && used.Consumed == 2.0m
        ? "" : "изразходваното от заварената партида не се вижда");
    notes.Add(used?.Available == 2.2m ? "" : "остатъкът от заварената партида е сбъркан");

    notes.Add(!db.Setup.RemoveOpening(companyId, carried)
        ? "" : "партида, от която е черпено, се маха и отнася произхода");
    notes.Add(db.Ledger.Read(companyId).Any(m => m.Kind == MoveKind.Treated)
        ? "" : "отказаното махане все пак отнесе обработката");

    // Пренаписването заменя, но не жертва вече започнатото.
    var kept = db.Setup.ReplaceOpenings(companyId, september,
        [new OpeningEntry(StoreKind.Received, "170405", 10.0m)]);

    notes.Add(kept.Count == 1 && kept[0].Batch.Id == carried
        ? "" : "пренаписването не казва кое е останало");

    var after = db.Setup.Openings(companyId);
    notes.Add(after.Count == 2 ? "" : $"след пренаписването са {after.Count} вместо 2");
    notes.Add(after.Any(o => o.Batch.Ewc == "170405" && o.Amount == 10.0m)
        ? "" : "новото обявено количество липсва");
    notes.Add(after.All(o => o.Batch.Ewc != "191202")
        ? "" : "непипнатото старо количество оцеля след пренаписването");

    // Началният месец се пази заедно с количествата.
    notes.Add(db.Setup.Start(companyId) == september ? "" : "началният месец не се пази");

    // Движение отпреди началото е грешка, а заварената наличност — не.
    var early = db.Ledger.Read(companyId);
    notes.Add(Reconcile.BeforeStart(early, september).Count == 0
        ? "" : "заварената наличност се брои за движение преди началото");

    db.Ledger.Add(companyId, new MoveDraft(
        MoveKind.Received, new DateOnly(2026, 7, 15), "160216", 1.0m));

    notes.Add(Reconcile.BeforeStart(db.Ledger.Read(companyId), september).Count == 1
        ? "" : "истинското движение преди началото не се хваща");

    // Пренасянето от стария изглед „Баланс“: числа без клиент, и се вижда.
    var otherId = db.Companies.Add("204455667", "Скрап ЕООД");
    db.Openings.Put(otherId, 2026, "160216", 6.5m);

    var moved = db.Setup.Import(otherId, september, db.Openings.List(2026, [otherId]));

    notes.Add(moved == 1 ? "" : $"пренесени са {moved} реда вместо един");
    notes.Add(db.Setup.Openings(otherId).FirstOrDefault()?.Amount == 6.5m
        ? "" : "пренесеното количество е сбъркано");
    notes.Add(db.Setup.Openings(otherId).All(o => o.Batch.Eik.Length == 0)
        ? "" : "пренасянето си измисли клиент");

    var problems = notes.Where(n => n.Length > 0).ToList();
    foreach (var problem in problems) Console.WriteLine("     " + problem);

    Check($"направени са всичките {notes.Count} проверки", notes.Count == 28, $"{notes.Count}");
    Check("началото на воденето се държи", problems.Count == 0, $"{problems.Count} проблема");
}

// =============================================== Складът: екранът „Месец“
Console.WriteLine();
Console.WriteLine("### ТЕСТ - екранът „Месец“ показва движенията и произхода им");
{
    var pageNotes = new List<string>();
    string? pageError = null;

    var pageThread = new Thread(() =>
    {
        try
        {
            var pagePath = Path.Combine(work, $"page_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(pagePath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");
            var september = new LedgerPeriod(2026, 9);

            db.Setup.SetStart(companyId, september);

            var metal = db.Batches.Add(companyId, new BatchDraft(
                "204455667", "Скрап ЕООД", "160216", new DateOnly(2026, 9, 3)));

            db.Ledger.Add(companyId, new MoveDraft(
                MoveKind.Received, new DateOnly(2026, 9, 3), "160216", 12.4m,
                Draws: [new Draw(metal, 12.4m)]));

            db.Ledger.Add(companyId, new MoveDraft(
                MoveKind.Treated, new DateOnly(2026, 9, 10), "160216", 8.0m,
                Draws: [new Draw(metal, 8.0m)]));

            // Друг месец — не бива да се вижда в септември.
            db.Ledger.Add(companyId, new MoveDraft(
                MoveKind.Received, new DateOnly(2026, 10, 2), "160216", 5.0m));

            // Друга площадка — също не бива.
            db.Ledger.Add(companyId, new MoveDraft(
                MoveKind.Received, new DateOnly(2026, 9, 4), "160216", 1.0m, Stagenum: "2"));

            using var page = new LedgerPage(service, db);

            // Страницата тръгва от най-новия месец: октомври има движение.
            pageNotes.Add(page.MonthChoices.Count >= 2 ? "" : "месеците не се изброяват");
            pageNotes.Add(page.StageChoices.SequenceEqual(["1", "2"])
                ? "" : "площадките не стигат до екрана");

            page.UseMonth(new LedgerPeriod(2026, 9));

            pageNotes.Add(page.RowCount == 2 ? "" : $"септември показва {page.RowCount} реда вместо 2");

            // Кодът стига до екрана с наименованието си от схемата.
            pageNotes.Add(page.CodeAt(0).StartsWith("160216", StringComparison.Ordinal)
                ? "" : "кодът не се изписва: " + page.CodeAt(0));
            pageNotes.Add(page.CodeAt(0).Contains(" — ", StringComparison.Ordinal)
                ? "" : "наименованието на кода не стига до екрана");

            // Произходът се чете от дяловете, а не от полето на движението.
            pageNotes.Add(page.OriginAt(0).Contains("Скрап ЕООД", StringComparison.Ordinal)
                ? "" : "произходът не стига до екрана: " + page.OriginAt(0));

            // Ситото по вид свежда до едно движение.
            page.UseKind(2);
            pageNotes.Add(page.RowCount == 1 ? "" : $"ситото „Обработен“ дава {page.RowCount} реда");

            page.UseKind(1);
            pageNotes.Add(page.RowCount == 1 ? "" : $"ситото „Получен“ дава {page.RowCount} реда");

            page.UseKind(0);
            pageNotes.Add(page.RowCount == 2 ? "" : "връщането към „Всички“ не показва всичко");

            // Сборовете и наличностите се описват под лентата.
            pageNotes.Add(page.SummaryText.Contains("12,400", StringComparison.Ordinal)
                ? "" : "полученото не се описва: " + page.SummaryText);
            pageNotes.Add(page.SummaryText.Contains("4,400", StringComparison.Ordinal)
                ? "" : "оставащото в склада не се описва: " + page.SummaryText);

            // Обработено 8 без образувано — това е бележка, не спиране.
            pageNotes.Add(page.ChecksText.Length > 0 ? "" : "разликата в масата не се съобщава");

            // Октомврийското движение не се брои към септември.
            page.UseMonth(new LedgerPeriod(2026, 10));
            pageNotes.Add(page.RowCount == 1 ? "" : $"октомври показва {page.RowCount} реда вместо 1");
        }
        catch (Exception ex)
        {
            pageError = ex.ToString();
        }
    });

    pageThread.SetApartmentState(ApartmentState.STA);
    pageThread.Start();
    pageThread.Join();

    if (pageError is not null) Console.WriteLine("     " + pageError);

    var pageProblems = pageNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in pageProblems) Console.WriteLine("     " + problem);

    Check("екранът „Месец“ се сглобява без изключение", pageError is null);
    Check($"направени са всичките {pageNotes.Count} проверки", pageNotes.Count == 13, $"{pageNotes.Count}");
    Check("месецът показва своето и само своето", pageProblems.Count == 0,
        $"{pageProblems.Count} проблема");
}

// =============================================== Складът: код и произход заедно
Console.WriteLine();
Console.WriteLine("### ТЕСТ - наличността се разбива по код И по произход");
{
    var notes = new List<string>();

    var metal = new Batch(1, "2026-0001", "203344556", "Метал ООД", "160216", new DateOnly(2026, 9, 3));
    var skrap = new Batch(2, "2026-0002", "204455667", "Скрап ЕООД", "160216", new DateOnly(2026, 9, 10));
    var batches = new List<Batch> { metal, skrap };

    // Обработката черпи от двете партиди; образуваното наследява дяловете им;
    // предаденото ги изразходва в същото съотношение.
    var treatedDraws = new List<Draw> { new(1, 12.4m), new(2, 2.6m) };
    var generatedDraws = Allocation.Inherit(treatedDraws, 12.0m);
    var submittedDraws = Allocation.Inherit(generatedDraws, 5.0m);

    var moves = new List<Move>
    {
        new(1, MoveKind.Received, new DateOnly(2026, 9, 3), "160216", 12.4m),
        new(2, MoveKind.Received, new DateOnly(2026, 9, 10), "160216", 8.0m),
        new(3, MoveKind.Treated, new DateOnly(2026, 9, 10), "160216", 15.0m),
        new(4, MoveKind.Generated, new DateOnly(2026, 9, 12), "191202", 12.0m),
        new(5, MoveKind.Submitted, new DateOnly(2026, 9, 20), "191202", 5.0m)
    };

    var links = new List<LedgerLink> { new(1, 1, 12.4m), new(2, 2, 8.0m) };
    links.AddRange(treatedDraws.Select(d => new LedgerLink(3, d.BatchId, d.Amount)));
    links.AddRange(generatedDraws.Select(d => new LedgerLink(4, d.BatchId, d.Amount)));
    links.AddRange(submittedDraws.Select(d => new LedgerLink(5, d.BatchId, d.Amount)));

    var last = new DateOnly(2026, 9, 30);

    var received = StockMath.Shares(batches, moves, links, StoreKind.Received, last);

    // Изчерпаната партида не е ред: нула не е наличност.
    notes.Add(received.Count == 1 ? "" : $"полученото дава {received.Count} реда вместо един");
    notes.Add(received.Count == 1 && received[0].Batch.Id == 2 && received[0].Amount == 5.4m
        ? "" : "остатъкът в „Получен“ е сбъркан");
    notes.Add(received.Count == 1 && received[0].Ewc == "160216"
        ? "" : "кодът в „Получен“ е сбъркан");

    var formed = StockMath.Shares(batches, moves, links, StoreKind.Formed, last);

    notes.Add(formed.Count == 2 ? "" : $"образуваното дава {formed.Count} реда вместо два");
    notes.Add(formed.Sum(s => s.Amount) == 7.0m
        ? "" : "наличното в „Образуван“ е " + formed.Sum(s => s.Amount));

    // Същината: редът носи кода СЛЕД обработката, а партидата — кода ПРЕДИ нея.
    notes.Add(formed.All(s => s.Ewc == "191202")
        ? "" : "образуваното не носи кода след обработката");
    notes.Add(formed.All(s => s.Batch.Ewc == "160216")
        ? "" : "партидата загуби кода, с който е дошла");

    // И оттам произходът: чии са тези седем тона мед.
    var owners = formed.ToDictionary(s => s.Batch.Eik, s => s.Amount);
    notes.Add(owners.Count == 2 && owners.ContainsKey("203344556") && owners.ContainsKey("204455667")
        ? "" : "произходът на образуваното се загуби");
    notes.Add(owners.GetValueOrDefault("203344556") > owners.GetValueOrDefault("204455667")
        ? "" : "по-големият дял не е на по-големия доставчик");

    // Към по-ранна дата предаденото още не се е случило.
    var before = StockMath.Shares(batches, moves, links, StoreKind.Formed, new DateOnly(2026, 9, 15));
    notes.Add(before.Sum(s => s.Amount) == 12.0m
        ? "" : "наличността към дата не пренебрегва по-късното предаване");

    var problems = notes.Where(n => n.Length > 0).ToList();
    foreach (var problem in problems) Console.WriteLine("     " + problem);

    Check($"направени са всичките {notes.Count} проверки", notes.Count == 10, $"{notes.Count}");
    Check("кодът и произходът се четат заедно", problems.Count == 0, $"{problems.Count} проблема");
}

// =============================================== Складът: екранът „Склад“
Console.WriteLine();
Console.WriteLine("### ТЕСТ - екранът „Склад“ отговаря чие е количеството");
{
    var stockNotes = new List<string>();
    string? stockError = null;

    var stockThread = new Thread(() =>
    {
        try
        {
            var viewPath = Path.Combine(work, $"stock_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(viewPath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");

            var metal = db.Batches.Add(companyId, new BatchDraft(
                "203344556", "Метал ООД", "160216", new DateOnly(2026, 9, 3), Document: "протокол 118"));
            var skrap = db.Batches.Add(companyId, new BatchDraft(
                "204455667", "Скрап ЕООД", "160216", new DateOnly(2026, 9, 10)));

            db.Ledger.Add(companyId, new MoveDraft(
                MoveKind.Received, new DateOnly(2026, 9, 3), "160216", 12.4m,
                Draws: [new Draw(metal, 12.4m)]));
            db.Ledger.Add(companyId, new MoveDraft(
                MoveKind.Received, new DateOnly(2026, 9, 10), "160216", 8.0m,
                Draws: [new Draw(skrap, 8.0m)]));

            var treated = new List<Draw> { new(metal, 12.4m), new(skrap, 2.6m) };
            db.Ledger.Add(companyId, new MoveDraft(
                MoveKind.Treated, new DateOnly(2026, 9, 10), "160216", 15.0m, Draws: treated));

            db.Ledger.Add(companyId, new MoveDraft(
                MoveKind.Generated, new DateOnly(2026, 9, 12), "191202", 12.0m,
                Draws: Allocation.Inherit(treated, 12.0m)));

            using var page = new StockPage(service, db);

            page.UseStore(StoreKind.Received);
            page.UseView(byPartner: false);
            page.UseDate(new DateOnly(2026, 9, 30));

            // Само остатъкът: партидата на Метал ООД е изчерпана от обработката.
            stockNotes.Add(page.RowCount == 1 ? "" : $"„Получен“ показва {page.RowCount} реда вместо 1");
            stockNotes.Add(page.KeyAt(0) == "160216" ? "" : "кодът не стига до екрана: " + page.KeyAt(0));
            stockNotes.Add(page.AmountAt(0) == "5,400" ? "" : "наличното е " + page.AmountAt(0));

            page.Pick(0);
            stockNotes.Add(page.DetailCount == 1
                ? "" : $"произходът дава {page.DetailCount} реда вместо един");
            stockNotes.Add(page.DetailPartnerAt(0).Contains("Скрап ЕООД", StringComparison.Ordinal)
                ? "" : "долната таблица не сочи клиента: " + page.DetailPartnerAt(0));

            // Образуваното: един код, но два произхода зад него.
            page.UseStore(StoreKind.Formed);
            stockNotes.Add(page.RowCount == 1 ? "" : $"„Образуван“ показва {page.RowCount} реда вместо 1");
            stockNotes.Add(page.KeyAt(0) == "191202"
                ? "" : "образуваното не се води по своя код: " + page.KeyAt(0));
            stockNotes.Add(page.AmountAt(0) == "12,000" ? "" : "образуваното е " + page.AmountAt(0));

            page.Pick(0);
            stockNotes.Add(page.DetailCount == 2
                ? "" : $"зад образуваното стоят {page.DetailCount} партиди вместо две");

            var behind = page.DetailPartnerAt(0) + " " + page.DetailPartnerAt(1);
            stockNotes.Add(behind.Contains("Метал ООД", StringComparison.Ordinal) &&
                           behind.Contains("Скрап ЕООД", StringComparison.Ordinal)
                ? "" : "произходът на образуваното не сочи двамата клиенти: " + behind);

            // Изгледът по клиент обръща същите числа наопаки.
            page.UseView(byPartner: true);
            stockNotes.Add(page.RowCount == 2 ? "" : $"по клиент дава {page.RowCount} реда вместо два");
            stockNotes.Add(page.KeyAt(0) == "203344556"
                ? "" : "най-големият дял не е първи: " + page.KeyAt(0));

            page.Pick(0);
            stockNotes.Add(page.DetailPartnerAt(0).StartsWith("191202", StringComparison.Ordinal)
                ? "" : "по клиент долу не стои кодът: " + page.DetailPartnerAt(0));

            // Търсенето пресява и горе, и долу. ЕИК-ът е еднозначен: по него
            // остава един произход от двата.
            page.UseView(byPartner: false);
            page.UseSearch("204455667");
            stockNotes.Add(page.RowCount == 1 ? "" : "търсенето по ЕИК не пресява");

            page.Pick(0);
            stockNotes.Add(page.DetailCount == 1
                ? "" : $"търсенето не стеснява произхода: {page.DetailCount} реда");
            stockNotes.Add(page.DetailPartnerAt(0).Contains("Скрап ЕООД", StringComparison.Ordinal)
                ? "" : "остана грешният произход: " + page.DetailPartnerAt(0));

            // Търсенето нарочно пробва и наименованието на кода. Затова дума
            // като „метал“ хваща и клиента „Метал ООД“, и кода „черни метали“ —
            // и двата реда остават. Това е избор, а не пропуск: човек, който
            // пише „метал“, обикновено търси материала.
            page.UseSearch("метал");
            page.Pick(0);
            stockNotes.Add(page.DetailCount == 2
                ? "" : $"търсенето не хваща наименованието на кода: {page.DetailCount} реда");

            page.UseSearch("няма такъв");
            stockNotes.Add(page.RowCount == 0 ? "" : "безсмислено търсене връща редове");

            // Наличността е към дата: преди приемането складът е празен.
            page.UseSearch("");
            page.UseDate(new DateOnly(2026, 9, 1));
            stockNotes.Add(page.RowCount == 0 ? "" : "складът не е празен преди първото приемане");
            stockNotes.Add(page.SummaryText.Length > 0 ? "" : "празният склад не се обяснява");
        }
        catch (Exception ex)
        {
            stockError = ex.ToString();
        }
    });

    stockThread.SetApartmentState(ApartmentState.STA);
    stockThread.Start();
    stockThread.Join();

    if (stockError is not null) Console.WriteLine("     " + stockError);

    var stockProblems = stockNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in stockProblems) Console.WriteLine("     " + problem);

    Check("екранът „Склад“ се сглобява без изключение", stockError is null);
    Check($"направени са всичките {stockNotes.Count} проверки", stockNotes.Count == 20,
        $"{stockNotes.Count}");
    Check("складът отговаря чие е количеството", stockProblems.Count == 0,
        $"{stockProblems.Count} проблема");
}

// =============================================== Складът: отчетът от склада
Console.WriteLine();
Console.WriteLine("### ТЕСТ - съставеният от склада отчет минава проверката");
{
    var notes = new List<string>();

    var book = service.GetSchema("book");
    string Sample(string column) => SampleFor(book, BookNodes.Report, column);

    var header = BookProjection.Header("203344556", new LedgerPeriod(2026, 7), new LedgerPeriod(2026, 9));

    notes.Add(header.Quarter == 3 ? "" : $"тримесечието е {header.Quarter} вместо 3");
    notes.Add(header.From == new DateOnly(2026, 7, 1) && header.To == new DateOnly(2026, 9, 30)
        ? "" : "обявеният период не покрива тримесечието");

    var rows = new List<BookRow>
    {
        new(MoveKind.Received, new DateOnly(2026, 9, 3), "160216", 12.4m, "1",
            new Dictionary<string, string>
            {
                ["type_originator_code"] = Sample("type_originator_code"),
                ["name_originator"] = "Скрап ЕООД",
                ["pin_originator"] = "204455667"
            }),

        new(MoveKind.Treated, new DateOnly(2026, 9, 10), "160216", 15.0m, "1",
            new Dictionary<string, string>
            {
                ["op_id_code"] = Sample("op_id_code"),
                ["op_descr"] = "Рязане и сортиране"
            }),

        // Три знака след запетаята: точно тук се вижда дали количеството е
        // минало през double някъде по пътя.
        new(MoveKind.Generated, new DateOnly(2026, 9, 12), "191202", 12.345m, "1",
            new Dictionary<string, string> { ["waste_desc"] = "Мед на скрап" }),

        new(MoveKind.Submitted, new DateOnly(2026, 9, 20), "191202", 5.0m, "1",
            new Dictionary<string, string>
            {
                ["type_receiver_code"] = Sample("type_receiver_code"),
                ["name_receiver"] = "Леярна АД",
                ["op_descr"] = "Оползотворяване",
                ["is_for_reuse"] = Sample("is_for_reuse")
            }),

        // Втора площадка: тя иска свой запис-носител в отчета.
        new(MoveKind.Received, new DateOnly(2026, 9, 5), "170405", 3.0m, "2",
            new Dictionary<string, string>
            {
                ["type_originator_code"] = Sample("type_originator_code"),
                ["name_originator"] = "Метал ООД"
            })
    };

    var workbook = BookProjection.Build(book, header, rows);
    var result = service.Process(workbook);

    if (result.HasErrors)
        foreach (var issue in result.AllIssues.Where(i => i.Severity == IssueSeverity.Error).Take(6))
            Console.WriteLine($"     {issue.Where}: {issue.Message}");

    notes.Add(result.SchemaErrors.Count == 0
        ? "" : "срещу схемата: " + string.Join("; ", result.SchemaErrors.Take(3)));
    notes.Add(!result.HasErrors ? "" : "проверката намери грешки");
    notes.Add(result.CanExport ? "" : "отчетът не може да се изнесе");
    // Пет движения плюс по един запис-носител на площадка: носителят също е ред.
    notes.Add(result.TotalValidRows == 7 ? "" : $"приетите редове са {result.TotalValidRows} вместо 7");

    var xml = result.Xml?.ToString() ?? string.Empty;

    foreach (var kind in Moves.All)
        notes.Add(xml.Contains("<" + Moves.Node(kind), StringComparison.Ordinal)
            ? "" : $"липсва разделът {Moves.Node(kind)}");

    // Заглавната част стига до XML-а такава, каквато е обявена.
    notes.Add(xml.Contains("quarter=\"3\"", StringComparison.Ordinal) ? "" : "тримесечието не стига до XML");
    notes.Add(xml.Contains("eik=\"203344556\"", StringComparison.Ordinal) ? "" : "ЕИК не стига до XML");
    notes.Add(xml.Contains("period_start=\"2026-07-01\"", StringComparison.Ordinal)
        ? "" : "началото на периода не е ISO");
    notes.Add(xml.Contains("period_end=\"2026-09-30\"", StringComparison.Ordinal)
        ? "" : "краят на периода не е ISO");

    // Датата на реда също става ISO, а количеството пази трите си знака.
    notes.Add(xml.Contains("date_record=\"2026-09-03\"", StringComparison.Ordinal)
        ? "" : "датата на реда не е ISO");
    notes.Add(xml.Contains("amount_ton=\"12.345\"", StringComparison.Ordinal)
        ? "" : "количеството загуби третия си знак");

    // Двете площадки дават два записа-носителя.
    notes.Add(System.Text.RegularExpressions.Regex.Matches(xml, "<r_b_recovery_dispose ").Count == 2
        ? "" : "площадките не дават по един запис всяка");

    // Редовете за записване в отчет са същите, по възли.
    var byNode = BookProjection.Rows(book, rows);

    notes.Add(byNode.Count == 5 ? "" : $"възлите са {byNode.Count} вместо 5");
    notes.Add(byNode.GetValueOrDefault(Moves.Node(MoveKind.Received))?.Count == 2
        ? "" : "приетите редове по възли са сбъркани");
    notes.Add(byNode.GetValueOrDefault(BookNodes.Report)?.Count == 2
        ? "" : "записите-носители по възли са сбъркани");
    notes.Add(byNode.GetValueOrDefault(Moves.Node(MoveKind.Treated))?[0]
        .GetValueOrDefault("op_descr") == "Рязане и сортиране"
        ? "" : "колоните от схемата не стигат до реда");

    // Раздел без движения не се подава празен.
    var only = BookProjection.Build(book, header, [rows[0]]);
    notes.Add(only.Sheets.Count == 2
        ? "" : $"един раздел дава {only.Sheets.Count} листа вместо два");

    var problems = notes.Where(n => n.Length > 0).ToList();
    foreach (var problem in problems) Console.WriteLine("     " + problem);

    Check($"направени са всичките {notes.Count} проверки", notes.Count == 22, $"{notes.Count}");
    Check("складът съставя отчет, който минава проверката", problems.Count == 0,
        $"{problems.Count} проблема");
}

// =============================================== Складът: отчетът се записва
Console.WriteLine();
Console.WriteLine("### ТЕСТ - съставеният отчет се записва като обикновен отчет");
{
    var notes = new List<string>();
    var bookPath = Path.Combine(work, $"book_{Guid.NewGuid():N}.db");

    using var db = Workspace.Open(bookPath);

    var book = service.GetSchema("book");
    string Sample(string column) => SampleFor(book, BookNodes.Report, column);

    var companyId = db.Companies.Add("203344556", "Метал ООД");
    var september = new LedgerPeriod(2026, 9);

    db.Setup.SetStart(companyId, september);
    db.Setup.AddOpening(companyId, september, new OpeningEntry(StoreKind.Received, "160216", 4.0m));

    var batch = db.Batches.Add(companyId, new BatchDraft(
        "204455667", "Скрап ЕООД", "160216", new DateOnly(2026, 9, 3)));

    db.Ledger.Add(companyId, new MoveDraft(
        MoveKind.Received, new DateOnly(2026, 9, 3), "160216", 12.4m,
        Fields: new Dictionary<string, string>
        {
            ["type_originator_code"] = Sample("type_originator_code"),
            ["name_originator"] = "Скрап ЕООД",
            ["pin_originator"] = "204455667"
        },
        Draws: [new Draw(batch, 12.4m)]));

    // Движение в друг месец — не влиза в септемврийския отчет.
    db.Ledger.Add(companyId, new MoveDraft(
        MoveKind.Received, new DateOnly(2026, 10, 4), "160216", 9.0m));

    var all = db.Ledger.Read(companyId, "1");

    // Заварената наличност не се подава: тя не е приемане.
    var submittable = BookProjection.Submittable(all, september, september).ToList();

    notes.Add(submittable.Count == 1
        ? "" : $"за подаване са {submittable.Count} движения вместо едно");
    notes.Add(submittable.All(m => !m.Opening)
        ? "" : "заварената наличност влиза в отчета");
    notes.Add(BookProjection.Submittable(all, september, september.Next).Count() == 2
        ? "" : "по-широкият период не хваща и октомври");

    var rows = submittable
        .Select(m => new BookRow(m.Kind, m.Date, m.Ewc, m.Amount, m.Stagenum, db.Ledger.Fields(m.Id)))
        .ToList();

    var header = BookProjection.Header("203344556", september, september);
    var byNode = BookProjection.Rows(book, rows);

    var documentId = db.Documents.Create(
        "book", BookNodes.Report, "Оползотворяване сеп 2026",
        new Dictionary<string, string>
        {
            ["year"] = header.Year.ToString(),
            ["quarter"] = header.Quarter.ToString(),
            ["eik"] = header.Eik,
            ["type"] = header.Type,
            ["period_start"] = "01.09.2026",
            ["period_end"] = "30.09.2026"
        },
        DocumentOrigin.Ledger,
        companyId);

    foreach (var (node, values) in byNode) db.Documents.SaveRows(documentId, node, values);

    // Отчетът е обикновен отчет и се държи като такъв в списъка.
    var summary = db.Documents.Find(documentId);

    notes.Add(summary?.Origin == DocumentOrigin.Ledger ? "" : "произходът не е „от склада“");
    notes.Add(summary?.OriginLabel == "от склада" ? "" : "произходът няма надпис: " + summary?.OriginLabel);
    notes.Add(summary?.CompanyId == companyId ? "" : "отчетът не помни фирмата си");
    notes.Add(summary?.Status == DocumentStatus.Draft ? "" : "съставеният отчет не е чернова");

    var stored = db.Documents.ReadHeader(documentId);
    notes.Add(stored.GetValueOrDefault("quarter") == "3" ? "" : "тримесечието не се записа");
    notes.Add(stored.GetValueOrDefault("eik") == "203344556" ? "" : "ЕИК не се записа");
    notes.Add(stored.GetValueOrDefault("type") == "REPLACE" ? "" : "видът на подаването не се записа");

    // Редовете преживяват обиколката до базата и обратно.
    var received = db.Documents.ReadRows(documentId, Moves.Node(MoveKind.Received));

    notes.Add(received.Count == 1 ? "" : $"приетите редове са {received.Count} вместо един");
    notes.Add(received.Count == 1 && received[0].Values.GetValueOrDefault("amount_ton") == "12.4"
        ? "" : "количеството не преживя записа: " +
               (received.Count == 1 ? received[0].Values.GetValueOrDefault("amount_ton") : "—"));
    notes.Add(received.Count == 1 && received[0].Values.GetValueOrDefault("name_originator") == "Скрап ЕООД"
        ? "" : "колоната от схемата не преживя записа");
    notes.Add(received.Count == 1 && received[0].Values.GetValueOrDefault("stagenum") == "1"
        ? "" : "площадката не преживя записа");

    var carrier = db.Documents.ReadRows(documentId, BookNodes.Report);
    notes.Add(carrier.Count == 1 ? "" : $"записите-носители са {carrier.Count} вместо един");

    // Разделите без движения не се записват изобщо.
    notes.Add(db.Documents.ReadRows(documentId, Moves.Node(MoveKind.Submitted)).Count == 0
        ? "" : "празният раздел се записа");

    var problems = notes.Where(n => n.Length > 0).ToList();
    foreach (var problem in problems) Console.WriteLine("     " + problem);

    Check($"направени са всичките {notes.Count} проверки", notes.Count == 16, $"{notes.Count}");
    Check("съставеният отчет се записва цял", problems.Count == 0, $"{problems.Count} проблема");
}

// =============================================== Складът: внасяне от „Деметра“
Console.WriteLine();
Console.WriteLine("### ТЕСТ - дневник от „Деметра“ се чете и се превръща в движения");
{
    var notes = new List<string>();
    var demetraPath = Path.Combine(work, $"demetra_{Guid.NewGuid():N}.xlsx");

    // Файл със същата шапка като истинския износ.
    using (var wb = new XLWorkbook())
    {
        var ws = wb.AddWorksheet("Подробен дневник на отпадъците");

        string[] headers =
        [
            "Площадка", "Валиден запис", "Вид запис", "Код на отпадъка", "Наименование",
            "Категория", "Вид", "Дата", "Контрагент", "ЕИК/ЕГН", "Изплатена сума",
            "Основание", "Общо приети отпадъци", "Общо предадени отпадъци", "Транспорт"
        ];

        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];

        void Put(int row, string kind, string code, double serial, string partner, string eik,
            double inAmount, double outAmount, string valid = "Валиден")
        {
            ws.Cell(row, 1).Value = "Склад гара Искър";
            ws.Cell(row, 2).Value = valid;
            ws.Cell(row, 3).Value = kind;
            ws.Cell(row, 4).Value = code;
            ws.Cell(row, 5).Value = "Велпапе";
            ws.Cell(row, 8).Value = serial;
            ws.Cell(row, 9).Value = partner;
            ws.Cell(row, 10).Value = eik;
            ws.Cell(row, 12).Value = "договор 4";
            ws.Cell(row, 13).Value = inAmount;
            ws.Cell(row, 14).Value = outAmount;
            ws.Cell(row, 15).Value = "франко склад";
        }

        // 46204 = 01.07.2026 по броенето на Excel.
        Put(2, "Приети от контрагенти", "15 01 01", 46204, "Макс Трейд Спед ООД", "201249070", 3.15, 0);
        Put(3, "Приети от контрагенти", "15 01 01", 46205, "Физически лица", "", 1.168, 0);

        // Обработката носи количеството в колоната за ПРЕДАДЕНО.
        Put(4, "Третирани", "20 01 01", 46206, "Унитрейд ЕКО ООД", "202099392", 0, 55.81);

        Put(5, "Образувани", "15 01 02", 46207, "Унитрейд ЕКО ООД", "202099392", 1.159, 0);
        Put(6, "Предадени на контрагенти", "15 01 01", 46208, "Вадиас ООД", "130837015", 0, 50.405);
        Put(7, "Приети от контрагенти", "15 01 01", 46209, "Некой ООД", "111222333", 9.0, 0, "Невалиден");

        wb.SaveAs(demetraPath);
    }

    var file = DemetraWorkbook.Read(demetraPath);

    notes.Add(file.Problems.Count == 0 ? "" : "четенето се оплака: " + string.Join("; ", file.Problems));
    notes.Add(file.Rows.Count == 6 ? "" : $"прочетени са {file.Rows.Count} реда вместо 6");
    notes.Add(file.Kinds.Count == 4 ? "" : $"видовете са {file.Kinds.Count} вместо 4");
    notes.Add(file.Sites.SequenceEqual(["Склад гара Искър"]) ? "" : "площадката не се чете");

    // Датата идва като число и трябва да стане дата.
    notes.Add(file.Rows[0].Date == new DateOnly(2026, 7, 1)
        ? "" : "числото за дата не се превръща: " + file.Rows[0].Date);

    notes.Add(file.Rows[2].Out == 55.81m && file.Rows[2].In == 0m
        ? "" : "обработката не носи количеството в колоната за предадено");
    notes.Add(!file.Rows[5].Valid ? "" : "невалидният ред не се разпознава");
    notes.Add(file.Counts.GetValueOrDefault("Приети от контрагенти") == 3
        ? "" : "броенето по вид е сбъркано");

    // Съответствието превръща чуждите думи в наши движения.
    var plan = new ImportPlan(
        DemetraWorkbook.SuggestedKinds,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Склад гара Искър"] = "2" },
        DemetraWorkbook.SuggestedDirect);

    var outcome = LedgerImport.Apply(file, plan);

    notes.Add(outcome.Moves.Count == 5 ? "" : $"разбрани са {outcome.Moves.Count} движения вместо 5");
    notes.Add(outcome.Problems.Count == 1 ? "" : $"проблемите са {outcome.Problems.Count} вместо един");

    var treated = outcome.Moves.FirstOrDefault(m => m.Kind == MoveKind.Treated);
    notes.Add(treated?.Amount == 55.81m
        ? "" : "обработката не взе количеството от колоната за предадено");

    var handed = outcome.Moves.FirstOrDefault(m => m.Kind == MoveKind.Submitted);
    notes.Add(handed?.Amount == 50.405m ? "" : "предаването не взе количеството си");
    notes.Add(handed is { Direct: true } ? "" : "предаването не е отбелязано като пряко");

    // Прякото предаване вади от „Получен“, а не от „Образуван“.
    notes.Add(handed is not null && Moves.From(handed.Kind, handed.Direct) == StoreKind.Received
        ? "" : "прякото предаване не черпи от полученото");

    notes.Add(outcome.Moves.All(m => m.Stagenum == "2")
        ? "" : "съответствието на площадката не се прилага");
    notes.Add(outcome.Moves[0].Eik == "201249070" && outcome.Moves[0].Partner == "Макс Трейд Спед ООД"
        ? "" : "клиентът не стига до движението");
    notes.Add(outcome.Moves[0].Ewc == "150101" ? "" : "кодът не се свежда: " + outcome.Moves[0].Ewc);

    // Ефектът върху двата склада се смята ПРЕДИ записа.
    var effect = outcome.Effect();
    notes.Add(effect[StoreKind.Received] == SplitMath.Round(3.15m + 1.168m - 55.81m - 50.405m)
        ? "" : "ефектът върху „Получен“ е сбъркан: " + effect[StoreKind.Received]);
    notes.Add(effect[StoreKind.Formed] == 1.159m
        ? "" : "ефектът върху „Образуван“ е сбъркан: " + effect[StoreKind.Formed]);

    // Без съответствие за вид редовете се описват, вместо да се внесат наум.
    var blind = LedgerImport.Apply(file, ImportPlan.Empty);
    notes.Add(blind.Moves.Count == 0 && blind.Problems.Count == 6
        ? "" : $"без съответствие минаха {blind.Moves.Count} движения");

    // Истинският износ, ако е под ръка: числата от него са в коментара горе.
    var sample = FindDemetraSample(AppContext.BaseDirectory);

    if (sample is null)
    {
        Console.WriteLine("     (истинският износ от „Деметра“ го няма — пропуснато)");
    }
    else
    {
        var real = DemetraWorkbook.Read(sample);
        var got = LedgerImport.Apply(real, new ImportPlan(
            DemetraWorkbook.SuggestedKinds,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            DemetraWorkbook.SuggestedDirect));

        Check("истинският износ се чете цял", real.Rows.Count == 583, $"{real.Rows.Count} реда");
        Check("истинският износ се разбира цял", got.Moves.Count == 583 && got.Problems.Count == 0,
            $"{got.Moves.Count} движения, {got.Problems.Count} проблема");
        Check("приетото за юли е 1014,371 т",
            SplitMath.Round(got.Moves.Where(m => m.Kind == MoveKind.Received).Sum(m => m.Amount)) == 1014.371m,
            SplitMath.Amount(got.Moves.Where(m => m.Kind == MoveKind.Received).Sum(m => m.Amount)));
        Check("предаденото за юли е 1188,776 т",
            SplitMath.Round(got.Moves.Where(m => m.Kind == MoveKind.Submitted).Sum(m => m.Amount)) == 1188.776m,
            SplitMath.Amount(got.Moves.Where(m => m.Kind == MoveKind.Submitted).Sum(m => m.Amount)));

        // Прякото предаване е разликата между работещ и невъзможен склад:
        // без него „Образуван“ би тръгнал на минус 1095 тона.
        var live = got.Effect();
        Check("прякото предаване държи „Образуван“ положителен",
            live[StoreKind.Formed] > 0, SplitMath.Amount(live[StoreKind.Formed]));
    }

    var problems = notes.Where(n => n.Length > 0).ToList();
    foreach (var problem in problems) Console.WriteLine("     " + problem);

    Check($"направени са всичките {notes.Count} проверки", notes.Count == 20, $"{notes.Count}");
    Check("чуждият дневник става наши движения", problems.Count == 0, $"{problems.Count} проблема");
}

// Истинският износ от „Деметра“ стои извън хранилището на програмата — търси се
// нагоре по папките, както се търсят и схемите, и се пропуска, ако го няма.
static string? FindDemetraSample(string start)
{
    for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
    {
        var folder = Path.Combine(dir.FullName, "Demetra");
        if (!Directory.Exists(folder)) continue;

        var found = Directory.GetFiles(folder, "*.xlsx").FirstOrDefault();
        if (found is not null) return found;
    }

    return null;
}


// =============================================== Складът: произход със задна дата
Console.WriteLine();
Console.WriteLine("### ТЕСТ - заварено количество се отделя към клиент със задна дата");
{
    var notes = new List<string>();
    var splitPath2 = Path.Combine(work, $"retro_{Guid.NewGuid():N}.db");

    using var db = Workspace.Open(splitPath2);

    var companyId = db.Companies.Add("203344556", "Метал ООД");
    var september = new LedgerPeriod(2026, 9);

    // Заварено количество от предишни периоди: 40 тона, без да се знае чие е.
    var carried = db.Setup.AddOpening(companyId, september,
        new OpeningEntry(StoreKind.Received, "160216", 40m));

    notes.Add(db.Batches.Find(carried)?.Eik == "" ? "" : "заварената партида тръгва с клиент");

    // После се сеща: 25 от тях са били на Скрап ЕООД.
    var made = db.Setup.SplitOpening(companyId, carried, 25m, "204455667", "Скрап ЕООД", "протокол 44");

    notes.Add(made is not null ? "" : "отделянето не стана");

    var openings = db.Setup.Openings(companyId);

    notes.Add(openings.Count == 2 ? "" : $"заварените са {openings.Count} вместо две");
    notes.Add(openings.Sum(o => o.Amount) == 40m
        ? "" : "сборът се промени при отделянето: " + openings.Sum(o => o.Amount));

    var named = openings.FirstOrDefault(o => o.Batch.Id == made);
    notes.Add(named?.Amount == 25m ? "" : "отделеното количество е сбъркано");
    notes.Add(named?.Batch.Eik == "204455667" && named.Batch.PartnerName == "Скрап ЕООД"
        ? "" : "клиентът не стигна до новата партида");
    notes.Add(named?.Batch.Document == "протокол 44" ? "" : "документът не се пази");
    notes.Add(named?.Batch.Ewc == "160216" ? "" : "кодът не се пренася при отделяне");
    notes.Add(named?.Batch.Opening == true ? "" : "отделеното престана да е заварено");

    var rest = openings.FirstOrDefault(o => o.Batch.Id == carried);
    notes.Add(rest?.Amount == 15m ? "" : "остатъкът е сбъркан: " + rest?.Amount);

    // Наличността не се променя от преподреждането на произхода.
    notes.Add(StockMath.Opening(db.Ledger.Read(companyId), StoreKind.Received, september)
        .GetValueOrDefault("160216") == 40m
        ? "" : "наличността се промени при отделянето");

    // Повече от наличното не се отделя.
    notes.Add(db.Setup.SplitOpening(companyId, carried, 20m, "111222333", "Трети ООД") is null
        ? "" : "отдели се повече от наличното");

    // Отделяне от партида, от която вече е черпено, важи само за неизразходваното.
    db.Ledger.Add(companyId, new MoveDraft(
        MoveKind.Treated, new DateOnly(2026, 9, 4), "160216", 10m,
        Draws: [new Draw(carried, 10m)]));

    notes.Add(db.Setup.SplitOpening(companyId, carried, 10m, "111222333", "Трети ООД") is null
        ? "" : "отдели се от вече изразходваното");

    var last = db.Setup.SplitOpening(companyId, carried, 5m, "111222333", "Трети ООД");
    notes.Add(last is not null ? "" : "неизразходваното не се отделя");

    // Отделено докрай: старата партида няма какво да държи и си отива.
    notes.Add(db.Setup.Openings(companyId).Any(o => o.Batch.Id == carried)
        ? "" : "остатъкът от 10 т изчезна заедно с изразходваното");

    notes.Add(db.Setup.Openings(companyId).Sum(o => o.Amount) == 40m
        ? "" : "сборът на заварените се разпадна");

    // Обработката още сочи към старата партида — произходът ѝ не е пипнат.
    var shares = StockMath.Shares(
        db.Batches.List(companyId), db.Ledger.Read(companyId), db.Ledger.Links(companyId),
        StoreKind.Received, new DateOnly(2026, 9, 30));

    notes.Add(shares.Sum(s => s.Amount) == 30m
        ? "" : "наличното след обработката е " + shares.Sum(s => s.Amount));
    notes.Add(shares.Any(s => s.Batch.Eik == "204455667" && s.Amount == 25m)
        ? "" : "отделеното към клиент не се вижда в склада");

    var problems = notes.Where(n => n.Length > 0).ToList();
    foreach (var problem in problems) Console.WriteLine("     " + problem);

    Check($"направени са всичките {notes.Count} проверки", notes.Count == 18, $"{notes.Count}");
    Check("произходът се вписва със задна дата, без да мести количества",
        problems.Count == 0, $"{problems.Count} проблема");
}


// =============================================== Складът: прозорчето има изход
Console.WriteLine();
Console.WriteLine("### ТЕСТ - прозорчето за движение показва копчето за записване");
{
    var dlgNotes = new List<string>();
    string? dlgError = null;

    var dlgThread = new Thread(() =>
    {
        try
        {
            var dlgPath = Path.Combine(work, $"dialog_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(dlgPath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");
            var september = new LedgerPeriod(2026, 9);

            // Всичките четири вида: полетата им идват от схемата и са различен
            // брой — приемането има най-много и точно то беше счупеното.
            foreach (var kind in Moves.All)
            {
                using var dialog = new MoveDialog(service, db, companyId, "1", kind, september);
                dialog.CreateControl();
                dialog.PerformLayout();

                dlgNotes.Add(dialog.SaveReachable
                    ? "" : $"„{Moves.Title(kind)}“: копчето за записване е извън прозореца · {dialog.SaveTrace}");
            }

            // И на нисък екран: прозорецът се смалява до най-малкия си размер.
            using var small = new MoveDialog(service, db, companyId, "1", MoveKind.Received, september);
            small.CreateControl();
            small.Size = small.MinimumSize;
            small.PerformLayout();

            dlgNotes.Add(small.SaveReachable
                ? "" : "при най-малкия размер копчето излиза извън прозореца");
        }
        catch (Exception ex)
        {
            dlgError = ex.ToString();
        }
    });

    dlgThread.SetApartmentState(ApartmentState.STA);
    dlgThread.Start();
    dlgThread.Join();

    if (dlgError is not null) Console.WriteLine("     " + dlgError);

    var dlgProblems = dlgNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in dlgProblems) Console.WriteLine("     " + problem);

    Check("прозорчето се сглобява без изключение", dlgError is null);
    Check($"направени са всичките {dlgNotes.Count} проверки", dlgNotes.Count == 5, $"{dlgNotes.Count}");
    Check("копчето за записване се вижда при всички видове", dlgProblems.Count == 0,
        $"{dlgProblems.Count} проблема");
}


// =============================================== Складът: месец наведнъж
Console.WriteLine();
Console.WriteLine("### ТЕСТ - цял месец се въвежда в таблица, а не ред по ред");
{
    var sheetNotes = new List<string>();
    string? sheetError = null;

    var sheetThread = new Thread(() =>
    {
        try
        {
            var tablePath = Path.Combine(work, $"table_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(tablePath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");
            var september = new LedgerPeriod(2026, 9);

            var sample = SampleFor(service.GetSchema("book"), BookNodes.Report, "type_originator_code");

            // Три приемания наведнъж — както биха дошли от залепен блок.
            using (var sheet = new MoveSheetDialog(
                service, db, companyId, "1", MoveKind.Received, september))
            {
                var grid = sheet.Grid!;

                foreach (var (day, code, amount, partner, eik) in new[]
                {
                    ("03.09.2026", "160216", "12,400", "Скрап ЕООД", "204455667"),
                    ("10.09.2026", "160216", "8,000", "Метал груп ЕООД", "201455152"),
                    ("12.09.2026", "191202", "3,500", "Скрап ЕООД", "204455667")
                })
                {
                    grid.AppendRow(new Dictionary<string, string>
                    {
                        ["date_record"] = day,
                        ["ewc"] = code,
                        ["amount_ton"] = amount,
                        ["stagenum"] = "1",
                        ["type_originator_code"] = sample,
                        ["name_originator"] = partner,
                        ["pin_originator"] = eik
                    });
                }

                sheet.AcceptForTest();
                sheetNotes.Add(sheet.Saved ? "" : "таблицата не се записа");
            }

            var moves = db.Ledger.Read(companyId, "1");

            sheetNotes.Add(moves.Count == 3 ? "" : $"записаха се {moves.Count} движения вместо 3");
            sheetNotes.Add(moves.All(m => m.Kind == MoveKind.Received) ? "" : "видът се загуби");
            sheetNotes.Add(moves.Sum(m => m.Amount) == 23.9m
                ? "" : "сборът е " + moves.Sum(m => m.Amount));

            // Всяко приемане ражда своя партида с клиента си.
            var batches = db.Batches.List(companyId, "1");

            sheetNotes.Add(batches.Count == 3 ? "" : $"партидите са {batches.Count} вместо 3");
            sheetNotes.Add(batches.Count(b => b.Eik == "204455667") == 2
                ? "" : "клиентът не стигна до партидите");
            sheetNotes.Add(batches.All(b => b.Code.StartsWith("2026-", StringComparison.Ordinal))
                ? "" : "номерата на партидите са сбъркани");

            // Колоните от схемата пътуват до движението.
            sheetNotes.Add(db.Ledger.Fields(moves[0].Id).GetValueOrDefault("type_originator_code") == sample
                ? "" : "колоната от схемата не се записа");

            // Отваряне наново: редовете се връщат с номерата си.
            using (var again = new MoveSheetDialog(
                service, db, companyId, "1", MoveKind.Received, september))
            {
                sheetNotes.Add(again.RowCount == 3
                    ? "" : $"при отваряне наново редовете са {again.RowCount} вместо 3");

                // Поправка на количеството: същото движение, не второ.
                var grid = again.Grid!;
                grid.PutValue(0, "amount_ton", "15,000");

                again.AcceptForTest();
            }

            var after = db.Ledger.Read(companyId, "1");

            sheetNotes.Add(after.Count == 3 ? "" : $"поправката направи {after.Count} движения");
            sheetNotes.Add(after.Sum(m => m.Amount) == 26.5m
                ? "" : "поправеното количество е " + after.Sum(m => m.Amount));
            sheetNotes.Add(db.Batches.List(companyId, "1").Count == 3
                ? "" : "поправката заведе нова партида");
            sheetNotes.Add(after.Select(m => m.Id).OrderBy(id => id)
                .SequenceEqual(moves.Select(m => m.Id).OrderBy(id => id))
                ? "" : "поправката смени номерата на движенията");

            // Обработка наведнъж: разпределението се смята по FIFO при записа.
            using (var treat = new MoveSheetDialog(
                service, db, companyId, "1", MoveKind.Treated, september))
            {
                treat.Grid!.AppendRow(new Dictionary<string, string>
                {
                    ["date_record"] = "20.09.2026",
                    ["ewc"] = "160216",
                    ["amount_ton"] = "18,000",
                    ["stagenum"] = "1",
                    ["op_id_code"] = SampleFor(service.GetSchema("book"), BookNodes.Report, "op_id_code"),
                    ["op_descr"] = "Рязане"
                });

                treat.AcceptForTest();
            }

            var treated = db.Ledger.Read(companyId, "1").FirstOrDefault(m => m.Kind == MoveKind.Treated);

            sheetNotes.Add(treated is not null ? "" : "обработката не се записа");

            var draws = treated is null ? [] : db.Ledger.Draws(treated.Id);

            sheetNotes.Add(draws.Sum(d => d.Amount) == 18.0m
                ? "" : "разпределението не покрива обработката: " + draws.Sum(d => d.Amount));
            sheetNotes.Add(draws.Count == 2 ? "" : $"разпределението е от {draws.Count} партиди вместо две");

            // Най-старата партида първа — 15 от нея, останалото от следващата.
            var oldest = db.Batches.List(companyId, "1")
                .Where(b => b.Ewc == "160216")
                .OrderBy(b => b.ReceivedOn).First();

            sheetNotes.Add(draws.FirstOrDefault(d => d.BatchId == oldest.Id)?.Amount == 15.0m
                ? "" : "най-старата партида не се изчерпва първа");

            // Махнат от таблицата ред си отива и от склада.
            using (var trim = new MoveSheetDialog(
                service, db, companyId, "1", MoveKind.Received, september))
            {
                trim.Grid!.RemoveRows([2]);
                trim.AcceptForTest();
            }

            sheetNotes.Add(db.Ledger.Read(companyId, "1").Count(m => m.Kind == MoveKind.Received) == 2
                ? "" : "махнатият ред остана в склада");
        }
        catch (Exception ex)
        {
            sheetError = ex.ToString();
        }
    });

    sheetThread.SetApartmentState(ApartmentState.STA);
    sheetThread.Start();
    sheetThread.Join();

    if (sheetError is not null) Console.WriteLine("     " + sheetError);

    var sheetProblems = sheetNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in sheetProblems) Console.WriteLine("     " + problem);

    Check("таблицата се сглобява без изключение", sheetError is null);
    Check($"направени са всичките {sheetNotes.Count} проверки", sheetNotes.Count == 18,
        $"{sheetNotes.Count}");
    Check("месец се въвежда наведнъж и се поправя на място", sheetProblems.Count == 0,
        $"{sheetProblems.Count} проблема");
}


// =============================================== Складът: внасяне и изнасяне
Console.WriteLine();
Console.WriteLine("### ТЕСТ - таблицата се изнася в шаблон и се внася обратно");
{
    var ioNotes = new List<string>();
    string? ioError = null;

    var ioThread = new Thread(() =>
    {
        try
        {
            var ioPath = Path.Combine(work, $"io_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(ioPath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");
            var september = new LedgerPeriod(2026, 9);
            var template = Path.Combine(work, $"template_{Guid.NewGuid():N}.xlsx");

            var sample = SampleFor(service.GetSchema("book"), BookNodes.Report, "type_originator_code");

            // Два реда, изнесени в наш шаблон.
            using (var sheet = new MoveSheetDialog(
                service, db, companyId, "1", MoveKind.Received, september))
            {
                sheet.Grid!.AppendRow(new Dictionary<string, string>
                {
                    ["date_record"] = "03.09.2026",
                    ["ewc"] = "160216",
                    ["amount_ton"] = "12,400",
                    ["stagenum"] = "1",
                    ["type_originator_code"] = sample,
                    ["name_originator"] = "Скрап ЕООД",
                    ["pin_originator"] = "204455667"
                });

                sheet.Grid!.AppendRow(new Dictionary<string, string>
                {
                    ["date_record"] = "10.09.2026",
                    ["ewc"] = "191202",
                    ["amount_ton"] = "8,000",
                    ["stagenum"] = "1",
                    ["type_originator_code"] = sample,
                    ["name_originator"] = "Метал груп ЕООД"
                });

                ioNotes.Add(sheet.SaveTemplate(template) == 2 ? "" : "изнесените редове не са два");
            }

            ioNotes.Add(File.Exists(template) ? "" : "шаблонът не се записа");

            // И обратно — в празна таблица.
            using (var back = new MoveSheetDialog(
                service, db, companyId, "1", MoveKind.Received, september))
            {
                var added = back.LoadTemplate(template, out var problems);

                ioNotes.Add(added == 2 ? "" : $"внесени са {added} реда вместо два");
                ioNotes.Add(problems.Count == 0 ? "" : "внасянето се оплака: " + string.Join("; ", problems));

                var rows = back.Grid!.Snapshot();

                ioNotes.Add(rows.Count == 2 ? "" : $"в таблицата са {rows.Count} реда");
                ioNotes.Add(rows.Count == 2 && rows[0].GetValueOrDefault("ewc") == "160216"
                    ? "" : "кодът не преживя обиколката");
                // Количеството се връща знак по знак, а не превърнато през
                // число: изнесено „12,400“ се чете „12,400“, а не „12,4“.
                ioNotes.Add(rows.Count == 2 && rows[0].GetValueOrDefault("amount_ton") == "12,400"
                    ? "" : "количеството не преживя обиколката: " +
                           (rows.Count == 2 ? rows[0].GetValueOrDefault("amount_ton") : "—"));
                ioNotes.Add(rows.Count == 2 && rows[0].GetValueOrDefault("name_originator") == "Скрап ЕООД"
                    ? "" : "клиентът не преживя обиколката");
                ioNotes.Add(rows.Count == 2 && rows[0].GetValueOrDefault("date_record") == "03.09.2026"
                    ? "" : "датата не преживя обиколката: " +
                           (rows.Count == 2 ? rows[0].GetValueOrDefault("date_record") : "—"));

                // Внесеното е ново, а не залепено за чужд запис.
                ioNotes.Add(rows.All(r => r.GetValueOrDefault("__move", string.Empty).Length == 0)
                    ? "" : "внесеният ред носи чужд номер на движение");

                back.AcceptForTest();
            }

            ioNotes.Add(db.Ledger.Read(companyId, "1").Count == 2
                ? "" : "внесеното не се записа в склада");

            // Празен шаблон: само шапката.
            var blank = Path.Combine(work, $"blank_{Guid.NewGuid():N}.xlsx");

            using (var empty = new MoveSheetDialog(
                service, db, companyId, "1", MoveKind.Treated, september))
            {
                ioNotes.Add(empty.SaveTemplate(blank, blank: true) == 0 ? "" : "празният шаблон не е празен");
            }

            using (var check = new MoveSheetDialog(
                service, db, companyId, "1", MoveKind.Treated, september))
            {
                var added = check.LoadTemplate(blank, out var problems);

                ioNotes.Add(added == 0 ? "" : "от празен шаблон дойдоха редове");
                ioNotes.Add(problems.Count == 1 ? "" : "празният шаблон не се обяснява");
            }

            // Истинският износ от „Деметра“, ако е под ръка: юли 2026.
            if (FindDemetraSample(AppContext.BaseDirectory) is { } demetra)
            {
                using var july = new MoveSheetDialog(
                    service, db, companyId, "1", MoveKind.Received, new LedgerPeriod(2026, 7));

                var loaded = july.LoadDemetra(demetra);

                Check("от „Деметра“ влизат приеманията за юли", loaded.Rows == 553, $"{loaded.Rows} реда");
                Check("останалите видове не влизат в тази таблица", loaded.Others == 30,
                    $"{loaded.Others} реда");
                Check("нищо не остава неразчетено", loaded.Unread == 0, $"{loaded.Unread} реда");

                var first = july.Grid!.Snapshot().FirstOrDefault();

                Check("внесеният ред носи клиента си",
                    first?.GetValueOrDefault("name_originator", string.Empty).Length > 0,
                    first?.GetValueOrDefault("name_originator") ?? "—");

                // Отворен друг месец: файлът пак влиза цял. Месецът горе е
                // изглед, а не заграждение — всеки ред отива в своя месец при
                // записа, и точно това трябва да може да се направи, без първо
                // да се отваря месецът, който още не съществува.
                using var elsewhere = new MoveSheetDialog(
                    service, db, companyId, "1", MoveKind.Received, september);

                var other = elsewhere.LoadDemetra(demetra);

                Check("чужд месец не пречи на внасянето", other.Rows == 553, $"{other.Rows} реда");
                Check("казва се в кой месец ще отидат редовете",
                    other.Months.Count == 1 && other.Months[0] == new LedgerPeriod(2026, 7),
                    other.MonthList);

                // И се записват там, а не в отворения септември.
                elsewhere.AcceptForTest();

                Check("внесеното отива в своя месец",
                    db.Ledger.Read(companyId, "1").Count(m => LedgerPeriod.Of(m.Date) == new LedgerPeriod(2026, 7)) == 553,
                    $"{db.Ledger.Read(companyId, "1").Count(m => LedgerPeriod.Of(m.Date) == new LedgerPeriod(2026, 7))} движения");
            }
            else
            {
                Console.WriteLine("     (истинският износ от „Деметра“ го няма — пропуснато)");
            }
        }
        catch (Exception ex)
        {
            ioError = ex.ToString();
        }
    });

    ioThread.SetApartmentState(ApartmentState.STA);
    ioThread.Start();
    ioThread.Join();

    if (ioError is not null) Console.WriteLine("     " + ioError);

    var ioProblems = ioNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in ioProblems) Console.WriteLine("     " + problem);

    Check("внасянето и изнасянето минават без изключение", ioError is null);
    Check($"направени са всичките {ioNotes.Count} проверки", ioNotes.Count == 14, $"{ioNotes.Count}");
    Check("шаблонът се затваря в кръг", ioProblems.Count == 0, $"{ioProblems.Count} проблема");
}


// =============================================== Складът: минал месец се избира
Console.WriteLine();
Console.WriteLine("### ТЕСТ - минал месец се избира, дори да е празен");
{
    var pastNotes = new List<string>();
    string? pastError = null;

    var pastThread = new Thread(() =>
    {
        try
        {
            var pastPath = Path.Combine(work, $"past_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(pastPath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");

            using var page = new LedgerPage(service, db);

            var today = LedgerPeriod.Of(DateOnly.FromDateTime(DateTime.Today));

            // Празна база: досега списъкът предлагаше САМО текущия месец, и
            // затова юлски дневник нямаше къде да се внесе.
            pastNotes.Add(page.MonthChoices.Count > 1
                ? "" : $"празната база предлага {page.MonthChoices.Count} месец");

            // Всеки от последната година трябва да се избира.
            var year = today;

            for (var i = 0; i < 12; i++)
            {
                year = year.Previous;
                page.UseMonth(year);

                if (page.CurrentMonth == year) continue;

                pastNotes.Add($"месец {year.Title()} не се избира");
                break;
            }

            if (pastNotes.Count < 2) pastNotes.Add("");

            // И месец, който вече има движения, остава в списъка.
            db.Ledger.Add(companyId, new MoveDraft(
                MoveKind.Received, new DateOnly(2020, 3, 4), "160216", 1m));

            page.Refresh();

            pastNotes.Add(page.MonthChoices.Count > 12 ? "" : "старият месец с движение изпадна от списъка");

            page.UseMonth(new LedgerPeriod(2020, 3));
            pastNotes.Add(page.CurrentMonth == new LedgerPeriod(2020, 3)
                ? "" : "месец отпреди години не се избира");
        }
        catch (Exception ex)
        {
            pastError = ex.ToString();
        }
    });

    pastThread.SetApartmentState(ApartmentState.STA);
    pastThread.Start();
    pastThread.Join();

    if (pastError is not null) Console.WriteLine("     " + pastError);

    var pastProblems = pastNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in pastProblems) Console.WriteLine("     " + problem);

    Check("страницата се сглобява без изключение", pastError is null);
    Check($"направени са всичките {pastNotes.Count} проверки", pastNotes.Count == 4, $"{pastNotes.Count}");
    Check("минал месец се отваря и без движения в него", pastProblems.Count == 0,
        $"{pastProblems.Count} проблема");
}


// =============================================== Складът: месецът се търкаля
Console.WriteLine();
Console.WriteLine("### ТЕСТ - месецът се върти с една стъпка напред и назад");
{
    var rollNotes = new List<string>();
    string? rollError = null;

    var rollThread = new Thread(() =>
    {
        try
        {
            var rollPath = Path.Combine(work, $"roll_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(rollPath);

            db.Companies.Add("203344556", "Метал ООД");

            using var page = new LedgerPage(service, db);

            var today = LedgerPeriod.Of(DateOnly.FromDateTime(DateTime.Today));

            rollNotes.Add(page.CurrentMonth == today
                ? "" : "страницата не се отваря на текущия месец");

            // Назад и напред — по една крачка, без да се пипа списъкът.
            page.RollMonth(-1);
            rollNotes.Add(page.CurrentMonth == today.Previous
                ? "" : "назад не се стига до предишния месец: " + page.CurrentMonth?.Title());

            page.RollMonth(1);
            rollNotes.Add(page.CurrentMonth == today
                ? "" : "напред не се връща обратно: " + page.CurrentMonth?.Title());

            // Търкалянето минава и през декември — там годината се сменя.
            var back = today;

            for (var i = 0; i < 14; i++)
            {
                back = back.Previous;
                page.RollMonth(-1);

                if (page.CurrentMonth == back) continue;

                rollNotes.Add($"на {back.Title()} търкалянето се откъсна: {page.CurrentMonth?.Title()}");
                break;
            }

            if (rollNotes.Count < 4) rollNotes.Add("");

            rollNotes.Add(page.MonthChoices.Contains(back.Title())
                ? "" : "месецът, до който се стигна, не е в списъка");

            // Напред се стига отвъд края на годината — там списъкът сам не
            // предлага нищо, а януари трябва да се отваря през декември.
            page.UseMonth(today);

            var ahead = today;

            for (var i = 0; i < 12; i++)
            {
                ahead = ahead.Next;
                page.RollMonth(1);

                if (page.CurrentMonth == ahead) continue;

                rollNotes.Add($"напред се спря на {page.CurrentMonth?.Title()} вместо {ahead.Title()}");
                break;
            }

            if (rollNotes.Count < 6) rollNotes.Add("");

            // Година напред е краят: по-нататък няма какво да се въвежда.
            rollNotes.Add(!page.CanRoll(1) ? "" : "напред се търкаля отвъд година напред");

            page.RollMonth(1);
            rollNotes.Add(page.CurrentMonth == ahead
                ? "" : "угасената стрелка все пак премести месеца");

            // И назад има край — докъдето стига и списъкът.
            page.UseMonth(today);

            var floor = today;

            for (var i = 0; i < 40; i++)
            {
                page.RollMonth(-1);

                if (page.CurrentMonth is { } now && now < floor) floor = now;
            }

            rollNotes.Add(!page.CanRoll(-1) ? "" : "назад се търкаля без край");
            rollNotes.Add(page.CurrentMonth == floor ? "" : "назад месецът не спря там, докъдето стигна");

            // Обновяването пази отворения месец: иначе всяко пресмятане би
            // връщало човека там, откъдето е тръгнал.
            var kept = page.CurrentMonth;

            page.Refresh();
            rollNotes.Add(page.CurrentMonth == kept
                ? "" : "обновяването изгуби отворения месец: " + page.CurrentMonth?.Title());
        }
        catch (Exception ex)
        {
            rollError = ex.ToString();
        }
    });

    rollThread.SetApartmentState(ApartmentState.STA);
    rollThread.Start();
    rollThread.Join();

    if (rollError is not null) Console.WriteLine("     " + rollError);

    var rollProblems = rollNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in rollProblems) Console.WriteLine("     " + problem);

    Check("страницата се търкаля без изключение", rollError is null);
    Check($"направени са всичките {rollNotes.Count} проверки", rollNotes.Count == 11, $"{rollNotes.Count}");
    Check("месецът се върти с една стъпка и спира в краищата си", rollProblems.Count == 0,
        $"{rollProblems.Count} проблема");
}


// =============================================== Складът: третиране от склада, сметката
Console.WriteLine();
Console.WriteLine("### ТЕСТ - третирането от склада се смята без екран");
{
    var notes = new List<string>();

    var metal = new Batch(1, "2026-0001", "203344556", "Метал ООД", "160216", new DateOnly(2026, 9, 3));
    var skrap = new Batch(2, "2026-0002", "204455667", "Скрап ЕООД", "160216", new DateOnly(2026, 9, 10));
    var cable = new Batch(3, "2026-0003", "204455667", "Скрап ЕООД", "170411", new DateOnly(2026, 9, 11));

    var shares = new List<StockShare>
    {
        new("160216", skrap, 8.0m),
        new("160216", metal, 12.4m),
        new("170411", cable, 5.0m)
    };

    // Купчините са по код — единицата, с която мисли човекът.
    var piles = Treatment.Piles(shares);

    notes.Add(piles.Count == 2 ? "" : $"купчините са {piles.Count} вместо две");
    notes.Add(piles[0].Ewc == "160216" ? "" : "купчините не са подредени по код: " + piles[0].Ewc);
    notes.Add(piles[0].Available == 20.4m ? "" : $"сборът по кода е {piles[0].Available} вместо 20,4");
    notes.Add(piles[0].Batches == 2 ? "" : $"зад кода стоят {piles[0].Batches} партиди вместо две");
    notes.Add(piles[0].Oldest == new DateOnly(2026, 9, 3) ? "" : "най-старото приемане не се намира");
    notes.Add(piles[0].Age(new DateOnly(2026, 9, 30)) == 27
        ? "" : $"престоят е {piles[0].Age(new DateOnly(2026, 9, 30))} дни вместо 27");

    // Част от купчина се изчерпва от най-старото приемане нататък.
    var part = Treatment.Spread(piles[0], 15m);

    notes.Add(part.Count == 2 ? "" : $"частта се взема от {part.Count} партиди вместо две");
    notes.Add(part[0].Share.Batch.Id == metal.Id ? "" : "не се тръгва от най-старата партида");
    notes.Add(part[0].Amount == 12.4m ? "" : $"от първата се взема {part[0].Amount} вместо 12,4");
    notes.Add(part[1].Amount == 2.6m ? "" : $"от втората се взема {part[1].Amount} вместо 2,6");

    // По подразбиране: един ред за код, а произходът остава в дяловете.
    var orders = new List<TreatOrder> { new(piles[0], 15m), new(piles[1], 5m) };
    var merged = Treatment.Plan(orders);

    notes.Add(merged.Count == 2 ? "" : $"обединяването дава {merged.Count} реда вместо два");
    notes.Add(merged[0].Amount == 15.0m ? "" : $"количеството е {merged[0].Amount} вместо 15");
    notes.Add(merged[0].Draws.Count == 2 ? "" : $"произходът е от {merged[0].Draws.Count} партиди вместо две");
    notes.Add(merged[0].Draws.Sum(d => d.Amount) == 15.0m ? "" : "дяловете не събират количеството");
    notes.Add(merged.All(m => m.EwcOut.Length == 0) ? "" : "без общ код се записва изходен код");

    // Отделен ред за всяка партида: същите тонове, три реда.
    var split = Treatment.Plan(orders, perBatch: true);

    notes.Add(split.Count == 3 ? "" : $"по партиди дава {split.Count} реда вместо три");
    notes.Add(split.Sum(m => m.Amount) == merged.Sum(m => m.Amount)
        ? "" : "разделянето мени общото количество");
    notes.Add(split.All(m => m.Draws.Count == 1) ? "" : "ред по партида носи повече от един дял");

    // Общ изходен код: складът се вади по приетия, книгата носи избрания.
    var common = Treatment.Plan(orders, ewcOut: "191202");

    notes.Add(common.All(m => m.EwcOut == "191202") ? "" : "общият код не стига до движенията");
    notes.Add(common[0].Ewc == "160216" ? "" : "общият код измества кода, по който се вади складът");
    notes.Add(Treatment.Plan(orders, ewcOut: "160216")[0].EwcOut.Length == 0
        ? "" : "код, съвпаднал с приетия, се записва втори път");

    // Нулата значи „не сега“, а не грешка.
    var partial = Treatment.Plan([new TreatOrder(piles[0], 0m), new TreatOrder(piles[1], 5m)]);

    notes.Add(partial.Count == 1 ? "" : $"нулевият ред не отпада: {partial.Count} реда");
    notes.Add(Treatment.Check([new TreatOrder(piles[0], 0m), new TreatOrder(piles[1], 5m)]).Count == 0
        ? "" : "нулев ред до непразен се брои за грешка");

    // Над наличното, под нулата и празно — трите неща, които спират записа.
    var over = Treatment.Check([new TreatOrder(piles[0], 21m)]);

    notes.Add(over.Any(i => i.Kind == TreatProblem.OverStock) ? "" : "искане над наличното минава");
    notes.Add(over.Count > 0 && over[0].Text.Contains("20,400", StringComparison.Ordinal)
        ? "" : "съобщението не казва колко има: " + (over.Count > 0 ? over[0].Text : "няма"));

    notes.Add(Treatment.Check([new TreatOrder(piles[0], -1m)]).Any(i => i.Kind == TreatProblem.Negative)
        ? "" : "отрицателно количество минава");

    var nothing = Treatment.Check([new TreatOrder(piles[0], 0m)]);
    notes.Add(nothing.Count == 1 && nothing[0].Kind == TreatProblem.Nothing
        ? "" : "празно третиране минава");

    // Пътят на партидата: чете се от същите връзки, от които и наличността.
    var moves = new List<Move>
    {
        new(10, MoveKind.Received, new DateOnly(2026, 9, 3), "160216", 12.4m),
        new(11, MoveKind.Treated, new DateOnly(2026, 9, 12), "160216", 10m),
        new(12, MoveKind.Generated, new DateOnly(2026, 9, 13), "191202", 8m)
    };

    var links = new List<LedgerLink>
    {
        new(10, metal.Id, 12.4m),
        new(11, metal.Id, 10m),
        new(12, metal.Id, 8m),
        new(11, skrap.Id, 1m)
    };

    var trail = StockMath.Trace(metal, moves, links);

    notes.Add(trail.Steps.Count == 3 ? "" : $"пътят е от {trail.Steps.Count} стъпки вместо три");
    notes.Add(trail.Steps[0].Move.Kind == MoveKind.Received ? "" : "пътят не тръгва от приемането");
    notes.Add(trail.Total(MoveKind.Treated) == 10m ? "" : $"третираното е {trail.Total(MoveKind.Treated)}");
    notes.Add(trail.InStore(StoreKind.Received) == 2.4m
        ? "" : $"в „Получен“ остават {trail.InStore(StoreKind.Received)} вместо 2,4");
    notes.Add(trail.InStore(StoreKind.Formed) == 8m
        ? "" : $"в „Образуван“ стоят {trail.InStore(StoreKind.Formed)} вместо 8");
    notes.Add(StockMath.Trace(cable, moves, links).IsEmpty ? "" : "партида без движения има път");

    // Изходният код стига до книгата, без да мени по какво се вади складът.
    var treated = new Move(20, MoveKind.Treated, new DateOnly(2026, 9, 12), "160216", 5m, EwcOut: "191202");

    notes.Add(treated.Reported == "191202" ? "" : "движението не носи изходния си код: " + treated.Reported);
    notes.Add(StockMath.Balance([treated], StoreKind.Received, "160216", new DateOnly(2026, 9, 30)) == -5m
        ? "" : "складът не се вади по приетия код");

    var bookRows = BookProjection.Rows(service.GetSchema("book"),
    [
        new BookRow(MoveKind.Treated, new DateOnly(2026, 9, 12), "160216", 5m, "1",
            new Dictionary<string, string>(StringComparer.Ordinal), "191202")
    ]);

    var treatedRow = bookRows.GetValueOrDefault(Moves.Node(MoveKind.Treated))?.FirstOrDefault();

    notes.Add(treatedRow?.GetValueOrDefault("ewc") == "191202"
        ? "" : "в отчета отива приетият код: " + (treatedRow?.GetValueOrDefault("ewc") ?? "нищо"));

    var problems = notes.Where(n => n.Length > 0).ToList();
    foreach (var problem in problems) Console.WriteLine("     " + problem);

    Check($"направени са всичките {notes.Count} проверки", notes.Count == 36, $"{notes.Count}");
    Check("третирането от склада се смята вярно", problems.Count == 0, $"{problems.Count} проблема");
}

// =============================================== Складът: третиране от екрана
Console.WriteLine();
Console.WriteLine("### ТЕСТ - складът третира избраното, филтрира го и показва сборове");
{
    var treatNotes = new List<string>();
    string? treatError = null;

    var treatThread = new Thread(() =>
    {
        try
        {
            var treatPath = Path.Combine(work, $"treat_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(treatPath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");

            var metal = db.Batches.Add(companyId, new BatchDraft(
                "203344556", "Метал ООД", "160216", new DateOnly(2026, 9, 3), Document: "протокол 118"));
            var skrap = db.Batches.Add(companyId, new BatchDraft(
                "204455667", "Скрап ЕООД", "160216", new DateOnly(2026, 9, 10)));
            var cable = db.Batches.Add(companyId, new BatchDraft(
                "204455667", "Скрап ЕООД", "170411", new DateOnly(2026, 9, 11)));

            db.Ledger.Add(companyId, new MoveDraft(
                MoveKind.Received, new DateOnly(2026, 9, 3), "160216", 12.4m,
                Draws: [new Draw(metal, 12.4m)]));
            db.Ledger.Add(companyId, new MoveDraft(
                MoveKind.Received, new DateOnly(2026, 9, 10), "160216", 8.0m,
                Draws: [new Draw(skrap, 8.0m)]));
            db.Ledger.Add(companyId, new MoveDraft(
                MoveKind.Received, new DateOnly(2026, 9, 11), "170411", 5.0m,
                Draws: [new Draw(cable, 5.0m)]));

            using var page = new StockPage(service, db);

            page.UseStore(StoreKind.Received);
            page.UseView(byPartner: false);
            page.UseDate(new DateOnly(2026, 9, 30));

            treatNotes.Add(page.TreatOffered ? "" : "в склад „Получен“ третирането не се предлага");

            // От „Образуван“ се предава, а не се третира.
            page.UseStore(StoreKind.Formed);
            treatNotes.Add(page.TreatOffered ? "третирането се предлага и в „Образуван“" : "");
            page.UseStore(StoreKind.Received);

            // Сборът на видимото стои под таблицата.
            treatNotes.Add(page.SummaryLine.Contains("25,400", StringComparison.Ordinal)
                ? "" : "сборът на видимото не се изписва: " + page.SummaryLine);

            // Филтър по код.
            treatNotes.Add(page.UseCodeFilter("160216") ? "" : "кодът не се избира във филтъра");
            treatNotes.Add(page.RowCount == 1 ? "" : $"филтърът по код дава {page.RowCount} реда");
            treatNotes.Add(page.SummaryLine.Contains("20,400", StringComparison.Ordinal)
                ? "" : "сборът не следва филтъра: " + page.SummaryLine);
            page.UseCodeFilter("");

            // Филтър по клиент: Скрап ЕООД носи два кода.
            treatNotes.Add(page.UsePartnerFilter("204455667") ? "" : "клиентът не се избира във филтъра");
            treatNotes.Add(page.RowCount == 2 ? "" : $"филтърът по клиент дава {page.RowCount} реда");
            treatNotes.Add(page.SummaryLine.Contains("13,000", StringComparison.Ordinal)
                ? "" : "сборът по клиент е друг: " + page.SummaryLine);
            page.UsePartnerFilter("");

            // Престой: към 30.09 само приетото на 3-и стои повече от 25 дни.
            page.UseDays("25");
            treatNotes.Add(page.RowCount == 1 ? "" : $"филтърът по престой дава {page.RowCount} реда");
            treatNotes.Add(page.SummaryLine.Contains("12,400", StringComparison.Ordinal)
                ? "" : "престоят не свежда сбора: " + page.SummaryLine);
            page.UseDays("");

            // Количество: филтърът пита за реда, не за дяла.
            page.UseLeast("6");
            treatNotes.Add(page.RowCount == 1 ? "" : $"филтърът по количество дава {page.RowCount} реда");
            treatNotes.Add(page.KeyAt(0) == "160216" ? "" : "остана грешният ред: " + page.KeyAt(0));

            page.ClearAllFilters();
            treatNotes.Add(page.RowCount == 2 ? "" : "изчистването не връща всички редове");

            // Подредба по заглавие: щракването подрежда наличността, а не
            // текста в клетките — иначе „5,000“ би застанало след „12,400“.
            treatNotes.Add(page.KeyAt(0) == "160216" ? "" : "без подредба редовете не са по код");

            page.SortMaster(2);
            treatNotes.Add(page.AmountAt(0) == "20,400"
                ? "" : "първото щракване по количество не слага най-голямото първо: " + page.AmountAt(0));

            page.SortMaster(2);
            treatNotes.Add(page.AmountAt(0) == "5,000"
                ? "" : "второто щракване не обръща посоката: " + page.AmountAt(0));
            treatNotes.Add(page.MasterGlyph(2) == "Ascending"
                ? "" : "стрелката не сочи посоката: " + page.MasterGlyph(2));

            page.SortMaster(4);
            treatNotes.Add(page.KeyAt(0) == "160216"
                ? "" : "по дата не тръгва от най-старото: " + page.KeyAt(0));
            treatNotes.Add(page.MasterGlyph(2) == "None" ? "" : "стрелката остава на старата колона");

            // Колоната с копчето не е за подреждане.
            page.SortMaster(5);
            treatNotes.Add(page.MasterGlyph(5) == "None" ? "" : "копчето се прави на колона за подредба");
            treatNotes.Add(page.KeyAt(0) == "160216" ? "" : "щракването по копчето пренареди редовете");

            // Долната таблица се подрежда отделно от горната.
            page.Pick(0);
            treatNotes.Add(page.DetailAmountAt(0) == "12,400"
                ? "" : "произходът не тръгва от най-старата партида: " + page.DetailAmountAt(0));

            // Пети стълб, а не трети: между документа и количеството влязоха
            // датата на предаването и получателят (виждат се само в летописа,
            // но стоят в таблицата).
            page.SortDetail(5);
            treatNotes.Add(page.DetailAmountAt(0) == "12,400"
                ? "" : "долу най-голямото не е първо: " + page.DetailAmountAt(0));

            page.SortDetail(5);
            treatNotes.Add(page.DetailAmountAt(0) == "8,000"
                ? "" : "долу посоката не се обръща: " + page.DetailAmountAt(0));

            // Обратно към подредбата по код: оттук нататък тестът разчита на нея.
            page.SortMaster(0);
            treatNotes.Add(page.KeyAt(0) == "160216" ? "" : "по код не тръгва от първия: " + page.KeyAt(0));


            // Горният ред е код; избраното зад него са партидите му.
            page.SelectMaster(0);
            var chosen = page.Selection();

            treatNotes.Add(chosen.Count == 2 ? "" : $"зад кода стоят {chosen.Count} дяла вместо два");
            treatNotes.Add(chosen.Sum(s => s.Amount) == 20.4m
                ? "" : $"избраното е {chosen.Sum(s => s.Amount)} т вместо 20,4");
            treatNotes.Add(page.ChosenText.Contains("20,400", StringComparison.Ordinal)
                ? "" : "сборът на избраното не се изписва: " + page.ChosenText);

            // Прозорчето говори за кодове: два дяла под един код са един ред.
            using (var dialog = new TreatDialog(
                       service, db, companyId, "1", chosen, new DateOnly(2026, 9, 30)))
            {
                treatNotes.Add(dialog.RowCount == 1 ? "" : $"прозорчето показва {dialog.RowCount} реда вместо един");
                treatNotes.Add(dialog.StockAt(0) == "20,400" ? "" : "купчината е " + dialog.StockAt(0));
                treatNotes.Add(dialog.BatchesAt(0) == "2" ? "" : "партидите зад купчината са " + dialog.BatchesAt(0));
                treatNotes.Add(dialog.TakeAt(0) == "20,400" ? "" : "не се предлага цялата купчина");
                treatNotes.Add(dialog.TotalsText.Contains("20,400", StringComparison.Ordinal)
                    ? "" : "сборът в прозорчето липсва: " + dialog.TotalsText);

                dialog.UseTake(0, 15m);
                treatNotes.Add(dialog.Planned().Count == 1 ? "" : "частичното третиране дава повече от един ред");
                treatNotes.Add(dialog.Planned()[0].Draws.Count == 2
                    ? "" : "частта не се разпределя по двете партиди");

                // Кой е донесъл количеството зад избрания код — и колко от него
                // ще бъде взето сега.
                treatNotes.Add(dialog.CanGoFullScreen ? "" : "прозорчето не се разтваря на цял екран");

                dialog.PickPile(0);

                treatNotes.Add(dialog.OriginCount == 2
                    ? "" : $"произходът показва {dialog.OriginCount} реда вместо два");
                treatNotes.Add(dialog.OriginPartnerAt(0).Contains("Метал ООД", StringComparison.Ordinal)
                    ? "" : "произходът не тръгва от най-старата доставка: " + dialog.OriginPartnerAt(0));
                treatNotes.Add(dialog.OriginPartnerAt(1).Contains("Скрап ЕООД", StringComparison.Ordinal)
                    ? "" : "вторият произход не е на място: " + dialog.OriginPartnerAt(1));
                treatNotes.Add(dialog.OriginTakeAt(0) == "12,400"
                    ? "" : "не се вижда колко ще се вземе от първата: " + dialog.OriginTakeAt(0));
                treatNotes.Add(dialog.OriginTakeAt(1) == "2,600"
                    ? "" : "остатъкът не пада върху втората: " + dialog.OriginTakeAt(1));
                treatNotes.Add(dialog.BehindText.Contains("2 клиента", StringComparison.Ordinal)
                    ? "" : "не се казва от колко клиента е: " + dialog.BehindText);

                // По-малкото количество не стига до втората доставка.
                dialog.UseTake(0, 5m);
                treatNotes.Add(dialog.OriginTakeAt(1).Length == 0
                    ? "" : "втората партида се пипа, преди първата да свърши");
                dialog.UseTake(0, 15m);

                dialog.UsePerBatch(true);
                treatNotes.Add(dialog.Planned().Count == 2 ? "" : "отметката не дели по партиди");
                dialog.UsePerBatch(false);

                // Общият изходен код: менюто е заключено, докато е вдигната
                // отметката „същия код“.
                treatNotes.Add(dialog.OutCodeOffered ? "„същият код“ не заключва менюто" : "");
                dialog.UseOutCode("191202");
                treatNotes.Add(dialog.OutCodeOffered ? "" : "менюто остава заключено след свалянето на отметката");
                treatNotes.Add(dialog.Planned()[0].EwcOut == "191202"
                    ? "" : "общият код не стига до движението");
                treatNotes.Add(dialog.Planned()[0].Ewc == "160216"
                    ? "" : "общият код измества кода, по който се вади складът");
                dialog.UseOutCode("");

                // Описанието на дейността се пише само.
                treatNotes.Add(dialog.UseOperation(0) ? "" : "дейността няма меню");
                treatNotes.Add(dialog.FieldText("op_descr").Length > 0
                    ? "" : "описанието на дейността не се попълва само");

                treatNotes.Add(dialog.SaveReachable ? "" : "копчето „Третирай“ не се побира в прозореца");

                // Същата ивица за датата, както при образуването: тя е
                // единственото, което изглежда вярно, когато е сгрешено.
                treatNotes.Add(TreatDialog.DateCaption == "Дата на третиране"
                    ? "" : "датата не е озаглавена: " + TreatDialog.DateCaption);
                treatNotes.Add(dialog.WhenText.Contains("септември 2026", StringComparison.Ordinal)
                    ? "" : "не се казва в кой месец влиза третирането: " + dialog.WhenText);
                treatNotes.Add(dialog.DateFits ? "" : "датата не се побира в дясната рамка");

                dialog.UseDate(new DateOnly(2026, 10, 5));
                treatNotes.Add(dialog.WhenText.Contains("октомври 2026", StringComparison.Ordinal)
                    ? "" : "смяната на датата не сменя месеца: " + dialog.WhenText);
                dialog.UseDate(new DateOnly(2026, 9, 30));
            }

            // Влаченето по редовете избира какво се третира.
            page.SelectMaster(0, 1);

            using (var many = new TreatDialog(
                       service, db, companyId, "1", page.Selection(), new DateOnly(2026, 9, 30)))
            {
                treatNotes.Add(many.RowCount == 2 ? "" : $"двата кода дават {many.RowCount} реда");
                treatNotes.Add(many.TakeAt(0) == "20,400" && many.TakeAt(1) == "5,000"
                    ? "" : "прозорчето не се отваря с всичко избрано");

                // Влачене само по втория ред: първият изпада.
                many.DragPiles(1);

                treatNotes.Add(many.TakeAt(0).Length == 0
                    ? "" : "изваденият ред още носи количество: " + many.TakeAt(0));
                treatNotes.Add(many.TakeAt(1) == "5,000" ? "" : "избраният ред изгуби количеството си");
                treatNotes.Add(many.Planned().Count == 1 && many.Planned()[0].Ewc == "170411"
                    ? "" : "третира се нещо, което не е избрано");
                treatNotes.Add(many.TotalsText.Contains("5,000", StringComparison.Ordinal)
                    ? "" : "сборът не следва избора: " + many.TotalsText);

                // Написаното преживява изваждането и връщането на реда.
                many.UseTake(1, 2m);
                many.DragPiles(0, 1);

                treatNotes.Add(many.TakeAt(1) == "2,000"
                    ? "" : "написаното количество се губи при връщане в избора: " + many.TakeAt(1));
                treatNotes.Add(many.TakeAt(0) == "20,400"
                    ? "" : "върнатият ред не взема цялата купчина: " + many.TakeAt(0));

                // Нищо избрано значи нищо за третиране.
                many.DragPiles();
                treatNotes.Add(many.Planned().Count == 0 ? "" : "празен избор пак третира");
            }

            // Записът минава по пътя на копчето: сглобено, после записано.
            var piles = Treatment.Piles(chosen);

            var written = page.Apply(
                Treatment.Plan([new TreatOrder(piles[0], 20.4m)], ewcOut: "191202"),
                new DateOnly(2026, 9, 12),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["op_id_code"] = "R04",
                    ["op_descr"] = "Рециклиране"
                });

            treatNotes.Add(written == 1 ? "" : $"записани са {written} движения вместо едно");

            var treated = db.Ledger.Read(companyId, "1").Where(m => m.Kind == MoveKind.Treated).ToList();

            treatNotes.Add(treated.Count == 1 ? "" : $"в склада стоят {treated.Count} третирания");
            treatNotes.Add(treated.Count == 1 && treated[0].Amount == 20.4m
                ? "" : "третираното количество не е 20,4");
            treatNotes.Add(treated.Count == 1 && treated[0].Ewc == "160216"
                ? "" : "третирането не е по кода на полученото");
            treatNotes.Add(treated.Count == 1 && treated[0].Reported == "191202"
                ? "" : "изходният код не преживя записа");

            var draws = treated.Count == 1 ? db.Ledger.Draws(treated[0].Id) : [];

            treatNotes.Add(draws.Count == 2 ? "" : $"третирането сочи {draws.Count} партиди вместо две");
            treatNotes.Add(draws.Any(d => d.BatchId == metal && d.Amount == 12.4m)
                ? "" : "делът на Метал ООД не е запазен");
            treatNotes.Add(draws.Any(d => d.BatchId == skrap && d.Amount == 8.0m)
                ? "" : "делът на Скрап ЕООД не е запазен");
            treatNotes.Add(treated.Count == 1 &&
                           db.Ledger.Fields(treated[0].Id).GetValueOrDefault("op_id_code") == "R04"
                ? "" : "дейността не стига до движението");

            // Складът вече го няма: остава само другият код.
            page.Refresh();
            page.UseDate(new DateOnly(2026, 9, 30));

            treatNotes.Add(page.RowCount == 1 ? "" : $"след третирането горе стоят {page.RowCount} реда");
            treatNotes.Add(page.KeyAt(0) == "170411" ? "" : "останалият код е " + page.KeyAt(0));

            // Долният ред е самата партида: третира се частично.
            page.Pick(0);
            page.SelectDetail(0);

            var one = page.Selection();

            treatNotes.Add(one.Count == 1 ? "" : $"долу е избрано {one.Count} вместо един дял");

            page.Apply(
                Treatment.Plan([new TreatOrder(Treatment.Piles(one)[0], 2m)]),
                new DateOnly(2026, 9, 13));

            page.Refresh();
            page.UseDate(new DateOnly(2026, 9, 30));

            treatNotes.Add(page.AmountAt(0) == "3,000"
                ? "" : "частичното третиране оставя " + page.AmountAt(0));

            // И целият път на партидата, отворен от склада.
            using var trail = new BatchDialog(
                db, companyId, "1", db.Batches.Find(cable)!,
                new Dictionary<string, string>(StringComparer.Ordinal));

            treatNotes.Add(trail.StepCount == 2
                ? "" : $"пътят на партидата е {trail.StepCount} стъпки вместо две");
            treatNotes.Add(trail.KindAt(0) == Moves.Title(MoveKind.Received)
                ? "" : "пътят не тръгва от приемането: " + trail.KindAt(0));
            treatNotes.Add(trail.ShareAt(1) == "2,000" ? "" : "третираният дял е " + trail.ShareAt(1));
            treatNotes.Add(trail.SummaryText.Contains("3,000", StringComparison.Ordinal)
                ? "" : "пътят не казва какво още стои: " + trail.SummaryText);
        }
        catch (Exception ex)
        {
            treatError = ex.ToString();
        }
    });

    treatThread.SetApartmentState(ApartmentState.STA);
    treatThread.Start();
    treatThread.Join();

    if (treatError is not null) Console.WriteLine("     " + treatError);

    var treatProblems = treatNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in treatProblems) Console.WriteLine("     " + problem);

    Check("третирането от склада се сглобява без изключение", treatError is null);
    Check($"направени са всичките {treatNotes.Count} проверки", treatNotes.Count == 82,
        $"{treatNotes.Count}");
    Check("складът третира избраното и показва сборовете", treatProblems.Count == 0,
        $"{treatProblems.Count} проблема");
}

// =============================================== Складът: служебните колони при запис
Console.WriteLine();
Console.WriteLine("### ТЕСТ - кодът за отчета и прякото предаване преживяват записа");
{
    var keepNotes = new List<string>();
    string? keepError = null;

    var keepThread = new Thread(() =>
    {
        try
        {
            var keepPath = Path.Combine(work, $"keep_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(keepPath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");
            var september = new LedgerPeriod(2026, 9);

            var batch = db.Batches.Add(companyId, new BatchDraft(
                "203344556", "Метал ООД", "160216", new DateOnly(2026, 9, 3)));

            db.Ledger.Add(companyId, new MoveDraft(
                MoveKind.Received, new DateOnly(2026, 9, 3), "160216", 10m,
                Draws: [new Draw(batch, 10m)]));

            var treatedId = db.Ledger.Add(companyId, new MoveDraft(
                MoveKind.Treated, new DateOnly(2026, 9, 12), "160216", 4m,
                Draws: [new Draw(batch, 4m)], EwcOut: "191202"));

            db.Ledger.Add(companyId, new MoveDraft(
                MoveKind.Submitted, new DateOnly(2026, 9, 14), "160216", 2m,
                Direct: true, Draws: [new Draw(batch, 2m)]));

            // Прозорчето за един ред показва кода за отчета и го връща обратно.
            using (var single = new MoveDialog(
                       service, db, companyId, "1", MoveKind.Treated, september, treatedId))
            {
                keepNotes.Add(single.OutCodeText.StartsWith("191202", StringComparison.Ordinal)
                    ? "" : "прозорчето не показва кода за отчета: " + single.OutCodeText);
            }

            // Таблицата на месеца: записана без промени, тя не бива да губи
            // онова, което не е нейно поле.
            using (var sheet = new MoveSheetDialog(
                       service, db, companyId, "1", MoveKind.Treated, september))
            {
                keepNotes.Add(sheet.RowCount == 1 ? "" : $"таблицата показва {sheet.RowCount} реда вместо един");
                sheet.AcceptForTest();
            }

            var treated = db.Ledger.Read(companyId, "1").FirstOrDefault(m => m.Kind == MoveKind.Treated);

            keepNotes.Add(treated?.EwcOut == "191202"
                ? "" : "кодът за отчета се губи при запис от таблицата: " + (treated?.EwcOut ?? "няма ред"));
            keepNotes.Add(treated?.Ewc == "160216" ? "" : "приетият код се измести при записа");

            using (var sheet = new MoveSheetDialog(
                       service, db, companyId, "1", MoveKind.Submitted, september))
            {
                sheet.AcceptForTest();
            }

            var submitted = db.Ledger.Read(companyId, "1").FirstOrDefault(m => m.Kind == MoveKind.Submitted);

            keepNotes.Add(submitted?.Direct == true
                ? "" : "прякото предаване се губи при запис от таблицата");

            // И складът си остава верен: прякото предаване вади от „Получен“.
            keepNotes.Add(StockMath.Stock(db.Ledger.Read(companyId, "1"), StoreKind.Received,
                              new DateOnly(2026, 9, 30)).GetValueOrDefault("160216") == 4m
                ? "" : "наличността след записа не е 4 т");
        }
        catch (Exception ex)
        {
            keepError = ex.ToString();
        }
    });

    keepThread.SetApartmentState(ApartmentState.STA);
    keepThread.Start();
    keepThread.Join();

    if (keepError is not null) Console.WriteLine("     " + keepError);

    var keepProblems = keepNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in keepProblems) Console.WriteLine("     " + problem);

    Check("прозорчетата се сглобяват без изключение", keepError is null);
    Check($"направени са всичките {keepNotes.Count} проверки", keepNotes.Count == 6, $"{keepNotes.Count}");
    Check("служебните колони преживяват записа", keepProblems.Count == 0, $"{keepProblems.Count} проблема");
}

// =============================================== Складът: третираното чака
Console.WriteLine();
Console.WriteLine("### ТЕСТ - склад „Третиран“ държи третираното, докато се обяви образуваното");
{
    var notes = new List<string>();

    var metal = new Batch(1, "2026-0001", "203344556", "Метал ООД", "160216", new DateOnly(2026, 9, 1));
    var skrap = new Batch(2, "2026-0002", "204455667", "Скрап ЕООД", "170411", new DateOnly(2026, 9, 2));
    var all = new List<Batch> { metal, skrap };

    var end = new DateOnly(2026, 9, 30);

    // Прието и третирано: количеството излиза от „Получен“ и влиза в „Третиран“.
    var moves = new List<Move>
    {
        new(1, MoveKind.Received, new DateOnly(2026, 9, 1), "160216", 20.0m),
        new(2, MoveKind.Treated, new DateOnly(2026, 9, 10), "160216", 15.0m)
    };

    var links = new List<LedgerLink> { new(1, 1, 20.0m), new(2, 1, 15.0m) };

    notes.Add(StockMath.Stock(moves, StoreKind.Received, end).GetValueOrDefault("160216") == 5.0m
        ? "" : "третирането не вади от „Получен“");

    var waiting = StockMath.Waiting(all, moves, links, end);

    notes.Add(waiting.Total == 15.0m ? "" : $"в „Третиран“ чакат {waiting.Total} т вместо 15");
    notes.Add(waiting.Shares.Count == 1 ? "" : $"чакащото е на {waiting.Shares.Count} реда вместо един");
    notes.Add(waiting.Shares.Count == 1 && waiting.Shares[0].Ewc == "160216"
        ? "" : "чакащото не носи кода, с който е третирано");
    notes.Add(waiting.Shares.Count == 1 && waiting.Shares[0].Batch.Id == metal.Id
        ? "" : "чакащото не носи партидата си");
    notes.Add(waiting.Draws.Count == 1 && waiting.Draws[0].Amount == 15.0m
        ? "" : "пулът за образуване не е пълен");

    // Образуваното изчерпва чакащото — с друг код, но от същата партида.
    moves.Add(new Move(3, MoveKind.Generated, new DateOnly(2026, 9, 12), "191202", 12.0m));
    links.Add(new LedgerLink(3, 1, 12.0m));

    notes.Add(StockMath.Waiting(all, moves, links, end).Total == 3.0m
        ? "" : $"след образуването чакат {StockMath.Waiting(all, moves, links, end).Total} т вместо 3");
    notes.Add(StockMath.Stock(moves, StoreKind.Formed, end).GetValueOrDefault("191202") == 12.0m
        ? "" : "образуваното не влиза в „Образуван“");

    moves.Add(new Move(4, MoveKind.Generated, new DateOnly(2026, 9, 13), "191204", 3.0m));
    links.Add(new LedgerLink(4, 1, 3.0m));

    notes.Add(StockMath.Waiting(all, moves, links, end).IsEmpty
        ? "" : "складът не се изпразва, когато всичко е обявено");

    // Собствен отпадък: образувано без третиране не вкарва склада на минус.
    var own = new List<Move>
    {
        new(1, MoveKind.Generated, new DateOnly(2026, 9, 4), "150101", 40.0m)
    };

    notes.Add(StockMath.Waiting(all, own, [], end).Total == 0m
        ? "" : "образуваното без третиране вкарва „Третиран“ на минус");
    notes.Add(Reconcile.CanClose(Reconcile.Check(all, own, [], new LedgerPeriod(2026, 9)))
        ? "" : "площадка, която само образува, не може да затвори месеца");

    // Третирано без посочена партида: не се вижда по редове, но чака.
    var loose = new List<Move>
    {
        new(1, MoveKind.Received, new DateOnly(2026, 9, 1), "160216", 20.0m),
        new(2, MoveKind.Treated, new DateOnly(2026, 9, 10), "160216", 15.0m)
    };

    var half = StockMath.Waiting(all, loose, [new LedgerLink(2, 1, 9.0m)], end);

    notes.Add(half.Loose == 6.0m ? "" : $"безпартидното третиране е {half.Loose} т вместо 6");
    notes.Add(half.Total == 15.0m ? "" : $"сборът пропуска безпартидното: {half.Total}");

    // Месецът е границата: третирано на 30 септември и образувано на 2 октомври
    // е неприключен септември и наред октомври.
    var across = new List<Move>
    {
        new(1, MoveKind.Received, new DateOnly(2026, 9, 1), "160216", 20.0m),
        new(2, MoveKind.Treated, new DateOnly(2026, 9, 30), "160216", 15.0m),
        new(3, MoveKind.Generated, new DateOnly(2026, 10, 2), "191202", 15.0m)
    };

    var acrossLinks = new List<LedgerLink> { new(1, 1, 20m), new(2, 1, 15m), new(3, 1, 15m) };

    notes.Add(StockMath.Waiting(all, across, acrossLinks, end).Total == 15.0m
        ? "" : "септември изглежда приключен, а третираното още чака");
    notes.Add(StockMath.Waiting(all, across, acrossLinks, new DateOnly(2026, 10, 31)).IsEmpty
        ? "" : "октомври не изчерпва чакащото от септември");

    // Контролата: чакащото спира затварянето на месеца.
    var september = new LedgerPeriod(2026, 9);
    var issues = Reconcile.Check(all, across, acrossLinks, september);
    var stop = issues.FirstOrDefault(i => i.Kind == LedgerProblem.Waiting);

    notes.Add(stop is not null ? "" : "чакащото не се съобщава");
    notes.Add(stop is { Blocks: true } ? "" : "чакащото не спира затварянето");
    notes.Add(stop?.Amount == 15.0m ? "" : "чакащото е с грешно число");
    notes.Add(!Reconcile.CanClose(issues) ? "" : "месец с чакащо в „Третиран“ се затваря");
    notes.Add(Reconcile.CanClose(Reconcile.Check(all, across, acrossLinks, new LedgerPeriod(2026, 10)))
        ? "" : "октомври не се затваря, макар да е чист");

    // Двете бележки не се дублират: щом складът чака, разликата в масата е
    // следствие и не се съобщава втори път.
    notes.Add(issues.All(i => i.Kind != LedgerProblem.Unbalanced)
        ? "" : "разликата в масата се съобщава заедно с чакащото");

    var problems = notes.Where(n => n.Length > 0).ToList();
    foreach (var problem in problems) Console.WriteLine("     " + problem);

    Check($"направени са всичките {notes.Count} проверки", notes.Count == 21, $"{notes.Count}");
    Check("третираното чака в своя склад, докато се обяви образуваното", problems.Count == 0,
        $"{problems.Count} проблема");
}

// =============================================== Складът: чакащото на екрана
Console.WriteLine();
Console.WriteLine("### ТЕСТ - склад „Третиран“ се вижда и се изпразва от екрана");
{
    var waitNotes = new List<string>();
    string? waitError = null;

    var waitThread = new Thread(() =>
    {
        try
        {
            var waitPath = Path.Combine(work, $"waiting_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(waitPath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");
            var september = new LedgerPeriod(2026, 9);

            var batch = db.Batches.Add(companyId, new BatchDraft(
                "203344556", "Метал ООД", "160216", new DateOnly(2026, 9, 1)));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 1), "160216", 20m,
                Eik: "203344556", Partner: "Метал ООД"));

            var received = db.Ledger.Read(companyId, "1").First(m => m.Kind == MoveKind.Received);
            var mine = db.Ledger.Draws(received.Id)[0].BatchId;

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Treated, new DateOnly(2026, 9, 10), "160216", 15m));

            using var page = new StockPage(service, db);

            page.UseDate(new DateOnly(2026, 9, 30));

            // Третираното вече го няма в „Получен“ — стои в „Третиран“.
            page.UseStore(StoreKind.Received);
            waitNotes.Add(page.AmountAt(0) == "5,000"
                ? "" : "в „Получен“ остават " + page.AmountAt(0));

            page.UseStore(StoreKind.Treated);
            waitNotes.Add(page.RowCount == 1 ? "" : $"„Третиран“ показва {page.RowCount} реда вместо един");
            waitNotes.Add(page.KeyAt(0) == "160216" ? "" : "чакащото не носи кода си: " + page.KeyAt(0));
            waitNotes.Add(page.AmountAt(0) == "15,000" ? "" : "чакащото е " + page.AmountAt(0));
            waitNotes.Add(page.SummaryLine.Contains("15,000", StringComparison.Ordinal)
                ? "" : "сборът на чакащото липсва: " + page.SummaryLine);

            page.Pick(0);
            waitNotes.Add(page.DetailCount == 1 ? "" : "произходът на чакащото не се вижда");
            waitNotes.Add(page.DetailPartnerAt(0).Contains("Метал ООД", StringComparison.Ordinal)
                ? "" : "чакащото не помни от кого е дошло");

            // Тук не се третира — оттук се образува. Копчето е едно и сменя
            // действието си според склада.
            waitNotes.Add(page.TreatOffered ? "третирането се предлага и от „Третиран“" : "");
            waitNotes.Add(page.ActionText.StartsWith("Образувай", StringComparison.Ordinal)
                ? "" : "копчето не стана за образуване: " + page.ActionText);

            page.UseStore(StoreKind.Received);
            waitNotes.Add(page.ActionText.StartsWith("Третирай", StringComparison.Ordinal)
                ? "" : "в „Получен“ копчето не е за третиране: " + page.ActionText);

            page.UseStore(StoreKind.Formed);
            waitNotes.Add(page.ActionText.StartsWith("Предай", StringComparison.Ordinal)
                ? "" : "в „Образуван“ копчето не е за предаване: " + page.ActionText);

            page.UseStore(StoreKind.Treated);

            // Отчетът не тръгва, докато складът чака.
            using (var ledger = new LedgerPage(service, db))
            {
                ledger.UseMonth(september);
                waitNotes.Add(ledger.PendingTreated(september) == 15m
                    ? "" : $"„Месец“ смята чакащото на {ledger.PendingTreated(september)} т");
                waitNotes.Add(!ledger.CanCompose(september, september)
                    ? "" : "отчет се съставя, докато складът чака");
            }

            // Образуваното изпразва склада — и през две трети от количеството
            // остава чакащо, защото от третирането има загуба.
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Generated, new DateOnly(2026, 9, 12), "191202", 10m));

            page.Refresh();
            page.UseStore(StoreKind.Treated);
            page.UseDate(new DateOnly(2026, 9, 30));

            waitNotes.Add(page.AmountAt(0) == "5,000"
                ? "" : "след образуването чакат " + page.AmountAt(0));

            // Произходът на образуваното идва от чакащото, не от календара.
            var generated = db.Ledger.Read(companyId, "1").First(m => m.Kind == MoveKind.Generated);
            var draws = db.Ledger.Draws(generated.Id);

            waitNotes.Add(draws.Count == 1 && draws[0].BatchId == mine
                ? "" : "образуваното не наследи произхода на третираното");

            // И остатъкът: обявен, складът се изпразва и отчетът тръгва.
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Generated, new DateOnly(2026, 9, 13), "191204", 5m));

            page.Refresh();
            page.UseStore(StoreKind.Treated);
            page.UseDate(new DateOnly(2026, 9, 30));

            waitNotes.Add(page.RowCount == 0 ? "" : $"складът не се изпразни: {page.RowCount} реда");

            using (var ledger = new LedgerPage(service, db))
            {
                ledger.UseMonth(september);
                waitNotes.Add(ledger.PendingTreated(september) == 0m ? "" : "„Месец“ още вижда чакащо");
                waitNotes.Add(ledger.CanCompose(september, september)
                    ? "" : "отчетът не тръгва и с празен склад");
            }

            // Затварянето вече живее в „Настройки → Затворени месеци“.
            using (var periods = new PeriodsDialog(db, new CompanyScope(db)))
            {
                waitNotes.Add(periods.Pick("1", september) ? "" : "септември го няма в списъка с месеци");
                waitNotes.Add(periods.LockText.Contains("Затвори") ? "" : "копчето не предлага затваряне");
                waitNotes.Add(periods.CloseSelected() ? "" : "празният месец не се затваря");
                waitNotes.Add(db.Periods.IsClosed(companyId, "1", september)
                    ? "" : "затварянето не стигна до базата");

                waitNotes.Add(periods.Pick("1", september) ? "" : "септември изпадна от списъка след затваряне");
                waitNotes.Add(periods.LockText.Contains("Отвори") ? "" : "копчето не предлага отваряне");
                waitNotes.Add(!periods.CloseSelected() ? "" : "затворен месец се затваря втори път");
                waitNotes.Add(periods.ReopenSelected() ? "" : "затвореният месец не се отваря наново");
                waitNotes.Add(!db.Periods.IsClosed(companyId, "1", september)
                    ? "" : "отварянето не стигна до базата");
            }

            // Третиране в затворен месец не се записва.
            db.Periods.Close(companyId, "1", september);
            db.Periods.Reopen(companyId, "1", september);
        }
        catch (Exception ex)
        {
            waitError = ex.ToString();
        }
    });

    waitThread.SetApartmentState(ApartmentState.STA);
    waitThread.Start();
    waitThread.Join();

    if (waitError is not null) Console.WriteLine("     " + waitError);

    var waitProblems = waitNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in waitProblems) Console.WriteLine("     " + problem);

    Check("склад „Третиран“ се сглобява без изключение", waitError is null);
    Check($"направени са всичките {waitNotes.Count} проверки", waitNotes.Count == 27,
        $"{waitNotes.Count}");
    Check("третираното се вижда, изпразва се и пуска отчета", waitProblems.Count == 0,
        $"{waitProblems.Count} проблема");
}


// =============================================== Дневникът на внесените файлове
Console.WriteLine();
Console.WriteLine("### ТЕСТ - внесеният файл се помни и се маха изцяло");
{
    var undoNotes = new List<string>();
    string? undoError = null;

    var undoThread = new Thread(() =>
    {
        try
        {
            var undoPath = Path.Combine(work, $"undo_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(undoPath);

            var companyId = db.Companies.Add("205566778", "Втора площадка ЕООД");
            var september = new LedgerPeriod(2026, 9);
            var sample = SampleFor(service.GetSchema("book"), BookNodes.Report, "type_originator_code");
            var file = Path.Combine(work, $"imported_{Guid.NewGuid():N}.xlsx");

            // Файл с два реда — изнесен от самата програма, за да е истински.
            using (var source = new MoveSheetDialog(
                service, db, companyId, "1", MoveKind.Received, september))
            {
                source.Grid!.AppendRow(new Dictionary<string, string>
                {
                    ["date_record"] = "03.09.2026",
                    ["ewc"] = "160216",
                    ["amount_ton"] = "12,000",
                    ["stagenum"] = "1",
                    ["type_originator_code"] = sample,
                    ["name_originator"] = "Скрап ЕООД",
                    ["pin_originator"] = "204455667"
                });

                source.Grid!.AppendRow(new Dictionary<string, string>
                {
                    ["date_record"] = "05.09.2026",
                    ["ewc"] = "160216",
                    ["amount_ton"] = "8,000",
                    ["stagenum"] = "1",
                    ["type_originator_code"] = sample,
                    ["name_originator"] = "Метал груп ЕООД"
                });

                source.SaveTemplate(file);
            }

            // Внесеното и въведеното на ръка влизат с един и същи запис.
            using (var sheet = new MoveSheetDialog(
                service, db, companyId, "1", MoveKind.Received, september))
            {
                sheet.LoadTemplate(file, out _);

                sheet.Grid!.AppendRow(new Dictionary<string, string>
                {
                    ["date_record"] = "07.09.2026",
                    ["ewc"] = "160216",
                    ["amount_ton"] = "3,000",
                    ["stagenum"] = "1",
                    ["type_originator_code"] = sample,
                    ["name_originator"] = "На ръка ООД"
                });

                sheet.AcceptForTest();
            }

            undoNotes.Add(db.Ledger.Read(companyId, "1").Count == 3
                ? "" : $"в склада влязоха {db.Ledger.Read(companyId, "1").Count} движения вместо три");

            var imports = db.History.List(companyId);

            undoNotes.Add(imports.Count == 1 ? "" : $"внасянията са {imports.Count} вместо едно");

            // Ръчният ред НЕ е на файла: той влиза с него, но не си отива с него.
            undoNotes.Add(imports.Count == 1 && imports[0].Moves == 2
                ? "" : "внасянето брои и ръчно въведеното: " +
                       (imports.Count == 1 ? imports[0].Moves.ToString() : "—"));

            undoNotes.Add(imports.Count == 1 && imports[0].Batches == 2
                ? "" : "партидите на внесеното не се помнят");

            undoNotes.Add(imports.Count == 1 && imports[0].Source == ImportSource.Template
                ? "" : "произходът на файла не се помни");

            undoNotes.Add(imports.Count == 1 && imports[0].FileName == Path.GetFileName(file)
                ? "" : "името на файла не се помни");

            // Същият файл, внесен втори път, е отделно събитие.
            using (var again = new MoveSheetDialog(
                service, db, companyId, "1", MoveKind.Received, september))
            {
                again.LoadTemplate(file, out _);
                again.AcceptForTest();
            }

            undoNotes.Add(db.Ledger.Read(companyId, "1").Count == 5
                ? "" : "второто внасяне не влезе цяло");

            var twice = db.History.List(companyId);

            undoNotes.Add(twice.Count == 2 ? "" : $"двете внасяния се сляха в {twice.Count}");

            // Махането на второто връща склада там, където беше преди него.
            var second = twice[0];
            var check = db.History.Check(second.Id);

            undoNotes.Add(check is { Allowed: true } ? "" : "махането се спъва без причина");

            var undone = db.History.Undo(second.Id);

            undoNotes.Add(undone is { Done: true, Moves: 2, Batches: 2 }
                ? "" : $"махнати са {undone.Moves} движения и {undone.Batches} партиди вместо по две");

            undoNotes.Add(db.Ledger.Read(companyId, "1").Count == 3
                ? "" : $"след махането останаха {db.Ledger.Read(companyId, "1").Count} движения вместо три");

            undoNotes.Add(db.Batches.List(companyId, "1").Count == 3
                ? "" : $"след махането останаха {db.Batches.List(companyId, "1").Count} партиди вместо три");

            undoNotes.Add(db.History.List(companyId).Count == 1
                ? "" : "махнатото внасяне остана в списъка");

            // Ръчният ред е още там — той не е дошъл с файла.
            undoNotes.Add(db.Ledger.Read(companyId, "1").Any(m => m.Amount == 3m)
                ? "" : "ръчно въведеният ред си отиде с чуждото внасяне");

            // Затворен месец спира махането — първо се отваря.
            var first = db.History.List(companyId)[0];
            db.Periods.Close(companyId, "1", september);

            var closed = db.History.Check(first.Id);

            undoNotes.Add(closed is { Allowed: false } ? "" : "затворен месец не спира махането");

            var refused = db.History.Undo(first.Id);

            undoNotes.Add(!refused.Done && refused.Total == 0
                ? "" : "спряното махане все пак пипна склада");

            undoNotes.Add(db.Ledger.Read(companyId, "1").Count == 3
                ? "" : "спряното махане промени склада");

            db.Periods.Reopen(companyId, "1", september);

            // Партида, от която вече е черпено, преживява махането: тя носи
            // произхода на истинска работа.
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Treated, new DateOnly(2026, 9, 20), "160216", 5m));

            var used = db.History.Check(first.Id);

            undoNotes.Add(used is { Allowed: true, Warnings.Count: > 0 }
                ? "" : "не се казва, че от внесеното вече е черпено");

            var partial = db.History.Undo(first.Id);

            undoNotes.Add(partial is { Done: true, Moves: 2 }
                ? "" : $"махнати са {partial.Moves} движения вместо две");

            undoNotes.Add(partial.Batches == 1 && partial.Kept.Count == 1
                ? "" : $"черпената партида не оцеля: махнати {partial.Batches}, оставени {partial.Kept.Count}");

            // Третирането остава — и си личи, защото складът му е на минус.
            undoNotes.Add(db.Ledger.Read(companyId, "1").Any(m => m.Kind == MoveKind.Treated)
                ? "" : "третирането си отиде с приемането, от което е черпило");

            // Отчет, внесен от Excel: същата следа, само друг вид запис.
            var documentId = db.Documents.Create("book", BookNodes.Report, "Внесен отчет",
                null, DocumentOrigin.Excel, companyId);

            var reportImport = db.Imports.Begin(companyId, ImportSource.Report,
                Path.Combine("C:", "изнесено", "отчет.xlsx"));

            db.Imports.Track(reportImport, ImportTarget.Document, documentId);

            var listed = db.History.List(companyId).FirstOrDefault(i => i.Id == reportImport);

            undoNotes.Add(listed is { Documents: 1 } ? "" : "внесеният отчет не се брои");
            undoNotes.Add(listed?.FileName == "отчет.xlsx" ? "" : "пази се цял път вместо име на файл");

            var dropped = db.History.Undo(reportImport);

            undoNotes.Add(dropped is { Done: true, Documents: 1 } ? "" : "внесеният отчет не се махна");
            undoNotes.Add(db.Documents.Find(documentId) is null ? "" : "отчетът остана в базата");

            // Изчезнал по друг път запис не се обещава втори път.
            var stale = db.Imports.Begin(companyId, ImportSource.Report, "няма.xlsx");
            db.Imports.Track(stale, ImportTarget.Document, 987654);

            undoNotes.Add(db.History.List(companyId).First(i => i.Id == stale).Empty
                ? "" : "внасяне без останали записи се брои за пълно");
        }
        catch (Exception ex)
        {
            undoError = ex.ToString();
        }
    });

    undoThread.SetApartmentState(ApartmentState.STA);
    undoThread.Start();
    undoThread.Join();

    if (undoError is not null) Console.WriteLine("     " + undoError);

    var undoProblems = undoNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in undoProblems) Console.WriteLine("     " + problem);

    Check("дневникът на внасянията работи без изключение", undoError is null);
    Check($"направени са всичките {undoNotes.Count} проверки", undoNotes.Count == 26,
        $"{undoNotes.Count}");
    Check("внесеният файл се помни и се маха изцяло", undoProblems.Count == 0,
        $"{undoProblems.Count} проблема");
}

// =============================================== Фирмата се избира веднъж
Console.WriteLine();
Console.WriteLine("### ТЕСТ - фирмата се избира в лентата и важи навсякъде");
{
    var scopeNotes = new List<string>();
    string? scopeError = null;

    var scopeThread = new Thread(() =>
    {
        try
        {
            var scopePath = Path.Combine(work, $"scope_{Guid.NewGuid():N}.db");

            long metal;
            long skrap;

            using (var db = Workspace.Open(scopePath))
            {
                metal = db.Companies.Add("203344556", "Метал ООД");
                skrap = db.Companies.Add("204455667", "Скрап ЕООД");

                // По едно движение на фирма, за да личи коя се показва.
                db.Writer.Write(metal, new MoveRequest(
                    MoveKind.Received, new DateOnly(2026, 9, 3), "160216", 12m));

                db.Writer.Write(skrap, new MoveRequest(
                    MoveKind.Received, new DateOnly(2026, 9, 4), "170405", 7m));

                db.Documents.Create("book", BookNodes.Report, "Книга на Метал", null,
                    DocumentOrigin.Manual, metal);

                db.Documents.Create("book", BookNodes.Report, "Книга на Скрап", null,
                    DocumentOrigin.Manual, skrap);

                // Внесен с непознат ЕИК: ничий, но трябва да се вижда.
                db.Documents.Create("book", BookNodes.Report, "Ничий отчет", null,
                    DocumentOrigin.Excel);

                var scope = new CompanyScope(db);

                scopeNotes.Add(scope.Current == metal
                    ? "" : "без запомнена фирма не се хваща първата");

                var told = 0;
                scope.Changed += (_, _) => told++;

                scope.Use(skrap);

                scopeNotes.Add(scope.Current == skrap ? "" : "смяната не стига до обхвата");
                scopeNotes.Add(told == 1 ? "" : $"смяната се обяви {told} пъти вместо веднъж");

                scope.Use(skrap);
                scopeNotes.Add(told == 1 ? "" : "същата фирма се брои за смяна");

                // Страниците следват обхвата, без да ги пипа никой друг.
                using var ledger = new LedgerPage(service, db, scope);
                ledger.UseMonth(new LedgerPeriod(2026, 9));

                scopeNotes.Add(ledger.RowCount == 1 ? "" : $"„Месец“ показва {ledger.RowCount} реда");

                scope.Use(metal);
                ledger.UseMonth(new LedgerPeriod(2026, 9));

                scopeNotes.Add(ledger.RowCount == 1
                    ? "" : $"след смяната „Месец“ показва {ledger.RowCount} реда");

                // Отчетите: своите плюс ничиите.
                using var reports = new WorkPage(service, db, scope);

                scopeNotes.Add(reports.ShownTitles.Count == 2 &&
                               reports.ShownTitles.Contains("Книга на Метал") &&
                               reports.ShownTitles.Contains("Ничий отчет")
                    ? "" : "отчетите не се свеждат до фирмата: " + string.Join(", ", reports.ShownTitles));

                scope.Use(skrap);

                scopeNotes.Add(reports.ShownTitles.Contains("Книга на Скрап") &&
                               !reports.ShownTitles.Contains("Книга на Метал")
                    ? "" : "смяната не стига до списъка с отчети");

                scopeNotes.Add(reports.ShownTitles.Contains("Ничий отчет")
                    ? "" : "отчетът без фирма изчезна — тогава няма как да му се посочи фирма");
            }

            // Второ пускане: продължава се от същата фирма.
            using (var db = Workspace.Open(scopePath))
            {
                var again = new CompanyScope(db);

                scopeNotes.Add(again.Current == skrap
                    ? "" : "при следващото пускане фирмата не се помни");

                // Изчезнала фирма: пада се към първата, вместо да остане празно.
                db.Companies.Remove(skrap);

                var after = new CompanyScope(db);

                scopeNotes.Add(after.Current == metal
                    ? "" : "премахнатата фирма не се заменя с друга");
            }

            // И в самата лента.
            using (var db = Workspace.Open(Path.Combine(work, $"rail_{Guid.NewGuid():N}.db")))
            {
                var alpha = db.Companies.Add("111111111", "Алфа ЕООД");
                db.Companies.Add("222222222", "Бета ООД");

                using var shell = new ShellForm(service, db);

                scopeNotes.Add(shell.CompanyChoices.Count == 2
                    ? "" : "лентата не показва двете фирми: " + string.Join(", ", shell.CompanyChoices));

                scopeNotes.Add(shell.CompanyChoices[0] == "Алфа ЕООД"
                    ? "" : "в лентата стои друго вместо името: " + shell.CompanyChoices[0]);

                scopeNotes.Add(shell.CurrentCompany == alpha ? "" : "лентата не тръгва от първата фирма");

                shell.UseCompany(1);

                scopeNotes.Add(shell.CurrentCompany != alpha ? "" : "изборът в лентата не сменя фирмата");

                scopeNotes.Add(db.Settings.Get(SettingsStore.Keys.CurrentCompany) ==
                               shell.CurrentCompany?.ToString()
                    ? "" : "изборът не се записва веднага");

                scopeNotes.Add(shell.CompanyPickerShown ? "" : "изборът не се вижда при отворена лента");

                shell.RailCollapsed = true;
                scopeNotes.Add(!shell.CompanyPickerShown ? "" : "изборът стои и в свитата лента");

                shell.RailCollapsed = false;
                scopeNotes.Add(shell.CompanyPickerShown ? "" : "изборът не се връща с лентата");
            }

            // Празна база: списъкът казва какво липсва, вместо да мълчи.
            using (var db = Workspace.Open(Path.Combine(work, $"norail_{Guid.NewGuid():N}.db")))
            {
                using var shell = new ShellForm(service, db);

                scopeNotes.Add(shell.CurrentCompany is null ? "" : "без заведени фирми се избира някоя");
                scopeNotes.Add(shell.CompanyChoices.Count == 1 && shell.CompanyChoices[0].Contains("заведете")
                    ? "" : "без фирми лентата не казва какво липсва");
            }
        }
        catch (Exception ex)
        {
            scopeError = ex.ToString();
        }
    });

    scopeThread.SetApartmentState(ApartmentState.STA);
    scopeThread.Start();
    scopeThread.Join();

    if (scopeError is not null) Console.WriteLine("     " + scopeError);

    var scopeProblems = scopeNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in scopeProblems) Console.WriteLine("     " + problem);

    Check("изборът на фирма работи без изключение", scopeError is null);
    Check($"направени са всичките {scopeNotes.Count} проверки", scopeNotes.Count == 21, $"{scopeNotes.Count}");
    Check("фирмата се избира веднъж и важи навсякъде", scopeProblems.Count == 0,
        $"{scopeProblems.Count} проблема");
}


// =============================================== Склад „Предаден“
Console.WriteLine();
Console.WriteLine("### ТЕСТ - склад „Предаден“ помни какво е излязло и от чие");
{
    var goneNotes = new List<string>();
    string? goneError = null;

    var goneThread = new Thread(() =>
    {
        try
        {
            var gonePath = Path.Combine(work, $"gone_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(gonePath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");

            // Две приемания от различни клиенти — по тях се познава произходът.
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 3), "160216", 12m,
                Eik: "204455667", Partner: "Скрап ЕООД"));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 5), "160216", 8m,
                Eik: "205566778", Partner: "Метал груп ЕООД"));

            // Третиране и образуване: 10 т кабел стават 10 т мед.
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Treated, new DateOnly(2026, 9, 10), "160216", 10m, EwcOut: "191202"));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Generated, new DateOnly(2026, 9, 11), "191202", 10m));

            // Предаване от образуваното и пряко предаване от полученото.
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Submitted, new DateOnly(2026, 9, 20), "191202", 6m));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Submitted, new DateOnly(2026, 9, 21), "160216", 3m, Direct: true));

            var moves = db.Ledger.Read(companyId, "1");
            var links = db.Ledger.Links(companyId, "1");
            var batches = db.Batches.List(companyId, "1");

            // Летописът събира и двете предавания.
            var gone = StockMath.Stock(moves, StoreKind.Submitted, new DateOnly(2026, 9, 30));

            goneNotes.Add(gone.GetValueOrDefault("191202") == 6m
                ? "" : $"предаденото от образуваното е {gone.GetValueOrDefault("191202")}");

            goneNotes.Add(gone.GetValueOrDefault("160216") == 3m
                ? "" : $"прякото предаване не влезе: {gone.GetValueOrDefault("160216")}");

            // И расте: летописът няма какво да го изчерпи.
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Submitted, new DateOnly(2026, 10, 5), "191202", 4m));

            moves = db.Ledger.Read(companyId, "1");

            var later = StockMath.Stock(moves, StoreKind.Submitted, new DateOnly(2026, 10, 31));

            goneNotes.Add(later.GetValueOrDefault("191202") == 10m
                ? "" : $"летописът не расте: {later.GetValueOrDefault("191202")}");

            // Към по-ранна дата стои по-малко — датата важи и за него.
            var earlier = StockMath.Stock(moves, StoreKind.Submitted, new DateOnly(2026, 9, 20));

            goneNotes.Add(earlier.GetValueOrDefault("191202") == 6m
                ? "" : $"летописът не помни датите: {earlier.GetValueOrDefault("191202")}");

            // Наличните складове не се пипат от новия.
            var received = StockMath.Stock(moves, StoreKind.Received, new DateOnly(2026, 10, 31));
            var formed = StockMath.Stock(moves, StoreKind.Formed, new DateOnly(2026, 10, 31));

            goneNotes.Add(received.GetValueOrDefault("160216") == 7m
                ? "" : $"„Получен“ се промени: {received.GetValueOrDefault("160216")}");

            goneNotes.Add(formed.GetValueOrDefault("191202") == 0m
                ? "" : $"„Образуван“ се промени: {formed.GetValueOrDefault("191202")}");

            // Произходът пътува: предаденото сочи към партидите, от които е дошло.
            links = db.Ledger.Links(companyId, "1");
            batches = db.Batches.List(companyId, "1");

            var shares = StockMath.Shares(batches, moves, links, StoreKind.Submitted,
                new DateOnly(2026, 10, 31));

            // Прякото предаване носи произход: то черпи от склад „Получен“, а
            // там партидата и движението имат един и същ код.
            goneNotes.Add(shares.Sum(s => s.Amount) == 3m
                ? "" : $"дяловете на прякото предаване са {shares.Sum(s => s.Amount)} т вместо 3");

            goneNotes.Add(shares.Any(s => s.Batch.PartnerName == "Скрап ЕООД")
                ? "" : "предаденото не помни чие е било");

            // Предаденото от склад „Образуван“ обаче остава без партида: тя
            // помни кода, с който отпадъкът е дошъл (160216), а движението носи
            // кода, с който е излязъл (191202). Числото не бива да изчезва
            // заради това — брои се като невързано.
            goneNotes.Add(StockMath.Loose(moves, links, StoreKind.Submitted, new DateOnly(2026, 10, 31)) == 10m
                ? "" : "невързаното предадено не се брои: " +
                       StockMath.Loose(moves, links, StoreKind.Submitted, new DateOnly(2026, 10, 31)));

            // И на екрана.
            using var page = new StockPage(service, db);

            page.UseStore(StoreKind.Submitted);
            page.UseDate(new DateOnly(2026, 10, 31));

            goneNotes.Add(page.RowCount == 1
                ? "" : $"„Предаден“ показва {page.RowCount} реда вместо един");

            goneNotes.Add(page.SummaryText.Contains("Предадени")
                ? "" : "обобщението говори за наличност: " + page.SummaryText);

            goneNotes.Add(page.SummaryText.Contains("без посочена партида")
                ? "" : "невързаното не се казва на екрана: " + page.SummaryText);

            goneNotes.Add(page.AmountHeaderText.Contains("Предадено")
                ? "" : "колоната още се казва „Налично“: " + page.AmountHeaderText);

            goneNotes.Add(!page.ActionShown ? "" : "летописът предлага действие");

            page.UseStore(StoreKind.Received);

            goneNotes.Add(page.ActionShown ? "" : "наличният склад остана без действие");
            goneNotes.Add(page.AmountHeaderText.Contains("Налично")
                ? "" : "колоната не се върна на „Налично“: " + page.AmountHeaderText);

            // Внасянето от „Склад“ пълни движението на гледания склад.
            var feeds = new (StoreKind Store, MoveKind Kind)[]
            {
                (StoreKind.Received, MoveKind.Received),
                (StoreKind.Treated, MoveKind.Treated),
                (StoreKind.Formed, MoveKind.Generated),
                (StoreKind.Submitted, MoveKind.Submitted)
            };

            var wrong = new List<string>();

            foreach (var (store, kind) in feeds)
            {
                page.UseStore(store);

                if (page.ImportKind != kind) wrong.Add($"{Moves.Short(store)} → {page.ImportKind}");
            }

            goneNotes.Add(wrong.Count == 0
                ? "" : "внасянето сочи към друго движение: " + string.Join(", ", wrong));

            // По клиент: на кого какво е било — през партидите, от които идва.
            page.UseStore(StoreKind.Submitted);
            page.UseView(byPartner: true);

            // Двама клиенти: прякото предаване е изчерпало остатъка от първата
            // партида и е добрало от втората.
            goneNotes.Add(page.RowCount == 2
                ? "" : $"по клиент „Предаден“ показва {page.RowCount} реда вместо двама клиента");
        }
        catch (Exception ex)
        {
            goneError = ex.ToString();
        }
    });

    goneThread.SetApartmentState(ApartmentState.STA);
    goneThread.Start();
    goneThread.Join();

    if (goneError is not null) Console.WriteLine("     " + goneError);

    var goneProblems = goneNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in goneProblems) Console.WriteLine("     " + problem);

    Check("склад „Предаден“ се сглобява без изключение", goneError is null);
    Check($"направени са всичките {goneNotes.Count} проверки", goneNotes.Count == 18, $"{goneNotes.Count}");
    Check("склад „Предаден“ помни излязлото и произхода му", goneProblems.Count == 0,
        $"{goneProblems.Count} проблема");
}


// =============================================== Филтри с отметки и месеци
Console.WriteLine();
Console.WriteLine("### ТЕСТ - складът се пресява с отметки и се върти по месеци");
{
    var sieveNotes = new List<string>();
    string? sieveError = null;

    var sieveThread = new Thread(() =>
    {
        try
        {
            var sievePath = Path.Combine(work, $"sieve_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(sievePath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");

            // Три кода от двама клиента — за да има какво да се отмята.
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 8, 12), "160216", 5m,
                Eik: "204455667", Partner: "Скрап ЕООД"));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 3), "170405", 7m,
                Eik: "204455667", Partner: "Скрап ЕООД"));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 5), "191202", 9m,
                Eik: "205566778", Partner: "Метал груп ЕООД"));

            using var page = new StockPage(service, db);

            page.UseStore(StoreKind.Received);
            page.UseDate(new DateOnly(2026, 9, 30));

            sieveNotes.Add(page.RowCount == 3 ? "" : $"без филтър редовете са {page.RowCount} вместо три");
            sieveNotes.Add(page.CodeFilterText.Contains("всички")
                ? "" : "копчето не казва, че филтърът е празен: " + page.CodeFilterText);

            // Един код — както досега.
            sieveNotes.Add(page.UseCodeFilter("170405") ? "" : "кодът не се отмята");
            sieveNotes.Add(page.RowCount == 1 ? "" : $"с един код редовете са {page.RowCount}");
            sieveNotes.Add(page.CodeFilterText.StartsWith("170405", StringComparison.Ordinal)
                ? "" : "копчето не показва единствения избран код: " + page.CodeFilterText);

            // Два кода наведнъж — това е новото.
            sieveNotes.Add(page.UseCodeFilter("170405", "191202") ? "" : "двата кода не се отмятат");
            sieveNotes.Add(page.RowCount == 2 ? "" : $"с два кода редовете са {page.RowCount} вместо два");
            sieveNotes.Add(page.SummaryText.Contains("16,000", StringComparison.Ordinal)
                ? "" : "сборът не следва двата кода: " + page.SummaryText);
            sieveNotes.Add(page.CodeFilterText.Contains("2")
                ? "" : "копчето не брои избраните: " + page.CodeFilterText);

            // Непознат код не пипа филтъра.
            sieveNotes.Add(!page.UseCodeFilter("999999") ? "" : "непознат код се приема");
            sieveNotes.Add(page.RowCount == 2 ? "" : "отказаният филтър промени таблицата");

            page.UseCodeFilter("");
            sieveNotes.Add(page.RowCount == 3 ? "" : "празният филтър не връща всичко");

            // Клиентите се отмятат по същия начин.
            sieveNotes.Add(page.UsePartnerFilter("204455667", "205566778")
                ? "" : "двамата клиенти не се отмятат");

            sieveNotes.Add(page.RowCount == 3 ? "" : $"с двамата клиенти редовете са {page.RowCount}");

            page.UsePartnerFilter("205566778");
            sieveNotes.Add(page.RowCount == 1 ? "" : $"с един клиент редовете са {page.RowCount}");

            // Празна таблица заради филтър си го казва, вместо да мълчи.
            page.UseCodeFilter("160216");
            sieveNotes.Add(page.RowCount == 0 ? "" : "двата несъвместими филтъра дават редове");
            sieveNotes.Add(page.SummaryText.Contains("филтри")
                ? "" : "празното от филтър не се обяснява: " + page.SummaryText);

            page.UseCodeFilter("");
            page.UsePartnerFilter("");

            // Въртенето по месеци: към края на месеца, а не с трийсет дни.
            page.UseDate(new DateOnly(2026, 9, 14));

            page.RollDate(1);
            sieveNotes.Add(page.CurrentDate == new DateOnly(2026, 9, 30)
                ? "" : "напред от средата на месеца не стига края му: " + page.CurrentDate);

            page.RollDate(1);
            sieveNotes.Add(page.CurrentDate == new DateOnly(2026, 10, 31)
                ? "" : "второто напред не стига следващия край: " + page.CurrentDate);

            page.RollDate(-1);
            sieveNotes.Add(page.CurrentDate == new DateOnly(2026, 9, 30)
                ? "" : "назад не се връща на предходния край: " + page.CurrentDate);

            page.RollDate(-1);
            sieveNotes.Add(page.CurrentDate == new DateOnly(2026, 8, 31)
                ? "" : "назад не минава през август: " + page.CurrentDate);

            // И складът го следва: към август стои само първото приемане.
            sieveNotes.Add(page.RowCount == 1 ? "" : $"в края на август редовете са {page.RowCount}");

            // През декември се минава в следващата година.
            page.UseDate(new DateOnly(2026, 12, 31));
            page.RollDate(1);

            sieveNotes.Add(page.CurrentDate == new DateOnly(2027, 1, 31)
                ? "" : "декември не пуска към януари: " + page.CurrentDate);
        }
        catch (Exception ex)
        {
            sieveError = ex.ToString();
        }
    });

    sieveThread.SetApartmentState(ApartmentState.STA);
    sieveThread.Start();
    sieveThread.Join();

    if (sieveError is not null) Console.WriteLine("     " + sieveError);

    var sieveProblems = sieveNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in sieveProblems) Console.WriteLine("     " + problem);

    Check("пресяването и въртенето минават без изключение", sieveError is null);
    Check($"направени са всичките {sieveNotes.Count} проверки", sieveNotes.Count == 23, $"{sieveNotes.Count}");
    Check("складът се пресява с отметки и се върти по месеци", sieveProblems.Count == 0,
        $"{sieveProblems.Count} проблема");
}


// =============================================== Фирмата в заглавията
Console.WriteLine();
Console.WriteLine("### ТЕСТ - заглавието на всеки екран носи избраната фирма");
{
    var titleNotes = new List<string>();
    string? titleError = null;

    var titleThread = new Thread(() =>
    {
        try
        {
            var titlePath = Path.Combine(work, $"title_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(titlePath);

            var metal = db.Companies.Add("203344556", "Метал ООД");
            var skrap = db.Companies.Add("204455667", "Скрап ЕООД");

            var scope = new CompanyScope(db);

            using var stock = new StockPage(service, db, scope);
            using var ledger = new LedgerPage(service, db, scope);
            using var work_ = new WorkPage(service, db, scope);
            using var lists = new ListsPage(service, db, scope);

            var pages = new (string Name, Func<string> Title)[]
            {
                ("Склад", () => stock.TitleText),
                ("Месец", () => ledger.TitleText),
                ("Отчети", () => work_.TitleText),
                ("Списъци", () => lists.TitleText)
            };

            foreach (var (name, title) in pages)
                titleNotes.Add(title() == $"{name} — Метал ООД"
                    ? "" : $"„{name}“ не носи фирмата: {title()}");

            // Смяната от лентата стига до всички наведнъж.
            scope.Use(skrap);

            foreach (var (name, title) in pages)
                titleNotes.Add(title() == $"{name} — Скрап ЕООД"
                    ? "" : $"„{name}“ не последва смяната: {title()}");

            scope.Use(metal);

            // Балансът казва какво НАИСТИНА е сборено, а не коя е в лентата.
            using var balance = new BalancePage(service, db, scope);

            titleNotes.Add(balance.TitleText == "Баланс — Метал ООД"
                ? "" : "балансът не носи фирмата: " + balance.TitleText);

            balance.UseAllCompanies(true);

            titleNotes.Add(balance.TitleText.Contains("2 фирми")
                ? "" : "балансът не казва, че събира две фирми: " + balance.TitleText);

            balance.UseAllCompanies(false);

            titleNotes.Add(balance.TitleText == "Баланс"
                ? "" : "без отметка балансът пак сочи фирма: " + balance.TitleText);

            // Без заведена фирма заглавието остава само името на екрана.
            using var empty = Workspace.Open(Path.Combine(work, $"notitle_{Guid.NewGuid():N}.db"));

            using var lonely = new StockPage(service, empty);

            titleNotes.Add(lonely.TitleText == "Склад"
                ? "" : "без фирми заглавието обещава дума, която няма: " + lonely.TitleText);
        }
        catch (Exception ex)
        {
            titleError = ex.ToString();
        }
    });

    titleThread.SetApartmentState(ApartmentState.STA);
    titleThread.Start();
    titleThread.Join();

    if (titleError is not null) Console.WriteLine("     " + titleError);

    var titleProblems = titleNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in titleProblems) Console.WriteLine("     " + problem);

    Check("заглавията се сглобяват без изключение", titleError is null);
    Check($"направени са всичките {titleNotes.Count} проверки", titleNotes.Count == 12, $"{titleNotes.Count}");
    Check("заглавието на всеки екран носи избраната фирма", titleProblems.Count == 0,
        $"{titleProblems.Count} проблема");
}


// =============================================== Третира се избраното
Console.WriteLine();
Console.WriteLine("### ТЕСТ - третира се само избраното, а не целият списък");
{
    var pickNotes = new List<string>();
    string? pickError = null;

    var pickThread = new Thread(() =>
    {
        try
        {
            var pickPath = Path.Combine(work, $"pick_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(pickPath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");

            // Три кода в склада — точно случаят от екрана: човек иска един от тях.
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 3), "150101", 10m,
                Eik: "204455667", Partner: "Скрап ЕООД"));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 4), "150102", 7m,
                Eik: "204455667", Partner: "Скрап ЕООД"));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 5), "200101", 4m,
                Eik: "205566778", Partner: "Метал груп ЕООД"));

            var moves = db.Ledger.Read(companyId, "1");
            var links = db.Ledger.Links(companyId, "1");
            var batches = db.Batches.List(companyId, "1");

            var shares = StockMath.Shares(batches, moves, links, StoreKind.Received,
                new DateOnly(2026, 9, 30));

            using var dialog = new TreatDialog(
                service, db, companyId, "1", shares, new DateOnly(2026, 9, 30));

            pickNotes.Add(dialog.RowCount == 3 ? "" : $"прозорчето показва {dialog.RowCount} реда вместо три");

            // Отваря се с всичко избрано — това си остава.
            pickNotes.Add(dialog.Planned().Count == 3
                ? "" : $"при отваряне се третират {dialog.Planned().Count} реда вместо три");

            // Едно щракване по ред: третира се САМО той. Дотук количествата
            // оставаха по трите реда и „Третирай“ вземаше целия склад.
            dialog.PickPile(1);

            pickNotes.Add(dialog.TakeAt(1) == "7,000" ? "" : "избраният ред остана без количество: " + dialog.TakeAt(1));
            pickNotes.Add(dialog.TakeAt(0).Length == 0 ? "" : "неизбраният ред пази количество: " + dialog.TakeAt(0));
            pickNotes.Add(dialog.TakeAt(2).Length == 0 ? "" : "третият ред пази количество: " + dialog.TakeAt(2));

            pickNotes.Add(dialog.Planned().Count == 1
                ? "" : $"третират се {dialog.Planned().Count} реда вместо един");

            pickNotes.Add(dialog.Planned().Count == 1 && dialog.Planned()[0].Ewc == "150102"
                ? "" : "третира се друг код: " + (dialog.Planned().Count == 1 ? dialog.Planned()[0].Ewc : "—"));

            // Няколко реда наведнъж — Ctrl, Shift или влачене.
            dialog.DragPiles(0, 2);

            pickNotes.Add(dialog.Planned().Count == 2
                ? "" : $"с два избрани реда се третират {dialog.Planned().Count}");

            pickNotes.Add(dialog.TakeAt(1).Length == 0 ? "" : "изваденият ред се върна в сметката");

            pickNotes.Add(dialog.TotalsText.Contains("14,000", StringComparison.Ordinal)
                ? "" : "сборът не следва избора: " + dialog.TotalsText);

            // Написаното количество преживява изваждането и връщането.
            dialog.DragPiles(0);
            dialog.UseTake(0, 3m);

            pickNotes.Add(dialog.Planned()[0].Amount == 3m
                ? "" : $"написаното не се пази: {dialog.Planned()[0].Amount}");

            dialog.PickPile(2);
            pickNotes.Add(dialog.TakeAt(0).Length == 0 ? "" : "изваденият ред още държи числото");

            dialog.PickPile(0);
            pickNotes.Add(dialog.TakeAt(0) == "3,000"
                ? "" : "върнатият ред не помни написаното: " + dialog.TakeAt(0));

            // Щракване в клетката за количество НЕ е избор: то не бива да
            // изважда останалите редове, докато човек посяга да пише.
            dialog.UseQuick(whole: true);
            dialog.PickTakeCell(1);

            pickNotes.Add(dialog.Planned().Count == 3
                ? "" : $"щракване за писане извади редове: останаха {dialog.Planned().Count}");

            // „Всичко“ и „Нищо“ работят върху същия избор.
            dialog.UseQuick(whole: false);
            pickNotes.Add(dialog.Planned().Count == 0 ? "" : "„Нищо“ остави редове за третиране");

            dialog.UseQuick(whole: true);
            pickNotes.Add(dialog.Planned().Count == 3 ? "" : "„Всичко“ не връща трите реда");
        }
        catch (Exception ex)
        {
            pickError = ex.ToString();
        }
    });

    pickThread.SetApartmentState(ApartmentState.STA);
    pickThread.Start();
    pickThread.Join();

    if (pickError is not null) Console.WriteLine("     " + pickError);

    var pickProblems = pickNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in pickProblems) Console.WriteLine("     " + problem);

    Check("изборът в третирането минава без изключение", pickError is null);
    Check($"направени са всичките {pickNotes.Count} проверки", pickNotes.Count == 16, $"{pickNotes.Count}");
    Check("третира се само избраното", pickProblems.Count == 0, $"{pickProblems.Count} проблема");
}


// =============================================== Датата води третирането
Console.WriteLine();
Console.WriteLine("### ТЕСТ - третирането следва датата и продължава без затваряне");
{
    var focusNotes = new List<string>();
    string? focusError = null;

    var focusThread = new Thread(() =>
    {
        try
        {
            var focusPath = Path.Combine(work, $"focus_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(focusPath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");

            // Три приемания на три дати: 3-и, 10-и и 20-и септември.
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 3), "150101", 10m,
                Eik: "204455667", Partner: "Скрап ЕООД"));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 10), "150102", 7m,
                Eik: "204455667", Partner: "Скрап ЕООД"));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 20), "200101", 4m,
                Eik: "205566778", Partner: "Метал груп ЕООД"));

            using var page = new StockPage(service, db);
            page.UseStore(StoreKind.Received);
            page.UseDate(new DateOnly(2026, 9, 30));

            var moves = db.Ledger.Read(companyId, "1");
            var links = db.Ledger.Links(companyId, "1");
            var batches = db.Batches.List(companyId, "1");

            var shares = StockMath.Shares(batches, moves, links, StoreKind.Received,
                new DateOnly(2026, 9, 30));

            using var dialog = new TreatDialog(
                service, db, companyId, "1", shares, new DateOnly(2026, 9, 30));

            focusNotes.Add(dialog.RowCount == 3 ? "" : $"прозорчето тръгва с {dialog.RowCount} реда вместо три");

            // Датата назад: каквото още не е дошло, не може да се третира.
            dialog.UseDate(new DateOnly(2026, 9, 15));

            focusNotes.Add(dialog.RowCount == 2
                ? "" : $"към 15.09 се предлагат {dialog.RowCount} реда вместо два");

            focusNotes.Add(dialog.Planned().All(m => m.Ewc != "200101")
                ? "" : "третира се код, който още не е приет");

            dialog.UseDate(new DateOnly(2026, 9, 5));

            focusNotes.Add(dialog.RowCount == 1
                ? "" : $"към 05.09 се предлага {dialog.RowCount} реда вместо един");

            dialog.UseDate(new DateOnly(2026, 9, 1));

            focusNotes.Add(dialog.RowCount == 0 ? "" : "преди първото приемане пак има какво да се третира");
            focusNotes.Add(!dialog.CanTreat ? "" : "при празен списък копчето още предлага третиране");

            // И обратно напред: списъкът се връща цял.
            dialog.UseDate(new DateOnly(2026, 9, 30));
            focusNotes.Add(dialog.RowCount == 3 ? "" : "напред списъкът не се връща цял");

            // Написаното се свива до наличното на новата дата, вместо да иска
            // повече, отколкото тогава е стояло.
            dialog.DragPiles(0);
            dialog.UseTake(0, 9m);
            dialog.UseDate(new DateOnly(2026, 9, 15));

            var first = dialog.Planned().FirstOrDefault(m => m.Ewc == "150101");

            focusNotes.Add(first?.Amount == 9m
                ? "" : $"написаното не преживя смяната: {first?.Amount}");

            // „Третирай и продължи“: записва и остава отворено.
            dialog.UseDate(new DateOnly(2026, 9, 30));
            dialog.DragPiles(2);

            // Задължителните полета от схемата — дейността R/D.
            focusNotes.Add(dialog.UseOperation(0) ? "" : "дейността няма меню");

            dialog.Write = (planned, date, fields, note) => page.Apply(planned, date, fields, note);

            dialog.TreatAndContinue();

            focusNotes.Add(dialog.Problems.Count == 0
                ? "" : "записът се спъна: " + string.Join("; ", dialog.Problems));

            focusNotes.Add(dialog.Saved == 1 ? "" : $"записани са {dialog.Saved} третирания вместо едно");
            focusNotes.Add(dialog.DoneText.Contains("1") ? "" : "не се казва какво е записано: " + dialog.DoneText);

            var treated = db.Ledger.Read(companyId, "1").Where(m => m.Kind == MoveKind.Treated).ToList();

            focusNotes.Add(treated.Count == 1 ? "" : $"в базата влязоха {treated.Count} третирания");
            focusNotes.Add(treated.Count == 1 && treated[0].Ewc == "200101"
                ? "" : "третиран е друг код");

            // Изтретираното си отива от списъка само̀ — него вече го няма в склада.
            focusNotes.Add(dialog.RowCount == 2
                ? "" : $"след записа остават {dialog.RowCount} реда вместо два");

            // И второ третиране, без прозорчето да се е затваряло.
            dialog.DragPiles(0);
            dialog.TreatAndContinue();

            focusNotes.Add(dialog.Saved == 2 ? "" : $"второто третиране не се брои: {dialog.Saved}");

            focusNotes.Add(db.Ledger.Read(companyId, "1").Count(m => m.Kind == MoveKind.Treated) == 2
                ? "" : "второто третиране не влезе в базата");

            focusNotes.Add(dialog.RowCount == 1
                ? "" : $"след второто записване остават {dialog.RowCount} реда вместо един");
        }
        catch (Exception ex)
        {
            focusError = ex.ToString();
        }
    });

    focusThread.SetApartmentState(ApartmentState.STA);
    focusThread.Start();
    focusThread.Join();

    if (focusError is not null) Console.WriteLine("     " + focusError);

    var focusProblems = focusNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in focusProblems) Console.WriteLine("     " + problem);

    Check("третирането по дата минава без изключение", focusError is null);
    Check($"направени са всичките {focusNotes.Count} проверки", focusNotes.Count == 18, $"{focusNotes.Count}");
    Check("третира се само стоялото на тази дата и работата продължава", focusProblems.Count == 0,
        $"{focusProblems.Count} проблема");
}


// =============================================== Смяната на датата пази избора
Console.WriteLine();
Console.WriteLine("### ТЕСТ - смяната на датата не разваля избраното");
{
    var keepNotes = new List<string>();
    string? keepError = null;

    var keepThread = new Thread(() =>
    {
        try
        {
            var keepPath = Path.Combine(work, $"keep_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(keepPath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");

            // Три доставки на един код от трима клиенти и втори код накрая.
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 3), "150101", 10m,
                Eik: "204455667", Partner: "Скрап ЕООД"));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 10), "150101", 7m,
                Eik: "205566778", Partner: "Метал груп ЕООД"));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 20), "150101", 5m,
                Eik: "206677889", Partner: "Желязо АД"));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 20), "200101", 4m,
                Eik: "205566778", Partner: "Метал груп ЕООД"));

            var moves = db.Ledger.Read(companyId, "1");
            var links = db.Ledger.Links(companyId, "1");
            var batches = db.Batches.List(companyId, "1");

            var shares = StockMath.Shares(batches, moves, links, StoreKind.Received,
                new DateOnly(2026, 9, 30));

            var middle = batches.Single(b => b.ReceivedOn == new DateOnly(2026, 9, 10)).Id;

            using var dialog = new TreatDialog(
                service, db, companyId, "1", shares, new DateOnly(2026, 9, 30));

            decimal Of(string ewc) => dialog.Planned().Where(m => m.Ewc == ewc).Sum(m => m.Amount);

            IEnumerable<long> From(string ewc) =>
                dialog.Planned().Where(m => m.Ewc == ewc).SelectMany(m => m.Draws).Select(d => d.BatchId);

            // И двата кода, а вдясно — само средната партида.
            dialog.DragPiles(0, 1);
            dialog.PickBatches(1);

            keepNotes.Add(Of("150101") == 7m ? "" : $"посочената партида дава {Of("150101")} вместо 7");

            // Датата се пише цифра по цифра — минава през 15-и.
            dialog.UseDate(new DateOnly(2026, 9, 15));

            keepNotes.Add(Of("150101") == 7m && From("150101").All(b => b == middle)
                ? "" : "смяната на датата върна партидите към „най-старата нататък“");

            // На 5-и посочената партида още я няма: не се взима чужда вместо нея.
            dialog.UseDate(new DateOnly(2026, 9, 5));

            keepNotes.Add(Of("150101") == 0m
                ? "" : $"преди посочената партида се взимат {Of("150101")} т от друга");

            // И обратно: всичко посочено се връща.
            dialog.UseDate(new DateOnly(2026, 9, 30));

            keepNotes.Add(Of("150101") == 7m && From("150101").All(b => b == middle)
                ? "" : "върнато на 30-и, посоченото по партиди е загубено");

            keepNotes.Add(Of("200101") == 4m
                ? "" : "код, който на междинна дата го нямаше, не се върна избран");

            // Числото по код отменя посоченото по партиди.
            dialog.UseTake(0, 12m);
            dialog.UseDate(new DateOnly(2026, 9, 29));

            keepNotes.Add(Of("150101") == 12m
                ? "" : $"написаното по код след смяна на датата е {Of("150101")} вместо 12");

            // Целият списък: търсене, пресяване, подреждане и избор.
            var pile = Treatment.Piles(shares).Single(p => p.Ewc == "150101");

            using var list = new BatchListDialog("Зад 150101", "Ще се вземе, т", pile.Shares,
                new Dictionary<long, decimal> { [middle] = 7m });

            keepNotes.Add(list.RowCount == 3 ? "" : $"целият списък има {list.RowCount} реда вместо три");
            keepNotes.Add(list.SumText.Contains("1 от 3")
                ? "" : "целият списък не тръгва с посоченото: " + list.SumText);

            list.UseSearch("желязо");
            keepNotes.Add(list.VisibleCount == 1 ? "" : $"търсенето оставя {list.VisibleCount} реда вместо един");

            // Избраното при търсене не изважда скритото.
            list.PickRows(2);
            keepNotes.Add(list.Takes.Count == 2 && list.Takes.ContainsKey(middle)
                ? "" : "избор при търсене извади скрита партида");

            list.UseSearch("");
            keepNotes.Add(list.VisibleCount == 3 ? "" : "изчистеното търсене не връща всички");

            list.FilterForTest("__partner", ["Скрап ЕООД — 204455667"]);
            keepNotes.Add(list.VisibleCount == 1 ? "" : $"пресяването оставя {list.VisibleCount} реда вместо един");
            list.FilterForTest("__partner", null);

            list.SortForTest("__stock", down: true);
            keepNotes.Add(list.StockAt(0) == 10m ? "" : "подреждането по наличното не е от голямото");

            list.UseTake(0, 3m);
            keepNotes.Add(list.Takes.Count == 3 && list.Takes.Values.Sum() == 15m
                ? "" : $"написаното в целия списък дава {list.Takes.Values.Sum()} вместо 15");

            // Върнатото отива в прозорчето като посочено по партиди.
            dialog.ApplyList(pile, list.Takes);
            keepNotes.Add(Of("150101") == 15m ? "" : $"върнатото от списъка дава {Of("150101")} вместо 15");

            dialog.UseDate(new DateOnly(2026, 9, 30));
            keepNotes.Add(Of("150101") == 15m ? "" : "върнатото от списъка не преживя смяната на датата");

            // Търсенето над таблиците: по клиент остават кодът и партидата му.
            var before = dialog.Planned().Sum(m => m.Amount);

            dialog.UseSearch("желязо");
            keepNotes.Add(dialog.VisiblePiles == 1 ? "" : $"търсене по клиент оставя {dialog.VisiblePiles} кода вместо един");
            keepNotes.Add(dialog.VisibleBatches == 1 ? "" : $"търсене по клиент оставя {dialog.VisibleBatches} партиди вместо една");

            // Скритото от търсенето не излиза от избора.
            keepNotes.Add(dialog.Planned().Sum(m => m.Amount) == before
                ? "" : $"търсенето извади скрито от избора: {dialog.Planned().Sum(m => m.Amount)} вместо {before}");

            // По код: кодът остава с всичките си партиди.
            dialog.UseSearch("200101");
            keepNotes.Add(dialog.VisiblePiles == 1 && dialog.VisibleBatches == 1
                ? "" : "търсене по код не остави само кода с партидите му");

            dialog.UseSearch("");
            keepNotes.Add(dialog.VisiblePiles == 2 ? "" : "изчистеното търсене не връща всички кодове");
        }
        catch (Exception ex)
        {
            keepError = ex.ToString();
        }
    });

    keepThread.SetApartmentState(ApartmentState.STA);
    keepThread.Start();
    keepThread.Join();

    if (keepError is not null) Console.WriteLine("     " + keepError);

    var keepProblems = keepNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in keepProblems) Console.WriteLine("     " + problem);

    Check("изборът при смяна на датата минава без изключение", keepError is null);
    Check($"направени са всичките {keepNotes.Count} проверки", keepNotes.Count == 21, $"{keepNotes.Count}");
    Check("смяната на датата пази избраното", keepProblems.Count == 0, $"{keepProblems.Count} проблема");
}


// =============================================== Образуването на лаптоп
//
// Вляво стоят разпределението и кодовете, вдясно — партидите. Колоната, в
// която се пише, не бива да остава зад воден плъзгач на петнайсетинчов екран.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - образуването се побира на лаптоп");
{
    var fitNotes = new List<string>();
    string? fitError = null;

    var fitThread = new Thread(() =>
    {
        try
        {
            var fitPath = Path.Combine(work, $"fit_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(fitPath);

            var company = db.Companies.Add("203344556", "Метал ООД");
            var batch = db.Batches.Add(company, new BatchDraft(
                "203344556", "Метал ООД", "160216", new DateOnly(2026, 9, 1)));

            var share = new StockShare("160216", db.Batches.Find(batch)!, 10m);

            using var dialog = new GenerateDialog(service, db, company, "1", [share], new DateOnly(2026, 9, 30));

            dialog.WindowState = FormWindowState.Normal;
            _ = dialog.Handle;
            // Петнайсетинчов лаптоп: 1920 точки при 125 % са 1536 на 100 %.
            dialog.ClientSize = new Size(1536, 700);

            var grids = Everything(dialog).OfType<DataGridView>().ToList();

            fitNotes.Add(grids.Count == 2 ? "" : $"{grids.Count} таблици вместо две");

            foreach (var grid in grids)
            {
                var need = grid.Columns.Cast<DataGridViewColumn>()
                    .Where(c => c.Visible)
                    .Sum(c => c.AutoSizeMode == DataGridViewAutoSizeColumnMode.Fill ? c.MinimumWidth : c.Width);

                fitNotes.Add(need <= grid.ClientSize.Width
                    ? "" : $"таблица с {grid.Columns.Count} колони иска {need}, а има {grid.ClientSize.Width}");
            }

            // Кодовете стоят вляво, под разпределението; партидите — вдясно.
            var split = Everything(dialog).OfType<SplitContainer>()
                .Single(x => x.Orientation == Orientation.Vertical);

            fitNotes.Add(split.Panel1.Contains(grids.Single(g => g.Columns.Count == 6)) &&
                         split.Panel2.Contains(grids.Single(g => g.Columns.Count == 5))
                ? "" : "кодовете не стоят вляво, а партидите — вдясно");
        }
        catch (Exception ex)
        {
            fitError = ex.ToString();
        }
    });

    fitThread.SetApartmentState(ApartmentState.STA);
    fitThread.Start();
    fitThread.Join();

    if (fitError is not null) Console.WriteLine("     " + fitError);

    var fitProblems = fitNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in fitProblems) Console.WriteLine("     " + problem);

    Check("образуването се сглобява без изключение", fitError is null);
    Check($"направени са всичките {fitNotes.Count} проверки", fitNotes.Count == 4, $"{fitNotes.Count}");
    Check("колоните вляво се побират на лаптоп", fitProblems.Count == 0, $"{fitProblems.Count} проблема" + (fitProblems.Count > 0 ? ": " + string.Join(" | ", fitProblems.Take(5)) : ""));
}


// =============================================== Предаване: вземи отгоре до тонаж
Console.WriteLine();
Console.WriteLine("### ТЕСТ - предаването взима отгоре до заявеното");
{
    var topNotes = new List<string>();
    string? topError = null;

    var topThread = new Thread(() =>
    {
        try
        {
            var topPath = Path.Combine(work, $"top_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(topPath);

            var company = db.Companies.Add("203344556", "Метал ООД");

            // Шест дребни партиди — десетки килограми — по дата; третата е от
            // клиент, чието количество не се предава.
            var amounts = new[] { 0.05m, 0.08m, 0.10m, 0.06m, 0.09m, 0.07m };
            var shares = new List<StockShare>();

            for (var i = 0; i < amounts.Length; i++)
            {
                var (eik, name) = i == 2 ? ("209988776", "Лош ЕООД") : ("204455667", "Скрап ЕООД");

                var id = db.Batches.Add(company, new BatchDraft(eik, name, "160216", new DateOnly(2026, 9, 1 + i)));
                shares.Add(new StockShare("160216", db.Batches.Find(id)!, amounts[i]));
            }

            // Самото правило: цели редове, последният — колкото липсва.
            var rule = Handover.FromTop(shares, 0.2m);

            topNotes.Add(rule.Count == 3 && rule.Sum(p => p.Amount) == 0.2m && rule[^1].Amount == 0.07m
                ? "" : $"правилото взе {rule.Count} реда за {rule.Sum(p => p.Amount)} т");

            topNotes.Add(Handover.FromTop(shares, 9m).Sum(p => p.Amount) == amounts.Sum()
                ? "" : "при недостиг правилото не взима всичко налично");

            using var dialog = new SubmitDialog(service, db, company, "1", shares, new DateOnly(2026, 9, 30));

            dialog.PickPile(0);

            // Нежеланият клиент се пресява, а редът е по дата — подразбирането.
            dialog.HideBatchesOf("Лош");
            dialog.UseFromTop(0.2m);

            topNotes.Add(dialog.PickedTotal == 0.2m ? "" : $"взети са {dialog.PickedTotal} т вместо 0,2");

            var takes = dialog.OriginTakes();

            topNotes.Add(takes.Count == 6 && takes[2].Length == 0
                ? "" : "пресятата партида влезе в предаването");

            // 0,05 + 0,08 + 0,06 = 0,19 — четвъртата видима дава само 0,01.
            topNotes.Add(takes[4] == SplitMath.Amount(0.01m) && takes[5].Length == 0
                ? "" : "последният ред не е изравнен: " + string.Join(" | ", takes));

            topNotes.Add(dialog.FromTopText.Contains("изравнена")
                ? "" : "не се казва кой ред е изравнен: " + dialog.FromTopText);

            // Повече от видимото: взима се всичко видимо и се казва, че не стига.
            dialog.UseFromTop(5m);

            topNotes.Add(dialog.PickedTotal == 0.35m ? "" : $"при недостиг са взети {dialog.PickedTotal} т вместо 0,35");
            topNotes.Add(dialog.FromTopText.Contains("стигат само")
                ? "" : "недостигът не се казва: " + dialog.FromTopText);

            // Целият списък прави същото в своя ред.
            using var list = new BatchListDialog("Зад 160216", "Ще се вземе, т", shares,
                new Dictionary<long, decimal>(), fromTop: 0.2m);

            list.SortForTest("__stock", down: true);
            list.UseFromTop(0.2m);

            // Отгоре надолу по наличното: 0,10 + 0,09 = 0,19, после 0,01 от 0,08.
            topNotes.Add(list.Takes.Count == 3 && list.Takes.Values.Sum() == 0.2m
                ? "" : $"целият списък взе {list.Takes.Count} партиди за {list.Takes.Values.Sum()} т");
        }
        catch (Exception ex)
        {
            topError = ex.ToString();
        }
    });

    topThread.SetApartmentState(ApartmentState.STA);
    topThread.Start();
    topThread.Join();

    if (topError is not null) Console.WriteLine("     " + topError);

    var topProblems = topNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in topProblems) Console.WriteLine("     " + problem);

    Check("взимането отгоре минава без изключение", topError is null);
    Check($"направени са всичките {topNotes.Count} проверки", topNotes.Count == 9, $"{topNotes.Count}");
    Check("взима се отгоре до заявеното, а последният ред се изравнява", topProblems.Count == 0,
        $"{topProblems.Count} проблема");
}


// =============================================== Затварянето живее в настройките
Console.WriteLine();
Console.WriteLine("### ТЕСТ - месеците се затварят от свое място, а не от работния екран");
{
    var lockNotes = new List<string>();
    string? lockError = null;

    var lockThread = new Thread(() =>
    {
        try
        {
            var lockPath = Path.Combine(work, $"lock_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(lockPath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");
            var september = new LedgerPeriod(2026, 9);
            var october = new LedgerPeriod(2026, 10);

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 3), "160216", 20m,
                Eik: "204455667", Partner: "Скрап ЕООД"));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 10, 3), "160216", 5m,
                Eik: "204455667", Partner: "Скрап ЕООД"));

            // Третирано без обявено образувано: това спира затварянето.
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Treated, new DateOnly(2026, 9, 10), "160216", 15m, EwcOut: "191202"));

            var scope = new CompanyScope(db);

            using (var periods = new PeriodsDialog(db, scope))
            {
                lockNotes.Add(periods.RowCount == 2
                    ? "" : $"месеците в списъка са {periods.RowCount} вместо два");

                lockNotes.Add(!periods.LockOffered ? "" : "без избран ред копчето е живо");

                lockNotes.Add(periods.Pick("1", september) ? "" : "септември го няма в списъка");
                lockNotes.Add(periods.LockText.Contains("Затвори") ? "" : "копчето не предлага затваряне");

                // Спиращата контрола не се прескача и оттук.
                lockNotes.Add(!periods.CloseSelected()
                    ? "" : "месец с чакащо третиране се затвори");

                lockNotes.Add(!db.Periods.IsClosed(companyId, "1", september)
                    ? "" : "спряното затваряне все пак стигна до базата");
            }

            // Обявеното образувано маха пречката.
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Generated, new DateOnly(2026, 9, 11), "191202", 15m));

            using (var periods = new PeriodsDialog(db, scope))
            {
                lockNotes.Add(periods.Pick("1", september) ? "" : "септември изчезна от списъка");
                lockNotes.Add(periods.CloseSelected() ? "" : "чистият месец не се затваря");
                lockNotes.Add(db.Periods.IsClosed(companyId, "1", september)
                    ? "" : "затварянето не стигна до базата");

                lockNotes.Add(periods.Changed ? "" : "прозорчето не обяви, че е пипало");
                lockNotes.Add(periods.SummaryText.Contains("1") ? "" : "не се брои колко са затворени: " + periods.SummaryText);

                // И обратно — отварянето не пита за контроли.
                lockNotes.Add(periods.Pick("1", september) ? "" : "затвореният месец изпадна от списъка");
                lockNotes.Add(periods.LockText.Contains("Отвори") ? "" : "копчето не предлага отваряне");
                lockNotes.Add(periods.ReopenSelected() ? "" : "затвореният месец не се отваря");
                lockNotes.Add(!db.Periods.IsClosed(companyId, "1", september)
                    ? "" : "отварянето не стигна до базата");
            }

            // Октомври няма движение, което да го спира — той е отделен ред.
            using (var periods = new PeriodsDialog(db, scope))
            {
                lockNotes.Add(periods.Pick("1", october) ? "" : "октомври го няма в списъка");
                lockNotes.Add(periods.CloseSelected() ? "" : "октомври не се затваря");
                lockNotes.Add(db.Periods.IsClosed(companyId, "1", october)
                    ? "" : "затварянето на октомври не стигна до базата");
                lockNotes.Add(!db.Periods.IsClosed(companyId, "1", september)
                    ? "" : "затварянето на октомври хвана и септември");
            }

            // Работният екран вече не предлага затваряне — само казва състоянието.
            using (var ledger = new LedgerPage(service, db, scope))
            {
                ledger.UseMonth(october);
                lockNotes.Add(ledger.ChecksText.Contains("затворен")
                    ? "" : "„Месец“ не казва, че месецът е затворен: " + ledger.ChecksText);
            }
        }
        catch (Exception ex)
        {
            lockError = ex.ToString();
        }
    });

    lockThread.SetApartmentState(ApartmentState.STA);
    lockThread.Start();
    lockThread.Join();

    if (lockError is not null) Console.WriteLine("     " + lockError);

    var lockProblems = lockNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in lockProblems) Console.WriteLine("     " + problem);

    Check("затварянето на месеци минава без изключение", lockError is null);
    Check($"направени са всичките {lockNotes.Count} проверки", lockNotes.Count == 20, $"{lockNotes.Count}");
    Check("месеците се затварят и отварят от свое място", lockProblems.Count == 0,
        $"{lockProblems.Count} проблема");
}


// =============================================== Партидите се избират поотделно
Console.WriteLine();
Console.WriteLine("### ТЕСТ - третират се избрани партиди, а не най-старите по ред");
{
    var pickNotes = new List<string>();
    string? pickError = null;

    var pickThread = new Thread(() =>
    {
        try
        {
            var pickPath = Path.Combine(work, $"batches_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(pickPath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");

            // Един код, три доставки от трима клиента — точно случаят, в който
            // FIFO не отговаря на въпроса „тази и онази“.
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 3), "150101", 10m,
                Eik: "204455667", Partner: "Скрап ЕООД"));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 5), "150101", 6m,
                Eik: "205566778", Partner: "Метал груп ЕООД"));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 7), "150101", 4m,
                Eik: "206677889", Partner: "Вторични ЕООД"));

            using var page = new StockPage(service, db);

            var moves = db.Ledger.Read(companyId, "1");
            var links = db.Ledger.Links(companyId, "1");
            var batches = db.Batches.List(companyId, "1");

            var shares = StockMath.Shares(batches, moves, links, StoreKind.Received,
                new DateOnly(2026, 9, 30));

            using var dialog = new TreatDialog(
                service, db, companyId, "1", shares, new DateOnly(2026, 9, 30));

            dialog.PickPile(0);

            pickNotes.Add(dialog.OriginCount == 3
                ? "" : $"зад кода стоят {dialog.OriginCount} партиди вместо три");

            pickNotes.Add(dialog.Planned().Count == 1 && dialog.Planned()[0].Draws.Count == 3
                ? "" : "при отваряне не се взима от трите партиди");

            // Само средната партида: изборът долу решава, не редът на приемане.
            dialog.PickBatches(1);

            var one = dialog.Planned();

            pickNotes.Add(one.Count == 1 && one[0].Draws.Count == 1
                ? "" : $"избраната партида дава {one.Count} движения с {(one.Count > 0 ? one[0].Draws.Count : 0)} дяла");

            pickNotes.Add(one.Count == 1 && one[0].Amount == 6m
                ? "" : $"взима се {(one.Count > 0 ? one[0].Amount : 0)} т вместо шест");

            pickNotes.Add(dialog.TakeAt(0) == "6,000"
                ? "" : "редът на кода не показва сбора на избраните партиди: " + dialog.TakeAt(0));

            pickNotes.Add(dialog.OriginTakeAt(0).Length == 0 && dialog.OriginTakeAt(2).Length == 0
                ? "" : "неизбрани партиди пак се взимат");

            // Две партиди наведнъж — това е искането: избери няколко и третирай.
            dialog.PickBatches(0, 2);

            var two = dialog.Planned();

            pickNotes.Add(two.Count == 1 && two[0].Draws.Count == 2
                ? "" : "двете избрани партиди не дават два дяла");

            pickNotes.Add(two.Count == 1 && two[0].Amount == 14m
                ? "" : $"сборът на двете е {(two.Count > 0 ? two[0].Amount : 0)} т вместо 14");

            // Част от една партида: пише се в самия ред.
            dialog.UseBatchTake(0, 4m);

            var part = dialog.Planned();

            pickNotes.Add(part.Count == 1 && part[0].Amount == 8m
                ? "" : $"частта не се брои: {(part.Count > 0 ? part[0].Amount : 0)} т вместо осем");

            pickNotes.Add(dialog.TakeAt(0) == "8,000"
                ? "" : "горният ред не следва частта: " + dialog.TakeAt(0));

            // Повече от стоялото не се взима.
            dialog.UseBatchTake(0, 99m);

            pickNotes.Add(dialog.OriginTakeAt(0) == "10,000"
                ? "" : "взима се повече от наличното по партидата: " + dialog.OriginTakeAt(0));

            // Всяка избрана партида поотделно — по един ред в „Третиран“.
            dialog.UsePerBatch(true);

            var apart = dialog.Planned();

            pickNotes.Add(apart.Count == 2 && apart.All(m => m.Draws.Count == 1)
                ? "" : $"поотделно дават {apart.Count} движения");

            dialog.UsePerBatch(false);

            // И записване: изтретираните партиди изчезват от списъка.
            dialog.PickBatches(2);
            dialog.UseOperation(0);
            dialog.Write = (planned, date, fields, note) => page.Apply(planned, date, fields, note);
            dialog.TreatAndContinue();

            pickNotes.Add(dialog.Problems.Count == 0
                ? "" : "записът се спъна: " + string.Join("; ", dialog.Problems));

            pickNotes.Add(dialog.Saved == 1 ? "" : $"записани са {dialog.Saved} третирания вместо едно");

            dialog.PickPile(0);

            pickNotes.Add(dialog.OriginCount == 2
                ? "" : $"изтретираната партида остана в списъка: {dialog.OriginCount} реда");

            var left = StockMath.Stock(db.Ledger.Read(companyId, "1"), StoreKind.Received,
                new DateOnly(2026, 9, 30));

            pickNotes.Add(left.GetValueOrDefault("150101") == 16m
                ? "" : $"в склада остават {left.GetValueOrDefault("150101")} т вместо 16");
        }
        catch (Exception ex)
        {
            pickError = ex.ToString();
        }
    });

    pickThread.SetApartmentState(ApartmentState.STA);
    pickThread.Start();
    pickThread.Join();

    if (pickError is not null) Console.WriteLine("     " + pickError);

    var pickProblems = pickNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in pickProblems) Console.WriteLine("     " + problem);

    Check("изборът по партиди минава без изключение", pickError is null);
    Check($"направени са всичките {pickNotes.Count} проверки", pickNotes.Count == 16, $"{pickNotes.Count}");
    Check("третират се избраните партиди", pickProblems.Count == 0, $"{pickProblems.Count} проблема");
}


// =============================================== Подреждане и залепване
Console.WriteLine();
Console.WriteLine("### ТЕСТ - третирането се подрежда по заглавие и приема залепени количества");
{
    var sortNotes = new List<string>();
    string? sortError = null;

    var sortThread = new Thread(() =>
    {
        try
        {
            var sortPath = Path.Combine(work, $"sort_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(sortPath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");

            // Три партиди от един код, нарочно в друг ред по количество и по клиент.
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 3), "150101", 10m,
                Eik: "204455667", Partner: "Скрап ЕООД"));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 5), "150101", 25m,
                Eik: "205566778", Partner: "Албена ЕООД"));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 7), "150101", 4m,
                Eik: "206677889", Partner: "Вторични ЕООД"));

            // И втори код, за да има какво да се подрежда и горе.
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 4), "200101", 40m,
                Eik: "204455667", Partner: "Скрап ЕООД"));

            var moves = db.Ledger.Read(companyId, "1");
            var links = db.Ledger.Links(companyId, "1");
            var batches = db.Batches.List(companyId, "1");

            var shares = StockMath.Shares(batches, moves, links, StoreKind.Received,
                new DateOnly(2026, 9, 30));

            using var dialog = new TreatDialog(
                service, db, companyId, "1", shares, new DateOnly(2026, 9, 30));

            // Горе: по подразбиране по код.
            sortNotes.Add(dialog.CodeAt(0).StartsWith("150101", StringComparison.Ordinal)
                ? "" : "горе не тръгва по код: " + dialog.CodeAt(0));

            // Щракване по „Налично“ подрежда от голямото надолу.
            dialog.SortPiles(3);

            sortNotes.Add(dialog.CodeAt(0).StartsWith("200101", StringComparison.Ordinal)
                ? "" : "по налично отгоре не е най-голямото: " + dialog.CodeAt(0));

            // Второ щракване обръща посоката.
            dialog.SortPiles(3);

            sortNotes.Add(dialog.CodeAt(0).StartsWith("150101", StringComparison.Ordinal)
                ? "" : "посоката не се обръща: " + dialog.CodeAt(0));

            // Долу: партидите на първия код.
            dialog.SortPiles(0);
            dialog.PickPile(0);

            sortNotes.Add(dialog.OriginCount == 3
                ? "" : $"долу стоят {dialog.OriginCount} партиди вместо три");

            sortNotes.Add(dialog.BatchAt(0) == "#2026-0001"
                ? "" : "долу не тръгва от най-старата: " + dialog.BatchAt(0));

            // По налично: 25, 10, 4.
            dialog.SortShares(3);

            sortNotes.Add(dialog.OriginTakeAt(0).Length > 0 && dialog.BatchAt(0) == "#2026-0002"
                ? "" : "по налично долу отгоре не е най-голямата: " + dialog.BatchAt(0));

            sortNotes.Add(dialog.ShareGlyph(3) == "Descending"
                ? "" : "стрелката не сочи надолу: " + dialog.ShareGlyph(3));

            dialog.SortShares(3);
            sortNotes.Add(dialog.BatchAt(0) == "#2026-0003"
                ? "" : "долу посоката не се обръща: " + dialog.BatchAt(0));

            // По клиент — по азбучен ред: Албена, Вторични, Скрап.
            dialog.SortShares(1);
            sortNotes.Add(dialog.BatchAt(0) == "#2026-0002"
                ? "" : "по клиент не тръгва от Албена: " + dialog.BatchAt(0));

            // Подредбата не мени избора: тя е изглед.
            sortNotes.Add(dialog.Planned().Count == 1 && dialog.Planned()[0].Draws.Count == 3
                ? "" : "подреждането промени избраното");

            // Залепване като от Excel — по ред на видимото.
            dialog.SortShares(0);

            var taken = dialog.PasteInto(batches: true, from: 0, text: "3\n2\n1\n");

            sortNotes.Add(taken == 3 ? "" : $"залепени са {taken} реда вместо три");

            sortNotes.Add(dialog.OriginTakeAt(0) == "3,000" && dialog.OriginTakeAt(2) == "1,000"
                ? "" : "залепените числа не стигат до редовете");

            sortNotes.Add(dialog.Planned().Count == 1 && dialog.Planned()[0].Amount == 6m
                ? "" : $"сборът след залепване е {(dialog.Planned().Count > 0 ? dialog.Planned()[0].Amount : 0)} т вместо шест");

            // Празен ред маха количеството, а над наличното се свежда до него.
            dialog.PasteInto(batches: true, from: 0, text: "\n999\n");

            sortNotes.Add(dialog.OriginTakeAt(0).Length == 0 ? "" : "празното не махна количеството");

            sortNotes.Add(dialog.OriginTakeAt(1) == "25,000"
                ? "" : "залепеното над наличното не се свежда: " + dialog.OriginTakeAt(1));

            // И в горната таблица: по един код на ред.
            dialog.PasteInto(batches: false, from: 0, text: "7\n8\n");

            sortNotes.Add(dialog.TakeAt(0) == "7,000" && dialog.TakeAt(1) == "8,000"
                ? "" : $"горе залепеното не стигна: {dialog.TakeAt(0)} / {dialog.TakeAt(1)}");

        }
        catch (Exception ex)
        {
            sortError = ex.ToString();
        }
    });

    sortThread.SetApartmentState(ApartmentState.STA);
    sortThread.Start();
    sortThread.Join();

    if (sortError is not null) Console.WriteLine("     " + sortError);

    var sortProblems = sortNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in sortProblems) Console.WriteLine("     " + problem);

    Check("подреждането и залепването минават без изключение", sortError is null);
    Check($"направени са всичките {sortNotes.Count} проверки", sortNotes.Count == 16, $"{sortNotes.Count}");
    Check("третирането се подрежда и приема залепено", sortProblems.Count == 0,
        $"{sortProblems.Count} проблема");
}


// =============================================== Сборовете под заглавието
Console.WriteLine();
Console.WriteLine("### ТЕСТ - сборовете стоят под заглавието и следват ситото");
{
    var sumNotes = new List<string>();
    string? sumError = null;

    var sumThread = new Thread(() =>
    {
        try
        {
            var sumPath = Path.Combine(work, $"sums_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(sumPath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 3), "150101", 10m,
                Eik: "204455667", Partner: "Скрап ЕООД"));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 5), "200101", 6m,
                Eik: "205566778", Partner: "Метал груп ЕООД"));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Treated, new DateOnly(2026, 9, 10), "150101", 4m, EwcOut: "191202"));

            // „Месец“: сборовете на месеца стоят винаги.
            using (var ledger = new LedgerPage(service, db))
            {
                ledger.UseMonth(new LedgerPeriod(2026, 9));

                sumNotes.Add(ledger.SummaryText.Contains("Получено 16,000", StringComparison.Ordinal)
                    ? "" : "сборът на месеца липсва: " + ledger.SummaryText);

                sumNotes.Add(!ledger.SummaryText.Contains("Показани", StringComparison.Ordinal)
                    ? "" : "без сито се изписва „показани“: " + ledger.SummaryText);

                // Със сито: казва се какво точно се вижда.
                ledger.UseKind(1);

                sumNotes.Add(ledger.SummaryText.Contains("Показани 2 реда", StringComparison.Ordinal)
                    ? "" : "ситото не се брои: " + ledger.SummaryText);

                sumNotes.Add(ledger.SummaryText.Contains("16,000 т", StringComparison.Ordinal)
                    ? "" : "сборът на показаното липсва: " + ledger.SummaryText);

                ledger.UseKind(2);

                sumNotes.Add(ledger.SummaryText.Contains("Показани 1 реда", StringComparison.Ordinal)
                    && ledger.SummaryText.Contains("4,000 т", StringComparison.Ordinal)
                    ? "" : "другият вид дава друг сбор: " + ledger.SummaryText);

                // Сборовете на месеца не мърдат със ситото.
                sumNotes.Add(ledger.SummaryText.Contains("третирано 4,000", StringComparison.Ordinal)
                    ? "" : "сборовете на месеца се менят със ситото: " + ledger.SummaryText);

                ledger.UseKind(0);
                sumNotes.Add(!ledger.SummaryText.Contains("Показани", StringComparison.Ordinal)
                    ? "" : "махнатото сито още се брои");

                // Сборът стои и в заглавието на колоната, под името ѝ. Без сито
                // това е сборът на ВСИЧКИ видими редове — и трите движения.
                sumNotes.Add(ledger.AmountHeaderText.Contains("20,000", StringComparison.Ordinal)
                    ? "" : "колоната не носи сбора си: " + ledger.AmountHeaderText.Replace(Environment.NewLine, " / "));

                ledger.UseKind(2);

                sumNotes.Add(ledger.AmountHeaderText.Contains("4,000", StringComparison.Ordinal)
                    ? "" : "сборът в заглавието не следва ситото: " +
                           ledger.AmountHeaderText.Replace(Environment.NewLine, " / "));

                // Копчето „Състави отчет…“ не бива да е смачкано от съседа си.
                sumNotes.Add(ledger.ComposeHeight >= 30
                    ? "" : $"копчето „Състави отчет…“ е {ledger.ComposeHeight} пиксела високо");
            }

            // „Склад“: сборът е на видимото и следва филтрите.
            using (var stock = new StockPage(service, db))
            {
                stock.UseStore(StoreKind.Received);
                stock.UseDate(new DateOnly(2026, 9, 30));

                sumNotes.Add(stock.SummaryText.Contains("12,000", StringComparison.Ordinal)
                    ? "" : "складът не показва сбора си: " + stock.SummaryText);

                stock.UseCodeFilter("200101");

                sumNotes.Add(stock.SummaryText.Contains("6,000", StringComparison.Ordinal)
                    ? "" : "сборът не следва филтъра: " + stock.SummaryText);

                sumNotes.Add(stock.AmountHeaderText.Contains("6,000", StringComparison.Ordinal)
                    ? "" : "заглавието на колоната не следва филтъра: " +
                           stock.AmountHeaderText.Replace(Environment.NewLine, " / "));

                stock.UseCodeFilter("");

                sumNotes.Add(stock.AmountHeaderText.Contains("12,000", StringComparison.Ordinal)
                    ? "" : "заглавието не се връща на пълния сбор: " +
                           stock.AmountHeaderText.Replace(Environment.NewLine, " / "));

                // И долната таблица: сборът на партидите зад избрания ред — той е
                // точно количеството на реда горе.
                stock.Pick(0);

                sumNotes.Add(stock.DetailAmountHeaderText.Contains(stock.AmountAt(0), StringComparison.Ordinal)
                    ? "" : $"долу сборът не отговаря на реда горе ({stock.AmountAt(0)}): " +
                           stock.DetailAmountHeaderText.Replace(Environment.NewLine, " / "));

                stock.UseStore(StoreKind.Treated);

                sumNotes.Add(stock.SummaryText.Contains("4,000", StringComparison.Ordinal)
                    ? "" : "сборът не следва склада: " + stock.SummaryText);
            }
        }
        catch (Exception ex)
        {
            sumError = ex.ToString();
        }
    });

    sumThread.SetApartmentState(ApartmentState.STA);
    sumThread.Start();
    sumThread.Join();

    if (sumError is not null) Console.WriteLine("     " + sumError);

    var sumProblems = sumNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in sumProblems) Console.WriteLine("     " + problem);

    Check("сборовете под заглавието минават без изключение", sumError is null);
    Check($"направени са всичките {sumNotes.Count} проверки", sumNotes.Count == 16, $"{sumNotes.Count}");
    Check("сборовете стоят под заглавието и следват ситото", sumProblems.Count == 0,
        $"{sumProblems.Count} проблема");
}


// =============================================== Нито едно копче не е смачкано
//
// Закачането в Windows Forms реди контролите в обратен ред на добавяне и
// последният добавен взима цялата височина. Един ред, разменен по погрешка,
// оставя съседа си с дванайсет пиксела — копчето се показва нарязано, а нищо в
// кода не изглежда сбъркано. Случи се веднъж и се видя чак на снимка от
// потребител.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - нито едно копче не е смачкано от съседа си");
{
    var squashed = new List<string>();
    string? squashError = null;

    var squashThread = new Thread(() =>
    {
        try
        {
            var squashPath = Path.Combine(work, $"squash_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(squashPath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 3), "150101", 10m,
                Eik: "204455667", Partner: "Скрап ЕООД"));

            var scope = new CompanyScope(db);

            var pages = new (string Name, Func<Control> Make)[]
            {
                ("Месец", () => new LedgerPage(service, db, scope)),
                ("Склад", () => new StockPage(service, db, scope)),
                ("Отчети", () => new WorkPage(service, db, scope)),
                ("Списъци", () => new ListsPage(service, db, scope)),
                ("Баланс", () => new BalancePage(service, db, scope)),
                ("Настройки", () => new SettingsPage(db, scope))
            };

            // Тясно нарочно: на лаптоп съдържанието получава около хиляда
            // пиксела и точно там се смачкват нещата.
            foreach (var width in new[] { 1366, 1700 })
            {
                foreach (var (name, make) in pages)
                {
                    using var host = new Form
                    {
                        StartPosition = FormStartPosition.Manual,
                        Location = new Point(-4000, -4000),
                        ShowInTaskbar = false,
                        ClientSize = new Size(width, 800)
                    };

                    var page = make();
                    page.Dock = DockStyle.Fill;
                    host.Controls.Add(page);
                    host.Show();
                    host.PerformLayout();
                    Application.DoEvents();

                    Walk(page, name, width);

                    host.Hide();
                }
            }

            void Walk(Control parent, string page, int width)
            {
                foreach (Control child in parent.Controls)
                {
                    if (child is AppButton button && button.Visible)
                    {
                        // Прагът е двайсет, а не трийсет: стрелките до датата са
                        // високи точно колкото полето до тях (двайсет и три) и
                        // това е нарочно. Смачканото обаче пада до дванайсет и
                        // по-малко — разликата е достатъчна.
                        if (button.Height < 20)
                            squashed.Add($"{page} при {width}: „{button.Text}“ е {button.Height} пиксела високо");

                        if (button.Width < 20)
                            squashed.Add($"{page} при {width}: „{button.Text}“ е {button.Width} пиксела широко");
                    }

                    Walk(child, page, width);
                }
            }
        }
        catch (Exception ex)
        {
            squashError = ex.ToString();
        }
    });

    squashThread.SetApartmentState(ApartmentState.STA);
    squashThread.Start();
    squashThread.Join();

    if (squashError is not null) Console.WriteLine("     " + squashError);

    foreach (var one in squashed.Take(10)) Console.WriteLine("     " + one);

    Check("страниците се сглобяват без изключение", squashError is null);
    Check("нито едно копче не е смачкано", squashed.Count == 0, $"{squashed.Count} смачкани");
}


// =============================================== Частично третиране
Console.WriteLine();
Console.WriteLine("### ТЕСТ - частичното третиране изнася избраното в свой прозорец");
{
    var partNotes = new List<string>();
    string? partError = null;

    var partThread = new Thread(() =>
    {
        try
        {
            var partPath = Path.Combine(work, $"partial_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(partPath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");

            // Пет доставки по един код и една по друг — за да има какво да се
            // изнесе и какво да остане отвън.
            foreach (var (day, amount, eik, name) in new (int, decimal, string, string)[]
            {
                (3, 10m, "204455667", "Скрап ЕООД"),
                (4, 6m, "205566778", "Метал груп ЕООД"),
                (5, 4m, "206677889", "Вторични ЕООД"),
                (6, 8m, "204455667", "Скрап ЕООД"),
                (7, 2m, "205566778", "Метал груп ЕООД")
            })
                db.Writer.Write(companyId, new MoveRequest(
                    MoveKind.Received, new DateOnly(2026, 9, day), "150101", amount,
                    Eik: eik, Partner: name));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 8), "200101", 40m,
                Eik: "204455667", Partner: "Скрап ЕООД"));

            var moves = db.Ledger.Read(companyId, "1");
            var links = db.Ledger.Links(companyId, "1");
            var batches = db.Batches.List(companyId, "1");

            var shares = StockMath.Shares(batches, moves, links, StoreKind.Received,
                new DateOnly(2026, 9, 30));

            using var dialog = new TreatDialog(
                service, db, companyId, "1", shares, new DateOnly(2026, 9, 30));

            dialog.PickPile(0);

            // Обхватът: избраните долу партиди.
            dialog.PickBatches(1, 3);

            var scope = dialog.PartialScope();

            partNotes.Add(scope.Count == 2
                ? "" : $"в частичното влизат {scope.Count} партиди вместо две");

            // Без избрано долу — партидите на избрания код.
            dialog.PickBatches();
            dialog.PickPile(0);

            partNotes.Add(dialog.PartialScope().Count == 5
                ? "" : $"без избрани партиди влизат {dialog.PartialScope().Count} вместо петте на кода");

            // Самото прозорче: само избраното, с наличните количества.
            dialog.PickPile(0);
            dialog.PickBatches(1, 3);
            scope = dialog.PartialScope();

            var taken = scope.ToDictionary(s => s.Batch.Id, _ => 0m);

            using (var partial = new PartialDialog(scope, taken))
            {
                partNotes.Add(partial.RowCount == 2
                    ? "" : $"прозорчето показва {partial.RowCount} реда вместо два");

                partNotes.Add(partial.SumText.Contains("0,000 т от", StringComparison.Ordinal)
                    ? "" : "тръгва с нещо избрано: " + partial.SumText);

                // Пише се по ред; над наличното се свежда до него.
                partial.UseTake(0, 3m);
                partial.UseTake(1, 99m);

                partNotes.Add(partial.TakeAt(0) == "3,000" ? "" : "написаното не остана: " + partial.TakeAt(0));

                partNotes.Add(partial.Takes.Count == 2 ? "" : $"пазят се {partial.Takes.Count} количества вместо две");

                var second = scope.OrderBy(s => s.Batch.ReceivedOn).ThenBy(s => s.Batch.Id).ToList()[1];

                partNotes.Add(partial.Takes[second.Batch.Id] == second.Amount
                    ? "" : "над наличното не се свежда до него");

                partNotes.Add(partial.SumText.Contains("11,000", StringComparison.Ordinal)
                    ? "" : "сборът долу не се брои: " + partial.SumText);

                // „Всичко“ и „Нищо“.
                partial.UseAll(whole: true);
                partNotes.Add(partial.Takes.Values.Sum() == scope.Sum(s => s.Amount)
                    ? "" : "„Всичко“ не взима цялото налично");

                partial.UseAll(whole: false);
                partNotes.Add(partial.Takes.Count == 0 ? "" : "„Нищо“ остави количества");

                // Залепване от Excel.
                var pasted = partial.PasteFrom(0, "2\n5\n");

                partNotes.Add(pasted == 2 ? "" : $"залепени са {pasted} реда вместо два");
                partNotes.Add(partial.TakeAt(0) == "2,000" ? "" : "залепеното не стигна: " + partial.TakeAt(0));

                // Едно число върху избрана област го слага навсякъде — както в Excel.
                partial.UseAll(whole: false);
                partial.PickCells(0, 1);

                partNotes.Add(partial.ChosenCount == 2
                    ? "" : $"маркирани са {partial.ChosenCount} клетки вместо две");

                var spread = partial.PasteHere("1,5");

                partNotes.Add(spread == 2 ? "" : $"едно число покри {spread} клетки вместо две");
                partNotes.Add(partial.TakeAt(0) == "1,500" && partial.TakeAt(1) == "1,500"
                    ? "" : $"числото не се разля: {partial.TakeAt(0)} / {partial.TakeAt(1)}");

                // Delete изчиства избраното, но редовете остават.
                partial.PickCells(0);
                partial.ClearCells();

                partNotes.Add(partial.TakeAt(0).Length == 0 && partial.RowCount == 2
                    ? "" : "изчистването пипна редовете");

                partNotes.Add(partial.TakeAt(1) == "1,500" ? "" : "изчистването хвана и съседа");

                // Ctrl+D повтаря числото отгоре.
                partial.UseTake(0, 2m);
                partial.PickCells(1);
                partial.RepeatAbove();

                partNotes.Add(partial.TakeAt(1) == "2,000"
                    ? "" : "Ctrl+D не повтаря числото отгоре: " + partial.TakeAt(1));

                // Копираното са само числата, без заглавия.
                partial.PickCells(0, 1);

                var copied = partial.Copied().Trim();

                partNotes.Add(copied.Contains("2,000", StringComparison.Ordinal)
                    && !copied.Contains("За третиране", StringComparison.Ordinal)
                    ? "" : "копираното не са само числата: " + copied.Replace(Environment.NewLine, " / "));

                // Връщането в големия прозорец.
                dialog.ApplyPartial(scope, partial.Takes);
            }

            var planned = dialog.Planned();

            partNotes.Add(planned.Count == 1 && planned[0].Draws.Count == 2
                ? "" : $"върнатото дава {planned.Count} движения с {(planned.Count > 0 ? planned[0].Draws.Count : 0)} дяла");

            // След разливането и Ctrl+D по двата реда стоят по два тона.
            partNotes.Add(planned.Count == 1 && planned[0].Amount == 4m
                ? "" : $"третира се {(planned.Count > 0 ? planned[0].Amount : 0)} т вместо четири");

            partNotes.Add(dialog.TakeAt(0) == "4,000"
                ? "" : "горният ред не следва частичното: " + dialog.TakeAt(0));

            // Празно количество изважда партидата от третирането.
            dialog.ApplyPartial(scope, new Dictionary<long, decimal>());

            partNotes.Add(dialog.Planned().Count == 0
                ? "" : "празното частично остави количества");
        }
        catch (Exception ex)
        {
            partError = ex.ToString();
        }
    });

    partThread.SetApartmentState(ApartmentState.STA);
    partThread.Start();
    partThread.Join();

    if (partError is not null) Console.WriteLine("     " + partError);

    var partProblems = partNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in partProblems) Console.WriteLine("     " + problem);

    Check("частичното третиране минава без изключение", partError is null);
    Check($"направени са всичките {partNotes.Count} проверки", partNotes.Count == 23, $"{partNotes.Count}");
    Check("частичното третиране връща посочените количества", partProblems.Count == 0,
        $"{partProblems.Count} проблема");
}


// =============================================== Кантар, подредба и пресяване в частичното
Console.WriteLine();
Console.WriteLine("### ТЕСТ - частичното предаване носи кантара, подрежда и пресява");
{
    var scaleNotes = new List<string>();
    string? scaleError = null;

    var scaleThread = new Thread(() =>
    {
        try
        {
            var scalePath = Path.Combine(work, $"scale_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(scalePath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");

            foreach (var (day, amount, eik, name) in new (int, decimal, string, string)[]
            {
                (3, 10m, "204455667", "Скрап ЕООД"),
                (4, 6m, "205566778", "Метал груп ЕООД"),
                (5, 4m, "206677889", "Вторични ЕООД"),
                (6, 8m, "204455667", "Скрап ЕООД"),
                (7, 2m, "205566778", "Метал груп ЕООД")
            })
                db.Writer.Write(companyId, new MoveRequest(
                    MoveKind.Received, new DateOnly(2026, 9, day), "150101", amount,
                    Eik: eik, Partner: name));

            var shares = StockMath.Shares(
                db.Batches.List(companyId, "1"), db.Ledger.Read(companyId, "1"),
                db.Ledger.Links(companyId, "1"), StoreKind.Received, new DateOnly(2026, 9, 30));

            var none = shares.ToDictionary(s => s.Batch.Id, _ => 0m);

            // Кантарът: заявено 20 от 30 налични.
            using (var partial = new PartialDialog(shares, none, MoveKind.Submitted, 20m))
            {
                _ = partial.Handle;

                scaleNotes.Add(partial.Target == 20m ? "" : $"кантарът тръгва с {partial.Target}");
                scaleNotes.Add(partial.GapText.Contains("не достигат 20,000", StringComparison.Ordinal)
                    ? "" : "празното не казва недостига: " + partial.GapText);

                partial.UseAll(whole: true);
                scaleNotes.Add(partial.GapText.Contains("10,000 т в повече", StringComparison.Ordinal)
                    ? "" : "цялото не казва излишъка: " + partial.GapText);

                // „Изравни“ сваля десетте от реда, на който се стои.
                partial.UseLevel(0);
                scaleNotes.Add(partial.Takes.Values.Sum() == 20m && partial.TakeAt(0).Length == 0
                    ? "" : $"изравняването даде {partial.Takes.Values.Sum()} т, първият ред {partial.TakeAt(0)}");
                scaleNotes.Add(partial.GapText.Contains("точно заявеното", StringComparison.Ordinal)
                    ? "" : "след изравняването не съвпада: " + partial.GapText);

                // „Пропорционално“ без нищо взето — по наличното.
                partial.UseAll(whole: false);
                partial.UseProportional();
                scaleNotes.Add(partial.Takes.Values.Sum() == 20m && partial.Takes.Count == 5
                    ? "" : $"пропорционалното даде {partial.Takes.Values.Sum()} т в {partial.Takes.Count} реда");

                // Подредба по наличното — по числото, не по текста.
                partial.SortForTest("__stock", down: true);
                scaleNotes.Add(partial.StockAt(0) == 10m && partial.StockAt(4) == 2m
                    ? "" : $"подредбата по налично: {partial.StockAt(0)} … {partial.StockAt(4)}");

                // Пресяване по клиент: „Всичко“ пипа само видимото.
                partial.UseAll(whole: false);
                partial.FilterForTest("__partner", ["Скрап ЕООД — 204455667"]);
                scaleNotes.Add(partial.VisibleCount == 2 ? "" : $"пресяването показва {partial.VisibleCount} реда");

                partial.UseAll(whole: true);
                scaleNotes.Add(partial.Takes.Values.Sum() == 18m
                    ? "" : $"„Всичко“ при пресяване взе {partial.Takes.Values.Sum()} т вместо 18");

                partial.FilterForTest("__partner", null);
                scaleNotes.Add(partial.VisibleCount == 5 ? "" : "махнатото пресяване не върна редовете");
            }

            // Взетото извън списъка се брои към кантара.
            using (var partial = new PartialDialog(shares, none, MoveKind.Submitted, 20m, outside: 5m))
            {
                partial.UseProportional();
                scaleNotes.Add(partial.Takes.Values.Sum() == 15m
                    ? "" : $"с 5 т отвън се разнесоха {partial.Takes.Values.Sum()} т вместо 15");
            }

            // Третирането няма кантар.
            using (var partial = new PartialDialog(shares, none))
                scaleNotes.Add(partial.GapText.Length == 0 ? "" : "третирането показва кантар");

            // Пресяването в голямото прозорче: скритото не излиза от избора.
            using var dialog = new TreatDialog(
                service, db, companyId, "1", shares, new DateOnly(2026, 9, 30));

            _ = dialog.Handle;
            dialog.PickPile(0);

            dialog.FilterBatchesByClient("Скрап");
            scaleNotes.Add(dialog.VisibleBatches == 2 ? "" : $"видими {dialog.VisibleBatches} партиди вместо две");

            // Първата (Скрап, 10 т) остава, втората видима (Скрап, 8 т) излиза,
            // скритите три си остават взети.
            dialog.PickBatches(0);

            var planned = dialog.Planned().Sum(m => m.Amount);
            scaleNotes.Add(planned == 22m ? "" : $"третира се {planned} т вместо 22");

            dialog.FilterBatchesByClient(null);
            scaleNotes.Add(dialog.VisibleBatches == 5 ? "" : "махнатото пресяване не върна партидите");
        }
        catch (Exception ex)
        {
            scaleError = ex.ToString();
        }
    });

    scaleThread.SetApartmentState(ApartmentState.STA);
    scaleThread.Start();
    scaleThread.Join();

    if (scaleError is not null) Console.WriteLine("     " + scaleError);

    var scaleProblems = scaleNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in scaleProblems) Console.WriteLine("     " + problem);

    Check("кантарът и пресяването минават без изключение", scaleError is null);
    Check($"направени са всичките {scaleNotes.Count} проверки", scaleNotes.Count == 15, $"{scaleNotes.Count}");
    Check("кантарът изравнява, а пресяването не губи избора", scaleProblems.Count == 0,
        $"{scaleProblems.Count} проблема");
}


// =============================================== Сборове в третирането
Console.WriteLine();
Console.WriteLine("### ТЕСТ - третирането казва какво ще влезе и какво е маркирано");
{
    var markNotes = new List<string>();
    string? markError = null;

    var markThread = new Thread(() =>
    {
        try
        {
            var markPath = Path.Combine(work, $"marked_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(markPath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");

            foreach (var (day, code, amount) in new (int, string, decimal)[]
            {
                (3, "150101", 10m), (4, "150101", 6m), (5, "150101", 4m), (6, "200101", 40m)
            })
                db.Writer.Write(companyId, new MoveRequest(
                    MoveKind.Received, new DateOnly(2026, 9, day), code, amount,
                    Eik: "204455667", Partner: "Скрап ЕООД"));

            var moves = db.Ledger.Read(companyId, "1");
            var links = db.Ledger.Links(companyId, "1");
            var batches = db.Batches.List(companyId, "1");

            var shares = StockMath.Shares(batches, moves, links, StoreKind.Received,
                new DateOnly(2026, 9, 30));

            using var dialog = new TreatDialog(
                service, db, companyId, "1", shares, new DateOnly(2026, 9, 30));

            // Общият сбор казва с думи кое какво е.
            markNotes.Add(dialog.TotalsText.Contains("За третиране: 60,000", StringComparison.Ordinal)
                ? "" : "сборът не казва какво ще влезе: " + dialog.TotalsText);

            markNotes.Add(dialog.TotalsText.Contains("налично: 60,000", StringComparison.Ordinal)
                ? "" : "сборът не казва наличното: " + dialog.TotalsText);

            markNotes.Add(dialog.TotalsText.Contains("кодове: 2 от 2", StringComparison.Ordinal)
                ? "" : "сборът не брои кодовете: " + dialog.TotalsText);

            // Същото стои и в заглавието на колоната.
            markNotes.Add(dialog.TakeHeaderText.Contains("60,000", StringComparison.Ordinal)
                ? "" : "колоната горе няма сбор: " + dialog.TakeHeaderText.Replace(Environment.NewLine, " / "));

            // Маркираното се брои отделно: един код от двата.
            dialog.PickPile(0);

            markNotes.Add(dialog.PickedText.Contains("1 кода", StringComparison.Ordinal)
                ? "" : "не се брои маркираното горе: " + dialog.PickedText);

            markNotes.Add(dialog.PickedText.Contains("20,000", StringComparison.Ordinal)
                ? "" : "маркираното горе не носи сбора си: " + dialog.PickedText);

            // Долу: партидите на този код.
            markNotes.Add(dialog.BatchTakeHeaderText.Contains("20,000", StringComparison.Ordinal)
                ? "" : "колоната долу няма сбор: " + dialog.BatchTakeHeaderText.Replace(Environment.NewLine, " / "));

            dialog.PickBatches(0, 1);

            markNotes.Add(dialog.PickedText.Contains("2 партиди", StringComparison.Ordinal)
                ? "" : "не се брои маркираното долу: " + dialog.PickedText);

            markNotes.Add(dialog.PickedText.Contains("16,000", StringComparison.Ordinal)
                ? "" : "маркираните партиди не носят сбора си: " + dialog.PickedText);

            markNotes.Add(dialog.BatchTakeHeaderText.Contains("16,000", StringComparison.Ordinal)
                ? "" : "колоната долу не следва избора: " +
                       dialog.BatchTakeHeaderText.Replace(Environment.NewLine, " / "));

            // И общият сбор следва избора: щракването по един код изважда
            // другия, а от този се взимат две партиди.
            markNotes.Add(dialog.TotalsText.Contains("За третиране: 16,000", StringComparison.Ordinal)
                ? "" : "общият сбор не следва избора: " + dialog.TotalsText);

            dialog.PickBatches();

            markNotes.Add(dialog.PickedText.Contains("Нищо", StringComparison.Ordinal)
                ? "" : "без маркирано пак се брои нещо: " + dialog.PickedText);
        }
        catch (Exception ex)
        {
            markError = ex.ToString();
        }
    });

    markThread.SetApartmentState(ApartmentState.STA);
    markThread.Start();
    markThread.Join();

    if (markError is not null) Console.WriteLine("     " + markError);

    var markProblems = markNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in markProblems) Console.WriteLine("     " + problem);

    Check("сборовете в третирането минават без изключение", markError is null);
    Check($"направени са всичките {markNotes.Count} проверки", markNotes.Count == 12, $"{markNotes.Count}");
    Check("третирането казва какво ще влезе и какво е маркирано", markProblems.Count == 0,
        $"{markProblems.Count} проблема");
}


// =============================================== Складът: образуване от склада, сметката
Console.WriteLine();
Console.WriteLine("### ТЕСТ - образуването от склада се смята без екран");
{
    var genNotes = new List<string>();

    var metal = new Batch(1, "2026-0001", "203344556", "Метал ООД", "160216", new DateOnly(2026, 9, 3));
    var skrap = new Batch(2, "2026-0002", "204455667", "Скрап ЕООД", "160216", new DateOnly(2026, 9, 10));
    var oil = new Batch(3, "2026-0003", "204455667", "Скрап ЕООД", "130205", new DateOnly(2026, 9, 11));

    // Чакащото в склад „Третиран“: кодът е онзи, с който количеството е ИЗЛЯЗЛО
    // от третирането, а партидата помни с какво е дошло.
    var waiting = new List<StockShare>
    {
        new("160216", skrap, 8m),
        new("160216", metal, 12m),
        new("130205", oil, 5m)
    };

    var piles = Generation.Piles(waiting);

    genNotes.Add(piles.Count == 2 ? "" : $"купчините са {piles.Count} вместо две");
    genNotes.Add(piles[1].Ewc == "160216" ? "" : "купчините не са подредени по код: " + piles[1].Ewc);
    genNotes.Add(piles[1].Available == 20m ? "" : $"чакащото по кода е {piles[1].Available} вместо 20");

    var cable = piles[1];
    var picks = Treatment.Spread(cable, 20m);

    genNotes.Add(picks.Count == 2 ? "" : $"изчерпването пипа {picks.Count} партиди вместо две");

    // Рецептата: едно към няколко, в проценти. Остатъкът от закръглянето пада
    // върху последния дял, за да е сборът точно колкото е влязло.
    var recipe = Generation.Recipe(20m,
        [("191202", 70m), ("191204", 25m), ("191212", 5m)]);

    genNotes.Add(recipe.Count == 3 ? "" : $"рецептата дава {recipe.Count} дяла вместо три");
    genNotes.Add(recipe.Sum(p => p.Amount) == 20m
        ? "" : $"дяловете събират {recipe.Sum(p => p.Amount)} вместо 20");
    genNotes.Add(recipe[0].Amount == 14m ? "" : $"първият дял е {recipe[0].Amount} вместо 14");
    genNotes.Add(recipe[2].Amount == 1m ? "" : $"остатъкът е {recipe[2].Amount} вместо 1");

    // Трите равни дяла: сборът остава точен, разликата стои на едно място.
    var thirds = Generation.Recipe(50m, [("191202", 33.33m), ("191204", 33.33m), ("191212", 33.34m)]);

    genNotes.Add(thirds.Sum(p => p.Amount) == 50m
        ? "" : $"три дяла от 50 събират {thirds.Sum(p => p.Amount)}");

    // Шаблонът пази ДЯЛОВЕ, не тонове: това е, което го прави годен за друго
    // количество следващия месец.
    var ratio = Generation.Ratio([new GenPart("191202", 15m), new GenPart("191204", 5m)]);

    genNotes.Add(ratio.Count == 2 ? "" : $"съотношението е от {ratio.Count} дяла вместо два");
    genNotes.Add(ratio.Count == 2 && ratio[0].Percent == 75m && ratio[1].Percent == 25m
        ? "" : "тоновете не стават проценти");
    genNotes.Add(ratio.Sum(r => r.Percent) == 100m
        ? "" : $"дяловете събират {ratio.Sum(r => r.Percent)} вместо сто");

    // Същият шаблон върху друго количество: дяловете важат, тоновете следват.
    var onSeven = Generation.Recipe(7m, ratio);

    genNotes.Add(onSeven.Sum(p => p.Amount) == 7m
        ? "" : $"върху 7 т шаблонът дава {onSeven.Sum(p => p.Amount)}");
    genNotes.Add(onSeven[0].Amount == 5.25m && onSeven[1].Amount == 1.75m
        ? "" : $"75 % от 7 излиза {onSeven[0].Amount} вместо 5,25");

    // Съотношение, което не прави точно сто, се чете като съотношение — и пак
    // се изравнява. Делено на сто, 70:20 щеше да стане 14 и 6 вместо 3,5 пъти.
    var odd = Generation.Recipe(20m, [("191202", 70m), ("191204", 20m)]);

    genNotes.Add(odd.Sum(p => p.Amount) == 20m
        ? "" : $"непълното съотношение дава {odd.Sum(p => p.Amount)} вместо 20");
    genNotes.Add(odd[0].Amount == 15.556m && odd[1].Amount == 4.444m
        ? "" : $"70:20 не се раздели като 70 към 90: {odd[0].Amount} и {odd[1].Amount}");

    // Закръглянето не пада върху ред, който не участва: няколко грама в празен
    // ред го превръщат в „количество без код“ и спират записа.
    var blankLast = Generation.Recipe(10m, [("191202", 33.33m), ("191204", 66.67m), ("", 0m)]);

    genNotes.Add(blankLast.Sum(p => p.Amount) == 10m
        ? "" : $"с празен ред накрая сборът е {blankLast.Sum(p => p.Amount)}");
    genNotes.Add(blankLast[2].Amount == 0m
        ? "" : $"празният ред получи {blankLast[2].Amount} т");

    // Три равни дяла, минали през съотношението: сборът пак е точен.
    var equal = Generation.Ratio(
        [new GenPart("191202", 10m), new GenPart("191204", 10m), new GenPart("191212", 10m)]);

    genNotes.Add(equal.Sum(r => r.Percent) == 100m
        ? "" : $"три равни дяла събират {equal.Sum(r => r.Percent)} процента");
    genNotes.Add(Generation.Recipe(50m, equal).Sum(p => p.Amount) == 50m
        ? "" : "три равни дяла не се връщат точно в количеството");

    // В шаблона не влизат редове без код или без количество.
    var partial = Generation.Ratio(
        [new GenPart("191202", 10m), new GenPart(string.Empty, 5m), new GenPart("191204", 0m)]);

    genNotes.Add(partial.Count == 1 ? "" : $"в шаблона влязоха {partial.Count} дяла вместо един");
    genNotes.Add(Generation.Ratio([]).Count == 0 ? "" : "празна рецепта прави шаблон");

    // Смяна на количеството: съотношението е решението, тоновете са следствие.
    var smaller = Generation.Rescale(recipe, 10m);

    genNotes.Add(smaller.Sum(p => p.Amount) == 10m
        ? "" : $"пресметнатото наново събира {smaller.Sum(p => p.Amount)} вместо 10");
    genNotes.Add(smaller[0].Amount == 7m ? "" : $"70 % от 10 излиза {smaller[0].Amount} вместо 7");
    genNotes.Add(smaller[0].Ewc == "191202" ? "" : "кодовете не преживяват пресмятането");

    var order = new GenOrder(cable, picks, recipe);

    genNotes.Add(order.Consumed == 20m ? "" : $"изчерпва се {order.Consumed} вместо 20");
    genNotes.Add(order.Produced == 20m ? "" : $"образува се {order.Produced} вместо 20");
    genNotes.Add(order.Left == 0m ? "" : $"остават {order.Left} т неразпределени");

    // Всеки изходен код става ред, а произходът се наследява пропорционално.
    var made = Generation.Plan([order]);

    genNotes.Add(made.Count == 3 ? "" : $"образуването дава {made.Count} реда вместо три");
    genNotes.Add(made[0].Ewc == "191202" ? "" : "редовете не са подредени по код: " + made[0].Ewc);
    genNotes.Add(made[0].Amount == 14m ? "" : $"по първия код излизат {made[0].Amount} вместо 14");
    genNotes.Add(made[0].Draws.Count == 2
        ? "" : $"произходът е от {made[0].Draws.Count} партиди вместо две");
    genNotes.Add(made[0].Draws.Sum(d => d.Amount) == 14m ? "" : "дяловете не събират количеството");
    genNotes.Add(made[0].Draws.Any(d => d.BatchId == metal.Id && d.Amount == 8.4m)
        ? "" : "произходът не следва съотношението на третираното");
    genNotes.Add(made.All(m => m.Source == "160216") ? "" : "не се помни от кой код е излязло");
    genNotes.Add(made.Sum(m => m.Amount) == 20m ? "" : "образуваното не събира изчерпаното");

    // Две купчини в един и същи код: по подразбиране един ред, с флага — два.
    var second = new GenOrder(
        piles[0], Treatment.Spread(piles[0], 5m), [new GenPart("191202", 5m)]);

    var merged = Generation.Plan([order, second]);

    genNotes.Add(merged.Count == 3 ? "" : $"обединяването дава {merged.Count} реда вместо три");
    genNotes.Add(merged[0].Ewc == "191202" && merged[0].Amount == 19m
        ? "" : $"еднаквите кодове не се събират: {merged[0].Amount}");
    genNotes.Add(merged[0].Draws.Count == 3
        ? "" : $"слетият ред сочи {merged[0].Draws.Count} партиди вместо три");
    genNotes.Add(merged[0].Source.Length == 0
        ? "" : "слят от две купчини ред се прави, че е от една");

    var apart = Generation.Plan([order, second], perPile: true);

    genNotes.Add(apart.Count == 4 ? "" : $"по купчини дава {apart.Count} реда вместо четири");
    genNotes.Add(apart.Sum(m => m.Amount) == merged.Sum(m => m.Amount)
        ? "" : "разделянето мени общото количество");

    // Купчина без изчерпано количество не е грешка — тя просто не участва.
    var idle = new GenOrder(piles[0], [], []);

    genNotes.Add(Generation.Check([order, idle]).Count == 0
        ? "" : "празна купчина до непразна се брои за грешка");
    genNotes.Add(Generation.Plan([order, idle]).Count == 3 ? "" : "празната купчина ражда ред");

    // Разминаването между изчерпано и обявено е самата грешка на този прозорец.
    var under = new GenOrder(cable, picks, [new GenPart("191202", 12m)]);
    var mismatch = Generation.Check([under]);

    genNotes.Add(mismatch.Any(i => i.Kind == GenProblem.Mismatch)
        ? "" : "разминаването между изчерпано и образувано минава");
    genNotes.Add(mismatch.Count > 0 && mismatch[0].Text.Contains("8,000", StringComparison.Ordinal)
        ? "" : "съобщението не казва колко остават: " + (mismatch.Count > 0 ? mismatch[0].Text : "няма"));

    var over = Generation.Check(
        [new GenOrder(cable, [new TreatPick(waiting[1], 30m)], [new GenPart("191202", 30m)])]);

    genNotes.Add(over.Any(i => i.Kind == GenProblem.OverStock) ? "" : "искане над чакащото минава");
    genNotes.Add(over.Count > 0 && over[0].Text.Contains("20,000", StringComparison.Ordinal)
        ? "" : "съобщението не казва колко чака: " + (over.Count > 0 ? over[0].Text : "няма"));

    var blank = Generation.Check([new GenOrder(cable, picks, [new GenPart(string.Empty, 20m)])]);

    genNotes.Add(blank.Any(i => i.Kind == GenProblem.NoCode) ? "" : "ред с количество, но без код минава");
    genNotes.Add(Generation.Plan([new GenOrder(cable, picks, [new GenPart(string.Empty, 20m)])]).Count == 0
        ? "" : "ред без код става движение");

    var negative = Generation.Check(
        [new GenOrder(cable, [new TreatPick(waiting[1], -1m)], [new GenPart("191202", 1m)])]);

    genNotes.Add(negative.Any(i => i.Kind == GenProblem.Negative) ? "" : "отрицателно количество минава");

    var nothing = Generation.Check([idle]);

    genNotes.Add(nothing.Count == 1 && nothing[0].Kind == GenProblem.Nothing
        ? "" : "празно образуване минава");

    var genProblems = genNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in genProblems) Console.WriteLine("     " + problem);

    Check($"направени са всичките {genNotes.Count} проверки", genNotes.Count == 52, $"{genNotes.Count}");
    Check("образуването от склада се смята вярно", genProblems.Count == 0, $"{genProblems.Count} проблема");
}

// =============================================== Складът: образуване от екрана
Console.WriteLine();
Console.WriteLine("### ТЕСТ - от склад „Третиран“ се образува едно към няколко кода");
{
    var formNotes = new List<string>();
    string? formError = null;

    var formThread = new Thread(() =>
    {
        try
        {
            var formPath = Path.Combine(work, $"generate_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(formPath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 1), "160216", 12m,
                Eik: "203344556", Partner: "Метал ООД"));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 2), "160216", 8m,
                Eik: "204455667", Partner: "Скрап ЕООД"));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Treated, new DateOnly(2026, 9, 10), "160216", 20m));

            using var page = new StockPage(service, db);

            page.UseDate(new DateOnly(2026, 9, 30));
            page.UseStore(StoreKind.Treated);

            formNotes.Add(page.RowCount == 1 ? "" : $"„Третиран“ показва {page.RowCount} реда вместо един");
            formNotes.Add(page.GenerateOffered ? "" : "образуването не се предлага от „Третиран“");
            formNotes.Add(!page.TreatOffered ? "" : "третирането се предлага и от „Третиран“");
            formNotes.Add(page.RowActionShown ? "" : "копчето на реда го няма в „Третиран“");
            formNotes.Add(page.RowActionText == "Образувай"
                ? "" : "копчето на реда не стана за образуване: " + page.RowActionText);

            page.SelectMaster(0);
            var chosen = page.Selection();

            formNotes.Add(chosen.Count == 2 ? "" : $"зад кода стоят {chosen.Count} дяла вместо два");

            using (var dialog = new GenerateDialog(
                       service, db, companyId, "1", chosen, new DateOnly(2026, 9, 30)))
            {
                formNotes.Add(dialog.RowCount == 1 ? "" : $"прозорчето показва {dialog.RowCount} реда");
                formNotes.Add(dialog.StockAt(0) == "20,000" ? "" : "чакащото е " + dialog.StockAt(0));
                formNotes.Add(dialog.TakeAt(0) == "20,000" ? "" : "не се предлага цялата купчина");
                formNotes.Add(dialog.CanGoFullScreen ? "" : "прозорчето не се разтваря на цял екран");
                formNotes.Add(dialog.SaveReachable ? "" : "копчето „Образувай“ не се побира в прозореца");
                // Размерът се задава ТУК: несглобеното прозорче има ширините,
                // с които WinForms го е направил, а лентата се мери спрямо
                // рамката, която ще получи при отваряне.
                dialog.ClientSize = new Size(1480, 900);

                formNotes.Add(dialog.ToolsFit ? "" : "лентата с шаблоните е отрязана отдясно");

                dialog.PickPile(0);

                // Без рецепта образуването е едно към едно: същият код, цялото
                // количество — най-честият случай и нула щраквания.
                formNotes.Add(dialog.PartCount == 1
                    ? "" : $"рецептата тръгва с {dialog.PartCount} реда вместо един");
                formNotes.Add(dialog.PartCodeAt(0) == "160216"
                    ? "" : "едно към едно не предлага същия код: " + dialog.PartCodeAt(0));
                formNotes.Add(dialog.PartAmountAt(0) == "20,000"
                    ? "" : "едно към едно не взема цялото: " + dialog.PartAmountAt(0));
                formNotes.Add(dialog.PartPercentAt(0) == "100"
                    ? "" : "процентът не е сто: " + dialog.PartPercentAt(0));
                formNotes.Add(dialog.PartNameAt(0).Length > 0 ? "" : "описанието на кода не се изписва");

                formNotes.Add(dialog.OriginCount == 2
                    ? "" : $"произходът показва {dialog.OriginCount} реда вместо два");
                formNotes.Add(dialog.BehindText.Contains("2 клиента", StringComparison.Ordinal)
                    ? "" : "не се казва от колко клиента е: " + dialog.BehindText);

                // Датата стои на своя ивица и казва в кой МЕСЕЦ влиза записът:
                // сгрешената дата почти винаги е сгрешен месец.
                formNotes.Add(GenerateDialog.DateCaption == "Дата на образуване"
                    ? "" : "датата не е озаглавена: " + GenerateDialog.DateCaption);
                formNotes.Add(dialog.WhenText.Contains("септември 2026", StringComparison.Ordinal)
                    ? "" : "не се казва в кой месец влиза записът: " + dialog.WhenText);
                formNotes.Add(dialog.DateFits ? "" : "датата не се побира в дясната рамка");

                dialog.UseDate(new DateOnly(2026, 10, 5));
                formNotes.Add(dialog.WhenText.Contains("октомври 2026", StringComparison.Ordinal)
                    ? "" : "смяната на датата не сменя месеца: " + dialog.WhenText);

                // Затворен месец се казва още при датата, а не чак при записа.
                db.Periods.Close(companyId, "1", new LedgerPeriod(2026, 10));
                dialog.UseDate(new DateOnly(2026, 10, 6));

                formNotes.Add(dialog.WhenText.Contains("затворен", StringComparison.Ordinal)
                    ? "" : "затвореният месец мълчи при датата: " + dialog.WhenText);

                db.Periods.Reopen(companyId, "1", new LedgerPeriod(2026, 10));
                dialog.UseDate(new DateOnly(2026, 9, 30));

                formNotes.Add(dialog.PartCountBox == 1
                    ? "" : $"„Брой“ тръгва с {dialog.PartCountBox} вместо с един");

                // Едно към няколко: кабелът се разпада на мед и пластмаса.
                // „Брой“ казва на колко — както в прозорчето „Дели“ в отчета.
                dialog.UsePartCount(2);
                formNotes.Add(dialog.PartCount == 2 ? "" : "вторият ред не се добавя");

                // „+“ и „−“ мърдат броя с един: стрелките на кутийката са две
                // точки на екрана и се уцелват трудно.
                dialog.StepParts(1);
                formNotes.Add(dialog.PartCount == 3 && dialog.PartCountBox == 3
                    ? "" : $"„+“ не добавя ред: {dialog.PartCount}");

                dialog.StepParts(-1);
                formNotes.Add(dialog.PartCount == 2 && dialog.PartCountBox == 2
                    ? "" : $"„−“ не маха ред: {dialog.PartCount}");

                // Написаното преживява смяната на броя: редовете не се правят
                // наново, а само се догражда или маха последният.
                dialog.UsePartCode(0, "191202");
                dialog.StepParts(1);
                formNotes.Add(dialog.PartCodeAt(0) == "191202"
                    ? "" : "смяната на броя изтри написаното: " + dialog.PartCodeAt(0));
                dialog.StepParts(-1);

                dialog.UsePartCode(1, "191204");
                dialog.UsePartAmount(0, 15m);

                formNotes.Add(dialog.PartNameAt(1).Length > 0
                    ? "" : "менюто не показва името на избрания код");

                formNotes.Add(dialog.Blocking().Any(p => p.Contains("остават", StringComparison.Ordinal))
                    ? "" : "неразпределеният остатък не спира записа");

                dialog.UseBalance();

                formNotes.Add(dialog.PartAmountAt(1) == "5,000"
                    ? "" : "„Изравни“ не слага остатъка в последния ред: " + dialog.PartAmountAt(1));
                formNotes.Add(dialog.PartPercentAt(1) == "25"
                    ? "" : "процентът не следва тоновете: " + dialog.PartPercentAt(1));
                formNotes.Add(dialog.Blocking().Count == 0
                    ? "" : "изравненото пак спира записа: " + string.Join(" | ", dialog.Blocking()));

                // Сборовете стоят под имената на колоните — както навсякъде
                // другаде в програмата.
                formNotes.Add(dialog.PartAmountTotalText.Contains("20,000", StringComparison.Ordinal)
                    ? "" : "сборът на тоновете липсва под заглавието: " + dialog.PartAmountTotalText);
                formNotes.Add(dialog.PartAmountTotalText.Contains("Тона", StringComparison.Ordinal)
                    ? "" : "сборът измести името на колоната: " + dialog.PartAmountTotalText);
                formNotes.Add(dialog.PartPercentTotalText.Contains("100", StringComparison.Ordinal)
                    ? "" : "сборът на процентите липсва: " + dialog.PartPercentTotalText);
                formNotes.Add(dialog.PartCodeTotalText.Contains("2 кода", StringComparison.Ordinal)
                    ? "" : "не се казва колко кода са набрани: " + dialog.PartCodeTotalText);

                // Сборът следва написаното още докато се пише.
                dialog.UsePartAmount(1, 3m);
                formNotes.Add(dialog.PartAmountTotalText.Contains("18,000", StringComparison.Ordinal)
                    ? "" : "сборът не следва написаното: " + dialog.PartAmountTotalText);
                dialog.UseBalance();

                var planned = dialog.Planned();

                formNotes.Add(planned.Count == 2 ? "" : $"сглобени са {planned.Count} реда вместо два");
                formNotes.Add(planned[0].Ewc == "191202" && planned[0].Amount == 15m
                    ? "" : "първият изходен код не носи количеството си");
                formNotes.Add(planned[1].Ewc == "191204" && planned[1].Amount == 5m
                    ? "" : "вторият изходен код не носи количеството си");
                formNotes.Add(planned.Sum(m => m.Amount) == 20m
                    ? "" : "образуваното не събира изчерпаното");
                formNotes.Add(planned[0].Draws.Count == 2
                    ? "" : "произходът не се наследява по двете партиди");
                formNotes.Add(planned[0].Draws.Sum(d => d.Amount) == 15m
                    ? "" : "дяловете не събират количеството на реда");
                formNotes.Add(dialog.OutAt(0).Contains("191202", StringComparison.Ordinal)
                    ? "" : "редът горе не казва в какво излиза: " + dialog.OutAt(0));
                formNotes.Add(dialog.SplitText.Contains("2 реда", StringComparison.Ordinal)
                    ? "" : "не се казва колко реда ще станат: " + dialog.SplitText);

                // Процентът пише тоновете също толкова добре, колкото и обратно.
                dialog.UsePartPercent(1, 10m);
                formNotes.Add(dialog.PartAmountAt(1) == "2,000"
                    ? "" : "процентът не пресмята тоновете: " + dialog.PartAmountAt(1));
                dialog.UseBalance();

                // Шаблонът е същият, който пази прозорчето „Дели“ в отчета.
                formNotes.Add(dialog.KeepTemplate("Стар кабел") ? "" : "рецептата не се записва като шаблон");

                dialog.UsePartAmount(1, 0m);
                dialog.UsePartAmount(0, 20m);

                formNotes.Add(dialog.UseTemplate("Стар кабел") ? "" : "записаният шаблон го няма в менюто");

                // Шаблонът се пази там, откъдето го чете и прозорчето „Дели“ в
                // отчета: ключът е името на възела от схемата и в двата случая.
                var shared = SplitTemplates.Load(db.Settings, Moves.Node(MoveKind.Generated))
                    .FirstOrDefault(t => t.Name == "Стар кабел");

                formNotes.Add(shared is not null
                    ? "" : "шаблонът не стига до общото място, което чете и отчетът");
                formNotes.Add(shared?.Parts.Count == 2
                    ? "" : $"шаблонът пази {shared?.Parts.Count} дяла вместо два");
                formNotes.Add(shared is not null && shared.Parts[0].Ewc == "191202" &&
                              shared.Parts[0].Percent == 75m
                    ? "" : "шаблонът не пази дяловете в проценти");
                formNotes.Add(shared?.Total == 100m
                    ? "" : $"дяловете на шаблона събират {shared?.Total} вместо сто");
                formNotes.Add(dialog.PartAmountAt(0) == "15,000"
                    ? "" : "шаблонът не връща дяловете: " + dialog.PartAmountAt(0));

                // Шаблон върху ДРУГО количество: дяловете важат, тоновете
                // следват — и сборът пада точно върху изчерпаното.
                dialog.UseTake(0, 7m);
                dialog.UseTemplate("Стар кабел");

                formNotes.Add(dialog.PartAmountAt(0) == "5,250" && dialog.PartAmountAt(1) == "1,750"
                    ? "" : $"шаблонът върху 7 т дава {dialog.PartAmountAt(0)} и {dialog.PartAmountAt(1)}");
                formNotes.Add(dialog.PartAmountTotalText.Contains("7,000", StringComparison.Ordinal)
                    ? "" : "сборът не пада точно върху изчерпаното: " + dialog.PartAmountTotalText);
                formNotes.Add(dialog.Blocking().Count == 0
                    ? "" : "шаблон върху друго количество оставя остатък: "
                           + string.Join(" | ", dialog.Blocking()));

                // По-малко изчерпано значи същите дялове, не същите числа.
                dialog.UseTake(0, 10m);

                formNotes.Add(dialog.PartAmountAt(0) == "7,500"
                    ? "" : "дяловете не следват количеството: " + dialog.PartAmountAt(0));
                formNotes.Add(dialog.PartPercentAt(0) == "75"
                    ? "" : "процентът се променя със количеството: " + dialog.PartPercentAt(0));

                // Шаблонът стои при купчината: менюто го показва, а при нула
                // тона дяловете не се губят — върнатото количество се дели пак
                // по тях, а не пада цялото в първия ред.
                formNotes.Add(dialog.TemplateText == "Стар кабел"
                    ? "" : "менюто не показва шаблона на купчината: " + dialog.TemplateText);

                dialog.UseTake(0, 0m);
                dialog.UseTake(0, 8m);

                formNotes.Add(dialog.PartAmountAt(0) == "6,000" && dialog.PartAmountAt(1) == "2,000"
                    ? "" : $"след нула шаблонът върху 8 т дава {dialog.PartAmountAt(0)} и {dialog.PartAmountAt(1)}");
                formNotes.Add(dialog.PartPercentAt(0) == "75" && dialog.PartPercentAt(1) == "25"
                    ? "" : $"след нула процентите са {dialog.PartPercentAt(0)} и {dialog.PartPercentAt(1)}");

                // Написаното на ръка, което не е шаблонът, го откача от менюто.
                dialog.UsePartPercent(1, 30m);

                formNotes.Add(dialog.TemplateText != "Стар кабел"
                    ? "" : "менюто още твърди шаблон над дялове, писани на ръка");

                dialog.UseTemplate("Стар кабел");

                dialog.UseTake(0, 20m);

                // Записът минава по пътя на копчето.
                var written = page.ApplyGenerated(
                    dialog.Planned(), new DateOnly(2026, 9, 12),
                    new Dictionary<string, string>(StringComparer.Ordinal));

                formNotes.Add(written == 2 ? "" : $"записани са {written} движения вместо две");
            }

            var generated = db.Ledger.Read(companyId, "1")
                .Where(m => m.Kind == MoveKind.Generated)
                .OrderBy(m => m.Ewc, StringComparer.Ordinal)
                .ToList();

            formNotes.Add(generated.Count == 2 ? "" : $"в книгата стоят {generated.Count} образувания");
            formNotes.Add(generated.Count == 2 && generated[0].Ewc == "191202" && generated[0].Amount == 15m
                ? "" : "първото образуване не е каквото беше сглобено");

            var copper = generated.FirstOrDefault(m => m.Ewc == "191202");
            var draws = copper is null ? [] : db.Ledger.Draws(copper.Id);

            formNotes.Add(draws.Count == 2 ? "" : $"образуваното сочи {draws.Count} партиди вместо две");
            formNotes.Add(draws.Sum(d => d.Amount) == 15m ? "" : "дяловете не събират количеството");
            formNotes.Add(draws.Any(d => d.Amount == 9m)
                ? "" : "произходът не следва съотношението на третираното");

            // „Описание на отпадъка“ е задължително по схемата и се попълва от
            // името на кода — на ред, а не веднъж за всички.
            formNotes.Add(copper is not null &&
                          db.Ledger.Fields(copper.Id).GetValueOrDefault("waste_desc", string.Empty).Length > 0
                ? "" : "описанието на отпадъка не стига до движението");

            var plastic = generated.FirstOrDefault(m => m.Ewc == "191204");

            formNotes.Add(copper is not null && plastic is not null &&
                          db.Ledger.Fields(copper.Id).GetValueOrDefault("waste_desc", "a") !=
                          db.Ledger.Fields(plastic.Id).GetValueOrDefault("waste_desc", "b")
                ? "" : "двата кода получиха едно и също описание");

            // Складът се изпразни, а образуваното застана в своя склад.
            page.Refresh();
            page.UseStore(StoreKind.Treated);
            page.UseDate(new DateOnly(2026, 9, 30));

            formNotes.Add(page.RowCount == 0 ? "" : $"„Третиран“ не се изпразни: {page.RowCount} реда");

            page.UseStore(StoreKind.Formed);

            formNotes.Add(page.RowCount == 2 ? "" : $"„Образуван“ показва {page.RowCount} реда вместо два");
            formNotes.Add(!page.GenerateOffered ? "" : "образуването се предлага и от „Образуван“");

            // Копчето на реда следва склада: в „Образуван“ то предава.
            formNotes.Add(page.RowActionShown ? "" : "копчето на реда го няма в „Образуван“");
            formNotes.Add(page.RowActionText == "Предай"
                ? "" : "копчето на реда не предава в „Образуван“: " + page.RowActionText);
            formNotes.Add(page.SubmitOffered ? "" : "предаването не се предлага от „Образуван“");

            // И отчетът тръгва: складът вече не чака нищо.
            using var ledger = new LedgerPage(service, db);

            ledger.UseMonth(new LedgerPeriod(2026, 9));

            formNotes.Add(ledger.PendingTreated(new LedgerPeriod(2026, 9)) == 0m
                ? "" : "„Месец“ още вижда чакащо третиране");
        }
        catch (Exception ex)
        {
            formError = ex.ToString();
        }
    });

    formThread.SetApartmentState(ApartmentState.STA);
    formThread.Start();
    formThread.Join();

    if (formError is not null) Console.WriteLine("     " + formError);

    var formProblems = formNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in formProblems) Console.WriteLine("     " + problem);

    Check("образуването от екрана минава без изключение", formError is null);
    Check($"направени са всичките {formNotes.Count} проверки", formNotes.Count == 79, $"{formNotes.Count}");
    Check("един код се образува в няколко и произходът се пази", formProblems.Count == 0,
        $"{formProblems.Count} проблема");
}


// =============================================== Образувано без основание
//
// Намерено в истинска база: месечната справка показваше 15,4 тона образувани
// над третираното и никой не можеше да каже откъде са. Оказа се, че образуване,
// чиито дялове сочат партиди без чакащо третиране, минава ПРЕЗ склада, без да
// го изчерпи — третираното остава да чака и се обявява втори път.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - образувано без основание се хваща, вместо да се брои два пъти");
{
    var groundNotes = new List<string>();

    var september = new LedgerPeriod(2026, 9);

    var metal = new Batch(1, "2026-0001", "203344556", "Метал ООД", "160216", new DateOnly(2026, 9, 1));
    var skrap = new Batch(2, "2026-0002", "204455667", "Скрап ЕООД", "160216", new DateOnly(2026, 9, 2));
    Batch[] batches = [metal, skrap];

    // Прието по десет тона от двамата; третирани са ДЕСЕТТЕ на Метал ООД.
    Move[] common =
    [
        new(1, MoveKind.Received, new DateOnly(2026, 9, 1), "160216", 10m),
        new(2, MoveKind.Received, new DateOnly(2026, 9, 2), "160216", 10m),
        new(3, MoveKind.Treated, new DateOnly(2026, 9, 10), "160216", 10m)
    ];

    LedgerLink[] shared =
    [
        new(1, metal.Id, 10m),
        new(2, skrap.Id, 10m),
        new(3, metal.Id, 10m)
    ];

    // --- сгрешеното: образуваното сочи партидата на СКРАП, в която нищо не чака
    Move[] wrong = [.. common, new Move(4, MoveKind.Generated, new DateOnly(2026, 9, 12), "191202", 10m)];
    LedgerLink[] wrongLinks = [.. shared, new LedgerLink(4, skrap.Id, 10m)];

    var bad = StockMath.Waiting(batches, wrong, wrongLinks, september.Last);

    groundNotes.Add(bad.Unbacked.Count == 1
        ? "" : $"необоснованите дялове са {bad.Unbacked.Count} вместо един");
    groundNotes.Add(bad.Groundless == 10m
        ? "" : $"без основание са {bad.Groundless} т вместо 10");
    groundNotes.Add(bad.Unbacked.Count == 1 && bad.Unbacked[0].MoveId == 4
        ? "" : "не се казва кое движение е сгрешено");
    groundNotes.Add(bad.Unbacked.Count == 1 && bad.Unbacked[0].BatchId == skrap.Id
        ? "" : "не се казва коя партида е посочена напразно");

    // Ето го двойното броене: третираното СИ СТОИ и може да се обяви пак.
    groundNotes.Add(bad.Total == 10m
        ? "" : $"след образуването чакат {bad.Total} т вместо 10 — тогава няма двойно броене");

    var issues = Reconcile.Unbacked(batches, wrong, wrongLinks, september);

    groundNotes.Add(issues.Count == 1 ? "" : $"контролата дава {issues.Count} бележки вместо една");
    groundNotes.Add(issues.Count == 1 && issues[0].Kind == LedgerProblem.Unbacked
        ? "" : "бележката не е от този вид");
    groundNotes.Add(issues.Count == 1 && issues[0].Amount == 10m
        ? "" : "бележката не носи количеството");
    groundNotes.Add(issues.Count == 1 && issues[0].Ewc == "191202"
        ? "" : "бележката не носи кода на образуваното");
    groundNotes.Add(issues.Count == 1 && issues[0].On == new DateOnly(2026, 9, 12)
        ? "" : "бележката не води до датата на движението");
    groundNotes.Add(issues.Count == 1 && issues[0].Blocks
        ? "" : "произход без основание не спира месеца");
    groundNotes.Add(issues.Count == 1 && issues[0].Text.Contains("втори път", StringComparison.Ordinal)
        ? "" : "съобщението не казва какво следва: " + (issues.Count == 1 ? issues[0].Text : "няма"));

    var all = Reconcile.Check(batches, wrong, wrongLinks, september);

    groundNotes.Add(all.Any(i => i.Kind == LedgerProblem.Unbacked)
        ? "" : "общата проверка подминава произхода без основание");
    groundNotes.Add(!Reconcile.CanClose(all) ? "" : "месец с необосновано образуване се затваря");

    // --- вярното: същото количество, но сочи партидата, която е третирана
    LedgerLink[] rightLinks = [.. shared, new LedgerLink(4, metal.Id, 10m)];

    var good = StockMath.Waiting(batches, wrong, rightLinks, september.Last);

    groundNotes.Add(good.Unbacked.Count == 0 ? "" : "вярното образуване се обявява за сгрешено");
    groundNotes.Add(good.Total == 0m ? "" : $"вярното образуване оставя {good.Total} т да чакат");
    groundNotes.Add(Reconcile.Unbacked(batches, wrong, rightLinks, september).Count == 0
        ? "" : "вярното образуване вдига бележка");

    // --- собственият отпадък: образува се, без нищо да е третирано, и няма дялове
    Move[] own =
    [
        new(1, MoveKind.Generated, new DateOnly(2026, 9, 12), "191202", 10m)
    ];

    var mine = StockMath.Waiting(batches, own, [], september.Last);

    groundNotes.Add(mine.Unbacked.Count == 0
        ? "" : "собственият отпадък се брои за произход без основание");
    groundNotes.Add(Reconcile.Unbacked(batches, own, [], september).Count == 0
        ? "" : "площадка, която само образува, получава бележка");

    // --- чуждият месец не се брои тук: всеки месец носи своите бележки
    groundNotes.Add(Reconcile.Unbacked(batches, wrong, wrongLinks, new LedgerPeriod(2026, 10)).Count == 0
        ? "" : "сгрешеното движение се брои и в следващия месец");

    var groundProblems = groundNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in groundProblems) Console.WriteLine("     " + problem);

    Check($"направени са всичките {groundNotes.Count} проверки", groundNotes.Count == 20,
        $"{groundNotes.Count}");
    Check("образувано без основание се хваща на място", groundProblems.Count == 0,
        $"{groundProblems.Count} проблема");
}


// =============================================== Предаването: кантарът и изравняването
//
// Работата тук е обърната на обичайната. Третирането пита „колко взимам“;
// предаването знае отговора предварително — камионът е теглен — и пита „кои
// редове правят точно толкова“. Затова се проверява не само сглобяването, а и
// разликата: тя е самото нещо, заради което прозорчето съществува.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - предаването от склада се смята без екран");
{
    var handNotes = new List<string>();

    var metal = new Batch(1, "2026-0001", "203344556", "Метал ООД", "160216", new DateOnly(2026, 9, 3));
    var skrap = new Batch(2, "2026-0002", "204455667", "Скрап ЕООД", "160216", new DateOnly(2026, 9, 10));
    var cable = new Batch(3, "2026-0003", "204455667", "Скрап ЕООД", "170411", new DateOnly(2026, 9, 11));

    // Складът е „Образуван“: партидата помни кода, с който отпадъкът е ДОШЪЛ
    // (кабел), а редът носи кода, с който е ИЗЛЯЗЪЛ (мед). Точно в това
    // разминаване живее проследимостта — и точно то късаше произхода досега.
    var shares = new List<StockShare>
    {
        new("191202", skrap, 8.0m),
        new("191202", metal, 12.4m),
        new("191204", cable, 5.0m)
    };

    var piles = Handover.Piles(shares);

    handNotes.Add(piles.Count == 2 ? "" : $"купчините са {piles.Count} вместо две");
    handNotes.Add(piles[0].Ewc == "191202" ? "" : "купчините не са подредени по код: " + piles[0].Ewc);
    handNotes.Add(piles[0].Available == 20.4m ? "" : $"сборът по кода е {piles[0].Available} вместо 20,4");

    var whole = new List<TreatPick>();
    foreach (var pile in piles) whole.AddRange(Handover.Spread(pile, pile.Available));

    handNotes.Add(whole.Count == 3 ? "" : $"цялото се разписва на {whole.Count} дяла вместо три");
    handNotes.Add(whole.Sum(p => p.Amount) == 25.4m ? "" : "дяловете не събират наличното");

    // Кантарът: заявеното срещу избраното. Разликата е със знак, защото двете
    // посоки искат различно действие.
    var quiet = Handover.Weigh(whole, 0m);

    handNotes.Add(!quiet.Stated ? "" : "празно поле се брои за заявено количество");
    handNotes.Add(quiet.Matches ? "" : "без заявено количество се иска съвпадение");

    var lacking = Handover.Weigh(whole, 30m);

    handNotes.Add(lacking.Short && lacking.Gap == 4.6m
        ? "" : $"недостигът е {lacking.Gap} вместо 4,6");

    var over = Handover.Weigh(whole, 20m);

    handNotes.Add(over.Over && over.Gap == -5.4m ? "" : $"излишъкът е {over.Gap} вместо −5,4");
    handNotes.Add(Handover.Weigh(whole, 25.4m).Matches ? "" : "съвпадащите числа не се познават");

    // „Изравни“: разликата сяда в един ред, доколкото той я побира.
    var (fits, nothingLeft) = Handover.Onto(8.0m, 8.0m, -5.4m);

    handNotes.Add(fits == 2.6m && nothingLeft == 0m
        ? "" : $"свалянето дава {fits} с остатък {nothingLeft} вместо 2,6 и нула");

    var (floored, below) = Handover.Onto(2.0m, 8.0m, -5.4m);

    handNotes.Add(floored == 0m && below == -3.4m
        ? "" : $"под нулата редът дава {floored} с остатък {below}");

    var (capped, above) = Handover.Onto(8.0m, 8.0m, 4.6m);

    handNotes.Add(capped == 8.0m && above == 4.6m
        ? "" : $"над наличното редът дава {capped} с остатък {above}");

    // „Пропорционално“: същото съотношение, а сборът — точно заявеното.
    var scaled = Handover.Scale(whole, 20.32m);

    handNotes.Add(scaled.Count == 3 ? "" : $"свиването оставя {scaled.Count} дяла вместо три");
    handNotes.Add(scaled.Sum(p => p.Amount) == 20.32m
        ? "" : $"свитите дялове събират {scaled.Sum(p => p.Amount)} вместо 20,32");
    handNotes.Add(scaled.All(p => p.Amount <= p.Share.Amount) ? "" : "свиването надхвърля налично по дял");

    // Нагоре се стига само докъдето стига складът: излишъкът не се измисля.
    var pushed = Handover.Scale(whole, 30m);

    handNotes.Add(pushed.Sum(p => p.Amount) == 25.4m
        ? "" : $"вдигането над наличното дава {pushed.Sum(p => p.Amount)} вместо 25,4");

    // А от половин избор нагоре — стига: всеки дял има място.
    var half = new List<TreatPick>
    {
        new(shares[1], 6.2m), new(shares[0], 4.0m), new(shares[2], 2.5m)
    };

    var lifted = Handover.Scale(half, 20.4m);

    handNotes.Add(lifted.Sum(p => p.Amount) == 20.4m
        ? "" : $"вдигането до заявеното дава {lifted.Sum(p => p.Amount)} вместо 20,4");

    // Проверката: складът и кантарът, на едно място.
    var orders = new List<HandOrder> { new(piles[0], 20.4m), new(piles[1], 5.0m) };

    handNotes.Add(Handover.Check(orders, 25.4m).Count == 0 ? "" : "съвпадащото предаване се спира");

    var missing = Handover.Check(orders, 30m);

    handNotes.Add(missing.Any(i => i.Kind == HandProblem.Short) ? "" : "недостигът до кантара минава");
    handNotes.Add(missing.Count > 0 && missing[0].Text.Contains("4,600", StringComparison.Ordinal)
        ? "" : "съобщението не казва колко не достига: " + (missing.Count > 0 ? missing[0].Text : "няма"));

    var tooMuch = Handover.Check(orders, 20m);

    handNotes.Add(tooMuch.Any(i => i.Kind == HandProblem.Over) ? "" : "излишъкът над кантара минава");
    handNotes.Add(tooMuch.Count > 0 && tooMuch[0].Text.Contains("5,400", StringComparison.Ordinal)
        ? "" : "съобщението не казва с колко се надхвърля: " + (tooMuch.Count > 0 ? tooMuch[0].Text : "няма"));

    handNotes.Add(Handover.Check(orders).Count == 0 ? "" : "без заявено количество се иска съвпадение");

    handNotes.Add(Handover.Check([new HandOrder(piles[0], 21m)]).Any(i => i.Kind == HandProblem.OverStock)
        ? "" : "искане над наличното минава");

    handNotes.Add(Handover.Check([new HandOrder(piles[0], 0m)]).Any(i => i.Kind == HandProblem.Nothing)
        ? "" : "празно предаване минава");

    // Сглобяването: един ред на код, а произходът остава в дяловете.
    var planned = Handover.Plan(orders);

    handNotes.Add(planned.Count == 2 ? "" : $"сглобени са {planned.Count} реда вместо два");
    handNotes.Add(planned[0].Ewc == "191202" && planned[0].Amount == 20.4m
        ? "" : "първият ред не носи кода и количеството си");
    handNotes.Add(planned[0].Draws.Count == 2 ? "" : "произходът не стига до двете партиди");
    handNotes.Add(planned[0].Draws.Sum(d => d.Amount) == 20.4m ? "" : "дяловете не събират количеството");

    handNotes.Add(Handover.Plan(orders, perBatch: true).Count == 3
        ? "" : "отделен ред за всяка партида не дава три реда");

    // Общ изходен код: рядко, но се случва — приемащият води отпадъка под друг
    // код. Складът пак се вади по кода, под който количеството СТОИ.
    var renamed = Handover.Plan(orders, ewcOut: "191203");

    handNotes.Add(renamed.All(m => m.EwcOut == "191203") ? "" : "общият код не стига до движенията");
    handNotes.Add(renamed[0].Ewc == "191202"
        ? "" : "общият код измести кода, по който се вади складът: " + renamed[0].Ewc);
    handNotes.Add(Handover.Plan(orders, ewcOut: "191202")[0].EwcOut.Length == 0
        ? "" : "код, съвпаднал със стоящия, се записва втори път");
    handNotes.Add(Handover.Plan(orders).All(m => m.EwcOut.Length == 0)
        ? "" : "без общ код се записва изходен код");

    // Недостигът назовава склада: при пряко предаване той е „Получен“, а не
    // „Образуван“, и съобщение без име праща човека да търси в другия.
    var elsewhere = Handover.Check([new HandOrder(piles[0], 21m)], 0m, StoreKind.Received);

    handNotes.Add(elsewhere.Any(i => i.Text.Contains("Получен", StringComparison.Ordinal))
        ? "" : "съобщението не казва в кой склад е недостигът: " + elsewhere[0].Text);
    handNotes.Add(Handover.Check([new HandOrder(piles[0], 21m)])
            .Any(i => i.Text.Contains("Образуван", StringComparison.Ordinal))
        ? "" : "по подразбиране недостигът не сочи склад „Образуван“");

    // И най-важното: посочените партиди минават такива, каквито са. Без тях
    // записът търси партида по кода на ДВИЖЕНИЕТО (191202), а партидата помни
    // кода, с който отпадъкът е дошъл (160216) — и не намира нищо.
    var chosenOnly = Handover.From([new TreatPick(shares[0], 8.0m)]);

    handNotes.Add(chosenOnly.Count == 1 && chosenOnly[0].Draws.Count == 1 &&
                  chosenOnly[0].Draws[0].BatchId == skrap.Id
        ? "" : "посоченият произход не стига до движението");

    var handProblems = handNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in handProblems) Console.WriteLine("     " + problem);

    Check($"направени са всичките {handNotes.Count} проверки", handNotes.Count == 38, $"{handNotes.Count}");
    Check("предаването от склада се смята вярно", handProblems.Count == 0,
        $"{handProblems.Count} проблема");
}


// =============================================== Предаването от екрана
//
// Истинският ред на работа: камионът е напълнен и теглен, а чак после се пита
// от кои редове е събран. Тук се минава по него от край до край — избор,
// заявено общо количество, изравняване в посочения ред, запис — и се проверява
// това, което дотук се късаше: произходът на предаденото.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - от склад „Образуван“ се предава и кантарът изравнява");
{
    var handNotes = new List<string>();
    string? handError = null;

    var handThread = new Thread(() =>
    {
        try
        {
            var handPath = Path.Combine(work, $"submit_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(handPath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 1), "160216", 12m,
                Eik: "203344556", Partner: "Метал ООД"));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 2), "160216", 8m,
                Eik: "204455667", Partner: "Скрап ЕООД"));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Treated, new DateOnly(2026, 9, 10), "160216", 20m));

            // Двайсетте тона кабел излизат като петнайсет мед и пет пластмаса.
            // Произходът се наследява в съотношението на третираното: 60 на 40.
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Generated, new DateOnly(2026, 9, 11), "191202", 15m));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Generated, new DateOnly(2026, 9, 11), "191204", 5m));

            using var page = new StockPage(service, db);

            page.UseDate(new DateOnly(2026, 9, 30));
            page.UseStore(StoreKind.Formed);

            handNotes.Add(page.RowCount == 2 ? "" : $"„Образуван“ показва {page.RowCount} реда вместо два");
            handNotes.Add(page.SubmitOffered ? "" : "предаването не се предлага от „Образуван“");
            handNotes.Add(page.ActionText.Contains("Предай", StringComparison.Ordinal)
                ? "" : "копчето в лентата не предава: " + page.ActionText);

            page.SelectMaster(0, 1);
            var chosen = page.Selection();

            handNotes.Add(chosen.Count == 4 ? "" : $"зад двата кода стоят {chosen.Count} дяла вместо четири");

            using (var dialog = new SubmitDialog(
                       service, db, companyId, "1", chosen, new DateOnly(2026, 9, 30)))
            {
                handNotes.Add(dialog.RowCount == 2 ? "" : $"прозорчето показва {dialog.RowCount} реда");
                handNotes.Add(dialog.PickedTotal == 20m
                    ? "" : $"отваря се с {dialog.PickedTotal} т вместо с целите 20");
                handNotes.Add(dialog.SaveReachable ? "" : "копчето „Предай“ не се побира в прозореца");

                // Датата и кантарът стоят един до друг, вдясно, до самите числа.
                handNotes.Add(SubmitDialog.DateCaption == "Дата на предаване"
                    ? "" : "датата не е озаглавена: " + SubmitDialog.DateCaption);
                handNotes.Add(SubmitDialog.TargetCaption == "Предадено общо, т"
                    ? "" : "кантарът не е озаглавен: " + SubmitDialog.TargetCaption);
                handNotes.Add(dialog.DateFits ? "" : "датата не се побира в дясната рамка");
                handNotes.Add(dialog.TargetFits ? "" : "кантарът не се побира в дясната рамка");
                handNotes.Add(dialog.BalanceFits ? "" : "копчетата за изравняване са отрязани отдясно");
                handNotes.Add(dialog.WhenText.Contains("септември 2026", StringComparison.Ordinal)
                    ? "" : "не се казва в кой месец влиза записът: " + dialog.WhenText);

                // Празно поле не пита нищо: не всяко предаване минава през кантар.
                handNotes.Add(!dialog.LevelOffered ? "" : "изравняването се предлага без заявено количество");
                handNotes.Add(dialog.GapText.Contains("Не е заявено", StringComparison.Ordinal)
                    ? "" : "мълчанието на кантара не се казва: " + dialog.GapText);
                handNotes.Add(dialog.Blocking().All(p => !p.Contains("заявен", StringComparison.Ordinal))
                    ? "" : "празният кантар спира записа");

                // Камионът е показал осемнайсет, сметката назад дава двайсет.
                dialog.UseTarget(18m);

                handNotes.Add(dialog.GapText.Contains("2,000 т в повече", StringComparison.Ordinal)
                    ? "" : "излишъкът не се изписва: " + dialog.GapText);
                handNotes.Add(dialog.LevelOffered ? "" : "изравняването не се предлага при разминаване");
                handNotes.Add(dialog.Blocking().Any(p => p.Contains("надхвърля", StringComparison.Ordinal))
                    ? "" : "разминаването с кантара не спира записа");

                // Разликата сяда в реда, чието количество е посочено — и
                // прозорчето го казва ПРЕДИ копчето да е натиснато. Посочва се
                // през колоната с количеството нарочно: всяко друго щракване е
                // ИЗБОР и би извадило другия код от предаването.
                dialog.PickTakeCell(0);

                handNotes.Add(dialog.PickedTotal == 20m
                    ? "" : $"посочването изяде избора: {dialog.PickedTotal} т вместо 20");

                handNotes.Add(dialog.AnchorText.Contains("191202", StringComparison.Ordinal)
                    ? "" : "не се казва кой ред ще поеме разликата: " + dialog.AnchorText);
                handNotes.Add(dialog.AnchorText.Contains("2,000", StringComparison.Ordinal)
                    ? "" : "не се казва колко ще слезе: " + dialog.AnchorText);

                dialog.UseLevel();

                handNotes.Add(dialog.PickedTotal == 18m
                    ? "" : $"изравняването дава {dialog.PickedTotal} т вместо 18");
                handNotes.Add(dialog.TakeAt(0) == "13,000"
                    ? "" : "разликата не седна в посочения ред: " + dialog.TakeAt(0));
                handNotes.Add(dialog.TakeAt(1) == "5,000"
                    ? "" : "изравняването пипна и другия ред: " + dialog.TakeAt(1));
                handNotes.Add(!dialog.LevelOffered ? "" : "изравненото пак предлага изравняване");
                handNotes.Add(dialog.GapText.Contains("точно заявеното", StringComparison.Ordinal)
                    ? "" : "съвпадението не се казва: " + dialog.GapText);
                handNotes.Add(dialog.Blocking().All(p => !p.Contains("надхвърля", StringComparison.Ordinal))
                    ? "" : "изравненото пак спира записа");

                // Обратната посока: заявено повече, отколкото редът побира —
                // казва се колко остава, вместо да се премълчи.
                dialog.UseTarget(30m);
                dialog.PickTakeCell(0);

                handNotes.Add(dialog.GapText.Contains("не достигат 12,000", StringComparison.Ordinal)
                    ? "" : "недостигът не се изписва: " + dialog.GapText);
                handNotes.Add(dialog.AnchorText.Contains("ще останат", StringComparison.Ordinal)
                    ? "" : "непобралото се премълчава: " + dialog.AnchorText);

                dialog.UseLevel();

                handNotes.Add(dialog.PickedTotal == 20m
                    ? "" : $"вдигането над наличното дава {dialog.PickedTotal} т вместо 20");
                handNotes.Add(dialog.LevelOffered ? "" : "недостигналото изравняване се обявява за готово");

                // Пропорционално: същото съотношение, сборът точно на кантара.
                dialog.UseTarget(15m);
                dialog.UseProportional();

                handNotes.Add(dialog.PickedTotal == 15m
                    ? "" : $"пропорционалното дава {dialog.PickedTotal} т вместо 15");
                handNotes.Add(dialog.TakeAt(0) == "11,250"
                    ? "" : "съотношението не се пази при свиване: " + dialog.TakeAt(0));

                // Обратно на осемнайсетте — това се записва.
                dialog.UseTake(0, 15m);
                dialog.UseTake(1, 5m);
                dialog.UseTarget(18m);
                dialog.PickTakeCell(0);
                dialog.UseLevel();

                // Изходният код се пита само когато някой го поиска.
                handNotes.Add(!dialog.OutCodeOffered ? "" : "менюто за общ код стои отключено");

                var planned = dialog.Planned();

                handNotes.Add(planned.Count == 2 ? "" : $"сглобени са {planned.Count} реда вместо два");
                handNotes.Add(planned.Sum(m => m.Amount) == 18m
                    ? "" : "сглобеното не събира заявеното");

                var copper = planned.FirstOrDefault(m => m.Ewc == "191202");

                handNotes.Add(copper is not null && copper.Amount == 13m
                    ? "" : "медта не носи изравненото си количество");
                handNotes.Add(copper is not null && copper.Draws.Count == 2
                    ? "" : "произходът не стига до двете партиди");
                handNotes.Add(copper is not null && copper.Draws.Sum(d => d.Amount) == 13m
                    ? "" : "дяловете не събират количеството на реда");

                // Задължителните полета на схемата спират записа, докато ги няма.
                handNotes.Add(dialog.Blocking().Any(p => p.Contains("задължително", StringComparison.Ordinal))
                    ? "" : "липсващ получател не спира записа");

                dialog.UseField("name_receiver", "Претопи ООД");
                dialog.UseField("op_descr", "Предаване за оползотворяване");

                var written = page.ApplySubmitted(
                    planned, new DateOnly(2026, 9, 20),
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["name_receiver"] = "Претопи ООД",
                        ["pin_receiver"] = "205566778"
                    });

                handNotes.Add(written == 2 ? "" : $"записани са {written} движения вместо две");
            }

            var submitted = db.Ledger.Read(companyId, "1")
                .Where(m => m.Kind == MoveKind.Submitted)
                .OrderBy(m => m.Ewc, StringComparer.Ordinal)
                .ToList();

            handNotes.Add(submitted.Count == 2 ? "" : $"в книгата стоят {submitted.Count} предавания");
            handNotes.Add(submitted.Sum(m => m.Amount) == 18m
                ? "" : "предаденото в книгата не е заявеното");

            var line = submitted.FirstOrDefault(m => m.Ewc == "191202");
            var draws = line is null ? [] : db.Ledger.Draws(line.Id);

            // Заради това съществува пътят оттук: без посочени дялове записът
            // търси партида по кода на движението (191202), а партидите го
            // помнят като 160216 — и движението оставаше без произход.
            handNotes.Add(draws.Count == 2
                ? "" : $"предаденото сочи {draws.Count} партиди вместо две");
            handNotes.Add(draws.Sum(d => d.Amount) == 13m ? "" : "дяловете не събират предаденото");

            handNotes.Add(line is not null &&
                          db.Ledger.Fields(line.Id).GetValueOrDefault("name_receiver", string.Empty).Length > 0
                ? "" : "получателят не стига до движението");

            // И складът се движи: „Образуван“ олеква, летописът расте.
            page.Refresh();
            page.UseStore(StoreKind.Formed);
            page.UseDate(new DateOnly(2026, 9, 30));

            handNotes.Add(page.RowCount == 1
                ? "" : $"„Образуван“ показва {page.RowCount} реда вместо един остатък");

            page.UseStore(StoreKind.Submitted);

            handNotes.Add(page.LooseAmount == 0m
                ? "" : $"предадено без посочена партида: {page.LooseAmount} т");
            handNotes.Add(!page.SubmitOffered ? "" : "от летописа се предлага предаване");
        }
        catch (Exception ex)
        {
            handError = ex.ToString();
        }
    });

    handThread.SetApartmentState(ApartmentState.STA);
    handThread.Start();
    handThread.Join();

    if (handError is not null) Console.WriteLine("     " + handError);

    var handProblems = handNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in handProblems) Console.WriteLine("     " + problem);

    Check("предаването от екрана минава без изключение", handError is null);
    Check($"направени са всичките {handNotes.Count} проверки", handNotes.Count == 50, $"{handNotes.Count}");
    Check("кантарът изравнява и произходът на предаденото се пази", handProblems.Count == 0,
        $"{handProblems.Count} проблема");
}



// =============================================== Прякото предаване
//
// Не всичко се третира. Площадка за събиране приема опаковки от стотина
// контрагента и ги предава почти както са дошли — а изискването да минат през
// третиране, за да излязат, би било измислено от програмата, не от работата.
// Движението е същото „предаден“, само че вади от другия склад.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - от склад „Получен“ се предава направо, без третиране");
{
    var directNotes = new List<string>();
    string? directError = null;

    var directThread = new Thread(() =>
    {
        try
        {
            var directPath = Path.Combine(work, $"direct_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(directPath);

            var companyId = db.Companies.Add("203344556", "Пакет ООД");

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 1), "150101", 12m,
                Eik: "203344556", Partner: "Търговец ООД", Document: "ППП 221/01.09"));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 2), "150101", 8m,
                Eik: "204455667", Partner: "Магазин ЕООД", Document: "ППП 222/02.09"));

            using var page = new StockPage(service, db);

            page.UseDate(new DateOnly(2026, 9, 30));
            page.UseStore(StoreKind.Received);

            directNotes.Add(page.RowCount == 1 ? "" : $"„Получен“ показва {page.RowCount} реда вместо един");

            // В „Получен“ важат ДВЕ действия: третиране и пряко предаване.
            directNotes.Add(page.TreatOffered ? "" : "третирането не се предлага от „Получен“");
            directNotes.Add(page.DirectOffered ? "" : "прякото предаване не се предлага от „Получен“");
            directNotes.Add(page.DirectButtonShown ? "" : "копчето „Предай направо…“ го няма в „Получен“");
            directNotes.Add(page.RowActionText == "Третирай"
                ? "" : "копчето на реда спря да третира в „Получен“: " + page.RowActionText);

            page.SelectMaster(0);
            var chosen = page.Selection();

            directNotes.Add(chosen.Count == 2 ? "" : $"зад кода стоят {chosen.Count} дяла вместо два");

            using (var dialog = new SubmitDialog(
                       service, db, companyId, "1", chosen, new DateOnly(2026, 9, 30), direct: true))
            {
                directNotes.Add(dialog.Direct ? "" : "прозорчето не знае, че предаването е пряко");
                directNotes.Add(dialog.Text.Contains("Пряко", StringComparison.Ordinal)
                    ? "" : "заглавието не казва, че предаването е пряко: " + dialog.Text);
                directNotes.Add(dialog.RowCount == 1 ? "" : $"прозорчето показва {dialog.RowCount} реда");
                directNotes.Add(dialog.PickedTotal == 20m
                    ? "" : $"отваря се с {dialog.PickedTotal} т вместо с целите 20");
                directNotes.Add(dialog.OriginCount == 2
                    ? "" : $"произходът показва {dialog.OriginCount} реда вместо два");

                // Кантарът работи еднакво и на двата склада.
                dialog.UseTarget(18m);
                dialog.PickTakeCell(0);
                dialog.UseLevel();

                directNotes.Add(dialog.PickedTotal == 18m
                    ? "" : $"изравняването дава {dialog.PickedTotal} т вместо 18");

                // Недостигът назовава СВОЯ склад.
                dialog.UseTarget(30m);
                directNotes.Add(dialog.Blocking().Any(p => p.Contains("не достигат", StringComparison.Ordinal))
                    ? "" : "недостигът до кантара не спира прякото предаване");

                dialog.UseTarget(18m);
                dialog.PickTakeCell(0);
                dialog.UseLevel();

                // Приемащият води отпадъка под друг код — рядко, но се случва.
                dialog.UseOutCode("150102");

                directNotes.Add(dialog.OutCodeOffered ? "" : "менюто за общ код остана заключено");

                var planned = dialog.Planned();

                directNotes.Add(planned.Count == 1 ? "" : $"сглобени са {planned.Count} реда вместо един");
                directNotes.Add(planned.Count == 1 && planned[0].Ewc == "150101"
                    ? "" : "общият код измести кода, по който се вади складът");
                directNotes.Add(planned.Count == 1 && planned[0].EwcOut == "150102"
                    ? "" : "общият код не стига до движението");
                directNotes.Add(planned.Count == 1 && planned[0].Draws.Count == 2
                    ? "" : "произходът не стига до двете партиди");

                var written = page.ApplySubmitted(
                    planned, new DateOnly(2026, 9, 20),
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["name_receiver"] = "Претопи ООД"
                    },
                    string.Empty, direct: true);

                directNotes.Add(written == 1 ? "" : $"записани са {written} движения вместо едно");
            }

            var submitted = db.Ledger.Read(companyId, "1")
                .Where(m => m.Kind == MoveKind.Submitted)
                .ToList();

            directNotes.Add(submitted.Count == 1 ? "" : $"в книгата стоят {submitted.Count} предавания");

            var line = submitted.FirstOrDefault();

            // Флагът пътува до самото движение: наличността се смята от него, а
            // не от това кой екран е записал реда.
            directNotes.Add(line is { Direct: true } ? "" : "прякото предаване не се помни като пряко");
            directNotes.Add(line is not null && line.Ewc == "150101"
                ? "" : "движението не носи кода, по който се вади складът");
            directNotes.Add(line is not null && line.Reported == "150102"
                ? "" : "в книгата не влиза избраният общ код: " + (line?.Reported ?? "няма"));
            directNotes.Add(line is not null && line.Amount == 18m ? "" : "предаденото не е изравненото");

            var draws = line is null ? [] : db.Ledger.Draws(line.Id);

            directNotes.Add(draws.Count == 2 ? "" : $"предаденото сочи {draws.Count} партиди вместо две");
            directNotes.Add(draws.Sum(d => d.Amount) == 18m ? "" : "дяловете не събират предаденото");

            // И складът олеква там, откъдето количеството наистина си отива.
            page.Refresh();
            page.UseStore(StoreKind.Received);
            page.UseDate(new DateOnly(2026, 9, 30));

            page.SelectMaster(0);

            directNotes.Add(page.RowCount == 1
                ? "" : $"„Получен“ показва {page.RowCount} реда вместо остатъка");
            directNotes.Add(page.Selection().Sum(x => x.Amount) == 2m
                ? "" : $"в „Получен“ остават {page.Selection().Sum(x => x.Amount)} т вместо два");

            page.UseStore(StoreKind.Formed);

            directNotes.Add(page.RowCount == 0
                ? "" : $"прякото предаване мина през „Образуван“: {page.RowCount} реда");

            page.UseStore(StoreKind.Submitted);

            directNotes.Add(!page.DirectOffered ? "" : "от летописа се предлага пряко предаване");
            directNotes.Add(page.LooseAmount == 0m
                ? "" : $"предадено без посочена партида: {page.LooseAmount} т");
        }
        catch (Exception ex)
        {
            directError = ex.ToString();
        }
    });

    directThread.SetApartmentState(ApartmentState.STA);
    directThread.Start();
    directThread.Join();

    if (directError is not null) Console.WriteLine("     " + directError);

    var directProblems = directNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in directProblems) Console.WriteLine("     " + problem);

    Check("прякото предаване минава без изключение", directError is null);
    Check($"направени са всичките {directNotes.Count} проверки", directNotes.Count == 31,
        $"{directNotes.Count}");
    Check("полученото си отива, без да минава през третиране", directProblems.Count == 0,
        $"{directProblems.Count} проблема");
}



// =============================================== Закаченото не се засенчва
//
// Капанът от АРХИТЕКТУРА.md, намерен в работещ вид: в трите прозорчета на
// склада редът под заглавието се добавяше ПРЕДИ пълнещия надпис. Закаченото се
// подрежда от последното добавено назад, тъй че пълнещият заемаше цялата
// височина, а обяснението оставаше със своите 24 пиксела ВЪРХУ него — на
// екрана го нямаше. Стоеше така от първото прозорче нататък и се виждаше само
// в кода.
//
// Проверката е обща, а не за трите надписа: пълнещ надпис, който покрива
// закачен свой брат, е една и съща грешка навсякъде.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - пълнещ надпис не покрива закачен до него");
{
    var coverNotes = new List<string>();
    string? coverError = null;
    var coverChecked = 0;

    var coverThread = new Thread(() =>
    {
        try
        {
            var coverPath = Path.Combine(work, $"cover_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(coverPath);

            var coverCompany = db.Companies.Add("203344556", "Метал ООД");
            var coverBatch = db.Batches.Add(coverCompany, new BatchDraft(
                "203344556", "Метал ООД", "160216", new DateOnly(2026, 9, 1)));

            var coverShare = new StockShare("160216", db.Batches.Find(coverBatch)!, 10m);

            var windows = new List<Form>
            {
                new TreatDialog(service, db, coverCompany, "1", [coverShare],
                    new DateOnly(2026, 9, 30)),

                new GenerateDialog(service, db, coverCompany, "1", [coverShare],
                    new DateOnly(2026, 9, 30)),

                new SubmitDialog(service, db, coverCompany, "1", [coverShare],
                    new DateOnly(2026, 9, 30))
            };

            foreach (var window in windows)
                using (window)
                {
                    _ = window.Handle;

                    foreach (var parent in Everything(window))
                    {
                        var labels = parent.Controls.OfType<Label>().ToList();

                        var filling = labels.FirstOrDefault(l => l.Dock == DockStyle.Fill);
                        if (filling is null) continue;

                        foreach (var pinned in labels.Where(l =>
                                     l.Dock is DockStyle.Top or DockStyle.Bottom))
                        {
                            coverChecked++;

                            if (!filling.Bounds.IntersectsWith(pinned.Bounds)) continue;

                            coverNotes.Add(
                                $"{window.GetType().Name}: „{Short(pinned.Text)}“ се покрива от " +
                                $"„{Short(filling.Text)}“");
                        }
                    }
                }
        }
        catch (Exception ex)
        {
            coverError = ex.ToString();
        }
    });

    coverThread.SetApartmentState(ApartmentState.STA);
    coverThread.Start();
    coverThread.Join();

    if (coverError is not null) Console.WriteLine("     " + coverError);
    foreach (var problem in coverNotes) Console.WriteLine("     " + problem);

    Check("прозорчетата се сглобяват без изключение", coverError is null);
    Check("проверени са надписите в трите прозорчета", coverChecked >= 3, $"{coverChecked}");
    Check("нито един закачен надпис не е покрит", coverNotes.Count == 0, $"{coverNotes.Count} покрити");
}



// =============================================== Едно копче на лентата
//
// Оплакването беше „изглежда, че съм отворил програмата няколко пъти“. Всяко
// прозорче си слагаше свое копче на лентата на задачите, а копче на лентата в
// Windows значи отделна програма: отскачаш до Excel, връщаш се и не знаеш коя
// от трите еднакви иконки е живата.
//
// Проверката е по ТИПОВЕТЕ, а не по сглобени прозорци: прозорче, добавено
// утре, ще се хване и без да бъде отворено от никого.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - прозорчетата не слагат свои копчета на лентата");
{
    var barNotes = new List<string>();

    var windows = typeof(ShellForm).Assembly.GetTypes()
        .Where(t => t.IsSubclassOf(typeof(Form)) && t != typeof(Dialog))
        .OrderBy(t => t.Name, StringComparer.Ordinal)
        .ToList();

    // Двата прозореца, които СА програмата: те държат копчето.
    var roots = new[] { "ShellForm", "MainForm" };

    foreach (var window in windows)
    {
        if (roots.Contains(window.Name)) continue;

        barNotes.Add(typeof(Dialog).IsAssignableFrom(window)
            ? ""
            : $"{window.Name} не е прозорче (Dialog) и ще си сложи копче на лентата");
    }

    // И обратното: основата наистина маха копчето и смаляването.
    string? barError = null;
    var barThread = new Thread(() =>
    {
        try
        {
            using var plain = new Dialog();

            barNotes.Add(!plain.ShowInTaskbar ? "" : "прозорчето пак слага копче на лентата");
            barNotes.Add(!plain.MinimizeBox ? "" : "прозорче без копче на лентата може да се смали");
            barNotes.Add(plain.StartPosition == FormStartPosition.CenterParent
                ? "" : "прозорчето не се отваря върху този, който го е отворил");

            // Черупката е програмата: тя държи копчето и тя се смалява.
            using var shellPath = new TempDb(work);
            using var db = Workspace.Open(shellPath.Path);
            using var shell = new ShellForm(service, db);

            barNotes.Add(shell.ShowInTaskbar ? "" : "програмата остана без копче на лентата");
        }
        catch (Exception ex)
        {
            barError = ex.ToString();
        }
    });

    barThread.SetApartmentState(ApartmentState.STA);
    barThread.Start();
    barThread.Join();

    if (barError is not null) Console.WriteLine("     " + barError);

    var barProblems = barNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in barProblems) Console.WriteLine("     " + problem);

    Check("основата се сглобява без изключение", barError is null);
    // 35: с проверката на месеца (MonthCheckDialog, 1.3.1).
    Check($"проверени са всичките {barNotes.Count} прозореца", barNotes.Count == 35,
        $"{barNotes.Count}");
    Check("програмата стои на лентата с едно копче", barProblems.Count == 0,
        $"{barProblems.Count} проблема" + (barProblems.Count > 0 ? ": " + string.Join(" | ", barProblems.Take(5)) : ""));
}



// =============================================== Една трета за избора
//
// Вляво стоят кодовете — списък, който се ЧЕТЕ. Вдясно се работи: партидите,
// количествата, датата, кантарът. Дотук разделителят тръгваше почти по средата
// и по-голямата половина отиваше при списъка, който я не искаше.
//
// Проверява се обещанието, а не сметката (правило 6): лявата рамка не бива да е
// по-широка от дясната. Върнат ли делът на 0,58, това пада веднага.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - работното прозорче тръгва с една трета вляво");
{
    var splitNotes = new List<string>();
    string? splitError = null;
    var splitChecked = 0;

    splitNotes.Add(Math.Abs(Theme.WorkSplit - 1.0 / 3.0) < 0.0001
        ? "" : $"делът не е една трета: {Theme.WorkSplit}");

    // Мащабът е мярката за всичко, което не е текст: при 100 % не мени нищо, а
    // при 125 % и 150 % кутиите растат заедно с буквите в тях.
    splitNotes.Add(Theme.Zoom >= 1.0 ? "" : $"мащабът излезе под сто процента: {Theme.Zoom}");
    splitNotes.Add(Theme.Scaled(600) >= 600 ? "" : $"мащабът свива размерите: {Theme.Scaled(600)}");
    splitNotes.Add(Theme.Scaled(0) == 0 ? "" : "мащабът прави нещо от нищо");

    var splitThread = new Thread(() =>
    {
        try
        {
            var splitPath = Path.Combine(work, $"split_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(splitPath);

            var splitCompany = db.Companies.Add("203344556", "Метал ООД");
            var splitBatch = db.Batches.Add(splitCompany, new BatchDraft(
                "203344556", "Метал ООД", "160216", new DateOnly(2026, 9, 1)));

            var splitShare = new StockShare("160216", db.Batches.Find(splitBatch)!, 10m);
            var splitDay = new DateOnly(2026, 9, 30);

            var windows = new List<Form>
            {
                new TreatDialog(service, db, splitCompany, "1", [splitShare], splitDay),
                new GenerateDialog(service, db, splitCompany, "1", [splitShare], splitDay),
                new SubmitDialog(service, db, splitCompany, "1", [splitShare], splitDay)
            };

            foreach (var window in windows)
                using (window)
                {
                    // Прозорчетата тръгват разтворени, а разтворен прозорец не
                    // приема размер отвън — затова тук се връща в нормален.
                    window.WindowState = FormWindowState.Normal;

                    _ = window.Handle;

                    // Размерът се задава ТУК, а не се чака от екрана: тестова
                    // машина с тесен работен плот иначе мери друго прозорче от
                    // това, което вижда потребителят. Разделителят следва
                    // прозореца, докато не бъде дръпнат — точно това се ползва.
                    var name = window.GetType().Name;

                    var splits = Everything(window)
                        .OfType<SplitContainer>()
                        .Where(x => x.Orientation == Orientation.Vertical)
                        .ToList();

                    splitNotes.Add(splits.Count == 1
                        ? "" : $"{name}: {splits.Count} разделителя вместо един");

                    foreach (var split in splits)
                    {
                        splitChecked++;

                        // Прозорец по подразбиране: работата взема повече и
                        // лявата таблица пак си показва всички стълбове.
                        window.ClientSize = new Size(1600, 900);

                        var left = split.SplitterDistance;
                        var right = split.Width - left - split.SplitterWidth;

                        // Образуването държи вляво разпределението и кодовете и
                        // лявата рамка иска ширината на кодовете — вдясно
                        // остава колкото искат партидите.
                        var generate = window is GenerateDialog;

                        splitNotes.Add((generate ? right >= Theme.Scaled(700) : left < right)
                            ? "" : $"{name}: вляво {left}, вдясно {right} — изборът взема " +
                                   "повече от работата");

                        // През мащаба, а не като кръгло число: на лаптоп с
                        // 125 % стълбовете искат повече пиксели за същото.
                        splitNotes.Add(split.Panel1MinSize == Theme.Scaled(generate ? 800 : 600)
                            ? "" : $"{name}: лявата рамка не пази стълбовете: {split.Panel1MinSize}");

                        // Широк прозорец: там една трета е повече от минимума и
                        // делът важи както е поискан — около 650 от 1944. При
                        // образуването минимумът е над третината и печели той,
                        // а работата пак взема повечето.
                        window.ClientSize = new Size(2000, 900);

                        var third = split.SplitterDistance;
                        var wide = split.Width - third - split.SplitterWidth;

                        splitNotes.Add((generate ? third == Theme.Scaled(800) && third < wide : third is > 620 and < 680)
                            ? "" : $"{name}: на широк прозорец вляво стоят {third} вместо около " +
                                   (generate ? "800" : "650"));

                        // „Остава на човека, дръпне ли го веднъж“ не се
                        // проверява тук: присвоеното отвън НЕ е дърпане (то не
                        // вдига SplitterMoving) и проверка, минаваща по тази
                        // причина, показва работещо нещо, което не е проверено.
                    }
                }
        }
        catch (Exception ex)
        {
            splitError = ex.ToString();
        }
    });

    splitThread.SetApartmentState(ApartmentState.STA);
    splitThread.Start();
    splitThread.Join();

    if (splitError is not null) Console.WriteLine("     " + splitError);

    var splitProblems = splitNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in splitProblems) Console.WriteLine("     " + problem);

    Check("трите прозорчета се сглобяват без изключение", splitError is null);
    Check("проверени са трите разделителя", splitChecked == 3, $"{splitChecked}");
    Check("работата взема повече място от избора", splitProblems.Count == 0,
        $"{splitProblems.Count} проблема" + (splitProblems.Count > 0 ? ": " + string.Join(" | ", splitProblems.Take(5)) : ""));
}



// =============================================== Работният период: самоконтрол
//
// Не е заключалка и това е цялата разлика. Затвореният месец е отчет, подаден в
// НИСО — него никой не пипа. Работният период е обещание пред самия себе си:
// „тази седмица въвеждам септември“. Програмата държи човека за думата, но той
// я оттегля с отметка, защото правилото е негово.
//
// Заради какво: грешките се случват в първите дни на новия месец, докато
// старият още се дописва.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - работният период пази, без да заключва");
{
    var guardNotes = new List<string>();

    var september = new WorkPeriod(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));

    guardNotes.Add(september.Covers(new DateOnly(2026, 9, 30)) ? "" : "последният ден на периода отпада");
    guardNotes.Add(september.Covers(new DateOnly(2026, 9, 1)) ? "" : "първият ден на периода отпада");
    guardNotes.Add(!september.Covers(new DateOnly(2026, 10, 1)) ? "" : "денят след периода минава");
    guardNotes.Add(!september.Covers(new DateOnly(2026, 8, 31)) ? "" : "денят преди периода минава");
    guardNotes.Add(september.Days == 30 ? "" : $"септември излезе {september.Days} дни");

    // Цял месец се изписва с ИМЕТО си: точно месецът се бърка, а
    // „01.09.2026 – 30.09.2026“ не се чете като септември достатъчно бързо.
    guardNotes.Add(september.IsMonth ? "" : "цял месец не се разпознава като месец");
    guardNotes.Add(september.Title == "септември 2026" ? "" : "месецът не се изписва с име: " + september.Title);

    var week = WorkPeriod.Week(new DateOnly(2026, 9, 17));

    guardNotes.Add(week.From == new DateOnly(2026, 9, 14) ? "" : $"седмицата тръгва от {week.From}");
    guardNotes.Add(week.To == new DateOnly(2026, 9, 20) ? "" : $"седмицата свършва на {week.To}");
    guardNotes.Add(week.Days == 7 ? "" : $"седмицата е {week.Days} дни");
    guardNotes.Add(!week.IsMonth ? "" : "седмица се брои за месец");
    guardNotes.Add(week.Title.Contains("14.09.2026", StringComparison.Ordinal)
        ? "" : "седмицата не се изписва с двете си дати: " + week.Title);

    // Изключеният период приема всичко — програмата не връзва човек, който не е
    // поискал да бъде вързан.
    guardNotes.Add(WorkPeriod.Off.Covers(new DateOnly(2030, 1, 1)) ? "" : "изключеният период спира дата");
    guardNotes.Add(!WorkPeriod.Off.On ? "" : "изключеният период се брои за включен");

    // Обърнати дати не вързват нищо, вместо да спрат всичко.
    var upside = new WorkPeriod(new DateOnly(2026, 9, 30), new DateOnly(2026, 9, 1));

    guardNotes.Add(!upside.Valid ? "" : "обърнатият период се брои за верен");
    guardNotes.Add(upside.Covers(new DateOnly(2026, 9, 15)) ? "" : "обърнатият период спира всичко");

    // Записва се като текст в настройките и се чете обратно същото.
    var written = september.Text;

    guardNotes.Add(WorkPeriod.Read(written) == september
        ? "" : "записаният период не се чете обратно: " + written);
    guardNotes.Add(!WorkPeriod.Read(WorkPeriod.Off.Text).On ? "" : "изключеното се чете за включено");
    guardNotes.Add(!WorkPeriod.Read("боклук").On ? "" : "нечетимото не се брои за изключено");
    guardNotes.Add(!WorkPeriod.Read(null).On ? "" : "липсващото не се брои за изключено");

    // Съобщението казва И датата, И периода: без периода човек не знае какво
    // нарушава, без датата — какво да поправи.
    var missed = september.Miss(new DateOnly(2026, 10, 3));

    guardNotes.Add(missed.Contains("03.10.2026", StringComparison.Ordinal)
        ? "" : "съобщението не казва коя дата е извън: " + missed);
    guardNotes.Add(missed.Contains("септември 2026", StringComparison.Ordinal)
        ? "" : "съобщението не казва кой е периодът: " + missed);

    // Името на месеца казва КОЙ е периодът, но не и докъде стига: човек с дата
    // 01.10 не знае с ден ли е сбъркал, или с месец. Затова в съобщението
    // вървят и двете граници.
    guardNotes.Add(missed.Contains("01.09.2026", StringComparison.Ordinal)
        ? "" : "съобщението не казва откъде тръгва периодът: " + missed);
    guardNotes.Add(missed.Contains("30.09.2026", StringComparison.Ordinal)
        ? "" : "съобщението не казва докъде стига периодът: " + missed);

    guardNotes.Add(september.Bounds.Contains("септември 2026", StringComparison.Ordinal)
        ? "" : "границите губят името на месеца: " + september.Bounds);

    // Период, който вече се изписва с датите си, не ги повтаря.
    guardNotes.Add(week.Bounds == week.Title
        ? "" : "седмицата си повтаря датите: " + week.Bounds);
    guardNotes.Add(WorkPeriod.Off.Bounds == WorkPeriod.Off.Title
        ? "" : "изключеният период получава граници: " + WorkPeriod.Off.Bounds);

    var guardProblems = guardNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in guardProblems) Console.WriteLine("     " + problem);

    Check($"направени са всичките {guardNotes.Count} проверки", guardNotes.Count == 27,
        $"{guardNotes.Count}");
    Check("работният период се смята вярно", guardProblems.Count == 0,
        $"{guardProblems.Count} проблема");
}


// =============================================== Самоконтролът и дневникът на екрана
//
// Двете заедно, защото се ползват заедно: периодът спира грешката, преди да е
// станала, а дневникът я намира, след като е станала. Второто е по-важното —
// „сбъркал съм датата и не знам в кой месец да го търся“ е оплакването, заради
// което дневникът съществува.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - прозорчетата пазят периода и помнят въведеното");
{
    var selfNotes = new List<string>();
    string? selfError = null;

    var selfThread = new Thread(() =>
    {
        try
        {
            var selfPath = Path.Combine(work, $"self_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(selfPath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 1), "160216", 20m,
                Eik: "203344556", Partner: "Метал ООД"));

            // Работи се в септември и само в септември.
            db.Periods.SetWindow(companyId, WorkPeriod.Of(new LedgerPeriod(2026, 9)));

            selfNotes.Add(db.Periods.Window(companyId).IsMonth
                ? "" : "работният период не се помни между отварянията");

            using var page = new StockPage(service, db);

            page.UseDate(new DateOnly(2026, 9, 30));
            page.UseStore(StoreKind.Received);
            page.SelectMaster(0);

            var chosen = page.Selection();

            using (var dialog = new TreatDialog(
                       service, db, companyId, "1", chosen, new DateOnly(2026, 9, 30)))
            {
                // В периода: нищо не се пита и отметката я няма.
                selfNotes.Add(!dialog.OutsideOffered
                    ? "" : "отметката стои и когато датата е в периода");
                selfNotes.Add(dialog.WhenText.Contains("Записва се", StringComparison.Ordinal)
                    ? "" : "в периода се казва нещо друго: " + dialog.WhenText);
                selfNotes.Add(dialog.Blocking().All(p => !p.Contains("работния период", StringComparison.Ordinal))
                    ? "" : "дата в периода спира записа");

                // Първите дни на новия месец — точно случаят, заради който го има.
                dialog.UseDate(new DateOnly(2026, 10, 2));

                selfNotes.Add(dialog.OutsideOffered ? "" : "извън периода отметката я няма");
                selfNotes.Add(dialog.WhenText.Contains("извън работния период", StringComparison.Ordinal)
                    ? "" : "не се казва, че датата е извън периода: " + dialog.WhenText);

                // И докъде стига периодът — иначе човек вижда само името му.
                selfNotes.Add(dialog.WhenText.Contains("01.09.2026", StringComparison.Ordinal) &&
                              dialog.WhenText.Contains("30.09.2026", StringComparison.Ordinal)
                    ? "" : "не се казват границите на периода: " + dialog.WhenText);

                // По-дългото съобщение не бива да се реже — картата расте за него.
                selfNotes.Add(dialog.WhenFits ? "" : "съобщението за периода не се побира");
                selfNotes.Add(dialog.Blocking().Any(p => p.Contains("извън работния период", StringComparison.Ordinal))
                    ? "" : "дата извън периода минава");

                // Но правилото е негово и той го оттегля.
                dialog.UseOutside(true);

                selfNotes.Add(dialog.Blocking().All(p => !p.Contains("извън работния период", StringComparison.Ordinal))
                    ? "" : "вдигнатата отметка не отваря периода");
                selfNotes.Add(dialog.WhenText.Contains("въпреки това", StringComparison.Ordinal)
                    ? "" : "оттеглянето не се казва: " + dialog.WhenText);
                selfNotes.Add(dialog.WhenText.Contains("30.09.2026", StringComparison.Ordinal)
                    ? "" : "оттегленото съобщение губи границите: " + dialog.WhenText);
                selfNotes.Add(dialog.WhenFits ? "" : "оттегленото съобщение не се побира");

                // Връщането в периода сваля отметката само: иначе тя остава
                // вдигната по навик и правилото спира да пази.
                dialog.UseDate(new DateOnly(2026, 9, 20));

                selfNotes.Add(!dialog.OutsideOffered ? "" : "отметката остава след връщане в периода");

                // Затвореният месец е по-силното: него отметка не отваря.
                db.Periods.Close(companyId, "1", new LedgerPeriod(2026, 9));
                dialog.UseDate(new DateOnly(2026, 9, 21));

                selfNotes.Add(dialog.Blocking().Any(p => p.Contains("затворен", StringComparison.Ordinal))
                    ? "" : "затворен месец минава");

                db.Periods.Reopen(companyId, "1", new LedgerPeriod(2026, 9));
            }

            // --- дневникът --------------------------------------------------

            selfNotes.Add(db.Actions.Count(companyId, "1") == 0
                ? "" : "дневникът не тръгва празен");

            // Едно вярно третиране и едно с СГРЕШЕНА дата — октомври вместо
            // септември. Точно това е случаят: числото е вярно, месецът — не.
            var piles = Treatment.Piles(chosen);

            page.Apply(Treatment.Plan([new TreatOrder(piles[0], 5m)]), new DateOnly(2026, 9, 20));
            page.Apply(Treatment.Plan([new TreatOrder(piles[0], 3m)]), new DateOnly(2026, 10, 2));

            var entries = db.Actions.List(companyId, "1");

            selfNotes.Add(entries.Count == 2 ? "" : $"дневникът помни {entries.Count} реда вместо два");
            selfNotes.Add(db.Actions.Count(companyId, "1") == 2 ? "" : "броенето не съвпада със списъка");

            // Най-новото отгоре: сбърканото току-що се търси най-често.
            var wrong = entries[0];

            selfNotes.Add(wrong.Date == new DateOnly(2026, 10, 2)
                ? "" : $"най-новото не е отгоре: {wrong.Date}");
            selfNotes.Add(wrong.Period == new LedgerPeriod(2026, 10)
                ? "" : $"редът не помни в кой месец е паднал: {wrong.Period}");
            selfNotes.Add(wrong.Where == "октомври 2026" ? "" : "месецът не се чете с име: " + wrong.Where);
            selfNotes.Add(wrong.Kind == MoveKind.Treated ? "" : "видът на движението не се помни");
            selfNotes.Add(wrong.Amount == 3m ? "" : $"количеството е {wrong.Amount} вместо три");
            selfNotes.Add(wrong.Alive ? "" : "току-що записаното се води махнато");

            // Оттук се поправя: движението си отива, редът остава и КАЗВА го.
            db.Writer.Remove(companyId, wrong.MoveId);

            var after = db.Actions.List(companyId, "1");

            selfNotes.Add(after.Count == 2 ? "" : "махането на движение изяде реда от дневника");
            selfNotes.Add(!after[0].Alive ? "" : "махнатото движение още се води живо");
            selfNotes.Add(after[1].Alive ? "" : "махането на едното повлия на другото");

            // Дневникът е за изхвърляне: изчистен, той не пипа склада.
            var before = db.Ledger.Read(companyId, "1").Count;

            db.Actions.Clear(companyId, "1");

            selfNotes.Add(db.Actions.Count(companyId, "1") == 0 ? "" : "дневникът не се изчиства");
            selfNotes.Add(db.Ledger.Read(companyId, "1").Count == before
                ? "" : "изчистването на дневника е махнало движения");

            // И прозорчето го показва, както го показва „Внесени файлове“.
            page.Apply(Treatment.Plan([new TreatOrder(piles[0], 2m)]), new DateOnly(2026, 10, 5));

            using (var log = new ActionLogDialog(db, companyId, "1"))
            {
                selfNotes.Add(log.RowCount == 1 ? "" : $"дневникът показва {log.RowCount} реда вместо един");
                selfNotes.Add(log.PeriodAt(0) == "октомври 2026"
                    ? "" : "месецът не се показва: " + log.PeriodAt(0));
                selfNotes.Add(log.StateAt(0) == "записано" ? "" : "състоянието не се показва: " + log.StateAt(0));
                selfNotes.Add(log.SummaryText.Contains("1 въведени", StringComparison.Ordinal)
                    ? "" : "редът отдолу не брои: " + log.SummaryText);

                log.Pick(0);
                selfNotes.Add(log.CanRemove ? "" : "избран жив ред не се маха");
                selfNotes.Add(log.CanClear ? "" : "пълен дневник не се изчиства");

                log.ClearNow();

                selfNotes.Add(log.RowCount == 0 ? "" : "изчистеният дневник още показва редове");
                selfNotes.Add(!log.CanClear ? "" : "празен дневник предлага изчистване");
            }
        }
        catch (Exception ex)
        {
            selfError = ex.ToString();
        }
    });

    selfThread.SetApartmentState(ApartmentState.STA);
    selfThread.Start();
    selfThread.Join();

    if (selfError is not null) Console.WriteLine("     " + selfError);

    var selfProblems = selfNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in selfProblems) Console.WriteLine("     " + problem);

    Check("самоконтролът и дневникът минават без изключение", selfError is null);
    Check($"направени са всичките {selfNotes.Count} проверки", selfNotes.Count == 37,
        $"{selfNotes.Count}");
    Check("периодът пази, а дневникът намира сбърканото", selfProblems.Count == 0,
        $"{selfProblems.Count} проблема");
}



// =============================================== Получателите се научават сами
//
// Името на получателя е СВОБОДЕН текст по схемата и такъв остава: НИСО не дава
// номенклатура. Но същото поле, писано на ръка по трийсет пъти месечно, ражда
// „Претопи ООД“, „Претопи OOD“ и „претопи оод“ — трима получателя вместо един,
// и нито едно търсене не ги събира.
//
// Затова написаното веднъж се помни и се предлага втория път — но НЕ ограничава:
// непознато име минава и се научава (правило 13).
Console.WriteLine();
Console.WriteLine("### ТЕСТ - получателите се помнят от самите предавания");
{
    var whoNotes = new List<string>();
    string? whoError = null;

    var whoThread = new Thread(() =>
    {
        try
        {
            var whoPath = Path.Combine(work, $"who_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(whoPath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");

            whoNotes.Add(db.Partners.Count(companyId) == 0 ? "" : "списъкът не тръгва празен");

            // Записано предаване вписва получателя си.
            db.Partners.Remember(companyId, "Претопи ООД", "205566778");

            var known = db.Partners.List(companyId);

            whoNotes.Add(known.Count == 1 ? "" : $"запомнени са {known.Count} вместо един");
            whoNotes.Add(known[0].Name == "Претопи ООД" ? "" : "името не се помни: " + known[0].Name);
            whoNotes.Add(known[0].Eik == "205566778" ? "" : "ЕИК-ът не се помни: " + known[0].Eik);
            whoNotes.Add(known[0].Used == 1 ? "" : $"ползването е {known[0].Used} вместо едно");
            whoNotes.Add(known[0].Display == "Претопи ООД — 205566778"
                ? "" : "не се показва с ЕИК: " + known[0].Display);

            // Втори път същият не прави втори запис — той брои.
            db.Partners.Remember(companyId, "Претопи ООД", "205566778");
            db.Partners.Remember(companyId, "Претопи ООД", "205566778");

            known = db.Partners.List(companyId);

            whoNotes.Add(known.Count == 1 ? "" : "същият получател се вписва втори път");
            whoNotes.Add(known[0].Used == 3 ? "" : $"ползванията са {known[0].Used} вместо три");

            // Най-ползваните отгоре: трима редовни и петнайсет по веднъж.
            db.Partners.Remember(companyId, "Вторсуровини АД", "111222333");

            known = db.Partners.List(companyId);

            whoNotes.Add(known[0].Name == "Претопи ООД"
                ? "" : "най-ползваният не е отгоре: " + known[0].Name);

            // Празно име не се помни: получател без име е липсващо поле.
            db.Partners.Remember(companyId, "   ", "999");

            whoNotes.Add(db.Partners.Count(companyId) == 2 ? "" : "празно име се вписва в списъка");

            // Вписаният на ръка чака, без да се брои за ползван — иначе би се
            // изкачил пред истинските.
            whoNotes.Add(db.Partners.Add(companyId, "Рециклаж ЕООД", "444555666")
                ? "" : "вписването на ръка не става");
            whoNotes.Add(!db.Partners.Add(companyId, "Рециклаж ЕООД", "444555666")
                ? "" : "същият се вписва два пъти");

            var fresh = db.Partners.List(companyId).First(x => x.Name == "Рециклаж ЕООД");

            whoNotes.Add(fresh.Fresh ? "" : "вписаният на ръка се брои за ползван");
            whoNotes.Add(db.Partners.List(companyId)[^1].Name == "Рециклаж ЕООД"
                ? "" : "неползваният не е най-отдолу");

            // ЕИК-ът се намира по име — оттам полето се попълва само.
            whoNotes.Add(db.Partners.EikOf(companyId, "Претопи ООД") == "205566778"
                ? "" : "ЕИК-ът не се намира по име");
            whoNotes.Add(db.Partners.EikOf(companyId, "претопи оод") == "205566778"
                ? "" : "малките букви не намират същия получател");
            whoNotes.Add(db.Partners.EikOf(companyId, "Непознат") == ""
                ? "" : "непознат получател връща ЕИК");

            // Поправката на сгрешен ЕИК е самата причина за екрана.
            db.Partners.Update(fresh.Id, "Рециклаж ЕООД", "444555667");

            whoNotes.Add(db.Partners.EikOf(companyId, "Рециклаж ЕООД") == "444555667"
                ? "" : "поправеният ЕИК не се помни");

            // Менюто показва „име — ЕИК“, а в книгата отива само името.
            whoNotes.Add(PartnerStore.NameOf("Претопи ООД — 205566778") == "Претопи ООД"
                ? "" : "името не се отделя от ЕИК-а");
            whoNotes.Add(PartnerStore.NameOf("Претопи ООД") == "Претопи ООД"
                ? "" : "име без ЕИК се реже");

            // --- и на екрана ------------------------------------------------

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 1), "150101", 10m,
                Eik: "203344556", Partner: "Търговец ООД"));

            using var page = new StockPage(service, db);

            page.UseDate(new DateOnly(2026, 9, 30));
            page.UseStore(StoreKind.Received);
            page.SelectMaster(0);

            using (var dialog = new SubmitDialog(
                       service, db, companyId, "1", page.Selection(),
                       new DateOnly(2026, 9, 30), direct: true))
            {
                whoNotes.Add(dialog.ReceiversOffered == 3
                    ? "" : $"полето предлага {dialog.ReceiversOffered} получателя вместо три");
                whoNotes.Add(dialog.ReceiverAt(0) == "Претопи ООД — 205566778"
                    ? "" : "най-ползваният не се предлага пръв: " + dialog.ReceiverAt(0));

                // Изборът носи и ЕИК-а — той се пише веднъж и после не се преписва.
                whoNotes.Add(dialog.UseReceiver(0) ? "" : "получател от менюто не се избира");
                whoNotes.Add(dialog.FieldText("name_receiver") == "Претопи ООД"
                    ? "" : "в полето остава и ЕИК-ът: " + dialog.FieldText("name_receiver"));
                whoNotes.Add(dialog.FieldText("pin_receiver") == "205566778"
                    ? "" : "ЕИК-ът не се попълва сам: " + dialog.FieldText("pin_receiver"));

                // Непознат минава: списъкът предлага, не ограничава.
                dialog.TypeReceiver("Нов Оползотворител ЕООД");
                dialog.UseField("pin_receiver", "777888999");

                whoNotes.Add(dialog.Blocking().All(p => !p.Contains("Име на получател", StringComparison.Ordinal))
                    ? "" : "непознат получател спира записа");

                // Проверката „какво спира записа“ е ВЪПРОС: тя не бива да
                // научава нищо. Научаването става при самия запис.
                whoNotes.Add(db.Partners.Count(companyId) == 3
                    ? "" : "питането какво спира записа вписа получател");

                // Записът минава по пътя на копчето — там се научава.
                page.ApplySubmitted(
                    dialog.Planned(), new DateOnly(2026, 9, 20),
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["name_receiver"] = "Нов Оползотворител ЕООД",
                        ["pin_receiver"] = "777888999"
                    },
                    string.Empty, direct: true);
            }

            whoNotes.Add(db.Partners.EikOf(companyId, "Нов Оползотворител ЕООД") == "777888999"
                ? "" : "новият получател не се научи при записа");
            whoNotes.Add(db.Partners.Count(companyId) == 4
                ? "" : $"списъкът стана {db.Partners.Count(companyId)} вместо четири");

            // Екранът за получатели показва същото.
            using (var who = new ReceiversDialog(db, companyId))
            {
                whoNotes.Add(who.RowCount == 4 ? "" : $"екранът показва {who.RowCount} реда вместо четири");
                whoNotes.Add(who.NameAt(0) == "Претопи ООД" ? "" : "подредбата не е по ползване");
                whoNotes.Add(who.EikAt(0) == "205566778" ? "" : "ЕИК-ът не се показва");
                whoNotes.Add(who.UsedAt(0) == "3" ? "" : "ползванията не се показват: " + who.UsedAt(0));
                whoNotes.Add(who.SummaryText.Contains("4", StringComparison.Ordinal)
                    ? "" : "редът отдолу не брои: " + who.SummaryText);

                whoNotes.Add(!who.AddNow("Претопи ООД", "205566778")
                    ? "" : "същият получател се вписва втори път от екрана");
                whoNotes.Add(who.AddNow("Депо АД", "121212121")
                    ? "" : "нов получател не се вписва от екрана");
                whoNotes.Add(who.RowCount == 5 ? "" : "вписаният не се появява в списъка");
            }
        }
        catch (Exception ex)
        {
            whoError = ex.ToString();
        }
    });

    whoThread.SetApartmentState(ApartmentState.STA);
    whoThread.Start();
    whoThread.Join();

    if (whoError is not null) Console.WriteLine("     " + whoError);

    var whoProblems = whoNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in whoProblems) Console.WriteLine("     " + problem);

    Check("получателите минават без изключение", whoError is null);
    Check($"направени са всичките {whoNotes.Count} проверки", whoNotes.Count == 37,
        $"{whoNotes.Count}");
    Check("списъкът се научава, без да ограничава", whoProblems.Count == 0,
        $"{whoProblems.Count} проблема");
}



// =============================================== „По получател“ в летописа
//
// Третият разрез обръща същите числа за трети път: „по код“ казва какво е
// излязло, „по клиент“ — чие е било, „по получател“ — на кого е отишло.
// Последният е въпросът, който идва отвън: оползотворителят се обажда и пита
// колко сме му предали.
//
// Заради него редовете в летописа не се събират по (код, партида): един и същ
// код от една и съща партида може да е излязъл към трима души.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - предаденото се гледа и откъм получателя");
{
    var toNotes = new List<string>();
    string? toError = null;

    var toThread = new Thread(() =>
    {
        try
        {
            var toPath = Path.Combine(work, $"to_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(toPath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 1), "150101", 30m,
                Eik: "204455667", Partner: "Търговец ООД"));

            // Едно и също — една партида, един код — но към ДВАМА получателя.
            static Dictionary<string, string> To(string name, string eik) =>
                new(StringComparer.Ordinal)
                {
                    ["name_receiver"] = name,
                    ["pin_receiver"] = eik,
                    ["op_descr"] = "Оползотворяване"
                };

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Submitted, new DateOnly(2026, 9, 10), "150101", 12m,
                Direct: true, Fields: To("Претопи ООД", "205566778")));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Submitted, new DateOnly(2026, 9, 12), "150101", 8m,
                Direct: true, Fields: To("Вторсуровини АД", "111222333")));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Submitted, new DateOnly(2026, 9, 14), "150101", 5m,
                Direct: true, Fields: To("Претопи ООД", "205566778")));

            using var page = new StockPage(service, db);

            page.UseDate(new DateOnly(2026, 9, 30));

            // Разрезът се появява само в летописа.
            page.UseStore(StoreKind.Received);

            toNotes.Add(!page.ReceiversOffered ? "" : "получателите се предлагат и в „Получен“");
            toNotes.Add(page.ViewCount == 2 ? "" : $"„Получен“ дава {page.ViewCount} разреза вместо два");
            toNotes.Add(!page.DetailReceiverShown ? "" : "стълбът за получател стои и в „Получен“");

            page.UseStore(StoreKind.Submitted);

            toNotes.Add(page.ReceiversOffered ? "" : "получателите не се предлагат в „Предаден“");
            toNotes.Add(page.ViewCount == 4 ? "" : $"„Предаден“ дава {page.ViewCount} разреза вместо четири");
            toNotes.Add(page.DetailReceiverShown ? "" : "стълбът за получател го няма в „Предаден“");

            // По код: един ред, 25 тона — сборът не се мени от разреза.
            toNotes.Add(page.RowCount == 1 ? "" : $"„по код“ дава {page.RowCount} реда вместо един");

            page.Pick(0);

            // Долу един ред е ЕДНО предаване. В един месец към един и същ
            // получател тръгват няколко курса и „кога и по колко“ е самият
            // въпрос — трите предавания са три реда, не двама получателя.
            toNotes.Add(page.DetailCount == 3
                ? "" : $"долу стоят {page.DetailCount} реда вместо три предавания");

            var receivers = new[]
            {
                page.DetailReceiverAt(0), page.DetailReceiverAt(1), page.DetailReceiverAt(2)
            };

            toNotes.Add(receivers.Any(r => r.Contains("Претопи", StringComparison.Ordinal))
                ? "" : "получателят не се показва долу: " + string.Join(" | ", receivers));
            toNotes.Add(receivers.Any(r => r.Contains("205566778", StringComparison.Ordinal))
                ? "" : "ЕИК-ът на получателя не се показва");
            toNotes.Add(receivers.Count(r => r.Contains("Претопи", StringComparison.Ordinal)) == 2
                ? "" : "двата курса към един получател се събраха в един ред");

            // Без избрана колона отгоре стои НАЙ-НОВОТО предаване: питането е
            // „какво излезе последно“.
            toNotes.Add(page.DetailHandedAt(0) == "14.09.2026"
                ? "" : "най-новото предаване не е отгоре: " + page.DetailHandedAt(0));
            toNotes.Add(page.DetailHandedAt(2) == "10.09.2026"
                ? "" : "най-старото предаване не е отдолу: " + page.DetailHandedAt(2));

            // Сборът не се мени от разбиването.
            toNotes.Add(page.DetailAmountAt(0) == "5,000"
                ? "" : "редът не носи количеството на своето предаване: " + page.DetailAmountAt(0));

            // По получател: двама, а Претопи е с 17 от 25.
            page.UseReceiverView();

            toNotes.Add(page.RowCount == 2
                ? "" : $"„по получател“ дава {page.RowCount} реда вместо двама");
            toNotes.Add(page.SummaryText.Contains("25,000", StringComparison.Ordinal)
                ? "" : "сборът се мени от разреза: " + page.SummaryText);

            page.SelectMaster(0);

            toNotes.Add(page.Selection().Sum(x => x.Amount) == 17m
                ? "" : $"най-големият получател е с {page.Selection().Sum(x => x.Amount)} вместо 17");

            // Ситото по получател: 8 тона остават.
            page.FilterReceivers("111222333");

            toNotes.Add(page.RowCount == 1 ? "" : $"ситото остави {page.RowCount} реда вместо един");
            toNotes.Add(page.SummaryText.Contains("8,000", StringComparison.Ordinal)
                ? "" : "ситото не свива сбора: " + page.SummaryText);
            toNotes.Add(page.ReceiverFilterText.Contains("Вторсуровини", StringComparison.Ordinal)
                ? "" : "копчето не казва по кого пресява: " + page.ReceiverFilterText);

            page.FilterReceivers();

            toNotes.Add(page.RowCount == 2 ? "" : "махнатото сито не връща всички");

            // Смяната на склада маха скритото сито: иначе таблицата излиза
            // празна и не се вижда защо.
            page.FilterReceivers("111222333");
            page.UseStore(StoreKind.Received);

            toNotes.Add(page.ReceiverFilterText.Contains("всички", StringComparison.Ordinal)
                ? "" : "скритото сито остава включено: " + page.ReceiverFilterText);
        }
        catch (Exception ex)
        {
            toError = ex.ToString();
        }
    });

    toThread.SetApartmentState(ApartmentState.STA);
    toThread.Start();
    toThread.Join();

    if (toError is not null) Console.WriteLine("     " + toError);

    var toProblems = toNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in toProblems) Console.WriteLine("     " + problem);

    Check("разрезът по получател минава без изключение", toError is null);
    Check($"направени са всичките {toNotes.Count} проверки", toNotes.Count == 22, $"{toNotes.Count}");
    Check("предаденото се чете и откъм получателя", toProblems.Count == 0,
        $"{toProblems.Count} проблема");
}



// =============================================== Лентата се пренася, не се реже
//
// Намерено на снимка от потребителя: филтрите бяха закачени отдясно наляво с
// постоянни ширини. Съберат ли се — изглежда добре; не се ли съберат, а на
// лаптоп и при мащаб 125 % не се събират — последните получаваха нула ширина и
// „Изчисти“ се рисуваше ВЪРХУ търсенето.
//
// Контрола, която я няма, не е филтър: човек я търси и не я намира. Затова
// лентата се ПРЕНАСЯ на втори ред и нито едно поле не изчезва.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - филтрите се пренасят, вместо да се режат");
{
    var barNotes = new List<string>();
    string? barError = null;

    var barThread = new Thread(() =>
    {
        try
        {
            var barPath = Path.Combine(work, $"bar_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(barPath);

            db.Companies.Add("203344556", "Метал ООД");

            using var host = new Form { ClientSize = new Size(1600, 900) };
            using var page = new StockPage(service, db);

            page.Dock = DockStyle.Fill;
            host.Controls.Add(page);

            _ = host.Handle;

            // Летописът е най-тесният случай: там филтрите са девет, а не шест.
            page.UseStore(StoreKind.Submitted);

            barNotes.Add(page.FilterCount == 9
                ? "" : $"в летописа стоят {page.FilterCount} филтъра вместо девет");

            // Широк прозорец: всичко на един ред и нищо отрязано.
            host.ClientSize = new Size(1700, 900);
            Application.DoEvents();

            barNotes.Add(page.FiltersFit ? "" : "на широк прозорец филтър излиза извън лентата");
            barNotes.Add(page.FilterRows == 1
                ? "" : $"на широк прозорец лентата е на {page.FilterRows} реда");

            // Тесен прозорец: пренася се, но пак нищо не изчезва и нищо не се реже.
            host.ClientSize = new Size(1100, 900);
            Application.DoEvents();

            barNotes.Add(page.FiltersFit ? "" : "на тесен прозорец филтър излиза извън лентата");
            barNotes.Add(page.FilterRows >= 2
                ? "" : "на тесен прозорец лентата не се пренася");
            barNotes.Add(page.FilterCount == 9
                ? "" : $"при пренасяне останаха {page.FilterCount} филтъра вместо девет");

            // И на съвсем тесен: пак се вижда всичко, на повече редове.
            host.ClientSize = new Size(900, 900);
            Application.DoEvents();

            barNotes.Add(page.FiltersFit ? "" : "на съвсем тесен прозорец филтър се реже");
            barNotes.Add(page.FilterCount == 9 ? "" : "на съвсем тесен прозорец филтър изчезва");

            // Извън летописа „Получател“ го няма: там нищо не е излязло.
            page.UseStore(StoreKind.Received);
            host.ClientSize = new Size(1700, 900);
            Application.DoEvents();

            barNotes.Add(page.FilterCount == 6
                ? "" : $"извън летописа стоят {page.FilterCount} филтъра вместо шест");
            barNotes.Add(page.FiltersFit ? "" : "извън летописа филтър излиза извън лентата");
        }
        catch (Exception ex)
        {
            barError = ex.ToString();
        }
    });

    barThread.SetApartmentState(ApartmentState.STA);
    barThread.Start();
    barThread.Join();

    if (barError is not null) Console.WriteLine("     " + barError);

    var barProblems = barNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in barProblems) Console.WriteLine("     " + problem);

    Check("лентата се сглобява без изключение", barError is null);
    Check($"направени са всичките {barNotes.Count} проверки", barNotes.Count == 10, $"{barNotes.Count}");
    Check("нито един филтър не се реже и не изчезва", barProblems.Count == 0,
        $"{barProblems.Count} проблема" + (barProblems.Count > 0 ? ": " + string.Join(" | ", barProblems.Take(5)) : ""));
}



// =============================================== „Само този месец“
//
// Складът отговаря на „какво СТОИ към 30.09“. Понякога въпросът е друг: „какво
// се случи ПРЕЗ септември“. Едно и също число под едно и също име би значело
// две различни неща, затова с отметката се мени и заглавието на стълба — а
// движението може да е и отрицателно.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - складът показва и движението за месеца");
{
    var onlyNotes = new List<string>();
    string? onlyError = null;

    var onlyThread = new Thread(() =>
    {
        try
        {
            var onlyPath = Path.Combine(work, $"only_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(onlyPath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");

            // Август: 10 т влизат. Септември: още 6 влизат и 4 излизат.
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 8, 10), "150101", 10m,
                Eik: "204455667", Partner: "Търговец ООД"));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 5), "150101", 6m,
                Eik: "204455667", Partner: "Търговец ООД"));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Submitted, new DateOnly(2026, 9, 20), "150101", 4m, Direct: true,
                Fields: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["name_receiver"] = "Претопи ООД",
                    ["pin_receiver"] = "205566778"
                }));

            using var page = new StockPage(service, db);

            page.UseDate(new DateOnly(2026, 9, 30));
            page.UseStore(StoreKind.Received);

            // Изключена: наличност — 10 + 6 − 4 = 12.
            onlyNotes.Add(!page.MonthOnlyOn ? "" : "отметката тръгва вдигната");
            onlyNotes.Add(page.MonthOnlyOffered ? "" : "отметката не се предлага в „Получен“");

            page.SelectMaster(0);

            onlyNotes.Add(page.Selection().Sum(x => x.Amount) == 12m
                ? "" : $"наличността е {page.Selection().Sum(x => x.Amount)} вместо 12");
            onlyNotes.Add(page.AmountHeaderText.Contains("Налично", StringComparison.Ordinal)
                ? "" : "стълбът не се казва „Налично“: " + page.AmountHeaderText);

            // Вдигната: движението за септември — 6 влезли минус 4 излезли = 2.
            page.UseMonthOnly(true);
            page.SelectMaster(0);

            onlyNotes.Add(page.Selection().Sum(x => x.Amount) == 2m
                ? "" : $"движението за месеца е {page.Selection().Sum(x => x.Amount)} вместо 2");
            onlyNotes.Add(page.AmountHeaderText.Contains("Движение", StringComparison.Ordinal)
                ? "" : "стълбът не се преименува на „Движение“: " + page.AmountHeaderText);

            // Август си е август: 10 влезли, нищо излязло.
            page.UseDate(new DateOnly(2026, 8, 31));
            page.SelectMaster(0);

            onlyNotes.Add(page.Selection().Sum(x => x.Amount) == 10m
                ? "" : $"августовското движение е {page.Selection().Sum(x => x.Amount)} вместо 10");

            // И в летописа: предадено ПРЕЗ месеца, а не до датата.
            page.UseDate(new DateOnly(2026, 9, 30));
            page.UseStore(StoreKind.Submitted);
            page.UseMonthOnly(false);

            onlyNotes.Add(page.SummaryText.Contains("4,000", StringComparison.Ordinal)
                ? "" : "летописът не брои предаденото: " + page.SummaryText);

            page.UseMonthOnly(true);

            onlyNotes.Add(page.SummaryText.Contains("4,000", StringComparison.Ordinal)
                ? "" : "септемврийското предаване изчезна при ограничаване до месеца");

            page.UseDate(new DateOnly(2026, 8, 31));

            onlyNotes.Add(page.RowCount == 0
                ? "" : $"през август е предадено нещо: {page.RowCount} реда");

            // Склад „Третиран“ се смята по лотове и не се реже по месеци —
            // отметката там е угасена, а не мълчаливо пренебрегната.
            page.UseStore(StoreKind.Treated);

            onlyNotes.Add(!page.MonthOnlyOffered
                ? "" : "отметката се предлага и в „Третиран“, където не важи");
        }
        catch (Exception ex)
        {
            onlyError = ex.ToString();
        }
    });

    onlyThread.SetApartmentState(ApartmentState.STA);
    onlyThread.Start();
    onlyThread.Join();

    if (onlyError is not null) Console.WriteLine("     " + onlyError);

    var onlyProblems = onlyNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in onlyProblems) Console.WriteLine("     " + problem);

    Check("отметката за месеца минава без изключение", onlyError is null);
    Check($"направени са всичките {onlyNotes.Count} проверки", onlyNotes.Count == 11,
        $"{onlyNotes.Count}");
    Check("складът показва и наличност, и движение", onlyProblems.Count == 0,
        $"{onlyProblems.Count} проблема");
}


// =============================================== Преглед преди внасяне
//
// Чуждият дневник се изнася цял и в него редовно има редове, които тук нямат
// работа. Влязат ли в таблицата, те вече са се смесили с останалите и се търсят
// като игла. Прозорчето стои МЕЖДУ файла и таблицата.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - прегледът отсява, преди файлът да влезе");
{
    var seeNotes = new List<string>();
    string? seeError = null;

    var seeThread = new Thread(() =>
    {
        try
        {
            static ImportedMove Move(int line, MoveKind kind, int day, string ewc, decimal amount,
                string partner, string document) =>
                new(line, kind, new DateOnly(2026, day > 30 ? 10 : 9, day > 30 ? day - 30 : day),
                    ewc, amount, "1", "204455667", partner, document, string.Empty, false,
                    new Dictionary<string, string>(StringComparer.Ordinal));

            var moves = new List<ImportedMove>
            {
                Move(3, MoveKind.Received, 2, "150101", 12m, "Метал ООД", "ППП 101"),
                Move(4, MoveKind.Received, 9, "150101", 8m, "Скрап ЕООД", "ППП 102"),
                Move(5, MoveKind.Received, 35, "160216", 5m, "Метал ООД", "ППП 110"),
                Move(6, MoveKind.Treated, 12, "150101", 20m, string.Empty, string.Empty)
            };

            using var dialog = new ImportPreviewDialog(
                "demetra.xlsx", moves, MoveKind.Received, problems: 2);

            _ = dialog.Handle;

            // Отваря се на вида на таблицата: точно тези редове ще влязат в нея.
            seeNotes.Add(dialog.RowCount == 3
                ? "" : $"прегледът показва {dialog.RowCount} реда вместо трите приемания");
            seeNotes.Add(dialog.SummaryText.Contains("4 реда", StringComparison.Ordinal)
                ? "" : "не се казва колко носи файлът: " + dialog.SummaryText);
            seeNotes.Add(dialog.SummaryText.Contains("25,000", StringComparison.Ordinal)
                ? "" : "не се казва колко тона ще влязат: " + dialog.SummaryText);
            seeNotes.Add(dialog.SummaryText.Contains("неразчетени", StringComparison.Ordinal)
                ? "" : "не се казва колко реда не са разчетени: " + dialog.SummaryText);

            // Всички видове: четирите реда.
            dialog.UseKind(null);
            seeNotes.Add(dialog.RowCount == 4 ? "" : $"„всички видове“ дава {dialog.RowCount} реда");

            dialog.UseKind(MoveKind.Received);

            // Подредбата по заглавие — маркерът често е количеството.
            dialog.SortBy(ImportPreviewDialog.AmountColumnName);
            seeNotes.Add(dialog.AmountAt(0) == "12,000"
                ? "" : "по количество отгоре не е най-голямото: " + dialog.AmountAt(0));

            dialog.SortBy(ImportPreviewDialog.AmountColumnName);
            seeNotes.Add(dialog.AmountAt(0) == "5,000"
                ? "" : "посоката не се обръща: " + dialog.AmountAt(0));

            // По дата: редът от октомври е този, който няма работа тук.
            dialog.SortBy(ImportPreviewDialog.DateColumnName);
            seeNotes.Add(dialog.DateAt(0) == "05.10.2026"
                ? "" : "по дата отгоре не е най-новото: " + dialog.DateAt(0));

            // Ситото по месец го намира сам.
            dialog.UseMonth(new LedgerPeriod(2026, 10));
            seeNotes.Add(dialog.RowCount == 1
                ? "" : $"ситото по месец остави {dialog.RowCount} реда вместо един");

            // И го маха. Махнатото се задрасква, а не изчезва: изчезнал ред не
            // може да бъде върнат.
            dialog.DropRows(0);

            seeNotes.Add(dialog.DroppedCount == 1 ? "" : "махането не се отбелязва");
            seeNotes.Add(dialog.RowCount == 1 ? "" : "махнатият ред изчезна от списъка");
            seeNotes.Add(dialog.Kept.Count == 3
                ? "" : $"нататък тръгват {dialog.Kept.Count} реда вместо три");
            seeNotes.Add(dialog.Kept.All(m => m.Line != 5)
                ? "" : "махнатият ред пак тръгва нататък");
            seeNotes.Add(dialog.SummaryText.Contains("махнати 1", StringComparison.Ordinal)
                ? "" : "не се казва колко са махнати: " + dialog.SummaryText);

            // Търсенето хваща и код, и контрагент, и документ.
            dialog.UseMonth(null);
            dialog.UseKind(null);
            dialog.UseSearch("Скрап");

            seeNotes.Add(dialog.RowCount == 1 ? "" : $"търсенето по контрагент дава {dialog.RowCount}");

            dialog.UseSearch("ППП 101");
            seeNotes.Add(dialog.RowCount == 1 ? "" : $"търсенето по документ дава {dialog.RowCount}");

            dialog.UseSearch("150101");
            seeNotes.Add(dialog.RowCount == 3 ? "" : $"търсенето по код дава {dialog.RowCount}");

            dialog.UseSearch(string.Empty);

            // Сгрешеното махане не бива да иска нов файл.
            dialog.RestoreAll();

            seeNotes.Add(dialog.DroppedCount == 0 ? "" : "върнатите не се връщат");
            seeNotes.Add(dialog.Kept.Count == 4 ? "" : "след връщането не тръгват всички");
        }
        catch (Exception ex)
        {
            seeError = ex.ToString();
        }
    });

    seeThread.SetApartmentState(ApartmentState.STA);
    seeThread.Start();
    seeThread.Join();

    if (seeError is not null) Console.WriteLine("     " + seeError);

    var seeProblems = seeNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in seeProblems) Console.WriteLine("     " + problem);

    Check("прегледът минава без изключение", seeError is null);
    Check($"направени са всичките {seeNotes.Count} проверки", seeNotes.Count == 19, $"{seeNotes.Count}");
    Check("прегледът отсява преди внасянето", seeProblems.Count == 0, $"{seeProblems.Count} проблема");
}



// =============================================== Внася се приемане, не сметки
//
// Приемането е вратата на площадката: то идва ОТВЪН и няма как да бъде сметнато
// отвътре. Третирането, образуването и предаването се РАЖДАТ тук — от избрани
// купчини, с посочени дялове по партиди, с произход, който минава през цялата
// верига. Внесени отвън, те носят количество, но не изчерпват партида и не се
// виждат в пътя ѝ, а двете сметки се разминават тихо.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - внасят се само приемания, освен ако не се отключи");
{
    var lockNotes = new List<string>();

    // Приемането не се заключва по устройство, не по подразбиране.
    lockNotes.Add(ImportPolicy.Always(MoveKind.Received) ? "" : "приемането може да се заключи");

    foreach (var kind in new[] { MoveKind.Treated, MoveKind.Generated, MoveKind.Submitted })
        lockNotes.Add(!ImportPolicy.Always(kind) ? "" : $"{Moves.Title(kind)} е винаги отключено");

    // По подразбиране минава само приемането.
    var plain = ImportPolicy.Default;

    lockNotes.Add(ImportPolicy.Allows(plain, MoveKind.Received) ? "" : "приемането не минава");
    lockNotes.Add(!ImportPolicy.Allows(plain, MoveKind.Treated) ? "" : "третирането минава без отключване");
    lockNotes.Add(!ImportPolicy.Allows(plain, MoveKind.Generated) ? "" : "образуването минава без отключване");
    lockNotes.Add(!ImportPolicy.Allows(plain, MoveKind.Submitted) ? "" : "предаването минава без отключване");

    // Празен списък пак пропуска приемането: то не е в списъка, а над него.
    lockNotes.Add(ImportPolicy.Allows(null, MoveKind.Received)
        ? "" : "без списък приемането спира");
    lockNotes.Add(!ImportPolicy.Allows(null, MoveKind.Treated)
        ? "" : "без списък третирането минава");

    // Записва се и се чете обратно същото.
    var some = ImportPolicy.Text([MoveKind.Received, MoveKind.Treated, MoveKind.Generated]);

    lockNotes.Add(!some.Contains("received", StringComparison.Ordinal)
        ? "" : "приемането се записва в списъка и някой ден може да бъде извадено");

    var back = ImportPolicy.Read(some);

    lockNotes.Add(ImportPolicy.Allows(back, MoveKind.Treated) ? "" : "отключеното не се чете обратно");
    lockNotes.Add(ImportPolicy.Allows(back, MoveKind.Generated) ? "" : "второто отключено се губи");
    lockNotes.Add(!ImportPolicy.Allows(back, MoveKind.Submitted) ? "" : "неотключеното се отключва само");
    lockNotes.Add(ImportPolicy.Allows(ImportPolicy.Read("боклук"), MoveKind.Received)
        ? "" : "нечетимото заключва и приемането");
    lockNotes.Add(!ImportPolicy.Allows(ImportPolicy.Read(null), MoveKind.Treated)
        ? "" : "липсващото отключва всичко");

    // Обяснението казва и кое, и къде се отключва.
    var why = ImportPolicy.Why(MoveKind.Treated);

    lockNotes.Add(why.Contains("Третиран", StringComparison.Ordinal)
        ? "" : "не се казва кое е заключено: " + why);
    lockNotes.Add(why.Contains("Настройки", StringComparison.Ordinal)
        ? "" : "не се казва къде се отключва: " + why);

    var lockProblems = lockNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in lockProblems) Console.WriteLine("     " + problem);

    Check($"направени са всичките {lockNotes.Count} проверки", lockNotes.Count == 18,
        $"{lockNotes.Count}");
    Check("внасянето пуска приемането и спира сметките", lockProblems.Count == 0,
        $"{lockProblems.Count} проблема");
}


// =============================================== Началният месец и шаблонът
//
// Началото се мести със стрелки и завареното върви с него; много редове се
// внасят от шаблон с два листа, по един на склад.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - началото се мести по месеци, а завареното се внася от Excel");
{
    var openNotes = new List<string>();
    string? openError = null;

    var openThread = new Thread(() =>
    {
        try
        {
            var openPath = Path.Combine(work, $"open_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(openPath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");

            db.Setup.SetStart(companyId, new LedgerPeriod(2026, 9));

            using var dialog = new StartStockDialog(service, db, companyId, "1");
            _ = dialog.Handle;

            dialog.Declare(StoreKind.Received, "160216", 10m);
            dialog.Declare(StoreKind.Formed, "191202", 4m);

            // Разделите: всеки показва своя склад.
            dialog.UseTab(StoreKind.Received);
            openNotes.Add(dialog.ShownCount == 1 && dialog.RowCount == 2
                ? "" : $"разделът „Получен“ показва {dialog.ShownCount} от {dialog.RowCount}");

            // Месец назад: завареното отива в деня преди август.
            dialog.StepMonth(-1);

            var eve = new DateOnly(2026, 7, 31);

            openNotes.Add(db.Setup.Start(companyId) == new LedgerPeriod(2026, 8)
                ? "" : $"началото е {db.Setup.Start(companyId)} вместо 2026-08");
            openNotes.Add(db.Ledger.Read(companyId).Where(m => m.Opening).All(m => m.Date == eve)
                ? "" : "движенията на завареното не се преместиха с месеца");
            openNotes.Add(db.Batches.List(companyId).Where(b => b.Opening).All(b => b.ReceivedOn == eve)
                ? "" : "партидите на завареното не се преместиха с месеца");

            // Истинско движение в август спира началото да отиде след него.
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 8, 10), "160216", 2m));

            dialog.StepMonth(1);

            openNotes.Add(dialog.Month == new LedgerPeriod(2026, 8) &&
                          db.Setup.Start(companyId) == new LedgerPeriod(2026, 8)
                ? "" : "началото мина след истинско движение");
            openNotes.Add(dialog.SaysText.Contains("10.08.2026", StringComparison.Ordinal)
                ? "" : "не се казва кое движение спира: " + dialog.SaysText);

            // Шаблонът носи обявеното — по лист на склад.
            var template = Path.Combine(work, $"open_{Guid.NewGuid():N}.xlsx");
            dialog.WriteTemplate(template);

            var back = StockOpeningWorkbook.Read(template);

            openNotes.Add(back.Problems.Count == 0 ? "" : "шаблонът се чете с грешки: " + string.Join(" | ", back.Problems));
            openNotes.Add(back.Rows.Count(r => r.Store == StoreKind.Received) == 1 &&
                          back.Rows.Count(r => r.Store == StoreKind.Formed) == 1
                ? "" : "шаблонът не разделя складовете по листове");
            openNotes.Add(back.Rows.Any(r => r.Store == StoreKind.Received && r.Ewc == "160216" && r.Amount == 10m)
                ? "" : "шаблонът не носи обявеното");

            // Внасянето заменя: два реда в „Получен“, един в „Образуван“.
            StockOpeningFile File(params StockOpeningRow[] rows) => new(rows, []);

            var file = File(
                new StockOpeningRow(3, StoreKind.Received, "160216", 7m),
                new StockOpeningRow(4, StoreKind.Received, "170405", 3m),
                new StockOpeningRow(3, StoreKind.Formed, "191202", 5m));

            dialog.Import(file);

            openNotes.Add(dialog.RowCount == 3 ? "" : $"след внасянето стоят {dialog.RowCount} реда вместо три");
            openNotes.Add(db.Setup.Openings(companyId, "1").Where(o => o.Store == StoreKind.Formed).Sum(o => o.Amount) == 5m
                ? "" : "внасянето добави към „Образуван“, вместо да замени");

            // От което е черпено, остава и не се удвоява при второ внасяне на
            // същия файл.
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Treated, new DateOnly(2026, 8, 15), "160216", 5m));

            dialog.Import(file);

            openNotes.Add(dialog.RowCount == 3 ? "" : $"второто внасяне остави {dialog.RowCount} реда вместо три");
            openNotes.Add(db.Setup.Openings(companyId, "1").Where(o => o.Batch.Ewc == "160216").Sum(o => o.Amount) == 7m
                ? "" : "изчерпаният ред се удвои при второто внасяне");

            // Грешните редове се пропускат с причина, верните влизат.
            var broken = Path.Combine(work, $"open_{Guid.NewGuid():N}.xlsx");

            using (var wb = new ClosedXML.Excel.XLWorkbook(template))
            {
                var ws = wb.Worksheet(StockOpeningWorkbook.ReceivedSheet);
                ws.Cell(4, 1).Value = "150101";
                ws.Cell(4, 2).Value = "много";
                ws.Cell(5, 2).Value = 3;
                wb.SaveAs(broken);
            }

            var read = StockOpeningWorkbook.Read(broken);

            openNotes.Add(read.Problems.Count == 2 && read.Rows.Count == 2
                ? "" : $"сбърканите редове: {read.Problems.Count} причини, {read.Rows.Count} приети");

            // Кодът се чете само по цифрите си — както и да е написан.
            (string Text, bool Numeric, string Code)[] codes =
            [
                ("16 02 16", false, "160216"),
                (" 160216 ", false, "160216"),
                ("16.02.16", false, "160216"),
                ("16-02-16 ", false, "160216"),
                ("160216 — Опаковки от 2 вида", false, "160216"),
                ("10101", true, "010101"),
                ("1602", false, "1602")
            ];

            foreach (var (text, numeric, code) in codes)
                openNotes.Add(StockOpeningWorkbook.Code(text, numeric) == code
                    ? "" : $"„{text}“ се чете като „{StockOpeningWorkbook.Code(text, numeric)}“ вместо „{code}“");

            // Във файла: разредено, числово без водеща нула, непознато и късо.
            var written = Path.Combine(work, $"open_{Guid.NewGuid():N}.xlsx");

            using (var wb = new ClosedXML.Excel.XLWorkbook(template))
            {
                var ws = wb.Worksheet(StockOpeningWorkbook.ReceivedSheet);

                ws.Cell(3, 1).Value = " 16 02 16 ";
                ws.Cell(3, 2).Value = 1;
                ws.Cell(4, 1).Value = 20101;
                ws.Cell(4, 2).Value = 2;
                ws.Cell(5, 1).Value = "999999";
                ws.Cell(5, 2).Value = 3;
                ws.Cell(6, 1).Value = "1602";
                ws.Cell(6, 2).Value = 4;
                wb.SaveAs(written);
            }

            var known = new HashSet<string>(StringComparer.Ordinal) { "160216", "020101", "191202" };
            var typed = StockOpeningWorkbook.Read(written, known);
            var mine = typed.Rows.Where(r => r.Store == StoreKind.Received).ToList();

            openNotes.Add(mine.Count == 2 && mine[0].Ewc == "160216" && mine[1].Ewc == "020101"
                ? "" : "кодовете от файла: " + string.Join(", ", mine.Select(r => r.Ewc)));
            openNotes.Add(typed.Problems.Count == 2 &&
                          typed.Problems.Any(p => p.Contains("999999", StringComparison.Ordinal)) &&
                          typed.Problems.Any(p => p.Contains("6 цифри", StringComparison.Ordinal))
                ? "" : "непознатият и късият код не се казват: " + string.Join(" | ", typed.Problems));
        }
        catch (Exception ex)
        {
            openError = ex.ToString();
        }
    });

    openThread.SetApartmentState(ApartmentState.STA);
    openThread.Start();
    openThread.Join();

    if (openError is not null) Console.WriteLine("     " + openError);

    var openProblems = openNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in openProblems) Console.WriteLine("     " + problem);

    Check("началото и шаблонът минават без изключение", openError is null);
    Check($"направени са всичките {openNotes.Count} проверки", openNotes.Count == 23, $"{openNotes.Count}");
    Check("началото се мести по месеци, а завареното се внася от Excel", openProblems.Count == 0,
        $"{openProblems.Count} проблема");
}


// =============================================== Доставчиците се научават
//
// От кого приемаме е друг списък от този на получателите. Пълни се от всяко
// записано приемане — на ръка, от таблицата и от внесен дневник — и допълва
// написаното, за да не се пише едно име по три начина.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - доставчиците се научават от приеманията и допълват написаното");
{
    var supNotes = new List<string>();
    string? supError = null;

    var supThread = new Thread(() =>
    {
        try
        {
            var supPath = Path.Combine(work, $"sup_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(supPath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");

            // Партида отпреди списъка — тя го допълва първия път.
            db.Batches.Add(companyId, new BatchDraft("209988776", "Стар Доставчик ЕООД", "160216",
                new DateOnly(2026, 8, 1)));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 2), "160216", 1m,
                Eik: "204455667", Partner: "Скрап ЕООД"));

            var first = db.Originators(companyId);

            supNotes.Add(first.Count == 2 ? "" : $"доставчиците са {first.Count} вместо двама");
            supNotes.Add(first.Any(p => p.Name == "Стар Доставчик ЕООД" && p.Fresh)
                ? "" : "заведената отпреди партида не допълни списъка");
            supNotes.Add(db.Partners.List(companyId, PartnerRole.Receiver).Count == 0
                ? "" : "приемането влезе в списъка на получателите");

            // Само име, с други букви: ЕИК-ът и правописът идват от списъка.
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 3), "160216", 2m, Partner: "скрап еоод",
                Fields: new Dictionary<string, string> { ["name_originator"] = "скрап еоод" }));

            var batch = db.Batches.List(companyId).OrderByDescending(b => b.Id).First();

            supNotes.Add(batch.PartnerName == "Скрап ЕООД" && batch.Eik == "204455667"
                ? "" : $"партидата е „{batch.PartnerName}“ / „{batch.Eik}“");

            var move = db.Ledger.Read(companyId).OrderByDescending(m => m.Id).First();
            var fields = db.Ledger.Fields(move.Id);

            supNotes.Add(fields.GetValueOrDefault("name_originator") == "Скрап ЕООД" &&
                         fields.GetValueOrDefault("pin_originator") == "204455667"
                ? "" : "полетата за книгата не са допълнени");

            // Само ЕИК: името идва от списъка.
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 4), "160216", 3m, Eik: "204455667"));

            supNotes.Add(db.Batches.List(companyId).OrderByDescending(b => b.Id).First().PartnerName == "Скрап ЕООД"
                ? "" : "познат ЕИК не донесе името");

            var scrap = db.Originators(companyId).Where(p => p.Eik == "204455667").ToList();

            supNotes.Add(scrap.Count == 1 && scrap[0].Used == 3
                ? "" : $"Скрап ЕООД е {scrap.Count} пъти в списъка, ползван {scrap.FirstOrDefault()?.Used}");

            // Различно написаното не се пипа: ново име е нов доставчик.
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 5), "160216", 1m, Partner: "Нов Доставчик АД"));

            supNotes.Add(db.Originators(companyId).Any(p => p.Name == "Нов Доставчик АД")
                ? "" : "новият доставчик не се научи");

            // Махнатият оттук не се връща от партидите при следващо отваряне.
            var old = db.Originators(companyId).First(p => p.Name == "Стар Доставчик ЕООД");
            db.Partners.Remove(old.Id);

            supNotes.Add(db.Originators(companyId).All(p => p.Name != "Стар Доставчик ЕООД")
                ? "" : "махнатият доставчик се върна");

            // Прозорецът е същият, списъкът — друг.
            using var dialog = new ReceiversDialog(db, companyId, PartnerRole.Originator);

            supNotes.Add(dialog.RowCount == 2 ? "" : $"прозорецът показва {dialog.RowCount} доставчика вместо двама");
            supNotes.Add(dialog.Text == "Доставчици" && dialog.SummaryText.Contains("доставчи", StringComparison.Ordinal)
                ? "" : $"прозорецът не говори за доставчици: {dialog.Text} · {dialog.SummaryText}");
        }
        catch (Exception ex)
        {
            supError = ex.ToString();
        }
    });

    supThread.SetApartmentState(ApartmentState.STA);
    supThread.Start();
    supThread.Join();

    if (supError is not null) Console.WriteLine("     " + supError);

    var supProblems = supNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in supProblems) Console.WriteLine("     " + problem);

    Check("доставчиците минават без изключение", supError is null);
    Check($"направени са всичките {supNotes.Count} проверки", supNotes.Count == 11, $"{supNotes.Count}");
    Check("доставчиците се научават и допълват написаното", supProblems.Count == 0,
        $"{supProblems.Count} проблема");
}


// =============================================== Поправка в прозореца на действието
//
// Най-честата грешка е датата. Действието се отваря в прозореца, от който е
// дошло, със своите партиди и количества „върнати“ в склада; по-късно движение,
// което е черпило от него, спира поправката.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - записаното действие се поправя в прозореца, от който е дошло");
{
    var fixNotes = new List<string>();
    string? fixError = null;

    var fixThread = new Thread(() =>
    {
        try
        {
            var fixPath = Path.Combine(work, $"fix_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(fixPath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 10), "160216", 10m,
                Eik: "204455667", Partner: "Скрап ЕООД"));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 20), "160216", 5m,
                Eik: "205566778", Partner: "Метал груп ЕООД"));

            using var page = new StockPage(service, db);
            page.UseDate(new DateOnly(2026, 9, 30));
            page.UseStore(StoreKind.Received);

            var moves = db.Ledger.Read(companyId, "1");
            var shares = StockMath.Shares(db.Batches.List(companyId, "1"), moves,
                db.Ledger.Links(companyId, "1"), StoreKind.Received, new DateOnly(2026, 9, 30));

            // Първоначалното третиране — на 25-и, цялото.
            Dictionary<string, string> fields;

            using (var first = new TreatDialog(service, db, companyId, "1", shares, new DateOnly(2026, 9, 25)))
            {
                first.UseOperation(0);

                fields = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["op_id_code"] = first.FieldText("op_id_code"),
                    ["op_descr"] = first.FieldText("op_descr")
                };

                page.Apply(first.Planned(), new DateOnly(2026, 9, 25), fields, "първо");
            }

            var entry = db.Actions.List(companyId, "1").First();

            fixNotes.Add(db.Actions.Group(entry).Count == 1 && entry.Action.Length > 0
                ? "" : "действието не носи белег в дневника");

            var seed = page.Prepare(entry, out var scope, out var why);

            fixNotes.Add(seed is not null ? "" : "поправката не се сглобява: " + why);
            fixNotes.Add(scope.Count == 2 ? "" : $"прозорецът получава {scope.Count} партиди вместо две");

            using var fix = new TreatDialog(service, db, companyId, "1", scope, seed!.Date, seed);
            fix.Guard = (_, made) => page.Check(seed, made);

            fixNotes.Add(fix.Planned().Sum(m => m.Amount) == 15m
                ? "" : $"прозорецът се отваря с {fix.Planned().Sum(m => m.Amount)} т вместо 15");
            fixNotes.Add(fix.FieldText("op_id_code") == fields["op_id_code"]
                ? "" : "полетата не се връщат");

            // По-ранна дата: партидата от 20-и още я няма — взима се само
            // стоялото, без чужди партиди вместо нея.
            fix.UseDate(new DateOnly(2026, 9, 15));

            fixNotes.Add(fix.Planned().Sum(m => m.Amount) == 10m
                ? "" : $"на 15-и се третират {fix.Planned().Sum(m => m.Amount)} т вместо 10");

            // Първоначалното количество стои до новото и казва разликата.
            fixNotes.Add(fix.TotalsText.Contains("първоначално 15,000", StringComparison.Ordinal) &&
                         fix.TotalsText.Contains("−5,000", StringComparison.Ordinal)
                ? "" : "не се казва, че количеството се е променило: " + fix.TotalsText);

            // По-късно движение черпи от третираното на 26-и.
            var treatedBatches = db.Ledger.Links(companyId, "1")
                .Where(l => l.MoveId == entry.MoveId).ToList();

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Generated, new DateOnly(2026, 9, 26), "191202", 15m,
                Draws: [.. treatedBatches.Select(l => new Draw(l.BatchId, l.Amount))]));

            // Датата след него: образуването остава без основание — спира.
            fix.UseDate(new DateOnly(2026, 9, 28));

            var blocked = fix.Blocking();

            fixNotes.Add(blocked.Any(b => b.Contains("не достигат", StringComparison.Ordinal))
                ? "" : "поправка след по-късното движение не спира: " + string.Join(" | ", blocked));

            // Датата преди него: държи се.
            fix.UseDate(new DateOnly(2026, 9, 22));

            fixNotes.Add(fix.Blocking().Count == 0
                ? "" : "поправката на 22-и се спира: " + string.Join(" | ", fix.Blocking()));
            fixNotes.Add(fix.TotalsText.Contains("без промяна", StringComparison.Ordinal)
                ? "" : "същото количество не се казва като непроменено: " + fix.TotalsText);

            var written = page.Replace(seed, () => page.Apply(fix.Planned(), fix.Date, fields, "първо"));

            var treated = db.Ledger.Read(companyId, "1").Where(m => m.Kind == MoveKind.Treated).ToList();

            fixNotes.Add(written >= 1 && treated.Count == written && treated.All(m => m.Date == new DateOnly(2026, 9, 22))
                ? "" : "поправката не замени старото третиране");
            fixNotes.Add(treated.Sum(m => m.Amount) == 15m ? "" : $"поправеното е {treated.Sum(m => m.Amount)} т");

            var log = db.Actions.List(companyId, "1");

            fixNotes.Add(log.All(e => e.Date == new DateOnly(2026, 9, 22)) && log.All(e => e.Alive)
                ? "" : "дневникът пази старата дата");

            // Приемането не се поправя оттук.
            var received = new ActionEntry(0, companyId, "1", MoveKind.Received, 1, new DateOnly(2026, 9, 10),
                new LedgerPeriod(2026, 9), "160216", 10m, "", DateTime.UtcNow, true);

            fixNotes.Add(page.Prepare(received, out _, out var no) is null && no.Contains("Месец", StringComparison.Ordinal)
                ? "" : "приемане се предлага за поправка в прозорец");
        }
        catch (Exception ex)
        {
            fixError = ex.ToString();
        }
    });

    fixThread.SetApartmentState(ApartmentState.STA);
    fixThread.Start();
    fixThread.Join();

    if (fixError is not null) Console.WriteLine("     " + fixError);

    var fixProblems = fixNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in fixProblems) Console.WriteLine("     " + problem);

    Check("поправката минава без изключение", fixError is null);
    Check($"направени са всичките {fixNotes.Count} проверки", fixNotes.Count == 14, $"{fixNotes.Count}");
    Check("действието се поправя в прозореца си и не разваля по-късното", fixProblems.Count == 0,
        $"{fixProblems.Count} проблема");
}


// =============================================== Дневникът: „Въведено“ и „За изтриване“
//
// Работният раздел стои чист до подаването на отчета. Махнатото оттам отива
// „За изтриване“ и движението остава; истински се трие само оттам — и само
// докато „Настройки“ не го заключат.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - сгрешеното се слага настрана и се трие само оттам");
{
    var binNotes = new List<string>();
    string? binError = null;

    var binThread = new Thread(() =>
    {
        try
        {
            var binPath = Path.Combine(work, $"bin_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(binPath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 10), "160216", 10m, Eik: "204455667", Partner: "Скрап ЕООД"));

            using var page = new StockPage(service, db);
            page.UseDate(new DateOnly(2026, 9, 30));
            page.UseStore(StoreKind.Received);

            var shares = StockMath.Shares(db.Batches.List(companyId, "1"), db.Ledger.Read(companyId, "1"),
                db.Ledger.Links(companyId, "1"), StoreKind.Received, new DateOnly(2026, 9, 30));

            var piles = Treatment.Piles(shares);

            page.Apply(Treatment.Plan([new TreatOrder(piles[0], 2m)]), new DateOnly(2026, 9, 12));
            page.Apply(Treatment.Plan([new TreatOrder(piles[0], 3m)]), new DateOnly(2026, 9, 14));

            int Treated() => db.Ledger.Read(companyId, "1").Count(m => m.Kind == MoveKind.Treated);

            using var log = new ActionLogDialog(db, companyId, "1");

            binNotes.Add(log.RowCount == 2 ? "" : $"„Въведено“ показва {log.RowCount} реда вместо два");

            // Настрана: от работния раздел си отива, движението остава.
            log.Pick(0);
            log.TrashNow();

            binNotes.Add(log.RowCount == 1 && Treated() == 2
                ? "" : "сложеното настрана изчезна от склада или остана в „Въведено“");
            binNotes.Add(db.Actions.Count(companyId, "1") == 1
                ? "" : "числото до копчето брои и сложеното настрана");

            // Изчистването на работния раздел не пипа сложеното настрана.
            log.ClearNow();

            log.UseBin(true);
            binNotes.Add(log.RowCount == 1 ? "" : "изчистването изяде и „За изтриване“");

            // Връщане и пак настрана.
            log.Pick(0);
            binNotes.Add(log.CanRestore ? "" : "„Върни“ не се предлага");
            log.RestoreNow();
            binNotes.Add(log.RowCount == 0 ? "" : "върнатото остана в „За изтриване“");

            log.UseBin(false);
            log.Pick(0);
            log.TrashNow();
            log.UseBin(true);
            log.Pick(0);

            // Заключено от „Настройки“: не се трие.
            db.Settings.Set(SettingsStore.Keys.LogDeleteLocked, true);
            log.Pick(0);

            binNotes.Add(!log.CanDelete ? "" : "заключеното изтриване се предлага");
            log.DeleteNow();
            binNotes.Add(Treated() == 2 ? "" : "заключеното изтриване изтри движение");

            // Затворен месец: и отключено, от него не се трие.
            db.Settings.Set(SettingsStore.Keys.LogDeleteLocked, false);
            db.Periods.Close(companyId, "1", new LedgerPeriod(2026, 9));
            log.UseBin(true);
            log.Pick(0);

            binNotes.Add(!log.CanDelete ? "" : "изтриване от затворен месец се предлага");
            log.DeleteNow();
            binNotes.Add(Treated() == 2 ? "" : "от затворен месец се изтри движение");
            binNotes.Add(log.SummaryText.Contains("затворен", StringComparison.Ordinal)
                ? "" : "не се казва, че месецът е затворен: " + log.SummaryText);

            db.Periods.Reopen(companyId, "1", new LedgerPeriod(2026, 9));

            // Отключено и отворено: трие се самото движение, и редът си отива.
            log.UseBin(true);
            log.Pick(0);

            binNotes.Add(log.CanDelete ? "" : "отключеното изтриване не се предлага");
            log.DeleteNow();

            binNotes.Add(Treated() == 1 ? "" : $"след изтриването остават {Treated()} третирания вместо едно");
            binNotes.Add(log.RowCount == 0 ? "" : "изтритото остана в „За изтриване“");
        }
        catch (Exception ex)
        {
            binError = ex.ToString();
        }
    });

    binThread.SetApartmentState(ApartmentState.STA);
    binThread.Start();
    binThread.Join();

    if (binError is not null) Console.WriteLine("     " + binError);

    var binProblems = binNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in binProblems) Console.WriteLine("     " + problem);

    Check("дневникът на два раздела минава без изключение", binError is null);
    Check($"направени са всичките {binNotes.Count} проверки", binNotes.Count == 14, $"{binNotes.Count}");
    Check("сгрешеното се слага настрана и се трие само оттам", binProblems.Count == 0,
        $"{binProblems.Count} проблема");
}


// =============================================== Балансът по склада
//
// Балансът по отчети казва какво е отчетено; по склада — какво стои сега.
// Двата четат едни и същи редове по едни и същи правила: разделът е видът на
// движението, кодът — онзи, под който то влиза в книгата.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - балансът се смята и по склада, без да се чака отчет");
{
    var balNotes = new List<string>();
    string? balError = null;

    var balThread = new Thread(() =>
    {
        try
        {
            var balPath = Path.Combine(work, $"bal_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(balPath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");

            // Заварено в „Образуван“ към 31.08 и образувано отпреди годината.
            db.Setup.SetStart(companyId, new LedgerPeriod(2026, 9));
            db.Setup.AddOpening(companyId, new LedgerPeriod(2026, 9),
                new OpeningEntry(StoreKind.Formed, "191202", 4m));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Generated, new DateOnly(2025, 12, 15), "191204", 2m));

            // Годината: приемане, третиране, образуване, предаване.
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 3), "160216", 20m, Eik: "204455667", Partner: "Скрап ЕООД"));
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Treated, new DateOnly(2026, 9, 10), "160216", 20m));
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Generated, new DateOnly(2026, 9, 11), "191202", 15m));
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Submitted, new DateOnly(2026, 10, 5), "191202", 5m));

            var moves = db.Ledger.Read(companyId);

            // Самото правило.
            var entries = StoreBalance.Entries(moves, 2026);

            balNotes.Add(entries.Count == 4 ? "" : $"движенията на годината са {entries.Count} вместо четири");
            balNotes.Add(entries.Any(e => e.Sheet == BalanceSheet.Submitted && e.Month == 10 && e.Amount == 5m)
                ? "" : "предаването не влиза в своя раздел и месец");
            balNotes.Add(entries.All(e => e.Ewc != "191202" || e.Sheet != BalanceSheet.Generated || e.Amount == 15m)
                ? "" : "завареното влезе като образувано през годината");

            var openings = StoreBalance.Openings(moves, 2026);

            balNotes.Add(openings.GetValueOrDefault("191202") == 4m && openings.GetValueOrDefault("191204") == 2m
                ? "" : "началното салдо: " + string.Join(", ", openings.Select(o => $"{o.Key}={o.Value}")));

            // Изходният код е този на отчета.
            var outMove = new Move(1, MoveKind.Treated, new DateOnly(2026, 9, 1), "160216", 1m, EwcOut: "191202");
            balNotes.Add(StoreBalance.Reported(outMove) == "191202" ? "" : "изходният код не е този на отчета");

            // Страницата: по склада — без нито един отчет, и без да се избира:
            // складът е подразбирането.
            using var page = new BalancePage(service, db);

            page.UseAllCompanies(true);
            page.UseView(BalanceSheets.All.Count, running: true);

            balNotes.Add(page.SummaryText.Contains("по склада", StringComparison.Ordinal)
                ? "" : "не се казва, че е по склада: " + page.SummaryText);

            // 4 заварени + 2 отпреди годината + 15 образувани − 5 предадени = 16.
            balNotes.Add(page.SummaryText.Contains("остават 16 т", StringComparison.Ordinal) &&
                         page.SummaryText.Contains("преди 2026 г.: 6 т", StringComparison.Ordinal)
                ? "" : "началото не е 6 т или в края на годината не остават 16 т: " + page.SummaryText);

            var row = page.RowTexts().FirstOrDefault(r => r.StartsWith("191202", StringComparison.Ordinal)) ?? string.Empty;

            balNotes.Add(row.Length > 0 ? "" : "кодът 191202 липсва в таблицата");

            // По отчети същата база няма нито един проверен отчет.
            page.UseStore(false);

            balNotes.Add(page.SummaryText.Contains("няма проверена", StringComparison.Ordinal)
                ? "" : "по отчети се виждат числата на склада: " + page.SummaryText);

            // Складът е подразбирането, но смененият избор се помни: последно
            // избраното тук е „По отчети“ и новата страница тръгва от него.
            using var again = new BalancePage(service, db);
            again.UseAllCompanies(true);

            balNotes.Add(again.SummaryText.Contains("няма проверена", StringComparison.Ordinal)
                ? "" : "източникът не се помни между пусканията: " + again.SummaryText);
        }
        catch (Exception ex)
        {
            balError = ex.ToString();
        }
    });

    balThread.SetApartmentState(ApartmentState.STA);
    balThread.Start();
    balThread.Join();

    if (balError is not null) Console.WriteLine("     " + balError);

    var balProblems = balNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in balProblems) Console.WriteLine("     " + problem);

    Check("балансът по склада минава без изключение", balError is null);
    Check($"направени са всичките {balNotes.Count} проверки", balNotes.Count == 10, $"{balNotes.Count}");
    Check("балансът се смята и по склада", balProblems.Count == 0, $"{balProblems.Count} проблема");
}


// =============================================== Свързаните колони в баланса
//
// Дванайсет месеца се разширяват с едно влачене: свързаните колони следват
// разширената, а ширините преживяват всяко сглобяване на таблицата.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - свързаните колони в баланса следват разширената");
{
    var linkNotes = new List<string>();
    string? linkError = null;

    var linkThread = new Thread(() =>
    {
        try
        {
            var linkPath = Path.Combine(work, $"links_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(linkPath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Generated, new DateOnly(2026, 9, 11), "191202", 15m));

            string[] months = [.. Enumerable.Range(1, 12).Select(m => "m" + m)];

            using (var page = new BalancePage(service, db))
            {
                page.UseAllCompanies(true);
                page.UseView(BalanceSheets.All.Count, running: false);

                page.Links.Link(months);
                linkNotes.Add(page.Links.Linked.Count == 12 ? "" : $"свързани са {page.Links.Linked.Count} вместо 12");

                var total = page.Links.WidthOf("__total");

                // Едно влачене — всички месеци.
                page.UseWidth("m3", 95);

                linkNotes.Add(months.All(m => page.Links.WidthOf(m) == 95)
                    ? "" : "месеците не последваха: " + string.Join(",", months.Select(page.Links.WidthOf)));
                linkNotes.Add(page.Links.WidthOf("__total") == total ? "" : "несвързаната колона също се промени");

                // Друг изглед сглобява таблицата наново — ширините остават.
                page.UseView(0, running: false);

                linkNotes.Add(page.Links.WidthOf("m7") == 95 ? "" : $"след смяна на изгледа m7 е {page.Links.WidthOf("m7")}");
                linkNotes.Add(page.Links.Linked.Count == 12 ? "" : "връзките се загубиха при смяна на изгледа");
            }

            // И между пусканията.
            using (var again = new BalancePage(service, db))
            {
                again.UseAllCompanies(true);
                again.UseView(BalanceSheets.All.Count, running: false);

                linkNotes.Add(again.Links.WidthOf("m7") == 95 && again.Links.Linked.Count == 12
                    ? "" : "ширините и връзките не се помнят между пусканията");

                // Развързани — всяка колона е сама за себе си.
                again.Links.Unlink();
                again.UseWidth("m3", 70);

                linkNotes.Add(again.Links.WidthOf("m3") == 70 && again.Links.WidthOf("m7") == 95
                    ? "" : "развързаните колони още се следват");
            }
        }
        catch (Exception ex)
        {
            linkError = ex.ToString();
        }
    });

    linkThread.SetApartmentState(ApartmentState.STA);
    linkThread.Start();
    linkThread.Join();

    if (linkError is not null) Console.WriteLine("     " + linkError);

    var linkProblems = linkNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in linkProblems) Console.WriteLine("     " + problem);

    Check("свързаните колони минават без изключение", linkError is null);
    Check($"направени са всичките {linkNotes.Count} проверки", linkNotes.Count == 7, $"{linkNotes.Count}");
    Check("свързаните колони следват разширената и се помнят", linkProblems.Count == 0,
        $"{linkProblems.Count} проблема");
}


// =============================================== Двете купчини и завареното
//
// В практиката складовете са два физически: суровината, както е дошла, и
// образуваното, което чака да бъде предадено. „Третиран“ е временен държател в
// рамките на периода, „Предаден“ — регистър на излязлото. В края на периода и в
// двете купчини остава нещо, което продължава нататък — и в началото на воденето
// същото това нещо се ОБЯВЯВА.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - двете купчини заварват количество, другите две не");
{
    var startNotes = new List<string>();
    string? startError = null;

    var startThread = new Thread(() =>
    {
        try
        {
            var startPath = Path.Combine(work, $"start_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(startPath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");

            using var page = new StockPage(service, db);

            // Без обявен начален месец прозорецът предлага месец и казва, че
            // той е само предложение; стрелките местят предложението, без да
            // записват нищо.
            using (var early = new StartStockDialog(service, db, companyId, "1"))
            {
                _ = early.Handle;

                startNotes.Add(early.EveText.Contains("не е обявен", StringComparison.Ordinal)
                    ? "" : "не се казва, че месецът е само предложен: " + early.EveText);

                early.StepMonth(-1);

                startNotes.Add(db.Setup.Start(companyId) is null
                    ? "" : "стрелката записа предложението като начало");
            }

            db.Setup.SetStart(companyId, new LedgerPeriod(2026, 9));

            using (var start = new StartStockDialog(service, db, companyId, "1"))
            {
                _ = start.Handle;

                // Двете физически купчини — и двете заварват.
                start.Declare(StoreKind.Received, "160216", 10m);
                start.Declare(StoreKind.Formed, "191202", 4m);

                startNotes.Add(start.RowCount == 2
                    ? "" : $"обявени са {start.RowCount} реда вместо два");
                startNotes.Add(start.SummaryText.Contains("10,000", StringComparison.Ordinal)
                    ? "" : "не се брои завареното в „Получен“: " + start.SummaryText);
                startNotes.Add(start.SummaryText.Contains("4,000", StringComparison.Ordinal)
                    ? "" : "не се брои завареното в „Образуван“: " + start.SummaryText);

                // Отрицателно и празно не се обявяват.
                start.Declare(StoreKind.Received, "160216", 0m);
                startNotes.Add(start.RowCount == 2 ? "" : "нула се обявява за заварено количество");
            }

            // И двете стоят в своя склад в деня преди първия воден месец.
            page.UseDate(new DateOnly(2026, 8, 31));
            page.UseStore(StoreKind.Received);
            page.SelectMaster(0);

            startNotes.Add(page.Selection().Sum(x => x.Amount) == 10m
                ? "" : $"в „Получен“ стоят {page.Selection().Sum(x => x.Amount)} вместо 10");

            page.UseStore(StoreKind.Formed);
            page.SelectMaster(0);

            startNotes.Add(page.Selection().Sum(x => x.Amount) == 4m
                ? "" : $"в „Образуван“ стоят {page.Selection().Sum(x => x.Amount)} вместо 4");

            // Завареното продължава и в следващия месец: то е начало, не епизод.
            page.UseDate(new DateOnly(2026, 9, 30));
            page.SelectMaster(0);

            startNotes.Add(page.Selection().Sum(x => x.Amount) == 4m
                ? "" : "завареното в „Образуван“ не продължава в септември");

            // Изчерпаното не се маха: то е начало на верига, а не число.
            var openings = db.Setup.Openings(companyId, "1");
            var raw = openings.First(o => o.Store == StoreKind.Received);

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Treated, new DateOnly(2026, 9, 5), "160216", 6m));

            startNotes.Add(!db.Setup.RemoveOpening(companyId, raw.Batch.Id)
                ? "" : "изчерпаното заварено количество се маха безнаказано");

            var formed = openings.First(o => o.Store == StoreKind.Formed);

            startNotes.Add(db.Setup.RemoveOpening(companyId, formed.Batch.Id)
                ? "" : "недокоснатото заварено количество не се маха");
        }
        catch (Exception ex)
        {
            startError = ex.ToString();
        }
    });

    startThread.SetApartmentState(ApartmentState.STA);
    startThread.Start();
    startThread.Join();

    if (startError is not null) Console.WriteLine("     " + startError);

    var startProblems = startNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in startProblems) Console.WriteLine("     " + problem);

    Check("завареното минава без изключение", startError is null);
    Check($"направени са всичките {startNotes.Count} проверки", startNotes.Count == 11,
        $"{startNotes.Count}");
    Check("двете купчини носят заварено количество", startProblems.Count == 0,
        $"{startProblems.Count} проблема");
}



// =============================================== Останалата една стойност
//
// Кратък списък с една стойност не е меню, а решение: „при нас това поле е
// винаги това“. Оттам нататък човек го преписва при всяко движение и точно
// това преписване няма кой да сбърка освен него. Затова се попълва само — но
// само когато го е поискал, и само когато стеснението е НЕГОВО, а не на
// схемата.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - останалата една стойност се попълва сама");
{
    var soleNotes = new List<string>();
    string? soleError = null;

    var bookSchema = service.GetSchema("book");

    var submitted = bookSchema.FindReport(BookNodes.Report)!
        .SelfAndDescendants()
        .First(n => n.ElementName == Moves.Node(MoveKind.Submitted));

    var operation = submitted.Attributes.First(a => a.Name == "op_id_code");

    // Втората, а не първата: попълнено първо място не отличава избора от
    // подразбирането на самото меню.
    var only = operation.Domain.Options[1];
    var other = operation.Domain.Options[0];

    var node = Moves.Node(MoveKind.Submitted);

    ValueLimits Limits(IReadOnlyList<string> allowed, bool fills) =>
        new([new ValueListEntry(BookNodes.Report, node, "op_id_code", allowed)], fills);

    // --- Правилото, без нито един прозорец.

    soleNotes.Add(Limits([only.Value], false).Fill(BookNodes.Report, node, operation) is null
        ? "" : "попълва се без отметка");

    soleNotes.Add(Limits([only.Value], true).Fill(BookNodes.Report, node, operation)?.Value == only.Value
        ? "" : "останалата една стойност не се връща");

    soleNotes.Add(Limits([only.Value, other.Value], true).Fill(BookNodes.Report, node, operation) is null
        ? "" : "избира се една от две");

    soleNotes.Add(new ValueLimits([], true).Fill(BookNodes.Report, node, operation) is null
        ? "" : "попълва се без кратък списък");

    // Списък от кодове, които схемата не познава, не стеснява нищо: Offer
    // връща пълното меню, а с него няма как да е „останала една“.
    soleNotes.Add(Limits(["ЯЯЯ"], true).Fill(BookNodes.Report, node, operation) is null
        ? "" : "чужд код се попълва като единствен");

    // Стесняването си върви и без отметката: тя добавя крачка, не я заменя.
    soleNotes.Add(Limits([only.Value], false).Offer(BookNodes.Report, node, operation).Count == 1
        ? "" : "без отметката менюто не се стеснява");

    // Чуждо поле не се пипа от този списък.
    if (submitted.Attributes.FirstOrDefault(a =>
            a.Name != "op_id_code" && a.Domain.IsEnum && !a.Domain.IsLargeEnum) is { } stranger)
    {
        soleNotes.Add(Limits([only.Value], true).Fill(BookNodes.Report, node, stranger) is null
            ? "" : "списъкът на едно поле попълва друго");
    }
    else
    {
        soleNotes.Add("възелът няма второ късо меню, с което да се провери");
    }

    // --- Отметката на фирмата и прозорчетата.

    var soleThread = new Thread(() =>
    {
        try
        {
            var solePath = Path.Combine(work, $"sole_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(solePath);

            var metal = db.Companies.Add("203344556", "Метал ООД");
            var skrap = db.Companies.Add("204455667", "Скрап ЕООД");

            db.Lists.Save(metal, "book", BookNodes.Report, node, "op_id_code", [only.Value]);

            // Отметката е на фирма и тръгва пусната.
            soleNotes.Add(!db.Lists.Fills(metal) ? "" : "отметката тръгва вдигната");

            db.Lists.SetFills(metal, true);

            soleNotes.Add(db.Lists.Fills(metal) ? "" : "вдигнатата отметка не се помни");
            soleNotes.Add(!db.Lists.Fills(skrap) ? "" : "отметката на едната фирма важи и за другата");
            soleNotes.Add(db.Lists.LimitsFor(metal, "book").Fills
                ? "" : "списъците не носят отметката нататък");
            soleNotes.Add(!db.Lists.LimitsFor(skrap, "book").Fills
                ? "" : "чуждите списъци носят чужда отметка");

            db.Writer.Write(metal, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 1), "160216", 20m));

            db.Writer.Write(metal, new MoveRequest(
                MoveKind.Generated, new DateOnly(2026, 9, 11), "191202", 20m));

            using var page = new StockPage(service, db);

            page.UseDate(new DateOnly(2026, 9, 30));
            page.UseStore(StoreKind.Formed);
            page.SelectMaster(0);

            var chosen = page.Selection();

            // Вдигната отметка: полето влиза попълнено, а с него и описанието
            // на дейността — то следва избора, не се пише отделно.
            using (var dialog = new SubmitDialog(
                       service, db, metal, "1", chosen, new DateOnly(2026, 9, 30)))
            {
                _ = dialog.Handle;

                soleNotes.Add(dialog.FieldText("op_id_code") == only.Display
                    ? "" : "дейността не влиза попълнена: " + dialog.FieldText("op_id_code"));

                soleNotes.Add(only.Description is null || dialog.FieldText("op_descr").Length > 0
                    ? "" : "описанието не тръгва след избраната дейност");
            }

            db.Lists.SetFills(metal, false);

            // Пусната отметка: менюто пак е свито до едно, но полето е празно.
            using (var dialog = new SubmitDialog(
                       service, db, metal, "1", chosen, new DateOnly(2026, 9, 30)))
            {
                _ = dialog.Handle;

                soleNotes.Add(dialog.FieldText("op_id_code").Length == 0
                    ? "" : "попълва се и с пусната отметка: " + dialog.FieldText("op_id_code"));

                soleNotes.Add(dialog.UseOperation(0) ? "" : "менюто на дейността е празно");
            }

            // --- Екранът „Списъци“: отметката се вижда и се обяснява.

            var scope = new CompanyScope(db);
            scope.Use(metal);

            using var lists = new ListsPage(service, db, scope);

            soleNotes.Add(lists.FillsOffered ? "" : "отметката не се предлага при избрана фирма");
            soleNotes.Add(!lists.FillsOn ? "" : "екранът показва вдигнато това, което е пуснато");
            soleNotes.Add(lists.FillsText.Contains("едно поле", StringComparison.Ordinal)
                ? "" : "не се казва колко полета засяга: " + lists.FillsText);

            lists.UseFills(true);

            soleNotes.Add(db.Lists.Fills(metal) ? "" : "вдигнатото от екрана не се записва");
            soleNotes.Add(lists.FillsText.Contains("предаване", StringComparison.Ordinal)
                ? "" : "не се казва къде важи: " + lists.FillsText);

            // Другата фирма няма списъци — отметката ѝ е своя и е пусната.
            scope.Use(skrap);

            soleNotes.Add(!lists.FillsOn ? "" : "отметката се пренася на съседната фирма");
            soleNotes.Add(lists.FillsText.Contains("няма поле", StringComparison.Ordinal)
                ? "" : "не се казва, че няма какво да се попълва: " + lists.FillsText);
        }
        catch (Exception ex)
        {
            soleError = ex.ToString();
        }
    });

    soleThread.SetApartmentState(ApartmentState.STA);
    soleThread.Start();
    soleThread.Join();

    if (soleError is not null) Console.WriteLine("     " + soleError);

    var soleProblems = soleNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in soleProblems) Console.WriteLine("     " + problem);

    Check("попълването минава без изключение", soleError is null);
    Check($"направени са всичките {soleNotes.Count} проверки", soleNotes.Count == 23,
        $"{soleNotes.Count}");
    Check("останалата една стойност се попълва само по поръчка", soleProblems.Count == 0,
        $"{soleProblems.Count} проблема");
}



// =============================================== Посоченият поемащ ред
//
// Изравняването слага разликата в ЕДИН ред. Дотук той се сочеше с курсора — а
// курсорът се мести при всяко щракване, и щракване по кой да е стълб освен
// количеството е ИЗБОР: то изважда останалите редове от предаването. Човек,
// който иска да посочи реда, рискуваше избора си, за да го направи.
//
// Затова редът се сочи от меню: то не пипа нито една таблица.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - поемащият ред се сочи, без да се разваля изборът");
{
    var pinNotes = new List<string>();
    string? pinError = null;

    var pinThread = new Thread(() =>
    {
        try
        {
            var pinPath = Path.Combine(work, $"pin_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(pinPath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");

            // Две приемания, за да има по две партиди зад всеки образуван код.
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 1), "160216", 12m,
                Eik: "204455667", Partner: "Търговец ООД", Document: "ППП 101"));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 3), "160216", 8m,
                Eik: "204455667", Partner: "Търговец ООД", Document: "ППП 102"));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Treated, new DateOnly(2026, 9, 10), "160216", 20m));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Generated, new DateOnly(2026, 9, 11), "191202", 15m));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Generated, new DateOnly(2026, 9, 11), "191204", 5m));

            using var page = new StockPage(service, db);

            page.UseDate(new DateOnly(2026, 9, 30));
            page.UseStore(StoreKind.Formed);
            page.SelectMaster(0, 1);

            using var dialog = new SubmitDialog(
                service, db, companyId, "1", page.Selection(), new DateOnly(2026, 9, 30));

            _ = dialog.Handle;

            pinNotes.Add(dialog.PickedTotal == 20m
                ? "" : $"отваря се с {dialog.PickedTotal} т вместо с целите 20");

            // Менюто изброява точно това, което участва: двата кода и по две
            // партиди зад всеки, плюс старото поведение най-отгоре.
            pinNotes.Add(dialog.IntoCount == 7
                ? "" : $"менюто предлага {dialog.IntoCount} реда вместо седем");
            pinNotes.Add(dialog.IntoAt(0).Contains("стоя", StringComparison.Ordinal)
                ? "" : "курсорът не е първият избор: " + dialog.IntoAt(0));
            pinNotes.Add(dialog.IntoPicked == 0 ? "" : "менюто тръгва посочено");
            pinNotes.Add(SubmitDialog.IntoCaption.Contains("Разликата", StringComparison.Ordinal)
                ? "" : "менюто не е озаглавено: " + SubmitDialog.IntoCaption);
            pinNotes.Add(dialog.IntoFits ? "" : "менюто не се побира в рамката");

            // Тесният прозорец е QA-лаптопът: лентата слиза на два реда, но
            // нито едно от трите не изчезва и не се реже.
            var wide = dialog.ClientSize;

            dialog.ClientSize = new Size(1000, 760);
            Application.DoEvents();

            pinNotes.Add(dialog.IntoFits ? "" : "на тесен прозорец менюто се губи");
            pinNotes.Add(dialog.BalanceFits ? "" : "на тесен прозорец копчетата се режат");
            pinNotes.Add(dialog.TargetFits ? "" : "на тесен прозорец кантарът се реже");
            pinNotes.Add(dialog.ScaleFits ? "" : "нещо в рамката на кантара излиза от нея");

            dialog.ClientSize = wide;
            Application.DoEvents();

            pinNotes.Add(dialog.IntoFits ? "" : "менюто не се връща на широк прозорец");

            var codes = Enumerable.Range(0, dialog.IntoCount).Select(dialog.IntoAt).ToList();

            pinNotes.Add(codes.Any(c => c.StartsWith("191202", StringComparison.Ordinal))
                ? "" : "първият код го няма в менюто");
            pinNotes.Add(codes.Any(c => c.StartsWith("191204", StringComparison.Ordinal))
                ? "" : "вторият код го няма в менюто");
            pinNotes.Add(codes.Count(c => c.TrimStart().StartsWith("#", StringComparison.Ordinal)) == 4
                ? "" : "партидите не влизат в менюто");

            // Камионът е показал осемнайсет: два тона в повече.
            dialog.UseTarget(18m);

            // Курсорът стои на първия код. Посочва се ВТОРИЯТ — и изборът
            // остава непокътнат, което е цялата причина менюто да го има.
            dialog.PickTakeCell(0);

            var second = codes.FindIndex(c => c.StartsWith("191204", StringComparison.Ordinal));
            pinNotes.Add(second > 0 ? "" : "вторият код не се намира в менюто");

            var before = dialog.PickedTotal;

            dialog.UseInto(second);

            pinNotes.Add(dialog.PickedTotal == before
                ? "" : $"посочването изяде избора: {dialog.PickedTotal} т вместо {before}");
            pinNotes.Add(dialog.IntoPicked == second ? "" : "посоченото не се помни");
            pinNotes.Add(dialog.AnchorText.Contains("191204", StringComparison.Ordinal)
                ? "" : "изречението не сочи посочения ред: " + dialog.AnchorText);
            pinNotes.Add(dialog.AnchorText.Contains("посочен", StringComparison.Ordinal)
                ? "" : "не се казва, че редът е посочен, а не под ръката: " + dialog.AnchorText);

            dialog.UseLevel();

            // Разликата е седнала в ПОСОЧЕНИЯ ред, не в този под курсора.
            pinNotes.Add(dialog.PickedTotal == 18m
                ? "" : $"изравняването дава {dialog.PickedTotal} т вместо 18");
            pinNotes.Add(dialog.TakeAt(0) == "15,000"
                ? "" : "редът под курсора е пипнат: " + dialog.TakeAt(0));
            pinNotes.Add(dialog.TakeAt(1) == "3,000"
                ? "" : "разликата не седна в посочения ред: " + dialog.TakeAt(1));

            // Посочването остава след изравняването: същият ред поема и
            // следващата разлика, без да се сочи наново.
            dialog.UseTarget(17m);

            pinNotes.Add(dialog.IntoPicked > 0 ? "" : "посоченото се губи след изравняване");

            dialog.UseLevel();

            pinNotes.Add(dialog.TakeAt(1) == "2,000"
                ? "" : "втората разлика не седна в същия ред: " + dialog.TakeAt(1));

            // Изпадне ли посоченият ред от избора, той не се пази мълчаливо:
            // в него разликата вече няма къде да седне.
            dialog.UseTake(1, 0m);

            pinNotes.Add(dialog.IntoPicked == 0 ? "" : "отпадналият ред остава посочен");
            pinNotes.Add(dialog.DoneText.Contains("отпадна", StringComparison.Ordinal)
                ? "" : "отпадането се премълчава: " + dialog.DoneText);

            // И без посочване всичко е както преди: курсорът сочи реда.
            dialog.UseTarget(13m);
            dialog.PickTakeCell(0);

            pinNotes.Add(dialog.AnchorText.Contains("191202", StringComparison.Ordinal)
                ? "" : "без посочване курсорът не сочи реда: " + dialog.AnchorText);

            dialog.UseLevel();

            pinNotes.Add(dialog.PickedTotal == 13m
                ? "" : $"старото поведение дава {dialog.PickedTotal} т вместо 13");
        }
        catch (Exception ex)
        {
            pinError = ex.ToString();
        }
    });

    pinThread.SetApartmentState(ApartmentState.STA);
    pinThread.Start();
    pinThread.Join();

    if (pinError is not null) Console.WriteLine("     " + pinError);

    var pinProblems = pinNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in pinProblems) Console.WriteLine("     " + problem);

    Check("посочването минава без изключение", pinError is null);
    Check($"направени са всичките {pinNotes.Count} проверки", pinNotes.Count == 28,
        $"{pinNotes.Count}");
    Check("посоченият ред поема разликата, без да се пипа изборът", pinProblems.Count == 0,
        $"{pinProblems.Count} проблема");
}


// =============================================== Образуваното от цяла купчина не оставя грамове
//
// Намерено в истинска база: след като всичко третирано е образувано наведнъж, в
// „Третиран“ оставаха по 0,002–0,035 т в десетки партиди и човек ги образуваше
// втори път на ръка (0,070 т в 54 партиди, 0,159 т в 29). Причината беше в
// наследяването на произхода: съотношението на партидата минаваше през процент
// със два знака — 1,029 т от 39,622 т са 2,597 %, станали 2,60 %, и партидата
// даде 1,030 т вместо 1,029, а съседката ѝ 0,598 вместо 0,600. Сборът на реда
// оставаше точен, затова екранът мълчеше; по партиди единият грам чакаше, а
// другият сочеше празна партида и спираше месеца като „произход без основание“.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - образуваното от цяла купчина не оставя грамове в „Третиран“");
{
    var gramNotes = new List<string>();
    string? gramError = null;

    try
    {
        // Числата от истинската база: цялото чакащо е образувано, значи всяка
        // партида трябва да даде точно каквото е чакало в нея.
        var real = new List<Draw> { new(105, 0.600m), new(106, 1.029m), new(107, 37.993m) };
        var whole = Allocation.Inherit(real, 39.622m);

        gramNotes.Add(whole.Any(d => d.BatchId == 106 && d.Amount == 1.029m)
            ? "" : "партидата с 1,029 т дава " + whole.FirstOrDefault(d => d.BatchId == 106)?.Amount);
        gramNotes.Add(whole.Any(d => d.BatchId == 105 && d.Amount == 0.600m)
            ? "" : "партидата с 0,600 т дава " + whole.FirstOrDefault(d => d.BatchId == 105)?.Amount);
        gramNotes.Add(whole.Sum(d => d.Amount) == 39.622m ? "" : "наследеното не събира образуваното");

        // Два изходни кода по равно от две равни партиди: поотделно всеки код
        // закръгляше 0,0025 нагоре в първата партида и тя даваше 0,006 от 0,005.
        var halves = Allocation.Split(
            [new Draw(1, 0.005m), new Draw(2, 0.005m)], [0.005m, 0.005m]);

        gramNotes.Add(halves.SelectMany(h => h).Where(d => d.BatchId == 1).Sum(d => d.Amount) == 0.005m
            ? "" : "при два кода първата партида дава повече, отколкото има");
        gramNotes.Add(halves.All(h => h.Sum(d => d.Amount) == 0.005m)
            ? "" : "при два кода някой код не получава количеството си");

        // Над чакащото произход няма: излишъкът остава без партида, вместо да
        // се припише на чужда доставка.
        var beyond = Allocation.Inherit([new Draw(1, 4m), new Draw(2, 6m)], 12m);

        gramNotes.Add(beyond.Sum(d => d.Amount) == 10m
            ? "" : $"над чакащото се приписват {beyond.Sum(d => d.Amount)} т на партиди");

        // По-малко от чакащото: пропорционално, до грам, без да се надхвърля
        // нито една партида.
        var part = Allocation.Inherit([new Draw(1, 1m), new Draw(2, 2m)], 1m);

        gramNotes.Add(part.Sum(d => d.Amount) == 1m && part.All(d => d.Amount <= (d.BatchId == 1 ? 1m : 2m))
            ? "" : "частичното наследяване не е пропорционално: " +
                   string.Join(", ", part.Select(d => $"{d.BatchId}:{d.Amount}")));

        gramNotes.Add(Allocation.Prorate(10m, [1m, 1m, 1m]).Sum() == 10m
            ? "" : "три равни дяла от 10 т не събират 10 т");

        // Голяма купчина в три кода: 400 партиди с количества от грамове до
        // тонове, рецепта 61,7 / 27,3 / 11 — всеки код точно, всяка партида точно.
        var rng = new Random(20261102);
        var many = Enumerable.Range(1, 400)
            .Select(i => new StockShare("150101",
                new Batch(i, $"2026-{i:D4}", "", "", "150101", new DateOnly(2026, 7, 1 + i % 28)),
                rng.Next(3) == 0 ? rng.Next(1, 60) / 1000m : rng.Next(1, 9_000_000) / 1000m))
            .ToList();

        var pile = Generation.Piles(many)[0];
        var order = new GenOrder(pile,
            [.. pile.Shares.Select(s => new TreatPick(s, s.Amount))],
            Generation.Recipe(pile.Available, [("191202", 61.7m), ("191204", 27.3m), ("191212", 11m)]));

        var planned = Generation.Plan([order]);
        var given = planned.SelectMany(m => m.Draws)
            .GroupBy(d => d.BatchId)
            .ToDictionary(g => g.Key, g => g.Sum(d => d.Amount));

        gramNotes.Add(many.All(s => given.GetValueOrDefault(s.Batch.Id) == s.Amount)
            ? "" : $"{many.Count(s => given.GetValueOrDefault(s.Batch.Id) != s.Amount)} от 400 партиди " +
                   "не дават точно каквото е чакало в тях");
        gramNotes.Add(planned.All(m => m.Draws.Sum(d => d.Amount) == m.Amount)
            ? "" : "някой изходен код не събира количеството си по партиди");
        gramNotes.Add(planned.SelectMany(m => m.Draws).All(d => d.Amount > 0 && decimal.Round(d.Amount, 3) == d.Amount)
            ? "" : "дял с нула, минус или повече от три знака");

        // Предаване „Пропорционално“: точно съотношение, до грам.
        var scaled = Handover.Scale([.. pile.Shares.Take(50).Select(s => new TreatPick(s, s.Amount))],
            SplitMath.Round(pile.Shares.Take(50).Sum(s => s.Amount)));

        gramNotes.Add(scaled.Zip(pile.Shares.Take(50)).All(pair => pair.First.Amount == pair.Second.Amount)
            ? "" : "„Пропорционално“ към същия сбор мести количества между редовете");

        // Целият път през базата: приемания, третиране и образуване наведнъж.
        var gramPath = Path.Combine(work, $"grams_{Guid.NewGuid():N}.db");
        using var db = Workspace.Open(gramPath);

        var companyId = db.Companies.Add("203344556", "Метал ООД");
        var day = new DateOnly(2026, 7, 31);

        foreach (var share in many.Take(120))
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, share.Batch.ReceivedOn, "150101", share.Amount, Eik: "203344556"));

        Treat(db, companyId, day);

        // Първо 60 % от купчината — останалото чака точно, без грам встрани.
        var first = Waiting(db, companyId, day);
        var firstPile = Generation.Piles(first.Shares)[0];
        var sixty = Treatment.Spread(firstPile, SplitMath.Round(firstPile.Available * 0.6m));

        Generate(db, companyId, day, new GenOrder(firstPile, sixty,
            Generation.Recipe(SplitMath.Round(sixty.Sum(p => p.Amount)), [("191202", 70m), ("191204", 30m)])));

        var middle = Waiting(db, companyId, day);

        gramNotes.Add(middle.Total == SplitMath.Round(firstPile.Available - sixty.Sum(p => p.Amount))
            ? "" : $"след 60 % чакат {middle.Total} т вместо остатъка");
        gramNotes.Add(middle.Unbacked.Count == 0 ? "" : "след 60 % има произход без основание");

        // После останалото: складът се изпразва до грам.
        var restPile = Generation.Piles(middle.Shares)[0];

        Generate(db, companyId, day, new GenOrder(restPile,
            [.. restPile.Shares.Select(s => new TreatPick(s, s.Amount))],
            Generation.Recipe(restPile.Available, [("191202", 70m), ("191204", 30m)])));

        var empty = Waiting(db, companyId, day);

        gramNotes.Add(empty.Total == 0m ? "" : $"след образуване на всичко чакат {empty.Total} т");
        gramNotes.Add(empty.Unbacked.Count == 0 ? "" : $"{empty.Unbacked.Count} дяла сочат празни партиди");

        // Образуване от „Месец“ — без посочени партиди, наследено от чакащото.
        foreach (var share in many.Skip(120).Take(80))
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, share.Batch.ReceivedOn, "200101", share.Amount, Eik: "203344556"));

        Treat(db, companyId, day);

        db.Writer.Write(companyId, new MoveRequest(
            MoveKind.Generated, day, "191201", Waiting(db, companyId, day).Total));

        var month = Waiting(db, companyId, day);

        gramNotes.Add(month.Total == 0m && month.Unbacked.Count == 0
            ? "" : $"образуваното от „Месец“ оставя {month.Total} т и {month.Unbacked.Count} празни дяла");

        // Истинският износ от „Деметра“: 553 доставки, 1014 т, наведнъж.
        if (FindDemetraSample(AppContext.BaseDirectory) is { } demetra)
        {
            var file = DemetraWorkbook.Read(demetra);
            var outcome = LedgerImport.Apply(file, new ImportPlan(
                DemetraWorkbook.SuggestedKinds,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                DemetraWorkbook.SuggestedDirect));

            var otherId = db.Companies.Add("204455667", "Скрап ЕООД");

            foreach (var move in outcome.Moves.Where(m => m.Kind == MoveKind.Received))
                db.Writer.Write(otherId, new MoveRequest(
                    move.Kind, move.Date, move.Ewc, move.Amount, "1", move.Eik, move.Partner,
                    move.Document, move.Note, Fields: move.Fields));

            Treat(db, otherId, day);

            var july = Waiting(db, otherId, day);

            Generate(db, otherId, day,
            [
                .. Generation.Piles(july.Shares).Select(p => new GenOrder(p,
                    [.. p.Shares.Select(s => new TreatPick(s, s.Amount))],
                    Generation.Recipe(p.Available, [("191202", 61.7m), ("191204", 27.3m), ("191212", 11m)])))
            ]);

            var after = Waiting(db, otherId, day);

            Check("месецът от „Деметра“, образуван наведнъж, не оставя нищо в „Третиран“",
                after.Total == 0m && after.Unbacked.Count == 0,
                $"чакат {SplitMath.Amount(after.Total)} т, празни дялове {after.Unbacked.Count}, " +
                $"образувано {SplitMath.Amount(july.Total)} т");

            var issues = Reconcile.Check(
                db.Batches.List(otherId, "1"), db.Ledger.Read(otherId, "1"),
                db.Ledger.Links(otherId, "1"), new LedgerPeriod(2026, 7));

            Check("юли от „Деметра“ може да се затвори", Reconcile.CanClose(issues),
                string.Join(" | ", issues.Where(i => i.Blocks).Select(i => i.Text)));
        }
        else
        {
            Console.WriteLine("     (истинският износ от „Деметра“ го няма — пропуснато)");
        }
    }
    catch (Exception ex)
    {
        gramError = ex.ToString();
    }

    if (gramError is not null) Console.WriteLine("     " + gramError);

    var gramProblems = gramNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in gramProblems) Console.WriteLine("     " + problem);

    Check("образуването на всичко минава без изключение", gramError is null);
    Check($"направени са всичките {gramNotes.Count} проверки", gramNotes.Count == 17, $"{gramNotes.Count}");
    Check("образуваното от цяла купчина изпразва „Третиран“ до грам", gramProblems.Count == 0,
        $"{gramProblems.Count} проблема");

    // Третира всичко, което стои в „Получен“ — всяка купчина цяла.
    static void Treat(Workspace db, long companyId, DateOnly day)
    {
        var batches = db.Batches.List(companyId, "1");
        var shares = StockMath.Shares(
            batches, db.Ledger.Read(companyId, "1"), db.Ledger.Links(companyId, "1"), StoreKind.Received, day);

        foreach (var move in Treatment.Plan(Treatment.Piles(shares).Select(p => new TreatOrder(p, p.Available))))
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Treated, day, move.Ewc, move.Amount, Draws: move.Draws));
    }

    static StockMath.TreatedStock Waiting(Workspace db, long companyId, DateOnly day) =>
        StockMath.Waiting(
            db.Batches.List(companyId, "1"), db.Ledger.Read(companyId, "1"), db.Ledger.Links(companyId, "1"), day);

    static void Generate(Workspace db, long companyId, DateOnly day, params GenOrder[] orders)
    {
        foreach (var move in Generation.Plan(orders))
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Generated, day, move.Ewc, move.Amount, Draws: move.Draws));
    }
}

// =============================================== Рециклиране
//
// Петото движение. Рециклираното излиза от склад „Третиран“ точно като
// образуваното — същият прозорец, същите купчини и кодове, същото наследяване
// на произхода, — но НЕ влиза в отчета: това е образуван отпадък, който
// площадката изважда от сметката. Отива в свой склад-летопис, който само расте.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - рециклирането излиза от „Третиран“ и не влиза в отчета");
{
    var recNotes = new List<string>();
    string? recError = null;

    // Правилата — без база и без прозорци.
    recNotes.Add(Moves.From(MoveKind.Recycled) == StoreKind.Treated
        ? "" : "рециклирането не вади от „Третиран“");
    recNotes.Add(Moves.To(MoveKind.Recycled) == StoreKind.Recycled
        ? "" : "рециклирането не влиза в свой склад");
    recNotes.Add(!Moves.IsStock(StoreKind.Recycled) ? "" : "„Рециклиран“ се води за наличност, а е летопис");
    recNotes.Add(!Moves.InBook(MoveKind.Recycled) && Moves.All.All(Moves.InBook)
        ? "" : "кое влиза в книгата е сбъркано");
    recNotes.Add(!Moves.All.Contains(MoveKind.Recycled) && Moves.Every.Contains(MoveKind.Recycled)
        ? "" : "рециклирането стигна до движенията на книгата или липсва от всички");
    recNotes.Add(Moves.Stores[^1] == StoreKind.Recycled && Moves.Stores.Count == 5
        ? "" : "складът „Рециклиран“ не стои последен");
    recNotes.Add(Moves.Node(MoveKind.Recycled).Length == 0 &&
                 Moves.All.All(k => Moves.Of(Moves.Node(k)) == k)
        ? "" : "рециклирането има възел в книгата или разваля връзката на другите");

    // Изчерпва „Третиран“ като образуваното, по партиди.
    var day = new DateOnly(2026, 9, 10);
    var lots = new List<Batch>
    {
        new(1, "2026-0001", "203344556", "Метал ООД", "160216", new DateOnly(2026, 9, 1)),
        new(2, "2026-0002", "204455667", "Скрап ЕООД", "160216", new DateOnly(2026, 9, 2))
    };

    var flow = new List<Move>
    {
        new(1, MoveKind.Received, new DateOnly(2026, 9, 1), "160216", 12m),
        new(2, MoveKind.Received, new DateOnly(2026, 9, 2), "160216", 8m),
        new(3, MoveKind.Treated, day, "160216", 20m),
        new(4, MoveKind.Recycled, day, "191204", 5m),
        new(5, MoveKind.Generated, day, "191202", 15m)
    };

    var flowLinks = new List<LedgerLink>
    {
        new(1, 1, 12m), new(2, 2, 8m), new(3, 1, 12m), new(3, 2, 8m),
        new(4, 1, 3m), new(4, 2, 2m), new(5, 1, 9m), new(5, 2, 6m)
    };

    var beforeGen = StockMath.Waiting(lots, flow.Take(4), flowLinks, day);

    recNotes.Add(beforeGen.Total == 15m ? "" : $"след рециклиране чакат {beforeGen.Total} т вместо 15");
    recNotes.Add(StockMath.Waiting(lots, flow, flowLinks, day).Total == 0m
        ? "" : "образувано плюс рециклирано не изпразва „Третиран“");

    // Масата се сверява с образуваното И рециклираното.
    var september = new LedgerPeriod(2026, 9);

    recNotes.Add(Reconcile.Unbalanced(flow, september).Count == 0
        ? "" : "рециклираното се брои за загуба при третирането");

    var lossy = Reconcile.Unbalanced(
        [.. flow.Take(4), new Move(5, MoveKind.Generated, day, "191202", 12m)], september);

    recNotes.Add(lossy.Count == 1 && lossy[0].Amount == 3m &&
                 lossy[0].Text.Contains("рециклирани", StringComparison.Ordinal)
        ? "" : "разликата при рециклиране не го споменава: " + (lossy.Count > 0 ? lossy[0].Text : "няма"));

    recNotes.Add(StockMath.Totals(flow, september)[MoveKind.Recycled] == 5m
        ? "" : "сборът на месеца не знае за рециклираното");

    // Нито в отчета, нито в баланса.
    recNotes.Add(BookProjection.Submittable(flow, september, september).All(m => m.Kind != MoveKind.Recycled)
        ? "" : "рециклираното стига до отчета");
    recNotes.Add(StoreBalance.Entries(flow, 2026).Sum(e => e.Amount) == 12m + 8m + 20m + 15m
        ? "" : "рециклираното стига до баланса по склада");

    var recThread = new Thread(() =>
    {
        try
        {
            var recPath = Path.Combine(work, $"recycle_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(recPath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 1), "160216", 12m,
                Eik: "203344556", Partner: "Метал ООД"));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 2), "160216", 8m,
                Eik: "204455667", Partner: "Скрап ЕООД"));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Treated, new DateOnly(2026, 9, 10), "160216", 20m));

            using var page = new StockPage(service, db);

            page.UseDate(new DateOnly(2026, 9, 30));

            // Петият склад стои в превключвателя и се чете цял.
            recNotes.Add(page.StoreChoices.Count == 5 && page.StoreChoices[^1] == "Рециклиран"
                ? "" : "складът „Рециклиран“ го няма: " + string.Join(", ", page.StoreChoices));
            recNotes.Add(page.StoreFits ? "" : "превключвателят на складовете реже надписите");

            // В „Третиран“ главното действие си остава образуването; отделно
            // копче за рециклиране на склада няма — то е вторият изход от
            // прозореца на образуването.
            page.UseStore(StoreKind.Treated);
            recNotes.Add(page.GenerateOffered && page.ActionText == "Образувай…"
                ? "" : "образуването спря да е главното действие в „Третиран“");
            recNotes.Add(!page.RevertOffered ? "" : "връщане на рециклирано се предлага от „Третиран“");

            page.SelectMaster(0);
            var chosen = page.Selection();

            // Прозорецът на образуването, с копчето „Рециклирай“ до „Затвори“.
            using (var dialog = page.FormDialog(chosen))
            {
                dialog.ClientSize = new Size(1480, 900);

                recNotes.Add(dialog.Kind == MoveKind.Generated ? "" : "отваря се друг прозорец, не образуването");
                recNotes.Add(dialog.RecycleShown && dialog.RecycleText == "Рециклирай"
                    ? "" : "копчето „Рециклирай“ го няма в прозореца на образуването");
                recNotes.Add(dialog.SaveReachable ? "" : "копчето за записване не се побира в прозореца");
                recNotes.Add(dialog.ToolsFit ? "" : "лентата с шаблоните е отрязана");

                dialog.UseDate(new DateOnly(2026, 9, 12));
                dialog.PickPile(0);

                // От двете партиди, посочено на ръка — 3 т от Метал и 2 т от
                // Скрап: така всяко рециклиране носи по два реда и после може да
                // се върне само единият.
                dialog.UseBatchTake(0, 3m);
                dialog.UseBatchTake(1, 2m);
                dialog.UsePartCount(2);
                dialog.UsePartCode(0, "191204");
                dialog.UsePartCode(1, "191212");
                dialog.UsePartAmount(0, 3m);
                dialog.UseBalance();

                var planned = dialog.Planned();

                recNotes.Add(planned.Sum(m => m.Amount) == 5m && planned.Count == 2
                    ? "" : $"сглобени са {planned.Count} реда за {planned.Sum(m => m.Amount)} т");

                // „Рециклирай“ записва избраното като рециклирано и оставя
                // прозореца отворен — за да се образува останалото.
                dialog.RecycleNow();

                recNotes.Add(dialog.RecycledCount == 2
                    ? "" : $"„Рециклирай“ записа {dialog.RecycledCount} реда вместо два");
                recNotes.Add(dialog.DoneText.Contains("рециклиран", StringComparison.Ordinal)
                    ? "" : "прозорецът не казва, че е рециклирано: " + dialog.DoneText);
                recNotes.Add(dialog.RowCount == 1 && dialog.StockAt(0) == "15,000"
                    ? "" : "след рециклирането не чака останалото: " + (dialog.RowCount > 0 ? dialog.StockAt(0) : "няма"));
            }

            // Поправката от дневника отваря рециклирането в същия прозорец, с
            // другия край — и там второ „Рециклирай“ няма.
            using (var fix = page.RecycleDialog(chosen))
            {
                recNotes.Add(fix.Kind == MoveKind.Recycled && fix.SaveText == "Рециклирай и затвори"
                    ? "" : "поправката на рециклиране не се отваря като рециклиране: " + fix.SaveText);
                recNotes.Add(fix.DateCaptionText == "Дата на рециклиране" && fix.ExtraCount == 0
                    ? "" : $"поправката показва полетата на книгата: {fix.ExtraCount}");
                recNotes.Add(!fix.RecycleShown ? "" : "в прозореца на рециклирането стои второ „Рециклирай“");
            }

            // Формирането показва полетата на книгата — рециклирането не.
            using (var form = new GenerateDialog(service, db, companyId, "1", chosen, new DateOnly(2026, 9, 30)))
                recNotes.Add(form.Kind == MoveKind.Generated && form.ExtraCount == 2
                    ? "" : $"образуването загуби полетата си: {form.ExtraCount}");

            var recycled = db.Ledger.Read(companyId, "1").Where(m => m.Kind == MoveKind.Recycled).ToList();

            recNotes.Add(recycled.Count == 2 && recycled.Sum(m => m.Amount) == 5m
                ? "" : "в базата рециклирането не се чете обратно като рециклиране");
            recNotes.Add(recycled.All(m => db.Ledger.Draws(m.Id).Sum(d => d.Amount) == m.Amount)
                ? "" : "рециклираното не носи произхода си по партиди");
            recNotes.Add(db.Actions.List(companyId).Count(e => e.Kind == MoveKind.Recycled) == 2
                ? "" : "дневникът не помни рециклирането като рециклиране");

            // „Третиран“ е изчерпан с рециклираното; „Образуван“ не е пипнат.
            page.Refresh();
            page.UseStore(StoreKind.Treated);
            recNotes.Add(page.SummaryText.Contains("15,000", StringComparison.Ordinal)
                ? "" : "„Третиран“ не намаля с рециклираното: " + page.SummaryText);

            page.UseStore(StoreKind.Formed);
            recNotes.Add(page.RowCount == 0 ? "" : "рециклираното стигна до „Образуван“");

            // Летописът „Рециклиран“.
            page.UseStore(StoreKind.Recycled);
            recNotes.Add(page.RowCount == 2 ? "" : $"„Рециклиран“ показва {page.RowCount} реда вместо два");
            recNotes.Add(page.SummaryText.Contains("Рециклирани 5,000 т", StringComparison.Ordinal) &&
                         page.SummaryText.Contains("извън отчета", StringComparison.Ordinal)
                ? "" : "летописът не се чете като рециклирано: " + page.SummaryText);
            recNotes.Add(page.AmountHeaderText.StartsWith("Рециклирано", StringComparison.Ordinal)
                ? "" : "стълбът се казва " + page.AmountHeaderText);

            // Тук действието е „Върни“ — и копчето горе, и копчето на реда.
            recNotes.Add(page.RevertOffered && page.ActionShown && page.ActionText == "Върни в „Третиран“…"
                ? "" : "в „Рециклиран“ няма копче за връщане: " + page.ActionText);
            recNotes.Add(page.RowActionShown && page.RowActionText == "Върни"
                ? "" : "копчето на реда не връща: " + page.RowActionText);

            // Всяко рециклиране е свой ред, с датата си.
            recNotes.Add(page.DetailDateShown && page.DetailDateHeader == "Рециклирано на"
                ? "" : "долу не се казва кога е рециклирано: " + page.DetailDateHeader);

            // Поправката отваря рециклирането в прозореца му.
            var entry = db.Actions.List(companyId).First(e => e.Kind == MoveKind.Recycled);
            var seed = page.Prepare(entry, out var again, out var why);

            recNotes.Add(seed is { Kind: MoveKind.Recycled } && again.Count > 0
                ? "" : "рециклирането не се отваря за поправка: " + why);

            // Намереното в дневника отваря летописа за месеца, не таблица на месеца.
            page.UseStore(StoreKind.Received);
            page.ShowRecycledMonth(september);
            recNotes.Add(page.RowCount == 2 && page.MonthOnlyOn
                ? "" : "дневникът не води до рециклираното за месеца");

            // Останалото се образува — складът се изпразва и месецът се отчита.
            var rest = Generation.Piles(
                StockMath.Waiting(db.Batches.List(companyId, "1"), db.Ledger.Read(companyId, "1"),
                    db.Ledger.Links(companyId, "1"), new DateOnly(2026, 9, 30)).Shares)[0];

            page.ApplyGenerated(
                Generation.Plan([new GenOrder(rest, [.. rest.Shares.Select(s => new TreatPick(s, s.Amount))],
                    [new GenPart("191202", rest.Available)])]),
                new DateOnly(2026, 9, 15));

            using var ledger = new LedgerPage(service, db);
            ledger.UseMonth(september);

            recNotes.Add(ledger.PendingTreated(september) == 0m ? "" : "образувано и рециклирано не изпразват месеца");
            recNotes.Add(ledger.CanCompose(september, september) ? "" : "отчетът не може да се състави");
            recNotes.Add(ledger.SummaryText.Contains("рециклирано 5,000 т", StringComparison.Ordinal)
                ? "" : "месецът не казва колко е рециклирано: " + ledger.SummaryText);
            recNotes.Add(ledger.KindChoices.Contains("Рециклиран") && ledger.KindFits
                ? "" : "ситото по вид не знае рециклирането или реже надписите");

            var issues = LedgerChecks.For(db, companyId, "1", september);
            recNotes.Add(Reconcile.CanClose(issues) && issues.All(i => i.Kind != LedgerProblem.Unbalanced)
                ? "" : "месецът с рециклиране не се затваря: " + string.Join(" | ", issues.Select(i => i.Text)));

            // Отчетът: образуваните 15 т са в него, рециклираните 5 т — не.
            var book = service.GetSchema("book");
            var rows = BookProjection.Submittable(db.Ledger.Read(companyId, "1"), september, september)
                .Select(m => new BookRow(m.Kind, m.Date, m.Ewc, m.Amount, m.Stagenum, db.Ledger.Fields(m.Id), m.EwcOut))
                .ToList();

            var byNode = BookProjection.Rows(book, rows);
            var generatedRows = byNode.GetValueOrDefault(Moves.Node(MoveKind.Generated)) ?? [];

            recNotes.Add(generatedRows.Sum(r => decimal.Parse(r["amount_ton"], System.Globalization.CultureInfo.InvariantCulture)) == 15m
                ? "" : "в отчета образуваното не е 15 т");
            recNotes.Add(generatedRows.All(r => r["ewc"] != "191204" && r["ewc"] != "191212")
                ? "" : "рециклираните кодове стигнаха до отчета");

            var built = BookProjection.Build(book, BookProjection.Header("203344556", september, september), rows);

            recNotes.Add(built.Sheets.SelectMany(s => s.Rows)
                    .All(r => r.Get("ewc").Text is not ("191204" or "191212"))
                ? "" : "рециклираните кодове стигнаха до листовете на отчета");

            // Баланс по склада — само каквото е в книгата.
            recNotes.Add(StoreBalance.Entries(db.Ledger.Read(companyId, "1"), 2026)
                    .Where(e => e.Ewc is "191204" or "191212").Sum(e => e.Amount) == 0m
                ? "" : "рециклираното стигна до баланса");

            // Записано без партиди (както от „Месец“) — наследява от чакащото.
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 10, 1), "160216", 4m, Eik: "203344556", Partner: "Метал ООД"));
            db.Writer.Write(companyId, new MoveRequest(MoveKind.Treated, new DateOnly(2026, 10, 2), "160216", 4m));
            var bare = db.Writer.Write(companyId, new MoveRequest(MoveKind.Recycled, new DateOnly(2026, 10, 3), "191204", 4m));

            recNotes.Add(db.Ledger.Draws(bare).Sum(d => d.Amount) == 4m
                ? "" : "рециклирано без посочени партиди не наследява произход");

            // „Върни“: сгрешеното рециклиране се връща ред по ред. Един ред е
            // дялът на една партида в едно рециклиране.
            page.Refresh();
            page.UseDate(new DateOnly(2026, 9, 30));
            page.UseStore(StoreKind.Recycled);

            var before = Treated(db, companyId);

            page.Pick(0);                               // 191204 — 3 т от двете партиди
            page.SelectDetail(0);                       // само едната партида
            var one = page.LineSelection();

            recNotes.Add(one.Count == 1 && one[0].Single ? "" : $"избраният ред не е едно рециклиране: {one.Count}");

            var touched = page.Revert(one, ask: false);
            var copper = db.Ledger.Read(companyId, "1").Single(m => m.Kind == MoveKind.Recycled && m.Ewc == "191204" && m.Date.Month == 9);

            recNotes.Add(touched == 1 && copper.Amount == SplitMath.Round(3m - one[0].Amount)
                ? "" : $"връщането на един ред остави {copper.Amount} т вместо остатъка");
            recNotes.Add(db.Ledger.Draws(copper.Id).Count == 1 && db.Ledger.Draws(copper.Id).Sum(d => d.Amount) == copper.Amount
                ? "" : "олекналото рециклиране не носи точно останалия си дял");
            recNotes.Add(Treated(db, companyId) == SplitMath.Round(before + one[0].Amount)
                ? "" : $"върнатото не се появи в „Третиран“: {Treated(db, companyId)} т");

            // Всичко останало наведнъж: двете рециклирания си отиват.
            page.Refresh();
            page.UseStore(StoreKind.Recycled);
            page.SelectMaster(0, 1);

            page.Revert(page.LineSelection(), ask: false);

            recNotes.Add(db.Ledger.Read(companyId, "1").Count(m => m.Kind == MoveKind.Recycled && m.Date.Month == 9) == 0
                ? "" : "върнатите изцяло рециклирания останаха в базата");
            recNotes.Add(Treated(db, companyId) == SplitMath.Round(before + 5m)
                ? "" : $"„Третиран“ не си получи обратно петте тона: {Treated(db, companyId)} т");
            recNotes.Add(db.Actions.List(companyId).Where(e => e.Kind == MoveKind.Recycled && e.Date.Month == 9).All(e => !e.Alive)
                ? "" : "дневникът не казва, че върнатото рециклиране го няма");

            // Затворен месец не се пипа — и при връщане.
            db.Periods.Close(companyId, "1", new LedgerPeriod(2026, 10));
            page.UseDate(new DateOnly(2026, 10, 31));
            page.UseStore(StoreKind.Recycled);
            page.SelectMaster(0);

            recNotes.Add(page.Revert(page.LineSelection(), ask: false) == 0 &&
                         db.Ledger.Read(companyId, "1").Any(m => m.Id == bare)
                ? "" : "върна се рециклиране от затворен месец");

            db.Periods.Reopen(companyId, "1", new LedgerPeriod(2026, 10));

            static decimal Treated(Workspace db, long companyId) =>
                StockMath.Waiting(db.Batches.List(companyId, "1"), db.Ledger.Read(companyId, "1"),
                    db.Ledger.Links(companyId, "1"), new DateOnly(2026, 9, 30)).Total;
        }
        catch (Exception ex)
        {
            recError = ex.ToString();
        }
    });

    recThread.SetApartmentState(ApartmentState.STA);
    recThread.Start();
    recThread.Join();

    if (recError is not null) Console.WriteLine("     " + recError);

    var recProblems = recNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in recProblems) Console.WriteLine("     " + problem);

    Check("рециклирането минава без изключение", recError is null);
    Check($"направени са всичките {recNotes.Count} проверки", recNotes.Count == 61, $"{recNotes.Count}");
    Check("рециклираното излиза от „Третиран“ и не стига до отчета", recProblems.Count == 0,
        $"{recProblems.Count} проблема");
}

// =============================================== Изтритото от дневника не е имало
//
// Дневникът съществува, за да пази от грешки: изтрито оттам движение трябва да
// върне количеството си там, откъдето е дошло, и да не остави нищо. Наличността
// се смята от движенията и връщането идва само; трудното е веригата. Махнато от
// средата, движението връщаше количеството назад, а минало напред си оставаше
// там: десет приети тона ставаха десет в „Получен“ и десет в „Предаден“.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - изтритото от дневника се връща в склада, все едно никога не се е случвало");
{
    var undoNotes = new List<string>();
    string? undoError = null;

    // Сметката, без прозорец: кое пречи и кое не.
    static Move M(long id, MoveKind kind, int day, string ewc, decimal amount, bool direct = false) =>
        new(id, kind, new DateOnly(2026, 9, day), ewc, amount, Direct: direct);

    static LedgerLink L(long move, long batch, decimal amount) => new(move, batch, amount);

    static string Takers(IReadOnlyList<Move> moves, IReadOnlyList<LedgerLink> links, params long[] gone) =>
        string.Join(",", Undo.Takers(moves, links, gone.ToHashSet()).Select(m => m.Id));

    // Партидата покрива: от две третирания по пет са взети четири — едното
    // може да си отиде. От шест — вече не.
    {
        var moves = new[] { M(1, MoveKind.Treated, 1, "160216", 5m), M(2, MoveKind.Treated, 2, "160216", 5m), M(3, MoveKind.Generated, 3, "191202", 4m) };
        var links = new[] { L(1, 9, 5m), L(2, 9, 5m), L(3, 9, 4m) };

        undoNotes.Add(Takers(moves, links, 2) == "" ? "" : "покритото образуване спира изтриването");

        var more = new[] { M(1, MoveKind.Treated, 1, "160216", 5m), M(2, MoveKind.Treated, 2, "160216", 5m), M(3, MoveKind.Generated, 3, "191202", 6m) };
        var moreLinks = new[] { L(1, 9, 5m), L(2, 9, 5m), L(3, 9, 6m) };

        undoNotes.Add(Takers(more, moreLinks, 2) == "3" ? "" : "непокритото образуване не спира изтриването");

        // И махнати заедно не си пречат: веригата си отива цяла.
        undoNotes.Add(Takers(more, moreLinks, 1, 2, 3) == "" ? "" : "движение спира изтриване, в което само участва");
    }

    // Гледа се всеки ден: накрая може да излиза на плюс, а по средата да е
    // взето от изтриваното.
    {
        var moves = new[] { M(1, MoveKind.Treated, 1, "160216", 5m), M(2, MoveKind.Generated, 2, "191202", 3m), M(3, MoveKind.Treated, 3, "160216", 5m) };
        var links = new[] { L(1, 9, 5m), L(2, 9, 3m), L(3, 9, 5m) };

        undoNotes.Add(Takers(moves, links, 3) == "" ? "" : "по-късното третиране се спира от по-ранно образуване");
        undoNotes.Add(Takers(moves, links, 1) == "2" ? "" : "денят, в който е взето, не се вижда");
    }

    // Купчината в „Образуван“ е кодът И партидата: предадената мед виси на
    // образуваната мед, не на пластмасата от същата партида.
    {
        var moves = new[]
        {
            M(1, MoveKind.Generated, 1, "191202", 3m),
            M(2, MoveKind.Generated, 1, "191204", 3m),
            M(3, MoveKind.Submitted, 2, "191202", 3m)
        };
        var links = new[] { L(1, 9, 3m), L(2, 9, 3m), L(3, 9, 3m) };

        undoNotes.Add(Takers(moves, links, 2) == "" ? "" : "предадената мед спира пластмасата");
        undoNotes.Add(Takers(moves, links, 1) == "3" ? "" : "предадената мед не спира образуваната мед");
    }

    // Предаване без партида не се вижда през дяловете, но количеството му пак е
    // излязло от склада и виси на образуваното.
    {
        var moves = new[] { M(1, MoveKind.Generated, 1, "191202", 10m), M(2, MoveKind.Submitted, 2, "191202", 10m) };

        undoNotes.Add(Takers(moves, [L(1, 9, 10m)], 1) == "2" ? "" : "предаване без партида не спира образуването");
    }

    // Заварените грамове не спират чуждо: каквото е било на минус, не е работа
    // на това изтриване. Каквото го сваля още — е.
    {
        var moves = new[] { M(1, MoveKind.Treated, 1, "160216", 1.029m), M(2, MoveKind.Generated, 2, "191202", 1.030m) };
        var links = new[] { L(1, 9, 1.029m), L(2, 9, 1.030m) };

        undoNotes.Add(Takers(moves, links, 2) == "" ? "" : "заварен грам спира образуване, което го оправя");
        undoNotes.Add(Takers(moves, links, 1) == "2" ? "" : "третиране с образувано зад него минава, защото е имало грам");
    }

    // Минус отпреди изтриваното не е негова работа. Образувано с минала дата
    // стои на минус, докато дойде третирането (дефект 7), и това не бива да спира
    // друго третиране, след което купчината пак излиза на плюс.
    {
        var moves = new[] { M(1, MoveKind.Generated, 1, "191202", 3m), M(2, MoveKind.Treated, 2, "160216", 5m), M(3, MoveKind.Treated, 3, "160216", 5m) };
        var links = new[] { L(1, 9, 3m), L(2, 9, 5m), L(3, 9, 5m) };

        undoNotes.Add(Takers(moves, links, 3) == "" ? "" : "старият минус спира третиране, което не го пипа");
        undoNotes.Add(Takers(moves, links, 2, 3) == "1" ? "" : "двете третирания си отидоха, а образуваното виси на тях");
    }

    // Излязлото от площадката не носи нищо след себе си.
    {
        var moves = new[]
        {
            M(1, MoveKind.Generated, 1, "191202", 5m),
            M(2, MoveKind.Submitted, 2, "191202", 5m),
            M(3, MoveKind.Recycled, 2, "191204", 1m)
        };
        var links = new[] { L(1, 9, 5m), L(2, 9, 5m), L(3, 9, 1m) };

        undoNotes.Add(Takers(moves, links, 2) == "" && Takers(moves, links, 3) == ""
            ? "" : "предаването или рециклирането спират собственото си изтриване");
    }

    var undoThread = new Thread(() =>
    {
        try
        {
            var end = new DateOnly(2026, 9, 30);
            var september = new LedgerPeriod(2026, 9);

            static Dictionary<string, string> To(string name, string eik) =>
                new(StringComparer.Ordinal) { ["name_receiver"] = name, ["pin_receiver"] = eik };

            // --- веригата: прието, третирано, образувано, предадено
            {
                using var db = Workspace.Open(Path.Combine(work, $"undo_{Guid.NewGuid():N}.db"));
                var companyId = db.Companies.Add("203344556", "Метал ООД");

                db.Writer.Write(companyId, new MoveRequest(
                    MoveKind.Received, new DateOnly(2026, 9, 1), "160216", 10m, Eik: "204455667", Partner: "Скрап ЕООД"));

                using var page = new StockPage(service, db);
                page.UseDate(end);

                IReadOnlyList<Batch> Batches() => db.Batches.List(companyId, "1");
                IReadOnlyList<Move> Read() => db.Ledger.Read(companyId, "1");
                IReadOnlyList<LedgerLink> Links() => db.Ledger.Links(companyId, "1");

                string Stores()
                {
                    int Of(StoreKind store) => (int)StockMath.Shares(Batches(), Read(), Links(), store, end).Sum(s => s.Amount);

                    return $"{Of(StoreKind.Received)}/{(int)StockMath.Waiting(Batches(), Read(), Links(), end).Total}/" +
                           $"{Of(StoreKind.Formed)}/{Of(StoreKind.Submitted)}";
                }

                var received = Treatment.Piles(StockMath.Shares(Batches(), Read(), Links(), StoreKind.Received, end))[0];

                page.Apply(Treatment.Plan([new TreatOrder(received, 10m)]), new DateOnly(2026, 9, 2));

                var waiting = Generation.Piles(StockMath.Waiting(Batches(), Read(), Links(), end).Shares)[0];

                page.ApplyGenerated(
                    Generation.Plan([new GenOrder(waiting, [.. waiting.Shares.Select(s => new TreatPick(s, s.Amount))],
                        [new GenPart("191202", waiting.Available)])]),
                    new DateOnly(2026, 9, 3));

                var formed = Handover.Piles(StockMath.Shares(Batches(), Read(), Links(), StoreKind.Formed, end))[0];

                page.ApplySubmitted(
                    Handover.Plan([new HandOrder(formed, 10m)]), new DateOnly(2026, 9, 4), To("Претопи ООД", "205566778"));

                undoNotes.Add(Stores() == "0/0/0/10" ? "" : "веригата не стигна до „Предаден“: " + Stores());

                using var log = new ActionLogDialog(db, companyId, "1");

                for (var i = 0; i < 3; i++)
                {
                    log.Pick(0);
                    log.TrashNow();
                }

                log.UseBin(true);

                // Най-новото е отгоре: предаване, образуване, третиране.
                undoNotes.Add(log.RowCount == 3 ? "" : $"„Отложено“ показва {log.RowCount} реда вместо три");

                // Точно това правеше двайсет тона от десет.
                log.Pick(2);
                log.DeleteNow();

                undoNotes.Add(Stores() == "0/0/0/10" ? "" : "третирането се изтри, а от него е образувано: " + Stores());
                undoNotes.Add(log.RowCount == 3 ? "" : "спряното изтриване махна реда");
                undoNotes.Add(log.Refusal.Contains("Образуван · 191202", StringComparison.Ordinal)
                    ? "" : "не се казва кое стои на пътя: " + log.Refusal);
                undoNotes.Add(!log.Refusal.Contains("„Месец“", StringComparison.Ordinal)
                    ? "" : "праща към „Месец“ движение, което е в дневника");

                log.Pick(1);
                log.DeleteNow();

                undoNotes.Add(Stores() == "0/0/0/10" ? "" : "образуването се изтри, а от него е предадено: " + Stores());
                undoNotes.Add(log.Refusal.Contains("Предаден · 191202", StringComparison.Ordinal)
                    ? "" : "не се казва кое предаване стои на пътя: " + log.Refusal);

                // Отзад напред: всяко количество се връща там, откъдето е дошло.
                log.Pick(0);
                log.DeleteNow();

                undoNotes.Add(log.Refusal.Length == 0 ? "" : "последното в реда се спира: " + log.Refusal);
                undoNotes.Add(Stores() == "0/0/10/0" ? "" : "изтритото предаване не се върна в „Образуван“: " + Stores());
                undoNotes.Add(log.SummaryText.Contains("обратно в „Образуван“", StringComparison.Ordinal)
                    ? "" : "не се казва къде се е върнало: " + log.SummaryText);

                log.Pick(0);
                log.DeleteNow();

                undoNotes.Add(Stores() == "0/10/0/0" ? "" : "изтритото образуване не се върна в „Третиран“: " + Stores());

                log.Pick(0);
                log.DeleteNow();

                undoNotes.Add(Stores() == "10/0/0/0" ? "" : "изтритото третиране не се върна в „Получен“: " + Stores());

                // Никаква следа: нито в склада, нито в дневника, нито сметката.
                undoNotes.Add(Read().Count == 1 && Links().Count == 1 ? "" : "остана движение или дял от изтритото");
                undoNotes.Add(log.RowCount == 0 && db.Actions.List(companyId, "1").Count == 0
                    ? "" : "изтритото остана в дневника");
                undoNotes.Add(log.Changed ? "" : "страницата отзад няма да научи за изтриването");

                var left = LedgerChecks.For(db, companyId, "1", september);

                undoNotes.Add(left.All(i => i.Kind is not (LedgerProblem.Unbalanced or LedgerProblem.Negative))
                    ? "" : "след изтриването остава контрола: " + string.Join(" | ", left.Select(i => i.Text)));
            }

            // --- каквото го няма в дневника, се търси другаде
            {
                using var db = Workspace.Open(Path.Combine(work, $"undo_{Guid.NewGuid():N}.db"));
                var companyId = db.Companies.Add("203344556", "Метал ООД");

                db.Writer.Write(companyId, new MoveRequest(
                    MoveKind.Received, new DateOnly(2026, 9, 1), "160216", 10m, Eik: "204455667", Partner: "Скрап ЕООД"));

                using var page = new StockPage(service, db);
                page.UseDate(end);

                var pile = Treatment.Piles(StockMath.Shares(db.Batches.List(companyId, "1"), db.Ledger.Read(companyId, "1"),
                    db.Ledger.Links(companyId, "1"), StoreKind.Received, end))[0];

                page.Apply(Treatment.Plan([new TreatOrder(pile, 10m)]), new DateOnly(2026, 9, 2));

                // Записано покрай прозорците — както от „Месец“: в дневника го няма.
                db.Writer.Write(companyId, new MoveRequest(MoveKind.Generated, new DateOnly(2026, 9, 3), "191202", 10m));

                using var log = new ActionLogDialog(db, companyId, "1");

                log.Pick(0);
                log.TrashNow();
                log.UseBin(true);
                log.Pick(0);
                log.DeleteNow();

                undoNotes.Add(log.Refusal.Contains("от „Месец“", StringComparison.Ordinal)
                    ? "" : "не се казва къде е движението, което го няма в дневника: " + log.Refusal);
            }

            // --- цяло действие
            {
                using var db = Workspace.Open(Path.Combine(work, $"undo_{Guid.NewGuid():N}.db"));
                var companyId = db.Companies.Add("203344556", "Метал ООД");

                db.Writer.Write(companyId, new MoveRequest(
                    MoveKind.Received, new DateOnly(2026, 9, 1), "160216", 12m, Eik: "204455667", Partner: "Скрап ЕООД"));
                db.Writer.Write(companyId, new MoveRequest(
                    MoveKind.Received, new DateOnly(2026, 9, 1), "150101", 8m, Eik: "204455667", Partner: "Скрап ЕООД"));

                using var page = new StockPage(service, db);
                page.UseDate(end);

                IReadOnlyList<TreatPile> Piles() => Handover.Piles(StockMath.Shares(db.Batches.List(companyId, "1"),
                    db.Ledger.Read(companyId, "1"), db.Ledger.Links(companyId, "1"), StoreKind.Received, end));

                decimal Left() => StockMath.Shares(db.Batches.List(companyId, "1"), db.Ledger.Read(companyId, "1"),
                    db.Ledger.Links(companyId, "1"), StoreKind.Received, end).Sum(s => s.Amount);

                int Handed() => db.Ledger.Read(companyId, "1").Count(m => m.Kind == MoveKind.Submitted);

                void Hand(int day)
                {
                    var piles = Piles();

                    page.ApplySubmitted(
                        Handover.Plan([new HandOrder(piles[0], 4m), new HandOrder(piles[1], 6m)]),
                        new DateOnly(2026, 9, day), To("Претопи ООД", "205566778"), direct: true);
                }

                Hand(5);
                Hand(6);

                undoNotes.Add(Left() == 0m && Handed() == 4 ? "" : $"двете предавания не изпразниха склада: {Left()}, {Handed()}");

                using var log = new ActionLogDialog(db, companyId, "1");

                // Настрана отива само редът. Съседът му остава във „Въведено“.
                log.Pick(0);
                log.TrashNow();
                undoNotes.Add(log.RowCount == 3 ? "" : "„Отложи“ взе повече от реда");

                log.UseBin(true);
                log.Pick(0);

                undoNotes.Add(log.WholeOffered ? "" : "цялото действие не се предлага");

                // Заключеното и затвореното спират и цялото.
                db.Settings.Set(SettingsStore.Keys.LogDeleteLocked, true);
                log.UseBin(true);
                log.Pick(0);
                log.DeleteWholeNow();
                undoNotes.Add(Handed() == 4 ? "" : "заключеното изтриване изтри цяло действие");
                db.Settings.Set(SettingsStore.Keys.LogDeleteLocked, false);

                db.Periods.Close(companyId, "1", september);
                log.UseBin(true);
                log.Pick(0);
                log.DeleteWholeNow();
                undoNotes.Add(Handed() == 4 ? "" : "от затворен месец се изтри цяло действие");
                db.Periods.Reopen(companyId, "1", september);

                // Цялото: и редът, останал във „Въведено“, си отива с него.
                log.UseBin(true);
                log.Pick(0);
                log.DeleteWholeNow();

                undoNotes.Add(Left() == 10m ? "" : $"цялото действие върна {Left()} т вместо десет");
                undoNotes.Add(Handed() == 2 ? "" : $"останаха {Handed()} предавания вместо две");
                undoNotes.Add(db.Actions.List(companyId, "1").Count == 2 ? "" : "редът от „Въведено“ остана в дневника");

                // Само редът: другият ред от действието си стои.
                log.UseBin(false);
                log.Pick(0);
                log.TrashNow();
                log.UseBin(true);
                log.Pick(0);
                log.DeleteNow();

                undoNotes.Add(Handed() == 1 ? "" : "изтриването на ред взе и съседа му");
                undoNotes.Add(Left() is 14m or 16m ? "" : $"върнати са {Left()} т вместо реда");

                // Останал сам, редът няма какво цяло да предлага.
                log.UseBin(false);
                log.Pick(0);
                log.TrashNow();
                log.UseBin(true);
                log.Pick(0);

                undoNotes.Add(!log.WholeOffered ? "" : "цялото действие се предлага за един ред");
            }
        }
        catch (Exception ex)
        {
            undoError = ex.ToString();
        }
    });

    undoThread.SetApartmentState(ApartmentState.STA);
    undoThread.Start();
    undoThread.Join();

    if (undoError is not null) Console.WriteLine("     " + undoError);

    var undoProblems = undoNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in undoProblems) Console.WriteLine("     " + problem);

    Check("изтриването от дневника минава без изключение", undoError is null);
    Check($"направени са всичките {undoNotes.Count} проверки", undoNotes.Count == 42, $"{undoNotes.Count}");
    Check("изтритото се връща в склада и не оставя следа; веригата се трие отзад напред", undoProblems.Count == 0,
        $"{undoProblems.Count} проблема");
}


// =============================================== Договорът на предаването
//
// Всичко, което отива в „Предаден“, може да носи договор. Полето е по избор, но
// се води като получателите: предлага вече писаното, новото влиза в списъка, а
// от „Настройки“ списъкът се поправя — иначе един договор, изписан по два начина,
// са два реда в ситото и търсенето по него лъже.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - договорът на предаването се предлага, научава и не се изписва по два начина");
{
    var dealNotes = new List<string>();
    string? dealError = null;

    var dealThread = new Thread(() =>
    {
        try
        {
            using var db = Workspace.Open(Path.Combine(work, $"deal_{Guid.NewGuid():N}.db"));
            var companyId = db.Companies.Add("203344556", "Метал ООД");

            var end = new DateOnly(2026, 9, 30);
            var september = new LedgerPeriod(2026, 9);

            static Dictionary<string, string> To(string name, string eik) =>
                new(StringComparer.Ordinal) { ["name_receiver"] = name, ["pin_receiver"] = eik };

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 1), "160216", 40m, Eik: "204455667", Partner: "Скрап ЕООД"));

            using var page = new StockPage(service, db);
            page.UseDate(end);

            IReadOnlyList<StockShare> Stock(StoreKind store) => StockMath.Shares(db.Batches.List(companyId, "1"),
                db.Ledger.Read(companyId, "1"), db.Ledger.Links(companyId, "1"), store, end);

            IReadOnlyList<Partner> Known() => db.Partners.List(companyId, PartnerRole.Contract);

            Move Last() => db.Ledger.Read(companyId, "1").Where(m => m.Kind == MoveKind.Submitted).OrderBy(m => m.Id).Last();

            void Hand(decimal amount, string contract, string note = "") => page.ApplySubmitted(
                Handover.Plan([new HandOrder(Handover.Piles(Stock(StoreKind.Received))[0], amount)]),
                new DateOnly(2026, 9, 5), To("Претопи ООД", "205566778"), note, true, contract);

            // Новото влиза в списъка при записа.
            Hand(2m, "Договор 12/2026", "Камион 7");

            dealNotes.Add(db.Ledger.Contract(Last().Id) == "Договор 12/2026" ? "" : "договорът не стигна до движението");
            dealNotes.Add(Known().Count == 1 && Known()[0].Name == "Договор 12/2026" ? "" : "новият договор не се научи");
            dealNotes.Add(Known()[0].Eik.Length == 0 ? "" : "договорът получи ЕИК");

            // Написаното иначе става такова, каквото е в списъка.
            Hand(2m, "  договор   12/2026 ");

            dealNotes.Add(db.Ledger.Contract(Last().Id) == "Договор 12/2026"
                ? "" : "написаното по друг начин не се изравни: „" + db.Ledger.Contract(Last().Id) + "“");
            dealNotes.Add(Known().Count == 1 ? "" : $"един договор стана {Known().Count} записа");
            dealNotes.Add(Known()[0].Used == 2 ? "" : $"договорът е ползван {Known()[0].Used} пъти вместо два");

            // По избор: празното минава и нищо не се научава.
            Hand(2m, "   ");

            dealNotes.Add(db.Ledger.Contract(Last().Id).Length == 0 && Known().Count == 1
                ? "" : "празният договор се запази или се научи");

            // Само предаването го носи, и в отчета не отива.
            var treated = db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Treated, new DateOnly(2026, 9, 6), "160216", 1m, Contract: "Чужд"));

            dealNotes.Add(db.Ledger.Contract(treated).Length == 0 && Known().Count == 1
                ? "" : "договорът се залепи за друг вид движение");

            db.Ledger.Remove(treated);

            dealNotes.Add(!db.Ledger.Fields(Last().Id).Keys.Any(k => k.Contains("contract", StringComparison.OrdinalIgnoreCase))
                ? "" : "договорът влезе при колоните за отчета");

            // Прозорецът: предлага списъка, а „Предай и продължи“ го опреснява.
            using (var dialog = new SubmitDialog(service, db, companyId, "1", Stock(StoreKind.Received), end, direct: true))
            {
                // Лаптоп при 100 %, на цял екран: единайсет полета по четири на ред.
                dialog.WindowState = FormWindowState.Normal;
                dialog.MinimumSize = Size.Empty;
                dialog.ClientSize = new Size(1366, 1000);
                dialog.PerformLayout();

                dealNotes.Add(dialog.FieldsFit ? "" : "с договора полетата не се побират в рамката си");

                dealNotes.Add(dialog.ContractsOffered == 1 && dialog.ContractAt(0) == "Договор 12/2026"
                    ? "" : $"полето предлага {dialog.ContractsOffered} договора");

                dialog.Write = (moves, date, fields, note) =>
                    page.ApplySubmitted(moves, date, fields, note, true, dialog.Contract);

                dialog.UseField("type_receiver_code", "01");
                dialog.UseField("name_receiver", "Претопи ООД");
                dialog.UseField("pin_receiver", "205566778");
                dialog.UseOperation(0);
                dialog.UseField("is_for_reuse", "0");

                dialog.UseContract("Анекс 3 към 12/2026");
                dialog.UseTake(0, 1m);

                var stop = dialog.Blocking();

                dealNotes.Add(stop.Count == 0 ? "" : "записът е спрян: " + string.Join(" | ", stop));

                dialog.HandAndContinue();

                dealNotes.Add(db.Ledger.Contract(Last().Id) == "Анекс 3 към 12/2026" ? "" : "прозорецът не записа договора");
                dealNotes.Add(dialog.ContractsOffered == 2 ? "" : "новият договор не е под ръка за следващия курс");
                dealNotes.Add(dialog.ContractText == "Анекс 3 към 12/2026" ? "" : "договорът изчезна след „и продължи“");
            }

            // Поправка: договорът се отваря със записаното и оцелява.
            var entry = db.Actions.List(companyId, "1").First();
            var seed = page.Prepare(entry, out var scope, out var why);

            dealNotes.Add(seed?.Contract == "Анекс 3 към 12/2026" ? "" : "поправката не носи договора: " + why);

            using (var fix = new SubmitDialog(service, db, companyId, "1", scope, seed!.Date, seed.Direct, seed))
            {
                dealNotes.Add(fix.ContractText == "Анекс 3 към 12/2026" ? "" : "поправката отваря празен договор");

                page.Replace(seed, () => page.ApplySubmitted(
                    fix.Planned(), fix.Date, To("Претопи ООД", "205566778"), string.Empty, seed.Direct, fix.Contract));
            }

            dealNotes.Add(db.Ledger.Contract(Last().Id) == "Анекс 3 към 12/2026" ? "" : "поправката изгуби договора");

            // Едно движение от „Месец“: същият списък.
            using (var single = new MoveDialog(service, db, companyId, "1", MoveKind.Submitted, september, Last().Id))
            {
                dealNotes.Add(single.ContractsOffered == 2 && single.ContractText == "Анекс 3 към 12/2026"
                    ? "" : "прозорчето от „Месец“ не носи договора");
            }

            using (var other = new MoveDialog(service, db, companyId, "1", MoveKind.Treated, september))
                dealNotes.Add(other.ContractsOffered == 0 ? "" : "третирането предлага договори");

            // Месец и пътят на партидата го показват.
            using (var ledger = new LedgerPage(service, db))
            {
                ledger.UseMonth(september);

                dealNotes.Add(Enumerable.Range(0, ledger.RowCount).Any(r => ledger.ContractAt(r) == "Договор 12/2026")
                    ? "" : "„Месец“ не показва договора");

                // В отчета — същите редове, които съставянето събира — нито договорът,
                // нито бележката: те са само за пресяване и не стигат до НИСО.
                var report = ledger.ReportRows(september, september);

                var written = BookProjection.Rows(service.GetSchema("book"), report)
                    .SelectMany(node => node.Value)
                    .SelectMany(row => row.Values)
                    .ToList();

                dealNotes.Add(written.Contains("Претопи ООД") ? "" : "отчетът не носи дори получателя — проверката не гледа нищо");
                dealNotes.Add(!written.Any(v => v.Contains("Договор 12/2026", StringComparison.Ordinal))
                    ? "" : "договорът стигна до отчета");
                dealNotes.Add(!written.Any(v => v.Contains("Камион 7", StringComparison.Ordinal))
                    ? "" : "бележката стигна до отчета");
            }

            var batch = db.Batches.List(companyId, "1")[0];

            using (var path = new BatchDialog(db, companyId, "1", batch, new Dictionary<string, string>()))
                dealNotes.Add(Enumerable.Range(0, path.RowCount).Any(r => path.ContractAt(r) == "Договор 12/2026")
                    ? "" : "пътят на партидата не показва договора");

            // Списъкът от „Настройки“: без ЕИК, без двойници, махнатото не пипа записаното.
            using (var list = new ReceiversDialog(db, companyId, PartnerRole.Contract))
            {
                dealNotes.Add(list.HeadingText == "Договори" && !list.EikShown ? "" : "прозорецът не е за договори");
                dealNotes.Add(list.RowCount == 2 ? "" : $"списъкът показва {list.RowCount} реда вместо два");
                dealNotes.Add(!list.AddNow("договор 12/2026", string.Empty) ? "" : "договорът се вписа втори път с други букви");
                dealNotes.Add(list.AddNow("Рамков   договор 2027", string.Empty) ? "" : "договор не се вписва предварително");

                var added = Enumerable.Range(0, list.RowCount).FirstOrDefault(r => list.NameAt(r).StartsWith("Рамков", StringComparison.Ordinal));

                dealNotes.Add(list.NameAt(added) == "Рамков договор 2027" ? "" : "вписаното запази излишните интервали");
                dealNotes.Add(list.UsedAt(added) == "още не" ? "" : "вписаният предварително се брои за ползван");

                // Поправка, която става точно съседът: казва се, не гърми.
                list.Pick(added);
                list.FixNow("анекс 3 към 12/2026", string.Empty);

                dealNotes.Add(Enumerable.Range(0, list.RowCount).Any(r => list.NameAt(r) == "Рамков договор 2027")
                    ? "" : "поправката стъпи върху съседа си");

                list.Pick(Enumerable.Range(0, list.RowCount).First(r => list.NameAt(r) == "Договор 12/2026"));
                list.RemoveNow();

                dealNotes.Add(list.RowCount == 2 ? "" : "договорът не се махна от списъка");
            }

            dealNotes.Add(db.Ledger.Read(companyId, "1").Any(m => db.Ledger.Contract(m.Id) == "Договор 12/2026")
                ? "" : "махнатият от списъка договор изчезна от записаното");
            dealNotes.Add(db.Partners.List(companyId).All(p => !p.Name.Contains("оговор", StringComparison.Ordinal))
                ? "" : "договорите се смесиха с получателите");

            // Таблицата на месеца: носи договора и не връща махнатия.
            string? file = null;

            using (var sheet = new MoveSheetDialog(service, db, companyId, "1", MoveKind.Submitted, september))
            {
                dealNotes.Add(sheet.Grid!.ColumnValues("__contract").Contains("Договор 12/2026")
                    ? "" : "таблицата не показва договора");

                file = Path.Combine(work, $"deal_{Guid.NewGuid():N}.xlsx");
                sheet.SaveTemplate(file);

                sheet.AcceptForTest();
            }

            dealNotes.Add(Known().All(p => p.Name != "Договор 12/2026") ? "" : "записът на месеца върна махнатия договор");
            dealNotes.Add(db.Ledger.Read(companyId, "1").Any(m => db.Ledger.Contract(m.Id) == "Договор 12/2026")
                ? "" : "записът на месеца изтри договора");

            // Изнесеното за Excel го носи и обратно.
            using (var back = new MoveSheetDialog(service, db, companyId, "1", MoveKind.Submitted, september))
            {
                var before = back.RowCount;

                back.LoadTemplate(file!, out var problems);

                dealNotes.Add(problems.Count == 0 && back.RowCount > before ? "" : "шаблонът не се чете: " + string.Join(" | ", problems));
                dealNotes.Add(back.Grid!.ColumnValues("__contract").Count(c => c == "Договор 12/2026") >= 4
                    ? "" : "договорът не се връща от шаблона");
            }

            // Написан в таблицата нов договор се научава при записа. Минава през
            // истинското залепване, а не покрай него: служебните колони по начало
            // само показват и жестовете за клетки ги прескачат.
            using (var sheet = new MoveSheetDialog(service, db, companyId, "1", MoveKind.Submitted, september))
            {
                dealNotes.Add(sheet.Grid!.IsWritableForTest("__contract") ? "" : "в клетката за договор не се пише");
                dealNotes.Add(sheet.Grid!.HintsForTest("__contract").Contains("Анекс 3 към 12/2026")
                    ? "" : "клетката за договор не подсказва");
                dealNotes.Add(sheet.Grid!.HintsForTest("name_receiver").Count == 0 ? "" : "подсказките се разляха по другите колони");

                sheet.Grid!.PasteForTest("Спот 44", 0, "__contract");

                dealNotes.Add(sheet.Grid!.LocalValue(0, "__contract") == "Спот 44" ? "" : "залепеното прескочи клетката за договор");

                sheet.AcceptForTest();
            }

            dealNotes.Add(Known().Any(p => p.Name == "Спот 44") ? "" : "новото от таблицата не се научи");

            // Другите служебни колони си остават за четене.
            using (var other = new MoveSheetDialog(service, db, companyId, "1", MoveKind.Treated, september))
                dealNotes.Add(!other.Grid!.IsWritableForTest("__ewc_out") ? "" : "кодът за отчета стана за писане покрай договора");
        }
        catch (Exception ex)
        {
            dealError = ex.ToString();
        }
    });

    dealThread.SetApartmentState(ApartmentState.STA);
    dealThread.Start();
    dealThread.Join();

    if (dealError is not null) Console.WriteLine("     " + dealError);

    var dealProblems = dealNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in dealProblems) Console.WriteLine("     " + problem);

    Check("договорът минава без изключение", dealError is null);
    Check($"направени са всичките {dealNotes.Count} проверки", dealNotes.Count == 46, $"{dealNotes.Count}");
    Check("договорът се предлага, научава и изравнява по всички пътища към „Предаден“", dealProblems.Count == 0,
        $"{dealProblems.Count} проблема" + (dealProblems.Count > 0 ? ": " + string.Join(" | ", dealProblems.Take(5)) : ""));
}


// =============================================== Таблицата на месеца не губи чуждото
//
// Записът от таблицата пренаписва всеки ред, а тя показва само част от
// движението. Без дяловете предаване от „Образуван“ не намира партида по FIFO —
// партидата помни кода, с който отпадъкът е ДОШЪЛ — и едно „Записване“ оставяше
// всички предавания на месеца без произход: количествата им изскачаха обратно в
// склада, а бележките им изчезваха.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - записаната без промяна таблица на месеца не пипа произхода и бележката");
{
    var sheetNotes = new List<string>();
    string? sheetError = null;

    var sheetThread = new Thread(() =>
    {
        try
        {
            using var db = Workspace.Open(Path.Combine(work, $"sheet_{Guid.NewGuid():N}.db"));
            var companyId = db.Companies.Add("203344556", "Метал ООД");

            var end = new DateOnly(2026, 9, 30);
            var september = new LedgerPeriod(2026, 9);

            var first = db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 1), "160216", 12m, Eik: "204455667", Partner: "Скрап ЕООД"));
            var second = db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 2), "160216", 8m, Eik: "204455668", Partner: "Метал груп ЕООД"));

            var older = db.Ledger.Draws(first)[0].BatchId;
            var newer = db.Ledger.Draws(second)[0].BatchId;

            // Посочено на ръка: по-новата партида, не най-старата.
            var treated = db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Treated, new DateOnly(2026, 9, 3), "160216", 8m, Draws: [new Draw(newer, 8m)]));

            var generated = db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Generated, new DateOnly(2026, 9, 4), "191202", 8m));

            var handed = db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Submitted, new DateOnly(2026, 9, 5), "191202", 3m, Note: "Камион 7",
                Draws: [new Draw(newer, 3m)], Contract: "Договор 1",
                Fields: new Dictionary<string, string>(StringComparer.Ordinal) { ["name_receiver"] = "Претопи ООД" }));

            decimal Formed() => StockMath.Shares(db.Batches.List(companyId, "1"), db.Ledger.Read(companyId, "1"),
                db.Ledger.Links(companyId, "1"), StoreKind.Formed, end).Sum(s => s.Amount);

            sheetNotes.Add(Formed() == 5m ? "" : $"преди таблицата в „Образуван“ стоят {Formed()} т");

            void Save(MoveKind kind)
            {
                using var sheet = new MoveSheetDialog(service, db, companyId, "1", kind, september);
                sheet.AcceptForTest();
            }

            Save(MoveKind.Submitted);

            sheetNotes.Add(db.Ledger.Draws(handed).Sum(d => d.Amount) == 3m ? "" : "предаването изгуби произхода си");
            sheetNotes.Add(Formed() == 5m ? "" : $"количеството изскочи обратно: в „Образуван“ стоят {Formed()} т");
            sheetNotes.Add(db.Ledger.Note(handed) == "Камион 7" ? "" : "бележката на предаването изчезна");
            sheetNotes.Add(db.Ledger.Contract(handed) == "Договор 1" ? "" : "договорът на предаването изчезна");

            // Бележката на предаването се пише и оттук, както договорът.
            using (var sheet = new MoveSheetDialog(service, db, companyId, "1", MoveKind.Submitted, september))
            {
                sheet.Grid!.PasteForTest("Камион 8", 0, "__note");
                sheet.AcceptForTest();
            }

            sheetNotes.Add(db.Ledger.Note(handed) == "Камион 8" ? "" : "бележката не се пише от таблицата");
            sheetNotes.Add(db.Ledger.Draws(handed).Sum(d => d.Amount) == 3m ? "" : "сменената бележка разпредели наново");

            // При другите видове колона няма, но таблицата пази бележката им.
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Treated, new DateOnly(2026, 9, 3), "160216", 8m, Note: "Смяна Б",
                Draws: [new Draw(newer, 8m)], MoveId: treated));

            // Посоченото на ръка третиране остава върху посочената партида.
            Save(MoveKind.Treated);

            sheetNotes.Add(db.Ledger.Note(treated) == "Смяна Б" ? "" : "таблицата изтри бележката на третирането");

            sheetNotes.Add(db.Ledger.Draws(treated).Single().BatchId == newer ? "" : "посоченото третиране мина на FIFO");

            Save(MoveKind.Generated);

            sheetNotes.Add(db.Ledger.Draws(generated).Sum(d => d.Amount) == 8m ? "" : "образуването изгуби дяловете си");

            // Променен ред е ново решение: разпределя се наново.
            using (var sheet = new MoveSheetDialog(service, db, companyId, "1", MoveKind.Treated, september))
            {
                sheet.Grid!.PutValue(0, "amount_ton", "10,000");
                sheet.AcceptForTest();
            }

            sheetNotes.Add(db.Ledger.Draws(treated).Sum(d => d.Amount) == 10m ? "" : "променено количество не се разпредели наново");
            sheetNotes.Add(db.Ledger.Draws(treated).Any(d => d.BatchId == older) ? "" : "наново разпределеното не е по FIFO");
        }
        catch (Exception ex)
        {
            sheetError = ex.ToString();
        }
    });

    sheetThread.SetApartmentState(ApartmentState.STA);
    sheetThread.Start();
    sheetThread.Join();

    if (sheetError is not null) Console.WriteLine("     " + sheetError);

    var sheetProblems = sheetNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in sheetProblems) Console.WriteLine("     " + problem);

    Check("таблицата на месеца минава без изключение", sheetError is null);
    Check($"направени са всичките {sheetNotes.Count} проверки", sheetNotes.Count == 12, $"{sheetNotes.Count}");
    Check("таблицата пази произхода, бележката и договора на непроменения ред", sheetProblems.Count == 0,
        $"{sheetProblems.Count} проблема");
}


// =============================================== „Предаден“: договор и „Поотделно“
//
// Трите разреза отговарят на „колко“ и събират. „Поотделно“ е пълният списък на
// излязлото: ред по дял на партида в предаване, без никакво събиране — и точно
// затова трябва да покаже ВСЯКО най-дребно движение, включително онова без
// партида, което иначе е само число в сбора.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - „Предаден“ се пресява по договор и бележка и показва всеки дял поотделно");
{
    var viewNotes = new List<string>();
    string? viewError = null;

    var viewThread = new Thread(() =>
    {
        try
        {
            using var db = Workspace.Open(Path.Combine(work, $"view_{Guid.NewGuid():N}.db"));
            var companyId = db.Companies.Add("203344556", "Метал ООД");

            var end = new DateOnly(2026, 9, 30);

            static Dictionary<string, string> To(string name, string eik) =>
                new(StringComparer.Ordinal) { ["name_receiver"] = name, ["pin_receiver"] = eik };

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 1), "160216", 10m, Eik: "204455667", Partner: "Скрап ЕООД"));
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 2), "160216", 10m, Eik: "201111111", Partner: "Метал груп ЕООД"));
            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 3), "150101", 10m, Eik: "204455667", Partner: "Скрап ЕООД"));

            using var page = new StockPage(service, db);
            page.UseDate(end);

            IReadOnlyList<TreatPile> Piles() => Handover.Piles(StockMath.Shares(db.Batches.List(companyId, "1"),
                db.Ledger.Read(companyId, "1"), db.Ledger.Links(companyId, "1"), StoreKind.Received, end));

            void Hand(string ewc, decimal amount, int day, string receiver, string eik, string contract, string note = "") =>
                page.ApplySubmitted(
                    Handover.Plan([new HandOrder(Piles().First(p => p.Ewc == ewc), amount)]),
                    new DateOnly(2026, 9, day), To(receiver, eik), note, true, contract);

            // Едно предаване от двама клиенти, две към същия получател по различни
            // договори, едно без договор и едно без партида изобщо.
            Hand("160216", 12m, 5, "Претопи ООД", "205566778", "Договор 1", "Камион 7");
            Hand("160216", 3m, 10, "Претопи ООД", "205566778", "Договор 2", "Камион 9");
            Hand("150101", 4m, 15, "Леярна АД", "175000111", string.Empty);

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Submitted, new DateOnly(2026, 9, 20), "191202", 2m,
                Fields: To("Леярна АД", "175000111"), Contract: "Договор 1"));

            // Извън „Предаден“ договор няма.
            page.UseStore(StoreKind.Received);

            viewNotes.Add(!page.ContractFilterOffered && !page.DetailContractShown && !page.DetailNoteShown
                ? "" : "договорът или бележката се предлагат и в „Получен“");
            viewNotes.Add(page.ViewCount == 2 ? "" : "„Получен“ получи разрезите на „Предаден“");

            page.UseStore(StoreKind.Submitted);

            // --- колоната и ситото
            viewNotes.Add(page.ViewCount == 4 ? "" : $"„Предаден“ дава {page.ViewCount} разреза вместо четири");
            viewNotes.Add(page.ViewFits ? "" : "разрезите се режат с многоточие");
            viewNotes.Add(page.ContractFilterOffered && page.DetailContractShown && page.DetailNoteShown
                ? "" : "договорът или бележката не се виждат в „Предаден“");
            viewNotes.Add(page.ContractColumnFilters ? "" : "десният бутон по заглавието не пресява договора и бележката");

            // По код: събрано. Предаването без партида е само число отстрани.
            viewNotes.Add(page.RowCount == 2 ? "" : $"„По код“ дава {page.RowCount} реда вместо два");
            viewNotes.Add(page.LooseAmount == 2m ? "" : $"без партида стоят {page.LooseAmount} т");

            var row = Enumerable.Range(0, page.RowCount).First(r => page.KeyAt(r) == "160216");
            page.Pick(row);

            var seen = Enumerable.Range(0, page.DetailCount).Select(page.DetailContractAt).ToList();

            viewNotes.Add(seen.Count == 3 && seen.Count(c => c == "Договор 1") == 2 && seen.Count(c => c == "Договор 2") == 1
                ? "" : "долу договорите не са на своите редове: " + string.Join(" | ", seen));

            var written = Enumerable.Range(0, page.DetailCount).Select(page.DetailNoteAt).ToList();

            viewNotes.Add(written.Count(n => n == "Камион 7") == 2 && written.Count(n => n == "Камион 9") == 1
                ? "" : "долу бележките не са на своите редове: " + string.Join(" | ", written));

            page.FilterContracts("Договор 2");

            viewNotes.Add(page.RowCount == 1 && page.KeyAt(0) == "160216" ? "" : "ситото по договор не свежда редовете");
            viewNotes.Add(page.ContractFilterText == "Договор 2" ? "" : "копчето не казва какво е отметнато: " + page.ContractFilterText);
            viewNotes.Add(page.AmountHeaderText.Contains("3,000", StringComparison.Ordinal)
                ? "" : "сборът не следва ситото: " + page.AmountHeaderText);

            // И „без договор“ е отговор: кое още не е вързано.
            page.FilterContracts(string.Empty);

            viewNotes.Add(page.RowCount == 1 && page.KeyAt(0) == "150101" ? "" : "ситото не намира предаденото без договор");
            viewNotes.Add(page.ContractFilterText == "(не е посочен)" ? "" : "празното няма име: " + page.ContractFilterText);

            page.ClearAllFilters();

            // Бележката се пресява като договора: и по стойност, и по липса.
            page.FilterNotes("Камион 9");

            viewNotes.Add(page.RowCount == 1 && page.KeyAt(0) == "160216" ? "" : "ситото по бележка не свежда редовете");
            viewNotes.Add(page.NoteFilterText == "Камион 9" ? "" : "копчето не казва коя бележка: " + page.NoteFilterText);
            viewNotes.Add(page.AmountHeaderText.Contains("3,000", StringComparison.Ordinal)
                ? "" : "сборът не следва бележката: " + page.AmountHeaderText);

            page.FilterNotes(string.Empty);

            viewNotes.Add(page.RowCount == 1 && page.KeyAt(0) == "150101" ? "" : "ситото не намира предаденото без бележка");

            page.ClearAllFilters();
            page.UseSearch("договор 2");

            viewNotes.Add(page.RowCount == 1 ? "" : "търсенето не гледа договора");

            page.UseSearch("камион 9");

            viewNotes.Add(page.RowCount == 1 ? "" : "търсенето не гледа бележката");

            page.UseSearch(string.Empty);

            // --- поотделно: всеки дял на партида в предаване е свой ред
            page.UseMoveView();

            viewNotes.Add(page.MoveViewOn ? "" : "„Поотделно“ не се включва");
            viewNotes.Add(page.RowCount == 5 ? "" : $"„Поотделно“ показва {page.RowCount} реда вместо пет");

            var dates = Enumerable.Range(0, page.RowCount).Select(page.MasterDateAt).ToList();

            viewNotes.Add(dates.SequenceEqual(["20.09.2026", "15.09.2026", "10.09.2026", "05.09.2026", "05.09.2026"])
                ? "" : "най-новото не е отгоре: " + string.Join(" | ", dates));

            // Предаването от двама клиенти НЕ е един ред от дванайсет тона.
            viewNotes.Add(page.AmountAt(3) == "10,000" && page.AmountAt(4) == "2,000"
                ? "" : $"предаването от двама клиенти не е разгънато: {page.AmountAt(3)} и {page.AmountAt(4)}");
            viewNotes.Add(page.MasterPartnerAt(3).Contains("Скрап", StringComparison.Ordinal) &&
                          page.MasterPartnerAt(4).Contains("Метал груп", StringComparison.Ordinal)
                ? "" : "редът не казва чий е делът: " + page.MasterPartnerAt(3));
            viewNotes.Add(page.MasterBatchAt(3).StartsWith('#') && page.MasterBatchAt(3) != page.MasterBatchAt(4)
                ? "" : "редът не казва партидата си: " + page.MasterBatchAt(3));
            viewNotes.Add(page.MasterDocumentAt(3).Contains("01.09.2026", StringComparison.Ordinal)
                ? "" : "редът не казва кога е приета партидата: " + page.MasterDocumentAt(3));

            viewNotes.Add(page.MasterReceiverAt(2).Contains("Претопи", StringComparison.Ordinal) ? "" : "редът не казва получателя");
            viewNotes.Add(page.MasterContractAt(2) == "Договор 2" ? "" : "редът не казва договора си: " + page.MasterContractAt(2));
            viewNotes.Add(page.MasterNoteAt(2) == "Камион 9" ? "" : "редът не казва бележката си: " + page.MasterNoteAt(2));
            viewNotes.Add(page.MasterContractAt(1).Length == 0 && page.MasterNoteAt(1).Length == 0
                ? "" : "празният ред показва чужд договор или бележка");
            viewNotes.Add(page.MasterContractShown && page.MasterNoteShown ? "" : "договорът и бележката не стоят горе");

            // Движението без партида е ред като всеки друг и го казва.
            viewNotes.Add(page.AmountAt(0) == "2,000" && page.MasterBatchAt(0) == "—"
                ? "" : "движението без партида не е на свой ред");
            viewNotes.Add(page.MasterPartnerAt(0).Contains("без посочена партида", StringComparison.Ordinal)
                ? "" : "редът без партида не го казва: " + page.MasterPartnerAt(0));

            // Всичко е на екрана, и то веднъж: няма „и още толкова без партида“.
            viewNotes.Add(page.LooseAmount == 0m ? "" : "движението без партида е казано два пъти");
            viewNotes.Add(page.AmountHeaderText.Contains("21,000", StringComparison.Ordinal)
                ? "" : "сборът не е на всички движения: " + page.AmountHeaderText);

            // Няма какво да се разгъва: долната таблица стои празна.
            page.Pick(3);

            viewNotes.Add(page.DetailCount == 0 ? "" : $"под най-дребния ред стоят {page.DetailCount} реда");

            // Избраните редове се събират — и двата дяла на предаването са дванайсетте му тона.
            page.SelectMaster(3, 4);

            viewNotes.Add(page.Selection().Sum(s => s.Amount) == 12m ? "" : "избраните дялове не се събират");

            // Ситата важат направо за реда: няма дял зад него.
            page.UsePartnerFilter("201111111");

            // Метал груп е втората партида: първото предаване е взело от нея два
            // тона от дванайсетте, второто — и трите си.
            viewNotes.Add(page.RowCount == 2 && page.AmountAt(0) == "3,000" && page.AmountAt(1) == "2,000"
                ? "" : "ситото по клиент не дава неговите редове: " + (page.RowCount > 1 ? page.AmountAt(1) : "няма"));

            page.ClearAllFilters();
            page.FilterContracts("Договор 1");

            viewNotes.Add(page.RowCount == 3 ? "" : $"по договор излизат {page.RowCount} реда вместо три");

            page.ClearAllFilters();
            page.FilterNotes("Камион 7");

            viewNotes.Add(page.RowCount == 2 && page.AmountHeaderText.Contains("12,000", StringComparison.Ordinal)
                ? "" : $"по бележка излизат {page.RowCount} реда: " + page.AmountHeaderText);

            page.ClearAllFilters();
            page.UseLeast("4");

            viewNotes.Add(page.RowCount == 2 ? "" : $"„над 4 т“ оставя {page.RowCount} реда вместо два");

            page.ClearAllFilters();
            page.SortMasterByContract();

            viewNotes.Add(page.MasterContractAt(0).Length == 0 ? "" : "подредбата по договор не работи");

            page.UseMonthOnly(true);
            page.UseDate(new DateOnly(2026, 8, 31));

            viewNotes.Add(page.RowCount == 0 ? "" : "„Само този месец“ не важи в „Поотделно“");

            page.UseMonthOnly(false);
            page.UseDate(new DateOnly(2026, 9, 12));

            viewNotes.Add(page.RowCount == 3 ? "" : $"към 12.09 стоят {page.RowCount} реда вместо три");

            page.UseDate(end);

            // --- изгледите: икони, с връщане към надписите от „Настройки“
            viewNotes.Add(page.ViewIconsOn ? "" : "изгледите не тръгват с икони");
            viewNotes.Add(page.ViewFits ? "" : "иконите не се побират в дяловете си");
            viewNotes.Add(page.ViewNameAt(3) == "Поотделно" ? "" : "иконата остана без име за подсказката");

            var narrowest = page.ViewWidth;

            using (var settings = new SettingsPage(db))
            {
                settings.UseViewIcons(false);

                viewNotes.Add(!db.Settings.GetBool(SettingsStore.Keys.StoreViewIcons, true)
                    ? "" : "отметката в „Настройки“ не се запазва");

                page.Refresh();

                viewNotes.Add(!page.ViewIconsOn ? "" : "изгледите не се връщат към надписи");
                viewNotes.Add(page.ViewFits && page.ViewWidth > 2 * narrowest
                    ? "" : $"надписите не получават място: {page.ViewWidth} срещу {narrowest}");
                viewNotes.Add(page.MoveViewOn ? "" : "смяната изгуби избрания изглед");

                settings.UseViewIcons(true);
                page.Refresh();

                viewNotes.Add(page.ViewIconsOn && page.ViewWidth == narrowest ? "" : "иконите не се връщат");
            }

            // Тесен прозорец: името на кода отстъпва, а не последният стълб. На трите
            // размера на лаптопа се събират и без това и проверката там не го вижда.
            using (var host = new Form { ClientSize = new Size(1060, 900) })
            using (var narrow = new StockPage(service, db))
            {
                narrow.Dock = DockStyle.Fill;
                host.Controls.Add(narrow);

                _ = host.Handle;

                narrow.UseDate(end);
                narrow.UseStore(StoreKind.Submitted);
                narrow.UseMoveView();
                Application.DoEvents();

                viewNotes.Add(narrow.ColumnsFit ? "" : "на тесен прозорец стълбовете излизат извън таблицата");
            }

            // Сменен склад: разрезът и ситата си отиват, таблицата не остава празна.
            page.FilterContracts("Договор 1");
            page.FilterNotes("Камион 7");
            page.UseStore(StoreKind.Received);

            viewNotes.Add(!page.MoveViewOn && page.ViewCount == 2 ? "" : "„Поотделно“ остана в „Получен“");
            viewNotes.Add(page.RowCount > 0 ? "" : "скрито сито по договор пресява „Получен“");
            viewNotes.Add(page.ContractFilterText == "(всички)" ? "" : "ситото по договор не се махна със склада");
            viewNotes.Add(page.NoteFilterText == "(всички)" ? "" : "ситото по бележка не се махна със склада");

            // Обратно в „Предаден“: първият изглед, с долната таблица.
            page.UseStore(StoreKind.Submitted);

            viewNotes.Add(!page.MoveViewOn ? "" : "„Поотделно“ се върна само");

            page.Pick(0);

            viewNotes.Add(page.DetailCount > 0 ? "" : "долната таблица не се върна");
        }
        catch (Exception ex)
        {
            viewError = ex.ToString();
        }
    });

    viewThread.SetApartmentState(ApartmentState.STA);
    viewThread.Start();
    viewThread.Join();

    if (viewError is not null) Console.WriteLine("     " + viewError);

    var viewProblems = viewNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in viewProblems) Console.WriteLine("     " + problem);

    Check("„Предаден“ минава без изключение", viewError is null);
    Check($"направени са всичките {viewNotes.Count} проверки", viewNotes.Count == 61, $"{viewNotes.Count}");
    Check("договорът и бележката се пресяват, а „Поотделно“ показва всеки дял веднъж", viewProblems.Count == 0,
        $"{viewProblems.Count} проблема" + (viewProblems.Count > 0 ? ": " + string.Join(" | ", viewProblems.Take(5)) : ""));
}


// =============================================== Поправката пресъздава прозореца
//
// „Поправи…“ отваря прозореца такъв, какъвто е бил — с предлаганото, с разделянето
// и с кантара — и пази веригата: нищо след действието не бива да остане без
// основание, а прекъснат запис не бива да загуби действието.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - поправката пресъздава прозореца, от който е дошло действието");
{
    var againNotes = new List<string>();
    string? againError = null;

    var againThread = new Thread(() =>
    {
        try
        {
            var notes = againNotes;

            DateOnly Day(int day) => AmendWorld.Day(day);

            bool Has(IEnumerable<string> said, params string[] words) =>
                said.Any(s => words.All(w => s.Contains(w, StringComparison.Ordinal)));

            string Draws(IEnumerable<Draw> draws) =>
                string.Join(",", draws.OrderBy(d => d.BatchId).Select(d => $"{d.BatchId}:{d.Amount:0.###}"));

            // ---------------------------------------------- обхватът
            using (var w = new AmendWorld(service, work))
            {
                var first = w.Receive(1, 4m);
                var second = w.Receive(2, 3m);
                var third = w.Receive(3, 5m);

                w.Treat(Day(10), d => d.UseTake(0, 6m));

                var (fix, seed) = w.FixTreat(w.Entry(MoveKind.Treated));

                using (fix)
                {
                    notes.Add(fix.BatchesOffered == 3
                        ? "" : $"прозорецът предлага {fix.BatchesOffered} партиди, при записа бяха 3");
                    notes.Add(fix.OriginCount == 3
                        ? "" : $"долу се виждат {fix.OriginCount} партиди, при записа бяха 3");
                    notes.Add(Draws(fix.Planned().SelectMany(m => m.Draws)) == $"{first}:4,{second}:2"
                        ? "" : "взетото не се нарежда: " + Draws(fix.Planned().SelectMany(m => m.Draws)));

                    // Грешната партида се сменя с вярната — точно заради това е обхватът.
                    fix.UseBatchTake(0, 1m);
                    fix.UseBatchTake(1, 0m);
                    fix.UseBatchTake(2, 5m);

                    notes.Add(fix.Blocking().Count == 0
                        ? "" : "смяната на партида е спряна: " + string.Join(" | ", fix.Blocking()));

                    w.Save(fix, seed);
                }

                notes.Add(w.Left(StoreKind.Received, first) == 3m && w.Left(StoreKind.Received, second) == 3m &&
                          w.Left(StoreKind.Received, third) == 0m
                    ? "" : "партидите не се смениха при записа");
            }

            // Взетото стои отгоре: партидите могат да са стотици, а поправката е за шепа.
            using (var w = new AmendWorld(service, work))
            {
                w.Receive(1, 4m);
                w.Receive(2, 3m);
                w.Receive(3, 5m);

                // Прозорецът тръгва с цялата купчина: без „Нищо“ тук биха се взели всичките
                // дванайсет тона, и подредбата нямаше да има равни, между които да реши.
                w.Treat(Day(10), d =>
                {
                    d.UseQuick(false);
                    d.UseBatchTake(2, 5m);
                });

                notes.Add(w.Moves.Where(m => m.Kind == MoveKind.Treated).Sum(m => m.Amount) == 5m
                    ? "" : "проверката иска да е взето само от третата партида");

                var (fix, _) = w.FixTreat(w.Entry(MoveKind.Treated));

                using (fix)
                {
                    notes.Add(fix.OriginTakeAt(0) == "5,000" ? "" : "третиране: взетото не е отгоре");
                    notes.Add(fix.BatchAt(1).EndsWith("0001", StringComparison.Ordinal) &&
                              fix.BatchAt(2).EndsWith("0002", StringComparison.Ordinal)
                        ? "" : $"останалото не е по реда си: {fix.BatchAt(1)}, {fix.BatchAt(2)}");
                }
            }

            using (var w = new AmendWorld(service, work))
            {
                w.Receive(1, 4m);
                w.Receive(2, 3m);
                w.Receive(3, 5m);

                w.Hand(Day(10), d =>
                {
                    d.UseQuick(false);
                    d.UseBatchTake(2, 5m);
                }, direct: true);

                notes.Add(w.Moves.Where(m => m.Kind == MoveKind.Submitted).Sum(m => m.Amount) == 5m
                    ? "" : "проверката иска да е предадено само от третата партида");

                var (fix, _) = w.FixHand(w.Entry(MoveKind.Submitted));

                using (fix)
                {
                    notes.Add(fix.OriginTakeAt(0) == "5,000" ? "" : "предаване: взетото не е отгоре");
                    notes.Add(fix.BatchAt(1).EndsWith("0001", StringComparison.Ordinal) &&
                              fix.BatchAt(2).EndsWith("0002", StringComparison.Ordinal)
                        ? "" : $"предаване: останалото не е по реда си: {fix.BatchAt(1)}, {fix.BatchAt(2)}");
                }
            }

            using (var w = new AmendWorld(service, work))
            {
                w.Receive(1, 4m);
                w.Receive(2, 3m);
                w.Receive(3, 5m);

                w.Treat(Day(5), d => d.UseQuick(true));

                var waiting = w.Waiting().Shares;
                var pile = Generation.Piles(waiting)[0];

                w.Page.ApplyGenerated(
                    Generation.Plan([new GenOrder(pile, [new TreatPick(waiting[2], 5m)], [new GenPart("191202", 5m)])]),
                    Day(6), new Dictionary<string, string>(StringComparer.Ordinal));

                var (fix, _) = w.FixMade(w.Entry(MoveKind.Generated));

                using (fix)
                {
                    notes.Add(fix.OriginTakeAt(0) == "5,000" ? "" : "образуване: взетото не е отгоре");
                    notes.Add(fix.BatchAt(1).EndsWith("0001", StringComparison.Ordinal) &&
                              fix.BatchAt(2).EndsWith("0002", StringComparison.Ordinal)
                        ? "" : $"образуване: останалото не е по реда си: {fix.BatchAt(1)}, {fix.BatchAt(2)}");
                }
            }

            // Запомненият обхват, а не всичко със същия код.
            using (var w = new AmendWorld(service, work))
            {
                w.Receive(1, 4m);
                w.Receive(2, 3m);
                w.Receive(3, 5m);

                w.Treat(Day(10), d => d.UseTake(0, 5m), [.. w.Stock(StoreKind.Received).Take(2)]);

                var (fix, _) = w.FixTreat(w.Entry(MoveKind.Treated));

                using (fix)
                    notes.Add(fix.BatchesOffered == 2
                        ? "" : $"прозорец от две партиди се върна с {fix.BatchesOffered}");
            }

            // Без запомнен прозорец — заварено действие: цялата купчина на кода.
            using (var w = new AmendWorld(service, work))
            {
                w.Receive(1, 4m);
                w.Receive(2, 3m);
                w.Receive(3, 5m, "150101");

                var pile = Treatment.Piles(w.Stock(StoreKind.Received)).First(p => p.Ewc == "160216");

                w.Page.Apply(Treatment.Plan([new TreatOrder(pile, 2m)]), Day(10));

                notes.Add(w.Db.Actions.Window(w.Entry(MoveKind.Treated).Action) is null
                    ? "" : "заварено действие има прозорец");

                var seed = w.Page.Prepare(w.Entry(MoveKind.Treated), out var scope, out _);

                notes.Add(scope.Count == 2 && scope.All(s => s.Ewc == "160216")
                    ? "" : $"заварено действие се сглобява с {scope.Count} дяла вместо двата от кода си");
                notes.Add(seed!.Scope!.Count == 2 ? "" : "обхватът на завареното не е запомнен в семето");
            }

            // Нечетим прозорец не бива да спира поправката: сглобява се наново.
            using (var w = new AmendWorld(service, work))
            {
                w.Receive(1, 4m);
                w.Receive(2, 3m);

                w.Treat(Day(10), d => d.UseTake(0, 1m), [.. w.Stock(StoreKind.Received).Take(1)]);

                using (var raw = new SqliteConnection($"Data Source={w.DbPath};Pooling=False"))
                {
                    raw.Open();
                    using var spoil = raw.CreateCommand();
                    spoil.CommandText = "UPDATE action_windows SET window = '{не е json';";
                    spoil.ExecuteNonQuery();
                }

                w.Page.Prepare(w.Entry(MoveKind.Treated), out var scope, out var why);

                notes.Add(scope.Count == 2 ? "" : $"нечетимият прозорец дава {scope.Count} дяла: {why}");
            }

            // ---------------------------------------------- отделният ред
            using (var w = new AmendWorld(service, work))
            {
                w.Receive(1, 4m);
                w.Receive(2, 3m);

                w.Treat(Day(10), d =>
                {
                    d.UsePerBatch(true);
                    d.UseTake(0, 7m);
                });

                notes.Add(w.Moves.Count(m => m.Kind == MoveKind.Treated) == 2 ? "" : "записът не е на два реда");

                var (fix, _) = w.FixTreat(w.Entry(MoveKind.Treated));

                using (fix)
                {
                    notes.Add(fix.PerBatchOn ? "" : "отметката за отделен ред не се връща");
                    notes.Add(fix.Planned().Count == 2 ? "" : $"поправката би записала {fix.Planned().Count} реда вместо 2");
                }
            }

            // Заварено: личи от самите редове. Два реда с два кода обаче не са разделяне.
            using (var w = new AmendWorld(service, work))
            {
                w.Receive(1, 4m);
                w.Receive(2, 3m);

                var pile = Treatment.Piles(w.Stock(StoreKind.Received))[0];

                w.Page.Apply(Treatment.Plan([new TreatOrder(pile, 7m)], perBatch: true), Day(10));

                var (fix, _) = w.FixTreat(w.Entry(MoveKind.Treated));

                using (fix) notes.Add(fix.PerBatchOn ? "" : "разделянето не се познава от редовете");
            }

            using (var w = new AmendWorld(service, work))
            {
                w.Receive(1, 4m, "160216");
                w.Receive(1, 3m, "150101");

                var piles = Treatment.Piles(w.Stock(StoreKind.Received));

                w.Page.Apply(Treatment.Plan([new TreatOrder(piles[0], 3m), new TreatOrder(piles[1], 4m)]), Day(10));

                var (fix, _) = w.FixTreat(w.Entry(MoveKind.Treated));

                using (fix)
                {
                    notes.Add(!fix.PerBatchOn ? "" : "два кода се четат като отделен ред");
                    notes.Add(fix.Planned().Count == 2 ? "" : "двата кода не остават два реда");
                }
            }

            // Купчините при образуване.
            using (var w = new AmendWorld(service, work))
            {
                w.Receive(1, 4m, "160216");
                w.Receive(1, 3m, "150101");

                w.Treat(Day(5), d => d.UseQuick(true));
                w.Generate(Day(6), perPile: true);

                var (fix, seed) = w.FixMade(w.Entry(MoveKind.Generated));

                using (fix)
                {
                    notes.Add(fix.PerPileOn ? "" : "„отделен ред за всяка купчина“ не се връща");
                    notes.Add(fix.Planned().Count == 2
                        ? "" : $"образуването би станало {fix.Planned().Count} реда вместо 2");
                    notes.Add(fix.BatchesOffered == 2 ? "" : $"образуването предлага {fix.BatchesOffered} партиди");
                    notes.Add(seed.Moves.Count == 2 ? "" : "двата реда не са едно действие");
                }
            }

            // ---------------------------------------------- кантарът
            using (var w = new AmendWorld(service, work))
            {
                w.Receive(1, 10m);

                w.Hand(Day(5), d =>
                {
                    d.UseTarget(6m);
                    d.UseTake(0, 6m);
                    d.UseContract("Договор 12/2026");
                }, direct: true);

                notes.Add(w.Moves.Any(m => m.Kind == MoveKind.Submitted) ? "" : "предаването не се записа");

                var before = w.Print();

                var (fix, seed) = w.FixHand(w.Entry(MoveKind.Submitted));

                using (fix)
                {
                    notes.Add(fix.TargetNow == 6m ? "" : $"кантарът се връща като {fix.TargetNow}");
                    notes.Add(fix.Direct ? "" : "прякото предаване се отваря като обикновено");
                    notes.Add(fix.ContractText == "Договор 12/2026" ? "" : "договорът не се връща");
                    notes.Add(fix.Blocking().Count == 0
                        ? "" : "същото предаване е спряно: " + string.Join(" | ", fix.Blocking()));

                    // Друго количество срещу същия кантар: както при записа, казва се.
                    fix.UseTake(0, 5m);

                    notes.Add(fix.Blocking().Count > 0 ? "" : "разминаването с кантара мина мълчаливо");

                    fix.UseTake(0, 6m);

                    w.Save(fix, seed);
                }

                var after = w.Moves.Single(m => m.Kind == MoveKind.Submitted);

                notes.Add(after.Direct ? "" : "поправеното предаване вече не е пряко");
                notes.Add(w.Db.Ledger.Contract(after.Id) == "Договор 12/2026" ? "" : "договорът се загуби при записа");

                // Нито дума различна освен следата в дневника.
                var again = w.Print();

                notes.Add(before.Split('\n').Where(l => l.Contains("|Submitted|", StringComparison.Ordinal))
                        .SequenceEqual(again.Split('\n').Where(l => l.Contains("|Submitted|", StringComparison.Ordinal)))
                    ? "" : "записано без промяна, предаването се промени");
            }

            // ---------------------------------------------- проверката на веригата
            using (var w = new AmendWorld(service, work))
            {
                var batch = w.Receive(1, 10m);

                w.Treat(Day(2), d => d.UseQuick(true));
                w.Generate(Day(3));

                var formed = Handover.Piles(w.Stock(StoreKind.Formed))[0];

                w.Page.ApplySubmitted(Handover.Plan([new HandOrder(formed, 10m)]), Day(4));

                var seed = w.Page.Prepare(w.Entry(MoveKind.Generated), out _, out _)!;

                // Друг изходен код, а медта вече е предадена.
                var said = w.Page.Check(seed, [new Planned(MoveKind.Generated, Day(3), "191204", 10m, [new Draw(batch, 10m)])]);

                notes.Add(Has(said, "не достигат", "10,000", "Предаден", "191202")
                    ? "" : "смененият код след предаване минава: " + string.Join(" | ", said));

                notes.Add(w.Page.Check(seed, [new Planned(MoveKind.Generated, Day(3), "191202", 10m, [new Draw(batch, 10m)])]).Count == 0
                    ? "" : "същото образуване се спира");
            }

            // Купчина, която и без това е къса с грам: не прикрива намаление с четири тона.
            using (var w = new AmendWorld(service, work))
            {
                var batch = w.Receive(1, 10m);

                w.Treat(Day(2), d => d.UseTake(0, 5m));

                w.Db.Writer.Write(w.Co, new MoveRequest(
                    MoveKind.Generated, Day(3), "191202", 5.001m, Draws: [new Draw(batch, 5.001m)]));

                var seed = w.Page.Prepare(w.Entry(MoveKind.Treated), out _, out _)!;

                var said = w.Page.Check(seed, [new Planned(MoveKind.Treated, Day(2), "160216", 1m, [new Draw(batch, 1m)])]);

                notes.Add(Has(said, "не достигат", "4,001", "Третиран")
                    ? "" : "заварен грам прикрива намаление: " + string.Join(" | ", said));

                // А самият грам не спира нищо, което не го пипа.
                notes.Add(w.Page.Check(seed, [new Planned(MoveKind.Treated, Day(2), "160216", 5m, [new Draw(batch, 5m)])]).Count == 0
                    ? "" : "заварен грам спира непроменена поправка");
            }

            // Предадено без партида: през дяловете не се вижда, а количеството е излязло.
            using (var w = new AmendWorld(service, work))
            {
                var batch = w.Receive(1, 10m);

                w.Treat(Day(2), d => d.UseQuick(true));
                w.Generate(Day(3));

                w.Db.Writer.Write(w.Co, new MoveRequest(MoveKind.Submitted, Day(4), "191202", 10m));

                var seed = w.Page.Prepare(w.Entry(MoveKind.Generated), out _, out _)!;

                var said = w.Page.Check(seed, [new Planned(MoveKind.Generated, Day(3), "191202", 2m, [new Draw(batch, 2m)])]);

                notes.Add(Has(said, "Код 191202", "не достигат", "8,000", "Предаден")
                    ? "" : "предаване без партида не спира намалението: " + string.Join(" | ", said));
            }

            // ---------------------------------------------- свободното
            using (var w = new AmendWorld(service, work))
            {
                w.Receive(1, 10m);

                w.Treat(Day(5), d => d.UseTake(0, 6m));
                w.Treat(Day(15), d => d.UseTake(0, 4m));

                var (fix, seed) = w.FixTreat(w.Entry(MoveKind.Treated, 1));

                using (fix)
                {
                    notes.Add(fix.StockAt(0) == "6,000"
                        ? "" : $"налично {fix.StockAt(0)} т, а без да се развали по-късното могат 6");
                    notes.Add(fix.NoticeText.Contains("без 4,000 т", StringComparison.Ordinal)
                        ? "" : "не се казва защо наличното е по-малко: " + fix.NoticeText);
                    notes.Add(seed.Held == 4m ? "" : $"задържаното е {seed.Held} т");

                    // След по-късното движение то вече е в наличното — обяснението отпада.
                    fix.UseDate(Day(16));

                    notes.Add(!fix.NoticeText.Contains("взети след", StringComparison.Ordinal)
                        ? "" : "обяснението остава и когато вече не важи: " + fix.NoticeText);
                }
            }

            // Какво виси на действието се казва при отваряне, не при записа.
            using (var w = new AmendWorld(service, work))
            {
                w.Receive(1, 10m);

                w.Treat(Day(2), d => d.UseQuick(true));

                var (bare, _) = w.FixTreat(w.Entry(MoveKind.Treated));

                using (bare)
                {
                    notes.Add(bare.NoticeText.Contains("Поправя се третиране от 02.09.2026", StringComparison.Ordinal)
                        ? "" : "лентата не казва какво се поправя: " + bare.NoticeText);
                    notes.Add(!bare.NoticeText.Contains("вече е взето", StringComparison.Ordinal) &&
                              !bare.NoticeText.Contains("взети след", StringComparison.Ordinal)
                        ? "" : "лентата предупреждава без повод: " + bare.NoticeText);
                }

                w.Generate(Day(3));

                var (fix, _) = w.FixTreat(w.Entry(MoveKind.Treated));

                using (fix)
                    notes.Add(fix.NoticeText.Contains("вече е взето", StringComparison.Ordinal) &&
                              fix.NoticeText.Contains("191202", StringComparison.Ordinal)
                        ? "" : "не се казва какво виси на действието: " + fix.NoticeText);
            }

            // Своето остава предложено, каквото и да показва складът.
            using (var w = new AmendWorld(service, work))
            {
                var batch = w.Receive(1, 2m);

                w.Treat(Day(2), d => d.UseQuick(true));

                // Партида, третирана втори път — заварено в истинската база.
                w.Db.Writer.Write(w.Co, new MoveRequest(
                    MoveKind.Treated, Day(5), "160216", 2m, Draws: [new Draw(batch, 2m)],
                    Fields: w.Db.Ledger.Fields(w.Moves.First(m => m.Kind == MoveKind.Treated).Id)));

                var twice = w.Moves.Where(m => m.Kind == MoveKind.Treated).OrderBy(m => m.Id).Last();

                var (fix, seed) = w.FixTreat(w.Bare(twice));

                using (fix)
                {
                    notes.Add(Draws(fix.Planned().SelectMany(m => m.Draws)) == $"{batch}:2"
                        ? "" : "партида без наличност изпада от поправката: " + Draws(fix.Planned().SelectMany(m => m.Draws)));
                    notes.Add(fix.Blocking().Count == 0
                        ? "" : "непроменената поправка е спряна: " + string.Join(" | ", fix.Blocking()));

                    // Партидата е стигнала и към по-ранната дата: само датата се мести.
                    fix.UseDate(Day(4));

                    notes.Add(fix.Planned().Sum(m => m.Amount) == 2m
                        ? "" : $"преместена с ден, поправката взема {fix.Planned().Sum(m => m.Amount)} т");

                    // Преди приемането я няма: своето не важи пред това.
                    fix.UseDate(new DateOnly(2026, 8, 31));

                    notes.Add(fix.Planned().Sum(m => m.Amount) == 0m
                        ? "" : "партида се предлага преди да е приета");

                    fix.UseDate(Day(5));

                    var before = w.Print();

                    w.Save(fix, seed);

                    var moves = before.Split('\n').Where(l => l.Contains("|Treated|", StringComparison.Ordinal));

                    notes.Add(moves.SequenceEqual(w.Print().Split('\n').Where(l => l.Contains("|Treated|", StringComparison.Ordinal)))
                        ? "" : "записано без промяна, движението се промени");
                }
            }

            // Образувано с грам над всичко стигнало: заварено от старото закръгляне.
            using (var w = new AmendWorld(service, work))
            {
                var batch = w.Receive(1, 5m);

                w.Treat(Day(2), d => d.UseQuick(true));

                w.Db.Writer.Write(w.Co, new MoveRequest(
                    MoveKind.Generated, Day(3), "191202", 5.001m, Draws: [new Draw(batch, 5.001m)]));

                var made = w.Moves.Single(m => m.Kind == MoveKind.Generated);

                var (fix, _) = w.FixMade(w.Bare(made));

                using (fix)
                {
                    notes.Add(fix.Planned().Sum(m => m.Amount) == 5.001m
                        ? "" : $"заваренят грам се отрязва: {fix.Planned().Sum(m => m.Amount)} т");
                    notes.Add(fix.Blocking().Count == 0
                        ? "" : "заварен грам спира поправката: " + string.Join(" | ", fix.Blocking()));

                    // Най-честата поправка е датата: грамът не бива да ѝ пречи.
                    fix.UseDate(Day(4));

                    notes.Add(fix.Planned().Sum(m => m.Amount) == 5.001m && fix.Blocking().Count == 0
                        ? "" : "заварен грам спира преместването на датата");
                }
            }

            // ---------------------------------------------- цялото действие
            using (var w = new AmendWorld(service, work))
            {
                w.Receive(1, 4m, "160216");
                w.Receive(1, 3m, "150101");

                var piles = Treatment.Piles(w.Stock(StoreKind.Received));

                w.Page.Apply(Treatment.Plan([new TreatOrder(piles[0], 3m), new TreatOrder(piles[1], 4m)]), Day(5));

                var rows = w.Db.Actions.List(w.Co, "1");

                w.Db.Actions.Trash(rows[0].Id);

                var seed = w.Page.Prepare(rows[1], out _, out _)!;

                notes.Add(seed.Moves.Count == 2 ? "" : "отложеният съсед не влиза в поправката");

                w.Page.Replace(seed, () => w.Page.Apply(
                    Treatment.Plan([new TreatOrder(piles[0], 3m), new TreatOrder(piles[1], 4m)]), Day(6)));

                var after = w.Db.Actions.List(w.Co, "1");

                notes.Add(after.Select(e => e.Action).Distinct().Count() == 1 && after.All(e => e.Date == Day(6))
                    ? "" : "действието се разцепи на две дати");
            }

            // ---------------------------------------------- всичко или нищо
            using (var w = new AmendWorld(service, work))
            {
                w.Receive(1, 10m);

                w.Treat(Day(5), d => d.UseTake(0, 6m));

                var entry = w.Entry(MoveKind.Treated);
                var before = w.Print();
                var seed = w.Page.Prepare(entry, out var scope, out _)!;

                var threw = false;

                try
                {
                    w.Page.Replace(seed, () => throw new IOException("дискът се напълни"));
                }
                catch (IOException)
                {
                    threw = true;
                }

                notes.Add(threw ? "" : "грешката при запис се губи");
                notes.Add(w.Print() == before ? "" : "прекъснат запис промени базата");

                // По средата: новото е записано, но нещо след него пада.
                try
                {
                    w.Page.Replace(seed, () =>
                    {
                        w.Page.Apply(Treatment.Plan([new TreatOrder(Treatment.Piles(scope)[0], 5m)]), Day(6));

                        throw new IOException("дискът се напълни");
                    });
                }
                catch (IOException)
                {
                }

                notes.Add(w.Print() == before ? "" : "записът, паднал по средата, остави следи");

                // Махнато и незаменено не е поправка.
                var nothing = false;

                try
                {
                    w.Page.Replace(seed, () => 0);
                }
                catch (InvalidOperationException)
                {
                    nothing = true;
                }

                notes.Add(nothing && w.Print() == before ? "" : "поправка без нито един запис заличи действието");

                // И работата продължава: нито старият номер, нито общата транзакция остават.
                notes.Add(typeof(LedgerStore).GetProperty("Joined", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                        .GetValue(w.Db.Ledger) is false
                    ? "" : "след падането хранилището още смята, че е в обща транзакция");

                var highest = w.Moves.Max(m => m.Id);

                w.Receive(2, 1m);

                notes.Add(w.Moves.Max(m => m.Id) > highest ? "" : "след падането новият запис взе стар номер");

                var saved = w.Moves.Count;

                using (var raw = new SqliteConnection($"Data Source={w.DbPath};Pooling=False"))
                {
                    raw.Open();
                    using var count = raw.CreateCommand();
                    count.CommandText = "SELECT COUNT(*) FROM ledger_moves;";

                    notes.Add(Convert.ToInt32(count.ExecuteScalar()) == saved
                        ? "" : "след падането записът не стига до файла");
                }
            }

            // ---------------------------------------------- същото действие
            using (var w = new AmendWorld(service, work))
            {
                w.Receive(1, 10m);

                w.Treat(Day(5), d => d.UseTake(0, 6m));
                Thread.Sleep(20);
                w.Treat(Day(6), d => d.UseTake(0, 1m));

                var was = w.Entry(MoveKind.Treated, 1);
                var move = was.MoveId;

                Thread.Sleep(20);

                var (fix, seed) = w.FixTreat(was);

                using (fix)
                {
                    fix.UseTake(0, 5m);
                    w.Save(fix, seed);
                }

                var now = w.Entry(MoveKind.Treated, 1);

                notes.Add(now.Action == was.Action ? "" : "поправеното получи нов белег");
                notes.Add(now.CreatedUtc == was.CreatedUtc ? "" : "времето на въвеждане се смени");
                notes.Add(now.MoveId == move ? "" : "движението получи нов номер");
                notes.Add(now.Amended is not null ? "" : "от дневника не личи, че е поправяно");
                notes.Add(now.Was == "6,000 т на 05.09.2026" ? "" : $"следата казва „{now.Was}“");
                notes.Add(now.Amount == 5m ? "" : "дневникът не показва поправеното");

                // Второ поправяне: следата остава първоначалната.
                var (again, seed2) = w.FixTreat(now);

                using (again)
                {
                    again.UseTake(0, 4m);
                    w.Save(again, seed2);
                }

                notes.Add(w.Entry(MoveKind.Treated, 1).Was == "6,000 т на 05.09.2026"
                    ? "" : "второто поправяне заличи първоначалното");

                using var log = new ActionLogDialog(w.Db, w.Co, "1");

                notes.Add(log.StateAt(0) == "записано" ? "" : "другото действие се брои за поправено");
                notes.Add(log.StateAt(1).StartsWith("поправено ", StringComparison.Ordinal)
                    ? "" : $"състоянието е „{log.StateAt(1)}“");
                notes.Add(log.WhatAt(1).Contains("беше 6,000 т на 05.09.2026", StringComparison.Ordinal)
                    ? "" : $"редът не казва какво е било: „{log.WhatAt(1)}“");
                notes.Add(log.StateTipAt(1).Contains("6,000 т на 05.09.2026", StringComparison.Ordinal)
                    ? "" : "подсказката не казва какво е било");
            }

            // Поправеното третиране стои пред образуването от същия ден.
            using (var w = new AmendWorld(service, work))
            {
                w.Receive(1, 10m);

                w.Treat(Day(5), d => d.UseQuick(true));
                w.Generate(Day(5));

                var (fix, seed) = w.FixTreat(w.Entry(MoveKind.Treated));

                using (fix)
                {
                    notes.Add(fix.Blocking().Count == 0
                        ? "" : "третиране със същия ден е спряно: " + string.Join(" | ", fix.Blocking()));

                    w.Save(fix, seed);
                }

                notes.Add(w.Waiting().Total == 0m && w.Waiting().Unbacked.Count == 0
                    ? "" : $"от нищото се появиха {w.Waiting().Total} т в „Третиран“");
                notes.Add(w.Stock(StoreKind.Formed).Sum(s => s.Amount) == 10m ? "" : "образуваното се промени");
            }

            // И ред с НОВ номер: разделеното става повече редове, отколкото беше.
            using (var w = new AmendWorld(service, work))
            {
                w.Receive(1, 4m);
                w.Receive(2, 6m);

                w.Treat(Day(5), d => d.UseQuick(true));
                w.Generate(Day(5));

                var (fix, seed) = w.FixTreat(w.Entry(MoveKind.Treated));

                using (fix)
                {
                    fix.UsePerBatch(true);

                    w.Save(fix, seed);
                }

                var generated = w.Moves.Single(m => m.Kind == MoveKind.Generated).Id;

                notes.Add(w.Moves.Count(m => m.Kind == MoveKind.Treated && m.Id > generated) == 1
                    ? "" : "проверката няма ред с нов номер след образуването");
                notes.Add(w.Waiting().Total == 0m && w.Waiting().Unbacked.Count == 0
                    ? "" : $"денят зависи от реда на въвеждане: {w.Waiting().Total} т, {w.Waiting().Unbacked.Count} без основание");
            }

            // ---------------------------------------------- и другите два прозореца
            // Свободното при смяна на датата — не само при отваряне.
            using (var w = new AmendWorld(service, work))
            {
                w.Receive(1, 10m);

                w.Hand(Day(5), d => d.UseTake(0, 6m), direct: true);
                w.Hand(Day(15), d => d.UseTake(0, 4m), direct: true);

                var (fix, _) = w.FixHand(w.Entry(MoveKind.Submitted, 1));

                using (fix)
                {
                    notes.Add(fix.StockAt(0) == "6,000" ? "" : $"предаването показва налично {fix.StockAt(0)} т");

                    // Още при отваряне, преди да е пипната каквато и да е дата.
                    notes.Add(fix.NoticeText.Contains("Поправя се предаване от 05.09.2026", StringComparison.Ordinal) &&
                              fix.NoticeText.Contains("без 4,000 т", StringComparison.Ordinal)
                        ? "" : "предаването мълчи при отваряне: " + fix.NoticeText);

                    fix.UseDate(Day(6));

                    notes.Add(fix.StockAt(0) == "6,000" ? "" : $"на друга дата предаването показва {fix.StockAt(0)} т");
                    notes.Add(fix.NoticeText.Contains("без 4,000 т", StringComparison.Ordinal)
                        ? "" : "предаването не казва защо наличното е по-малко: " + fix.NoticeText);
                }
            }

            using (var w = new AmendWorld(service, work))
            {
                w.Receive(1, 10m);

                w.Treat(Day(2), d => d.UseQuick(true));
                w.Generate(Day(5), take: 6m);
                w.Generate(Day(15), take: 4m);

                var (fix, _) = w.FixMade(w.Entry(MoveKind.Generated, 1));

                using (fix)
                {
                    notes.Add(fix.StockAt(0) == "6,000" ? "" : $"образуването показва налично {fix.StockAt(0)} т");

                    // Още при отваряне, преди да е пипната каквато и да е дата.
                    notes.Add(fix.NoticeText.Contains("Поправя се образуване от 05.09.2026", StringComparison.Ordinal) &&
                              fix.NoticeText.Contains("без 4,000 т", StringComparison.Ordinal)
                        ? "" : "образуването мълчи при отваряне: " + fix.NoticeText);

                    fix.UseDate(Day(6));

                    notes.Add(fix.StockAt(0) == "6,000" ? "" : $"на друга дата образуването показва {fix.StockAt(0)} т");
                    notes.Add(fix.NoticeText.Contains("без 4,000 т", StringComparison.Ordinal)
                        ? "" : "образуването не казва защо наличното е по-малко: " + fix.NoticeText);
                }
            }

            // Отделният ред и при предаването.
            using (var w = new AmendWorld(service, work))
            {
                w.Receive(1, 4m);
                w.Receive(2, 3m);

                w.Hand(Day(5), d =>
                {
                    d.UsePerBatch(true);
                    d.UseTake(0, 7m);
                }, direct: true);

                notes.Add(w.Moves.Count(m => m.Kind == MoveKind.Submitted) == 2 ? "" : "предаването не е на два реда");

                var (fix, _) = w.FixHand(w.Entry(MoveKind.Submitted));

                using (fix)
                {
                    notes.Add(fix.PerBatchOn ? "" : "предаването губи отделния ред");
                    notes.Add(fix.Planned().Count == 2 ? "" : "предаването би се събрало в един ред");
                }
            }

            // Работният период и при другите два.
            using (var w = new AmendWorld(service, work))
            {
                w.Receive(1, 10m);

                w.Hand(Day(5), d => d.UseTake(0, 6m), direct: true);

                w.Db.Periods.SetWindow(w.Co, WorkPeriod.Of(new LedgerPeriod(2026, 10)));

                var (fix, _) = w.FixHand(w.Entry(MoveKind.Submitted));

                using (fix)
                {
                    notes.Add(fix.Blocking().Count == 0
                        ? "" : "предаване на старата дата иска период: " + string.Join(" | ", fix.Blocking()));

                    fix.UseDate(Day(6));

                    notes.Add(Has(fix.Blocking(), "работния период") ? "" : "предаване на нова дата минава извън периода");
                }
            }

            using (var w = new AmendWorld(service, work))
            {
                w.Receive(1, 10m);

                w.Treat(Day(2), d => d.UseQuick(true));
                w.Generate(Day(5));

                w.Db.Periods.SetWindow(w.Co, WorkPeriod.Of(new LedgerPeriod(2026, 10)));

                var (fix, _) = w.FixMade(w.Entry(MoveKind.Generated));

                using (fix)
                {
                    notes.Add(fix.Blocking().Count == 0
                        ? "" : "образуване на старата дата иска период: " + string.Join(" | ", fix.Blocking()));

                    fix.UseDate(Day(6));

                    notes.Add(Has(fix.Blocking(), "работния период") ? "" : "образуване на нова дата минава извън периода");
                }
            }

            // Запомненият обхват при образуване, и прозорецът, който поправката записва.
            using (var w = new AmendWorld(service, work))
            {
                w.Receive(1, 4m);
                w.Receive(2, 3m);

                w.Treat(Day(5), d => d.UseQuick(true));

                var waiting = w.Waiting().Shares;
                var pile = Generation.Piles([waiting[0]])[0];

                w.Page.ApplyGenerated(
                    Generation.Plan([new GenOrder(pile, [new TreatPick(waiting[0], 4m)], [new GenPart("191202", 4m)])]),
                    Day(6), new Dictionary<string, string>(StringComparer.Ordinal), "",
                    new ActionWindow([new WindowShare(waiting[0].Ewc, waiting[0].Batch.Id)], Split: true));

                var (fix, seed) = w.FixMade(w.Entry(MoveKind.Generated));

                using (fix)
                {
                    notes.Add(fix.BatchesOffered == 1
                        ? "" : $"образуване от една партида се върна с {fix.BatchesOffered}");
                    notes.Add(fix.PerPileOn ? "" : "запомненото разделяне при образуване не се връща");

                    w.Save(fix, seed);
                }

                notes.Add(w.Db.Actions.Window(seed.Action) is { Split: true, Scope.Count: 1 }
                    ? "" : "поправеното образуване не запомни прозореца си");
            }

            // Поправка, която не носи свой прозорец, не губи стария.
            using (var w = new AmendWorld(service, work))
            {
                w.Receive(1, 4m);
                w.Receive(2, 3m);

                w.Treat(Day(10), d => d.UseTake(0, 5m), [.. w.Stock(StoreKind.Received).Take(1)]);

                var entry = w.Entry(MoveKind.Treated);
                var seed = w.Page.Prepare(entry, out var scope, out _)!;

                w.Page.Replace(seed, () => w.Page.Apply(
                    Treatment.Plan([new TreatOrder(Treatment.Piles(scope)[0], 3m)]), Day(11)));

                notes.Add(w.Db.Actions.Window(entry.Action) is { Scope.Count: 1 }
                    ? "" : "старият прозорец се загуби при поправка без нов");
            }

            // Свито до нула: не се предлага, но обхватът го помни.
            using (var w = new AmendWorld(service, work))
            {
                var first = w.Receive(1, 4m);
                var second = w.Receive(2, 3m);

                w.Treat(Day(5), d => d.UseTake(0, 4m));
                w.Treat(Day(15), d => d.UseTake(0, 3m));

                var entry = w.Entry(MoveKind.Treated, 1);
                var (fix, seed) = w.FixTreat(entry);

                using (fix)
                {
                    notes.Add(fix.BatchesOffered == 1
                        ? "" : $"партида без свободно се предлага: {fix.BatchesOffered}");
                    notes.Add(fix.NoticeText.Contains("без 3,000 т", StringComparison.Ordinal)
                        ? "" : "не се казва какво е задържано: " + fix.NoticeText);

                    w.Save(fix, seed);
                }

                notes.Add(w.Db.Actions.Window(entry.Action) is { } kept &&
                          kept.Scope.Any(s => s.Batch == second) && kept.Scope.Any(s => s.Batch == first)
                    ? "" : "свитото изпадна от запомнения обхват");
            }

            // И при предаването, и при образуването: прозорецът на поправката не губи обхвата.
            using (var w = new AmendWorld(service, work))
            {
                var first = w.Receive(1, 4m);
                var second = w.Receive(2, 3m);

                w.Hand(Day(5), d => d.UseTake(0, 4m), direct: true);
                w.Hand(Day(15), d => d.UseTake(0, 3m), direct: true);

                var entry = w.Entry(MoveKind.Submitted, 1);
                var (fix, seed) = w.FixHand(entry);

                using (fix)
                {
                    notes.Add(fix.BatchesOffered == 1 ? "" : $"предаването предлага {fix.BatchesOffered} партиди");

                    w.Save(fix, seed);
                }

                notes.Add(w.Db.Actions.Window(entry.Action) is { } kept &&
                          kept.Scope.Any(s => s.Batch == second) && kept.Scope.Any(s => s.Batch == first)
                    ? "" : "при предаване свитото изпадна от обхвата");
            }

            using (var w = new AmendWorld(service, work))
            {
                var first = w.Receive(1, 4m);
                var second = w.Receive(2, 3m);

                w.Treat(Day(3), d => d.UseQuick(true));

                void Made(int day, decimal tons)
                {
                    var shares = w.Waiting().Shares;

                    w.Page.ApplyGenerated(
                        Generation.Plan([new GenOrder(
                            Generation.Piles(shares)[0], [new TreatPick(shares[0], tons)], [new GenPart("191202", tons)])]),
                        Day(day), new Dictionary<string, string>(StringComparer.Ordinal), "",
                        new ActionWindow([.. shares.Select(s => new WindowShare(s.Ewc, s.Batch.Id))]));
                }

                Made(5, 4m);
                Made(15, 3m);

                var entry = w.Entry(MoveKind.Generated, 1);
                var (fix, seed) = w.FixMade(entry);

                using (fix)
                {
                    notes.Add(fix.BatchesOffered == 1 ? "" : $"образуването предлага {fix.BatchesOffered} партиди");

                    w.Save(fix, seed);
                }

                notes.Add(w.Db.Actions.Window(entry.Action) is { } kept &&
                          kept.Scope.Any(s => s.Batch == second) && kept.Scope.Any(s => s.Batch == first)
                    ? "" : "при образуване свитото изпадна от обхвата");
            }

            // Мери се най-ниската точка, а не краят: след нея купчината пак расте.
            using (var w = new AmendWorld(service, work))
            {
                var batch = w.Receive(1, 10m);

                w.Treat(Day(2), d => d.UseTake(0, 5m));

                var waiting = w.Waiting().Shares[0];
                var pile = Generation.Piles([waiting])[0];

                void Made(int day, decimal tons) => w.Page.ApplyGenerated(
                    Generation.Plan([new GenOrder(pile, [new TreatPick(waiting, tons)], [new GenPart("191202", tons)])]),
                    Day(day), new Dictionary<string, string>(StringComparer.Ordinal));

                Made(3, 2m);
                Made(5, 3m);

                w.Treat(Day(7), d => d.UseTake(0, 5m));

                var (fix, _) = w.FixMade(w.Entry(MoveKind.Generated, 1));

                using (fix)
                    notes.Add(fix.StockAt(0) == "2,000"
                        ? "" : $"мери се краят, а не дъното: налично {fix.StockAt(0)} т вместо 2");
            }

            // Стигналото брои само влязлото: взетото от чужди по средата не го увеличава.
            using (var w = new AmendWorld(service, work))
            {
                w.Receive(1, 6m);

                void Made(int day, decimal tons)
                {
                    var share = w.Waiting().Shares[0];

                    w.Page.ApplyGenerated(
                        Generation.Plan([new GenOrder(
                            Generation.Piles([share])[0], [new TreatPick(share, tons)], [new GenPart("191202", tons)])]),
                        Day(day), new Dictionary<string, string>(StringComparer.Ordinal));
                }

                w.Treat(Day(2), d => d.UseTake(0, 3m));
                Made(3, 1m);
                w.Treat(Day(6), d => d.UseTake(0, 3m));
                Made(8, 3.5m);

                var (fix, _) = w.FixMade(w.Entry(MoveKind.Generated));

                using (fix)
                {
                    // Към 4-и са стигнали три тона: своето, три и половина, не може повече.
                    fix.UseDate(Day(4));

                    notes.Add(fix.StockAt(0) == "3,000"
                        ? "" : $"към 4-и се предлагат {fix.StockAt(0)} т, а са стигнали 3");
                }
            }

            // Без партиди, а наличното е взето по-късно: нищо за показване, казва се защо.
            using (var w = new AmendWorld(service, work))
            {
                // Записано, преди да има какво да се вземе: движението остава без дялове.
                w.Db.Writer.Write(w.Co, new MoveRequest(MoveKind.Treated, Day(5), "160216", 1m));

                var bare = w.Moves.Single();

                notes.Add(w.Links.All(l => l.MoveId != bare.Id) ? "" : "проверката иска движение без дялове");

                w.Receive(2, 4m);

                w.Treat(Day(8), d => d.UseTake(0, 4m));

                notes.Add(w.Page.Prepare(w.Bare(bare), out _, out var why) is null &&
                          why.Contains("не се намират", StringComparison.Ordinal)
                    ? "" : "празен прозорец вместо отказ: " + why);
            }

            // Седемдесет партиди не се режат без дума: прозорецът показва осем реда.
            using (var w = new AmendWorld(service, work))
            {
                var batches = Enumerable.Range(1, 10).Select(day => w.Receive(day, 1m)).ToList();

                w.Treat(Day(11), d => d.UseQuick(true));
                w.Generate(Day(12));

                var seed = w.Page.Prepare(w.Entry(MoveKind.Treated), out _, out _)!;

                var said = w.Page.Check(seed,
                    [new Planned(MoveKind.Treated, Day(13), "160216", 10m, [.. batches.Select(b => new Draw(b, 1m))])]);

                notes.Add(said.Count == 8 && said[^1].Contains("и още 3", StringComparison.Ordinal)
                    ? "" : $"{said.Count} реда, последният: {said.LastOrDefault()}");
            }

            // Една партида, една грешка: кодът не повтаря казаното за партидата.
            using (var w = new AmendWorld(service, work))
            {
                var batch = w.Receive(1, 10m);

                w.Treat(Day(2), d => d.UseQuick(true));
                w.Generate(Day(3));

                var formed = Handover.Piles(w.Stock(StoreKind.Formed))[0];

                w.Page.ApplySubmitted(Handover.Plan([new HandOrder(formed, 10m)]), Day(4));

                var seed = w.Page.Prepare(w.Entry(MoveKind.Generated), out _, out _)!;

                var said = w.Page.Check(seed, [new Planned(MoveKind.Generated, Day(3), "191202", 2m, [new Draw(batch, 2m)])]);

                notes.Add(said.Count == 1 ? "" : $"една грешка се казва {said.Count} пъти: " + string.Join(" | ", said));
            }

            // Задържаното е сбор на свитото, а своето, което расте, не го намалява.
            {
                var batch = new Batch(1, "2026-0001", "204455667", "Скрап ЕООД", "160216", Day(1));
                var other = batch with { Id = 2 };

                notes.Add(Amend.Held(
                        [new StockShare("160216", batch, 1m), new StockShare("160216", other, 3m)],
                        [new StockShare("160216", batch, 2m)]) == 3m
                    ? "" : "задържаното смесва свитото с порасналото");
            }

            // Своето остава в обхвата и там, където складът не го показва.
            using (var w = new AmendWorld(service, work))
            {
                var batch = w.Receive(1, 2m);

                w.Treat(Day(2), d => d.UseQuick(true));

                w.Db.Writer.Write(w.Co, new MoveRequest(
                    MoveKind.Treated, Day(5), "160216", 2m, Draws: [new Draw(batch, 2m)],
                    Fields: w.Db.Ledger.Fields(w.Moves.First(m => m.Kind == MoveKind.Treated).Id)));

                var seed = w.Page.Prepare(
                    w.Bare(w.Moves.Where(m => m.Kind == MoveKind.Treated).OrderBy(m => m.Id).Last()), out _, out _)!;

                notes.Add(seed.Scope!.Contains(("160216", batch)) ? "" : "взетото не е в обхвата");
            }

            // ---------------------------------------------- прозорецът като запис
            {
                var window = new ActionWindow([new WindowShare("160216", 5), new WindowShare("150101", 7)], 12.345m, true);

                var culture = System.Globalization.CultureInfo.CurrentCulture;

                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("bg-BG");

                var read = ActionWindow.Read(window.Write());

                System.Globalization.CultureInfo.CurrentCulture = culture;

                notes.Add(read is { Target: 12.345m, Split: true } && read.Scope.SequenceEqual(window.Scope)
                    ? "" : "прозорецът не се чете такъв, какъвто е записан");
                notes.Add(ActionWindow.Read("{не е json") is null && ActionWindow.Read("") is null && ActionWindow.Read(null) is null
                    ? "" : "нечетимото не е като липсващо");
            }

            using (var w = new AmendWorld(service, work))
            {
                var move = w.Db.Writer.Write(w.Co, new MoveRequest(MoveKind.Received, Day(1), "160216", 1m, Eik: "204455667", Partner: "Скрап ЕООД"));

                var one = w.Db.Actions.Add(w.Co, "1", MoveKind.Received, move, Day(1), "160216", 1m, action: "a");
                var two = w.Db.Actions.Add(w.Co, "1", MoveKind.Received, move, Day(1), "160216", 1m, action: "a");

                w.Db.Actions.Keep("a", new ActionWindow([new WindowShare("160216", 1)]));
                w.Db.Actions.Keep("a", new ActionWindow([new WindowShare("160216", 2)], 3m));

                notes.Add(w.Db.Actions.Window("a") is { Target: 3m } kept && kept.Scope[0].Batch == 2
                    ? "" : "вторият запис не заменя първия");

                w.Db.Actions.Forget(one);

                notes.Add(w.Db.Actions.Window("a") is not null ? "" : "прозорецът си отиде с първия от двата реда");

                w.Db.Actions.Forget(two);

                notes.Add(w.Db.Actions.Window("a") is null ? "" : "прозорецът остана след последния ред");

                w.Db.Actions.Add(w.Co, "1", MoveKind.Received, move, Day(1), "160216", 1m, action: "b");
                w.Db.Actions.Keep("b", new ActionWindow([new WindowShare("160216", 1)]));

                w.Db.Actions.Clear(w.Co, "1");

                notes.Add(w.Db.Actions.Window("b") is null ? "" : "изчистеният дневник остави прозорци");
            }

            // ---------------------------------------------- работният период
            using (var w = new AmendWorld(service, work))
            {
                w.Receive(1, 10m);

                w.Treat(Day(5), d => d.UseTake(0, 6m));

                w.Db.Periods.SetWindow(w.Co, WorkPeriod.Of(new LedgerPeriod(2026, 10)));

                var (fix, _) = w.FixTreat(w.Entry(MoveKind.Treated));

                using (fix)
                {
                    notes.Add(fix.Blocking().Count == 0
                        ? "" : "поправка на старата дата иска период: " + string.Join(" | ", fix.Blocking()));

                    // Друга дата е ново решение и се пита.
                    fix.UseDate(Day(6));

                    notes.Add(Has(fix.Blocking(), "работния период")
                        ? "" : "сменената дата минава извън периода");
                }
            }

            // ---------------------------------------------- рециклирано
            using (var w = new AmendWorld(service, work))
            {
                w.Receive(1, 10m);

                w.Treat(Day(2), d => d.UseQuick(true));
                w.Generate(Day(3), recycled: true);

                var (fix, seed) = w.FixMade(w.Entry(MoveKind.Recycled));

                using (fix) w.Save(fix, seed);

                notes.Add(w.Moves.Count(m => m.Kind == MoveKind.Recycled) == 1 && !w.Moves.Any(m => m.Kind == MoveKind.Generated)
                    ? "" : "поправеното рециклиране смени вида си");
            }
        }
        catch (Exception ex)
        {
            againError = ex.ToString();
        }
    });

    againThread.SetApartmentState(ApartmentState.STA);
    againThread.Start();
    againThread.Join();

    if (againError is not null) Console.WriteLine("     " + againError);

    var againProblems = againNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in againProblems) Console.WriteLine("     " + problem);

    Check("пресъздаването минава без изключение", againError is null);
    Check($"направени са всичките {againNotes.Count} проверки", againNotes.Count == 125, $"{againNotes.Count}");
    Check("прозорецът се връща, какъвто е бил, и веригата остава цяла", againProblems.Count == 0,
        $"{againProblems.Count} проблема");
}


// =============================================== Обновяване и известия
//
// Програмата пита GitHub при пускане, а тестът — никога: обновяването минава
// през подставена страна (FakeUpdates), а новините — през готов текст. Пита се
// това, което човекът вижда: кое известие стои горе, какво пише на копчето,
// и че нищо не хвърля навън, когато мрежата я няма.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - обновяване и известия");
{
    Lang.Use("bg");

    // ---------------------------------------------- новините: файлът е истината
    using (var newsTemp = new TempDb(work))
    using (var db = Workspace.Open(newsTemp.Path))
    {
        var day = new DateTime(2026, 11, 14);

        db.News.Add(new NewsItem("local:updated:1.3.0", day, NewsSeverity.Info, "Обновено", "", false));

        var added = db.News.Sync(
        [
            new NewsItem("a", day, NewsSeverity.Info, "Първо", "текст", false, "https://example.org/a"),
            new NewsItem("b", day.AddDays(-1), NewsSeverity.Important, "Второ", "", false)
        ]);

        Check("новото от файла влиза", added == 2 && db.News.List().Count == 3, $"{added} нови, {db.News.List().Count} общо");
        Check("връзката на съобщението се пази", db.News.List().First(n => n.Id == "a").Link == "https://example.org/a");

        db.News.MarkSeen("a");
        db.News.Hide("b");

        var again = db.News.Sync(
        [
            new NewsItem("a", day, NewsSeverity.Info, "Първо, поправено", "текст", false),
            new NewsItem("b", day.AddDays(-1), NewsSeverity.Important, "Второ", "", false),
            new NewsItem("c", day.AddDays(1), NewsSeverity.Critical, "Трето", "спешно", false)
        ]);

        var list = db.News.List();

        Check("при второ теглене ново е само новото", again == 1, $"{again}");
        Check("поправеният текст стига до базата", list.Any(n => n.Id == "a" && n.Title == "Първо, поправено"));
        Check("прочетеното остава прочетено и след поправка", list.First(n => n.Id == "a").Seen);
        Check("скритото не се връща от файла", list.All(n => n.Id != "b"));
        Check("скритото не се брои за непрочетено", db.News.UnseenCount() == 2, $"{db.News.UnseenCount()}");

        db.News.Sync([new NewsItem("c", day.AddDays(1), NewsSeverity.Critical, "Трето", "спешно", false)]);

        var ids = db.News.List().Select(n => n.Id).ToList();

        Check("махнатото от файла си отива и оттук", !ids.Contains("a"), string.Join(", ", ids));
        Check("местното съобщение не се маха от файла", ids.Contains("local:updated:1.3.0"), string.Join(", ", ids));

        db.News.Sync([]);

        Check("празен файл оставя само местното",
            db.News.List().Select(n => n.Id).SequenceEqual(["local:updated:1.3.0"]),
            string.Join(", ", db.News.List().Select(n => n.Id)));
    }

    // ---------------------------------------------- news.json
    var today = new DateOnly(2026, 11, 14);

    const string feed = """
        {
          // Пише се на ръка — коментарите и запетаята накрая не пречат.
          "news": [
            { "id": "ok", "date": "2026-11-10", "severity": "important", "title": "Срок", "body": "До 31.03", "link": "https://example.org" },
            { "id": "http", "date": "2026-11-10", "title": "Без https", "link": "http://example.org" },
            { "id": "", "date": "2026-11-10", "title": "Без белег" },
            { "id": "notitle", "date": "2026-11-10" },
            { "id": "nodate", "title": "Без дата" },
            { "id": "local:x", "date": "2026-11-10", "title": "Подправено местно" },
            { "id": "old", "date": "2026-01-01", "title": "Изтекло", "until": "2026-11-13" },
            { "id": "today", "date": "2026-01-01", "title": "Изтича днес", "until": "2026-11-14" },
            { "id": "future", "date": "2026-11-10", "title": "За новите", "from": "9.0.0" },
            { "id": "fixed", "date": "2026-11-10", "title": "За старите", "below": "1.3.0" },
            { "id": "mine", "date": "2026-11-10", "title": "За това издание", "from": "1.3.0", "below": "1.3.1" },
            { "id": "ok", "date": "2026-11-11", "title": "Повторен белег" },
            { "id": "crit", "date": "2026-11-12", "severity": "CRITICAL", "title": "Спешно" },
          ]
        }
        """;

    var parsed = NewsFeed.Parse(feed, "1.3.0", today);
    var parsedIds = parsed?.Select(n => n.Id).ToList() ?? [];

    Check("файлът с новини се чете", parsed is not null);
    Check("влизат само годните и отнасящите се до това издание",
        parsedIds.SequenceEqual(["ok", "http", "today", "mine", "crit"]), string.Join(", ", parsedIds));
    Check("тежестта се разпознава без значение от регистъра",
        parsed?.FirstOrDefault(n => n.Id == "crit")?.Severity == NewsSeverity.Critical);
    Check("важното остава важно", parsed?.FirstOrDefault(n => n.Id == "ok")?.Severity == NewsSeverity.Important);
    Check("връзка без https се изпуска", parsed?.FirstOrDefault(n => n.Id == "http")?.Link == "");
    Check("https връзката остава", parsed?.FirstOrDefault(n => n.Id == "ok")?.Link == "https://example.org");
    Check("повреден файл не се чете изобщо", NewsFeed.Parse("{ не е json", "1.3.0", today) is null);
    Check("файл без списък не се чете", NewsFeed.Parse("""{ "items": [] }""", "1.3.0", today) is null);
    Check("и направо масив се чете",
        NewsFeed.Parse("""[{ "id": "x", "date": "2026-11-01", "title": "T" }]""", "1.3.0", today)?.Count == 1);
    Check("дълъг текст се реже",
        NewsFeed.Parse($$"""[{ "id": "x", "date": "2026-11-01", "title": "T", "body": "{{new string('а', 5000)}}" }]""",
            "1.3.0", today)?[0].Body.Length <= 2001);

    // Истинският news.json (в корена на хранилището) се пише на ръка — и трябва да
    // се чете. Търси се нагоре по папките, до папката с RiosvImports.sln.
    string? template = null;

    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && template is null; dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, "news.json");
        if (File.Exists(candidate) && File.Exists(Path.Combine(dir.FullName, "RiosvImports.sln"))) template = candidate;
    }

    Check("news.json в корена се чете и има съобщение",
        template is not null && NewsFeed.Parse(File.ReadAllText(template), "1.3.0", today) is { Count: > 0 },
        template ?? "няма го нагоре по папките");

    // ---------------------------------------------- „Обновено до …“
    using (var noteTemp = new TempDb(work))
    using (var db = Workspace.Open(noteTemp.Path))
    {
        Check("първото пускане изобщо не казва нищо", !NewsFeed.NoteUpgrade(db, "1.3.0"));
        Check("същото издание не казва нищо", !NewsFeed.NoteUpgrade(db, "1.3.0"));

        db.Settings.Set(SettingsStore.Keys.UpdateNotes, "1.3.1\n## Ново\n- По-бързо пускане");

        Check("новото издание се казва", NewsFeed.NoteUpgrade(db, "1.3.1"));

        var note = db.News.List().FirstOrDefault(n => n.Id == "local:updated:1.3.1");

        Check("известието носи бележките на изданието", note?.Body.Contains("• По-бързо пускане") == true, note?.Body);
        Check("и връзка към страницата му", note?.Link.EndsWith("/releases/tag/v1.3.1") == true, note?.Link);
        Check("казва се веднъж", !NewsFeed.NoteUpgrade(db, "1.3.1") && db.News.List().Count == 1);

        db.Settings.Set(SettingsStore.Keys.UpdateNotes, "9.9.9\nчужди бележки");
        NewsFeed.NoteUpgrade(db, "1.4.0");

        var other = db.News.List().FirstOrDefault(n => n.Id == "local:updated:1.4.0");

        Check("бележки за друго издание не се показват",
            other is not null && !other.Body.Contains("чужди") && other.Body.Contains("1.3.1"), other?.Body);
        Check("старото „Обновено до“ отстъпва на новото",
            db.News.List().Select(n => n.Id).SequenceEqual(["local:updated:1.4.0"]),
            string.Join(", ", db.News.List().Select(n => n.Id)));
        Check("връщане към по-старо издание не казва нищо", !NewsFeed.NoteUpgrade(db, "1.2.0"));
    }

    // ---------------------------------------------- номерата
    Check("„v“ пред номера не пречи", AppVersion.Parse("v1.3.0")?.ToNormalizedString() == "1.3.0");
    Check("1.10.0 е след 1.9.0", AppVersion.IsNewer("1.10.0", "1.9.0"));
    Check("пробното е преди окончателното",
        AppVersion.IsNewer("1.3.0", "1.3.0-beta.2") && !AppVersion.IsNewer("1.3.0-beta.2", "1.3.0"));
    Check("същото не е по-ново", !AppVersion.IsNewer("1.3.0", "v1.3.0"));
    Check("нечетимото никога не е по-ново", !AppVersion.IsNewer("последно", "1.0.0"));
    Check("сглобката има четим номер", AppVersion.Parse(AppVersion.Build) is not null, AppVersion.Build);

    // ---------------------------------------------- бележките
    var plain = ReleaseNotes.Plain("""
        ## 1.3.1
        **Ново:**
        - Известия на началния екран
        * Проверка за [издание](https://example.org)
        1. Таблица `r_yr_generated_waste`

        ---
        Край
        """);

    Check("заглавието губи решетките", plain.StartsWith("1.3.1"), plain);
    Check("удебеленото губи звездичките", plain.Contains("Ново:") && !plain.Contains("**"), plain);
    Check("списъкът става точки",
        plain.Contains("• Известия на началния екран") && plain.Contains("• Проверка за издание"), plain);
    Check("и номерираният", plain.Contains("• Таблица r_yr_generated_waste"), plain);
    Check("правата черта изчезва", !plain.Contains("---"), plain);

    var wrapped = ReleaseNotes.Plain("### Ново\n\n- **Известия на началния\n  екран.** Стоят най-горе.\n- Второ");

    Check("пренесеният ред остава в своята точка, с удебеленото през него",
        wrapped.Contains("• Известия на началния екран. Стоят най-горе.") && wrapped.Contains("• Второ") &&
        !wrapped.Contains("**"), wrapped.Replace(Environment.NewLine, " | "));

    var cutNotes = ReleaseNotes.Plain(string.Join("\n", Enumerable.Range(1, 200).Select(i => $"- ред {i}")), 300);

    Check("дългите бележки се режат на край на ред, с многоточие",
        cutNotes.EndsWith("…") && cutNotes.Split('\n').Length < 60 && !cutNotes.Contains("ред 200"),
        $"{cutNotes.Split('\n').Length} реда");

    // ---------------------------------------------- изданията в GitHub
    const string releases = """
        [
          { "tag_name": "v1.4.0-beta.1", "prerelease": true, "draft": false, "body": "проба", "html_url": "https://github.com/x/y/releases/tag/v1.4.0-beta.1" },
          { "tag_name": "v1.5.0", "prerelease": false, "draft": true, "body": "чернова" },
          { "tag_name": "nightly", "prerelease": false, "draft": false },
          { "tag_name": "v1.3.1", "prerelease": false, "draft": false, "body": "## Поправки", "html_url": "https://github.com/x/y/releases/tag/v1.3.1" },
          { "tag_name": "v1.2.0", "prerelease": false, "draft": false }
        ]
        """;

    Check("последното окончателно издание, без черновата", ReleaseProbe.Latest(releases, prerelease: false)?.Version == "1.3.1",
        ReleaseProbe.Latest(releases, prerelease: false)?.Version);
    Check("с пробните — пробното", ReleaseProbe.Latest(releases, prerelease: true)?.Version == "1.4.0-beta.1",
        ReleaseProbe.Latest(releases, prerelease: true)?.Version);
    Check("бележките и страницата идват от GitHub",
        ReleaseProbe.Latest(releases, false) is { Notes: "## Поправки", Page: var releasePage } && releasePage.EndsWith("v1.3.1"));
    Check("празен списък — няма издание", ReleaseProbe.Latest("[]", false) is null);

    // ---------------------------------------------- грешките с думи
    Check("404 значи, че хранилището още го няма",
        UpdateBackends.Explain(new HttpRequestException("x", null, System.Net.HttpStatusCode.NotFound)).Contains("още го няма"));
    Check("403 значи, че GitHub е спрял проверките",
        UpdateBackends.Explain(new HttpRequestException("x", null, System.Net.HttpStatusCode.Forbidden)).Contains("спря проверките"));
    Check("без мрежа — „няма връзка“", UpdateBackends.Explain(new HttpRequestException("No such host")).Contains("Няма връзка"));
    Check("изтекло време — „не отговори навреме“", UpdateBackends.Explain(new TaskCanceledException()).Contains("навреме"));
    Check("номер на ред в текста не е отказ от GitHub",
        UpdateBackends.Explain(new InvalidOperationException("в ред 403 и 404")) == "в ред 403 и 404");

    // ---------------------------------------------- службата, без мрежа
    {
        var fake = new FakeUpdates { Found = new ReleaseInfo("1.3.1", "- ново", "https://example.org/v1.3.1") };
        var updates = new UpdateService(_ => fake);
        var changes = 0;

        updates.Changed += (_, _) => changes++;

        Check("службата тръгва без проверка", updates.State.Stage == UpdateStage.Idle);

        updates.CheckAsync().Wait();

        Check("проверката намира по-новото",
            updates.State is { Stage: UpdateStage.Available, Version: "1.3.1", CheckedAt: not null }, $"{updates.State.Stage}");

        updates.DownloadAsync().Wait();

        Check("свалянето стига до готово", updates.State.Stage == UpdateStage.Ready, $"{updates.State.Stage}");
        Check("напредъкът се казва по пътя", fake.Reported == 3 && changes >= 5, $"{fake.Reported} отчета, {changes} промени");

        fake.LateProgress?.Invoke(50);

        Check("закъснял напредък не връща готовото назад", updates.State.Stage == UpdateStage.Ready, $"{updates.State.Stage}");
        Check("готовото се слага при излизане", updates.ApplyOnExit() && fake.Applied);
    }

    {
        var failing = new FakeUpdates { Fail = new HttpRequestException("x", null, System.Net.HttpStatusCode.NotFound) };
        var updates = new UpdateService(_ => failing);
        var threw = false;

        try { updates.CheckAsync().Wait(); }
        catch { threw = true; }

        Check("неуспехът не хвърля навън", !threw);
        Check("неуспехът се записва в дневника на теста, не в профила",
            RiosvImports.App.Program.CrashLogPath.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) &&
            File.Exists(RiosvImports.App.Program.CrashLogPath), RiosvImports.App.Program.CrashLogPath);
        Check("неуспехът е състояние с обяснение",
            updates.State is { Stage: UpdateStage.Failed, Problem.Length: > 0 }, updates.State.Problem);
        Check("без намерено няма сваляне", updates.DownloadAsync().IsCompleted && failing.Downloads == 0);
        Check("без готово нищо не се слага", !updates.ApplyOnExit() && !failing.Applied);
    }

    {
        var probe = new FakeUpdates { CanApply = false, Found = new ReleaseInfo("1.3.1", "", "https://example.org") };
        var updates = new UpdateService(_ => probe);

        updates.CheckAsync().Wait();
        updates.DownloadAsync().Wait();

        Check("пуснатата от папка само казва, не сваля",
            updates.State.Stage == UpdateStage.Available && probe.Downloads == 0, $"{updates.State.Stage}, {probe.Downloads}");
    }

    {
        var made = new List<bool>();
        var pending = new FakeUpdates { Pending = new ReleaseInfo("1.3.1", "", "") };
        var updates = new UpdateService(pre => { made.Add(pre); return pending; });

        Check("свалено отпреди чака рестарт още при пускане", updates.State.Stage == UpdateStage.Ready);

        updates.UsePrerelease(true);

        Check("пробните издания сглобяват страната наново", made.SequenceEqual([false, true]) && updates.Prerelease,
            string.Join(", ", made));
    }

    {
        var updates = new UpdateService(_ => new FakeUpdates());
        updates.CheckAsync().Wait();

        Check("без по-ново — „последното издание“", updates.State.Stage == UpdateStage.Current);
    }

    // ---------------------------------------------- екраните
    var uiNotes = new List<string>();
    string? uiError = null;

    void Say(bool ok, string problem) => uiNotes.Add(ok ? "" : problem);

    var uiThread = new Thread(() =>
    {
        try
        {
            using var temp = new TempDb(work);
            using var db = Workspace.Open(temp.Path);

            var docId = db.Documents.Create("year", "r_yr_generated_waste", "Отчет за проба",
                new Dictionary<string, string> { ["year"] = "2025" });

            var release = new ReleaseInfo("1.3.1", "## Ново\n- Известия на началния екран", "https://example.org/v1.3.1");
            var fake = new FakeUpdates { Found = release };
            var updates = new UpdateService(_ => fake);

            db.News.Sync([new NewsItem("n1", new DateTime(2026, 11, 14), NewsSeverity.Important, "Срок за подаване",
                "Годишните отчети — до 31 март.", false, "https://example.org/srok")]);

            using var home = new HomePage(db, updates) { Width = 1000, Height = 800 };
            _ = home.Handle;
            home.PerformLayout();
            home.Refresh();

            var cards = home.Notices;

            Say(cards.Count == 1 && cards[0].Heading == "Срок за подаване", "новината не стои в известията: " +
                string.Join(" | ", cards.Select(c => c.Heading)));
            Say(home.NoticesTitle.Contains("1 нови"), "заглавието не казва колко са новите: " + home.NoticesTitle);
            Say(cards.Count > 0 && cards[0].ActionTexts.Contains("Прочети още") && cards[0].Dismissible,
                "новината е без „Прочети още“ или без кръстче");
            Say(db.News.UnseenCount() == 0, "показаното не е отбелязано като прочетено");

            updates.CheckAsync().Wait();
            home.ShowUpdates();
            cards = home.Notices;

            Say(cards.Count == 2 && cards[0].Heading == "Налично е издание 1.3.1",
                "изданието не е първото известие: " + string.Join(" | ", cards.Select(c => c.Heading)));
            Say(cards[0].ActionTexts.SequenceEqual(["Обнови сега", "Какво е новото"]),
                "копчетата на изданието са " + string.Join(", ", cards[0].ActionTexts));
            Say(cards[0].Message.Contains("• Известия на началния екран"), "бележките не стоят в картата: " + cards[0].Message);

            // „Скрий до следващото издание“ — картата си отива, настройките помнят кое.
            cards[0].PressDismiss();

            Say(home.Notices.All(c => !c.Heading.Contains("1.3.1")), "скритото издание остана на началния екран");
            Say(db.Settings.Get(SettingsStore.Keys.UpdateSkipped) == "1.3.1", "скритото издание не е запомнено");

            db.Settings.Set(SettingsStore.Keys.UpdateSkipped, "");

            // Свалянето — картата се пипа на място, не се пресъздава при всеки процент.
            updates.Show(new UpdateState(UpdateStage.Downloading, release, 40));
            home.ShowUpdates();

            var downloading = home.Notices[0];

            updates.Show(new UpdateState(UpdateStage.Downloading, release, 41));
            home.ShowUpdates();

            Say(ReferenceEquals(downloading, home.Notices[0]), "картата при сваляне се пресъздава при всеки процент");
            Say(home.Notices[0].Heading.Contains("41 %") && home.Notices[0].Progress == 41,
                "напредъкът не стига до картата: " + home.Notices[0].Heading);

            var restarts = 0;
            home.RestartRequested += (_, _) => restarts++;

            updates.Show(new UpdateState(UpdateStage.Ready, release, 100));
            home.ShowUpdates();

            Say(home.Notices[0].Heading == "Издание 1.3.1 е готово" && home.Notices[0].Press("Рестартирай и обнови") && restarts == 1,
                "готовото издание не води до рестарт: " + home.Notices[0].Heading);

            // Махнатата новина си отива и от базата; празното казва какво е проверено.
            updates.Show(new UpdateState(UpdateStage.Current, CheckedAt: new DateTime(2026, 11, 14, 9, 30, 0)));
            home.Refresh();
            home.Notices.First(c => c.Heading == "Срок за подаване").PressDismiss();

            Say(home.Notices.Count == 1 && home.Notices[0].Heading == "Няма нови известия",
                "след махането остава: " + string.Join(" | ", home.Notices.Select(c => c.Heading)));
            Say(db.News.List().Count == 0, "махнатата новина се връща от базата");

            // Пуснатата от папка: изтегля се от GitHub, не се сваля.
            var folder = new UpdateService(_ => new FakeUpdates { CanApply = false, Found = release });
            folder.CheckAsync().Wait();

            using (var plainHome = new HomePage(db, folder) { Width = 1000, Height = 800 })
            {
                _ = plainHome.Handle;
                plainHome.Refresh();

                Say(plainHome.Notices.Count > 0 && plainHome.Notices[0].ActionTexts.SequenceEqual(["Изтегли от GitHub"]),
                    "пуснатата от папка предлага " + string.Join(", ", plainHome.Notices.FirstOrDefault()?.ActionTexts ?? []));
            }

            // „Продължете оттам…“ отваря самия отчет.
            long? opened = null;
            home.OpenReport += (_, id) => opened = id;
            home.ClickRecent(0);

            Say(opened == docId, $"щракнатият отчет отвори {opened?.ToString() ?? "нищо"}, а не {docId}");

            // Настройките казват същото, което и началният екран.
            using (var settings = new SettingsPage(db, null, updates) { Width = 1100, Height = 800 })
            {
                _ = settings.Handle;

                var asked = 0;
                settings.RestartRequested += (_, _) => asked++;

                updates.Show(new UpdateState(UpdateStage.Available, release));
                settings.ShowUpdates();

                Say(settings.UpdateButtonText == "Обнови до 1.3.1", "копчето в настройките е „" + settings.UpdateButtonText + "“");
                Say(settings.UpdateSays.Contains("Налично е издание 1.3.1"), "настройките казват: " + settings.UpdateSays);

                updates.Show(new UpdateState(UpdateStage.Ready, release, 100));
                settings.ShowUpdates();
                settings.ClickUpdate();

                Say(settings.UpdateButtonText == "Рестартирай и обнови" && asked == 1,
                    "готовото в настройките: „" + settings.UpdateButtonText + $"“, {asked} искания");

                updates.Show(new UpdateState(UpdateStage.Current, CheckedAt: DateTime.Now));
                settings.ShowUpdates();

                Say(settings.UpdateButtonText.Length == 0 && settings.UpdateSays.StartsWith("Това е последното издание"),
                    "без ново копчето остава: „" + settings.UpdateButtonText + "“");

                var diagnostics = SettingsPage.Diagnostics(db, updates);

                Say(diagnostics.Contains("RIOSV Imports 1.3.0") && diagnostics.Contains("Update: Current"),
                    "данните за поддръжка: " + diagnostics.Replace(Environment.NewLine, " | "));
            }

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var hidden = SettingsPage.HideProfile(Path.Combine(local, "RIOSV Imports", "workspace.db"));

            Say(hidden == Path.Combine("%LOCALAPPDATA%", "RIOSV Imports", "workspace.db"),
                "профилът на потребителя не е скрит: " + hidden);

            // Главният прозорец: Ctrl+цифра, номерът долу в лентата, броячът.
            using var shell = new ShellForm(service, db, null, updates);
            _ = shell.Handle;

            Say(shell.Text.StartsWith("RIOSV Imports 1.3.0"), "заглавната лента е „" + shell.Text + "“");

            Say(shell.Press(Keys.Control | Keys.D2) && shell.ActivePage == "LedgerPage", "Ctrl+2 отвори " + shell.ActivePage);
            Say(shell.Press(Keys.Control | Keys.NumPad8) && shell.ActivePage == "SettingsPage", "Ctrl+8 отвори " + shell.ActivePage);
            Say(!shell.Press(Keys.Control | Keys.D9) && shell.ActivePage == "SettingsPage", "Ctrl+9 смени екрана на " + shell.ActivePage);
            Say(shell.Press(Keys.Control | Keys.D1) && shell.ActivePage == "HomePage", "Ctrl+1 отвори " + shell.ActivePage);
            Say(ShellForm.PageByKey(Keys.Control | Keys.Shift | Keys.D1) is null, "Ctrl+Shift+1 се взима за екран");

            updates.Show(new UpdateState(UpdateStage.Current));

            Say(shell.RailVersionText == "Издание 1.3.0", "долу в лентата пише „" + shell.RailVersionText + "“");

            updates.Show(new UpdateState(UpdateStage.Ready, release, 100));

            Say(shell.RailVersionText.Contains("Ново издание 1.3.1"), "поканата долу в лентата е „" + shell.RailVersionText + "“");
            Say(shell.HomeBadge >= 1, $"броячът на „Начало“ е {shell.HomeBadge} при готово издание");
        }
        catch (Exception ex)
        {
            uiError = ex.ToString();
        }
    });

    uiThread.SetApartmentState(ApartmentState.STA);
    uiThread.Start();
    uiThread.Join();

    if (uiError is not null) Console.WriteLine("     " + uiError);

    var uiProblems = uiNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in uiProblems) Console.WriteLine("     " + problem);

    Check("екраните на обновяването минават без изключение", uiError is null);
    Check($"направени са всичките {uiNotes.Count} проверки на екраните", uiNotes.Count == 31, $"{uiNotes.Count}");
    Check("началният екран, настройките и лентата казват едно и също", uiProblems.Count == 0, $"{uiProblems.Count} проблема");
}

// =============================================== 1.3.1: „Месец“ казва защо отчетът не излиза
//
// Дотук под таблицата стоеше първата бележка от списъка, отрязана на един ред, а
// съставянето казваше „чакат 1,250 т“ без месец и пускаше копчето „Състави“, за
// да откаже след натискането. Сега присъдата казва може ли, защо не и какво да
// се направи; проверката показва целия списък; съставянето назовава месеца и
// купчините и гаси копчето. Внасянето от файл вече е само в „Склад“.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - „Месец“ казва защо отчетът не излиза и не внася файлове");
{
    var whyNotes = new List<string>();
    string? whyError = null;

    var whyThread = new Thread(() =>
    {
        try
        {
            var whyPath = Path.Combine(work, $"why_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(whyPath);

            var companyId = db.Companies.Add("203344556", "Метал ООД");

            // Август: само приемане — отчетът излиза.
            var august = db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 8, 4), "170405", 10m,
                Eik: "204455667", Partner: "Скрап ЕООД", Document: "Протокол 1330"));

            db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 8, 6), "150101", 3m,
                Eik: "205566778", Partner: "Хартия АД"));

            // Септември: третирано без образувано — отчетът спира.
            var treated = db.Writer.Write(companyId, new MoveRequest(
                MoveKind.Treated, new DateOnly(2026, 9, 5), "170405", 6m));

            var september = new LedgerPeriod(2026, 9);
            var augustMonth = new LedgerPeriod(2026, 8);

            using (var ledger = new LedgerPage(service, db))
            {
                ledger.UseMonth(september);

                whyNotes.Add(ledger.ChecksText.Contains("не може да се състави", StringComparison.Ordinal) &&
                             ledger.ChecksText.Contains("6,000", StringComparison.Ordinal)
                    ? "" : "присъдата не казва, че отчетът спира и колко чака: " + ledger.ChecksText);

                whyNotes.Add(ledger.FixText.Contains("Образувай", StringComparison.Ordinal) &&
                             ledger.FixText.Contains("170405", StringComparison.Ordinal)
                    ? "" : "поправката не казва къде и коя купчина: " + ledger.FixText);

                whyNotes.Add(ledger.ChecksColour == Theme.Error ? "" : "спрян отчет не е в червено");

                whyNotes.Add(ledger.Found.Any(i => i.StopsReport)
                    ? "" : "проверката не отбелязва, че спира отчета");

                whyNotes.Add(ledger.ReviewText.Contains($"({ledger.Found.Count})", StringComparison.Ordinal)
                    ? "" : "„Проверка…“ не носи броя: " + ledger.ReviewText);

                whyNotes.Add(!ledger.CanCompose(september, september) ? "" : "септември се съставя, докато чака");

                var weight = ledger.WeighForTest(september, september);

                whyNotes.Add(weight.Blocked && weight.Waiting.Count == 1 &&
                             weight.Waiting[0].Month == september &&
                             weight.Waiting[0].Named().Contains("170405", StringComparison.Ordinal)
                    ? "" : "съставянето не назовава месеца и купчината");

                // Прозорецът за съставяне: спряното тримесечие отваря месеца, който
                // може, и казва изхода; спряното копче не се натиска.
                using (var book = new BookDialog(augustMonth, (from, to) => ledger.WeighForTest(from, to)))
                {
                    whyNotes.Add(book.ScopeIndex == 0 && book.ComposeOffered
                        ? "" : "готов месец в неготово тримесечие не се предлага сам");

                    book.UseScope(1);

                    whyNotes.Add(!book.ComposeOffered ? "" : "„Състави“ работи за спряно тримесечие");

                    whyNotes.Add(book.PreviewText.Contains("септември 2026", StringComparison.Ordinal) &&
                                 book.PreviewText.Contains("Отчет само за август 2026", StringComparison.Ordinal)
                        ? "" : "спряното тримесечие не казва месеца и изхода: " + book.PreviewText);
                }

                // Августът е чист, но тримесечието — не: и това се казва.
                ledger.UseMonth(augustMonth);

                whyNotes.Add(ledger.ChecksText.Contains("може да се състави", StringComparison.Ordinal) &&
                             !ledger.ChecksText.Contains("не може", StringComparison.Ordinal)
                    ? "" : "чистият месец не казва, че отчетът излиза: " + ledger.ChecksText);

                whyNotes.Add(ledger.FixText.Contains("Тримесечието още не е готово", StringComparison.Ordinal) &&
                             ledger.FixText.Contains("септември", StringComparison.Ordinal)
                    ? "" : "не се казва, че тримесечието чака септември: " + ledger.FixText);

                // Проверката: целият списък, с думата „спира отчета“ и пътя до склада.
                ledger.UseMonth(september);

                using (var check = new MonthCheckDialog("Проверка", MonthVerdict.For(september, 1, false,
                           ledger.Found, [ledger.WeighForTest(september, september).Waiting[0]]),
                           ledger.Found, ledger.WeighForTest(september, september).Waiting[0]))
                {
                    whyNotes.Add(check.EntryCount == ledger.Found.Count ? "" : $"проверката показва {check.EntryCount} реда");
                    whyNotes.Add(check.Words.FirstOrDefault() == "Спира отчета"
                        ? "" : "първият ред не е това, което спира отчета: " + string.Join(", ", check.Words));
                    whyNotes.Add(check.TreatedOffered ? "" : "„Отвори „Третиран““ го няма");

                    check.Pick(0);

                    whyNotes.Add(check.DetailText.Contains("Какво да се направи", StringComparison.Ordinal) &&
                                 check.DetailText.Contains("170405", StringComparison.Ordinal)
                        ? "" : "избраното не казва какво да се направи: " + check.DetailText);

                    check.Press();

                    whyNotes.Add(check.Action == CheckAction.OpenTreated ? "" : "двойното щракване не води до склада");
                }

                // Обявеното образуване изпразва „Третиран“ — и присъдата се обръща.
                db.Writer.Write(companyId, new MoveRequest(
                    MoveKind.Generated, new DateOnly(2026, 9, 7), "191202", 6m));

                ledger.Refresh();

                whyNotes.Add(!ledger.ChecksText.Contains("не може да се състави", StringComparison.Ordinal) &&
                             ledger.CanCompose(september, september)
                    ? "" : "след образуването отчетът пак спира: " + ledger.ChecksText);

                // Търсенето: по клиент, по документ зад партидата, по код.
                ledger.UseMonth(augustMonth);

                ledger.UseSearch("скрап");
                whyNotes.Add(ledger.RowCount == 1 && ledger.SummaryText.Contains("Показани 1 реда", StringComparison.Ordinal)
                    ? "" : $"търсенето по клиент дава {ledger.RowCount} реда: " + ledger.SummaryText);

                ledger.UseSearch("1330");
                whyNotes.Add(ledger.RowCount == 1 ? "" : $"търсенето по документ дава {ledger.RowCount} реда");

                ledger.UseSearch("150101 хартия");
                whyNotes.Add(ledger.RowCount == 1 ? "" : $"две думи наведнъж дават {ledger.RowCount} реда");

                ledger.UseSearch(string.Empty);
                whyNotes.Add(ledger.RowCount == 2 && !ledger.SummaryText.Contains("Показани", StringComparison.Ordinal)
                    ? "" : "изчистеното търсене не връща всичко");

                // Избраният ред оцелява при пресмятане.
                ledger.Pick(1);
                var chosen = ledger.SelectedMove;
                ledger.Refresh();

                whyNotes.Add(chosen is not null && ledger.SelectedMove == chosen
                    ? "" : "изборът се губи при пресмятане");

                // Махането гледа веригата (дефект 9): от приемането е черпено.
                var refusal = ledger.RemoveForTest(august);

                whyNotes.Add(refusal is not null && refusal.Contains("вече е взето", StringComparison.Ordinal) &&
                             db.Ledger.Read(companyId).Any(m => m.Id == august)
                    ? "" : "приемане, от което е черпено, се маха: " + (refusal ?? "махна се"));

                // Затвореният месец не се маха.
                db.Periods.Close(companyId, "1", augustMonth);

                var other = db.Ledger.Read(companyId).First(m => m.Kind == MoveKind.Received && m.Id != august).Id;

                whyNotes.Add(ledger.RemoveForTest(other) == "затворен" &&
                             db.Ledger.Read(companyId).Any(m => m.Id == other)
                    ? "" : "от затворен месец се маха");

                db.Periods.Reopen(companyId, "1", augustMonth);

                whyNotes.Add(ledger.RemoveForTest(other) is null && db.Ledger.Read(companyId).All(m => m.Id != other)
                    ? "" : "свободно приемане не се маха");

                // И таблицата на месеца спира записа, щом махнатият ред е черпен.
                using (var sheet = new MoveSheetDialog(service, db, companyId, "1", MoveKind.Received, augustMonth))
                {
                    sheet.Grid!.RemoveRows([0]);
                    sheet.AcceptForTest();

                    whyNotes.Add(!sheet.Saved && sheet.Refusal.Contains("вече е взето", StringComparison.Ordinal) &&
                                 db.Ledger.Read(companyId).Any(m => m.Id == august)
                        ? "" : "таблицата маха черпено приемане: " + sheet.Refusal);
                }

                whyNotes.Add(treated > 0 ? "" : "третирането не се записа");
            }

            // Внасянето от файл: от „Месец“ го няма и се казва къде е; от склада го има.
            using (var month = new MoveSheetDialog(service, db, companyId, "1", MoveKind.Received, augustMonth))
            {
                whyNotes.Add(!month.ImportOffered ? "" : "„Месец“ още внася файлове");
                whyNotes.Add(month.HintText.Contains("Склад", StringComparison.Ordinal) &&
                             month.HintText.Contains("Внеси", StringComparison.Ordinal)
                    ? "" : "не се казва къде се внася: " + month.HintText);
            }

            using (var store = new MoveSheetDialog(service, db, companyId, "1", MoveKind.Received, augustMonth,
                       SheetImport.Offered))
                whyNotes.Add(store.ImportOffered && store.HintText.Length == 0
                    ? "" : "от склада внасянето го няма");

            using (var locked = new MoveSheetDialog(service, db, companyId, "1", MoveKind.Treated, augustMonth,
                       SheetImport.Offered))
                whyNotes.Add(!locked.ImportOffered ? "" : "заключеното третиране се внася");

            // Присъдата без прозорец: затворен и празен месец.
            var closed = MonthVerdict.For(augustMonth, 3, closed: true, [], []);
            whyNotes.Add(closed.Text.StartsWith("Август 2026 е затворен", StringComparison.Ordinal)
                ? "" : "затвореният месец: " + closed.Text);

            var empty = MonthVerdict.For(new LedgerPeriod(2026, 10), 0, false, [], []);
            whyNotes.Add(empty.Tone == VerdictTone.Quiet && empty.Fix.Contains("Внеси", StringComparison.Ordinal)
                ? "" : "празният месец не казва откъде се внася: " + empty.Fix);

            // Недостигът назовава склада и сочи движението.
            var short_ = Reconcile.Negative(
            [
                new Move(1, MoveKind.Received, new DateOnly(2026, 9, 1), "170405", 2m),
                new Move(2, MoveKind.Treated, new DateOnly(2026, 9, 2), "170405", 5m)
            ], StoreKind.Received);

            whyNotes.Add(short_.Count == 1 && short_[0].MoveId == 2 && short_[0].Store == StoreKind.Received &&
                         short_[0].Text.Contains("„Получен“", StringComparison.Ordinal) && short_[0].Fix.Length > 0
                ? "" : "недостигът не казва склада и движението: " + (short_.Count > 0 ? short_[0].Text : "няма"));
        }
        catch (Exception ex)
        {
            whyError = ex.ToString();
        }
    });

    whyThread.SetApartmentState(ApartmentState.STA);
    whyThread.Start();
    whyThread.Join();

    if (whyError is not null) Console.WriteLine("     " + whyError);

    var whyProblems = whyNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in whyProblems) Console.WriteLine("     " + problem);

    Check("„Месец“ и съставянето минават без изключение", whyError is null);
    Check($"направени са всичките {whyNotes.Count} проверки", whyNotes.Count == 35, $"{whyNotes.Count}");
    Check("„Месец“ казва защо отчетът не излиза и какво да се направи", whyProblems.Count == 0,
        $"{whyProblems.Count} проблема" + (whyProblems.Count > 0 ? ": " + string.Join(" | ", whyProblems.Take(5)) : ""));
}


// =============================================== 1.3.1: базата до програмата
//
// Работата стои в „Data“ до програмата, а при инсталирана — над „current“, която
// Velopack сменя при всяко обновяване. Старата база от профила се пренася веднъж,
// преименува се и до нея остава бележка; при деинсталиране работата се връща в
// профила. Нищо тук не пипа истинския профил: всички пътища са временни.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - базата стои до програмата и се пренася от профила");
{
    var placeNotes = new List<string>();
    string? placeError = null;

    try
    {
        var root = Path.Combine(work, $"place_{Guid.NewGuid():N}");

        // Инсталирана: „current“ с Update.exe отгоре — коренът е горната папка.
        var installed = Path.Combine(root, "RiosvImports");
        var current = Path.Combine(installed, "current");
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(installed, "Update.exe"), string.Empty);

        placeNotes.Add(string.Equals(DataLocation.Root(current), installed, StringComparison.OrdinalIgnoreCase)
            ? "" : "инсталираната пише в „current“: " + DataLocation.Root(current));

        placeNotes.Add(string.Equals(DataLocation.Root(current + Path.DirectorySeparatorChar), installed,
                StringComparison.OrdinalIgnoreCase)
            ? "" : "наклонената черта накрая обърква корена");

        // „current“ без Update.exe е просто папка с такова име.
        var loose = Path.Combine(root, "loose", "current");
        Directory.CreateDirectory(loose);

        placeNotes.Add(string.Equals(DataLocation.Root(loose), loose, StringComparison.OrdinalIgnoreCase)
            ? "" : "папка „current“ без Update.exe се брои за инсталирана");

        var folder = Path.Combine(root, "folder");
        Directory.CreateDirectory(folder);

        placeNotes.Add(string.Equals(DataLocation.Resolve(folder, Path.Combine(root, "profile")),
                Path.Combine(folder, DataLocation.FolderName), StringComparison.OrdinalIgnoreCase)
            ? "" : "до програмата не се пише в „Data“");

        // Папка, в която не може да се пише: на мястото на „Data“ стои файл.
        var blocked = Path.Combine(root, "blocked");
        Directory.CreateDirectory(blocked);
        File.WriteAllText(Path.Combine(blocked, DataLocation.FolderName), string.Empty);

        placeNotes.Add(!DataLocation.Writable(Path.Combine(blocked, DataLocation.FolderName))
            ? "" : "невъзможна папка се брои за пригодна");

        placeNotes.Add(string.Equals(DataLocation.Resolve(blocked, Path.Combine(root, "profile")),
                Path.Combine(root, "profile"), StringComparison.OrdinalIgnoreCase)
            ? "" : "неписаемата папка не връща към профила");

        // Пренасяне: базата в профила отива до програмата — с последните записи от WAL.
        var profile = Path.Combine(root, "profile");
        Directory.CreateDirectory(profile);

        var old = Path.Combine(profile, DataLocation.FileName);
        var target = Path.Combine(installed, DataLocation.FolderName, DataLocation.FileName);

        using (var db = Workspace.Open(old))
        {
            db.Companies.Add("203344556", "Метал ООД");
            db.Writer.Write(db.Companies.List()[0].Id, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 1), "170405", 4m, Eik: "204455667", Partner: "Скрап ЕООД"));
        }

        SqliteConnection.ClearAllPools();

        var moved = DataLocation.Adopt(old, target);

        placeNotes.Add(moved == AdoptOutcome.Moved ? "" : "базата от профила не се пренесе: " + moved);
        placeNotes.Add(File.Exists(target) ? "" : "до програмата няма база");
        placeNotes.Add(!File.Exists(old) && File.Exists(DataLocation.Retired(old))
            ? "" : "старото копие не е преименувано");
        placeNotes.Add(File.Exists(Path.Combine(profile, "ПРЕМЕСТЕНА.txt")) ? "" : "в профила няма бележка къде е работата");
        placeNotes.Add(!File.Exists(target + ".partial") ? "" : "остана наполовина копирана база");

        using (var db = Workspace.Open(target))
            placeNotes.Add(db.Companies.List().Count == 1 && db.Ledger.Read(db.Companies.List()[0].Id).Count == 1
                ? "" : "пренесената база е без работата");

        SqliteConnection.ClearAllPools();

        // Второ пускане: тук вече има база — нищо не се пипа.
        placeNotes.Add(DataLocation.Adopt(old, target) == AdoptOutcome.Nothing ? "" : "пренася се втори път");

        // Без база в профила — нищо.
        placeNotes.Add(DataLocation.Adopt(Path.Combine(profile, "none.db"), Path.Combine(folder, "Data", "x.db"))
                       == AdoptOutcome.Nothing
            ? "" : "пренася се несъществуваща база");

        // Деинсталиране: работата се връща в профила и нищо не се презаписва.
        var back = DataLocation.Rescue(Path.GetDirectoryName(target)!, profile);

        placeNotes.Add(back is not null && string.Equals(back, Path.Combine(profile, DataLocation.FileName),
                StringComparison.OrdinalIgnoreCase) && File.Exists(back)
            ? "" : "деинсталирането не връща работата в профила: " + back);

        var again = DataLocation.Rescue(Path.GetDirectoryName(target)!, profile);

        placeNotes.Add(again is not null && !string.Equals(again, back, StringComparison.OrdinalIgnoreCase) && File.Exists(again)
            ? "" : "второто връщане презаписва първото");

        placeNotes.Add(DataLocation.Rescue(profile, profile) is null ? "" : "профилът се копира сам в себе си");

        // И връщането от профила при ново инсталиране: същото пренасяне.
        SqliteConnection.ClearAllPools();
        File.Delete(target);

        placeNotes.Add(DataLocation.Adopt(back!, target) == AdoptOutcome.Moved ? "" : "върнатата работа не се взема обратно");

        // Последните записи от WAL пътуват с копието, дори базата да е отворена.
        var live = Path.Combine(root, "live.db");
        var copy = Path.Combine(root, "live-copy.db");

        using (var db = Workspace.Open(live))
        {
            db.Companies.Add("175000111", "Жива ООД");

            Workspace.Copy(live, copy);
        }

        SqliteConnection.ClearAllPools();

        using (var db = Workspace.Open(copy))
            placeNotes.Add(db.Companies.List().Any(c => c.Name == "Жива ООД") ? "" : "копието изпусна последния запис");

        SqliteConnection.ClearAllPools();
    }
    catch (Exception ex)
    {
        placeError = ex.ToString();
    }

    if (placeError is not null) Console.WriteLine("     " + placeError);

    var placeProblems = placeNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in placeProblems) Console.WriteLine("     " + problem);

    Check("мястото на базата се решава без изключение", placeError is null);
    Check($"направени са всичките {placeNotes.Count} проверки", placeNotes.Count == 19, $"{placeNotes.Count}");
    Check("базата стои до програмата, пренася се веднъж и не се губи при деинсталиране", placeProblems.Count == 0,
        $"{placeProblems.Count} проблема" + (placeProblems.Count > 0 ? ": " + string.Join(" | ", placeProblems.Take(5)) : ""));
}


// =============================================== 1.3.1: изчистване за тестване
//
// Тестващият внася един и същ дневник отново и отново. Копчето в „Настройки“
// маха склада и внесеното за всички фирми — след резервно копие — и оставя
// фирмите, отчетите, списъците и настройките.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - „За тестване“ изчиства склада и внесеното, а останалото остава");
{
    var clearNotes = new List<string>();
    string? clearError = null;

    var clearThread = new Thread(() =>
    {
        try
        {
            var clearPath = Path.Combine(work, $"clear_{Guid.NewGuid():N}.db");
            using var db = Workspace.Open(clearPath);

            var first = db.Companies.Add("203344556", "Метал ООД");
            var second = db.Companies.Add("204455667", "Хартия АД");

            var received = db.Writer.Write(first, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 9, 1), "170405", 10m, Eik: "205566778", Partner: "Скрап ЕООД"));

            var treated = db.Writer.Write(first, new MoveRequest(
                MoveKind.Treated, new DateOnly(2026, 9, 3), "170405", 4m));

            db.Writer.Write(second, new MoveRequest(
                MoveKind.Received, new DateOnly(2026, 8, 1), "150101", 2m, Eik: "205566778", Partner: "Скрап ЕООД"));

            var import = db.Imports.Begin(first, ImportSource.Demetra, "м.09.2026.xlsx");
            db.Imports.Track(import, ImportTarget.Move, [received]);

            db.Actions.Add(first, "1", MoveKind.Treated, treated, new DateOnly(2026, 9, 3), "170405", 4m, action: "проба");
            db.Periods.Close(first, "1", new LedgerPeriod(2026, 8));
            db.Setup.SetStart(first, new LedgerPeriod(2026, 8));

            var report = db.Documents.Create("book", BookNodes.Report, "Проба", origin: DocumentOrigin.Ledger, companyId: first);
            db.Partners.Remember(first, "Леярна Юг АД", "175000111");
            db.Settings.Set(SettingsStore.Keys.UiTheme, "dark");

            var before = db.CountStock();

            clearNotes.Add(before.Moves == 3 && before.Batches == 2 && before.Imports == 1 && before.Logged == 1 &&
                           before.Closed == 1 && before.Starts == 1 && !before.IsEmpty
                ? "" : $"броят преди е грешен: {before}");

            string backup;

            using (var settings = new SettingsPage(db))
                backup = settings.ClearWithBackup();

            clearNotes.Add(File.Exists(backup) ? "" : "резервното копие го няма");
            clearNotes.Add(db.CountStock().IsEmpty ? "" : "остана склад: " + db.CountStock());
            clearNotes.Add(db.Ledger.Read(first).Count == 0 && db.Ledger.Read(second).Count == 0
                ? "" : "движенията на някоя фирма останаха");
            clearNotes.Add(db.Imports.List().Count == 0 ? "" : "внесените файлове останаха");
            clearNotes.Add(db.Setup.Start(first) is null ? "" : "началото на воденето остана");
            clearNotes.Add(!db.Periods.IsClosed(first, "1", new LedgerPeriod(2026, 8)) ? "" : "затвореният месец остана");

            // Остават фирмите, отчетите, списъците и настройките.
            clearNotes.Add(db.Companies.List().Count == 2 ? "" : "фирмите се изтриха");
            clearNotes.Add(db.Documents.Find(report) is not null ? "" : "отчетът се изтри");
            clearNotes.Add(db.Partners.List(first).Any(p => p.Name == "Леярна Юг АД") ? "" : "получателите се изтриха");
            clearNotes.Add(db.Settings.Get(SettingsStore.Keys.UiTheme) == "dark" ? "" : "настройките се изтриха");

            // Копието носи изтритото — от него работата се връща.
            using (var copy = Workspace.Open(backup))
                clearNotes.Add(copy.Ledger.Read(first).Count == 2 && copy.Imports.List().Count == 1
                    ? "" : "резервното копие е без склада");

            SqliteConnection.ClearAllPools();

            // Склад, отворен към края на месеца — с едно пресмятане и без филтри.
            using (var stock = new StockPage(service, db))
            {
                stock.UseSearch("нищо такова");
                stock.Open(StoreKind.Treated, new DateOnly(2026, 9, 30));

                clearNotes.Add(stock.CurrentDate == new DateOnly(2026, 9, 30) && stock.ImportKind == MoveKind.Treated
                    ? "" : "складът не се отваря на „Третиран“ към датата");
            }
        }
        catch (Exception ex)
        {
            clearError = ex.ToString();
        }
    });

    clearThread.SetApartmentState(ApartmentState.STA);
    clearThread.Start();
    clearThread.Join();

    if (clearError is not null) Console.WriteLine("     " + clearError);

    var clearProblems = clearNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in clearProblems) Console.WriteLine("     " + problem);

    Check("изчистването минава без изключение", clearError is null);
    Check($"направени са всичките {clearNotes.Count} проверки", clearNotes.Count == 13, $"{clearNotes.Count}");
    Check("„За тестване“ маха склада и внесеното, а фирмите, отчетите и настройките остават",
        clearProblems.Count == 0,
        $"{clearProblems.Count} проблема" + (clearProblems.Count > 0 ? ": " + string.Join(" | ", clearProblems.Take(5)) : ""));
}

// =============================================== 1.3.2: инсталаторът пита къде
//
// Velopack смята цялата папка на програмата за своя: при повторно инсталиране я
// мести встрани и я трие, при махане я изпразва. Затова програмата влиза само в
// празна папка — в непразна получава подпапка „RIOSV Imports“ — и никога в
// Windows, Program Files, мрежова папка или направо в корена на диска. Нищо тук
// не инсталира: само се решава къде.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - инсталаторът пита къде и не слага програмата сред чужди файлове");
{
    var planNotes = new List<string>();
    string? planError = null;

    try
    {
        var root = Path.Combine(work, $"plan_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var guarded = new[] { Path.Combine(root, "Windows"), Path.Combine(root, "Program Files") };

        InstallPlan Plan(string? typed, ExistingInstall? existing = null) => InstallPlan.For(typed, existing, guarded);

        static string N(string path) => InstallPlan.Normalize(path);

        planNotes.Add(Plan("").Kind == PlanKind.Invalid ? "" : "празното поле се приема");
        planNotes.Add(Plan(@"\\server\share\RIOSV").Kind == PlanKind.Invalid ? "" : "мрежова папка се приема");
        planNotes.Add(Plan("RIOSV Imports").Kind == PlanKind.Invalid ? "" : "път без диск се приема");
        planNotes.Add(Plan(Path.Combine(root, "a|b")).Kind == PlanKind.Invalid ? "" : "забранен знак в името се приема");
        planNotes.Add(Plan(Path.Combine(root, new string('x', 160))).Kind == PlanKind.Invalid ? "" : "твърде дълъг път се приема");

        // Нова папка — направо; с „\“ накрая — същата.
        var fresh = Path.Combine(root, "fresh");
        var plan = Plan(fresh + "\\");
        planNotes.Add(plan.Kind == PlanKind.Fresh && plan.Folder == N(fresh) && !plan.Extended
            ? "" : $"нова папка: {plan.Kind} {plan.Folder}");

        // Празна — също направо; „.dead“ от махната програма не я прави чужда.
        var empty = Path.Combine(root, "empty");
        Directory.CreateDirectory(empty);
        File.WriteAllText(Path.Combine(empty, ".dead"), "");
        plan = Plan(empty);
        planNotes.Add(plan.Kind == PlanKind.Fresh && plan.Folder == N(empty) && !plan.Extended
            ? "" : $"празна папка с „.dead“: {plan.Kind} {plan.Folder}");

        // Току-що махната: Velopack трие Update.exe чак след няколко секунди —
        // папката с „.dead“ е празна, а с „Data“ в нея — останала работа.
        var removed = Path.Combine(root, "removed");
        Directory.CreateDirectory(removed);
        File.WriteAllText(Path.Combine(removed, ".dead"), "");
        File.WriteAllText(Path.Combine(removed, "Update.exe"), "");
        planNotes.Add(InstallPlan.Look(removed) == FolderState.Empty ? "" : "махнатата програма се брои за инсталирана");
        Directory.CreateDirectory(Path.Combine(removed, "Data"));
        planNotes.Add(InstallPlan.Look(removed) == FolderState.Leftover ? "" : "работата в махната папка не се вижда");

        // Непразна чужда — подпапка „RIOSV Imports“, никога направо.
        var busy = Path.Combine(root, "Documents");
        Directory.CreateDirectory(busy);
        File.WriteAllText(Path.Combine(busy, "отчет.docx"), "x");
        plan = Plan(busy);
        planNotes.Add(plan.Kind == PlanKind.Fresh && plan.Extended &&
                      plan.Folder == Path.Combine(N(busy), InstallPlan.SubFolder)
            ? "" : $"непразна папка: {plan.Kind} {plan.Folder}");

        // ... а ако и подпапката е чужда — не става; втора подпапка няма.
        Directory.CreateDirectory(Path.Combine(busy, InstallPlan.SubFolder));
        File.WriteAllText(Path.Combine(busy, InstallPlan.SubFolder, "чуждо.txt"), "x");
        planNotes.Add(Plan(busy).Kind == PlanKind.Invalid ? "" : "чуждата подпапка „RIOSV Imports“ се приема");
        planNotes.Add(Plan(Path.Combine(busy, InstallPlan.SubFolder)).Kind == PlanKind.Invalid
            ? "" : "непразна „RIOSV Imports“ получава втора подпапка");

        // Коренът на диска — винаги подпапка. Само се гледа, нищо не се пише.
        var system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;
        plan = Plan(system);
        planNotes.Add(plan.Extended && plan.Folder == Path.Combine(system, InstallPlan.SubFolder)
            ? "" : $"коренът на диска: {plan.Kind} {plan.Folder}");

        // Windows и Program Files — не.
        planNotes.Add(Plan(Path.Combine(guarded[1], "RIOSV Imports")).Kind == PlanKind.Invalid ? "" : "Program Files се приема");
        planNotes.Add(Plan(guarded[0]).Kind == PlanKind.Invalid ? "" : "папката на Windows се приема");

        // Вече инсталирана: същата папка — обновяване; вътре в нея — не; друга — преместване.
        var installed = Path.Combine(root, "installed");
        Directory.CreateDirectory(Path.Combine(installed, "current"));
        File.WriteAllText(Path.Combine(installed, "Update.exe"), "");
        File.WriteAllText(Path.Combine(installed, "current", "RIOSV Imports.exe"), "");
        var existing = new ExistingInstall(installed, "1.3.1");

        plan = Plan(installed, existing);
        planNotes.Add(plan.Kind == PlanKind.Update && plan.Existing == existing ? "" : $"същата папка: {plan.Kind}");
        planNotes.Add(Plan(Path.Combine(installed, "Data"), existing).Kind == PlanKind.Invalid ? "" : "папка вътре в програмата се приема");

        plan = Plan(fresh, existing);
        planNotes.Add(plan.Kind == PlanKind.Move && plan.Existing == existing ? "" : $"друга папка при инсталирана: {plan.Kind}");

        // Непразният родител на инсталираната води до нея — обновяване, не втора програма.
        var parent = Path.Combine(root, "parent");
        var inside = Path.Combine(parent, InstallPlan.SubFolder);
        Directory.CreateDirectory(Path.Combine(inside, "current"));
        File.WriteAllText(Path.Combine(inside, "Update.exe"), "");
        plan = Plan(parent, new ExistingInstall(inside, "1.3.1"));
        planNotes.Add(plan.Kind == PlanKind.Update ? "" : $"родителят на инсталираната: {plan.Kind} {plan.Folder}");

        // Копие, което Windows не помни — слага се наново; до друго инсталирано — не.
        plan = Plan(installed);
        planNotes.Add(plan.Kind == PlanKind.Update && plan.Existing is null ? "" : $"непомнено копие: {plan.Kind}");
        planNotes.Add(Plan(installed, new ExistingInstall(fresh, "")).Kind == PlanKind.Invalid
            ? "" : "второ копие до инсталираното се приема");

        // Преносимото копие се обновява само — инсталатор там не влиза.
        var portable = Path.Combine(root, "portable");
        Directory.CreateDirectory(portable);
        File.WriteAllText(Path.Combine(portable, "Update.exe"), "");
        File.WriteAllText(Path.Combine(portable, ".portable"), "");
        planNotes.Add(Plan(portable).Kind == PlanKind.Invalid ? "" : "преносимото копие се приема");

        // Останала работа без програма: новата продължава с нея — но там не се мести друга.
        var leftover = Path.Combine(root, "leftover");
        Directory.CreateDirectory(Path.Combine(leftover, "Data"));
        File.WriteAllText(Path.Combine(leftover, "Data", "workspace.db"), "");
        plan = Plan(leftover);
        planNotes.Add(plan.Kind == PlanKind.Fresh && plan.KeepsWork && !plan.Extended ? "" : $"останала работа: {plan.Kind}");
        planNotes.Add(Plan(leftover, existing).Kind == PlanKind.Invalid ? "" : "преместване върху чужда работа се приема");

        // Записът в „Приложения“ е инсталация само ако програмата или работата са в
        // папката. Velopack, спрян по средата (17.11.2026: антивирусната програма не
        // пусна новия exe), оставя Update.exe и „packages“ — там няма какво да се
        // обновява и инсталаторът пита наново къде.
        planNotes.Add(InstallPlan.Holds(installed) ? "" : "истинската инсталация не се признава");
        var halfway = Path.Combine(root, "halfway");
        Directory.CreateDirectory(Path.Combine(halfway, "packages"));
        File.WriteAllText(Path.Combine(halfway, "Update.exe"), "");
        planNotes.Add(!InstallPlan.Holds(halfway) ? "" : "спряната по средата инсталация се брои за програма");
        Directory.CreateDirectory(Path.Combine(halfway, "Data"));
        File.WriteAllText(Path.Combine(halfway, "Data", "workspace.db"), "");
        planNotes.Add(InstallPlan.Holds(halfway) ? "" : "работата в спряната по средата инсталация не се пази");
        planNotes.Add(!InstallPlan.Holds(portable) ? "" : "преносимото копие се брои за инсталация");
        planNotes.Add(!InstallPlan.Holds(Path.Combine(root, "nowhere")) ? "" : "липсваща папка се брои за инсталация");

        // Променливи като %TEMP% се разгръщат.
        Environment.SetEnvironmentVariable("RIOSV_PLAN_TEST", root);
        plan = Plan(@"%RIOSV_PLAN_TEST%\env");
        Environment.SetEnvironmentVariable("RIOSV_PLAN_TEST", null);
        planNotes.Add(plan.Folder == Path.Combine(N(root), "env") ? "" : "променливата в пътя не се разгъва: " + plan.Folder);

        // Пътят на командния ред: с интервал — в кавички; „\“ накрая не глъта кавичката.
        planNotes.Add(CommandLine.Quote(@"C:\RIOSV Imports") == "\"C:\\RIOSV Imports\"" ? "" : "кавичките около пътя");
        planNotes.Add(CommandLine.Quote(@"C:\A\") == "\"C:\\A\\\\\"" ? "" : "„\\“ накрая глъта кавичката");
    }
    catch (Exception ex)
    {
        planError = ex.ToString();
    }

    if (planError is not null) Console.WriteLine("     " + planError);

    var planProblems = planNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in planProblems) Console.WriteLine("     " + problem);

    Check("решението къде да се инсталира минава без изключение", planError is null);
    Check($"направени са всичките {planNotes.Count} проверки", planNotes.Count == 32, $"{planNotes.Count}");
    Check("програмата влиза само в своя папка, а инсталираната се обновява или мести", planProblems.Count == 0,
        $"{planProblems.Count} проблема" + (planProblems.Count > 0 ? ": " + string.Join(" | ", planProblems.Take(5)) : ""));
}


// =============================================== 1.3.2: работата не се губи при инсталиране
//
// Пуснат там, където програмата вече е, инсталаторът на Velopack мести старата
// папка встрани и я трие след новата — с „Data“ вътре. В 1.3.1 така се губеше
// работата. Сега новата програма я взема от преместената папка (Recover), а
// инсталаторът на RIOSV Imports пази и свое копие в профила и го връща, ако я
// няма. При преместване копията, които старата програма оставя в профила при
// махането си, се преименуват — иначе някой ден биха се взели за работата.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - повторното инсталиране и преместването не губят работата");
{
    var keepNotes = new List<string>();
    string? keepError = null;

    try
    {
        var root = Path.Combine(work, $"keep_{Guid.NewGuid():N}");
        var profile = Path.Combine(root, "profile");
        var apps = Path.Combine(root, "apps");

        // Старата инсталация: работа, резервно копие и дневник в „Data“.
        var old = Path.Combine(apps, "RiosvImports");
        var oldData = Path.Combine(old, "Data");
        Directory.CreateDirectory(Path.Combine(oldData, "Backups"));

        using (var db = Workspace.Open(Path.Combine(oldData, "workspace.db")))
            db.Companies.Add("203344556", "Стара ООД");

        SqliteConnection.ClearAllPools();
        File.WriteAllText(Path.Combine(oldData, "Backups", "before-clear-2026-11-15-120000.db"), "копие");
        File.WriteAllText(Path.Combine(oldData, "error.log"), "дневник");

        keepNotes.Add(InstallData.HasWork(old) ? "" : "работата в „Data“ не се вижда");
        keepNotes.Add(InstallData.Backup(Path.Combine(root, "nothing"), profile, DateTime.Now) is null
            ? "" : "копие на несъществуваща работа");

        // Копие в профила — цялата „Data“; второ в същата секунда не презаписва първото.
        var at = new DateTime(2026, 11, 16, 10, 15, 0);
        var backup = InstallData.Backup(old, profile, at);
        keepNotes.Add(backup == Path.Combine(profile, "Backups", "before-install-2026-11-16-101500")
            ? "" : "копието е другаде: " + backup);
        keepNotes.Add(backup is not null && File.Exists(Path.Combine(backup, "workspace.db")) &&
                      File.Exists(Path.Combine(backup, "Backups", "before-clear-2026-11-15-120000.db")) &&
                      File.Exists(Path.Combine(backup, "error.log"))
            ? "" : "копието не е на цялата „Data“");
        var second = InstallData.Backup(old, profile, at);
        keepNotes.Add(second is not null && second != backup && second.EndsWith("-2", StringComparison.Ordinal)
            ? "" : "второто копие презаписва първото: " + second);

        // Връщане в нова папка: базата се отваря с работата, без остатък „.partial“.
        var fresh = Path.Combine(root, "C", "RIOSV Imports");
        Directory.CreateDirectory(fresh);
        keepNotes.Add(InstallData.Restore(backup!, fresh) ? "" : "работата не се върна");
        keepNotes.Add(!File.Exists(Path.Combine(fresh, "Data", "workspace.db.partial")) ? "" : "остана „.partial“");
        keepNotes.Add(File.Exists(Path.Combine(fresh, "Data", "Backups", "before-clear-2026-11-15-120000.db"))
            ? "" : "резервните копия не се върнаха");

        using (var db = Workspace.Open(Path.Combine(fresh, "Data", "workspace.db")))
            keepNotes.Add(db.Companies.List().Any(c => c.Name == "Стара ООД") ? "" : "върнатата база е без работата");

        SqliteConnection.ClearAllPools();

        // Работата, която вече е там, е по-новата — не се презаписва.
        keepNotes.Add(!InstallData.Restore(backup!, fresh) ? "" : "връщането презаписа по-новата работа");

        // След преместване: копията от махането на старата програма се преименуват — само те.
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "workspace-earlier.db"), "отпреди");
        var before = InstallData.Rescues(profile);
        File.WriteAllText(Path.Combine(profile, "workspace.db"), "от махането");
        File.WriteAllText(Path.Combine(profile, "workspace-20261116-101600.db"), "от махането");
        var retired = InstallData.RetireNew(profile, before, fresh, at);
        keepNotes.Add(retired.Count == 2 && !File.Exists(Path.Combine(profile, "workspace.db")) &&
                      File.Exists(Path.Combine(profile, "workspace.db.moved"))
            ? "" : $"копията от махането не са преименувани: {retired.Count}");
        keepNotes.Add(File.Exists(Path.Combine(profile, "workspace-earlier.db")) ? "" : "преименувано е и отпреди");
        keepNotes.Add(File.Exists(Path.Combine(profile, "ПРЕМЕСТЕНА.txt")) ? "" : "няма бележка къде е работата");

        // Имената, под които Velopack мести старата папка встрани.
        keepNotes.Add(DataLocation.SetAside("RiosvImports", "RiosvImports_AbCdEfGh12345678") &&
                      DataLocation.SetAside("RiosvImports", "RiosvImports.AbCdEfGh12345678") &&
                      DataLocation.SetAside("RIOSV Imports", "RIOSV Imports_0123456789abcdef")
            ? "" : "преместената встрани папка не се познава");
        keepNotes.Add(!DataLocation.SetAside("RiosvImports", "RiosvImports_backup") &&
                      !DataLocation.SetAside("RiosvImports", "RiosvImportsX_AbCdEfGh12345678") &&
                      !DataLocation.SetAside("RiosvImports", "RiosvImports_AbCdEfGh1234567!") &&
                      !DataLocation.SetAside("RiosvImports", "Other_AbCdEfGh12345678")
            ? "" : "чужда папка се взема за преместената встрани");

        // Повторно инсталиране: старата папка е встрани, новата е без „Data“. Куката взема работата.
        var aside = Path.Combine(apps, "RiosvImports_AbCdEfGh12345678");
        Directory.Move(old, aside);
        Directory.CreateDirectory(Path.Combine(old, "current"));
        File.WriteAllText(Path.Combine(old, "Update.exe"), "");

        // По-старо копие встрани — не то.
        var older = Path.Combine(apps, "RiosvImports_ZyXwVuTs98765432");
        Directory.CreateDirectory(Path.Combine(older, "Data"));
        File.WriteAllText(Path.Combine(older, "Data", "workspace.db"), "старо");
        File.SetLastWriteTimeUtc(Path.Combine(older, "Data", "workspace.db"), DateTime.UtcNow.AddDays(-3));

        // Чужда папка до нея — не се пипа.
        Directory.CreateDirectory(Path.Combine(apps, "RiosvImports_backup", "Data"));
        File.WriteAllText(Path.Combine(apps, "RiosvImports_backup", "Data", "workspace.db"), "чуждо");

        var source = DataLocation.Recover(old);
        keepNotes.Add(source is not null &&
                      string.Equals(source, Path.Combine(aside, "Data"), StringComparison.OrdinalIgnoreCase)
            ? "" : "куката не взе работата от преместената папка: " + source);

        using (var db = Workspace.Open(Path.Combine(old, "Data", "workspace.db")))
            keepNotes.Add(db.Companies.List().Any(c => c.Name == "Стара ООД") ? "" : "взетата база е без работата");

        SqliteConnection.ClearAllPools();

        keepNotes.Add(File.Exists(Path.Combine(old, "Data", "Backups", "before-clear-2026-11-15-120000.db")) &&
                      File.Exists(Path.Combine(old, "Data", "error.log"))
            ? "" : "резервните копия и дневникът не дойдоха");
        keepNotes.Add(!File.Exists(Path.Combine(old, "Data", "workspace.db.partial")) ? "" : "куката остави „.partial“");

        // Втори път — тук вече има работа, нищо не се пипа.
        keepNotes.Add(DataLocation.Recover(old) is null ? "" : "куката презаписа работата");

        // Без преместена встрани папка — нищо, и „Data“ не се прави празна.
        var lone = Path.Combine(root, "lone", "RiosvImports");
        Directory.CreateDirectory(lone);
        keepNotes.Add(DataLocation.Recover(lone) is null && !Directory.Exists(Path.Combine(lone, "Data"))
            ? "" : "куката направи „Data“ без работа");

        // Профилът може да се подмени — за проверката от край до край.
        var previous = Environment.GetEnvironmentVariable(Workspace.ProfileVariable);
        var expected = previous is { Length: > 0 }
            ? Path.GetFullPath(previous)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RIOSV Imports");

        Environment.SetEnvironmentVariable(Workspace.ProfileVariable, profile);
        keepNotes.Add(Workspace.ProfileFolder() == Path.GetFullPath(profile) &&
                      DataLocation.ProfileFolder == Path.GetFullPath(profile) &&
                      Workspace.DefaultPath() == Path.Combine(Path.GetFullPath(profile), "workspace.db")
            ? "" : "профилът не се подменя");
        Environment.SetEnvironmentVariable(Workspace.ProfileVariable, previous);
        keepNotes.Add(Workspace.ProfileFolder() == expected ? "" : "без променливата профилът не е истинският");
    }
    catch (Exception ex)
    {
        keepError = ex.ToString();
    }

    if (keepError is not null) Console.WriteLine("     " + keepError);

    var keepProblems = keepNotes.Where(n => n.Length > 0).ToList();
    foreach (var problem in keepProblems) Console.WriteLine("     " + problem);

    Check("пазенето на работата при инсталиране минава без изключение", keepError is null);
    Check($"направени са всичките {keepNotes.Count} проверки", keepNotes.Count == 23, $"{keepNotes.Count}");
    Check("работата преживява повторно инсталиране и преместване, а старите копия не се връщат", keepProblems.Count == 0,
        $"{keepProblems.Count} проблема" + (keepProblems.Count > 0 ? ": " + string.Join(" | ", keepProblems.Take(5)) : ""));
}



// =============================================== Лаптопът: нищо не се реже
//
// Програмата се пише на монитор при 100 %, а се проверява на лаптоп при 125 % и
// 150 %. Шрифтовете растат с мащаба, а размерите, писани в пиксели — не: надписи
// се режеха отгоре и отдолу, редове от списъци влизаха един в друг, а при
// предаването таблицата с кодовете оставаше под ръба на прозореца. Видя се чак
// на снимки от проверяващия.
//
// Затова главните екрани и прозорчета се сглобяват при подражаван мащаб — в
// отделен процес, защото шрифтовете се правят веднъж (виж LayoutAudit) — и всяко
// изрязано нещо се казва поименно. Трите размера тръгват едновременно.
Console.WriteLine();
Console.WriteLine("### ТЕСТ - на лаптоп при 100 %, 125 % и 150 % нищо не се реже");
{
    var laptops = new (string Zoom, int Width, int Height)[]
    {
        ("1", 1366, 700),       // 1366×768 при 100 %
        ("1.25", 1920, 970),    // 1920×1080 при 125 %
        ("1.5", 1920, 960)      // 1920×1080 при 150 %
    };

    var runs = laptops.Select(laptop => Task.Run(() =>
    {
        var start = new System.Diagnostics.ProcessStartInfo(
            Environment.ProcessPath!, $"--layout {laptop.Width} {laptop.Height}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        start.Environment[Theme.ZoomVariable] = laptop.Zoom;

        using var child = System.Diagnostics.Process.Start(start)!;

        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        var finished = child.WaitForExit(300_000);

        if (!finished)
        {
            try { child.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }

        return (Laptop: laptop, Finished: finished, Code: finished ? child.ExitCode : -1,
                Text: output.Result + errors.Result);
    })).ToList();

    foreach (var run in runs)
    {
        var (laptop, finished, code, text) = run.Result;

        var found = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith('[')).ToList();

        foreach (var line in found.Take(12)) Console.WriteLine("     " + line);

        Check($"при {laptop.Zoom}× на {laptop.Width}×{laptop.Height} нищо не се реже",
            finished && code == 0 && found.Count == 0,
            finished ? $"{found.Count} находки" : "не свърши навреме");
    }
}

Console.WriteLine($"\n=========== ПРИЕТИ: {pass}   ПРОВАЛЕНИ: {fail} ===========");
return fail == 0 ? 0 : 1;

static string Base64Url(byte[] data) =>
    Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

static string[] Headers(IXLWorksheet ws)
{
    var last = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
    var h = new string[last + 1];
    Array.Fill(h, string.Empty);
    for (var c = 1; c <= last; c++) h[c] = ws.Cell(2, c).GetString().Trim();
    return h;
}

void Fill(XLWorkbook wb, string sheetName, int row, Dictionary<string, string> values)
{
    var ws = wb.Worksheet(sheetName);
    var headers = Headers(ws);
    for (var c = 1; c < headers.Length; c++)
        if (headers[c] is { Length: > 0 } h && values.TryGetValue(h, out var v))
            ws.Cell(row, c).Value = v;
}

static string SampleFor(SchemaDefinition schema, string reportName, string column)
{
    var node = schema.FindReport(reportName)!;
    var col = node.SelfAndDescendants().SelectMany(n => n.Attributes)
                  .FirstOrDefault(a => a.Name == column);
    if (col is null) return "1";

    return col.Domain.Kind switch
    {
        ValueKind.Enum => col.Domain.Options[0].Value,
        ValueKind.Date => "15.01.2024",
        ValueKind.Integer => "3",
        ValueKind.Decimal => "2,5",
        _ => "Примерен текст"
    };
}

static bool IsCyrillic(char c) => c is >= '\u0400' and <= '\u04FF';

/// <summary>Всяка контрола в дървото, заедно с корена.</summary>
static IEnumerable<Control> Everything(Control root)
{
    yield return root;

    foreach (Control child in root.Controls)
        foreach (var deep in Everything(child))
            yield return deep;
}

/// <summary>Първите думи от надпис — колкото да се познае кой е.</summary>
static string Short(string text) =>
    text.Length <= 40 ? text : text[..40] + "…";

/// <summary>
/// Обновяване без мрежа — за службата и за екраните. Напредъкът при сваляне
/// се казва три пъти, а последното обаждане се пази, за да се пусне закъсняло.
/// </summary>
sealed class FakeUpdates : IUpdateBackend
{
    public bool CanApply { get; init; } = true;

    public InstallKind Kind => CanApply ? InstallKind.Installed : InstallKind.Unpacked;

    public string CurrentVersion => "1.3.0";

    public ReleaseInfo? Pending { get; set; }

    public ReleaseInfo? Found { get; init; }

    public Exception? Fail { get; init; }

    public int Reported { get; private set; }

    public int Downloads { get; private set; }

    public bool Applied { get; private set; }

    public Action<int>? LateProgress { get; private set; }

    public Task<ReleaseInfo?> CheckAsync(CancellationToken token) =>
        Fail is not null ? Task.FromException<ReleaseInfo?>(Fail) : Task.FromResult(Found);

    public Task DownloadAsync(Action<int> progress, CancellationToken token)
    {
        Downloads++;

        foreach (var percent in new[] { 10, 60, 100 })
        {
            progress(percent);
            Reported++;
        }

        LateProgress = progress;
        Pending = Found;

        return Task.CompletedTask;
    }

    public void ApplyOnExit() => Applied = true;
}

/// <summary>Временна база, която се маха след себе си.</summary>
sealed class TempDb(string folder) : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(folder, $"taskbar_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try
        {
            if (File.Exists(Path)) File.Delete(Path);
        }
        catch (IOException)
        {
            // Базата още е заключена — временната папка се чисти и без нас.
        }
    }
}
