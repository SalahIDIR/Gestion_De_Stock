using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GestionStock.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountClosing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountClosings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Date = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PreviousCash = table.Column<decimal>(type: "TEXT", nullable: false),
                    TotalRecettes = table.Column<decimal>(type: "TEXT", nullable: false),
                    TotalDepenses = table.Column<decimal>(type: "TEXT", nullable: false),
                    Cash = table.Column<decimal>(type: "TEXT", nullable: false),
                    StockValue = table.Column<decimal>(type: "TEXT", nullable: false),
                    ClientCredit = table.Column<decimal>(type: "TEXT", nullable: false),
                    SupplierCredit = table.Column<decimal>(type: "TEXT", nullable: false),
                    Prelevements = table.Column<decimal>(type: "TEXT", nullable: false),
                    Total = table.Column<decimal>(type: "TEXT", nullable: false),
                    Benefice = table.Column<decimal>(type: "TEXT", nullable: false),
                    Moyenne = table.Column<decimal>(type: "TEXT", nullable: false),
                    Comments = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountClosings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountClosings_Date",
                table: "AccountClosings",
                column: "Date");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountClosings");
        }
    }
}
