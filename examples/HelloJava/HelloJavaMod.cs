using PineLoader;

namespace HelloJava;

public sealed class HelloJavaMod : IPineMod
{
    public void Load(IPineHost host)
    {
        var folder = Path.GetDirectoryName(typeof(HelloJavaMod).Assembly.Location) ?? "";
        var jar = Path.Combine(folder, "hello-java-agent.jar");
        var log = Path.Combine(host.Root, "Logs", "hello-java.txt");
        host.UseJavaAgent(jar, log);
        host.Log("HelloJava registered the agent.");
    }
}
