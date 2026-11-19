using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentForge.Verticals.Travel.Checkout.Migrations
{
    /// <inheritdoc />
    public partial class InitialTravelCheckout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "travel_bookings",
                columns: table => new
                {
                    reference = table.Column<string>(type: "text", nullable: false),
                    checkout_id = table.Column<string>(type: "text", nullable: false),
                    scope_key = table.Column<string>(type: "text", nullable: false),
                    hold_reference = table.Column<string>(type: "text", nullable: false),
                    quote_reference = table.Column<string>(type: "text", nullable: false),
                    paid_amount = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    payment_id = table.Column<string>(type: "text", nullable: false),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    notification_status = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_travel_bookings", x => x.reference);
                });

            migrationBuilder.CreateTable(
                name: "travel_checkout_attempts",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    scope_key = table.Column<string>(type: "text", nullable: false),
                    partition_key = table.Column<string>(type: "text", nullable: false),
                    hold_reference = table.Column<string>(type: "text", nullable: false),
                    quote_reference = table.Column<string>(type: "text", nullable: false),
                    quote_json = table.Column<string>(type: "jsonb", nullable: false),
                    account_id = table.Column<string>(type: "text", nullable: false),
                    chat_id = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<long>(type: "bigint", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    provider_link_id = table.Column<string>(type: "text", nullable: true),
                    url = table.Column<string>(type: "text", nullable: true),
                    payment_id = table.Column<string>(type: "text", nullable: true),
                    notification_status = table.Column<string>(type: "text", nullable: false),
                    review_reason = table.Column<string>(type: "text", nullable: true),
                    reconcile_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_travel_checkout_attempts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "travel_payment_events",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    partition_key = table.Column<string>(type: "text", nullable: false),
                    link_id = table.Column<string>(type: "text", nullable: false),
                    reference = table.Column<string>(type: "text", nullable: false),
                    payment_id = table.Column<string>(type: "text", nullable: true),
                    kind = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    body_hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_travel_payment_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_travel_bookings_checkout_id",
                table: "travel_bookings",
                column: "checkout_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_travel_bookings_hold_reference",
                table: "travel_bookings",
                column: "hold_reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_travel_bookings_payment_id",
                table: "travel_bookings",
                column: "payment_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_travel_checkout_attempts_hold_reference",
                table: "travel_checkout_attempts",
                column: "hold_reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_travel_checkout_attempts_partition_key_status",
                table: "travel_checkout_attempts",
                columns: new[] { "partition_key", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_travel_payment_events_partition_key_status",
                table: "travel_payment_events",
                columns: new[] { "partition_key", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "travel_bookings");

            migrationBuilder.DropTable(
                name: "travel_checkout_attempts");

            migrationBuilder.DropTable(
                name: "travel_payment_events");
        }
    }
}
