using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daleel.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCreditsModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CreditsUsed",
                table: "UserQuotas",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MonthlyCredits",
                table: "SubscriptionPlans",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "FeaturesJson", "MonthlyCredits" },
                values: new object[] { "[\"500 credits per month\",\"Smart product & price search\",\"Price & store comparison\",\"Brand reputation & reviews\",\"Deal monitoring & alerts\",\"Search history & saved results\",\"English & Arabic interface\"]", 500 });

            migrationBuilder.UpdateData(
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: 2,
                columns: new[] { "FeaturesJson", "MonthlyCredits" },
                values: new object[] { "[\"5,000 credits per month\",\"Smart product & price search\",\"Price & store comparison\",\"Brand reputation & reviews\",\"Deal monitoring & alerts\",\"Search history & saved results\",\"English & Arabic interface\"]", 5000 });

            migrationBuilder.UpdateData(
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: 3,
                columns: new[] { "FeaturesJson", "MonthlyCredits" },
                values: new object[] { "[\"50,000 credits per month\",\"Smart product & price search\",\"Price & store comparison\",\"Brand reputation & reviews\",\"Deal monitoring & alerts\",\"Search history & saved results\",\"English & Arabic interface\"]", 50000 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreditsUsed",
                table: "UserQuotas");

            migrationBuilder.DropColumn(
                name: "MonthlyCredits",
                table: "SubscriptionPlans");

            migrationBuilder.UpdateData(
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: 1,
                column: "FeaturesJson",
                value: "[\"5 searches per month\",\"Smart product & price search\",\"Price & store comparison\",\"Brand reputation & reviews\",\"Deal monitoring & alerts\",\"Search history & saved results\",\"English & Arabic interface\"]");

            migrationBuilder.UpdateData(
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: 2,
                column: "FeaturesJson",
                value: "[\"50 searches per month\",\"Smart product & price search\",\"Price & store comparison\",\"Brand reputation & reviews\",\"Deal monitoring & alerts\",\"Search history & saved results\",\"English & Arabic interface\"]");

            migrationBuilder.UpdateData(
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: 3,
                column: "FeaturesJson",
                value: "[\"250 searches per month\",\"Smart product & price search\",\"Price & store comparison\",\"Brand reputation & reviews\",\"Deal monitoring & alerts\",\"Search history & saved results\",\"English & Arabic interface\"]");
        }
    }
}
