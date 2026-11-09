namespace AppBlocks.DataSources;

/// <summary>
/// The values of the bind variables that hold for a whole operation: the caller's facts (<c>:USER_*</c>), the clock (<c>:NOW</c>,
/// <c>:NOW_UTC</c>), the request's parameters (<c>:PARAM.</c>) and the application's values (<c>:APP.</c>). The engine's statements, the
/// shared queries and the authoring preview all read them here; a record's fields (<c>:FIELD.</c>) and a backing class's arguments
/// (<c>:ARG.</c>) belong to their callers, and are null here.
/// </summary>
/// <param name="Now">The operation's one reading of the clock, with the offset of the data source's date-times (<see cref="DataSourceRuntime.Now(DataSourceDefinition)"/>).</param>
internal readonly record struct BindVariableValues(DataSourceUser User, IReadOnlyDictionary<string, string>? Parameters, DateTimeOffset Now,
    IReadOnlyDictionary<string, object?>? Application)
{
    /// <summary>The value a statement binds: the clock in the form the dialect compares, everything else as it is.</summary>
    public object? Bind(BindVariable variable, SqlDialect dialect) => variable.Kind switch {
        BindVariableKind.Now => dialect.Now(Now.DateTime),
        BindVariableKind.NowUtc => dialect.NowUtc(Now.UtcDateTime),
        _ => Value(variable),
    };

    /// <summary>The plain value: the caller's facts, a parameter or an application value; null for the clock, which <see cref="Bind"/> answers.</summary>
    public object? Value(BindVariable variable) => variable.Kind switch {
        BindVariableKind.UserSid => User.Sid,
        BindVariableKind.UserName => User.Name,
        BindVariableKind.UserKey => User.Key,
        BindVariableKind.UserEmail => User.Email,
        BindVariableKind.UserGroups => User.Groups,
        BindVariableKind.Param => Parameter(variable.Name!),
        BindVariableKind.App => Application?.GetValueOrDefault(variable.Name!),
        _ => null,
    };

    /// <summary>A request parameter by name in any letter case; null when the request does not carry it.</summary>
    public string? Parameter(string name) => Parameters?.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
}
