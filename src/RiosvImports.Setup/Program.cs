using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace RiosvImports.Setup;

/// <summary>
/// Инсталаторът на RIOSV Imports — файлът RiosvImports-win-Setup.exe от изданието.
///
/// Без аргументи: прозорецът с въпроса къде. С „--silent“: без прозорец — за
/// администратори и за проверката от край до край:
///
///   RiosvImports-win-Setup.exe --silent [--installto "D:\RIOSV Imports"] [--log файл]
///
/// Тихото инсталиране не пуска програмата накрая. Изход: 0 — готово, 1 — не
/// стана, 2 — папката не става, 3 — програмата е отворена.
///
/// За проверки: „--payload Setup.exe“ пуска друг инсталатор на Velopack вместо
/// вградения, а „--snapshot файл.png [--page choose|working|done|failed]
/// [--scale 1.5]“ снима прозореца, без да инсталира нищо.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = Options.Parse(args);

        // Два инсталатора наведнъж биха се сбили за една и съща папка.
        using var single = new Mutex(true, @"Local\RiosvImports.Setup", out var first);

        if (!first && options.Snapshot is null)
        {
            if (!options.Silent)
                MessageBox.Show("Инсталаторът на RIOSV Imports вече е отворен.", Product.Title,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);

            return 1;
        }

        if (options.Silent) return Unattended(options);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var form = new SetupForm(options.InstallTo, options.Payload, options.Scale);

        if (options.Snapshot is { } png)
        {
            form.Snapshot(png, options.Page);
            return 0;
        }

        Application.Run(form);

        return form.ExitCode;
    }

    private static int Unattended(Options options)
    {
        var existing = Registration.Find();
        var plan = InstallPlan.For(options.InstallTo ?? existing?.Root ?? Product.DefaultFolder, existing,
            InstallPlan.ProtectedFolders());

        if (!plan.Valid)
        {
            Log(options, "Папката не става: " + plan.Reason);
            return 2;
        }

        Log(options, $"{plan.Kind}: {plan.Folder}" + (plan.Existing is { } old ? $" (от {old.Root})" : string.Empty));

        var installer = new Installer(plan, Product.ProfileFolder, text => Log(options, text), options.Payload);
        var outcome = installer.Run();

        Log(options, outcome + (installer.Problem.Length > 0 ? ": " + installer.Problem : string.Empty) +
                     (installer.Backup is { } backup ? " | копие: " + backup : string.Empty));

        return outcome switch
        {
            InstallOutcome.Done => 0,
            InstallOutcome.Busy => 3,
            _ => 1
        };
    }

    private static void Log(Options options, string text)
    {
        if (options.Log is null) return;

        try
        {
            File.AppendAllText(options.Log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}");
        }
        catch
        {
            // Дневникът е за удобство.
        }
    }
}

internal sealed class Options
{
    public string? InstallTo { get; private set; }

    public bool Silent { get; private set; }

    public string? Log { get; private set; }

    public string? Payload { get; private set; }

    public string? Snapshot { get; private set; }

    public string Page { get; private set; } = "choose";

    public float? Scale { get; private set; }

    public static Options Parse(string[] args)
    {
        var options = new Options();

        for (var i = 0; i < args.Length; i++)
        {
            var next = i + 1 < args.Length ? args[i + 1] : null;

            switch (args[i].ToLowerInvariant())
            {
                case "--silent":
                case "-s":
                    options.Silent = true;
                    break;

                case "--installto":
                case "-t":
                    options.InstallTo = next;
                    i++;
                    break;

                case "--log":
                case "-l":
                    options.Log = next;
                    i++;
                    break;

                case "--payload":
                    options.Payload = next;
                    i++;
                    break;

                case "--snapshot":
                    options.Snapshot = next;
                    i++;
                    break;

                case "--page":
                    options.Page = (next ?? "choose").ToLowerInvariant();
                    i++;
                    break;

                case "--scale":
                    if (float.TryParse(next, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) && scale > 0)
                        options.Scale = scale;
                    i++;
                    break;
            }
        }

        return options;
    }
}
