extern alias QuotesApiProject;

using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Moq;
using QuotesApiProject::QuotesApi.Caching;
using QuotesApiProject::QuotesApi.Controllers;
using QuotesApiProject::QuotesApi.Features.Collections;
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

        // Day 21 gave CollectionsController a cached reader and a cache
        // invalidator. Neither is on the GetById path this test exercises, so
        // they are here only to satisfy the constructor - unconfigured, so
        // that if GetById ever did start touching them the test would fail on
        // a null result rather than passing against a silent stub.
        var mockSummaries = new Mock<ICollectionSummaryReader>();
        var mockCacheInvalidator = new Mock<ICollectionSummaryCacheInvalidator>();

        var controller = new CollectionsController(
            mockMediator.Object,
            mockRepo.Object,
            mockSummaries.Object,
            mockCacheInvalidator.Object);
        
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await controller.GetById(1, cts.Token);
        });
    }
}