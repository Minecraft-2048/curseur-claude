# Compile CurseurClaude.exe avec le compilateur C# livré avec Windows (.NET Framework 4, C# 5).
#   .\construire.ps1            -> compile CurseurClaude.exe (l'appli ne doit pas tourner)
#   .\construire.ps1 -Relancer  -> compile à côté, arrête l'appli qui tourne, remplace l'exe et la relance
param([switch]$Relancer)

$ErrorActionPreference = 'Stop'
$ici = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe = Join-Path $ici 'CurseurClaude.exe'
$icone = Join-Path $ici 'assets\curseur-claude.ico'
$sources = Get-ChildItem (Join-Path $ici 'src\*.cs') | ForEach-Object { $_.FullName }

function Compiler([string]$sortie) {
    $options = @('/nologo', '/target:winexe', '/optimize+', '/unsafe', '/codepage:65001',
        "/out:$sortie", "/win32manifest:$(Join-Path $ici 'src\app.manifest')",
        '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll')
    if (Test-Path $icone) { $options += "/win32icon:$icone" }
    & $csc @options @sources
    if ($LASTEXITCODE -ne 0) { throw "La compilation a échoué." }
}

if ($Relancer) {
    $essai = Join-Path $ici 'obj\CurseurClaude.exe'
    New-Item -ItemType Directory -Force (Split-Path $essai) | Out-Null
    Compiler $essai
    $tourne = Get-Process CurseurClaude -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
    if ($tourne) {
        & $exe --quitter
        $tourne | ForEach-Object { $_.WaitForExit(5000) | Out-Null }
    }
    Copy-Item $essai $exe -Force
    Start-Process $exe
    "Compilé et relancé : $exe"
} else {
    Compiler $exe
    "Compilé : $exe"
}
