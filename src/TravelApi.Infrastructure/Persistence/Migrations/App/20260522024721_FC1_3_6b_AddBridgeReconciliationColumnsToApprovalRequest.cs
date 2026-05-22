using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// FC1.3.6b (ADR-009 §2.12 round 3, 2026-05-21): tres columnas en
    /// <c>ApprovalRequests</c> para que el job de reconciliacion bridge
    /// (<c>PartialCreditNoteBridgeReconciliationJob</c>) pueda llevar registro
    /// de cuantas veces reintento llamar al callback del bridge y por que
    /// fallo el ultimo intento.
    ///
    /// <para><b>Por que en esta tabla y no en una nueva</b>: el job filtra por
    /// <c>RequestType = PartialCreditNoteApproval</c> + <c>Status</c> +
    /// <c>BridgeRetryCount &lt; maxRetries</c> en una sola query. Una tabla 1:1
    /// nos forzaria a LEFT JOIN o nullables igualmente — no ganamos nada.</para>
    ///
    /// <para><b>Columnas agregadas</b>:</para>
    /// <list type="bullet">
    /// <item><c>BridgeRetryCount</c> (integer NOT NULL DEFAULT 0): cuantos intentos
    ///   acumula el job. Reset a 0 cuando bridge tiene exito o cuando admin fuerza
    ///   el callback. El default 0 cubre las filas legacy backfilleadas
    ///   automaticamente por Postgres al ALTER TABLE.</item>
    /// <item><c>BridgeLastError</c> (varchar(2000) NULL): mensaje de error truncado
    ///   del ultimo intento fallido. Se limpia a NULL cuando bridge OK.</item>
    /// <item><c>BridgeLastAttemptAt</c> (timestamptz NULL): cuando corrio el ultimo
    ///   intento. Sirve para diagnostico ("el job lo tomo o el filtro lo dejo afuera?").</item>
    /// </list>
    ///
    /// <para><b>Datos</b>: aditiva. Postgres rellena <c>BridgeRetryCount=0</c>
    /// en todas las filas existentes via DEFAULT; las otras dos quedan NULL.
    /// No hay backfill manual — los approvals que ya estan Approved/Rejected
    /// con BC huerfano necesitarian intervencion manual igual (force-callback),
    /// y el job arrancara desde 0 en su proxima corrida.</para>
    ///
    /// <para><b>Rollback</b>: aditiva. <c>Down()</c> dropea las 3 columnas. No
    /// hay perdida de datos relevantes (los counters son operativos, no fiscales).</para>
    /// </summary>
    public partial class FC1_3_6b_AddBridgeReconciliationColumnsToApprovalRequest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Nullable: hasta que el job corra contra esta fila, no hay attempt.
            migrationBuilder.AddColumn<DateTime>(
                name: "BridgeLastAttemptAt",
                table: "ApprovalRequests",
                type: "timestamp with time zone",
                nullable: true);

            // Nullable + max 2000 chars: el bridge puede tirar stack traces
            // largos; truncamos del lado del job para no romper el insert.
            migrationBuilder.AddColumn<string>(
                name: "BridgeLastError",
                table: "ApprovalRequests",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            // NOT NULL DEFAULT 0: el counter siempre tiene valor. Las filas
            // legacy quedan en 0 (Postgres aplica el DEFAULT al ALTER TABLE).
            migrationBuilder.AddColumn<int>(
                name: "BridgeRetryCount",
                table: "ApprovalRequests",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BridgeLastAttemptAt",
                table: "ApprovalRequests");

            migrationBuilder.DropColumn(
                name: "BridgeLastError",
                table: "ApprovalRequests");

            migrationBuilder.DropColumn(
                name: "BridgeRetryCount",
                table: "ApprovalRequests");
        }
    }
}
