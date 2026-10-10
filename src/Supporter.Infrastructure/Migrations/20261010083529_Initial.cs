using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Supporter.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Inbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Digest = table.Column<string>(type: "TEXT", nullable: false),
                    CampaignId = table.Column<string>(type: "TEXT", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", nullable: false),
                    ReceivedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DeadLetter = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Inbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OAuthAttempts",
                columns: table => new
                {
                    StateHash = table.Column<string>(type: "TEXT", nullable: false),
                    SessionHash = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OAuthAttempts", x => x.StateHash);
                });

            migrationBuilder.CreateTable(
                name: "People",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    ExternalUserId = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    HasSupported = table.Column<bool>(type: "INTEGER", nullable: false),
                    PublicOptIn = table.Column<bool>(type: "INTEGER", nullable: false),
                    Pseudonym = table.Column<string>(type: "TEXT", nullable: true),
                    ConsentChangedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSuccessfulSyncAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_People", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncStates",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    LastAttemptAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSuccessfulSyncAt = table.Column<long>(type: "INTEGER", nullable: true),
                    NextAllowedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastErrorCode = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tokens",
                columns: table => new
                {
                    Owner = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedTokens = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tokens", x => x.Owner);
                });

            migrationBuilder.CreateTable(
                name: "Credentials",
                columns: table => new
                {
                    Hash = table.Column<string>(type: "TEXT", nullable: false),
                    PersonId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RevokedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Credentials", x => x.Hash);
                    table.ForeignKey(
                        name: "FK_Credentials_People_PersonId",
                        column: x => x.PersonId,
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Memberships",
                columns: table => new
                {
                    PersonId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CampaignId = table.Column<string>(type: "TEXT", nullable: false),
                    MemberId = table.Column<string>(type: "TEXT", nullable: true),
                    PatronStatus = table.Column<string>(type: "TEXT", nullable: true),
                    LastChargeStatus = table.Column<string>(type: "TEXT", nullable: true),
                    HistoricalEvidence = table.Column<bool>(type: "INTEGER", nullable: false),
                    EntitledAmountCents = table.Column<long>(type: "INTEGER", nullable: true),
                    TierIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    LastSuccessfulSyncAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Memberships", x => x.PersonId);
                    table.ForeignKey(
                        name: "FK_Memberships_People_PersonId",
                        column: x => x.PersonId,
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Credentials_PersonId",
                table: "Credentials",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_Inbox_CompletedAt_NextAttemptAt",
                table: "Inbox",
                columns: new[] { "CompletedAt", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Inbox_Digest",
                table: "Inbox",
                column: "Digest",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Memberships_CampaignId_MemberId",
                table: "Memberships",
                columns: new[] { "CampaignId", "MemberId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_People_Provider_ExternalUserId",
                table: "People",
                columns: new[] { "Provider", "ExternalUserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Credentials");

            migrationBuilder.DropTable(
                name: "Inbox");

            migrationBuilder.DropTable(
                name: "Memberships");

            migrationBuilder.DropTable(
                name: "OAuthAttempts");

            migrationBuilder.DropTable(
                name: "SyncStates");

            migrationBuilder.DropTable(
                name: "Tokens");

            migrationBuilder.DropTable(
                name: "People");
        }
    }
}
