using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RiosvImports.Setup;

/// <summary>
/// Работата при инсталиране: копие преди всяка промяна и връщане, ако я няма след нея.
///
/// Програмата пази работата си в „Data“ в своята папка, а Velopack трие папката
/// при повторно инсталиране и при махане. Новата програма сама взема работата
/// от старата папка (DataLocation.Recover в програмата), но тук не се разчита
/// само на това: преди инсталиране работата се копира в профила, а след него,
/// ако в папката на програмата я няма, се връща оттам.
///
/// Сглобява се и в тестовете — само API, което го има в .NET Framework 4.7.2.
/// </summary>
internal static class InstallData
{
    public const string DataFolder = "Data";
    public const string Database = "workspace.db";

    /// <summary>Подпапката в профила с копията от инсталиране.</summary>
    public const string BackupsFolder = "Backups";

    public static string DatabaseIn(string root) => Path.Combine(root, DataFolder, Database);

    public static bool HasWork(string root) => File.Exists(DatabaseIn(root));

    /// <summary>
    /// Копие на цялата „Data“ в профила: Backups\before-install-гггг-ММ-дд-ччммсс.
    /// Програмата трябва да е затворена — тогава базата е цяла във файла си.
    /// </summary>
    /// <returns>Папката с копието или null, ако работа няма.</returns>
    public static string? Backup(string root, string profileFolder, DateTime now)
    {
        var source = Path.Combine(root, DataFolder);

        if (!File.Exists(Path.Combine(source, Database))) return null;

        var target = Path.Combine(profileFolder, BackupsFolder, "before-install-" + now.ToString("yyyy-MM-dd-HHmmss"));

        for (var n = 2; Directory.Exists(target); n++)
            target = Path.Combine(profileFolder, BackupsFolder, "before-install-" + now.ToString("yyyy-MM-dd-HHmmss") + "-" + n);

        CopyTree(source, target);

        // Копие, което не е цяло, е по-лошо от никакво: то би се върнало като работа.
        if (new FileInfo(Path.Combine(target, Database)).Length != new FileInfo(Path.Combine(source, Database)).Length)
            throw new IOException("Копието на работата не излезе цяло: " + target);

        return target;
    }

    /// <summary>
    /// Връща работата от копието — само ако в папката на програмата я няма:
    /// каквото е там, е по-новото.
    ///
    /// Базата влиза първа и под временно име, после идва нейният WAL. Обратният
    /// ред би оставил при прекъсване WAL без база — а SQLite би го приложил към
    /// празна нова база.
    /// </summary>
    public static bool Restore(string backup, string root)
    {
        var data = Path.Combine(root, DataFolder);
        var database = Path.Combine(data, Database);

        if (File.Exists(database) || !File.Exists(Path.Combine(backup, Database))) return false;

        Directory.CreateDirectory(data);

        var partial = database + ".partial";

        if (File.Exists(partial)) File.Delete(partial);

        File.Copy(Path.Combine(backup, Database), partial);
        File.Move(partial, database);

        foreach (var tail in new[] { "-wal", "-shm" })
        {
            var file = Path.Combine(backup, Database + tail);

            if (File.Exists(file) && !File.Exists(database + tail)) File.Copy(file, database + tail);
        }

        CopyTree(backup, data, name => name.StartsWith(Database, StringComparison.OrdinalIgnoreCase));

        return true;
    }

    /// <summary>Базите в профила: workspace.db и копията с дата от махане (workspace-….db).</summary>
    public static HashSet<string> Rescues(string profileFolder)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(profileFolder)) return names;

        foreach (var file in Directory.EnumerateFiles(profileFolder, "workspace*.db"))
            names.Add(Path.GetFileName(file));

        return names;
    }

    /// <summary>
    /// След преместване: копията, които старата програма е оставила в профила
    /// при махането си, се преименуват.
    ///
    /// Махана, програмата пренася работата в профила, за да я вземе следващото
    /// инсталиране. Тук работата вече е пренесена направо в новата папка —
    /// оставено под старото име, копието би било взето отново някой ден, като
    /// по-стара работа върху по-новата.
    /// </summary>
    /// <returns>Преименуваните файлове.</returns>
    public static IReadOnlyList<string> RetireNew(string profileFolder, ISet<string> before, string home, DateTime now)
    {
        var retired = new List<string>();

        foreach (var name in Rescues(profileFolder).Where(name => !before.Contains(name)).ToList())
        {
            var file = Path.Combine(profileFolder, name);
            var target = file + ".moved";

            if (File.Exists(target)) File.Delete(target);

            File.Move(file, target);

            foreach (var tail in new[] { "-wal", "-shm" })
            {
                if (!File.Exists(file + tail)) continue;
                if (File.Exists(target + tail)) File.Delete(target + tail);

                File.Move(file + tail, target + tail);
            }

            retired.Add(target);
        }

        if (retired.Count > 0)
            File.WriteAllText(
                Path.Combine(profileFolder, "ПРЕМЕСТЕНА.txt"),
                $"Работата на RIOSV Imports е преместена на {now:dd.MM.yyyy HH:mm} заедно с програмата:{Environment.NewLine}" +
                $"{Path.Combine(home, DataFolder, Database)}{Environment.NewLine}{Environment.NewLine}" +
                $"Файловете с „.moved“ тук са стари копия. Програмата не ги отваря и те могат да се изтрият.{Environment.NewLine}");

        return retired;
    }

    /// <summary>Копира папка с подпапките — без да презаписва нищо в целта.</summary>
    public static void CopyTree(string from, string to, Func<string, bool>? skip = null)
    {
        var source = InstallPlan.Normalize(from);

        Directory.CreateDirectory(to);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = file.Substring(source.Length).TrimStart('\\', '/');

            if (skip is not null && skip(relative)) continue;

            var copy = Path.Combine(to, relative);

            if (File.Exists(copy)) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }
    }
}
