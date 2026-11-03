using Restaurant_Aqa_Framework.Ui.Components;

namespace Restaurant_Aqa_Framework.Screenplay.Questions;

public sealed class TheCookieBannerShown : IQuestion<bool>
{
    public Task<bool> AnsweredByAsync(Actor actor) =>
        new CookieDialogComponent(actor.LookingAtThePage()).Banner.IsVisibleAsync();
}
