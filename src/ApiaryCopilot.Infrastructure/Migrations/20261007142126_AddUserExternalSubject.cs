using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApiaryCopilot.Infrastructure.Migrations
{
    // Additive (DEPLOYMENT.md §9): a nullable column - existing users, the staging seed's included, have no subject until
    // one is set - and a unique index, so one Keycloak subject is never two users. The previous image ignores both.
    /// <inheritdoc />
    public partial class AddUserExternalSubject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalSubject",
                table: "Users",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_ExternalSubject",
                table: "Users",
                column: "ExternalSubject",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_ExternalSubject",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ExternalSubject",
                table: "Users");
        }
    }
}
