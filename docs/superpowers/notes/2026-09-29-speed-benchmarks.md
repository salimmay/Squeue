# Speed benchmarks

How to reproduce: see the header of `bench/Squeue.Bench/Program.cs`. Numbers depend on the drives; rerun on your own card and backup drives.

## Baseline (before plan 3), 2026-09-29, Intel Core i5-14600K, source E: (NVMe, NTFS) -> destination E: and C: (NVMe)

Source: 1 x 1024 MB file plus 1000 x 300 KB files, warm cache. Windows' figure is likely inflated by write-back caching on a fast NVMe; Squeue's includes journal overhead per file.

Destination E:

| Engine | Seconds | MB/s |
| --- | ---: | ---: |
| Windows (CopyFileEx) | 0.6 | 2082 |
| Squeue, no verify | 4.2 | 313 |
| Squeue, verify (xxh3) | 10.5 | 126 |

Destination C:

| Engine | Seconds | MB/s |
| --- | ---: | ---: |
| Windows (CopyFileEx) | 0.6 | 2161 |
| Squeue, no verify | 4.8 | 274 |
| Squeue, verify (xxh3) | 11.0 | 119 |
