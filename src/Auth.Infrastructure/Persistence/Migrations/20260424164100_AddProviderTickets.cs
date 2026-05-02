using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auth.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderTickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProviderTickets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TicketHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    BindingHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    Purpose = table.Column<short>(type: "smallint", nullable: false),
                    Provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IdentityKey = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Email = table.Column<string>(type: "text", nullable: true),
                    EmailVerified = table.Column<bool>(type: "boolean", nullable: false),
                    InviteTokenHash = table.Column<byte[]>(type: "bytea", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderTickets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderTickets_ExpiresAt",
                table: "ProviderTickets",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderTickets_TicketHash",
                table: "ProviderTickets",
                column: "TicketHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProviderTickets");
        }
    }
}
