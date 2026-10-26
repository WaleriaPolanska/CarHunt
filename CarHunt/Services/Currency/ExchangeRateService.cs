using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CarHunt.Services.Currency;

/// <summary>
/// Converts listing prices into EUR so results from different countries can be
/// compared and sorted against each other. Rates are published once a day, so a
/// single fetch per session is enough.
/// </summary>
public class ExchangeRateService
{
    private readonly HttpClient _http;
    private const string ApiUrl = "https://open.er-api.com/v6/latest/EUR";

    // rates[X] = how many X make one EUR.
    private Dictionary<string, double>? _rates;

    public ExchangeRateService(HttpClient http) => _http = http;

    /// <summary>
    /// Converts <paramref name="amount"/> to EUR, or returns 0 when the rate is
    /// unknown (offline, unsupported currency) so callers can fall back.
    /// </summary>
    public async Task<int> ToEurAsync(int amount, string currency, CancellationToken ct = default)
    {
        if (amount <= 0) return 0;
        if (string.IsNullOrWhiteSpace(currency) || currency.Equals("EUR", StringComparison.OrdinalIgnoreCase))
            return amount;

        var rates = await GetRatesAsync(ct);
        if (rates is null || !rates.TryGetValue(currency.ToUpperInvariant(), out var rate) || rate <= 0)
            return 0;

        return (int)Math.Round(amount / rate);
    }

    /// <summary>
    /// The other direction: a sum typed in euro, expressed in <paramref name="currency"/>,
    /// so it can be handed to a site that quotes prices in it. Returns 0 when the rate is
    /// unknown, so callers can fall back to sending nothing rather than a wrong number.
    /// </summary>
    public async Task<int> FromEurAsync(int amount, string currency, CancellationToken ct = default)
    {
        if (amount <= 0) return 0;
        if (string.IsNullOrWhiteSpace(currency) || currency.Equals("EUR", StringComparison.OrdinalIgnoreCase))
            return amount;

        var rates = await GetRatesAsync(ct);
        if (rates is null || !rates.TryGetValue(currency.ToUpperInvariant(), out var rate) || rate <= 0)
            return 0;

        return (int)Math.Round(amount * rate);
    }

    private async Task<Dictionary<string, double>?> GetRatesAsync(CancellationToken ct)
    {
        if (_rates is not null) return _rates;

        try
        {
            var json = await _http.GetStringAsync(ApiUrl, ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("rates", out var ratesEl) ||
                ratesEl.ValueKind != JsonValueKind.Object)
                return null;

            var parsed = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in ratesEl.EnumerateObject())
                if (r.Value.ValueKind == JsonValueKind.Number)
                    parsed[r.Name] = r.Value.GetDouble();

            // Only cache a usable result, so a failed attempt can be retried later.
            if (parsed.Count > 0) _rates = parsed;
            return _rates;
        }
        catch
        {
            return null;
        }
    }
}
