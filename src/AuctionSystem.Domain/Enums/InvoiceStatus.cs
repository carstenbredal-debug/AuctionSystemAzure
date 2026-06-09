namespace AuctionSystem.Domain.Enums;

public enum InvoiceStatus
{
    Draft,
    Issued,
    Sent,
    Downpayment,
    Paid,
    Overdue,
    Cancelled,
    PartiallyCredited,
    FullyCredited,
    Alloted,
    ReleasedToShip,
    Closed
}
