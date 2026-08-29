using Bmd.Commands;
using Bmd.Commands.Videohub;
using ConsoleAppFramework;

var app = ConsoleApp.Create();

// Registered as individual method delegates against one shared instance rather than
// app.Add<ConfigCommands>("config"): ConfigCommands has two constructors (parameterless
// + ConfigStore-factory, for tests), but ConsoleAppFramework's class-based Add<T> requires
// exactly one constructor and treats any non-primitive constructor parameter as a
// DI-resolved service — which throws (ServiceProvider is never configured here) the moment
// a command runs. Registering bound method groups sidesteps constructor analysis entirely.
var config = new ConfigCommands();
app.Add("config set", config.Set);
app.Add("config get", config.Get);
app.Add("config unset", config.Unset);
app.Add("config list", config.List);

var videohub = new VideohubCommands();
app.Add("videohub info", videohub.Info);

app.Run(args);
