#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Fails when a relative link in the repository's Markdown points at nothing.

.DESCRIPTION
    Link rot here is not cosmetic. A rule's help link is what an IDE opens from a squiggle, the
    README is the only map of the rule set, and the skill's references link to each other — all of
    them maintained by hand, none of them exercised by a build. A dead link looks exactly like a
    live one until somebody clicks it.

    Relative links only. External URLs are somebody else's uptime and checking them would make this
    fail for reasons that have nothing to do with the commit.

.PARAMETER Path
    Repository root. Defaults to the parent of this script.
#>
[CmdletBinding()]
param(
    [string] $Path = (Join-Path $PSScriptRoot '..')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path $Path).Path
$broken = [System.Collections.Generic.List[string]]::new()

$documents = Get-ChildItem -Path $root -Filter *.md -Recurse -File |
    Where-Object { $_.FullName -notmatch '[\/](bin|obj|node_modules|\.git)[\/]' }

foreach ($document in $documents) {
    $text = Get-Content -Raw -LiteralPath $document.FullName

    foreach ($match in [regex]::Matches($text, '\[[^\]]*\]\(([^)\s]+)\)')) {
        $target = $match.Groups[1].Value

        # Absolute URLs, mail links and same-document anchors are not this script's business.
        if ($target -match '^([a-z][a-z0-9+.-]*:|#|//)') { continue }

        # A link may carry an anchor; the file is what is being checked.
        $file = ($target -split '#')[0]
        if ([string]::IsNullOrEmpty($file)) { continue }

        $resolved = Join-Path $document.DirectoryName ([Uri]::UnescapeDataString($file))
        if (-not (Test-Path -LiteralPath $resolved)) {
            $from = [IO.Path]::GetRelativePath($root, $document.FullName).Replace([IO.Path]::DirectorySeparatorChar, '/')
            $broken.Add("${from}: $target")
        }
    }
}

if ($broken.Count -gt 0) {
    Write-Host "Broken relative links:`n"
    $broken | Sort-Object | ForEach-Object { Write-Host "  $_" }
    exit 1
}

Write-Host "All relative links in $($documents.Count) Markdown files resolve."
