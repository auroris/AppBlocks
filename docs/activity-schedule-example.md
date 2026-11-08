# Worked example: a multi-tenant activity schedule

One application, several units, each with its own schedule and its own editors, every unit's pages living inside its own part of the site. This guide puts the pieces of AppBlocks together for it: the page tree, the tables, three data source nodes, a Schedule block, a form page, and the C# that holds every rule. It is the [business rules guide](business-rules.md) applied end to end, so that guide explains each rule; this one shows where it goes.

The shape of the rules is short:

- **The page tree decides the tenant.** A unit's home page carries a Tenant id. Every page below it, the schedule and its administration pages, belongs to that tenant, and nothing in a URL parameter or a form field says otherwise.
- **One C# class decides who may edit.** A backoffice User allowed to edit the calling page may change its tenant's schedule. Anyone else needs to be a Member of that tenant's editors group. Anyone signed in may read.
- **SQL stays tenant-blind.** The data sources filter and restrict on `:APP.tenant`, a value the application computes, so one set of nodes serves every tenant and a new unit is a page copy.

## The pages

```text
Home                                    (/en/)
├── 4 Wing                              (/en/4wing/)            tenantId = 4WING
│   ├── Activity schedule               Schedule block
│   ├── Categories                      Interactive Report on wing.categories, form page Category
│   ├── Category                        Forms container, form on wing.categories, key parameter id
│   └── Activity                        Forms container, form on wing.activity, key parameter id
└── Units
    └── 10 FTTS                         (/en/units/10ftts/)     tenantId = 10FTTS
        ├── Activity schedule
        ├── Categories
        ├── Category
        └── Activity
```

The unit home pages use a document type with a text property aliased `tenantId`. That property is the whole definition of a tenant: the C# below walks up from the calling page to the nearest page that has one. Depth, naming and language do not matter, which is why 4 Wing can sit at the root and 10 FTTS under Units.

No page needs Public Access for the rules to work. The Schedule block, the categories report and the category form read through the data source engine, which shows a sign-in notice to anyone it does not know, and the activity form's class refuses anyone it does not know. Protecting the subtree with Public Access as well keeps the pages out of the menu for visitors, but Umbraco then refuses a backoffice User who has no Member session, editors included, unless the host registers an `IProtectedPageAdmission` that admits them.

## The tables

```sql
CREATE TABLE WING_CATEGORY (
    ID               INTEGER PRIMARY KEY,
    TENANT_ID        TEXT NOT NULL,
    NAME             TEXT NOT NULL,
    BACKGROUND_COLOR TEXT,
    TEXT_COLOR       TEXT,
    ORDERING         INTEGER NOT NULL DEFAULT 0,
    ORIENTATION      INTEGER NOT NULL DEFAULT 0,
    DESCRIPTION      TEXT
);

CREATE TABLE WING_ACTIVITY (
    ID          TEXT NOT NULL PRIMARY KEY,
    TENANT_ID   TEXT NOT NULL,
    NAME        TEXT NOT NULL,
    START_DATE  TEXT NOT NULL,
    END_DATE    TEXT NOT NULL,
    CATEGORY    TEXT NOT NULL,
    DESCRIPTION TEXT,
    CONTACT     TEXT,
    UNITS       TEXT,
    LINK        TEXT,
    HIDDEN      INTEGER NOT NULL DEFAULT 0,
    CREATED_BY  TEXT NOT NULL,
    CREATED_AT  TEXT NOT NULL,
    MODIFIED_BY TEXT NOT NULL,
    MODIFIED_AT TEXT NOT NULL
);
```

Both tables carry the tenant. Categories take a database-generated key, since the engine inserts them; activities take GUID text keys, since the form class chooses them. Dates are ISO text, which every supported database compares correctly. Create them in the application database, `AppBlocksDb`, as the [database setup guide](database-setup.md) describes. The form class detects a stale save by comparing a checksum of the activity's values, so the table holds only the activity's own columns.

## The data source nodes

Three nodes under **Data sources**, each on the `AppBlocksDb` connection. Their aliases start with `wing.` so the policy below recognises them. Every one has the same row restriction, `TENANT_ID = :APP.tenant`, which keeps every read and every write inside the tenant of the page, whatever the SQL or a submitted row says.

**`wing.categories`**, the table a unit's editors maintain through the Categories report, whose form page is Category and key column `ID`, with its create button and open link left at Form page decides, and the Category form, whose resource is this node:

| Setting | Value |
| --- | --- |
| Source | `WING_CATEGORY` |
| Key columns | `ID` |
| Row restriction | `TENANT_ID = :APP.tenant` |
| Insert defaults | `TENANT_ID = :APP.tenant` |
| Read groups, Write groups | Empty. The policy decides, so backoffice Users are not shut out |

The insert default is what keeps a new category in the right tenant without the category form carrying a tenant field at all, and the row restriction is what refuses a row that tries to leave it.

**`wing.schedule.rows`**, the Schedule block's rows, with the columns the block needs and the optional ones it can use:

```sql
SELECT ID AS "key", NAME AS "label", NULL AS "group", ORIENTATION AS "orientation",
       BACKGROUND_COLOR AS "color", TEXT_COLOR AS "textColor", DESCRIPTION AS "description"
FROM WING_CATEGORY
WHERE TENANT_ID = :APP.tenant
```

Rows keep the order the query returns them, and a SELECT source takes no `ORDER BY`. When the order matters, point the block at a published report definition over this source with its sort set to `ORDERING`; the block accepts either.

**`wing.schedule.bars`**, the activities of the window shown:

```sql
SELECT ID AS "key", CATEGORY AS "row", START_DATE AS "start", END_DATE AS "end", NAME AS "label", NULL AS "kind", LINK AS "link",
       DESCRIPTION AS "description"
FROM WING_ACTIVITY
WHERE TENANT_ID = :APP.tenant AND HIDDEN = 0 AND END_DATE >= :PARAM.from AND START_DATE <= :PARAM.to
```

The bars' row is the category's name, so the rows query must offer the name as its key. Using `ID` for both works too if the activity stores the category's id. Contact and units are kept out of the bars and shown on the activity form, which the link template opens.

## The Schedule block

On each unit's Activity schedule page, a Schedule block with these settings. The [Schedule block guide](schedule.md) has the rest.

| Setting | Value |
| --- | --- |
| Rows source | `wing.schedule.rows` |
| Bars source | `wing.schedule.bars` |
| Days before today, after today | 7 and 60, or whatever the unit prefers |
| Link template | `../activity/?id={key}` |
| Hide empty rows | On, so a category with nothing in the window stays out of the way |

A category whose `ORIENTATION` is 1, statutory holidays say, becomes a band across the top, its activities written top to bottom and shading their days through every row. Colours come from the category's columns and a bar with a description opens it, with the link, when clicked. The link is relative, so the same block on every copy of the page opens that unit's own Activity page.

## The form page

The Activity page holds a Forms container with one form. Its Form interface selects the resource `wing.activity` and the key parameter `id`, so `../activity/?id=…` from the schedule opens an existing activity and the bare page starts a new one. The field blocks bind to the class's fields: `name`, `startDate`, `endDate`, `category`, `description`, `contact`, `units`, `link` and `hidden`. Nothing on the page says which tenant it is; the class learns that from the page.

## The C#

Four small classes and a composer. The tenant rule, the bind variable provider, the data source policy and the form class each do one thing, and the form class checks its rules on every operation.

```csharp
using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AppBlocks.Connections;
using AppBlocks.DataSources;
using AppBlocks.Forms;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.Services.Navigation;
using Umbraco.Extensions;

namespace Wing.ActivitySchedule;

/// <summary>
/// The one place that knows what a tenant is: the nearest page at or above the calling page whose type carries a
/// Tenant id property, such as a unit's home page. Everything else asks this class.
/// </summary>
public sealed class WingTenants(IPublishedContentCache pages, IDocumentNavigationQueryService navigation)
{
    public const string Property = "tenantId";

    /// <summary>The tenant of the page a block renders on or a form posts from; null outside any tenant.</summary>
    public string? Find(Guid pageKey)
    {
        if (pageKey == Guid.Empty || !navigation.TryGetAncestorsOrSelfKeys(pageKey, out var keys)) return null;
        foreach (var key in keys)
            if (pages.GetById(key)?.Value<string>(Property) is { Length: > 0 } tenant) return tenant.Trim().ToUpperInvariant();
        return null;
    }

    /// <summary>
    /// Who may change a tenant's schedule: a backoffice User allowed to edit the calling page, or a Member of the
    /// tenant's editors group. Reading needs only a signed-in account, which the data sources and the form check.
    /// </summary>
    public static bool CanEdit(DataSourceUser account, string tenant) => account.CanEditPage || account.IsInMemberGroup(EditorsGroup(tenant));

    public static string EditorsGroup(string tenant) => tenant + " schedule editors";
}

/// <summary>Hands the tenant to editor SQL as <c>:APP.tenant</c>, so one data source serves every tenant's pages.</summary>
public sealed class WingTenantProvider(WingTenants tenants) : IBindVariableProvider
{
    public ValueTask<IReadOnlyDictionary<string, object?>?> ResolveAsync(FormExecutionContext context, CancellationToken ct)
        => ValueTask.FromResult<IReadOnlyDictionary<string, object?>?>(new Dictionary<string, object?> { ["tenant"] = tenants.Find(context.PageKey) });
}

/// <summary>
/// The rule for the data source nodes whose alias starts with <c>wing.</c>, the categories and the schedule's rows and
/// bars: outside a tenant nothing is read or written, anyone signed in may read, and only the tenant's editors may write.
/// The nodes' row restriction, <c>TENANT_ID = :APP.tenant</c>, keeps every read and write inside the tenant.
/// </summary>
public sealed class WingDataSourcePolicy(WingTenants tenants) : IDataSourcePolicy
{
    public ValueTask<bool> IsAllowedAsync(DataSourceRequest request, CancellationToken ct)
    {
        if (!request.DataSource.StartsWith("wing.", StringComparison.Ordinal)) return ValueTask.FromResult(true);
        if (tenants.Find(request.Context.PageKey) is not { } tenant) return ValueTask.FromResult(false);
        if (request.Action is DataSourceAction.Query or DataSourceAction.Load or DataSourceAction.Initialize) return ValueTask.FromResult(!request.User.IsAnonymous);
        return ValueTask.FromResult(WingTenants.CanEdit(request.User, tenant));
    }
}

/// <summary>What the activity form submits. Binding is strict: an unknown property or a wrong type is refused before the class runs.</summary>
public sealed class ActivityInput
{
    [Required, MaxLength(120)] public string Name { get; set; } = "";
    [Required] public DateOnly? StartDate { get; set; }
    [Required] public DateOnly? EndDate { get; set; }
    [Required, MaxLength(60)] public string Category { get; set; } = "";
    [MaxLength(2048)] public string? Description { get; set; }
    [MaxLength(1024)] public string? Contact { get; set; }
    [MaxLength(512)] public string? Units { get; set; }
    [MaxLength(512)] public string? Link { get; set; }
    public bool Hidden { get; set; }
}

/// <summary>
/// The activity form's class. The tenant comes from the page, the account from the resolver, and every operation
/// checks both before it touches the database. The form never learns the tenant from a field.
/// </summary>
public sealed class ActivityResource(IApplicationConnectionFactory connections, IDataSourceUserResolver users, WingTenants tenants) : FormResource<ActivityInput, ActivityInput>
{
    public override string Alias => "wing.activity";

    public override IReadOnlyList<FormField> Fields { get; } = [
        new("name", "Activity", "text", Required: true, MaxLength: 120),
        new("startDate", "Starts", "date", Required: true),
        new("endDate", "Ends", "date", Required: true),
        new("category", "Category", "text", Required: true, MaxLength: 60),
        new("description", "Description", "text", MaxLength: 2048),
        new("contact", "Contact", "text", MaxLength: 1024),
        new("units", "Units", "text", MaxLength: 512),
        new("link", "Link", "text", MaxLength: 512),
        new("hidden", "Hidden from the schedule", "checkbox"),
    ];

    private static readonly string[] Columns = ["NAME", "START_DATE", "END_DATE", "CATEGORY", "DESCRIPTION", "CONTACT", "UNITS", "LINK", "HIDDEN"];

    /// <summary>The account and the tenant, or the refusal to return: no account, no tenant, or for a change, no permission.</summary>
    private async Task<(DataSourceUser Account, string Tenant, FormResult? Refusal)> AdmitAsync(FormExecutionContext context, bool change, CancellationToken ct)
    {
        var account = await users.ResolveAsync(context, ct);
        if (account is null) return (null!, "", FormResult.Denied());
        if (tenants.Find(context.PageKey) is not { } tenant) return (account, "", FormResult.Denied());
        if (change && !WingTenants.CanEdit(account, tenant)) return (account, tenant, FormResult.Denied());
        return (account, tenant, null);
    }

    public override async Task<FormResult> InitializeAsync(FormExecutionContext context, CancellationToken ct)
    {
        var (_, _, refusal) = await AdmitAsync(context, change: true, ct);
        if (refusal is not null) return refusal;
        var values = Fields.ToDictionary(f => f.Key, _ => (object?)null);
        values["hidden"] = false;
        return FormResult.Ok(new FormRecord(null, null, values, new(Create: true)));
    }

    public override async Task<FormResult> LoadAsync(string key, FormExecutionContext context, CancellationToken ct)
    {
        // Anyone signed in may open an activity of their tenant; the capabilities say whether this person may change it.
        var (account, tenant, refusal) = await AdmitAsync(context, change: false, ct);
        if (refusal is not null) return refusal;
        await using var connection = await OpenAsync(ct);
        return await ReadAsync(connection, null, key, tenant, WingTenants.CanEdit(account, tenant), ct) ?? FormResult.Missing();
    }

    protected override async Task<FormResult> CreateAsync(ActivityInput input, FormExecutionContext context, CancellationToken ct)
    {
        var (account, tenant, refusal) = await AdmitAsync(context, change: true, ct);
        if (refusal is not null) return refusal;
        await using var connection = await OpenAsync(ct);
        if (await ValidateAsync(connection, input, tenant, ct) is { } invalid) return invalid;
        var key = Guid.NewGuid().ToString("D");
        var now = Stamp(DateTime.UtcNow);
        await ExecuteAsync(connection, null, """
            INSERT INTO WING_ACTIVITY (ID, TENANT_ID, NAME, START_DATE, END_DATE, CATEGORY, DESCRIPTION, CONTACT, UNITS, LINK, HIDDEN, CREATED_BY, CREATED_AT, MODIFIED_BY, MODIFIED_AT)
            VALUES (@id, @tenant, @name, @start, @end, @category, @description, @contact, @units, @link, @hidden, @by, @at, @by, @at)
            """, Values(input, key, tenant, account, now), ct);
        return (await ReadAsync(connection, null, key, tenant, true, ct))!;
    }

    protected override async Task<FormResult> UpdateAsync(string key, ActivityInput input, string? checksum, FormExecutionContext context, CancellationToken ct)
    {
        var (account, tenant, refusal) = await AdmitAsync(context, change: true, ct);
        if (refusal is not null) return refusal;
        await using var connection = await OpenAsync(ct);
        if (await ValidateAsync(connection, input, tenant, ct) is { } invalid) return invalid;
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (await StaleAsync(connection, transaction, key, tenant, checksum, ct) is { } stale) return stale;
        await ExecuteAsync(connection, transaction, """
            UPDATE WING_ACTIVITY SET NAME = @name, START_DATE = @start, END_DATE = @end, CATEGORY = @category, DESCRIPTION = @description,
                CONTACT = @contact, UNITS = @units, LINK = @link, HIDDEN = @hidden, MODIFIED_BY = @by, MODIFIED_AT = @at
            WHERE ID = @id AND TENANT_ID = @tenant
            """, Values(input, key, tenant, account, Stamp(DateTime.UtcNow)), ct);
        await transaction.CommitAsync(ct);
        return (await ReadAsync(connection, null, key, tenant, true, ct))!;
    }

    public override async Task<FormResult> DeleteAsync(string key, string? checksum, FormExecutionContext context, CancellationToken ct)
    {
        var (_, tenant, refusal) = await AdmitAsync(context, change: true, ct);
        if (refusal is not null) return refusal;
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (await StaleAsync(connection, transaction, key, tenant, checksum, ct) is { } stale) return stale;
        await ExecuteAsync(connection, transaction, "DELETE FROM WING_ACTIVITY WHERE ID = @id AND TENANT_ID = @tenant", new() { ["id"] = key, ["tenant"] = tenant }, ct);
        await transaction.CommitAsync(ct);
        return new FormResult(200);
    }

    /// <summary>
    /// Locks the activity for the rest of the transaction, then compares the checksum the caller read with the row as it stands:
    /// a missing row or a different checksum is the answer to return, and null lets the write go ahead.
    /// </summary>
    private static async Task<FormResult?> StaleAsync(DbConnection connection, DbTransaction transaction, string key, string tenant, string? checksum, CancellationToken ct)
    {
        // An update that changes nothing still takes the row's write lock, so no other save can change it before this one commits.
        var locked = await ExecuteAsync(connection, transaction, "UPDATE WING_ACTIVITY SET MODIFIED_AT = MODIFIED_AT WHERE ID = @id AND TENANT_ID = @tenant",
            new() { ["id"] = key, ["tenant"] = tenant }, ct);
        if (locked == 0 || await ReadAsync(connection, transaction, key, tenant, true, ct) is not { Record: { } current }) return FormResult.Missing();
        return current.Checksum == checksum ? null : FormResult.Conflict();
    }

    /// <summary>Rules the annotations cannot say: the dates are in order, and the category is one of the tenant's.</summary>
    private static async Task<FormResult?> ValidateAsync(DbConnection connection, ActivityInput input, string tenant, CancellationToken ct)
    {
        if (input.EndDate < input.StartDate) return FormResult.Invalid("endDate", "The activity cannot end before it starts.");
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM WING_CATEGORY WHERE TENANT_ID = @tenant AND NAME = @category";
        Bind(command, new() { ["tenant"] = tenant, ["category"] = input.Category.Trim() });
        if (Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) == 0)
            return FormResult.Invalid("category", "Choose one of this unit's categories.");
        return null;
    }

    private async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var connection = connections.CreateConnection(ConnectionCatalog.ApplicationConnection);
        await connection.OpenAsync(ct);
        return connection;
    }

    /// <summary>The record as the form shows it, within the tenant only; null when there is no such row.</summary>
    private static async Task<FormResult?> ReadAsync(DbConnection connection, DbTransaction? transaction, string key, string tenant, bool canEdit, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {string.Join(", ", Columns)} FROM WING_ACTIVITY WHERE ID = @id AND TENANT_ID = @tenant";
        Bind(command, new() { ["id"] = key, ["tenant"] = tenant });
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var values = new Dictionary<string, object?> {
            ["name"] = reader.GetString(0), ["startDate"] = reader.GetString(1), ["endDate"] = reader.GetString(2), ["category"] = reader.GetString(3),
            ["description"] = reader.IsDBNull(4) ? null : reader.GetString(4), ["contact"] = reader.IsDBNull(5) ? null : reader.GetString(5),
            ["units"] = reader.IsDBNull(6) ? null : reader.GetString(6), ["link"] = reader.IsDBNull(7) ? null : reader.GetString(7),
            ["hidden"] = Convert.ToInt64(reader.GetValue(8), CultureInfo.InvariantCulture) != 0,
        };
        return FormResult.Ok(new FormRecord(key, Checksum(values), values, new(Update: canEdit, Delete: canEdit)));
    }

    /// <summary>A checksum of the activity's values as the form shows them: a save by someone else since the read gives a different one.</summary>
    private static string Checksum(Dictionary<string, object?> values)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(values)))[..32];

    private static Dictionary<string, object?> Values(ActivityInput input, string key, string tenant, DataSourceUser account, string now) => new() {
        ["id"] = key, ["tenant"] = tenant, ["name"] = input.Name.Trim(), ["start"] = Date(input.StartDate), ["end"] = Date(input.EndDate),
        ["category"] = input.Category.Trim(), ["description"] = input.Description, ["contact"] = input.Contact, ["units"] = input.Units,
        ["link"] = input.Link, ["hidden"] = input.Hidden ? 1 : 0, ["by"] = account.Key, ["at"] = now,
    };

    private static async Task<int> ExecuteAsync(DbConnection connection, DbTransaction? transaction, string sql, Dictionary<string, object?> values, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        Bind(command, values);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static void Bind(DbCommand command, Dictionary<string, object?> values)
    {
        foreach (var (name, value) in values)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@" + name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
    }

    private static string Date(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
    private static string Stamp(DateTime moment) => moment.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
}

/// <summary>Registers the pieces. Umbraco finds a composer on its own; nothing else is configured.</summary>
public sealed class WingComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        builder.Services.AddScoped<WingTenants>();
        builder.Services.AddScoped<IBindVariableProvider, WingTenantProvider>();
        builder.Services.AddScoped<IDataSourcePolicy, WingDataSourcePolicy>();
        builder.Services.AddScoped<IFormResource, ActivityResource>();
    }
}
```

A few things in that code are deliberate.

- **`AdmitAsync` runs first in every operation**, and `Load` admits with `change: false` so a signed-in reader can open an activity while the returned capabilities say whether this person may change it. The capabilities only shape the buttons; `Update` and `Delete` admit with `change: true` and check again.
- **The tenant is in every predicate.** `WHERE ID = @id AND TENANT_ID = @tenant` means a key from another unit's schedule is a missing record, not a leak, even if someone types it into the address.
- **`Update` and `Delete` lock the activity before they compare checksums.** `StaleAsync` runs an update that changes nothing, which takes the row's write lock in SQLite, SQL Server and PostgreSQL, then reads the row in the same transaction. Another save of the same activity waits until this one commits. A checksum that differs from the one the caller read is a conflict, as Oracle APEX's row-values check reports it.
- **`CanEditPage` is Umbraco's Update permission on the calling page**, resolved for `context.PageKey`. A backoffice User who may edit a unit's pages may edit its schedule; one who may not is treated like any Member and needs the group. That is the one rule the whole application shares, so it lives in `WingTenants.CanEdit` and the policy and the form class both call it.
- **The provider answers for every data source**, not only the three above, and binds `NULL` outside a tenant. Nothing else reads `:APP.tenant`, so that is harmless, and the policy refuses the `wing.` sources outside a tenant anyway.
- **Parameter names use `@`**, which SQLite, SQL Server and PostgreSQL accept. Oracle wants `:`; adjust `Bind` if the application database is Oracle.

## Who decides what

| Question | Decided by |
| --- | --- |
| Which tenant is this page? | `WingTenants.Find`, from the nearest page above with a Tenant id |
| May this person read the schedule or the categories? | The data source engine's sign-in check, then `WingDataSourcePolicy` |
| May this person change a category? | `WingDataSourcePolicy`, through `WingTenants.CanEdit` |
| May this person open, create, change or delete an activity? | `ActivityResource`, through `WingTenants.CanEdit` on every operation |
| Which rows can a query or a write touch? | The nodes' row restriction `TENANT_ID = :APP.tenant`, bound from `WingTenantProvider` |
| Which tenant does a new category get? | The node's insert default `TENANT_ID = :APP.tenant` |
| Which tenant does a new activity get? | `ActivityResource`, from the page, never from the form |
| Is the activity's category real, and are its dates in order? | `ActivityResource.ValidateAsync` |
| Has someone else saved this activity meanwhile? | `ActivityResource.StaleAsync`, which compares a checksum of the activity's values with the locked row inside the update or delete transaction |

Nothing on this list is decided by a URL parameter, a hidden field or a block setting.

## Adding a unit

1. Copy a unit's subtree in the backoffice, with its descendants, to where the new unit's pages belong.
2. Set the new home page's Tenant id.
3. Create the Member Group `<tenant> schedule editors` and put the unit's editors in it, or give the backoffice Users who run that unit edit permission on the subtree.
4. Publish.

The data source nodes, the C# and the block settings are untouched. The Schedule block on the copied page points at the same two sources and reads the new tenant's rows; the activity form's link is relative, so it opens the copy's own Activity page.
