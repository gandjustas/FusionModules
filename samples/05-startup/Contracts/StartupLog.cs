using System.Collections.Concurrent;

namespace Startup.Contracts;

/// <summary>
/// Records what ran, in the order it ran, so the sample can assert on startup ordering instead of
/// describing it.
/// </summary>
/// <remarks>
/// A singleton the host registers and the module writes to. Nothing an application would ship —
/// it exists because the claim this sample makes is about sequence, and a claim about sequence
/// should be a test rather than a paragraph.
/// </remarks>
public sealed class StartupLog
{
    private readonly ConcurrentQueue<string> _events = new();

    public void Record(string @event) => _events.Enqueue(@event);

    public IReadOnlyList<string> Events => [.. _events];
}
