/**
 * Formulario de Traslado dentro de la ficha de carga en línea (ServiceInlineCard).
 *
 * Campos a la vista SIEMPRE (sin revelado progresivo — guía UX ronda 1):
 *   Buscador de trayecto · Operador · Fecha · Llegada o salida · Pasajeros
 *   Privado o compartido · Costo · Venta · Moneda
 *
 * Más detalles (plegado):
 *   Número de vuelo asociado · Horario de búsqueda · Confirmación del operador
 *
 * Permiso `cobranzas.see_cost`:
 *   - Con permiso: ve el campo Costo + ganancia en el footer.
 *   - Sin permiso: no ve costo ni ganancia (jamás "$0").
 *
 * Cálculo del total: precio total de venta directo (traslado privado = precio cerrado;
 * compartido = normalmente precio por persona, pero el vendedor ingresa el total).
 */

import { useState, useEffect } from "react";
import { Car, ChevronDown, ChevronUp, Calendar, Users } from "lucide-react";
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
import { FreeTextWithMemoryField } from "../../rates/components/FreeTextWithMemoryField";
import { useVariantPriceSuggestion } from "./useVariantPriceSuggestion";
import { resolverCamposAlCambiarVariante } from "./variantPriceSuggestionLogic";
import { VariantSuggestionHint } from "./VariantSuggestionHint";
import { useSeleccionPendienteDelTipo } from "./useSeleccionPendienteDelTipo";

// D13 (spec 2026-08-10): Traslado tiene UN SOLO campo de fecha (sin rango) — solo
// "desde" en aplicarInterpretacionComoSugerencia, el "hasta" de la interpretación (si
// vino) se ignora acá.
const CAMPOS_FECHA_TRASLADO = ["pickupDate"];

// ─── Clases CSS ───────────────────────────────────────────────────────────────
const INPUT_BASE = "w-full py-2 px-3 text-sm border rounded-lg bg-white focus:outline-none focus:ring-1 focus:border-blue-500 focus:ring-blue-500 disabled:bg-slate-50 disabled:text-slate-400";
const INPUT_NORMAL = `${INPUT_BASE} border-slate-200`;
const INPUT_SUGERIDO = `${INPUT_BASE} border-yellow-400 bg-yellow-50`;
const LABEL_BASE = "block text-xs font-semibold text-slate-600 mb-1";

// ─── Recuadro violeta para trayecto nuevo ────────────────────────────────────

/**
 * Recuadro que aparece al crear un trayecto de traslado nuevo.
 * Campo mínimo: nombre del trayecto (ej: "EZE → Sheraton Pilar") + operador.
 *
 * `supplierSugerido`/`onSupplierTouched` (D13-bis, spec 2026-08-10): ver NewHotelBox.
 */
function NewTransferBox({ newProduct, onChange, suppliers, supplierSugerido, onSupplierTouched }) {
    return (
        <div className="border border-dashed border-violet-400 bg-violet-50 rounded-xl p-4 mb-4">
            <div className="flex items-center gap-2 mb-3">
                <Car className="w-4 h-4 text-violet-600" />
                <span className="text-sm font-semibold text-violet-700">
                    Trayecto nuevo — se guarda en tu tarifario al confirmar
                </span>
                <span className="text-[11px] font-semibold px-2 py-0.5 rounded-full bg-violet-200 text-violet-700">
                    Creado en venta
                </span>
            </div>
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                <div>
                    <label className={LABEL_BASE}>Trayecto *</label>
                    <input
                        type="text"
                        className={INPUT_NORMAL}
                        value={newProduct.name || ""}
                        onChange={(event) => onChange({ ...newProduct, name: event.target.value })}
                        placeholder="Ej: EZE → Sheraton Pilar"
                        required
                        data-testid="new-transfer-name"
                        aria-label="Nombre del trayecto nuevo"
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
                        data-testid="new-transfer-supplier"
                        aria-label="Operador del traslado nuevo"
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

// ─── Componente principal TransferInlineForm ──────────────────────────────────

export function TransferInlineForm({
    reservaId,
    form,
    setForm,
    suppliers,
    isEditing,
    // Salto de solapa (spec 2026-08-10, D1..D13) — ver ServiceInlineCard.jsx.
    onSelectOtherType,
    seleccionPendiente,
    onConsumirSeleccionPendiente,
    // Fix #4 (auditoría 2026-08-10): levantados a ServiceInlineCard — ver HotelInlineForm
    // para la explicación completa de por qué (sobreviven al remount de cambiar de solapa).
    camposSugeridos,
    setCamposSugeridos,
    precioTocadoPorElUsuario,
    setPrecioTocadoPorElUsuario,
    monedaTocadaPorElUsuario,
    setMonedaTocadaPorElUsuario,
    // Fix regresión #1+#6 (re-review 2026-08-10): APARTE de camposSugeridos — ver el
    // comentario largo en ServiceInlineCard.jsx donde se declara.
    camposTocadosAMano,
    setCamposTocadosAMano,
}) {
    const canSeeCost = hasPermission("cobranzas.see_cost");

    const ventaTotal = redondearDinero(Number(form.salePrice) || 0);
    const costoTotal = canSeeCost ? redondearDinero(Number(form.netCost) || 0) : null;
    const ganancia = canSeeCost && costoTotal !== null ? redondearDinero(ventaTotal - costoTotal) : null;

    // "Más detalles" se abre automáticamente al editar si ya hay datos en esos campos.
    // vehicleType ya NO se considera acá: subió a la vista principal (es la variante).
    const tieneDetallesExistentes = Boolean(
        form.associatedFlightNumber || form.pickupTime || form.confirmationNumber
    );
    const [mostrarDetalles, setMostrarDetalles] = useState(tieneDetallesExistentes || isEditing);

    // ─── Sugerencia POR VEHÍCULO (spec 2026-08-07, §3.3 / M-15 / V9=A / V10=A) ────────
    // En Traslado la variante es el vehículo (texto libre con memoria) — ver HotelInlineForm
    // para la explicación completa del patrón "se acomoda sola mientras no la toques".
    const campoPrecioVariante = canSeeCost ? "netCost" : "salePrice";
    const { suggestion: sugerenciaVariante } = useVariantPriceSuggestion({
        ratePublicId: form.rateId,
        supplierId: form.supplierId,
        vehicleType: form.vehicleType,
    });
    const [hintVariante, setHintVariante] = useState(null);

    useEffect(() => {
        if (!form.rateId) {
            // Fix ronda 3: sin producto elegido todavía no hay nada que sugerir ni que
            // limpiar — evita pintar de amarillo un casillero vacío que nadie sugirió.
            setHintVariante(null);
            return;
        }
        const resultado = resolverCamposAlCambiarVariante({
            estaPrecioTocado: precioTocadoPorElUsuario,
            estaMonedaTocada: monedaTocadaPorElUsuario,
            suggestion: sugerenciaVariante,
            // Fix #8 (auditoría 2026-08-10): ver HotelInlineForm — nunca vaciar un
            // precio que ya tiene valor.
            precioActual: form[campoPrecioVariante],
        });
        setHintVariante(resultado.hintText);
        if (resultado.debeActualizarPrecio || resultado.debeActualizarMoneda) {
            setForm((prev) => ({
                ...prev,
                ...(resultado.debeActualizarPrecio ? { [campoPrecioVariante]: resultado.price } : {}),
                ...(resultado.debeActualizarMoneda ? { currency: resultado.currency || prev.currency } : {}),
            }));
        }
        if (resultado.debeActualizarPrecio) {
            setCamposSugeridos((prev) => ({ ...prev, [campoPrecioVariante]: true }));
        }
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [sugerenciaVariante]);

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
            campoVenta: "salePrice",
            campoCosto: "netCost",
        });

        // Fix C-5(a) (review 2026-08-10): tampoco pisa lo tipeado a mano la precarga de
        // la frase (D13).
        const { patch: patchFrase, sugeridos: sugeridosFrase } = aplicarInterpretacionComoSugerencia(
            interpretacion,
            { yaHaySupplierDeLaVenta: Boolean(sale.supplierPublicId), camposFecha: CAMPOS_FECHA_TRASLADO, formActual: form, camposTocadosAMano }
        );

        setForm((prev) => ({
            ...prev,
            // D13: nombre limpio del producto elegido, nunca la frase entera.
            routeName: resolverNombreEnCasillero(catalogResult, prev.routeName),
            rateId: catalogResult.ratePublicId,
            newCatalogProduct: null,
            ...patchVenta,
            ...patchFrase,
        }));

        setCamposSugeridos({
            supplierId: false,
            netCost: false,
            salePrice: false,
            currency: false,
            pickupDate: false,
            ...sugeridosVenta,
            ...sugeridosFrase,
        });
        // Fix residual (re-review 2026-08-10, ítem A): sembrar desde el mapa persistente,
        // no apagar a ciegas — mismo motivo que HotelInlineForm.jsx.
        setPrecioTocadoPorElUsuario(camposTocadosAMano[campoPrecioVariante] === true);
        setMonedaTocadaPorElUsuario(camposTocadosAMano.currency === true);
        // El renglón gris de abajo ya NO sale de acá: lo arma la sugerencia POR VEHÍCULO
        // (useVariantPriceSuggestion), que se dispara sola apenas rateId queda seteado.
    };

    // Salto de solapa (D3/D7, spec 2026-08-10): pendiente elegida desde OTRA solapa.
    useSeleccionPendienteDelTipo({
        seleccionPendiente,
        serviceType: "Traslado",
        onSeleccionar: handleSelectExisting,
        onConsumida: onConsumirSeleccionPendiente,
    });

    const handleCreateNew = (searchText, interpretacion) => {
        // Bug #28 (Tanda 4, 2026-07-24): antes esto borraba operador/costo/venta/moneda
        // SIEMPRE, aunque el usuario los hubiera tipeado a mano. Ahora solo se limpian los
        // campos que el vendedor NUNCA tocó a mano (fix regresión #1+#6: la señal es
        // `camposTocadosAMano`, no `camposSugeridos`).
        const camposLimpios = resolverCamposALimpiarAlCrearNuevo(
            { supplierId: form.supplierId, netCost: form.netCost, salePrice: form.salePrice, currency: form.currency },
            camposTocadosAMano,
            { supplierId: "", netCost: "", salePrice: "", currency: "ARS" }
        );

        // D13-bis (spec 2026-08-10, fix "crear nuevo pelado"): fecha de la frase, misma
        // función/guardas que handleSelectExisting. El operador va aparte (recuadro
        // violeta, ver abajo) — acá no hay `sale`.
        const { patch: patchFechas, sugeridos: sugeridosFechas } = aplicarInterpretacionComoSugerencia(
            interpretacion,
            { yaHaySupplierDeLaVenta: true, camposFecha: CAMPOS_FECHA_TRASLADO, formActual: form, camposTocadosAMano }
        );

        // El operador del recuadro "trayecto nuevo" arranca SIEMPRE vacío — si la frase
        // trajo un operador REAL (matcheado por el motor), se precarga ahí, editable.
        const supplierSugeridoDelNuevo = resolverOperadorSugeridoParaProductoNuevo(interpretacion);

        setForm((prev) => ({
            ...prev,
            routeName: searchText,
            rateId: null,
            newCatalogProduct: { name: searchText, supplierPublicId: supplierSugeridoDelNuevo },
            ...camposLimpios,
            ...patchFechas,
        }));
        // `supplierId` se reusa para el amarillo del Operador del recuadro violeta
        // (D13-bis) — los dos campos nunca conviven en pantalla.
        setCamposSugeridos({
            supplierId: Boolean(supplierSugeridoDelNuevo),
            netCost: false,
            salePrice: false,
            currency: false,
            pickupDate: false,
            ...sugeridosFechas,
        });
        // Mismo criterio que en handleSelectExisting (fix residual, ítem A).
        setPrecioTocadoPorElUsuario(camposTocadosAMano[campoPrecioVariante] === true);
        setMonedaTocadaPorElUsuario(camposTocadosAMano.currency === true);
    };

    const handleSearchChange = (texto) => {
        setForm((prev) => ({
            ...prev,
            routeName: texto,
            rateId: null,
            newCatalogProduct: texto ? prev.newCatalogProduct : null,
        }));
        // Fix #6 (auditoría 2026-08-10): CUALQUIER tecleo desvincula el rateId — los
        // amarillos que quedaban pintados ya no corresponden a ninguna selección viva
        // (el VALOR se queda, deja de ser "sugerencia"). Antes esto solo pasaba si el
        // vendedor borraba TODO el texto.
        setCamposSugeridos({ supplierId: false, netCost: false, salePrice: false, currency: false, pickupDate: false });
        // Fix REGRESIÓN #1+#6 (re-review 2026-08-10): estos dos NO se tocan acá (ver
        // HotelInlineForm para la explicación completa). `camposTocadosAMano` tampoco.
    };

    return (
        <div className="space-y-4">

            {/* === BUSCADOR (trayecto) === */}
            <ProductSearchField
                reservaId={reservaId}
                serviceType="Traslado"
                value={form.routeName || ""}
                onChange={handleSearchChange}
                onSelectExisting={handleSelectExisting}
                onSelectOtherType={onSelectOtherType}
                onCreateNew={handleCreateNew}
                disabled={isEditing}
                esEdicion={isEditing}
                rateId={form.rateId}
                supplierIdElegido={form.supplierId}
                label="Trayecto"
                placeholder="Ej: EZE → hotel, Aeropuerto → ciudad..."
            />

            {/* === RECUADRO PRODUCTO NUEVO === */}
            {form.newCatalogProduct && (
                <NewTransferBox
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
                    <label className={LABEL_BASE} htmlFor="transfer-operador">Operador</label>
                    <select
                        id="transfer-operador"
                        className={camposSugeridos.supplierId ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.supplierId || ""}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, supplierId: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, supplierId: false }));
                            setCamposTocadosAMano((prev) => ({ ...prev, supplierId: true }));
                        }}
                        data-testid="transfer-supplier"
                        aria-label="Operador del traslado"
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

            {/* === FECHA + LLEGADA O SALIDA + PASAJEROS + TIPO === */}
            <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
                <div>
                    <label className={LABEL_BASE} htmlFor="transfer-fecha">
                        <Calendar className="inline w-3 h-3 mr-1" />
                        Fecha
                    </label>
                    <input
                        id="transfer-fecha"
                        type="date"
                        // Amarillo (D13, spec 2026-08-10) cuando la fecha salió de la frase
                        // tipeada en el buscador.
                        className={camposSugeridos.pickupDate ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.pickupDate || ""}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, pickupDate: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, pickupDate: false }));
                            setCamposTocadosAMano((prev) => ({ ...prev, pickupDate: true }));
                        }}
                        data-testid="transfer-fecha"
                        aria-label="Fecha del traslado"
                    />
                </div>
                <div>
                    {/*
                     * "Llegada o salida": mapea a direction "in"/"out" del backend
                     * (TransferBookingDto.Direction). El value del option ES el valor backend.
                     */}
                    <label className={LABEL_BASE} htmlFor="transfer-tipo-movimiento">Llegada o salida</label>
                    <select
                        id="transfer-tipo-movimiento"
                        className={INPUT_NORMAL}
                        value={form.movementType || ""}
                        onChange={(event) => setForm((prev) => ({ ...prev, movementType: event.target.value }))}
                        data-testid="transfer-tipo-movimiento"
                        aria-label="Tipo de movimiento: llegada o salida"
                    >
                        <option value="">Sin especificar</option>
                        <option value="in">Llegada</option>
                        <option value="out">Salida</option>
                    </select>
                </div>
                <div>
                    <label className={LABEL_BASE} htmlFor="transfer-pasajeros">
                        <Users className="inline w-3 h-3 mr-1" />
                        Pasajeros
                    </label>
                    <input
                        id="transfer-pasajeros"
                        type="text"
                        inputMode="numeric"
                        className={INPUT_NORMAL}
                        value={form.passengers || ""}
                        onChange={(event) => setForm((prev) => ({ ...prev, passengers: sanitizarCantidadPositiva(event.target.value) }))}
                        placeholder="1"
                        data-testid="transfer-pasajeros"
                        aria-label="Cantidad de pasajeros"
                    />
                </div>
                <div>
                    {/*
                     * Privado/Compartido: mapea a serviceMode "private"/"shared" del backend
                     * (TransferBookingDto.ServiceMode). El value del option ES el valor backend.
                     */}
                    <label className={LABEL_BASE} htmlFor="transfer-modalidad">Modalidad</label>
                    <select
                        id="transfer-modalidad"
                        className={INPUT_NORMAL}
                        value={form.transferType || ""}
                        onChange={(event) => setForm((prev) => ({ ...prev, transferType: event.target.value }))}
                        data-testid="transfer-modalidad"
                        aria-label="Privado o compartido"
                    >
                        <option value="">Sin especificar</option>
                        <option value="private">Privado</option>
                        <option value="shared">Compartido</option>
                    </select>
                </div>
            </div>

            {/* === VEHÍCULO (texto libre CON MEMORIA — spec 2026-08-07, §5.2) ===
                Sube de "Más detalles" a la vista principal: es la VARIANTE del traslado
                (junto con el trayecto y el operador definen qué precio recordar — V1=A). */}
            <div className="sm:max-w-xs">
                <FreeTextWithMemoryField
                    id="transfer-tipo-vehiculo"
                    dataTestId="inline-transfer-vehicle-type"
                    serviceType="Traslado"
                    label="Vehículo"
                    placeholder="Van, sedán, microbús..."
                    value={form.vehicleType}
                    onChange={(texto) => setForm((prev) => ({ ...prev, vehicleType: texto }))}
                />
            </div>

            {/* === PRECIOS + MONEDA === */}
            <div className={`grid gap-3 ${canSeeCost ? "grid-cols-2 sm:grid-cols-3" : "grid-cols-2"}`}>
                {canSeeCost && (
                    <div>
                        <label className={LABEL_BASE} htmlFor="transfer-costo">Costo</label>
                        <MoneyInput
                            id="transfer-costo"
                            className={camposSugeridos.netCost ? INPUT_SUGERIDO : INPUT_NORMAL}
                            value={form.netCost || ""}
                            onChange={(nuevoValor) => {
                                setForm((prev) => ({ ...prev, netCost: nuevoValor }));
                                setCamposSugeridos((prev) => ({ ...prev, netCost: false }));
                                setCamposTocadosAMano((prev) => ({ ...prev, netCost: true }));
                                // Con permiso de costos, "costo" ES el campo que la variante
                                // sigue (campoPrecioVariante === "netCost").
                                setPrecioTocadoPorElUsuario(true);
                            }}
                            data-testid="transfer-costo"
                            aria-label="Costo del traslado"
                        />
                        {/* Renglón gris POR VEHÍCULO (spec 2026-08-07, §3.3): dice si el precio
                            es de este vehículo o de uno parecido (V9=A). */}
                        <VariantSuggestionHint text={hintVariante} />
                    </div>
                )}
                <div>
                    <label className={LABEL_BASE} htmlFor="transfer-venta">Venta</label>
                    <MoneyInput
                        id="transfer-venta"
                        className={camposSugeridos.salePrice ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.salePrice || ""}
                        onChange={(nuevoValor) => {
                            setForm((prev) => ({ ...prev, salePrice: nuevoValor }));
                            setCamposSugeridos((prev) => ({ ...prev, salePrice: false }));
                            setCamposTocadosAMano((prev) => ({ ...prev, salePrice: true }));
                            // "Venta" solo es la variante rastreada para quien NO ve costos
                            // (campoPrecioVariante === "salePrice"); con permiso de costos
                            // es un campo aparte, ajeno a la sugerencia.
                            if (!canSeeCost) setPrecioTocadoPorElUsuario(true);
                        }}
                        required
                        data-testid="transfer-venta"
                        aria-label="Precio de venta del traslado"
                    />
                    {!canSeeCost && <VariantSuggestionHint text={hintVariante} />}
                </div>
                <div>
                    <label className={LABEL_BASE} htmlFor="transfer-moneda">Moneda</label>
                    <select
                        id="transfer-moneda"
                        className={camposSugeridos.currency ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.currency || "ARS"}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, currency: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, currency: false }));
                            setCamposTocadosAMano((prev) => ({ ...prev, currency: true }));
                            setMonedaTocadaPorElUsuario(true);
                        }}
                        data-testid="transfer-moneda"
                        aria-label="Moneda"
                    >
                        <option value="ARS">ARS (pesos)</option>
                        <option value="USD">USD (dólares)</option>
                    </select>
                </div>
            </div>

            {/* === MÁS DETALLES: Nº vuelo asociado · Horario de búsqueda · Confirmación === */}
            <div>
                <button
                    type="button"
                    onClick={() => setMostrarDetalles((prev) => !prev)}
                    className="flex items-center gap-1 text-sm font-semibold text-blue-600 hover:text-blue-800 transition-colors"
                    data-testid="transfer-mas-detalles-toggle"
                    aria-expanded={mostrarDetalles}
                >
                    {mostrarDetalles ? <ChevronUp className="w-4 h-4" /> : <ChevronDown className="w-4 h-4" />}
                    {mostrarDetalles ? "Menos detalles" : "+ Más detalles"}
                </button>

                {mostrarDetalles && (
                    <div className="mt-3 grid grid-cols-1 sm:grid-cols-2 gap-3">
                        <div>
                            <label className={LABEL_BASE} htmlFor="transfer-vuelo-asociado">Número de vuelo asociado</label>
                            <input
                                id="transfer-vuelo-asociado"
                                type="text"
                                className={INPUT_NORMAL}
                                value={form.associatedFlightNumber || ""}
                                onChange={(event) => setForm((prev) => ({ ...prev, associatedFlightNumber: event.target.value.toUpperCase() }))}
                                placeholder="Ej: AR1234"
                                data-testid="transfer-vuelo-asociado"
                                aria-label="Número de vuelo asociado al traslado"
                            />
                        </div>
                        <div>
                            <label className={LABEL_BASE} htmlFor="transfer-horario">Horario de búsqueda</label>
                            <input
                                id="transfer-horario"
                                type="time"
                                className={INPUT_NORMAL}
                                value={form.pickupTime || ""}
                                onChange={(event) => setForm((prev) => ({ ...prev, pickupTime: event.target.value }))}
                                data-testid="transfer-horario"
                                aria-label="Horario de búsqueda del traslado"
                            />
                        </div>
                        <div className="sm:col-span-2">
                            <label className={LABEL_BASE} htmlFor="transfer-confirmacion">Confirmación del operador</label>
                            <input
                                id="transfer-confirmacion"
                                type="text"
                                className={INPUT_NORMAL}
                                value={form.confirmationNumber || ""}
                                onChange={(event) => setForm((prev) => ({ ...prev, confirmationNumber: event.target.value }))}
                                placeholder="Número o código de confirmación"
                                data-testid="transfer-confirmacion"
                                aria-label="Número de confirmación del operador"
                            />
                        </div>
                        {/* Tipo de vehículo: subió a la vista principal (ver más arriba, campo
                            "Vehículo" con memoria) — ya no vive acá adentro. */}
                    </div>
                )}
            </div>
        </div>
    );
}

// ─── Cálculo de totales exportado para el footer de ServiceInlineCard ─────────

/**
 * Calcula los totales del traslado para mostrar en el footer.
 * El traslado usa precio total directo (privado = precio cerrado; compartido = total también).
 */
export function calcularTotalesTraslado({ salePrice, netCost, canSeeCost }) {
    const ventaTotal = redondearDinero(Number(salePrice) || 0);
    const costoTotal = canSeeCost ? redondearDinero(Number(netCost) || 0) : null;
    const ganancia = canSeeCost && costoTotal !== null ? redondearDinero(ventaTotal - costoTotal) : null;
    return { ventaTotal, costoTotal, ganancia };
}
