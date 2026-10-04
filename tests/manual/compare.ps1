<#
.SYNOPSIS
  Checks that every file in Source exists in Destination with the same content.
  Independent of Squeue: uses Windows' own SHA-256.
.EXAMPLE
  ./compare.ps1 -Source "$HOME\Desktop\SqueueTest\Card\DCIM" -Destination "$HOME\Desktop\SqueueTest\Backup\DCIM"
#>
param(
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$Destination
)

$ErrorActionPreference = 'Stop'
$Source = (Resolve-Path -LiteralPath $Source).Path.TrimEnd('\')
$Destination = (Resolve-Path -LiteralPath $Destination).Path.TrimEnd('\')

$missing = 0; $different = 0; $same = 0
foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -File -Force) {
    $relative = $file.FullName.Substring($Source.Length + 1)
    $target = Join-Path $Destination $relative
    if (-not (Test-Path -LiteralPath $target)) {
        Write-Host "MISSING    $relative" -ForegroundColor Red
        $missing++
        continue
    }
    $a = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    $b = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
    if ($a -ne $b) {
        Write-Host "DIFFERENT  $relative" -ForegroundColor Red
        $different++
    }
    else { $same++ }
}

$leftovers = @(Get-ChildItem -LiteralPath $Destination -Recurse -File -Force -Filter '~tq*.tmp')
foreach ($t in $leftovers) { Write-Host "TEMP FILE  $($t.FullName)" -ForegroundColor Yellow }

Write-Host ""
Write-Host "Same: $same   Missing: $missing   Different: $different   Leftover temp files: $($leftovers.Count)"
if ($missing -or $different -or $leftovers.Count) { exit 1 }
Write-Host "PASS" -ForegroundColor Green
