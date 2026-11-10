namespace silaa.Services;

internal static class AppPaths
{
    public static string DataRoot
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("SILAA_DATA");
            return string.IsNullOrWhiteSpace(custom)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "silaa")
                : Path.GetFullPath(custom);
        }
    }
}
