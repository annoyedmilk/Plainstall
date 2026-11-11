using System.Text;
using Plainstall.Core.Imaging.Drivers;

namespace Plainstall.Core.Tests.Imaging.Drivers;

/// <summary>Small synthetic INF files. No real driver is copied into the repo.</summary>
internal static class SyntheticInf
{
    public static string Text(
        string infClass = "Net",
        string? classGuid = "{4D36E972-E325-11CE-BFC1-08002BE10318}",
        string? driverVer = "07/18/2024,1.2.3.4",
        string? catalog = "contoso.cat",
        string manufacturerLine = "%Mfg% = Contoso, NTamd64",
        string mfgString = "\"Contoso Ltd.\"")
    {
        var sb = new StringBuilder();
        sb.AppendLine("; A synthetic test driver");
        sb.AppendLine("[Version]");
        sb.AppendLine("Signature   = \"$Windows NT$\"");
        sb.AppendLine($"Class       = {infClass}");
        if (classGuid is not null) sb.AppendLine($"ClassGuid   = {classGuid}");
        sb.AppendLine("Provider    = %Mfg%");
        if (driverVer is not null) sb.AppendLine($"DriverVer   = {driverVer}");
        if (catalog is not null) sb.AppendLine($"CatalogFile = {catalog}");
        sb.AppendLine("PnpLockdown = 1");
        sb.AppendLine();
        sb.AppendLine("[Manufacturer]");
        sb.AppendLine(manufacturerLine);
        sb.AppendLine();
        sb.AppendLine("[Contoso.NTamd64]");
        sb.AppendLine("%Device% = Install, PCI\\VEN_1234&DEV_5678");
        sb.AppendLine();
        sb.AppendLine("[Strings]");
        sb.AppendLine($"Mfg = {mfgString}");
        sb.AppendLine("Device = \"Contoso Device\"");
        return sb.ToString();
    }

    public static byte[] Utf16(string text)
    {
        return [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text)];
    }

    public static string Write(string folder, string fileName, string text)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, fileName);
        File.WriteAllBytes(path, Utf16(text));
        return path;
    }

    public static InfFile Parse(string text)
    {
        return InfFile.Parse(@"C:\drivers\test.inf", Utf16(text));
    }
}

/// <summary>Builds <see cref="DriverPackage" /> values without touching the disk, for the pure selector.</summary>
internal static class TestPackages
{
    public static DriverPackage Make(string folder, string infName = "contoso.inf", string? provider = "Contoso",
        string? driverVer = "07/18/2024,1.0.0.0",
        InfArchitectures architectures = InfArchitectures.Amd64,
        InfArchitectures folderArchitecture = InfArchitectures.None, bool driverInf = true)
    {
        return new DriverPackage
        {
            Folder = folder,
            FolderArchitecture = folderArchitecture,
            Infs =
            [
                new InfFile
                {
                    Path = Path.Combine(folder, infName),
                    HasVersionSection = driverInf,
                    Signature = driverInf ? "$Windows NT$" : null,
                    Class = driverInf ? "Net" : null,
                    Provider = provider,
                    ProviderText = provider,
                    DriverVer = InfDriverVer.TryParse(driverVer),
                    CatalogFile = "x.cat",
                    Architectures = architectures
                }
            ]
        };
    }
}

/// <summary>A process runner that records the request, replays canned output and can create files as DISM would.</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    public List<ProcessRequest> Requests { get; } = [];
    public int ExitCode { get; set; }
    public List<string> Output { get; } = [];
    public Action<ProcessRequest>? SideEffect { get; set; }

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        foreach (var line in Output) request.OnLine?.Invoke(line);
        SideEffect?.Invoke(request);
        return Task.FromResult(new ProcessResult(ExitCode, [.. Output]));
    }
}