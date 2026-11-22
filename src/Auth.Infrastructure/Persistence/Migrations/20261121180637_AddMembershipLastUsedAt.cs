using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Auth.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMembershipLastUsedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastUsedAt",
                table: "Memberships",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastUsedAt",
                table: "Memberships");
        }
    }
}
