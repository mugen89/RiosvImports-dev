using RiosvImports.Account;
using RiosvImports.Data;

namespace RiosvImports.App;

/// <summary>
/// Едно място, където се сглобяват услугите извън ядрото: профилът, базата и
/// връзката с мрежата.
///
/// Екраните взимат готовия <see cref="Account"/> оттук, без да знаят как е
/// сглобен. Обновяването НЕ е тук: то е на главния прозорец, за да не пита
/// тестът GitHub, когато само сглобява екраните.
/// </summary>
internal static class AppServices
{
    private static readonly Lazy<HttpClient> Http = new(() => new HttpClient
    {
        // Повикванията стават при натиснат бутон: по-добре бърза грешка,
        // отколкото програма, която изглежда забита.
        Timeout = TimeSpan.FromSeconds(20)
    });

    /// <summary>
    /// Една връзка за цялата програма — профилът, новините и проверката за
    /// издание. Нова връзка при всяко повикване изчерпва портовете на машината.
    /// </summary>
    public static HttpClient Web => Http.Value;

    private static readonly Lazy<AccountSession> Session = new(() =>
    {
        var options = AccountOptions.Default;

        return new AccountSession(
            new AccountClient(Http.Value, options),
            new TokenStore(),
            options);
    });

    /// <summary>
    /// Профилът и правото на ползване. Докато адресите в
    /// <see cref="AccountOptions.Default"/> не са попълнени, се държи като
    /// „всичко е позволено“, за да не пречи на разработката.
    ///
    /// Примерно ползване от бъдещия екран за вход:
    ///
    ///   await AppServices.Account.SignUpAsync(email, password);
    ///   await AppServices.Account.SignInAsync(email, password);
    ///   if (!AppServices.Account.CanSign) { ... покажи покана за абонамент ... }
    /// </summary>
    public static AccountSession Account => Session.Value;

    /// <summary>
    /// Друга база вместо тази до програмата. Задава се от „--db“ при стартиране.
    ///
    /// Съществува заради прегледа: пробната база пътува заедно с програмата и
    /// се отваря, без да се пипа това, което рецензентът има в своята.
    /// </summary>
    private static string? _path;

    /// <summary>Казва коя база да се отвори. Вика се ПРЕДИ първото обръщение.</summary>
    public static void UseWorkspace(string? path) => _path = path;

    private static readonly Lazy<Workspace> Local = new(Open);

    /// <summary>
    /// Местната база с работата на потребителя. Отваря се при първото обръщение
    /// и се привежда до последната версия още там.
    /// </summary>
    public static Workspace Workspace => Local.Value;

    /// <summary>Какво стана с базата в профила при това пускане — началният екран го казва веднъж.</summary>
    public static AdoptOutcome Adopted { get; private set; }

    /// <summary>
    /// Отворена ли е базата по подразбиране — до програмата, а не подадена с „--db“.
    ///
    /// Пита се за ТАЗИ база: тестовете сглобяват главния прозорец с временни бази
    /// и никога не отварят тази тук.
    /// </summary>
    public static bool IsOwn(Workspace workspace) =>
        _path is null && Local.IsValueCreated && ReferenceEquals(Local.Value, workspace);

    /// <summary>
    /// Отваря базата до програмата — след като пренесе тази от профила, ако
    /// тук още няма. Подадена с „--db“, базата се отваря такава, каквато е.
    /// </summary>
    private static Workspace Open()
    {
        if (_path is not null) return Workspace.Open(_path);

        var target = DataLocation.Workspace;

        Adopted = DataLocation.Adopt(Workspace.DefaultPath(), target);

        // Пренасянето не стана: работата остава в профила, където е и била.
        // Празна нова база до програмата би изглеждала като загубена работа.
        if (Adopted == AdoptOutcome.Failed) return Workspace.Open(Workspace.DefaultPath());

        var workspace = Workspace.Open(target);

        if (Adopted == AdoptOutcome.Moved) Announce(workspace, target);

        return workspace;
    }

    /// <summary>
    /// Известие на началния екран: къде е работата сега.
    ///
    /// Местно (<see cref="NewsStore.LocalPrefix"/>): новините от мрежата махат
    /// всичко, което го няма във файла им, а това го няма там.
    /// </summary>
    private static void Announce(Workspace workspace, string target)
    {
        try
        {
            workspace.News.Add(new NewsItem(
                NewsStore.LocalPrefix + "data-moved",
                DateTime.Now,
                NewsSeverity.Important,
                Core.Localization.Lang.T("Работата вече стои до програмата", "Your work now sits next to the program"),
                Core.Localization.Lang.T(
                    $"Базата е пренесена от профила в папка „{DataLocation.FolderName}“ до програмата: {target}. " +
                    "Нищо не е изтрито — старото копие стои в профила преименувано. Мястото се вижда в „Настройки“ → „База“.",
                    $"The database was moved from your profile into the “{DataLocation.FolderName}” folder next to the program: {target}. " +
                    "Nothing was deleted — the old copy stays in your profile, renamed. The location is shown under “Settings” → “Database”."),
                false));
        }
        catch
        {
            // Известието е удобство; работата вече е пренесена.
        }
    }

    public static void Close()
    {
        if (Local.IsValueCreated) Local.Value.Dispose();
    }
}
