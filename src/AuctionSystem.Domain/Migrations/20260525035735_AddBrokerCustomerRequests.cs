using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuctionSystem.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddBrokerCustomerRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BrokerCustomerRequests",
                schema: "auction",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BrokerId = table.Column<int>(type: "int", nullable: false),
                    BuyerId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RespondedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrokerCustomerRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BrokerCustomerRequests_Brokers_BrokerId",
                        column: x => x.BrokerId,
                        principalSchema: "auction",
                        principalTable: "Brokers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BrokerCustomerRequests_Buyers_BuyerId",
                        column: x => x.BuyerId,
                        principalSchema: "auction",
                        principalTable: "Buyers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BrokerCustomerRequests_BrokerId_BuyerId",
                schema: "auction",
                table: "BrokerCustomerRequests",
                columns: new[] { "BrokerId", "BuyerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BrokerCustomerRequests_BuyerId",
                schema: "auction",
                table: "BrokerCustomerRequests",
                column: "BuyerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BrokerCustomerRequests",
                schema: "auction");
        }
    }
}
