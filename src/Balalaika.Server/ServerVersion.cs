using System.Reflection;

namespace Balalaika.Server;

public static class ServerVersion
{
    public static string Current { get; } = Read();

    private static string Read()
    {
        var assembly = typeof(ServerVersion).Assembly;
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info))
            return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }
}
