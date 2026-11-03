using Restaurant_Aqa_Framework.Screenplay;
using Restaurant_Aqa_Framework.Screenplay.Questions;
using Restaurant_Aqa_Framework.Screenplay.Tasks;
using Restaurant_Aqa_Framework.Support;

namespace Restaurant_Aqa_Framework.StepDefinitions;

[Binding]
public sealed class StaySearchStepDefinitions(Actor actor) : StepDefinitionBase(actor)
{
    [Given("the visitor is on the stay search")]
    public Task GivenVisitorIsOnTheStaySearch() => Actor.AttemptsToAsync(new OpenTheStaySearch());

    [When("the visitor opens the stay search again")]
    public Task WhenVisitorOpensTheStaySearchAgain() => Actor.AttemptsToAsync(new OpenTheStaySearch());

    [Then("only the necessary cookies are accepted")]
    public async Task ThenOnlyTheNecessaryCookiesAreAccepted()
    {
        var consent = await Actor.AsksForAsync(new TheCookieConsent());
        var saved = string.Join(", ", consent.Select(c => $"{c.Key}:{c.Value}"));
        Console.WriteLine($"[CookieConsent] saved consent: {saved}");

        await Assert.That(consent.GetValueOrDefault("necessary")).IsEqualTo("yes").Because(saved);
        foreach (var category in new[] { "functional", "analytics", "performance", "advertisement", "other" })
            await Assert.That(consent.GetValueOrDefault(category)).IsEqualTo("no").Because($"{category} in: {saved}");
    }

    [Then("the cookie banner is not shown")]
    public async Task ThenTheCookieBannerIsNotShown() =>
        await Assert.That(await Actor.AsksForAsync(new TheCookieBannerShown())).IsFalse();

    [When("the visitor searches for stays in a country")]
    public Task WhenVisitorSearchesForStaysInACountry() => Actor.AttemptsToAsync(new SearchForALocation(StayInputs.Country));

    [Then("the search shows that country")]
    public async Task ThenTheSearchShowsThatCountry()
    {
        var search = await Actor.AsksForAsync(new TheSearchShown());
        await Assert.That(search.Location).IsEqualTo(StayInputs.Country).Because($"url was {search.Url}");
    }

    [Then("the results stay in English")]
    public async Task ThenTheResultsStayInEnglish()
    {
        var search = await Actor.AsksForAsync(new TheSearchShown());
        await Assert.That(new Uri(search.Url).AbsolutePath).StartsWith("/en/").Because($"url was {search.Url}");
    }

    [Then("some places are offered")]
    public async Task ThenSomePlacesAreOffered()
    {
        var count = await Actor.AsksForAsync(new TheNumberOfPlacesOffered());
        await Assert.That(count).IsGreaterThan(0);
    }

    [Then("every place shown has a name, a price and a link")]
    public async Task ThenEveryPlaceHasNamePriceAndLink()
    {
        var results = await Actor.AsksForAsync(new TheResultsShown());
        await Assert.That(results).IsNotEmpty();

        var incomplete = results
            .Where(r => r.Name.Length == 0 || r.Price.Length == 0 || string.IsNullOrEmpty(r.Href))
            .Select(r => r.Name)
            .ToList();
        await Assert.That(incomplete).IsEmpty();
    }

    [Then("every place shown is in that country")]
    public async Task ThenEveryPlaceIsInThatCountry()
    {
        var results = await Actor.AsksForAsync(new TheResultsShown());
        var elsewhere = results.Where(r => !r.Location.Contains(StayInputs.Country, StringComparison.Ordinal))
            .Select(r => $"{r.Name} ({r.Location})")
            .ToList();
        await Assert.That(elsewhere).IsEmpty();
    }
}
