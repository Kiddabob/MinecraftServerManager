param(
    [switch]$Silent
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseDirectory = Join-Path $repositoryRoot 'artifacts\releases'
$installer = Get-ChildItem -LiteralPath $releaseDirectory -Filter '*Setup.exe' -File |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1

if ($null -eq $installer) {
    throw "No installer was found in $releaseDirectory. Run scripts\build-release.ps1 first."
}

$arguments = if ($Silent) { @('--silent') } else { @() }
$process = Start-Process -FilePath $installer.FullName -ArgumentList $arguments -PassThru -Wait
if ($process.ExitCode -ne 0) {
    throw "Installer exited with code $($process.ExitCode)."
}

Write-Output "Installed from $($installer.FullName)"
