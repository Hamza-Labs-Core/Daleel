using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Daleel.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class B2bApiCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApiPlans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MonthlyApiCredits = table.Column<int>(type: "integer", nullable: false),
                    MaxMonitoredStores = table.Column<int>(type: "integer", nullable: false),
                    WebhooksEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    MonthlyPriceUsd = table.Column<decimal>(type: "numeric(10,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiPlans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApiApplications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ContactEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    OwnerUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    ApiPlanId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiApplications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiApplications_ApiPlans_ApiPlanId",
                        column: x => x.ApiPlanId,
                        principalTable: "ApiPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ApiCreditLedger",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ApplicationId = table.Column<int>(type: "integer", nullable: false),
                    Delta = table.Column<long>(type: "bigint", nullable: false),
                    Reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiCreditLedger", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiCreditLedger_ApiApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "ApiApplications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ApiKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ApplicationId = table.Column<int>(type: "integer", nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Prefix = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Scopes = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    RevokedAt = table.Column<long>(type: "bigint", nullable: true),
                    LastUsedAt = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiKeys_ApiApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "ApiApplications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "ApiPlans",
                columns: new[] { "Id", "MaxMonitoredStores", "MonthlyApiCredits", "MonthlyPriceUsd", "Name", "WebhooksEnabled" },
                values: new object[,]
                {
                    { 1, 1, 2000, 0m, "Trial", false },
                    { 2, 2, 60000, 49m, "Starter", false },
                    { 3, 10, 600000, 199m, "Growth", true }
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiApplications_ApiPlanId",
                table: "ApiApplications",
                column: "ApiPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiApplications_OwnerUserId",
                table: "ApiApplications",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiApplications_Status",
                table: "ApiApplications",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ApiCreditLedger_ApplicationId_CreatedAt",
                table: "ApiCreditLedger",
                columns: new[] { "ApplicationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_ApplicationId",
                table: "ApiKeys",
                column: "ApplicationId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_Hash",
                table: "ApiKeys",
                column: "Hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiCreditLedger");

            migrationBuilder.DropTable(
                name: "ApiKeys");

            migrationBuilder.DropTable(
                name: "ApiApplications");

            migrationBuilder.DropTable(
                name: "ApiPlans");
        }
    }
}
