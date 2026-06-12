namespace CheckoutDemo;

public record LineItem(string Name, decimal Price, int Quantity);

public static class Checkout
{
    public static decimal Subtotal(IEnumerable<LineItem> items) =>
        items.Sum(i => i.Price * i.Quantity);

    // BUG (deliberate): the tax rate is typed as a string, so it can't be multiplied by a decimal.
    // Roslyn flags GrandTotal below with CS0019. The clean fix is: decimal TaxRate = 0.08m;
    private static readonly string TaxRate = "0.08";

    public static decimal GrandTotal(IEnumerable<LineItem> items)
    {
        var subtotal = Subtotal(items);
        return subtotal + subtotal * TaxRate; // CS0019: operator '*' can't be applied to 'decimal' and 'string'
    }
}

public static class Program
{
    public static void Main()
    {
        var cart = new List<LineItem>
        {
            new("Widget", 9.99m, 3),
            new("Gadget", 19.95m, 1),
        };

        Console.WriteLine($"Order total: {Checkout.GrandTotal(cart):C}");
    }
}
