using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuctionSystem.Domain.Migrations
{
    /// <inheritdoc />
    public partial class AddTakebackInitiatedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InitiatedBy",
                schema: "auction",
                table: "TakebackRequests",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Broker");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InitiatedBy",
                schema: "auction",
                table: "TakebackRequests");
        }
    }
}
