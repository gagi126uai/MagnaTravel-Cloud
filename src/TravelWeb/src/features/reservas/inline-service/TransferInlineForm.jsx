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
 *
 * LÍNEA INTELIGENTE (spec 2026-08-07, §3): mientras el vendedor escribe la frase entera
 * en el buscador de trayecto, `useServiceLineInterpretationForForm` va precargando en
 * amarillo lo que el motor entendió (operador, vehículo, fecha, costo).
 */

import { useState, useEffect } from "react";
import { Car, ChevronDown, ChevronUp, Calendar, Users } from "lucide-react";
import { hasPermission } from "../../../auth";
import { ProductSearchField } from "./ProductSearchField";
import { redondearDinero, formatearPrecio } from "./HotelInlineForm";
import { resolverCamposALimpiarAlCrearNuevo } from "./inlineServiceFormHelpers";
import { FreeTextWithMemoryField } from "../../rates/components/FreeTextWithMemoryField";
import { useVariantPriceSuggestion } from "./useVariantPriceSuggestion";
import { resolverCamposAlCambiarVariante } from "./variantPriceSuggestionLogic";
import { VariantSuggestionHint } from "./VariantSuggestionHint";
import { useServiceLineInterpretationForForm } from "./useServiceLineInterpretationForForm";
import { ServiceLineDoubtQuestion } from "./ServiceLineDoubtQuestion";
import { ResolvedProductRow } from "./ResolvedProductRow";
import { DOUBT_FIELD, construirPatchDeSeleccionManual, debeResetearTocadoTrasSeleccion } from "./serviceLineInterpretationLogic";

// ─── Clases CSS ───────────────────────────────────────────────────────────────
const INPUT_BASE = "w-full py-2 px-3 text-sm border rounded-lg bg-white focus:outline-none focus:ring-1 focus:border-blue-500 focus:ring-blue-500 disabled:bg-slate-50 disabled:text-slate-400";
const INPUT_NORMAL = `${INPUT_BASE} border-slate-200`;
const INPUT_SUGERIDO = `${INPUT_BASE} border-yellow-400 bg-yellow-50`;
const LABEL_BASE = "block text-xs font-semibold text-slate-600 mb-1";

// Ids de los campos que puede señalar una duda grande de la línea inteligente (§4).
const IDS_DUDA_LINEA_INTELIGENTE = {
    supplierId: "transfer-operador",
    netCost: "transfer-costo",
    pickupDate: "transfer-fecha",
};

// ─── Recuadro violeta para trayecto nuevo ────────────────────────────────────

/**
 * Recuadro que aparece al crear un trayecto de traslado nuevo.
 * Campo mínimo: nombre del trayecto (ej: "EZE → Sheraton Pilar") + operador.
 */
function NewTransferBox({ newProduct, onChange, suppliers }) {
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
                        className={INPUT_NORMAL}
                        value={newProduct.supplierPublicId || ""}
                        onChange={(event) => onChange({ ...newProduct, supplierPublicId: event.target.value })}
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

export function TransferInlineForm({ reservaId, form, setForm, suppliers, isEditing }) {
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

    const [camposSugeridos, setCamposSugeridos] = useState({
        supplierId: false,
        netCost: false,
        salePrice: false,
        currency: false,
    });

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

    // Flags EXPLÍCITOS de "el vendedor escribió acá a mano" (fix ronda 2 de review —
    // hallazgos #4 y #5, ver HotelInlineForm para la explicación completa). Reemplazan a
    // derivar el touch-status de `camposSugeridos`, que confundía "vacío y nunca tocado"
    // con "tocado" y trataba precio+moneda como un solo territorio.
    //
    // Fix ronda 3 (BLOQUEANTE, ver HotelInlineForm): en modo edición arrancan en `true` —
    // el precio/moneda de un servicio YA GUARDADO no es una sugerencia del sistema, es un
    // dato del vendedor. Sin este seed, el efecto de abajo corría en el MONTAJE con
    // `sugerenciaVariante` todavía en null (la consulta recién se disparó) y borraba el
    // costo/venta cargado del servicio.
    const [precioTocadoPorElUsuario, setPrecioTocadoPorElUsuario] = useState(isEditing);
    const [monedaTocadaPorElUsuario, setMonedaTocadaPorElUsuario] = useState(isEditing);

    // Texto de la caja de arriba, separado de routeName (mockup firmado §3.3, ver
    // HotelInlineForm para la explicación completa del porqué).
    const [textoBuscador, setTextoBuscador] = useState(() => form.routeName || "");

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

    // ─── LA LÍNEA INTELIGENTE (spec 2026-08-07, §3) ───────────────────────────────────
    const {
        isThinking: pensandoLineaInteligente,
        duda: dudaLineaInteligente,
        onRespuestaDuda,
        aiOverride,
        productoResueltoPorLineaInteligente,
        limpiarResolucionIA,
        marcarTocado,
        camposTocados,
    } = useServiceLineInterpretationForForm({
        reservaId,
        serviceType: "Traslado",
        isEditing,
        canSeeCost,
        form,
        setForm,
        setCamposSugeridos,
        precioTocadoPorElUsuario,
        monedaTocadaPorElUsuario,
        idsDeCampoParaEnfocar: IDS_DUDA_LINEA_INTELIGENTE,
        // Ver el mismo comentario en HotelInlineForm: evita que la sugerencia POR VEHÍCULO
        // reponga un precio/moneda que el vendedor acaba de rechazar con "No".
        alVaciarCampoPorDuda: (campo) => {
            if (campo === "netCost" || campo === "salePrice") setPrecioTocadoPorElUsuario(true);
            if (campo === "currency") setMonedaTocadaPorElUsuario(true);
        },
        // Bug bloqueante B2: el costo que salió de LA FRASE cuenta como "tocado" para la
        // sugerencia POR VEHÍCULO, para que no lo pise 300ms después.
        alPrecargarPrecioDeLaFrase: (cual) => {
            if (cual === "costo") setPrecioTocadoPorElUsuario(true);
            if (cual === "moneda") setMonedaTocadaPorElUsuario(true);
        },
    });

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

    const handleSelectExisting = (catalogResult, meta) => {
        const sale = catalogResult.lastSale || catalogResult.rateFallback || {};

        // Bug bloqueante B3: ver el comentario completo en HotelInlineForm.
        const { patch: patchVentaCatalogo, camposSugeridos: sugeridosVentaCatalogo } = meta?.fromAiOverride
            ? construirPatchDeSeleccionManual({
                  serviceType: "Traslado", sale, canSeeCost,
                  camposActualmenteSugeridos: camposSugeridos, camposTocados,
              })
            : {
                  patch: {
                      supplierId: sale.supplierPublicId || "",
                      supplierName: sale.supplierName || null,
                      salePrice: sale.salePrice != null ? String(sale.salePrice) : "",
                      netCost: canSeeCost && sale.netCost != null ? String(sale.netCost) : form.netCost,
                      currency: sale.currency || "ARS",
                  },
                  camposSugeridos: {
                      supplierId: Boolean(sale.supplierPublicId),
                      netCost: canSeeCost && sale.netCost != null,
                      salePrice: Boolean(sale.salePrice),
                      currency: Boolean(sale.currency),
                  },
              };

        setForm((prev) => ({
            ...prev,
            routeName: catalogResult.name || prev.routeName,
            rateId: catalogResult.ratePublicId,
            newCatalogProduct: null,
            ...patchVentaCatalogo,
        }));
        setTextoBuscador(catalogResult.name || form.routeName || "");

        setCamposSugeridos((prev) => ({ ...prev, ...sugeridosVentaCatalogo }));
        // Producto NUEVO recién elegido: sueltan los flags SOLO si el campo se pisó de
        // verdad con la venta del catálogo (bug bloqueante B2, segunda vuelta) — ver el
        // comentario completo en HotelInlineForm.
        if (debeResetearTocadoTrasSeleccion({ fromAiOverride: meta?.fromAiOverride, campo: campoPrecioVariante, camposSugeridosDeVenta: sugeridosVentaCatalogo })) {
            setPrecioTocadoPorElUsuario(false);
        }
        if (debeResetearTocadoTrasSeleccion({ fromAiOverride: meta?.fromAiOverride, campo: "currency", camposSugeridosDeVenta: sugeridosVentaCatalogo })) {
            setMonedaTocadaPorElUsuario(false);
        }
        // El renglón gris de abajo ya NO sale de acá: lo arma la sugerencia POR VEHÍCULO
        // (useVariantPriceSuggestion), que se dispara sola apenas rateId queda seteado.
        limpiarResolucionIA();
    };

    const handleCreateNew = (searchText) => {
        // Bug #28 (Tanda 4, 2026-07-24): antes esto borraba operador/costo/venta/moneda
        // SIEMPRE, aunque el usuario los hubiera tipeado a mano. Ahora solo se limpian los
        // campos que TODAVÍA son sugerencia sin tocar (ver resolverCamposALimpiarAlCrearNuevo).
        const camposLimpios = resolverCamposALimpiarAlCrearNuevo(
            { supplierId: form.supplierId, netCost: form.netCost, salePrice: form.salePrice, currency: form.currency },
            camposSugeridos,
            { supplierId: "", netCost: "", salePrice: "", currency: "ARS" }
        );
        setForm((prev) => ({
            ...prev,
            routeName: searchText,
            rateId: null,
            newCatalogProduct: { name: searchText, supplierPublicId: "" },
            ...camposLimpios,
        }));
        setTextoBuscador(searchText);
        setCamposSugeridos({ supplierId: false, netCost: false, salePrice: false, currency: false });
        setPrecioTocadoPorElUsuario(false);
        setMonedaTocadaPorElUsuario(false);
        limpiarResolucionIA();
    };

    const handleSearchChange = (texto) => {
        setTextoBuscador(texto);
        setForm((prev) => ({
            ...prev,
            routeName: texto,
            rateId: null,
            newCatalogProduct: texto ? prev.newCatalogProduct : null,
        }));
        limpiarResolucionIA();
        if (!texto) {
            setCamposSugeridos({ supplierId: false, netCost: false, salePrice: false, currency: false });
            setPrecioTocadoPorElUsuario(false);
            setMonedaTocadaPorElUsuario(false);
        }
    };

    return (
        <div className="space-y-4">

            {/* === BUSCADOR (trayecto) === */}
            <ProductSearchField
                serviceType="Traslado"
                value={textoBuscador}
                onChange={handleSearchChange}
                onSelectExisting={handleSelectExisting}
                onCreateNew={handleCreateNew}
                disabled={isEditing}
                label="Escribilo como te salga"
                placeholder="Ej: EZE al hotel julia tours 25000 pesos 12/9"
                aiCandidates={aiOverride?.candidates ?? null}
                aiCreateText={aiOverride?.createText ?? null}
                externalThinking={pensandoLineaInteligente}
            />

            {/* === RENGLÓN "Producto *" (Momento 3, §3.3 — mockup firmado) === */}
            {productoResueltoPorLineaInteligente && form.rateId && !form.newCatalogProduct && (
                <ResolvedProductRow
                    id="transfer-producto-resuelto"
                    label="Trayecto *"
                    value={form.routeName}
                    dataTestId="transfer-producto-resuelto"
                />
            )}

            {/* === RECUADRO PRODUCTO NUEVO === */}
            {form.newCatalogProduct && (
                <NewTransferBox
                    newProduct={form.newCatalogProduct}
                    onChange={(newProduct) => setForm((prev) => ({ ...prev, newCatalogProduct: newProduct }))}
                    suppliers={suppliers}
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
                            marcarTocado("supplierId");
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
                    {dudaLineaInteligente?.field === DOUBT_FIELD.SUPPLIER && (
                        <ServiceLineDoubtQuestion doubt={dudaLineaInteligente} onRespuesta={onRespuestaDuda} />
                    )}
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
                        className={camposSugeridos.pickupDate ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.pickupDate || ""}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, pickupDate: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, pickupDate: false }));
                            marcarTocado("pickupDate");
                        }}
                        data-testid="transfer-fecha"
                        aria-label="Fecha del traslado"
                    />
                    {dudaLineaInteligente?.field === DOUBT_FIELD.DATES && (
                        <ServiceLineDoubtQuestion doubt={dudaLineaInteligente} onRespuesta={onRespuestaDuda} />
                    )}
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
                        type="number"
                        min={1}
                        className={INPUT_NORMAL}
                        value={form.passengers || ""}
                        onChange={(event) => setForm((prev) => ({ ...prev, passengers: event.target.value }))}
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
                    onChange={(texto) => {
                        setForm((prev) => ({ ...prev, vehicleType: texto }));
                        setCamposSugeridos((prev) => ({ ...prev, vehicleType: false }));
                        marcarTocado("vehicleType");
                    }}
                    isSuggested={Boolean(camposSugeridos.vehicleType)}
                />
            </div>

            {/* === PRECIOS + MONEDA === */}
            <div className={`grid gap-3 ${canSeeCost ? "grid-cols-2 sm:grid-cols-3" : "grid-cols-2"}`}>
                {canSeeCost && (
                    <div>
                        <label className={LABEL_BASE} htmlFor="transfer-costo">Costo</label>
                        <input
                            id="transfer-costo"
                            type="number"
                            min={0}
                            step="0.01"
                            className={camposSugeridos.netCost ? INPUT_SUGERIDO : INPUT_NORMAL}
                            value={form.netCost || ""}
                            onChange={(event) => {
                                setForm((prev) => ({ ...prev, netCost: event.target.value }));
                                setCamposSugeridos((prev) => ({ ...prev, netCost: false }));
                                // Con permiso de costos, "costo" ES el campo que la variante
                                // sigue (campoPrecioVariante === "netCost").
                                setPrecioTocadoPorElUsuario(true);
                            }}
                            placeholder="0,00"
                            data-testid="transfer-costo"
                            aria-label="Costo del traslado"
                        />
                        {/* Renglón gris POR VEHÍCULO (spec 2026-08-07, §3.3): dice si el precio
                            es de este vehículo o de uno parecido (V9=A). */}
                        <VariantSuggestionHint text={hintVariante} />
                        {dudaLineaInteligente?.field === DOUBT_FIELD.PRICE && (
                            <ServiceLineDoubtQuestion doubt={dudaLineaInteligente} onRespuesta={onRespuestaDuda} />
                        )}
                    </div>
                )}
                <div>
                    <label className={LABEL_BASE} htmlFor="transfer-venta">Venta</label>
                    <input
                        id="transfer-venta"
                        type="number"
                        min={0}
                        step="0.01"
                        className={camposSugeridos.salePrice ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.salePrice || ""}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, salePrice: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, salePrice: false }));
                            // "Venta" solo es la variante rastreada para quien NO ve costos
                            // (campoPrecioVariante === "salePrice"); con permiso de costos
                            // es un campo aparte, ajeno a la sugerencia.
                            if (!canSeeCost) setPrecioTocadoPorElUsuario(true);
                        }}
                        placeholder="0,00"
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
