/**
 * Formulario de Asistencia al viajero dentro de la ficha de carga en línea (ServiceInlineCard).
 *
 * Campos a la vista SIEMPRE (sin revelado progresivo — guía UX ronda 1):
 *   Buscador del plan · Operador · Vigencia desde · Vigencia hasta
 *   Días (calculados solos) · Pasajeros · Costo · Venta · Moneda
 *
 * Más detalles (plegado):
 *   Confirmación del operador · Números de voucher por pasajero · Upgrades (edad, embarazo, deportes)
 *
 * "Confirmación del operador" es el mismo campo que ya tienen Hotel/Traslado/Paquete: sin
 * cargarlo, el motor de resolución de la reserva nunca da por confirmado el servicio (queda
 * "Falta voucher" para siempre) y la reserva no puede pasar a Confirmada.
 *
 * Permiso `cobranzas.see_cost`:
 *   - Con permiso: ve el campo Costo + ganancia en el footer.
 *   - Sin permiso: no ve costo ni ganancia (jamás "$0").
 *
 * Cálculo del total: precio por persona × pasajeros × días.
 * Los "días" se calculan automáticamente a partir de vigencia desde/hasta.
 */

import { useState } from "react";
import { ShieldCheck, ChevronDown, ChevronUp, Calendar, Users } from "lucide-react";
import { hasPermission } from "../../../auth";
import { ProductSearchField } from "./ProductSearchField";
import { redondearDinero, formatearPrecio } from "./HotelInlineForm";
import {
    resolverCamposALimpiarAlCrearNuevo,
    aplicarInterpretacionComoSugerencia,
    resolverNombreEnCasillero,
    resolverPatchDeVentaDelCatalogo,
} from "./inlineServiceFormHelpers";
import { buildLastSaleHintText } from "./lastSaleHintLogic";
import { LastSaleHint } from "./LastSaleHint";
import { useSeleccionPendienteDelTipo } from "./useSeleccionPendienteDelTipo";

// D13 (spec 2026-08-10): campos de fecha de ESTE form, [desde, hasta] — Asistencia
// tiene vigencia desde/hasta.
const CAMPOS_FECHA_ASISTENCIA = ["validFrom", "validTo"];

// ─── Clases CSS ───────────────────────────────────────────────────────────────
const INPUT_BASE = "w-full py-2 px-3 text-sm border rounded-lg bg-white focus:outline-none focus:ring-1 focus:border-blue-500 focus:ring-blue-500 disabled:bg-slate-50 disabled:text-slate-400";
const INPUT_NORMAL = `${INPUT_BASE} border-slate-200`;
const INPUT_SUGERIDO = `${INPUT_BASE} border-yellow-400 bg-yellow-50`;
const INPUT_CALCULADO = `${INPUT_BASE} border-slate-200 border-dashed bg-slate-50 text-slate-600 font-semibold cursor-default`;
const LABEL_BASE = "block text-xs font-semibold text-slate-600 mb-1";

// ─── Helper: calcular días de vigencia ───────────────────────────────────────

/**
 * Calcula la cantidad de días entre dos fechas de vigencia (validFrom, validTo).
 * La asistencia al viajero se mide en días completos (ej: seguro de 8 días).
 * Devuelve 0 si alguna fecha falta o la fecha final es anterior a la inicial.
 */
export function calcularDiasVigencia(validFrom, validTo) {
    if (!validFrom || !validTo) return 0;
    const inicio = new Date(validFrom);
    const fin = new Date(validTo);
    // +1 porque el día de llegada también cuenta (sale el día 1, llega el día 8 = 8 días)
    const dias = Math.ceil((fin - inicio) / (1000 * 60 * 60 * 24)) + 1;
    return dias > 0 ? dias : 0;
}

// ─── Recuadro violeta para plan nuevo ────────────────────────────────────────

/**
 * Recuadro que aparece al crear un plan de asistencia nuevo.
 * Campo mínimo: nombre del plan + proveedor.
 */
function NewAssistanceBox({ newProduct, onChange, suppliers }) {
    return (
        <div className="border border-dashed border-violet-400 bg-violet-50 rounded-xl p-4 mb-4">
            <div className="flex items-center gap-2 mb-3">
                <ShieldCheck className="w-4 h-4 text-violet-600" />
                <span className="text-sm font-semibold text-violet-700">
                    Plan nuevo — se guarda en tu tarifario al confirmar
                </span>
                <span className="text-[11px] font-semibold px-2 py-0.5 rounded-full bg-violet-200 text-violet-700">
                    Creado en venta
                </span>
            </div>
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                <div>
                    <label className={LABEL_BASE}>Plan / cobertura *</label>
                    <input
                        type="text"
                        className={INPUT_NORMAL}
                        value={newProduct.name || ""}
                        onChange={(event) => onChange({ ...newProduct, name: event.target.value })}
                        placeholder="Ej: AC 150 Americas Plata"
                        required
                        data-testid="new-assistance-name"
                        aria-label="Nombre del plan de asistencia nuevo"
                    />
                </div>
                <div>
                    <label className={LABEL_BASE}>Operador *</label>
                    <select
                        className={INPUT_NORMAL}
                        value={newProduct.supplierPublicId || ""}
                        onChange={(event) => onChange({ ...newProduct, supplierPublicId: event.target.value })}
                        required
                        data-testid="new-assistance-supplier"
                        aria-label="Operador del plan nuevo"
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

// ─── Componente principal AssistanceInlineForm ────────────────────────────────

export function AssistanceInlineForm({
    reservaId,
    form,
    setForm,
    suppliers,
    isEditing,
    // Salto de solapa (spec 2026-08-10, D1..D13) — ver ServiceInlineCard.jsx.
    onSelectOtherType,
    seleccionPendiente,
    onConsumirSeleccionPendiente,
}) {
    const canSeeCost = hasPermission("cobranzas.see_cost");

    // Días calculados automáticamente desde vigencia desde/hasta
    const dias = calcularDiasVigencia(form.validFrom, form.validTo);

    // Pasajeros: mínimo 1 para el cálculo
    const pasajeros = Math.max(Number(form.passengers) || 1, 1);

    // Total = precio por persona × pasajeros × días
    // El precio unitario en el tarifario es "por persona por día"
    const factorTotal = Math.max(dias, 0) * pasajeros;
    const ventaTotal = redondearDinero((Number(form.unitSalePrice) || 0) * factorTotal);
    const costoTotal = canSeeCost
        ? redondearDinero((Number(form.unitNetCost) || 0) * factorTotal)
        : null;
    const ganancia = canSeeCost && costoTotal !== null ? redondearDinero(ventaTotal - costoTotal) : null;

    // "Más detalles" se abre automáticamente al editar si ya hay datos
    const tieneDetallesExistentes = Boolean(form.voucherNumbers || form.upgrades || form.confirmationNumber);
    const [mostrarDetalles, setMostrarDetalles] = useState(tieneDetallesExistentes || isEditing);

    const [camposSugeridos, setCamposSugeridos] = useState({
        supplierId: false,
        unitNetCost: false,
        unitSalePrice: false,
        currency: false,
        // validFrom/validTo (D13, spec 2026-08-10): amarillo cuando salen de la
        // interpretación de la frase completa tipeada en el buscador.
        validFrom: false,
        validTo: false,
    });

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

    const handleSelectExisting = (catalogResult, interpretacion, { esSeleccionPendiente } = {}) => {
        const sale = catalogResult.lastSale || catalogResult.rateFallback || {};

        // Fix C-5(b) (review 2026-08-10): en el camino de selección PENDIENTE (salto de
        // solapa) no se pisa lo que el vendedor ya había tipeado a mano en esta solapa.
        const { patch: patchVenta, sugeridos: sugeridosVenta } = resolverPatchDeVentaDelCatalogo({
            sale,
            canSeeCost,
            formActual: form,
            esSeleccionPendiente,
            campoVenta: "unitSalePrice",
            campoCosto: "unitNetCost",
        });

        // Fix C-5(a) (review 2026-08-10): tampoco pisa lo tipeado a mano la precarga de
        // la frase (D13).
        const { patch: patchFrase, sugeridos: sugeridosFrase } = aplicarInterpretacionComoSugerencia(
            interpretacion,
            { yaHaySupplierDeLaVenta: Boolean(sale.supplierPublicId), camposFecha: CAMPOS_FECHA_ASISTENCIA, formActual: form }
        );

        setForm((prev) => ({
            ...prev,
            // D13: nombre limpio del producto elegido, nunca la frase entera.
            planName: resolverNombreEnCasillero(catalogResult, prev.planName),
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
            validFrom: false,
            validTo: false,
            ...sugeridosVenta,
            ...sugeridosFrase,
        });
        setUltimoPrecioSugerido(catalogResult);
    };

    // Salto de solapa (D3/D7, spec 2026-08-10): pendiente elegida desde OTRA solapa.
    useSeleccionPendienteDelTipo({
        seleccionPendiente,
        serviceType: "Asistencia",
        onSeleccionar: handleSelectExisting,
        onConsumida: onConsumirSeleccionPendiente,
    });

    const handleCreateNew = (searchText) => {
        // Bug #28 (Tanda 4, 2026-07-24): antes esto borraba operador/costo/venta/moneda
        // SIEMPRE, aunque el usuario los hubiera tipeado a mano. Ahora solo se limpian los
        // campos que TODAVÍA son sugerencia sin tocar (ver resolverCamposALimpiarAlCrearNuevo).
        const camposLimpios = resolverCamposALimpiarAlCrearNuevo(
            { supplierId: form.supplierId, unitNetCost: form.unitNetCost, unitSalePrice: form.unitSalePrice, currency: form.currency },
            camposSugeridos,
            { supplierId: "", unitNetCost: "", unitSalePrice: "", currency: "ARS" }
        );
        setForm((prev) => ({
            ...prev,
            planName: searchText,
            rateId: null,
            newCatalogProduct: { name: searchText, supplierPublicId: "" },
            ...camposLimpios,
        }));
        setCamposSugeridos({ supplierId: false, unitNetCost: false, unitSalePrice: false, currency: false, validFrom: false, validTo: false });
        setUltimoPrecioSugerido(null);
    };

    const handleSearchChange = (texto) => {
        setForm((prev) => ({
            ...prev,
            planName: texto,
            rateId: null,
            newCatalogProduct: texto ? prev.newCatalogProduct : null,
        }));
        if (!texto) {
            setCamposSugeridos({ supplierId: false, unitNetCost: false, unitSalePrice: false, currency: false, validFrom: false, validTo: false });
        }
        setUltimoPrecioSugerido(null);
    };

    return (
        <div className="space-y-4">

            {/* === BUSCADOR (plan de asistencia) === */}
            <ProductSearchField
                reservaId={reservaId}
                serviceType="Asistencia"
                value={form.planName || ""}
                onChange={handleSearchChange}
                onSelectExisting={handleSelectExisting}
                onSelectOtherType={onSelectOtherType}
                onCreateNew={handleCreateNew}
                disabled={isEditing}
                esEdicion={isEditing}
                label="Plan / cobertura"
                placeholder="Ej: AC 150, Assist Card, Fullcard..."
            />

            {/* === RECUADRO PRODUCTO NUEVO === */}
            {form.newCatalogProduct && (
                <NewAssistanceBox
                    newProduct={form.newCatalogProduct}
                    onChange={(newProduct) => setForm((prev) => ({ ...prev, newCatalogProduct: newProduct }))}
                    suppliers={suppliers}
                />
            )}

            {/* === OPERADOR === */}
            {!form.newCatalogProduct && (
                <div>
                    <label className={LABEL_BASE} htmlFor="assistance-proveedor">Operador</label>
                    <select
                        id="assistance-proveedor"
                        className={camposSugeridos.supplierId ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.supplierId || ""}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, supplierId: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, supplierId: false }));
                        }}
                        data-testid="assistance-supplier"
                        aria-label="Operador de la asistencia"
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

            {/* === VIGENCIA DESDE + HASTA + DÍAS (calculados) + PASAJEROS === */}
            <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
                <div>
                    <label className={LABEL_BASE} htmlFor="assistance-desde">
                        <Calendar className="inline w-3 h-3 mr-1" />
                        Vigencia desde
                    </label>
                    <input
                        id="assistance-desde"
                        type="date"
                        // Amarillo (D13, spec 2026-08-10) cuando la fecha salió de la frase
                        // tipeada en el buscador.
                        className={camposSugeridos.validFrom ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.validFrom || ""}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, validFrom: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, validFrom: false }));
                        }}
                        data-testid="assistance-desde"
                        aria-label="Vigencia desde"
                    />
                </div>
                <div>
                    <label className={LABEL_BASE} htmlFor="assistance-hasta">
                        <Calendar className="inline w-3 h-3 mr-1" />
                        Vigencia hasta
                    </label>
                    <input
                        id="assistance-hasta"
                        type="date"
                        className={camposSugeridos.validTo ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.validTo || ""}
                        min={form.validFrom || undefined}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, validTo: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, validTo: false }));
                        }}
                        data-testid="assistance-hasta"
                        aria-label="Vigencia hasta"
                    />
                </div>
                <div>
                    {/* Días calculados solos (guía UX: "días calculados solos") */}
                    <label className={LABEL_BASE}>Días</label>
                    <input
                        type="text"
                        className={INPUT_CALCULADO}
                        value={dias > 0 ? dias : "—"}
                        readOnly
                        tabIndex={-1}
                        aria-label={`Días de cobertura: ${dias}`}
                        data-testid="assistance-dias"
                    />
                </div>
                <div>
                    <label className={LABEL_BASE} htmlFor="assistance-pasajeros">
                        <Users className="inline w-3 h-3 mr-1" />
                        Pasajeros
                    </label>
                    <input
                        id="assistance-pasajeros"
                        type="number"
                        min={1}
                        className={INPUT_NORMAL}
                        value={form.passengers || ""}
                        onChange={(event) => setForm((prev) => ({ ...prev, passengers: event.target.value }))}
                        placeholder="1"
                        data-testid="assistance-pasajeros"
                        aria-label="Cantidad de pasajeros"
                    />
                </div>
            </div>

            {/* === PRECIOS + MONEDA === */}
            <div className={`grid gap-3 ${canSeeCost ? "grid-cols-2 sm:grid-cols-3" : "grid-cols-2"}`}>
                {canSeeCost && (
                    <div>
                        <label className={LABEL_BASE} htmlFor="assistance-costo">Costo por persona/día</label>
                        <input
                            id="assistance-costo"
                            type="number"
                            min={0}
                            step="0.01"
                            className={camposSugeridos.unitNetCost ? INPUT_SUGERIDO : INPUT_NORMAL}
                            value={form.unitNetCost || ""}
                            onChange={(event) => {
                                setForm((prev) => ({ ...prev, unitNetCost: event.target.value }));
                                setCamposSugeridos((prev) => ({ ...prev, unitNetCost: false }));
                            }}
                            placeholder="0,00"
                            data-testid="assistance-costo"
                            aria-label="Costo por persona por día"
                        />
                        <LastSaleHint text={textoUltimoPrecio} />
                    </div>
                )}
                <div>
                    <label className={LABEL_BASE} htmlFor="assistance-venta">Venta por persona/día</label>
                    <input
                        id="assistance-venta"
                        type="number"
                        min={0}
                        step="0.01"
                        className={camposSugeridos.unitSalePrice ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.unitSalePrice || ""}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, unitSalePrice: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, unitSalePrice: false }));
                        }}
                        placeholder="0,00"
                        required
                        data-testid="assistance-venta"
                        aria-label="Precio de venta por persona por día"
                    />
                    {!canSeeCost && <LastSaleHint text={textoUltimoPrecio} />}
                </div>
                <div>
                    <label className={LABEL_BASE} htmlFor="assistance-moneda">Moneda</label>
                    <select
                        id="assistance-moneda"
                        className={camposSugeridos.currency ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.currency || "ARS"}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, currency: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, currency: false }));
                        }}
                        data-testid="assistance-moneda"
                        aria-label="Moneda"
                    >
                        <option value="ARS">ARS (pesos)</option>
                        <option value="USD">USD (dólares)</option>
                    </select>
                </div>
            </div>

            {/* === MÁS DETALLES: Nº voucher por pax · Upgrades === */}
            <div>
                <button
                    type="button"
                    onClick={() => setMostrarDetalles((prev) => !prev)}
                    className="flex items-center gap-1 text-sm font-semibold text-blue-600 hover:text-blue-800 transition-colors"
                    data-testid="assistance-mas-detalles-toggle"
                    aria-expanded={mostrarDetalles}
                >
                    {mostrarDetalles ? <ChevronUp className="w-4 h-4" /> : <ChevronDown className="w-4 h-4" />}
                    {mostrarDetalles ? "Menos detalles" : "+ Más detalles"}
                </button>

                {mostrarDetalles && (
                    <div className="mt-3 grid grid-cols-1 sm:grid-cols-2 gap-3">
                        <div className="sm:col-span-2">
                            <label className={LABEL_BASE} htmlFor="assistance-confirmacion">Confirmación del operador</label>
                            <input
                                id="assistance-confirmacion"
                                type="text"
                                className={INPUT_NORMAL}
                                value={form.confirmationNumber || ""}
                                onChange={(event) => setForm((prev) => ({ ...prev, confirmationNumber: event.target.value }))}
                                placeholder="Número o código de confirmación"
                                data-testid="assistance-confirmacion"
                                aria-label="Número de confirmación del operador"
                            />
                        </div>
                        <div className="sm:col-span-2">
                            <label className={LABEL_BASE} htmlFor="assistance-vouchers">Números de voucher por pasajero</label>
                            <textarea
                                id="assistance-vouchers"
                                className={`${INPUT_NORMAL} resize-none`}
                                rows={2}
                                value={form.voucherNumbers || ""}
                                onChange={(event) => setForm((prev) => ({ ...prev, voucherNumbers: event.target.value }))}
                                placeholder="Ej: Pasajero 1: V-123456 · Pasajero 2: V-123457"
                                data-testid="assistance-vouchers"
                                aria-label="Números de voucher por pasajero"
                            />
                        </div>
                        <div className="sm:col-span-2">
                            <label className={LABEL_BASE} htmlFor="assistance-upgrades">Upgrades (edad, embarazo, deportes)</label>
                            <input
                                id="assistance-upgrades"
                                type="text"
                                className={INPUT_NORMAL}
                                value={form.upgrades || ""}
                                onChange={(event) => setForm((prev) => ({ ...prev, upgrades: event.target.value }))}
                                placeholder="Ej: Upgrade edad 70+, Embarazo 24 semanas, Deportes extremos"
                                data-testid="assistance-upgrades"
                                aria-label="Upgrades de la cobertura"
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
 * Calcula los totales de la asistencia para mostrar en el footer.
 * Total = precio por persona × pasajeros × días.
 */
export function calcularTotalesAsistencia({ unitSalePrice, unitNetCost, passengers, validFrom, validTo, canSeeCost }) {
    const dias = calcularDiasVigencia(validFrom, validTo);
    const pasajeros = Math.max(Number(passengers) || 1, 1);
    const factorTotal = Math.max(dias, 0) * pasajeros;
    const ventaTotal = redondearDinero((Number(unitSalePrice) || 0) * factorTotal);
    const costoTotal = canSeeCost ? redondearDinero((Number(unitNetCost) || 0) * factorTotal) : null;
    const ganancia = canSeeCost && costoTotal !== null ? redondearDinero(ventaTotal - costoTotal) : null;
    return { dias, pasajeros, factorTotal, ventaTotal, costoTotal, ganancia };
}
