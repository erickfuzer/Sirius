using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace Sirius.Terminal;

// Um programa rodando dentro de um pseudoconsole do Windows (ConPTY).
// Os eventos chegam em threads de fundo; TerminalSession os leva para a interface.
public sealed class ConPtySession : IDisposable
{
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly Channel<byte[]> _writes = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly object _gate = new();
    private readonly IntPtr _process;
    private readonly int _processId;
    private readonly IntPtr _attributes;
    private IntPtr _console;
    private bool _exited;

    public event Action<string>? Output;
    public event Action<int>? Exited;

    private ConPtySession(FileStream input, FileStream output, IntPtr console, IntPtr process, int processId, IntPtr attributes)
    {
        _input = input;
        _output = output;
        _console = console;
        _process = process;
        _processId = processId;
        _attributes = attributes;
    }

    public static ConPtySession Start(string commandLine, string workingDirectory, int columns, int rows)
    {
        if (!CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0))
            throw new Win32Exception();
        if (!CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
            throw new Win32Exception();

        var result = CreatePseudoConsole(new Coord((short)columns, (short)rows), inputRead, outputWrite, 0, out var console);
        if (result != 0)
            throw new Win32Exception(result);

        // O console já tem as próprias cópias; manter estas impediria o fim da leitura.
        inputRead.Dispose();
        outputWrite.Dispose();

        var attributes = CreateAttributes(console);
        var startup = new StartupInfoEx
        {
            StartupInfo = new StartupInfo { cb = Marshal.SizeOf<StartupInfoEx>(), dwFlags = UseStdHandles },
            lpAttributeList = attributes,
        };

        var created = CreateProcessW(
            null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
            ExtendedStartupInfoPresent | CreateUnicodeEnvironment, IntPtr.Zero, workingDirectory, ref startup, out var info);
        if (!created)
        {
            var error = new Win32Exception();
            ClosePseudoConsole(console);
            DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
            inputWrite.Dispose();
            outputRead.Dispose();
            throw error;
        }
        CloseHandle(info.hThread);

        return new ConPtySession(
            new FileStream(inputWrite, FileAccess.Write, 1, false),
            new FileStream(outputRead, FileAccess.Read, 4096, false),
            console, info.hProcess, info.dwProcessId, attributes);
    }

    public void Write(string text)
    {
        _writes.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
    }

    public void Resize(int columns, int rows)
    {
        lock (_gate)
        {
            if (_console != IntPtr.Zero)
                ResizePseudoConsole(_console, new Coord((short)columns, (short)rows));
        }
    }

    // Encerra o programa e tudo o que ele abriu.
    public void Kill()
    {
        if (_exited)
            return;
        try
        {
            using var process = Process.GetProcessById(_processId);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // O programa já terminou por conta própria.
        }
    }

    public void Dispose()
    {
        Kill();
        _writes.Writer.TryComplete();
    }

    // Chame depois de se inscrever nos eventos. Até lá a saída espera no canal, e um
    // programa rápido não termina antes de alguém estar ouvindo.
    public void Begin()
    {
        var readerDone = new ManualResetEventSlim();
        StartThread("sirius-pty-read", () =>
        {
            var decoder = Encoding.UTF8.GetDecoder();
            var bytes = new byte[8192];
            var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            try
            {
                int read;
                while ((read = _output.Read(bytes, 0, bytes.Length)) > 0)
                {
                    var count = decoder.GetChars(bytes, 0, read, chars, 0);
                    if (count > 0)
                        Output?.Invoke(new string(chars, 0, count));
                }
            }
            catch (IOException)
            {
                // O pseudoconsole fechou a ponta dele.
            }
            readerDone.Set();
        });

        StartThread("sirius-pty-write", () =>
        {
            try
            {
                while (_writes.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                {
                    while (_writes.Reader.TryRead(out var bytes))
                    {
                        _input.Write(bytes);
                        _input.Flush();
                    }
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                // O programa terminou com algo ainda na fila.
            }
        });

        StartThread("sirius-pty-wait", () =>
        {
            WaitForSingleObject(_process, Infinite);
            GetExitCodeProcess(_process, out var code);
            _exited = true;

            IntPtr console;
            lock (_gate)
            {
                console = _console;
                _console = IntPtr.Zero;
            }
            ClosePseudoConsole(console);
            readerDone.Wait(TimeSpan.FromSeconds(3));

            _writes.Writer.TryComplete();
            DeleteProcThreadAttributeList(_attributes);
            Marshal.FreeHGlobal(_attributes);
            CloseHandle(_process);
            _output.Dispose();
            Exited?.Invoke((int)code);
        });
    }

    private static void StartThread(string name, Action body) =>
        new Thread(() => body()) { IsBackground = true, Name = name }.Start();

    private static IntPtr CreateAttributes(IntPtr console)
    {
        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        var list = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
            throw new Win32Exception();
        if (!UpdateProcThreadAttribute(list, 0, AttributePseudoConsole, console, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            throw new Win32Exception();
        return list;
    }

    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int UseStdHandles = 0x00000100;
    private const uint Infinite = 0xFFFFFFFF;
    private static readonly IntPtr AttributePseudoConsole = new(0x00020016);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord(short x, short y)
    {
        public readonly short X = x;
        public readonly short Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);

    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(IntPtr console, Coord size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr console);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);

    private static bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, int size, IntPtr previous, IntPtr returned) =>
        UpdateProcThreadAttribute(list, flags, attribute, value, new IntPtr(size), previous, returned);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
        uint flags, IntPtr environment, string workingDirectory, ref StartupInfoEx startup, out ProcessInformation information);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint code);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
