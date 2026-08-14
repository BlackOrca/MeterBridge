<#
.SYNOPSIS
    Baut das MeterBridge-Container-Image (kein Dockerfile - nutzt das im
    .NET SDK eingebaute PublishContainer-Target) und pusht es nach ghcr.io.
    Zählt danach die VERSION-Datei hoch.

.DESCRIPTION
    Voraussetzung: einmalig "docker login ghcr.io -u <github-user> -p <PAT
    mit write:packages>" ausführen. Der Push läuft direkt gegen die
    Registry, ein laufender Docker-Daemon ist dafür nicht nötig - nur die
    Credential-Datei (~/.docker/config.json), die "docker login" anlegt.

.PARAMETER Bump
    Welcher Versionsteil hochgezählt wird (patch/minor/major).

.PARAMETER Arch
    Ziel-Architektur (arm64 für 64-bit Raspberry Pi OS, arm für 32-bit).
#>
[CmdletBinding()]
param(
    [ValidateSet('major', 'minor', 'patch')]
    [string]$Bump = 'patch',

    [ValidateSet('arm64', 'arm')]
    [string]$Arch = 'arm64'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$versionFile = Join-Path $repoRoot 'VERSION'
$csproj = Join-Path $repoRoot 'src\MeterBridge\MeterBridge.csproj'

if (-not (Test-Path $csproj)) {
    throw "Projektdatei nicht gefunden: $csproj"
}

if (-not (Test-Path $versionFile)) {
    Set-Content -Path $versionFile -Value '0.1.0' -NoNewline -Encoding utf8
}

$current = (Get-Content $versionFile -Raw).Trim()
if ($current -notmatch '^(\d+)\.(\d+)\.(\d+)$') {
    throw "VERSION-Datei enthaelt keine gueltige Version (erwartet x.y.z): '$current'"
}
$majorNum = [int]$Matches[1]
$minorNum = [int]$Matches[2]
$patchNum = [int]$Matches[3]

switch ($Bump) {
    'major' { $majorNum++; $minorNum = 0; $patchNum = 0 }
    'minor' { $minorNum++; $patchNum = 0 }
    'patch' { $patchNum++ }
}
$newVersion = "$majorNum.$minorNum.$patchNum"

Write-Host "Baue MeterBridge $newVersion (linux-$Arch) und pushe nach ghcr.io/blackorca/meterbridge ..." -ForegroundColor Cyan

# ContainerImageTags (mehrere Tags in einem Property-Wert, durch ";"
# getrennt) lässt sich von PowerShell aus nicht zuverlässig als ein
# einzelnes Kommandozeilen-Argument durchreichen: MSBuilds "-p:"-Parser
# trennt an ";" (mehrere Properties pro Schalter), und %3B-Escaping
# verhindert zwar das, blockiert aber gleichzeitig auch die SDK-interne
# Aufsplittung von ContainerImageTags in einzelne Tags. Deshalb hier zwei
# separate Publish-Läufe mit je einem einzelnen ContainerImageTag - keine
# Semikolons im Spiel, keine Mehrdeutigkeit.
foreach ($tag in @($newVersion, 'latest')) {
    dotnet publish $csproj `
        -c Release `
        -r "linux-$Arch" `
        --self-contained false `
        -t:PublishContainer `
        -p:ContainerImageTag=$tag

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish (Tag '$tag') ist fehlgeschlagen (Exit Code $LASTEXITCODE) - VERSION bleibt bei $current."
    }
}

Set-Content -Path $versionFile -Value $newVersion -NoNewline -Encoding utf8
Write-Host "Fertig: ghcr.io/blackorca/meterbridge:$newVersion und :latest gepusht. VERSION -> $newVersion" -ForegroundColor Green
