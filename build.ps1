<#
.SYNOPSIS
  Tests and publishes Squeue as a single self-contained exe in dist\Squeue.
.PARAMETER SkipTests
  Skip the test run.
.PARAMETER Installer
  Also build dist\SqueueSetup.exe. Needs Inno Setup 6.
.PARAMETER Version
  Version shown by the app and the installer.
#>
param([switch]$SkipTests, [switch]$Installer, [string]$Version = '0.1.0')

$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    if (-not $SkipTests) {
        dotnet test
        if ($LASTEXITCODE -ne 0) { throw "Tests failed (exit code $LASTEXITCODE)." }
    }

    $exe = Join-Path $PSScriptRoot 'dist\Squeue\Squeue.exe'
    if (Test-Path $exe) {
        try {
            $stream = [System.IO.File]::Open($exe, 'Open', 'Write', 'None')
            $stream.Dispose()
        }
        catch [System.IO.IOException] {
            throw "Close Squeue before building: dist\Squeue\Squeue.exe is running."
        }
    }
    Remove-Item (Join-Path $PSScriptRoot 'dist\Squeue') -Recurse -Force -ErrorAction SilentlyContinue

    dotnet publish src/Squeue.App -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=embedded -p:Version=$Version -o dist/Squeue
    if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit code $LASTEXITCODE)." }

    if (-not (Test-Path $exe)) { throw "Expected $exe was not produced." }
    $mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "Built $exe ($mb MB)"

    if ($Installer) {
        $iscc = @(
            (Get-Command iscc -ErrorAction SilentlyContinue).Source,
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
        ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
        if (-not $iscc) { throw "Inno Setup 6 was not found. Install it from https://jrsoftware.org/isinfo.php." }

        & $iscc /Qp "/DAppVersion=$Version" installer\Squeue.iss
        if ($LASTEXITCODE -ne 0) { throw "Installer build failed (exit code $LASTEXITCODE)." }

        $setup = Join-Path $PSScriptRoot 'dist\SqueueSetup.exe'
        if (-not (Test-Path $setup)) { throw "Expected $setup was not produced." }
        $mb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
        Write-Host "Built $setup ($mb MB)"
    }
}
finally {
    Pop-Location
}
