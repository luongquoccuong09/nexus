#requires -Version 5.1

<#
.SYNOPSIS
Cleans generated build outputs, staging directories, and installer intermediates without touching source files or node_modules.

.EXAMPLE
.\scripts\build\Clean-BuildOutputs.ps1
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '..\internal\Workspace.ps1')
$ws = Get-NexusWorkspaceLayout -StartPath $PSScriptRoot

$targets = @(
    $ws.ArtifactRoot,
    (Join-Path $ws.ClientRoot 'dist'),
    (Join-Path $ws.WorkspaceRoot 'src\Nexus.Web\bin'),
    (Join-Path $ws.WorkspaceRoot 'src\Nexus.Web\obj'),
    (Join-Path $ws.WorkspaceRoot 'src\Nexus.Web\wwwroot'),
    (Join-Path $ws.WorkspaceRoot 'src\Nexus.Desktop\bin'),
    (Join-Path $ws.WorkspaceRoot 'src\Nexus.Desktop\obj'),
    (Join-Path $ws.WorkspaceRoot 'tests\Nexus.IntegrationTests\bin'),
    (Join-Path $ws.WorkspaceRoot 'tests\Nexus.IntegrationTests\obj'),
    (Join-Path $ws.WorkspaceRoot 'installer\Nexus.Desktop.Installer\bin'),
    (Join-Path $ws.WorkspaceRoot 'installer\Nexus.Desktop.Installer\obj'),
    (Join-Path $ws.WorkspaceRoot 'installer\Nexus.Desktop.Installer\Generated')
)

foreach ($path in $targets) {
    if (Test-Path -LiteralPath $path) {
        Write-NexusStepStatus 'RUN' "Removing $path"
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

Write-NexusStepStatus 'PASS' 'Clean-BuildOutputs completed.'
