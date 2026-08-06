namespace RevitPluginTest
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public sealed class SchedulableAttribute : Attribute
    {
        public string Name { get; }

        // Opts out of Scheduler.Invoke's Success/Fail logging - for functions
        // invoked too frequently (e.g. every Idling tick) for that to be useful.
        public bool Quiet { get; init; }

        public SchedulableAttribute(string name)
        {
            Name = name;
        }
    }
}
