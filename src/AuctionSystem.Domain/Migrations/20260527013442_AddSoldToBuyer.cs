using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuctionSystem.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddSoldToBuyer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "SoldAt",
                schema: "auction",
                table: "AuctionResults",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SoldToBuyerId",
                schema: "auction",
                table: "AuctionResults",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuctionResults_SoldToBuyerId",
                schema: "auction",
                table: "AuctionResults",
                column: "SoldToBuyerId");

            migrationBuilder.AddForeignKey(
                name: "FK_AuctionResults_Buyers_SoldToBuyerId",
                schema: "auction",
                table: "AuctionResults",
                column: "SoldToBuyerId",
                principalSchema: "auction",
                principalTable: "Buyers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AuctionResults_Buyers_SoldToBuyerId",
                schema: "auction",
                table: "AuctionResults");

            migrationBuilder.DropIndex(
                name: "IX_AuctionResults_SoldToBuyerId",
                schema: "auction",
                table: "AuctionResults");

            migrationBuilder.DropColumn(
                name: "SoldAt",
                schema: "auction",
                table: "AuctionResults");

            migrationBuilder.DropColumn(
                name: "SoldToBuyerId",
                schema: "auction",
                table: "AuctionResults");
        }
    }
}
