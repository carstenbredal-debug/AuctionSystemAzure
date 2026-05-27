using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuctionSystem.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddCommissionFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CommissionAmount",
                schema: "auction",
                table: "AuctionResults",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CommissionType",
                schema: "auction",
                table: "AuctionResults",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CommissionValue",
                schema: "auction",
                table: "AuctionResults",
                type: "decimal(18,4)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CommissionAmount",
                schema: "auction",
                table: "AuctionResults");

            migrationBuilder.DropColumn(
                name: "CommissionType",
                schema: "auction",
                table: "AuctionResults");

            migrationBuilder.DropColumn(
                name: "CommissionValue",
                schema: "auction",
                table: "AuctionResults");
        }
    }
}
