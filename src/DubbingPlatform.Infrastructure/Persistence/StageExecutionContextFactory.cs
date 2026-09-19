using DubbingPlatform.Application.Services;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Infrastructure.Persistence;

/// <summary>
/// Infrastructure implementation of <see cref="IStageExecutionContextFactory"/>
/// backed by <c>AppDbContext</c>. Registered alongside
/// <c>AddDbContextFactory&lt;AppDbContext&gt;</c> so Application code can create
/// short-lived contexts without referencing Infrastructure types.
/// </summary>
public sealed class StageExecutionContextFactory : IStageExecutionContextFactory
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public StageExecutionContextFactory(IDbContextFactory<AppDbContext> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    public DbContext CreateDbContext()
    {
        return _factory.CreateDbContext();
    }
}
