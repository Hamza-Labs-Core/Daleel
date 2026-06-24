using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daleel.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AdjustProUnlimitedQuotas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: 2,
                columns: new[] { "FeaturesJson", "SearchesPerMonth" },
                values: new object[] { "[\"50 searches per month\",\"Smart product & price search\",\"Price & store comparison\",\"Brand reputation & reviews\",\"Deal monitoring & alerts\",\"Search history & saved results\",\"English & Arabic interface\"]", 50 });

            migrationBuilder.UpdateData(
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: 3,
                columns: new[] { "FeaturesJson", "SearchesPerMonth" },
                values: new object[] { "[\"250 searches per month\",\"Smart product & price search\",\"Price & store comparison\",\"Brand reputation & reviews\",\"Deal monitoring & alerts\",\"Search history & saved results\",\"English & Arabic interface\"]", 250 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: 2,
                columns: new[] { "FeaturesJson", "SearchesPerMonth" },
                values: new object[] { "[\"100 searches per month\",\"Smart product & price search\",\"Price & store comparison\",\"Brand reputation & reviews\",\"Deal monitoring & alerts\",\"Search history & saved results\",\"English & Arabic interface\"]", 100 });

            migrationBuilder.UpdateData(
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: 3,
                columns: new[] { "FeaturesJson", "SearchesPerMonth" },
                values: new object[] { "[\"Unlimited searches\",\"Smart product & price search\",\"Price & store comparison\",\"Brand reputation & reviews\",\"Deal monitoring & alerts\",\"Search history & saved results\",\"English & Arabic interface\"]", null });
        }
    }
}
