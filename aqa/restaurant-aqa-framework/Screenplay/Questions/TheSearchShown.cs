using Restaurant_Aqa_Framework.Ui;

namespace Restaurant_Aqa_Framework.Screenplay.Questions;

/// <summary>What the search field shows and the address the visitor is on.</summary>
public sealed record SearchShown(string Location, string Url);

public sealed class TheSearchShown : IQuestion<SearchShown>
{
    public async Task<SearchShown> AnsweredByAsync(Actor actor)
    {
        var page = actor.LookingAtThePage();
        return new SearchShown(await new StaySearchPage(page).SearchField.InputValueAsync(), page.Url);
    }
}
