/**
 * Extracto contable del proveedor: cronología de compras y pagos.
 *
 * Estilo libro mayor / extracto bancario, igual que EstadoCuentaExtracto.jsx del lado del cliente.
 * Un bloque por moneda. Dentro de cada bloque, columnas Cargo / Abono / Saldo corriente.
 *
 *   - Purchase (compra) → Cargo: aumenta lo que le debemos al proveedor.
 *   - Payment  (pago)  → Abono: reduce lo que le debemos.
 *   - El saldo corriente acumula línea a línea (positivo = debemos; negativo = pagamos de más).
 *
 * Enmascarado de montos:
 *   Si amountsVisible === false (a nivel de TODO el extracto, no por bloque) el backend
 *   devolvió los montos ocultos (sin permiso de costo). Mostramos "—" en gris en vez de
 *   valores para distinguir "sin permiso" de "saldo cero".
 *
 * Circuito de cancelación (Fase D, 2026-07-01):
 *   Debajo de la tabla Cargo/Abono/Saldo de cada moneda se agrega un bloque colapsable
 *   (arranca cerrado) con las multas retenidas por el operador y los reembolsos ya recibidos
 *   de esa moneda (bloque.circuitLines). Si una moneda no tuvo anulaciones, el bloque no
 *   se renderiza (ni siquiera vacío). Ver CircuitoCancelacionBloque más abajo.
 *
 * Editar / Eliminar un pago desde el extracto:
 *   Si canEditarEliminar=true, las filas de tipo Payment muestran botones Editar y Eliminar.
 *   Para Editar: el padre recibe el pago completo (desde allPayments) vía onEditarPago(payment).
 *   Para Eliminar: el padre recibe el pago vía onEliminarPago(payment) y se encarga de la confirmación.
 *   allPayments es la lista plana de pagos del proveedor — se cruza con sourcePublicId de cada línea.
 *
 * Se recarga automáticamente cuando refreshKey cambia (el padre lo incrementa al registrar un pago).
 *
 * Aplicaciones de saldo a favor (activeApplications):
 *   Se muestran en una sección separada debajo del extracto.
 *   Cada fila tiene un botón "Revertir" (inline, sin modal).
 *   El motivo de reversión es opcional.
 *   Los montos se enmascaran con "—" si no hay permiso cobranzas.see_cost.
 *
 * Props:
 *   - supplierPublicId: string — publicId del proveedor
 *   - refreshKey: number — incrementar desde el padre para forzar recarga
 *   - allPayments: Array<object> — lista de pagos completos (para cross-ref con sourcePublicId)
 *   - canEditarEliminar: boolean — si el usuario puede editar o eliminar pagos
 *   - onEditarPago: (payment: object) => void — callback al hacer click en Editar
 *   - onEliminarPago: (payment: object) => void — callback al hacer click en Eliminar
 *   - activeApplications: Array<object> — aplicaciones de saldo a favor vigentes (de SupplierCreditOverviewDto)
 *   - canRevertir: boolean — si el usuario puede revertir aplicaciones (tesoreria.supplier_payments)
 *   - onRevertirTerminado: () => void — callback al revertir una aplicación (para que el padre recargue)
 */

import { useCallback, useEffect, useState } from "react";
import { BookOpen, ChevronDown, ChevronRight, Loader2, Pencil, RefreshCw, Trash2, Undo2 } from "lucide-react";
import { api } from "../../../api";
import { hasPermission } from "../../../auth";
import { getApiErrorMessage } from "../../../lib/errors";
import { getPublicId } from "../../../lib/publicIds";
import { formatCurrency } from "../../../lib/utils";
import { CurrencyBadge } from "../../../components/ui/CurrencyBadge";
import {
    DataGrid,
    DataGridBody,
    DataGridCell,
    DataGridEmptyState,
    DataGridHeader,
    DataGridHeaderCell,
    DataGridHeaderRow,
    DataGridRow,
} from "../../../components/ui/DataGrid";

export function SupplierExtractoSection({
    supplierPublicId,
    refreshKey,
    allPayments = [],
    canEditarEliminar = false,
    onEditarPago,
    onEliminarPago,
    // Aplicaciones de saldo a favor vigentes (de SupplierCreditOverviewDto.ActiveApplications)
    activeApplications = [],
    // Si el usuario tiene tesoreria.supplier_payments para poder revertir
    canRevertir = false,
    // Callback al revertir: el padre recarga el overview + extracto
    onRevertirTerminado,
}) {
    const [extracto, setExtracto] = useState(null);
    const [cargando, setCargando] = useState(true);
    const [error, setError] = useState(null);

    // ─── Estado inline de reversión de aplicaciones ──────────────────────────
    // Solo una fila puede estar "en modo reversión" a la vez.
    const [revirtiendoAplicacionId, setRevirtiendoAplicacionId] = useState(null);
    const [motivoReversion, setMotivoReversion] = useState("");
    const [guardandoReversion, setGuardandoReversion] = useState(false);
    const [errorReversion, setErrorReversion] = useState(null);

    // El usuario ve montos solo si tiene este permiso de costo
    const puedeVerMontos = hasPermission("cobranzas.see_cost");

    const handleRevertirAplicacion = async (applicationPublicId) => {
        setGuardandoReversion(true);
        setErrorReversion(null);
        try {
            await api.post(
                `/suppliers/${supplierPublicId}/credit/applications/${applicationPublicId}/reverse`,
                { reason: motivoReversion.trim() || null }
            );
            // Limpiar el estado local y avisar al padre para que recargue el overview
            setRevirtiendoAplicacionId(null);
            setMotivoReversion("");
            onRevertirTerminado && onRevertirTerminado();
        } catch (err) {
            setErrorReversion(
                getApiErrorMessage(err, "No se pudo revertir la aplicación. Intentá de nuevo.")
            );
        } finally {
            setGuardandoReversion(false);
        }
    };

    const cargarExtracto = useCallback(async () => {
        setCargando(true);
        setError(null);
        try {
            const data = await api.get(`/suppliers/${supplierPublicId}/account/statement`);
            setExtracto(data);
        } catch (err) {
            setError(getApiErrorMessage(err) || "No se pudo cargar el extracto.");
        } finally {
            setCargando(false);
        }
        // refreshKey como dependencia: el padre lo sube al registrar un pago nuevo.
        // Así el extracto se actualiza solo sin que el usuario tenga que refrescar la página.
    }, [supplierPublicId, refreshKey]); // eslint-disable-line react-hooks/exhaustive-deps

    // Carga al montar y cada vez que cambia el proveedor o refreshKey
    useEffect(() => {
        cargarExtracto();
    }, [cargarExtracto]);

    const bloques = extracto?.currencies ?? [];
    const totalLineas = bloques.reduce((acc, b) => acc + (b.lines?.length ?? 0), 0);

    if (cargando) {
        return (
            <div className="overflow-hidden rounded-xl border bg-card shadow-sm" data-testid="extracto-loading">
                <div className="border-b p-4 flex items-center gap-2">
                    <BookOpen className="h-5 w-5" />
                    <h2 className="font-semibold">Extracto de cuenta</h2>
                </div>
                <div className="flex items-center justify-center gap-2 py-10 text-sm text-slate-400 dark:text-slate-500">
                    <Loader2 className="h-4 w-4 animate-spin" />
                    Cargando extracto…
                </div>
            </div>
        );
    }

    if (error) {
        return (
            <div className="overflow-hidden rounded-xl border bg-card shadow-sm" data-testid="extracto-error">
                <div className="border-b p-4 flex items-center gap-2">
                    <BookOpen className="h-5 w-5" />
                    <h2 className="font-semibold">Extracto de cuenta</h2>
                </div>
                <div className="flex flex-col items-center gap-3 py-10 text-center">
                    <p className="text-sm text-rose-600 dark:text-rose-400">{error}</p>
                    <button
                        type="button"
                        onClick={cargarExtracto}
                        className="inline-flex items-center gap-1.5 rounded-lg border border-slate-200 px-3 py-1.5 text-xs font-bold text-slate-600 transition-colors hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800"
                    >
                        <RefreshCw className="h-3.5 w-3.5" />
                        Reintentar
                    </button>
                </div>
            </div>
        );
    }

    return (
        <div className="space-y-4">
            {/* ── Extracto de movimientos ──────────────────────────────────── */}
            <div className="overflow-hidden rounded-xl border bg-card shadow-sm" data-testid="extracto-section">
                <div className="border-b p-4 flex items-center gap-2">
                    <BookOpen className="h-5 w-5" />
                    <h2 className="font-semibold">Extracto de cuenta</h2>
                </div>

                {totalLineas === 0 ? (
                    <div className="py-10 text-center" data-testid="extracto-vacio">
                        <BookOpen className="mx-auto mb-3 h-8 w-8 text-slate-300 dark:text-slate-600" />
                        <p className="text-sm text-slate-500 dark:text-slate-400">
                            Todavía no hay movimientos con este proveedor.
                        </p>
                    </div>
                ) : (
                    <div className="p-4 space-y-6">
                        {bloques.map((bloque) => (
                            <BloqueExtractoProveedor
                                key={bloque.currency}
                                bloque={bloque}
                                // amountsVisible viene del DTO RAIZ (extracto.amountsVisible), no del bloque
                                // por moneda (SupplierAccountStatementCurrencyBlockDto no tiene ese campo).
                                // Antes este componente leia bloque.amountsVisible, que siempre daba
                                // "undefined" y por lo tanto SIEMPRE mostraba los montos aunque el backend
                                // los hubiera enmascarado a 0 — se corrige leyendo la fuente correcta.
                                montosVisiblesGlobal={extracto?.amountsVisible !== false}
                                allPayments={allPayments}
                                canEditarEliminar={canEditarEliminar}
                                onEditarPago={onEditarPago}
                                onEliminarPago={onEliminarPago}
                            />
                        ))}
                    </div>
                )}
            </div>

            {/*
              ── Sección de aplicaciones de saldo a favor vigentes ─────────────

              Muestra las aplicaciones activas: saldos que se aplicaron
              de este proveedor a reservas específicas.
              Cada fila tiene un botón "Revertir" (inline, sin modal).
              El motivo de reversión es opcional.
            */}
            {activeApplications.length > 0 && (
                <div
                    className="overflow-hidden rounded-xl border border-emerald-200 bg-emerald-50/40 shadow-sm dark:border-emerald-900/40 dark:bg-emerald-950/10"
                    data-testid="aplicaciones-saldo-proveedor"
                >
                    <div className="border-b border-emerald-100 px-5 py-3 dark:border-emerald-900/30">
                        <h3 className="text-sm font-bold text-emerald-800 dark:text-emerald-300">
                            Saldo a favor aplicado a reservas
                        </h3>
                    </div>
                    <ul className="divide-y divide-emerald-100 dark:divide-emerald-900/20">
                        {activeApplications.map((aplicacion) => {
                            const aplicacionId = String(aplicacion.applicationPublicId);
                            const estaRevirtiendoEsta = revirtiendoAplicacionId === aplicacionId;
                            const simbolo = aplicacion.currency === "USD" ? "US$" : "$";
                            const monto = Number(aplicacion.amount).toLocaleString("es-AR", {
                                minimumFractionDigits: 2,
                                maximumFractionDigits: 2,
                            });
                            const fechaTexto = aplicacion.appliedAt
                                ? new Date(aplicacion.appliedAt).toLocaleDateString("es-AR")
                                : "—";

                            return (
                                <li key={aplicacionId} className="px-5 py-3">
                                    <div className="flex items-start justify-between gap-3 flex-wrap">
                                        <div className="min-w-0">
                                            <p className="text-sm font-semibold text-slate-800 dark:text-slate-200">
                                                Saldo a favor aplicado a{" "}
                                                <span className="text-emerald-700 dark:text-emerald-400 font-bold">
                                                    {aplicacion.targetReservaNumber ?? "la reserva"}
                                                </span>
                                                <span className="ml-2 font-bold text-emerald-700 dark:text-emerald-400">
                                                    {/* Enmascarar montos si no tiene permiso cobranzas.see_cost */}
                                                    {puedeVerMontos
                                                        ? `−${simbolo}${monto}`
                                                        : <span className="text-slate-400" title="Sin permiso para ver montos">—</span>
                                                    }
                                                </span>
                                            </p>
                                            {aplicacion.targetReservaHolderName && (
                                                <p className="text-xs text-slate-500 dark:text-slate-400 mt-0.5">
                                                    Titular: {aplicacion.targetReservaHolderName}
                                                </p>
                                            )}
                                            <p className="text-xs text-slate-400 dark:text-slate-500 mt-0.5">
                                                Aplicado el {fechaTexto}
                                            </p>
                                        </div>

                                        {/* Botón Revertir: solo si el usuario tiene el permiso correcto */}
                                        {canRevertir && !estaRevirtiendoEsta && (
                                            <button
                                                type="button"
                                                onClick={() => {
                                                    setRevirtiendoAplicacionId(aplicacionId);
                                                    setMotivoReversion("");
                                                    setErrorReversion(null);
                                                }}
                                                className="flex-shrink-0 flex items-center gap-1.5 rounded-lg border border-emerald-200 bg-white px-3 py-1.5 text-xs font-semibold text-emerald-700 hover:bg-emerald-50 dark:border-emerald-800 dark:bg-slate-900 dark:text-emerald-400 dark:hover:bg-emerald-950/30 transition-colors"
                                                data-testid={`revertir-aplicacion-proveedor-${aplicacionId}`}
                                            >
                                                <Undo2 className="h-3.5 w-3.5" />
                                                Revertir
                                            </button>
                                        )}
                                    </div>

                                    {/* Formulario inline de reversión */}
                                    {estaRevirtiendoEsta && (
                                        <div className="mt-3 space-y-2 rounded-lg bg-white dark:bg-slate-800 border border-emerald-200 dark:border-emerald-900/40 p-3">
                                            <label
                                                htmlFor={`motivo-reversion-proveedor-${aplicacionId}`}
                                                className="text-xs font-semibold text-slate-600 dark:text-slate-400"
                                            >
                                                Motivo de la reversión (opcional)
                                            </label>
                                            <textarea
                                                id={`motivo-reversion-proveedor-${aplicacionId}`}
                                                rows={2}
                                                value={motivoReversion}
                                                onChange={(e) => setMotivoReversion(e.target.value)}
                                                disabled={guardandoReversion}
                                                placeholder="Indicá el motivo si lo tenés..."
                                                className="w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:ring-2 focus:ring-emerald-400 dark:border-slate-700 dark:bg-slate-900 dark:text-white disabled:opacity-50 resize-none"
                                                data-testid={`motivo-reversion-proveedor-${aplicacionId}`}
                                            />
                                            {errorReversion && (
                                                <p className="text-xs text-rose-600 dark:text-rose-400" role="alert">
                                                    {errorReversion}
                                                </p>
                                            )}
                                            <div className="flex justify-end gap-2">
                                                <button
                                                    type="button"
                                                    onClick={() => {
                                                        setRevirtiendoAplicacionId(null);
                                                        setMotivoReversion("");
                                                        setErrorReversion(null);
                                                    }}
                                                    disabled={guardandoReversion}
                                                    className="rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-xs font-medium text-slate-700 hover:bg-slate-50 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-200 disabled:opacity-50 transition-colors"
                                                >
                                                    Cancelar
                                                </button>
                                                <button
                                                    type="button"
                                                    onClick={() => handleRevertirAplicacion(aplicacionId)}
                                                    disabled={guardandoReversion}
                                                    className="flex items-center gap-1.5 rounded-lg bg-emerald-600 px-3 py-1.5 text-xs font-bold text-white hover:bg-emerald-700 disabled:opacity-50 transition-colors"
                                                    data-testid={`confirmar-reversion-proveedor-${aplicacionId}`}
                                                >
                                                    {guardandoReversion ? (
                                                        <>
                                                            <Loader2 className="h-3 w-3 animate-spin" />
                                                            Revirtiendo…
                                                        </>
                                                    ) : (
                                                        <>
                                                            <Undo2 className="h-3 w-3" />
                                                            Confirmar reversión
                                                        </>
                                                    )}
                                                </button>
                                            </div>
                                        </div>
                                    )}
                                </li>
                            );
                        })}
                    </ul>
                </div>
            )}
        </div>
    );
}

// ─── Bloque de una moneda ────────────────────────────────────────────────────

/**
 * Tabla del extracto para una moneda.
 * Muestra cabecera con saldo de cierre + tabla de líneas.
 * La columna "Acciones" solo aparece cuando canEditarEliminar=true.
 */
function BloqueExtractoProveedor({ bloque, montosVisiblesGlobal, allPayments, canEditarEliminar, onEditarPago, onEliminarPago }) {
    // montosVisiblesGlobal viene del DTO raíz (extracto.amountsVisible): false = el usuario no tiene
    // permiso de costo. En ese caso, mostramos "—" en TODOS los montos para que quede claro que es
    // restricción de permisos, no que la cuenta está en cero.
    const montosVisibles = montosVisiblesGlobal !== false;

    return (
        <div className="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
            {/* Cabecera: moneda + saldo de cierre */}
            <div className="flex items-center justify-between gap-3 border-b border-slate-100 bg-slate-50/30 px-6 py-3 dark:border-slate-800 dark:bg-slate-800/10">
                <div className="flex items-center gap-2">
                    <CurrencyBadge currency={bloque.currency} />
                    <span className="text-sm font-bold text-slate-700 dark:text-slate-300">
                        {bloque.currency === "USD" ? "Dólares" : "Pesos"}
                    </span>
                </div>
                <div className="flex items-center gap-2">
                    <span className="text-[10px] font-bold uppercase tracking-wider text-slate-400">Saldo</span>
                    {montosVisibles ? (
                        <span
                            className={`text-sm font-extrabold ${
                                (bloque.closingBalance ?? 0) > 0
                                    ? "text-rose-600 dark:text-rose-500"
                                    : (bloque.closingBalance ?? 0) < 0
                                    ? "text-emerald-600 dark:text-emerald-500"
                                    : "text-slate-400 dark:text-slate-600"
                            }`}
                            data-testid={`extracto-saldo-${bloque.currency}`}
                        >
                            {formatCurrency(bloque.closingBalance ?? 0, bloque.currency)}
                        </span>
                    ) : (
                        <span className="text-sm font-extrabold text-muted-foreground" title="Sin permiso para ver montos">—</span>
                    )}
                </div>
            </div>

            {/* Tabla de movimientos.
                La columna Acciones solo aparece cuando hay permiso de editar/eliminar,
                para no ocupar espacio innecesario en modo solo lectura. */}
            <DataGrid density="compact" minWidth={canEditarEliminar ? "860px" : "780px"}>
                <DataGridHeader>
                    <DataGridHeaderRow>
                        <DataGridHeaderCell>Fecha</DataGridHeaderCell>
                        <DataGridHeaderCell>Concepto</DataGridHeaderCell>
                        <DataGridHeaderCell>Comprobante</DataGridHeaderCell>
                        <DataGridHeaderCell align="right">Cargo</DataGridHeaderCell>
                        <DataGridHeaderCell align="right">Abono</DataGridHeaderCell>
                        <DataGridHeaderCell align="right">Saldo</DataGridHeaderCell>
                        {canEditarEliminar && <DataGridHeaderCell align="center">Acciones</DataGridHeaderCell>}
                    </DataGridHeaderRow>
                </DataGridHeader>
                <DataGridBody>
                    {bloque.lines?.length > 0 ? (
                        bloque.lines.map((linea, idx) => (
                            <FilaExtractoProveedor
                                key={`${linea.kind}-${idx}`}
                                linea={linea}
                                currency={bloque.currency}
                                montosVisibles={montosVisibles}
                                allPayments={allPayments}
                                canEditarEliminar={canEditarEliminar}
                                onEditarPago={onEditarPago}
                                onEliminarPago={onEliminarPago}
                            />
                        ))
                    ) : (
                        <DataGridEmptyState colSpan={canEditarEliminar ? 7 : 6} title="Sin movimientos en esta moneda." />
                    )}
                </DataGridBody>
            </DataGrid>

            {/* Circuito de cancelación: bloque aparte, debajo del extracto de caja de esta moneda.
                Solo existe si hubo anulaciones en esta moneda (si no, CircuitoCancelacionBloque no
                renderiza nada — ni siquiera un cartel de "vacío", por decisión de Gastón). */}
            <CircuitoCancelacionBloque
                currency={bloque.currency}
                lineas={bloque.circuitLines}
                montosVisibles={montosVisibles}
            />
        </div>
    );
}

// ─── Bloque colapsable "Circuito de cancelación" ─────────────────────────────

/**
 * Tablita colapsable con los movimientos del "circuito de cancelación" de una moneda:
 * la multa que el operador retuvo y la plata que ya nos devolvió por viajes anulados.
 *
 * Arranca CERRADA (Gastón, 2026-07-01: no abarrotar el extracto con algo que no siempre
 * aplica). Si el operador no tuvo anulaciones en esta moneda, este componente no renderiza
 * nada — ni un cartel de "vacío" (decisión explícita: un operador sin anulaciones no necesita
 * ver un bloque vacío).
 *
 * El texto de "Movimiento" (description) viene tal cual del backend, ya en español
 * ("Multa retenida por el operador" / "Reembolso recibido del operador"); no lo derivamos
 * de ningún código interno.
 */
function CircuitoCancelacionBloque({ currency, lineas, montosVisibles }) {
    const [abierto, setAbierto] = useState(false);

    if (!lineas || lineas.length === 0) return null;

    const nombreMoneda = currency === "USD" ? "Dólares" : "Pesos";
    const simboloMoneda = currency === "USD" ? "US$" : "$";

    return (
        <div className="border-t border-slate-100 dark:border-slate-800">
            <button
                type="button"
                onClick={() => setAbierto((anterior) => !anterior)}
                className="flex w-full items-center justify-between gap-2 px-6 py-3 text-left hover:bg-slate-50/60 dark:hover:bg-slate-800/20 transition-colors"
                aria-expanded={abierto}
                data-testid={`circuito-cancelacion-toggle-${currency}`}
            >
                <span className="text-xs font-bold uppercase tracking-wider text-slate-500 dark:text-slate-400">
                    Circuito de cancelación ({nombreMoneda} {simboloMoneda})
                </span>
                <span className="inline-flex items-center gap-1 text-xs font-semibold text-indigo-600 dark:text-indigo-400">
                    {abierto ? (
                        <>
                            <ChevronDown className="h-3.5 w-3.5" />
                            Ocultar
                        </>
                    ) : (
                        <>
                            <ChevronRight className="h-3.5 w-3.5" />
                            Mostrar
                        </>
                    )}
                </span>
            </button>

            {abierto && (
                <DataGrid density="compact" minWidth="640px">
                    <DataGridHeader>
                        <DataGridHeaderRow>
                            <DataGridHeaderCell>Fecha</DataGridHeaderCell>
                            <DataGridHeaderCell>Movimiento</DataGridHeaderCell>
                            <DataGridHeaderCell>Reserva</DataGridHeaderCell>
                            <DataGridHeaderCell align="right">Monto</DataGridHeaderCell>
                        </DataGridHeaderRow>
                    </DataGridHeader>
                    <DataGridBody>
                        {lineas.map((linea, idx) => (
                            <DataGridRow key={`circuito-${currency}-${idx}`} data-testid={`circuito-cancelacion-linea-${currency}-${idx}`}>
                                <DataGridCell className="text-slate-500 dark:text-slate-400">
                                    {linea.date ? new Date(linea.date).toLocaleDateString("es-AR") : "—"}
                                </DataGridCell>
                                <DataGridCell>{linea.description || "—"}</DataGridCell>
                                <DataGridCell className="font-mono text-xs text-slate-500 dark:text-slate-400">
                                    {linea.documentRef || "—"}
                                </DataGridCell>
                                {/* Monto: el backend siempre lo trae en "charge" (cada línea del circuito
                                    es un cargo positivo; ver nota de mapeo en SupplierService). */}
                                <DataGridCell align="right">
                                    {montosVisibles ? (
                                        <span className="font-bold text-slate-800 dark:text-slate-200">
                                            {formatCurrency(linea.charge ?? 0, currency)}
                                        </span>
                                    ) : (
                                        <span className="text-muted-foreground" title="Sin permiso para ver montos">—</span>
                                    )}
                                </DataGridCell>
                            </DataGridRow>
                        ))}
                    </DataGridBody>
                </DataGrid>
            )}
        </div>
    );
}

// ─── Fila individual del extracto ────────────────────────────────────────────

function FilaExtractoProveedor({ linea, currency, montosVisibles, allPayments, canEditarEliminar, onEditarPago, onEliminarPago }) {
    // Purchase → columna Cargo (cargamos deuda); Payment → columna Abono (abonamos deuda)
    const esCargo = linea.charge > 0;
    const esAbono = linea.credit > 0;
    const esPago = linea.kind === "Payment";

    // Cruzamos sourcePublicId de la línea con la lista de pagos completos del padre.
    // Así tenemos el objeto completo (con method, reference, exchangeRate, etc.)
    // que PagarProveedorInline necesita para pre-cargar el formulario de edición.
    const pagoCompleto = esPago && linea.sourcePublicId
        ? (allPayments || []).find((p) => String(getPublicId(p)) === String(linea.sourcePublicId))
        : null;

    // Solo mostramos botones si canEditarEliminar y tenemos el pago completo en la lista.
    // Si el pago no está en allPayments (porque es muy antiguo y no se cargó), no ofrecemos Editar
    // para no abrir un formulario vacío. Eliminar sigue siendo posible con sourcePublicId solo.
    const puedeEditar = canEditarEliminar && esPago && pagoCompleto != null;
    const puedeEliminar = canEditarEliminar && esPago && linea.sourcePublicId != null;

    return (
        <DataGridRow>
            <DataGridCell className="text-slate-500 dark:text-slate-400">
                {linea.date ? new Date(linea.date).toLocaleDateString("es-AR") : "—"}
            </DataGridCell>

            <DataGridCell>
                <span className={esCargo ? "font-medium text-slate-800 dark:text-slate-200" : "text-slate-600 dark:text-slate-400"}>
                    {linea.description || "—"}
                </span>
            </DataGridCell>

            <DataGridCell className="font-mono text-xs text-slate-500 dark:text-slate-400">
                {linea.documentRef || "—"}
            </DataGridCell>

            {/* Cargo: solo se muestra para compras; si no hay permiso → "—" gris */}
            <DataGridCell align="right">
                {esCargo ? (
                    montosVisibles ? (
                        <span className="font-bold text-slate-800 dark:text-slate-200">
                            {formatCurrency(linea.charge, currency)}
                        </span>
                    ) : (
                        <span className="text-muted-foreground" title="Sin permiso para ver montos">—</span>
                    )
                ) : (
                    <span className="text-slate-300 dark:text-slate-700">—</span>
                )}
            </DataGridCell>

            {/* Abono: solo se muestra para pagos; si no hay permiso → "—" gris */}
            <DataGridCell align="right">
                {esAbono ? (
                    montosVisibles ? (
                        <span className="font-bold text-emerald-600 dark:text-emerald-500">
                            {formatCurrency(linea.credit, currency)}
                        </span>
                    ) : (
                        <span className="text-muted-foreground" title="Sin permiso para ver montos">—</span>
                    )
                ) : (
                    <span className="text-slate-300 dark:text-slate-700">—</span>
                )}
            </DataGridCell>

            {/* Saldo corriente: positivo = debemos (rojo), negativo = a favor (verde), cero = gris */}
            <DataGridCell align="right">
                {montosVisibles ? (
                    <span
                        className={`font-extrabold ${
                            (linea.runningBalance ?? 0) > 0
                                ? "text-rose-600 dark:text-rose-500"
                                : (linea.runningBalance ?? 0) < 0
                                ? "text-emerald-600 dark:text-emerald-500"
                                : "text-slate-400 dark:text-slate-600"
                        }`}
                    >
                        {formatCurrency(linea.runningBalance ?? 0, currency)}
                    </span>
                ) : (
                    <span className="text-muted-foreground" title="Sin permiso para ver montos">—</span>
                )}
            </DataGridCell>

            {/* Columna de acciones: solo presente cuando hay permiso.
                Editar requiere pagoCompleto (objeto completo del pago).
                Eliminar solo necesita sourcePublicId, que siempre está en la línea. */}
            {canEditarEliminar && (
                <DataGridCell align="center">
                    {(puedeEditar || puedeEliminar) && (
                        <div className="flex items-center justify-center gap-1">
                            {puedeEditar && (
                                <button
                                    type="button"
                                    onClick={() => onEditarPago && onEditarPago(pagoCompleto)}
                                    className="rounded p-1 text-slate-500 hover:bg-slate-100 hover:text-slate-700 dark:hover:bg-slate-800 dark:hover:text-slate-300 transition-colors"
                                    title="Editar pago"
                                    aria-label="Editar pago"
                                    data-testid={`editar-pago-${linea.sourcePublicId}`}
                                >
                                    <Pencil className="h-3.5 w-3.5" />
                                </button>
                            )}
                            {puedeEliminar && (
                                <button
                                    type="button"
                                    onClick={() => {
                                        // Pasamos el pago completo si lo tenemos; si no, un objeto mínimo
                                        // con el publicId para que el padre pueda hacer el DELETE.
                                        const objetoPago = pagoCompleto ?? { publicId: linea.sourcePublicId, amount: linea.credit, currency };
                                        onEliminarPago && onEliminarPago(objetoPago);
                                    }}
                                    className="rounded p-1 text-slate-500 hover:bg-rose-50 hover:text-rose-600 dark:hover:bg-rose-950/20 dark:hover:text-rose-400 transition-colors"
                                    title="Eliminar pago"
                                    aria-label="Eliminar pago"
                                    data-testid={`eliminar-pago-${linea.sourcePublicId}`}
                                >
                                    <Trash2 className="h-3.5 w-3.5" />
                                </button>
                            )}
                        </div>
                    )}
                </DataGridCell>
            )}
        </DataGridRow>
    );
}
