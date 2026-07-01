using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// Idempotencia del atajo record-and-allocate (2026-07-01): agrega
    /// <c>OperatorRefundsReceived.IdempotencyKey</c> + su indice UNICO PARCIAL. Es el candado server-side contra el
    /// doble cobro: dos requests con la misma llave (doble clic, reintento de red, dos pestañas) no pueden crear dos
    /// ingresos ni dos saldos a favor del cliente.
    ///
    /// <para><b>Por que es SEGURA (aditiva)</b>: la columna es NULLABLE, SIN default y SIN backfill -> las filas
    /// historicas (y las del flujo de 2 pasos) quedan en NULL y NO participan del candado. El indice es UNICO pero
    /// FILTRADO a NOT NULL, asi que esas filas NULL no chocan entre si (Postgres permite multiples NULL solo con el
    /// filtro; sin filtro un unico sobre nullable igual permite varios NULL, pero el filtro ademas ahorra indexar
    /// filas que nunca compiten). No reescribe ninguna fila existente: no hay lock pesado ni riesgo de datos.</para>
    /// </summary>
    public partial class Adr041_M6_AddOperatorRefundIdempotencyKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "IdempotencyKey",
                table: "OperatorRefundsReceived",
                type: "uuid",
                nullable: true);

            // UNICO PARCIAL: red DURA bajo concurrencia real. Si dos transacciones intentan sellar la MISMA llave,
            // Postgres rechaza la segunda con 23505 (unique_violation) y el service la resuelve devolviendo la
            // operacion original. El nombre del indice contiene "IdempotencyKey" — el service filtra la excepcion por
            // ese nombre para NO tragar otras violaciones de unicidad del modulo.
            migrationBuilder.CreateIndex(
                name: "IX_OperatorRefundsReceived_IdempotencyKey",
                table: "OperatorRefundsReceived",
                column: "IdempotencyKey",
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OperatorRefundsReceived_IdempotencyKey",
                table: "OperatorRefundsReceived");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "OperatorRefundsReceived");
        }
    }
}
