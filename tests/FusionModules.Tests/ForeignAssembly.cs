using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Extensions.Hosting;

namespace FusionModules.Tests;

/// <summary>
/// Code a module did not write: a library's hosted service and a library's configuring delegate. The
/// runtime tells a module's own registrations from a library's by the assembly the code is declared in,
/// and every type in this test assembly is the test modules' own.
/// </summary>
internal static class ForeignAssembly
{
    private static readonly Lazy<Type> Worker = new(EmitWorker);

    /// <summary>An <see cref="IHostedService"/> declared in an assembly of its own, outside the framework.</summary>
    public static Type HostedService => Worker.Value;

    /// <summary>A configuring delegate declared in no assembly of the application — compiled at run time.</summary>
    public static Action<T> Configure<T>(string property, string value)
    {
        var options = Expression.Parameter(typeof(T), "options");
        var body = Expression.Assign(Expression.Property(options, property), Expression.Constant(value));
        return Expression.Lambda<Action<T>>(body, options).Compile();
    }

    private static Type EmitWorker()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TelemetryLibrary"), AssemblyBuilderAccess.Run);
        var type = assembly.DefineDynamicModule("TelemetryLibrary")
            .DefineType("TelemetryLibrary.TelemetryHostedService", TypeAttributes.Public | TypeAttributes.Sealed);
        type.AddInterfaceImplementation(typeof(IHostedService));
        type.DefineDefaultConstructor(MethodAttributes.Public);

        var completed = typeof(Task).GetProperty(nameof(Task.CompletedTask))!.GetMethod!;
        foreach (var name in new[] { nameof(IHostedService.StartAsync), nameof(IHostedService.StopAsync) })
        {
            var method = type.DefineMethod(name,
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                typeof(Task), [typeof(CancellationToken)]);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Call, completed);
            il.Emit(OpCodes.Ret);
            type.DefineMethodOverride(method, typeof(IHostedService).GetMethod(name)!);
        }

        return type.CreateType();
    }
}
