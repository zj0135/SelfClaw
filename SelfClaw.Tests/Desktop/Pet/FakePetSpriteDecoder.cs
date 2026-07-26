using System.Windows.Media.Imaging;
using SelfClaw.Desktop.Pet;

namespace SelfClaw.Tests.Desktop.Pet;

internal sealed class FakePetSpriteDecoder(Func<string, BitmapSource> load) : IPetSpriteDecoder
{
    public BitmapSource Load(string path) => load(path);
}
