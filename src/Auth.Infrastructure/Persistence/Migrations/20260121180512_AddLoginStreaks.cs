using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auth.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLoginStreaks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LoginStreaks",
                columns: table => new
                {
                    IdentifierHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LockedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    BurstStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    BurstCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoginStreaks", x => x.IdentifierHash);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LoginStreaks_LastAttemptAt",
                table: "LoginStreaks",
                column: "LastAttemptAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LoginStreaks");
        }
    }
}
