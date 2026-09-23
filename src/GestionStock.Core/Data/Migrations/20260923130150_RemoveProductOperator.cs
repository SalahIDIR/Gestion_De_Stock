using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GestionStock.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveProductOperator : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Products_Operators_OperatorId",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_OperatorId",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "OperatorId",
                table: "Products");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OperatorId",
                table: "Products",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_OperatorId",
                table: "Products",
                column: "OperatorId");

            migrationBuilder.AddForeignKey(
                name: "FK_Products_Operators_OperatorId",
                table: "Products",
                column: "OperatorId",
                principalTable: "Operators",
                principalColumn: "Id");
        }
    }
}
