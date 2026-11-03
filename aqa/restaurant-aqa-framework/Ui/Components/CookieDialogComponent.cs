using Microsoft.Playwright;

namespace Restaurant_Aqa_Framework.Ui.Components;

/// <summary>The site's two-step cookie banner. "Small cookie always!" is accept-all, so it is never used.</summary>
public sealed class CookieDialogComponent(IPage page)
{
    public ILocator Banner => page.GetByRole(AriaRole.Dialog, new() { Name = "Hi! Craving something sweet?" });
    private ILocator Preferences => page.GetByRole(AriaRole.Dialog, new() { Name = "Customize Consent Preferences" });

    /// <summary>Keeps only the necessary cookies: opens the preferences, switches every optional category off, saves.</summary>
    public async Task AcceptOnlyNecessaryAsync(Action<string>? log = null)
    {
        await Banner.GetByRole(AriaRole.Button, new() { Name = "Let me choose", Exact = true }).ClickAsync();
        log?.Invoke("clicked \"Let me choose\"");

        foreach (var category in new[] { "Analytics", "Advertisement" })
            await Preferences.GetByRole(AriaRole.Switch, new() { Name = $"Enable {category}", Exact = true }).SetCheckedAsync(false);
        log?.Invoke("optional categories (Analytics, Advertisement) are off");

        await Preferences.GetByRole(AriaRole.Button, new() { Name = "Save my preferences", Exact = true }).ClickAsync();
        log?.Invoke("clicked \"Save my preferences\"");

        await Preferences.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        await Banner.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        log?.Invoke("dialog closed");
    }
}
