using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Channels;

namespace ThreeCTool.FluidNcSimulator;

/// <summary>
/// Exposes a <see cref="Simulator"/> as a serial device on Linux through a pseudo-terminal: clients open
/// <see cref="DevicePath"/> (e.g. /dev/pts/7) with a normal serial port API.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed partial class PtySimulatorPort : IDisposable
{
    private const int ReadWrite = 2;
    private const int NoControllingTerminal = 0x100;
    private const short PollIn = 1;
    private const int TcsaNow = 0;

    private readonly Simulator simulator;
    private readonly int master;
    private readonly int slave;
    private readonly SimChannel channel;
    private readonly Channel<string> output = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Thread reader;
    private readonly Task writer;
    private volatile bool running = true;

    public unsafe PtySimulatorPort(Simulator simulator)
    {
        this.simulator = simulator;
        master = posix_openpt(ReadWrite | NoControllingTerminal);
        if (master < 0 || grantpt(master) != 0 || unlockpt(master) != 0)
        {
            throw new IOException($"posix_openpt failed (errno {Marshal.GetLastPInvokeError()})");
        }

        var name = stackalloc byte[256];
        if (ptsname_r(master, name, 256) != 0)
        {
            throw new IOException($"ptsname_r failed (errno {Marshal.GetLastPInvokeError()})");
        }

        DevicePath = Marshal.PtrToStringUTF8((nint)name)!;

        // Keep the slave open so the master never sees EIO between client sessions; make it raw.
        slave = open(DevicePath, ReadWrite | NoControllingTerminal);
        if (slave < 0)
        {
            throw new IOException($"open {DevicePath} failed (errno {Marshal.GetLastPInvokeError()})");
        }

        var termios = stackalloc byte[256];
        if (tcgetattr(slave, termios) == 0)
        {
            cfmakeraw(termios);
            _ = tcsetattr(slave, TcsaNow, termios);
        }

        channel = simulator.Connect("uart", line => output.Writer.TryWrite(line));
        reader = new Thread(ReadLoop) { IsBackground = true, Name = "pty reader" };
        reader.Start();
        writer = Task.Run(WriteLoopAsync);
    }

    public string DevicePath { get; }

    public void Dispose()
    {
        running = false;
        reader.Join();
        output.Writer.TryComplete();
        writer.Wait();
        simulator.Disconnect(channel);
        _ = close(slave);
        _ = close(master);
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int posix_openpt(int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int grantpt(int fd);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int unlockpt(int fd);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int ptsname_r(int fd, byte* buffer, nuint length);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string path, int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial nint read(int fd, byte* buffer, nint count);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial nint write(int fd, byte* buffer, nint count);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int poll(PollFd* fds, nuint count, int timeout);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int tcgetattr(int fd, byte* termios);

    [LibraryImport("libc")]
    private static unsafe partial void cfmakeraw(byte* termios);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int tcsetattr(int fd, int action, byte* termios);

    private unsafe void ReadLoop()
    {
        var buffer = new byte[512];
        while (running)
        {
            var fd = new PollFd { Fd = master, Events = PollIn };
            if (poll(&fd, 1, 50) <= 0 || (fd.Revents & PollIn) == 0)
            {
                if (fd.Revents != 0 && (fd.Revents & PollIn) == 0)
                {
                    Thread.Sleep(10);
                }

                continue;
            }

            nint n;
            fixed (byte* p = buffer)
            {
                n = read(master, p, buffer.Length);
            }

            if (n > 0)
            {
                simulator.Receive(channel, buffer.AsSpan(0, (int)n));
            }
        }
    }

    private async Task WriteLoopAsync()
    {
        await foreach (var line in output.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (!simulator.IsPowered)
            {
                continue;
            }

            var bytes = Encoding.UTF8.GetBytes(line);
            WriteAll(bytes);
        }
    }

    private unsafe void WriteAll(byte[] bytes)
    {
        var offset = 0;
        while (offset < bytes.Length && running)
        {
            nint n;
            fixed (byte* p = bytes)
            {
                n = write(master, p + offset, bytes.Length - offset);
            }

            if (n <= 0)
            {
                return;
            }

            offset += (int)n;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }
}
