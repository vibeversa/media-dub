using System.Globalization;

namespace DubbingPlatform.Domain.ValueObjects;

/// <summary>
/// Monetary amount with ISO 4217 currency. Null currency defaults to USD.
/// </summary>
public sealed record Money
{
    public decimal Amount { get; init; }

    public string Currency { get; init; }

    public Money(decimal amount, string? currency = "USD")
    {
        if (amount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be >= 0.");
        }

        Currency = NormalizeCurrency(currency);

        Amount = amount;
    }

    internal static string NormalizeCurrency(string? currency)
    {
        if (currency is null)
        {
            return "USD";
        }

        var normalized = currency.ToUpperInvariant();
        if (normalized.Length != 3)
        {
            throw new ArgumentException("Currency must be a 3-letter ISO 4217 code.", nameof(currency));
        }

        for (var i = 0; i < normalized.Length; i++)
        {
            if (normalized[i] < 'A' || normalized[i] > 'Z')
            {
                throw new ArgumentException("Currency must be a 3-letter ISO 4217 code.", nameof(currency));
            }
        }

        return normalized;
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Amount} {Currency}");
}
