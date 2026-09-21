// One host, and every shape of the pricing edge is a choice of module names:
//
//   CheckoutModule;PricingModule                    the edge collapsed — a method call
//   CheckoutModule;PricingClientModule              the edge kept — an HTTP call
//   PricingModule;PricingApiModule                  the pricing service, published
//   CheckoutModule;PricingModule;PricingApiModule   merged, and still reachable from outside

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.UseRouting();
app.MapGet("/", () => "host");

await app.RunAsync();

// Integration tests need a handle on the entry point.
public partial class Program;
