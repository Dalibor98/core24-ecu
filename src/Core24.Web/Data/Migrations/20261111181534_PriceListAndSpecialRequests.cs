using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Core24.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class PriceListAndSpecialRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Mods");

            migrationBuilder.DropColumn(
                name: "ModId",
                table: "OrderItems");

            // Renamed by hand (EF generated drop + add): existing orders keep the names of what was ordered.
            migrationBuilder.RenameColumn(
                name: "ModName",
                table: "OrderItems",
                newName: "Name");

            migrationBuilder.AddColumn<string>(
                name: "Type",
                table: "Orders",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                // Changed by hand from "": every order made before this migration had a fixed price.
                defaultValue: "OriginalFile");

            migrationBuilder.AddColumn<int>(
                name: "PriceListEntryId",
                table: "OrderItems",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PriceList",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Brand = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Engine = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Ecu = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    PriceCents = table.Column<long>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceList", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PriceList_Brand_Model_Engine_Ecu",
                table: "PriceList",
                columns: new[] { "Brand", "Model", "Engine", "Ecu" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PriceList");

            migrationBuilder.DropColumn(
                name: "Type",
                table: "Orders");

            migrationBuilder.RenameColumn(
                name: "Name",
                table: "OrderItems",
                newName: "ModName");

            migrationBuilder.DropColumn(
                name: "PriceListEntryId",
                table: "OrderItems");

            migrationBuilder.AddColumn<int>(
                name: "ModId",
                table: "OrderItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "Mods",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    PriceCents = table.Column<long>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Mods", x => x.Id);
                });
        }
    }
}
