using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Customers;

// Public, and MOD0001 allows it because of the configuration below: EF Core reaches entity types
// by reflection, and the configuration in the same assembly is the evidence that this is one.
public class Customer
{
    public int Id { get; set; }

    [MaxLength(100)]
    public required string Name { get; set; }

    [MaxLength(200)]
    [EmailAddress]
    public required string Email { get; set; }
}

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> entity) =>
        // A schema per module keeps table names from colliding and makes ownership obvious in
        // the database itself.
        entity.ToTable("Customers", "Marketing");
}
