using System.Windows.Media.Imaging;

namespace SelfClaw.Desktop.Pet;

internal interface IPetSpriteDecoder
{
    BitmapSource Load(string path);
}
