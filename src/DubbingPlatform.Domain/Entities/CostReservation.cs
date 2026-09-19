using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;

namespace DubbingPlatform.Domain.Entities;

/// <summary>
/// Atomic cost reservation with reconciled actuals.
/// Allowed <see cref="State"/> values: Reserved, Reconciled, Released.
/// Consumers must reject unknown states.
/// </summary>
public sealed class CostReservation
{
    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ProjectId { get; private set; }

    public Guid ProcessingRunId { get; private set; }

    public Guid? SegmentId { get; private set; }

    public ProviderCapability Capability { get; private set; }

    public double ReservedAmount { get; private set; }

    public double ActualAmount { get; private set; }

    public string Currency { get; private set; }

    public string PriceTableVersion { get; private set; }

    public string State { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private CostReservation()
    {
        Currency = string.Empty;
        PriceTableVersion = string.Empty;
        State = string.Empty;
    }

    public CostReservation(
        Guid id,
        Guid tenantId,
        Guid projectId,
        Guid processingRunId,
        Guid? segmentId,
        ProviderCapability capability,
        double reservedAmount,
        double actualAmount,
        string currency,
        string priceTableVersion,
        string state,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        ProjectId = projectId;
        ProcessingRunId = processingRunId;
        SegmentId = segmentId;
        Capability = capability;
        ReservedAmount = reservedAmount;
        ActualAmount = actualAmount;
        Currency = currency;
        PriceTableVersion = priceTableVersion;
        State = state;
        CreatedAt = createdAt;

        Validate();
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new DomainException("CostReservation Id must not be empty.");
        }

        if (TenantId == Guid.Empty)
        {
            throw new DomainException("CostReservation TenantId must not be empty.");
        }

        if (ProjectId == Guid.Empty)
        {
            throw new DomainException("CostReservation ProjectId must not be empty.");
        }

        if (ProcessingRunId == Guid.Empty)
        {
            throw new DomainException("CostReservation ProcessingRunId must not be empty.");
        }

        if (SegmentId.HasValue && SegmentId.Value == Guid.Empty)
        {
            throw new DomainException("CostReservation SegmentId must not be empty when set.");
        }

        if (double.IsNaN(ReservedAmount) || ReservedAmount < 0.0)
        {
            throw new DomainException("CostReservation ReservedAmount must be >= 0.");
        }

        if (double.IsNaN(ActualAmount) || ActualAmount < 0.0)
        {
            throw new DomainException("CostReservation ActualAmount must be >= 0.");
        }

        if (!IsValidCurrency(Currency))
        {
            throw new DomainException("CostReservation Currency must be a 3-letter code.");
        }

        if (string.IsNullOrWhiteSpace(PriceTableVersion))
        {
            throw new DomainException("CostReservation PriceTableVersion must not be empty.");
        }

        if (State != "Reserved" && State != "Reconciled" && State != "Released")
        {
            throw new DomainException("CostReservation State must be one of Reserved, Reconciled, Released.");
        }
    }

    private static bool IsValidCurrency(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 3)
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var isLetter = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
            if (!isLetter)
            {
                return false;
            }
        }

        return true;
    }
}
