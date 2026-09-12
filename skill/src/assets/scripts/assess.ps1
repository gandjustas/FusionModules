#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Finds the collisions that make a merged monolith silently wrong.

.DESCRIPTION
    Two things, and deliberately only two:

      - the same configuration key holding different values in different files
      - the same route template declared in more than one place

    Both are silent at runtime. The one that wins is decided by load order, which is decided by
    HOSTINGSTARTUPASSEMBLIES, which means it can differ between topologies. Neither is findable by
    reading, which is why they are worth a script.

    Everything else an assessment needs — the project inventory, the transports, the DbContexts,
    the deployment artefacts — an agent finds with grep, and finds better, because it sees the
    context around each hit. This script does not try.

    It is a starting point rather than an oracle. Regular expressions find route templates, not a
    compiler: MapGroup("/billing") followed by MapGet("/overdue") is reported as two templates, not
    one, and a collision only matters if some topology loads both declarations. Confirm before
    raising. The authoritative route inventory comes from EndpointDataSource at runtime.

.PARAMETER Path
    Solution root. Defaults to the current directory.

.PARAMETER Output
    Where to write the JSON. Defaults to stdout.

.EXAMPLE
    pwsh assess.ps1 -Path ./src -Output modulith-collisions.json
#>
[CmdletBinding()]
param(
    [string] $Path = '.',
    [string] $Output
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path $Path).Path
$excludedDirectories = @('bin', 'obj', 'node_modules', '.git')

function Test-Included([string] $full) {
    # Path segments rather than a regex: one fewer thing to escape, and it reads as what it means.
    $segments = $full.Split([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    return -not ($segments | Where-Object { $excludedDirectories -contains $_ })
}

function Get-RelativePath([string] $full) {
    # Plain string replace: -replace takes a regex, and a lone backslash is not one.
    [IO.Path]::GetRelativePath($root, $full).Replace([IO.Path]::DirectorySeparatorChar, '/')
}

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

# --- Configuration keys -----------------------------------------------------------------------

$configuration = foreach ($file in Get-ChildItem -Path $root -Recurse -File -Include appsettings*.json |
                          Where-Object { Test-Included $_.FullName }) {
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

# --- Route templates --------------------------------------------------------------------------

# Named captures, because an optional group that does not participate is simply absent from
# $Matches — so indexing by position silently reads the wrong group, or nothing at all.
$routePatterns = @(
    'Map(Get|Post|Put|Delete|Patch|Group|ControllerRoute|RazorPages)\s*(<[^>]*>)?\s*\(\s*"(?<template>[^"]*)"'
    '\[(HttpGet|HttpPost|HttpPut|HttpDelete|HttpPatch|Route)\s*\(\s*"(?<template>[^"]*)"'
)

$routes = foreach ($file in Get-ChildItem -Path $root -Recurse -File -Include *.cs |
                   Where-Object { Test-Included $_.FullName }) {
    $relative = Get-RelativePath $file.FullName
    $lines = Get-Content $file.FullName
    for ($i = 0; $i -lt $lines.Count; $i++) {
        foreach ($pattern in $routePatterns) {
            if ($lines[$i] -match $pattern) {
                [pscustomobject]@{
                    template = $Matches['template']
                    file     = $relative
                    line     = $i + 1
                }
            }
        }
    }
}

# --- Result -----------------------------------------------------------------------------------

$result = [pscustomobject]@{
    root           = $root
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    routeCollisions = @(
        $routes | Group-Object template | Where-Object { $_.Count -gt 1 -and $_.Name } |
            ForEach-Object { [pscustomobject]@{ template = $_.Name; sites = @($_.Group) } }
    )
    configurationKeyCollisions = @(
        $configuration | Group-Object key |
            Where-Object { @($_.Group.value | Sort-Object -Unique).Count -gt 1 } |
            ForEach-Object { [pscustomobject]@{ key = $_.Name; values = @($_.Group) } }
    )
}

$json = $result | ConvertTo-Json -Depth 6

if ($Output) {
    $json | Set-Content -Path $Output -Encoding utf8
    Write-Host "Wrote $Output"
    Write-Host "  route collisions            $($result.routeCollisions.Count)"
    Write-Host "  configuration key collisions $($result.configurationKeyCollisions.Count)"
}
else {
    $json
}
