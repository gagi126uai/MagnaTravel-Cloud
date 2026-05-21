using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// FC1.3.0a (ADR-009 §2.2 punto 10 / §5.1 M0 / RH-006, 2026-05-21):
    /// migracion pre-requisito de FC1.3 — habilita concurrency token <c>xmin</c>
    /// para la tabla <c>ApprovalRequests</c>.
    ///
    /// <para><b>Por que existe esta migracion separada</b>:
    /// FC1.3 introduce edicion del <c>Metadata</c> de un approval pending por
    /// parte de un admin. Si dos admins lo editan en paralelo (escenario
    /// realista: ambos abren la bandeja al mismo tiempo), sin lock optimista
    /// el "last write wins" pisa cambios fiscales silenciosamente. Esta
    /// migracion entra como HOTFIX antes que el resto de FC1.3 para que la
    /// proteccion exista al momento de subir el flag <c>EnablePartialCreditNotes</c>.
    /// </para>
    ///
    /// <para><b>Que hace realmente</b>:
    /// EL SQL emitido para <c>Up()</c> es VACIO. <c>xmin</c> es una pseudo-columna
    /// de sistema que Postgres mantiene automaticamente en TODAS las tablas
    /// (es el id de la transaccion que ultimo modifico la fila). Npgsql
    /// detecta que <c>xmin</c> es columna de sistema y NO emite
    /// <c>ALTER TABLE ApprovalRequests ADD COLUMN xmin</c> — verificado con
    /// <c>dotnet ef migrations script ... --idempotent</c>: el bloque generado
    /// solo registra el <c>MigrationId</c> en <c>__EFMigrationsHistory</c>.
    /// </para>
    ///
    /// <para><b>Pero entonces por que aparece <c>AddColumn</c> en este Up()</b>:
    /// Para que el snapshot del modelo EF refleje la shadow property uint que
    /// agrega <c>UseXminAsConcurrencyToken()</c>. Sin esto el snapshot
    /// (<c>...Designer.cs</c> + <c>AppDbContextModelSnapshot.cs</c>) queda
    /// desincronizado y la proxima migracion EF generada interpreta que la
    /// propiedad "se agrego" y vuelve a emitir el cambio.
    /// </para>
    ///
    /// <para><b>Rollback</b>: inocuo. <c>Down()</c> tampoco emite DDL real
    /// (Npgsql intercepta el <c>DROP COLUMN xmin</c> tambien). Lo unico que
    /// "se pierde" es la registracion del concurrency token en el modelo —
    /// las filas existentes y sus xmin siguen intactas (son metadata de
    /// sistema de Postgres). Si FC1.3 vuelve a prenderse, re-aplicar M0.
    /// </para>
    ///
    /// <para><b>Datos</b>: aditiva, no toca filas existentes. <c>xmin</c> de
    /// cada fila se inicializa automaticamente con el txid de la transaccion
    /// que creo la fila — Postgres ya lo hizo desde siempre, lo unico nuevo
    /// es que EF lo lee ahora como concurrency token.
    /// </para>
    /// </summary>
    public partial class FC1_3_PRE_AddApprovalRequestConcurrencyToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Npgsql intercepta esta operacion: <c>xmin</c> es columna de sistema
            // Postgres, por lo que el SQL emitido al ejecutar la migracion contra
            // la BD es VACIO. La presencia del AddColumn aqui es exclusivamente
            // para que el snapshot del modelo refleje la shadow property uint
            // que agrega <c>UseXminAsConcurrencyToken()</c>.
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                table: "ApprovalRequests",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Tambien no-op a nivel SQL — Npgsql NO emite <c>DROP COLUMN xmin</c>
            // porque xmin no existe en <c>information_schema.columns</c>. Solo
            // limpia el registro de la migracion del snapshot del modelo.
            migrationBuilder.DropColumn(
                name: "xmin",
                table: "ApprovalRequests");
        }
    }
}
