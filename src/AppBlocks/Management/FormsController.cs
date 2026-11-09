using AppBlocks.Samples;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using AppBlocks.Authentication;
using Microsoft.AspNetCore.Authorization;

namespace AppBlocks.Management;

[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("appblocks/forms")]
[ApiExplorerSettings(GroupName = "AppBlocks")]
[Authorize(Policy = AppBlocksAuthorization.AdministratorPolicy)]
public sealed class FormsController(FormInventory inventory) : ManagementApiControllerBase
{
    // TODO(merge): FormInventory.ReadAsync comes with fix/forms (316dd25); this does not compile on fix/setup alone.
    [HttpGet]
    public async Task<IActionResult> List()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(await inventory.ReadAsync());
    }

    [HttpGet("{page:guid}/{form:guid}/sample")]
    public async Task<IActionResult> Sample(Guid page, Guid form, string? culture = null)
    {
        Response.Headers.CacheControl = "no-store";
        var selected = (await inventory.ReadAsync(page)).Forms.SingleOrDefault(f => f.FormKey == form && string.Equals(f.Culture, culture, StringComparison.OrdinalIgnoreCase));
        return selected is null ? NotFound() : Content(FormSampleGenerator.Generate(selected), "text/plain; charset=utf-8");
    }
}
