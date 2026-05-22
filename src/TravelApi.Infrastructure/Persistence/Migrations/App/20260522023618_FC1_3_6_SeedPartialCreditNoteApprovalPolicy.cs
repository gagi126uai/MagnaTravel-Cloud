using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// FC1.3.6 (ADR-009 §2.10 + plan tactico FC1.3 §FC1.3.6, 2026-05-21):
    /// seed inicial de la <c>ApprovalPolicy</c> para el nuevo tipo
    /// <c>PartialCreditNoteApproval=11</c>.
    ///
    /// <para>Defaults acordados con Gaston:
    /// <list type="bullet">
    ///   <item><c>RequiresApproval=TRUE</c>: la NC parcial es fiscal-sensitive
    ///   y la decision la toma siempre un admin (no auto).</item>
    ///   <item><c>ExpirationDaysOverride=5</c>: mas bajo que el default global
    ///   (7) para forzar atencion temprana. Si el approval se vence sin resolver,
    ///   el solicitante debe re-pedir — eso anula el riesgo de aprobar algo
    ///   muy viejo donde la liquidacion calculada ya no aplique.</item>
    ///   <item><c>CooldownHoursOverride=NULL</c>: hereda el cooldown global
    ///   (1 hora). Apropiado porque un BC rechazado tiene baja chance de
    ///   re-aprobarse cambiando los inputs en menos de 1h.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// El INSERT es idempotente con WHERE NOT EXISTS sobre el unique index
    /// (RequestType). Si la fila ya existe (caso re-run o env donde un admin
    /// la creo a mano via UI), la migracion no la pisa.
    /// </para>
    /// </summary>
    public partial class FC1_3_6_SeedPartialCreditNoteApprovalPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO "ApprovalPolicies" ("RequestType", "RequiresApproval", "ExpirationDaysOverride", "UpdatedAt")
                SELECT 'PartialCreditNoteApproval', TRUE, 5, NOW()
                WHERE NOT EXISTS (
                    SELECT 1 FROM "ApprovalPolicies"
                    WHERE "RequestType" = 'PartialCreditNoteApproval'
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rollback: borra la fila seeded. Si el admin la modifico a mano
            // (cambio el ExpirationDays o el RequiresApproval), igual la borramos
            // — la asuncion del Down es "volver al estado anterior a la migracion",
            // que es "no existe la fila".
            migrationBuilder.Sql("""
                DELETE FROM "ApprovalPolicies"
                WHERE "RequestType" = 'PartialCreditNoteApproval';
                """);
        }
    }
}
