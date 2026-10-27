$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-Checked {
    param([string]$Command, [string[]]$CommandArguments)
    & $Command @CommandArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Falha ao executar $Command (codigo $LASTEXITCODE)."
    }
}

Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'Execute este script no PowerShell do Windows.'
    }
    foreach ($command in @('dotnet.exe', 'npm.cmd', 'docker.exe')) {
        if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
            throw "Comando $command ausente. Instale .NET SDK 10.0.204, Node.js 22.12+ com npm e Docker Compose no Windows."
        }
    }
    if ([string]::IsNullOrWhiteSpace($env:DINAMICO_DATABASE_CONNECTION)) {
        if (-not [string]::IsNullOrWhiteSpace($env:DINAMICO_POSTGRES_PASSWORD)) {
            throw 'Defina DINAMICO_DATABASE_CONNECTION para usar sua senha personalizada.'
        }
        $databasePort = $env:DINAMICO_POSTGRES_PORT
        if ([string]::IsNullOrWhiteSpace($databasePort)) { $databasePort = '55432' }
        $env:DINAMICO_DATABASE_CONNECTION = "Host=127.0.0.1;Port=$databasePort;Username=dinamico;Password=dinamico-local-m2;Database=dinamico_m2"
    }

    Invoke-Checked 'docker.exe' @('compose', '-f', 'compose.m2.yaml', 'up', '-d', '--wait')
    Invoke-Checked 'npm.cmd' @('ci', '--prefix', 'FrontEnd')
    Invoke-Checked 'npm.cmd' @('run', 'build', '--prefix', 'FrontEnd')
    Invoke-Checked 'dotnet.exe' @('restore', 'Dinamico.slnx', '--locked-mode')
    Invoke-Checked 'dotnet.exe' @('build', 'src/Dinamico.Web/Dinamico.Web.csproj', '-c', 'Release', '--no-restore')
    Write-Host 'Abra http://127.0.0.1:5080 - Ctrl+C encerra o painel e interrompe a execucao ativa.'
    Invoke-Checked 'dotnet.exe' @('src/Dinamico.Web/bin/Release/net10.0/Dinamico.Web.dll')
}
finally {
    Pop-Location
}
