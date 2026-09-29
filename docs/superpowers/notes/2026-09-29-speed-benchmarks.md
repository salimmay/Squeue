# Speed benchmarks

How to reproduce: see the header of `bench/Squeue.Bench/Program.cs`. Numbers depend on the drives; rerun on your own card and backup drives.

## Baseline (before plan 3), 2026-09-29, Intel Core i5-14600K, source E: (NVMe, NTFS) -> destination E: and C: (NVMe)

Source: 1 x 1024 MB file plus 1000 x 300 KB files, read from memory (warm-up pass). Each number is the median of 3 runs (`--rounds 3`, engines rotated each round). E: -> E: is the same physical drive, so reads and writes share it. "Cache only" returns before data reaches the disk; Squeue and "+ flush" both wait for it. The bench adds each file's journal entry in its own commit inside the timed loop (the app adds a whole job in one commit), so Squeue's small-file times here are slightly pessimistic.

Destination E:

| Engine | Median seconds | Median MB/s | Runs (s) |
| --- | ---: | ---: | --- |
| Windows (CopyFileEx, cache only) | 0.5 | 2883 | 0.4, 0.5, 0.5 |
| Windows + flush each file | 5.4 | 246 | 5.5, 5.4, 5.3 |
| Squeue, no verify | 4.1 | 324 | 4.3, 4.1, 4.1 |
| Squeue, verify (xxh3) | 10.2 | 129 | 10.3, 10.2, 10.2 |

Destination C:

| Engine | Median seconds | Median MB/s | Runs (s) |
| --- | ---: | ---: | --- |
| Windows (CopyFileEx, cache only) | 0.6 | 2175 | 0.6, 0.6, 0.6 |
| Windows + flush each file | 5.9 | 222 | 5.9, 5.9, 6.3 |
| Squeue, no verify | 4.7 | 280 | 4.8, 4.7, 4.7 |
| Squeue, verify (xxh3) | 11.1 | 119 | 11.3, 11.1, 11.1 |

## After plan 3, 2026-09-29, same machine, same source

Same source (1 x 1024 MB + 1000 x 300 KB), same method (`--rounds 3`, medians, warm-up read first), branch `feat/speed` after read-ahead on a reader thread and five journal commits per file. Defaults are chunk 4 MB, depth 4. Commands, all with `dotnet run --project bench/Squeue.Bench -c Release -- run <src> <dst> --rounds 3`.

Source E: -> destination E: (defaults)

| Engine | Median seconds | Median MB/s | Runs (s) |
| --- | ---: | ---: | --- |
| Windows (CopyFileEx, cache only) | 0.5 | 2644 | 0.5, 0.5, 0.5 |
| Windows + flush each file | 5.5 | 239 | 5.6, 5.5, 5.5 |
| Squeue, no verify | 3.2 | 407 | 3.4, 3.2, 3.2 |
| Squeue, verify (xxh3) | 8.6 | 154 | 8.7, 8.6, 8.6 |

Source E: -> destination C: (defaults)

| Engine | Median seconds | Median MB/s | Runs (s) |
| --- | ---: | ---: | --- |
| Windows (CopyFileEx, cache only) | 0.6 | 2136 | 0.6, 0.6, 0.7 |
| Windows + flush each file | 6.1 | 216 | 6.1, 5.9, 7.1 |
| Squeue, no verify | 3.8 | 349 | 3.8, 3.8, 3.7 |
| Squeue, verify (xxh3) | 9.2 | 144 | 9.1, 12.2, 9.2 |

E: -> E:, `--depth 2`

| Engine | Median seconds | Median MB/s | Runs (s) |
| --- | ---: | ---: | --- |
| Windows (CopyFileEx, cache only) | 0.5 | 2691 | 0.5, 0.5, 0.5 |
| Windows + flush each file | 5.4 | 242 | 5.4, 5.4, 5.7 |
| Squeue, no verify | 3.2 | 414 | 3.4, 3.1, 3.2 |
| Squeue, verify (xxh3) | 8.6 | 154 | 8.9, 8.6, 8.5 |

E: -> E:, `--chunk-mb 8`

| Engine | Median seconds | Median MB/s | Runs (s) |
| --- | ---: | ---: | --- |
| Windows (CopyFileEx, cache only) | 0.5 | 2597 | 0.5, 0.5, 0.5 |
| Windows + flush each file | 5.5 | 240 | 5.5, 5.5, 5.4 |
| Squeue, no verify | 3.3 | 404 | 3.5, 3.3, 3.2 |
| Squeue, verify (xxh3) | 8.7 | 151 | 8.9, 8.7, 8.6 |

The two halves separately, E: -> E: (defaults)

Only the 1000 small files (1000 files, 293 MB):

| Engine | Median seconds | Median MB/s | Runs (s) |
| --- | ---: | ---: | --- |
| Windows (CopyFileEx, cache only) | 0.3 | 1126 | 0.3, 0.3, 0.3 |
| Windows + flush each file | 4.9 | 60 | 4.9, 4.9, 4.8 |
| Squeue, no verify | 2.6 | 112 | 2.6, 2.6, 2.6 |
| Squeue, verify (xxh3) | 7.5 | 39 | 7.6, 7.5, 7.5 |

Only the big file (1 file, 1024 MB; hard link to the source's big.bin):

| Engine | Median seconds | Median MB/s | Runs (s) |
| --- | ---: | ---: | --- |
| Windows (CopyFileEx, cache only) | 0.2 | 4778 | 0.2, 0.2, 0.2 |
| Windows + flush each file | 0.5 | 1948 | 0.5, 0.5, 0.5 |
| Squeue, no verify | 0.7 | 1565 | 0.7, 0.7, 0.6 |
| Squeue, verify (xxh3) | 0.9 | 1183 | 1.1, 0.9, 0.9 |

## Reading the numbers

Before -> after, median seconds, defaults (percent is the change in time, negative is faster):

| Destination | Engine | Before | After | Change |
| --- | --- | ---: | ---: | ---: |
| E: | Windows, cache only | 0.5 | 0.5 | 0% |
| E: | Windows + flush each file | 5.4 | 5.5 | +2% |
| E: | Squeue, no verify | 4.1 | 3.2 | -22% |
| E: | Squeue, verify | 10.2 | 8.6 | -16% |
| C: | Windows, cache only | 0.6 | 0.6 | 0% |
| C: | Windows + flush each file | 5.9 | 6.1 | +3% |
| C: | Squeue, no verify | 4.7 | 3.8 | -19% |
| C: | Squeue, verify | 11.1 | 9.2 | -17% |

The Windows rows did not move (they are the control; +2%/+3% is noise). Squeue got about a fifth faster without verify and about a sixth faster with it. Squeue without verify now beats "Windows + flush each file" on the mixed set (3.2 s vs 5.5 s on E:, 3.8 s vs 6.1 s on C:). Both rows wait for file data to reach the disk, but they differ in method: the Windows row reopens each file to flush it, while Squeue flushes through the handle it already has. Additionally, the Squeue journal lives on C: (%TEMP%) during these runs, so in E:→E: runs its database syncs do not share the data drive with the copies. Squeue's durable copy time is in the same range as Windows plus a flush, not faster at equal durability.

The measured gain (4.1 → 3.2 s on E: without verify) matches the 4 fewer database commits per file (about 0.9 ms each × 1000 files ≈ 0.9 s). The read-ahead engine is not shown to help in these runs: small files do not use it, the source was read from memory (warm cache), and the big file was not measured on its own before the change. Its benefit on a slow or cold source (an SD card, a USB drive) is not yet measured.

Where Squeue is still slower:

- Squeue with verify (8.6 s on E:, 9.2 s on C:) is slower than "Windows + flush each file" (5.5 s, 6.1 s). Verification re-reads every written file, so this is expected, but it is the cost of the safety feature, roughly 5 s extra here.
- On the big file alone, Squeue is slower than Windows even with flushing: 0.7 s (no verify) and 0.9 s (verify) against 0.5 s for "Windows + flush". Against cache-only Windows (0.2 s) the gap is larger, but that engine does not wait for the disk and is not a like-for-like comparison.
- All Squeue rows are far behind Windows "cache only", which returns before data reaches the disk.

Small files versus the big file: the 1000 small files dominate the total (2.6 s of the 3.2 s no-verify run; 7.5 s of 8.6 s with verify). Squeue beats "Windows + flush" on the small files (2.6 s vs 4.9 s) and loses on the big file (0.7 s vs 0.5 s). Verification costs little on the big file (+0.2 s) and a lot on the small files (+4.9 s, nearly triple the no-verify time). Verification adds no journal commits (a verified new file takes the same 5); per file it adds opening the new copy uncached, reading it back, and closing it. The ~4.9 ms per small file is far more than that read should cost on NVMe; a likely cause (unconfirmed) is antivirus scanning a just-written file when it is first opened for reading. The same cost appears in the "Windows + flush" row, which also reopens each file. Suggested check: rerun the small-files set with a Windows Defender exclusion on the destination.

Depth and chunk size: neither changed anything measurable. `--depth 2` gave 3.2 s / 8.6 s, the same as depth 4; `--chunk-mb 8` gave 3.3 s / 8.7 s against 3.2 s / 8.6 s, within run-to-run spread (about 0.2 s). The defaults stay.

Caveats: source data is read from memory after a warm-up pass, so these runs measure the write and verify side, not cold reads. E: -> E: shares one drive for reads and writes. One run on C: (Squeue verify, 12.2 s) was an outlier; the median is unaffected. The 1000-small-files half is where the remaining time is, so per-file overhead in the verify pass is the next place to look; unbuffered writes for the big file would only help the big-file row (0.7 s).
