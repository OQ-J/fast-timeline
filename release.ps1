param(
    [string]$InputDll = (Join-Path $PSScriptRoot 'bin\build\Timeline.KK\Timeline.dll'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts')
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $InputDll -PathType Leaf)) {
    throw "Build Timeline.KK in Release first, or provide -InputDll: $InputDll"
}
$version = [Reflection.AssemblyName]::GetAssemblyName($InputDll).Version.ToString()
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$outputRoot = (Get-Item -LiteralPath $OutputDirectory).FullName
if ((Get-Item -LiteralPath $outputRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'OutputDirectory must not be a linked directory.'
}
$stageName = '.package-' + [Guid]::NewGuid().ToString('N')
$stagePath = Join-Path $outputRoot $stageName
$zipPath = Join-Path $outputRoot ("fast_timeline_v$version.zip")
try {
    $pluginFolder = Join-Path $stagePath 'BepInEx\plugins'
    New-Item -ItemType Directory -Path $pluginFolder -Force | Out-Null
    Copy-Item -LiteralPath $InputDll -Destination (Join-Path $pluginFolder 'fast_timeline.dll')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $stagePath
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $stagePath
    Compress-Archive -Path (Join-Path $stagePath '*') -DestinationPath $zipPath -Force
    Write-Output "Created $zipPath"
}
finally {
    if (Test-Path -LiteralPath $stagePath) {
        $resolvedStage = (Get-Item -LiteralPath $stagePath -Force).FullName
        if ((Split-Path -Parent $resolvedStage) -cne $outputRoot -or
            (Split-Path -Leaf $resolvedStage) -cne $stageName -or
            ((Get-Item -LiteralPath $resolvedStage -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Refusing to remove an unexpected packaging directory.'
        }
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
