using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// Fix B1 (bug critico fiscal, revision del reviewer sobre el barrido de fechas 2026-07-22/23):
    /// agrega <c>Invoice.CbteFchArgentina</c>, el dia calendario ARGENTINO exacto que se
    /// mando/registro como <c>&lt;CbteFch&gt;</c> en ARCA. Antes el PDF y el QR de ARCA re-derivaban
    /// esta fecha de <c>IssuedAt</c> pasandola por <c>ArgentinaTime.ToArgentinaTime</c>; en el camino
    /// de recuperacion anti-doble-CAE eso restaba 3 horas a una fecha-a-medianoche y mostraba un dia
    /// ANTES del que ARCA realmente tenia registrado — bug deterministico, no un caso borde.
    ///
    /// <para><b>Aditiva, nullable, SIN backfill</b> (T-8, tabla "Invoices" con datos reales en
    /// PROD): las filas existentes quedan en <c>NULL</c>. No hay forma confiable de reconstruir el
    /// dia exacto para facturas historicas sin volver a consultar ARCA factura por factura, asi que
    /// no se intenta — <c>InvoicePdfService.GetEmissionDateArgentina</c> cae a un fallback (con un
    /// riesgo residual documentado y testeado) para esas filas viejas. Las facturas emitidas o
    /// recuperadas DESPUES de este deploy quedan con la columna poblada en ambos caminos de
    /// <c>AfipService.ProcessInvoiceJob</c> (POST directo y recovery de idempotencia).</para>
    /// </summary>
    public partial class Adr049_B1_AddCbteFchArgentinaToInvoice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CbteFchArgentina",
                table: "Invoices",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CbteFchArgentina",
                table: "Invoices");
        }
    }
}
