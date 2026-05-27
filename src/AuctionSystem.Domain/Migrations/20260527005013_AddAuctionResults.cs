using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuctionSystem.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddAuctionResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuctionResults",
                schema: "auction",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LotNumber = table.Column<int>(type: "int", nullable: false),
                    BrokerId = table.Column<int>(type: "int", nullable: false),
                    PriceEur = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    SalesType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Gender = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Group = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Color = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Quality = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Size = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Clarity = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    HairLength = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    TotalSkins = table.Column<int>(type: "int", nullable: false),
                    BoxCount = table.Column<int>(type: "int", nullable: false),
                    Processed = table.Column<bool>(type: "bit", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuctionResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuctionResults_Brokers_BrokerId",
                        column: x => x.BrokerId,
                        principalSchema: "auction",
                        principalTable: "Brokers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuctionResults_BrokerId",
                schema: "auction",
                table: "AuctionResults",
                column: "BrokerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuctionResults",
                schema: "auction");
        }
    }
}
