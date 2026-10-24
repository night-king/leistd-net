using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompanyName.ProjectName.Infrastructure.Persistence.Migrations.Identity
{
    /// <inheritdoc />
    public partial class AddTwoFactorMethods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EmailTwoFactorEnabled",
                schema: "companyname-projectname",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PreferredTwoFactorMethod",
                schema: "companyname-projectname",
                table: "Users",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmailTwoFactorEnabled",
                schema: "companyname-projectname",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PreferredTwoFactorMethod",
                schema: "companyname-projectname",
                table: "Users");
        }
    }
}
