# Pulse roadmap

Pulse's main diagnostic goal is to answer “What slowed this machine down a few minutes ago?” Each milestone records progress toward that goal. [Features available in the current build](README.md)

## Release priorities

1. **Next release — configurable tables and a performance baseline.** Let users arrange each table for the task at hand. Record Pulse's own CPU and memory use at the 1-, 2- and 5-second refresh intervals before expanding history collection.
2. **Following release — explain and save a slowdown.** Make a selected time range the primary unit of investigation. A user should be able to identify an exited process, save that interval and reproduce the result offline.
3. **Then — watch one process over time.** Use the same history to investigate rising memory or handle counts during a repeatable task.
4. **After that — add GPU monitoring.** Connect each adapter's activity to the process and GPU engine responsible for it.

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

**Planned GPU view.** Give each GPU its own card. Show dedicated and shared memory usage and separate graphs for the engines the adapter exposes, including 3D, video decode and copy where available. Add the busiest GPU engine and its adapter to process rows. Verify with video playback and a 3D workload that the card and process row identify the active engine on the correct adapter. [How Windows reports GPU utilization](https://devblogs.microsoft.com/directx/gpus-in-the-task-manager/)

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

**Selected intervals.** Drag across a graph or select the interval with the keyboard. For each process, show CPU time consumed, change in private bytes, and bytes read and written during that interval. Keep exited processes identifiable by PID and creation time. Use the first and last available samples for each process and show gaps in coverage.

The current top five at each point cannot account for a process that uses moderate CPU throughout a slowdown. Retain bounded per-process measurements for the full history window so that such a process appears in the interval ranking. Check memory growth against the baseline from the preceding release.

System Informer's history graphs link a point in time to the responsible process. This milestone builds on Pulse's existing sample tooltips. [System Informer](https://systeminformer.io/)

## 3. Inspect a process in context

**Question.** Which thread, service or connection accounts for this process's activity?

- Add on-demand inspector tabs for thread CPU deltas, services hosted by the process, and TCP connections with local and remote endpoints.
- Show what Windows makes available at the user's current access level. Close inspection handles when a tab closes or the selected process exits.
- **Acceptance.** Select a known service host, find its hosted service, and identify a busy thread during a controlled workload. Rapidly switch between processes and verify that counters and connections always belong to the selected process instance.

**Status.** The inspector shows hosted services, the busiest five threads, both endpoints of established TCP connections, and local TCP listeners and UDP ports for the selected process. Services and Connections link back to a process by exact PID. Regression checks cover protocol and state filters, IPv6 scope IDs and partial table access. Separate inspector tabs and per-connection throughput remain open.

**Pinned process.** Pin a process instance and plot its CPU, private bytes, thread count and handle count on a shared time axis while browsing other rows. To check the view, open and close the same document several times. The graph should show whether memory and handle counts return to their starting levels after each cycle. If the process exits, mark the end of its history even if Windows reuses its PID.

TaskExplorer's thread and socket panels inform the inspector. Task Manager's Services view supplies the service-to-process workflow. [TaskExplorer](https://github.com/DavidXanatos/TaskExplorer) · [Task Manager](https://learn.microsoft.com/en-us/troubleshoot/windows-server/support-tools/support-tools-task-manager)

## 4. Compare two runs

**Question.** Did a change reduce the same workload's CPU, memory or I/O use?

- Build on the history model from milestone 2. Export a selected time range with timestamps, units and sample intervals. Document the capture schema.
- Open a saved capture offline, compare two ranges, and flag differences in hardware or sampling intervals that affect interpretation.
- **First capture check.** Run a known workload for 20 seconds and let it exit. Five minutes later, select its interval and identify the process and its measured contribution. Save the interval, reopen it offline and verify that the process identity, CPU time, private-byte change and I/O totals match the live interval report.
- **Acceptance.** Capture a repeatable workload twice, reopen both files on another machine, and reproduce the peak CPU and memory values shown during capture. Missing samples survive the round trip.

## 5. Cover Task Manager and Resource Monitor

**Question.** Can Pulse replace both tools for everyday diagnosis?

- Give the lower pane tabs that match Task Manager's pages and add what Resource Monitor shows per process.
- **Configurable columns.** Drag header dividers to resize columns and drag headings to reorder them. Let users hide and restore individual columns and pin Name while scrolling horizontally. Save each tab's widths, visible columns, order, pinning and sort direction across restarts.
- **Table layout check.** In Details, place Name, Working set and Command line side by side. Pin Name, scroll to the right in a narrow window and confirm that the process stays identifiable. Reopen Pulse and verify that Details restores the layout and sort column while Services retains its own choices.
- **Done.** Processes with a collapsible tree and branch totals. Details with separate read and write rates, priority, user and command line. Users with per-session totals. Connections with TCP sockets and bound UDP ports per process. Network with adapters and the devices on their networks. Startup apps from the registry and Startup folders, with enabling, disabling, deleting and an editor for the name and command. Services with status, host process, start, stop, restart and startup type.
- **Next.** Disk activity by file and byte rates per connection need an ETW session.
- App history depends on data that Windows collects for packaged apps and stays out of scope until its source is confirmed.
- **Acceptance.** For each tab, compare the listed items and values with Task Manager or Resource Monitor on the same machine.

## 6. Show what fills a disk

**Question.** Which folders and files take the space on a nearly full volume?

- Scan a chosen volume or folder in the background. Show partial results while the scan runs and let the user cancel it.
- Present the result as a sunburst or treemap that drills down into a folder, with a sortable list of the largest items beside it.
- Open the selected item in File Explorer. Pulse does not delete files.
- Start with a directory walk at the user's access level. Consider reading the NTFS master file table for speed only if Pulse gains an elevated mode.
- **Acceptance.** The totals for a test folder match File Explorer's properties. Folders the user cannot read are listed as unread instead of counted as empty. Cancelling a scan of a large volume returns control within a second.

**Status.** The Disk space tab scans a selected drive or folder in the background. The treemap and list support drill-down. The list sorts by name, size or share. Cancelling retains partial results, and the current folder opens in File Explorer. Regression checks cover logical byte totals, unread entries, keyboard focus during list reordering and the area assigned to smaller items. Remaining work:

- Run the acceptance checks against File Explorer and on a large volume.
- Report allocated size beside file length.

WinDirStat and WizTree inform the views. A general registry editor is out of scope. Startup entries are edited in Pulse. [WinDirStat](https://windirstat.net/) · [WizTree](https://diskanalyzer.com/)

Before releasing each milestone, exercise its keyboard path, light and dark themes, high contrast, and the shipped translations. Measure idle overhead and memory growth using the same process count and refresh interval as the baseline.
