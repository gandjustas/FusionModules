using Customers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Orders;

namespace BillingModule;

/// <summary>
/// A second <see cref="IEntityTypeConfiguration{T}"/> for <see cref="Order"/>, in a different
/// module from the first.
/// </summary>
/// <remarks>
/// This is the whole trick of the modular data model. Orders.Entities declares the table; this
/// module declares its relationship to another module's table. EF Core applies configurations in
/// activation order, so the composing module must come last in HOSTINGSTARTUPASSEMBLIES.
/// <para>
/// The alternative — a navigation property on Order pointing at Customer — would make Orders
/// depend on Customers in every topology, which is the coupling the split exists to avoid.
/// </para>
/// </remarks>
internal sealed class OrderCustomerConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> entity) =>
        entity.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(order => order.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);
}
