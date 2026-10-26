using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kvytok.Domain.Migrations
{
    /// <inheritdoc />
    public partial class OutboxClaim : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClaimedAt",
                table: "OutboxMessages",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClaimedAt",
                table: "OutboxMessages");
        }
    }
}
