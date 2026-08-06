using Autodesk.Revit.UI;

namespace RevitPluginTest
{
    public sealed class PluginPaneProvider : IDockablePaneProvider
    {
        private readonly PluginPanel _panel = new();

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = _panel;
            data.InitialState = new DockablePaneState
            {
                DockPosition = DockPosition.Right
            };
        }
    }
}
