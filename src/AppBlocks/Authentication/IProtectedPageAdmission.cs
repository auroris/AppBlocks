using System.Security.Claims;
using Umbraco.Cms.Core.Models.PublishedContent;

namespace AppBlocks.Authentication;

/// <summary>
/// A host's rule for a page Umbraco's Public Access protects, asked only once Umbraco has refused the request: there is no
/// Member session the page admits. Answer the authenticated principal to treat as the request's person, or null to let
/// Umbraco's refusal stand. The same answer governs the page itself and every block endpoint on it, so a page a host shows
/// also works. AppBlocks registers no rule of its own, and a request with a Member session Umbraco refused is never asked
/// about: a refused Member is not replaced by another account. UmbracoAppHost's rule admits a backoffice User who may edit
/// the page. Register as many as the site needs; the first one that answers decides.
/// </summary>
public interface IProtectedPageAdmission
{
    Task<ClaimsPrincipal?> AdmitAsync(HttpContext context, IPublishedContent page, CancellationToken ct);
}
