using Autodesk.Revit.UI;
using RevitPluginTest;

namespace RevitPluginTest.Core
{
    // Entry points the host's stable tick calls by name (Scheduler.Invoke),
    // never by holding a direct reference to anything defined in Core - that
    // would pin this assembly's AssemblyLoadContext and block reload.
    public static class DebugOverlay
    {
        private static DebugOverlayWindow? _window;

        [Schedulable("UpdateOverlay", Quiet = true)]
        public static bool UpdateOverlay(UIApplication uiApp)
        {
            _window ??= new DebugOverlayWindow();
            _window.Update(uiApp);
            GridTest.UpdateHighlight(uiApp);
            GridTest.UpdateClickPlacement(uiApp);
            return true;
        }

        // Invoked by ReloadCore() before the assembly is unloaded - a shown
        // Window holds real resources and would otherwise block collection.
        [Schedulable("CloseOverlay")]
        public static bool CloseOverlay()
        {
            _window?.Close();
            _window = null;
            return true;
        }

        [Schedulable("ClearOverlay")]
        public static bool ClearOverlay()
        {
            DebugDraw.ClearAll();
            return true;
        }

        // Logs raw view basis vectors, zoom corners, and the exact world/screen
        // coordinates of everything currently drawn, on the next tick.
        [Schedulable("DumpOverlay")]
        public static bool DumpOverlay()
        {
            DebugOverlayWindow.RequestDump();
            return true;
        }
    }
}
