using Tapline.Model;

namespace Tapline.UI.Pages;

sealed class RoomPage : PageBase
{
    public RoomPage(AppModel model) : base(model)
    {
        Content = Ui.Page(Ui.VStack(16, Ui.Title("Room")));
    }
}
