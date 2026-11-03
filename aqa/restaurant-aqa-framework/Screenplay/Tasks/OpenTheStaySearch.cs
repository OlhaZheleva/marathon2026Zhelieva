using Restaurant_Aqa_Framework.Ui;

namespace Restaurant_Aqa_Framework.Screenplay.Tasks;

public sealed class OpenTheStaySearch : ITask
{
    public Task PerformAsAsync(Actor actor) => new StaySearchPage(actor.LookingAtThePage()).NavigateAsync();
}
