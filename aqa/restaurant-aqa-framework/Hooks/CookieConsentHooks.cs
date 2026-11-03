using Reqnroll.BoDi;
using Restaurant_Aqa_Framework.Contexts;
using Restaurant_Aqa_Framework.Screenplay;
using Restaurant_Aqa_Framework.Ui;
using Restaurant_Aqa_Framework.Ui.Components;

namespace Restaurant_Aqa_Framework.Hooks;

/// <summary>Answers the cookie banner with the minimal choice whenever it appears; a no-op for sites without it.</summary>
[Binding]
public class CookieConsentHooks(IObjectContainer container)
{
    // Reqnroll's default hook order is 10000, so this runs after SetUpUiStage built the page.
    [BeforeScenario("ui", Order = 20000)]
    public async Task HandleCookieBanner()
    {
        var page = container.Resolve<Actor>().LookingAtThePage();
        var dialog = new CookieDialogComponent(page);
        await page.AddLocatorHandlerAsync(dialog.Banner, async _ =>
        {
            Console.WriteLine("[CookieConsent] banner appeared");
            await page.Context.TraceInfo("Cookie banner appeared");
            await dialog.AcceptOnlyNecessaryAsync(step => Console.WriteLine($"[CookieConsent] {step}"));
            await page.Context.TraceSuccess("Cookie banner answered: necessary cookies only");
        });
    }
}
