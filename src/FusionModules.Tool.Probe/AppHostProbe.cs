using System.Collections;
using System.Reflection;
using Microsoft.Extensions.Hosting;

namespace FusionModules.Tool.Probe;

/// <summary>
/// An Aspire AppHost is a host like any other: DistributedApplication.CreateBuilder wraps a
/// HostApplicationBuilder, so HostFactoryResolver sees it built, and the DistributedApplicationModel
/// is in its container by then — which is how Aspire's own testing builder gets at it. Everything
/// below is reflection, by name: the probe has no Aspire reference, and has to read Aspire 8, 9 and
/// 13 alike.
/// </summary>
internal static class AppHostProbe
{
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.Instance;

    public static void Run(Assembly target, string[] args, TimeSpan timeout, ProbeOutput result)
    {
        var factory = HostFactoryResolver.ResolveHostFactory(target, waitTimeout: timeout, stopApplication: true);
        if (factory is null)
        {
            result.Errors.Add(ProbeError.Of("resolve", "The AppHost has no entry point, or does not use Microsoft.Extensions.Hosting."));
            return;
        }

        object host;
        try
        {
            host = factory(args);
        }
        catch (Exception exception)
        {
            result.Errors.Add(ProbeError.From("build", exception));
            return;
        }

        result.Status = "built";
        var services = HostServices.Of(host)!;
        var aspire = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Aspire.Hosting");
        var modelType = aspire?.GetType("Aspire.Hosting.ApplicationModel.DistributedApplicationModel");
        if (aspire is null || modelType is null || services.GetService(modelType) is not { } model)
        {
            result.Errors.Add(ProbeError.Of("model", "The host built, but no DistributedApplicationModel is registered: not an Aspire AppHost?"));
            return;
        }

        result.Aspire = new ProbeAspireModel
        {
            Version = aspire.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
                      ?? aspire.GetName().Version?.ToString(),
        };

        object? executionContext = null;
        try
        {
            executionContext = PublishContext(aspire, services);
        }
        catch (Exception exception)
        {
            result.Errors.Add(ProbeError.From("execution-context", exception));
        }

        var resources = (IEnumerable?)Get(model, "Resources") ?? Array.Empty<object>();
        foreach (var resource in resources)
        {
            result.Aspire.Resources.Add(Describe(resource, aspire, executionContext));
        }

        result.Status = "started";
    }

    private static ProbeAspireResource Describe(object resource, Assembly aspire, object? executionContext)
    {
        var type = resource.GetType();
        var description = new ProbeAspireResource
        {
            Name = Get(resource, "Name") as string ?? "?",
            Type = type.Name,
            Parent = Get(Get(resource, "Parent"), "Name") as string,
            Command = Get(resource, "Command") as string,
            WorkingDirectory = Get(resource, "WorkingDirectory") as string,
            Annotations = [],
        };

        if (type.Name == "ParameterResource")
        {
            description.Secret = Get(resource, "Secret") as bool?;
        }

        if (Implements(type, "IResourceWithConnectionString"))
        {
            description.ConnectionString = Expression(Get(resource, "ConnectionStringExpression"));
        }

        var callbacks = new List<object>();
        var relationships = new List<ProbeAspireRelationship>();
        var endpoints = new List<ProbeAspireEndpoint>();

        foreach (var annotation in (IEnumerable?)Get(resource, "Annotations") ?? Array.Empty<object>())
        {
            var annotationType = annotation.GetType();
            if (!description.Annotations.Contains(annotationType.Name))
            {
                description.Annotations.Add(annotationType.Name);
            }

            try
            {
                // IProjectMetadata since Aspire 8.0; other annotations with a ProjectPath before it.
                if (Implements(annotationType, "IProjectMetadata") || annotationType.GetProperty("ProjectPath") is not null)
                {
                    description.ProjectPath ??= Get(annotation, "ProjectPath") as string;
                }

                switch (KnownAnnotation(annotationType))
                {
                    case "ContainerImageAnnotation":
                        var registry = Get(annotation, "Registry") as string;
                        var image = Get(annotation, "Image") as string;
                        var tag = Get(annotation, "Tag") as string;
                        description.Image = $"{(string.IsNullOrEmpty(registry) ? "" : registry + "/")}{image}{(string.IsNullOrEmpty(tag) ? "" : ":" + tag)}";
                        break;
                    case "DockerfileBuildAnnotation":
                        description.DockerfileContext = Get(annotation, "ContextPath") as string;
                        description.Dockerfile = Get(annotation, "DockerfilePath") as string;
                        break;
                    case "EndpointAnnotation":
                        endpoints.Add(new ProbeAspireEndpoint
                        {
                            Name = Get(annotation, "Name") as string,
                            Scheme = Get(annotation, "UriScheme") as string,
                            Transport = Get(annotation, "Transport") as string,
                            Port = Get(annotation, "Port") as int?,
                            TargetPort = Get(annotation, "TargetPort") as int?,
                            External = Get(annotation, "IsExternal") as bool? ?? false,
                        });
                        break;
                    case "ReplicaAnnotation":
                        description.Replicas = Get(annotation, "Replicas") as int?;
                        break;
                    case "ResourceRelationshipAnnotation":
                        AddRelationship(relationships, Get(Get(annotation, "Resource"), "Name") as string, Get(annotation, "Type") as string ?? "Reference");
                        break;
                    case "WaitAnnotation":
                        AddRelationship(relationships, Get(Get(annotation, "Resource"), "Name") as string, "WaitFor");
                        break;
                    case "EnvironmentCallbackAnnotation":
                        callbacks.Add(annotation);
                        break;
                }
            }
            catch (Exception exception)
            {
                (description.Errors ??= []).Add(ProbeError.From(annotationType.Name, exception));
            }
        }

        if (description.Parent is not null)
        {
            AddRelationship(relationships, description.Parent, "Parent");
        }

        if (callbacks.Count > 0)
        {
            try
            {
                description.Environment = Environment(resource, callbacks, aspire, executionContext);
            }
            catch (Exception exception)
            {
                (description.Errors ??= []).Add(ProbeError.From("environment", exception));
            }
        }

        description.Endpoints = endpoints.Count > 0 ? endpoints : null;
        description.Relationships = relationships.Count > 0 ? relationships : null;
        return description;
    }

    /// <summary>The annotations the model is read from, by the name of the Aspire type.</summary>
    private static readonly string[] KnownAnnotations =
    [
        "ContainerImageAnnotation", "DockerfileBuildAnnotation", "EndpointAnnotation", "ReplicaAnnotation",
        "ResourceRelationshipAnnotation", "WaitAnnotation", "EnvironmentCallbackAnnotation",
    ];

    /// <summary>
    /// The known annotation this one is or derives from, else its own name. Aspire's
    /// <c>WithEnvironment(name, "value")</c> adds an <c>EnvironmentAnnotation</c>, an internal
    /// <c>EnvironmentCallbackAnnotation</c> of its own type: matched by its own name it was passed
    /// over, and with it every literal variable of a resource — <c>ASPNETCORE_HOSTINGSTARTUPASSEMBLIES</c>,
    /// the topology itself.
    /// </summary>
    internal static string KnownAnnotation(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (Array.IndexOf(KnownAnnotations, current.Name) >= 0)
            {
                return current.Name;
            }
        }

        return type.Name;
    }

    private static void AddRelationship(List<ProbeAspireRelationship> relationships, string? resource, string type)
    {
        if (resource is not null && !relationships.Any(r => r.Resource == resource && r.Type == type))
        {
            relationships.Add(new ProbeAspireRelationship { Resource = resource, Type = type });
        }
    }

    /// <summary>
    /// Runs the resource's environment callbacks against a publish-mode execution context — the
    /// values then come back as manifest expressions such as <c>{catalogdb.connectionString}</c>
    /// rather than as addresses of containers that are not running.
    /// </summary>
    private static SortedDictionary<string, string?> Environment(object resource, List<object> callbacks, Assembly aspire, object? executionContext)
    {
        var contextType = aspire.GetType("Aspire.Hosting.ApplicationModel.EnvironmentCallbackContext")
                          ?? throw new InvalidOperationException("No EnvironmentCallbackContext in Aspire.Hosting.");
        var variables = new Dictionary<string, object>();
        var context = Construct(contextType, executionContext, variables, resource);
        var collected = Get(context, "EnvironmentVariables") as IDictionary ?? variables;
        var errors = new List<string>();

        foreach (var annotation in callbacks)
        {
            try
            {
                if (Get(annotation, "Callback") is Delegate callback && callback.DynamicInvoke(context) is Task task)
                {
                    task.Wait(TimeSpan.FromSeconds(10));
                }
            }
            catch (Exception exception)
            {
                var error = ProbeError.From("environment", exception);
                errors.Add($"{error.Type}: {error.Message}");
            }
        }

        var environment = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in collected)
        {
            environment[(string)entry.Key] = Expression(entry.Value);
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"{errors.Count} environment callback(s) failed: {string.Join(" | ", errors.Distinct())}");
        }

        return environment;
    }

    /// <summary>
    /// A publish-mode execution context, or null where this Aspire has none (8.0 previews): the
    /// environment callbacks then run without one, and those that need it fail one by one.
    /// </summary>
    private static object? PublishContext(Assembly aspire, IServiceProvider services)
    {
        var operationType = aspire.GetType("Aspire.Hosting.DistributedApplicationOperation");
        var contextType = aspire.GetType("Aspire.Hosting.DistributedApplicationExecutionContext");
        if (operationType is null || contextType is null || !Enum.TryParse(operationType, "Publish", out var publish))
        {
            return null;
        }

        var optionsType = aspire.GetType("Aspire.Hosting.DistributedApplicationExecutionContextOptions");

        if (optionsType is not null
            && optionsType.GetConstructors()
                .OrderBy(c => c.GetParameters().Length)
                .FirstOrDefault(c => c.GetParameters().Length > 0 && c.GetParameters()[0].ParameterType == operationType) is { } optionsConstructor)
        {
            var optionsArguments = optionsConstructor.GetParameters()
                .Select((p, i) => i == 0 ? publish : p.HasDefaultValue ? p.DefaultValue : null)
                .ToArray();
            var options = optionsConstructor.Invoke(optionsArguments);
            optionsType.GetProperty("ServiceProvider")?.SetValue(options, services);
            if (contextType.GetConstructor([optionsType]) is { } withOptions)
            {
                return withOptions.Invoke([options]);
            }
        }

        return contextType.GetConstructor([operationType]) is { } withOperation ? withOperation.Invoke([publish]) : null;
    }

    private static object Construct(Type type, object? executionContext, Dictionary<string, object> variables, object resource)
    {
        var constructor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var arguments = constructor.GetParameters().Select(p =>
        {
            var parameterType = p.ParameterType;
            if (executionContext is not null && parameterType.IsInstanceOfType(executionContext))
            {
                return executionContext;
            }

            if (parameterType.IsInstanceOfType(variables))
            {
                return variables;
            }

            if (parameterType.IsInstanceOfType(resource))
            {
                return resource;
            }

            if (parameterType == typeof(CancellationToken))
            {
                return CancellationToken.None;
            }

            if (parameterType.FullName == "Microsoft.Extensions.Logging.ILogger")
            {
                return Type.GetType("Microsoft.Extensions.Logging.Abstractions.NullLogger, Microsoft.Extensions.Logging.Abstractions")
                    ?.GetProperty("Instance")?.GetValue(null);
            }

            if (p.HasDefaultValue)
            {
                return p.DefaultValue;
            }

            return parameterType.IsValueType ? Activator.CreateInstance(parameterType) : null;
        }).ToArray();

        return constructor.Invoke(arguments);
    }

    /// <summary>What a value says in the Aspire manifest: its expression if it has one.</summary>
    internal static string? Expression(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string text:
                return text;
            case IFormattable formattable when value.GetType().IsPrimitive:
                return formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
        }

        if (Get(value, "ValueExpression") is string expression)
        {
            return expression;
        }

        if (Implements(value.GetType(), "IResource") && Get(value, "Name") is string name)
        {
            return "{" + name + "}";
        }

        return value.ToString();
    }

    private static bool Implements(Type type, string interfaceName) => type.GetInterfaces().Any(i => i.Name == interfaceName);

    /// <summary>A property by name, on the type or — for an explicit implementation — on one of its interfaces.</summary>
    internal static object? Get(object? target, string name)
    {
        if (target is null)
        {
            return null;
        }

        var type = target.GetType();
        // GetProperties rather than GetProperty: a property hidden with `new` is ambiguous to the latter.
        var property = type.GetProperties(Instance).FirstOrDefault(p => p.Name == name)
                       ?? type.GetInterfaces().SelectMany(i => i.GetProperties()).FirstOrDefault(p => p.Name == name);

        try
        {
            return property is { CanRead: true } && property.GetIndexParameters().Length == 0 ? property.GetValue(target) : null;
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }
}
