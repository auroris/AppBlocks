using System.Data.Common;
using System.Text.Json;
using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;

namespace AppBlocks.Connections;

internal sealed class ManagedConnectionFactory(IReportConnectionFactory inner, ConnectionCatalog catalog) : IReportConnectionFactory
{
    public DbConnection CreateConnection(string name) => catalog.Find(name) is { } item ? ConnectionCatalog.Create(item) : inner.CreateConnection(name);
    public void SetDetectedDialect(string name, ReportDialect dialect) => inner.SetDetectedDialect(name, dialect);
}

internal static class ManagedReportCompiler
{
    public static ReportDefinition CompileManaged(this ReportDefinitionCompiler compiler, string name, ReportDefinition definition, ConnectionCatalog? catalog)
    {
        if (catalog?.Find(definition.Connection ?? "") is not { } connection) return compiler.Compile(name, definition);
        // Package-owned files need their registered factory to create the directory and initialize SQLite.
        if (connection.LocalDefault) return compiler.Compile(name, definition);
        var detached = JsonSerializer.Deserialize<ReportDefinition>(JsonSerializer.Serialize(definition, IrJson.Options), IrJson.Options)!;
        detached.Connection = "";
        detached.DataSource = connection.Value;
        // Interactive Reports uses one Oracle driver token and a separate legacy SQL dialect.
        detached.Provider = connection.Provider == "oracle11g" ? "oracle" : connection.Provider;
        var compiled = compiler.Compile(name, detached);
        if (connection.Provider == "oracle11g") compiled.Dialect = ReportDialect.Oracle11g;
        // The engine stores connection material privately under a content-addressed identifier.
        // Definitions returned to clients never include the material used to resolve that identifier.
        compiled.DataSource = null;
        compiled.Provider = null;
        return compiled;
    }
}
