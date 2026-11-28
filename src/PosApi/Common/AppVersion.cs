namespace PosApi.Common;

/// <summary>The release number, e.g. 1.27: major from the repository's VERSION file, minor = the build number (version.txt in the package).</summary>
public static class AppVersion
{
    public static readonly string Number = Read();

    private static string Read()
    {
        try
        {
            var file = Path.Combine(AppContext.BaseDirectory, "version.txt");
            if (File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } v) return v;
        }
        catch (IOException) { }
        return "dev";
    }
}
