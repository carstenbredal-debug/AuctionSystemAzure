namespace AuctionSystem.Domain.Enums;

public enum SettlementStatus
{
    Pending,
    InvoiceGenerated,
    PaymentReceived,
    SettledWithFarmer,
    Completed,
    Disputed
}
