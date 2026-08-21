<#
.SYNOPSIS
  TIA MCP build script - outputs UTF-8 log to avoid Chinese garbled text.
.DESCRIPTION
  Uses cmd /c to redirect dotnet build output to a temp file (preserving raw bytes),
  then writes back as UTF-8 without BOM. VS Code opens it correctly by default.
  The cmd /c approach completely bypasses PowerShell pipe capture issues in
  restricted hosts.
.EXAMPLE
  .\build.ps1                                  # Default Release / V18 / bin-verify
  .\build.ps1 -Configuration Debug             # Change config
  .\build.ps1 -TiaPortalLocation "C:/.../V20"  # Switch version
#>
param(
    [string]$Configuration       = 'Release',
    [string]$TiaPortalLocation   = 'C:/Program Files/Siemens/Automation/Portal V18',
    [string]$Project             = 'src/TiaMcpServer/TiaMcpServer.V18.csproj',
    [string]$LogPath             = ''
)

$ErrorActionPreference = 'Continue'

# Base directory: $PSScriptRoot works when .ps1 is called via &
$baseDir = $PSScriptRoot
if (-not $baseDir) { $baseDir = (Get-Location).Path }
if (-not $LogPath) { $LogPath = [string](Join-Path $baseDir 'build.log') }

$projPath = [string](Join-Path $baseDir $Project)
$tmpFile = [string](Join-Path $baseDir 'build.tmp')

# Use cmd /c for redirection - preserves raw bytes from dotnet output
$cmdArgs = "build `"$projPath`" -c $Configuration -p:TiaPortalLocation=`"$TiaPortalLocation`" -p:BaseOutputPath=bin-verify/ -p:BaseIntermediateOutputPath=obj-verify/ > `"$tmpFile`" 2>&1"
Write-Host "Running: dotnet $cmdArgs"
cmd /c "dotnet $cmdArgs"
$exitCode = $LASTEXITCODE

# Convert temp file to UTF-8 log
if (Test-Path $tmpFile) {
    $rawBytes = [System.IO.File]::ReadAllBytes($tmpFile)
    if ($rawBytes.Length -gt 0) {
        $text = $null
        $hasBadChars = $false
        $text = [System.Text.Encoding]::UTF8.GetString($rawBytes)
        $replacementChar = [char]0xFFFD
        $hasBadChars = $text.Contains($replacementChar)
        if ($hasBadChars) {
            $text = [System.Text.Encoding]::Default.GetString($rawBytes)
        }
        $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText($LogPath, $text, $utf8NoBom)
    }
    Remove-Item $tmpFile -Force -ErrorAction SilentlyContinue
}

Write-Host ("Build finished. exitCode={0}  log(UTF-8)={1}" -f $exitCode, $LogPath)
exit $exitCode