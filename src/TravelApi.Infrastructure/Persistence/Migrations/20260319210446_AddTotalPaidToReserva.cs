using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTotalPaidToReserva : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.table_constraints
                        WHERE constraint_schema = 'public'
                          AND table_name = 'ReservaAttachments'
                          AND constraint_name = 'FK_ReservaAttachments_TravelFiles_TravelFileId'
                    ) THEN
                        ALTER TABLE "ReservaAttachments"
                            DROP CONSTRAINT "FK_ReservaAttachments_TravelFiles_TravelFileId";
                    END IF;
                END
                $$;
                """);

            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'ReservaAttachments'
                          AND column_name = 'TravelFileId'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'ReservaAttachments'
                          AND column_name = 'ReservaId'
                    ) THEN
                        ALTER TABLE "ReservaAttachments"
                            RENAME COLUMN "TravelFileId" TO "ReservaId";
                    END IF;
                END
                $$;
                """);

            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM pg_indexes
                        WHERE schemaname = 'public'
                          AND tablename = 'ReservaAttachments'
                          AND indexname = 'IX_ReservaAttachments_TravelFileId'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM pg_indexes
                        WHERE schemaname = 'public'
                          AND tablename = 'ReservaAttachments'
                          AND indexname = 'IX_ReservaAttachments_ReservaId'
                    ) THEN
                        ALTER INDEX "IX_ReservaAttachments_TravelFileId"
                            RENAME TO "IX_ReservaAttachments_ReservaId";
                    END IF;
                END
                $$;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "TravelFiles"
                    ADD COLUMN IF NOT EXISTS "TotalPaid" numeric(18,2) NOT NULL DEFAULT 0.0;
                """);

            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'ReservaAttachments'
                          AND column_name = 'ReservaId'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM information_schema.table_constraints
                        WHERE constraint_schema = 'public'
                          AND table_name = 'ReservaAttachments'
                          AND constraint_name = 'FK_ReservaAttachments_TravelFiles_ReservaId'
                    ) THEN
                        ALTER TABLE "ReservaAttachments"
                            ADD CONSTRAINT "FK_ReservaAttachments_TravelFiles_ReservaId"
                            FOREIGN KEY ("ReservaId") REFERENCES "TravelFiles" ("Id") ON DELETE CASCADE;
                    END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.table_constraints
                        WHERE constraint_schema = 'public'
                          AND table_name = 'ReservaAttachments'
                          AND constraint_name = 'FK_ReservaAttachments_TravelFiles_ReservaId'
                    ) THEN
                        ALTER TABLE "ReservaAttachments"
                            DROP CONSTRAINT "FK_ReservaAttachments_TravelFiles_ReservaId";
                    END IF;
                END
                $$;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "TravelFiles"
                    DROP COLUMN IF EXISTS "TotalPaid";
                """);

            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'ReservaAttachments'
                          AND column_name = 'ReservaId'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'ReservaAttachments'
                          AND column_name = 'TravelFileId'
                    ) THEN
                        ALTER TABLE "ReservaAttachments"
                            RENAME COLUMN "ReservaId" TO "TravelFileId";
                    END IF;
                END
                $$;
                """);

            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM pg_indexes
                        WHERE schemaname = 'public'
                          AND tablename = 'ReservaAttachments'
                          AND indexname = 'IX_ReservaAttachments_ReservaId'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM pg_indexes
                        WHERE schemaname = 'public'
                          AND tablename = 'ReservaAttachments'
                          AND indexname = 'IX_ReservaAttachments_TravelFileId'
                    ) THEN
                        ALTER INDEX "IX_ReservaAttachments_ReservaId"
                            RENAME TO "IX_ReservaAttachments_TravelFileId";
                    END IF;
                END
                $$;
                """);

            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = 'public'
                          AND table_name = 'ReservaAttachments'
                          AND column_name = 'TravelFileId'
                    ) AND NOT EXISTS (
                        SELECT 1
                        FROM information_schema.table_constraints
                        WHERE constraint_schema = 'public'
                          AND table_name = 'ReservaAttachments'
                          AND constraint_name = 'FK_ReservaAttachments_TravelFiles_TravelFileId'
                    ) THEN
                        ALTER TABLE "ReservaAttachments"
                            ADD CONSTRAINT "FK_ReservaAttachments_TravelFiles_TravelFileId"
                            FOREIGN KEY ("TravelFileId") REFERENCES "TravelFiles" ("Id") ON DELETE CASCADE;
                    END IF;
                END
                $$;
                """);
        }
    }
}
