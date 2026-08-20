using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// Obra "la ficha del operador no borra la historia" (2026-08-20, punto 5, ALTO RIESGO T-8): limpia
    /// GUIDs internos fosilizados en <c>ManualCashMovements.Description</c> — texto que un cajero (usuario
    /// NO programador) lee en el Libro de Caja. Corrige DOS bugs ya arreglados en el CODIGO (los movimientos
    /// nuevos ya nacen limpios), pero las filas viejas de la ventana 2026-05 a 2026-07 quedaron con el texto
    /// roto para siempre si nadie las reescribe:
    ///
    /// <list type="bullet">
    /// <item>H5 (fix 2026-07-25, <c>ManualCashMovementBuilder.BuildIncomeForRefund</c>): "Devolucion del
    /// operador {nombre} ({guid})" -&gt; "Devolucion del operador {nombre}".</item>
    /// <item>Lote 2 (fix 2026-07-27, <c>ManualCashMovementBuilder.BuildExpenseForWithdrawal</c>): "Retiro
    /// credito cliente {guid} ({Kind})" -&gt; una frase criolla por <c>WithdrawalKind</c> (con el NOMBRE del
    /// cliente, dato que el texto viejo ni siquiera tenia).</item>
    /// </list>
    ///
    /// <para><b>Por que es SQL crudo con WHERE ultra-especifico (no un UPDATE masivo)</b>: cada UPDATE trae
    /// una expresion regular de Postgres (<c>~</c>) que matchea EXACTAMENTE el patron viejo (GUID entre
    /// parentesis al final, o el template viejo completo de retiro). Ninguna otra columna ni ninguna otra
    /// fila se toca. Es IDEMPOTENTE por construccion: una vez reescrita, la Description ya NO matchea el
    /// patron viejo, asi que correr esta migracion de nuevo (o en una base que ya tenga el codigo nuevo
    /// desde el dia 1, sin filas rotas) es un no-op — cero filas afectadas.</para>
    ///
    /// <para><b>Segundo UPDATE (retiro de credito) hace JOIN a datos VIVOS, no a texto</b>: el template
    /// viejo del retiro NO tenia el nombre del cliente (solo un GUID y el <c>WithdrawalKind</c> crudo), asi
    /// que no alcanza con recortar el texto — hace falta reconstruirlo. Se lee <c>Kind</c> y el nombre del
    /// cliente de las tablas REALES (<c>ClientCreditWithdrawals</c> -&gt; <c>ClientCreditEntries</c> -&gt;
    /// <c>Customers</c>), nunca del texto roto: son la fuente de verdad, no un intento de "adivinar" el
    /// nombre parseando el GUID viejo.</para>
    ///
    /// <para><b>Down() vacio a proposito</b>: revertir un texto LIMPIO a uno con un GUID filtrado seria
    /// reintroducir a mano el bug que esta migracion corrige — no hay "vuelta atras" deseable para esta
    /// limpieza (T-8: migracion aditiva/correctiva, no estructural; no hay columna que dropear).</para>
    /// </summary>
    public partial class BackfillManualCashMovementLegacyDescriptions : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ============================================================================================
            // Fix 1 (H5, 2026-07-25): "Devolucion del operador {nombre} ({guid})" -> "Devolucion del operador {nombre}"
            //
            // El nombre del operador YA esta en el texto (antes del parentesis): alcanza con recortar el
            // sufijo " (guid)" al final, sin tocar ninguna otra tabla. regexp_replace con '\1' conserva
            // TODO lo que hay antes del parentesis tal cual (incluido un nombre de operador que a su vez
            // tuviera parentesis propios, porque el patron ancla el GUID pegado al final de la cadena "$").
            // ============================================================================================
            migrationBuilder.Sql(@"
                UPDATE ""ManualCashMovements""
                SET ""Description"" = regexp_replace(
                    ""Description"",
                    '^(Devolucion del operador .+) \([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\)$',
                    '\1'
                )
                WHERE ""Description"" ~ '^Devolucion del operador .+ \([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\)$';
            ");

            // ============================================================================================
            // Fix 2 (Lote 2, 2026-07-27): "Retiro credito cliente {guid} ({Kind})" -> frase criolla por Kind,
            // CON el nombre del cliente (que el texto viejo no tenia). WithdrawalKind en BD es un entero:
            // 1 = PhysicalCash, 2 = Transfer, 4 = ReversedToOperator (ver enum WithdrawalKind.cs). El WHERE
            // exige que la fila matchee el patron viejo Y tenga un ClientCreditWithdrawalId enlazado — si
            // por algun motivo el enlace faltara, la fila NO se toca (mejor dejarla con el texto viejo que
            // adivinar datos que no se pueden reconstruir con certeza).
            // ============================================================================================
            migrationBuilder.Sql(@"
                UPDATE ""ManualCashMovements"" AS mcm
                SET ""Description"" = CASE w.""Kind""
                    WHEN 1 THEN 'Retiro de saldo a favor ' ||
                        (CASE WHEN c.""FullName"" IS NOT NULL AND btrim(c.""FullName"") <> ''
                              THEN 'de ' || c.""FullName"" ELSE 'del cliente' END) ||
                        ' en efectivo'
                    WHEN 2 THEN 'Retiro de saldo a favor ' ||
                        (CASE WHEN c.""FullName"" IS NOT NULL AND btrim(c.""FullName"") <> ''
                              THEN 'de ' || c.""FullName"" ELSE 'del cliente' END) ||
                        ' por transferencia'
                    WHEN 4 THEN 'Devolucion al operador del saldo a favor ' ||
                        (CASE WHEN c.""FullName"" IS NOT NULL AND btrim(c.""FullName"") <> ''
                              THEN 'de ' || c.""FullName"" ELSE 'del cliente' END)
                    ELSE mcm.""Description""
                END
                FROM ""ClientCreditWithdrawals"" AS w
                JOIN ""ClientCreditEntries"" AS e ON e.""Id"" = w.""ClientCreditEntryId""
                JOIN ""Customers"" AS c ON c.""Id"" = e.""CustomerId""
                WHERE mcm.""ClientCreditWithdrawalId"" = w.""Id""
                  AND mcm.""Description"" ~ '^Retiro credito cliente [0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12} \((PhysicalCash|Transfer|ReversedToOperator)\)$';
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Sin revertir a proposito: ver el XML-doc de la clase ("Down() vacio a proposito").
        }
    }
}
