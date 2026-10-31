namespace AppBlocks.DocumentOutput;

public static class PaperReportLinks
{
    public static Guid? PickedKey(string value)
    {
        if (Guid.TryParse(value, out var key)) return key;
        const string prefix = "umb://document/";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Guid.TryParse(value[prefix.Length..], out key) ? key : null;
    }

}
