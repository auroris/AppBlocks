using System.Data.Common;
using System.Text.Json;
using InteractiveReport.AspNetCore;
using InteractiveReport.Core.Execution;
using InteractiveReport.Core.Model;
using AppBlocks.Connections;

namespace AppBlocks.InteractiveReports.Connections;

internal sealed class ManagedConnectionFactory(IReportConnectionFactory inner, ConnectionCatalog catalog) : IReportConnectionFactory
{
    public DbConnection CreateConnection(string name) => catalog.Contains(name) ? catalog.Openable(name) : inner.CreateConnection(name);
    public void SetDetectedDialect(string name, ReportDialect dialect) => inner.SetDetectedDialect(name, dialect);
}

internal static class ManagedReportCompiler
{
    public static ReportDefinition CompileManaged(this ReportDefinitionCompiler compiler, string name, ReportDefinition definition, ConnectionCatalog? catalog)
    {
        using var connection = catalog?.ForCompilation(definition.Connection ?? "");
        if (connection is null) return compiler.Compile(name, definition);
        var detached = JsonSerializer.Deserialize<ReportDefinition>(JsonSerializer.Serialize(definition, IrJson.Options), IrJson.Options)!;
        detached.Connection = "";
        detached.DataSource = connection.ConnectionString;
        // Interactive Reports uses one Oracle driver token and a separate legacy SQL dialect.
        var legacyOracle = catalog!.ConfiguredDialect(definition.Connection!) == DatabaseDialect.Oracle11g;
        detached.Provider = legacyOracle ? DatabaseDialect.Oracle.ProviderName() : DatabaseKinds.Of(connection).ProviderName();
        var compiled = compiler.Compile(name, detached);
        if (legacyOracle) compiled.Dialect = ReportDialect.Oracle11g;
        // The engine stores connection material privately under a content-addressed identifier.
        // Definitions returned to clients never include the material used to resolve that identifier.
        compiled.DataSource = null;
        compiled.Provider = null;
        // The engine opens what this name resolves to, and only the managed factory knows an application database's schema.
        if (catalog!.IsApplicationDatabase(definition.Connection!)) compiled.Connection = definition.Connection!;
        return compiled;
    }
}
