using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SRSS.IAM.Repositories.Migrations
{
    /// <inheritdoc />
    public partial class FixProjectInvitationContract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Role",
                table: "project_member_invitations",
                newName: "role");

            migrationBuilder.CreateIndex(
                name: "IX_project_member_invitations_project_id_invited_user_id_status",
                table: "project_member_invitations",
                columns: new[] { "project_id", "invited_user_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_project_member_invitations_project_id_invited_user_id_status",
                table: "project_member_invitations");

            migrationBuilder.RenameColumn(
                name: "role",
                table: "project_member_invitations",
                newName: "Role");
        }
    }
}
