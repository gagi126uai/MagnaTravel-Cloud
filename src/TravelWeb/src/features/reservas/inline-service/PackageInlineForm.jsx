/**
 * Formulario de Paquete dentro de la ficha de carga en línea (ServiceInlineCard).
 *
 * Campos a la vista SIEMPRE (sin revelado progresivo — guía UX ronda 1):
 *   Buscador del paquete · Operador · Salida · Fecha de fin · Pasajeros y base (doble/triple)
 *   Precio por persona · Costo · Venta · Moneda
 *
 * Más detalles (plegado):
 *   Qué incluye (texto libre) · Número de file del operador
 *
 * Permiso `cobranzas.see_cost`:
 *   - Con permiso: ve el campo Costo + ganancia en el footer.
 *   - Sin permiso: no ve costo ni ganancia (jamás "$0").
 *
 * Cálculo del total: precio por persona × pasajeros.
 * La venta total y el costo total se calculan separados (el vendedor puede ingresar
 * un precio por persona de venta y uno de costo independientes).
 *
 * Fecha de fin: campo opcional. Si se carga, no puede ser anterior a la salida
 * (la validación vive en ServiceInlineCard.validarForm).
 */

import { useState } from "react";
import { Package, ChevronDown, ChevronUp, Calendar, Users } from "lucide-react";
import { hasPermission } from "../../../auth";
import { ProductSearchField } from "./ProductSearchField";
import { redondearDinero, formatearPrecio } from "./HotelInlineForm";
import { MoneyInput } from "../../../components/ui/MoneyInput";
import {
    resolverCamposALimpiarAlCrearNuevo,
    aplicarInterpretacionComoSugerencia,
    resolverNombreEnCasillero,
    resolverPatchDeVentaDelCatalogo,
    resolverOperadorSugeridoParaProductoNuevo,
    sanitizarCantidadPositiva,
} from "./inlineServiceFormHelpers";
import { buildLastSaleHintText } from "./lastSaleHintLogic";
import { LastSaleHint } from "./LastSaleHint";
import { useSeleccionPendienteDelTipo } from "./useSeleccionPendienteDelTipo";

// D13 (spec 2026-08-10): campos de fecha de ESTE form, [desde, hasta] — Paquete tiene
// salida/fin.
const CAMPOS_FECHA_PAQUETE = ["startDate", "endDate"];

// ─── Clases CSS ───────────────────────────────────────────────────────────────
const INPUT_BASE = "w-full py-2 px-3 text-sm border rounded-lg bg-white focus:outline-none focus:ring-1 focus:border-blue-500 focus:ring-blue-500 disabled:bg-slate-50 disabled:text-slate-400";
const INPUT_NORMAL = `${INPUT_BASE} border-slate-200`;
const INPUT_SUGERIDO = `${INPUT_BASE} border-yellow-400 bg-yellow-50`;
const INPUT_CALCULADO = `${INPUT_BASE} border-slate-200 border-dashed bg-slate-50 text-slate-600 font-semibold cursor-default`;
const LABEL_BASE = "block text-xs font-semibold text-slate-600 mb-1";

// ─── Recuadro violeta para paquete nuevo ─────────────────────────────────────

/**
 * Recuadro que aparece al crear un paquete nuevo.
 * Campo mínimo: nombre del paquete + operador.
 *
 * `supplierSugerido`/`onSupplierTouched` (D13-bis, spec 2026-08-10): ver NewHotelBox.
 */
function NewPackageBox({ newProduct, onChange, suppliers, supplierSugerido, onSupplierTouched }) {
    return (
        <div className="border border-dashed border-violet-400 bg-violet-50 rounded-xl p-4 mb-4">
            <div className="flex items-center gap-2 mb-3">
                <Package className="w-4 h-4 text-violet-600" />
                <span className="text-sm font-semibold text-violet-700">
                    Paquete nuevo — se guarda en tu tarifario al confirmar
                </span>
                <span className="text-[11px] font-semibold px-2 py-0.5 rounded-full bg-violet-200 text-violet-700">
                    Creado en venta
                </span>
            </div>
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                <div>
                    <label className={LABEL_BASE}>Nombre del paquete *</label>
                    <input
                        type="text"
                        className={INPUT_NORMAL}
                        value={newProduct.name || ""}
                        onChange={(event) => onChange({ ...newProduct, name: event.target.value })}
                        placeholder="Ej: Iguazú 7 noches todo incluido"
                        required
                        data-testid="new-package-name"
                        aria-label="Nombre del paquete nuevo"
                    />
                </div>
                <div>
                    <label className={LABEL_BASE}>Operador *</label>
                    <select
                        className={supplierSugerido ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={newProduct.supplierPublicId || ""}
                        onChange={(event) => {
                            onChange({ ...newProduct, supplierPublicId: event.target.value });
                            onSupplierTouched?.();
                        }}
                        required
                        data-testid="new-package-supplier"
                        aria-label="Operador del paquete nuevo"
                    >
                        <option value="">Seleccioná...</option>
                        {suppliers.map((supplier) => (
                            <option
                                key={supplier.publicId || supplier.PublicId}
                                value={supplier.publicId || supplier.PublicId}
                            >
                                {supplier.name}
                            </option>
                        ))}
                    </select>
                </div>
            </div>
        </div>
    );
}

// ─── Componente principal PackageInlineForm ───────────────────────────────────

export function PackageInlineForm({
    reservaId,
    form,
    setForm,
    suppliers,
    isEditing,
    // Salto de solapa (spec 2026-08-10, D1..D13) — ver ServiceInlineCard.jsx.
    onSelectOtherType,
    seleccionPendiente,
    onConsumirSeleccionPendiente,
    // Fix #4 (auditoría 2026-08-10): levantado a ServiceInlineCard — ver HotelInlineForm
    // para la explicación completa de por qué (sobrevive al remount de cambiar de solapa).
    camposSugeridos,
    setCamposSugeridos,
    // Fix regresión #1+#6 (re-review 2026-08-10): APARTE de camposSugeridos — ver el
    // comentario largo en ServiceInlineCard.jsx donde se declara.
    camposTocadosAMano,
    setCamposTocadosAMano,
}) {
    const canSeeCost = hasPermission("cobranzas.see_cost");

    // Cantidad de pasajeros (mínimo 1 para el cálculo)
    const pasajeros = Math.max(Number(form.passengers) || 1, 1);

    // Total = precio por persona × pasajeros
    const ventaTotal = redondearDinero((Number(form.unitSalePrice) || 0) * pasajeros);
    const costoTotal = canSeeCost
        ? redondearDinero((Number(form.unitNetCost) || 0) * pasajeros)
        : null;
    const ganancia = canSeeCost && costoTotal !== null ? redondearDinero(ventaTotal - costoTotal) : null;

    // "Más detalles" se abre automáticamente al editar si ya hay datos
    const tieneDetallesExistentes = Boolean(form.itinerary || form.fileNumber);
    const [mostrarDetalles, setMostrarDetalles] = useState(tieneDetallesExistentes || isEditing);

    // Renglón gris "Último precio" (spec 2026-08-06, §3.2, P9=A) — ver HotelInlineForm.
    const [ultimoPrecioSugerido, setUltimoPrecioSugerido] = useState(null);
    const textoUltimoPrecio = buildLastSaleHintText(ultimoPrecioSugerido, { canSeeCost });

    // C5: operador sugerido que no está en la lista de la reserva
    const supplierListaIds = new Set(suppliers.map((s) => s.publicId || s.PublicId));
    const supplierSugeridoFuera =
        camposSugeridos.supplierId &&
        form.supplierId &&
        !supplierListaIds.has(form.supplierId);
    const suppliersFull = supplierSugeridoFuera
        ? [
              {
                  publicId: form.supplierId,
                  // Sin supplierName no mostramos el ID interno recortado (eso es un dato
                  // técnico que un usuario no programador no puede leer) — mejor un texto
                  // genérico que igual identifica que hay un operador sugerido (2026-08-03).
                  name: form.supplierName || "Operador sugerido",
              },
              ...suppliers,
          ]
        : suppliers;

    const handleSelectExisting = (catalogResult, interpretacion) => {
        const sale = catalogResult.lastSale || catalogResult.rateFallback || {};

        // Regla transversal (auditoría 2026-08-10, #1): un campo se reemplaza si está
        // vacío O si NO fue tocado a mano por el vendedor (`camposTocadosAMano`) —
        // nunca si lo tipeó/eligió a mano. Fix regresión #1+#6: la señal es
        // `camposTocadosAMano`, NO `camposSugeridos` (ese es solo el amarillo).
        const { patch: patchVenta, sugeridos: sugeridosVenta } = resolverPatchDeVentaDelCatalogo({
            sale,
            canSeeCost,
            formActual: form,
            camposTocadosAMano,
            campoVenta: "unitSalePrice",
            campoCosto: "unitNetCost",
        });

        // Fix C-5(a) (review 2026-08-10): tampoco pisa lo tipeado a mano la precarga de
        // la frase (D13).
        const { patch: patchFrase, sugeridos: sugeridosFrase } = aplicarInterpretacionComoSugerencia(
            interpretacion,
            { yaHaySupplierDeLaVenta: Boolean(sale.supplierPublicId), camposFecha: CAMPOS_FECHA_PAQUETE, formActual: form, camposTocadosAMano }
        );

        setForm((prev) => ({
            ...prev,
            // D13: nombre limpio del producto elegido, nunca la frase entera.
            packageName: resolverNombreEnCasillero(catalogResult, prev.packageName),
            rateId: catalogResult.ratePublicId,
            newCatalogProduct: null,
            ...patchVenta,
            ...patchFrase,
        }));

        setCamposSugeridos({
            supplierId: false,
            unitNetCost: false,
            unitSalePrice: false,
            currency: false,
            startDate: false,
            endDate: false,
            ...sugeridosVenta,
            ...sugeridosFrase,
        });
        setUltimoPrecioSugerido(catalogResult);
    };

    // Salto de solapa (D3/D7, spec 2026-08-10): pendiente elegida desde OTRA solapa.
    useSeleccionPendienteDelTipo({
        seleccionPendiente,
        serviceType: "Paquete",
        onSeleccionar: handleSelectExisting,
        onConsumida: onConsumirSeleccionPendiente,
    });

    const handleCreateNew = (searchText, interpretacion) => {
        // Bug #28 (Tanda 4, 2026-07-24): antes esto borraba operador/costo/venta/moneda
        // SIEMPRE, aunque el usuario los hubiera tipeado a mano. Ahora solo se limpian los
        // campos que el vendedor NUNCA tocó a mano (fix regresión #1+#6: la señal es
        // `camposTocadosAMano`, no `camposSugeridos`).
        const camposLimpios = resolverCamposALimpiarAlCrearNuevo(
            { supplierId: form.supplierId, unitNetCost: form.unitNetCost, unitSalePrice: form.unitSalePrice, currency: form.currency },
            camposTocadosAMano,
            { supplierId: "", unitNetCost: "", unitSalePrice: "", currency: "ARS" }
        );

        // D13-bis (spec 2026-08-10, fix "crear nuevo pelado"): fechas de la frase, misma
        // función/guardas que handleSelectExisting. El operador va aparte (recuadro
        // violeta, ver abajo) — acá no hay `sale`.
        const { patch: patchFechas, sugeridos: sugeridosFechas } = aplicarInterpretacionComoSugerencia(
            interpretacion,
            { yaHaySupplierDeLaVenta: true, camposFecha: CAMPOS_FECHA_PAQUETE, formActual: form, camposTocadosAMano }
        );

        // El operador del recuadro "paquete nuevo" arranca SIEMPRE vacío — si la frase
        // trajo un operador REAL (matcheado por el motor), se precarga ahí, editable.
        const supplierSugeridoDelNuevo = resolverOperadorSugeridoParaProductoNuevo(interpretacion);

        setForm((prev) => ({
            ...prev,
            packageName: searchText,
            rateId: null,
            newCatalogProduct: { name: searchText, supplierPublicId: supplierSugeridoDelNuevo },
            ...camposLimpios,
            ...patchFechas,
        }));
        // `supplierId` se reusa para el amarillo del Operador del recuadro violeta
        // (D13-bis) — los dos campos nunca conviven en pantalla.
        setCamposSugeridos({
            supplierId: Boolean(supplierSugeridoDelNuevo),
            unitNetCost: false,
            unitSalePrice: false,
            currency: false,
            startDate: false,
            endDate: false,
            ...sugeridosFechas,
        });
        setUltimoPrecioSugerido(null);
    };

    const handleSearchChange = (texto) => {
        setForm((prev) => ({
            ...prev,
            packageName: texto,
            rateId: null,
            newCatalogProduct: texto ? prev.newCatalogProduct : null,
        }));
        // Fix #6 (auditoría 2026-08-10): CUALQUIER tecleo desvincula el rateId — los
        // amarillos que quedaban pintados ya no corresponden a ninguna selección viva
        // (el VALOR se queda, deja de ser "sugerencia"). Antes esto solo pasaba si el
        // vendedor borraba TODO el texto.
        setCamposSugeridos({ supplierId: false, unitNetCost: false, unitSalePrice: false, currency: false, startDate: false, endDate: false });
        setUltimoPrecioSugerido(null);
    };

    return (
        <div className="space-y-4">

            {/* === BUSCADOR (nombre del paquete) === */}
            <ProductSearchField
                reservaId={reservaId}
                serviceType="Paquete"
                value={form.packageName || ""}
                onChange={handleSearchChange}
                onSelectExisting={handleSelectExisting}
                onSelectOtherType={onSelectOtherType}
                onCreateNew={handleCreateNew}
                disabled={isEditing}
                esEdicion={isEditing}
                rateId={form.rateId}
                supplierIdElegido={form.supplierId}
                label="Paquete"
                placeholder="Ej: Iguazú 7 noches, Cancún todo incluido..."
            />

            {/* === RECUADRO PRODUCTO NUEVO === */}
            {form.newCatalogProduct && (
                <NewPackageBox
                    newProduct={form.newCatalogProduct}
                    onChange={(newProduct) => setForm((prev) => ({ ...prev, newCatalogProduct: newProduct }))}
                    suppliers={suppliers}
                    supplierSugerido={camposSugeridos.supplierId}
                    onSupplierTouched={() => setCamposSugeridos((prev) => ({ ...prev, supplierId: false }))}
                />
            )}

            {/* === OPERADOR === */}
            {!form.newCatalogProduct && (
                <div>
                    <label className={LABEL_BASE} htmlFor="package-operador">Operador</label>
                    <select
                        id="package-operador"
                        className={camposSugeridos.supplierId ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.supplierId || ""}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, supplierId: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, supplierId: false }));
                            setCamposTocadosAMano((prev) => ({ ...prev, supplierId: true }));
                        }}
                        data-testid="package-supplier"
                        aria-label="Operador del paquete"
                    >
                        <option value="">Seleccioná un operador...</option>
                        {suppliersFull.map((supplier) => (
                            <option
                                key={supplier.publicId || supplier.PublicId}
                                value={supplier.publicId || supplier.PublicId}
                            >
                                {supplier.name}
                            </option>
                        ))}
                    </select>
                </div>
            )}

            {/* === SALIDA + FECHA DE FIN + PASAJEROS + BASE === */}
            {/*
             * grid-cols-2 en mobile (Salida + Fin en primera fila, Pasajeros + Base en segunda).
             * sm:grid-cols-4 en pantallas más anchas: los 4 campos en una sola fila.
             */}
            <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
                <div>
                    <label className={LABEL_BASE} htmlFor="package-salida">
                        <Calendar className="inline w-3 h-3 mr-1" />
                        Salida
                    </label>
                    <input
                        id="package-salida"
                        type="date"
                        // Amarillo (D13, spec 2026-08-10) cuando la fecha salió de la frase
                        // tipeada en el buscador.
                        className={camposSugeridos.startDate ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.startDate || ""}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, startDate: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, startDate: false }));
                            setCamposTocadosAMano((prev) => ({ ...prev, startDate: true }));
                        }}
                        data-testid="package-salida"
                        aria-label="Fecha de salida del paquete"
                    />
                </div>
                <div>
                    {/*
                     * Fecha de fin: opcional. El backend la acepta null (PackageBookingDto.EndDate
                     * es nullable desde ADR-018). Si el usuario no la carga, el backend coalesce
                     * EndDate a StartDate para calcular noches (Nights = 0).
                     * La validación fin < salida vive en ServiceInlineCard.validarForm.
                     */}
                    <label className={LABEL_BASE} htmlFor="package-fin">
                        <Calendar className="inline w-3 h-3 mr-1" />
                        Fecha de fin
                    </label>
                    <input
                        id="package-fin"
                        type="date"
                        className={camposSugeridos.endDate ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.endDate || ""}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, endDate: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, endDate: false }));
                            setCamposTocadosAMano((prev) => ({ ...prev, endDate: true }));
                        }}
                        data-testid="package-end-date"
                        aria-label="Fecha de fin del paquete"
                    />
                </div>
                <div>
                    <label className={LABEL_BASE} htmlFor="package-pasajeros">
                        <Users className="inline w-3 h-3 mr-1" />
                        Pasajeros
                    </label>
                    <input
                        id="package-pasajeros"
                        type="text"
                        inputMode="numeric"
                        className={INPUT_NORMAL}
                        value={form.passengers || ""}
                        onChange={(event) => setForm((prev) => ({ ...prev, passengers: sanitizarCantidadPositiva(event.target.value) }))}
                        placeholder="1"
                        data-testid="package-pasajeros"
                        aria-label="Cantidad de pasajeros"
                    />
                </div>
                <div>
                    {/*
                     * Base de habitación: mapea a occupancyBase del backend
                     * (PackageBookingDto.OccupancyBase). El value del option ES el valor backend.
                     */}
                    <label className={LABEL_BASE} htmlFor="package-base">Base</label>
                    <select
                        id="package-base"
                        className={INPUT_NORMAL}
                        value={form.roomBase || ""}
                        onChange={(event) => setForm((prev) => ({ ...prev, roomBase: event.target.value }))}
                        data-testid="package-base"
                        aria-label="Base de la habitación (doble, triple, etc.)"
                    >
                        <option value="">Sin especificar</option>
                        <option value="double">Doble</option>
                        <option value="triple">Triple</option>
                        <option value="quadruple">Cuádruple</option>
                        <option value="single">Simple</option>
                    </select>
                </div>
            </div>

            {/* === PRECIO POR PERSONA + COSTO POR PERSONA + TOTAL (calculado) + MONEDA === */}
            <div className={`grid gap-3 ${canSeeCost ? "grid-cols-2 sm:grid-cols-4" : "grid-cols-2 sm:grid-cols-3"}`}>
                {canSeeCost && (
                    <div>
                        <label className={LABEL_BASE} htmlFor="package-costo-persona">Costo por persona</label>
                        <MoneyInput
                            id="package-costo-persona"
                            className={camposSugeridos.unitNetCost ? INPUT_SUGERIDO : INPUT_NORMAL}
                            value={form.unitNetCost || ""}
                            onChange={(nuevoValor) => {
                                setForm((prev) => ({ ...prev, unitNetCost: nuevoValor }));
                                setCamposSugeridos((prev) => ({ ...prev, unitNetCost: false }));
                                setCamposTocadosAMano((prev) => ({ ...prev, unitNetCost: true }));
                            }}
                            data-testid="package-costo-persona"
                            aria-label="Costo por persona"
                        />
                        <LastSaleHint text={textoUltimoPrecio} />
                    </div>
                )}
                <div>
                    <label className={LABEL_BASE} htmlFor="package-venta-persona">Venta por persona</label>
                    <MoneyInput
                        id="package-venta-persona"
                        className={camposSugeridos.unitSalePrice ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.unitSalePrice || ""}
                        onChange={(nuevoValor) => {
                            setForm((prev) => ({ ...prev, unitSalePrice: nuevoValor }));
                            setCamposSugeridos((prev) => ({ ...prev, unitSalePrice: false }));
                            setCamposTocadosAMano((prev) => ({ ...prev, unitSalePrice: true }));
                        }}
                        required
                        data-testid="package-venta-persona"
                        aria-label="Precio de venta por persona"
                    />
                    {!canSeeCost && <LastSaleHint text={textoUltimoPrecio} />}
                </div>
                <div>
                    {/* Total calculado automáticamente: precio/persona × pasajeros */}
                    <label className={LABEL_BASE}>Total venta</label>
                    <input
                        type="text"
                        className={INPUT_CALCULADO}
                        value={ventaTotal > 0 ? formatearPrecio(ventaTotal, form.currency || "ARS") : "—"}
                        readOnly
                        tabIndex={-1}
                        aria-label={`Venta total: ${formatearPrecio(ventaTotal, form.currency || "ARS")}`}
                        data-testid="package-venta-total"
                    />
                </div>
                <div>
                    <label className={LABEL_BASE} htmlFor="package-moneda">Moneda</label>
                    <select
                        id="package-moneda"
                        className={camposSugeridos.currency ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.currency || "ARS"}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, currency: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, currency: false }));
                            setCamposTocadosAMano((prev) => ({ ...prev, currency: true }));
                        }}
                        data-testid="package-moneda"
                        aria-label="Moneda"
                    >
                        <option value="ARS">ARS (pesos)</option>
                        <option value="USD">USD (dólares)</option>
                    </select>
                </div>
            </div>

            {/* Campo "Fecha límite de seña" eliminado en F2 (Próximos Inicios).
                El aviso de la campanita se calcula desde firstStartDate (backend).
                operatorPaymentDeadline NO se mantiene en el estado ni en el payload. */}

            {/* === MÁS DETALLES: Qué incluye · Nº de file === */}
            <div>
                <button
                    type="button"
                    onClick={() => setMostrarDetalles((prev) => !prev)}
                    className="flex items-center gap-1 text-sm font-semibold text-blue-600 hover:text-blue-800 transition-colors"
                    data-testid="package-mas-detalles-toggle"
                    aria-expanded={mostrarDetalles}
                >
                    {mostrarDetalles ? <ChevronUp className="w-4 h-4" /> : <ChevronDown className="w-4 h-4" />}
                    {mostrarDetalles ? "Menos detalles" : "+ Más detalles"}
                </button>

                {mostrarDetalles && (
                    <div className="mt-3 grid grid-cols-1 sm:grid-cols-2 gap-3">
                        <div className="sm:col-span-2">
                            <label className={LABEL_BASE} htmlFor="package-incluye">Qué incluye</label>
                            <textarea
                                id="package-incluye"
                                className={`${INPUT_NORMAL} resize-none`}
                                rows={3}
                                value={form.itinerary || ""}
                                onChange={(event) => setForm((prev) => ({ ...prev, itinerary: event.target.value }))}
                                placeholder="Ej: Hotel 4* · Traslados aeropuerto · Excursión cataratas · Seguro de viaje"
                                data-testid="package-incluye"
                                aria-label="Qué incluye el paquete"
                            />
                        </div>
                        <div>
                            <label className={LABEL_BASE} htmlFor="package-file">Número de file del operador</label>
                            <input
                                id="package-file"
                                type="text"
                                className={INPUT_NORMAL}
                                value={form.fileNumber || ""}
                                onChange={(event) => setForm((prev) => ({ ...prev, fileNumber: event.target.value }))}
                                placeholder="Ej: PKG-2026-0482"
                                data-testid="package-file"
                                aria-label="Número de file del operador"
                            />
                        </div>
                    </div>
                )}
            </div>
        </div>
    );
}

// ─── Cálculo de totales exportado para el footer de ServiceInlineCard ─────────

/**
 * Calcula los totales del paquete para mostrar en el footer.
 * Total = precio por persona × pasajeros.
 */
export function calcularTotalesPaquete({ unitSalePrice, unitNetCost, passengers, canSeeCost }) {
    const pasajeros = Math.max(Number(passengers) || 1, 1);
    const ventaTotal = redondearDinero((Number(unitSalePrice) || 0) * pasajeros);
    const costoTotal = canSeeCost ? redondearDinero((Number(unitNetCost) || 0) * pasajeros) : null;
    const ganancia = canSeeCost && costoTotal !== null ? redondearDinero(ventaTotal - costoTotal) : null;
    return { pasajeros, ventaTotal, costoTotal, ganancia };
}
