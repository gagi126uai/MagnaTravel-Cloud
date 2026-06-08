using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class Adr020_M1_ReservaLifecycleRedesign : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnableSoldToSettleStates",
                table: "OperationalFinanceSettings");

            migrationBuilder.AddColumn<decimal>(
                name: "ConfirmedSale",
                table: "TravelFiles",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTime>(
                name: "CancelledAt",
                table: "TransferBookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancelledByUserId",
                table: "TransferBookings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancelledByUserName",
                table: "TransferBookings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ConfirmedAt",
                table: "TransferBookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NoConfirmationMarkedAt",
                table: "TransferBookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NoConfirmationMarkedByUserId",
                table: "TransferBookings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NoConfirmationMarkedByUserName",
                table: "TransferBookings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "NoConfirmationRequired",
                table: "TransferBookings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "CancelledAt",
                table: "Reservations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancelledByUserId",
                table: "Reservations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancelledByUserName",
                table: "Reservations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ConfirmedAt",
                table: "Reservations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CancelledAt",
                table: "PackageBookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancelledByUserId",
                table: "PackageBookings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancelledByUserName",
                table: "PackageBookings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ConfirmedAt",
                table: "PackageBookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CancelledAt",
                table: "HotelBookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancelledByUserId",
                table: "HotelBookings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancelledByUserName",
                table: "HotelBookings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ConfirmedAt",
                table: "HotelBookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CancelledAt",
                table: "FlightSegments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancelledByUserId",
                table: "FlightSegments",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancelledByUserName",
                table: "FlightSegments",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ConfirmedAt",
                table: "FlightSegments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TicketIssuedAt",
                table: "FlightSegments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TicketIssuedByUserId",
                table: "FlightSegments",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TicketIssuedByUserName",
                table: "FlightSegments",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CancelledAt",
                table: "AssistanceBookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancelledByUserId",
                table: "AssistanceBookings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CancelledByUserName",
                table: "AssistanceBookings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ConfirmedAt",
                table: "AssistanceBookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ReservaEditAuthorizations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReservaId = table.Column<int>(type: "integer", nullable: false),
                    RequestedByUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RequestedByUserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AuthorizedByUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AuthorizedByUserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReservaStatusSnapshot = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReservaEditAuthorizations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReservaEditAuthorizations_TravelFiles_ReservaId",
                        column: x => x.ReservaId,
                        principalTable: "TravelFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReservaEditAuthorizationChanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AuthorizationId = table.Column<int>(type: "integer", nullable: false),
                    Operation = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    EntityType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    EntityId = table.Column<int>(type: "integer", nullable: true),
                    Summary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    PerformedByUserId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PerformedByUserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReservaEditAuthorizationChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReservaEditAuthorizationChanges_ReservaEditAuthorizations_A~",
                        column: x => x.AuthorizationId,
                        principalTable: "ReservaEditAuthorizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReservaEditAuthorizationChanges_AuthorizationId",
                table: "ReservaEditAuthorizationChanges",
                column: "AuthorizationId");

            migrationBuilder.CreateIndex(
                name: "IX_ReservaEditAuthorizations_PublicId",
                table: "ReservaEditAuthorizations",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReservaEditAuthorizations_ReservaId_ExpiresAt",
                table: "ReservaEditAuthorizations",
                columns: new[] { "ReservaId", "ExpiresAt" });

            // ADR-020: remapeo de estados + CHECK nuevo de 11 valores. Orden: dropear el CHECK viejo
            // ANTES del UPDATE (sino 'InManagement' no esta en el CHECK de 9 y el UPDATE rebota), remapear
            // 'Sold' -> 'InManagement' (en el VPS el flag nunca se prendio: se esperan 0 filas, es defensa),
            // y recrear el CHECK con los 11 valores del ciclo unico.
            migrationBuilder.Sql("""
                ALTER TABLE "TravelFiles" DROP CONSTRAINT IF EXISTS chk_TravelFiles_status_valid;
                UPDATE "TravelFiles" SET "Status" = 'InManagement' WHERE "Status" = 'Sold';
                ALTER TABLE "TravelFiles"
                  ADD CONSTRAINT chk_TravelFiles_status_valid
                  CHECK ("Status" IN (
                    'Quotation','Budget','InManagement','Confirmed','Traveling','ToSettle',
                    'Closed','Lost','Cancelled','PendingOperatorRefund','Archived'
                  ));
                """);

            // ADR-020: backfill de ConfirmedAt en servicios historicos ya confirmados. Predicados
            // ESPEJO de WorkflowStatusHelper.MapGenericStatus / MapFlightStatus por construccion.
            // Es un PROXY (CreatedAt no es la fecha real de confirmacion); aceptable porque las
            // penalidades por servicio corren hacia adelante. El servicio generico mapea a la tabla
            // "Reservations" (ToTable historico de ServicioReserva).
            foreach (var genericTable in new[] { "HotelBookings", "PackageBookings", "TransferBookings", "AssistanceBookings", "Reservations" })
            {
                migrationBuilder.Sql($"""
                    UPDATE "{genericTable}"
                       SET "ConfirmedAt" = "CreatedAt"
                     WHERE lower("Status") NOT LIKE '%cancel%'
                       AND (lower("Status") LIKE '%confirm%' OR lower("Status") LIKE '%emit%');
                    """);
            }

            // Aereos: ConfirmedAt si el PNR esta confirmado (HK/TK/KK/KL); TicketIssuedAt si ya tiene
            // numero de ticket cargado (se considera emitido). Un HK sin ticket queda confirmado-pero-
            // NO-resuelto (correcto segun B4: no se borra, pero no resuelve el file).
            migrationBuilder.Sql("""
                UPDATE "FlightSegments"
                   SET "ConfirmedAt" = "CreatedAt"
                 WHERE upper(trim("Status")) IN ('HK','TK','KK','KL');
                UPDATE "FlightSegments"
                   SET "TicketIssuedAt" = "CreatedAt"
                 WHERE "TicketNumber" IS NOT NULL AND "TicketNumber" <> '';
                """);

            // ADR-020 F4: seed del permiso nuevo para Admin (idempotente). Admin igual bypasa por rol,
            // pero lo dejamos explicito para que aparezca en la UI de permisos (mismo patron Adr013_M2).
            migrationBuilder.Sql("""
                INSERT INTO "RolePermissions" ("RoleName", "Permission")
                SELECT 'Admin', 'reservas.authorize_locked_edit'
                WHERE NOT EXISTS (
                    SELECT 1 FROM "RolePermissions"
                    WHERE "RoleName" = 'Admin' AND "Permission" = 'reservas.authorize_locked_edit'
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ADR-020 (B2): remapear estados nuevos a viejos ANTES de restaurar el CHECK de 9 (sino el
            // CHECK viejo rechaza las filas nuevas). Lossy pero coherente con el ciclo viejo:
            // Quotation->Budget, InManagement->Confirmed, Lost->Cancelled. Despues restaurar el CHECK 9.
            migrationBuilder.Sql("""
                ALTER TABLE "TravelFiles" DROP CONSTRAINT IF EXISTS chk_TravelFiles_status_valid;
                UPDATE "TravelFiles" SET "Status" = 'Budget'    WHERE "Status" = 'Quotation';
                UPDATE "TravelFiles" SET "Status" = 'Confirmed' WHERE "Status" = 'InManagement';
                UPDATE "TravelFiles" SET "Status" = 'Cancelled' WHERE "Status" = 'Lost';
                ALTER TABLE "TravelFiles"
                  ADD CONSTRAINT chk_TravelFiles_status_valid
                  CHECK ("Status" IN (
                    'Budget','Sold','Confirmed','Traveling','ToSettle',
                    'Closed','Cancelled','PendingOperatorRefund','Archived'
                  ));
                DELETE FROM "RolePermissions"
                 WHERE "RoleName" = 'Admin' AND "Permission" = 'reservas.authorize_locked_edit';
                """);

            migrationBuilder.DropTable(
                name: "ReservaEditAuthorizationChanges");

            migrationBuilder.DropTable(
                name: "ReservaEditAuthorizations");

            migrationBuilder.DropColumn(
                name: "ConfirmedSale",
                table: "TravelFiles");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                table: "TransferBookings");

            migrationBuilder.DropColumn(
                name: "CancelledByUserId",
                table: "TransferBookings");

            migrationBuilder.DropColumn(
                name: "CancelledByUserName",
                table: "TransferBookings");

            migrationBuilder.DropColumn(
                name: "ConfirmedAt",
                table: "TransferBookings");

            migrationBuilder.DropColumn(
                name: "NoConfirmationMarkedAt",
                table: "TransferBookings");

            migrationBuilder.DropColumn(
                name: "NoConfirmationMarkedByUserId",
                table: "TransferBookings");

            migrationBuilder.DropColumn(
                name: "NoConfirmationMarkedByUserName",
                table: "TransferBookings");

            migrationBuilder.DropColumn(
                name: "NoConfirmationRequired",
                table: "TransferBookings");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "CancelledByUserId",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "CancelledByUserName",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "ConfirmedAt",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                table: "PackageBookings");

            migrationBuilder.DropColumn(
                name: "CancelledByUserId",
                table: "PackageBookings");

            migrationBuilder.DropColumn(
                name: "CancelledByUserName",
                table: "PackageBookings");

            migrationBuilder.DropColumn(
                name: "ConfirmedAt",
                table: "PackageBookings");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                table: "HotelBookings");

            migrationBuilder.DropColumn(
                name: "CancelledByUserId",
                table: "HotelBookings");

            migrationBuilder.DropColumn(
                name: "CancelledByUserName",
                table: "HotelBookings");

            migrationBuilder.DropColumn(
                name: "ConfirmedAt",
                table: "HotelBookings");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "CancelledByUserId",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "CancelledByUserName",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "ConfirmedAt",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "TicketIssuedAt",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "TicketIssuedByUserId",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "TicketIssuedByUserName",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "CancelledAt",
                table: "AssistanceBookings");

            migrationBuilder.DropColumn(
                name: "CancelledByUserId",
                table: "AssistanceBookings");

            migrationBuilder.DropColumn(
                name: "CancelledByUserName",
                table: "AssistanceBookings");

            migrationBuilder.DropColumn(
                name: "ConfirmedAt",
                table: "AssistanceBookings");

            migrationBuilder.AddColumn<bool>(
                name: "EnableSoldToSettleStates",
                table: "OperationalFinanceSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
