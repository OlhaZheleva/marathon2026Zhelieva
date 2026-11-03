using Restaurant_Aqa_Framework.Ui;

namespace Restaurant_Aqa_Framework.Screenplay.Questions;

/// <summary>One listing tile as the visitor sees it.</summary>
public sealed record ResultShown(string Name, string Location, string Price, string? Href);

/// <summary>The listing tiles currently loaded in the results.</summary>
public sealed class TheResultsShown : IQuestion<IReadOnlyList<ResultShown>>
{
    public async Task<IReadOnlyList<ResultShown>> AnsweredByAsync(Actor actor)
    {
        var cards = await new StaySearchPage(actor.LookingAtThePage()).ResultCardsAsync();
        var results = new List<ResultShown>();
        foreach (var card in cards)
        {
            results.Add(new ResultShown(
                (await card.Name.TextContentAsync())?.Trim() ?? string.Empty,
                (await card.Location.TextContentAsync())?.Trim() ?? string.Empty,
                (await card.Price.First.TextContentAsync())?.Trim() ?? string.Empty,
                await card.Link.GetAttributeAsync("href")));
        }

        return results;
    }
}
