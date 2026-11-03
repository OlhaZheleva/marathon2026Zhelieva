using System.Net;
using Restaurant_Aqa_Framework.Screenplay.Abilities;

namespace Restaurant_Aqa_Framework.Screenplay.Questions;

/// <summary>The cookie consent categories saved in the browser (category to "yes"/"no"); empty when none is saved.</summary>
public sealed class TheCookieConsent : IQuestion<IReadOnlyDictionary<string, string>>
{
    public async Task<IReadOnlyDictionary<string, string>> AnsweredByAsync(Actor actor)
    {
        var cookies = await actor.AbilityTo<IBrowseTheWeb>().Context.CookiesAsync();
        var consent = cookies.FirstOrDefault(c => c.Name == "cookieyes-consent");
        if (consent is null)
            return new Dictionary<string, string>();

        return WebUtility.UrlDecode(consent.Value)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split(':', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0], pair => pair[1]);
    }
}
