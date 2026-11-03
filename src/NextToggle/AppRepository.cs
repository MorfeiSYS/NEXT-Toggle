namespace NextToggle;

/// <summary>GitHub repository used for releases, issues, and in-app updates.</summary>
public static class AppRepository
{
    public const string Slug = "MorfeiSYS/NEXT-Toggle";

    public static Uri Home => new($"https://github.com/{Slug}");

    public static Uri ReleasesLatest => new($"https://github.com/{Slug}/releases/latest");

    public static string LatestReleaseApi => $"https://api.github.com/repos/{Slug}/releases/latest";

    public static string DownloadPrefix => $"https://github.com/{Slug}/releases/download/";

    public static string ReleasesPage => $"https://github.com/{Slug}/releases";
}
