using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace SelfClaw.Desktop.Services.Terminal;

public sealed class ConPtyTerminalSession : IDisposable
{
    private const int ProcThreadAttributePseudoConsole = 0x00020016;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const int StartfUseStdHandles = 0x00000100;

    private readonly object _gate = new();
    private readonly StreamWriter _inputWriter;
    private readonly FileStream _outputReader;
    private readonly SafeFileHandle _pseudoConsoleInputReadSide;
    private readonly SafeFileHandle _pseudoConsoleOutputWriteSide;
    private readonly CancellationTokenSource _outputCancellation = new();
    private readonly ProcessInformation _processInformation;
    private readonly IntPtr _pseudoConsole;
    private readonly IntPtr _attributeList;
    private bool _disposed;
    private bool _started;
    private int _exitPublished;

    public ConPtyTerminalSession(
        string shellPath,
        string workingDirectory,
        int columns,
        int rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shellPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        Columns = Math.Max(1, columns);
        Rows = Math.Max(1, rows);
        WorkingDirectory = workingDirectory;

        CreatePipe(out var inputReadSide, out var inputWriteSide);
        CreatePipe(out var outputReadSide, out var outputWriteSide);

        try
        {
            _pseudoConsole = CreatePseudoConsole(inputReadSide, outputWriteSide, Columns, Rows);
            _attributeList = InitializePseudoConsoleAttributeList(_pseudoConsole);
            _processInformation = CreateShellProcess(shellPath, workingDirectory, _attributeList);

            _pseudoConsoleInputReadSide = inputReadSide;
            _pseudoConsoleOutputWriteSide = outputWriteSide;
            inputReadSide = null!;
            outputWriteSide = null!;
            _inputWriter = new StreamWriter(new FileStream(inputWriteSide, FileAccess.Write, bufferSize: 4096, isAsync: false), new UTF8Encoding(false))
            {
                AutoFlush = true
            };
            _outputReader = new FileStream(outputReadSide, FileAccess.Read, bufferSize: 8192, isAsync: false);
        }
        catch
        {
            inputWriteSide.Dispose();
            outputReadSide.Dispose();
            inputReadSide?.Dispose();
            outputWriteSide?.Dispose();
            if (_attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(_attributeList);
                Marshal.FreeHGlobal(_attributeList);
            }

            if (_pseudoConsole != IntPtr.Zero)
            {
                ClosePseudoConsole(_pseudoConsole);
            }

            throw;
        }
        finally
        {
            inputReadSide?.Dispose();
            outputWriteSide?.Dispose();
        }

    }

    public event EventHandler<string>? OutputReceived;

    public event EventHandler<int?>? Exited;

    public int Columns { get; private set; }

    public int Rows { get; private set; }

    public string WorkingDirectory { get; }

    public void Start()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_started)
            {
                return;
            }

            _started = true;
        }

        _ = Task.Run(ReadOutputLoop);
    }

    public void WriteInput(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return;
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            _inputWriter.Write(input);
        }
    }

    public void Resize(int columns, int rows)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);

        lock (_gate)
        {
            ThrowIfDisposed();
            if (Columns == columns && Rows == rows)
            {
                return;
            }

            ResizePseudoConsole(_pseudoConsole, columns, rows);
            Columns = columns;
            Rows = rows;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _outputCancellation.Cancel();
        TryTerminateProcess(_processInformation.Process);
        _inputWriter.Dispose();
        _outputReader.Dispose();
        CloseHandleIfNeeded(_processInformation.Thread);
        CloseHandleIfNeeded(_processInformation.Process);
        DeleteProcThreadAttributeList(_attributeList);
        Marshal.FreeHGlobal(_attributeList);
        ClosePseudoConsole(_pseudoConsole);
        _pseudoConsoleOutputWriteSide.Dispose();
        _pseudoConsoleInputReadSide.Dispose();
        _outputCancellation.Dispose();
    }

    private void ReadOutputLoop()
    {
        var buffer = new byte[8192];
        try
        {
            while (!_outputCancellation.IsCancellationRequested)
            {
                var read = _outputReader.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                OutputReceived?.Invoke(this, Encoding.UTF8.GetString(buffer, 0, read));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            PublishExited();
        }
    }

    private void PublishExited()
    {
        if (Interlocked.Exchange(ref _exitPublished, 1) != 0)
        {
            return;
        }

        Exited?.Invoke(this, TryGetExitCode(_processInformation.Process));
    }

    private static IntPtr CreatePseudoConsole(
        SafeFileHandle inputReadSide,
        SafeFileHandle outputWriteSide,
        int columns,
        int rows)
    {
        var size = new Coord
        {
            X = (short)columns,
            Y = (short)rows
        };

        var result = CreatePseudoConsole(size, inputReadSide, outputWriteSide, 0, out var pseudoConsole);
        if (result != 0)
        {
            throw new Win32Exception(result, "Failed to create a Windows pseudoconsole.");
        }

        return pseudoConsole;
    }

    private static IntPtr InitializePseudoConsoleAttributeList(IntPtr pseudoConsole)
    {
        var attributeListSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);

        var attributeList = Marshal.AllocHGlobal(attributeListSize);
        try
        {
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to initialize process attribute list.");
            }

            if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    (IntPtr)ProcThreadAttributePseudoConsole,
                    pseudoConsole,
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to attach pseudoconsole attribute.");
            }

            return attributeList;
        }
        catch
        {
            DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(attributeList);
            throw;
        }
    }

    private static ProcessInformation CreateShellProcess(
        string shellPath,
        string workingDirectory,
        IntPtr attributeList)
    {
        var startupInfoEx = new StartupInfoEx
        {
            StartupInfo = new StartupInfo
            {
                Cb = Marshal.SizeOf<StartupInfoEx>(),
                Flags = StartfUseStdHandles
            },
            LpAttributeList = attributeList
        };

        var commandLine = new StringBuilder($"\"{shellPath}\" -NoLogo");
        var environment = IntPtr.Zero;
        var securityAttributeSize = Marshal.SizeOf<SecurityAttributes>();
        var processSecurityAttributes = new SecurityAttributes
        {
            Length = securityAttributeSize
        };
        var threadSecurityAttributes = new SecurityAttributes
        {
            Length = securityAttributeSize
        };

        if (!CreateProcess(
                null,
                commandLine,
                ref processSecurityAttributes,
                ref threadSecurityAttributes,
                false,
                ExtendedStartupInfoPresent,
                environment,
                workingDirectory,
                ref startupInfoEx,
                out var processInformation))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to start terminal shell process.");
        }

        return processInformation;
    }

    private static void CreatePipe(out SafeFileHandle readSide, out SafeFileHandle writeSide)
    {
        if (!CreatePipe(out readSide, out writeSide, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create terminal pipe.");
        }
    }

    private static void TryTerminateProcess(IntPtr processHandle)
    {
        if (processHandle == IntPtr.Zero)
        {
            return;
        }

        _ = TerminateProcess(processHandle, 0);
    }

    private static int? TryGetExitCode(IntPtr processHandle)
    {
        if (processHandle == IntPtr.Zero)
        {
            return null;
        }

        return GetExitCodeProcess(processHandle, out var exitCode)
            ? (int)exitCode
            : null;
    }

    private static void ResizePseudoConsole(IntPtr pseudoConsole, int columns, int rows)
    {
        var size = new Coord
        {
            X = (short)columns,
            Y = (short)rows
        };

        var result = ResizePseudoConsole(pseudoConsole, size);
        if (result != 0)
        {
            throw new Win32Exception(result, "Failed to resize terminal pseudoconsole.");
        }
    }

    private static void CloseHandleIfNeeded(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            CloseHandle(handle);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(
        out SafeFileHandle hReadPipe,
        out SafeFileHandle hWritePipe,
        IntPtr lpPipeAttributes,
        uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr attribute,
        IntPtr lpValue,
        IntPtr cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        ref SecurityAttributes lpProcessAttributes,
        ref SecurityAttributes lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        [In] ref StartupInfoEx lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(
        Coord size,
        SafeFileHandle hInput,
        SafeFileHandle hOutput,
        uint dwFlags,
        out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(
        IntPtr hPC,
        Coord size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Cb;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr LpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }
}
