#requires -Version 5.1

Set-StrictMode -Version Latest

function Get-NexusWorkspaceLayout {
    param(
        [Parameter(Mandatory)]
        [string]$StartPath
    )

    $current = [System.IO.Path]::GetFullPath($StartPath)
    while ($true) {
        $solutionPath = Join-Path $current 'Nexus.slnx'
        if (Test-Path -LiteralPath $solutionPath -PathType Leaf) {
            return [PSCustomObject]@{
                WorkspaceRoot = $current
                SolutionPath = $solutionPath
                SrcRoot = Join-Path $current 'src'
                WebProject = Join-Path $current 'src\Nexus.Web\Nexus.Web.csproj'
                ClientRoot = Join-Path $current 'src\Nexus.Client'
                DesktopProject = Join-Path $current 'src\Nexus.Desktop\Nexus.Desktop.csproj'
                InstallerProject = Join-Path $current 'installer\Nexus.Desktop.Installer\Nexus.Desktop.Installer.wixproj'
                TestRoot = Join-Path $current 'tests'
                ScriptRoot = Join-Path $current 'scripts'
                ArtifactRoot = Join-Path $current 'artifacts'
            }
        }

        $parent = Split-Path -Parent $current
        if ([string]::IsNullOrEmpty($parent) -or $parent -eq $current) {
            throw "Could not locate Nexus.slnx above '$StartPath'."
        }
        $current = $parent
    }
}

function Add-DockerDesktopToPath {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\DockerDesktop\resources\bin'),
        'C:\Program Files\Docker\Docker\resources\bin'
    )
    foreach ($candidate in $candidates) {
        if ((Test-Path -LiteralPath $candidate -PathType Container) -and
            ($env:Path -notlike "*$candidate*")) {
            $env:Path = "$candidate;$env:Path"
        }
    }
}

function Get-NexusDeterministicProductCode {
    param(
        [Parameter(Mandatory)][string]$ProductVersion,
        [Parameter(Mandatory)][string]$BuildIdentity
    )

    $seed = "project-nexus-desktop|$ProductVersion|$BuildIdentity"
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($seed))
    }
    finally {
        $sha.Dispose()
    }

    $bytes = New-Object byte[] 16
    [Array]::Copy($hash, 0, $bytes, 0, 16)
    $bytes[6] = ($bytes[6] -band 0x0F) -bor 0x50
    $bytes[8] = ($bytes[8] -band 0x3F) -bor 0x80
    return ([Guid]::new($bytes)).ToString('B').ToUpperInvariant()
}

function Write-NexusStepStatus {
    param(
        [Parameter(Mandatory)][ValidateSet('RUN', 'PASS', 'FAIL', 'WARN', 'INFO')][string]$Level,
        [Parameter(Mandatory)][string]$Message
    )

    $color = switch ($Level) {
        'RUN'  { 'Cyan' }
        'PASS' { 'Green' }
        'FAIL' { 'Red' }
        'WARN' { 'Yellow' }
        default { 'Gray' }
    }
    Write-Host ("[{0}] {1}" -f $Level, $Message) -ForegroundColor $color
}
