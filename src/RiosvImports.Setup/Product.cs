using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace RiosvImports.Setup;

/// <summary>Името и мястото на програмата — същите като в програмата и в tools\release.ps1.</summary>
internal static class Product
{
    /// <summary>
    /// = „vpk pack --packId“. Под него Windows помни инсталацията — не се сменя никога.
    /// RIOSV_PACKID го подменя само за проверката от край до край: пробният пакет
    /// е под друго име, за да не пипне инсталираната програма на машината.
    /// </summary>
    public static string PackId =>
        Environment.GetEnvironmentVariable("RIOSV_PACKID") is { Length: > 0 } id ? id : "RiosvImports";

    public const string Title = "RIOSV Imports";

    /// <summary>= AssemblyName на програмата.</summary>
    public const string MainExe = "RIOSV Imports.exe";

    /// <summary>= Workspace.ProfileVariable в програмата: друга папка вместо профила, за проверки.</summary>
    public const string ProfileVariable = "RIOSV_PROFILE";

    /// <summary>Изданието, което носи този инсталатор — без „+…“, което .NET добавя от git.</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>C:\RIOSV Imports — на диска на Windows: видимо и лесно за резервно копие.</summary>
    public static string DefaultFolder =>
        Path.Combine(
            Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) is { Length: > 0 } drive
                ? drive
                : @"C:\",
            InstallPlan.SubFolder);

    /// <summary>%LOCALAPPDATA%\RIOSV Imports — там чакат копията на работата.</summary>
    public static string ProfileFolder =>
        Environment.GetEnvironmentVariable(ProfileVariable) is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RIOSV Imports");

    private static string ReadVersion()
    {
        var text = typeof(Product).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? typeof(Product).Assembly.GetName().Version?.ToString(3)
                   ?? string.Empty;

        var plus = text.IndexOf('+');

        return plus >= 0 ? text.Substring(0, plus) : text;
    }
}

/// <summary>Къде Windows помни, че е инсталирана програмата — записът на Velopack в „Приложения“.</summary>
internal static class Registration
{
    private const string Uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\";

    /// <summary>
    /// Инсталираната програма — или null. Запис, чиито файлове ги няма (папката е
    /// изтрита на ръка), не се брои: там програма няма.
    /// </summary>
    public static ExistingInstall? Find()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Uninstall + Product.PackId);

            if (key is null) return null;

            var root = Clean(key.GetValue("InstallLocation"));

            if (root.Length == 0 || InstallPlan.Look(root) != FolderState.Ours) return null;

            return new ExistingInstall(InstallPlan.Normalize(root), Clean(key.GetValue("DisplayVersion")));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Velopack пише номера на изданието с празни знаци (\0) след него, а
    /// надписът в Windows спира да рисува на първия такъв — „…инсталирана
    /// RIOSV Imports 1.3.1“ и край, без останалото изречение.
    /// </summary>
    internal static string Clean(object? value) =>
        (value as string ?? string.Empty).Replace("\0", string.Empty).Trim();
}
