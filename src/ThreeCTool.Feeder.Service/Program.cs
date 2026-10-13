using ThreeCTool.Feeder.Service;

var app = FeederHost.Build(args);
await app.RunAsync().ConfigureAwait(false);
