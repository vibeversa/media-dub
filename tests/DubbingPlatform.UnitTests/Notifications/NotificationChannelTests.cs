// Task 049: notification channel abstraction contract.
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Notifications;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using DubbingPlatform.Infrastructure.Messaging;
using DubbingPlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DubbingPlatform.UnitTests.Notifications;

/// <summary>
/// The outbox channel seam (Task 049): persist-then-publish ordering, the
/// per-channel idempotency rule, and the closed channel vocabulary.
/// <para>
/// Everything here is hermetic. Projection runs through the real
/// <see cref="NotificationProjector"/> over an in-process InMemory
/// <see cref="AppDbContext"/> with a recording channel substituted for the
/// in-app one, so the ordering assertions are made against stored rows rather
/// than against a mock's opinion of them. No containers, no network, no database
/// server.
/// </para>
/// <para>
/// R2 ("exactly one active channel, zero external sends") is asserted three
/// ways, because one of them would pass if the other two were lying: by
/// reflection over every product assembly (one concrete publisher exists), by a
/// comment-stripped scan of <c>src/**</c> (no mail/webhook client is reachable),
/// and by a scan of both host registrations (only the in-app channel is wired).
/// </para>
/// </summary>
public sealed class NotificationChannelTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    /// <summary>
    /// Code-shaped markers of an external send. Each is a token that only appears
    /// in code reaching for a mail or webhook transport; none of them is a word
    /// that also appears in this repository's prose.
    /// </summary>
    private static readonly string[] ExternalDeliveryTokens =
    [
        "System.Net.Mail",
        "SmtpClient",
        "MailKit",
        "MimeKit",
        "SendGrid",
        "EmailSender",
        "EmailClient",
        "IEmail",
        "WebhookSender",
        "WebhookClient",
        "IWebhook",
    ];

    // ---------------------------------------------------------------- R1: order

    [Fact]
    public async Task ProjectAsync_CommitsTheRow_BeforeAnyChannelIsCalled()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedProject(factory, tenantId, projectId, recipient);

        // The channel counts rows through its own context at the moment it is
        // called. Seeing the row proves SaveChangesAsync already committed it, so
        // a publisher can never observe, or fail to erase, an uncommitted row.
        var visibleWhenPublished = -1;
        var channel = new RecordingPublisher(NotificationChannels.InApp)
        {
            OnPublish = _ =>
            {
                visibleWhenPublished = CountAll(factory);
                return Task.CompletedTask;
            }
        };
        var projector = CreateProjector(factory, channel);

        var created = await projector.ProjectAsync(Input(tenantId, projectId, Guid.NewGuid()));

        Assert.Single(created);
        Assert.Equal(1, channel.PublishCount);
        Assert.Equal(1, visibleWhenPublished);
    }

    [Fact]
    public async Task ProjectAsync_PublishesEveryRecipient_OncePerNewRow()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var member = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedProject(factory, tenantId, projectId, owner);
        SeedMembership(factory, tenantId, projectId, member, ProjectRole.Reviewer);
        var channel = new RecordingPublisher(NotificationChannels.InApp);
        var projector = CreateProjector(factory, channel);

        var created = await projector.ProjectAsync(Input(tenantId, projectId, Guid.NewGuid()));

        Assert.Equal(2, created.Count);
        Assert.Equal(2, channel.PublishCount);
        Assert.Equal(
            created.Select(n => n.Id).OrderBy(id => id),
            channel.Published.Select(n => n.Id).OrderBy(id => id));
        Assert.All(channel.Published, n => Assert.Equal(tenantId, n.TenantId));
    }

    [Fact]
    public async Task ProjectAsync_WithNoRecipients_PublishesNothing()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedUser(factory, tenantId, Guid.NewGuid(), TenantUserStatus.Disabled);
        var channel = new RecordingPublisher(NotificationChannels.InApp);
        var projector = CreateProjector(factory, channel);

        Assert.Empty(await projector.ProjectAsync(Input(tenantId, projectId, Guid.NewGuid())));
        Assert.Empty(await projector.ProjectAsync(new NotificationInput(
            tenantId, null, NotificationType.QuotaWarning, NotificationSeverity.Warning,
            "Quota warning", "Quota 'storage' is near its limit.",
            "Tenant", tenantId.ToString("N"), Guid.NewGuid(), Now.AddDays(30))));

        Assert.Equal(0, channel.PublishCount);
        Assert.Equal(0, CountAll(factory));
    }

    // ------------------------------------------------------- R4: dedup rules

    [Fact]
    public async Task DuplicateSourceEvent_StoresOneRow_AndPublishesExactlyOnce()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        var sourceEventId = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedProject(factory, tenantId, projectId, recipient);
        var channel = new RecordingPublisher(NotificationChannels.InApp);
        var projector = CreateProjector(factory, channel);
        var input = Input(tenantId, projectId, sourceEventId);

        var first = await projector.ProjectAsync(input);
        var second = await projector.ProjectAsync(input);

        // Instruction 4's contract: one row, one in-app publish, and the second
        // publish attempt is deduplicated rather than rejected.
        Assert.Single(first);
        Assert.Single(second);
        Assert.Equal(first[0].Id, second[0].Id);
        Assert.Equal(1, CountAll(factory));
        Assert.Equal(1, channel.PublishCount);
        Assert.Equal(first[0].Id, Assert.Single(channel.Published).Id);
        Assert.Equal(sourceEventId, second[0].SourceEventId);
    }

    [Fact]
    public async Task DuplicateSourceEvent_AcrossFiveRedeliveries_StillPublishesOnce()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        var sourceEventId = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedProject(factory, tenantId, projectId, recipient);
        var channel = new RecordingPublisher(NotificationChannels.InApp);

        // A brand-new projector and contexts each time, as a restart would.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var projector = CreateProjector(factory, channel);
            var projected = await projector.ProjectAsync(Input(tenantId, projectId, sourceEventId));
            Assert.Single(projected);
        }

        Assert.Equal(1, channel.PublishCount);
        Assert.Equal(1, CountAll(factory));
    }

    [Fact]
    public async Task DuplicateSourceEvent_WithoutADedupKey_AppendsAndPublishes_EachTime()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedProject(factory, tenantId, projectId, recipient);
        var channel = new RecordingPublisher(NotificationChannels.InApp);
        var projector = CreateProjector(factory, channel);

        var first = await projector.ProjectAsync(Input(tenantId, projectId, sourceEventId: null));
        var second = await projector.ProjectAsync(Input(tenantId, projectId, sourceEventId: null));

        // Dedup is opt-in: with no SourceEventId every delivery is new, so both
        // the row and the publish are appended. The idempotency key degrades to
        // the notification id (see IdempotencyKey_Reuses...).
        Assert.NotEqual(first[0].Id, second[0].Id);
        Assert.Equal(2, channel.PublishCount);
        Assert.Equal(2, CountAll(factory));
    }

    [Fact]
    public void IdempotencyKey_ReusesSourceEventId_ScopedByChannel()
    {
        var sourceEventId = Guid.NewGuid();
        var withSource = NewNotification(sourceEventId: sourceEventId);
        var publisher = new RecordingPublisher(NotificationChannels.InApp);

        // The default interface member is the documented rule, inherited rather
        // than reimplemented: a new channel author cannot forget it.
        var key = ((INotificationChannelPublisher)publisher).IdempotencyKey(withSource);

        Assert.Equal($"in-app:{sourceEventId.ToString("N")}", key);
        Assert.Equal(NotificationChannels.IdempotencyKey(NotificationChannels.InApp, withSource), key);
        Assert.Contains(NotificationChannels.InApp, key, StringComparison.Ordinal);
        Assert.Contains(sourceEventId.ToString("N"), key, StringComparison.Ordinal);
    }

    [Fact]
    public void IdempotencyKey_FallsBackToTheNotificationId_WhenDedupIsDisabled()
    {
        var publisher = new RecordingPublisher(NotificationChannels.InApp);
        var withoutSource = NewNotification(sourceEventId: null);

        var key = ((INotificationChannelPublisher)publisher).IdempotencyKey(withoutSource);

        Assert.Equal($"in-app:notification:{withoutSource.Id.ToString("N")}", key);
        // Two rows for the same tenant/type/resource stay distinguishable.
        Assert.NotEqual(key, ((INotificationChannelPublisher)publisher).IdempotencyKey(NewNotification(sourceEventId: null)));
    }

    [Fact]
    public void IdempotencyKey_RejectsAnUnknownChannel()
    {
        Assert.Throws<DomainException>(
            () => NotificationChannels.IdempotencyKey("smtp", NewNotification()));
        Assert.Throws<DomainException>(
            () => NotificationChannels.IdempotencyKey(string.Empty, NewNotification()));
    }

    // ------------------------------------------------ R2: one channel, no send

    [Fact]
    public void InApp_Is_TheOnlyImplementedChannel_InEveryProductAssembly()
    {
        var implementations = ProductAssemblies()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => typeof(INotificationChannelPublisher).IsAssignableFrom(type))
            .Where(type => type is { IsInterface: false, IsAbstract: false })
            .Select(type => type.FullName!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        // R2: exactly one active channel. A second implementation (an email or
        // webhook publisher) fails here before it can be registered.
        Assert.Equal(new[] { typeof(InAppChannelPublisher).FullName }, implementations);
    }

    [Fact]
    public void NoMailOrWebhookClient_IsReachable_FromProductCode()
    {
        var root = RepoRoot();
        var src = Path.Combine(root, "src");
        var files = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories);
        var offenders = new List<string>();

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file);
            var code = StripComments(File.ReadAllText(file));
            foreach (var token in ExternalDeliveryTokens)
            {
                if (code.Contains(token, StringComparison.Ordinal))
                {
                    offenders.Add($"{relative}: {token}");
                }
            }
        }

        // The scan must actually cover the product, or "zero offenders" would be
        // a statement about an empty file list.
        Assert.True(files.Length > 100, $"Expected the product source tree, scanned {files.Length} files.");
        Assert.Contains(files, f => f.EndsWith(
            Path.Combine("Notifications", "InAppChannelPublisher.cs"), StringComparison.Ordinal));

        // R2: zero external sends. The scan is comment-stripped so a sentence
        // that explains why these types are absent is not itself an offence.
        Assert.Empty(offenders);
    }

    [Fact]
    public void NoMailOrWebhookPackage_IsDeclared_ByAnyProductProject()
    {
        var root = RepoRoot();
        var projects = Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories);
        var offenders = new List<string>();

        foreach (var file in projects)
        {
            var text = File.ReadAllText(file);
            foreach (var token in ExternalDeliveryTokens)
            {
                if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add($"{Path.GetRelativePath(root, file)}: {token}");
                }
            }
        }

        Assert.Equal(6, projects.Length);
        Assert.Empty(offenders);
    }

    [Fact]
    public void TheRegisteredGraph_Resolves()
    {
        // These four registrations must stay identical to the ones in both hosts;
        // BothHosts_RegisterTheInAppChannel_AndOnlyIt pins the host side by
        // reading Program.cs, and this proves the graph actually composes —
        // including the IEnumerable<INotificationChannelPublisher> injection and
        // the interface-to-implementation alias. It is here as well as in the
        // HTTP tier because the HTTP tier needs a database.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IStageExecutionContextFactory>(new TestContextFactory(CreateOptions()));
        services.AddScoped<InAppChannelPublisher>();
        services.AddScoped<INotificationChannelPublisher>(
            provider => provider.GetRequiredService<InAppChannelPublisher>());
        services.AddScoped<NotificationChannelDispatcher>();
        services.AddScoped<NotificationProjector>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var dispatcher = scope.ServiceProvider.GetRequiredService<NotificationChannelDispatcher>();
        Assert.Equal(new[] { NotificationChannels.InApp }, dispatcher.ActiveChannels);
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<NotificationProjector>());
        Assert.Single(scope.ServiceProvider.GetServices<INotificationChannelPublisher>());
    }

    [Theory]
    [InlineData("DubbingPlatform.Api")]
    [InlineData("DubbingPlatform.Workers")]
    public void BothHosts_RegisterTheInAppChannel_AndOnlyIt(string host)
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "src", host, "Program.cs"));
        var registrations = Regex.Matches(
            program,
            @"AddScoped<[^>]*INotificationChannelPublisher[^>]*>",
            RegexOptions.CultureInvariant);

        // Exactly one publisher registration per host, and it is the in-app one.
        // Adding a channel means adding a line here, which is visible here.
        Assert.Single(registrations);
        Assert.Contains("InAppChannelPublisher", program, StringComparison.Ordinal);
        Assert.Contains("NotificationChannelDispatcher", program, StringComparison.Ordinal);
    }

    // ---------------------------------------------------- the closed vocabulary

    [Fact]
    public void ChannelVocabulary_IsClosed_AndInAppIsFirst()
    {
        Assert.Equal([NotificationChannels.InApp], NotificationChannels.All);
        Assert.Equal("in-app", NotificationChannels.InApp);
        Assert.True(NotificationChannels.IsKnown(NotificationChannels.InApp));
        Assert.False(NotificationChannels.IsKnown("email"));
        Assert.False(NotificationChannels.IsKnown("webhook"));
        Assert.False(NotificationChannels.IsKnown(string.Empty));
        Assert.False(NotificationChannels.IsKnown(null));
        // Trim-tolerant, so a configuration value with stray whitespace still
        // resolves to the same channel rather than becoming a second one.
        Assert.Equal(NotificationChannels.InApp, NotificationChannels.Normalize("  in-app "));
    }

    [Fact]
    public void UnknownChannelKey_IsAValidationError()
    {
        // The edge case: an unknown future channel is rejected loudly, never
        // silently skipped.
        var ex = Assert.Throws<DomainException>(() => new NotificationChannelDispatcher(
            [new InAppChannelPublisher(NullLoggerFor<InAppChannelPublisher>()), new RecordingPublisher("email")],
            NullLoggerFor<NotificationChannelDispatcher>()));

        Assert.Contains("email", ex.Message, StringComparison.Ordinal);
        Assert.Contains("in-app", ex.Message, StringComparison.Ordinal);
        Assert.Throws<DomainException>(() => NotificationChannels.Normalize("  "));
        Assert.Throws<DomainException>(() => NotificationChannels.Normalize(null));
    }

    [Fact]
    public void MissingInAppChannel_IsRejected_NotSilentlyDropped()
    {
        // In-app is the channel that must never be dropped, so a registration set
        // without it fails at construction instead of at delivery.
        var ex = Assert.Throws<DomainException>(
            () => new NotificationChannelDispatcher([new RecordingPublisher("email")], NullLoggerFor<NotificationChannelDispatcher>()));

        Assert.Contains("in-app", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyOrDuplicateOrNullChannelRegistrations_AreRejected()
    {
        Assert.Throws<DomainException>(
            () => new NotificationChannelDispatcher([], NullLoggerFor<NotificationChannelDispatcher>()));
        Assert.Throws<DomainException>(() => new NotificationChannelDispatcher(
            [new InAppChannelPublisher(NullLoggerFor<InAppChannelPublisher>()), new RecordingPublisher(NotificationChannels.InApp)],
            NullLoggerFor<NotificationChannelDispatcher>()));
        Assert.Throws<ArgumentException>(
            () => new NotificationChannelDispatcher([null!], NullLoggerFor<NotificationChannelDispatcher>()));
        Assert.Throws<ArgumentNullException>(() => new NotificationChannelDispatcher(null!, NullLoggerFor<NotificationChannelDispatcher>()));
        Assert.Throws<ArgumentNullException>(
            () => new NotificationChannelDispatcher([], null!));
    }

    [Fact]
    public async Task Dispatch_PublishesIn_FrozenVocabularyOrder_InAppFirst()
    {
        var inApp = new RecordingPublisher(NotificationChannels.InApp);
        var dispatcher = new NotificationChannelDispatcher(
            [inApp],
            NullLoggerFor<NotificationChannelDispatcher>());

        await dispatcher.DispatchAsync(NewNotification());

        // The sequence is NotificationChannels.All order, not registration
        // order, so the channel that must never be dropped is always attempted
        // first. With one channel that list is in-app alone; when a second key is
        // added to All, ActiveChannels grows with it and in-app stays at index 0.
        Assert.Equal(NotificationChannels.InApp, NotificationChannels.All[0]);
        Assert.Equal(new[] { NotificationChannels.InApp }, dispatcher.ActiveChannels);
        Assert.Equal(1, inApp.PublishCount);
    }

    [Fact]
    public async Task Dispatch_StopsAtTheFirstFailingChannel()
    {
        var inApp = new RecordingPublisher(NotificationChannels.InApp);
        var dispatcher = new NotificationChannelDispatcher(
            [inApp],
            NullLoggerFor<NotificationChannelDispatcher>());
        inApp.OnPublish = _ => throw new TimeoutException("channel outage");

        await Assert.ThrowsAsync<TimeoutException>(() => dispatcher.DispatchAsync(NewNotification()));

        // Recorded before the throw, so the counter reflects the attempt.
        Assert.Equal(1, inApp.PublishCount);
    }

    [Fact]
    public async Task Dispatch_GuardsNulls_InDispatcherAndPublisher()
    {
        var dispatcher = new NotificationChannelDispatcher(
            [new InAppChannelPublisher(NullLoggerFor<InAppChannelPublisher>())],
            NullLoggerFor<NotificationChannelDispatcher>());

        await Assert.ThrowsAsync<ArgumentNullException>(() => dispatcher.DispatchAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => new RecordingPublisher(NotificationChannels.InApp).PublishAsync(null!));
        Assert.Throws<ArgumentNullException>(() => new InAppChannelPublisher(null!));
    }

    // ----------------------------------------------- failure after persistence

    [Fact]
    public async Task PublishFailure_KeepsTheRow_AndARetryReusesTheSameSourceEventId()
    {
        var tenantId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        var sourceEventId = Guid.NewGuid();
        using var factory = CreateFactory();
        SeedProject(factory, tenantId, projectId, recipient);
        var channel = new RecordingPublisher(NotificationChannels.InApp)
        {
            OnPublish = _ => throw new TimeoutException("simulated channel outage"),
        };
        var projector = CreateProjector(factory, channel);
        var input = Input(tenantId, projectId, sourceEventId);

        // The edge case: the channel fails after the row is already committed.
        var failure = await Assert.ThrowsAsync<TimeoutException>(() => projector.ProjectAsync(input));
        Assert.Equal("simulated channel outage", failure.Message);
        Assert.Equal(1, CountAll(factory));

        // The retry reuses SourceEventId, deduplicates to the same row, and
        // publishes nothing the second time.
        channel.OnPublish = null;
        var retried = await projector.ProjectAsync(input);

        Assert.Single(retried);
        Assert.Equal(sourceEventId, retried[0].SourceEventId);
        Assert.Equal(1, CountAll(factory));
        Assert.Equal(1, channel.PublishCount);
    }

    [Fact]
    public async Task Dispatch_RethrowsTheOriginalException_Unwrapped()
    {
        var failure = new HttpRequestException("webhook endpoint refused");
        var channel = new RecordingPublisher(NotificationChannels.InApp)
        {
            OnPublish = _ => throw failure,
        };
        var dispatcher = new NotificationChannelDispatcher(
            [channel],
            NullLoggerFor<NotificationChannelDispatcher>());

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(
            () => dispatcher.DispatchAsync(NewNotification()));

        // Unwrapped on purpose: this product classifies transient versus poison
        // by exception type (MessageDisposition.IsTransient). Wrapping a future
        // channel's transport failure would turn every one of them into poison
        // and silently stop the retry.
        Assert.Same(failure, thrown);
        Assert.True(MessageDisposition.IsTransient(thrown));
    }

    [Fact]
    public async Task Dispatch_Cancellation_IsNotRecordedAsAChannelFailure()
    {
        var logger = new RecordingLogger<NotificationChannelDispatcher>();
        var channel = new RecordingPublisher(NotificationChannels.InApp);
        var dispatcher = new NotificationChannelDispatcher([channel], logger);
        using var cancelled = new CancellationTokenSource();
        channel.OnPublish = _ => throw new OperationCanceledException(cancelled.Token);
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => dispatcher.DispatchAsync(NewNotification(), cancelled.Token));

        // The attempt was made, but the caller going away is not a delivery
        // failure: no channel log line, so the metric stays honest.
        Assert.Equal(1, channel.PublishCount);
        Assert.Empty(logger.Lines);
    }

    [Fact]
    public async Task Dispatch_LogsIdsAndTypeOnly_WhenAChannelFails()
    {
        var logger = new RecordingLogger<NotificationChannelDispatcher>();
        var body = "Run abcd1234 failed (PROVIDER_TIMEOUT) in /var/data/runs";
        var notification = NewNotification(body: body);
        var channel = new RecordingPublisher(NotificationChannels.InApp)
        {
            OnPublish = _ => throw new InvalidOperationException("boom"),
        };
        var dispatcher = new NotificationChannelDispatcher([channel], logger);

        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.DispatchAsync(notification));

        var line = Assert.Single(logger.Lines);
        // 002's rule, inherited: ids and type reach the log, the body never does.
        Assert.Contains(NotificationChannels.InApp, line, StringComparison.Ordinal);
        Assert.Contains(notification.Id.ToString(), line, StringComparison.Ordinal);
        Assert.Contains(notification.Type.ToString(), line, StringComparison.Ordinal);
        Assert.DoesNotContain(body, line, StringComparison.Ordinal);
        Assert.DoesNotContain("PROVIDER_TIMEOUT", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InAppPublisher_LogsIdsAndTypeOnly_AndRejectsNull()
    {
        var logger = new RecordingLogger<InAppChannelPublisher>();
        var publisher = new InAppChannelPublisher(logger);
        var body = "Run abcd1234 failed (PROVIDER_TIMEOUT) in /var/data/runs";
        var notification = NewNotification(body: body);

        // Delivery is the persisted row: no write, no transport, no external send.
        await publisher.PublishAsync(notification);

        var line = Assert.Single(logger.Lines);
        Assert.Contains(notification.Id.ToString(), line, StringComparison.Ordinal);
        Assert.Contains(notification.Type.ToString(), line, StringComparison.Ordinal);
        Assert.Contains(NotificationChannels.InApp, line, StringComparison.Ordinal);
        Assert.DoesNotContain(body, line, StringComparison.Ordinal);
        Assert.DoesNotContain("PROVIDER_TIMEOUT", line, StringComparison.Ordinal);

        await Assert.ThrowsAsync<ArgumentNullException>(() => publisher.PublishAsync(null!));
    }

    // ------------------------------------------------------------------ helpers

    private static NotificationChannelDispatcher Dispatcher(params INotificationChannelPublisher[] channels)
    {
        return new NotificationChannelDispatcher(channels, NullLoggerFor<NotificationChannelDispatcher>());
    }

    private static NotificationProjector CreateProjector(TestContextFactory factory, params INotificationChannelPublisher[] channels)
    {
        return new NotificationProjector(factory, Dispatcher(channels));
    }

    private static RecordingLogger<T> NullLoggerFor<T>() => new();

    private static NotificationInput Input(Guid tenantId, Guid projectId, Guid? sourceEventId)
    {
        return new NotificationInput(
            tenantId, projectId, NotificationType.ProcessingCompleted, NotificationSeverity.Info,
            "Processing completed", "Run abcd1234 completed.",
            "ProcessingRun", Guid.NewGuid().ToString("N"), sourceEventId, Now.AddDays(30));
    }

    private static Notification NewNotification(Guid? sourceEventId = null, string body = "Run abcd1234 completed.")
    {
        return new Notification(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            NotificationType.ProcessingFailed, NotificationSeverity.Error,
            "Processing failed", body,
            "ProcessingRun", "run-1", sourceEventId,
            null, Now, Now.AddDays(30));
    }

    private static int CountAll(TestContextFactory factory)
    {
        using var scope = TenantContext.BeginMaintenanceScope();
        using var db = ((IStageExecutionContextFactory)factory).CreateDbContext();
        return db.Set<Notification>().AsNoTracking().Count();
    }

    private static TestContextFactory CreateFactory()
    {
        return new TestContextFactory(CreateOptions());
    }

    private static DbContextOptions<AppDbContext> CreateOptions()
    {
        return new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("NotificationChannelTests-" + Guid.NewGuid().ToString("N"))
            .ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>()
            .Options;
    }

    private static void SeedUser(TestContextFactory factory, Guid tenantId, Guid userId, TenantUserStatus status)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = factory.CreateSetup();
            db.Set<TenantUser>().Add(new TenantUser(
                userId, tenantId, "sub-" + userId.ToString("N"), "user@example.test", "User", status, Now, Now));
            db.SaveChanges();
        }
    }

    private static void SeedMembership(TestContextFactory factory, Guid tenantId, Guid projectId, Guid userId, ProjectRole role)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = factory.CreateSetup();
            db.Set<ProjectMembership>().Add(new ProjectMembership(
                Guid.NewGuid(), tenantId, projectId, userId, role, null, Now));
            db.SaveChanges();
        }
    }

    private static void SeedProject(TestContextFactory factory, Guid tenantId, Guid projectId, Guid? ownerUserId)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = factory.CreateSetup();
            db.Set<DubbingProject>().Add(new DubbingProject(
                projectId, tenantId, "en", "es", ProjectStatus.Created, "{}", "cfg-hash",
                null, null, Now, Now, "Synthetic project", null, ownerUserId));
            db.SaveChanges();
        }
    }

    /// <summary>
    /// Every product assembly, loaded by reference. Loading by name is what lets
    /// the "one channel" assertion cover the Workers host, whose entry point is
    /// internal and cannot be named from here.
    /// </summary>
    private static IEnumerable<Assembly> ProductAssemblies()
    {
        return Assembly.GetExecutingAssembly()
            .GetReferencedAssemblies()
            .Where(reference => reference.Name is not null
                && reference.Name.StartsWith("DubbingPlatform.", StringComparison.Ordinal))
            .Select(reference => Assembly.Load(reference))
            .OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal);
    }

    /// <summary>
    /// The repository root, found by walking up from the test output directory
    /// (the same idiom <c>MigrationCompatTests</c> uses).
    /// </summary>
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DubbingPlatform.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Repository root (DubbingPlatform.sln) was not found above " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// Removes line comments, block comments, and doc comments while preserving
    /// string, verbatim, raw, and character literals verbatim. A scanner that
    /// cannot tell a comment from code does not enforce a rule, it forbids a
    /// word — and the first response to a forbidden word is a vaguer explanation.
    /// </summary>
    private static string StripComments(string source)
    {
        var output = new StringBuilder(source.Length);
        var index = 0;

        while (index < source.Length)
        {
            var current = source[index];
            var next = index + 1 < source.Length ? source[index + 1] : '\0';

            if (current == '/' && next == '/')
            {
                while (index < source.Length && source[index] != '\n')
                {
                    index++;
                }

                continue;
            }

            if (current == '/' && next == '*')
            {
                index += 2;
                while (index + 1 < source.Length && !(source[index] == '*' && source[index + 1] == '/'))
                {
                    index++;
                }

                index = Math.Min(index + 2, source.Length);
                continue;
            }

            // Raw string literal: """...""" (the EF migrations use this form).
            if (current == '"' && next == '"' && index + 2 < source.Length && source[index + 2] == '"')
            {
                var closing = source.IndexOf("\"\"\"", index + 3, StringComparison.Ordinal);
                index = closing < 0 ? source.Length : closing + 3;
                continue;
            }

            // Verbatim string: @"..." — a "//" inside it is literal text.
            if (current == '@' && next == '"')
            {
                index += 2;
                while (index < source.Length)
                {
                    if (source[index] == '"')
                    {
                        if (index + 1 < source.Length && source[index + 1] == '"')
                        {
                            index++;
                        }
                        else
                        {
                            index++;
                            break;
                        }
                    }

                    index++;
                }

                continue;
            }

            if (current == '"' || current == '\'')
            {
                var quote = current;
                index++;
                while (index < source.Length)
                {
                    if (source[index] == '\\')
                    {
                        index += 2;
                        continue;
                    }

                    if (source[index] == quote)
                    {
                        index++;
                        break;
                    }

                    index++;
                }

                continue;
            }

            output.Append(current);
            index++;
        }

        return output.ToString();
    }

    private sealed class RecordingPublisher : INotificationChannelPublisher
    {
        private readonly List<Notification> _published = [];

        public RecordingPublisher(string channel)
        {
            Channel = channel;
        }

        public string Channel { get; }

        public Func<Notification, Task>? OnPublish { get; set; }

        public IReadOnlyList<Notification> Published
        {
            get
            {
                lock (_published)
                {
                    return [.. _published];
                }
            }
        }

        public int PublishCount => Published.Count;

        public Task PublishAsync(Notification notification, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(notification);
            lock (_published)
            {
                _published.Add(notification);
            }

            return OnPublish?.Invoke(notification) ?? Task.CompletedTask;
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            lock (_lines)
            {
                _lines.Add(formatter(state, exception));
            }
        }
    }

    private sealed class TestAppDbContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        public bool SealDispose { get; set; }

        public override void Dispose()
        {
            if (!SealDispose)
            {
                base.Dispose();
            }
        }

        public override ValueTask DisposeAsync()
        {
            if (!SealDispose)
            {
                return base.DisposeAsync();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestContextFactory(DbContextOptions<AppDbContext> options) : IStageExecutionContextFactory, IDisposable
    {
        private readonly List<TestAppDbContext> _all = [];

        public TestAppDbContext CreateSetup()
        {
            var context = new TestAppDbContext(options);
            _all.Add(context);
            return context;
        }

        DbContext IStageExecutionContextFactory.CreateDbContext()
        {
            var context = new TestAppDbContext(options) { SealDispose = true };
            _all.Add(context);
            return context;
        }

        public void Dispose()
        {
            foreach (var context in _all)
            {
                context.SealDispose = false;
                context.Dispose();
            }
        }
    }
}