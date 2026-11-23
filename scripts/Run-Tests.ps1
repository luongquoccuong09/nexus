#requires -Version 5.1

<#
.SYNOPSIS
Runs the full Project Nexus verification pipeline (security guardrails, knowledge index, .NET build/tests, React client tests/build, and Desktop build).

.EXAMPLE
.\scripts\Run-Tests.ps1

.EXAMPLE
.\scripts\Run-Tests.ps1 -SkipDockerTests
#>
[CmdletBinding()]
param(
    [switch]$SkipDockerTests,
    [switch]$SkipDesktopBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'internal\Workspace.ps1')
$ws = Get-NexusWorkspaceLayout -StartPath $PSScriptRoot
Add-DockerDesktopToPath

function Invoke-AuditScript {
    param(
        [Parameter(Mandatory)][string]$Label,
        [Parameter(Mandatory)][string]$ScriptPath,
        [hashtable]$ScriptParams = @{}
    )
    Write-NexusStepStatus 'RUN' $Label
    $global:LASTEXITCODE = 0
    & $ScriptPath @ScriptParams
    if ($global:LASTEXITCODE -ne 0) {
        throw "$Label failed with exit code $global:LASTEXITCODE."
    }
    Write-NexusStepStatus 'PASS' $Label
}

Invoke-AuditScript 'Security guardrails audit' (Join-Path $ws.ScriptRoot 'audit\Test-SecurityGuardrails.ps1')
Invoke-AuditScript 'UI design system compliance audit' (Join-Path $ws.ScriptRoot 'audit\Test-UiDesignCompliance.ps1')
Invoke-AuditScript 'Knowledge index check' (Join-Path $ws.ScriptRoot 'audit\Build-KnowledgeIndex.ps1') @{ Check = $true }

Write-NexusStepStatus 'RUN' 'Build Nexus.slnx (Release)'
& dotnet build $ws.SolutionPath -c Release
if ($LASTEXITCODE -ne 0) { throw 'Solution Release build failed.' }
Write-NexusStepStatus 'PASS' 'Build Nexus.slnx (Release)'

if ($SkipDockerTests) {
    Write-NexusStepStatus 'RUN' 'Run non-Docker integration tests'
    & dotnet test $ws.SolutionPath --no-build -c Release --filter "FullyQualifiedName!~PostgresFoundationTests&FullyQualifiedName!~IdentityM1aTests&FullyQualifiedName!~DirectoryM1bTests&FullyQualifiedName!~MessagingM2Tests"
} else {
    Write-NexusStepStatus 'RUN' 'Run full integration test suite (PostgreSQL 17 Testcontainers)'
    & dotnet test $ws.SolutionPath --no-build -c Release
}
if ($LASTEXITCODE -ne 0) { throw 'Integration test suite failed.' }
Write-NexusStepStatus 'PASS' 'Integration tests passed'

$isWin = [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT
$npmExe = if ($isWin) { 'npm.cmd' } else { 'npm' }

Write-NexusStepStatus 'RUN' 'Client unit tests (SEC-10 Markdown XSS)'
& $npmExe --prefix $ws.ClientRoot run test
if ($LASTEXITCODE -ne 0) { throw 'Client unit tests failed.' }
Write-NexusStepStatus 'PASS' 'Client unit tests passed'

Write-NexusStepStatus 'RUN' 'Client TypeScript + Vite production build'
& $npmExe --prefix $ws.ClientRoot run build
if ($LASTEXITCODE -ne 0) { throw 'Client build failed.' }
Write-NexusStepStatus 'PASS' 'Client production build passed'

if (-not $SkipDesktopBuild -and $isWin -and (Test-Path -LiteralPath $ws.DesktopProject)) {
    Write-NexusStepStatus 'RUN' 'Build Nexus.Desktop (Release)'
    & dotnet build $ws.DesktopProject -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Nexus.Desktop build failed.' }
    Write-NexusStepStatus 'PASS' 'Build Nexus.Desktop (Release)'
}

Write-NexusStepStatus 'PASS' 'All Project Nexus verification checks completed.'
