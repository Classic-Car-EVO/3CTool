using System.Globalization;

using ThreeCTool.FluidNcSimulator;

var options = new SimulatorOptions();
int? tcpPort = null;
var pty = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--tcp":
            tcpPort = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--pty":
            pty = true;
            break;
        case "--time-scale":
            options.TimeScale = double.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--line-numbers":
            options.UseLineNumbers = true;
            break;
        case "--no-must-home":
            options.MustHome = false;
            break;
        default:
            Console.Error.WriteLine("usage: FluidNcSimulator [--tcp PORT] [--pty] [--time-scale N] [--line-numbers] [--no-must-home]");
            return 2;
    }
}

if (tcpPort is null && !pty)
{
    tcpPort = 23;
}

using var simulator = new Simulator(options);
await using var server = tcpPort is { } port ? new TcpSimulatorServer(simulator, port) : null;
using var serial = pty && OperatingSystem.IsLinux() ? new PtySimulatorPort(simulator) : null;
var serialPath = OperatingSystem.IsLinux() ? serial?.DevicePath : null;
if (server is not null)
{
    Console.WriteLine($"TCP: 127.0.0.1:{server.Port}");
}

if (serialPath is not null)
{
    Console.WriteLine($"Serial: {serialPath}");
}

Console.WriteLine("Commands: estop, power-off, power-on, drop, status, quit");
while (Console.ReadLine() is { } command)
{
    switch (command.Trim())
    {
        case "estop":
            simulator.EmergencyStop();
            break;
        case "power-off":
            simulator.PowerLoss();
            break;
        case "power-on":
            simulator.PowerOn();
            break;
        case "drop":
            simulator.DropConnections();
            break;
        case "status":
            Console.WriteLine($"{simulator.State} mpos={string.Join(',', simulator.MachinePosition)} physical={string.Join(',', simulator.PhysicalPosition)} blocks={simulator.ExecutedBlocks}");
            break;
        case "quit":
            return 0;
        default:
            Console.WriteLine("?");
            break;
    }
}

return 0;
