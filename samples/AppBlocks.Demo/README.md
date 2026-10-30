# Plain Umbraco smoke site

Run `dotnet run --project samples/AppBlocks.Demo --urls http://localhost:5079` and use the native Umbraco installer. The only AppBlocks integration is its `AppBlocks.InteractiveReports` project reference, which brings in the required AppBlocks base package. Both packages register through their composers. No custom composer, startup call, identity server, theme, application database or starter export is required.

After sign-in, open Settings → AppBlocks. The package installs its own missing document/data types. Its default connection uses a separate `AppBlocks.sqlite.db` beside Umbraco's SQLite database. Forms, saved report views and uploads share that application database by default. The optional demo seed creates its task table there.

Local automated smoke checks may supply an ignored `appsettings.Development.local.json` with unattended installation settings. Do not commit that file or publish it with credentials.
