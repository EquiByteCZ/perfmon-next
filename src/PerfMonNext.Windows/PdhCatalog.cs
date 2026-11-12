using System.Runtime.InteropServices;
using PerfMonNext.Core;

namespace PerfMonNext.Windows;

internal static class PdhCatalog
{
    public const uint PerfDetailWizard = 400;
    public const uint PdhMoreData = 0x800007D2;

    public static void RefreshObjects(string pcMachine)
    {
        uint size = 0;
        _ = PdhEnumObjects(null, Machine(pcMachine), null, ref size, PerfDetailWizard, true);
    }

    public static bool TryEnumItems(string pcMachine, string category, out string[] counters, out string[] instances, out string? error)
    {
        counters = [];
        instances = [];
        error = null;
        try
        {
            uint counterSize = 0;
            uint instanceSize = 0;
            var status = PdhEnumObjectItems(
                null,
                Machine(pcMachine),
                category,
                null,
                ref counterSize,
                null,
                ref instanceSize,
                PerfDetailWizard,
                0);
            if (status != 0 && status != PdhMoreData)
            {
                error = "PDH could not list " + category + " (0x" + status.ToString("X") + ").";
                return false;
            }

            var counterBuffer = counterSize == 0 ? null : new char[counterSize];
            var instanceBuffer = instanceSize == 0 ? null : new char[instanceSize];
            status = PdhEnumObjectItems(
                null,
                Machine(pcMachine),
                category,
                counterBuffer,
                ref counterSize,
                instanceBuffer,
                ref instanceSize,
                PerfDetailWizard,
                0);
            if (status != 0)
            {
                error = "PDH could not list " + category + " (0x" + status.ToString("X") + ").";
                return false;
            }

            counters = ParseMultiSz(counterBuffer, counterSize);
            instances = ParseMultiSz(instanceBuffer, instanceSize);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static string? Machine(string pcMachine) =>
        MachineHost.IsLocal(pcMachine) ? null : @"\\" + pcMachine.Trim().TrimStart('\\');

    private static string[] ParseMultiSz(char[]? buffer, uint length)
    {
        if (buffer is null || length == 0)
            return [];
        var text = new string(buffer, 0, (int)Math.Min(buffer.Length, length));
        return text.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhEnumObjects(
        string? szDataSource,
        string? szMachineName,
        char[]? mszObjectList,
        ref uint pcchBufferSize,
        uint dwDetailLevel,
        bool bRefresh);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhEnumObjectItems(
        string? szDataSource,
        string? szMachineName,
        string szObjectName,
        char[]? mszCounterList,
        ref uint pcchCounterListLength,
        char[]? mszInstanceList,
        ref uint pcchInstanceListLength,
        uint dwDetailLevel,
        uint dwFlags);
}
