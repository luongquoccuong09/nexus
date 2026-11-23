#requires -Version 5.1

<#
.SYNOPSIS
Generates WiX v4 payload component markup for Nexus.Desktop.Installer from a staged payload directory.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PayloadRoot,

    [Parameter(Mandatory)]
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolvedRoot = [System.IO.Path]::GetFullPath($PayloadRoot).TrimEnd('\')
if (-not (Test-Path -LiteralPath $resolvedRoot -PathType Container)) {
    throw "Payload root does not exist: $resolvedRoot"
}

function Get-SafeWixId {
    param(
        [Parameter(Mandatory)][string]$Prefix,
        [Parameter(Mandatory)][string]$RelativePath
    )
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hashBytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($RelativePath.ToLowerInvariant()))
    }
    finally {
        $sha.Dispose()
    }
    $hex = -join ($hashBytes[0..7] | ForEach-Object { $_.ToString('x2') })
    $clean = [regex]::Replace($RelativePath, '[^A-Za-z0-9_]', '_')
    if ($clean.Length -gt 28) {
        $clean = $clean.Substring($clean.Length - 28)
    }
    return "${Prefix}_${clean}_${hex}"
}

function Get-DeterministicGuid {
    param([Parameter(Mandatory)][string]$Key)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes("project-nexus-wix-component|$($Key.ToLowerInvariant())"))
    }
    finally {
        $sha.Dispose()
    }
    $bytes = New-Object byte[] 16
    [Array]::Copy($hash, 0, $bytes, 0, 16)
    $bytes[6] = ($bytes[6] -band 0x0F) -bor 0x50
    $bytes[8] = ($bytes[8] -band 0x3F) -bor 0x80
    return ([Guid]::new($bytes)).ToString('D').ToUpperInvariant()
}

function Escape-Xml {
    param([string]$Value)
    return [System.Security.SecurityElement]::Escape($Value)
}

$files = @(Get-ChildItem -LiteralPath $resolvedRoot -File -Recurse | Sort-Object FullName)
if ($files.Count -eq 0) {
    throw "No payload files found under: $resolvedRoot"
}

$subDirs = @{}
foreach ($file in $files) {
    $rel = $file.FullName.Substring($resolvedRoot.Length + 1)
    $dir = Split-Path -Parent $rel
    if (-not [string]::IsNullOrEmpty($dir)) {
        $parts = $dir -split '\\'
        $accum = ''
        foreach ($part in $parts) {
            $parent = $accum
            $accum = if ($accum) { "$accum\$part" } else { $part }
            if (-not $subDirs.ContainsKey($accum)) {
                $subDirs[$accum] = [PSCustomObject]@{
                    Path = $accum
                    Name = $part
                    Parent = $parent
                    Id = Get-SafeWixId -Prefix 'Dir' -RelativePath $accum
                }
            }
        }
    }
}

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')

# Emit nested directory definitions under INSTALLFOLDER
if ($subDirs.Count -gt 0) {
    [void]$sb.AppendLine('  <Fragment>')
    [void]$sb.AppendLine('    <DirectoryRef Id="INSTALLFOLDER">')

    function Write-DirNode {
        param([string]$ParentPath, [int]$Indent)
        $pad = ' ' * $Indent
        $children = @($subDirs.Values | Where-Object { $_.Parent -eq $ParentPath } | Sort-Object Name)
        foreach ($child in $children) {
            [void]$sb.AppendLine("$pad<Directory Id=`"$($child.Id)`" Name=`"$(Escape-Xml $child.Name)`">")
            Write-DirNode -ParentPath $child.Path -Indent ($Indent + 2)
            [void]$sb.AppendLine("$pad</Directory>")
        }
    }

    Write-DirNode -ParentPath '' -Indent 6
    [void]$sb.AppendLine('    </DirectoryRef>')
    [void]$sb.AppendLine('  </Fragment>')
}

[void]$sb.AppendLine('  <Fragment>')
[void]$sb.AppendLine('    <ComponentGroup Id="DesktopPayload">')

foreach ($file in $files) {
    $rel = $file.FullName.Substring($resolvedRoot.Length + 1)
    $dir = Split-Path -Parent $rel
    $dirId = if ([string]::IsNullOrEmpty($dir)) { 'INSTALLFOLDER' } else { $subDirs[$dir].Id }
    $isMainExe = $rel.Equals('ProjectNexus.exe', [System.StringComparison]::OrdinalIgnoreCase)
    $compId = if ($isMainExe) { 'Cmp_ProjectNexusExe' } else { Get-SafeWixId -Prefix 'Cmp' -RelativePath $rel }
    $fileId = if ($isMainExe) { 'ProjectNexusExe' } else { Get-SafeWixId -Prefix 'Fil' -RelativePath $rel }
    $regName = if ($isMainExe) { 'File_ProjectNexusExe' } else { Get-SafeWixId -Prefix 'Reg' -RelativePath $rel }

    $compGuid = Get-DeterministicGuid -Key "file:$rel"

    [void]$sb.AppendLine("      <Component Id=`"$compId`" Directory=`"$dirId`" Guid=`"$compGuid`">")
    [void]$sb.AppendLine("        <RegistryValue Root=`"HKCU`" Key=`"Software\ProjectNexus\Files`" Name=`"$regName`" Type=`"integer`" Value=`"1`" KeyPath=`"yes`" />")
    [void]$sb.AppendLine("        <File Id=`"$fileId`" Source=`"!(bindpath.Payload)\$(Escape-Xml $rel)`" />")
    [void]$sb.AppendLine('      </Component>')
}

if ($subDirs.Count -gt 0) {
    $cleanupGuid = Get-DeterministicGuid -Key 'dirs:cleanup'
    [void]$sb.AppendLine("      <Component Id=`"Cmp_RemoveSubDirs`" Directory=`"INSTALLFOLDER`" Guid=`"$cleanupGuid`">")
    [void]$sb.AppendLine('        <RegistryValue Root="HKCU" Key="Software\ProjectNexus\Files" Name="SubDirsCleanup" Type="integer" Value="1" KeyPath="yes" />')
    foreach ($dirEntry in ($subDirs.Values | Sort-Object { $_.Path.Length } -Descending)) {
        [void]$sb.AppendLine("        <RemoveFolder Id=`"Rm_$($dirEntry.Id)`" Directory=`"$($dirEntry.Id)`" On=`"uninstall`" />")
    }
    [void]$sb.AppendLine('      </Component>')
}

[void]$sb.AppendLine('    </ComponentGroup>')
[void]$sb.AppendLine('  </Fragment>')
[void]$sb.AppendLine('</Wix>')

$outDir = Split-Path -Parent $OutputPath
if (-not (Test-Path -LiteralPath $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}
[System.IO.File]::WriteAllText($OutputPath, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Generated WiX payload manifest ($($files.Count) files): $OutputPath"
