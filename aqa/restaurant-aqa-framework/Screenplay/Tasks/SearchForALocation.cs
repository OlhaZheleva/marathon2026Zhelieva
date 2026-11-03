using Restaurant_Aqa_Framework.Ui;

namespace Restaurant_Aqa_Framework.Screenplay.Tasks;

public sealed class SearchForALocation(string location) : ITask
{
    public Task PerformAsAsync(Actor actor) => new StaySearchPage(actor.LookingAtThePage()).SearchForLocationAsync(location);
}
