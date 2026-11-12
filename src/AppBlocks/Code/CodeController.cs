using AppBlocks.Authentication;
using AppBlocks.Code;
using AppBlocks.DataSources;
using AppBlocks.Samples;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Web.Common.Authorization;

// Public namespace retained for source compatibility; the file lives with its owning feature.
namespace AppBlocks.Management;

public sealed record CodeFileRequest(string Path, string? Content = null);
public sealed record CodeRenameRequest(string From, string To);
public sealed record CodeRecipeRequest(string Connection, string Table, string Recipe, string Access, string ClassName, string Alias,
    string[]? ReadMemberGroups = null, string[]? ReadUserGroups = null, string[]? WriteMemberGroups = null, string[]? WriteUserGroups = null,
    bool AllowAnonymousWrites = false, string? Folder = null, string? DataSource = null);

/// <summary>
/// The C# section: the folder's files, the compiler's messages and the New file wizard. Saving code here runs it inside the site, so every
/// endpoint checks AppBlocks administration as well as Umbraco's Settings section access, every write is refused when the site has no
/// compiler, and every save, create, rename and delete goes to Umbraco's audit log.
/// </summary>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("appblocks/code")]
[ApiExplorerSettings(GroupName = "AppBlocks")]
[Authorize(Policy = AuthorizationPolicies.SectionAccessSettings)]
public sealed class CodeController(CodeCompilation compilation, ApplicationAccessPolicy access, IAuditService audit, IBackOfficeSecurityAccessor security,
    DataSourceRuntime runtime, DataSourceCatalog catalog, IMemberGroupService memberGroups, IUserGroupService userGroups, IHostEnvironment environment,
    ILogger<CodeController> logger) : ManagementApiControllerBase
{
    private const string AuditEntity = "AppBlocks C#";
    private CodeFolder Folder => compilation.Folder;

    [HttpGet("access")]
    public async Task<IActionResult> Access()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(new { allowed = await access.IsBackOfficeAdministratorAsync(User) });
    }

    [HttpGet]
    public Task<IActionResult> State() => Run(() => Task.FromResult<IActionResult>(Ok(Snapshot(compilation.Status))));

    [HttpGet("file")]
    public Task<IActionResult> Read(string path, CancellationToken ct) => Run(async () => Ok(new { path = CodeFolder.Normalize(path, file: true), content = await Folder.ReadAsync(path, ct) }));

    [HttpPost("file")]
    public Task<IActionResult> Save(CodeFileRequest request, CancellationToken ct) => Write(async () => {
        await Folder.WriteAsync(request.Path, request.Content ?? "", create: false, ct);
        return await Changed(AuditType.Save, "Saved", request.Path, ct);
    });

    [HttpPost("file/new")]
    public Task<IActionResult> Create(CodeFileRequest request, CancellationToken ct) => Write(async () => {
        await Folder.WriteAsync(request.Path, request.Content ?? "", create: true, ct);
        return await Changed(AuditType.New, "Created", request.Path, ct);
    });

    [HttpPost("folder")]
    public Task<IActionResult> CreateFolder(CodeFileRequest request, CancellationToken ct) => Write(async () => {
        Folder.CreateFolder(request.Path);
        return await Changed(AuditType.New, "Created the folder", request.Path, ct);
    });

    [HttpPost("rename")]
    public Task<IActionResult> Rename(CodeRenameRequest request, CancellationToken ct) => Write(async () => {
        Folder.Rename(request.From, request.To);
        return await Changed(AuditType.Move, "Renamed " + CodeFolder.Normalize(request.From, request.From.EndsWith(".cs", StringComparison.Ordinal)) + " to", request.To, ct);
    });

    [HttpPost("delete")]
    public Task<IActionResult> Delete(CodeFileRequest request, CancellationToken ct) => Write(async () => {
        Folder.Delete(request.Path);
        return await Changed(AuditType.Delete, "Deleted", request.Path, ct);
    });

    [HttpPost("compile")]
    public Task<IActionResult> Compile(CancellationToken ct) => Write(async () => Ok(Snapshot(await compilation.CompileAsync("compile requested from the backoffice", ct, force: true))));

    /// <summary>The Member Groups by name and User Groups by alias the wizard's access step offers.</summary>
    [HttpGet("wizard/groups")]
    public Task<IActionResult> Groups(CancellationToken ct) => Run(async () => Ok(new {
        memberGroups = (await memberGroups.GetAllAsync()).Select(g => g.Name).OfType<string>().Order(StringComparer.OrdinalIgnoreCase),
        userGroups = (await userGroups.GetAllAsync(0, 1000)).Items.Select(g => new { alias = g.Alias, name = g.Name }).OrderBy(g => g.name, StringComparer.OrdinalIgnoreCase),
    }));

    /// <summary>The published SQL data sources that read <paramref name="table"/> through <paramref name="connection"/>, for a backing class.</summary>
    [HttpGet("wizard/data-sources")]
    public Task<IActionResult> DataSources(string connection, string table) => Run(async () => {
        await catalog.LoadAsync();
        var wanted = TableName.Parse(table);
        return Ok(catalog.DataSources.Where(source => source.Definition.Connection.Equals(connection, StringComparison.OrdinalIgnoreCase)
                && wanted is not null && TableName.Parse(source.Definition.Source) is { } read && read.Name.Equals(wanted.Name, StringComparison.OrdinalIgnoreCase)
                && (read.Schema is null || wanted.Schema is null || read.Schema.Equals(wanted.Schema, StringComparison.OrdinalIgnoreCase)))
            .Select(source => new { alias = source.Alias, name = source.Definition.Name, handler = source.Definition.Handler, fields = source.FieldColumns }));
    });

    [HttpGet("wizard/suggest")]
    public Task<IActionResult> Suggest(string table, string recipe) => Run(() => {
        var (className, alias) = CodeRecipes.Suggest(table, ParseKind(recipe));
        return Task.FromResult<IActionResult>(Ok(new { className, alias }));
    });

    /// <summary>The class the wizard would write, and where; nothing is saved until the client creates the file.</summary>
    [HttpPost("wizard/preview")]
    public Task<IActionResult> Preview(CodeRecipeRequest request, CancellationToken ct) => Run(async () => {
        var recipe = new CodeRecipe {
            Connection = request.Connection, Table = request.Table, Kind = ParseKind(request.Recipe), ClassName = request.ClassName?.Trim() ?? "", Alias = request.Alias?.Trim() ?? "",
            Access = request.Access switch { "public" => DataSourceAccess.Public, "specificGroups" => DataSourceAccess.SpecificGroups, _ => DataSourceAccess.NotPublic },
            ReadMemberGroups = request.ReadMemberGroups ?? [], ReadUserGroups = request.ReadUserGroups ?? [], WriteMemberGroups = request.WriteMemberGroups ?? [],
            WriteUserGroups = request.WriteUserGroups ?? [], AllowAnonymousWrites = request.AllowAnonymousWrites, DataSource = request.DataSource,
        };
        if (CodeRecipes.Validate(recipe) is { Count: > 0 } errors) return Problem(string.Join(" ", errors), statusCode: 422);
        var path = CodeFolder.Normalize(CodeRecipes.FilePath(recipe, request.Folder), file: true);
        var (schema, dialect) = await TableAsync(recipe.Connection, recipe.Table, ct);
        IReadOnlyList<DataSourceFieldColumn>? fields = null;
        if (recipe.Kind == CodeRecipeKind.BackingClass && recipe.DataSource is { Length: > 0 } alias)
        {
            await catalog.LoadAsync();
            fields = catalog.DataSources.FirstOrDefault(source => source.Alias == alias)?.FieldColumns is { Count: > 0 } known ? known : null;
        }
        var content = recipe.Kind == CodeRecipeKind.FormClass ? CodeRecipes.FormClass(recipe, schema, dialect) : CodeRecipes.BackingClass(recipe, schema, dialect, fields);
        return Ok(new { path, content });
    });

    private static CodeRecipeKind ParseKind(string? recipe) => recipe == "backingClass" ? CodeRecipeKind.BackingClass : CodeRecipeKind.FormClass;

    /// <summary>The table as the connection's catalog describes it, and the SQL the connection speaks.</summary>
    private async Task<(TableSchema Table, SqlDialect Dialect)> TableAsync(string connection, string table, CancellationToken ct)
    {
        var errors = new List<string>();
        var dialect = runtime.Dialect(connection, errors) ?? throw new CodeRecipeException(string.Join(" ", errors));
        var name = TableName.Parse(table) ?? throw new CodeRecipeException("Choose a table or view.");
        await using var open = await runtime.OpenAsync(connection, ct);
        if (dialect.Kind == Connections.DatabaseDialect.Oracle && int.TryParse(open.ServerVersion.Split('.')[0], out var major) && major < 12) dialect = OracleDialect.Legacy;
        return (dialect.ReadTable(open, name) ?? throw new CodeRecipeException($"The connection {connection} has no table or view {table}."), dialect);
    }

    private object Snapshot(CodeStatus status) => new {
        canEdit = compilation.CanCompile, package = status.Package,
        folder = Path.GetRelativePath(environment.ContentRootPath, Folder.Root).Replace(Path.DirectorySeparatorChar, '/'),
        entries = Folder.List().Select(e => new { path = e.Path, isFolder = e.IsFolder, size = e.Size, modified = e.Modified }),
        compile = new { status.Succeeded, status.Attempted, status.Generation, status.Compiled, status.Diagnostics, status.Classes },
    };

    private async Task<IActionResult> Changed(AuditType type, string verb, string path, CancellationToken ct)
    {
        var user = security.BackOfficeSecurity?.CurrentUser;
        var shown = CodeFolder.Normalize(path, path.EndsWith(".cs", StringComparison.Ordinal));
        var comment = $"{verb} {shown} in the AppBlocks C# folder";
        await audit.AddAsync(type, user?.Key ?? Guid.Empty, -1, AuditEntity, comment[..Math.Min(comment.Length, 255)]);
        var reason = char.ToLowerInvariant(comment[0]) + comment[1..];
        logger.LogInformation("{User} {Comment}.", user?.Username ?? "A backoffice user", reason);
        compilation.Begin();
        return Ok(Snapshot(await compilation.CompileAsync(reason, ct)));
    }

    /// <summary>A read: the section's own access, and refusals as the editor sees them.</summary>
    private async Task<IActionResult> Run(Func<Task<IActionResult>> action)
    {
        Response.Headers.CacheControl = "no-store";
        if (!await access.IsBackOfficeAdministratorAsync(User)) return Forbid();
        try { return await action(); }
        catch (CodePathException error) { return Problem(error.Message, statusCode: 422); }
        catch (CodeRecipeException error) { return Problem(error.Message, statusCode: 422); }
        catch (FileNotFoundException error) { return Problem(error.Message, statusCode: 404); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(error, "The C# folder could not be changed.");
            return Problem("The C# folder could not be changed: " + error.Message, statusCode: 422);
        }
        catch (Exception error) when (error is System.Data.Common.DbException or Connections.DatabaseDrivers.MissingDriverException)
        {
            logger.LogWarning(error, "The wizard could not read the table.");
            return Problem("The table could not be read. Check the connection and its database permissions.", statusCode: 422);
        }
    }

    /// <summary>A write, which a site without a compiler refuses: its section is read-only.</summary>
    private Task<IActionResult> Write(Func<Task<IActionResult>> action) => Run(() => compilation.CanCompile ? action()
        : Task.FromResult<IActionResult>(Problem($"The C# section is read-only on this site: add the {RoslynCompiler.Package} package to edit and compile code here.", statusCode: 409)));
}
