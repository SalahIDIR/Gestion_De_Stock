using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GestionStock.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierOpeningBalance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "OpeningBalance",
                table: "Suppliers",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OpeningBalance",
                table: "Suppliers");
        }
    }
}
