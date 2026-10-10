using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Supporter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSupportStartedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "SupportStartedAt",
                table: "Memberships",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SupportStartedAt",
                table: "Memberships");
        }
    }
}
