using PineLoader;

namespace HelloMod;

public sealed class HelloMod : IPineMod
{
    public void Load(IPineHost host)
    {
        host.Log("HelloMod loaded.");
        host.WhileGameRunning(game => host.Log("Game is running. Process " + game.Id));
    }
}
