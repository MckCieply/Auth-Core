using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auth.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionStatesAndNotificationMails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "MailRequests",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SessionStates",
                columns: table => new
                {
                    AuthorizationId = table.Column<string>(type: "text", nullable: false),
                    SessionStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConfirmedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionStates", x => x.AuthorizationId);
                    table.ForeignKey(
                        name: "FK_SessionStates_OpenIddictAuthorizations_AuthorizationId",
                        column: x => x.AuthorizationId,
                        principalTable: "OpenIddictAuthorizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SessionStates");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "MailRequests");
        }
    }
}
