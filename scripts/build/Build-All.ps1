#requires -Version 5.1

<#
.SYNOPSIS
Runs the complete Project Nexus build pipeline: automated verification, server package, and Windows Desktop MSI installer.

.EXAMPLE
.\scripts\build\Build-All.ps1 -ProductVersion 0.2.0 -ServerUrl "https://project-nexus.onrender.com"
#>
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$ProductVersion = '0.2.0',

    [string]$ServerUrl = '',

    [switch]$BuildDockerImage,

    [switch]$SkipDockerTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '..\internal\Workspace.ps1')
$ws = Get-NexusWorkspaceLayout -StartPath $PSScriptRoot

& (Join-Path $ws.ScriptRoot 'Run-Tests.ps1') -SkipDockerTests:$SkipDockerTests
& (Join-Path $PSScriptRoot 'Build-ServerPackage.ps1') -SkipTests -BuildDockerImage:$BuildDockerImage
& (Join-Path $PSScriptRoot 'Build-DesktopInstaller.ps1') -ProductVersion $ProductVersion -ServerUrl $ServerUrl -SkipTests -NoPause

Write-NexusStepStatus 'PASS' 'Build-All completed cleanly.'
