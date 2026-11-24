# Pulse recording format

A `.pulse` file is a UTF-8 JSON document. Pulse saves only the selected interval. The reader and writer support recordings up to 256 MiB. Select a shorter interval on systems with unusually high process counts. Version 1 uses the following keys. The arrays are ordered by monotonic sample time.

| Field | Meaning |
| --- | --- |
| `Version` | Integer format version, currently 1. |
| `Host` | Computer name, processor identifier, number of logical processors and physical memory in bytes when available. |
| `Samples[].Time` | Monotonic seconds on the recording machine. Differences between samples are meaningful within that recording. |
| `Clock` | Local observation timestamp including its UTC offset, encoded as ISO 8601. |
| `IntervalSeconds` | Configured polling interval at this sample. Actual observation times are stored separately. |
| `CpuPercent`, `UsedMemoryBytes` | System-wide CPU utilization and physical memory in use. An unavailable measurement is JSON `null`. |
| `Processes[].Key` | A process instance's PID and creation time as Windows FILETIME ticks. |
| `Name`, `CpuPercent` | Sampled image name and CPU utilization as a percentage of total machine capacity. |
| `CpuTicks`, `ReadBytes`, `WriteBytes` | Cumulative process CPU time in 100-nanosecond ticks and cumulative process I/O in bytes. Process I/O includes file, network and device operations. |
| `PrivateBytes`, `Threads`, `Handles` | Private committed memory in bytes, thread count and handle count at the sample. |

Pulse ranks processes by the sum of CPU time measured between adjacent observations of the same instance. Read and write totals use the same pairs. An interval longer than twice the larger configured sampling period plus half a second breaks continuity. Pulse excludes such gaps from cumulative CPU and I/O contributions. A single observation has no measurable delta. Private memory change uses the first and last available observations. The **Observed** column shows those timestamps.

Offline comparison shows the system CPU and memory peaks of each selected interval and their differences. The two interval selectors remain independent. A change in host metadata or sampling periods appears beside the comparison. Recordings contain process names and identities. Paths, command lines and activity trace events are outside the recording schema.
