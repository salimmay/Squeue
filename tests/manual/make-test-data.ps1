<#
.SYNOPSIS
  Creates a test folder that looks like a camera card, for the manual tests.
.PARAMETER Root
  Where to create it. Default: SqueueTest on your Desktop.
.PARAMETER LargeGB
  Size of each of the two large video files, in GB. Default 2.
.EXAMPLE
  ./make-test-data.ps1
  ./make-test-data.ps1 -Root D:\SqueueTest -LargeGB 4
#>
param(
    [string]$Root = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'SqueueTest'),
    [int]$LargeGB = 2
)

$ErrorActionPreference = 'Stop'
$card = Join-Path $Root 'Card\DCIM'
New-Item -ItemType Directory -Force -Path $card | Out-Null

$rng = [System.Random]::new(42)

function Write-RandomFile([string]$path, [long]$bytes) {
    $buffer = [byte[]]::new([math]::Min($bytes, 4MB))
    $stream = [System.IO.File]::Create($path)
    try {
        $left = $bytes
        while ($left -gt 0) {
            $rng.NextBytes($buffer)
            $n = [int][math]::Min($left, $buffer.Length)
            $stream.Write($buffer, 0, $n)
            $left -= $n
        }
    }
    finally { $stream.Dispose() }
}

# 600 photos of 200 KB to 3 MB, in three camera folders.
foreach ($folder in '100CANON', '101CANON', '102CANON') {
    $dir = Join-Path $card $folder
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    for ($i = 1; $i -le 200; $i++) {
        $size = $rng.Next(200KB, 3MB)
        Write-RandomFile (Join-Path $dir ('IMG_{0:D4}.JPG' -f $i)) $size
    }
    Write-Host "Made $dir"
}

# Two large videos.
$video = Join-Path $card '103CANON'
New-Item -ItemType Directory -Force -Path $video | Out-Null
foreach ($name in 'MVI_0001.MP4', 'MVI_0002.MP4') {
    Write-RandomFile (Join-Path $video $name) ([long]$LargeGB * 1GB)
    Write-Host "Made $video\$name"
}

# Awkward names: empty file, non-English characters, spaces, a deep path.
$odd = Join-Path $card 'Odd names'
New-Item -ItemType Directory -Force -Path $odd | Out-Null
New-Item -ItemType File -Force -Path (Join-Path $odd 'empty.txt') | Out-Null
# "Café Été — 写真 ✓.jpg", built from code points so the script's own encoding doesn't matter.
$unicode = 'Caf{0} {1}t{0} {2} {3}{4} {5}.jpg' -f [char]0xE9, [char]0xC9, [char]0x2014, [char]0x5199, [char]0x771F, [char]0x2713
Write-RandomFile (Join-Path $odd $unicode) 100KB
Write-RandomFile (Join-Path $odd 'name with  two  spaces.jpg') 100KB
$deep = Join-Path $odd ('a' * 60)
$deep = Join-Path $deep ('b' * 60)
$deep = Join-Path $deep ('c' * 60)
$deep = Join-Path $deep ('d' * 60)
New-Item -ItemType Directory -Force -Path "\\?\$deep" | Out-Null
Write-RandomFile "\\?\$deep\deep-file.jpg" 100KB
Write-Host "Made $odd"

New-Item -ItemType Directory -Force -Path (Join-Path $Root 'Backup') | Out-Null

$files = (Get-ChildItem $card -Recurse -File).Count
$gb = [math]::Round((Get-ChildItem $card -Recurse -File | Measure-Object Length -Sum).Sum / 1GB, 2)
Write-Host ""
Write-Host "Source:      $card  ($files files, $gb GB)"
Write-Host "Destination: $(Join-Path $Root 'Backup')"
