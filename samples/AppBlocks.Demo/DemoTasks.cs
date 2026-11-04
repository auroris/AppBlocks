using System.Text.Json;
using AppBlocks.Connections;
using AppBlocks.DataSources;
using AppBlocks.Forms;

/// <summary>
/// The demo's C# form class owns its persistence and delegates transactions to the SQL resource engine. On a page with no
/// Public Access an anonymous visitor reaches this class, so every operation starts by requiring a sign-in. The engine refuses
/// them too; the explicit check is here so that code copied from this class is not open by accident when its engine is replaced.
/// </summary>
public sealed class DemoTasks(DataSourceRuntime runtime, IDataSourceUserResolver users, IEnumerable<IDataSourcePolicy> policies) : IFormResource
{
    private readonly SqlDataSource records = new(new DataSourceDefinition {
        Key = DemoSeed.Key("tasks-class"), Name = "Tasks", Alias = "demo.tasks",
        Connection = ConnectionCatalog.ApplicationConnection, Source = "AppBlocksDemoTasks",
        Access = DataSourceAccess.NotPublic,
        KeyColumns = ["id"], AllowInsert = true, AllowUpdate = true,
    }, runtime, users, policies);

    public string Alias => records.Alias;
    public string ContractVersion => records.ContractVersion;
    public IReadOnlyList<FormField> Fields => records.Fields;
    private static bool SignedIn(FormExecutionContext context) => context.Member.Identity?.IsAuthenticated == true;
    private static readonly Task<FormResult> Refused = Task.FromResult(FormResult.Denied());

    public Task<FormResult> InitializeAsync(FormExecutionContext context, CancellationToken ct) => !SignedIn(context) ? Refused : records.InitializeAsync(context, ct);
    public Task<FormResult> LoadAsync(string key, FormExecutionContext context, CancellationToken ct) => !SignedIn(context) ? Refused : records.LoadAsync(key, context, ct);
    public Task<FormResult> CreateAsync(JsonElement input, FormExecutionContext context, CancellationToken ct) => !SignedIn(context) ? Refused : records.CreateAsync(input, context, ct);
    public Task<FormResult> UpdateAsync(string key, JsonElement input, string? version, FormExecutionContext context, CancellationToken ct)
        => !SignedIn(context) ? Refused : records.UpdateAsync(key, input, version, context, ct);
    public Task<FormResult> DeleteAsync(string key, string? version, FormExecutionContext context, CancellationToken ct) => !SignedIn(context) ? Refused : records.DeleteAsync(key, version, context, ct);
}
