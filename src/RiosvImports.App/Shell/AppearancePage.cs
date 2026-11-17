using RiosvImports.App.Controls;
using RiosvImports.Data;

using RiosvImports.Core.Localization;

namespace RiosvImports.App.Shell;

/// <summary>
/// Изгледът — отделен раздел, а не поле в настройките.
///
/// Темата се пробва: човек я сменя, поглежда, връща я. Докато стоеше между
/// ЕИК-а и резервните копия, всяко пробване значеше превъртане покрай неща,
/// които нямат нищо общо. Тук цялата страница е за това.
/// </summary>
internal sealed class AppearancePage : ContentPage
{
    private readonly Workspace _workspace;
    private readonly Label _caption = new();

    public AppearancePage(Workspace workspace) : base(1020)
    {
        _workspace = workspace;

        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true
        };

        stack.Controls.Add(SettingsPage.Title(Lang.T("Изглед", "Appearance")));
        stack.Controls.Add(SettingsPage.Subtitle(Lang.T("Как изглежда програмата. Изборът се помни.", "How the program looks. Your choice is remembered.")));
        stack.Controls.Add(BuildLanguage());
        stack.Controls.Add(BuildThemes());

        Controls.Add(stack);
    }

    /// <summary>
    /// Езикът стои над темата: той сменя всяка дума на екрана, а темата — само
    /// цветовете. По-голямата промяна се предлага първа.
    /// </summary>
    private Control BuildLanguage()
    {
        var card = SettingsPage.Frame(Lang.T("Език", "Language"), out var body);

        var picker = new SegmentedControl
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(44)
        };

        picker.SetItems(Lang.All.Select(l => l.Name));
        picker.SelectedIndex = Math.Max(0, Array.FindIndex(Lang.All, l => l.Key == Lang.Key));

        var caption = new Label
        {
            Dock = DockStyle.Bottom,
            Height = Theme.Scaled(28),
            Font = Theme.Small,
            ForeColor = Theme.InkFaint,
            Text = Lang.T(
                "Имената на отчетите и на полетата остават на български — така се казват в наредбата.",
                "Report and field names stay in Bulgarian — that is what they are called in the regulation.")
        };

        picker.SelectedIndexChanged += (_, _) =>
        {
            var chosen = Lang.All[picker.SelectedIndex];

            _workspace.Settings.Set(SettingsStore.Keys.UiLanguage, chosen.Key);

            // Както при темата: главният прозорец слуша и пресъздава екраните,
            // включително този. Надписите се четат при сглобяване.
            Lang.Use(chosen.Key);
        };

        body.Controls.Add(caption);
        body.Controls.Add(picker);
        card.Height = Theme.Scaled(150);

        return card;
    }

    private Control BuildThemes()
    {
        var card = SettingsPage.Frame(Lang.T("Тема", "Theme"), out var body);

        var picker = new SegmentedControl
        {
            Dock = DockStyle.Top,
            Height = Theme.Scaled(44)
        };

        picker.SetItems(Palette.All.Select(p => p.Name));

        var current = _workspace.Settings.Get(SettingsStore.Keys.UiTheme);
        picker.SelectedIndex = Math.Max(0, Palette.All.ToList().FindIndex(p => p.Key == Palette.ByKey(current).Key));

        _caption.Dock = DockStyle.Bottom;
        _caption.Height = Theme.Scaled(28);
        _caption.Font = Theme.Small;
        _caption.ForeColor = Theme.InkFaint;
        _caption.Text = Describe(Palette.All[picker.SelectedIndex].Key);

        picker.SelectedIndexChanged += (_, _) =>
        {
            var chosen = Palette.All[picker.SelectedIndex];

            _caption.Text = Describe(chosen.Key);
            _workspace.Settings.Set(SettingsStore.Keys.UiTheme, chosen.Key);

            // Смяната минава през Theme, а не се прилага тук: главният прозорец
            // слуша и пресъздава екраните, включително този.
            Theme.Use(chosen.Key);
        };

        body.Controls.Add(_caption);
        body.Controls.Add(picker);
        card.Height = Theme.Scaled(150);

        return card;
    }

    /// <summary>По ключа на темата, не по мястото ѝ: нова тема в средата не разменя описанията.</summary>
    private static string Describe(string key) => key switch
    {
        "dark" => Lang.T("За вечер и слабо осветена стая.", "For evenings and dim rooms."),
        "contrast" => Lang.T("Плътни рамки и максимална разлика между цветовете — за слабо зрение.", "Solid borders and maximum contrast — for weak eyesight."),
        "contrast-dark" => Lang.T("Бял текст върху черно и ярки цветове — за слабо зрение и тъмна стая.", "White text on black with bright colours — for weak eyesight and dark rooms."),
        "warm" => Lang.T("Кремав фон вместо бял, за дълга работа с числа.", "A cream background instead of white, for long spells with numbers."),
        _ => Lang.T("Обичайната — светла, със спокойни цветове.", "The usual one — light, with calm colours.")
    };
}
