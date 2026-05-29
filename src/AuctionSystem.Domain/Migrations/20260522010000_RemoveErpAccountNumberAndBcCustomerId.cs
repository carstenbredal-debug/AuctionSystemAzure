using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuctionSystem.Domain.Migrations
{
    /// <inheritdoc />
    public partial class RemoveErpAccountNumberAndBcCustomerId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ErpAccountNumber",
                schema: "auction",
                table: "Brokers");

            migrationBuilder.DropColumn(
                name: "BcCustomerId",
                schema: "auction",
                table: "Brokers");

            migrationBuilder.DropColumn(
                name: "ErpAccountNumber",
                schema: "auction",
                table: "Buyers");

            migrationBuilder.DropColumn(
                name: "BcCustomerId",
                schema: "auction",
                table: "Buyers");

            migrationBuilder.DropColumn(
                name: "ErpAccountNumber",
                schema: "auction",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "BcCustomerId",
                schema: "auction",
                table: "Sellers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ErpAccountNumber",
                schema: "auction",
                table: "Brokers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "BcCustomerId",
                schema: "auction",
                table: "Brokers",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErpAccountNumber",
                schema: "auction",
                table: "Buyers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "BcCustomerId",
                schema: "auction",
                table: "Buyers",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErpAccountNumber",
                schema: "auction",
                table: "Sellers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "BcCustomerId",
                schema: "auction",
                table: "Sellers",
                type: "nvarchar(max)",
                nullable: true);
        }
    }
}
