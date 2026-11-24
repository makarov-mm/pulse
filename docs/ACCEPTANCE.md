# Windows acceptance checks

## Repeatable checks

Run `tests/Run.ps1` for the English, Russian and German WinUI checks. Add `-Extended` for a live 15-minute history, two 20-second CPU workloads, a five-minute delayed report, saved recordings and cancellation during a system-volume scan. Each extended run writes its own metrics, two recordings and an acceptance result in the test artifacts directory.

The window checks exercise resizing, column order, visibility, frozen names, sorting, independent tab preferences and reopening a window from disk-backed settings. Diagnostics checks cover offline comparison, missing samples, exited and reused PIDs, bounded history with 400 processes, pinned charts, GPU memory and engine attribution. Connection checks match rates by address family, local and remote endpoints, protocol and PID. The disk fixture checks logical and allocated bytes.

## Measurements on November 24–25, 2026

The baseline was built from commit `600731e`. The release candidate included the tables, GPU sampling, process history and diagnostics window. Both builds used the same profiling loop. Both isolated test sessions could see three processes. A background-priority metadata scan overlapped part of the candidate run. CPU is per-process CPU time. Each interval had a six-second warm-up. Measurements for the 2- and 5-second settings lasted 30 seconds each. The 1-second setting ran for 911 seconds on the baseline and 912 seconds on the release candidate.

| Refresh | Baseline CPU, % of one logical core | New CPU, % of one logical core | Baseline final working set / private, MiB | New final working set / private, MiB |
| --- | ---: | ---: | ---: | ---: |
| 1 s | 2.22 | 2.41 | 195.9 / 127.5 | 208.3 / 143.0 |
| 2 s | 3.07 | 2.08 | 179.4 / 115.8 | 182.2 / 119.4 |
| 5 s | 0.88 | 0.68 | 180.6 / 116.3 | 179.9 / 116.7 |

Between the first and last ten-second readings at 1 Hz, private memory grew by 11.2 MiB in the baseline and 24.1 MiB in the release candidate. The desktop smoke test displayed about 289 processes. Its process count differs materially from the performance fixture. A full desktop performance baseline remains a separate field check.

| Check | Evidence |
| --- | --- |
| Physical desktop | Pulse reported 32 logical processors, AMD Radeon 610M memory and engine readings, and NVIDIA GeForce RTX 4060 Laptop GPU attribution in the process table. Windows Task Manager showed 43–44% physical memory usage around the same time as Pulse's 44.3%. |
| Independent Windows lookups | The process identity, creation time, session and base priority matched the .NET Windows process API. All five listed startup entries existed in their registry keys or Startup folders. Pulse retrieved a session user. The isolated session had no accessible owner-PID socket tables. Netstat also omitted the controlled loopback endpoints. Netsh could not supply the adapter reference list. |
| GPU workloads | An offscreen Direct3D workload completed 160 draws on the NVIDIA adapter. Media Foundation decoded 150 frames to 150 DXGI surfaces. The isolated workload session could not read Windows GPU performance counters. Matching both workloads to the live Pulse engine and adapter remains a field check. |
| Process inspection | Three file open/close cycles in a monitored child process increased its handle count from 207 to 231 and returned it to 207 each time. The sampler identified its known busy thread at 2.93–3.02% of total CPU across 32 logical processors in two runs and dropped the thread after exit. Service Control Manager returned access denied in the isolated session. |
| ETW decoding | Replaying the Microsoft TraceEvent fixture through the application's accumulator produced 16 file groups and 26 endpoint groups from 1,229 disk and 526 network events. Independent event sums matched the reported read, write, receive and send byte counts. [Microsoft ETL fixture](https://github.com/microsoft/perfview/blob/main/src/TraceEvent/TraceEvent.Tests/inputs/net.4.5.2.x64.etl.zip) |
| ETW permissions | A standard Windows desktop session reported the kernel tracing access error and kept Pulse responsive. Live rates with an elevated recorder still require a suitably authorized session. |
| Disk space | Pulse and the Windows Shell property store used by Explorer both reported 80,235 logical and 86,016 allocated bytes for the two-file fixture. A system-volume scan cancelled in 29 ms. A separate background-priority walk completed on the 953 GiB system volume in 24.1 minutes. It counted 396,949 directories and 1,914,619 file entries. Pulse marked 228 directories unread. The accessible logical total was 953,440,044,985 bytes. Windows withheld some metadata, so Pulse marked total allocation as partial. |
| Themes and device changes | The desktop smoke test used the system dark theme. The three-language WinUI suite also switched both application windows between light and dark without changing the Windows setting. Automated checks exercise removal, recovery and timestamp gaps. A physical disconnect, actual sleep/resume, system high contrast and comparison against Performance Monitor on an unrestricted desktop remain field checks. |
| Recording and playback | Two 20-second CPU workloads exited. Five minutes after the second run, the first interval was still present. Its process identities, CPU times, private-memory changes and I/O totals matched the reopened recording. System CPU and memory peaks survived both local round trips. The measured workloads contributed 20.062 and 19.094 CPU seconds in a repeated run. Opening the two files on a second Windows machine remains a field check. |

Other field checks include comparing every tab against Resource Monitor or Task Manager under the same workload and checking the service-to-host mapping in a desktop session with service enumeration access. App history and an NTFS master-file-table scanner remain conditional items in the roadmap.
