using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuctionSystem.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddCreditNoteFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsCreditNote",
                schema: "auction",
                table: "Invoices",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "OriginalInvoiceId",
                schema: "auction",
                table: "Invoices",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_OriginalInvoiceId",
                schema: "auction",
                table: "Invoices",
                column: "OriginalInvoiceId");

            migrationBuilder.AddForeignKey(
                name: "FK_Invoices_Invoices_OriginalInvoiceId",
                schema: "auction",
                table: "Invoices",
                column: "OriginalInvoiceId",
                principalSchema: "auction",
                principalTable: "Invoices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Invoices_Invoices_OriginalInvoiceId",
                schema: "auction",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_OriginalInvoiceId",
                schema: "auction",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "IsCreditNote",
                schema: "auction",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "OriginalInvoiceId",
                schema: "auction",
                table: "Invoices");
        }
    }
}
