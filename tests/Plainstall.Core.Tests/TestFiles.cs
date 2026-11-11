namespace Plainstall.Core.Tests;

/// <summary>Locations of the repo files that the test project copies next to its binaries.</summary>
internal static class TestFiles
{
    public static string CatalogDirectory => Path.Combine(AppContext.BaseDirectory, "CatalogFiles");

    public static string Schema(string name)
    {
        return Path.Combine(AppContext.BaseDirectory, "Schemas", name);
    }

    public static string CatalogFile(string name)
    {
        return Path.Combine(AppContext.BaseDirectory, "CatalogFiles", name);
    }

    public static IEnumerable<string> ExampleProfiles()
    {
        return Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "TestData", "Profiles"),
            "*.plainstall.json");
    }
}

/// <summary>A temp folder that is removed when the test ends.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "plainstall-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp folder must not fail a test.
        }
    }

    public string File(string name)
    {
        return System.IO.Path.Combine(Path, name);
    }

    public string Combine(params string[] parts)
    {
        return System.IO.Path.Combine([Path, .. parts]);
    }

    /// <summary>Writes a file below the folder and creates its parent folders. The default content is four bytes.</summary>
    public string WriteFile(string relativePath, byte[]? content = null)
    {
        var full = Combine(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, content ?? [1, 2, 3, 4]);
        return full;
    }
}