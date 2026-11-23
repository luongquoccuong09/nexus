#requires -Version 5.1

<#
.SYNOPSIS
Builds the per-user Windows Desktop MSI installer (WebView2 shell) for Project Nexus.

.DESCRIPTION
Runs automated checks (unless -SkipTests is specified), publishes Nexus.Desktop as a self-contained
win-x64 single-file executable (no separate .NET Runtime install required on colleague PCs),
optionally bakes a default HTTPS -ServerUrl into desktop-config.json, verifies the staged binary
via --verify-install, generates WiX v4 payload markup, builds ProjectNexus-<version>-x64.msi,
and writes SHA-256 metadata under artifacts\installer\.

.EXAMPLE
.\scripts\build\Build-DesktopInstaller.ps1 -NoPause

.EXAMPLE
.\scripts\build\Build-DesktopInstaller.ps1 -ProductVersion 0.2.0 -ServerUrl "https://project-nexus.onrender.com" -NoPause

.EXAMPLE
.\scripts\build\Build-DesktopInstaller.ps1 -SkipTests -NoPause
#>
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$ProductVersion = '0.2.0',

    [string]$ServerUrl = '',

    [switch]$SkipTests,

    [switch]$KeepStaging,

    [switch]$NoPause
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '..\internal\Workspace.ps1')
$ws = Get-NexusWorkspaceLayout -StartPath $PSScriptRoot

$gitRevText = (& git -C $ws.WorkspaceRoot rev-list --count HEAD).Trim()
$gitRev = 1
if ($LASTEXITCODE -eq 0 -and [int]::TryParse($gitRevText, [ref]$gitRev)) {
    $gitRev = [Math]::Max(1, [Math]::Min(65535, $gitRev))
}
$fileVersion = "$ProductVersion.$gitRev"

$artifactRoot = [System.IO.Path]::GetFullPath($ws.ArtifactRoot)
$stagingRoot = Join-Path $artifactRoot 'work\installer'
$payloadDir = Join-Path $stagingRoot 'payload'
$wixBinDir = Join-Path $stagingRoot 'wix-bin'
$installerOutputDir = Join-Path $artifactRoot 'installer'
$logPath = Join-Path $artifactRoot 'Build-DesktopInstaller.latest.log'
$generatedWix = Join-Path $ws.WorkspaceRoot 'installer\Nexus.Desktop.Installer\Generated\Payload.generated.wxs'
$generateScript = Join-Path $ws.WorkspaceRoot 'installer\Nexus.Desktop.Installer\Generate-PayloadWix.ps1'

New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
[System.IO.File]::WriteAllText($logPath, "Project Nexus Desktop Installer Build Log ($(Get-Date -Format o))`n", (New-Object System.Text.UTF8Encoding($false)))

function Invoke-Logged {
    param(
        [Parameter(Mandatory)][string]$StepName,
        [Parameter(Mandatory)][scriptblock]$Action
    )
    Write-NexusStepStatus 'RUN' $StepName
    Add-Content -LiteralPath $logPath -Value "`n=== $StepName ==="
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $global:LASTEXITCODE = 0
        $output = & $Action 2>&1
        $exitCode = if ($null -ne $global:LASTEXITCODE) { $global:LASTEXITCODE } else { 0 }
    }
    finally {
        $ErrorActionPreference = $prevEap
    }
    if ($output) {
        $output | ForEach-Object { Add-Content -LiteralPath $logPath -Value ($_.ToString()) }
    }
    if ($exitCode -ne 0) {
        Write-NexusStepStatus 'FAIL' "$StepName (see $logPath)"
        throw "$StepName failed with exit code $exitCode. Inspect $logPath."
    }
    Write-NexusStepStatus 'PASS' $StepName
}

try {
    # Auto-read NEXUS_SERVER_URL from local .env when -ServerUrl is not explicitly provided
    if ([string]::IsNullOrWhiteSpace($ServerUrl)) {
        $dotEnvPath = Join-Path $ws.WorkspaceRoot '.env'
        if (Test-Path -LiteralPath $dotEnvPath -PathType Leaf) {
            foreach ($line in Get-Content -LiteralPath $dotEnvPath) {
                $trimmed = $line.Trim()
                if ($trimmed -match '^NEXUS_SERVER_URL\s*=\s*(.+)$') {
                    $ServerUrl = $Matches[1].Trim().Trim('"', "'")
                    break
                }
            }
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($ServerUrl)) {
        $trimmedUrl = $ServerUrl.Trim().TrimEnd('/')
        if ($trimmedUrl -notmatch '^https://' -and $trimmedUrl -notmatch '^http://(localhost|127\.0\.0\.1)(:\d+)?$') {
            throw "Invalid -ServerUrl '$ServerUrl'. Must use https:// (or http://127.0.0.1 / http://localhost for local dev)."
        }
        $ServerUrl = $trimmedUrl
    }

    if (-not $SkipTests) {
        Invoke-Logged 'Run automated verification suite (Run-Tests.ps1)' {
            & (Join-Path $ws.ScriptRoot 'Run-Tests.ps1')
        }
    } else {
        Write-NexusStepStatus 'WARN' 'Skipping automated test suite (-SkipTests specified).'
    }

    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $payloadDir, $wixBinDir, $installerOutputDir -Force | Out-Null

    Invoke-Logged "Publish Nexus.Desktop (win-x64 self-contained single-file, v$fileVersion)" {
        & dotnet publish $ws.DesktopProject `
            -c Release `
            -r win-x64 `
            --self-contained true `
            -o $payloadDir `
            /p:PublishSingleFile=true `
            /p:IncludeNativeLibrariesForSelfExtract=true `
            /p:EnableCompressionInSingleFile=true `
            /p:DebugType=none `
            /p:DebugSymbols=false `
            /p:Version=$ProductVersion `
            /p:FileVersion=$fileVersion `
            /p:AssemblyVersion=$fileVersion
    }

    # Write bundled desktop-config.json (either empty for first-run prompt or pre-configured ServerUrl)
    $configJson = (@{ defaultServerUrl = $ServerUrl } | ConvertTo-Json)
    [System.IO.File]::WriteAllText(
        (Join-Path $payloadDir 'desktop-config.json'),
        $configJson + "`n",
        (New-Object System.Text.UTF8Encoding($false)))

    # Remove stray debug symbols and NuGet XML docs (avoid -LiteralPath + -Include bug in Windows PowerShell 5.1)
    Get-ChildItem -LiteralPath $payloadDir -File -Recurse |
        Where-Object { $_.Extension -eq '.pdb' -or $_.Name -like 'Microsoft.Web.WebView2.*.xml' } |
        Remove-Item -Force

    $stagedExe = Join-Path $payloadDir 'ProjectNexus.exe'
    if (-not (Test-Path -LiteralPath $stagedExe -PathType Leaf)) {
        throw "Staged executable missing: $stagedExe"
    }

    Invoke-Logged 'Smoke-verify staged ProjectNexus.exe (--verify-install)' {
        & $stagedExe --verify-install
    }

    Invoke-Logged 'Generate WiX v4 payload manifest' {
        & $generateScript -PayloadRoot $payloadDir -OutputPath $generatedWix
    }

    $buildId = [Guid]::NewGuid().ToString('N')
    $productCode = Get-NexusDeterministicProductCode -ProductVersion $ProductVersion -BuildIdentity $buildId

    Invoke-Logged "Build WiX MSI (Nexus-$ProductVersion-x64.msi)" {
        & dotnet build $ws.InstallerProject `
            -c Release `
            -o $wixBinDir `
            /p:ProductVersion=$ProductVersion `
            /p:ProductCode=$productCode `
            /p:PayloadRoot=$payloadDir
    }

    $builtMsi = Join-Path $wixBinDir "Nexus-$ProductVersion-x64.msi"
    if (-not (Test-Path -LiteralPath $builtMsi -PathType Leaf)) {
        throw "Expected MSI output was not produced: $builtMsi"
    }

    $finalMsi = Join-Path $installerOutputDir "Nexus-$ProductVersion-x64.msi"
    Copy-Item -LiteralPath $builtMsi -Destination $finalMsi -Force

    $msiHash = (Get-FileHash -LiteralPath $finalMsi -Algorithm SHA256).Hash.ToLowerInvariant()
    $shaPath = "$finalMsi.sha256"
    [System.IO.File]::WriteAllText(
        $shaPath,
        "$msiHash  Nexus-$ProductVersion-x64.msi`n",
        (New-Object System.Text.UTF8Encoding($false)))

    $sizeMb = [Math]::Round((Get-Item -LiteralPath $finalMsi).Length / 1MB, 2)
    Write-NexusStepStatus 'PASS' "MSI built: $finalMsi ($sizeMb MB)"
    Write-NexusStepStatus 'INFO' "SHA-256: $msiHash"
    if ($ServerUrl) {
        Write-NexusStepStatus 'INFO' "Bundled default Server URL: $ServerUrl"
    } else {
        Write-NexusStepStatus 'INFO' 'No default Server URL bundled; users will be prompted on first launch.'
    }

    if (-not $KeepStaging) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}
catch {
    $errMsg = $_.Exception.Message
    Add-Content -LiteralPath $logPath -Value "`n[EXCEPTION] $errMsg`n$($_.ScriptStackTrace)"
    Write-NexusStepStatus 'FAIL' "$errMsg (see $logPath)"
    throw
}
finally {
    if (-not $NoPause) {
        Read-Host 'Press Enter to close' | Out-Null
    }
}
