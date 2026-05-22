using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuctionSystem.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddBrokerExtendedFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AddressLine1",
                schema: "auction",
                table: "Brokers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AddressLine2",
                schema: "auction",
                table: "Brokers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "City",
                schema: "auction",
                table: "Brokers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CompanyName2",
                schema: "auction",
                table: "Brokers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Country",
                schema: "auction",
                table: "Brokers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                schema: "auction",
                table: "Brokers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CustomerGroup",
                schema: "auction",
                table: "Brokers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ErpAccountNumber",
                schema: "auction",
                table: "Brokers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "HomePage",
                schema: "auction",
                table: "Brokers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                schema: "auction",
                table: "Brokers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Language",
                schema: "auction",
                table: "Brokers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MobilePhone",
                schema: "auction",
                table: "Brokers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PaymentMethod",
                schema: "auction",
                table: "Brokers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PaymentTerm",
                schema: "auction",
                table: "Brokers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PostalCode",
                schema: "auction",
                table: "Brokers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RegistrationNo",
                schema: "auction",
                table: "Brokers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SalesPerson",
                schema: "auction",
                table: "Brokers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SearchName",
                schema: "auction",
                table: "Brokers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "VatRegistrationNo",
                schema: "auction",
                table: "Brokers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AddressLine1",
                schema: "auction",
                table: "Brokers");

            migrationBuilder.DropColumn(
                name: "AddressLine2",
                schema: "auction",
                table: "Brokers");

            migrationBuilder.DropColumn(
                name: "City",
                schema: "auction",
                table: "Brokers");

            migrationBuilder.DropColumn(
                name: "CompanyName2",
                schema: "auction",
                table: "Brokers");

            migrationBuilder.DropColumn(
                name: "Country",
                schema: "auction",
                table: "Brokers");

            migrationBuilder.DropColumn(
                name: "Currency",
                schema: "auction",
                table: "Brokers");

            migrationBuilder.DropColumn(
                name: "CustomerGroup",
                schema: "auction",
                table: "Brokers");

            migrationBuilder.DropColumn(
                name: "ErpAccountNumber",
                schema: "auction",
                table: "Brokers");

            migrationBuilder.DropColumn(
                name: "HomePage",
                schema: "auction",
                table: "Brokers");

            migrationBuilder.DropColumn(
                name: "IsActive",
                schema: "auction",
                table: "Brokers");

            migrationBuilder.DropColumn(
                name: "Language",
                schema: "auction",
                table: "Brokers");

            migrationBuilder.DropColumn(
                name: "MobilePhone",
                schema: "auction",
                table: "Brokers");

            migrationBuilder.DropColumn(
                name: "PaymentMethod",
                schema: "auction",
                table: "Brokers");

            migrationBuilder.DropColumn(
                name: "PaymentTerm",
                schema: "auction",
                table: "Brokers");

            migrationBuilder.DropColumn(
                name: "PostalCode",
                schema: "auction",
                table: "Brokers");

            migrationBuilder.DropColumn(
                name: "RegistrationNo",
                schema: "auction",
                table: "Brokers");

            migrationBuilder.DropColumn(
                name: "SalesPerson",
                schema: "auction",
                table: "Brokers");

            migrationBuilder.DropColumn(
                name: "SearchName",
                schema: "auction",
                table: "Brokers");

            migrationBuilder.DropColumn(
                name: "VatRegistrationNo",
                schema: "auction",
                table: "Brokers");
        }
    }
}
