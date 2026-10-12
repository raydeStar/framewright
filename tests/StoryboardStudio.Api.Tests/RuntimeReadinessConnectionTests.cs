using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using StoryboardStudio.Api.Persistence;
using StoryboardStudio.Api.Services;

namespace StoryboardStudio.Api.Tests;

public sealed class RuntimeReadinessConnectionTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task InspectionRestoresTheConnectionStateItFound(
        bool callerOpenedConnection, bool callerStartedTransaction, bool migrationMissing)
    {
        using var factory = new StudioApiFactory();
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StudioDbContext>();
        var readiness = scope.ServiceProvider.GetRequiredService<RuntimeReadinessService>();
        var connection = db.Database.GetDbConnection();
        if (migrationMissing)
            Assert.Equal(1, await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM \"SchemaMigrations\" WHERE \"Id\" = '20260830-ratified-shot-intent-v5';"));
        if (callerOpenedConnection) await connection.OpenAsync();
        await using var transaction = callerStartedTransaction ? await db.Database.BeginTransactionAsync() : null;
        var originalState = connection.State;

        var result = await readiness.InspectAsync(CancellationToken.None);

        var database = Assert.Single(result.Checks, check => check.Id == "database");
        Assert.Equal(migrationMissing ? "Failed" : "Ready", database.State);
        if (migrationMissing) Assert.Equal("NotReady", result.Status);
        Assert.Equal(callerOpenedConnection ? ConnectionState.Open : ConnectionState.Closed, originalState);
        Assert.Equal(originalState, connection.State);
        Assert.Equal(callerStartedTransaction, db.Database.CurrentTransaction is not null);
        if (transaction is not null) Assert.NotNull(transaction.GetDbTransaction().Connection);
    }
}
