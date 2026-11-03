using Microsoft.Playwright;
using Restaurant_Aqa_Framework.Configuration;
using Restaurant_Aqa_Framework.Ui.Components;

namespace Restaurant_Aqa_Framework.Ui;

/// <summary>The home page search bar and the catalog it leads to.</summary>
public sealed class StaySearchPage(IPage page) : BasePage(page)
{
    private ILocator SearchBar => Page.GetByRole(AriaRole.Searchbox, new() { Name = "Suggestion field", Exact = true });
    // The page renders a hidden mobile list next to the desktop one; only the visible tiles are the results.
    private ILocator Cards => Page.Locator(".search-results a.product-card >> visible=true");

    public ILocator SearchField => SearchBar;
    public ILocator PlacesCount => Page.GetByTestId("search-panel-product-count");

    // UI_URL already carries the language segment, so it is navigated to as is.
    public Task NavigateAsync() => Page.GotoAsync(Settings.Current.UiBaseUrl);

    public async Task SearchForLocationAsync(string location)
    {
        await SearchBar.ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = location, Exact = true }).ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Hop", Exact = true }).ClickAsync();
        // The "load" event never settles on the site (third-party trackers), so the cards are the readiness signal.
        await Page.WaitForURLAsync(url => url.Contains("/catalog"), new() { WaitUntil = WaitUntilState.Commit });
        await Cards.First.WaitForAsync();
    }

    public async Task<IReadOnlyList<ResultCardComponent>> ResultCardsAsync() =>
        (await Cards.AllAsync()).Select(card => new ResultCardComponent(card)).ToList();
}
