using AppBlocks.DocumentOutput;

namespace AppBlocks.Documents;

public sealed record PressroomConfigurationInfo(Guid Id, string Name, string Url, bool HasApiKey, string Output, double TimeoutSeconds);
public sealed record PressroomConfigurationList(IReadOnlyList<PressroomConfigurationInfo> Configurations, bool Saved);
/// <summary>Output is "pdf" or "printer:" followed by a printer identifier returned by discovery.</summary>
public sealed record SavePressroomConfiguration(Guid? Id, string Name, string Url, string Output = "pdf",
    string? ApiKey = null, bool ClearApiKey = false, double TimeoutSeconds = 65);
public sealed record DiscoverPressroomPrinters(Guid? Id, string Url, string? ApiKey = null,
    bool ClearApiKey = false, double TimeoutSeconds = 65);
public sealed record PressroomPrinterList(IReadOnlyList<string> Printers);

internal sealed record StoredPressroomConfiguration(Guid Id, string Name, string Url, string? ProtectedApiKey,
    string Output, double TimeoutSeconds);
internal sealed record StoredPressroomConfigurations(int Version, List<StoredPressroomConfiguration> Configurations);
internal sealed record ResolvedPressroomConfiguration(Guid Id, string Name, string Url, string? ApiKey,
    string Output, double TimeoutSeconds)
{
    public string? Printer => Output.StartsWith("printer:", StringComparison.Ordinal) ? Output[8..] : null;
    public DocumentDestination Destination => new(Id, Name, Printer is null ? DocumentOutputKind.Pdf : DocumentOutputKind.Printer, Printer);
}
