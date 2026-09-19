using System.Globalization;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Infrastructure.Orchestration;
using DubbingPlatform.Infrastructure.Persistence;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DubbingPlatform.Infrastructure.Messaging;

/// <summary>
/// MassTransit bus registration with EntityFramework outbox/inbox and workload
/// queue taxonomy. Fast profile (<c>Transport:Provider=InMemory</c>) uses the
/// in-memory transport with no external broker; full profile
/// (<c>Transport:Provider=RabbitMq</c>) uses durable RabbitMQ queues.
/// Seven workload queues exist by class, never per micro-stage, plus
/// <c>_skipped</c> (unsupported schema versions and poison parked without retry)
/// and <c>_error</c> (explicit cross-tenant/operational faults; the frozen DLQ
/// name in <see cref="DeadLetterQueueName"/>). EF outbox (<c>UsePostgres</c> +
/// <c>UseBusOutbox</c>, 1s query delay) plus inbox state tables guarantee
/// at-least-once delivery with no lost messages on DB commit. Transport-exhausted
/// faults additionally follow the MassTransit default per-endpoint fault
/// pipeline; Task 38 alerts on <c>dlq.depth</c> plus broker error-queue depth.
/// When <paramref name="includeOrchestration"/> is true (worker role only), the
/// run saga, the lease-timeout consumer, and the delayed-message scheduler are
/// registered and attached to <c>control.orchestration</c> and
/// <c>maintenance</c>; the API host leaves it false and only publishes.
/// </summary>
public static class MassTransitConfig
{
    /// <summary>
    /// Frozen dead-letter queue name for explicit fault routing.
    /// MassTransit exposes no per-endpoint dead-letter rename API (verified on
    /// 8.5.10), so DLQ routing is explicit application sends (see
    /// <see cref="MessageDisposition"/>), not broker topology rewrites.
    /// </summary>
    public const string DeadLetterQueueName = QueueNames.Error;

    private static readonly string[] WorkloadQueues =
    [
        QueueNames.ControlOrchestration,
        QueueNames.MediaPreparation,
        QueueNames.MediaRender,
        QueueNames.AiProvider,
        QueueNames.AiGpu,
        QueueNames.Export,
        QueueNames.Maintenance,
    ];

    public static void AddDubbingMassTransit(IServiceCollection services, IConfiguration configuration, bool includeOrchestration = false, Action<MassTransit.IBusRegistrationConfigurator>? configureExtra = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddMassTransit(x =>
        {
            x.AddEntityFrameworkOutbox<AppDbContext>(o =>
            {
                o.QueryDelay = TimeSpan.FromSeconds(1);
                o.UsePostgres();
                o.UseBusOutbox();
            });

            if (includeOrchestration)
            {
                x.AddSagaStateMachine<ProcessingRunSaga, ProcessingRunSagaState>().InMemoryRepository();
                x.AddConsumer<StageLeaseTimeoutConsumer>();
                x.AddDelayedMessageScheduler();
            }

            configureExtra?.Invoke(x);

            if (IsRabbitMq(configuration))
            {
                x.UsingRabbitMq((ctx, cfg) =>
                {
                    cfg.Host(RabbitUri(configuration), h =>
                    {
                        var username = configuration["RabbitMq:Username"];
                        var password = configuration["RabbitMq:Password"];
                        if (!string.IsNullOrWhiteSpace(username))
                        {
                            h.Username(username);
                        }

                        if (!string.IsNullOrWhiteSpace(password))
                        {
                            h.Password(password);
                        }
                    });

                    if (includeOrchestration)
                    {
                        cfg.UseDelayedMessageScheduler();
                    }

                    foreach (var queue in WorkloadQueues)
                    {
                        cfg.ReceiveEndpoint(queue, e =>
                        {
                            e.Durable = true;
                            e.AutoDelete = false;
                            if (includeOrchestration)
                            {
                                AttachOrchestration(e, ctx, queue, configureExtra is not null);
                            }
                        });
                    }

                    cfg.ReceiveEndpoint(QueueNames.Skipped, e =>
                    {
                        e.Durable = true;
                        e.AutoDelete = false;
                    });

                    cfg.ReceiveEndpoint(QueueNames.Error, e =>
                    {
                        e.Durable = true;
                        e.AutoDelete = false;
                    });

                    cfg.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(5)));
                    cfg.UseInMemoryOutbox(ctx);
                });
            }
            else
            {
                x.UsingInMemory((ctx, cfg) =>
                {
                    if (includeOrchestration)
                    {
                        cfg.UseDelayedMessageScheduler();
                    }

                    foreach (var queue in WorkloadQueues)
                    {
                        cfg.ReceiveEndpoint(queue, e =>
                        {
                            if (includeOrchestration)
                            {
                                AttachOrchestration(e, ctx, queue, configureExtra is not null);
                            }
                        });
                    }

                    cfg.ReceiveEndpoint(QueueNames.Skipped, _ => { });
                    cfg.ReceiveEndpoint(QueueNames.Error, _ => { });

                    cfg.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(5)));
                    cfg.UseInMemoryOutbox(ctx);
                });
            }
        });
    }

    private static void AttachOrchestration(IReceiveEndpointConfigurator endpoint, IBusRegistrationContext context, string queue, bool hasExtraConsumers = false)
    {
        if (string.Equals(queue, QueueNames.ControlOrchestration, StringComparison.Ordinal))
        {
            endpoint.ConfigureSaga<ProcessingRunSagaState>(context, _ => { });
        }

        if (string.Equals(queue, QueueNames.Maintenance, StringComparison.Ordinal))
        {
            if (hasExtraConsumers)
            {
                // Retention/deletion work (e.g. DeletionJobWorker on maintenance
                // per QueueNames) attaches here. ConfigureConsumers attaches every
                // registered consumer including StageLeaseTimeoutConsumer, so the
                // explicit single-consumer path below is only for hosts without
                // extras (no double subscription).
                endpoint.ConfigureConsumers(context);
            }
            else
            {
                endpoint.ConfigureConsumer<StageLeaseTimeoutConsumer>(context, _ => { });
            }
        }

        if (hasExtraConsumers && string.Equals(queue, QueueNames.MediaPreparation, StringComparison.Ordinal))
        {
            // Extra role consumers (e.g. MediaIngestionWorker registered by the
            // worker host via configureExtra) attach here. Infrastructure must
            // not reference the worker assembly (circular), so configure all
            // extra consumers on their workload queue.
            endpoint.ConfigureConsumers(context);
        }

        if (hasExtraConsumers && string.Equals(queue, QueueNames.AiProvider, StringComparison.Ordinal))
        {
            // AI workload consumers (e.g. DiarizationWorker on ai.provider per
            // WorkQueueRouter) attach here. All extra consumers share the
            // endpoint; each worker ignores foreign stages via its
            // ShouldProcess pre-claim filter before claiming, so sharing is
            // safe and no message is orphaned.
            endpoint.ConfigureConsumers(context);
        }

        if (hasExtraConsumers && string.Equals(queue, QueueNames.AiGpu, StringComparison.Ordinal))
        {
            // GPU-backed generation (e.g. VoiceGenerationWorker on ai.gpu per
            // WorkQueueRouter) attaches here. Sharing semantics match
            // ai.provider: every extra consumer is configured on the endpoint
            // and filters via ShouldProcess before claiming.
            endpoint.ConfigureConsumers(context);
        }

        if (hasExtraConsumers && string.Equals(queue, QueueNames.MediaRender, StringComparison.Ordinal))
        {
            // Rendering-class work (e.g. TimelineAssemblerWorker on media.render
            // per WorkQueueRouter) attaches here. Sharing semantics match
            // ai.provider: every extra consumer is configured on the endpoint
            // and filters via ShouldProcess before claiming.
            endpoint.ConfigureConsumers(context);
        }

        if (hasExtraConsumers && string.Equals(queue, QueueNames.Export, StringComparison.Ordinal))
        {
            // On-demand exports (ExportWorker on export) attach here. Export is
            // independent of the core DAG with a single consumer, so no
            // ShouldProcess filter is needed.
            endpoint.ConfigureConsumers(context);
        }
    }

    public static bool IsRabbitMq(IConfiguration configuration)
    {
        var provider = configuration["Transport:Provider"]
            ?? configuration["Messaging:Transport"]
            ?? TransportOptions.InMemory;
        return string.Equals(provider, TransportOptions.RabbitMq, StringComparison.OrdinalIgnoreCase);
    }

    private static Uri RabbitUri(IConfiguration configuration)
    {
        var host = configuration["RabbitMq:Host"] ?? "localhost";
        var portText = configuration["RabbitMq:Port"];
        var port = int.TryParse(portText, CultureInfo.InvariantCulture, out var parsed) ? parsed : 5672;
        var vhost = (configuration["RabbitMq:VirtualHost"] ?? "/").Trim();
        if (vhost.Length == 0)
        {
            vhost = "/";
        }

        var path = string.Equals(vhost, "/", StringComparison.Ordinal) ? "/" : "/" + vhost.TrimStart('/');
        return new Uri($"rabbitmq://{host}:{port.ToString(CultureInfo.InvariantCulture)}{path}", UriKind.Absolute);
    }
}
