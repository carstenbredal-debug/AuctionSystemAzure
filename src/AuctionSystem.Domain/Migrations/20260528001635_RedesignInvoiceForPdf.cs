using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuctionSystem.Domain.Migrations
{
    /// <inheritdoc />
    public partial class RedesignInvoiceForPdf : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InvoiceLines_Lots_LotId",
                schema: "auction",
                table: "InvoiceLines");

            migrationBuilder.DropForeignKey(
                name: "FK_Invoices_Auctions_AuctionId",
                schema: "auction",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_InvoiceLines_LotId",
                schema: "auction",
                table: "InvoiceLines");

            migrationBuilder.DropColumn(
                name: "BcInvoiceId",
                schema: "auction",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "DueDate",
                schema: "auction",
                table: "Invoices");

            migrationBuilder.RenameColumn(
                name: "Tax",
                schema: "auction",
                table: "Invoices",
                newName: "AuctionFee");

            migrationBuilder.RenameColumn(
                name: "PaidDate",
                schema: "auction",
                table: "Invoices",
                newName: "PromptDate");

            migrationBuilder.RenameColumn(
                name: "IssuedDate",
                schema: "auction",
                table: "Invoices",
                newName: "InvoiceDate");

            migrationBuilder.RenameColumn(
                name: "AuctionId",
                schema: "auction",
                table: "Invoices",
                newName: "BuyerId");

            migrationBuilder.RenameIndex(
                name: "IX_Invoices_AuctionId",
                schema: "auction",
                table: "Invoices",
                newName: "IX_Invoices_BuyerId");

            migrationBuilder.RenameColumn(
                name: "UnitPrice",
                schema: "auction",
                table: "InvoiceLines",
                newName: "PricePerSkin");

            migrationBuilder.RenameColumn(
                name: "Quantity",
                schema: "auction",
                table: "InvoiceLines",
                newName: "Skins");

            migrationBuilder.RenameColumn(
                name: "LotId",
                schema: "auction",
                table: "InvoiceLines",
                newName: "LotNumber");

            migrationBuilder.RenameColumn(
                name: "LineTotal",
                schema: "auction",
                table: "InvoiceLines",
                newName: "HammerPrice");

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                schema: "auction",
                table: "Invoices",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<byte[]>(
                name: "PdfData",
                schema: "auction",
                table: "Invoices",
                type: "varbinary(max)",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                schema: "auction",
                table: "InvoiceLines",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AddColumn<int>(
                name: "AuctionResultId",
                schema: "auction",
                table: "InvoiceLines",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLines_AuctionResultId",
                schema: "auction",
                table: "InvoiceLines",
                column: "AuctionResultId");

            migrationBuilder.AddForeignKey(
                name: "FK_InvoiceLines_AuctionResults_AuctionResultId",
                schema: "auction",
                table: "InvoiceLines",
                column: "AuctionResultId",
                principalSchema: "auction",
                principalTable: "AuctionResults",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Invoices_Buyers_BuyerId",
                schema: "auction",
                table: "Invoices",
                column: "BuyerId",
                principalSchema: "auction",
                principalTable: "Buyers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_InvoiceLines_AuctionResults_AuctionResultId",
                schema: "auction",
                table: "InvoiceLines");

            migrationBuilder.DropForeignKey(
                name: "FK_Invoices_Buyers_BuyerId",
                schema: "auction",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_InvoiceLines_AuctionResultId",
                schema: "auction",
                table: "InvoiceLines");

            migrationBuilder.DropColumn(
                name: "Currency",
                schema: "auction",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "PdfData",
                schema: "auction",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "AuctionResultId",
                schema: "auction",
                table: "InvoiceLines");

            migrationBuilder.RenameColumn(
                name: "PromptDate",
                schema: "auction",
                table: "Invoices",
                newName: "PaidDate");

            migrationBuilder.RenameColumn(
                name: "InvoiceDate",
                schema: "auction",
                table: "Invoices",
                newName: "IssuedDate");

            migrationBuilder.RenameColumn(
                name: "BuyerId",
                schema: "auction",
                table: "Invoices",
                newName: "AuctionId");

            migrationBuilder.RenameColumn(
                name: "AuctionFee",
                schema: "auction",
                table: "Invoices",
                newName: "Tax");

            migrationBuilder.RenameIndex(
                name: "IX_Invoices_BuyerId",
                schema: "auction",
                table: "Invoices",
                newName: "IX_Invoices_AuctionId");

            migrationBuilder.RenameColumn(
                name: "Skins",
                schema: "auction",
                table: "InvoiceLines",
                newName: "Quantity");

            migrationBuilder.RenameColumn(
                name: "PricePerSkin",
                schema: "auction",
                table: "InvoiceLines",
                newName: "UnitPrice");

            migrationBuilder.RenameColumn(
                name: "LotNumber",
                schema: "auction",
                table: "InvoiceLines",
                newName: "LotId");

            migrationBuilder.RenameColumn(
                name: "HammerPrice",
                schema: "auction",
                table: "InvoiceLines",
                newName: "LineTotal");

            migrationBuilder.AddColumn<string>(
                name: "BcInvoiceId",
                schema: "auction",
                table: "Invoices",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DueDate",
                schema: "auction",
                table: "Invoices",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                schema: "auction",
                table: "InvoiceLines",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(500)",
                oldMaxLength: 500);

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLines_LotId",
                schema: "auction",
                table: "InvoiceLines",
                column: "LotId");

            migrationBuilder.AddForeignKey(
                name: "FK_InvoiceLines_Lots_LotId",
                schema: "auction",
                table: "InvoiceLines",
                column: "LotId",
                principalSchema: "auction",
                principalTable: "Lots",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Invoices_Auctions_AuctionId",
                schema: "auction",
                table: "Invoices",
                column: "AuctionId",
                principalSchema: "auction",
                principalTable: "Auctions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
