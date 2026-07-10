using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// ADR-044 T3b Decision 3 (S2, 2026-07-10): agrega <c>SettledBySupplierPaymentId</c> (int nullable, SIN FK)
    /// sobre <c>BookingCancellationLineOperatorCharges</c> — la red DURA anti doble-liquidacion de un cargo
    /// FacturadaAparte. Se setea al registrar el pago al operador que liquida el cargo y se limpia al eliminarlo;
    /// un segundo pago sobre un cargo con este campo ya seteado se rechaza en el servicio.
    ///
    /// <para><b>Sin FK a proposito</b> (referencia debil, misma politica que <c>SupplierPayment.ServicePublicId</c>):
    /// no queremos cascade ni que esta referencia bloquee el borrado del pago — la integridad la maneja el
    /// servicio. <b>Aditiva, sin backfill</b>: los cargos previos quedan en null (nunca se liquidaron por esta
    /// via mecanizada) — comportamiento identico al de hoy.</para>
    ///
    /// <para><b>Consulta de validacion (solo lectura)</b>:
    /// <code>
    /// SELECT COUNT(*) FROM "BookingCancellationLineOperatorCharges" WHERE "SettledBySupplierPaymentId" IS NOT NULL;
    /// -- esperado: 0 justo despues de aplicar (ningun cargo previo a esta tanda tiene el dato)
    /// </code></para>
    /// </summary>
    public partial class Adr044_M_T3b4_AddSettledBySupplierPaymentIdToOperatorCharge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SettledBySupplierPaymentId",
                table: "BookingCancellationLineOperatorCharges",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SettledBySupplierPaymentId",
                table: "BookingCancellationLineOperatorCharges");
        }
    }
}
