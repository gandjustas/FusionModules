using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Orders;

public class Order
{
    public int Id { get; set; }

    // A plain integer, not a navigation property. This module knows there is a customer; it does
    // not know there is a Customers module, and in a topology without one there is no foreign key
    // — see the Billing module, which is what adds the relationship.
    public int CustomerId { get; set; }

    public DateTimeOffset PlacedOn { get; set; }

    public DateTimeOffset? PaidOn { get; set; }

    public decimal Amount { get; set; }
}

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> entity)
    {
        entity.ToTable("Orders", "Sales");
        entity.Property(o => o.Amount).HasPrecision(18, 2);
    }
}
