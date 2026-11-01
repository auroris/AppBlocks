# Business rules for AppBlocks forms

Generated form samples contain the fields and example database reads and writes for one form. These recipes cover behaviour shared by many forms. They are examples to adapt in your application; AppBlocks does not execute code from the backoffice.

The generated repository uses SQLite and a separate `SampleForm` table to keep the example easy to run. It does not assume your production database is SQLite. Dates and times in the example use ISO strings. Replace the example table and migrations when integrating it with your application's storage.

For application forms and reports, use the separate `AppBlocksDb` database rather than Umbraco's SQLite file. The default `AppBlocks.sqlite.db` sits beside Umbraco's database and holds application data, saved report views and uploads. This separates application writes from CMS saves. Choose another named connection when needed; `Attachments:Connection` can select a separate upload store, including the optional `AttachmentsDb`. The application owns its business tables and schema changes.

## Where each rule belongs

Forms and reports are blocks on ordinary Umbraco pages, so start with Umbraco's own access rules. Protect the page with **Public Access**, by Member Group or by specific Members, and its blocks, their data and their files follow that page, including protection inherited from a parent page. Many sites need nothing more.

The other layers are there for rules a page cannot express. Requests pass through them in order, and each one can only narrow what the last allowed. Use the first layer that can state your rule, and remember that showing or hiding something in the browser is never one of them.

| Layer | What it decides | Set up in |
| --- | --- | --- |
| Sign-in | Who the caller is: an Umbraco Member or a backoffice User | Umbraco, including its external login providers |
| Public Access (the baseline) | Which Members may reach a page | The page's **Public Access** in the backoffice |
| Read groups and Write groups | Which Member Groups may use a data source | The data source |
| Row restriction | Which rows a caller can see and write | The data source's **Row restriction** |
| `IDataSourcePolicy` | Any rule a data source's settings cannot state | C#, registered with dependency injection |
| `IFormResource` | Everything about a custom form's operations | C#, your resource |
| `IAttachmentOwner` | Who may change a record's files | C#, on your resource |
| `IReportAccessPolicy` | What may be done with reports and saved views | C#, or `AppBlocks:ReportAdministratorGroups` |

Two things apply to every layer. First, Umbraco's Public Access is the baseline, and there is no site setting beside it. A block under Public Access, the page's own or inherited from an ancestor, refuses whoever that rule refuses. On a page without it, an anonymous visitor reaches the block, and whatever is behind the block decides what they get. **A C# class with no check is public on an unprotected page**, because your code is trusted to know what it is doing. And AppBlocks recognizes only Umbraco's Member and backoffice sign-ins, so a caller authenticated another way, such as with a bearer token or an API key, is not recognized: they count as anonymous on an unprotected page and are refused under Public Access. To use another identity system, sign people in through it as Umbraco Members or Users.

| Block on an unprotected page | What an anonymous visitor gets |
| --- | --- |
| Form bound to your C# class | Whatever the class decides. A class returning `Denied()` shows them a sign-in notice |
| Form on the data source engine | A sign-in notice, because its rules assume a known user |
| Report | Public: they can view, query and export. Saved views are hidden. A report whose SQL uses `:USER_` bind variables shows a sign-in notice. `IReportAccessPolicy` and `IDataSourcePolicy` can still refuse |
| Attachments | They can read the files if the owning record loads for them. They can change the files only if the owner's class implements `IAttachmentOwner` and allows it |
| Tabular form, Schedule, paper report | A sign-in notice, because these read and write through the data source engine |

## The authenticated caller

In Umbraco, a **Member** signs in to the website and can belong to **Member Groups**. A **User** signs in to the backoffice and can belong to **User Groups**. A **backoffice administrator** is a User in the Administrators User Group.

A C# `IFormResource` receives a `FormExecutionContext`. Despite its name, the `Member` property can contain an Umbraco Member's principal, a backoffice User's principal, both identities together or, on a page without Public Access, an unauthenticated principal for an anonymous visitor. It is never null. AppBlocks supplies the identities; your application decides which account types and groups may perform each operation. Never take the signed-in account's identity from submitted form fields.

Begin every operation with a sign-in check unless you mean that operation to be public, as generated samples do:

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
var account = await userResolver.ResolveAsync(context, cancellationToken);
if (account is null)
    return FormResult.Denied();

var ownerKey = account.Key;     // Member ID, or "backoffice:" followed by the User ID.
var name = account.Name;
var email = account.Email;
var groups = account.Groups;   // Member Group names, or backoffice User Group names.
var isMember = account.IsMember;
var isBackOfficeUser = account.IsBackOfficeUser;
var canEditPage = account.CanEditPage;
var canPublishPage = account.CanPublishPage;
var canDeletePage = account.CanDeletePage;
var isBackOfficeAdministrator = account.IsInUserGroup("admin"); // Umbraco's Administrators User Group alias.
```

A browser can be signed in to both accounts at once. In that case `account.IsMember && account.IsBackOfficeUser` is true. Each account has its own details; AppBlocks does not link them by matching names, emails or numeric IDs:

```csharp
var member = account.Member;                 // Null without a valid Member session.
var backOfficeUser = account.BackOfficeUser; // Null without a valid backoffice session.

var memberKey = member?.Key;
var memberEmail = member?.Email;
var memberGroups = member?.GroupNames;
var userKey = backOfficeUser?.Key;
var userEmail = backOfficeUser?.Email;
var userGroupNames = backOfficeUser?.GroupNames;
var userGroupAliases = backOfficeUser?.GroupAliases;
var canEdit = backOfficeUser?.CanEditPage == true; // Same answer as account.CanEditPage.
var canPublish = backOfficeUser?.CanPublishPage == true;
var canDelete = backOfficeUser?.CanDeletePage == true;
```

Both account objects also expose `Name` and `Sid`. For compatibility, the top-level `Key`, `Name`, `Email`, `Sid` and `Groups` use the Member when both are present, otherwise the only signed-in account. This also keeps `:USER_` bind variables, ownership and audit fields on the Member while both sessions are active. `MemberGroupNames` always describes the Member, and `UserGroupNames` and `UserGroupAliases` always describe the backoffice User; their groups are never merged. Each account's approval, lockout status and groups are read afresh on every operation. A disabled or locked account contributes no identity, groups or permissions.

The resolver accepts approved, unlocked Members and backoffice Users, regardless of group membership. `null` means there is no valid authenticated account. Resolving an account grants no business permission, and membership of the Administrators User Group grants no automatic application access.

The resolver reads the current account and groups rather than relying only on old cookie claims. An external login's provider key is available as `Sid` for compatibility; it is not necessarily a Windows SID on every site. `Groups` contains display names; `UserGroupAliases` contains current backoffice User Group aliases. Member Groups and User Groups are separate, even when their names match.

`account.CanEditPage`, `account.CanPublishPage` and `account.CanDeletePage` check the signed-in backoffice User's permissions on the page that called your C# code, including when a Member is also signed in. Pass the whole `FormExecutionContext` to `ResolveAsync` as above: its `PageKey` identifies the hosting page, including for API requests. AppBlocks asks Umbraco for each permission independently, including group permissions and content start nodes:

| Property | Umbraco permission |
| --- | --- |
| `CanEditPage` | Update |
| `CanPublishPage` | Publish |
| `CanDeletePage` | Delete |

All three are false without a valid backoffice account, when no page is supplied (the older `ResolveAsync(context.Member, ...)` overload), or when the page does not exist. A denied permission makes its corresponding property false; permission to edit does not imply permission to publish or delete. These are permission checks, not guarantees that a particular content change will pass validation. The same properties are available on `account.BackOfficeUser`. In an `IDataSourcePolicy`, use `request.User.CanEditPage`, `request.User.CanPublishPage` or `request.User.CanDeletePage`; AppBlocks has already resolved them for `request.Context.PageKey`. The results grant no application permission unless your rule uses them:

```csharp
var account = await userResolver.ResolveAsync(context, cancellationToken);
if (account is not { CanEditPage: true })
    return FormResult.Denied();
```

Umbraco's Public Access restrictions still protect Member pages. A backoffice User visiting without a Member session passes them only on a page they may edit, which is Umbraco's Update permission on it within their start nodes. When a Member session is present, its Public Access check still decides admission to the page; a refused Member is not replaced by a backoffice session. Once admitted, both signed-in accounts are available to your business rules. On pages without Member Public Access restrictions, either account type can reach your resource, where your code must enforce the application's rules.

## Group membership

Choose the rule your application needs:

| Application rule | Identity check |
| --- | --- |
| Any Umbraco Member | `account.IsMember` |
| Any backoffice User | `account.IsBackOfficeUser` |
| Both accounts signed in | `account.IsMember && account.IsBackOfficeUser` |
| A Member with no backoffice account | `account.IsMember && !account.IsBackOfficeUser` |
| A backoffice User who can edit the calling page | `account.CanEditPage` |
| A backoffice User who can publish the calling page | `account.CanPublishPage` |
| A backoffice User who can delete the calling page | `account.CanDeletePage` |
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

For the built-in data-source engine, **Read groups** and **Write groups** select Umbraco Member Groups, with these results:

| Read groups | Write groups | Who may read | Who may write |
| --- | --- | --- | --- |
| Empty | Empty | Anyone signed in who can reach the page, including backoffice Users | The same people |
| Set | Empty | Members in the read groups | The same Members |
| Empty | Set | Anyone signed in who can reach the page | Members in the write groups |
| Set | Set | Members in either list | Members in the write groups |

Two consequences are easy to miss. Empty groups do not mean "nobody": on a page without Public Access, any signed-in account can read and write. And there is no administrator exception, so setting a group refuses every backoffice User, Administrators included. When your rules involve backoffice Users, leave these settings empty and enforce the rules in `IDataSourcePolicy` using `request.User`. Each registered policy must allow the operation. Custom `IFormResource` implementations enforce their own rules on every operation.

**Row restriction** limits rows without any C#. It is added to every fetch and write, for example `OWNER_KEY = :USER_KEY` or `REGION IN (:USER_GROUPS)`, and a write that would leave the restriction is refused. It may use `:USER_KEY`, `:USER_SID`, `:USER_NAME`, `:USER_EMAIL`, `:USER_GROUPS` and `:NOW`. `:USER_GROUPS` holds the account's group display names, so a Member and a backoffice User are matched against different lists.

By default, whoever can read a record can read its files and, for a custom resource, change them too if they are signed in. An anonymous visitor can change them only through this interface. They are not told which account filed a file, only that someone signed in did. Implement `IAttachmentOwner` on the resource when the rule for changing files is narrower. The data source engine already limits changes to its write groups.

Interactive Reports exposes `IReportAccessPolicy` for report permissions. By default, any signed-in account that can reach a report's page can view, query and export it, and manage its own saved views. Report administration is separate: its group list is empty until a site configures `AppBlocks:ReportAdministratorGroups` with User Group aliases, and only backoffice Users can hold it. To make Members report administrators, supply your own policy. Umbraco's own backoffice management permissions remain separate from application business rules.

## Common scenarios

These are starting points, and they combine. Check the rule on every operation it applies to, because a resource's `Load`, `Create`, `Update` and `Delete` are separate requests.

| Scenario | Approach |
| --- | --- |
| Open to anyone, with no account | Leave the page unprotected and write a class with its own checks. See Public contact form |
| Members only | Protect the page with Public Access, or check `account.IsMember` |
| Backoffice Users only | Leave the page unprotected and check `account.IsBackOfficeUser` |
| Editors of the calling page only | Resolve with `context` and check `account.CanEditPage` |
| Members and backoffice Users together | Leave the page unprotected and check each account type's groups |
| Certain Member Groups | Read groups and Write groups, or `account.IsInMemberGroup(...)` |
| Certain User Groups | `account.IsInUserGroup(...)`, in a resource or policy |
| Single sign-on | Umbraco external logins, then any rule here. See below |
| Roles kept in your own database | A role table keyed by `account.Key`. See below |
| Groups from another system | A resolver that adds them. See below |
| Roles assigned on each Member | A custom Member property. See User Group selections on a Member |
| Each person sees only their own rows | Row restriction, or filter in your resource |
| Rules that depend on the record | `IDataSourcePolicy` or your resource. See Ownership and record status |

A person signed in as both a Member and a backoffice User can satisfy either account-type check or a rule requiring both. Use the account-specific properties when a rule depends on one identity's key, email or groups. Reports embedded in the backoffice itself continue to use only the backoffice account, including for saved-view ownership.

### Single sign-on

Configure the provider, such as Microsoft Entra ID, OpenID Connect or Windows authentication, as an Umbraco external login for Members or for Users. AppBlocks then sees the Umbraco account the login is linked to, and every rule in this guide applies to it. The provider's key for the person is available as `account.Sid` and `:USER_SID`.

AppBlocks reads groups from Umbraco, not from the provider. For provider groups to count, assign them to the account's Member Groups or User Groups when it signs in, or add them with a resolver as shown below. Other claims your sign-in keeps are on `context.Member`, but they are only as current as the cookie, so don't rely on them for a permission that must take effect immediately.

### Choosing an account key

Store something stable to identify an account in your own tables:

| Value | Use it for | Watch out for |
| --- | --- | --- |
| `account.Key` | Owners and roles within one site | It holds a Member ID, or `backoffice:` and a User ID, so it won't match across separate databases. Store it as text |
| `account.Sid` | People who sign in through a provider | Null without an external login. With several logins, it is the first one found |
| `account.Name` or `account.Email` | Display | Both can change, and may be reused by a later account, so don't use them to grant access |

### Roles in your own database

Your application can keep its own roles. Read them on each request so that a revoked role takes effect immediately:

```csharp
using System.Threading;
using System.Threading.Tasks;
using AppBlocks.Connections;
using AppBlocks.DataSources;

public sealed class ApplicationRoles(IApplicationConnectionFactory connections)
{
    public async Task<bool> HasRoleAsync(DataSourceUser account, string role, CancellationToken ct)
    {
        await using var connection = connections.CreateConnection("AppBlocksDb");
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM APP_ROLE WHERE ACCOUNT_KEY = @key AND ROLE_NAME = @role";
        foreach (var (name, value) in new[] { ("key", account.Key), ("role", role) })
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        return System.Convert.ToInt64(await command.ExecuteScalarAsync(ct)) > 0;
    }
}
```

Register it with `builder.Services.AddScoped<ApplicationRoles>()`, then call it from a resource or policy after resolving the account. Create and maintain `APP_ROLE` as you would any other business table, and adjust the parameter syntax if your database needs another form.

### Adding groups from another system

To bring in groups from a directory or your own tables, wrap the resolver. Read groups, Write groups, `:USER_GROUPS`, `account.IsInMemberGroup(...)` and your own code then see the combined list:

```csharp
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AppBlocks.DataSources;

public interface IExternalGroups
{
    Task<IReadOnlyList<string>> GetAsync(DataSourceUser account, CancellationToken ct);
}

public sealed class ResolverWithExternalGroups(UmbracoDataSourceUsers umbraco, IExternalGroups external) : IDataSourceUserResolver
{
    public async Task<DataSourceUser?> ResolveAsync(ClaimsPrincipal principal, CancellationToken ct)
        => await AddGroupsAsync(await umbraco.ResolveAsync(principal, ct), ct);

    public async Task<DataSourceUser?> ResolveAsync(AppBlocks.Forms.FormExecutionContext context, CancellationToken ct)
        => await AddGroupsAsync(await umbraco.ResolveAsync(context, ct), ct);

    private async Task<DataSourceUser?> AddGroupsAsync(DataSourceUser? account, CancellationToken ct)
    {
        if (account is null) return null;
        return account with { Groups = [.. account.Groups, .. await external.GetAsync(account, ct)] };
    }
}
```

Register `builder.Services.AddScoped<UmbracoDataSourceUsers>()` and `builder.Services.AddScoped<IDataSourceUserResolver, ResolverWithExternalGroups>()`, with your `IExternalGroups` implementation. The resolver runs on every operation, so cache a slow lookup briefly. Public Access still uses the account's real Member Groups, and `IsInUserGroup` still uses `UserGroupAliases`, which this example does not change.

Forward both resolver overloads as shown to preserve `CanEditPage`, `CanPublishPage` and `CanDeletePage` for the calling page. Existing custom resolvers that implement only the principal overload continue to work, but do not calculate page permissions.

## User Group selections on a Member

A Member Type can have a custom property that picks backoffice User Groups. This is a valid application-defined authorization model: your application can use those selections as roles, just as it can use Member Groups, actual User Group membership or another rule. Umbraco stores the selections as property data; your C# resource or policy gives them their application meaning.

Umbraco 18.3's built-in **User Group Picker** cannot be used for this directly. It isn't offered when creating a Data Type, and it fails to render a property that has no value yet, which leaves a new Member with a blank field and no way to choose. A small wrapper in your site solves both. Add these two files under `wwwroot/App_Plugins/ApplicationUserGroups/`:

```json
{
  "name": "Application User Group Picker",
  "version": "1.0.0",
  "extensions": [
    {
      "type": "propertyEditorUi",
      "alias": "Application.PropertyEditorUi.UserGroupPicker",
      "name": "Application User Group Picker",
      "element": "/App_Plugins/ApplicationUserGroups/user-group-picker.js",
      "meta": {
        "label": "Application User Group Picker",
        "icon": "icon-users",
        "group": "people",
        "propertyEditorSchemaAlias": "Umbraco.Plain.Json"
      }
    }
  ]
}
```

Save that as `umbraco-package.json`, and this as `user-group-picker.js`:

```javascript
import { UmbLitElement } from "@umbraco-cms/backoffice/lit-element";
import { html } from "@umbraco-cms/backoffice/external/lit";
import { UmbChangeEvent } from "@umbraco-cms/backoffice/event";

export default class ApplicationUserGroupPicker extends UmbLitElement {
    static properties = { value: { attribute: false } };

    render() {
        return html`<umb-user-group-input
            .selection=${(this.value ?? []).map(item => item.unique)}
            @change=${event => {
                this.value = event.target.selection.map(unique => ({ unique }));
                this.dispatchEvent(new UmbChangeEvent());
            }}></umb-user-group-input>`;
    }
}
customElements.define("application-user-group-picker", ApplicationUserGroupPicker);
```

After restarting the site:

1. In **Settings → Data Types**, create a Data Type and choose **Application User Group Picker**.
2. In **Settings → Member Types**, add a property using it, with the alias `applicationUserGroups`.
3. Leave the property's member-editing and profile-display options off, so Members can't grant themselves roles.

Anyone who can edit Members can now assign these roles, so treat access to the **Members** section accordingly.

The picker stores [Umbraco's own value format](https://github.com/umbraco/Umbraco-CMS/blob/release-18.3.0/src/Umbraco.Web.UI.Client/src/packages/user/user-group/property-editor/user-group-picker/property-editor-ui-user-group-picker.element.ts): a JSON array of references, each containing a `unique` value with the User Group's GUID key. These stored values are keys, not names or aliases:

```json
[
  { "unique": "765ef0cc-1932-4c10-9f10-25e7e138c406" }
]
```

The following application-owned C# class resolves the current Member from the authenticated principal, reads the property, and loads the referenced groups through `IUserGroupService`. It returns each group's key, current display name and current alias. Empty or malformed selections return no groups, so a bad value refuses access rather than granting it; deleted groups are omitted.

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
var account = await userResolver.ResolveAsync(context, cancellationToken);
if (account is null) return FormResult.Denied();

var selectedGroups = await memberUserGroupSelections.GetAsync(context.Member, cancellationToken);
var canApprove = account.IsInUserGroup("orderApprovers")
    || selectedGroups.Any(group => group.Alias.Equals("orderApprovers", StringComparison.OrdinalIgnoreCase));
if (!canApprove) return FormResult.Denied();

// Perform this application's approval operation.
```

The same helper can be injected into an `IDataSourcePolicy` and called with `request.Context.Member`. Enforce the chosen rule on every relevant operation. A developer can also enumerate `selectedGroups` or compare `group.Key` when a particular group's identity matters independently of its editable alias, which is the safer choice for a rule that must survive an alias change.

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

## Public contact form

A contact form needs no account, so it needs a page with no Public Access, on the page or any ancestor, and a class that makes its own checks. Nothing else is switched on. This class only creates: there is no record to load, and no key on the page's address. The antiforgery token works for a visitor with no identity, including on their first request, and validation messages show as usual.

Everything a developer would check for a signed-in person is still the class's job. The Anti-spam block can take the spam checks off its hands with no code, as described under Anti-spam. This recipe is for a class that checks for itself, with a honeypot and a captcha token. Neither is a field of the form, so they arrive in `FormExecutionContext.FormData`, a read-only dictionary of the form's other named controls. `FormData` is apart from the submitted values, so field validation and the strict binding of `ContactMessage` never see it.

```csharp
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using AppBlocks.Forms;

public sealed record ContactMessage(
    [property: Required, StringLength(100)] string Name,
    [property: Required, EmailAddress, StringLength(200)] string Email,
    [property: Required, StringLength(4000)] string Message);

public interface IContactInbox
{
    Task SaveAsync(ContactMessage message, string? submissionKey, CancellationToken ct);
}

/// <summary>Create-only and public: every other operation is refused, and creating needs a verified captcha token.</summary>
public sealed class ContactFormResource(ContactCaptcha captcha, IContactInbox inbox) : FormResource<ContactMessage, ContactMessage>
{
    public override string Alias => "site.contact";

    public override IReadOnlyList<FormField> Fields { get; } = [
        new("name", "Your name", "text", Required: true, MaxLength: 100),
        new("email", "Your email", "text", Required: true, MaxLength: 200),
        new("message", "Message", "text", Required: true, MaxLength: 4000),
    ];

    /// <summary>An empty form, ready for the next message. With no key, the page's address stays as it is.</summary>
    private static FormRecord Blank() => new(null, null, new Dictionary<string, object?>(), new FormCapabilities(Create: true));

    public override Task<FormResult> InitializeAsync(FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Ok(Blank()));

    public override Task<FormResult> LoadAsync(string key, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());

    public override Task<FormResult> DeleteAsync(string key, string version, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());

    protected override Task<FormResult> UpdateAsync(string key, ContactMessage input, string version, FormExecutionContext context, CancellationToken ct) => Task.FromResult(FormResult.Denied());

    protected override async Task<FormResult> CreateAsync(ContactMessage input, FormExecutionContext context, CancellationToken ct)
    {
        // A person never fills in the field they cannot see. Look successful, so a robot learns nothing.
        if (!string.IsNullOrEmpty(context.FormData.GetValueOrDefault("website"))) return FormResult.Ok(Blank());

        if (!await captcha.VerifyAsync(context.FormData.GetValueOrDefault("g-recaptcha-response"), ct))
            return FormResult.Invalid("", "Please confirm you are not a robot.");

        await inbox.SaveAsync(input, context.SubmissionKey, ct);
        return FormResult.Ok(Blank());
    }
}

/// <summary>Asks the captcha service about a token. The visitor's address comes from the request, not from the form.</summary>
public sealed class ContactCaptcha(IHttpClientFactory clients, IHttpContextAccessor http, IConfiguration configuration)
{
    public async Task<bool> VerifyAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var fields = new Dictionary<string, string> { ["secret"] = configuration["Captcha:Secret"] ?? "", ["response"] = token };
        if (http.HttpContext?.Connection.RemoteIpAddress is { } address) fields["remoteip"] = address.ToString();
        try
        {
            using var response = await clients.CreateClient().PostAsync("https://www.google.com/recaptcha/api/siteverify", new FormUrlEncodedContent(fields), ct);
            if (!response.IsSuccessStatusCode) return false;
            using var answer = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return answer.RootElement.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True;
        }
        // No answer is no proof. Refuse, and let the visitor try again.
        catch (Exception error) when (error is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested) { return false; }
    }
}
```

Register them, and add `IHttpContextAccessor` if your site does not have it already. Behind a proxy, `RemoteIpAddress` is the proxy's address unless you configure ASP.NET Core's forwarded headers. `IContactInbox` is your own storage or mail:

```csharp
builder.Services.AddHttpClient();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ContactCaptcha>();
builder.Services.AddScoped<IContactInbox, MyContactInbox>();
builder.Services.AddScoped<IFormResource, ContactFormResource>();
```

The honeypot is an ordinary named control inside the form, hidden from people. A captcha widget adds its own hidden control there in the same way, under a name such as `g-recaptcha-response`:

```html
<input type="text" name="website" tabindex="-1" autocomplete="off" aria-hidden="true" style="position:absolute;left:-9999px">
```

The browser runtime sends every named control inside the form except its bound fields, password and file inputs, and the antiforgery field. On the server, `FormData` is bounded the way `Parameters` is: at most 50 names of letters, digits, `_`, `-`, `.` and `:`, up to 64 characters, each with one value of at most 16,384 characters, which leaves room for a real captcha token. A repeated name is left out rather than guessed. AppBlocks stores nothing from it and validates nothing in it, and like any other browser input it can say anything, so verify what matters.

Your class runs twice for each anonymous page view, once when the page renders and once when the runtime loads the form, because the page shows a class's refusal as a sign-in notice in place of the form. Keep `InitializeAsync` and `LoadAsync` cheap and free of side effects.

AppBlocks does not limit how often anyone calls a public form or report. Add rate limiting in your application if the page will be exposed to the internet. Limiting how often, under Anti-spam, has a recipe for ASP.NET Core's rate limiter on `/api/forms`.

A select field using a published list of values reads it through the data source engine, which refuses someone anonymous. On a public form, give the field its choices in your class's `FormField.Choices` instead.

## Anti-spam

Anonymous visitors can use forms on open pages, and so can bots. The **Anti-spam** block protects a form with no code. An editor places it inside the form, in any area of it, and chooses a service. AppBlocks renders the service's widget and script, then checks every create and update on the server before your class is called. A class gets this protection without writing any spam code.

| Service | What the editor sets | What a visitor sees |
| --- | --- | --- |
| Honeypot | Nothing | Nothing. It is an empty control kept out of sight, needing no account and loading nothing. |
| Cloudflare Turnstile | Site key, theme, size | A small widget, which usually completes by itself. |
| Google reCAPTCHA v2 | Site key, theme, size | The "I'm not a robot" checkbox, sometimes with a picture puzzle. |
| Google reCAPTCHA v3 | Site key, score threshold | Nothing in the form, only Google's badge. The score threshold defaults to 0.5, and a lower score is refused. |
| hCaptcha | Site key, theme, size | A checkbox, sometimes with a picture puzzle. |

Use one block per form. Publishing refuses a second one, a block outside a form, an unknown service, a score outside 0 to 1, and a site key that is really one of the site's secret keys. A page using both reCAPTCHA versions needs them in separate pages, because Google's script loads for one of them at a time.

### Keys

A site key is public, so it can go on the block. Leave it empty there to use a site-wide one. A secret key stays on the server and never appears in block content, exports, API responses or logs. Set each in configuration or in **Settings → AppBlocks → Anti-spam**:

```json
{
  "AppBlocks": {
    "AntiSpam": {
      "turnstile": { "SiteKey": "public-site-key", "SecretKey": "secret-key" }
    }
  }
}
```

The sections are `turnstile`, `recaptchaV2`, `recaptchaV3` and `hcaptcha`. Configuration wins, and the screen cannot change what it sets. A secret key saved on the screen is encrypted with this installation's Data Protection keys, as a connection string is, in `App_Data/AppBlocks/anti-spam.json`. If those keys change, the screen says the secret can no longer be read, and you save it again. The screen shows whether a secret is set, never its value.

### What happens to a submission

The server checks the antiforgery token, then the fields, then the Anti-spam block, then runs your class. Fields come first so that a typo does not spend a token, which works only once. The runtime gets a fresh token after each attempt.

- A failed check, a missing token and a filled-in honeypot all return the same 422 and the same message. The class is not called, and nothing tells a bot which it was.
- A request cannot remove the block, because the server reads it from the published page.
- If the service cannot be reached within 5 seconds, or the secret or site key is missing, or the service rejects the site's keys, the submission is refused with a friendly message to try later. The problem is logged once, shown on the settings screen and cleared when the service next answers. Publishing also warns when a secret is missing.
- The token is not stored, and your class does not receive it. Your class still gets the form's other controls in `FormData`, and a form without the block still gets everything, so a class can keep checking a token itself as in Public contact form.
- Deleting is not checked, because it only acts on a record your class allows.
- AppBlocks does not send the visitor's address to the service.

Signed-in Members and backoffice Users go through the same check, because an account can be stolen or scripted too. The one exception is an editor in Umbraco's preview. Preview shows a note in place of the widget, including anything that would turn visitors away, loads no third-party script and skips the check for that editor. The preview cookie alone earns nothing: it has to hold a token Umbraco issued to a backoffice User.

### Third-party scripts

Turnstile, reCAPTCHA and hCaptcha load third-party scripts in the visitor's browser and may need cookie consent on some sites. Consent handling stays with the site, and until the script loads the visitor is told the check could not load and cannot submit. The honeypot loads nothing from anywhere else.

A site with its own Content-Security-Policy has to allow the scripts and frames. If it sets a nonce in `HttpContext.Items` under `CspNonce`, the block's script tag carries it. Each service's own documentation has the current list, which is at least:

| Service | `script-src` and `frame-src` |
| --- | --- |
| Turnstile | `https://challenges.cloudflare.com` |
| reCAPTCHA | `https://www.google.com` and, for `script-src`, `https://www.gstatic.com` |
| hCaptcha | `https://hcaptcha.com` and `https://*.hcaptcha.com`, also for `style-src` and `connect-src` |

### Adding another service

A service is a small class implementing `IAntiSpamService`, registered with `builder.Services.AddSingleton<IAntiSpamService, MyService>()`. It says which script to load, which form control holds the token and how to verify a token. The block's picker then offers it, and its keys go under its alias like the others. Derive from `SiteVerifyService` if the service takes a form post and answers with `success` and `error-codes`. Post only to the service's own fixed address, never one that content supplies.

### Limiting how often

The Anti-spam block is not a rate limit. It decides whether one submission looks like a person, and anyone can still call a public form as often as they like. Each call with a token also costs your server a request to the service. ASP.NET Core's rate limiter covers this in the site's `Program.cs`. This allows 10 submissions a minute from each address, and leaves loading a form alone:

```csharp
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
        http.Request.Path.StartsWithSegments("/api/forms") && !HttpMethods.IsGet(http.Request.Method)
            ? RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) })
            : RateLimitPartition.GetNoLimiter("not limited"));
});

var app = builder.Build();

// Before app.UseUmbraco(), so it runs ahead of the form endpoints.
app.UseRateLimiter();

app.Run();
```

Behind a proxy, use ASP.NET Core's forwarded headers first, or every visitor counts as the proxy's address.

A form submission is also limited to 1 MB, which leaves room for long text fields because files go through Attachments. A larger one gets a 413 before it is parsed. Change it with `AppBlocks:Forms:MaxBodySize`, in bytes.

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

A hidden field, a disabled control, a query parameter (`FormExecutionContext.Parameters`, `:PARAM.name`) or another named control (`FormExecutionContext.FormData`) is still browser input. Do not use it as the authority for an owner, approval, price, permission or audit value. Bind SQL parameters for submitted values; never concatenate them into SQL.

## Concurrency and duplicate submissions

The generated sample returns an ID and version from its read. An upsert uses that version and returns `false` when the stored version no longer matches. Show a conflict and let the user reload rather than silently overwriting the newer record. A stale update must not recreate a deleted record.

The built-in engine handles optimistic concurrency and create receipts. Custom resources should make creation idempotent when a retry could create a duplicate; `FormExecutionContext.SubmissionKey` is available for that purpose. Persist the receipt and the new record together.

## Wiring a custom resource

Implement `IFormResource`, or derive from `FormResource<TCreate, TUpdate>` for typed input and data-annotation validation. Register it as an `IFormResource`, choose its alias in the Form interface and map the field blocks to its field descriptors. The generated repository is a persistence example to call from those operations, not an automatically registered form resource.

Return `FormResult.Denied()` for refused operations, `Missing()` for absent records, `Conflict()` for stale updates, `Invalid(field, message)` for validation, and `Ok(record)` for success. Set the returned `FormCapabilities` to describe the actions the caller may see, and enforce the same rules again when an action is submitted.
