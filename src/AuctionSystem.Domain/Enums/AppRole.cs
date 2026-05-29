namespace AuctionSystem.Domain.Enums;

public static class AppRole
{
    public const string Admin = "Admin";
    public const string Broker = "Broker";
    public const string Farmer = "Farmer";
    public const string Buyer = "Buyer";

    public static readonly string[] All = [Admin, Broker, Farmer, Buyer];

    public static bool IsValid(string role) => All.Contains(role);
}
