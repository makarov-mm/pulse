# Pulse roadmap

The milestones below are planned work. The [README](README.md) describes the features available in the current build.

## 1. Identify the resource under pressure

**Question.** Is one core, disk or adapter saturated while the overall graph looks quiet?

- Add drill-down views for logical processors, physical disks and network interfaces. Show units and the selected time span on every axis.
- For disks, expose throughput, active time and response time when Windows supplies those counters. For network adapters, show direction, link speed and the selected interface.
- Keep the compact overview and make each resource view reachable by mouse and keyboard.
- **Acceptance.** A user can follow a disk spike to a named device and distinguish high throughput from high latency. Disconnecting a device leaves the remaining graphs usable. Compare measurements and Pulse's own CPU and memory footprint against a recorded baseline at each refresh interval.

**Status.** The CPU, disk and network cards open device views with one tile per logical processor, physical disk and network adapter. Disk tiles show throughput, active time and response time. Adapter tiles show direction and link speed. A selected tile expands into a larger graph with its maximum and time span labelled. Remaining work:

- Verify the per-device counters against Task Manager and Performance Monitor, including a disk or adapter removed while Pulse is running.
- Record the baseline and compare Pulse's CPU and memory footprint at each refresh interval with the device counters enabled.
- Exercise the device views with Windows high contrast enabled. Keyboard expansion, collapse and arrow navigation have been checked on the desktop. The WinUI regression checks cover device views in all three shipped languages.

Windows Task Manager's Performance view supplies the device-level model. btop4win informs the compact graphs, selectable disks and keyboard navigation. [Task Manager](https://learn.microsoft.com/en-us/troubleshoot/windows-server/support-tools/support-tools-task-manager) · [btop4win](https://github.com/aristocratos/btop4win)

## 2. Explain a spike after it has passed

**Question.** What was running when the machine slowed down several minutes ago?

- Extend the current one-minute history with an optional 15-minute window. Store bounded samples keyed by PID and creation time.
- Let a user select a point on a graph to see the top CPU consumers and largest working sets at that instant. Show the sampled values and indicate when a process has since exited.
- **Acceptance.** Run a short-lived CPU-heavy process, let it exit, and select its peak five minutes later. The historical inspector still identifies the original process. Resuming from sleep leaves a visible gap in the timeline.

**Status.** The history window switches between 1 and 15 minutes. Clicking the CPU or memory graph lists the leading processes of that sample, keyed by PID and creation time, and marks those that have exited. Remaining work:

- Run the acceptance test with a short-lived CPU-heavy workload and an actual resume from sleep. Timestamped fixtures cover the 15-minute retention boundary, exited process identities and gaps in the graph.
- Reach a past sample from the keyboard.
- Measure memory growth over a full 15-minute history at the 1-second interval.

System Informer's history graphs link a point in time to the responsible process. This milestone builds on Pulse's existing sample tooltips. [System Informer](https://systeminformer.io/)

## 3. Inspect a process in context

**Question.** Which thread, service or connection accounts for this process's activity?

- Add on-demand inspector tabs for thread CPU deltas, services hosted by the process, and TCP connections with local and remote endpoints.
- Show what Windows makes available at the user's current access level. Close inspection handles when a tab closes or the selected process exits.
- **Acceptance.** Select a known service host, find its hosted service, and identify a busy thread during a controlled workload. Rapidly switch between processes and verify that counters and connections always belong to the selected process instance.

**Status.** The inspector lists the services hosted by the selected process, and the Services tab links each running service to its process. Thread CPU deltas and TCP connections are not started.

TaskExplorer's thread and socket panels inform the inspector. Task Manager's Services view supplies the service-to-process workflow. [TaskExplorer](https://github.com/DavidXanatos/TaskExplorer) · [Task Manager](https://learn.microsoft.com/en-us/troubleshoot/windows-server/support-tools/support-tools-task-manager)

## 4. Compare two runs

**Question.** Did a change reduce the same workload's CPU, memory or I/O use?

- Build on the history model from milestone 2. Export a selected time range with timestamps, units and sample intervals. Document the capture schema.
- Open a saved capture offline, compare two ranges, and flag differences in hardware or sampling intervals that affect interpretation.
- **Acceptance.** Capture a repeatable workload twice, reopen both files on another machine, and reproduce the peak CPU and memory values shown during capture. Missing samples survive the round trip.

## 5. Cover Task Manager and Resource Monitor

**Question.** Can Pulse replace both tools for everyday diagnosis?

- Give the lower pane tabs that match Task Manager's pages and add what Resource Monitor shows per process.
- **Done.** Processes with a collapsible tree and branch totals. Details with separate read and write rates. Users with per-session totals. Startup apps from the registry and Startup folders, with a jump to the key in Registry Editor. Services with status and host process.
- **Next.** Priority, owner and command line on the Details tab. Enabling and disabling startup entries. Disk activity by file and network activity by connection need thread, file and socket inspection from milestone 3 or an ETW session.
- App history depends on data that Windows collects for packaged apps and stays out of scope until its source is confirmed.
- **Acceptance.** For each tab, compare the listed items and values with Task Manager or Resource Monitor on the same machine.

## 6. Show what fills a disk

**Question.** Which folders and files take the space on a nearly full volume?

- Scan a chosen volume or folder in the background. Show partial results while the scan runs and let the user cancel it.
- Present the result as a sunburst or treemap that drills down into a folder, with a sortable list of the largest items beside it.
- Open the selected item in File Explorer. Pulse does not delete files.
- Start with a directory walk at the user's access level. Consider reading the NTFS master file table for speed only if Pulse gains an elevated mode.
- **Acceptance.** The totals for a test folder match File Explorer's properties. Folders the user cannot read are listed as unread instead of counted as empty. Cancelling a scan of a large volume returns control within a second.

WinDirStat and WizTree inform the views. A registry editor is out of scope. Startup entries link to Registry Editor instead. [WinDirStat](https://windirstat.net/) · [WizTree](https://diskanalyzer.com/)

Before releasing each milestone, exercise its keyboard path, light and dark themes, high contrast, and the shipped translations. Measure idle overhead and memory growth using the same process count and refresh interval as the baseline.
