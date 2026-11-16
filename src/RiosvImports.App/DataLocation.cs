using RiosvImports.Data;

namespace RiosvImports.App;

/// <summary>Какво стана с базата в профила при пускането.</summary>
internal enum AdoptOutcome
{
    /// <summary>Нямаше какво да се пренася — или тук вече има база.</summary>
    Nothing,

    /// <summary>Базата от профила е пренесена до програмата.</summary>
    Moved,

    /// <summary>Пренасянето не стана — програмата работи с базата в профила.</summary>
    Failed
}

/// <summary>
/// Къде стои работата: в папка „Data“ до програмата.
///
/// Дотук базата стоеше в профила на потребителя (%LOCALAPPDATA%\RIOSV Imports),
/// далеч от програмата. Преносимото копие не я носеше със себе си, а две копия
/// на програмата — пробното и истинското — пишеха в една и съща база. Сега всяко
/// копие пази своята работа до себе си и тя се намира там, където е програмата.
///
/// <b>„До програмата“ не е папката „current“.</b> Инсталираната програма стои в
/// %LOCALAPPDATA%\RiosvImports\current, а Velopack сменя „current“ цялата при
/// всяко обновяване — база вътре би изчезнала с първото ново издание. Затова
/// коренът е папката над нея, тази с Update.exe: там стоят и инсталаторът, и
/// пакетите, и при обновяване тя остава. Същото е за преносимото копие.
///
/// Където до програмата не може да се пише — програма, сложена в Program Files
/// или на мрежов диск само за четене, — работата остава в профила, както преди.
/// </summary>
internal static class DataLocation
{
    public const string FolderName = "Data";
    public const string FileName = "workspace.db";

    /// <summary>
    /// Друга папка вместо тази до програмата — за проверка на пренасянето без
    /// пипане на истинската база. Тестовете не я ползват: те подават пътищата сами.
    /// </summary>
    internal const string FolderVariable = "RIOSV_DATA";

    /// <summary>Папката на профила — старото място на базата и мястото, където чака при деинсталиране.</summary>
    public static string ProfileFolder => Data.Workspace.ProfileFolder();

    private static readonly Lazy<string> Resolved = new(() =>
        Environment.GetEnvironmentVariable(FolderVariable) is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Resolve(AppContext.BaseDirectory, ProfileFolder));

    /// <summary>Папката, в която програмата пише работата си.</summary>
    public static string Folder => Resolved.Value;

    /// <summary>Базата по подразбиране — в <see cref="Folder"/>.</summary>
    public static string Workspace => Path.Combine(Folder, FileName);

    /// <summary>Стои ли работата до програмата — или е останала в профила, защото там не може.</summary>
    public static bool BesideProgram => !Same(Folder, ProfileFolder);

    /// <summary>
    /// Коренът на програмата.
    ///
    /// Инсталирана или преносима, програмата се пуска от „current“, а над нея
    /// стои Update.exe. Тогава коренът е горната папка — тя оцелява при
    /// обновяване. Във всеки друг случай (Visual Studio, разархивирана папка)
    /// коренът е папката на самата програма.
    /// </summary>
    internal static string Root(string programFolder)
    {
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(programFolder));
        var parent = Path.GetDirectoryName(folder);

        var updated = parent is not null &&
                      string.Equals(Path.GetFileName(folder), "current", StringComparison.OrdinalIgnoreCase) &&
                      File.Exists(Path.Combine(parent, "Update.exe"));

        return updated ? parent! : folder;
    }

    /// <summary>„Data“ до програмата, ако в нея може да се пише — иначе профилът.</summary>
    internal static string Resolve(string programFolder, string profileFolder)
    {
        var data = Path.Combine(Root(programFolder), FolderName);

        return Writable(data) ? data : profileFolder;
    }

    /// <summary>
    /// Може ли да се пише в папката — пробва се, не се гадае по правата.
    ///
    /// Правата в Windows се четат трудно и лъжат (виртуализация, мрежови
    /// дискове). Един празен файл, записан и изтрит, казва истината.
    /// </summary>
    internal static bool Writable(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);

            var probe = Path.Combine(folder, $".write-{Guid.NewGuid():N}.tmp");

            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Пренася базата от профила до програмата — веднъж, при първото пускане.
    ///
    /// Само ако до програмата още няма база: там вече написаното е по-новото и не
    /// се застъпва. Копира се през SQLite (<see cref="Data.Workspace.Copy"/>), а не
    /// с File.Copy: при WAL последните записи стоят в отделен файл до базата.
    ///
    /// Старото копие НЕ се трие — преименува се и до него остава бележка къде е
    /// отишла работата. Преименувано, то не се отваря от следващо копие на
    /// програмата като „заварена база“: работата е една и вече стои другаде.
    /// </summary>
    internal static AdoptOutcome Adopt(string profileDatabase, string targetDatabase)
    {
        if (Same(profileDatabase, targetDatabase)) return AdoptOutcome.Nothing;
        if (File.Exists(targetDatabase) || !File.Exists(profileDatabase)) return AdoptOutcome.Nothing;

        var partial = targetDatabase + ".partial";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetDatabase)!);

            // Наполовина копирана база под истинското име би била отворена като
            // празна работа. Копира се под друго име и се преименува накрая.
            if (File.Exists(partial)) File.Delete(partial);

            Data.Workspace.Copy(profileDatabase, partial);
            File.Move(partial, targetDatabase);
        }
        catch
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { /* остава за следващия път */ }

            return AdoptOutcome.Failed;
        }

        Retire(profileDatabase, targetDatabase);

        return AdoptOutcome.Moved;
    }

    /// <summary>Името, под което старото копие остава в профила.</summary>
    internal static string Retired(string profileDatabase) => profileDatabase + ".moved";

    /// <summary>
    /// Старото копие се преименува и до него се оставя бележка.
    ///
    /// Не пречи, ако не стане — например старо издание на програмата още е
    /// отворено и държи файла. Новото копие вече е цяло; при следващото пускане
    /// тук има база и пренасяне не се прави.
    /// </summary>
    private static void Retire(string profileDatabase, string targetDatabase)
    {
        try
        {
            var retired = Retired(profileDatabase);

            if (File.Exists(retired)) File.Delete(retired);

            File.Move(profileDatabase, retired);

            foreach (var tail in new[] { "-wal", "-shm" })
                if (File.Exists(profileDatabase + tail))
                    File.Move(profileDatabase + tail, retired + tail, overwrite: true);

            File.WriteAllText(
                Path.Combine(Path.GetDirectoryName(profileDatabase)!, "ПРЕМЕСТЕНА.txt"),
                $"Работата на RIOSV Imports е преместена на {DateTime.Now:dd.MM.yyyy HH:mm} до програмата:{Environment.NewLine}" +
                $"{targetDatabase}{Environment.NewLine}{Environment.NewLine}" +
                $"{Path.GetFileName(retired)} е старото копие. Програмата не го отваря и то може да се изтрие.{Environment.NewLine}");
        }
        catch
        {
            // Виж горе: новото копие е цяло, а старото просто остава под името си.
        }
    }

    /// <summary>
    /// Преди деинсталиране: работата се пренася в профила, за да не си отиде с
    /// програмата.
    ///
    /// Velopack трие цялата папка на програмата при деинсталиране, а „Data“ е в
    /// нея. Пренесена в профила, работата чака там като преди 1.3.1 — и при
    /// следващо инсталиране се пренася обратно сама (<see cref="Adopt"/>).
    /// Има ли вече база там — от друго копие, — тази влиза до нея с датата си и
    /// нищо не се презаписва.
    /// </summary>
    internal static string? Rescue(string dataFolder, string profileFolder)
    {
        var source = Path.Combine(dataFolder, FileName);

        if (Same(dataFolder, profileFolder) || !File.Exists(source)) return null;

        try
        {
            Directory.CreateDirectory(profileFolder);

            var target = Path.Combine(profileFolder, FileName);

            if (File.Exists(target))
                target = Path.Combine(profileFolder, $"workspace-{DateTime.Now:yyyyMMdd-HHmmss}.db");

            Data.Workspace.Copy(source, target);

            return target;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// При инсталиране върху вече инсталирана програма: работата се взема от
    /// старата папка, преди тя да бъде изтрита.
    ///
    /// Пуснат там, където програмата вече е, Setup.exe на Velopack пита „Repair“
    /// или „Update“ и после премества цялата стара папка встрани —
    /// RiosvImports_&lt;16 знака&gt;, а в по-новите издания RiosvImports.&lt;16 знака&gt;,
    /// — слага новата и трие старата заедно с „Data“. Куката за инсталиране на
    /// новата програма се пуска, докато старата папка още стои: тогава работата
    /// се копира в новата. В 1.3.1 повторното инсталиране изтриваше работата.
    ///
    /// Само ако в новата папка още няма база: там написаното е по-новото. Базата
    /// минава през SQLite — с последните записи от WAL — и първа: Velopack дава на
    /// куката 30 секунди, а резервните копия до нея са само добавка.
    /// </summary>
    /// <returns>Папката „Data“, от която е взета работата, или null.</returns>
    internal static string? Recover(string root)
    {
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

            var parent = Path.GetDirectoryName(root);
            var target = Path.Combine(root, FolderName);

            if (parent is null || !Directory.Exists(parent) || File.Exists(Path.Combine(target, FileName))) return null;

            var name = Path.GetFileName(root);

            var source = Directory.EnumerateDirectories(parent)
                .Where(folder => SetAside(name, Path.GetFileName(folder)))
                .Select(folder => Path.Combine(folder, FolderName))
                .Where(data => File.Exists(Path.Combine(data, FileName)))
                .OrderByDescending(data => LastWritten(Path.Combine(data, FileName)))
                .FirstOrDefault();

            if (source is null) return null;

            Directory.CreateDirectory(target);

            var database = Path.Combine(target, FileName);
            var partial = database + ".partial";

            if (File.Exists(partial)) File.Delete(partial);

            Data.Workspace.Copy(Path.Combine(source, FileName), partial);
            File.Move(partial, database);

            // Останалото — резервни копия, дневник — без да се презаписва нищо.
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);

                if (relative.StartsWith(FileName, StringComparison.OrdinalIgnoreCase)) continue;

                var copy = Path.Combine(target, relative);

                if (File.Exists(copy)) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(file, copy);
            }

            return source;
        }
        catch
        {
            // Куката не бива да спира инсталирането. Инсталаторът на RIOSV Imports
            // пази и свое копие на работата и го връща, ако тук нещо не стане.
            return null;
        }
    }

    /// <summary>
    /// Името на папка, която Velopack е преместил встрани при повторно инсталиране.
    ///
    /// 0.0.1298: „{корен}_{16 букви и цифри}“; по-новите издания сменят
    /// разширението: „{корен без разширение}.{16 букви и цифри}“.
    /// </summary>
    internal static bool SetAside(string rootName, string candidate)
    {
        static bool Random(string tail) => tail.Length == 16 && tail.All(char.IsAsciiLetterOrDigit);

        if (candidate.Length == rootName.Length + 17 &&
            candidate.StartsWith(rootName + "_", StringComparison.OrdinalIgnoreCase) &&
            Random(candidate[(rootName.Length + 1)..])) return true;

        var stem = Path.GetFileNameWithoutExtension(rootName);

        return candidate.Length == stem.Length + 17 &&
               candidate.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase) &&
               Random(candidate[(stem.Length + 1)..]);
    }

    private static DateTime LastWritten(string database)
    {
        var written = File.GetLastWriteTimeUtc(database);
        var wal = database + "-wal";

        return File.Exists(wal) && File.GetLastWriteTimeUtc(wal) > written ? File.GetLastWriteTimeUtc(wal) : written;
    }

    private static bool Same(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);
}
