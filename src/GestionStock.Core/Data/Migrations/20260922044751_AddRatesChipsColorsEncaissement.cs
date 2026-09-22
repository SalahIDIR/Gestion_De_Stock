using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GestionStock.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRatesChipsColorsEncaissement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ClientChips_ClientId_OperatorId",
                table: "ClientChips");

            migrationBuilder.AddColumn<string>(
                name: "ColorHex",
                table: "Products",
                type: "TEXT",
                nullable: false,
                defaultValue: "#6B7280");

            migrationBuilder.AddColumn<string>(
                name: "RecipientPhone",
                table: "DeliveryLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Slot",
                table: "ClientChips",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "ClientProductRates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ClientId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProductId = table.Column<int>(type: "INTEGER", nullable: false),
                    Rate = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientProductRates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientProductRates_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientProductRates_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierProductRates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SupplierId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProductId = table.Column<int>(type: "INTEGER", nullable: false),
                    Rate = table.Column<decimal>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierProductRates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierProductRates_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierProductRates_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientChips_ClientId_OperatorId_Slot",
                table: "ClientChips",
                columns: new[] { "ClientId", "OperatorId", "Slot" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientProductRates_ClientId_ProductId",
                table: "ClientProductRates",
                columns: new[] { "ClientId", "ProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientProductRates_ProductId",
                table: "ClientProductRates",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierProductRates_ProductId",
                table: "SupplierProductRates",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierProductRates_SupplierId_ProductId",
                table: "SupplierProductRates",
                columns: new[] { "SupplierId", "ProductId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientProductRates");

            migrationBuilder.DropTable(
                name: "SupplierProductRates");

            migrationBuilder.DropIndex(
                name: "IX_ClientChips_ClientId_OperatorId_Slot",
                table: "ClientChips");

            migrationBuilder.DropColumn(
                name: "ColorHex",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "RecipientPhone",
                table: "DeliveryLines");

            migrationBuilder.DropColumn(
                name: "Slot",
                table: "ClientChips");

            migrationBuilder.CreateIndex(
                name: "IX_ClientChips_ClientId_OperatorId",
                table: "ClientChips",
                columns: new[] { "ClientId", "OperatorId" },
                unique: true);
        }
    }
}
