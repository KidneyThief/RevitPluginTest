using Autodesk.Revit.UI;

namespace RevitPluginTest
{
    // Marshals named-command invocations onto Revit's main thread at a point
    // where a valid API context is guaranteed - needed for anything triggered
    // from outside Revit's own command-invocation path (dockable pane buttons,
    // a future console) that might end up touching the document/Transactions.
    public sealed class CommandDispatcher : IExternalEventHandler
    {
        private readonly Scheduler _scheduler;
        private readonly Queue<(string Name, object?[] Args)> _pending = new();

        public CommandDispatcher(Scheduler scheduler)
        {
            _scheduler = scheduler;
        }

        public void Enqueue(string name, object?[] args)
        {
            _pending.Enqueue((name, args));
        }

        public void Execute(UIApplication app)
        {
            while (_pending.Count > 0)
            {
                var (name, args) = _pending.Dequeue();
                _scheduler.Invoke(name, app, args);
            }
        }

        public string GetName() => "RevitPluginTest Command Dispatcher";
    }
}
