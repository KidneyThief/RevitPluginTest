using System.Collections.ObjectModel;

namespace RevitPluginTest
{
    // Backed by an ObservableCollection so the panel's log list updates live.
    // Safe to call from Idling and from CommandDispatcher.Execute - both run
    // on Revit's main/UI thread, the same thread that owns the bound control.
    public static class Logger
    {
        public static ObservableCollection<string> Entries { get; } = new();

        public static void Log(string message)
        {
            Entries.Add($"[{DateTime.Now:T}] {message}");
        }

        public static void Clear()
        {
            Entries.Clear();
        }
    }
}
