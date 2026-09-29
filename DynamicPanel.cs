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

        // A button whose displayed text switches to activeLabel while
        // OverlayState.IsAddingObstruction is true, and back to idleLabel
        // once it's false - polled the same way the overlay visibility
        // checkbox already reflects Core-owned state, rather than Core
        // handing Host a delegate (which would pin Core's ALC alive).
        public static void AddObstructionButton(string? section, string idleLabel, string activeLabel, string commandName)
        {
            Current?.AddDynamicObstructionButton(section, idleLabel, activeLabel, commandName);
        }

        public static void AddDropdown(string? section, string label, IReadOnlyList<string> options, string commandName)
        {
            Current?.AddDynamicDropdown(section, label, options, commandName);
        }

        public static void AddCheckbox(string? section, string label, bool initialValue, string commandName)
        {
            Current?.AddDynamicCheckbox(section, label, initialValue, commandName);
        }

        public static void AddSlider(string? section, string label, double min, double max, double initialValue, string commandName, double step = 0)
        {
            Current?.AddDynamicSlider(section, label, min, max, initialValue, commandName, step);
        }

        public static void NewLine(string? section)
        {
            Current?.AddDynamicNewLine(section);
        }

        public static void ClearWidgets()
        {
            Current?.ClearDynamicWidgets();
        }
    }
}
