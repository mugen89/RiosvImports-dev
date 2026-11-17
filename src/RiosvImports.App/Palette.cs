using RiosvImports.Core.Localization;

namespace RiosvImports.App;

/// <summary>
/// Един набор цветове.
///
/// Имената са по роля, не по цвят: „Ink“, а не „тъмносиво“. Така тъмната
/// тема разменя стойностите, без нито едно място в програмата да разбере, че
/// нещо се е сменило.
///
/// Ролите са малко нарочно. Двайсет нюанса се поддържат зле и всяка нова тема
/// изисква двайсет решения; тук са петнайсет и всяка тема е един прочит.
/// </summary>
internal sealed class Palette
{
    public required string Key { get; init; }

    /// <summary>
    /// Името се смята при всяко четене.
    ///
    /// Палитрите са статични полета: създават се веднъж, при първото обръщение,
    /// заедно с езика, който е бил в сила тогава. Записано в полето име би
    /// останало на стария език след смяна и щеше да се оправи чак при
    /// следващото пускане.
    /// </summary>
    public string Name => Key switch
    {
        "dark" => Lang.T("Тъмна", "Dark"),
        "contrast" => Lang.T("Висок контраст", "High contrast"),
        "contrast-dark" => Lang.T("Тъмен контраст", "Dark contrast"),
        "warm" => Lang.T("Топла", "Warm"),
        _ => Lang.T("Светла", "Light")
    };

    /// <summary>Дали фонът е тъмен — от това зависят сенките и рамките на Windows.</summary>
    public required bool IsDark { get; init; }

    public required Color Accent { get; init; }
    public required Color AccentHover { get; init; }
    public required Color AccentPressed { get; init; }
    public required Color AccentLight { get; init; }
    public required Color AccentSoft { get; init; }

    /// <summary>
    /// Цветът на текста ВЪРХУ плътния основен цвят.
    ///
    /// Не се извежда от „Ink“: в тъмния набор Ink е светъл, защото стои върху
    /// тъмен фон, а основният цвят там е светло синьо — светло върху светло.
    /// Затова всеки набор го казва сам.
    /// </summary>
    public required Color OnAccent { get; init; }

    public required Color Ink { get; init; }
    public required Color InkStrong { get; init; }
    public required Color InkMuted { get; init; }
    public required Color InkFaint { get; init; }

    public required Color Border { get; init; }
    public required Color BorderSoft { get; init; }
    public required Color Surface { get; init; }
    public required Color SurfaceAlt { get; init; }
    public required Color Canvas { get; init; }

    public required Color Ok { get; init; }
    public required Color OkSoft { get; init; }
    public required Color Warn { get; init; }
    public required Color WarnSoft { get; init; }
    public required Color Error { get; init; }
    public required Color ErrorSoft { get; init; }

    private static Color Rgb(int value) =>
        Color.FromArgb((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);

    /// <summary>
    /// Светла — основната. Тъмно петролно синьо върху бяло, с неутрални
    /// цветове с лек синкав уклон: чисто сивото до синьо изглежда мръсно.
    /// </summary>
    public static readonly Palette Light = new()
    {
        Key = "light", IsDark = false,
        Accent = Rgb(0x10496E), AccentHover = Rgb(0x165A87), AccentPressed = Rgb(0x0B3856),
        AccentLight = Rgb(0xE4EFF6), AccentSoft = Rgb(0xF1F7FB), OnAccent = Color.White,
        Ink = Rgb(0x131A21), InkStrong = Rgb(0x2B3742), InkMuted = Rgb(0x566470), InkFaint = Rgb(0x8795A1),
        Border = Rgb(0xDDE4EB), BorderSoft = Rgb(0xECF1F5),
        Surface = Rgb(0xF5F8FA), SurfaceAlt = Rgb(0xFAFCFD), Canvas = Color.White,
        Ok = Rgb(0x1B6E3F), OkSoft = Rgb(0xE6F3EA),
        Warn = Rgb(0x935C04), WarnSoft = Rgb(0xFCF2DF),
        Error = Rgb(0xA82329), ErrorSoft = Rgb(0xFBE9E9)
    };

    /// <summary>
    /// Тъмна — за работа вечер и в слабо осветени стаи.
    ///
    /// Фонът не е черен, а много тъмно синьо-сиво: чисто черното под бял текст
    /// „трепти“ и уморява по-бързо от меко тъмното.
    /// </summary>
    public static readonly Palette Dark = new()
    {
        Key = "dark", IsDark = true,
        Accent = Rgb(0x4FA8D8), AccentHover = Rgb(0x6FBCE6), AccentPressed = Rgb(0x3C8FBC),
        AccentLight = Rgb(0x1B3444), AccentSoft = Rgb(0x16262F), OnAccent = Rgb(0x0A1218),
        Ink = Rgb(0xE8EEF2), InkStrong = Rgb(0xCBD6DE), InkMuted = Rgb(0x9AA9B4), InkFaint = Rgb(0x6E7D88),
        Border = Rgb(0x2A3944), BorderSoft = Rgb(0x22303A),
        Surface = Rgb(0x151F27), SurfaceAlt = Rgb(0x18242D), Canvas = Rgb(0x101920),
        Ok = Rgb(0x69C48E), OkSoft = Rgb(0x142B1F),
        Warn = Rgb(0xD9A65A), WarnSoft = Rgb(0x2C2415),
        Error = Rgb(0xE8848A), ErrorSoft = Rgb(0x2E191B)
    };

    /// <summary>
    /// Висок контраст — за слабо зрение и за екрани, гледани отстрани.
    ///
    /// Чисто бяло и почти черно, плътни рамки навсякъде. Меките подложки са
    /// по-наситени от обичайното, за да се различават и на слаб екран.
    /// </summary>
    public static readonly Palette Contrast = new()
    {
        Key = "contrast", IsDark = false,
        Accent = Rgb(0x00366B), AccentHover = Rgb(0x004E99), AccentPressed = Rgb(0x002548),
        AccentLight = Rgb(0xCFE2F3), AccentSoft = Rgb(0xE8F1F9), OnAccent = Color.White,
        Ink = Color.Black, InkStrong = Rgb(0x111111), InkMuted = Rgb(0x333333), InkFaint = Rgb(0x555555),
        Border = Rgb(0x8C99A5), BorderSoft = Rgb(0xB8C2CC),
        Surface = Rgb(0xEFF2F5), SurfaceAlt = Color.White, Canvas = Color.White,
        Ok = Rgb(0x0A5C2E), OkSoft = Rgb(0xD2ECDB),
        Warn = Rgb(0x7A4600), WarnSoft = Rgb(0xF7E6C8),
        Error = Rgb(0x8C0F16), ErrorSoft = Rgb(0xF6D9DA)
    };

    /// <summary>
    /// Тъмен контраст — бял текст върху черно, като „Висок контраст“ на Windows.
    ///
    /// За слабо зрение в тъмна стая: светлият висок контраст свети с бялото си,
    /// а обикновената тъмна е мека нарочно. Основният цвят е циан с черен надпис
    /// върху него, сигналните цветове са ярки, а рамките — светлосиви, за да
    /// личат кутиите върху черното. Избраният ред е тъмен циан: бялото върху
    /// него се чете, а той личи от черния фон.
    /// </summary>
    public static readonly Palette ContrastDark = new()
    {
        Key = "contrast-dark", IsDark = true,
        Accent = Rgb(0x1AEBFF), AccentHover = Rgb(0x66F2FF), AccentPressed = Rgb(0x00C4D9),
        AccentLight = Rgb(0x004C55), AccentSoft = Rgb(0x002A30), OnAccent = Color.Black,
        Ink = Color.White, InkStrong = Rgb(0xF0F0F0), InkMuted = Rgb(0xD6D6D6), InkFaint = Rgb(0xAFAFAF),
        Border = Rgb(0xC8C8C8), BorderSoft = Rgb(0x8A8A8A),
        Surface = Rgb(0x0D0D0D), SurfaceAlt = Rgb(0x161616), Canvas = Color.Black,
        Ok = Rgb(0x56F08C), OkSoft = Rgb(0x0B3319),
        Warn = Rgb(0xFFD84D), WarnSoft = Rgb(0x3A3000),
        Error = Rgb(0xFF7A7A), ErrorSoft = Rgb(0x3D0F12)
    };

    /// <summary>
    /// Топла — кремав фон вместо бял, за дълга работа с числа.
    ///
    /// Бялото на цял екран свети силно в осветена стая; кремавото сваля
    /// блясъка, без да губи контраста на текста.
    /// </summary>
    public static readonly Palette Warm = new()
    {
        Key = "warm", IsDark = false,
        Accent = Rgb(0x8A4B1F), AccentHover = Rgb(0xA35C2A), AccentPressed = Rgb(0x6B3915),
        AccentLight = Rgb(0xF3E5D6), AccentSoft = Rgb(0xFAF2E9), OnAccent = Color.White,
        Ink = Rgb(0x2A2118), InkStrong = Rgb(0x453727), InkMuted = Rgb(0x6E5C48), InkFaint = Rgb(0x9C8A75),
        Border = Rgb(0xE0D3C2), BorderSoft = Rgb(0xEDE4D7),
        Surface = Rgb(0xF7F1E7), SurfaceAlt = Rgb(0xFCF8F2), Canvas = Rgb(0xFEFBF6),
        Ok = Rgb(0x3F6B34), OkSoft = Rgb(0xE7F0DF),
        Warn = Rgb(0x8A5A0B), WarnSoft = Rgb(0xF8EBD3),
        Error = Rgb(0x99302B), ErrorSoft = Rgb(0xF7E2DF)
    };

    public static readonly IReadOnlyList<Palette> All = [Light, Dark, Contrast, ContrastDark, Warm];

    public static Palette ByKey(string? key) =>
        All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) ?? Light;
}
