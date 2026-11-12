# PerfMon Next

A Windows desktop replacement for Performance Monitor, aimed at watching the same counters across several cluster nodes at once.

The window is dark. The left side is the machine list and a searchable counter picker. The right side is live ScottPlot charts (combined, one chart per counter, or one chart per machine) and a table of last / min / max / average. Sampling, workspaces, CSV, and `.blg` import are described below.

## Downloads

Signed Windows builds are published in the public **[PerfMon Next releases](https://github.com/EquiByteCZ/perfmon-next-releases/releases)** repository (this source repository stays private). Download `PerfMonNext-win-x64.zip`, unzip, and run `PerfMonNext.exe`.

The executable is signed by **EquiByte s.r.o.** with Azure Artifact Signing (public trust). Windows SmartScreen may still warn on the first run until the publisher builds reputation; the dialog should show EquiByte s.r.o. as the signer.

A user guide and screenshots live in the [public documentation](https://github.com/EquiByteCZ/perfmon-next-releases).


## Why this stack

.NET 8 and WPF. WinUI 3 pulls in the Windows App SDK, which does not cross-compile cleanly, and WPF's virtualized lists and data grid fit a dense operator tool. Charts use ScottPlot 5 (Skia) because it stays responsive with long histories and many series. Counters use `System.Diagnostics.PerformanceCounter`, the supported managed wrapper over PDH, including remote machines. Alternate credentials open `\\host\IPC$` with `WNetAddConnection2`, because remote PDH uses that session. Binary `.blg` logs are converted with `relog.exe` (the supported tool) and then imported as PDH-CSV.

## Prerequisites

Development:

- .NET 8 SDK
- On Windows, the .NET 8 **desktop** runtime to run the app
- This repo cross-compiles the Windows project on Linux (`EnableWindowsTargeting`). The UI still has to be run on Windows.

Remote counters, on each target machine:

1. Start the Remote Registry service and set it to automatic.
   ```powershell
   Set-Service RemoteRegistry -StartupType Automatic
   Start-Service RemoteRegistry
   ```
2. Allow the firewall groups Performance Logs and Alerts, File and Printer Sharing, and Remote Service Management.
   ```powershell
   Enable-NetFirewallRule -DisplayGroup "Performance Logs and Alerts"
   Enable-NetFirewallRule -DisplayGroup "File and Printer Sharing"
   Enable-NetFirewallRule -DisplayGroup "Remote Service Management"
   ```
   That covers RPC (TCP 135 plus dynamic ports) and SMB (TCP 445), which PDH needs.
3. Put the account in **Performance Monitor Users** (read counters) or Administrators. **Performance Log Users** is the group used by Data Collector Sets (`logman`), not by a live read.
4. The account also needs "Access this computer from the network". A few counters still fail unless the account can read `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Perflib`.
5. The first sample of a rate counter is discarded. Values show up on the second interval.

The app reads as the signed-in user unless a machine is set to different credentials. The password is kept in memory. "Remember on this PC" stores it with DPAPI under `%AppData%\PerfMonNext\credentials.json`. Workspace JSON never contains a password; if one is present it is ignored.

If Windows already has a session to that host as someone else, the connect fails with a message that includes `net use \\host /delete`.

## Build and run

Linux or macOS (compile and unit tests only):

```bash
./build.sh
```

Windows:

```powershell
.\build.ps1
```

That publishes `artifacts\win-x64\PerfMonNext.exe` (framework-dependent; the machine needs the .NET 8 desktop runtime).

Run from source on Windows:

```powershell
dotnet run --project src\PerfMonNext.App -c Release
```

GitHub Actions (`.github/workflows/build.yml`) runs the tests, cross-compiles on Ubuntu, publishes a self-contained win-x64 build from the Windows runner, and on version tags signs `PerfMonNext.exe` and publishes the ZIP to the public releases repository.

## Using it

- **Add** a machine. `.` is this computer. **Test** reads `Memory\Available MBytes` and shows online, access denied, or offline on that row.
- **Add to all machines** means one tick creates the counter for every machine. Clear it to target only the selected machine.
- Multi-instance counters accept `*` (every instance, expanded while sampling) or a prefix such as `chrome*`. `_Total` is the default when that instance exists.
- Search matches counter set, counter, instance, and help text. Explain text is loaded from PDH when you select a counter. The **Starter** list is a built-in subset (processor, memory, disk, Hyper-V, CSV, SMB) for when a remote catalog is slow or down.
- Paste a path such as `\Processor(_Total)\% Processor Time` or `\\node02\Memory\Available MBytes` and press **Add path**. A path that names a computer adds that computer.
- **Follow** keeps the time axis on the latest samples. Turn it off, then drag to pan and use the wheel to zoom. **Fit scales** picks a power-of-ten factor so each series lands near 10–100. The scale is applied for display; CSV export stores raw values. The sample workspace scales CSV read latency by 1000 so the chart shows milliseconds.
- **F5** starts sampling. Ctrl+O / Ctrl+S open and save a workspace.

`samples/azure-local-cluster.workspace.json` is a three-node workspace (CPU, memory, queue, Hyper-V logical processor, CSV read latency). Change the host names before you start it. Hyper-V and CSV counters exist only when those roles are installed; a missing counter is reported on that machine and the others continue.

## Files

| File | Contents |
| --- | --- |
| Workspace JSON | Machines, counter selections, interval, history length, chart grouping, colors, scales, visibility. No passwords. |
| CSV | `timestamp,machine,category,counter,instance,value`. Gaps are empty cells. |
| PDH-CSV | The header `relog -f CSV` writes. Import detects it. |
| `.blg` | On Windows, PerfMon Next runs `relog.exe file.blg -f CSV -o file.csv -y` and imports that CSV. |

## What was verified here

On this Linux machine:

- `dotnet test -c Release` — 36 tests covering path parsing, workspace JSON (including a stripped password and the sample cluster file), ring-buffer stats, multi-machine sampling when one host fails, the single-flight poller, catalog search, chart grouping and gap encoding, CSV and PDH-CSV import, `.blg` command planning, and the credential file format.
- `dotnet build src/PerfMonNext.App -c Release` — the WPF app and the PDH project compile with `EnableWindowsTargeting`.

Not run here, because there is no Windows desktop session:

- The window itself (layout, dark theme, ScottPlot pan/zoom, live refresh).
- A real local or remote PDH read, Remote Registry, the IPC$ credential connect, DPAPI, and `relog.exe`.

## Suggested next steps

- Open the app on a Windows Server or Azure Local node, load the real catalog, and sample two or three nodes at 1 second.
- Confirm an alternate-credential connect against a node that your current account cannot read, and the `net use \\host /delete` recovery when a session is already open.
- If a combined chart is still hard to read after Fit scales, add a second Y axis per unit.
- A "clear connection" button next to a machine would save the manual `net use` step.
