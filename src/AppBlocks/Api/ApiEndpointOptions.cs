namespace AppBlocks.Api;

/// <summary>The <c>AppBlocks:ApiEndpoints</c> configuration section: limits on the routes editors give API endpoints.</summary>
public sealed class ApiEndpointOptions
{
    public const string Section = "AppBlocks:ApiEndpoints";

    /// <summary>The most segments a route may have, <see cref="ApiRouteTemplate.DefaultMaxSegments"/> unless the site allows more.</summary>
    public int MaxRouteSegments { get; set; } = ApiRouteTemplate.DefaultMaxSegments;
}
