using System.IO;

namespace Tartarus;

/// <summary>
/// Where the program's own files (models/..., the dataset) are looked for: the program's folder, then, when it runs
/// from a build in the source tree (&lt;root&gt;\src|tools|tests\&lt;project&gt;\bin\...), the folders above it up to the
/// repository's root and never past it. A released program has its models beside it and looks nowhere else.
/// One source file, compiled into each project that needs it (internal there).
/// </summary>
internal static class AppFiles
{
    /// <summary>The repository's root (holding TartarusSolver.sln) when running from a build in it, else null.</summary>
    public static string? Root { get; } = FindRoot();

    /// <summary>The program's folder, then each one above it up to <see cref="Root"/>.</summary>
    public static IEnumerable<string> Folders
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                yield return dir.FullName;
                if (Root == null || Same(dir.FullName, Root)) yield break;
            }
        }
    }

    /// <summary>The first existing file or folder at <paramref name="relative"/> in <see cref="Folders"/>, or null.</summary>
    public static string? Find(params string[] relative) =>
        Folders.Select(f => Path.Combine([f, .. relative])).FirstOrDefault(p => File.Exists(p) || Directory.Exists(p));

    private static string? FindRoot()
    {
        // Only the path's own names are walked: the root is three above the project's bin folder.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (dir.Name.Equals("bin", StringComparison.OrdinalIgnoreCase))
                return dir.Parent?.Parent?.Parent is { } root && File.Exists(Path.Combine(root.FullName, "TartarusSolver.sln")) ? root.FullName : null;
        return null;
    }

    private static bool Same(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);
}
