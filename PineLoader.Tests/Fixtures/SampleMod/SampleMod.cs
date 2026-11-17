using PineLoader;

namespace SampleMod;

public sealed class SampleMod : IPineMod
{
    public ModLoad Load(IPineHost host)
    {
        return ModLoad.Loaded;
    }
}
