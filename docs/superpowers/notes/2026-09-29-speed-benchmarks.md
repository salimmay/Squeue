# Speed benchmarks

How to reproduce: see the header of `bench/Squeue.Bench/Program.cs`. Numbers depend on the drives; rerun on your own card and backup drives.

## Baseline (before plan 3), 2026-09-29, Intel Core i5-14600K, source E: (NVMe, NTFS) -> destination E: and C: (NVMe)

Source: 1 x 1024 MB file plus 1000 x 300 KB files, read from memory (warm-up pass). Each number is the median of 3 runs (`--rounds 3`, engines rotated each round). E: -> E: is the same physical drive, so reads and writes share it. "Cache only" returns before data reaches the disk; Squeue and "+ flush" both wait for it.

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
