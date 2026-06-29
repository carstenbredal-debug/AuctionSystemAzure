namespace AuctionSystem.Domain.Services;

/// <summary>
/// VAT is owned and posted by Business Central; the app does not change what it sends. This helper
/// only mirrors BC's rule so invoices can DISPLAY Net / VAT / Total incl. VAT and reconcile against
/// the gross <c>RemainingAmount</c> BC reports back. The rate is driven by the party's VAT Bus.
/// Posting Group on their card: domestic (POLAND) is standard-rated, everyone else is 0%
/// (export / reverse charge). Keep this in sync with BC's VAT Posting Setup.
/// </summary>
public static class VatRules
{
    public const string DomesticVatBusPostingGroup = "POLAND";
    public const decimal DomesticRate = 0.23m;

    /// <summary>VAT rate for a party's VAT Bus. Posting Group (trimmed, case-insensitive).</summary>
    public static decimal RateFor(string? vatBusPostingGroup) =>
        string.Equals(vatBusPostingGroup?.Trim(), DomesticVatBusPostingGroup, StringComparison.OrdinalIgnoreCase)
            ? DomesticRate
            : 0m;

    /// <summary>Returns (VAT amount, total incl. VAT) for a net total and a party's VAT Bus. Posting Group.</summary>
    public static (decimal Vat, decimal TotalInclVat) Compute(decimal netTotal, string? vatBusPostingGroup)
    {
        var vat = decimal.Round(netTotal * RateFor(vatBusPostingGroup), 2, MidpointRounding.AwayFromZero);
        return (vat, netTotal + vat);
    }
}
