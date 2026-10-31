# Business rules for AppBlocks forms

Generated form samples contain the fields and example database reads and writes for one form. These recipes cover behaviour shared by many forms. They are examples to adapt in your application; AppBlocks does not execute code from the backoffice.

The generated repository uses SQLite and a separate `SampleForm` table to keep the example easy to run. It does not assume your production database is SQLite. Dates and times in the example use ISO strings. Replace the example table and migrations when integrating it with your application's storage.

For application forms and reports, use the separate `AppBlocksDb` database rather than Umbraco's SQLite file. The default `AppBlocks.sqlite.db` sits beside Umbraco's database and holds application data, saved report views and uploads. This separates application writes from CMS saves. Choose another named connection when needed; `Attachments:Connection` can select a separate upload store, including the optional `AttachmentsDb`. The application owns its business tables and schema changes.

## The authenticated caller

In Umbraco, a **Member** signs in to the website and can belong to **Member Groups**. A **User** signs in to the backoffice and can belong to **User Groups**. A **backoffice administrator** is a User in the Administrators User Group.

A C# `IFormResource` receives a `FormExecutionContext`. Despite its name, the `Member` property can contain either an Umbraco Member's principal or a backoffice User's principal. AppBlocks supplies the identity; your application decides which account types and groups may perform each operation. Never take the signed-in account's identity from submitted form fields.

```csharp
using System.Security.Claims;
using AppBlocks.Forms;

var principal = context.Member;
if (principal.Identity?.IsAuthenticated != true)
    return FormResult.Denied();

var loginName = principal.Identity.Name;
var authenticationId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
```

Member IDs and User IDs belong to different account stores. Use AppBlocks' resolver when you need a stable application key or current account information:

```csharp
using AppBlocks.DataSources;

// Inject IDataSourceUserResolver into your resource.
var account = await userResolver.ResolveAsync(context.Member, cancellationToken);
if (account is null)
    return FormResult.Denied();

var ownerKey = account.Key;     // Member ID, or "backoffice:" followed by the User ID.
var name = account.Name;
var email = account.Email;
var groups = account.Groups;   // Member Group names, or backoffice User Group names.
var isMember = account.IsMember;
var isBackOfficeUser = account.IsBackOfficeUser;
var isBackOfficeAdministrator = account.IsInUserGroup("admin"); // Umbraco's Administrators User Group alias.
```

The resolver accepts approved, unlocked Members and backoffice Users, regardless of group membership. `null` means there is no valid authenticated account. Resolving an account grants no business permission, and membership of the Administrators User Group grants no automatic application access.

The resolver reads the current account and groups rather than relying only on old cookie claims. An external login's provider key is available as `Sid` for compatibility; it is not necessarily a Windows SID on every site. `Groups` contains display names; `UserGroupAliases` contains current backoffice User Group aliases. Member Groups and User Groups are separate, even when their names match.

Umbraco's Public Access restrictions still protect Member pages. AppBlocks does not let a backoffice User bypass those restrictions. On pages without Member Public Access restrictions, either account type can reach your resource, where your code must enforce the application's rules. If both account types are signed in, the Member session takes precedence.

## Group membership

Choose the rule your application needs:

| Application rule | Identity check |
| --- | --- |
| Any Umbraco Member | `account.IsMember` |
| Any backoffice User | `account.IsBackOfficeUser` |
| A specific Member Group | `account.IsInMemberGroup("Order editors")` |
| The Administrators User Group | `account.IsInUserGroup("admin")` |
| Another backoffice User Group | `account.IsInUserGroup("editor")` |

`IsInMemberGroup(string name)` checks the Member Group's **name**, for example `"Order editors"`. `IsInUserGroup(string alias)` checks the backoffice User Group's **alias**, for example `"admin"`, whose default display name is "Administrators". Both comparisons ignore case. Neither method accepts a numeric group ID or GUID, and neither treats the other account type as a match.

For a backoffice User Group, the alias is the second string shown with the lock control beside the editable name. Renaming the display name does not change the alias. An editable alias can be unlocked and changed deliberately; update any code or configuration that references the old alias when doing so. An alias is not an immutable group identity.

Umbraco 18 Member Groups have a name and an entity key, but no separate alias field. This is why the Member Group method takes a name, while the User Group method takes an alias.

You can enumerate the signed-in account's current groups:

```csharp
foreach (var memberGroupName in account.MemberGroupNames)
{
    // A Member Group name, usable with account.IsInMemberGroup(memberGroupName).
}

foreach (var userGroupAlias in account.UserGroupAliases)
{
    // A backoffice User Group alias, usable with account.IsInUserGroup(userGroupAlias).
}

var userGroupDisplayNames = account.UserGroupNames;
```

`MemberGroupNames` is empty for a backoffice User. `UserGroupNames` and `UserGroupAliases` are empty for a Member. `Groups` remains the list of group display names for whichever account type was resolved. The name and alias lists are separate collections, not paired by position.

For example, this application chooses to allow Members in its Order editors Member Group:

```csharp
var canEdit = account.IsInMemberGroup("Order editors");
if (!canEdit)
    return FormResult.Denied();
```

Avoid granting a Member access by comparing their email address with a backoffice User's email. Sign-in, page access and permission to update a particular record are separate checks.

For the built-in data-source engine, **Read groups** and **Write groups** select Umbraco Member Groups. They have no administrator exception. Leave these settings empty when your application uses its own account-type or User Group rules, and enforce those rules in `IDataSourcePolicy` using `request.User`. Each registered policy must allow the operation. Custom `IFormResource` implementations enforce their own rules on every operation.

Interactive Reports exposes `IReportAccessPolicy` for report permissions. Its default report-administrator group list is empty; a site can explicitly configure `AppBlocks:ReportAdministratorGroups` with User Group aliases or supply its own policy. Umbraco's own backoffice management permissions remain separate from application business rules.

## User Group selections on a Member

A Member Type can have a custom property that picks backoffice User Groups. This is a valid application-defined authorization model: your application can use those selections as roles, just as it can use Member Groups, actual User Group membership or another rule. Umbraco stores the selections as property data; your C# resource or policy gives them their application meaning.

In Umbraco 18.3, the built-in **User Group Picker** UI needs a custom Data Type registration before it can be used as a Member Type property. Create a JSON-backed Data Type using that picker UI and add a property such as `applicationUserGroups` to the Member Type. Configure this property to be managed by the backoffice editors responsible for assigning application access, rather than exposed as a self-editable profile field.

Preserve the [picker's native value format](https://github.com/umbraco/Umbraco-CMS/blob/release-18.3.0/src/Umbraco.Web.UI.Client/src/packages/user/user-group/property-editor/user-group-picker/property-editor-ui-user-group-picker.element.ts): a JSON array of references, each containing a `unique` value with the User Group's GUID key. These stored values are keys, not names or aliases:

```json
[
  { "unique": "765ef0cc-1932-4c10-9f10-25e7e138c406" }
]
```

The following application-owned C# class resolves the current Member from the authenticated principal, reads the property, and loads the referenced groups through `IUserGroupService`. It returns each group's key, current display name and current alias. Empty or malformed selections return no groups; deleted groups are omitted.

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AppBlocks.DataSources;
using Umbraco.Cms.Core.Services;

public sealed record SelectedUserGroup(Guid Key, string Name, string Alias);

public sealed class MemberUserGroupSelections(
    IDataSourceUserResolver identities,
    IMemberService members,
    IUserGroupService userGroups)
{
    private const string PropertyAlias = "applicationUserGroups";
    private sealed record Reference([property: JsonPropertyName("unique")] Guid Key);

    public async Task<IReadOnlyList<SelectedUserGroup>> GetAsync(
        ClaimsPrincipal principal, CancellationToken ct)
    {
        var account = await identities.ResolveAsync(principal, ct);
        if (account is not { IsMember: true }) return [];

        var member = int.TryParse(account.Key, out var id) ? members.GetById(id)
            : Guid.TryParse(account.Key, out var key)
                ? (await members.GetByKeysAsync(key)).FirstOrDefault() : null;
        if (member is not { IsApproved: true, IsLockedOut: false }) return [];

        var json = member.GetValue<string>(PropertyAlias);
        if (string.IsNullOrWhiteSpace(json)) return [];

        Guid[] keys;
        try
        {
            var references = JsonSerializer.Deserialize<Reference?[]>(json) ?? [];
            if (references.Any(item => item is null || item.Key == Guid.Empty)) return [];
            keys = references.Select(item => item!.Key).Distinct().ToArray();
        }
        catch (JsonException) { return []; }
        if (keys.Length == 0) return [];

        ct.ThrowIfCancellationRequested();
        var groups = await userGroups.GetAsync(keys);
        return groups.Select(group => new SelectedUserGroup(
            group.Key, group.Name ?? group.Alias, group.Alias)).ToArray();
    }
}
```

Register this class with `builder.Services.AddScoped<MemberUserGroupSelections>()` in your application's startup or composer, then inject it into your resource. This example application accepts either a backoffice User in the `orderApprovers` User Group or a Member whose custom property selects that same group:

```csharp
var account = await userResolver.ResolveAsync(context.Member, cancellationToken);
if (account is null) return FormResult.Denied();

var selectedGroups = await memberUserGroupSelections.GetAsync(context.Member, cancellationToken);
var canApprove = account.IsInUserGroup("orderApprovers")
    || selectedGroups.Any(group => group.Alias.Equals("orderApprovers", StringComparison.OrdinalIgnoreCase));
if (!canApprove) return FormResult.Denied();

// Perform this application's approval operation.
```

The same helper can be injected into an `IDataSourcePolicy` and called with `request.Context.Member`. Enforce the chosen rule on every relevant operation. A developer can also enumerate `selectedGroups` or compare `group.Key` when a particular group's identity matters independently of its editable alias.

These selections describe this application's roles; they do not alter Umbraco's native Member Groups, backoffice User membership or backoffice permissions. `account.IsInUserGroup(...)` continues to check actual User Group membership, while `MemberUserGroupSelections` reads the Member's application-defined selections. A group rename preserves the stored GUID reference; changing its alias requires updating application rules that compare that alias.

## Ownership and record status

Read the stored record before deciding whether it can change. Submitted owner and status values are not evidence of permission. An owner rule can look like this inside a resource's update operation:

```csharp
if (stored.OwnerKey != account.Key)
    return FormResult.Denied();

if (stored.Status != "Draft")
    return FormResult.Invalid("", "Submitted records cannot be edited.");
```

Read these values and perform the write in the same transaction, using the engine's concurrency checks or an explicit version predicate. Apply ownership to reads and deletes too. Filter list queries at the database so records belonging to other accounts are never returned.

For the built-in data-source engine, implement `IDataSourcePolicy`. Each registered policy must allow an operation:

```csharp
using AppBlocks.DataSources;

public sealed class DraftOrderPolicy : IDataSourcePolicy
{
    public ValueTask<bool> IsAllowedAsync(DataSourceRequest request, CancellationToken ct)
    {
        if (request.DataSource != "orders") return ValueTask.FromResult(true);

        // This policy adds an edit rule. Configure read/query access separately.
        if (request.Action is not (DataSourceAction.Update or DataSourceAction.Delete))
            return ValueTask.FromResult(true);

        var current = request.Current;
        var allowed = current is not null
            && current.TryGetValue("ownerKey", out var owner)
            && string.Equals(Convert.ToString(owner), request.User.Key, StringComparison.Ordinal)
            && current.TryGetValue("status", out var status)
            && string.Equals(Convert.ToString(status), "Draft", StringComparison.Ordinal);
        return ValueTask.FromResult(allowed);
    }
}
```

Register it with `builder.Services.AddScoped<IDataSourcePolicy, DraftOrderPolicy>()` in your application's startup or composer. Write policies run inside the engine's transaction. A policy may query `request.Connection` with `request.Transaction`; it must not close, commit or roll back either one. Protect ownership fields from reassignment in your resource or a policy comparing `Current` with `Proposed`.

## Validation and trusted defaults

Use field settings for required values, lengths and numeric limits. Use a resource for rules involving several fields or database records:

```csharp
if (input.EndDate < input.StartDate)
    return FormResult.Invalid("endDate", "The end date must follow the start date.");
```

Set audit values on the server after resolving the signed-in account:

```csharp
var createdBy = account.Key;
var createdAt = DateTimeOffset.UtcNow;
```

A hidden field or a disabled control is still browser input. Do not use it as the authority for an owner, approval, price, permission or audit value. Bind SQL parameters for submitted values; never concatenate them into SQL.

## Concurrency and duplicate submissions

The generated sample returns an ID and version from its read. An upsert uses that version and returns `false` when the stored version no longer matches. Show a conflict and let the user reload rather than silently overwriting the newer record. A stale update must not recreate a deleted record.

The built-in engine handles optimistic concurrency and create receipts. Custom resources should make creation idempotent when a retry could create a duplicate; `FormExecutionContext.SubmissionKey` is available for that purpose. Persist the receipt and the new record together.

## Wiring a custom resource

Implement `IFormResource`, or derive from `FormResource<TCreate, TUpdate>` for typed input and data-annotation validation. Register it as an `IFormResource`, choose its alias in the Form interface and map the field blocks to its field descriptors. The generated repository is a persistence example to call from those operations, not an automatically registered form resource.

Return `FormResult.Denied()` for refused operations, `Missing()` for absent records, `Conflict()` for stale updates, `Invalid(field, message)` for validation, and `Ok(record)` for success. Set the returned `FormCapabilities` to describe the actions the caller may see, and enforce the same rules again when an action is submitted.
