using PineLoader;

namespace HelloMod;

public sealed class HelloMod : IPineMod
{
    public void Load(IPineHost host)
    {
        host.Log("HelloMod loaded.");
    }
}
