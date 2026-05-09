using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// B1.15 Fase 2a (Decision 5 + 13 de Gaston):
    ///
    /// 1) Agrega <c>OperationalFinanceSettings.MaxDiscountPercentWithoutOverride</c>
    ///    (decimal(5,2), NOT NULL, default 10) — tope de descuento sin permiso
    ///    <c>reservas.discount_above_threshold</c>.
    ///
    /// 2) Backfill: <c>UPDATE TravelFiles SET ResponsibleUserId = (primer Admin
    ///    activo) WHERE ResponsibleUserId IS NULL</c>. Verificado en VPS 2026-05-07:
    ///    14 reservas legacy en NULL. Necesario antes de migrar
    ///    ReservasController a <c>RequireOwnership</c> (que rechaza si la Reserva
    ///    no tiene responsable).
    ///
    /// 3) Sincroniza <c>TravelFiles.ResponsibleUserName</c> (snapshot denormalizado
    ///    introducido por la migracion <c>20260506203203_DenormalizeReservaResponsibleUserName</c>)
    ///    con el <c>FullName</c> del usuario responsable. Es necesario porque el
    ///    backfill del paso 2 puede dejar <c>ResponsibleUserName</c> en NULL para
    ///    reservas que recien ganan responsable, y porque el snapshot puede haber
    ///    quedado desactualizado en historicos donde se asigno <c>ResponsibleUserId</c>
    ///    sin tocar el snapshot.
    ///
    /// El <c>Down()</c> NO revierte el backfill — no se puede recuperar el
    /// estado original de "ResponsibleUserId IS NULL" sin perdida de auditoria.
    /// Solo elimina la columna nueva.
    /// </summary>
    public partial class AddDiscountThresholdAndBackfillReservaResponsible : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) Columna nueva en OperationalFinanceSettings.
            migrationBuilder.AddColumn<decimal>(
                name: "MaxDiscountPercentWithoutOverride",
                table: "OperationalFinanceSettings",
                type: "numeric(5,2)",
                nullable: false,
                defaultValue: 10m);

            // 2) Backfill TravelFiles.ResponsibleUserId al primer Admin activo.
            // Si no hay Admin activo, deja NULL (resolver de ownership rechazara
            // y el ticket de ops B1.15 limpiara el caso, pero no rompemos la
            // migracion por un dato faltante).
            //
            // Nota: usa "TravelFiles" (no "Reservas") — desalineo de naming
            // historico. ORDER BY u."Id" ASC — ApplicationUser hereda IdentityUser
            // y NO tiene CreatedAt; el Id (string) es estable y deterministico.
            migrationBuilder.Sql("""
                UPDATE "TravelFiles"
                SET "ResponsibleUserId" = (
                    SELECT u."Id"
                    FROM "AspNetUsers" u
                    INNER JOIN "AspNetUserRoles" ur ON ur."UserId" = u."Id"
                    INNER JOIN "AspNetRoles" r ON r."Id" = ur."RoleId"
                    WHERE r."Name" = 'Admin' AND u."IsActive" = TRUE
                    ORDER BY u."Id" ASC
                    LIMIT 1
                )
                WHERE "ResponsibleUserId" IS NULL
                  AND EXISTS (
                    SELECT 1
                    FROM "AspNetUsers" u
                    INNER JOIN "AspNetUserRoles" ur ON ur."UserId" = u."Id"
                    INNER JOIN "AspNetRoles" r ON r."Id" = ur."RoleId"
                    WHERE r."Name" = 'Admin' AND u."IsActive" = TRUE
                  );
                """);

            // 3) Sincronizar TravelFiles.ResponsibleUserName con el FullName del
            // usuario responsable. Cubre tanto las reservas que recien obtienen
            // responsable (paso 2) como historicos donde el snapshot quedo en NULL
            // (migracion 20260506203203 lo introdujo, pero no backfilleo cuando
            // ResponsibleUserId ya estaba seteado).
            //
            // Solo actualiza filas con ResponsibleUserName IS NULL para no pisar
            // un valor manualmente editado en runtime.
            migrationBuilder.Sql("""
                UPDATE "TravelFiles"
                SET "ResponsibleUserName" = (
                    SELECT u."FullName"
                    FROM "AspNetUsers" u
                    WHERE u."Id" = "TravelFiles"."ResponsibleUserId"
                )
                WHERE "ResponsibleUserName" IS NULL
                  AND "ResponsibleUserId" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Solo revierte la columna nueva. El backfill de ResponsibleUserId
            // no se revierte: no hay forma de recuperar cuales reservas estaban
            // en NULL originalmente sin perder informacion.
            migrationBuilder.DropColumn(
                name: "MaxDiscountPercentWithoutOverride",
                table: "OperationalFinanceSettings");
        }
    }
}
