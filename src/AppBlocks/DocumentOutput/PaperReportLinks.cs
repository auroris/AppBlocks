namespace AppBlocks.DocumentOutput;

/// <summary>What a form or report block's Paper report setting holds, which the core, the reports companion and the documents companion share.</summary>
public static class PaperReportLinks
{
    /// <summary>The document type of a Paper report node, the only node that <c>/api/paper-reports/{key}</c> answers for.</summary>
    public const string ContentTypeAlias = "paperReport";

    public static Guid? PickedKey(string value)
    {
        if (Guid.TryParse(value, out var key)) return key;
        const string prefix = "umb://document/";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Guid.TryParse(value[prefix.Length..], out key) ? key : null;
    }

}
