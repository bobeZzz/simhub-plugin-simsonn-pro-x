<#
.SYNOPSIS
Builds the plugin and publishes it as a GitHub release, which the plugin's updater then offers to every PC.

.DESCRIPTION
Run from a clean, pushed main branch:
    .\release.ps1 -Version 1.0.1
    .\release.ps1 -Version 1.1.0 -Notes "Clutch panel"

Steps: set <Version> in the .csproj, build Release against the local SimHub, commit "Release vX.Y.Z",
tag vX.Y.Z, push both, then create the release with SimsonnPedals.dll attached. The release is created as
a draft and published only once the DLL is uploaded, so the updater never sees a release without it.

GitHub access uses the sign-in Git already stored for github.com (from your first git push); the token is
only sent to api.github.com and never printed.
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes
)

$ErrorActionPreference = 'Stop'
$repo = 'bobeZzz/simhub-plugin-simsonn-pro-x'
$root = $PSScriptRoot
$project = Join-Path $root 'SimsonnPedals.csproj'
$dll = Join-Path $root 'bin\Release\net48\SimsonnPedals.dll'
$tag = "v$Version"

function Git { & git -C $root @args; if ($LASTEXITCODE -ne 0) { throw "git $args failed" } }

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3" }
if ((Git rev-parse --abbrev-ref HEAD) -ne 'main') { throw "Switch to the main branch first." }
if (Git status --porcelain) { throw "Commit or stash your changes first; a release must match a commit." }
Git fetch --quiet origin
if ((Git rev-parse HEAD) -ne (Git rev-parse origin/main)) { throw "main differs from origin/main; push or pull first." }
if (Git tag --list $tag) { throw "Tag $tag already exists." }
$previous = (& git -C $root describe --tags --abbrev=0 2>$null)

# Version, build
$xml = Get-Content $project -Raw
$xml = [regex]::Replace($xml, '<Version>[^<]*</Version>', "<Version>$Version</Version>")
[IO.File]::WriteAllText($project, $xml)
& dotnet build $project -c Release -p:DeployToSimHub=false --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Build failed." }
$built = [Reflection.AssemblyName]::GetAssemblyName($dll).Version
if ("$($built.Major).$($built.Minor).$($built.Build)" -ne $Version) { throw "Built DLL is version $built, expected $Version." }

# Release notes: the given text, else the commit subjects since the last release.
if (-not $Notes) {
    $range = if ($previous) { "$previous..HEAD" } else { 'HEAD' }
    $Notes = ((& git -C $root log $range --no-merges --format='- %s') -join "`n")
}

if (Git status --porcelain) {
    Git add $project
    Git commit --quiet -m "Release $tag"
}
Git tag -a $tag -m "Release $tag"
Git push --quiet origin main $tag

# GitHub token from Git's credential store
$cred = "protocol=https`nhost=github.com`n`n" | & git credential fill
$token = ($cred | Where-Object { $_ -like 'password=*' }) -replace '^password=', ''
if (-not $token) { throw "No stored GitHub sign-in. Run 'git push' once to sign in, then run: .\release.ps1 -Version $Version (after deleting tag $tag)" }
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$headers = @{ Authorization = "Bearer $token"; Accept = 'application/vnd.github+json'; 'User-Agent' = 'SimsonnPedals-release' }

$body = @{ tag_name = $tag; name = "SIMSONN Pedals $Version"; body = $Notes; draft = $true } | ConvertTo-Json
$release = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$repo/releases" -Headers $headers -Body ([Text.Encoding]::UTF8.GetBytes($body)) -ContentType 'application/json'
Invoke-RestMethod -Method Post -Uri "https://uploads.github.com/repos/$repo/releases/$($release.id)/assets?name=SimsonnPedals.dll" `
    -Headers $headers -InFile $dll -ContentType 'application/octet-stream' | Out-Null
$published = Invoke-RestMethod -Method Patch -Uri "https://api.github.com/repos/$repo/releases/$($release.id)" -Headers $headers `
    -Body '{"draft":false}' -ContentType 'application/json'

Write-Host "Published $($published.html_url)"
