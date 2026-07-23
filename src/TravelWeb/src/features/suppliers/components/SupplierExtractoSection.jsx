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
 * Circuito de cancelación — SALDO ÚNICO (Fase D, 2026-07-01):
 *   La multa retenida por el operador y el reembolso recibido YA vienen como renglones del
 *   extracto (el backend los fusiona en bloque.lines con su saldo corriente), así que el
 *   "Saldo" del pie es el saldo económico y cuadra con los recuadros del encabezado. Cada
 *   renglón de anulación lleva un chip "Anulación" (siempre visible) para no confundirlo con
 *   una compra. Cuando en una moneda coexisten "Le debo" (viajes vivos) y "Me tiene que
 *   devolver" (anulaciones), el saldo único es el neto y se aclara con ReconciliacionSaldoOperador.
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
import { BookOpen, Loader2, Pencil, RefreshCw, Trash2, Undo2 } from "lucide-react";
import { api } from "../../../api";
import { hasPermission } from "../../../auth";
import { getApiErrorMessage } from "../../../lib/errors";
import { getPublicId } from "../../../lib/publicIds";
import { formatCurrency, formatDate } from "../../../lib/utils";
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
                            // aplicacion.appliedAt es un INSTANTE REAL de sistema (backend lo arma con
                            // AppliedAt = CreatedAt de la aplicación de saldo a favor, no una fecha que el
                            // usuario eligió), así que formatDate() cae en su rama de "instante real" — la
                            // arreglamos igual para que esa rama use la hora de Argentina fija (no la del
                            // navegador), no porque tuviera el bug de corrimiento de día.
                            const fechaTexto = aplicacion.appliedAt ? formatDate(aplicacion.appliedAt) : "—";

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
 * Muestra las tres partidas independientes de la cuenta + tabla de líneas.
 * La columna "Acciones" solo aparece cuando canEditarEliminar=true.
 */
function BloqueExtractoProveedor({ bloque, montosVisiblesGlobal, allPayments, canEditarEliminar, onEditarPago, onEliminarPago }) {
    // montosVisiblesGlobal viene del DTO raíz (extracto.amountsVisible): false = el usuario no tiene
    // permiso de costo. En ese caso, mostramos "—" en TODOS los montos para que quede claro que es
    // restricción de permisos, no que la cuenta está en cero.
    const montosVisibles = montosVisiblesGlobal !== false;

    return (
        <div className="overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
            {/* Cabecera: cada partida se informa por separado. No existe compensación implícita. */}
            <div className="flex flex-col gap-3 border-b border-slate-100 bg-slate-50/30 px-6 py-3 dark:border-slate-800 dark:bg-slate-800/10 lg:flex-row lg:items-center lg:justify-between">
                <div className="flex items-center gap-2">
                    <CurrencyBadge currency={bloque.currency} />
                    <span className="text-sm font-bold text-slate-700 dark:text-slate-300">
                        {bloque.currency === "USD" ? "Dólares" : "Pesos"}
                    </span>
                </div>
                <div className="grid grid-cols-3 gap-4 text-right">
                    {[
                        ["Le debemos", bloque.iTheyOwe ?? 0, "text-rose-600 dark:text-rose-500", "le-debemos"],
                        ["Nos debe", bloque.theyOweMe ?? 0, "text-amber-600 dark:text-amber-400", "nos-debe"],
                        ["Saldo aplicable", bloque.prepayment ?? 0, "text-emerald-600 dark:text-emerald-500", "saldo-aplicable"],
                    ].map(([label, amount, color, testId]) => (
                        <div key={testId}>
                            <div className="text-[10px] font-bold uppercase tracking-wider text-slate-400">{label}</div>
                            {montosVisibles ? (
                                <div className={`text-sm font-extrabold ${color}`} data-testid={`extracto-${testId}-${bloque.currency}`}>
                                    {formatCurrency(amount, bloque.currency)}
                                </div>
                            ) : (
                                <div className="text-sm font-extrabold text-muted-foreground" title="Sin permiso para ver montos">—</div>
                            )}
                        </div>
                    ))}
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
                        <DataGridEmptyState colSpan={canEditarEliminar ? 6 : 5} title="Sin movimientos en esta moneda." />
                    )}
                </DataGridBody>
            </DataGrid>

            {/* Línea de reconciliación (Fase D — saldo único, 2026-07-01): la multa retenida y el reembolso
                recibido YA vienen como renglones del extracto (el backend los fusiona en `bloque.lines` con su
                saldo corriente), así que el "Saldo" de arriba ya cuadra 1:1 con "Me tiene que devolver" en el
                caso normal. Solo cuando en esta moneda COEXISTEN dos cosas distintas (le debo por un viaje vivo
                Y me tiene que devolver por una anulación, o saldo a favor + reembolso pendiente) el saldo único
                es el NETO y no representa cada parte: ahí mostramos una línea que lo explica. */}
            <ReconciliacionSaldoOperador bloque={bloque} montosVisibles={montosVisibles} />
        </div>
    );
}

// ─── Línea de reconciliación del saldo (coexistencia "Le debo" + "Me tiene que devolver") ─────

/**
 * Nota explicativa que aparece SOLO cuando el saldo único de la moneda es una POSICIÓN NETA que
 * no representa fielmente los tres números del encabezado. Pasa cuando coexisten, con el mismo
 * operador y moneda, dos conceptos que a propósito NO se netean entre sí: p.ej. le debo por un
 * viaje vivo (iTheyOwe) y me tiene que devolver por una anulación (theyOweMe). En ese caso el
 * saldo del extracto (neto) no es "lo mismo que arriba", y esta línea aclara los componentes.
 *
 * En el caso normal (a lo sumo uno de los tres es > 0) NO se muestra nada: el saldo del extracto
 * ya cuadra 1:1 con el recuadro correspondiente del encabezado.
 *
 * Sin permiso de ver costos no se muestra (no revelamos montos).
 */
function ReconciliacionSaldoOperador({ bloque, montosVisibles }) {
    if (!montosVisibles) return null;

    const leDebo = bloque.iTheyOwe ?? 0;
    const meDebe = bloque.theyOweMe ?? 0;
    const aFavor = bloque.prepayment ?? 0;

    // Coexistencia = dos o más componentes distintos de cero. Con uno solo, el saldo único ya cuadra.
    const componentes = [leDebo > 0, meDebe > 0, aFavor > 0].filter(Boolean).length;
    if (componentes < 2) return null;

    const partes = [];
    if (leDebo > 0) partes.push(`le debés ${formatCurrency(leDebo, bloque.currency)} (viajes vivos)`);
    if (meDebe > 0) partes.push(`te tiene que devolver ${formatCurrency(meDebe, bloque.currency)} (anulaciones)`);
    if (aFavor > 0) partes.push(`tenés ${formatCurrency(aFavor, bloque.currency)} de saldo a favor`);

    return (
        <div
            className="border-t border-amber-100 dark:border-amber-900/30 bg-amber-50/40 dark:bg-amber-950/10 px-6 py-3"
            data-testid={`reconciliacion-saldo-${bloque.currency}`}
        >
            <p className="text-xs text-amber-800 dark:text-amber-300">
                <span className="font-bold">Estas partidas no se compensan automáticamente.</span>{" "}
                Con este operador {partes.join(", y ")}. Para descontar saldo a favor hay que usar la acción
                &quot;Usar saldo a favor&quot;; los reembolsos se registran por separado.
            </p>
        </div>
    );
}

// ─── Fila individual del extracto ────────────────────────────────────────────

/**
 * True si el `kind` de una línea del extracto pertenece al circuito de cancelación
 * (movimiento de una anulación, no una compra normal) — decide si la fila lleva el
 * chip "Anulación". Exportada como función PURA (sin JSX) para poder testearla sin
 * montar el componente.
 *
 * ADR-044 T4 (2026-07-10): se suman los dos kinds nuevos del extracto del operador
 * (ADR-044 T3b) a la lista original de dos — sin esto quedaban pintados como una
 * compra normal, confundiendo al usuario:
 *   - "OperatorChargeInvoiced" ("Cargo del operador facturado aparte"): deuda nueva
 *     hacia el operador cuando la multa se factura aparte en vez de retenerse.
 *   - "TreasuryFxAdjustment" ("Ajuste por el dólar" en la UI): el backend ya manda
 *     ese rótulo tal cual en `linea.description` (verificado en
 *     SupplierCancellationCircuitReader.cs — la frase prohibida "Diferencia de
 *     cambio" que existía al momento de construir este chip ya fue corregida del
 *     lado del servidor). Este componente NO arma el texto, solo agrega el chip
 *     encima del que venga del servidor.
 *
 * @param {string} kind
 * @returns {boolean}
 */
export function esLineaDeCircuitoCancelacion(kind) {
  return (
    kind === "PenaltyRetained" ||
    kind === "RefundReceived" ||
    kind === "OperatorChargeInvoiced" ||
    kind === "TreasuryFxAdjustment"
  );
}

/**
 * Rediseño "Registrar pago" (2026-07-20, punto 5.2.4 del análisis
 * `docs/architecture/2026-07-20-analisis-cuenta-proveedor-vs-erps.md`): antes, la línea
 * de un pago en el extracto solo mostraba el MÉTODO ("Transferencia"/"Efectivo"...) y
 * nunca decía a qué reserva se había imputado — para saber "¿esto bajó la deuda de la
 * 1051?" había que ir a mano a otra solapa a comparar de memoria. El backend ahora manda
 * `reservaNumero`/`servicioDescripcion` en cada línea de pago (viajan SIEMPRE, incluso
 * sin el permiso de tesorería, porque son identidad de la reserva, no un dato de caja).
 *
 * Arma el sufijo " · Reserva F-2026-1051 (Hotel Bariloche)" para agregar después de la
 * descripción de una línea de PAGO. Exportada como función PURA (mismo patrón que
 * `esLineaDeCircuitoCancelacion`) para poder testearla sin montar el componente.
 *
 * @param {{kind?:string, reservaNumero?:string|null, servicioDescripcion?:string|null}} linea
 * @returns {string|null} el sufijo, o null si el pago fue "a cuenta" (sin reserva imputada)
 */
export function construirSufijoDestinoPago(linea) {
    if (linea?.kind !== "Payment") return null;
    if (!linea?.reservaNumero) return null; // pago a cuenta: no hay reserva que nombrar
    return linea.servicioDescripcion
        ? ` · Reserva ${linea.reservaNumero} (${linea.servicioDescripcion})`
        : ` · Reserva ${linea.reservaNumero}`;
}

function FilaExtractoProveedor({ linea, currency, montosVisibles, allPayments, canEditarEliminar, onEditarPago, onEliminarPago }) {
    // Purchase → columna Cargo (cargamos deuda); Payment → columna Abono (abonamos deuda)
    const esCargo = linea.charge > 0;
    const esAbono = linea.credit > 0;
    const esPago = linea.kind === "Payment";
    const esCircuito = esLineaDeCircuitoCancelacion(linea.kind);
    const sufijoDestino = construirSufijoDestinoPago(linea);

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
                {/* fix 2026-07-22: mismo bug que EstadoCuentaExtracto.jsx (cobros/pagos fechados
                    corrían un día menos al mostrarse). formatDate() no convierte a hora local
                    una fecha de negocio (día elegido por el usuario, guardada como medianoche
                    UTC) — ver lib/utils.js. */}
                {linea.date ? formatDate(linea.date) : "—"}
            </DataGridCell>

            <DataGridCell>
                <span
                    className={esCargo ? "font-medium text-slate-800 dark:text-slate-200" : "text-slate-600 dark:text-slate-400"}
                    title={esCircuito ? "Movimiento de una anulación: reduce lo que el operador te tiene que devolver (no es una compra nueva)." : undefined}
                >
                    {linea.description || "—"}
                    {/* Rediseño 2026-07-20: a qué reserva/servicio bajó la plata de este pago.
                        Viaja siempre (identidad, no dato de tesorería) — ver construirSufijoDestinoPago. */}
                    {sufijoDestino && (
                        <span className="text-slate-400 dark:text-slate-500 font-normal">{sufijoDestino}</span>
                    )}
                </span>
                {esCircuito && (
                    <span
                        className="ml-2 inline-flex items-center rounded-full bg-amber-100 px-2 py-0.5 text-[10px] font-bold uppercase tracking-wide text-amber-700 dark:bg-amber-950/30 dark:text-amber-300"
                        title="Movimiento de una anulación: reduce lo que el operador te tiene que devolver (no es una compra nueva)."
                        data-testid="extracto-anulacion-chip"
                    >
                        Anulación
                    </span>
                )}
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
