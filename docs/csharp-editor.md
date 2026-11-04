# The C# section

**Settings → C#** edits a folder of C# that the site compiles while it runs, the way Umbraco's development mode compiles Razor views. A class in the folder that implements one of AppBlocks' extension points is used beside the classes compiled into the site, without a build or a restart:

| Extension point | What it is for |
| --- | --- |
| `IFormResource`, or `FormResource<TCreate, TUpdate>` | A form class, picked in a form's **Form Interface** |
| `IDataSourceHandler` | A backing class, picked in a SQL data source's **Backing class** |
| `IDataSourcePolicy` | A rule every data source operation must pass |
| `IBindVariableProvider` | Values for `:APP.name` |

The [business rules guide](business-rules.md) describes each of them. The section adds nothing to what a class can do; it only changes where the class is compiled.

## The development-mode package

AppBlocks never ships or references a compiler. It uses the one the site brings: `Umbraco.Cms.DevelopmentMode.Backoffice`, the package Umbraco uses for runtime Razor compilation and the InMemoryAuto models builder, depends on `Microsoft.CodeAnalysis.CSharp`. AppBlocks finds it by type name at runtime, as it finds the Npgsql and Oracle drivers.

```bash
dotnet add package Umbraco.Cms.DevelopmentMode.Backoffice
```

Umbraco's own runtime-mode validation refuses that package in Production mode, so code is compiled here only on development sites.

### Read-only without it

Without the package, the section is read-only. It lists the folder and shows each file, but nothing can be created, saved, renamed or deleted, and a notice names the package to install. Nothing in the folder is compiled or used: a form or data source that names one of its classes is unavailable, as it is when a class compiled into the site is removed.

## The folder

The folder is `AppBlocks/Code` under the site's content root. It may contain subfolders. To move it, set both of these:

```json
{ "AppBlocks": { "CodeDirectory": "App_Code/AppBlocks" } }
```

```xml
<PropertyGroup><AppBlocksCodeDirectory>App_Code/AppBlocks</AppBlocksCodeDirectory></PropertyGroup>
```

The MSBuild property is read by a build file in the AppBlocks package, which removes the folder from the site project's `Compile` items. The files are therefore never compiled into the site as well, and each class is only one type. A site that references AppBlocks as a project, as the demo does, imports `src/AppBlocks/buildTransitive/AppBlocks.targets` itself.

The folder is created by the first file or folder saved in the section, not on startup. The section takes only paths inside it:

- Names use letters, digits, `_`, `-` and `.`, start with a letter, digit or `_`, have at most 100 characters, and are not device names such as `CON`.
- Files end in `.cs`. Other files in the folder are neither listed nor compiled.
- `..`, rooted and drive paths are refused, and so is any path through a symbolic link or junction below the folder. Links are not listed.
- Folders nest at most 8 deep, a file holds at most 512 KB, and the folder holds at most 500 files.

Keep the folder in source control with the rest of the site.

## Compiling

The whole folder compiles into one assembly, loaded into a collectible `AssemblyLoadContext`. It references the .NET framework and the assemblies the site has loaded, which include AppBlocks, Umbraco and the site's own project, so a file can use any type they have. The Web SDK's implicit usings are in place, nullable reference types are enabled, and the code is optimized.

The folder compiles on startup, when a file is saved, created, renamed or deleted in the section, and when a file watcher sees a change made outside the backoffice, such as by an IDE, after a short pause. The first request waits for the startup compilation, so a data source that names a compiled backing class works from the first request.

A compilation with errors changes nothing that is running. The classes from the last compilation that succeeded stay in use until the folder compiles again, so a mistake saved halfway through an edit never takes a working class away. The compiler's errors and warnings are listed with their file, line and column, and are marked in the open file. Each compilation is written to the log.

### Which classes are used

Each public, non-abstract class implementing one of the extension points is made for each request through dependency injection, so its constructor takes services as a registered class's would. The registries ask for compiled classes after the ones compiled into the site:

- A form class or backing class whose alias a class compiled into the site already has is not used, and the section reports it. The site's class keeps the alias.
- Of two compiled classes with the same alias, the first by full type name is used.
- Compiled policies are asked as one more registered policy: each must allow the operation.
- Compiled bind variable providers are asked before the site's own, so a registered provider wins a name they share.

The section also reports a class that is not public, a class that cannot be constructed, and a backing class alias that is not one. These are marked `AB0001` to `AB0005`. A class compiled from the folder is not itself registered with dependency injection, so other services cannot ask for it.

Pickers fetch their choices again after the section compiles, so a new form class or backing class is offered at once.

### Unloading

When a compilation succeeds, the previous assembly's load context is unloaded. The runtime completes that only once nothing references its types. A request still running keeps the old class until it finishes, and anything that holds an old type for longer, such as a static cache or a background task the class started, keeps the old assembly in memory. Unloading is therefore not guaranteed. A site whose memory grows after many compilations can be restarted.

## Who can use it

The section appears to backoffice Users in the Administrators group and in any User Group whose alias `AppBlocks:CodeEditorGroups` lists:

```json
{ "AppBlocks": { "CodeEditorGroups": [ "developers" ] } }
```

Every endpoint checks the same rule, and also requires access to the Settings section. Every save, create, rename and delete is written to Umbraco's audit log with the entity type `AppBlocks C#` and the path.

## Security

Code saved in the section runs inside the site, with the site's full rights: its database connections, its file system and its configuration. Giving someone the section is giving them the server. Keep the groups small, and treat the folder like the rest of the site's code: review changes, and keep them in source control.

The section is for development. Production sites run without the development-mode package, so they compile nothing from the folder. To ship a class, move its file into the site's project and register it with dependency injection, such as `services.AddScoped<IFormResource, OrdersForm>()`. Remove it from the folder at the same time, or the build file leaves it out of the project.

A form class owns its own authorization. On a page with no Public Access, an anonymous visitor reaches it, and a class with no check is public. Classes the wizard writes check the caller in every operation; keep those checks when you change them.

## The New file wizard

**New file** offers a blank file, or the wizard, which writes a class for one table:

1. **Connection and table.** A configured connection and one of its tables or views.
2. **Recipe.**
   - A **form class** derives from `FormResource<TCreate, TUpdate>` and owns create, read, update and delete for the table through `IApplicationConnectionFactory`. Pick it in a form's Form Interface.
   - A **backing class** implements `IDataSourceHandler` for a SQL data source on the table. Choose the data source to list its field aliases in the class. Pick the class in the data source's **Backing class**.
3. **Access**, in the data source vocabulary:

   | Choice | Who may read | Who may write |
   | --- | --- | --- |
   | Public | Anyone the page admits, with or without an account | Any signed-in reader, or anyone with **Allow anonymous writes** |
   | Not public | Any signed-in Member or backoffice User | Any signed-in reader |
   | Specific groups | Members in a read or write Member group, and Users in a read or write User group | Any reader |

   Write groups narrow writes in every mode: with any, only readers in a write Member group or write User group may write. Anonymous writes need Public and no write groups. The choice becomes explicit checks in the class, using `IDataSourceUserResolver`, `IsInMemberGroup` and `IsInUserGroup` as the business rules guide does. Member groups are matched by name and User groups by alias; neither account type ever satisfies the other's groups.
4. **Names.** The class name, the alias and the folder. Both names are suggested from the table.
5. **Preview.** Read the class, then **Create file**. It compiles at once.

### What a form class does

- Every operation resolves the caller first, and refuses one that may not read or write. An account that no longer resolves is refused, never treated as anonymous.
- The SQL is written for the connection's database (SQLite, SQL Server, PostgreSQL or Oracle), with quoted names and every value a bound parameter.
- Last save wins by default. Set `UseChecksum = true` in a generated form class to detect conflicting edits without adding a column. Update and delete then compare the loaded checksum with the current row inside the write transaction and refuse a stale change.
- Fields follow the columns: their kind, whether they are required, a text column's length (4,000 characters when it has none) and an integer column's range. A computed or database-generated column is read-only, and binary columns are left out.

The table needs a one-column primary or unique key, of a number or text. Choose a SQL data source for a table without one.

### What a backing class does

The data source checks its own Access, groups and allow flags before it asks a backing class, so the class's checks can only narrow access, never widen it. Every hook checks the caller, and the write hooks show where to change or refuse values.

## Limits

- Only assemblies the site has loaded can be referenced. A package the site does not use cannot be added from the section.
- Static state starts again with each compilation, because each compilation's types are new types.
- Only the four extension points above are read from the folder. Other services, such as an `IProtectedPageAdmission`, still belong in the site's project.
- The wizard writes a class once. It never changes a file again, and a table that changes needs its class changed by hand.
- The downloadable sample on **Settings → AppBlocks → Forms** is still an example AppBlocks never compiles or runs, whether or not the section is available.
