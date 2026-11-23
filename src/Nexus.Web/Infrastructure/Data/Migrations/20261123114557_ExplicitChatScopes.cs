using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexus.Web.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExplicitChatScopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_user_blocks_organizations_organization_id",
                table: "user_blocks");

            migrationBuilder.DropPrimaryKey(
                name: "PK_user_blocks",
                table: "user_blocks");

            migrationBuilder.DropIndex(
                name: "IX_user_blocks_organization_id",
                table: "user_blocks");

            migrationBuilder.DropIndex(
                name: "ux_conversations_org_dm_key",
                table: "conversations");

            migrationBuilder.AlterColumn<Guid>(
                name: "organization_id",
                table: "users",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "organization_id",
                table: "user_reports",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<bool>(
                name: "legacy_review_pending",
                table: "organizations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<Guid>(
                name: "organization_id",
                table: "conversations",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "organization_id",
                table: "attachments",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddPrimaryKey(
                name: "PK_user_blocks",
                table: "user_blocks",
                columns: new[] { "organization_id", "blocker_user_id", "blocked_user_id" });

            migrationBuilder.CreateTable(
                name: "organization_invitations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    redeemed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    redeemed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_invitations", x => x.id);
                    table.ForeignKey(
                        name: "FK_organization_invitations_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_organization_invitations_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_organization_invitations_users_redeemed_by_user_id",
                        column: x => x.redeemed_by_user_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "organization_memberships",
                columns: table => new
                {
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    username = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    display_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    org_role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    trust_level = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    is_owner = table.Column<bool>(type: "boolean", nullable: false),
                    employee_id = table.Column<Guid>(type: "uuid", nullable: true),
                    department_id = table.Column<Guid>(type: "uuid", nullable: true),
                    branch_id = table.Column<Guid>(type: "uuid", nullable: true),
                    custom_status = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    avatar_object_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_memberships", x => new { x.organization_id, x.user_id });
                    table.ForeignKey(
                        name: "FK_organization_memberships_branches_branch_id",
                        column: x => x.branch_id,
                        principalTable: "branches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_organization_memberships_departments_department_id",
                        column: x => x.department_id,
                        principalTable: "departments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_organization_memberships_employees_employee_id",
                        column: x => x.employee_id,
                        principalTable: "employees",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_organization_memberships_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_organization_memberships_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "personal_connections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FirstUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SecondUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeclinedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_personal_connections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_personal_connections_users_FirstUserId",
                        column: x => x.FirstUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_personal_connections_users_SecondUserId",
                        column: x => x.SecondUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "personal_contact_links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_personal_contact_links", x => x.Id);
                    table.ForeignKey(
                        name: "FK_personal_contact_links_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_blocks_blocker_user_id",
                table: "user_blocks",
                column: "blocker_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_conversations_org_dm_key",
                table: "conversations",
                columns: new[] { "organization_id", "dm_key" },
                unique: true,
                filter: "dm_key IS NOT NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_organization_invitations_created_by_user_id",
                table: "organization_invitations",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_organization_invitations_organization_id_created_at",
                table: "organization_invitations",
                columns: new[] { "organization_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_organization_invitations_redeemed_by_user_id",
                table: "organization_invitations",
                column: "redeemed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_organization_invitations_token_hash",
                table: "organization_invitations",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_organization_memberships_branch_id",
                table: "organization_memberships",
                column: "branch_id");

            migrationBuilder.CreateIndex(
                name: "IX_organization_memberships_department_id",
                table: "organization_memberships",
                column: "department_id");

            migrationBuilder.CreateIndex(
                name: "IX_organization_memberships_employee_id",
                table: "organization_memberships",
                column: "employee_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_organization_memberships_organization_id",
                table: "organization_memberships",
                column: "organization_id",
                unique: true,
                filter: "is_owner");

            migrationBuilder.CreateIndex(
                name: "IX_organization_memberships_user_id_status",
                table: "organization_memberships",
                columns: new[] { "user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_personal_connections_FirstUserId_SecondUserId",
                table: "personal_connections",
                columns: new[] { "FirstUserId", "SecondUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_personal_connections_SecondUserId",
                table: "personal_connections",
                column: "SecondUserId");

            migrationBuilder.CreateIndex(
                name: "IX_personal_contact_links_TokenHash",
                table: "personal_contact_links",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_personal_contact_links_UserId",
                table: "personal_contact_links",
                column: "UserId");

            migrationBuilder.Sql("""
                UPDATE organizations SET legacy_review_pending = true;

                INSERT INTO organization_memberships
                    (organization_id, user_id, username, display_name, status, org_role, trust_level, is_owner,
                     employee_id, department_id, branch_id, custom_status, avatar_object_key, created_at)
                SELECT organization_id, "Id", "UserName", display_name, 'Former', org_role, trust_level, false,
                       employee_id, department_id, branch_id, custom_status, avatar_object_key, created_at
                FROM users WHERE organization_id IS NOT NULL;

                UPDATE admin_reset_links SET used_at = now() WHERE used_at IS NULL;
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM users WHERE organization_id IS NULL)
                       OR EXISTS (SELECT 1 FROM conversations WHERE organization_id IS NULL)
                       OR EXISTS (SELECT 1 FROM organization_memberships m JOIN users u ON u."Id" = m.user_id
                                  WHERE u.organization_id IS DISTINCT FROM m.organization_id) THEN
                        RAISE EXCEPTION 'Explicit chat scopes contain live data. Restore the pre-cutover backup or use a forward migration.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropTable(
                name: "organization_invitations");

            migrationBuilder.DropTable(
                name: "organization_memberships");

            migrationBuilder.DropTable(
                name: "personal_connections");

            migrationBuilder.DropTable(
                name: "personal_contact_links");

            migrationBuilder.DropPrimaryKey(
                name: "PK_user_blocks",
                table: "user_blocks");

            migrationBuilder.DropIndex(
                name: "IX_user_blocks_blocker_user_id",
                table: "user_blocks");

            migrationBuilder.DropIndex(
                name: "ux_conversations_org_dm_key",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "legacy_review_pending",
                table: "organizations");

            migrationBuilder.AlterColumn<Guid>(
                name: "organization_id",
                table: "users",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "organization_id",
                table: "user_reports",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "organization_id",
                table: "conversations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "organization_id",
                table: "attachments",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_user_blocks",
                table: "user_blocks",
                columns: new[] { "blocker_user_id", "blocked_user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_user_blocks_organization_id",
                table: "user_blocks",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ux_conversations_org_dm_key",
                table: "conversations",
                columns: new[] { "organization_id", "dm_key" },
                unique: true,
                filter: "dm_key IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_user_blocks_organizations_organization_id",
                table: "user_blocks",
                column: "organization_id",
                principalTable: "organizations",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
