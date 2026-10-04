<#
.SYNOPSIS
  Keeps a file open so no other program can read it, until you press Enter.
  Used to make Squeue fail on one file on purpose.
.EXAMPLE
  ./lock-file.ps1 "$HOME\Desktop\SqueueTest\Card\DCIM\100CANON\IMG_0007.JPG"
#>
param([Parameter(Mandatory)][string]$Path)

$stream = [System.IO.File]::Open($Path, 'Open', 'Read', 'None')
try {
    Write-Host "Locked: $Path"
    Read-Host "Press Enter to unlock"
}
finally { $stream.Dispose() }
Write-Host "Unlocked."
