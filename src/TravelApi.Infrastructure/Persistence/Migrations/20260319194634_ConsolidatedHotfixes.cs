using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConsolidatedHotfixes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$ 
BEGIN 
    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_FlightSegments_Reservations_ReservationId') THEN
        ALTER TABLE ""FlightSegments"" DROP CONSTRAINT ""FK_FlightSegments_Reservations_ReservationId"";
    END IF;
    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_TravelFileAttachments_TravelFiles_TravelFileId') THEN
        ALTER TABLE ""TravelFileAttachments"" DROP CONSTRAINT ""FK_TravelFileAttachments_TravelFiles_TravelFileId"";
    END IF;
END $$;
");

            migrationBuilder.Sql(@"
DO $$ 
BEGIN
    IF EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'PK_TravelFileAttachments') THEN
        ALTER TABLE ""TravelFileAttachments"" DROP CONSTRAINT ""PK_TravelFileAttachments"";
    END IF;
    IF EXISTS (SELECT FROM pg_tables WHERE schemaname = 'public' AND tablename = 'TravelFileAttachments') THEN
        ALTER TABLE ""TravelFileAttachments"" RENAME TO ""ReservaAttachments"";
        ALTER INDEX IF EXISTS ""IX_TravelFileAttachments_TravelFileId"" RENAME TO ""IX_ReservaAttachments_TravelFileId"";
    END IF;
END $$;
");

            migrationBuilder.Sql(@"ALTER TABLE ""AfipSettings"" ADD COLUMN IF NOT EXISTS ""PadronSign"" text;");
            migrationBuilder.Sql(@"ALTER TABLE ""AfipSettings"" ADD COLUMN IF NOT EXISTS ""PadronToken"" text;");
            migrationBuilder.Sql(@"ALTER TABLE ""AfipSettings"" ADD COLUMN IF NOT EXISTS ""PadronTokenExpiration"" timestamp with time zone;");

            migrationBuilder.AlterColumn<string>(
                name: "UploadedBy",
                table: "ReservaAttachments",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "ContentType",
                table: "ReservaAttachments",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.Sql(@"
DO $$ 
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'PK_ReservaAttachments') THEN
        ALTER TABLE ""ReservaAttachments"" ADD CONSTRAINT ""PK_ReservaAttachments"" PRIMARY KEY (""Id"");
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_FlightSegments_Reservations_ReservationId') THEN
        ALTER TABLE ""FlightSegments"" ADD CONSTRAINT ""FK_FlightSegments_Reservations_ReservationId"" FOREIGN KEY (""ReservationId"") REFERENCES ""Reservations"" (""Id"");
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_ReservaAttachments_TravelFiles_TravelFileId') THEN
        ALTER TABLE ""ReservaAttachments"" ADD CONSTRAINT ""FK_ReservaAttachments_TravelFiles_TravelFileId"" FOREIGN KEY (""TravelFileId"") REFERENCES ""TravelFiles"" (""Id"") ON DELETE CASCADE;
    END IF;
END $$;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FlightSegments_Reservations_ReservationId",
                table: "FlightSegments");

            migrationBuilder.DropForeignKey(
                name: "FK_ReservaAttachments_TravelFiles_TravelFileId",
                table: "ReservaAttachments");

            migrationBuilder.DropPrimaryKey(
                name: "PK_ReservaAttachments",
                table: "ReservaAttachments");

            migrationBuilder.DropColumn(
                name: "PadronSign",
                table: "AfipSettings");

            migrationBuilder.DropColumn(
                name: "PadronToken",
                table: "AfipSettings");

            migrationBuilder.DropColumn(
                name: "PadronTokenExpiration",
                table: "AfipSettings");

            migrationBuilder.RenameTable(
                name: "ReservaAttachments",
                newName: "TravelFileAttachments");

            migrationBuilder.RenameIndex(
                name: "IX_ReservaAttachments_TravelFileId",
                table: "TravelFileAttachments",
                newName: "IX_TravelFileAttachments_TravelFileId");

            migrationBuilder.AlterColumn<string>(
                name: "UploadedBy",
                table: "TravelFileAttachments",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ContentType",
                table: "TravelFileAttachments",
                type: "text",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_TravelFileAttachments",
                table: "TravelFileAttachments",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_FlightSegments_Reservations_ReservationId",
                table: "FlightSegments",
                column: "ReservationId",
                principalTable: "Reservations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_TravelFileAttachments_TravelFiles_TravelFileId",
                table: "TravelFileAttachments",
                column: "TravelFileId",
                principalTable: "TravelFiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
