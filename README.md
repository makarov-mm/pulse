# Pulse

A native Windows 11 task manager developed by Mykhailo Makarov. Pulse brings up to 15 minutes of system history, a searchable process tree and a live process inspector into one WinUI 3 window.

## Why use Pulse over Windows Task Manager?

| When you want to… | What Pulse gives you today |
| --- | --- |
| Find out which process led a CPU spike | Hover over the CPU graph. Each sample retains its leading process name, PID and CPU share for the selected time window, even if the process has since exited. The memory graph identifies the largest working set at each sample. |
| Find a worker process and understand who launched it | Search by name or PID in **Tree** mode. The results retain the chain of parent processes. Typing a worker's PID can reveal the browser, editor or build tool that spawned it. |
| Find the core, disk or adapter behind a busy overview graph | Open the device view from the CPU, disk or network card. Each logical processor, physical disk and network adapter has its own graph for the selected time window. Disk tiles separate throughput from active time and response time. |
| Compare system load with the activity of one process | Keep CPU, memory, disk and network history visible while inspecting a process's working set, private memory, read and write rates, thread count and handles. Pause to investigate a busy moment without the rows reordering. |

Windows Task Manager provides system-wide performance, process details and services in dedicated views. The table describes workflows available in the current Pulse build. [Windows Task Manager overview](https://learn.microsoft.com/en-us/troubleshoot/windows-server/support-tools/support-tools-task-manager)

## Run

Open the portable `Pulse.exe` on Windows 11 x64. It bundles .NET and the Windows App SDK and extracts its runtime files on first launch. Pulse follows the Windows app theme and accent color. The **ⓘ** button beside the refresh interval opens **About Pulse**, with the version and developer credit.

## Using the monitor

- Press **Ctrl+F** to search by process name or PID. Click a column heading to sort, and click it again to reverse the order.
- The process list opens as a tree of parent and child processes. Collapse a branch to see the number of processes in it and their combined CPU, memory, I/O and threads. Toggle **Tree** for a flat list. Select a row to inspect its executable path, start time, counters and the services it hosts.
- Switch to **Details** for a flat list with every counter in its own column: parent PID, session, working set, private bytes, read and write rates, handles and start time. It follows the sort order of the **Processes** tab. Double-click a row to show that process with its parents.
- Switch to **Users** for the processes, CPU, memory, I/O and threads of each session.
- Switch to **Connections** for every TCP socket with its owning process, local and remote address and state, including listening ports. Search by process, PID, address or port. Double-click a row to show its process. The process inspector lists the remote addresses of the selected process's open connections.
- Switch to **Startup apps** for the entries in the Run and RunOnce registry keys and the Startup folders, with their command, source, whether Windows has them enabled and whether their program is running now. Double-click an entry to open its key in Registry Editor or its file in File Explorer. Opening a key stores it as Registry Editor's last position.
- Switch to **Services** to list Windows services with their status and host process. Search by name, description or PID. Double-click a running service to show its process on the **Processes** tab.
- Use the chevron on the CPU, disk or network card to open its device view, and again to close it. The buttons are reachable with **Tab**. Device history is collected while the view is closed, so the tiles open with the available history already drawn.
- Click a device tile, or press **Enter** or **Space** on it, to expand it into a larger graph with its maximum and time span labelled. Arrow keys move between tiles. Select the tile again to shrink it.
- Choose a history window of 1 or 15 minutes beside the refresh interval. Pulse always keeps the last 15 minutes, so switching does not lose samples.
- Click the CPU or memory graph to list the five processes with the highest CPU use and the five largest working sets at that sample. Processes that have since exited are marked.
- Choose a refresh interval of 1, 2 or 5 seconds. The default is 2 seconds. **Pause** freezes the view, including graph history. **Resume** starts a fresh history.
- **End task** asks for confirmation. Pulse checks the selected process's creation time and critical status on the same handle it uses to end the process.

### Reading the numbers

| Metric | Meaning |
| --- | --- |
| CPU | A process at 100% uses the capacity of all logical processors on the machine. |
| Memory | The card shows physical memory in use. Process rows show working sets, which may include shared pages. The inspector also shows private bytes. |
| Disk | Combined throughput across physical disks, with read and write rates below the graph. |
| Process I/O | Bytes read and written by a process, including file, device and network I/O. These rates can differ from physical disk throughput. |
| Logical processors | One tile per logical processor, on a fixed 0–100% scale. Machines with several processor groups show the group number. |
| Physical disks | Read and write rates per disk. **Active** is the share of time the disk spent servicing requests. **Response** is the average time per transfer. A busy disk with low throughput and a long response time is limited by latency. |
| Network adapters | Receive and send rates per adapter, with the link speed Windows reports. |
| Network | Receive and send rates summed across active interfaces, including virtual adapters. Loopback and tunnel interfaces are excluded. Traffic traversing multiple interfaces may be counted more than once. |

A dash indicates that a counter is unavailable or another sample is needed. CPU and memory charts use a 0–100% scale. Throughput charts scale to the highest value in the visible window. Disk and adapter tiles show that maximum in the corner of each graph. A tile disappears when Windows stops reporting its device, and the other tiles keep their history.

## Languages

Pulse ships with English, Russian and German. At startup it chooses the first supported language in the Windows preferred language list, including regional variants such as `de-CH`. English is the fallback. To change the language, reorder **Settings → Time & language → Language & region → Preferred languages** and reopen Pulse. Numbers, dates and times use the Windows regional format.

English strings live in [Strings.resx](Localization/Strings.resx). To add a translation, copy it to `Localization/Strings.<language>.resx`, translate the values, preserve the numbered format placeholders, and add the language tag to `UiText.Supported`. Keep identifiers, comments and documentation in English.

## Build from source

Use Windows 11 x64, the .NET 11 SDK and Windows SDK 10.0.26100. The project uses C# 15 and WinUI 3 from Windows App SDK 2.7.0. Run these commands from the directory containing `Pulse.csproj`. The first restore downloads packages from NuGet.

```powershell
dotnet run --project Pulse.csproj -c Release
```

Build a self-contained `publish/Pulse.exe` with the portable profile. The profile bundles .NET and the Windows App SDK into one compressed executable.

```powershell
dotnet publish Pulse.csproj -c Release -p:PublishProfile=Portable -o publish
```

`publish.ps1` runs the same publish step and then the Windows regression checks, and writes their output to `publish/test-results.txt`. Pass `-SkipChecks` to publish only, or `-Output <folder>` to choose another folder.

```powershell
powershell -File .\publish.ps1
```

## Run the Windows regression checks

From the project directory, run `powershell -File .\tests\Run.ps1`. The test build opens its own WinUI window for each shipped language. It checks live sampling, device removal and recovery, short-window layout, gaps in graphs, pause and the 15-minute history boundary. Device changes and missing time intervals are simulated in the test process. Build output and reports go under `obj/window-checks`.

## Development roadmap

The current milestone identifies a saturated CPU core, physical disk or network adapter. The device views are in place and their measurements still need checking against a recorded baseline. The 15-minute window and the per-sample process list from milestone 2 are also in place. Later milestones cover process inspection and capture comparison. [Roadmap and acceptance criteria](ROADMAP.md)

