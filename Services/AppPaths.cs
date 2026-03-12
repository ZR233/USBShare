namespace USBShare.Services;

public static class AppPaths
{
    public static string BaseDirectory
    {
        get
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "USBShare");

            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string ConfigFilePath => Path.Combine(BaseDirectory, "config.json");

    public static string SecretsDirectory
    {
        get
        {
            var path = Path.Combine(BaseDirectory, "secrets");
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
