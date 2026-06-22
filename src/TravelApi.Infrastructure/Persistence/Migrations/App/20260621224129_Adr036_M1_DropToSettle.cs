using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// ADR-036 (2026-06-21, "prepago puro"): elimina el estado "A liquidar" (ToSettle) del ciclo de vida de
    /// la Reserva. En el modelo prepago el cliente paga el 100% y el operador cobra el 100% ANTES del viaje,
    /// asi que no existe la etapa de liquidacion posterior. El CHECK <c>chk_TravelFiles_status_valid</c>
    /// (creado en Adr020_M1 con 11 valores) pasa a 10 valores, sin ToSettle.
    ///
    /// <para><b>Re-mapeo de filas existentes (Up):</b> cualquier reserva que haya quedado en 'ToSettle' se
    /// reubica segun su saldo, ANTES de recrear el CHECK (sino el CHECK de 10 rechazaria las filas 'ToSettle'):
    /// <list type="bullet">
    ///   <item>Balance &lt;= 0 (saldada) -&gt; 'Closed' (Finalizada). Se estampa ClosedAt si estaba null
    ///     (COALESCE con now()), para no dejar una reserva "cerrada" sin fecha de cierre.</item>
    ///   <item>Balance &gt; 0 (con deuda) -&gt; 'Confirmed' (Confirmada). NO va a 'Traveling': una reserva en
    ///     viaje con deuda quedaria en un callejon sin salida (Traveling salio de SaleFirmStatuses, asi que no
    ///     es cobrable, no aparece en cuentas por cobrar formales y no es cerrable porque el cierre exige
    ///     Balance &lt;= 0). 'Confirmed' SI es cobrable y visible en AR; el job re-promueve a 'Traveling' recien
    ///     cuando la reserva queda saldada (gate IsClientFullyPaid).</item>
    /// </list>
    /// El orden es: DROP del CHECK -&gt; UPDATE de las dos ramas -&gt; ADD del CHECK de 10 valores.</para>
    ///
    /// <para><b>RIESGO DE DEPLOY (gate del dueño):</b> esta migracion usa SQL CRUDO contra Postgres y NO se
    /// valida en InMemory (hubo un incidente asi en Adr020_M2 con una columna inexistente que InMemory oculto).
    /// Debe correrse/validarse contra Postgres REAL antes de desplegar. SQL de chequeo previo (informativo):
    ///   SELECT "Status", COUNT(*) FROM "TravelFiles" WHERE "Status" = 'ToSettle' GROUP BY "Status";
    ///
    /// <para><b>Down (LOSSY — el re-mapeo NO se deshace):</b> solo restaura el CHECK con los 11 valores
    /// (incluyendo 'ToSettle') para permitir el rollback del esquema. NO reconstruye que reservas estaban en
    /// 'ToSettle' (esa informacion se perdio al re-mapear en el Up): quedan como 'Closed'/'Traveling'. Es una
    /// decision consciente — re-derivar "estaba en liquidacion" desde el saldo no es fiable.</para>
    /// </summary>
    public partial class Adr036_M1_DropToSettle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "TravelFiles" DROP CONSTRAINT IF EXISTS chk_TravelFiles_status_valid;

                -- ToSettle saldada -> Closed (estampa ClosedAt si faltaba, para no dejar un cierre sin fecha).
                UPDATE "TravelFiles"
                  SET "Status" = 'Closed',
                      "ClosedAt" = COALESCE("ClosedAt", now())
                  WHERE "Status" = 'ToSettle' AND "Balance" <= 0;

                -- ToSettle con deuda -> Confirmed (cobrable y visible en AR; NO Traveling, que con deuda queda
                -- en callejon: no cobrable, no en AR formal, no cerrable). El job la re-promueve a Traveling
                -- recien cuando quede saldada (gate IsClientFullyPaid).
                UPDATE "TravelFiles"
                  SET "Status" = 'Confirmed'
                  WHERE "Status" = 'ToSettle' AND "Balance" > 0;

                ALTER TABLE "TravelFiles"
                  ADD CONSTRAINT chk_TravelFiles_status_valid
                  CHECK ("Status" IN (
                    'Quotation','Budget','InManagement','Confirmed','Traveling',
                    'Closed','Lost','Cancelled','PendingOperatorRefund','Archived'
                  ));
            """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // LOSSY: solo restaura el CHECK de 11 valores (con 'ToSettle'). El re-mapeo de filas del Up NO se
            // revierte — las reservas que estaban en 'ToSettle' ya quedaron en 'Closed'/'Traveling' y asi se
            // dejan (re-derivar el estado original desde el saldo no es confiable).
            migrationBuilder.Sql("""
                ALTER TABLE "TravelFiles" DROP CONSTRAINT IF EXISTS chk_TravelFiles_status_valid;
                ALTER TABLE "TravelFiles"
                  ADD CONSTRAINT chk_TravelFiles_status_valid
                  CHECK ("Status" IN (
                    'Quotation','Budget','InManagement','Confirmed','Traveling','ToSettle',
                    'Closed','Lost','Cancelled','PendingOperatorRefund','Archived'
                  ));
            """);
        }
    }
}
