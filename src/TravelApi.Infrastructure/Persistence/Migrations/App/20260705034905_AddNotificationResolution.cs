using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// (Tanda 5, 2026-07-05) AUTO-RESOLUCION DE NOTIFICACIONES. Agrega a "Notifications" dos columnas nuevas y las
    /// rellena para los avisos ya existentes:
    ///   - <c>ResolutionKey</c> (text, con indice): clave estable de la CAUSA del aviso ("{RelatedEntityType}:{Id}").
    ///     Permite apagar de una todos los avisos de una misma causa cuando esa causa muere, y deduplicar entre dias.
    ///   - <c>ResolvedAt</c> (timestamptz): momento en que la causa murio. Un aviso resuelto deja de mostrarse aunque
    ///     nadie lo haya leido: es lo que hace que los avisos se apaguen SOLOS (decision del dueno).
    ///
    /// <para><b>Backfill (paso 2)</b>: a los avisos historicos se les deriva la clave de la misma convencion que usa
    /// el codigo. Caso especial: la marca "confirmada con cambios" (Type=<c>ReservaNeedsReview</c>) comparte
    /// RelatedEntityType="Reserva" con otros avisos, asi que su clave lleva prefijo dedicado ("ReservaNeedsReview:{id}")
    /// para no chocar — se rellena igual que en runtime.</para>
    ///
    /// <para><b>Reparacion legacy D3 (paso 3)</b>: el caso grave del que nace esta tanda. Cuando la anulacion de una
    /// factura fallaba con un error tecnico ("se reintentara automaticamente") quedaba un aviso de ERROR vivo para
    /// siempre; si un reintento posterior anulaba con exito, ese error seguia conviviendo con el "Anulacion exitosa".
    /// Aca marcamos <c>ResolvedAt = now()</c> en esos errores de anulacion cuya factura YA quedo anulada con exito
    /// (<c>AnnulmentStatus = 2</c>). El WHERE es CONSERVADOR: solo avisos Invoice + Error + cuyo texto menciona "anul"
    /// (no toca errores de emision) + cuya factura esta efectivamente anulada.</para>
    ///
    /// <para><b>Seguridad</b>: solo agrega columnas nullable (las filas existentes quedan validas sin default) e
    /// indexa. El backfill es idempotente (guarda por <c>ResolutionKey IS NULL</c> / <c>ResolvedAt IS NULL</c>).</para>
    /// </summary>
    public partial class AddNotificationResolution : Migration
    {
        // Valor entero de AnnulmentStatus.Succeeded (el enum se persiste como int). Documentado aca porque el SQL
        // crudo no ve el enum: si el orden del enum cambiara, este numero hay que revisarlo.
        private const int AnnulmentStatusSucceeded = 2;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── PASO 1 — columnas nuevas (nullable: las filas existentes quedan validas sin tocarlas) ──
            migrationBuilder.AddColumn<string>(
                name: "ResolutionKey",
                table: "Notifications",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ResolvedAt",
                table: "Notifications",
                type: "timestamp with time zone",
                nullable: true);

            // ── PASO 2 — backfill de ResolutionKey en los avisos historicos ──

            // 2.a Caso especial primero: la marca "confirmada con cambios" usa prefijo dedicado (comparte
            //     RelatedEntityType="Reserva" con otros avisos). Debe correr ANTES del caso generico.
            migrationBuilder.Sql(@"
                UPDATE ""Notifications""
                SET ""ResolutionKey"" = 'ReservaNeedsReview:' || ""RelatedEntityId""
                WHERE ""ResolutionKey"" IS NULL
                  AND ""Type"" = 'ReservaNeedsReview'
                  AND ""RelatedEntityId"" IS NOT NULL;
            ");

            // 2.b Caso generico: "{RelatedEntityType}:{RelatedEntityId}" para todo aviso con entidad relacionada que
            //     todavia no tenga clave (el guard ResolutionKey IS NULL preserva el paso 2.a).
            migrationBuilder.Sql(@"
                UPDATE ""Notifications""
                SET ""ResolutionKey"" = ""RelatedEntityType"" || ':' || ""RelatedEntityId""
                WHERE ""ResolutionKey"" IS NULL
                  AND ""RelatedEntityType"" IS NOT NULL
                  AND ""RelatedEntityId"" IS NOT NULL;
            ");

            // ── PASO 3 — reparacion legacy D3: apagar errores de anulacion cuya factura ya se anulo con exito ──
            migrationBuilder.Sql($@"
                UPDATE ""Notifications"" n
                SET ""ResolvedAt"" = now()
                FROM ""Invoices"" i
                WHERE n.""ResolvedAt"" IS NULL
                  AND n.""RelatedEntityType"" = 'Invoice'
                  AND n.""Type"" = 'Error'
                  AND n.""RelatedEntityId"" = i.""Id""
                  AND i.""AnnulmentStatus"" = {AnnulmentStatusSucceeded}
                  AND lower(n.""Message"") LIKE '%anul%';
            ");

            // ── PASO 4 — indice sobre la clave (lookups del auto-resolutor y del dedup entre dias) ──
            migrationBuilder.CreateIndex(
                name: "IX_Notifications_ResolutionKey",
                table: "Notifications",
                column: "ResolutionKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revierte solo el esquema. El backfill/reparacion de datos no se "des-hace" (no guardamos el estado
            // previo fila por fila): al dropear las columnas esos valores desaparecen con ellas, que es lo correcto.
            migrationBuilder.DropIndex(
                name: "IX_Notifications_ResolutionKey",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "ResolutionKey",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "ResolvedAt",
                table: "Notifications");
        }
    }
}
