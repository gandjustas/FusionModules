namespace Startup.Contracts;

/// <summary>
/// Work a module needs finished before the application accepts its first request.
/// </summary>
/// <remarks>
/// <para>
/// A service being migrated often had a few lines between <c>Build()</c> and <c>RunAsync()</c>:
/// check the schema, seed reference data, warm a cache that the first request would otherwise
/// miss. A module has nowhere to put those. Its overrides are synchronous, and
/// <c>AddHostedService</c> is not the substitute it looks like — see the README.
/// </para>
/// <para>
/// So the module declares the work and the host runs it. This type is the declaration. It lives
/// in a plain library rather than in a module, which is what lets both sides reference it: the
/// host enumerates a contract it owns, and stays ignorant of who registered what.
/// </para>
/// </remarks>
/// <param name="Name">What to say in the log, and in the error if it throws.</param>
/// <param name="RunAsync">The work itself, given a scope over the built container.</param>
public sealed record StartupTask(string Name, Func<IServiceProvider, CancellationToken, Task> RunAsync);
