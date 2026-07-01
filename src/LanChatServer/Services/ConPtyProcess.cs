using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LanChatServer.Services;

/// <summary>
/// Windows ConPTY 経由でプロセスを起動する。
/// prompt_toolkit など「本物のコンソール」を必要とする TUI アプリを
/// stdin/stdout リダイレクトで動かすために仮想コンソールを提供する。
/// </summary>
public sealed class ConPtyProcess : IAsyncDisposable
{
    private IntPtr _hPC = IntPtr.Zero;
    private IntPtr _hProcess = IntPtr.Zero;
    private Stream? _input;
    private Stream? _output;

    /// <summary>仮想コンソールへの書き込みストリーム (= プロセスの stdin)。</summary>
    public Stream Input => _input!;

    /// <summary>仮想コンソールからの読み取りストリーム (= プロセスの stdout+stderr)。</summary>
    public Stream Output => _output!;

    public bool HasExited => _hProcess == IntPtr.Zero ||
        NativeMethods.WaitForSingleObject(_hProcess, 0) == 0;

    private ConPtyProcess() { }

    /// <summary>ConPTY 付きでプロセスを起動する。</summary>
    public static ConPtyProcess Start(string command, short cols = 220, short rows = 40)
    {
        // パイプ作成
        // inRead → ConPTY(stdin として渡す) / inWrite → 親が書き込む
        if (!NativeMethods.CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0))
            throw new Exception($"CreatePipe(in) failed: {Marshal.GetLastWin32Error()}");

        // outRead → 親が読み取る / outWrite → ConPTY(stdout として渡す)
        if (!NativeMethods.CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0))
            throw new Exception($"CreatePipe(out) failed: {Marshal.GetLastWin32Error()}");

        // ConPTY 作成 (size: ターミナルの列数×行数)
        var size = new NativeMethods.COORD { X = cols, Y = rows };
        int hr = NativeMethods.CreatePseudoConsole(size, inRead, outWrite, 0, out var hPC);
        if (hr != 0)
            throw new Exception($"CreatePseudoConsole failed: hr=0x{hr:X8}");

        // ConPTY がパイプを複製したので元のハンドルは閉じる
        NativeMethods.CloseHandle(inRead);
        NativeMethods.CloseHandle(outWrite);

        // プロセス属性リスト (ConPTY を渡す)
        nint attrListSize = 0;
        NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrListSize);
        var attrList = Marshal.AllocHGlobal(attrListSize);
        try
        {
            if (!NativeMethods.InitializeProcThreadAttributeList(attrList, 1, 0, ref attrListSize))
                throw new Exception($"InitializeProcThreadAttributeList failed: {Marshal.GetLastWin32Error()}");

            IntPtr hPCValue = hPC;
            if (!NativeMethods.UpdateProcThreadAttribute(attrList, 0,
                (IntPtr)NativeMethods.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                ref hPCValue, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Exception($"UpdateProcThreadAttribute failed: {Marshal.GetLastWin32Error()}");

            var si = new NativeMethods.STARTUPINFOEX
            {
                StartupInfo = new NativeMethods.STARTUPINFO
                {
                    cb = Marshal.SizeOf<NativeMethods.STARTUPINFOEX>(),
                },
                lpAttributeList = attrList,
            };

            if (!NativeMethods.CreateProcess(null, command,
                IntPtr.Zero, IntPtr.Zero, false,
                NativeMethods.EXTENDED_STARTUPINFO_PRESENT,
                IntPtr.Zero, null, ref si, out var pi))
                throw new Exception($"CreateProcess failed: {Marshal.GetLastWin32Error()}");

            NativeMethods.CloseHandle(pi.hThread);

            return new ConPtyProcess
            {
                _hPC = hPC,
                _hProcess = pi.hProcess,
                _input = new FileStream(new SafeFileHandle(inWrite, ownsHandle: true), FileAccess.Write),
                _output = new FileStream(new SafeFileHandle(outRead, ownsHandle: true), FileAccess.Read),
            };
        }
        finally
        {
            NativeMethods.DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _input?.Dispose(); } catch { }
        try { _output?.Dispose(); } catch { }
        if (_hPC != IntPtr.Zero)
        {
            NativeMethods.ClosePseudoConsole(_hPC);
            _hPC = IntPtr.Zero;
        }
        if (_hProcess != IntPtr.Zero)
        {
            NativeMethods.TerminateProcess(_hProcess, 1);
            NativeMethods.CloseHandle(_hProcess);
            _hProcess = IntPtr.Zero;
        }
        await Task.CompletedTask;
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct COORD { public short X, Y; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct STARTUPINFO
        {
            public int cb;
            public string? lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize;
            public int dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        internal const int EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        internal const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CreatePipe(out IntPtr hRead, out IntPtr hWrite, IntPtr attr, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern int CreatePseudoConsole(COORD size, IntPtr hIn, IntPtr hOut, uint flags, out IntPtr hPC);

        [DllImport("kernel32.dll")]
        internal static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref nint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attr, ref IntPtr value, IntPtr size, IntPtr prev, IntPtr retSize);

        [DllImport("kernel32.dll")]
        internal static extern void DeleteProcThreadAttributeList(IntPtr list);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool CreateProcess(string? app, string cmd, IntPtr pAttr, IntPtr tAttr,
            bool inherit, int flags, IntPtr env, string? dir, ref STARTUPINFOEX si, out PROCESS_INFORMATION pi);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool TerminateProcess(IntPtr h, uint code);

        [DllImport("kernel32.dll")]
        internal static extern uint WaitForSingleObject(IntPtr h, uint ms);
    }
}
