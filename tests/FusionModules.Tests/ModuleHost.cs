using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FusionModules.Tests;

/// <summary>
/// Starts and stops a web host with modules activated exactly as ASP.NET Core activates the
/// assemblies named in <c>HOSTINGSTARTUPASSEMBLIES</c>, and reports what happened.
/// </summary>
/// <remarks>
/// The startup checks run where the module validation runs — when the pipeline is built — so a
/// failure surfaces from <c>StartAsync</c>, not from <c>Build</c>.
/// </remarks>
internal static class ModuleHost
{
    /// <param name="Failure">What startup threw, or null when the host started and stopped.</param>
    /// <param name="Logged">Every message logged, in order.</param>
    /// <param name="Fields">The named values of each structured log entry, in order.</param>
    public sealed record Result(InvalidOperationException? Failure, List<string> Logged, List<Dictionary<string, object?>> Fields);

    /// <param name="applicationName">
    /// The host's assembly, <c>IHostEnvironment.ApplicationName</c>. By default the test assembly —
    /// the entry assembly — which is also the modules' assembly: the host is a module itself.
    /// </param>
    public static async Task<Result> StartAsync(
        IEnumerable<ModuleBase> modules,
        Action<IServiceCollection>? configureHost = null,
        Action<WebApplication>? configureApp = null,
        Action<IServiceCollection>? afterModules = null,
        string? applicationName = null,
        params (string Key, string Value)[] settings)
    {
        var logged = new List<string>();
        var fields = new List<Dictionary<string, object?>>();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = applicationName });
        builder.Environment.EnvironmentName = Environments.Development;
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)));
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new ListLoggerProvider(logged, fields));
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(1));

        configureHost?.Invoke(builder.Services);

        foreach (var module in modules)
        {
            ((IHostingStartup)module).Configure(builder.WebHost);
        }

        // What a test's ConfigureTestServices does: change the container after the modules.
        afterModules?.Invoke(builder.Services);

        await using var app = builder.Build();
        configureApp?.Invoke(app);
        try
        {
            await app.StartAsync();
            await app.StopAsync();
            return new Result(null, logged, fields);
        }
        catch (InvalidOperationException e)
        {
            return new Result(e, logged, fields);
        }
    }

    private sealed class ListLoggerProvider(List<string> messages, List<Dictionary<string, object?>> fields) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ListLogger(messages, fields);

        public void Dispose()
        {
        }

        private sealed class ListLogger(List<string> messages, List<Dictionary<string, object?>> fields) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (messages)
                {
                    messages.Add(formatter(state, exception));
                    fields.Add(state is IEnumerable<KeyValuePair<string, object?>> pairs
                        ? pairs.ToDictionary(pair => pair.Key, pair => pair.Value)
                        : []);
                }
            }
        }
    }
}
