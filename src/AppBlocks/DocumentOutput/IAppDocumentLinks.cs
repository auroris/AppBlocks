using Microsoft.AspNetCore.Http;

namespace AppBlocks.DocumentOutput;

/// <summary>
/// Optional document capability. Forms and companions resolve it with GetService; when it is absent,
/// they omit document actions. The document endpoint always checks the caller's access again.
/// </summary>
public interface IAppDocumentLinks
{
    string Url(Guid documentKey, PathString pathBase);
}
