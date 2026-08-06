namespace RevitPluginTest
{
    // Lets Core register panel widgets by name/data instead of constructing
    // WPF objects itself. PluginPanel does all the actual widget construction
    // and event wiring, so nothing Core-defined ever gets embedded in the
    // panel's object graph - which Revit holds a permanent reference to via
    // RegisterDockablePane, so anything it ends up holding must be Host-owned.
    public static class DynamicPanel
    {
        public static PluginPanel? Current { get; set; }

        public static void AddButton(string? section, string label, string commandName)
        {
            Current?.AddDynamicButton(section, label, commandName);
        }

        public static void ClearWidgets()
        {
            Current?.ClearDynamicWidgets();
        }
    }
}
