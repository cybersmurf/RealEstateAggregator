namespace RealEstate.App.Services;

/// <summary>Výsledek hypoteční kalkulačky – všechny částky v Kč.</summary>
public sealed record MortgageResult(
    decimal Loan,
    decimal Monthly,
    decimal TotalPaid,
    decimal TotalInterest,
    decimal LtvPercent);

/// <summary>
/// Čistá matematika hypoteční kalkulačky (anuitní splátka). Bez závislostí, aby šla
/// otestovat i použít mimo komponentu.
/// </summary>
public static class MortgageMath
{
    /// <summary>Výše úvěru = cena − vlastní zdroje (v % z ceny).</summary>
    public static decimal LoanAmount(decimal price, decimal ownFundsPercent)
    {
        if (price <= 0)
            return 0;
        var pct = Math.Clamp(ownFundsPercent, 0, 100);
        return Math.Round(price * (1 - pct / 100m), 0, MidpointRounding.AwayFromZero);
    }

    /// <summary>Anuitní měsíční splátka: L · r · (1+r)^n / ((1+r)^n − 1), r = roční sazba / 12.</summary>
    public static decimal MonthlyPayment(decimal loan, decimal annualRatePercent, int years)
    {
        if (loan <= 0 || years <= 0)
            return 0;
        var months = years * 12;
        if (annualRatePercent <= 0)
            return Math.Round(loan / months, 0, MidpointRounding.AwayFromZero);

        var r = (double)annualRatePercent / 100d / 12d;
        var pow = Math.Pow(1 + r, months);
        var payment = (double)loan * r * pow / (pow - 1);
        return Math.Round((decimal)payment, 0, MidpointRounding.AwayFromZero);
    }

    public static MortgageResult Compute(decimal price, decimal ownFundsPercent, decimal annualRatePercent, int years)
    {
        var loan = LoanAmount(price, ownFundsPercent);
        var monthly = MonthlyPayment(loan, annualRatePercent, years);
        var totalPaid = monthly * years * 12;
        var ltv = price > 0 ? Math.Round(loan / price * 100m, 1) : 0;
        return new MortgageResult(loan, monthly, totalPaid, Math.Max(0, totalPaid - loan), ltv);
    }

    /// <summary>
    /// Přidá UTM a částky k URL partnera; respektuje už přítomný '?' v adrese.
    /// </summary>
    public static string BuildPartnerUrl(string partnerUrl, decimal price, decimal loan)
    {
        var separator = partnerUrl.Contains('?') ? '&' : '?';
        var query = "utm_source=realestate-aggregator&utm_medium=listing&utm_campaign=mortgage"
                    + $"&price={Math.Round(price, 0):0}&loan={Math.Round(loan, 0):0}";
        return partnerUrl.TrimEnd('&', '?') + separator + query;
    }
}
