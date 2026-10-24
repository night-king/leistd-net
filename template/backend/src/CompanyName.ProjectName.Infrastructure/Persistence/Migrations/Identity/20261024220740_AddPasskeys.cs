using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompanyName.ProjectName.Infrastructure.Persistence.Migrations.Identity
{
    /// <inheritdoc />
    public partial class AddPasskeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "TwoFactor_RecoveryCodes",
                schema: "companyname-projectname",
                table: "Users",
                newName: "RecoveryCodes_Hashes");

            migrationBuilder.AddColumn<int>(
                name: "PasskeyCount",
                schema: "companyname-projectname",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "UserPasskeys",
                schema: "companyname-projectname",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CredentialId = table.Column<byte[]>(type: "bytea", maxLength: 1023, nullable: false),
                    PublicKey = table.Column<byte[]>(type: "bytea", maxLength: 2048, nullable: false),
                    UserHandle = table.Column<byte[]>(type: "bytea", maxLength: 64, nullable: false),
                    SignCount = table.Column<long>(type: "bigint", nullable: false),
                    Aaguid = table.Column<Guid>(type: "uuid", nullable: false),
                    Transports = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IsBackupEligible = table.Column<bool>(type: "boolean", nullable: false),
                    IsBackedUp = table.Column<bool>(type: "boolean", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LastUsedTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatorId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPasskeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserPasskeys_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "companyname-projectname",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserPasskeys_CredentialId",
                schema: "companyname-projectname",
                table: "UserPasskeys",
                column: "CredentialId",
                unique: true,
                filter: "\"TenantId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UserPasskeys_TenantId_CredentialId",
                schema: "companyname-projectname",
                table: "UserPasskeys",
                columns: new[] { "TenantId", "CredentialId" },
                unique: true,
                filter: "\"TenantId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UserPasskeys_UserId",
                schema: "companyname-projectname",
                table: "UserPasskeys",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserPasskeys",
                schema: "companyname-projectname");

            migrationBuilder.DropColumn(
                name: "PasskeyCount",
                schema: "companyname-projectname",
                table: "Users");

            migrationBuilder.RenameColumn(
                name: "RecoveryCodes_Hashes",
                schema: "companyname-projectname",
                table: "Users",
                newName: "TwoFactor_RecoveryCodes");
        }
    }
}
