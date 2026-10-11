using Tapline.Model;

namespace Tapline.UI.Pages;

sealed class AssistantPage : PageBase
{
    public AssistantPage(AppModel model) : base(model)
    {
        Content = Ui.Page(Ui.VStack(16, Ui.Title("Assistant")));
    }
}
