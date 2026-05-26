using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuctionSystem.Domain.Migrations
{
    /// <inheritdoc />
    public partial class ExpandSellerFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AddressLine1",
                schema: "auction",
                table: "Sellers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AddressLine2",
                schema: "auction",
                table: "Sellers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Assignee",
                schema: "auction",
                table: "Sellers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "AssignmentOfReceivable",
                schema: "auction",
                table: "Sellers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "BankAddress",
                schema: "auction",
                table: "Sellers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "BankCountry",
                schema: "auction",
                table: "Sellers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "BankIbanNumber",
                schema: "auction",
                table: "Sellers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "BankName",
                schema: "auction",
                table: "Sellers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "City",
                schema: "auction",
                table: "Sellers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ContactName",
                schema: "auction",
                table: "Sellers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Country",
                schema: "auction",
                table: "Sellers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                schema: "auction",
                table: "Sellers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CustomerGroup",
                schema: "auction",
                table: "Sellers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ErpAccountNumber",
                schema: "auction",
                table: "Sellers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "HomePage",
                schema: "auction",
                table: "Sellers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                schema: "auction",
                table: "Sellers",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Language",
                schema: "auction",
                table: "Sellers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "MobilePhone",
                schema: "auction",
                table: "Sellers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Name2",
                schema: "auction",
                table: "Sellers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PaymentMethod",
                schema: "auction",
                table: "Sellers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PaymentTerm",
                schema: "auction",
                table: "Sellers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PostalCode",
                schema: "auction",
                table: "Sellers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RegistrationNo",
                schema: "auction",
                table: "Sellers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SalesPerson",
                schema: "auction",
                table: "Sellers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SearchName",
                schema: "auction",
                table: "Sellers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SwiftCode",
                schema: "auction",
                table: "Sellers",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "VatRegistrationNo",
                schema: "auction",
                table: "Sellers",
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
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "AddressLine2",
                schema: "auction",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "Assignee",
                schema: "auction",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "AssignmentOfReceivable",
                schema: "auction",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "BankAddress",
                schema: "auction",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "BankCountry",
                schema: "auction",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "BankIbanNumber",
                schema: "auction",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "BankName",
                schema: "auction",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "City",
                schema: "auction",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "ContactName",
                schema: "auction",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "Country",
                schema: "auction",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "Currency",
                schema: "auction",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "CustomerGroup",
                schema: "auction",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "ErpAccountNumber",
                schema: "auction",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "HomePage",
                schema: "auction",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "IsActive",
                schema: "auction",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "Language",
                schema: "auction",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "MobilePhone",
                schema: "auction",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "Name2",
                schema: "auction",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "PaymentMethod",
                schema: "auction",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "PaymentTerm",
                schema: "auction",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "PostalCode",
                schema: "auction",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "RegistrationNo",
                schema: "auction",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "SalesPerson",
                schema: "auction",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "SearchName",
                schema: "auction",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "SwiftCode",
                schema: "auction",
                table: "Sellers");

            migrationBuilder.DropColumn(
                name: "VatRegistrationNo",
                schema: "auction",
                table: "Sellers");
        }
    }
}
