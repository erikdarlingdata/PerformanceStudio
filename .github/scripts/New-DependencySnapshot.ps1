<#
.SYNOPSIS
    Builds a GitHub dependency snapshot from the obj/project.assets.json files NuGet wrote.

.DESCRIPTION
    Reads every obj/project.assets.json under the repository and writes the JSON body for
    POST /repos/{owner}/{repo}/dependency-graph/snapshots. No third-party tool is involved: the
    assets file already holds the exact package versions NuGet resolved, transitive packages
    included, and which of them the project references directly.

    The resolved map is keyed by package URL and dependencies list package URLs, as in the
    snapshots GitHub accepted from the component-detection action. Packages in tests/ projects
    get scope 'development', all others 'runtime'.

    One manifest per project per target framework. The manifest is named after the project file
    (relative to the repository root, forward slashes), with the target framework appended in
    parentheses only when a project has more than one target.

    Fails when it finds no assets file or resolves no package at all, so a broken restore cannot
    replace the dependency graph with an empty snapshot.

.PARAMETER Sha
    Commit SHA the snapshot describes (github.sha).

.PARAMETER Ref
    Fully qualified ref, e.g. refs/heads/main (github.ref).

.PARAMETER RunId
    Workflow run id (github.run_id).

.PARAMETER Correlator
    Stable string that makes a new snapshot replace the previous one from the same source.

.PARAMETER OutputPath
    Where to write the snapshot JSON.

.PARAMETER RepoRoot
    Repository root. Defaults to two levels above this script.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Sha,
    [Parameter(Mandatory)][string]$Ref,
    [Parameter(Mandatory)][string]$RunId,
    [Parameter(Mandatory)][string]$Correlator,
    [string]$OutputPath = 'snapshot.json',
    [string]$RepoRoot = (Join-Path $PSScriptRoot '..\..')
)

$ErrorActionPreference = 'Stop'

$root = (Resolve-Path -LiteralPath $RepoRoot).Path.TrimEnd('\', '/')
$rootPrefix = $root.Replace('\', '/') + '/'

function Get-RelativePath([string]$fullPath) {
    $normalized = $fullPath.Replace('\', '/')
    if ($normalized.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $normalized.Substring($rootPrefix.Length)
    }
    return $normalized
}

# Assets files under .claude/, tmp-*/ and bin/ are scratch copies, not part of the product.
$assetsFiles = Get-ChildItem -LiteralPath $root -Recurse -Filter 'project.assets.json' -File |
    Where-Object { $_.Directory.Name -eq 'obj' } |
    Where-Object {
        $rel = Get-RelativePath $_.FullName
        $rel -notmatch '(^|/)(\.claude|bin|tmp-[^/]*)/'
    } |
    Sort-Object FullName

if (-not $assetsFiles) {
    throw "No obj/project.assets.json found under $root. Did the restore steps run?"
}

# package_url values: percent-encode the name and version (a version can carry '+' build metadata).
function Get-PackageUrl([string]$name, [string]$version) {
    'pkg:nuget/{0}@{1}' -f [uri]::EscapeDataString($name), [uri]::EscapeDataString($version)
}

# Split a "Name/Version" key at the last slash.
function Split-LibraryKey([string]$key) {
    $i = $key.LastIndexOf('/')
    if ($i -lt 1) { return $null }
    [pscustomobject]@{ Name = $key.Substring(0, $i); Version = $key.Substring($i + 1) }
}

$manifests = [ordered]@{}
$totalResolved = 0

foreach ($file in $assetsFiles) {
    $assets = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json

    $projectPath = $assets.project.restore.projectPath
    if ([string]::IsNullOrWhiteSpace($projectPath)) {
        Write-Warning "Skipping $($file.FullName): no project.restore.projectPath."
        continue
    }
    $projectFile = Get-RelativePath $projectPath
    # Test-only packages are development dependencies; everything else ships.
    $scope = $(if ($projectFile -like 'tests/*') { 'development' } else { 'runtime' })

    # The direct references: names listed for the target framework, plus the framework-less group.
    $directByFramework = @{}
    foreach ($group in $assets.projectFileDependencyGroups.PSObject.Properties) {
        $names = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($line in @($group.Value)) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            [void]$names.Add(($line.Trim() -split '\s+', 2)[0])
        }
        $directByFramework[$group.Name] = $names
    }

    # A restore with runtime identifiers adds "tfm/rid" targets that repeat the plain "tfm" target's
    # packages; keep one manifest per framework and drop the runtime-specific copies.
    $allTargets = @($assets.targets.PSObject.Properties)
    $plainNames = @($allTargets | Where-Object { $_.Name -notlike '*/*' } | ForEach-Object { $_.Name })
    $targets = @($allTargets | Where-Object { $_.Name -notlike '*/*' -or ($plainNames -notcontains ($_.Name -split '/', 2)[0]) })
    foreach ($target in $targets) {
        # A runtime-specific target is keyed "tfm/rid"; its direct references sit under "tfm".
        $framework = ($target.Name -split '/', 2)[0]
        $direct = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($key in @($framework, $target.Name, '')) {
            if ($directByFramework.ContainsKey($key)) { $direct.UnionWith($directByFramework[$key]) }
        }

        # Pass 1: every package NuGet resolved for this target.
        $packages = [ordered]@{}
        foreach ($entry in $target.Value.PSObject.Properties) {
            if ($entry.Value.type -ne 'package') { continue }
            $id = Split-LibraryKey $entry.Name
            if ($null -eq $id) { continue }
            $packages[$id.Name] = [pscustomobject]@{ Version = $id.Version; Entry = $entry.Value }
        }

        # Pass 2: build the resolved map, keyed by package URL (the shape GitHub accepted from the
        # previous action). Dependencies are package URLs of packages in this same map.
        $purlByName = @{}
        foreach ($name in $packages.Keys) { $purlByName[$name] = Get-PackageUrl $name $packages[$name].Version }

        $resolved = [ordered]@{}
        foreach ($name in $packages.Keys) {
            $pkg = $packages[$name]
            $purl = $purlByName[$name]
            $dependsOn = [System.Collections.Generic.List[string]]::new()
            if ($pkg.Entry.PSObject.Properties['dependencies']) {
                foreach ($dep in $pkg.Entry.dependencies.PSObject.Properties) {
                    # $purlByName is case-insensitive, like NuGet package ids.
                    if ($purlByName.ContainsKey($dep.Name) -and $purlByName[$dep.Name] -ne $purl) {
                        if (-not $dependsOn.Contains($purlByName[$dep.Name])) { $dependsOn.Add($purlByName[$dep.Name]) }
                    }
                }
            }
            $resolved[$purl] = [ordered]@{
                package_url  = $purl
                relationship = $(if ($direct.Contains($name)) { 'direct' } else { 'indirect' })
                scope        = $scope
                dependencies = [string[]]$dependsOn.ToArray()
            }
        }

        if ($resolved.Count -eq 0) { continue }

        $manifestName = $projectFile
        if ($targets.Count -gt 1) { $manifestName = "$projectFile ($($target.Name))" }

        $manifests[$manifestName] = [ordered]@{
            name     = $manifestName
            file     = [ordered]@{ source_location = $projectFile }
            resolved = $resolved
        }
        $totalResolved += $resolved.Count
        Write-Host ("{0}: {1} packages" -f $manifestName, $resolved.Count)
    }
}

if ($manifests.Count -eq 0 -or $totalResolved -eq 0) {
    throw "Found $($assetsFiles.Count) assets file(s) but no resolved packages; refusing to write an empty snapshot."
}

$snapshot = [ordered]@{
    version  = 0
    sha      = $Sha
    ref      = $Ref
    job      = [ordered]@{ correlator = $Correlator; id = $RunId }
    detector = [ordered]@{
        name    = 'performancestudio-assets-snapshot'
        version = '1.0.0'
        url     = 'https://github.com/erikdarlingdata/PerformanceStudio'
    }
    scanned  = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    manifests = $manifests
}

$json = $snapshot | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($OutputPath, (Get-Location).Path), $json, [System.Text.UTF8Encoding]::new($false))

Write-Host ("Wrote {0}: {1} manifests, {2} resolved packages from {3} assets files." -f $OutputPath, $manifests.Count, $totalResolved, $assetsFiles.Count)
