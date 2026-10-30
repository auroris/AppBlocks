using System.Data.Common;
using System.Text.Json;
using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;

namespace AppBlocks.Connections;

internal sealed class ManagedConnectionFactory(IReportConnectionFactory inner, ConnectionCatalog catalog) : IReportConnectionFactory
{
    public DbConnection CreateConnection(string name) => catalog.Find(name) is { } item ? ConnectionCatalog.Create(item.Provider, item.Value) : inner.CreateConnection(name);
    public void SetDetectedDialect(string name, ReportDialect dialect) => inner.SetDetectedDialect(name, dialect);
}

internal static class ManagedReportCompiler
{
    public static ReportDefinition CompileManaged(this ReportDefinitionCompiler compiler, string name, ReportDefinition definition, ConnectionCatalog? catalog)
    {
        if (catalog?.Find(definition.Connection ?? "") is not { } connection) return compiler.Compile(name, definition);
        var detached = JsonSerializer.Deserialize<ReportDefinition>(JsonSerializer.Serialize(definition, IrJson.Options), IrJson.Options)!;
        detached.Connection = "";
        detached.DataSource = connection.Value;
        detached.Provider = connection.Provider;
        var compiled = compiler.Compile(name, detached);
        // The engine stores connection material privately under a content-addressed identifier.
        // Definitions returned to clients never include the material used to resolve that identifier.
        compiled.DataSource = null;
        compiled.Provider = null;
        return compiled;
    }
}
