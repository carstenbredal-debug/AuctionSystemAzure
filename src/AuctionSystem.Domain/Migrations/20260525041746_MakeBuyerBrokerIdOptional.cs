using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuctionSystem.Domain.Migrations
{
    /// <inheritdoc />
    public partial class MakeBuyerBrokerIdOptional : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Buyers_Brokers_BrokerId",
                schema: "auction",
                table: "Buyers");

            migrationBuilder.AlterColumn<int>(
                name: "BrokerId",
                schema: "auction",
                table: "Buyers",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddForeignKey(
                name: "FK_Buyers_Brokers_BrokerId",
                schema: "auction",
                table: "Buyers",
                column: "BrokerId",
                principalSchema: "auction",
                principalTable: "Brokers",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Buyers_Brokers_BrokerId",
                schema: "auction",
                table: "Buyers");

            migrationBuilder.AlterColumn<int>(
                name: "BrokerId",
                schema: "auction",
                table: "Buyers",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Buyers_Brokers_BrokerId",
                schema: "auction",
                table: "Buyers",
                column: "BrokerId",
                principalSchema: "auction",
                principalTable: "Brokers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
