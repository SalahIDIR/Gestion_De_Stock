using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GestionStock.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOperatorBalanceUssdCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BalanceUssdCode",
                table: "Operators",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BalanceUssdCode",
                table: "Operators");
        }
    }
}
