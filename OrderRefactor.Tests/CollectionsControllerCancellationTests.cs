extern alias QuotesApiProject;

using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Moq;
using QuotesApiProject::QuotesApi.Controllers;
using QuotesApiProject::QuotesApi.Repositories;
using Xunit;

public class CollectionsControllerCancellationTests
{
    [Fact]
    public async Task GetById_WhenTokenIsCancelled_ThrowsOrCancelsOperation()
    {
        // Arrange
        var mockRepo = new Mock<ICollectionRepository>();
        
        mockRepo.Setup(r => r.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<int, CancellationToken>((id, token) => 
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult<QuotesApiProject::QuotesApi.Domain.Collection?>(null);
            });

        var mockMediator = new Mock<IMediator>();

        var controller = new CollectionsController(mockMediator.Object, mockRepo.Object);
        
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await controller.GetById(1, cts.Token);
        });
    }
}