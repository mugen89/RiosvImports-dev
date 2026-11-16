using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace RiosvImports.Setup;

internal enum InstallOutcome
{
    Done,

    /// <summary>Програмата е отворена — трябва да се затвори. Нищо не е пипнато.</summary>
    Busy,

    Failed
}

/// <summary>
/// Самото инсталиране: инсталаторът на Velopack, пуснат тихо в избраната папка.
///
/// Velopack не пита къде (има само „--installto“ на командния ред), не пази
/// работата при повторно инсталиране и не мести програма. Тук той прави само
/// своето — файловете, връзките, записа в „Приложения“, — а всичко около него е
/// наше: преди да се пипне нещо, работата се копира в профила; след това се
/// проверява дали е на мястото си и ако я няма — се връща.
/// </summary>
internal sealed class Installer
{
    /// <summary>Името, под което пакетът на Velopack стои вътре в този файл (виж .csproj).</summary>
    public const string PayloadName = "VelopackSetup.exe";

    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(10);

    private readonly InstallPlan _plan;
    private readonly string _profile;
    private readonly Action<string> _say;
    private readonly string? _payload;

    /// <param name="payload">Друг Setup.exe на Velopack вместо вградения — за проверки от край до край.</param>
    public Installer(InstallPlan plan, string profileFolder, Action<string> say, string? payload = null)
    {
        _plan = plan;
        _profile = profileFolder;
        _say = say;
        _payload = payload;

        Work = Path.Combine(Path.GetTempPath(), "RIOSV Imports Setup", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        LogPath = Path.Combine(Work, "setup.log");
    }

    /// <summary>Временната папка: разопакованият инсталатор и дневникът му.</summary>
    public string Work { get; }

    /// <summary>Дневникът на Velopack — остава при неуспех, за да се види защо.</summary>
    public string LogPath { get; }

    /// <summary>Копието на работата отпреди инсталирането, ако е имало работа.</summary>
    public string? Backup { get; private set; }

    /// <summary>Какво не стана — с думи, за човека.</summary>
    public string Problem { get; private set; } = string.Empty;

    public InstallOutcome Run()
    {
        var folder = _plan.Folder;
        var old = _plan.Kind == PlanKind.Move ? _plan.Existing?.Root : null;

        if (Running(folder, old))
        {
            Problem = "RIOSV Imports е отворена. Затворете я и опитайте пак.";
            return InstallOutcome.Busy;
        }

        var created = false;
        var success = false;

        try
        {
            Directory.CreateDirectory(Work);

            _say("Подготвя се…");
            var setup = _payload ?? Unpack();

            // Работата се копира, преди да е пипнато каквото и да е.
            var source = old ?? folder;

            if (InstallData.HasWork(source))
            {
                _say("Пази се копие на работата…");
                Backup = InstallData.Backup(source, _profile, DateTime.Now);
            }

            // В новата папка трябва да може да се пише — проверява се, преди старата да е махната.
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
                created = true;
            }

            if (!Writable(folder))
            {
                Problem = $"В „{folder}“ не може да се пише. Изберете друга папка.";
                return InstallOutcome.Failed;
            }

            HashSet<string>? before = null;

            if (old is not null)
            {
                before = InstallData.Rescues(_profile);

                _say("Маха се старата инсталация…");
                var code = RunAndWait(Path.Combine(old, "Update.exe"), "--uninstall --silent");

                if (InstallPlan.Look(old) == FolderState.Ours)
                {
                    Problem = $"Старата инсталация в „{old}“ не се махна (код {code}). Нищо не е преместено.";
                    return InstallOutcome.Failed;
                }
            }

            _say("Инсталира се…");
            var exit = RunAndWait(setup,
                "--silent --installto " + CommandLine.Quote(folder) + " --log " + CommandLine.Quote(LogPath));

            if (exit != 0 || !Installed(folder))
            {
                Problem = exit == 0
                    ? "Инсталаторът на Velopack приключи, но програмата не е в папката."
                    : $"Инсталаторът на Velopack спря с код {exit}.";

                return InstallOutcome.Failed;
            }

            // Работата трябва да е до програмата. Ако новата програма не я е взела
            // сама — от копието.
            if (Backup is not null && !InstallData.HasWork(folder))
            {
                _say("Връща се работата…");
                InstallData.Restore(Backup, folder);
            }

            if (before is not null) InstallData.RetireNew(_profile, before, folder, DateTime.Now);
            if (old is not null) Tidy(old);

            success = true;
            return InstallOutcome.Done;
        }
        catch (Exception e)
        {
            Problem = e.Message;
            return InstallOutcome.Failed;
        }
        finally
        {
            CleanUp(success, created ? folder : null);
        }
    }

    /// <summary>Програмата е на мястото си: Update.exe и главният файл в „current“.</summary>
    public static bool Installed(string folder) =>
        File.Exists(Path.Combine(folder, "Update.exe")) &&
        File.Exists(Path.Combine(folder, "current", Product.MainExe));

    /// <summary>Отворена ли е програмата от някоя от папките — по пътя на файла, не по името.</summary>
    public static bool Running(params string?[] folders)
    {
        var name = Path.GetFileNameWithoutExtension(Product.MainExe);

        foreach (var process in Process.GetProcessesByName(name))
        {
            try
            {
                var path = process.MainModule?.FileName;

                if (path is not null && folders.Any(f => f is not null && InstallPlan.Inside(path, f))) return true;
            }
            catch
            {
                // Процес на друг потребител или вече затворен — не е нашият.
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    /// <summary>
    /// След преместване старата папка не остава с „.dead“ вътре. Velopack я
    /// изпразва, но не я маха, а Update.exe трие с няколко секунди закъснение.
    /// Маха се само ако в нея няма нищо друго — чуждо там не се пипа.
    /// </summary>
    private static void Tidy(string old)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (!Directory.Exists(old)) return;

                // Своето на Velopack: белегът, закъснелият Update.exe и дневникът
                // от махането (velopack.log).
                var strangers = Directory.EnumerateFileSystemEntries(old)
                    .Select(entry => Path.GetFileName(entry) ?? string.Empty)
                    .Any(name => !string.Equals(name, ".dead", StringComparison.OrdinalIgnoreCase) &&
                                 !string.Equals(name, "Update.exe", StringComparison.OrdinalIgnoreCase) &&
                                 !(name.StartsWith("velopack", StringComparison.OrdinalIgnoreCase) &&
                                   name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)));

                if (strangers) return;

                Directory.Delete(old, true);
                return;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                System.Threading.Thread.Sleep(500);
            }
        }
    }

    private string Unpack()
    {
        using var stream = typeof(Installer).Assembly.GetManifestResourceStream(PayloadName)
                           ?? throw new InvalidOperationException(
                               "В този файл няма програма — свалете инсталатора наново от страницата на изданието.");

        var path = Path.Combine(Work, "RiosvImports-velopack-Setup.exe");

        using (var file = File.Create(path)) stream.CopyTo(file);

        return path;
    }

    private static bool Writable(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, ".write-" + Guid.NewGuid().ToString("N") + ".tmp");

            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int RunAndWait(string exe, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory
        }) ?? throw new InvalidOperationException("Не се пусна: " + exe);

        if (process.WaitForExit((int)Patience.TotalMilliseconds)) return process.ExitCode;

        try { process.Kill(); } catch { /* вече е спрял */ }

        return -1;
    }

    /// <summary>
    /// Разопакованият инсталатор (над 50 MB) не остава в Temp. При неуспех
    /// остава дневникът, а нова празна папка, направена тук, се маха.
    /// </summary>
    private void CleanUp(bool success, string? createdFolder)
    {
        try
        {
            if (success)
            {
                if (Directory.Exists(Work)) Directory.Delete(Work, true);
            }
            else
            {
                foreach (var exe in Directory.Exists(Work) ? Directory.EnumerateFiles(Work, "*.exe").ToList() : new List<string>())
                    File.Delete(exe);

                if (createdFolder is not null && Directory.Exists(createdFolder) &&
                    !Directory.EnumerateFileSystemEntries(createdFolder).Any())
                    Directory.Delete(createdFolder);
            }
        }
        catch
        {
            // Temp се чисти и от Windows.
        }
    }
}
