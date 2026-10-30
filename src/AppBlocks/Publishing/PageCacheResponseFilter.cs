using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.WebUtilities;

namespace UmbracoAppHost.Website;

/// <summary>Prevents Razor output from reaching the server when a layout returns Not Modified.</summary>
internal sealed class PageCacheResponseFilter : IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is not ViewResult and not PartialViewResult)
        {
            await next();
            return;
        }

        // Layouts compute cache validators after rendering their child views. Returning from a
        // layout still leaves buffered Razor whitespace for MVC to flush, which Kestrel rejects
        // for a 304. Hold view output until its final status is known; large views spill to disk.
        var response = context.HttpContext.Response;
        var originalBody = response.Body;
        await using var buffer = new FileBufferingWriteStream();
        response.Body = buffer;
        ResultExecutedContext executed;
        try { executed = await next(); }
        finally { response.Body = originalBody; }

        if (executed.Exception is not null && !executed.ExceptionHandled) return;
        if (response.StatusCode == StatusCodes.Status304NotModified)
        {
            response.ContentLength = null;
            response.ContentType = null;
            return;
        }

        await buffer.DrainBufferAsync(originalBody, context.HttpContext.RequestAborted);
    }
}
