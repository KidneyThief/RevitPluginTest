using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using System.IO;
using System.Reflection;

namespace RevitPluginTest
{
    public class RevitPluginTestApplication : IExternalApplication
    {
        public static RevitPluginTestApplication? Current { get; private set; }
        public static readonly DockablePaneId PanelId = new(new Guid("91621780-5CA9-4EE9-B854-8B23F800E893"));

        public Scheduler Scheduler => _scheduler;

        private readonly Scheduler _scheduler = new();
        private PluginLoadContext? _coreContext;
        private CommandDispatcher? _dispatcher;
        private ExternalEvent? _externalEvent;

        public Result OnStartup(UIControlledApplication application)
        {
            Current = this;

            CreateRibbon(application);
            application.RegisterDockablePane(PanelId, "RevitPluginTest", new PluginPaneProvider());

            _scheduler.RegisterStableFunctions(typeof(RevitPluginTestApplication).Assembly);
            _dispatcher = new CommandDispatcher(_scheduler);
            _externalEvent = ExternalEvent.Create(_dispatcher);

            LoadCore();

            application.Idling += OnIdling;

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            application.Idling -= OnIdling;
            Current = null;

            return Result.Succeeded;
        }

        // Safe to call from anywhere - button click handlers, a future console -
        // regardless of whether Revit is currently in a valid API context.
        public void InvokeCommand(string name, params object?[] args)
        {
            _dispatcher?.Enqueue(name, args);
            _externalEvent?.Raise();
        }

        private void OnIdling(object? sender, IdlingEventArgs e)
        {
            _scheduler.Pump(DateTime.Now);

            if (sender is UIApplication uiApp)
            {
                // Goes through Invoke rather than a direct field reference so
                // this stable tick never holds a reference to a Core-defined
                // type - Invoke already wraps the call in its own try/catch.
                _scheduler.Invoke("UpdateOverlay", uiApp);
            }
        }

        public void ReloadCore()
        {
            var oldContext = _coreContext;
            _coreContext = null;

            if (oldContext != null)
            {
                // The one reload-time hook into Core - Core's own Initialize()
                // decides what needs cleaning up (currently: closing the overlay
                // window, resetting the graph). This call site is permanent;
                // new cleanup steps get added inside Initialize() itself, not
                // here, so Host never needs to change as Core grows. Must run
                // before the dictionary is cleared below - the name lookup
                // needs to still resolve here.
                _scheduler.Invoke("Initialize", null);

                // Must happen before Unload(): the dictionary's MethodInfo entries
                // and any scheduled-call arguments could reference types from
                // oldContext, and holding either would keep it alive forever.
                _scheduler.ClearReloadableFunctions();
                _scheduler.ClearScheduledCalls();
                DynamicPanel.ClearWidgets();

                var contextRef = new WeakReference(oldContext, trackResurrection: true);
                oldContext.Unload();

                for (var i = 0; contextRef.IsAlive && i < 10; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
            }

            LoadCore();
        }

        private void LoadCore()
        {
            _coreContext = new PluginLoadContext();

            var coreAssembly = LoadCoreAssemblyFromMemory(_coreContext);

            _scheduler.RegisterReloadableFunctions(coreAssembly);

            // Lets Core (re-)declare its panel widgets fresh on every load;
            // no-op (logged only to Debug output, not the panel) if Core
            // doesn't define this function.
            _scheduler.Invoke("RegisterPanelWidgets", null);
        }

        // LoadFromAssemblyPath keeps an OS file lock on the DLL for as long as
        // it's loaded, even inside a collectible ALC - which would defeat the
        // whole point of hot-reloading Core. Reading the bytes into memory
        // first and loading from that stream avoids holding the file open.
        private static Assembly LoadCoreAssemblyFromMemory(PluginLoadContext context)
        {
            var assemblyPath = ResolveCoreAssemblyPath();
            var assemblyBytes = File.ReadAllBytes(assemblyPath);

            using var assemblyStream = new MemoryStream(assemblyBytes);

            var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");

            if (File.Exists(pdbPath))
            {
                var pdbBytes = File.ReadAllBytes(pdbPath);
                using var pdbStream = new MemoryStream(pdbBytes);

                return context.LoadFromStream(assemblyStream, pdbStream);
            }

            return context.LoadFromStream(assemblyStream);
        }

        private static string ResolveCoreAssemblyPath()
        {
            var hostBinDirectory = Path.GetDirectoryName(typeof(RevitPluginTestApplication).Assembly.Location)!;
            var projectRoot = Path.GetFullPath(Path.Combine(hostBinDirectory, "..", "..", ".."));

            // Assumes Core was built in Debug config alongside the host project.
            return Path.Combine(projectRoot, "RevitPluginTest.Core", "bin", "Debug", "net10.0-windows", "RevitPluginTest.Core.dll");
        }

        private static void CreateRibbon(UIControlledApplication application)
        {
            var panel = application.CreateRibbonPanel("RevitPluginTest");
            var assemblyPath = typeof(RevitPluginTestApplication).Assembly.Location;

            panel.AddItem(new PushButtonData(
                "HelloWorldCommand",
                "Hello World",
                assemblyPath,
                typeof(HelloWorldCommand).FullName!));

            panel.AddItem(new PushButtonData(
                "ReloadCoreCommand",
                "Reload Core",
                assemblyPath,
                typeof(ReloadCoreCommand).FullName!));

            panel.AddItem(new PushButtonData(
                "TogglePanelCommand",
                "Show Panel",
                assemblyPath,
                typeof(TogglePanelCommand).FullName!));
        }
    }
}
