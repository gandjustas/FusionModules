using Customers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CustomersModule;

/// <summary>
/// The shape a service being migrated actually arrives in: a controller that takes a
/// <see cref="DbContext"/> and returns its own models.
/// </summary>
/// <remarks>
/// <para>
/// Two things here look like they should be impossible together, and the file exists to show that
/// they are not. The class is <c>internal</c>, as MOD0001 wants every type in a module to be. Its
/// constructor is <c>public</c>, as MVC's activator requires — it reads <c>GetConstructors()</c>,
/// which is public-only. And from there the constructor may take internal parameters and the
/// action may return an internal type, because C# bounds a member's effective accessibility by
/// its containing type.
/// </para>
/// <para>
/// What makes MVC find it is <c>AllowInternalControllers()</c> in <c>Module.cs</c>. Without that
/// call the stock <c>ControllerFeatureProvider</c> skips this type for not being public, the route
/// is simply absent, and nothing anywhere says so.
/// </para>
/// <para>
/// The alternative — a public controller — is what the migration is escaping. A public
/// constructor cannot take an internal service and a public action cannot return an internal
/// model, so the controller drags its dependencies and its DTOs public with it, and then whatever
/// those name, until most of the module is public and MOD0001 has bought nothing.
/// </para>
/// </remarks>
[ApiController]
internal sealed class CustomersController(DbContext db) : ControllerBase
{
    /// <summary>
    /// Reads through <see cref="DbContext"/>, never through an application context type.
    /// </summary>
    /// <remarks>
    /// This is the other half of the answer, and the half that removes the question rather than
    /// working around it. A service arriving here had its own <c>CustomersDbContext</c>, injected
    /// exactly like this — and that type would have had to be public for a public controller and
    /// internal for MOD0001. It becomes neither: the host owns one context and registers
    /// <c>AddTransient&lt;DbContext&gt;</c>, the module asks for the base type and says
    /// <c>db.Set&lt;Customer&gt;()</c>, and there is no longer a type whose visibility to argue
    /// about.
    /// </remarks>
    [HttpGet("/customers")]
    public Task<List<CustomerView>> List(CancellationToken cancellationToken) =>
        db.Set<Customer>()
            .OrderBy(customer => customer.Id)
            .Select(customer => new CustomerView(customer.Id, customer.Name, customer.Email))
            .Take(50)
            .ToListAsync(cancellationToken);
}

/// <summary>
/// Internal, and returned from a public action on an internal controller — which is legal for the
/// same reason the constructor's parameters are.
/// </summary>
internal sealed record CustomerView(int Id, string Name, string Email);
