using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompanyName.ProjectName.Infrastructure.Persistence.Migrations.Resource
{
    /// <inheritdoc />
    public partial class InitialResource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "companyname-projectname");

            migrationBuilder.CreateTable(
                name: "AuthorizationVersionRecord",
                schema: "companyname-projectname",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProviderName = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    LastModificationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastModifierId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthorizationVersionRecord", x => x.Id);
                });

#if (IncludeNotifications)
            migrationBuilder.CreateTable(
                name: "NotificationRecord",
                schema: "companyname-projectname",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Title = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Content = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Link = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Icon = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsRead = table.Column<bool>(type: "boolean", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RelatedEntityId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    RelatedEntityType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    MetadataJson = table.Column<string>(type: "text", nullable: true),
                    CreationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatorId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationRecord", x => x.Id);
                });
#endif

#if (IncludeOperationRecords)
            migrationBuilder.CreateTable(
                name: "OperationRecordArchives",
                schema: "companyname-projectname",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorTenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    TargetId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    AuthorizationBasis = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ArchivedTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ActorId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ActorName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    ImpersonatorId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ImpersonatorName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TargetName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    Visibility = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Host"),
                    FailureCode = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    FailureData = table.Column<string>(type: "text", nullable: true),
                    FailureDetail = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationRecordArchives", x => x.Id);
                });
#endif

#if (IncludeOperationRecords)
            migrationBuilder.CreateTable(
                name: "OperationRecords",
                schema: "companyname-projectname",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorTenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    TargetId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    AuthorizationBasis = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ActorId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ActorName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    ImpersonatorId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ImpersonatorName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TargetName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    Visibility = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Host"),
                    FailureCode = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    FailureData = table.Column<string>(type: "text", nullable: true),
                    FailureDetail = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationRecords", x => x.Id);
                });
#endif

            migrationBuilder.CreateTable(
                name: "PermissionGrantRecords",
                schema: "companyname-projectname",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    PermissionName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ProviderName = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProviderKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatorId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermissionGrantRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecurringJobStates",
                schema: "companyname-projectname",
                columns: table => new
                {
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    LastCompletedSlot = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringJobStates", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                schema: "companyname-projectname",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    IsStatic = table.Column<bool>(type: "boolean", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    Sort = table.Column<int>(type: "integer", nullable: false),
                    CreatorId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastModifierId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastModificationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeleterId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DeletionTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SettingRecord",
                schema: "companyname-projectname",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ScopeKey = table.Column<string>(type: "character varying(192)", maxLength: 192, nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Value = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SettingRecord", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                schema: "companyname-projectname",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Avatar = table.Column<string>(type: "text", nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsSuperAdmin = table.Column<bool>(type: "boolean", nullable: false),
                    CreatorId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastModifierId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastModificationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeleterId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DeletionTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.CheckConstraint("CK_User_SuperAdminIsHostOnly", "NOT (\"IsSuperAdmin\" AND \"TenantId\" IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "UserRoles",
                schema: "companyname-projectname",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatorId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastModifierId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastModificationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    DeleterId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DeletionTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserRoles_Roles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "companyname-projectname",
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserRoles_Users_UserId",
                        column: x => x.UserId,
                        principalSchema: "companyname-projectname",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuthorizationVersionRecord_ProviderName_ProviderKey",
                schema: "companyname-projectname",
                table: "AuthorizationVersionRecord",
                columns: new[] { "ProviderName", "ProviderKey" },
                unique: true,
                filter: "\"TenantId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AuthorizationVersionRecord_TenantId_ProviderName_ProviderKey",
                schema: "companyname-projectname",
                table: "AuthorizationVersionRecord",
                columns: new[] { "TenantId", "ProviderName", "ProviderKey" },
                unique: true,
                filter: "\"TenantId\" IS NOT NULL");

#if (IncludeNotifications)
            migrationBuilder.CreateIndex(
                name: "IX_NotificationRecord_CreationTime",
                schema: "companyname-projectname",
                table: "NotificationRecord",
                column: "CreationTime");
#endif

#if (IncludeNotifications)
            migrationBuilder.CreateIndex(
                name: "IX_NotificationRecord_UserId_CreationTime",
                schema: "companyname-projectname",
                table: "NotificationRecord",
                columns: new[] { "UserId", "CreationTime" });
#endif

#if (IncludeNotifications)
            migrationBuilder.CreateIndex(
                name: "IX_NotificationRecord_UserId_IsRead",
                schema: "companyname-projectname",
                table: "NotificationRecord",
                columns: new[] { "UserId", "IsRead" });
#endif

#if (IncludeOperationRecords)
            migrationBuilder.CreateIndex(
                name: "IX_OperationRecordArchives_TenantId_CreationTime",
                schema: "companyname-projectname",
                table: "OperationRecordArchives",
                columns: new[] { "TenantId", "CreationTime" },
                descending: new[] { false, true });
#endif

#if (IncludeOperationRecords)
            migrationBuilder.CreateIndex(
                name: "IX_OperationRecords_CreationTime",
                schema: "companyname-projectname",
                table: "OperationRecords",
                column: "CreationTime");
#endif

#if (IncludeOperationRecords)
            migrationBuilder.CreateIndex(
                name: "IX_OperationRecords_TenantId_CreationTime",
                schema: "companyname-projectname",
                table: "OperationRecords",
                columns: new[] { "TenantId", "CreationTime" },
                descending: new[] { false, true });
#endif

#if (IncludeOperationRecords)
            migrationBuilder.CreateIndex(
                name: "IX_OperationRecords_TenantId_Visibility_CreationTime",
                schema: "companyname-projectname",
                table: "OperationRecords",
                columns: new[] { "TenantId", "Visibility", "CreationTime" },
                descending: new[] { false, false, true });
#endif

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGrantRecords_PermissionName_ProviderName_Provider~",
                schema: "companyname-projectname",
                table: "PermissionGrantRecords",
                columns: new[] { "PermissionName", "ProviderName", "ProviderKey" },
                unique: true,
                filter: "\"TenantId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGrantRecords_TenantId_PermissionName_ProviderName~",
                schema: "companyname-projectname",
                table: "PermissionGrantRecords",
                columns: new[] { "TenantId", "PermissionName", "ProviderName", "ProviderKey" },
                unique: true,
                filter: "\"TenantId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PermissionGrantRecords_TenantId_ProviderName_ProviderKey",
                schema: "companyname-projectname",
                table: "PermissionGrantRecords",
                columns: new[] { "TenantId", "ProviderName", "ProviderKey" });

            migrationBuilder.CreateIndex(
                name: "IX_SettingRecord_ScopeKey_Name",
                schema: "companyname-projectname",
                table: "SettingRecord",
                columns: new[] { "ScopeKey", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_RoleId",
                schema: "companyname-projectname",
                table: "UserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_UserId_RoleId",
                schema: "companyname-projectname",
                table: "UserRoles",
                columns: new[] { "UserId", "RoleId" },
                unique: true,
                filter: "NOT \"IsDeleted\"");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                schema: "companyname-projectname",
                table: "Users",
                column: "Email",
                unique: true,
                filter: "\"TenantId\" IS NULL AND \"Email\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_Users_TenantId_Email",
                schema: "companyname-projectname",
                table: "Users",
                columns: new[] { "TenantId", "Email" },
                unique: true,
                filter: "\"TenantId\" IS NOT NULL AND \"Email\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_Users_TenantId_Username",
                schema: "companyname-projectname",
                table: "Users",
                columns: new[] { "TenantId", "Username" },
                unique: true,
                filter: "\"TenantId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                schema: "companyname-projectname",
                table: "Users",
                column: "Username",
                unique: true,
                filter: "\"TenantId\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuthorizationVersionRecord",
                schema: "companyname-projectname");

#if (IncludeNotifications)
            migrationBuilder.DropTable(
                name: "NotificationRecord",
                schema: "companyname-projectname");
#endif

#if (IncludeOperationRecords)
            migrationBuilder.DropTable(
                name: "OperationRecordArchives",
                schema: "companyname-projectname");
#endif

#if (IncludeOperationRecords)
            migrationBuilder.DropTable(
                name: "OperationRecords",
                schema: "companyname-projectname");
#endif

            migrationBuilder.DropTable(
                name: "PermissionGrantRecords",
                schema: "companyname-projectname");

            migrationBuilder.DropTable(
                name: "RecurringJobStates",
                schema: "companyname-projectname");

            migrationBuilder.DropTable(
                name: "SettingRecord",
                schema: "companyname-projectname");

            migrationBuilder.DropTable(
                name: "UserRoles",
                schema: "companyname-projectname");

            migrationBuilder.DropTable(
                name: "Roles",
                schema: "companyname-projectname");

            migrationBuilder.DropTable(
                name: "Users",
                schema: "companyname-projectname");
        }
    }
}
