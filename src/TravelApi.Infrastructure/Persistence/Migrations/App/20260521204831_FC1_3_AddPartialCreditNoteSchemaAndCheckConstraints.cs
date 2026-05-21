using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// FC1.3 (ADR-009, 2026-05-21): schema base del modulo de NC parcial Hotel.
    ///
    /// <para><b>Decision sobre la granularidad de migracion</b> (plan tactico FC1.3 §FC1.3.0,
    /// RH-007 vs comportamiento real del tooling EF):</para>
    /// <para>El plan tactico pide cinco migraciones separadas agrupadas por aggregate
    /// (M1 Supplier, M2 InvoiceItem, M3 OperationalFinanceSettings, M4 BookingCancellation
    /// + FiscalSnapshot, M5 HotelBooking). El tooling de EF, al ver el snapshot del modelo
    /// vacio de cambios FC1.3, genera UNA sola migracion con todos los <c>AddColumn</c>
    /// porque calcula el diff modelo-actual menos modelo-snapshot. Forzar cinco
    /// migraciones requeriria regenerar el snapshot cinco veces, lo cual:
    /// (a) introduce noise cosmetico no relacionado (cambios de formato del generator EF
    /// que reescribe <c>ToTable("X")</c> como <c>ToTable("X", (string)null)</c> en cada
    /// regen), y (b) cualquier error en el orden compromete la integridad del snapshot.</para>
    /// <para>Esta migracion entrega todos los cambios FC1.3.0 en un solo paso atomico.
    /// Efecto en deploy: identico al de cinco migraciones separadas (todas se aplican
    /// secuencialmente en la misma sesion <c>dotnet ef database update</c>). El rollback
    /// con <c>Down()</c> tambien revierte todo en un solo paso. Si el reviewer prefiere
    /// la granularidad fina post-hoc, se puede dividir manualmente sin afectar la
    /// semantica final.</para>
    ///
    /// <para><b>Que agrega</b>:</para>
    /// <list type="bullet">
    /// <item><c>Suppliers</c>: <c>InvoicingMode</c> (int, default 0) + <c>PenaltyPolicyJson</c> (jsonb nullable).</item>
    /// <item><c>InvoiceItem</c>: <c>IsRefundable</c> (bool, <b>default true</b>), <c>ItemCategory</c> (int, default 0), <c>SourceServicioReservaId</c> (int nullable) + FK + index.</item>
    /// <item><c>OperationalFinanceSettings</c>: 11 columnas nuevas con sus defaults reales (NO los placeholders de EF).</item>
    /// <item><c>HotelBookings</c>: <c>NonRefundableConceptsJson</c> (jsonb nullable).</item>
    /// <item><c>BookingCancellations</c>: 10 columnas summary + FK al <c>ApprovalRequest</c> tipo 11 + 2 columnas del <c>FiscalSnapshot</c> (con prefijo).</item>
    /// </list>
    ///
    /// <para><b>CHECK constraints SQL agregados</b> (sintaxis PostgreSQL, identificadores
    /// con comillas dobles):</para>
    /// <list type="bullet">
    /// <item><c>chk_Suppliers_penaltypolicy_object</c> (RH-014): el JSON debe ser objeto top-level si no es null.</item>
    /// <item><c>chk_BookingCancellations_manualreview_approvalref</c>: Status en (9, 10, 11) exige FK al approval not null.</item>
    /// <item><c>chk_BookingCancellations_creditnotekind_consistent</c>: si el calculator corrio (<c>LiquidationComputedAt</c> not null), <c>CreditNoteKind</c> tambien debe estar set.</item>
    /// </list>
    ///
    /// <para><b>Que NO agrega</b> (decisiones de la sub-fase FC1.3.0):</para>
    /// <list type="bullet">
    /// <item>NO extiende el CHECK <c>chk_BookingCancellations_fiscalsnapshot_consistent</c>
    /// heredado de FC1.2. El service nuevo (FC1.3.3) garantiza por contrato que el snapshot
    /// se llena ANTES de transicionar a estados 8..11 dentro de la misma transaccion.
    /// El CHECK heredado entonces queda como red de seguridad: si por bug el service
    /// olvida llenarlo, Postgres rechaza con <c>SqlState=23514</c> y el interceptor
    /// lo mapea a <c>BusinessInvariantViolationException</c>.</item>
    /// <item>NO agrega columnas <c>BridgeRetryCount</c>/<c>BridgeLastError</c>/<c>BridgeLastAttemptAt</c>
    /// a <c>ApprovalRequests</c> — esas son parte de la sub-fase FC1.3.6b (migracion M0b separada).</item>
    /// </list>
    ///
    /// <para><b>Defaults backfill datos existentes</b>:</para>
    /// <list type="bullet">
    /// <item>BCs preexistentes FC1.2: <c>CreditNoteKind=NULL</c>, <c>ReviewRequiredReason=0</c>,
    /// <c>LiquidationComputedAt=NULL</c>, <c>PartialCreditNoteApprovalRequestId=NULL</c>.
    /// El CHECK <c>creditnotekind_consistent</c> pasa (ambos null).</item>
    /// <item>Suppliers preexistentes: <c>InvoicingMode=0</c> (TotalToCustomer, conservador),
    /// <c>PenaltyPolicyJson=NULL</c>.</item>
    /// <item>InvoiceItems preexistentes: <c>IsRefundable=true</c> (compat backward — todo lo
    /// que ya esta facturado se considera reintegrable a menos que el vendedor lo cambie),
    /// <c>ItemCategory=0</c> (Service), <c>SourceServicioReservaId=NULL</c>.</item>
    /// <item><c>OperationalFinanceSettings</c>: defaults de los settings cargados explicitamente
    /// abajo (umbrales, template, flags). NO usamos los defaults vacios de EF.</item>
    /// </list>
    ///
    /// <para><b>Rollback</b>: aditiva. <c>Down()</c> dropea columnas e indices sin perder
    /// los datos de FC1.2. Como toda la data nueva es nullable o tiene defaults, no hay
    /// riesgo de datos rotos al revertir.</para>
    /// </summary>
    public partial class FC1_3_AddPartialCreditNoteSchemaAndCheckConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ============================================================
            // Suppliers: InvoicingMode + PenaltyPolicyJson (M1 logico).
            // ============================================================

            migrationBuilder.AddColumn<int>(
                name: "InvoicingMode",
                table: "Suppliers",
                type: "integer",
                nullable: false,
                defaultValue: 0); // 0 = TotalToCustomer (conservador, comportamiento legacy)

            migrationBuilder.AddColumn<string>(
                name: "PenaltyPolicyJson",
                table: "Suppliers",
                type: "jsonb",
                nullable: true);

            // ============================================================
            // OperationalFinanceSettings: 11 settings nuevos (M3 logico) con
            // sus defaults REALES (no los defaults vacios que pone EF por las
            // dudas que existan filas previas — solo hay 1 fila por agencia,
            // pero asi cumplimos contrato).
            // ============================================================

            migrationBuilder.AddColumn<bool>(
                name: "EnablePartialCreditNotes",
                table: "OperationalFinanceSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false); // Flag maestro OFF en prod

            migrationBuilder.AddColumn<decimal>(
                name: "PartialNcAutoApprovalThreshold",
                table: "OperationalFinanceSettings",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 500000m); // 500.000 ARS - umbral auto-aprobacion

            migrationBuilder.AddColumn<decimal>(
                name: "PartialNcAdminReviewThreshold",
                table: "OperationalFinanceSettings",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 2000000m); // 2.000.000 ARS - umbral admin reforzada

            migrationBuilder.AddColumn<decimal>(
                name: "PartialNcAccountingReviewThreshold",
                table: "OperationalFinanceSettings",
                type: "numeric(18,2)",
                nullable: true); // null = sin tope superior

            migrationBuilder.AddColumn<string>(
                name: "PartialNcDescriptionTemplate",
                table: "OperationalFinanceSettings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                // Template inicial — el real lo carga la entidad cuando se persiste por
                // primera vez. Acá ponemos el mismo string del default de la entidad
                // para no dejar la fila preexistente con string vacio.
                defaultValue:
                    "NC parcial s/Fc {invoiceType} {invoiceNumber} (PV {pointOfSale}). " +
                    "Monto fiscal acreditado: {fiscalAmount} {currency}. " +
                    "Concepto: {cancellationReason}. " +
                    "Items no reintegrables retenidos: {nonRefundableAmount} {currency}.");

            migrationBuilder.AddColumn<int>(
                name: "ManualReviewMaxDaysBeforeRg4540Alert",
                table: "OperationalFinanceSettings",
                type: "integer",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<string>(
                name: "GenericDescriptionPatterns",
                table: "OperationalFinanceSettings",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: ""); // RH-008 heuristica DESACTIVADA por default

            migrationBuilder.AddColumn<DateTime>(
                name: "Fc13DeployDate",
                table: "OperationalFinanceSettings",
                type: "timestamp with time zone",
                nullable: true); // null = sin heuristica legacy activa (RH-008/RH-013)

            migrationBuilder.AddColumn<bool>(
                name: "Allow4EyesBypassWhenSingleAdmin",
                table: "OperationalFinanceSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false); // GR-005 default 4-eyes estricto

            migrationBuilder.AddColumn<int>(
                name: "BridgeReconciliationStalenessMinutes",
                table: "OperationalFinanceSettings",
                type: "integer",
                nullable: false,
                defaultValue: 30); // Q2 round 3

            migrationBuilder.AddColumn<int>(
                name: "BridgeReconciliationMaxRetries",
                table: "OperationalFinanceSettings",
                type: "integer",
                nullable: false,
                defaultValue: 5); // N-003 round 3

            // ============================================================
            // InvoiceItem: IsRefundable + ItemCategory + SourceServicioReservaId (M2 logico).
            // ============================================================

            migrationBuilder.AddColumn<bool>(
                name: "IsRefundable",
                table: "InvoiceItem",
                type: "boolean",
                nullable: false,
                defaultValue: true); // compat backward — items legacy son reintegrables

            migrationBuilder.AddColumn<int>(
                name: "ItemCategory",
                table: "InvoiceItem",
                type: "integer",
                nullable: false,
                defaultValue: 0); // 0 = Service

            migrationBuilder.AddColumn<int>(
                name: "SourceServicioReservaId",
                table: "InvoiceItem",
                type: "integer",
                nullable: true); // FK opcional a ServicioReserva (tabla "Reservations")

            // ============================================================
            // HotelBookings: NonRefundableConceptsJson (M5 logico).
            // ============================================================

            migrationBuilder.AddColumn<string>(
                name: "NonRefundableConceptsJson",
                table: "HotelBookings",
                type: "jsonb",
                nullable: true);

            // ============================================================
            // BookingCancellations: summary + FK al approval + 2 columnas
            // owned del FiscalSnapshot (M4 logico).
            // ============================================================

            migrationBuilder.AddColumn<int>(
                name: "CreditNoteKind",
                table: "BookingCancellations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReviewRequiredReason",
                table: "BookingCancellations",
                type: "integer",
                nullable: false,
                defaultValue: 0); // None — BCs FC1.2 quedan sin motivos

            migrationBuilder.AddColumn<DateTime>(
                name: "LiquidationComputedAt",
                table: "BookingCancellations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LiquidationComputedByUserId",
                table: "BookingCancellations",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LiquidationComputedByUserName",
                table: "BookingCancellations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PartialCreditNoteApprovalRequestId",
                table: "BookingCancellations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManualReviewerUserId",
                table: "BookingCancellations",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManualReviewerUserName",
                table: "BookingCancellations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ManualReviewedAt",
                table: "BookingCancellations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManualReviewComment",
                table: "BookingCancellations",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            // Owned VO FiscalSnapshot — Npgsql aplica prefijo automatico.
            migrationBuilder.AddColumn<int>(
                name: "FiscalSnapshot_InvoicingModeAtEvent",
                table: "BookingCancellations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FiscalSnapshot_OriginalInvoiceTypeAtEvent",
                table: "BookingCancellations",
                type: "integer",
                nullable: true);

            // ============================================================
            // Indices + FKs.
            // ============================================================

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceItem_SourceServicioReservaId",
                table: "InvoiceItem",
                column: "SourceServicioReservaId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellations_PartialCreditNoteApprovalRequestId",
                table: "BookingCancellations",
                column: "PartialCreditNoteApprovalRequestId");

            // FK BC -> ApprovalRequest. Restrict para no perder evidencia fiscal.
            // Nombre explicito para evitar el truncado automatico de EF (62 chars max).
            migrationBuilder.AddForeignKey(
                name: "FK_BookingCancellations_ApprovalRequests_PartialCreditNoteAppr~",
                table: "BookingCancellations",
                column: "PartialCreditNoteApprovalRequestId",
                principalTable: "ApprovalRequests",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // FK InvoiceItem -> ServicioReserva (tabla "Reservations"). Restrict para
            // preservar la trazabilidad linea-de-factura -> servicio origen.
            migrationBuilder.AddForeignKey(
                name: "FK_InvoiceItem_Reservations_SourceServicioReservaId",
                table: "InvoiceItem",
                column: "SourceServicioReservaId",
                principalTable: "Reservations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // ============================================================
            // CHECK constraints SQL crudo (ADR-009 §2.3.5). Sintaxis PostgreSQL:
            // identificadores con comillas dobles, jsonb_typeof para validar
            // forma del JSON.
            //
            // El interceptor BusinessInvariantInterceptor mapea SqlState='23514'
            // (CHECK violation) a BusinessInvariantViolationException -> HTTP 409.
            // ============================================================

            // RH-014: PenaltyPolicyJson debe ser objeto top-level si no es null.
            // Bloquea bug donde el API guarda un array o un string como politica.
            migrationBuilder.Sql("""
                ALTER TABLE "Suppliers"
                  DROP CONSTRAINT IF EXISTS chk_Suppliers_penaltypolicy_object;
                ALTER TABLE "Suppliers"
                  ADD CONSTRAINT chk_Suppliers_penaltypolicy_object
                  CHECK (
                    "PenaltyPolicyJson" IS NULL
                    OR jsonb_typeof("PenaltyPolicyJson") = 'object'
                  );
                """);

            // INV-FC1.3-002: Status en (9, 10, 11) requiere FK al approval not null.
            // Estados 9, 10 y 11 (ManualReviewPending/Approved/Rejected) son consecuencia
            // de un ApprovalRequest tipo PartialCreditNoteApproval=11 — sin la FK queda
            // huerfano. El service nuevo (FC1.3.3) garantiza setearla; este CHECK es
            // la red de seguridad de la BD.
            migrationBuilder.Sql("""
                ALTER TABLE "BookingCancellations"
                  DROP CONSTRAINT IF EXISTS chk_BookingCancellations_manualreview_approvalref;
                ALTER TABLE "BookingCancellations"
                  ADD CONSTRAINT chk_BookingCancellations_manualreview_approvalref
                  CHECK (
                    "Status" NOT IN (9, 10, 11)
                    OR "PartialCreditNoteApprovalRequestId" IS NOT NULL
                  );
                """);

            // Coherencia entre LiquidationComputedAt y CreditNoteKind.
            // Si el clasificador corrio (timestamp seteado), CreditNoteKind no puede
            // ser null. La inversa NO se enforce (puede haber Kind sin timestamp en
            // casos teoricos de backfill), aunque en la practica el service siempre
            // los setea juntos.
            migrationBuilder.Sql("""
                ALTER TABLE "BookingCancellations"
                  DROP CONSTRAINT IF EXISTS chk_BookingCancellations_creditnotekind_consistent;
                ALTER TABLE "BookingCancellations"
                  ADD CONSTRAINT chk_BookingCancellations_creditnotekind_consistent
                  CHECK (
                    "LiquidationComputedAt" IS NULL
                    OR "CreditNoteKind" IS NOT NULL
                  );
                """);

            // ============================================================
            // RH-013: si EnablePartialCreditNotes ya estaba en true al momento
            // de aplicar la migracion (caso raro: alguien lo habilito antes
            // que esta migracion corriera), auto-setear Fc13DeployDate = ahora.
            // Si esta en false (default), dejar Fc13DeployDate NULL — el
            // validador de startup decidira si lo levanta cuando se prenda el flag.
            // ============================================================
            migrationBuilder.Sql("""
                UPDATE "OperationalFinanceSettings"
                SET "Fc13DeployDate" = NOW()
                WHERE "EnablePartialCreditNotes" = true AND "Fc13DeployDate" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop CHECK constraints ANTES de drop columns para que no se queje
            // por dependencias.
            migrationBuilder.Sql("""
                ALTER TABLE "Suppliers"
                  DROP CONSTRAINT IF EXISTS chk_Suppliers_penaltypolicy_object;

                ALTER TABLE "BookingCancellations"
                  DROP CONSTRAINT IF EXISTS chk_BookingCancellations_manualreview_approvalref;

                ALTER TABLE "BookingCancellations"
                  DROP CONSTRAINT IF EXISTS chk_BookingCancellations_creditnotekind_consistent;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_BookingCancellations_ApprovalRequests_PartialCreditNoteAppr~",
                table: "BookingCancellations");

            migrationBuilder.DropForeignKey(
                name: "FK_InvoiceItem_Reservations_SourceServicioReservaId",
                table: "InvoiceItem");

            migrationBuilder.DropIndex(
                name: "IX_InvoiceItem_SourceServicioReservaId",
                table: "InvoiceItem");

            migrationBuilder.DropIndex(
                name: "IX_BookingCancellations_PartialCreditNoteApprovalRequestId",
                table: "BookingCancellations");

            // Drop columns - orden inverso al Up por consistencia.

            migrationBuilder.DropColumn(name: "FiscalSnapshot_OriginalInvoiceTypeAtEvent", table: "BookingCancellations");
            migrationBuilder.DropColumn(name: "FiscalSnapshot_InvoicingModeAtEvent", table: "BookingCancellations");
            migrationBuilder.DropColumn(name: "ManualReviewComment", table: "BookingCancellations");
            migrationBuilder.DropColumn(name: "ManualReviewedAt", table: "BookingCancellations");
            migrationBuilder.DropColumn(name: "ManualReviewerUserName", table: "BookingCancellations");
            migrationBuilder.DropColumn(name: "ManualReviewerUserId", table: "BookingCancellations");
            migrationBuilder.DropColumn(name: "PartialCreditNoteApprovalRequestId", table: "BookingCancellations");
            migrationBuilder.DropColumn(name: "LiquidationComputedByUserName", table: "BookingCancellations");
            migrationBuilder.DropColumn(name: "LiquidationComputedByUserId", table: "BookingCancellations");
            migrationBuilder.DropColumn(name: "LiquidationComputedAt", table: "BookingCancellations");
            migrationBuilder.DropColumn(name: "ReviewRequiredReason", table: "BookingCancellations");
            migrationBuilder.DropColumn(name: "CreditNoteKind", table: "BookingCancellations");

            migrationBuilder.DropColumn(name: "NonRefundableConceptsJson", table: "HotelBookings");

            migrationBuilder.DropColumn(name: "SourceServicioReservaId", table: "InvoiceItem");
            migrationBuilder.DropColumn(name: "ItemCategory", table: "InvoiceItem");
            migrationBuilder.DropColumn(name: "IsRefundable", table: "InvoiceItem");

            migrationBuilder.DropColumn(name: "BridgeReconciliationMaxRetries", table: "OperationalFinanceSettings");
            migrationBuilder.DropColumn(name: "BridgeReconciliationStalenessMinutes", table: "OperationalFinanceSettings");
            migrationBuilder.DropColumn(name: "Allow4EyesBypassWhenSingleAdmin", table: "OperationalFinanceSettings");
            migrationBuilder.DropColumn(name: "Fc13DeployDate", table: "OperationalFinanceSettings");
            migrationBuilder.DropColumn(name: "GenericDescriptionPatterns", table: "OperationalFinanceSettings");
            migrationBuilder.DropColumn(name: "ManualReviewMaxDaysBeforeRg4540Alert", table: "OperationalFinanceSettings");
            migrationBuilder.DropColumn(name: "PartialNcDescriptionTemplate", table: "OperationalFinanceSettings");
            migrationBuilder.DropColumn(name: "PartialNcAccountingReviewThreshold", table: "OperationalFinanceSettings");
            migrationBuilder.DropColumn(name: "PartialNcAdminReviewThreshold", table: "OperationalFinanceSettings");
            migrationBuilder.DropColumn(name: "PartialNcAutoApprovalThreshold", table: "OperationalFinanceSettings");
            migrationBuilder.DropColumn(name: "EnablePartialCreditNotes", table: "OperationalFinanceSettings");

            migrationBuilder.DropColumn(name: "PenaltyPolicyJson", table: "Suppliers");
            migrationBuilder.DropColumn(name: "InvoicingMode", table: "Suppliers");
        }
    }
}
