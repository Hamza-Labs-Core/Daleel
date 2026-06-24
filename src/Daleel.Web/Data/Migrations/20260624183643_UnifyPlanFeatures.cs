using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daleel.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class UnifyPlanFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                value: "[\"100 searches per month\",\"Smart product & price search\",\"Price & store comparison\",\"Brand reputation & reviews\",\"Deal monitoring & alerts\",\"Search history & saved results\",\"English & Arabic interface\"]");

            migrationBuilder.UpdateData(
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: 3,
                column: "FeaturesJson",
                value: "[\"Unlimited searches\",\"Smart product & price search\",\"Price & store comparison\",\"Brand reputation & reviews\",\"Deal monitoring & alerts\",\"Search history & saved results\",\"English & Arabic interface\"]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: 1,
                column: "FeaturesJson",
                value: "[\"5 searches per month\",\"Halal-filtered results\",\"Up to 10 saved results\"]");

            migrationBuilder.UpdateData(
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: 2,
                column: "FeaturesJson",
                value: "[\"100 searches per month\",\"Full results & JSON export\",\"Unlimited saved results\",\"Priority models\"]");

            migrationBuilder.UpdateData(
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: 3,
                column: "FeaturesJson",
                value: "[\"Unlimited searches\",\"Everything in Pro\",\"Priority support\"]");
        }
    }
}
