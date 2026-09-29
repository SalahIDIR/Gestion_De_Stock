using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GestionStock.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOperatorUssdConfirmation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConfirmKeystroke",
                table: "Operators",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ConfirmationViaSms",
                table: "Operators",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SuccessKeyword",
                table: "Operators",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConfirmKeystroke",
                table: "Operators");

            migrationBuilder.DropColumn(
                name: "ConfirmationViaSms",
                table: "Operators");

            migrationBuilder.DropColumn(
                name: "SuccessKeyword",
                table: "Operators");
        }
    }
}
