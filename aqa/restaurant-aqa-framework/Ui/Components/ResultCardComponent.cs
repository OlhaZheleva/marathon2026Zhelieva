using Microsoft.Playwright;

namespace Restaurant_Aqa_Framework.Ui.Components;

/// <summary>One listing tile in the catalog results. The tile is a link with no role-based hook of its own, so CSS is used.</summary>
public sealed class ResultCardComponent(ILocator root)
{
    public ILocator Name => root.Locator(".product-card__product-name");
    public ILocator Location => root.Locator(".simple-list").First;
    public ILocator Price => root.Locator(".accented-price__value");
    public ILocator Link => root;
}
