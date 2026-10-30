using System.Text.Json;
using AppBlocks.Connections;
using UmbracoAppHost.DataSources;
using UmbracoAppHost.Forms;

/// <summary>The demo's C# form class owns its persistence and delegates transactions to the SQL resource engine.</summary>
public sealed class DemoTasks(DataSourceRuntime runtime, IDataSourceUserResolver users, IEnumerable<IDataSourcePolicy> policies) : IFormResource
{
    private readonly SqlDataSource records = new(new DataSourceDefinition {
        Key = DemoSeed.Key("tasks-class"), Name = "Tasks", Alias = "demo.tasks",
        Connection = ConnectionCatalog.ApplicationConnection, Source = "AppBlocksDemoTasks",
        KeyColumns = ["id"], AllowInsert = true, AllowUpdate = true,
    }, runtime, users, policies);

    public string Alias => records.Alias;
    public string ContractVersion => records.ContractVersion;
    public IReadOnlyList<FormField> Fields => records.Fields;
    public Task<FormResult> InitializeAsync(FormExecutionContext context, CancellationToken ct) => records.InitializeAsync(context, ct);
    public Task<FormResult> LoadAsync(string key, FormExecutionContext context, CancellationToken ct) => records.LoadAsync(key, context, ct);
    public Task<FormResult> CreateAsync(JsonElement input, FormExecutionContext context, CancellationToken ct) => records.CreateAsync(input, context, ct);
    public Task<FormResult> UpdateAsync(string key, JsonElement input, string version, FormExecutionContext context, CancellationToken ct)
        => records.UpdateAsync(key, input, version, context, ct);
    public Task<FormResult> DeleteAsync(string key, string version, FormExecutionContext context, CancellationToken ct) => records.DeleteAsync(key, version, context, ct);
}
