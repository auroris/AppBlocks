using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using AppBlocks.Blocks;
using AppBlocks.DataSources;
using AppBlocks.Forms;

namespace AppBlocks.Tests;

/// <summary>
/// What <c>:APP.</c> binds: the values registered providers compute for an operation, how editor SQL and field
/// defaults accept the variables, and that a row restriction may use them, since they come from the application
/// rather than from the caller.
/// </summary>
public sealed class AppBindVariableTests
{
    private static readonly FormExecutionContext Context = new(new System.Security.Claims.ClaimsPrincipal(), Guid.NewGuid(), Guid.NewGuid(), "1");

    [Fact]
    public async Task Providers_are_merged_by_name_in_any_letter_case_and_nothing_is_asked_without_one()
    {
        var first = new Provider(new Dictionary<string, object?> { ["tenant"] = "4wing", ["period"] = new DateOnly(2026, 11, 1), ["not a name"] = 1 });
        var second = new Provider(new Dictionary<string, object?> { ["TENANT"] = "10ftts", ["flag"] = true, ["empty"] = null });
        var providers = new BindVariableProviders([first, second]);
        Assert.True(providers.Any);
        var values = await providers.ResolveAsync(Context, default);
        // A later provider wins a name; a name that is not an identifier is dropped; null is kept as a value that binds NULL.
        Assert.Equal("10ftts", values["Tenant"]);
        Assert.Equal(new DateOnly(2026, 11, 1), values["period"]);
        Assert.True(values.ContainsKey("empty"));
        Assert.False(values.ContainsKey("not a name"));
        Assert.Equal((1, 1), (first.Calls, second.Calls));
        var none = new BindVariableProviders();
        Assert.False(none.Any);
        Assert.Empty(await none.ResolveAsync(Context, default));
        Assert.Empty(await new BindVariableProviders([new Provider(null)]).ResolveAsync(Context, default));
        // A field default reads a value as text.
        Assert.Equal(("2026-11-01", "true", "7", null, null), (BindVariableProviders.Text(new DateOnly(2026, 11, 1)), BindVariableProviders.Text(true), BindVariableProviders.Text(7), BindVariableProviders.Text(null), BindVariableProviders.Text("")));
    }

    [Fact]
    public void Editor_sql_accepts_application_variables_everywhere_including_a_row_restriction()
    {
        var errors = new List<string>();
        var source = SqlTemplate.Parse("SELECT ID FROM ACTIVITIES WHERE TENANT = :APP.tenant AND OWNER = :USER_KEY", SqlUse.RowSource, "The source", errors);
        Assert.Empty(errors);
        Assert.Contains(new BindVariable(BindVariableKind.App, "tenant"), source!.Variables);
        Assert.Equal(":APP.tenant", new BindVariable(BindVariableKind.App, "tenant").ToString());
        // The value is the application's, not the caller's, so unlike :PARAM. it may restrict rows.
        Assert.NotNull(SqlTemplate.Parse("TENANT = :APP.tenant", SqlUse.RowRestriction, "The row restriction", errors));
        Assert.Empty(errors);
        Assert.Null(SqlTemplate.Parse("TENANT = :APP", SqlUse.RowSource, "The source", errors));
        Assert.Contains(errors, e => e.Contains(":APP") && e.Contains("not a bind variable") && e.Contains(":APP.name"));
        Assert.Equal(new BindVariable(BindVariableKind.App, "fiscal_year"), BindVariable.Parse("app.fiscal_year"));
        Assert.Null(BindVariable.Parse("APP.fiscal-year"));
        // Rendering binds the value through a parameter, never into the text.
        var parameters = new SqlParameters(SqlDialect.For(AppBlocks.Connections.DatabaseDialect.Sqlite)!);
        var sql = source.Render(parameters, variable => variable.Kind == BindVariableKind.App ? "4wing" : "42");
        Assert.DoesNotContain("4wing", sql);
        Assert.Contains(parameters.Bindings, binding => Equals(binding.Value, "4wing"));
    }

    [Fact]
    public void Field_defaults_read_the_application_values_and_resolve_to_nothing_without_them()
    {
        var query = new QueryCollection(new Dictionary<string, StringValues> { ["topic"] = "sales" });
        var application = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["tenant"] = "4wing", ["since"] = new DateOnly(2026, 11, 1) };
        var variables = new AppBindVariables("1", null, "pilot", null, DateTimeOffset.Now, query, application);
        Assert.Equal("4wing", variables.Resolve(":APP.tenant", "text"));
        Assert.Equal("4wing", variables.Resolve(":app.TENANT", "text"));
        Assert.Equal("2026-11-01", variables.Resolve(":APP.since", "date"));
        Assert.Null(variables.Resolve(":APP.region", "text"));
        Assert.Equal("sales", variables.Resolve(":PARAM.topic", "text"));
        // Without providers, as in a paper report rendered on its own, the application variables leave the field as the resource set it.
        var nowhere = new AppBindVariables("1", null, "pilot", null, DateTimeOffset.Now, query);
        Assert.Null(nowhere.Resolve(":APP.tenant", "text"));
        Assert.Equal("sales", nowhere.Resolve(":PARAM.topic", "text"));
    }

    private sealed class Provider(IReadOnlyDictionary<string, object?>? values) : IBindVariableProvider
    {
        public int Calls { get; private set; }
        public ValueTask<IReadOnlyDictionary<string, object?>?> ResolveAsync(FormExecutionContext context, CancellationToken ct) { Calls++; return ValueTask.FromResult(values); }
    }

    private sealed class Declaring(params string[] names) : IBindVariableProvider
    {
        public IReadOnlyCollection<string> Names => names;
        public ValueTask<IReadOnlyDictionary<string, object?>?> ResolveAsync(FormExecutionContext context, CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyDictionary<string, object?>?>(names.ToDictionary(name => name, name => (object?)name));
    }

    [Fact]
    public void Declared_names_are_known_only_when_every_provider_declares_its_own()
    {
        Assert.Empty(((IBindVariableProvider)new Provider(null)).Names);
        Assert.Null(new BindVariableProviders().DeclaredNames);
        // A provider that declares nothing may answer any name, so nothing can be called unanswered.
        Assert.Null(new BindVariableProviders([new Declaring("tenant"), new Provider(null)]).DeclaredNames);
        var providers = new BindVariableProviders([new Declaring("tenant"), new Declaring("Period", "region")]);
        Assert.True(providers.DeclaredNames!.SetEquals(["TENANT", "period", "region"]));
        Assert.Null(providers.UndeclaredWarning(["Tenant", "period"]));
        var warning = providers.UndeclaredWarning(["tenant", "tennant", "TENNANT"]);
        Assert.Contains(":APP.tennant is not declared", warning);
        Assert.Contains("The providers declare :APP.Period, :APP.region, :APP.tenant.", warning);
        Assert.Null(new BindVariableProviders([new Provider(null)]).UndeclaredWarning(["anything"]));
    }

    [Fact]
    public void A_definition_names_the_application_values_its_SQL_and_defaults_use()
    {
        var errors = new List<string>();
        var values = new Dictionary<string, object?> {
            ["alias"] = "activities", ["access"] = "Public", ["connection"] = "Db", ["keyColumns"] = "ID",
            ["source"] = "SELECT ID, TENANT FROM ACTIVITIES WHERE TENANT = :APP.tenant -- :APP.comment is text",
            ["rowRestriction"] = "REGION = :APP.region OR :APP.TENANT IS NULL",
            ["insertDefaults"] = "PERIOD = :APP.period\nTENANT = :APP.tenant",
            ["customStatements"] = "-- delete\nDELETE FROM ACTIVITIES WHERE ID = :FIELD.id AND OWNER = :APP.owner",
        };
        var definition = DataSourceDefinition.Read(Guid.NewGuid(), "Activities", alias => values.GetValueOrDefault(alias), picked => [], errors);
        Assert.True(definition is not null, string.Join(" ", errors));
        Assert.Equal(["tenant", "region", "owner", "period"], definition.ApplicationNames());

        // Publishing warns, and does not refuse, when declared names leave one unanswered.
        var validator = new DataSourcePublishValidator(null!, null!, null!, [], providers: new BindVariableProviders([new Declaring("tenant", "region", "period")]));
        Assert.Contains(":APP.owner is not declared", validator.Warning(Guid.NewGuid(), "Activities", alias => values.GetValueOrDefault(alias)));
        var silent = new DataSourcePublishValidator(null!, null!, null!, [], providers: new BindVariableProviders([new Provider(null)]));
        Assert.Null(silent.Warning(Guid.NewGuid(), "Activities", alias => values.GetValueOrDefault(alias)));
    }
}
