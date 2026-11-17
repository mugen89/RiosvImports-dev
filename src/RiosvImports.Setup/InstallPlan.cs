using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RiosvImports.Setup;

// Този файл се сглобява и в тестовете (net9), а инсталаторът е на .NET
// Framework 4.7.2: само API, което го има и в двете — без Path.GetRelativePath,
// без File.Move(…, overwrite), без индекси с „^“ и „..“, без записи (record).

/// <summary>Какво ще направи инсталаторът в избраната папка.</summary>
internal enum PlanKind
{
    /// <summary>Не става — причината е в <see cref="InstallPlan.Reason"/>.</summary>
    Invalid,

    /// <summary>Ново инсталиране в празна (или още несъществуваща) папка.</summary>
    Fresh,

    /// <summary>Програмата вече е тук — обновява се на място, работата остава.</summary>
    Update,

    /// <summary>Програмата е инсталирана другаде — мести се тук заедно с работата.</summary>
    Move
}

/// <summary>Какво има в една папка — от гледна точка на инсталатора.</summary>
internal enum FolderState
{
    Missing,
    Empty,

    /// <summary>Само „Data“ — работата от предишна инсталация, без програмата.</summary>
    Leftover,

    /// <summary>Инсталирана RIOSV Imports: Update.exe и „current“.</summary>
    Ours,

    /// <summary>Преносимото копие — то също има Update.exe, но и „.portable“.</summary>
    Portable,

    /// <summary>Чужди файлове.</summary>
    Occupied
}

/// <summary>Вече инсталираната програма — така, както я помни Windows.</summary>
internal sealed class ExistingInstall
{
    public ExistingInstall(string root, string version)
    {
        Root = root;
        Version = version;
    }

    public string Root { get; }

    public string Version { get; }
}

/// <summary>
/// Решението къде точно отива програмата — преди да е пипнато каквото и да е.
///
/// Главното правило: <b>програмата влиза само в своя папка</b>. Velopack
/// смята цялата папка за своя: при повторно инсталиране я мести встрани и я
/// трие, при махане изпразва всичко в нея. Програма, сложена в „Документи“ или
/// в D:\, би отнесла при махане и всичко друго там. Затова непразна чужда
/// папка получава подпапка „RIOSV Imports“, а в нея — само програмата и нейната
/// работа („Data“).
/// </summary>
internal sealed class InstallPlan
{
    /// <summary>Подпапката, в която програмата влиза, когато избраната не е празна.</summary>
    public const string SubFolder = "RIOSV Imports";

    /// <summary>Главният файл на програмата — = AssemblyName на RiosvImports.App.</summary>
    public const string MainExe = "RIOSV Imports.exe";

    /// <summary>
    /// По-дълъг път не се приема: вътре Velopack слага „current\…“ и
    /// „…_&lt;16 знака&gt;“, а Windows по подразбиране спира при 260 знака.
    /// </summary>
    public const int MaxLength = 150;

    private InstallPlan(PlanKind kind, string folder, string reason, ExistingInstall? existing,
        bool extended = false, bool keepsWork = false)
    {
        Kind = kind;
        Folder = folder;
        Reason = reason;
        Existing = existing;
        Extended = extended;
        KeepsWork = keepsWork;
    }

    public PlanKind Kind { get; }

    /// <summary>Папката, в която отива програмата — може да е подпапка на избраната.</summary>
    public string Folder { get; }

    /// <summary>Защо не става — само при <see cref="PlanKind.Invalid"/>.</summary>
    public string Reason { get; }

    /// <summary>Инсталираната програма, ако има такава.</summary>
    public ExistingInstall? Existing { get; }

    /// <summary>Избраната папка не беше празна и програмата влиза в подпапка.</summary>
    public bool Extended { get; }

    /// <summary>В папката вече има работа („Data“) и програмата продължава с нея.</summary>
    public bool KeepsWork { get; }

    public bool Valid => Kind != PlanKind.Invalid;

    /// <summary>Папките, в които обикновен потребител не пише: Windows и Program Files.</summary>
    public static IReadOnlyList<string> ProtectedFolders()
    {
        var folders = new List<string>();

        foreach (var special in new[]
                 {
                     Environment.SpecialFolder.Windows,
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86
                 })
        {
            var path = Environment.GetFolderPath(special);

            if (path.Length > 0) folders.Add(path);
        }

        // От 32-битов процес ProgramFiles сочи „(x86)“; истинската е тук.
        var native = Environment.GetEnvironmentVariable("ProgramW6432");
        if (!string.IsNullOrEmpty(native)) folders.Add(native!);

        return folders;
    }

    public static InstallPlan For(string? typed, ExistingInstall? existing, IEnumerable<string> protectedFolders)
    {
        var text = Environment.ExpandEnvironmentVariables((typed ?? string.Empty).Trim().Trim('"').Trim());

        if (text.Length == 0)
            return Invalid("Напишете папка или я изберете с „Избери…“.");

        if (text.StartsWith(@"\\", StringComparison.Ordinal) || text.StartsWith("//", StringComparison.Ordinal))
            return Invalid("Мрежова папка не става: програмата се обновява, като пише в папката си. " +
                           "Изберете папка на този компютър.");

        if (!HasDrive(text))
            return Invalid(@"Напишете целия път — например C:\RIOSV Imports.");

        // Проверката е ръчна: .NET Framework и .NET 9 приемат различни знаци.
        if (text.Substring(2).IndexOfAny(new[] { '"', '<', '>', '|', '*', '?', ':' }) >= 0 ||
            text.Any(char.IsControl))
            return Invalid("В пътя има знак, който Windows не приема в име на папка: \" < > | * ? :");

        string folder;

        try
        {
            folder = Normalize(text);
        }
        catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException)
        {
            return Invalid("Windows не приема този път като име на папка.");
        }

        var extended = false;

        // Направо в C:\ или D:\ — никога: коренът на диска е пълен с чуждо.
        if (IsDriveRoot(folder))
        {
            folder = Path.Combine(folder, SubFolder);
            extended = true;
        }

        for (var attempt = 0; ; attempt++)
        {
            if (folder.Length > MaxLength)
                return Invalid(@"Пътят е твърде дълъг. Изберете по-кратък — например C:\RIOSV Imports.");

            if (existing is not null && Same(folder, existing.Root))
                return new InstallPlan(PlanKind.Update, folder, string.Empty, existing, extended);

            if (protectedFolders.Any(p => Inside(folder, p)))
                return Invalid("В тази папка Windows не дава да се пише без администратор, а програмата се " +
                               @"обновява, като пише в папката си. Изберете друга — например C:\RIOSV Imports.");

            if (existing is not null && Inside(folder, existing.Root))
                return Invalid("Тази папка е вътре в инсталираната програма. Изберете друга.");

            var state = Look(folder);

            // Непразна чужда папка: програмата влиза в своя подпапка — веднъж.
            if (state == FolderState.Occupied && attempt == 0 &&
                !string.Equals(Path.GetFileName(folder), SubFolder, StringComparison.OrdinalIgnoreCase))
            {
                folder = Path.Combine(folder, SubFolder);
                extended = true;
                continue;
            }

            switch (state)
            {
                case FolderState.Occupied:
                    return Invalid($"„{folder}“ не е празна. Програмата влиза само в празна папка: " +
                                   "при обновяване и махане тя изпразва папката си.");

                case FolderState.Portable:
                    return Invalid("Тук стои преносимото копие на RIOSV Imports — то се обновява само. " +
                                   "За инсталиране изберете друга папка.");

                case FolderState.Ours when existing is not null:
                    return Invalid($"Тук стои друго копие на RIOSV Imports, а инсталираното е в „{existing.Root}“. " +
                                   $"Изберете празна папка или „{existing.Root}“.");

                // Копие, което Windows не помни (махнато от списъка на приложенията):
                // слага се наново на място, работата остава.
                case FolderState.Ours:
                    return new InstallPlan(PlanKind.Update, folder, string.Empty, null, extended);

                case FolderState.Leftover when existing is not null:
                    return Invalid($"В „{folder}“ вече има работа на RIOSV Imports (папка „Data“). " +
                                   $"Изберете празна папка — работата от „{existing.Root}“ иска свое място.");
            }

            var leftover = state == FolderState.Leftover;

            return existing is not null
                ? new InstallPlan(PlanKind.Move, folder, string.Empty, existing, extended)
                : new InstallPlan(PlanKind.Fresh, folder, string.Empty, null, extended, leftover);
        }
    }

    /// <summary>
    /// Какво има в папката.
    ///
    /// „.dead“ е белегът на Velopack за махната програма. Той трие Update.exe
    /// чак няколко секунди след като е свършил, а при следващо инсталиране
    /// изпразва такава папка без въпроси. Затова тя е празна — или останала
    /// работа, ако „Data“ още е там.
    /// </summary>
    public static FolderState Look(string folder)
    {
        if (!Directory.Exists(folder)) return File.Exists(folder) ? FolderState.Occupied : FolderState.Missing;

        List<string> names;

        try
        {
            names = Directory.EnumerateFileSystemEntries(folder)
                .Select(entry => Path.GetFileName(entry) ?? string.Empty)
                .ToList();
        }
        catch (Exception e) when (e is UnauthorizedAccessException || e is IOException)
        {
            return FolderState.Occupied;
        }

        if (names.RemoveAll(name => string.Equals(name, ".dead", StringComparison.OrdinalIgnoreCase)) > 0)
            return names.Any(name => string.Equals(name, InstallData.DataFolder, StringComparison.OrdinalIgnoreCase))
                ? FolderState.Leftover
                : FolderState.Empty;

        if (names.Count == 0) return FolderState.Empty;

        if (names.Any(name => string.Equals(name, "Update.exe", StringComparison.OrdinalIgnoreCase)))
            return names.Any(name => string.Equals(name, ".portable", StringComparison.OrdinalIgnoreCase))
                ? FolderState.Portable
                : FolderState.Ours;

        return names.All(name => string.Equals(name, InstallData.DataFolder, StringComparison.OrdinalIgnoreCase))
            ? FolderState.Leftover
            : FolderState.Occupied;
    }

    /// <summary>
    /// Има ли в папката истинска инсталация: главният файл в „current“ — или поне
    /// работата в „Data“, която трябва да се пази.
    ///
    /// Velopack, спрян по средата, оставя запис в „Приложения“ и папка само с
    /// Update.exe и „packages“. Така стана на 17.11.2026, когато антивирусната
    /// програма не пусна новия exe при инсталиране. Там няма нито програма за
    /// обновяване, нито работа за пазене — инсталаторът не предлага „Обнови“ в
    /// такава папка, а пита наново къде.
    /// </summary>
    public static bool Holds(string root) =>
        Look(root) == FolderState.Ours &&
        (File.Exists(Path.Combine(root, "current", MainExe)) || InstallData.HasWork(root));

    /// <summary>Пълен път без наклонена черта накрая — освен за корена на диска.</summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;

        while (full.Length > root.Length && (full.EndsWith("\\", StringComparison.Ordinal) ||
                                             full.EndsWith("/", StringComparison.Ordinal)))
            full = full.Substring(0, full.Length - 1);

        return full;
    }

    public static bool Same(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Папката е същата или е под другата.</summary>
    public static bool Inside(string path, string folder)
    {
        string inner, outer;

        try
        {
            inner = Normalize(path);
            outer = Normalize(folder);
        }
        catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException)
        {
            return false;
        }

        if (string.Equals(inner, outer, StringComparison.OrdinalIgnoreCase)) return true;

        var prefix = outer.EndsWith("\\", StringComparison.Ordinal) ? outer : outer + "\\";

        return inner.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasDrive(string text) =>
        text.Length >= 3 && char.IsLetter(text[0]) && text[1] == ':' && (text[2] == '\\' || text[2] == '/');

    private static bool IsDriveRoot(string folder) =>
        string.Equals(Path.GetPathRoot(folder), folder, StringComparison.OrdinalIgnoreCase);

    private static InstallPlan Invalid(string reason) =>
        new InstallPlan(PlanKind.Invalid, string.Empty, reason, null);
}

/// <summary>Аргумент за командния ред — с кавички, без да се счупи от „\“ накрая.</summary>
internal static class CommandLine
{
    /// <summary>
    /// По правилата на Windows „\"“ е буквална кавичка: път с „\“ накрая би
    /// глътнал затварящата кавичка. Затова наклонените черти накрая се удвояват.
    /// </summary>
    public static string Quote(string argument)
    {
        var trailing = 0;

        for (var i = argument.Length - 1; i >= 0 && argument[i] == '\\'; i--) trailing++;

        return "\"" + argument + new string('\\', trailing) + "\"";
    }
}
