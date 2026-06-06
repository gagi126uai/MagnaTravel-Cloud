using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// ADR-017 F1.1 (catalogo find-or-create + fechas limite, 2026-06-05): migracion 2 de 2,
    /// 100% ADITIVA. Solo ESTRUCTURA; en F1.1 nadie escribe estos campos (eso es F1.3/F1.4).
    ///
    /// <para>Que agrega:
    /// <list type="bullet">
    ///   <item><b>Fechas limite</b>: <c>HotelBookings.OperatorPaymentDeadline</c> +
    ///   <c>PackageBookings.OperatorPaymentDeadline</c> + <c>FlightSegments.TicketingDeadline</c>
    ///   (todas <c>timestamp</c> nullable). Traslado y Asistencia NO llevan (no estan en el mockup).</item>
    ///   <item><b>Costo a confirmar (D7)</b>: <c>CostToConfirm</c> (bool default false) +
    ///   <c>CostToConfirmReason</c> (varchar(30) nullable) en las 5 entidades de servicio.</item>
    ///   <item><b>Settings</b>: <c>EnableCatalogFindOrCreate</c> + <c>EnableServiceDeadlineAlerts</c>
    ///   (bool default false) + <c>StaleCostReferenceDays</c> (int default 60) en
    ///   <c>OperationalFinanceSettings</c>.</item>
    /// </list></para>
    ///
    /// <para><b>StaleCostReferenceDays default 60</b>: se setea a mano en la migracion (mismo patron que
    /// <c>OperatorRefundTimeoutDays</c>), no via HasDefaultValue en el modelo. Asi la fila de settings ya
    /// existente queda en 60 (el inicializador <c>= 60</c> de la entidad solo aplica a filas NUEVAS
    /// creadas en codigo, no backfillea las existentes). El snapshot no lleva default, asi que esto NO
    /// genera drift en el proximo migrations add.</para>
    ///
    /// <para><b>Orden de deploy / R8</b>: encolada DETRAS de la cola de migraciones pendientes del VPS,
    /// sin tocar ni reordenar ninguna. Aditiva, defaults seguros, sin DROP/ALTER destructivo. NO se
    /// aplica desde aca.</para>
    /// </summary>
    public partial class Adr017_M2_AddDeadlinesCostToConfirmAndSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CostToConfirm",
                table: "TransferBookings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CostToConfirmReason",
                table: "TransferBookings",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CostToConfirm",
                table: "PackageBookings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CostToConfirmReason",
                table: "PackageBookings",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OperatorPaymentDeadline",
                table: "PackageBookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EnableCatalogFindOrCreate",
                table: "OperationalFinanceSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "EnableServiceDeadlineAlerts",
                table: "OperationalFinanceSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Default 60 a nivel BD (no 0): asi la fila de settings ya existente queda en 60 al aplicar
            // la migracion. Mismo patron que OperatorRefundTimeoutDays (ver FC1_2_0). El inicializador
            // = 60 de la entidad solo cubre filas NUEVAS creadas en codigo, no backfillea las viejas.
            migrationBuilder.AddColumn<int>(
                name: "StaleCostReferenceDays",
                table: "OperationalFinanceSettings",
                type: "integer",
                nullable: false,
                defaultValue: 60);

            migrationBuilder.AddColumn<bool>(
                name: "CostToConfirm",
                table: "HotelBookings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CostToConfirmReason",
                table: "HotelBookings",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OperatorPaymentDeadline",
                table: "HotelBookings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CostToConfirm",
                table: "FlightSegments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CostToConfirmReason",
                table: "FlightSegments",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TicketingDeadline",
                table: "FlightSegments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CostToConfirm",
                table: "AssistanceBookings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CostToConfirmReason",
                table: "AssistanceBookings",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CostToConfirm",
                table: "TransferBookings");

            migrationBuilder.DropColumn(
                name: "CostToConfirmReason",
                table: "TransferBookings");

            migrationBuilder.DropColumn(
                name: "CostToConfirm",
                table: "PackageBookings");

            migrationBuilder.DropColumn(
                name: "CostToConfirmReason",
                table: "PackageBookings");

            migrationBuilder.DropColumn(
                name: "OperatorPaymentDeadline",
                table: "PackageBookings");

            migrationBuilder.DropColumn(
                name: "EnableCatalogFindOrCreate",
                table: "OperationalFinanceSettings");

            migrationBuilder.DropColumn(
                name: "EnableServiceDeadlineAlerts",
                table: "OperationalFinanceSettings");

            migrationBuilder.DropColumn(
                name: "StaleCostReferenceDays",
                table: "OperationalFinanceSettings");

            migrationBuilder.DropColumn(
                name: "CostToConfirm",
                table: "HotelBookings");

            migrationBuilder.DropColumn(
                name: "CostToConfirmReason",
                table: "HotelBookings");

            migrationBuilder.DropColumn(
                name: "OperatorPaymentDeadline",
                table: "HotelBookings");

            migrationBuilder.DropColumn(
                name: "CostToConfirm",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "CostToConfirmReason",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "TicketingDeadline",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "CostToConfirm",
                table: "AssistanceBookings");

            migrationBuilder.DropColumn(
                name: "CostToConfirmReason",
                table: "AssistanceBookings");
        }
    }
}
