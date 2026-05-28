using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuctionSystem.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddBrokerBuyerJunction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // BrokerBuyers table (idempotent — may already exist from manual SQL)
            migrationBuilder.Sql(@"
                IF NOT EXISTS (SELECT * FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = 'auction' AND TABLE_NAME = 'BrokerBuyers')
                BEGIN
                    CREATE TABLE auction.BrokerBuyers (
                        BrokerId INT NOT NULL,
                        BuyerId INT NOT NULL,
                        CreatedAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
                        CONSTRAINT PK_BrokerBuyers PRIMARY KEY (BrokerId, BuyerId),
                        CONSTRAINT FK_BrokerBuyers_Brokers_BrokerId FOREIGN KEY (BrokerId) REFERENCES auction.Brokers(Id) ON DELETE CASCADE,
                        CONSTRAINT FK_BrokerBuyers_Buyers_BuyerId FOREIGN KEY (BuyerId) REFERENCES auction.Buyers(Id) ON DELETE CASCADE
                    );
                    CREATE INDEX IX_BrokerBuyers_BuyerId ON auction.BrokerBuyers (BuyerId);
                END;
            ");

            // Migrate existing BrokerId data
            migrationBuilder.Sql(@"
                INSERT INTO auction.BrokerBuyers (BrokerId, BuyerId, CreatedAt)
                SELECT BrokerId, Id, GETUTCDATE()
                FROM auction.Buyers
                WHERE BrokerId IS NOT NULL
                AND NOT EXISTS (SELECT 1 FROM auction.BrokerBuyers bb WHERE bb.BrokerId = auction.Buyers.BrokerId AND bb.BuyerId = auction.Buyers.Id);
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Lots_Sellers_SellerId",
                schema: "auction",
                table: "Lots");

            migrationBuilder.DropTable(
                name: "BrokerBuyers",
                schema: "auction");

            migrationBuilder.AlterColumn<int>(
                name: "SellerId",
                schema: "auction",
                table: "Lots",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Lots_Sellers_SellerId",
                schema: "auction",
                table: "Lots",
                column: "SellerId",
                principalSchema: "auction",
                principalTable: "Sellers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
