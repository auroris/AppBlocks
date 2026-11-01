[CmdletBinding()]
param(
    [int] $Port = 5189,
    [string] $InteractiveReportsRoot = (Join-Path $PSScriptRoot '../../../Interactive-Reports'),
    [string] $PressroomRoot = (Join-Path $PSScriptRoot '../../../Pressroom')
)
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$run = Join-Path $repository ('artifacts/clean-install-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
$feed = New-Item -ItemType Directory -Path (Join-Path $run 'feed')
$address = "http://localhost:$Port"
$password = 'Clean!AppBlocks-' + [guid]::NewGuid().ToString('N')
$version = ([xml](Get-Content (Join-Path $repository 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
function Invoke-Checked([string] $Log, [scriptblock] $Command) {
    & $Command *> (Join-Path $run $Log)
    if ($LASTEXITCODE -ne 0) {
        Get-Content (Join-Path $run $Log) -Tail 60
        throw "Command failed; see $run/$Log"
    }
}
function Verify-Site([string] $Site, [string] $Stage) {
    $project = Get-ChildItem -LiteralPath $Site -Filter '*.csproj' | Select-Object -First 1
    $executable = Join-Path $Site "bin/Debug/net10.0/$($project.BaseName).exe"
    $process = Start-Process -FilePath $executable -ArgumentList @('--urls', $address, '--environment', 'Development') -WorkingDirectory $Site -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $run "$Stage-server.log") -RedirectStandardError (Join-Path $run "$Stage-server-error.log")
    try {
        $ready = $false
        for ($attempt = 0; $attempt -lt 90; $attempt++) {
            if ($process.HasExited) { throw "Host exited; see $Stage-server-error.log" }
            try { $ready = (Invoke-WebRequest "$address/umbraco" -TimeoutSec 2).StatusCode -eq 200 } catch { }
            if ($ready) { break }
            Start-Sleep -Seconds 1
        }
        if (!$ready) { throw "Host did not start; see $Stage-server.log" }
        $env:APPBLOCKS_CLEAN_SITE = $Site
        $env:APPBLOCKS_CLEAN_URL = $address
        $env:APPBLOCKS_CLEAN_STAGE = $Stage
        $env:INTERACTIVE_REPORTS_ROOT = (Resolve-Path $InteractiveReportsRoot).Path
        if ($Stage -eq 'core') {
            Invoke-Checked 'wizard-tests.log' { node (Join-Path $PSScriptRoot 'wizard.mjs') }
            Get-Content (Join-Path $run 'wizard-tests.log')
        }
        Invoke-Checked "$Stage-tests.log" { node (Join-Path $PSScriptRoot 'verify.mjs') }
        Get-Content (Join-Path $run "$Stage-tests.log")
    } finally {
        if (!$process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }
        $process.Dispose()
    }
    $startup = Get-Content (Join-Path $run "$Stage-server.log") -Raw
    if ($startup -match '\[FTL\]|The factory has not been configured with a proper connection string') {
        throw "Fatal startup error; see $Stage-server.log"
    }
}
Write-Host "Clean install artifacts: $run"
Push-Location $repository
$previous = @{}
foreach ($name in @('APPBLOCKS_CLEAN_SITE', 'APPBLOCKS_CLEAN_URL', 'APPBLOCKS_CLEAN_STAGE', 'INTERACTIVE_REPORTS_ROOT')) { $previous[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    Invoke-Checked 'template.log' { dotnet new install Umbraco.Templates@18.3.0 --force }
    $projects = @(
        (Join-Path $InteractiveReportsRoot 'src/InteractiveReport.Core'),
        (Join-Path $InteractiveReportsRoot 'src/InteractiveReport.AspNetCore'),
        (Join-Path $InteractiveReportsRoot 'src/InteractiveReport.Client.Json'),
        (Join-Path $InteractiveReportsRoot 'src/InteractiveReport.Client.FileDownload'),
        (Join-Path $PressroomRoot 'src/Pressroom.Client'),
        'src/AppBlocks', 'src/AppBlocks.InteractiveReports', 'src/AppBlocks.Documents'
    )
    foreach ($project in $projects) {
        $name = Split-Path $project -Leaf
        Invoke-Checked "pack-$name.log" { dotnet pack $project -c Release -o $feed.FullName --nologo -v minimal "-p:InteractiveReportsRoot=$InteractiveReportsRoot" "-p:PressroomRoot=$PressroomRoot" }
    }
    @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="clean" value="feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <config><add key="globalPackagesFolder" value="cache" /></config>
</configuration>
'@ | Set-Content (Join-Path $run 'NuGet.Config')
    foreach ($mode in @('core', 'companions')) {
        $site = Join-Path $run $mode
        if ($mode -eq 'core') {
            Invoke-Checked "new-$mode.log" { dotnet new umbraco -n CleanAppBlocks -o $site --package-management PerProject --models-mode Nothing --no-restore }
        } else {
            Invoke-Checked "new-$mode.log" { dotnet new umbraco -n CleanAppBlocks -o $site --package-management PerProject --development-database-type SQLite --friendly-name 'Clean install tester' --email clean@appblocks.test --password $password --telemetry-level Minimal --models-mode Nothing --no-restore }
        }
        @{ UnattendedUserName = 'Clean install tester'; UnattendedUserEmail = 'clean@appblocks.test'; UnattendedUserPassword = $password } | ConvertTo-Json | Set-Content (Join-Path $site 'test-account.json')
        $packages = if ($mode -eq 'core') { @('AppBlocks') } else { @('AppBlocks.InteractiveReports', 'AppBlocks.Documents') }
        foreach ($package in $packages) {
            Invoke-Checked "add-$package.log" { dotnet add "$site/CleanAppBlocks.csproj" package $package --version $version --no-restore }
        }
        $file = Join-Path $site 'appsettings.Development.json'
        $settings = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json -AsHashtable
        $settings.Umbraco.CMS.Global = @{ UseHttps = $false }
        $settings.Umbraco.CMS.Security = @{ AllowConcurrentLogins = $true }
        $settings.Umbraco.CMS.WebRouting = @{ UmbracoApplicationUrl = $address }
        $settings | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $file
        Invoke-Checked "restore-$mode.log" { dotnet restore "$site/CleanAppBlocks.csproj" --configfile "$run/NuGet.Config" --nologo }
        Invoke-Checked "build-$mode.log" { dotnet build "$site/CleanAppBlocks.csproj" --no-restore --nologo -v minimal }
        Verify-Site $site $mode
        if ($mode -eq 'companions') {
            # Add ordinary website content only after automatic installation has passed on the empty site.
            Copy-Item samples/AppBlocks.Demo/DemoSeed.cs,samples/AppBlocks.Demo/DemoTasks.cs -Destination $site
            Copy-Item (Join-Path $PSScriptRoot 'SeedComposer.cs') -Destination $site
            Copy-Item samples/AppBlocks.Demo/Views/* -Destination "$site/Views" -Recurse -Force
            Copy-Item (Join-Path $PSScriptRoot 'Views/CleanDocument.cshtml') -Destination "$site/Views"
            $settings.AppBlocksDemo = @{ SeedContent = $true }
            $settings | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $file
            Invoke-Checked 'build-seeded.log' { dotnet build "$site/CleanAppBlocks.csproj" --no-restore --nologo -v minimal }
            Verify-Site $site 'functional'
            Verify-Site $site 'restart'
        }
        $published = Join-Path $run "publish-$mode"
        Invoke-Checked "publish-$mode.log" { dotnet publish "$site/CleanAppBlocks.csproj" --no-restore --nologo -v minimal -o $published }
        if (!(Test-Path (Join-Path $published 'oracle/Oracle.ManagedDataAccess.dll'))) { throw "The Oracle driver is missing from the $mode site's publish output." }
    }
    Write-Host "Clean installation checks passed. Results: $run/results"
} finally {
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
    Pop-Location
}
