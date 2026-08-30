using Bmd.Commands;
using Bmd.Commands.Atem;
using Bmd.Commands.MultiView;
using Bmd.Commands.Videohub;
using Bmd.Update;
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

var multiview = new MultiViewCommands();
app.Add("multiview info", multiview.Info);
app.Add("multiview input list", multiview.InputList);
app.Add("multiview view list", multiview.ViewList);
app.Add("multiview config", multiview.Config);
app.Add("multiview view set", multiview.ViewSet);
app.Add("multiview input rename", multiview.InputRename);
app.Add("multiview view rename", multiview.ViewRename);
app.Add("multiview view lock", multiview.ViewLock);
app.Add("multiview view unlock", multiview.ViewUnlock);
app.Add("multiview layout", multiview.Layout);
app.Add("multiview format", multiview.Format);
app.Add("multiview solo", multiview.Solo);
app.Add("multiview show", multiview.Show);
app.Add("multiview take-mode", multiview.TakeMode);
app.Add("multiview widescreen-sd", multiview.WidescreenSd);
app.Add("multiview watch", multiview.Watch);
app.Add("multiview export", multiview.Export);
app.Add("multiview restore", multiview.Restore);

var discover = new DiscoverCommands();
app.Add("discover", discover.Discover);

var version = new VersionCommands();
app.Add("version", version.Version);

var atem = new AtemCommands();
app.Add("atem info", atem.Info);
app.Add("atem input list", atem.InputList);
app.Add("atem input rename", atem.InputRename);
app.Add("atem aux list", atem.AuxList);
app.Add("atem aux set", atem.AuxSet);
app.Add("atem status", atem.Status);
app.Add("atem program set", atem.ProgramSet);
app.Add("atem preview set", atem.PreviewSet);

var update = new UpdateCommands();
app.Add("update", update.Update);

if (GroupHelp.TryWrite(args, Console.Out)) return 0;

// The passive update check (see the spec's "Self-update") starts here so it overlaps the
// command's own work, and prints at most a two-line stderr notice once the command is done.
// It suppresses itself for --json, non-TTY stderr, `update`/`version`, and update.check = false.
var notice = UpdateNoticeRunner.Start(args);

app.Run(args);

notice.WriteIfAny(Console.Error);
return Environment.ExitCode;
