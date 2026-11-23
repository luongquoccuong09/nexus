#requires -Version 5.1

<#
.SYNOPSIS
Reads local `.env` (gitignored) and runs EF Core migrations (`--migrate`) and optional initial admin bootstrap (`--bootstrap-admin`) against Supabase Session Pooler (port 5432) without echoing secrets.

.EXAMPLE
.\scripts\deploy\Invoke-SupabaseMigrate.ps1

.EXAMPLE
.\scripts\deploy\Invoke-SupabaseMigrate.ps1 -MigrateOnly
#>
[CmdletBinding()]
param(
    [string]$EnvFile = '',
    [switch]$MigrateOnly,
    [switch]$BootstrapOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot '..\internal\Workspace.ps1')
$ws = Get-NexusWorkspaceLayout -StartPath $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($EnvFile)) {
    $EnvFile = Join-Path $ws.WorkspaceRoot '.env'
}

if (-not (Test-Path -LiteralPath $EnvFile -PathType Leaf)) {
    throw "Env file not found: $EnvFile. Copy .env.example to .env and fill in your passwords first."
}

$envMap = @{}
foreach ($rawLine in Get-Content -LiteralPath $EnvFile) {
    $line = $rawLine.Trim()
    if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#')) {
        continue
    }
    $eqIdx = $line.IndexOf('=')
    if ($eqIdx -lt 1) {
        continue
    }
    $key = $line.Substring(0, $eqIdx).Trim()
    $val = $line.Substring($eqIdx + 1).Trim()
    if (($val.StartsWith('"') -and $val.EndsWith('"')) -or ($val.StartsWith("'") -and $val.EndsWith("'"))) {
        if ($val.Length -ge 2) {
            $val = $val.Substring(1, $val.Length - 2)
        }
    }
    $envMap[$key] = $val
}

function Get-RequiredEnv {
    param([Parameter(Mandatory)][string]$Key)
    if (-not $envMap.ContainsKey($Key) -or [string]::IsNullOrWhiteSpace($envMap[$Key])) {
        throw "Missing required key '$Key' in $EnvFile."
    }
    return $envMap[$Key]
}

function Format-NpgsqlValue {
    param([Parameter(Mandatory)][string]$Value)
    # Quote Npgsql connection string values containing special delimiters
    if ($Value -match '[;=''"]') {
        return '"' + ($Value -replace '"', '""') + '"'
    }
    return $Value
}

$poolerHost = Get-RequiredEnv 'SUPABASE_POOLER_HOST'
$projectRef = Get-RequiredEnv 'SUPABASE_PROJECT_REF'

try {
    if (-not $BootstrapOnly) {
        $migratorPassword = Get-RequiredEnv 'NEXUS_MIGRATOR_PASSWORD'
        $migUser = "nexus_migrator.$projectRef"
        $env:ConnectionStrings__Migration = "Host=$poolerHost;Port=5432;Database=postgres;Username=$migUser;Password=$(Format-NpgsqlValue $migratorPassword);SSL Mode=Require"
        $env:NexusDatabase__RequireTls = 'true'

        Write-NexusStepStatus 'RUN' "Applying EF Core migrations via $poolerHost`:5432 as $migUser"
        & dotnet run --project $ws.WebProject -- --migrate
        if ($LASTEXITCODE -ne 0) {
            throw "Database migration failed with exit code $LASTEXITCODE."
        }
        Write-NexusStepStatus 'PASS' 'EF Core migrations applied cleanly.'
    }

    if (-not $MigrateOnly) {
        $runtimePassword = Get-RequiredEnv 'NEXUS_RUNTIME_PASSWORD'
        $adminUser = if ($envMap.ContainsKey('NEXUS_ADMIN_USERNAME') -and $envMap['NEXUS_ADMIN_USERNAME']) { $envMap['NEXUS_ADMIN_USERNAME'] } else { 'nexus_sysadmin' }
        $adminName = if ($envMap.ContainsKey('NEXUS_ADMIN_DISPLAY_NAME') -and $envMap['NEXUS_ADMIN_DISPLAY_NAME']) { $envMap['NEXUS_ADMIN_DISPLAY_NAME'] } else { 'System Admin' }
        $adminPassword = Get-RequiredEnv 'NEXUS_ADMIN_PASSWORD'

        $runUser = "nexus_runtime.$projectRef"
        $env:ConnectionStrings__Runtime = "Host=$poolerHost;Port=5432;Database=postgres;Username=$runUser;Password=$(Format-NpgsqlValue $runtimePassword);SSL Mode=Require"
        $env:NexusDatabase__RequireTls = 'true'
        $env:BootstrapAdmin__Username = $adminUser
        $env:BootstrapAdmin__Password = $adminPassword
        $env:BootstrapAdmin__DisplayName = $adminName

        Write-NexusStepStatus 'RUN' "Bootstrapping initial SystemAdmin '$adminUser' via $poolerHost`:5432 as $runUser"
        & dotnet run --project $ws.WebProject -- --bootstrap-admin
        if ($LASTEXITCODE -ne 0) {
            throw "Admin bootstrap failed with exit code $LASTEXITCODE."
        }
        Write-NexusStepStatus 'PASS' "SystemAdmin '$adminUser' ready."
    }
}
finally {
    Remove-Item Env:ConnectionStrings__Migration, Env:ConnectionStrings__Runtime, Env:NexusDatabase__RequireTls, Env:BootstrapAdmin__Username, Env:BootstrapAdmin__Password, Env:BootstrapAdmin__DisplayName -ErrorAction SilentlyContinue
}
