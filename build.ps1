<#
.SYNOPSIS
  Tests and publishes Squeue as a single self-contained exe in dist\Squeue.
.PARAMETER SkipTests
  Skip the test run.
#>
param([switch]$SkipTests)

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
        -p:DebugType=embedded -o dist/Squeue
    if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit code $LASTEXITCODE)." }

    if (-not (Test-Path $exe)) { throw "Expected $exe was not produced." }
    $mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "Built $exe ($mb MB)"
}
finally {
    Pop-Location
}
