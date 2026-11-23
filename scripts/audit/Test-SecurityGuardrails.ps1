<#
.SYNOPSIS
    Static security guardrails for Project Nexus (M0.7).

.DESCRIPTION
    Rejects forbidden patterns from proposal section 9.1 and AGENTS.md:
    - CORS AllowAnyOrigin in src/
    - Disabled TLS certificate validation in src/ and deploy/
    - Privileged storage credentials in src/Nexus.Client/
    - Supabase Transaction Mode pooler port (6543) in src/ and deploy/

    ASCII-only so Windows PowerShell 5.1 and pwsh 7+ parse it identically.
#>
[CmdletBinding()]
param(
    [string]$Root
)

$ErrorActionPreference = 'Stop'
if (-not $Root) {
    $Root = (Resolve-Path (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\..')).Path
}

$violations = New-Object System.Collections.Generic.List[string]

function Get-SourceFiles([string]$RelativePath) {
    $base = Join-Path $Root $RelativePath
    if (-not (Test-Path $base)) { return @() }
    return @(Get-ChildItem -Path $base -Recurse -File | Where-Object {
        $_.FullName -notmatch '[\\/](bin|obj|node_modules|dist|artifacts|App_Data)[\\/]' -and
        $_.Name -ne 'package-lock.json'
    })
}

function Scan-Pattern([System.IO.FileInfo[]]$Files, [string]$Pattern, [string]$RuleName) {
    foreach ($file in $Files) {
        $hits = Select-String -Path $file.FullName -Pattern $Pattern -AllMatches
        foreach ($hit in $hits) {
            $rel = $file.FullName.Substring($Root.Length).TrimStart('\', '/')
            $violations.Add("[$RuleName] ${rel}:$($hit.LineNumber): $($hit.Line.Trim())")
        }
    }
}

$srcFiles = Get-SourceFiles 'src'
$deployFiles = Get-SourceFiles 'deploy'
$clientFiles = Get-SourceFiles 'src/Nexus.Client'
$srcAndDeploy = @($srcFiles) + @($deployFiles)

# 1. No AllowAnyOrigin in CORS
Scan-Pattern $srcFiles '\bAllowAnyOrigin\s*\(' 'NO_ALLOW_ANY_ORIGIN'

# 2. No disabled TLS certificate validation
Scan-Pattern $srcAndDeploy 'DangerousAcceptAnyServerCertificateValidator|ServerCertificateCustomValidationCallback|TrustServerCertificate\s*=\s*true|SslMode\s*=\s*Disable' 'NO_DISABLED_TLS_VALIDATION'

# 3. No privileged storage credentials in frontend client
Scan-Pattern $clientFiles 'service_role|SUPABASE_SERVICE_ROLE|SupabaseServiceRoleKey|AWS_SECRET_ACCESS_KEY|R2_SECRET_ACCESS_KEY' 'NO_PRIVILEGED_STORAGE_KEYS_IN_CLIENT'

# 4. No Supabase Transaction Mode pooler port 6543
Scan-Pattern $srcAndDeploy 'pooler\.supabase\.com:6543|Port\s*=\s*6543' 'SUPABASE_SESSION_MODE_5432_ONLY'

if ($violations.Count -gt 0) {
    foreach ($v in $violations) {
        Write-Host "ERROR $v" -ForegroundColor Red
    }
    exit 1
}

Write-Host "Security guardrails OK: scanned $($srcAndDeploy.Count) files (0 violations)."
