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

        // Written by GridTest (Core) while armed for the next obstruction
        // click; polled by the "Add Obstruction" button's label, same bridge
        // pattern as the two flags above.
        public static bool IsAddingObstruction { get; set; }
    }
}
