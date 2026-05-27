using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuctionSystem.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddTakebackRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TakebackRequests",
                schema: "auction",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AuctionResultId = table.Column<int>(type: "int", nullable: false),
                    BrokerId = table.Column<int>(type: "int", nullable: false),
                    BuyerId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RespondedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TakebackRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TakebackRequests_AuctionResults_AuctionResultId",
                        column: x => x.AuctionResultId,
                        principalSchema: "auction",
                        principalTable: "AuctionResults",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TakebackRequests_Brokers_BrokerId",
                        column: x => x.BrokerId,
                        principalSchema: "auction",
                        principalTable: "Brokers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TakebackRequests_Buyers_BuyerId",
                        column: x => x.BuyerId,
                        principalSchema: "auction",
                        principalTable: "Buyers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TakebackRequests_AuctionResultId",
                schema: "auction",
                table: "TakebackRequests",
                column: "AuctionResultId");

            migrationBuilder.CreateIndex(
                name: "IX_TakebackRequests_BrokerId",
                schema: "auction",
                table: "TakebackRequests",
                column: "BrokerId");

            migrationBuilder.CreateIndex(
                name: "IX_TakebackRequests_BuyerId",
                schema: "auction",
                table: "TakebackRequests",
                column: "BuyerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TakebackRequests",
                schema: "auction");
        }
    }
}
