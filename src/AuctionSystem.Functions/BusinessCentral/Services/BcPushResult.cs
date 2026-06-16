namespace AuctionSystem.Functions.BusinessCentral.Services;

/// <summary>Outcome of pushing one invoice/credit-note to BC.</summary>
public enum BcPushOutcome
{
    /// <summary>Created and posted in BC just now.</summary>
    Posted,
    /// <summary>Already present in BC (recorded, nothing to do).</summary>
    AlreadyPushed,
    /// <summary>Another push holds the claim; skipped to avoid a duplicate (transient).</summary>
    Concurrent,
    /// <summary>Not pushed for a real reason that needs attention (e.g. buyer not in BC).</summary>
    NotPushed
}

public readonly record struct BcPushResult(BcPushOutcome Outcome, string? Reason = null)
{
    public static readonly BcPushResult Posted = new(BcPushOutcome.Posted);
    public static readonly BcPushResult AlreadyPushed = new(BcPushOutcome.AlreadyPushed);
    public static readonly BcPushResult Concurrent = new(BcPushOutcome.Concurrent);
    public static BcPushResult NotPushed(string reason) => new(BcPushOutcome.NotPushed, reason);
}
