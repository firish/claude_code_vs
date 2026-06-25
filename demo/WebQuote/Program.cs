// WebQuote - a tiny ASP.NET Core API whose only job is to test ATTACH + break-on-thrown against a
// *running* web app (the real-app case you can't F5: you attach to the live process). GET /quote/{id}
// prices an order; order 103 has a null Customer, so Pricing.QuoteFor throws a NullReferenceException
// deep in the request handler - and a generic catch turns it into a bland 500, hiding WHERE/WHY it threw.
// Attach to this process, enable break-on-thrown for System.NullReferenceException, then hit /quote/103:
// VS breaks at the THROW site (Pricing.QuoteFor, with the null-customer order in scope), not at the
// swallowing catch. See README.md for the full walkthrough (incl. Claude triggering the request itself).

using WebQuote;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// In-memory "orders" - order 103 has a null Customer (mirrors the NullOrigin console fixture, web-ified).
var orders = new Dictionary<int, Order>
{
    [101] = new Order { Id = 101, Customer = new Customer { Name = "Acme",    Tier = "Gold"   } },
    [102] = new Order { Id = 102, Customer = new Customer { Name = "Globex",  Tier = "Silver" } },
    [103] = new Order { Id = 103, Customer = null },   // the landmine: null customer
    [104] = new Order { Id = 104, Customer = new Customer { Name = "Initech", Tier = "Gold"   } },
};

app.MapGet("/", () => "WebQuote up. Try /quote/101 (ok), /quote/102 (ok), /quote/103 (the bug).");

app.MapGet("/quote/{id:int}", (int id) =>
{
    if (!orders.TryGetValue(id, out var order))
        return Results.NotFound($"no order {id}");
    try
    {
        var price = Pricing.QuoteFor(order);
        return Results.Ok(new { order = id, price });
    }
    catch (Exception e)
    {
        // Swallows the real cause into a generic 500 - the "invisible in the output" part. The status
        // code tells you /quote/103 is broken, but not that it's a null Customer in Pricing.QuoteFor.
        return Results.Problem($"could not price order {id}: {e.GetType().Name}");
    }
});

app.Run("http://localhost:5179"); // fixed port so the README's curl commands are exact

namespace WebQuote
{
    public sealed class Customer
    {
        public string Name { get; set; } = "";
        public string Tier { get; set; } = "";
    }

    public sealed class Order
    {
        public int Id { get; set; }
        public Customer Customer { get; set; }   // null for some orders
    }

    public static class Pricing
    {
        private const decimal Base = 100m;

        public static decimal QuoteFor(Order order)
        {
            // NRE here when Customer is null (order 103). The throw site we want break-on-thrown to land on.
            decimal discount = order.Customer.Tier == "Gold" ? 0.20m : 0m;
            return Base * (1 - discount);
        }
    }
}
