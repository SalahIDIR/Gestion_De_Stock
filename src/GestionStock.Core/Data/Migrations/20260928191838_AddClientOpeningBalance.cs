using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GestionStock.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClientOpeningBalance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "OpeningBalance",
                table: "Clients",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OpeningBalance",
                table: "Clients");
        }
    }
}
