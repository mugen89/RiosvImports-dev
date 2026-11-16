using System.Globalization;
using RiosvImports.Core;
using Velopack;

using RiosvImports.Core.Localization;

namespace RiosvImports.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Velopack поема инсталирането, обновяването и премахването. Трябва да
        // е първото нещо в Main: при част от стартиранията — например веднага
        // след обновяване — работата приключва точно тук и прозорец не се
        // отваря изобщо.
        //
        // Без слагане при пускане: по подразбиране Velopack слага всяко вече
        // свалено издание още при следващото отваряне, без да пита. Тук новото
        // издание се слага само с копче („Рестартирай и обнови“) — отчетите се
        // подават срещу срок и програмата не бива да се смени сама сутринта, в
        // която човек сяда да подава.
        //
        // Преди деинсталиране работата се пренася в профила: Velopack трие цялата
        // папка на програмата, а от 1.3.1 базата стои в нея („Data“). Оттам тя се
        // връща сама при следващо инсталиране (DataLocation.Adopt).
        //
        // При инсталиране върху вече инсталирана програма Velopack мести старата
        // папка встрани и я трие след новата — без да пита старата програма.
        // Работата се взема оттам, докато още стои (DataLocation.Recover).
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .OnAfterInstallFastCallback(_ => DataLocation.Recover(DataLocation.Root(AppContext.BaseDirectory)))
            .OnBeforeUninstallFastCallback(_ => DataLocation.Rescue(DataLocation.Folder, DataLocation.ProfileFolder))
            .Run();

        // Българска локализация на диалозите и форматите в интерфейса.
        var culture = new CultureInfo("bg-BG");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;

        // „--zoom 1.5“: програмата изглежда както на лаптоп при 150 % — на
        // монитора, на който се пише (виж Theme.Emulated). Подава се ПРЕДИ
        // темата да бъде пипната: шрифтовете се правят веднъж, при първото
        // пипане, и после не се сменят.
        if (Argument(args, "--zoom") is { } zoom) Environment.SetEnvironmentVariable(Theme.ZoomVariable, zoom);

        ApplicationConfiguration.Initialize();
        Application.SetCompatibleTextRenderingDefault(false);

        // Шрифтът по подразбиране расте с мащаба като всички останали — иначе
        // при подражаване полетата, които не носят шрифт от темата, остават
        // дребни и картината лъже в полза на лаптопа.
        if (Theme.Emulated > 1f)
            Application.SetDefaultFont(new Font(SystemFonts.MessageBoxFont!.FontFamily, 9f * Theme.Emulated));

        Application.ThreadException += (_, e) => ShowFatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ShowFatal(e.ExceptionObject as Exception);

        ImportService service;
        try
        {
            service = new ImportService(ImportService.LocateSchemaFolder());
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Lang.T("Схемите не могат да бъдат заредени.\r\n\r\n",
                       "The schemas could not be loaded.\r\n\r\n") + ex.Message + "\r\n\r\n" +
                Lang.T("Уверете се, че папка „schemas“ се намира до програмата и съдържа " +
                       "year.xsd, quarter.xsd, book.xsd, common-types.xsd и ewc.xsd.",
                       "Make sure the “schemas” folder sits next to the program and contains " +
                       "year.xsd, quarter.xsd, book.xsd, common-types.xsd and ewc.xsd."),
                Lang.T("Грешка при стартиране", "Startup error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // Компилирането на схемите за проверка е бавно — правим го предварително.
        Task.Run(() =>
        {
            try { service.Warmup(); }
            catch { /* грешката ще излезе при първия импорт */ }
        });

        // Друга база вместо тази в профила: „--db <път>“. Ползва се при
        // преглед, за да не се пипа работата на човека, който гледа.
        var db = Argument(args, "--db");
        if (db is not null) AppServices.UseWorkspace(db);

        // Позволява файлът да се отвори направо с програмата (Отвори с…).
        // Пътят до базата се изключва: той също съществува, а не е отчет.
        var startupFile = args.FirstOrDefault(a => a != db && File.Exists(a));

        Shell.ShellForm? form = null;

        try
        {
            // Темата се прилага преди първия прозорец: иначе екранът мигва от
            // светло към тъмно пред очите на потребителя при всяко стартиране.
            var workspace = AppServices.Workspace;

            // Езикът се прилага преди темата и преди първия прозорец: надписите
            // се четат при сглобяване и после не се препитват.
            Core.Localization.Lang.Use(workspace.Settings.Get(Data.SettingsStore.Keys.UiLanguage));
            Theme.Use(workspace.Settings.Get(Data.SettingsStore.Keys.UiTheme));

            form = new Shell.ShellForm(service, workspace, startupFile);

            // Проверката тръгва чак след като прозорецът се покаже: така
            // стартирането не се бави, а отговорът идва в известията на
            // началния екран — не в прозорец, който спира работата.
            //
            // Оттук, а не от самия прозорец: тестовете сглобяват и показват
            // главния прозорец стотици пъти и не бива да питат GitHub нито веднъж.
            var shell = form;
            shell.Shown += (_, _) => shell.OnLaunched();

            Application.Run(form);
        }
        catch (Exception ex)
        {
            ShowFatal(ex);
        }
        finally
        {
            AppServices.Close();

            // Чак след като базата е затворена: инсталаторът чака програмата да
            // излезе и тогава сменя файловете. Отвореното в базата не бива да
            // остане наполовина записано, докато под него сменят програмата.
            if (form is { RestartForUpdate: true }) form.Updates.ApplyOnExit();
        }
    }

    /// <summary>Стойността след даден ключ от командния ред, или нищо.</summary>
    private static string? Argument(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);

        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private static void ShowFatal(Exception? ex)
    {
        var path = WriteCrashLog(ex);

        MessageBox.Show(
            Lang.T("Възникна неочаквана грешка:\r\n\r\n", "An unexpected error occurred:\r\n\r\n") +
            (ex?.Message ?? Lang.T(Lang.T("неизвестна грешка", "unknown error"), "unknown error")) +
            (path is null
                ? string.Empty
                : Lang.T("\r\n\r\nПодробностите са записани в:\r\n",
                         "\r\n\r\nThe details are written to:\r\n") + path),
            Lang.T("Грешка", "Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    /// <summary>Променливата, с която тестовете пращат дневника на грешките в своя папка.</summary>
    internal const string CrashLogVariable = "RIOSV_ERROR_LOG";

    /// <summary>
    /// Дневникът на грешките — до базата, в папка „Data“ до програмата.
    ///
    /// Тестовете го пренасочват (<see cref="CrashLogVariable"/>): проверката, че
    /// неуспялото обновяване се записва, иначе пишеше в истинския дневник на
    /// човека, който пуска тестовете — пет измислени грешки за един ден.
    /// </summary>
    internal static string CrashLogPath =>
        Environment.GetEnvironmentVariable(CrashLogVariable) is { Length: > 0 } custom
            ? custom
            : Path.Combine(DataLocation.Folder, "error.log");

    /// <summary>Записва подробностите за грешката, за да може да се разчете после.</summary>
    internal static string? WriteCrashLog(Exception? ex)
    {
        try
        {
            var path = CrashLogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            File.AppendAllText(path,
                $"=== {DateTime.Now:dd.MM.yyyy HH:mm:ss} ==={Environment.NewLine}" +
                (ex?.ToString() ?? Lang.T("неизвестна грешка", "unknown error")) + Environment.NewLine + Environment.NewLine);

            return path;
        }
        catch
        {
            return null;
        }
    }
}
