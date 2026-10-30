using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace AppBlocks.Connections;

/// <summary>
/// Finds the Oracle driver in its own folder. Umbraco's startup scan loads every DLL beside the site and whatever
/// those reference, and this driver references optional cloud packages it does not ship, so a site cannot start with
/// it in that folder. For the same reason nothing in AppBlocks references it at compile time.
/// </summary>
internal static class OracleDriver
{
    private const string AssemblyName = "Oracle.ManagedDataAccess";

    /// <summary>Interactive Reports looks the driver up by name, so the search covers every caller, not just <see cref="Create"/>.</summary>
    [ModuleInitializer]
    internal static void Register() => AssemblyLoadContext.Default.Resolving += (context, name) =>
        name.Name == AssemblyName && Path.Combine(AppContext.BaseDirectory, "oracle", AssemblyName + ".dll") is var path && File.Exists(path)
            ? context.LoadFromAssemblyPath(path) : null;

    public static DbConnection Create(string connectionString) => (DbConnection)Activator.CreateInstance(
        Type.GetType($"Oracle.ManagedDataAccess.Client.OracleConnection, {AssemblyName}", throwOnError: true)!, connectionString)!;
}
