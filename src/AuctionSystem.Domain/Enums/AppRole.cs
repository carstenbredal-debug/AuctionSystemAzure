namespace AuctionSystem.Domain.Enums;

public static class AppRole
{
    public const string Admin = "Admin";
    public const string Broker = "Broker";
    public const string Seller = "Seller";
    public const string Buyer = "Buyer";

    public static readonly string[] All = [Admin, Broker, Seller, Buyer];

    public static bool IsValid(string role) => All.Contains(role);
}
