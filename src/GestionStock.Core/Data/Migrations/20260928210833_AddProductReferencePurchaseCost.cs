using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GestionStock.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProductReferencePurchaseCost : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ReferencePurchaseCost",
                table: "Products",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReferencePurchaseCost",
                table: "Products");
        }
    }
}
