using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// ADR-014 (Fase 1, 2026-06-02): confirmacion DIFERIDA de la penalidad. Migracion 100%
    /// ADITIVA, sin backfill de datos del dominio:
    ///
    /// <para><b>BookingCancellations</b>: 2 columnas nullable —
    /// <c>OperatorPenaltyConfirmedDate</c> (timestamptz) y <c>SupportingDocumentReference</c>
    /// (varchar 500). Los BCs existentes quedan con NULL (no confirmaron por el flujo diferido),
    /// que es el comportamiento correcto. NO toca el token de concurrencia xmin (es un system
    /// column de Postgres, no una columna fisica).</para>
    ///
    /// <para><b>OperationalFinanceSettings</b>: 3 parametros del flujo diferido. <b>Importante</b>:
    /// los defaults a nivel BD se setean a los MISMOS valores que el modelo C# (15 / 60 /
    /// 2.000.000) para que la fila de settings YA EXISTENTE quede con valores operativos sanos,
    /// NO en cero (un GraceDays=0 marcaria toda ND como tardia; un FourEyesThreshold=0 exigiria
    /// 4-eyes por monto siempre). Como solo importan con EnableCancellationDebitNote ON (OFF por
    /// default), no hay efecto observable hasta prender el flag.</para>
    ///
    /// <para>Rollback: drop limpio de las 5 columnas (Down). Sin perdida de datos del dominio.</para>
    /// </summary>
    public partial class Adr014_M1_AddDeferredPenaltyConfirmation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Default 2.000.000 ARS: la fila de settings existente queda con un umbral 4-eyes
            // operativo, no en cero (cero forzaria 4-eyes por monto en toda ND).
            migrationBuilder.AddColumn<decimal>(
                name: "CancellationDebitNoteFourEyesThreshold",
                table: "OperationalFinanceSettings",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 2_000_000m);

            // Default 15 dias (RG 4540): cero marcaria toda ND como fuera de plazo.
            migrationBuilder.AddColumn<int>(
                name: "CancellationDebitNoteGraceDays",
                table: "OperationalFinanceSettings",
                type: "integer",
                nullable: false,
                defaultValue: 15);

            // Default 60 dias: segundo umbral de aviso (warning elevado).
            migrationBuilder.AddColumn<int>(
                name: "CancellationDebitNoteHardWarnDays",
                table: "OperationalFinanceSettings",
                type: "integer",
                nullable: false,
                defaultValue: 60);

            migrationBuilder.AddColumn<DateTime>(
                name: "OperatorPenaltyConfirmedDate",
                table: "BookingCancellations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SupportingDocumentReference",
                table: "BookingCancellations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CancellationDebitNoteFourEyesThreshold",
                table: "OperationalFinanceSettings");

            migrationBuilder.DropColumn(
                name: "CancellationDebitNoteGraceDays",
                table: "OperationalFinanceSettings");

            migrationBuilder.DropColumn(
                name: "CancellationDebitNoteHardWarnDays",
                table: "OperationalFinanceSettings");

            migrationBuilder.DropColumn(
                name: "OperatorPenaltyConfirmedDate",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "SupportingDocumentReference",
                table: "BookingCancellations");
        }
    }
}
