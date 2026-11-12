namespace AppBlocks.DataSources;

/// <summary>A read-only SQL statement and its bound values for an external query engine. Values are never interpolated into SQL.</summary>
public sealed record DataSourceQueryCommand(string Sql, IReadOnlyDictionary<string, object?> Parameters);

/// <summary>
/// A prepared projection of a data source, without exposing the engine's execution plan. The caller must authorize the
/// read through <see cref="DataSourceQueries.AuthorizeAsync(DataSourceDefinition, Forms.FormExecutionContext, CancellationToken)"/>
/// before executing it. An additional predicate is trusted server SQL, such as an external engine's access placeholder.
/// </summary>
public sealed class DataSourceReadQuery
{
    private readonly DataSourcePlan plan;
    private readonly Func<DateTimeOffset> now;
    private readonly string? predicate;

    internal DataSourceReadQuery(DataSourcePlan plan, Func<DateTimeOffset> now, string? predicate)
    {
        this.plan = plan;
        this.now = now;
        this.predicate = predicate;
        FieldColumns = plan.Columns.Select(column => new DataSourceFieldColumn(column.Field, column.Column, column.Kind)).ToArray();
        Columns = FieldColumns.Select(column => column.Column).ToArray();
    }

    public IReadOnlyList<string> Columns { get; }
    /// <summary>The field aliases and database columns available to consumers such as schedule validation and authoring tools.</summary>
    public IReadOnlyList<DataSourceFieldColumn> FieldColumns { get; }
    public bool BindsUser => plan.BindsUser;

    /// <summary>The statement's shape with null parameter values, for schema discovery and authoring.</summary>
    public DataSourceQueryCommand Preview() => Build(_ => null);

    /// <summary>Binds one caller's values using one clock reading and the data source's date-time mode.</summary>
    public DataSourceQueryCommand Bind(DataSourceUser user, IReadOnlyDictionary<string, string>? parameters = null,
        IReadOnlyDictionary<string, object?>? applicationValues = null)
    {
        var values = new BindVariableValues(user, parameters, now(), applicationValues);
        return Build(variable => values.Bind(variable, plan.Dialect));
    }

    private DataSourceQueryCommand Build(Func<BindVariable, object?> value)
    {
        var parameters = plan.Parameters();
        var from = plan.From(parameters, value);
        var restriction = plan.Restriction?.Render(parameters, value);
        var where = predicate is null ? restriction : predicate + (restriction is null ? "" : " AND (" + restriction + ")");
        var sql = plan.Dialect.Select(from, plan.ReadColumns, [], where);
        return new(sql, parameters.Bindings.ToDictionary(parameter => parameter.Name,
            parameter => parameter.Value is TypedValue typed ? typed.Value : parameter.Value));
    }
}
