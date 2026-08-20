/**
 * Sección "Reembolsos ya registrados" de la solapa "Reembolsos" del operador (Tanda P2,
 * spec docs/ux/2026-07-22-p2-deshacer-reasociar-reembolso.md).
 *
 * Muestra lo que YA se anotó como recibido de este operador (vivo y deshecho) y ofrece,
 * fila por fila, "Deshacer" y "Corregir reserva" (P1=A: bloque aparte, debajo del de
 * "a cobrar" — ese bloque es OperatorRefundsPendingSection, no se toca acá).
 *
 * Permiso de la solapa: tesoreria.supplier_payments (mismo que el bloque "a cobrar"). Las
 * acciones Deshacer/Corregir exigen además caja.edit — sin ese permiso la fila se ve pero
 * sin botones (solo lectura).
 *
 * Props:
 *   - supplierPublicId (string): GUID del operador cuya solapa se está mostrando.
 *   - onAccionCompletada (() => void): se llama tras deshacer/corregir con éxito. ÚNICA vía
 *     de recarga (fix de review, 2026-07-22): antes esta sección también se recargaba sola
 *     (reload() del hook) Y el padre remontaba todo por cambio de key — doble fetch. Ahora
 *     el padre (SupplierAccountPage) es el único que decide cuándo recargar: bumpea la key
 *     que remonta ESTE bloque junto con OperatorRefundsPendingSection y el encabezado de la
 *     cuenta, todo de una vez (regla de coherencia de la spec).
 */

import { useState } from "react";
import { Link } from "react-router-dom";
import { ExternalLink } from "lucide-react";
import { hasPermission } from "../../../auth";
import { formatCurrency, formatDate } from "../../../lib/utils";
import { useOperatorRefundsRegistered } from "../hooks/useOperatorRefundsRegistered";
import { PaginationFooter } from "../../../components/ui/PaginationFooter";
import { Button } from "../../../components/ui/button";
import { StatusChip } from "../../../components/ui/badge";
import { DeshacerReembolsoInline } from "./DeshacerReembolsoInline";
import { CorregirReembolsoInline } from "./CorregirReembolsoInline";
import { hayMontosEnmascarados } from "../lib/operatorRefundRegisteredLogic";

// ─── Fila: un reembolso ya registrado (vivo o deshecho) ───────────────────────

/**
 * Una fila del bloque "ya registrados". Si está deshecha se ve tachada/gris con la
 * etiqueta "Deshecho" y SIN botones (spec §4: "no hay nada más que hacer" con esa fila).
 *
 * `accionAbierta` viene del padre: solo una ficha inline (Deshacer o Corregir) puede
 * estar abierta a la vez en TODO el bloque — evita que el usuario tenga dos acciones de
 * plata a medio completar al mismo tiempo.
 */
function FilaReembolsoRegistrado({
  item,
  supplierPublicId,
  canEdit,
  accionAbierta,
  onAbrirDeshacer,
  onAbrirCorregir,
  onCerrarAccion,
  onAccionCompletada,
}) {
  const montoTexto = item.amountsMasked ? "—" : formatCurrency(item.netAmount, item.currency);
  const estaAbierta = accionAbierta?.allocationPublicId === item.publicId;

  // Fila deshecha: tachada/gris, con motivo y fecha, sin botones (rastro auditable que
  // nunca se borra de la lista — regla dura de la spec).
  if (item.isVoided) {
    return (
      <div
        className="px-6 py-3 opacity-60"
        data-testid={`reembolso-fila-${item.publicId}`}
        data-state="voided"
      >
        <div className="flex flex-wrap items-center gap-2 text-sm">
          <Link
            to={`/reservas/${item.reservaPublicId}`}
            className="font-semibold text-slate-500 line-through hover:underline dark:text-slate-400"
          >
            Reserva #{item.numeroReserva}
          </Link>
          <span className="text-slate-400 line-through">· {item.clienteNombre}</span>
          <StatusChip tone="neutro">Deshecho</StatusChip>
          <span className="text-slate-400 line-through">
            {item.currency} {montoTexto}
          </span>
          <span className="text-slate-400 line-through">{formatDate(item.registeredAt)}</span>
        </div>
        {item.voidedAt && (
          <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">
            Deshecho el {formatDate(item.voidedAt)}
            {item.voidedReason && <> — "{item.voidedReason}"</>}
          </p>
        )}
      </div>
    );
  }

  // Fila viva: reserva + cliente + moneda/monto + fecha, con Deshacer / Corregir si hay permiso.
  //
  // Bloque 1 "descalce devolución-caja" (2026-08-19): cuando esta fila no cierra contra la caja,
  // se agrega la cajita ámbar de abajo. El orden visual es DISTINTO en escritorio (info + botones
  // en la misma línea, cajita abajo ocupando todo el ancho) y en mobile (info, cajita, botones al
  // final) — en vez de duplicar los botones en el DOM (rompería los data-testid, que tienen que
  // ser únicos para que la automatización los encuentre), usamos `order` de flexbox: es el MISMO
  // elemento, solo cambia dónde se dibuja según el ancho de pantalla.
  return (
    <div className="px-6 py-3" data-testid={`reembolso-fila-${item.publicId}`} data-state="live">
      <div className="flex flex-col sm:flex-row sm:flex-wrap sm:items-center sm:justify-between gap-2">
        <div className="order-1 flex flex-wrap items-center gap-2 text-sm min-w-0">
          <Link
            to={`/reservas/${item.reservaPublicId}`}
            className="font-semibold text-slate-900 hover:underline dark:text-white"
          >
            Reserva #{item.numeroReserva}
          </Link>
          {item.clienteNombre && (
            <span className="text-slate-500 dark:text-slate-400">· {item.clienteNombre}</span>
          )}
          <span className="text-[11px] font-black uppercase tracking-wider text-muted-foreground">
            {item.currency}
          </span>
          <span className="text-slate-700 dark:text-slate-300">{montoTexto}</span>
          <span className="text-xs text-slate-400">{formatDate(item.registeredAt)}</span>
        </div>

        {canEdit && (
          <div className="order-3 sm:order-2 flex flex-shrink-0 gap-2">
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => onAbrirDeshacer(item)}
              disabled={estaAbierta}
              data-testid="reembolso-deshacer-boton"
            >
              Deshacer
            </Button>
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => onAbrirCorregir(item)}
              disabled={estaAbierta}
              data-testid="reembolso-corregir-boton"
            >
              Corregir reserva
            </Button>
          </div>
        )}

        {item.hasCashLedgerDivergence && (
          <div
            className="order-2 sm:order-3 sm:basis-full mt-1 rounded-[10px] border border-amber-200 bg-amber-50 px-3 py-2 dark:border-amber-900/40 dark:bg-amber-950/20"
            data-testid={`descalce-caja-${item.publicId}`}
          >
            <StatusChip tone="ambar">No coincide con la caja</StatusChip>

            {/* F-14: sin permiso de costos el backend manda derivedAmount/ledgerAmount en 0
                (mismo enmascarado que netAmount). Mostrar esos ceros como si fueran plata real
                sería mentirle al usuario, así que en ese caso el chip queda solo, sin números. */}
            {!item.amountsMasked && (
              <div className="mt-1.5 space-y-0.5 text-xs text-amber-900 dark:text-amber-200">
                <div className="flex items-center justify-between gap-4">
                  <span>Figura recibido</span>
                  <span>{formatCurrency(item.derivedAmount, item.currency)}</span>
                </div>
                <div className="flex items-center justify-between gap-4">
                  <span>En la caja</span>
                  <span>{formatCurrency(item.ledgerAmount, item.currency)}</span>
                </div>
                <div className="flex items-center justify-between gap-4 font-bold">
                  <span>Diferencia</span>
                  <span>{formatCurrency(Math.abs(item.derivedAmount - item.ledgerAmount), item.currency)}</span>
                </div>
              </div>
            )}

            <div className="mt-1.5 flex flex-wrap gap-x-4 gap-y-1">
              <Link
                to={`/reservas/${item.reservaPublicId}`}
                className="inline-flex items-center gap-1 text-xs font-semibold text-amber-800 underline underline-offset-2 hover:text-amber-950 dark:text-amber-300"
                data-testid={`descalce-caja-link-anulacion-${item.publicId}`}
              >
                Ver la anulación <ExternalLink className="h-3 w-3" />
              </Link>
              {/* La página de Caja no tiene pestañas ni resalta filas por hoy — ir al listado
                  general es lo mejor disponible sin trabajo nuevo (mejora futura opcional). */}
              <Link
                to="/cash"
                className="inline-flex items-center gap-1 text-xs font-semibold text-amber-800 underline underline-offset-2 hover:text-amber-950 dark:text-amber-300"
                data-testid={`descalce-caja-link-movimiento-${item.publicId}`}
              >
                Ver el movimiento de caja <ExternalLink className="h-3 w-3" />
              </Link>
            </div>
          </div>
        )}
      </div>

      {estaAbierta && accionAbierta.tipo === "deshacer" && (
        <DeshacerReembolsoInline item={item} onCerrar={onCerrarAccion} onCompletado={onAccionCompletada} />
      )}
      {estaAbierta && accionAbierta.tipo === "corregir" && (
        <CorregirReembolsoInline
          item={item}
          supplierId={supplierPublicId}
          onCerrar={onCerrarAccion}
          onCompletado={onAccionCompletada}
        />
      )}
    </div>
  );
}

// ─── Componente principal exportado ───────────────────────────────────────────

export function OperatorRefundsRegisteredSection({ supplierPublicId, onAccionCompletada }) {
  // Gate de permiso: mismo criterio que OperatorRefundsPendingSection — el backend valida
  // el mismo permiso, esto es solo para no confundir al usuario.
  const puedeVer = hasPermission("tesoreria.supplier_payments");
  const puedeEditar = hasPermission("caja.edit");

  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(25);

  const { items, totalCount, totalPages, hasNextPage, hasPreviousPage, loading, error, reload } =
    useOperatorRefundsRegistered({ supplierPublicId, page, pageSize, enabled: puedeVer });

  // Solo UNA ficha inline (Deshacer o Corregir) abierta a la vez en todo el bloque.
  const [accionAbierta, setAccionAbierta] = useState(null);

  if (!puedeVer) {
    return null;
  }

  const handleAbrirDeshacer = (item) => setAccionAbierta({ allocationPublicId: item.publicId, tipo: "deshacer" });
  const handleAbrirCorregir = (item) => setAccionAbierta({ allocationPublicId: item.publicId, tipo: "corregir" });
  const handleCerrarAccion = () => setAccionAbierta(null);

  // Tras deshacer/corregir con éxito: cerramos la ficha y avisamos al padre. NO llamamos a
  // reload() acá — el padre bumpea la key que remonta este bloque entero (junto con el de
  // "a cobrar" y el encabezado), así que un reload() propio duplicaría el fetch contra el
  // mismo remount (fix de review, 2026-07-22).
  const handleAccionCompletada = () => {
    setAccionAbierta(null);
    if (onAccionCompletada) onAccionCompletada();
  };

  return (
    <div
      className="mt-4 overflow-hidden rounded-[14px] border bg-card shadow-sm"
      data-testid="reembolsos-registrados-bloque"
    >
      <div className="flex items-center justify-between border-b px-6 py-4 flex-wrap gap-2">
        <div className="min-w-0">
          <h2 className="font-semibold text-slate-900 dark:text-white">Reembolsos ya registrados</h2>
          <p className="text-xs text-slate-500 dark:text-slate-400 mt-0.5">
            Lo que ya anotaste como devuelto por este operador.
          </p>
        </div>
        {!loading && totalCount > 0 && (
          <span className="text-sm font-semibold text-slate-600 dark:text-slate-300">
            {totalCount} caso{totalCount !== 1 ? "s" : ""}
          </span>
        )}
      </div>

      {/* Fix de review B1 (2026-07-22): aviso ÚNICO por pantalla cuando algún monto viene
          enmascarado (sin cobranzas.see_cost) — mismo texto/estilo que ya usan
          OperatorRefundsPendingSection y RegistrarReembolsoRecibidoInline. Antes, si no
          había ningún reembolso PENDIENTE arriba (bloque que sí tenía el aviso), el "—" de
          este bloque quedaba sin explicación. */}
      {!loading && !error && hayMontosEnmascarados(items) && (
        <p
          className="px-6 pt-3 text-[11px] text-muted-foreground"
          data-testid="aviso-montos-enmascarados-registrados"
        >
          No tenés permiso para ver los montos.
        </p>
      )}

      {loading ? (
        <div className="px-6 py-10 text-center text-sm text-slate-500" data-testid="reembolsos-registrados-loading">
          Cargando reembolsos…
        </div>
      ) : error ? (
        <div className="px-6 py-10 text-center space-y-2" data-testid="reembolsos-registrados-error">
          <p className="text-sm text-rose-600 dark:text-rose-400">
            No se pudo cargar la información. Intentá de nuevo.
          </p>
          <Button type="button" variant="link" size="sm" onClick={reload} className="h-auto p-0 text-xs">
            Reintentar
          </Button>
        </div>
      ) : items.length === 0 ? (
        <div
          className="px-6 py-10 text-center text-sm text-slate-500 dark:text-slate-400"
          data-testid="reembolsos-registrados-vacio"
        >
          Todavía no registraste ningún reembolso de este operador.
        </div>
      ) : (
        <>
          <div className="divide-y divide-slate-100 dark:divide-slate-800">
            {items.map((item) => (
              <FilaReembolsoRegistrado
                key={item.publicId}
                item={item}
                supplierPublicId={supplierPublicId}
                canEdit={puedeEditar}
                accionAbierta={accionAbierta}
                onAbrirDeshacer={handleAbrirDeshacer}
                onAbrirCorregir={handleAbrirCorregir}
                onCerrarAccion={handleCerrarAccion}
                onAccionCompletada={handleAccionCompletada}
              />
            ))}
          </div>
          <div className="border-t px-4 py-3">
            <PaginationFooter
              page={page}
              pageSize={pageSize}
              totalCount={totalCount}
              totalPages={totalPages}
              hasPreviousPage={hasPreviousPage}
              hasNextPage={hasNextPage}
              onPageChange={setPage}
              onPageSizeChange={(nuevoTamano) => {
                setPageSize(nuevoTamano);
                setPage(1);
              }}
            />
          </div>
        </>
      )}
    </div>
  );
}
