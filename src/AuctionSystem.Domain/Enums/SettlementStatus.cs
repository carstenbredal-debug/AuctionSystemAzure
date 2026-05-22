namespace AuctionSystem.Domain.Enums;

public enum SettlementStatus
{
    Pending,
    InvoiceGenerated,
    PaymentReceived,
    SettledWithSeller,
    Completed,
    Disputed
}
