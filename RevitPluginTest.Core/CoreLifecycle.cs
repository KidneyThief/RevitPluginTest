using RevitPluginTest;

namespace RevitPluginTest.Core
{
    // The one reload-boundary entry point Host knows about.
    // RevitPluginTestApplication.ReloadCore() always calls just "Initialize"
    // before unloading Core - Host never needs to learn about individual
    // subsystems' cleanup needs as Core grows. Add new steps here directly,
    // entirely within Core, fully hot-reloadable - no Host change, no restart.
    public static class CoreLifecycle
    {
        [Schedulable("Initialize")]
        public static bool Initialize()
        {
            DebugOverlay.CloseOverlay();
            DebugOverlay.ClearOverlay();
            GraphTestFunctions.ResetGraph();
            return true;
        }
    }
}
