using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VoiceAssistant.WebOverlay.HostAudio;

internal readonly record struct ProcessEntry(int ProcessId, int ParentProcessId, string ExeName);

/// <summary>Finds the root process of the new Microsoft Teams desktop app (ms-teams.exe) in this Windows session.</summary>
internal static class TeamsProcessLocator
{
    public const string TeamsExe = "ms-teams.exe";

    /// <summary>
    /// The parent-most ms-teams.exe whose own parent is not an ms-teams.exe started before it (a reused parent PID does
    /// not count). Only processes of the current session are considered, so another signed-in user's Teams is never
    /// captured. With several independent roots, the one with the largest process tree wins, then the oldest.
    /// </summary>
    public static int? FindRoot(IReadOnlyCollection<ProcessEntry> processes, Func<int, bool> inCurrentSession,
        Func<int, DateTime?> startTime)
    {
        var teams = new Dictionary<int, ProcessEntry>();
        foreach (var process in processes)
        {
            if (string.Equals(process.ExeName, TeamsExe, StringComparison.OrdinalIgnoreCase) && inCurrentSession(process.ProcessId))
                teams[process.ProcessId] = process;
        }
        if (teams.Count == 0) return null;
        var started = teams.Keys.ToDictionary(pid => pid, startTime);
        // A parent id only counts when that process existed first; Windows reuses ids of exited processes.
        bool IsChildOf(ProcessEntry child, int parent) =>
            child.ParentProcessId == parent && parent != child.ProcessId &&
            !(started.TryGetValue(parent, out var parentStart) && parentStart is { } p &&
              started.TryGetValue(child.ProcessId, out var childStart) && childStart is { } c && p > c);
        var roots = teams.Values.Where(process => !teams.ContainsKey(process.ParentProcessId) || !IsChildOf(process, process.ParentProcessId)).ToList();
        if (roots.Count == 0) roots = [.. teams.Values];
        if (roots.Count == 1) return roots[0].ProcessId;
        var children = processes.Where(p => IsChildOf(p, p.ParentProcessId)).ToLookup(p => p.ParentProcessId);
        int Descendants(int root)
        {
            var seen = new HashSet<int> { root };
            var pending = new Stack<int>([root]);
            while (pending.TryPop(out var pid))
                foreach (var child in children[pid])
                    if (seen.Add(child.ProcessId)) pending.Push(child.ProcessId);
            return seen.Count - 1;
        }
        return roots
            .OrderByDescending(root => Descendants(root.ProcessId))
            .ThenBy(root => started[root.ProcessId] ?? DateTime.MaxValue)
            .ThenBy(root => root.ProcessId)
            .First().ProcessId;
    }

    public static int? FindRoot() =>
        FindRoot(ProcessSnapshot.Take(), ProcessSnapshot.InCurrentSession, ProcessSnapshot.StartTime);
}

/// <summary>Process ids, parent ids and executable names from one Toolhelp snapshot; no process is opened.</summary>
internal static class ProcessSnapshot
{
    private const uint SnapProcess = 0x2;
    private static readonly uint CurrentSession = (uint)Process.GetCurrentProcess().SessionId;

    public static IReadOnlyList<ProcessEntry> Take()
    {
        using var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
        var processes = new List<ProcessEntry>();
        if (!Process32FirstW(snapshot, ref entry)) return processes;
        do processes.Add(new((int)entry.ProcessId, (int)entry.ParentProcessId, entry.ExeFile));
        while (Process32NextW(snapshot, ref entry));
        return processes;
    }

    public static bool InCurrentSession(int processId) =>
        ProcessIdToSessionId((uint)processId, out var session) && session == CurrentSession;

    public static DateTime? StartTime(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
}
