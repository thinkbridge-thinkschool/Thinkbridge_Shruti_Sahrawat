using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using OrderRefactor.Models;
using OrderRefactor.Repositories;
using OrderRefactor.Services;

namespace Quotes.Tests.Unit;

public class OrderServiceTests
{
    // OrderService stamps Order.CreatedAt from the injected clock now, so the
    // tests supply one instead of the service reaching for DateTime.UtcNow.
    private static readonly OrderTestClock Clock = new();

    private static CreateOrderRequest SingleItemRequest(string email = "grace@example.com", string? discountCode = null) => new()
    {
        CustomerName = "Grace Hopper",
        CustomerEmail = email,
        DiscountCode = discountCode,
        Items = new List<CreateOrderItemRequest>
        {
            new() { ProductName = "Compiler Manual", Price = 100m, Quantity = 1 }
        }
    };

    [Fact]
    public async Task CreateOrderAsync_ExistingCustomer_DoesNotCreateNewCustomer()
    {
        var repository = Substitute.For<IOrderRepository>();
        var discountCalculator = Substitute.For<IDiscountCalculator>();
        var config = Substitute.For<IConfiguration>();
        var logger = Substitute.For<ILogger<OrderService>>();
        var existingCustomer = new Customer { Id = 7, Name = "Grace Hopper", Email = "grace@example.com", IsVip = false, LoyaltyPoints = 10 };
        repository.GetCustomerByEmailAsync("grace@example.com", Arg.Any<CancellationToken>()).Returns(existingCustomer);
        repository.AddOrderAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>()).Returns(callInfo => (Order)callInfo[0]);
        discountCalculator.GetDiscountPercent(Arg.Any<string?>(), Arg.Any<bool>()).Returns(0m);
        var service = new OrderService(repository, discountCalculator, config, logger, Clock);

        await service.CreateOrderAsync(SingleItemRequest(), CancellationToken.None);

        await repository.DidNotReceive().AddCustomerAsync(Arg.Any<Customer>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateOrderAsync_UnknownCustomer_CreatesNewCustomerWithZeroLoyaltyPoints()
    {
        var repository = Substitute.For<IOrderRepository>();
        var discountCalculator = Substitute.For<IDiscountCalculator>();
        var config = Substitute.For<IConfiguration>();
        var logger = Substitute.For<ILogger<OrderService>>();
        Customer? newCustomerAsPassedIn = null;
        repository.GetCustomerByEmailAsync("new@example.com", Arg.Any<CancellationToken>()).Returns((Customer?)null);
        repository.AddCustomerAsync(Arg.Any<Customer>(), Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            var passedCustomer = (Customer)callInfo[0];
            // Snapshot the fields here: OrderService mutates this same instance's LoyaltyPoints
            // later in the method, so asserting on it after CreateOrderAsync returns would be comparing
            // against post-mutation state instead of what was actually passed to AddCustomerAsync.
            newCustomerAsPassedIn = new Customer { Name = passedCustomer.Name, Email = passedCustomer.Email, LoyaltyPoints = passedCustomer.LoyaltyPoints, IsVip = passedCustomer.IsVip };
            return passedCustomer;
        });
        repository.AddOrderAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>()).Returns(callInfo => (Order)callInfo[0]);
        discountCalculator.GetDiscountPercent(Arg.Any<string?>(), Arg.Any<bool>()).Returns(0m);
        var service = new OrderService(repository, discountCalculator, config, logger, Clock);

        await service.CreateOrderAsync(SingleItemRequest(email: "new@example.com"), CancellationToken.None);

        newCustomerAsPassedIn.Should().NotBeNull();
        newCustomerAsPassedIn!.Email.Should().Be("new@example.com");
        newCustomerAsPassedIn.LoyaltyPoints.Should().Be(0);
        newCustomerAsPassedIn.IsVip.Should().BeFalse();
    }

    [Fact]
    public async Task CreateOrderAsync_MultipleItems_SumsSubtotalAcrossItems()
    {
        var repository = Substitute.For<IOrderRepository>();
        var discountCalculator = Substitute.For<IDiscountCalculator>();
        var config = Substitute.For<IConfiguration>();
        var logger = Substitute.For<ILogger<OrderService>>();
        var customer = new Customer { Id = 1, Email = "grace@example.com", IsVip = false, LoyaltyPoints = 0 };
        repository.GetCustomerByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(customer);
        repository.AddOrderAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>()).Returns(callInfo => (Order)callInfo[0]);
        discountCalculator.GetDiscountPercent(Arg.Any<string?>(), Arg.Any<bool>()).Returns(0m);
        var service = new OrderService(repository, discountCalculator, config, logger, Clock);
        var request = new CreateOrderRequest
        {
            CustomerName = "Grace Hopper",
            CustomerEmail = "grace@example.com",
            Items = new List<CreateOrderItemRequest>
            {
                new() { ProductName = "Compiler Manual", Price = 25.00m, Quantity = 2 },
                new() { ProductName = "Debugging Kit", Price = 15.50m, Quantity = 1 }
            }
        };

        var result = await service.CreateOrderAsync(request, CancellationToken.None);

        result.Total.Should().Be(65.50m);
    }

    [Fact]
    public async Task CreateOrderAsync_WithDiscountApplied_ReducesTotal()
    {
        var repository = Substitute.For<IOrderRepository>();
        var discountCalculator = Substitute.For<IDiscountCalculator>();
        var config = Substitute.For<IConfiguration>();
        var logger = Substitute.For<ILogger<OrderService>>();
        var customer = new Customer { Id = 1, Email = "grace@example.com", IsVip = false, LoyaltyPoints = 0 };
        repository.GetCustomerByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(customer);
        repository.AddOrderAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>()).Returns(callInfo => (Order)callInfo[0]);
        discountCalculator.GetDiscountPercent("SAVE10", false).Returns(0.10m);
        var service = new OrderService(repository, discountCalculator, config, logger, Clock);

        var result = await service.CreateOrderAsync(SingleItemRequest(discountCode: "SAVE10"), CancellationToken.None);

        result.Total.Should().Be(90m);
    }

    [Fact]
    public async Task CreateOrderAsync_WithConfiguredTaxRate_AddsTaxToTotal()
    {
        var repository = Substitute.For<IOrderRepository>();
        var discountCalculator = Substitute.For<IDiscountCalculator>();
        var config = Substitute.For<IConfiguration>();
        var logger = Substitute.For<ILogger<OrderService>>();
        var customer = new Customer { Id = 1, Email = "grace@example.com", IsVip = false, LoyaltyPoints = 0 };
        repository.GetCustomerByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(customer);
        repository.AddOrderAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>()).Returns(callInfo => (Order)callInfo[0]);
        discountCalculator.GetDiscountPercent(Arg.Any<string?>(), Arg.Any<bool>()).Returns(0m);
        config["Orders:TaxRate"].Returns("0.10");
        var service = new OrderService(repository, discountCalculator, config, logger, Clock);

        var result = await service.CreateOrderAsync(SingleItemRequest(), CancellationToken.None);

        result.Total.Should().Be(110m);
    }

    [Fact]
    public async Task CreateOrderAsync_WithMissingTaxRateConfig_DefaultsToZeroTax()
    {
        var repository = Substitute.For<IOrderRepository>();
        var discountCalculator = Substitute.For<IDiscountCalculator>();
        var config = Substitute.For<IConfiguration>();
        var logger = Substitute.For<ILogger<OrderService>>();
        var customer = new Customer { Id = 1, Email = "grace@example.com", IsVip = false, LoyaltyPoints = 0 };
        repository.GetCustomerByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(customer);
        repository.AddOrderAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>()).Returns(callInfo => (Order)callInfo[0]);
        discountCalculator.GetDiscountPercent(Arg.Any<string?>(), Arg.Any<bool>()).Returns(0m);
        config["Orders:TaxRate"].Returns((string?)null);
        var service = new OrderService(repository, discountCalculator, config, logger, Clock);

        var result = await service.CreateOrderAsync(SingleItemRequest(), CancellationToken.None);

        result.Total.Should().Be(100m);
    }

    [Fact]
    public async Task CreateOrderAsync_WithUnparsableTaxRateConfig_DefaultsToZeroTax()
    {
        var repository = Substitute.For<IOrderRepository>();
        var discountCalculator = Substitute.For<IDiscountCalculator>();
        var config = Substitute.For<IConfiguration>();
        var logger = Substitute.For<ILogger<OrderService>>();
        var customer = new Customer { Id = 1, Email = "grace@example.com", IsVip = false, LoyaltyPoints = 0 };
        repository.GetCustomerByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(customer);
        repository.AddOrderAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>()).Returns(callInfo => (Order)callInfo[0]);
        discountCalculator.GetDiscountPercent(Arg.Any<string?>(), Arg.Any<bool>()).Returns(0m);
        config["Orders:TaxRate"].Returns("not-a-number");
        var service = new OrderService(repository, discountCalculator, config, logger, Clock);

        var result = await service.CreateOrderAsync(SingleItemRequest(), CancellationToken.None);

        result.Total.Should().Be(100m);
    }

    [Fact]
    public async Task CreateOrderAsync_VipCustomer_ReturnsVipMessage()
    {
        var repository = Substitute.For<IOrderRepository>();
        var discountCalculator = Substitute.For<IDiscountCalculator>();
        var config = Substitute.For<IConfiguration>();
        var logger = Substitute.For<ILogger<OrderService>>();
        var customer = new Customer { Id = 1, Email = "grace@example.com", IsVip = true, LoyaltyPoints = 0 };
        repository.GetCustomerByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(customer);
        repository.AddOrderAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>()).Returns(callInfo => (Order)callInfo[0]);
        discountCalculator.GetDiscountPercent(Arg.Any<string?>(), Arg.Any<bool>()).Returns(0m);
        var service = new OrderService(repository, discountCalculator, config, logger, Clock);

        var result = await service.CreateOrderAsync(SingleItemRequest(), CancellationToken.None);

        result.Message.Should().Be("VIP order created");
    }

    [Fact]
    public async Task CreateOrderAsync_NonVipCustomer_ReturnsRegularMessage()
    {
        var repository = Substitute.For<IOrderRepository>();
        var discountCalculator = Substitute.For<IDiscountCalculator>();
        var config = Substitute.For<IConfiguration>();
        var logger = Substitute.For<ILogger<OrderService>>();
        var customer = new Customer { Id = 1, Email = "grace@example.com", IsVip = false, LoyaltyPoints = 0 };
        repository.GetCustomerByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(customer);
        repository.AddOrderAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>()).Returns(callInfo => (Order)callInfo[0]);
        discountCalculator.GetDiscountPercent(Arg.Any<string?>(), Arg.Any<bool>()).Returns(0m);
        var service = new OrderService(repository, discountCalculator, config, logger, Clock);

        var result = await service.CreateOrderAsync(SingleItemRequest(), CancellationToken.None);

        result.Message.Should().Be("Order created");
    }

    [Fact]
    public async Task CreateOrderAsync_ComputesPointsEarnedAsIntegerTotalAndCreditsCustomer()
    {
        var repository = Substitute.For<IOrderRepository>();
        var discountCalculator = Substitute.For<IDiscountCalculator>();
        var config = Substitute.For<IConfiguration>();
        var logger = Substitute.For<ILogger<OrderService>>();
        var customer = new Customer { Id = 1, Email = "grace@example.com", IsVip = false, LoyaltyPoints = 5 };
        repository.GetCustomerByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(customer);
        repository.AddOrderAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>()).Returns(callInfo => (Order)callInfo[0]);
        discountCalculator.GetDiscountPercent(Arg.Any<string?>(), Arg.Any<bool>()).Returns(0m);
        var service = new OrderService(repository, discountCalculator, config, logger, Clock);

        var result = await service.CreateOrderAsync(SingleItemRequest(), CancellationToken.None);

        result.PointsEarned.Should().Be(100);
        customer.LoyaltyPoints.Should().Be(105);
    }

    [Fact]
    public async Task CreateOrderAsync_ReturnsItemCountMatchingRequestedItems()
    {
        var repository = Substitute.For<IOrderRepository>();
        var discountCalculator = Substitute.For<IDiscountCalculator>();
        var config = Substitute.For<IConfiguration>();
        var logger = Substitute.For<ILogger<OrderService>>();
        var customer = new Customer { Id = 1, Email = "grace@example.com", IsVip = false, LoyaltyPoints = 0 };
        repository.GetCustomerByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(customer);
        repository.AddOrderAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>()).Returns(callInfo => (Order)callInfo[0]);
        discountCalculator.GetDiscountPercent(Arg.Any<string?>(), Arg.Any<bool>()).Returns(0m);
        var service = new OrderService(repository, discountCalculator, config, logger, Clock);
        var request = new CreateOrderRequest
        {
            CustomerName = "Grace Hopper",
            CustomerEmail = "grace@example.com",
            Items = new List<CreateOrderItemRequest>
            {
                new() { ProductName = "Compiler Manual", Price = 25.00m, Quantity = 2 },
                new() { ProductName = "Debugging Kit", Price = 15.50m, Quantity = 1 }
            }
        };

        var result = await service.CreateOrderAsync(request, CancellationToken.None);

        result.ItemCount.Should().Be(2);
    }
}
