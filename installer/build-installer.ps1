# Genera el instalador de ArmandoDB Studio:
#   1. Publica la aplicación "autónoma" (con .NET incluido) en ..\publish\win-x64.
#   2. La empaqueta con Inno Setup en installer\Output\ArmandoDBStudio-Setup-<versión>.exe.
# Uso, desde la carpeta del proyecto:  .\installer\build-installer.ps1
# Requiere Inno Setup 6:  winget install JRSoftware.InnoSetup

$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $project 'publish\win-x64'

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'No se encontró Inno Setup 6. Instálalo con:  winget install JRSoftware.InnoSetup' }

Write-Host 'Publicando la aplicación con .NET incluido...'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
# Sin símbolos de depuración (.pdb): no hacen falta en el equipo del usuario.
dotnet publish (Join-Path $project 'MySmdb.csproj') -c Release -r win-x64 --self-contained true -o $publish -p:DebugType=none -p:DebugSymbols=false --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Falló la publicación.' }

Write-Host 'Creando el instalador...'
& $iscc /Q (Join-Path $PSScriptRoot 'ArmandoDBStudio.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup devolvió un error.' }

$setup = Get-ChildItem (Join-Path $PSScriptRoot 'Output') -Filter 'ArmandoDBStudio-Setup-*.exe' | Sort-Object LastWriteTime | Select-Object -Last 1
Write-Host ("Listo: {0}  ({1:N1} MB)" -f $setup.FullName, ($setup.Length / 1MB))
