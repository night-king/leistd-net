using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompanyName.ProjectName.Infrastructure.Persistence.Migrations.Identity
{
    /// <inheritdoc />
    public partial class AddEmailDerivedSignInMarkers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PasswordSetByEmailRecovery",
                schema: "companyname-projectname",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);
#if (ExternalLogin)

            migrationBuilder.AddColumn<bool>(
                name: "LinkedByEmail",
                schema: "companyname-projectname",
                table: "ExternalLoginConnections",
                type: "boolean",
                nullable: false,
                defaultValue: false);
#endif
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PasswordSetByEmailRecovery",
                schema: "companyname-projectname",
                table: "Users");
#if (ExternalLogin)

            migrationBuilder.DropColumn(
                name: "LinkedByEmail",
                schema: "companyname-projectname",
                table: "ExternalLoginConnections");
#endif
        }
    }
}
