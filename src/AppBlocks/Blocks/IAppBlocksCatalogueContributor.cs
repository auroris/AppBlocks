using System.Security.Claims;

namespace AppBlocks.Blocks;

/// <summary>Adds companion-specific choices to the backoffice block catalogue.</summary>
public interface IAppBlocksCatalogueContributor
{
    Task PopulateAsync(IDictionary<string, object> catalogue, ClaimsPrincipal user);
}
