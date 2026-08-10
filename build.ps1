[CmdletBinding()]
param(
    [string]$GameDirectory = 'D:\SteamLibrary\steamapps\common\Iron Nest Heavy Turret Simulator',
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot 'Release'
}

$repositoryRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$gameRoot = [IO.Path]::GetFullPath($GameDirectory).TrimEnd('\')
$releaseRoot = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
$releaseParent = Split-Path -Parent $releaseRoot
$stagingRoot = "$releaseRoot.tmp.$([Guid]::NewGuid().ToString('N'))"

function Assert-DirectoryExists {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Description does not exist: $Path"
    }
}

function Assert-SafeReleasePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $pathRoot = [IO.Path]::GetPathRoot($Path).TrimEnd('\')
    if ($Path -eq $pathRoot -or $Path -eq $repositoryRoot -or $Path -eq $gameRoot) {
        throw "Refusing to use an unsafe release directory: $Path"
    }
}

function Copy-ReleaseArtifact {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Source,

        [Parameter(Mandatory = $true)]
        [string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "Build artifact was not found: $Source"
    }

    $destinationParent = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

Assert-SafeReleasePath -Path $releaseRoot
Assert-DirectoryExists -Path $gameRoot -Description 'Game directory'
Assert-DirectoryExists -Path (Join-Path $gameRoot 'MelonLoader') -Description 'MelonLoader directory'

$solutionPath = Join-Path $repositoryRoot 'IronNestFCS.sln'
$buildArguments = @(
    'build'
    $solutionPath
    '--configuration', 'Release'
    '--nologo'
    '--no-incremental'
    "-p:GameDir=$gameRoot"
)

Write-Host 'Building IronNestFCS (Release)...' -ForegroundColor Cyan
& dotnet @buildArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE."
}

try {
    New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null

    $fcsRoot = Join-Path $stagingRoot 'IronNestFCS'
    $customRecordsRoot = Join-Path $stagingRoot 'CustomRecords'

    # Keep the empty data directory in the release layout for user-provided audio files.
    New-Item -ItemType Directory -Path (Join-Path $customRecordsRoot 'UserData\CustomRecords') -Force | Out-Null

    Copy-ReleaseArtifact `
        -Source (Join-Path $repositoryRoot 'IronNestFCS\bin\Release\IronNestFCS.dll') `
        -Destination (Join-Path $fcsRoot 'Mods\IronNestFCS.dll')
    Copy-ReleaseArtifact `
        -Source (Join-Path $gameRoot 'UserData\IronNestFCS\IronNestFCS.Logic.dll') `
        -Destination (Join-Path $fcsRoot 'UserData\IronNestFCS\IronNestFCS.Logic.dll')
    Copy-ReleaseArtifact `
        -Source (Join-Path $repositoryRoot 'IronNestFCS.Abstractions\bin\Release\IronNestFCS.Abstractions.dll') `
        -Destination (Join-Path $fcsRoot 'UserLibs\IronNestFCS.Abstractions.dll')

    Copy-ReleaseArtifact `
        -Source (Join-Path $gameRoot 'Mods\IronNestFCS.CustomRecords.dll') `
        -Destination (Join-Path $customRecordsRoot 'Mods\IronNestFCS.CustomRecords.dll')
    Copy-ReleaseArtifact `
        -Source (Join-Path $gameRoot 'UserLibs\CSCore.dll') `
        -Destination (Join-Path $customRecordsRoot 'UserLibs\CSCore.dll')
    Copy-ReleaseArtifact `
        -Source (Join-Path $gameRoot 'UserLibs\TagLibSharp.dll') `
        -Destination (Join-Path $customRecordsRoot 'UserLibs\TagLibSharp.dll')

    New-Item -ItemType Directory -Path $releaseParent -Force | Out-Null
    if (Test-Path -LiteralPath $releaseRoot) {
        Remove-Item -LiteralPath $releaseRoot -Recurse -Force
    }
    Move-Item -LiteralPath $stagingRoot -Destination $releaseRoot
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}

Write-Host "Release artifacts created at: $releaseRoot" -ForegroundColor Green
Get-ChildItem -LiteralPath $releaseRoot -Recurse -File |
    ForEach-Object { $_.FullName.Substring($releaseRoot.Length + 1) } |
    Sort-Object |
    ForEach-Object { Write-Host "  $_" }
