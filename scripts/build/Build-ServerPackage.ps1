#requires -Version 5.1

<#
.SYNOPSIS
Builds and packages the single-origin Project Nexus server (ASP.NET Core + React SPA) into artifacts\server-package.

.EXAMPLE
.\scripts\build\Build-ServerPackage.ps1

.EXAMPLE
.\scripts\build\Build-ServerPackage.ps1 -BuildDockerImage -SkipTests
#>
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$BuildDockerImage,
    [string]$DockerImageTag = 'project-nexus:local'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '..\internal\Workspace.ps1')
$ws = Get-NexusWorkspaceLayout -StartPath $PSScriptRoot
Add-DockerDesktopToPath

if (-not $SkipTests) {
    & (Join-Path $ws.ScriptRoot 'Run-Tests.ps1')
}

$outputDir = Join-Path $ws.ArtifactRoot 'server-package'
if (Test-Path -LiteralPath $outputDir) {
    Remove-Item -LiteralPath $outputDir -Recurse -Force
}
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

Write-NexusStepStatus 'RUN' "Publishing Nexus.Web (Release) to $outputDir"
& dotnet publish $ws.WebProject -c Release -o $outputDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish for Nexus.Web failed.' }

$spaIndex = Join-Path $outputDir 'wwwroot\index.html'
if (-not (Test-Path -LiteralPath $spaIndex -PathType Leaf)) {
    throw "Published server package is missing SPA entry point: $spaIndex"
}

$dllHash = (Get-FileHash -LiteralPath (Join-Path $outputDir 'Nexus.Web.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText(
    (Join-Path $outputDir 'SHA256SUMS.txt'),
    "$dllHash  Nexus.Web.dll`n",
    (New-Object System.Text.UTF8Encoding($false)))

Write-NexusStepStatus 'PASS' "Server package ready: $outputDir (Nexus.Web.dll SHA-256: $dllHash)"

if ($BuildDockerImage) {
    Write-NexusStepStatus 'RUN' "Building Docker image '$DockerImageTag' via deploy/Dockerfile"
    $dockerfile = Join-Path $ws.WorkspaceRoot 'deploy\Dockerfile'
    & docker build -f $dockerfile -t $DockerImageTag $ws.WorkspaceRoot
    if ($LASTEXITCODE -ne 0) { throw 'Docker image build failed.' }
    Write-NexusStepStatus 'PASS' "Docker image built: $DockerImageTag"
}
