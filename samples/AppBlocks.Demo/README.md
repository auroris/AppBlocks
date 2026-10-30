# Plain Umbraco smoke site

Run `dotnet run --project samples/AppBlocks.Demo --urls http://localhost:5079` and use the native Umbraco installer. The only AppBlocks integration is its project reference. No custom composer, startup call, identity server, theme, application database or starter export is required.

After sign-in, open Settings → AppBlocks. The package installs its own missing document/data types. Its default connection follows the database chosen in the Umbraco installer.

Local automated smoke checks may supply an ignored `appsettings.Development.local.json` with unattended installation settings. Do not commit that file or publish it with credentials.
