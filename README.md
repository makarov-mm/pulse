# Pulse

A native Windows 11 task manager developed by Mykhailo Makarov. Pulse brings up to 15 minutes of system history, a searchable process tree and a live process inspector into one WinUI 3 window.

## Why use Pulse over Windows Task Manager?

| When you want to… | What Pulse gives you today |
| --- | --- |
| Find out which process led a CPU spike | Hover over the CPU graph. Each sample retains its leading process name, PID and CPU share for the selected time window, even if the process has since exited. The memory graph identifies the largest working set at each sample. |
| Find a worker process and understand who launched it | Search by name or PID in **Tree** mode. The results retain the chain of parent processes. Typing a worker's PID can reveal the browser, editor or build tool that spawned it. |
| Find the core, disk or adapter behind a busy overview graph | Open the device view from the CPU, disk or network card. Each logical processor, physical disk and network adapter has its own graph for the selected time window. Disk tiles separate throughput from active time and response time. |
| Compare system load with the activity of one process | Keep CPU, memory, disk and network history visible while inspecting a process's working set, private memory, read and write rates, thread count and handles. Pause to investigate a busy moment without the rows reordering. |
| Explain a slowdown after the process exits | Drag across a graph to rank the processes observed in that interval. Save the interval and compare it with a recording of the same workload. |
| Find what fills a drive or project folder | Scan it from **Disk space**, then open a tile to see which subfolders and files account for its size. |

Windows Task Manager provides system-wide performance, process details and services in dedicated views. The table describes workflows available in the current Pulse build. [Windows Task Manager overview](https://learn.microsoft.com/en-us/troubleshoot/windows-server/support-tools/support-tools-task-manager)

## Run

Open the portable `Pulse.exe` on Windows 11 x64. It bundles .NET and the Windows App SDK and extracts its runtime files on first launch. Pulse follows the Windows app theme and accent color. The **ⓘ** button beside the refresh interval opens **About Pulse**, with the version and developer credit.

## Using the monitor

- Press **Ctrl+F** to search by process name or PID. Type `pid:` and a number to match one process exactly.
- Every table tab supports column sorting. Click a heading or focus it with **Tab** and press **Enter** or **Space**. The arrow shows the direction. Activate the heading again to reverse it. Drag a heading to reorder it or its right divider to resize it. Right-click a heading or use the Menu key for visibility, pinning and keyboard layout commands. Pulse restores each tab’s layout and sort order across restarts.
- The process list opens as a tree of parent and child processes. Collapse a branch to see the number of processes in it and their combined CPU, memory, I/O and threads. Toggle **Tree** for a flat list. Select a row to inspect its executable path, command line, user, priority, start time, counters, busiest threads and its GPU engine. The inspector has separate pages for threads, hosted services and connections.
- Switch to **Details** for a flat list with every counter in its own column: parent PID, session, working set, private bytes, read and write rates, handles, start time, priority, user and command line. Pulse loads the last three in the background. The displayed priority class updates when a process's base priority changes. Details follows the **Processes** sort order until you choose a heading in Details. Scroll horizontally to reach the command line in a narrow window. Hover over a truncated process name to read it in full. Double-click a row to show that process with its parents.
- Switch to **Users** for the processes, CPU, memory, I/O and threads of each session.
- Switch to **Connections** to list TCP connections, listeners and bound UDP ports with their owning processes. Filter by process, PID, address, port, protocol (such as `UDPv6`) or TCP state (such as `LISTENING`). Double-click a row to show its process. In the process inspector, **Connections** pairs the local and remote endpoints of established TCP connections. **Local ports** lists TCP listeners and UDP bindings. Each section shows up to six entries and counts the remainder. Scoped IPv6 addresses include the interface index, for example `[fe80::1%7]:443`. If Windows withholds a socket table, Pulse identifies it and keeps the available results.
- Switch to **Network** for every network adapter with its type, address, MAC address, status, link speed and current receive and send rates. Under each adapter are the devices Windows has recently exchanged packets with on that network, taken from the ARP and IPv6 neighbour tables, with the gateway first by default. Sorting keeps devices under their adapter. The **Activity** heading sorts adapters by combined receive and send rate. Device names come from reverse lookups that run while the tab is open. Hover over a row for all its addresses, the gateway and the DNS servers. Search by name, address or MAC.
- Switch to **Startup apps** for the entries in the Run and RunOnce registry keys and the Startup folders, with their command, source, whether Windows has them enabled and whether their program is running now. Double-click an entry, or press **Enter**, to edit its name, command and enabled state in Pulse. Registry Editor is not involved. Right-click an entry, or press the Menu key, to enable or disable it, edit it, delete it or show a Startup folder file in File Explorer. Enabling and disabling use the same switch as Task Manager and keep the entry. Renaming an entry keeps its enabled state.
- Switch to **Services** to list Windows services with their status and host process. Search by name, description or PID. Double-click a running service to show its process on the **Processes** tab. Right-click a service to start, stop or restart it or to change its startup type.
- Use the chevron on the CPU, disk or network card to open its device view, and again to close it. The buttons are reachable with **Tab**. Device history is collected while the view is closed, so the tiles open with the available history already drawn.
- Click a device tile, or press **Enter** or **Space** on it, to expand it into a larger graph with its maximum and time span labelled. Arrow keys move between tiles. Select the tile again to shrink it.
- Choose a history window of 1 or 15 minutes beside the refresh interval. Pulse always keeps the last 15 minutes, so switching does not lose samples.
- Click the CPU or memory graph to list the five processes with the highest CPU use and the five largest working sets at that sample. Processes that have since exited are marked.
- In **Disk space**, select a drive and click **Scan**, or use **Choose a folder to scan** to start from a directory. The treemap groups smaller items so that tile areas reflect their share of the whole folder. Sort the list by name, size, allocated bytes or share without moving the treemap tiles. Use the context menu on an individual file or folder to select it in File Explorer. The list includes every subfolder, including empty and unreadable ones. It shows the 20 largest files individually and groups the rest. Click a folder to drill down and use **Up one level** to go back. Cancelling preserves the partial result. Size is the logical file length. **On disk** shows allocated bytes reported by the file system. An incomplete allocation total appears as a dash. The scan skips reparse points and offline files.
- Changing a service or a machine-wide startup entry needs administrator rights. The menus offer **Restart Pulse as administrator** when Pulse runs without them. Entries of the current user can be changed without elevation.
- Open **Diagnostics** beside About, or drag across a main graph to inspect an interval. With a graph focused, arrow keys select a sample, Shift+arrow extends the interval and Enter opens the selection. Use **Save interval** to write a recording. **Open recording** loads it offline. **Compare recording** adds a reference with its own start and end controls. [Recording format](docs/CAPTURES.md)
- Choose **Watch process** in the process inspector to keep CPU, private memory, threads and handles on the same timeline while inspecting other rows. Diagnostics also shows available GPU adapters with their dedicated and shared memory and engine histories. The GPU engine column identifies a process's busiest engine.
- Open **File and socket activity** in Diagnostics and start recording to see file paths, local and remote endpoints, and measured byte rates. While the view is recording, **Connections** and the selected process inspector show matching endpoint rates. Pulse releases its ETW session when the activity view closes. Windows may require elevation.
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

From the project directory, run `powershell -File .\tests\Run.ps1`. The test build opens its own WinUI window for each shipped language. It checks live sampling, device removal and recovery, short-window layout, gaps in graphs, pause and the 15-minute history boundary. The disk space checks create a temporary folder tree and verify byte totals, small-file grouping, treemap proportions, navigation, keyboard focus, cancellation and rescanning. The checks activate all main table headings in both directions. They measure translated headings and the Details name column in a narrow window and verify numeric ordering, adapter grouping and selection after sorting. Services and Connections report when Windows restricts access to their data. Device changes and missing time intervals are simulated in the test process. Build output and reports go under `obj/window-checks`.

For the longer live checks, run `powershell -File .\tests\Run.ps1 -Extended`. Allow about 24 minutes for a full one-second history, two controlled workloads, a delayed offline comparison and a volume scan cancellation check.

## Development roadmap

The roadmap tracks device measurements, diagnostic history, capture comparison and the Windows acceptance checks. [Roadmap and acceptance criteria](ROADMAP.md)

