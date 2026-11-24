# Makarov Pulse roadmap

Makarov Pulse's main diagnostic goal is to answer “What slowed this machine down a few minutes ago?” Each milestone records progress toward that goal. [Features available in the current build](README.md)

## Implementation and acceptance

| Capability | Available in this build |
| --- | --- |
| Configurable tables | Drag and keyboard layout commands, column visibility, pinned names and per-tab preferences. |
| Slowdown analysis | Fifteen-minute per-process history, graph interval selection, offline recordings and comparison. |
| Process inspection | Separate thread, service and socket pages, plus a modeless watch window for a pinned process. |
| GPU and I/O | GPU memory and engine graphs by adapter, per-process engine attribution, optional kernel ETW activity and allocated file sizes. |

Field checks and performance measurements are tracked separately from the feature list. [Windows acceptance results](docs/ACCEPTANCE.md)

## 1. Identify the resource under pressure

**Question.** Is one core, disk or adapter saturated while the overall graph looks quiet?

- Add drill-down views for logical processors, physical disks and network interfaces. Show units and the selected time span on every axis.
- For disks, expose throughput, active time and response time when Windows supplies those counters. For network adapters, show direction, link speed and the selected interface.
- Keep the compact overview and make each resource view reachable by mouse and keyboard.
- **Acceptance.** A user can follow a disk spike to a named device and distinguish high throughput from high latency. Disconnecting a device leaves the remaining graphs usable. Compare measurements and Makarov Pulse's own CPU and memory footprint against a recorded baseline at each refresh interval.

**Status.** The CPU, disk and network cards open device views with one tile per logical processor, physical disk and network adapter. Disk tiles show throughput, active time and response time. Adapter tiles show direction and link speed. A selected tile expands into a larger graph with its maximum and time span labelled. Remaining work:

- Verify the per-device counters against Task Manager and Performance Monitor, including a disk or adapter removed while Makarov Pulse is running.
- Exercise the device views with Windows high contrast enabled. Keyboard expansion, collapse and arrow navigation have been checked on the desktop. The WinUI regression checks cover device views in all three shipped languages.

**GPU view.** Diagnostics groups dedicated memory, shared memory and individual engine charts under each adapter. The process table identifies the busiest engine and its adapter. Verify with video playback and a 3D workload that the card and process row identify the active engine on the correct adapter. [How Windows reports GPU utilization](https://devblogs.microsoft.com/directx/gpus-in-the-task-manager/)

Windows Task Manager's Performance view supplies the device-level model. btop4win informs the compact graphs, selectable disks and keyboard navigation. [Task Manager](https://learn.microsoft.com/en-us/troubleshoot/windows-server/support-tools/support-tools-task-manager) · [btop4win](https://github.com/aristocratos/btop4win)

## 2. Explain a spike after it has passed

**Question.** What was running when the machine slowed down several minutes ago?

- Extend the current one-minute history with an optional 15-minute window. Store bounded samples keyed by PID and creation time.
- Let a user select a point on a graph to see the top CPU consumers and largest working sets at that instant. Show the sampled values and indicate when a process has since exited.
- **Acceptance.** Run a short-lived CPU-heavy process, let it exit, and select its peak five minutes later. The historical inspector still identifies the original process. Resuming from sleep leaves a visible gap in the timeline.

**Status.** The history window switches between 1 and 15 minutes. Clicking the CPU or memory graph lists the leading processes of that sample, keyed by PID and creation time, and marks those that have exited. Remaining work:

- Verify an actual sleep and resume on a machine available for that test. The graph and capture regression checks cover gaps in timestamped samples. Keyboard sample selection and interval selection are implemented.

**Selected intervals.** Drag across a graph or select the interval with the keyboard. For each process, show CPU time consumed, change in private bytes, and bytes read and written during that interval. Keep exited processes identifiable by PID and creation time. Use the first and last available samples for each process and show gaps in coverage.

The point inspector shows five leaders. Interval rankings include every observed process instance, including one with moderate CPU across the entire slowdown. History is bounded to 900 seconds and 901 samples.

System Informer's history graphs link a point in time to the responsible process. This milestone builds on Makarov Pulse's existing sample tooltips. [System Informer](https://systeminformer.io/)

## 3. Inspect a process in context

**Question.** Which thread, service or connection accounts for this process's activity?

- Add on-demand inspector tabs for thread CPU deltas, services hosted by the process, and TCP connections with local and remote endpoints.
- Show what Windows makes available at the user's current access level. Close inspection handles when a tab closes or the selected process exits.
- **Acceptance.** Select a known service host, find its hosted service, and identify a busy thread during a controlled workload. Rapidly switch between processes and verify that counters and connections always belong to the selected process instance.

**Status.** The inspector has separate pages for threads ranked by CPU, hosted services and sockets. Selecting Watch process opens a separate timeline keyed by PID and creation time. The Connections table and inspector show endpoint rates while the activity recorder runs. Regression checks cover protocol and state filters, IPv6 scope IDs and partial table access.

**Pinned process.** Pin a process instance and plot its CPU, private bytes, thread count and handle count on a shared time axis while browsing other rows. To check the view, open and close the same document several times. The graph should show whether memory and handle counts return to their starting levels after each cycle. If the process exits, mark the end of its history even if Windows reuses its PID.

TaskExplorer's thread and socket panels inform the inspector. Task Manager's Services view supplies the service-to-process workflow. [TaskExplorer](https://github.com/DavidXanatos/TaskExplorer) · [Task Manager](https://learn.microsoft.com/en-us/troubleshoot/windows-server/support-tools/support-tools-task-manager)

## 4. Compare two runs

**Question.** Did a change reduce the same workload's CPU, memory or I/O use?

- Build on the history model from milestone 2. Export a selected time range with timestamps, units and sample intervals. Document the capture schema.
- Open a saved capture offline, compare two ranges, and flag differences in hardware or sampling intervals that affect interpretation.
- **First capture check.** Run a known workload for 20 seconds and let it exit. Five minutes later, select its interval and identify the process and its measured contribution. Save the interval, reopen it offline and verify that the process identity, CPU time, private-byte change and I/O totals match the live interval report.
- **Acceptance.** Capture a repeatable workload twice, reopen both files on another machine, and reproduce the peak CPU and memory values shown during capture. Missing samples survive the round trip.

**Status.** Save interval, Open recording and Compare recording are available in Diagnostics. The reference and current recordings have independent time ranges. The five-minute retention and local playback check passed with two real workloads. Second-machine playback remains a field check. [Capture schema](docs/CAPTURES.md)

## 5. Cover Task Manager and Resource Monitor

**Question.** Can Makarov Pulse replace both tools for everyday diagnosis?

- Give the lower pane tabs that match Task Manager's pages and add what Resource Monitor shows per process.
- **Configurable columns.** Drag header dividers to resize columns and drag headings to reorder them. Let users hide and restore individual columns and pin Name while scrolling horizontally. Save each tab's widths, visible columns, order, pinning and sort direction across restarts.
- **Table layout check.** In Details, place Name, Working set and Command line side by side. Pin Name, scroll to the right in a narrow window and confirm that the process stays identifiable. Reopen Makarov Pulse and verify that Details restores the layout and sort column while Services retains its own choices.
- **Done.** Processes with a collapsible tree and branch totals. Details with separate read and write rates, priority, user and command line. Users with per-session totals. Connections with TCP sockets and bound UDP ports per process. Network with adapters and the devices on their networks. Startup apps from the registry and Startup folders, with enabling, disabling, deleting and an editor for the name and command. Services with status, host process, start, stop, restart and startup type.
- **Available on demand.** Diagnostics can start an ETW session for physical disk transfers and endpoint byte rates. It reports access failures and lost events and stops its own session when the activity view closes.
- App history depends on data that Windows collects for packaged apps and stays out of scope until its source is confirmed.
- **Acceptance.** For each tab, compare the listed items and values with Task Manager or Resource Monitor on the same machine.

## 6. Show what fills a disk

**Question.** Which folders and files take the space on a nearly full volume?

- Scan a chosen volume or folder in the background. Show partial results while the scan runs and let the user cancel it.
- Present the result as a sunburst or treemap that drills down into a folder, with a sortable list of the largest items beside it.
- Open the selected item in File Explorer. Makarov Pulse does not delete files.
- Start with a directory walk at the user's access level. Consider reading the NTFS master file table for speed only if Makarov Pulse gains an elevated mode.
- **Acceptance.** The totals for a test folder match File Explorer's properties. Folders the user cannot read are listed as unread instead of counted as empty. Cancelling a scan of a large volume returns control within a second.

**Status.** The Disk space tab scans a selected drive or folder in the background. The treemap and list support drill-down. The list sorts by name, logical size, allocated size or share. The context menu selects an individual file or directory in File Explorer. Cancelling retains partial results, and the current folder opens in File Explorer. Regression checks cover logical byte totals, unread entries, keyboard focus during list reordering and the area assigned to smaller items. The system-volume scan, cancellation timing and comparison with Explorer are documented in the acceptance results.

WinDirStat and WizTree inform the views. A general registry editor is out of scope. Startup entries are edited in Makarov Pulse. [WinDirStat](https://windirstat.net/) · [WizTree](https://diskanalyzer.com/)

## Process provenance and maintenance

- **Available.** Open file location selects the inspected process executable in Explorer. Disk space opens Windows cleanup recommendations so the user can review candidates and choose what to remove.
- **Next.** Show the executable publisher, signature status and an on-demand SHA-256 digest beside its path. These fields help distinguish similarly named processes.
- **Startup investigation.** Add a copy action for the exact source registry key or Startup folder. Windows Registry Editor remains the general-purpose editor.

Before releasing each milestone, exercise its keyboard path, light and dark themes, high contrast, and the shipped translations. Measure idle overhead and memory growth using the same process count and refresh interval as the baseline.
