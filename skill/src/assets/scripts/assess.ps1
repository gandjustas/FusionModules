#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Inventories a .NET solution for a Modulith migration.

.DESCRIPTION
    Produces one JSON document describing the projects, the calls between services, the data
    topology, the deployment artefacts and the collisions that a merge would make silent.

    The point is determinism. Reading a solution ad hoc produces a different picture every time
    and a confident conclusion from an incomplete one; this always looks at the same things.

    It is a starting point, not an oracle: regular expressions find call sites, not a compiler.
    Treat anything it reports as something to confirm, and anything it misses as still possible.

.PARAMETER Path
    Solution root. Defaults to the current directory.

.PARAMETER Output
    Where to write the JSON. Defaults to stdout.

.EXAMPLE
    pwsh assess.ps1 -Path ./src -Output modulith-assessment.json
#>
[CmdletBinding()]
param(
    [string] $Path = '.',
    [string] $Output
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path $Path).Path
$excluded = '[\\/](bin|obj|node_modules|\.git)[\\/]'

function Get-SourceFiles {
    Get-ChildItem -Path $root -Recurse -File -Include *.cs |
        Where-Object { $_.FullName -notmatch $excluded }
}

function Get-RelativePath([string] $full) {
    [IO.Path]::GetRelativePath($root, $full) -replace '\\', '/'
}

# --- Projects ---------------------------------------------------------------------------------

$projects = Get-ChildItem -Path $root -Recurse -File -Include *.csproj |
    Where-Object { $_.FullName -notmatch $excluded } |
    ForEach-Object {
        # XPath rather than property access: most of these elements are absent from most project
        # files, and under StrictMode a missing property is an error rather than a null.
        $xml = [xml](Get-Content $_.FullName -Raw)
        $file = $_

        function Property([string] $name) {
            $xml.SelectNodes("//PropertyGroup/$name") |
                ForEach-Object { $_.InnerText.Trim() } |
                Where-Object { $_ } |
                Select-Object -First 1
        }

        function Attributes([string] $element) {
            @($xml.SelectNodes("//ItemGroup/$element/@Include") | ForEach-Object { $_.Value })
        }

        [pscustomobject]@{
            path            = Get-RelativePath $file.FullName
            name            = $file.BaseName
            sdk             = $xml.DocumentElement.GetAttribute('Sdk')
            targetFramework = @((Property 'TargetFramework'), (Property 'TargetFrameworks')) | Where-Object { $_ } | Select-Object -First 1
            outputType      = Property 'OutputType'
            assemblyName    = @((Property 'AssemblyName'), $file.BaseName) | Where-Object { $_ } | Select-Object -First 1
            isExecutable    = Test-Path (Join-Path $file.Directory 'Program.cs')
            packages        = @(Attributes 'PackageReference' | Sort-Object -Unique)
            projectRefs     = Attributes 'ProjectReference'
        }
    }

# --- Inherited properties ---------------------------------------------------------------------
#
# A property set in Directory.Build.props is invisible to the per-project read above, and
# TargetFramework in particular is often set there. Reported separately rather than resolved:
# resolving properly means an MSBuild evaluation per project, which turns seconds into minutes.

$directoryBuildProps = Get-ChildItem -Path $root -Recurse -File -Include 'Directory.Build.props','Directory.Build.targets','Directory.Packages.props' |
    Where-Object { $_.FullName -notmatch $excluded } |
    ForEach-Object {
        $xml = [xml](Get-Content $_.FullName -Raw)
        [pscustomobject]@{
            path       = Get-RelativePath $_.FullName
            properties = @(
                $xml.SelectNodes('//PropertyGroup/*') |
                    ForEach-Object { [pscustomobject]@{ name = $_.LocalName; value = $_.InnerText.Trim() } }
            )
        }
    }

# --- Call sites between services --------------------------------------------------------------

$callPatterns = [ordered]@{
    http     = 'AddHttpClient|HttpClient\s*\(|GetFromJsonAsync|PostAsJsonAsync|\.SendAsync\s*\('
    grpc     = 'AddGrpcClient|GrpcChannel\.ForAddress'
    rabbitmq = 'IBus\b|IRpc\b|EasyNetQ|IPublishEndpoint|MassTransit|IConnectionFactory'
    azure    = 'ServiceBusClient|QueueClient|EventHubProducerClient'
    kafka    = 'IProducer<|IConsumer<|ProducerBuilder|ConsumerBuilder'
}

$calls = foreach ($file in Get-SourceFiles) {
    $lines = Get-Content $file.FullName
    for ($i = 0; $i -lt $lines.Count; $i++) {
        foreach ($transport in $callPatterns.Keys) {
            if ($lines[$i] -match $callPatterns[$transport]) {
                [pscustomobject]@{
                    transport = $transport
                    file      = Get-RelativePath $file.FullName
                    line      = $i + 1
                    text      = $lines[$i].Trim()
                }
            }
        }
    }
}

# --- Data -------------------------------------------------------------------------------------

# The optional parameter list is not decoration: a primary constructor is the usual shape now —
# class AppDbContext(DbContextOptions options) : DbContext(options) — and a pattern without it
# reports no contexts at all rather than failing.
$contextPattern = 'class\s+(\w+)\s*(\([^)]*\))?\s*:\s*(\w+\.)*DbContext'

$contexts = foreach ($file in Get-SourceFiles) {
    $content = Get-Content $file.FullName -Raw
    foreach ($match in [regex]::Matches($content, $contextPattern)) {
        [pscustomobject]@{
            name     = $match.Groups[1].Value
            file     = Get-RelativePath $file.FullName
            entities = @([regex]::Matches($content, 'DbSet<(\w+)>') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
        }
    }
}

$providers = $projects |
    ForEach-Object { $_.packages } |
    Where-Object { $_ -match 'EntityFrameworkCore\.(SqlServer|Sqlite|Cosmos|InMemory)$|^Npgsql\.|^Pomelo\.' } |
    Sort-Object -Unique

# --- Routes -----------------------------------------------------------------------------------

$routes = foreach ($file in Get-SourceFiles) {
    $lines = Get-Content $file.FullName
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match 'Map(Get|Post|Put|Delete|Patch|Group|ControllerRoute|RazorPages)\s*(<[^>]*>)?\s*\(\s*"([^"]*)"') {
            [pscustomobject]@{
                template = $Matches[3]
                file     = Get-RelativePath $file.FullName
                line     = $i + 1
            }
        }
        elseif ($lines[$i] -match '\[(HttpGet|HttpPost|HttpPut|HttpDelete|HttpPatch|Route)\s*\(\s*"([^"]*)"') {
            [pscustomobject]@{
                template = $Matches[2]
                file     = Get-RelativePath $file.FullName
                line     = $i + 1
            }
        }
    }
}

# --- Configuration keys -----------------------------------------------------------------------

$settings = Get-ChildItem -Path $root -Recurse -File -Include appsettings*.json |
    Where-Object { $_.FullName -notmatch $excluded }

function Get-Keys($node, [string] $prefix) {
    foreach ($property in $node.PSObject.Properties) {
        $key = if ($prefix) { "$prefix`:$($property.Name)" } else { $property.Name }
        if ($property.Value -is [System.Management.Automation.PSCustomObject]) {
            Get-Keys $property.Value $key
        }
        else {
            [pscustomobject]@{ key = $key; value = "$($property.Value)" }
        }
    }
}

$configuration = foreach ($file in $settings) {
    $relative = Get-RelativePath $file.FullName
    try {
        foreach ($entry in Get-Keys (Get-Content $file.FullName -Raw | ConvertFrom-Json) '') {
            [pscustomobject]@{ file = $relative; key = $entry.key; value = $entry.value }
        }
    }
    catch {
        Write-Warning "Could not parse $relative"
    }
}

# --- Collisions -------------------------------------------------------------------------------

$collisions = [pscustomobject]@{
    routes = @(
        $routes | Group-Object template | Where-Object { $_.Count -gt 1 -and $_.Name } |
            ForEach-Object { [pscustomobject]@{ template = $_.Name; sites = @($_.Group) } }
    )
    configurationKeys = @(
        $configuration | Group-Object key |
            Where-Object { @($_.Group.value | Sort-Object -Unique).Count -gt 1 } |
            ForEach-Object { [pscustomobject]@{ key = $_.Name; values = @($_.Group) } }
    )
}

# --- Deployment -------------------------------------------------------------------------------

$deployment = [pscustomobject]@{
    dockerfiles    = @(Get-ChildItem -Path $root -Recurse -File -Filter 'Dockerfile*' | Where-Object { $_.FullName -notmatch $excluded } | ForEach-Object { Get-RelativePath $_.FullName })
    composeFiles   = @(Get-ChildItem -Path $root -Recurse -File -Include 'docker-compose*.y*ml','compose*.y*ml' | Where-Object { $_.FullName -notmatch $excluded } | ForEach-Object { Get-RelativePath $_.FullName })
    kubernetes     = @(Get-ChildItem -Path $root -Recurse -File -Include '*.yaml','*.yml' | Where-Object { $_.FullName -notmatch $excluded -and (Select-String -Path $_.FullName -Pattern '^kind:\s*(Deployment|StatefulSet)' -Quiet) } | ForEach-Object { Get-RelativePath $_.FullName })
    aspireProjects = @($projects | Where-Object { $_.sdk -match 'Aspire' -or ($_.packages -match 'Aspire\.Hosting') } | ForEach-Object { $_.path })
    ciWorkflows    = @(Get-ChildItem -Path $root -Recurse -File -Include '*.yml','*.yaml' | Where-Object { $_.FullName -match '[\\/]\.github[\\/]workflows[\\/]' } | ForEach-Object { Get-RelativePath $_.FullName })
}

# --- Result -----------------------------------------------------------------------------------

$result = [pscustomobject]@{
    root             = $root
    generatedAtUtc   = (Get-Date).ToUniversalTime().ToString('o')
    projects         = @($projects)
    directoryBuildProps = @($directoryBuildProps)
    targetFrameworks = @($projects | ForEach-Object { $_.targetFramework } | Where-Object { $_ } | Sort-Object -Unique)
    calls            = @($calls)
    data             = [pscustomobject]@{ contexts = @($contexts); providers = @($providers) }
    routes           = @($routes)
    configuration    = @($configuration)
    collisions       = $collisions
    deployment       = $deployment
}

$json = $result | ConvertTo-Json -Depth 8

if ($Output) {
    $json | Set-Content -Path $Output -Encoding utf8
    Write-Host "Wrote $Output"
    Write-Host "  projects            $($result.projects.Count)"
    Write-Host "  target frameworks   $($result.targetFrameworks -join ', ')"
    Write-Host "  cross-service calls $($result.calls.Count)"
    Write-Host "  routes              $($result.routes.Count)"
    Write-Host "  route collisions    $($collisions.routes.Count)"
    Write-Host "  config collisions   $($collisions.configurationKeys.Count)"
}
else {
    $json
}
