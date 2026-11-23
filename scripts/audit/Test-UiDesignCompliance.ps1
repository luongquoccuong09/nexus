#requires -Version 5.1

<#
.SYNOPSIS
Audits Nexus frontend and desktop UI code against docs/design-system/README.md
(modeled after RevitR4Converter's Audit-UiXamlCompliance.ps1).

.DESCRIPTION
Enforces six automated design system & UI security rules:
  1. UI-TOKEN-FILE       - src/Nexus.Client/src/ui/tokens.css exists and defines core Ocean Blue Light/Dark tokens.
  2. UI-NO-HARDCODED-HEX - No raw #RGB/#RRGGBB hex color literals in client CSS/TS/TSX outside tokens.css.
  3. UI-BRAND-NAME       - User-facing UI & Desktop strings must use "Nexus", never "Project Nexus".
  4. UI-SAFE-HTML        - dangerouslySetInnerHTML may only invoke renderSafeMarkdown(...) (SEC-10).
  5. UI-ICON-BUTTON-A11Y - Every iconOnly <Button> must include an aria-label attribute.
  6. UI-TRUST-BADGE      - TrustBadge / UserNameWithBadge is exported from src/ui/Badge.tsx and used in main.tsx & chat.tsx (M1.4b).
#>
[CmdletBinding()]
param(
    [string]$Root = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
} else {
    $Root = [System.IO.Path]::GetFullPath($Root)
}

$findings = [System.Collections.Generic.List[object]]::new()
function Add-Finding {
    param(
        [string]$Rule,
        [string]$File,
        [string]$Message
    )
    $findings.Add([PSCustomObject]@{ Rule = $Rule; File = $File; Message = $Message })
}

$clientSrc = Join-Path $Root 'src\Nexus.Client\src'
$tokensFile = Join-Path $clientSrc 'ui\tokens.css'

# 1. Token file presence & core Ocean Blue Light/Dark tokens
if (-not (Test-Path -LiteralPath $tokensFile -PathType Leaf)) {
    Add-Finding 'UI-TOKEN-FILE' 'src/Nexus.Client/src/ui/tokens.css' 'Single-source-of-truth design token file is missing.'
} else {
    $tokensText = Get-Content -LiteralPath $tokensFile -Raw
    foreach ($requiredToken in @('--brand-950:', '--brand-600:', '--bg-app:', '--bg-surface:', '--text-primary:', 'body.dark')) {
        if ($tokensText -notmatch [regex]::Escape($requiredToken)) {
            Add-Finding 'UI-TOKEN-FILE' 'src/Nexus.Client/src/ui/tokens.css' "Required token or theme selector missing: $requiredToken"
        }
    }
}

# 2. Scan client source files
$clientFiles = @(Get-ChildItem -LiteralPath $clientSrc -Recurse -File -Include '*.css', '*.ts', '*.tsx')
$scanned = 0

foreach ($file in $clientFiles) {
    $scanned++
    $rel = $file.FullName.Substring($Root.Length + 1).Replace('\', '/')
    $content = Get-Content -LiteralPath $file.FullName -Raw

    # Rule 2: No hardcoded #hex colors outside tokens.css
    if ($rel -ne 'src/Nexus.Client/src/ui/tokens.css') {
        $hexMatches = [regex]::Matches($content, '(?<![A-Za-z0-9_&])#(?:[0-9A-Fa-f]{3}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})\b')
        foreach ($m in $hexMatches) {
            Add-Finding 'UI-NO-HARDCODED-HEX' $rel "Hardcoded hex color '$($m.Value)' found outside ui/tokens.css. Use a CSS custom property (var(--...)) instead."
        }
    }

    # Rule 3: User-facing brand must be "Nexus" (no "Project Nexus")
    if ($content -match '\bProject Nexus\b') {
        Add-Finding 'UI-BRAND-NAME' $rel 'User-facing string contains "Project Nexus". Use "Nexus" instead.'
    }

    # Rule 4: dangerouslySetInnerHTML restricted strictly to renderSafeMarkdown (SEC-10)
    $dangerMatches = [regex]::Matches($content, 'dangerouslySetInnerHTML\s*=\s*\{\{([^}]+)\}\}')
    foreach ($m in $dangerMatches) {
        if ($m.Groups[1].Value -notmatch 'renderSafeMarkdown\s*\(') {
            Add-Finding 'UI-SAFE-HTML' $rel "dangerouslySetInnerHTML without renderSafeMarkdown(...): $($m.Value)"
        }
    }

    # Rule 5: iconOnly <Button ...> must have aria-label (allow '=>' inside JSX arrow props)
    if ($file.Extension -eq '.tsx') {
        $iconBtns = [regex]::Matches($content, '<Button\b(?:=>|[^>])*\biconOnly\b(?:=>|[^>])*>')
        foreach ($m in $iconBtns) {
            if ($m.Value -notmatch '\baria-label=') {
                Add-Finding 'UI-ICON-BUTTON-A11Y' $rel 'iconOnly <Button> is missing an aria-label attribute.'
            }
        }
    }
}

# Check index.html & Desktop UI strings for "Project Nexus"
$brandCheckFiles = @(
    (Join-Path $Root 'src\Nexus.Client\index.html'),
    (Join-Path $Root 'src\Nexus.Desktop\MainForm.cs'),
    (Join-Path $Root 'src\Nexus.Desktop\ServerUrlDialog.cs'),
    (Join-Path $Root 'src\Nexus.Desktop\DesktopConfig.cs')
)
foreach ($bf in $brandCheckFiles) {
    if (Test-Path -LiteralPath $bf -PathType Leaf) {
        $scanned++
        $rel = $bf.Substring($Root.Length + 1).Replace('\', '/')
        $text = Get-Content -LiteralPath $bf -Raw
        if ($text -match '\bProject Nexus\b') {
            Add-Finding 'UI-BRAND-NAME' $rel 'User-facing UI string contains "Project Nexus". Use "Nexus" instead.'
        }
    }
}

# Rule 6: Trust badge primitive usage (M1.4b)
$badgeFile = Join-Path $clientSrc 'ui\Badge.tsx'
if (-not (Test-Path -LiteralPath $badgeFile -PathType Leaf)) {
    Add-Finding 'UI-TRUST-BADGE' 'src/Nexus.Client/src/ui/Badge.tsx' 'TrustBadge / UserNameWithBadge primitive is missing.'
} else {
    foreach ($consumer in @('main.tsx', 'chat.tsx')) {
        $p = Join-Path $clientSrc $consumer
        if (Test-Path -LiteralPath $p -PathType Leaf) {
            $t = Get-Content -LiteralPath $p -Raw
            if ($t -notmatch 'TrustBadge|UserNameWithBadge') {
                Add-Finding 'UI-TRUST-BADGE' "src/Nexus.Client/src/$consumer" 'Must render TrustBadge or UserNameWithBadge for M1.4b Unverified visibility.'
            }
        }
    }
}

if ($findings.Count -gt 0) {
    Write-Host "UI design compliance FAILED ($($findings.Count) violation(s)):" -ForegroundColor Red
    foreach ($f in $findings) {
        Write-Host ("  [{0}] {1}: {2}" -f $f.Rule, $f.File, $f.Message) -ForegroundColor Red
    }
    exit 1
}

Write-Host "UI design compliance OK: scanned $scanned files (0 violations)." -ForegroundColor Green
