using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ApiaryCopilot.Infrastructure.Migrations
{
    // The one initial migration, account-rooted: it replaced InitialCreate, AddYards, AddColonies, AddInspections and
    // AddInspectionMedia before any database outside a developer's machine existed (DEPLOYMENT.md §9).

    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Accounts",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.AccountId);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_Users_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "AccountId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Yards",
                columns: table => new
                {
                    YardId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastModifiedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    LastModifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Latitude = table.Column<double>(type: "double precision", nullable: false),
                    Longitude = table.Column<double>(type: "double precision", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    County = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AccessNotes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ForageNotes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    LandownerContact = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    LandownerTerms = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    NearbyCrops = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SprayContact = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Yards", x => x.YardId);
                    table.ForeignKey(
                        name: "FK_Yards_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "AccountId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Yards_Users_CreatedBy",
                        column: x => x.CreatedBy,
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Yards_Users_LastModifiedBy",
                        column: x => x.LastModifiedBy,
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Colonies",
                columns: table => new
                {
                    ColonyId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastModifiedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    LastModifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TagId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    HiveNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    HiveType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    BoxCount = table.Column<int>(type: "integer", nullable: false),
                    Origin = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ParentColonyId = table.Column<Guid>(type: "uuid", nullable: true),
                    YardId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MergedIntoColonyId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Colonies", x => x.ColonyId);
                    table.ForeignKey(
                        name: "FK_Colonies_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "AccountId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Colonies_Colonies_MergedIntoColonyId",
                        column: x => x.MergedIntoColonyId,
                        principalTable: "Colonies",
                        principalColumn: "ColonyId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Colonies_Colonies_ParentColonyId",
                        column: x => x.ParentColonyId,
                        principalTable: "Colonies",
                        principalColumn: "ColonyId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Colonies_Users_CreatedBy",
                        column: x => x.CreatedBy,
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Colonies_Users_LastModifiedBy",
                        column: x => x.LastModifiedBy,
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Colonies_Yards_YardId",
                        column: x => x.YardId,
                        principalTable: "Yards",
                        principalColumn: "YardId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ColonyEvents",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastModifiedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    LastModifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ColonyId = table.Column<Guid>(type: "uuid", nullable: false),
                    RelatedColonyId = table.Column<Guid>(type: "uuid", nullable: true),
                    OccurredOn = table.Column<DateOnly>(type: "date", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Data = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ColonyEvents", x => x.EventId);
                    table.ForeignKey(
                        name: "FK_ColonyEvents_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "AccountId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ColonyEvents_Colonies_ColonyId",
                        column: x => x.ColonyId,
                        principalTable: "Colonies",
                        principalColumn: "ColonyId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ColonyEvents_Colonies_RelatedColonyId",
                        column: x => x.RelatedColonyId,
                        principalTable: "Colonies",
                        principalColumn: "ColonyId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ColonyEvents_Users_CreatedBy",
                        column: x => x.CreatedBy,
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ColonyEvents_Users_LastModifiedBy",
                        column: x => x.LastModifiedBy,
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Inspections",
                columns: table => new
                {
                    InspectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastModifiedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    LastModifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ColonyId = table.Column<Guid>(type: "uuid", nullable: false),
                    InspectedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Findings = table.Column<string>(type: "jsonb", nullable: false),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Inspections", x => x.InspectionId);
                    table.ForeignKey(
                        name: "FK_Inspections_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "AccountId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Inspections_Colonies_ColonyId",
                        column: x => x.ColonyId,
                        principalTable: "Colonies",
                        principalColumn: "ColonyId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Inspections_Users_CreatedBy",
                        column: x => x.CreatedBy,
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Inspections_Users_LastModifiedBy",
                        column: x => x.LastModifiedBy,
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Queens",
                columns: table => new
                {
                    QueenId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastModifiedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    LastModifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ColonyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    MarkColour = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    MarkYear = table.Column<int>(type: "integer", nullable: true),
                    IntroducedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    ReplacedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ReplacedByQueenId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Queens", x => x.QueenId);
                    table.ForeignKey(
                        name: "FK_Queens_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "AccountId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Queens_Colonies_ColonyId",
                        column: x => x.ColonyId,
                        principalTable: "Colonies",
                        principalColumn: "ColonyId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Queens_Queens_ReplacedByQueenId",
                        column: x => x.ReplacedByQueenId,
                        principalTable: "Queens",
                        principalColumn: "QueenId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Queens_Users_CreatedBy",
                        column: x => x.CreatedBy,
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Queens_Users_LastModifiedBy",
                        column: x => x.LastModifiedBy,
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InspectionMedia",
                columns: table => new
                {
                    MediaId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastModifiedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    LastModifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    InspectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ObjectKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    AttachedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InspectionMedia", x => x.MediaId);
                    table.ForeignKey(
                        name: "FK_InspectionMedia_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "AccountId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InspectionMedia_Inspections_InspectionId",
                        column: x => x.InspectionId,
                        principalTable: "Inspections",
                        principalColumn: "InspectionId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InspectionMedia_Users_CreatedBy",
                        column: x => x.CreatedBy,
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InspectionMedia_Users_LastModifiedBy",
                        column: x => x.LastModifiedBy,
                        principalTable: "Users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Colonies_AccountId_TagId",
                table: "Colonies",
                columns: new[] { "AccountId", "TagId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Colonies_CreatedBy",
                table: "Colonies",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Colonies_LastModifiedBy",
                table: "Colonies",
                column: "LastModifiedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Colonies_MergedIntoColonyId",
                table: "Colonies",
                column: "MergedIntoColonyId");

            migrationBuilder.CreateIndex(
                name: "IX_Colonies_ParentColonyId",
                table: "Colonies",
                column: "ParentColonyId");

            migrationBuilder.CreateIndex(
                name: "IX_Colonies_YardId",
                table: "Colonies",
                column: "YardId");

            migrationBuilder.CreateIndex(
                name: "IX_ColonyEvents_AccountId",
                table: "ColonyEvents",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ColonyEvents_ColonyId_RecordedAt",
                table: "ColonyEvents",
                columns: new[] { "ColonyId", "RecordedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ColonyEvents_CreatedBy",
                table: "ColonyEvents",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_ColonyEvents_LastModifiedBy",
                table: "ColonyEvents",
                column: "LastModifiedBy");

            migrationBuilder.CreateIndex(
                name: "IX_ColonyEvents_RelatedColonyId",
                table: "ColonyEvents",
                column: "RelatedColonyId");

            migrationBuilder.CreateIndex(
                name: "IX_InspectionMedia_AccountId",
                table: "InspectionMedia",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_InspectionMedia_CreatedBy",
                table: "InspectionMedia",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_InspectionMedia_InspectionId_AttachedAt",
                table: "InspectionMedia",
                columns: new[] { "InspectionId", "AttachedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_InspectionMedia_LastModifiedBy",
                table: "InspectionMedia",
                column: "LastModifiedBy");

            migrationBuilder.CreateIndex(
                name: "IX_InspectionMedia_ObjectKey",
                table: "InspectionMedia",
                column: "ObjectKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Inspections_AccountId",
                table: "Inspections",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Inspections_ColonyId_InspectedOn_RecordedAt",
                table: "Inspections",
                columns: new[] { "ColonyId", "InspectedOn", "RecordedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Inspections_CreatedBy",
                table: "Inspections",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Inspections_LastModifiedBy",
                table: "Inspections",
                column: "LastModifiedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Queens_AccountId",
                table: "Queens",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Queens_ColonyId",
                table: "Queens",
                column: "ColonyId");

            migrationBuilder.CreateIndex(
                name: "IX_Queens_CreatedBy",
                table: "Queens",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Queens_LastModifiedBy",
                table: "Queens",
                column: "LastModifiedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Queens_ReplacedByQueenId",
                table: "Queens",
                column: "ReplacedByQueenId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_AccountId",
                table: "Users",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Yards_AccountId",
                table: "Yards",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Yards_CreatedBy",
                table: "Yards",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_Yards_LastModifiedBy",
                table: "Yards",
                column: "LastModifiedBy");

            // The last of three locks on the append-only history (ARCHITECTURE.md D-2): whatever reaches the
            // database, an UPDATE, DELETE or TRUNCATE of ColonyEvents is refused.
            migrationBuilder.Sql("""
                CREATE FUNCTION colony_events_refuse_change() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'ColonyEvents is append-only: % refused', TG_OP;
                END;
                $$;
                CREATE TRIGGER "ColonyEvents_append_only" BEFORE UPDATE OR DELETE ON "ColonyEvents"
                    FOR EACH ROW EXECUTE FUNCTION colony_events_refuse_change();
                CREATE TRIGGER "ColonyEvents_no_truncate" BEFORE TRUNCATE ON "ColonyEvents"
                    FOR EACH STATEMENT EXECUTE FUNCTION colony_events_refuse_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER "ColonyEvents_no_truncate" ON "ColonyEvents";
                DROP TRIGGER "ColonyEvents_append_only" ON "ColonyEvents";
                DROP FUNCTION colony_events_refuse_change();
                """);

            migrationBuilder.DropTable(
                name: "ColonyEvents");

            migrationBuilder.DropTable(
                name: "InspectionMedia");

            migrationBuilder.DropTable(
                name: "Queens");

            migrationBuilder.DropTable(
                name: "Inspections");

            migrationBuilder.DropTable(
                name: "Colonies");

            migrationBuilder.DropTable(
                name: "Yards");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "Accounts");
        }
    }
}
