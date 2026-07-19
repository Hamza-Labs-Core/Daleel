using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Daleel.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class StoreInventoryMonitor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LastInventoryCount",
                table: "Stores",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastInventorySyncAt",
                table: "Stores",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MonitorCadenceHours",
                table: "Stores",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "MonitorEnabled",
                table: "Stores",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSeenAt",
                table: "ScrapedPrices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StoreCatalogPages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Domain = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Url = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LastSeenAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoreCatalogPages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StoreCatalogPages_Domain_Url",
                table: "StoreCatalogPages",
                columns: new[] { "Domain", "Url" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StoreCatalogPages");

            migrationBuilder.DropColumn(
                name: "LastInventoryCount",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "LastInventorySyncAt",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "MonitorCadenceHours",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "MonitorEnabled",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "LastSeenAt",
                table: "ScrapedPrices");
        }
    }
}
