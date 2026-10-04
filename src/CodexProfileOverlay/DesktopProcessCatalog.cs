using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CodexProfileOverlay.Core.Services;
using Microsoft.Win32.SafeHandles;

namespace CodexProfileOverlay;

internal sealed record CapturedDesktopProcess(DesktopProcessSnapshot Identity, string ExecutableName);

internal static class DesktopProcessCatalog
{
    public static IReadOnlyList<CapturedDesktopProcess> Capture(int sessionId)
    {
        using SafeFileHandle snapshot = ProcessNative.CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        var result = new List<CapturedDesktopProcess>();
        bool available = ProcessNative.Process32First(snapshot, ref entry);
        while (available)
        {
            if (ProcessNative.ProcessIdToSessionId(entry.ProcessId, out uint processSession) && processSession == sessionId)
            {
                DateTime? started = null;
                using SafeProcessHandle process = ProcessNative.OpenProcess(0x1000, false, entry.ProcessId);
                if (!process.IsInvalid && ProcessNative.GetProcessTimes(process, out ProcessFileTime creation, out _, out _, out _))
                {
                    started = creation.ToDateTimeUtc();
                }
                result.Add(new CapturedDesktopProcess(new DesktopProcessSnapshot(
                    checked((int)entry.ProcessId), checked((int)entry.ParentProcessId), sessionId, started), entry.ExecutableName));
            }
            available = ProcessNative.Process32Next(snapshot, ref entry);
        }
        if (Marshal.GetLastWin32Error() != 18) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        return result;
    }

    public static string? ExecutablePath(DesktopProcessSnapshot identity)
    {
        using SafeProcessHandle process = ProcessNative.OpenProcess(0x1000, false, (uint)identity.ProcessId);
        if (process.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 87) { return null; }
            throw new Win32Exception(error);
        }
        if (!ProcessNative.GetProcessTimes(process, out ProcessFileTime creation, out _, out _, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        if (identity.StartTimeUtc is null) { throw new InvalidOperationException($"Cannot verify desktop candidate PID {identity.ProcessId}."); }
        if (identity.StartTimeUtc != creation.ToDateTimeUtc()) { throw new InvalidOperationException("Desktop candidate identity changed; retry switching."); }
        var path = new StringBuilder(32768);
        int size = path.Capacity;
        if (!ProcessNative.QueryFullProcessImageName(process, 0, path, ref size))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return path.ToString();
    }

    // This fallback recognizes a background Store desktop app without relying on its process name.
    public static bool IsInstalledDesktopPath(string path)
    {
        string? appDirectory = Path.GetDirectoryName(path);
        string? packageDirectory = appDirectory is null ? null : Path.GetDirectoryName(appDirectory);
        string? packagesDirectory = packageDirectory is null ? null : Path.GetDirectoryName(packageDirectory);
        string file = Path.GetFileName(path);
        string packageName = Path.GetFileName(packageDirectory) ?? string.Empty;
        string expectedPackagesDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        return (file.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase) || file.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase))
            && string.Equals(Path.GetFileName(appDirectory), "app", StringComparison.OrdinalIgnoreCase)
            && packageName.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)
            && packageName.EndsWith("_2p2nqsd0c76g0", StringComparison.OrdinalIgnoreCase)
            && string.Equals(packagesDirectory, expectedPackagesDirectory, StringComparison.OrdinalIgnoreCase);
    }

    public static TrackedDesktopProcess? Open(DesktopProcessSnapshot identity)
    {
        SafeProcessHandle handle = ProcessNative.OpenProcess(0x1000 | 0x100000, false, (uint)identity.ProcessId);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error == 87) { return null; } // The process disappeared after the snapshot.
            throw new Win32Exception(error, $"Cannot inspect selected desktop process {identity.ProcessId}.");
        }
        try
        {
            if (!ProcessNative.GetProcessTimes(handle, out ProcessFileTime creation, out _, out _, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            if (identity.StartTimeUtc is null)
            {
                throw new InvalidOperationException($"Cannot verify creation time of desktop process {identity.ProcessId}; authorization has not been changed.");
            }
            if (creation.ToDateTimeUtc() != identity.StartTimeUtc) { handle.Dispose(); return null; }
            // Keep the handle open throughout shutdown so the original process identity remains pinned.
            return new TrackedDesktopProcess(identity, handle);
        }
        catch { handle.Dispose(); throw; }
    }
}

internal sealed class TrackedDesktopProcess(DesktopProcessSnapshot identity, SafeProcessHandle handle) : IDisposable
{
    public DesktopProcessSnapshot Identity { get; } = identity;

    public DateTime? ExitTimeUtc
    {
        get
        {
            if (!HasExited) { return null; }
            if (!ProcessNative.GetProcessTimes(handle, out _, out ProcessFileTime exit, out _, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            return exit.ToDateTimeUtc();
        }
    }

    public bool HasExited
    {
        get
        {
            uint result = ProcessNative.WaitForSingleObject(handle, 0);
            return result switch
            {
                0 => true,
                258 => false,
                _ => throw new Win32Exception(Marshal.GetLastWin32Error()),
            };
        }
    }

    public void RequestClose(IntPtr window)
    {
        NativeMethods.GetWindowThreadProcessId(window, out uint windowProcessId);
        if (windowProcessId != Identity.ProcessId || HasExited) { return; }
        if (!ProcessNative.PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Terminate()
    {
        if (HasExited) { return; }
        using SafeProcessHandle terminationHandle = ProcessNative.OpenProcess(0x1000 | 0x100000 | 1, false, (uint)Identity.ProcessId);
        if (terminationHandle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (HasExited) { return; }
            throw new Win32Exception(error, $"Cannot terminate selected desktop process {Identity.ProcessId}.");
        }
        if (!ProcessNative.GetProcessTimes(terminationHandle, out ProcessFileTime creation, out _, out _, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        if (creation.ToDateTimeUtc() != Identity.StartTimeUtc) { throw new InvalidOperationException("Desktop process identity changed; refusing termination."); }
        if (!ProcessNative.TerminateProcess(terminationHandle, 1))
        {
            int error = Marshal.GetLastWin32Error();
            if (!HasExited) { throw new Win32Exception(error); }
        }
    }

    public void Dispose() => handle.Dispose();
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct ProcessEntry
{
    public uint Size;
    public uint Usage;
    public uint ProcessId;
    public UIntPtr DefaultHeap;
    public uint ModuleId;
    public uint Threads;
    public uint ParentProcessId;
    public int BasePriority;
    public uint Flags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string ExecutableName;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessFileTime
{
    public uint Low;
    public uint High;
    public readonly DateTime ToDateTimeUtc() => DateTime.FromFileTimeUtc(((long)High << 32) | Low);
}

internal static class ProcessNative
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessTimes(SafeProcessHandle process, out ProcessFileTime creation, out ProcessFileTime exit, out ProcessFileTime kernel, out ProcessFileTime user);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
