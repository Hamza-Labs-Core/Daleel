using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Daleel.Web.Events.Migrations
{
    /// <inheritdoc />
    public partial class InitialEventStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pipeline_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EventType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Provider = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    SearchId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: false),
                    EstimatedCost = table.Column<decimal>(type: "numeric(12,6)", nullable: false),
                    Success = table.Column<bool>(type: "boolean", nullable: false),
                    MetadataJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pipeline_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_pipeline_events_Category_Timestamp",
                table: "pipeline_events",
                columns: new[] { "Category", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_pipeline_events_Provider_Timestamp",
                table: "pipeline_events",
                columns: new[] { "Provider", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_pipeline_events_SearchId",
                table: "pipeline_events",
                column: "SearchId");

            migrationBuilder.CreateIndex(
                name: "IX_pipeline_events_Timestamp",
                table: "pipeline_events",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pipeline_events");
        }
    }
}
