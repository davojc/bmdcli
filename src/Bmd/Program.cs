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
app.Add("videohub input list", videohub.InputList);
app.Add("videohub output list", videohub.OutputList);
app.Add("videohub route list", videohub.RouteList);
app.Add("videohub watch", videohub.Watch);
app.Add("videohub export", videohub.Export);
app.Add("videohub restore", videohub.Restore);
app.Add("videohub route set", videohub.RouteSet);
app.Add("videohub input rename", videohub.InputRename);
app.Add("videohub output rename", videohub.OutputRename);
app.Add("videohub output lock", videohub.OutputLock);
app.Add("videohub output unlock", videohub.OutputUnlock);

var discover = new DiscoverCommands();
app.Add("discover", discover.Discover);

var version = new VersionCommands();
app.Add("version", version.Version);

var update = new UpdateCommands();
app.Add("update", update.Update);

if (GroupHelp.TryWrite(args, Console.Out)) return 0;

app.Run(args);
return Environment.ExitCode;
