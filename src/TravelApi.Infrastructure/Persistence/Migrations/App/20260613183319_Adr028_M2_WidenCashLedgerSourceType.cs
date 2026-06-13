using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// ADR-028 fix (2026-06-13): el Libro de Caja (<c>CashLedgerEntries</c>) nacio con
    /// <c>SourceType varchar(20)</c>, pero la constante de origen mas larga, "ClientCreditWithdrawal"
    /// (devolucion fisica de saldo al cliente), mide 22. El bug quedo LATENTE porque el unico path
    /// probado en integracion ("OperatorRefund", 14 chars) cabia; recien explota al asentar un RETIRO
    /// de saldo (flujo Withdraw) con <c>Npgsql 22001 value too long for type character varying(20)</c>.
    /// InMemory no valida longitud de columna, asi que solo se ve contra Postgres real (trap M2 del repo).
    ///
    /// <para>Fix: ampliar la columna a <c>varchar(50)</c> (mismo ancho que <c>Method</c>). 100% ADITIVO y
    /// reversible: ensanchar nunca trunca datos existentes. EF puro (sin SQL crudo). El warning de
    /// "possible loss of data" del scaffolder NO aplica: es para el sentido inverso (achicar), no para
    /// ensanchar. <b>Down</b> vuelve a varchar(20); seguro mientras no haya filas con SourceType > 20
    /// (hoy ninguna constante supera 22 y "ClientCreditWithdrawal" recien se escribe POST-Up).</para>
    /// </summary>
    public partial class Adr028_M2_WidenCashLedgerSourceType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "SourceType",
                table: "CashLedgerEntries",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "SourceType",
                table: "CashLedgerEntries",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50);
        }
    }
}
