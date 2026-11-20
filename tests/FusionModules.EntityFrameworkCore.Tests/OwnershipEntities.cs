// The entities of TableOwnershipTests' modules. No IEntityTypeConfiguration here: this assembly is a
// module, and every test that names it would apply such a class.
namespace Ownership.Orders
{
    public sealed class Order
    {
        public int Id { get; set; }
    }

    public sealed class OrderDetails
    {
        public int Id { get; set; }

        public string? Notes { get; set; }
    }

    public sealed class Note
    {
        public int Id { get; set; }
    }

    [System.ComponentModel.DataAnnotations.Schema.Table("Invoices")]
    public sealed class Invoice
    {
        public int Id { get; set; }
    }
}

namespace Ownership.Billing
{
    public sealed class Order
    {
        public int Id { get; set; }
    }

    public sealed class Note
    {
        public int Id { get; set; }
    }
}

namespace Ownership.Shared
{
    public sealed class Order
    {
        public int Id { get; set; }

        public int CustomerId { get; set; }
    }

    public sealed class Customer
    {
        public int Id { get; set; }

        public Address Address { get; set; } = new();
    }

    public sealed class Address
    {
        public string? Street { get; set; }
    }

    public class Payment
    {
        public int Id { get; set; }
    }

    public sealed class CardPayment : Payment
    {
        public string? Last4 { get; set; }
    }
}
