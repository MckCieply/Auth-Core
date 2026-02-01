using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Auth.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailFlows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailTokens",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<short>(type: "smallint", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailTokens", x => new { x.UserId, x.Kind });
                    table.ForeignKey(
                        name: "FK_EmailTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MailRequestLimits",
                columns: table => new
                {
                    IdentifierHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    Kind = table.Column<short>(type: "smallint", nullable: false),
                    WindowStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    WindowCount = table.Column<int>(type: "integer", nullable: false),
                    LastAcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailRequestLimits", x => new { x.IdentifierHash, x.Kind });
                });

            migrationBuilder.CreateTable(
                name: "MailRequests",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Kind = table.Column<short>(type: "smallint", nullable: false),
                    NormalizedEmail = table.Column<string>(type: "text", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailTokens_ExpiresAt",
                table: "EmailTokens",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_EmailTokens_TokenHash",
                table: "EmailTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MailRequestLimits_LastAcceptedAt",
                table: "MailRequestLimits",
                column: "LastAcceptedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MailRequests_NextAttemptAt",
                table: "MailRequests",
                column: "NextAttemptAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailTokens");

            migrationBuilder.DropTable(
                name: "MailRequestLimits");

            migrationBuilder.DropTable(
                name: "MailRequests");
        }
    }
}
