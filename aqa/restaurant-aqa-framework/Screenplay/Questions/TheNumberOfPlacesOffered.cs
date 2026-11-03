using System.Globalization;
using System.Text.RegularExpressions;
using Restaurant_Aqa_Framework.Ui;

namespace Restaurant_Aqa_Framework.Screenplay.Questions;

/// <summary>The number in the "N places" label above the results.</summary>
public sealed partial class TheNumberOfPlacesOffered : IQuestion<int>
{
    public async Task<int> AnsweredByAsync(Actor actor)
    {
        var text = await new StaySearchPage(actor.LookingAtThePage()).PlacesCount.TextContentAsync() ?? string.Empty;
        var digits = Digits().Replace(text, string.Empty);
        return int.Parse(digits, CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"\D")]
    private static partial Regex Digits();
}
