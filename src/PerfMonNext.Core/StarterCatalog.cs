namespace PerfMonNext.Core;

/// <summary>
/// A curated subset so a workspace can be built before a remote catalog answers.
/// Names match Windows counter objects. Help text is a short original summary, not the PDH explain text.
/// </summary>
public static class StarterCatalog
{
    public static CounterCatalog Create() => new()
    {
        SourceHost = "(starter)",
        IsStarter = true,
        Warning = "Showing a built-in subset. Reload a machine to list every counter set installed there.",
        Sets = Build()
    };

    private static IReadOnlyList<CounterSetInfo> Build() =>
    [
        Set("Processor", "Processor time for each CPU, or _Total for the whole box.", false, ["_Total"],
            ("% Processor Time", "Share of elapsed time the processor spent doing work."),
            ("% Privileged Time", "Share of time spent in kernel mode."),
            ("% User Time", "Share of time spent in user mode."),
            ("Interrupts/sec", "Hardware interrupts received per second.")),
        Set("Processor Information", "Same family as Processor, including utility counters on newer Windows.", false, ["_Total"],
            ("% Processor Time", "Share of elapsed time the logical processor spent doing work."),
            ("% Processor Utility", "Processor utility, which can exceed 100 when the CPU boosts."),
            ("% Privileged Utility", "Kernel-mode processor utility.")),
        Set("Memory", "System-wide memory pressure.", false, [],
            ("Available MBytes", "Memory that can be given to a process without freeing other working sets."),
            ("Pages/sec", "Pages read from or written to disk to resolve faults."),
            ("Committed Bytes", "Memory committed by the system."),
            ("Pool Nonpaged Bytes", "Nonpaged pool in use.")),
        Set("System", "Scheduler and system-wide totals.", false, [],
            ("Processor Queue Length", "Threads waiting for a processor. Sustained values above the CPU count mean the box is compute bound."),
            ("Context Switches/sec", "Switches from one thread to another."),
            ("System Calls/sec", "Calls into the kernel.")),
        Set("Network Interface", "Bytes and packets per adapter. Use * when adapters differ between nodes.", true, [],
            ("Bytes Total/sec", "Bytes sent and received on the interface."),
            ("Bytes Received/sec", "Bytes received."),
            ("Bytes Sent/sec", "Bytes sent."),
            ("Current Bandwidth", "Nominal link speed in bits per second.")),
        Set("Network Adapter", "Adapter throughput. Prefer this over Network Interface when both exist.", true, [],
            ("Bytes Total/sec", "Bytes sent and received."),
            ("Packets Received/sec", "Packets received.")),
        Set("PhysicalDisk", "Latency and throughput for physical disks. _Total sums every disk.", true, ["_Total"],
            ("Avg. Disk sec/Read", "Average seconds per read. Above a few milliseconds on flash is worth a look."),
            ("Avg. Disk sec/Write", "Average seconds per write."),
            ("Disk Transfers/sec", "Read and write operations per second."),
            ("Current Disk Queue Length", "Requests waiting or in service.")),
        Set("LogicalDisk", "Per-volume disk counters.", true, ["_Total"],
            ("% Free Space", "Percent of the volume that is free."),
            ("Free Megabytes", "Free space in megabytes."),
            ("Avg. Disk sec/Read", "Average seconds per read on the volume.")),
        Set("Process", "Per-process CPU and memory. * expands to every process at sample time.", true, [],
            ("% Processor Time", "Processor time charged to the process."),
            ("Working Set", "Bytes currently in the process working set."),
            ("Private Bytes", "Bytes allocated privately to the process."),
            ("Thread Count", "Threads in the process.")),
        Set("TCPv4", "IPv4 TCP activity for the whole computer.", false, [],
            ("Segments Sent/sec", "TCP segments sent."),
            ("Segments Received/sec", "TCP segments received."),
            ("Connections Established", "TCP connections in the established state.")),
        Set("Hyper-V Hypervisor Logical Processor", "Physical logical-processor time as seen by the hypervisor. Present on Hyper-V hosts.", true, ["_Total"],
            ("% Total Run Time", "Guest plus hypervisor run time. Sustained values over 90 mean the host is out of CPU."),
            ("% Guest Run Time", "Time guests spent running on this logical processor."),
            ("% Hypervisor Run Time", "Time the hypervisor itself spent running.")),
        Set("Hyper-V Hypervisor Virtual Processor", "Virtual processors of running virtual machines.", true, ["_Total"],
            ("% Total Run Time", "Time this virtual processor was running.")),
        Set("Hyper-V Hypervisor Root Virtual Processor", "Virtual processors of the root partition.", true, ["_Total"],
            ("% Total Run Time", "Time the root virtual processor was running.")),
        Set("Cluster CSV File System", "Cluster Shared Volume I/O, including redirected I/O. Present on clustered CSV nodes.", true, [],
            ("IO Reads/sec", "Direct or block-redirected reads completed per second."),
            ("IO Writes/sec", "Direct or block-redirected writes completed per second."),
            ("IO Read Latency", "Average seconds CSV spent on a read, including the underlying disk or redirect."),
            ("IO Write Latency", "Average seconds CSV spent on a write."),
            ("Redirected Reads/sec", "Reads sent to the coordinating node over SMB."),
            ("Redirected Writes/sec", "Writes sent to the coordinating node over SMB.")),
        Set("Cluster CSV Volume Cache", "CSV in-memory cache on the coordinating node.", true, [],
            ("Cache IO Read - Bytes/sec", "Bytes served from the CSV cache."),
            ("Disk IO Read - Bytes/Sec", "Bytes read from disk into the CSV path."),
            ("Cache Size - Current", "Current cache size.")),
        Set("SMB Client Shares", "SMB client traffic per share. Useful on non-coordinating CSV nodes.", true, [],
            ("Data Bytes/sec", "Application bytes read and written through the share."),
            ("Avg. sec/Read", "Average seconds per read as seen by the client."),
            ("Avg. sec/Write", "Average seconds per write as seen by the client.")),
        Set("SMB Server Shares", "SMB server traffic per share. Useful on the coordinating node and file servers.", true, [],
            ("Data Bytes/sec", "Application bytes read and written on the share."),
            ("Avg. sec/Read", "Average seconds per read on the server."),
            ("Avg. sec/Write", "Average seconds per write on the server.")),
        Set("SMB Server Sessions", "Per-session SMB server activity. Can be expensive on a busy file server.", true, [],
            ("Data Bytes/sec", "Bytes transferred for the session."))
    ];

    private static CounterSetInfo Set(string name, string help, bool multi, string[] instances, params (string Name, string Help)[] counters) =>
        new()
        {
            Name = name,
            Help = help,
            MultiInstance = multi || instances.Length > 0,
            Instances = instances,
            Counters = counters.Select(counter => new CounterInfo { Name = counter.Name, Help = counter.Help }).ToList()
        };
}
