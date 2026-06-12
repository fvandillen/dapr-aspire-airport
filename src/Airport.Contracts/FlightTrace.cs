using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Airport.Contracts;

/// <summary>
/// Helpers to give every span emitted for one flight the same OpenTelemetry TraceId,
/// even though Dapr Workflow activities aren't natively chained into the workflow's
/// trace context. The TraceId is derived deterministically from the FlightId, so all
/// activities + the original schedule call end up grouped under one trace in the
/// Aspire dashboard.
/// </summary>
public static class FlightTrace
{
    /// <summary>
    /// Returns a deterministic <see cref="ActivityContext"/> built from the FlightId.
    /// Use this as the parent context when starting an Activity so that every span
    /// for the same flight lands in the same trace.
    /// </summary>
    public static ActivityContext ContextFor(string flightId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("airport-flight:" + flightId));
        var traceId = ActivityTraceId.CreateFromBytes(hash.AsSpan(0, 16));
        var spanId = ActivitySpanId.CreateFromBytes(hash.AsSpan(16, 8));
        return new ActivityContext(traceId, spanId, ActivityTraceFlags.Recorded);
    }
}
