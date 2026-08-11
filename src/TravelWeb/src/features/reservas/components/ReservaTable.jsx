import React from "react";
import { Archive } from "lucide-react";
import { Button } from "../../../components/ui/button";
import {
  DataGrid,
  DataGridActionCell,
  DataGridBody,
  DataGridCell,
  DataGridEmptyState,
  DataGridHeader,
  DataGridHeaderCell,
  DataGridHeaderRow,
  DataGridRow,
} from "../../../components/ui/DataGrid";
import { ReservaStatusBadge, ReservaEstadoSello } from "./ReservaStatusBadge";
import { CurrencyBadge } from "../../../components/ui/CurrencyBadge";
import { formatCurrency, formatDate } from "../../../lib/utils";
import { getPublicId } from "../../../lib/publicIds";
import { getReservaArchiveBlockReason } from "../archiveRules";
import { getReservaSaleLines, getReservaFinanzasChips, FINANZAS_CHIP_TONE_CLASSES } from "../lib/reservaMoneyDisplay";
import { debeMostrarComoSello } from "../lib/reservaEstadoSelloLogic";

/**
 * Tabla del listado de Reservas (versión de escritorio). Tanda 1 rediseño
 * (2026-08-04, plan B4): el renglón chico bajo el número de reserva pasa a
 * mostrar el DESTINO en vez del nombre autogenerado ("Reserva F-2026-…"), la
 * columna Finanzas separa cada moneda en su propia línea (P-3⭐) y la única
 * acción por fila es "Archivar" con la palabra al lado del ícono y, si está
 * bloqueada, el motivo del motor (P-9) — se elimina el botón de chat, que solo
 * repetía lo que ya hace un clic en la fila.
 *
 * Motivo de "Archivar" bloqueado (decisión del dueño, 11/08/2026 — REEMPLAZA la de
 * 2026-08-04, y enmendada el mismo día en P-9 de la constitución tras el review B1/B2):
 * en listados de ESCRITORIO va como globito nativo (`title`) al pasar el mouse, en vez
 * del texto fijo debajo de cada fila — con muchas reservas en la tabla, el motivo
 * repetido en cada renglón era ruido visual (P-16: un dato no se dice dos veces). El
 * `title` vive en un <span> que ENVUELVE al botón, no en el botón mismo: el Button de
 * shadcn tiene `disabled:pointer-events-none`, así que un botón deshabilitado nunca
 * dispara hover — el envoltorio sí lo recibe. En mobile/táctil (ReservaMobileList.jsx)
 * no hay hover, así que ahí el motivo sigue escrito a la vista, sin cambios.
 *
 * `emptyState`: nodo opcional que reemplaza el cartel por default cuando no hay
 * filas. ReservasPage arma un mensaje distinto según el motivo (mes sin datos,
 * búsqueda sin resultados) porque solo esa pantalla tiene los datos del filtro
 * activo (mes, período) para escribir un mensaje útil.
 *
 * Lavado de cara (2026-08-11, maqueta firmada docs/ux/2026-08-11-maqueta-reservas-
 * firmada.html): la columna Reserva combina destino y fecha de viaje en UNA sola
 * línea gris (antes eran dos, con un puntito índigo) · la columna Cliente/pasajeros
 * pierde los íconos de persona (la columna ya dice "Cliente", el ícono no sumaba
 * nada) y dice "pasajero(s)" entero en vez de la abreviatura "pax" · los importes
 * de Finanzas llevan el cartelito de moneda (CurrencyBadge) al lado en vez del
 * símbolo pegado al número, con las cifras en `tabular-nums` para que las comas
 * queden alineadas leyendo la columna de arriba a abajo (P-3⭐) · el Estado de las
 * reservas ya sin efecto (Anulada/Perdida/Finalizada) se reemplaza por el SELLO en
 * vez del chip de color (ver `debeMostrarComoSello`/`ReservaEstadoSello`). La
 * columna Acciones ("Archivar" + su globito) NO se tocó en esta tanda.
 */
export function ReservaTable({ reservas, onRowClick, onArchive, emptyState, className }) {
  return (
    // `className`: la Tanda de realineación a la maqueta (2026-08-04) mete esta tabla
    // DENTRO de la tarjeta única de ReservasPage — así que acá se le puede pisar el
    // marco propio (borde/sombra/fondo) para que no queden dos tarjetas anidadas.
    // `density="compact"`: decisión 2A del dueño (2026-08-11, "listas compactas") —
    // se pisa solo ACÁ (prop explícita), sin tocar el default "comfortable" que
    // siguen usando el resto de las tablas de la app (proveedores, clientes, etc.).
    <DataGrid minWidth="920px" className={className} density="compact">
      <DataGridHeader>
        <DataGridHeaderRow>
          <DataGridHeaderCell>Reserva</DataGridHeaderCell>
          <DataGridHeaderCell>Cliente / pasajeros</DataGridHeaderCell>
          <DataGridHeaderCell>Estado</DataGridHeaderCell>
          <DataGridHeaderCell>Creada</DataGridHeaderCell>
          <DataGridHeaderCell align="right">Finanzas</DataGridHeaderCell>
          <DataGridHeaderCell align="center">Acciones</DataGridHeaderCell>
        </DataGridHeaderRow>
      </DataGridHeader>
      <DataGridBody>
        {reservas.length === 0 ? (
          emptyState ? (
            <tr>
              <td colSpan={6} className="p-0">
                {emptyState}
              </td>
            </tr>
          ) : (
            <DataGridEmptyState
              colSpan={6}
              icon={Archive}
              title="No se encontraron reservas"
              description="Probá cambiando los filtros."
            />
          )
        ) : (
          reservas.map((reserva) => {
            const archiveBlockReason = getReservaArchiveBlockReason(reserva);
            const canArchive = !archiveBlockReason;
            const ventaLineas = getReservaSaleLines(reserva);
            const chips = getReservaFinanzasChips(reserva);

            // Segunda línea de la columna Reserva: destino y fecha de viaje, combinados
            // en un solo renglón gris separado por "·" (maqueta firmada). Si falta uno de
            // los dos datos, se muestra solo el que hay; si faltan los dos, no hay segunda
            // línea (no dejamos un "·" solo ni un renglón vacío).
            const destinoYViaje = [
              reserva.destino || null,
              reserva.startDate ? `Viaja ${formatDate(reserva.startDate)}` : null,
            ].filter(Boolean).join(" · ");

            const pasajeros = reserva.passengerCount || 0;
            const etiquetaPasajeros = pasajeros === 1 ? "pasajero" : "pasajeros";

            return (
              <DataGridRow
                key={getPublicId(reserva)}
                clickable
                onClick={() => onRowClick(getPublicId(reserva))}
              >
                <DataGridCell>
                  <div className="flex flex-col">
                    {/* Fix review (2026-08-11, I5): el hover era índigo — mata el índigo
                        suelto y usa el mismo azul boleto (token `primary`) que el resto
                        de las acciones de la app, en vez de un tercer color a mano. */}
                    <span className="text-sm font-bold text-slate-900 transition-colors hover:text-primary dark:text-white dark:hover:text-primary">
                      #{reserva.numeroReserva}
                    </span>
                    {destinoYViaje ? (
                      <span className="mt-0.5 line-clamp-1 text-xs text-slate-500 dark:text-slate-400">
                        {destinoYViaje}
                      </span>
                    ) : null}
                  </div>
                </DataGridCell>
                <DataGridCell>
                  {/* Fix Lavado de cara: se sacan los íconos de persona/personas — la
                      columna ya se llama "Cliente / pasajeros", el ícono no agregaba
                      información (hallazgo de la auditoría del estándar visual, A.2). */}
                  <div className="flex flex-col gap-0.5">
                    <span className="max-w-[180px] truncate text-sm font-medium text-slate-700 dark:text-slate-300">
                      {reserva.customerName}
                    </span>
                    <span className="text-xs text-slate-500 dark:text-slate-400">
                      {pasajeros} {etiquetaPasajeros}
                    </span>
                  </div>
                </DataGridCell>
                <DataGridCell>
                  {/* El sello reemplaza al chip SOLO en Anulada/Perdida/Finalizada — los
                      estados vivos (y Archivada) siguen con el chip de toda la vida. */}
                  {debeMostrarComoSello(reserva) ? (
                    <ReservaEstadoSello reserva={reserva} />
                  ) : (
                    <ReservaStatusBadge status={reserva.status} mostrarCandado />
                  )}
                </DataGridCell>
                <DataGridCell>
                  <div className="flex flex-col">
                    <span className="text-xs text-slate-600 dark:text-slate-300">
                      {reserva.createdAt ? formatDate(reserva.createdAt) : "-"}
                    </span>
                    {reserva.responsibleUserName ? (
                      <span className="mt-0.5 text-[10px] text-slate-400 dark:text-slate-500">
                        {reserva.responsibleUserName}
                      </span>
                    ) : null}
                  </div>
                </DataGridCell>
                <DataGridCell align="right">
                  <div className="flex flex-col items-end gap-1">
                    <div className="flex flex-col items-end gap-0.5">
                      {ventaLineas.map((linea, index) => {
                        const enCero = Number(linea.amount) === 0;
                        return (
                          // Fix Lavado de cara: el símbolo de moneda pasa del texto pegado
                          // al número ("US$800,00") a un cartelito aparte (CurrencyBadge),
                          // mismo patrón que ya usa la tabla de servicios de la ficha —
                          // `tabular-nums` alinea las cifras en columna (P-3⭐: cada moneda
                          // en su propio renglón, nunca sumadas).
                          <span key={linea.currency} className="inline-flex items-center gap-1">
                            <CurrencyBadge currency={linea.currency} />
                            <span
                              className={
                                index === 0
                                  ? `text-sm font-bold tabular-nums ${enCero ? "text-slate-300 dark:text-slate-700" : "text-slate-900 dark:text-white"}`
                                  : `text-xs font-semibold tabular-nums ${enCero ? "text-slate-300 dark:text-slate-700" : "text-slate-500 dark:text-slate-400"}`
                              }
                            >
                              {formatCurrency(linea.amount, linea.currency, { withSymbol: false })}
                            </span>
                          </span>
                        );
                      })}
                    </div>
                    <div className="flex flex-col items-end gap-1">
                      {chips.map((chip, index) => (
                        <span key={index} className={FINANZAS_CHIP_TONE_CLASSES[chip.tone]}>
                          {chip.text}
                        </span>
                      ))}
                    </div>
                  </div>
                </DataGridCell>
                <DataGridActionCell
                  align="center"
                  onClick={(event) => event.stopPropagation()}
                >
                  {/* Fix B1 (review): el Button de shadcn tiene disabled:pointer-events-none
                      en su clase base — con el botón deshabilitado, el navegador NUNCA
                      dispara el hover sobre él, así que un title puesto directo en el
                      <button> no se ve jamás. Envolvemos en un <span> (sí recibe hover) y
                      el title vive ahí — patrón "envoltorio", enmienda P-9 (11/08/2026):
                      en listados de escritorio el globito va sobre un envoltorio del botón. */}
                  <span title={archiveBlockReason || undefined}>
                    <Button
                      variant="outline"
                      size="sm"
                      disabled={!canArchive}
                      onClick={() => onArchive(reserva)}
                      className={`h-7 gap-1.5 px-2.5 text-xs font-semibold ${
                        canArchive
                          ? "text-slate-600 hover:border-amber-300 hover:bg-amber-50 hover:text-amber-700 dark:text-slate-300 dark:hover:bg-amber-900/20"
                          : "text-slate-400 dark:text-slate-600"
                      }`}
                    >
                      <Archive className="h-3.5 w-3.5" />
                      Archivar
                    </Button>
                  </span>
                </DataGridActionCell>
              </DataGridRow>
            );
          })
        )}
      </DataGridBody>
    </DataGrid>
  );
}
