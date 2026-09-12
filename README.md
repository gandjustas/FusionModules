# Modulith

A modular monolith for ASP.NET Core — with no framework.

A module is an ordinary class library. It is activated by naming its assembly in
`HOSTINGSTARTUPASSEMBLIES`. One image, any deployment topology, chosen by an environment
variable rather than a rebuild. ASP.NET Core has had every mechanism for this for years;
this package is one base class over them, plus the analyzers that stop the architecture
from leaking.

```csharp
// OrdersModule/Module.cs — the whole module contract
[assembly: HostingStartup(typeof(Module))]

class Module : ModuleBase
{
    protected override void ConfigureServices(WebHostBuilderContext context, IServiceCollection services)
        => services.AddScoped<IOrderService, OrderService>();

    protected override void Configure(IApplicationBuilder app)
        => app.UseEndpoints(e => e.MapGroup("/orders").MapGet("/unpaid", (IOrderService s) => s.Unpaid()));
}
```

```csharp
// the host — note that it knows nothing about any module
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.UseRouting();
app.Run();
```

```yaml
# same image, three services
orders:    { environment: { HOSTINGSTARTUPASSEMBLIES: "Orders.Entities;OrdersModule" } }
customers: { environment: { HOSTINGSTARTUPASSEMBLIES: "Customers.Entities;CustomersModule" } }
monolith:  { environment: { HOSTINGSTARTUPASSEMBLIES: "Orders.Entities;Customers.Entities;Monolith" } }
```

## Install

```
dotnet add package Modulith
```

That is the whole dependency. The analyzers come with it.

## Status

Early development, pre-1.0. The API surface is not yet locked.

## Origin

Extracted from the DotNext talk *«Модульность без микросервисов»*
([sources](https://github.com/gandjustas/dotnext-2026)).

## License

MIT
