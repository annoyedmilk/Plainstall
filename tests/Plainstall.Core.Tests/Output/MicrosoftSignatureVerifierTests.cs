using System.Runtime.InteropServices;
using Plainstall.Core.Output;

namespace Plainstall.Core.Tests.Output;

/// <summary>
///     These run against files that every Windows installation has, so they need no test data, no
///     network and no admin rights.
/// </summary>
public class MicrosoftSignatureVerifierTests
{
    private static readonly MicrosoftSignatureVerifier Verifier = new();

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("dism.exe")]
    [InlineData("kernel32.dll")]
    public void Windows_system_file_is_trusted(string name)
    {
        var path = Path.Combine(Environment.SystemDirectory, name);

        var result = Verifier.Verify(path);

        Assert.True(result.IsTrusted, result.Detail);
        Assert.Contains("O=Microsoft Corporation", result.Signer);
        TestContext.Current.TestOutputHelper?.WriteLine($"{name}: via catalog = {result.ViaCatalog}; {result.Detail}");
    }

    [Fact]
    public void File_with_an_embedded_microsoft_signature_is_trusted()
    {
        // The .NET runtime's own files carry their signature inside the file.
        var path = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "coreclr.dll");
        Assert.SkipUnless(File.Exists(path), "coreclr.dll was not found next to the runtime.");

        var result = Verifier.Verify(path);

        Assert.True(result.IsTrusted, result.Detail);
        Assert.False(result.ViaCatalog);
        Assert.Contains("O=Microsoft Corporation", result.Signer);
    }

    [Fact]
    public void Unsigned_file_is_not_trusted()
    {
        using var folder = new TempDirectory();
        var path = folder.WriteFile("tool.exe", [.. "MZ not really a program"u8]);

        var result = Verifier.Verify(path);

        Assert.False(result.IsTrusted);
        Assert.Null(result.Signer);
        Assert.NotEmpty(result.Detail);
    }

    [Fact]
    public void Modified_copy_of_a_signed_file_is_not_trusted()
    {
        using var folder = new TempDirectory();
        var bytes = File.ReadAllBytes(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "coreclr.dll"));
        // Change one byte in the middle of the code: the signature stays in place but no longer fits.
        bytes[bytes.Length / 2] ^= 0xFF;
        var path = folder.WriteFile("coreclr.dll", bytes);

        var result = Verifier.Verify(path);

        Assert.False(result.IsTrusted, result.Detail);
    }

    [Fact]
    public void Unmodified_copy_of_a_catalog_signed_file_is_still_trusted_elsewhere()
    {
        // A catalog signature belongs to the content, not to the location. That is what makes it usable for an ADK tool.
        using var folder = new TempDirectory();
        var path = folder.Combine("copy of cmd.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), path);

        var result = Verifier.Verify(path);

        Assert.True(result.IsTrusted, result.Detail);
    }

    [Fact]
    public void Missing_file_is_not_trusted()
    {
        using var folder = new TempDirectory();

        var result = Verifier.Verify(folder.Combine("nothing.exe"));

        Assert.False(result.IsTrusted);
    }

    [Fact]
    public void File_signed_by_someone_else_is_not_trusted()
    {
        // Needs a validly signed program that is not from Microsoft. Git for Windows is one that developer PCs often have.
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd",
            "git.exe");
        Assert.SkipUnless(File.Exists(path),
            "Git for Windows is not installed, so there is no third-party signed file to test with.");

        var result = Verifier.Verify(path);

        Assert.False(result.IsTrusted, result.Detail);
        Assert.NotNull(result.Signer);
        Assert.DoesNotContain("Microsoft", result.Signer);
        TestContext.Current.TestOutputHelper?.WriteLine(result.Detail);
    }
}