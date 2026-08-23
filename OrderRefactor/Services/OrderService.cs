using OrderRefactor.Models;
using OrderRefactor.Repositories;

namespace OrderRefactor.Services;

public class OrderService : IOrderService
{
    private readonly IOrderRepository _repository;
    private readonly IDiscountCalculator _discountCalculator;
    private readonly IConfiguration _config;
    private readonly ILogger<OrderService> _logger;
    private readonly IClock _clock;

    public OrderService(
        IOrderRepository repository,
        IDiscountCalculator discountCalculator,
        IConfiguration config,
        ILogger<OrderService> logger,
        IClock clock)
    {
        _repository = repository;
        _discountCalculator = discountCalculator;
        _config = config;
        _logger = logger;
        _clock = clock;
    }

    public async Task<OrderResponse> CreateOrderAsync(CreateOrderRequest request, CancellationToken ct)
    {
        var customer = await _repository.GetCustomerByEmailAsync(request.CustomerEmail, ct);

        if (customer is null)
        {
            customer = await _repository.AddCustomerAsync(new Customer
            {
                Name = request.CustomerName,
                Email = request.CustomerEmail,
                LoyaltyPoints = 0,
                IsVip = false
            }, ct);
        }

        // Fixed: foreach instead of an off-by-one indexed loop (smell #4)
        var orderItems = new List<OrderItem>();
        decimal subtotal = 0;

        foreach (var item in request.Items)
        {
            orderItems.Add(new OrderItem
            {
                ProductName = item.ProductName,
                Price = item.Price,
                Quantity = item.Quantity
            });
            subtotal += item.Price * item.Quantity;
        }

        var discountPercent = _discountCalculator.GetDiscountPercent(request.DiscountCode, customer.IsVip);
        var discountAmount = subtotal * discountPercent;
        var total = subtotal - discountAmount;

        // Fixed: safe config read with a sane default instead of an unguarded Parse (smell #5)
        var taxRate = GetTaxRate();
        total += total * taxRate;

        var pointsEarned = (int)total;
        customer.LoyaltyPoints += pointsEarned;

        var order = new Order
        {
            CustomerName = request.CustomerName,
            CustomerEmail = request.CustomerEmail,
            DiscountCode = request.DiscountCode,
            Status = "Pending",
            Items = orderItems,
            Total = total,
            // From the injected clock, not DateTime.UtcNow, so a test can assert
            // the exact value rather than a tolerance window around "now".
            CreatedAt = _clock.UtcNow.UtcDateTime
        };

        var created = await _repository.AddOrderAsync(order, ct);
        await _repository.UpdateCustomerAsync(customer, ct);

        var message = customer.IsVip ? "VIP order created" : "Order created";

        return new OrderResponse(created.Id, created.Total, message, pointsEarned, orderItems.Count);
    }

    private decimal GetTaxRate()
    {
        var raw = _config["Orders:TaxRate"];
        if (!string.IsNullOrWhiteSpace(raw) && decimal.TryParse(raw, out var parsed))
        {
            return parsed;
        }

        _logger.LogWarning("Orders:TaxRate missing or invalid in configuration, defaulting to 0");
        return 0m;
    }
}
