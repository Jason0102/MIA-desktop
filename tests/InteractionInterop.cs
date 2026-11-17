using System.Text.Json;
using Mia.Services;

internal static class InteractionInterop
{
    public static int Run(string directory, string identity, string operation)
    {
        var memory = new InteractionMemory(directory, identity);
        switch (operation)
        {
            case "record":
                memory.Record(new InteractionEvent("windows-fixture", DateTimeOffset.UtcNow,
                    "PC", "chat", Topics: ["桌面測試"]));
                break;
            case "read":
                Console.OutputEncoding = System.Text.Encoding.UTF8;
                Console.WriteLine(JsonSerializer.Serialize(memory.Read()));
                break;
            case "reset":
                try { memory.Reset(); }
                catch (InvalidOperationException) { return 3; }
                break;
            case "exclude":
                memory.ExcludeTopic("混合話題");
                break;
            case "pause":
                memory.Configure(false, 30);
                break;
            case "resume":
                memory.Configure(true, 30);
                break;
            default:
                return 2;
        }
        return 0;
    }
}
