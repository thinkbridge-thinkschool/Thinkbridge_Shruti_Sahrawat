namespace OrderRefactor.Models;

public class Order
{
    public int Id { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    public List<OrderItem> Items { get; set; } = new();
    public decimal Total { get; set; }
    public string Status { get; set; } = "Pending";

    // No `= DateTime.UtcNow` default. A property initialiser that reads the
    // ambient clock fires on every materialisation, including when EF rehydrates
    // a row, and it silently overrides anything a test wants to pin. OrderService
    // sets this from the injected IClock instead.
    public DateTime CreatedAt { get; set; }

    public string? DiscountCode { get; set; }
}

public class OrderItem
{
    public int Id { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int Quantity { get; set; }
    public int OrderId { get; set; }
}

public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public int LoyaltyPoints { get; set; }
    public bool IsVip { get; set; }
}
