namespace StoryboardStudio.Api.Services;

internal static class StudioPaths
{
    public static string ResolveDataRoot(IConfiguration configuration, IWebHostEnvironment environment)
        => Resolve(configuration["Studio:DataRoot"], environment.ContentRootPath, "App_Data");

    public static string ResolveAssetRoot(IConfiguration configuration, IWebHostEnvironment environment)
    {
        var configured = configuration["Studio:AssetRoot"];
        return string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(Path.Combine(ResolveDataRoot(configuration, environment), "assets"))
            : Resolve(configured, environment.ContentRootPath);
    }

    private static string Resolve(string? configured, string contentRoot, params string[] fallbackSegments)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(fallbackSegments.Aggregate(contentRoot, (current, segment) => Path.Combine(current, segment)));
        return Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(contentRoot, configured));
    }
}
