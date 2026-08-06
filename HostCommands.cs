namespace RevitPluginTest
{
    // Host-side commands, invokable by name through Scheduler.Invoke just like
    // any Core-defined function. Ribbon commands delegate here so the ribbon
    // and any future name-based dispatch (panel buttons, a console) share one
    // implementation.
    public static class HostCommands
    {
        [Schedulable("ReloadCore")]
        public static bool ReloadCore()
        {
            var app = RevitPluginTestApplication.Current;

            if (app == null)
            {
                return false;
            }

            app.ReloadCore();
            return true;
        }
    }
}
