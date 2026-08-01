param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+([-.+][0-9A-Za-z.-]+)?$')]
    [string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repositoryRoot 'src\MinecraftServerManager\MinecraftServerManager.csproj'
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$publishDirectory = [IO.Path]::GetFullPath((Join-Path $artifactRoot 'publish\win-x64'))
$releaseDirectory = [IO.Path]::GetFullPath((Join-Path $artifactRoot 'releases'))

if (-not $publishDirectory.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean a publish directory outside the artifact root: $publishDirectory"
}

if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null

Push-Location $repositoryRoot
try {
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet tool restore failed with exit code $LASTEXITCODE."
    }

    dotnet publish $projectPath `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:Platform=x64 `
        -p:Version=$Version `
        -o $publishDirectory `
        --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    dotnet tool run vpk pack `
        --packId Kidda.MinecraftServerManager `
        --packVersion $Version `
        --packDir $publishDirectory `
        --mainExe MinecraftServerManager.exe `
        --packTitle "Minecraft Server Manager" `
        --packAuthors "Kiddabob" `
        --runtime win-x64 `
        --shortcuts Desktop,StartMenuRoot `
        --outputDir $releaseDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Velopack packaging failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

Write-Output "Release $Version created in $releaseDirectory"
