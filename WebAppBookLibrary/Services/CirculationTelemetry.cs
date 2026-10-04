using System.Diagnostics.Metrics;

namespace WebAppBookLibrary.Services;

public static class CirculationTelemetry
{
    public static readonly Meter Meter = new("BookLibrary.Circulation");
    public static readonly Counter<long> Expired = Meter.CreateCounter<long>("circulation.pickups.expired");
    public static readonly Counter<long> Conflicts = Meter.CreateCounter<long>("circulation.conflicts");
    public static readonly Counter<long> Failures = Meter.CreateCounter<long>("circulation.worker.failures");
    public static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("circulation.worker.duration", "s");
    public static readonly Histogram<double> QueueAge = Meter.CreateHistogram<double>("circulation.queue.oldest", "s");
}
