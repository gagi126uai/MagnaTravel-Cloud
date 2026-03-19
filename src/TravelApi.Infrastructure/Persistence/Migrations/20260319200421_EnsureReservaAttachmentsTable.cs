using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnsureReservaAttachmentsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$ 
BEGIN
    IF NOT EXISTS (SELECT FROM pg_tables WHERE schemaname = 'public' AND tablename = 'ReservaAttachments') THEN
        CREATE TABLE ""ReservaAttachments"" (
            ""Id"" SERIAL PRIMARY KEY,
            ""TravelFileId"" integer NOT NULL,
            ""FileName"" text NOT NULL,
            ""StoredFileName"" text NOT NULL,
            ""ContentType"" text,
            ""FileSize"" bigint NOT NULL,
            ""UploadedBy"" text,
            ""UploadedAt"" timestamp with time zone NOT NULL DEFAULT NOW(),
            CONSTRAINT ""FK_ReservaAttachments_TravelFiles_TravelFileId"" FOREIGN KEY (""TravelFileId"") REFERENCES ""TravelFiles"" (""Id"") ON DELETE CASCADE
        );
        CREATE INDEX ""IX_ReservaAttachments_TravelFileId"" ON ""ReservaAttachments"" (""TravelFileId"");
    END IF;
END $$;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
