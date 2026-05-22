using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuctionSystem.Domain.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "auction");

            migrationBuilder.CreateTable(
                name: "Auctions",
                schema: "auction",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AuctionNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Location = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ScheduledDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Auctions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Brokers",
                schema: "auction",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BrokerNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    CompanyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContactPerson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContactEmail = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContactPhone = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Address = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BcCustomerId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Brokers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Sellers",
                schema: "auction",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SellerNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContactEmail = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContactPhone = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Address = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BcCustomerId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sellers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Buyers",
                schema: "auction",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BuyerNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContactEmail = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContactPhone = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Address = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BrokerId = table.Column<int>(type: "int", nullable: false),
                    BcCustomerId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Buyers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Buyers_Brokers_BrokerId",
                        column: x => x.BrokerId,
                        principalSchema: "auction",
                        principalTable: "Brokers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Invoices",
                schema: "auction",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    InvoiceNumber = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    SubTotal = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Commission = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Tax = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TotalAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    IssuedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DueDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PaidDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    BcInvoiceId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    BrokerId = table.Column<int>(type: "int", nullable: false),
                    AuctionId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invoices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Invoices_Auctions_AuctionId",
                        column: x => x.AuctionId,
                        principalSchema: "auction",
                        principalTable: "Auctions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Invoices_Brokers_BrokerId",
                        column: x => x.BrokerId,
                        principalSchema: "auction",
                        principalTable: "Brokers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Lots",
                schema: "auction",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LotNumber = table.Column<int>(type: "int", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    StartingPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ReservePrice = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    HammerPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AuctionId = table.Column<int>(type: "int", nullable: false),
                    SellerId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Lots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Lots_Auctions_AuctionId",
                        column: x => x.AuctionId,
                        principalSchema: "auction",
                        principalTable: "Auctions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Lots_Sellers_SellerId",
                        column: x => x.SellerId,
                        principalSchema: "auction",
                        principalTable: "Sellers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Bids",
                schema: "auction",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    PlacedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LotId = table.Column<int>(type: "int", nullable: false),
                    BrokerId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bids", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Bids_Brokers_BrokerId",
                        column: x => x.BrokerId,
                        principalSchema: "auction",
                        principalTable: "Brokers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Bids_Lots_LotId",
                        column: x => x.LotId,
                        principalSchema: "auction",
                        principalTable: "Lots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceLines",
                schema: "auction",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    LineTotal = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    InvoiceId = table.Column<int>(type: "int", nullable: false),
                    LotId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvoiceLines_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalSchema: "auction",
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InvoiceLines_Lots_LotId",
                        column: x => x.LotId,
                        principalSchema: "auction",
                        principalTable: "Lots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LotAllocations",
                schema: "auction",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    PricePerUnit = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    TotalPrice = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AllocatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LotId = table.Column<int>(type: "int", nullable: false),
                    BrokerId = table.Column<int>(type: "int", nullable: false),
                    BuyerId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LotAllocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LotAllocations_Brokers_BrokerId",
                        column: x => x.BrokerId,
                        principalSchema: "auction",
                        principalTable: "Brokers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LotAllocations_Buyers_BuyerId",
                        column: x => x.BuyerId,
                        principalSchema: "auction",
                        principalTable: "Buyers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LotAllocations_Lots_LotId",
                        column: x => x.LotId,
                        principalSchema: "auction",
                        principalTable: "Lots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Settlements",
                schema: "auction",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SettlementNumber = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    GrossAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Commission = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Fees = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    NetAmount = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    SettledDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    BcPaymentId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LotId = table.Column<int>(type: "int", nullable: false),
                    SellerId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settlements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Settlements_Lots_LotId",
                        column: x => x.LotId,
                        principalSchema: "auction",
                        principalTable: "Lots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Settlements_Sellers_SellerId",
                        column: x => x.SellerId,
                        principalSchema: "auction",
                        principalTable: "Sellers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Auctions_AuctionNumber",
                schema: "auction",
                table: "Auctions",
                column: "AuctionNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Bids_BrokerId",
                schema: "auction",
                table: "Bids",
                column: "BrokerId");

            migrationBuilder.CreateIndex(
                name: "IX_Bids_LotId",
                schema: "auction",
                table: "Bids",
                column: "LotId");

            migrationBuilder.CreateIndex(
                name: "IX_Brokers_BrokerNumber",
                schema: "auction",
                table: "Brokers",
                column: "BrokerNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Buyers_BrokerId",
                schema: "auction",
                table: "Buyers",
                column: "BrokerId");

            migrationBuilder.CreateIndex(
                name: "IX_Buyers_BuyerNumber",
                schema: "auction",
                table: "Buyers",
                column: "BuyerNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLines_InvoiceId",
                schema: "auction",
                table: "InvoiceLines",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLines_LotId",
                schema: "auction",
                table: "InvoiceLines",
                column: "LotId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_AuctionId",
                schema: "auction",
                table: "Invoices",
                column: "AuctionId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_BrokerId",
                schema: "auction",
                table: "Invoices",
                column: "BrokerId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_InvoiceNumber",
                schema: "auction",
                table: "Invoices",
                column: "InvoiceNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LotAllocations_BrokerId",
                schema: "auction",
                table: "LotAllocations",
                column: "BrokerId");

            migrationBuilder.CreateIndex(
                name: "IX_LotAllocations_BuyerId",
                schema: "auction",
                table: "LotAllocations",
                column: "BuyerId");

            migrationBuilder.CreateIndex(
                name: "IX_LotAllocations_LotId",
                schema: "auction",
                table: "LotAllocations",
                column: "LotId");

            migrationBuilder.CreateIndex(
                name: "IX_Lots_AuctionId",
                schema: "auction",
                table: "Lots",
                column: "AuctionId");

            migrationBuilder.CreateIndex(
                name: "IX_Lots_SellerId",
                schema: "auction",
                table: "Lots",
                column: "SellerId");

            migrationBuilder.CreateIndex(
                name: "IX_Sellers_SellerNumber",
                schema: "auction",
                table: "Sellers",
                column: "SellerNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Settlements_LotId",
                schema: "auction",
                table: "Settlements",
                column: "LotId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Settlements_SellerId",
                schema: "auction",
                table: "Settlements",
                column: "SellerId");

            migrationBuilder.CreateIndex(
                name: "IX_Settlements_SettlementNumber",
                schema: "auction",
                table: "Settlements",
                column: "SettlementNumber",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Bids",
                schema: "auction");

            migrationBuilder.DropTable(
                name: "InvoiceLines",
                schema: "auction");

            migrationBuilder.DropTable(
                name: "LotAllocations",
                schema: "auction");

            migrationBuilder.DropTable(
                name: "Settlements",
                schema: "auction");

            migrationBuilder.DropTable(
                name: "Invoices",
                schema: "auction");

            migrationBuilder.DropTable(
                name: "Buyers",
                schema: "auction");

            migrationBuilder.DropTable(
                name: "Lots",
                schema: "auction");

            migrationBuilder.DropTable(
                name: "Brokers",
                schema: "auction");

            migrationBuilder.DropTable(
                name: "Auctions",
                schema: "auction");

            migrationBuilder.DropTable(
                name: "Sellers",
                schema: "auction");
        }
    }
}
