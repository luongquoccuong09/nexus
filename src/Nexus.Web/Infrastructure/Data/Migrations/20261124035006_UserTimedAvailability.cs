using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Web.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class UserTimedAvailability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "availability_expires_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "availability_state",
                table: "users",
                type: "text",
                nullable: false,
                defaultValue: "Available");

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_availability_expires_at",
                table: "users",
                sql: "availability_state <> 'Available' OR availability_expires_at IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_users_availability_state",
                table: "users",
                sql: "availability_state IN ('Available', 'Busy', 'Away', 'DoNotDisturb')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_users_availability_expires_at",
                table: "users");

            migrationBuilder.DropCheckConstraint(
                name: "ck_users_availability_state",
                table: "users");

            migrationBuilder.DropColumn(
                name: "availability_expires_at",
                table: "users");

            migrationBuilder.DropColumn(
                name: "availability_state",
                table: "users");
        }
    }
}
