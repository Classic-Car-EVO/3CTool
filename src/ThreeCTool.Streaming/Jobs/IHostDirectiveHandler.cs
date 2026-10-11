using ThreeCTool.Streaming.Gcode;

namespace ThreeCTool.Streaming.Jobs;

/// <summary>Executes host directives such as <c>(MC:HEAT HOTEND 215 WAIT)</c> (Moonraker on the feeder).</summary>
public interface IHostDirectiveHandler
{
    bool CanHandle(HostDirective directive);

    /// <summary>Runs the directive; for <see cref="HostDirective.Wait"/> directives, returns once the target is reached.</summary>
    Task ExecuteAsync(HostDirective directive, CancellationToken cancellationToken);

    /// <summary>Switches every heater off (stop, alarm, abort, connection loss).</summary>
    Task AllHeatersOffAsync(CancellationToken cancellationToken);
}
