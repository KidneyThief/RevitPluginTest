namespace RevitPluginTest
{
    // Shared between Host (the panel's visibility checkbox) and Core (the
    // overlay window, the only thing that knows whether the current view
    // supports projection). Lives in Host so both sides can reach it safely -
    // Core writing into a Host-defined static is fine (Core already
    // references Host); the reverse would risk pinning Core's ALC.
    public static class OverlayState
    {
        public static bool UserWantsVisible { get; set; } = true;
        public static bool IsViewSupported { get; set; } = true;
    }
}
