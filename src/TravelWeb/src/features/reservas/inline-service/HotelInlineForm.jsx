/**
 * Formulario de Hotel dentro de la ficha de carga en línea (ServiceInlineCard).
 *
 * Maneja los dos caminos definidos en el mockup y la guía UX de Gastón:
 *   - Producto existente: el usuario elige del buscador → operador + costo/venta
 *     se precargan EN AMARILLO (sugeridos, editables). Se guarda con rateId.
 *   - Producto nuevo: el usuario hace clic en "crear nuevo" → aparece un recuadro
 *     violeta con Nombre/Ciudad/Operador. Se guarda con newCatalogProduct.
 *
 * Campos a la vista SIEMPRE (sin revelado progresivo — decisión Gastón ronda 1):
 *   Buscador · Operador · Entrada · Salida · Noches (calculadas) · Habitaciones · Pasajeros
 *   Régimen · Tipo de habitación (OBLIGATORIOS — decisión Gastón 2026-06-06, fix bug 400)
 *   Costo por noche · Venta por noche · Moneda
 *
 * Total = noches × habitaciones × precio por noche (decisión Gastón 2026-06-06).
 *
 * Footer: "Venta $X · Ganás $Y  + Más detalles" | "Descartar" + "Guardar servicio"
 *
 * Detrás de "+ Más detalles" (plegado por defecto):
 *   Estrellas del hotel · Confirmación del operador · Cuotas / Valor por cuota · Dirección
 *   (Cuotas y Valor por cuota, obra "PDF completo" 2026-08-13 §8.3: plan de cuotas
 *   informativo para el PDF, no participa del cálculo de Venta total)
 *
 * Por qué Régimen y Habitación están a la vista y son obligatorios:
 *   CreateHotelRequest / UpdateHotelRequest exigen string RoomType y string MealPlan
 *   (NO nullables). Con null o vacío el backend responde 400. Los selects con default
 *   garantizan que siempre se envíe un valor válido. Decisión UX aprobada por Gastón.
 *
 * Permiso `cobranzas.see_cost`:
 *   - Con permiso: ve Costo por noche + ganancia en el footer.
 *   - Sin permiso: no ve costo ni ganancia (jamás mostrar "$0").
 *     El buscador le muestra el precio de VENTA de la última vez (salePrice).
 */

import { useState, useEffect } from "react";
import { Hotel, ChevronDown, ChevronUp, Calendar, Users } from "lucide-react";
import { hasPermission } from "../../../auth";
import { formatCurrency } from "../../../lib/utils";
import { MoneyInput } from "../../../components/ui/MoneyInput";
import { ProductSearchField } from "./ProductSearchField";
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

// D13 (spec 2026-08-10): campos de fecha de ESTE form, en el orden [desde, hasta] que
// espera aplicarInterpretacionComoSugerencia — Hotel tiene rango (entrada/salida).
const CAMPOS_FECHA_HOTEL = ["checkIn", "checkOut"];

// ─── Helpers de formato ──────────────────────────────────────────────────────

/**
 * Calcula la cantidad de noches entre checkIn y checkOut.
 * Devuelve 0 si alguna fecha falta o es inválida.
 */
function calcularNoches(checkIn, checkOut) {
    if (!checkIn || !checkOut) return 0;
    const inicio = new Date(checkIn);
    const fin = new Date(checkOut);
    const diferencia = Math.ceil((fin - inicio) / (1000 * 60 * 60 * 24));
    return diferencia > 0 ? diferencia : 0;
}

/**
 * Formatea un número como precio en la moneda del servicio.
 * ej: formatearPrecio(48000, "ARS") → "$48.000,00"
 *
 * Bug #26 (Tanda 4, 2026-07-24): antes SIEMPRE mostraba "$" con formato es-AR sin
 * mirar la moneda real del servicio — un hotel en USD mostraba "$1.200,00" (parecía
 * pesos) en vez de "US$1.200,00". Delegamos en formatCurrency() (lib/utils.js), el
 * formateador canónico del proyecto, pasándole SIEMPRE la moneda explícita: si no se
 * pasa, formatCurrency() cae a su default legacy (USD), que acá rompería el default
 * histórico de este formulario (ARS). currency = "ARS" solo si el llamador no la pasó.
 */
function formatearPrecio(valor, currency = "ARS") {
    return formatCurrency(valor, currency);
}

/**
 * Redondea un valor monetario a 2 decimales (mismo criterio que el backend y el modal viejo).
 */
function redondearDinero(valor) {
    return Math.round((Number(valor) || 0) * 100) / 100;
}

// ─── Clases CSS reutilizables ─────────────────────────────────────────────────

// Molde ".campo" de la maqueta firmada 2026-08-11 (docs/ux/2026-08-11-maqueta-reservas-
// firmada.html): inputs ~36px de alto, borde gris parejo, foco en el ÚNICO azul de acción
// del sistema (token `primary`, ya no un `blue-500` suelto). Estas 4 constantes se repiten
// IDÉNTICAS en los 5 formularios de la ficha de carga (Hotel/Aéreo/Traslado/Paquete/
// Asistencia) — cada uno las define localmente porque cada archivo se usa solo (no hay
// import cruzado entre ellos), pero conviene tocarlas TODAS igual si cambia el molde.
const INPUT_BASE = "w-full py-2 px-2.5 text-[13px] border rounded-[7px] bg-white text-slate-800 focus:outline-none focus:ring-1 focus:border-primary focus:ring-primary disabled:bg-slate-50 disabled:text-slate-400 dark:bg-slate-900 dark:text-slate-100 dark:disabled:bg-slate-800/60 dark:disabled:text-slate-500";
const INPUT_NORMAL = `${INPUT_BASE} border-slate-300 dark:border-slate-600`;
// Amarillo: campo precargado como sugerencia (editable) — mockup estilo .sugerido. El color
// en modo claro NO se toca (decisión 2026-08-11, ítem 6: "sugerido amarillo... como ya
// está") — sólo se agrega la versión oscura, que antes no existía (hallazgo B, "cero reglas
// dark" en toda la carpeta inline-service/).
const INPUT_SUGERIDO = `${INPUT_BASE} border-yellow-400 bg-yellow-50 dark:border-amber-600/70 dark:bg-amber-900/25 dark:text-amber-100`;
// Calculado: solo lectura con estilo gris punteado — mockup estilo .calc
const INPUT_CALCULADO = `${INPUT_BASE} border-slate-300 border-dashed bg-slate-50 text-slate-600 font-semibold cursor-default dark:border-slate-600 dark:bg-slate-800/60 dark:text-slate-300`;
// Label 11px (molde .campo de la maqueta): antes 12px (text-xs).
const LABEL_BASE = "block text-[11px] font-semibold tracking-wide text-slate-500 mb-1 dark:text-slate-400";

// ─── Componente NewHotelBox ───────────────────────────────────────────────────

/**
 * Recuadro violeta que aparece cuando el usuario elige "crear nuevo hotel".
 * Campos: Nombre · Ciudad/destino (OBLIGATORIA) · Operador.
 * La Ciudad es el arma principal contra duplicados (guía UX).
 *
 * `supplierSugerido` (D13-bis, spec 2026-08-10): true cuando el Operador se precargó
 * solo desde la frase completa tipeada en el buscador ("... con Delfos") — pinta el
 * select en amarillo, igual que cualquier otra sugerencia editable de la ficha.
 * `onSupplierTouched` avisa al padre que el vendedor lo tocó a mano (saca el amarillo).
 */
function NewHotelBox({ newProduct, onChange, suppliers, supplierSugerido, onSupplierTouched }) {
    return (
        <div className="border border-dashed border-violet-400 bg-violet-50 rounded-[14px] p-4 mb-4 dark:border-violet-700 dark:bg-violet-950/20">
            <div className="flex items-center gap-2 mb-3">
                <Hotel className="w-4 h-4 text-violet-600 dark:text-violet-400" />
                <span className="text-sm font-semibold text-violet-700 dark:text-violet-300">
                    Hotel nuevo — se guarda en tu tarifario al confirmar
                </span>
                <span className="text-[11px] font-semibold px-2 py-0.5 rounded-full bg-violet-200 text-violet-700 dark:bg-violet-900/50 dark:text-violet-300">
                    Creado en venta
                </span>
            </div>
            <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
                <div>
                    <label className={LABEL_BASE}>Nombre *</label>
                    <input
                        type="text"
                        className={INPUT_NORMAL}
                        value={newProduct.name || ""}
                        onChange={(event) => onChange({ ...newProduct, name: event.target.value })}
                        placeholder="Nombre del hotel"
                        required
                        data-testid="new-hotel-name"
                        aria-label="Nombre del hotel nuevo"
                    />
                </div>
                <div>
                    {/* Ciudad OBLIGATORIA — es el arma principal contra duplicados (guía UX D6) */}
                    <label className={LABEL_BASE}>Ciudad / destino *</label>
                    <input
                        type="text"
                        className={INPUT_NORMAL}
                        value={newProduct.city || ""}
                        onChange={(event) => onChange({ ...newProduct, city: event.target.value })}
                        placeholder="Ciudad (ej: Posadas)"
                        required
                        data-testid="new-hotel-city"
                        aria-label="Ciudad del hotel nuevo"
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
                        data-testid="new-hotel-supplier"
                        aria-label="Operador del hotel nuevo"
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

// ─── Componente principal HotelInlineForm ─────────────────────────────────────

export function HotelInlineForm({
    reservaId,
    form,
    setForm,
    suppliers,
    isEditing,
    // Salto de solapa (spec 2026-08-10, D1..D13) — ver ServiceInlineCard.jsx.
    onSelectOtherType,
    seleccionPendiente,
    onConsumirSeleccionPendiente,
    // Fix #4 (auditoría 2026-08-10): antes vivían acá como useState local — un remount
    // (cambio de solapa) los reseteaba y perdía si un campo seguía siendo "sugerencia
    // reemplazable" o no. Ahora los levanta ServiceInlineCard junto con `form`.
    camposSugeridos,
    setCamposSugeridos,
    precioTocadoPorElUsuario,
    setPrecioTocadoPorElUsuario,
    monedaTocadaPorElUsuario,
    setMonedaTocadaPorElUsuario,
    // Fix regresión #1+#6 (re-review 2026-08-10): APARTE de camposSugeridos (el
    // amarillo) — dice qué campo tocó el vendedor a mano de verdad. Ver el comentario
    // largo en ServiceInlineCard.jsx donde se declara.
    camposTocadosAMano,
    setCamposTocadosAMano,
}) {
    const canSeeCost = hasPermission("cobranzas.see_cost");

    // Noches calculadas automáticamente a partir de las fechas (guía UX: "le gusta el conteo")
    const noches = calcularNoches(form.checkIn, form.checkOut);

    // Habitaciones: mínimo 1 (el campo nunca puede quedar en 0 o vacío para el cálculo)
    const habitaciones = Math.max(Number(form.rooms) || 1, 1);

    // Total = noches × habitaciones × precio por noche (decisión Gastón 2026-06-06:
    // el tarifario guarda el precio de UNA habitación UNA noche, y el sistema multiplica).
    const factorTotal = Math.max(noches, 0) * habitaciones;
    const ventaTotal = redondearDinero((Number(form.unitSalePrice) || 0) * factorTotal);
    const costoTotal = canSeeCost ? redondearDinero((Number(form.unitNetCost) || 0) * factorTotal) : null;
    const ganancia = canSeeCost && costoTotal !== null ? redondearDinero(ventaTotal - costoTotal) : null;

    // "Más detalles" plegado por defecto. Se abre automáticamente si ya hay datos
    // (por ejemplo, al editar un hotel que ya tiene confirmación o dirección cargada).
    // Régimen y Tipo de habitación ya NO están aquí: subieron a la vista principal.
    // operatorPaymentDeadline NO se chequea: el campo fue eliminado en F2 y siempre es undefined.
    // starRating (spec 2026-08-12, §2): dato del PDF de presupuesto, opcional — también cuenta
    // para abrir la sección sola al editar un hotel que ya lo tiene cargado.
    // installmentsCount/installmentAmount (spec 2026-08-13, §8.3): plan de cuotas, mismo criterio.
    const tieneDetallesExistentes = Boolean(
        form.confirmationNumber || form.address || form.starRating ||
        form.installmentsCount || form.installmentAmount
    );
    const [mostrarDetalles, setMostrarDetalles] = useState(tieneDetallesExistentes || isEditing);

    // `camposSugeridos` (qué campos siguen "en amarillo") llega por prop desde
    // ServiceInlineCard (fix #4, auditoría 2026-08-10) — cuando el usuario elige un
    // hotel existente del buscador, `handleSelectExisting` más abajo precarga operador
    // y precio de la última venta EN AMARILLO (sugeridos, editables — mockup Momento 3).

    // ─── Sugerencia POR HABITACIÓN (spec 2026-08-07, §3.3 / M-15 / V9=A / V10=A) ───────
    // El campo de precio que este usuario ve y edita: costo para quien tiene permiso de
    // verlo, venta para el resto (misma regla que F-14 en el resto de la pantalla).
    const campoPrecioVariante = canSeeCost ? "unitNetCost" : "unitSalePrice";

    // Se re-consulta cada vez que cambia el producto elegido O la combinación que define
    // la variante (habitación, régimen, nombre fino) — es EXACTAMENTE lo que dispara el
    // "se acomoda sola" de V10=A.
    const { suggestion: sugerenciaVariante } = useVariantPriceSuggestion({
        ratePublicId: form.rateId,
        supplierId: form.supplierId,
        roomType: form.roomType,
        mealPlan: form.mealPlan,
        roomCategory: form.roomCategory,
    });
    const [hintVariante, setHintVariante] = useState(null);

    // Flags EXPLÍCITOS de "el vendedor escribió acá a mano" (fix ronda 2 de review —
    // hallazgos #4 y #5). Antes se derivaban de `camposSugeridos`, que también da "no
    // sugerido" para un casillero vacío que NUNCA se tocó (bloqueaba la precarga amarilla
    // de V9=A) y no distinguía precio de moneda como territorios separados (elegir la
    // moneda a mano se pisaba solo con volver a acomodar el precio, V10=A).
    //
    // Fix ronda 3 (BLOQUEANTE): en modo edición arrancan en `true`, NO en `false`. El
    // precio/moneda que trae un servicio YA GUARDADO no es una sugerencia del sistema —
    // es un dato del vendedor (de esta sesión o de una anterior), y por V10=A "lo que
    // escribiste vos no se toca nunca". Sin este seed, el efecto de abajo corría en el
    // MONTAJE con `sugerenciaVariante` todavía en null (la consulta real recién se acaba
    // de disparar y no resolvió) y lo interpretaba como "no hay precio para esta
    // habitación", BORRANDO el costo/venta que `buildHotelFormInitial` ya había cargado.
    // Solo se apagan de nuevo al elegir OTRO producto (nuevo contexto, nueva decisión —
    // ver handleSelectExisting/handleCreateNew/handleSearchChange más abajo); un cambio
    // de habitación DURANTE la edición no las reactiva a propósito — mover el precio de
    // un servicio ya guardado sin que el vendedor lo pida sigue siendo territorio
    // prohibido, edite lo que edite después.
    // (Fix #4, auditoría 2026-08-10: estos dos flags llegan por prop — el seed en
    // `isEditing` ahora lo hace ServiceInlineCard, una sola vez, al levantar el estado.)

    // useEffect con dependencia en `sugerenciaVariante`: corre cada vez que llega una
    // respuesta nueva del hook (que ya viene debounced). NO se agregan los flags de
    // "tocado" ni campoPrecioVariante a las deps a propósito (eslint-disable): si el
    // usuario tipea en OTRO campo mientras tanto, no queremos relanzar este efecto — solo
    // nos importa reaccionar cuando cambia la sugerencia en sí.
    useEffect(() => {
        if (!form.rateId) {
            // Fix ronda 3: sin producto elegido todavía no hay NADA que sugerir ni que
            // limpiar. Cortamos acá para no pintar de amarillo (más abajo, vía
            // camposSugeridos) un casillero vacío que nadie sugirió — pasaba en TODA
            // ficha de servicio nueva, apenas se montaba, antes de elegir nada.
            setHintVariante(null);
            return;
        }
        const resultado = resolverCamposAlCambiarVariante({
            estaPrecioTocado: precioTocadoPorElUsuario,
            estaMonedaTocada: monedaTocadaPorElUsuario,
            suggestion: sugerenciaVariante,
            // Fix #8 (auditoría 2026-08-10): el valor ACTUAL del campo — así la función
            // pura sabe si hay algo con valor que no se puede vaciar (ej: el precio que
            // `handleSelectExisting` acaba de precargar de la venta real).
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
            // Sigue siendo territorio del sistema (recién se acomodó solo): la próxima vez
            // que cambie la habitación, se puede volver a acomodar.
            setCamposSugeridos((prev) => ({ ...prev, [campoPrecioVariante]: true }));
        }
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [sugerenciaVariante]);

    // C5: si el operador sugerido (de la última venta del buscador) NO está en la lista
    // de operadores de la reserva, lo agregamos como opción dinámica para que el <select>
    // no quede en amarillo con ninguna fila seleccionable.
    // Solo aplicamos cuando el campo fue marcado como sugerido (camino "eligió del buscador").
    const supplierListaIds = new Set(suppliers.map((s) => s.publicId || s.PublicId));
    const supplierSugeridoNoEstaEnLista =
        camposSugeridos.supplierId &&
        form.supplierId &&
        !supplierListaIds.has(form.supplierId);
    const suppliersFull = supplierSugeridoNoEstaEnLista
        ? [
              {
                  publicId: form.supplierId,
                  // supplierName viene del resultado del buscador si el backend lo incluye.
                  // Sin supplierName no mostramos el ID interno recortado (eso es un dato
                  // técnico que un usuario no programador no puede leer) — mejor un texto
                  // genérico que igual identifica que hay un operador sugerido (2026-08-03).
                  name: form.supplierName || "Operador sugerido",
              },
              ...suppliers,
          ]
        : suppliers;

    const handleSelectExisting = (catalogResult, interpretacion) => {
        // Tomamos la sugerencia del lastSale (venta real) o del rateFallback (campos del Rate)
        const sale = catalogResult.lastSale || catalogResult.rateFallback || {};

        // Regla transversal (auditoría 2026-08-10, #1): un campo se reemplaza si está
        // vacío O si NO fue tocado a mano por el vendedor (`camposTocadosAMano` de ANTES
        // de esta selección) — nunca si lo tipeó/eligió a mano. Vale igual en la misma
        // solapa que tras un salto (D3): ya no hay distinción entre "camino normal" y
        // "pendiente". Fix regresión #1+#6: la señal es `camposTocadosAMano`, NO
        // `camposSugeridos` (ese es solo el amarillo, se apaga con cualquier tecleo del
        // buscador — ver el comentario largo en ServiceInlineCard.jsx).
        const { patch: patchVenta, sugeridos: sugeridosVenta } = resolverPatchDeVentaDelCatalogo({
            sale,
            canSeeCost,
            formActual: form,
            camposTocadosAMano,
            campoVenta: "unitSalePrice",
            campoCosto: "unitNetCost",
        });

        // Fix C-5(a) (review 2026-08-10): la precarga de la frase (D13) tampoco pisa un
        // campo que el vendedor ya tenía cargado a mano — ni la venta real (chequeado
        // acá con Boolean(sale.supplierPublicId)) ni lo que ya estaba tocado a mano.
        const { patch: patchFrase, sugeridos: sugeridosFrase } = aplicarInterpretacionComoSugerencia(
            interpretacion,
            { yaHaySupplierDeLaVenta: Boolean(sale.supplierPublicId), camposFecha: CAMPOS_FECHA_HOTEL, formActual: form, camposTocadosAMano }
        );

        setForm((prev) => ({
            ...prev,
            // D13: el casillero se queda con el nombre LIMPIO del producto elegido, nunca
            // con la frase entera que haya tipeado el vendedor.
            hotelName: resolverNombreEnCasillero(catalogResult, prev.hotelName),
            city: catalogResult.subtitle || prev.city,
            rateId: catalogResult.ratePublicId,
            // Limpiamos el modo "producto nuevo" porque ahora el usuario eligió uno existente
            newCatalogProduct: null,
            ...patchVenta,
            ...patchFrase,
        }));

        // Marcamos los campos que vinieron como sugerencia para pintar el fondo amarillo.
        // Arrancamos TODO en false (default explícito, no implícito): en el camino de
        // selección pendiente, `sugeridosVenta`/`sugeridosFrase` pueden no traer todas
        // las claves (los campos que NO se escribieron por estar ya cargados a mano).
        setCamposSugeridos({
            supplierId: false,
            unitNetCost: false,
            unitSalePrice: false,
            currency: false,
            checkIn: false,
            checkOut: false,
            ...sugeridosVenta,
            ...sugeridosFrase,
        });
        // Fix residual (re-review 2026-08-10, ítem A): estos dos flags NO se apagan a
        // ciegas. Si el vendedor ya había tocado a mano el campo que sigue la variante
        // (`campoPrecioVariante`) ANTES de elegir este producto, ese toque tiene que
        // sobrevivir — si no, 300ms después la sugerencia por habitación (el useEffect de
        // arriba, disparado por el rateId nuevo) lo pisa igual, aunque `camposTocadosAMano`
        // ya lo tuviera protegido. Sembramos desde el mapa persistente, no desde `false` fijo.
        setPrecioTocadoPorElUsuario(camposTocadosAMano[campoPrecioVariante] === true);
        setMonedaTocadaPorElUsuario(camposTocadosAMano.currency === true);
        // El renglón gris de abajo ya NO sale de acá: lo arma la sugerencia POR HABITACIÓN
        // (useVariantPriceSuggestion), que se dispara sola apenas rateId queda seteado.
    };

    // Salto de solapa (D3/D7, spec 2026-08-10): si el vendedor eligió, desde OTRA solapa,
    // un hotel en el buscador, la pendiente llega acá y se aplica exactamente como si se
    // hubiera elegido del propio buscador de Hotel.
    useSeleccionPendienteDelTipo({
        seleccionPendiente,
        serviceType: "Hotel",
        onSeleccionar: handleSelectExisting,
        onConsumida: onConsumirSeleccionPendiente,
    });

    const handleCreateNew = (searchText, interpretacion) => {
        // Bug #28 (Tanda 4, 2026-07-24): antes esto borraba operador/costo/venta/moneda
        // SIEMPRE, aunque el usuario los hubiera tipeado a mano. Ahora solo se limpian los
        // campos que el vendedor NUNCA tocó a mano (fix regresión #1+#6: la señal es
        // `camposTocadosAMano`, no `camposSugeridos` — ver resolverCamposALimpiarAlCrearNuevo).
        const camposLimpios = resolverCamposALimpiarAlCrearNuevo(
            { supplierId: form.supplierId, unitNetCost: form.unitNetCost, unitSalePrice: form.unitSalePrice, currency: form.currency },
            camposTocadosAMano,
            { supplierId: "", unitNetCost: "", unitSalePrice: "", currency: "ARS" }
        );

        // D13-bis (spec 2026-08-10, fix "crear nuevo pelado"): las fechas de la frase
        // completa también se aplican acá — misma función y mismas guardas que
        // handleSelectExisting (nunca pisa un campo que el vendedor ya tipeó a mano).
        // yaHaySupplierDeLaVenta:true a propósito: en "crear nuevo" no hay `sale`, y el
        // operador NO va al campo genérico `supplierId` (queda oculto mientras se crea
        // un producto nuevo) — va aparte, al operador del recuadro violeta, ver abajo.
        const { patch: patchFechas, sugeridos: sugeridosFechas } = aplicarInterpretacionComoSugerencia(
            interpretacion,
            { yaHaySupplierDeLaVenta: true, camposFecha: CAMPOS_FECHA_HOTEL, formActual: form, camposTocadosAMano }
        );

        // El operador del recuadro "hotel nuevo" arranca SIEMPRE vacío (recién se está
        // armando ahora mismo) — si la frase trajo un operador REAL (matcheado por el
        // motor entre los proveedores de la agencia; si no matcheó, no se inventa nada),
        // se precarga ahí, editable.
        const supplierSugeridoDelNuevo = resolverOperadorSugeridoParaProductoNuevo(interpretacion);

        setForm((prev) => ({
            ...prev,
            hotelName: searchText,
            // Fix #5 (auditoría 2026-08-10): `city` es un dato de la IDENTIDAD del hotel
            // EXISTENTE que se estaba mirando (lo llena handleSelectExisting desde el
            // subtitle del resultado) — al crear un hotel nuevo, esa ciudad vieja queda
            // huérfana y no corresponde. La ciudad del producto NUEVO se carga aparte,
            // en el recuadro violeta (newCatalogProduct.city), a mano.
            city: "",
            // Limpiamos el rateId porque ahora vamos al path "producto nuevo"
            rateId: null,
            newCatalogProduct: {
                name: searchText,
                city: "",
                supplierPublicId: supplierSugeridoDelNuevo,
            },
            ...camposLimpios,
            ...patchFechas,
        }));
        // Los campos que quedaron limpios dejan de ser "sugeridos"; los preservados ya
        // estaban en false (si no, se habrían limpiado), así que todo queda en false.
        // `supplierId` se reusa acá para el amarillo del Operador del recuadro violeta
        // (D13-bis): los dos campos nunca conviven en pantalla (uno se oculta cuando el
        // otro se muestra), así que no hay conflicto en compartir la misma bandera.
        setCamposSugeridos({
            supplierId: Boolean(supplierSugeridoDelNuevo),
            unitNetCost: false,
            unitSalePrice: false,
            currency: false,
            checkIn: false,
            checkOut: false,
            ...sugeridosFechas,
        });
        // Mismo criterio que en handleSelectExisting (fix residual, ítem A): sembrar desde
        // el mapa persistente, no apagar a ciegas. En "crear nuevo" no hay rateId todavía,
        // así que la sugerencia por habitación no se dispara — pero si el vendedor vuelve
        // a elegir un producto EXISTENTE después sin haber tocado nada nuevo, el flag debe
        // seguir reflejando lo que ya estaba protegido.
        setPrecioTocadoPorElUsuario(camposTocadosAMano[campoPrecioVariante] === true);
        setMonedaTocadaPorElUsuario(camposTocadosAMano.currency === true);
        // "Crear nuevo" no tiene rateId: la sugerencia por habitación se apaga sola (el hook
        // no consulta sin producto elegido).
    };

    // Cuando el usuario escribe en el buscador después de haber elegido un producto,
    // limpiamos el rateId (C4): si no lo hacemos, el payload llevaría el id del hotel
    // viejo mientras el texto del input ya apunta a otro nombre.
    // También limpiamos el producto nuevo si borra todo el texto.
    const handleSearchChange = (texto) => {
        setForm((prev) => ({
            ...prev,
            hotelName: texto,
            // Siempre limpiamos el rateId al tipear: el usuario tiene que volver a elegir
            // del dropdown para que el producto quede vinculado de nuevo.
            rateId: null,
            // city (fix #5) es un dato de la IDENTIDAD del hotel que se estaba mirando —
            // editar el nombre desvincula el producto, así que la ciudad vieja también
            // queda huérfana. Se recupera al elegir de nuevo (handleSelectExisting).
            city: "",
            newCatalogProduct: texto ? prev.newCatalogProduct : null,
        }));
        // Fix #6 (auditoría 2026-08-10): CUALQUIER tecleo desvincula el rateId — los
        // amarillos que quedaban pintados ya no corresponden a ninguna selección viva
        // (el VALOR se queda tal cual, deja de ser "sugerencia"). Antes esto solo pasaba
        // si el vendedor borraba TODO el texto; una letra de más dejaba operador/precio/
        // fechas pintados de amarillo como si siguieran siendo sugerencia del producto
        // que ya no está vinculado.
        setCamposSugeridos({ supplierId: false, unitNetCost: false, unitSalePrice: false, currency: false, checkIn: false, checkOut: false });
        // Fix REGRESIÓN #1+#6 (re-review 2026-08-10): `precioTocadoPorElUsuario`/
        // `monedaTocadaPorElUsuario` (guardan de la sugerencia POR HABITACIÓN) NO se
        // tocan acá — antes se apagaban en cada tecleo del buscador por error, dejando
        // que esa sugerencia pisara un precio que el vendedor acababa de tocar a mano.
        // Solo los apaga elegir/crear un producto (como siempre fue) o el onChange del
        // propio campo de precio/moneda (los prende).
        // `camposTocadosAMano` TAMPOCO se toca acá — tipear en el buscador no es "tocar
        // a mano" ningún campo puntual (esa es justamente la separación del fix #1+#6).
    };

    return (
        <div className="space-y-4">

            {/* === BUSCADOR (primer campo — mockup Momento 1) === */}
            <ProductSearchField
                reservaId={reservaId}
                serviceType="Hotel"
                value={form.hotelName || ""}
                onChange={handleSearchChange}
                onSelectExisting={handleSelectExisting}
                onSelectOtherType={onSelectOtherType}
                onCreateNew={handleCreateNew}
                disabled={isEditing} // Al editar, el producto no cambia (solo los datos del servicio)
                esEdicion={isEditing}
                rateId={form.rateId}
                supplierIdElegido={form.supplierId}
                label="Hotel"
                placeholder="Escribí el nombre del hotel..."
            />

            {/* === RECUADRO DE HOTEL NUEVO (solo aparece si el usuario elige "crear nuevo") === */}
            {form.newCatalogProduct && (
                <NewHotelBox
                    newProduct={form.newCatalogProduct}
                    onChange={(newProduct) => setForm((prev) => ({ ...prev, newCatalogProduct: newProduct }))}
                    suppliers={suppliers}
                    supplierSugerido={camposSugeridos.supplierId}
                    onSupplierTouched={() => setCamposSugeridos((prev) => ({ ...prev, supplierId: false }))}
                />
            )}

            {/* === OPERADOR (campo aparte del buscador; amarillo si fue sugerido) === */}
            {!form.newCatalogProduct && (
                <div>
                    <label className={LABEL_BASE} htmlFor="hotel-operador">Operador</label>
                    <select
                        id="hotel-operador"
                        className={camposSugeridos.supplierId ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.supplierId || ""}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, supplierId: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, supplierId: false }));
                            // Fix regresión #1+#6: el vendedor tocó ESTE campo a mano de
                            // verdad — queda protegido hasta que cambie el contexto.
                            setCamposTocadosAMano((prev) => ({ ...prev, supplierId: true }));
                        }}
                        data-testid="hotel-supplier"
                        aria-label="Operador del hotel"
                    >
                        <option value="">Seleccioná un operador...</option>
                        {/* suppliersFull incluye el sugerido si no estaba en la lista original (C5) */}
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

            {/* === FECHAS + NOCHES + HABITACIONES + PASAJEROS (segunda fila) === */}
            {/* 5 columnas: Entrada · Salida · Noches (calc) · Habitaciones · Pasajeros */}
            <div className="grid grid-cols-2 sm:grid-cols-5 gap-3">
                <div>
                    <label className={LABEL_BASE} htmlFor="hotel-checkin">
                        <Calendar className="inline w-3 h-3 mr-1" />
                        Entrada
                    </label>
                    <input
                        id="hotel-checkin"
                        type="date"
                        // Amarillo (D13, spec 2026-08-10) cuando la fecha salió de la frase
                        // tipeada en el buscador — tocarla a mano la saca del amarillo.
                        className={camposSugeridos.checkIn ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.checkIn || ""}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, checkIn: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, checkIn: false }));
                            setCamposTocadosAMano((prev) => ({ ...prev, checkIn: true }));
                        }}
                        data-testid="hotel-checkin"
                        aria-label="Fecha de entrada"
                    />
                </div>
                <div>
                    <label className={LABEL_BASE} htmlFor="hotel-checkout">
                        <Calendar className="inline w-3 h-3 mr-1" />
                        Salida
                    </label>
                    <input
                        id="hotel-checkout"
                        type="date"
                        className={camposSugeridos.checkOut ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.checkOut || ""}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, checkOut: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, checkOut: false }));
                            setCamposTocadosAMano((prev) => ({ ...prev, checkOut: true }));
                        }}
                        min={form.checkIn || undefined}
                        data-testid="hotel-checkout"
                        aria-label="Fecha de salida"
                    />
                </div>
                <div>
                    <label className={LABEL_BASE}>Noches</label>
                    {/* Calculado automáticamente — solo lectura (mockup estilo .calc) */}
                    <input
                        type="text"
                        className={INPUT_CALCULADO}
                        value={noches > 0 ? noches : "—"}
                        readOnly
                        tabIndex={-1}
                        aria-label={`Cantidad de noches: ${noches}`}
                        data-testid="hotel-noches"
                    />
                </div>
                <div>
                    {/* Habitaciones: default 1, mínimo 1. Afecta el total (noches × hab × precio/noche) */}
                    <label className={LABEL_BASE} htmlFor="hotel-habitaciones">Habitaciones</label>
                    <input
                        id="hotel-habitaciones"
                        type="text"
                        inputMode="numeric"
                        className={INPUT_NORMAL}
                        value={form.rooms || ""}
                        // Bug 2 (QA 11/08/2026): el min={1} de un <input type="number"> nativo
                        // es solo decorativo — el navegador igual deja tipear "-1". Filtramos a
                        // mano: nunca deja pasar un signo "-" ni una coma/punto (se cuenta en
                        // enteros). validarForm() en ServiceInlineCard.jsx es la última red,
                        // por si igual llega un valor viejo/pegado con el pegado del mouse.
                        onChange={(event) => setForm((prev) => ({ ...prev, rooms: sanitizarCantidadPositiva(event.target.value) }))}
                        placeholder="1"
                        data-testid="hotel-habitaciones"
                        aria-label="Cantidad de habitaciones"
                    />
                </div>
                <div>
                    <label className={LABEL_BASE} htmlFor="hotel-pasajeros">
                        <Users className="inline w-3 h-3 mr-1" />
                        Pasajeros
                    </label>
                    <input
                        id="hotel-pasajeros"
                        type="text"
                        inputMode="numeric"
                        className={INPUT_NORMAL}
                        value={form.passengers || ""}
                        onChange={(event) => setForm((prev) => ({ ...prev, passengers: sanitizarCantidadPositiva(event.target.value) }))}
                        placeholder="1"
                        data-testid="hotel-pasajeros"
                        aria-label="Cantidad de pasajeros"
                    />
                </div>
            </div>

            {/* === RÉGIMEN + TIPO DE HABITACIÓN + CATEGORÍA (obligatorios los dos primeros) === */}
            {/* Razón: CreateHotelRequest/UpdateHotelRequest tienen RoomType y MealPlan como
                string no-nullable. Con null o vacío el backend responde 400. Los selects con
                default garantizan que SIEMPRE se envíe un valor válido. Decisión Gastón 2026-06-06.
                Categoría (roomCategory) es OPCIONAL: es el nombre fino ("Superior", "Vista al
                mar") que junto con Régimen y Tipo arma la variante que el tarifario recuerda
                por separado (spec 2026-08-07, §5.2 — texto libre CON MEMORIA). */}
            <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
                <div>
                    <label className={LABEL_BASE} htmlFor="hotel-regimen">
                        Régimen *
                    </label>
                    <select
                        id="hotel-regimen"
                        className={INPUT_NORMAL}
                        value={form.mealPlan || "Desayuno"}
                        onChange={(event) => setForm((prev) => ({ ...prev, mealPlan: event.target.value }))}
                        required
                        data-testid="inline-hotel-meal-plan"
                        aria-label="Régimen de comidas del hotel"
                    >
                        <option value="Solo Alojamiento">Solo alojamiento</option>
                        <option value="Desayuno">Desayuno</option>
                        <option value="Media Pension">Media pensión</option>
                        <option value="Pension Completa">Pensión completa</option>
                        <option value="All Inclusive">All inclusive</option>
                    </select>
                </div>
                <div>
                    <label className={LABEL_BASE} htmlFor="hotel-tipo-habitacion">
                        Tipo de habitación *
                    </label>
                    <select
                        id="hotel-tipo-habitacion"
                        className={INPUT_NORMAL}
                        value={form.roomType || "Doble"}
                        onChange={(event) => setForm((prev) => ({ ...prev, roomType: event.target.value }))}
                        required
                        data-testid="inline-hotel-room-type"
                        aria-label="Tipo de habitación del hotel"
                    >
                        <option value="Single">Single</option>
                        <option value="Doble">Doble</option>
                        <option value="Twin">Twin</option>
                        <option value="Triple">Triple</option>
                        <option value="Cuadruple">Cuádruple</option>
                        <option value="Familiar">Familiar</option>
                        <option value="Suite">Suite</option>
                    </select>
                </div>
                <div>
                    <FreeTextWithMemoryField
                        id="hotel-categoria"
                        serviceType="Hotel"
                        label="Categoría"
                        placeholder="Ej: Superior, Vista al mar"
                        value={form.roomCategory}
                        onChange={(texto) => setForm((prev) => ({ ...prev, roomCategory: texto }))}
                    />
                </div>
            </div>

            {/* === PRECIOS + MONEDA (tercera fila) === */}
            <div className={`grid gap-3 ${canSeeCost ? "grid-cols-2 sm:grid-cols-3" : "grid-cols-2"}`}>
                {/* Costo por noche: solo visible para quien tiene permiso de ver costos */}
                {canSeeCost && (
                    <div>
                        <label className={LABEL_BASE} htmlFor="hotel-costo-noche">Costo por noche</label>
                        <MoneyInput
                            id="hotel-costo-noche"
                            className={camposSugeridos.unitNetCost ? INPUT_SUGERIDO : INPUT_NORMAL}
                            value={form.unitNetCost || ""}
                            onChange={(nuevoValor) => {
                                setForm((prev) => ({ ...prev, unitNetCost: nuevoValor }));
                                setCamposSugeridos((prev) => ({ ...prev, unitNetCost: false }));
                                setCamposTocadosAMano((prev) => ({ ...prev, unitNetCost: true }));
                                // Con permiso de costos, "costo" ES el campo que la variante
                                // sigue (campoPrecioVariante === "unitNetCost") — tocarlo a
                                // mano lo saca del territorio del sistema para siempre.
                                setPrecioTocadoPorElUsuario(true);
                            }}
                            data-testid="hotel-costo-noche"
                            aria-label="Costo por noche"
                        />
                        {/* Renglón gris POR HABITACIÓN (spec 2026-08-07, §3.3): reemplaza al
                            genérico "Último precio" de la venta del producto — este SÍ sabe
                            si el precio es de esta habitación o de una parecida (V9=A). */}
                        <VariantSuggestionHint text={hintVariante} />
                    </div>
                )}
                <div>
                    <label className={LABEL_BASE} htmlFor="hotel-venta-noche">Venta por noche</label>
                    <MoneyInput
                        id="hotel-venta-noche"
                        className={camposSugeridos.unitSalePrice ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.unitSalePrice || ""}
                        onChange={(nuevoValor) => {
                            setForm((prev) => ({ ...prev, unitSalePrice: nuevoValor }));
                            setCamposSugeridos((prev) => ({ ...prev, unitSalePrice: false }));
                            setCamposTocadosAMano((prev) => ({ ...prev, unitSalePrice: true }));
                            // "Venta" solo es la variante rastreada por el sistema para quien
                            // NO ve costos (campoPrecioVariante === "unitSalePrice"); con
                            // permiso de costos es un campo aparte, ajeno a la sugerencia.
                            if (!canSeeCost) setPrecioTocadoPorElUsuario(true);
                        }}
                        required
                        data-testid="hotel-venta-noche"
                        aria-label="Precio de venta por noche"
                    />
                    {/* Sin permiso de costos, el renglón gris POR HABITACIÓN va acá (no hay campo de costo a la vista) */}
                    {!canSeeCost && <VariantSuggestionHint text={hintVariante} />}
                </div>
                <div>
                    <label className={LABEL_BASE} htmlFor="hotel-moneda">Moneda</label>
                    <select
                        id="hotel-moneda"
                        className={camposSugeridos.currency ? INPUT_SUGERIDO : INPUT_NORMAL}
                        value={form.currency || "ARS"}
                        onChange={(event) => {
                            setForm((prev) => ({ ...prev, currency: event.target.value }));
                            setCamposSugeridos((prev) => ({ ...prev, currency: false }));
                            setCamposTocadosAMano((prev) => ({ ...prev, currency: true }));
                            setMonedaTocadaPorElUsuario(true);
                        }}
                        data-testid="hotel-moneda"
                        aria-label="Moneda"
                    >
                        <option value="ARS">ARS (pesos)</option>
                        <option value="USD">USD (dólares)</option>
                    </select>
                </div>
            </div>

            {/* === MÁS DETALLES (plegado por defecto — sin cartelitos ni "(opcional)") === */}
            <div>
                <button
                    type="button"
                    onClick={() => setMostrarDetalles((prev) => !prev)}
                    className="flex items-center gap-1 text-sm font-semibold text-primary hover:opacity-80 transition-colors dark:text-primary"
                    data-testid="hotel-mas-detalles-toggle"
                    aria-expanded={mostrarDetalles}
                >
                    {mostrarDetalles ? <ChevronUp className="w-4 h-4" /> : <ChevronDown className="w-4 h-4" />}
                    {mostrarDetalles ? "Menos detalles" : "+ Más detalles"}
                </button>

                {mostrarDetalles && (
                    <div className="mt-3 grid grid-cols-1 sm:grid-cols-2 gap-3">
                        {/* Estrellas del hotel (spec 2026-08-12, §2): dato descriptivo del PDF de
                            presupuesto, va PRIMERO acá adentro — no es un dato operativo como
                            Confirmación/Dirección. OJO: esto es DISTINTO de "Categoría" (roomCategory,
                            a la vista arriba) — Categoría es el nombre fino de la habitación
                            ("Superior", "Vista al mar"), Estrellas es del hotel en sí. Opcional,
                            nunca se valida al guardar (si queda "Sin especificar" el PDF no imprime
                            la línea — es un espejo de lo cargado, nunca inventa un dato). */}
                        <div>
                            <label className={LABEL_BASE} htmlFor="hotel-estrellas">Estrellas del hotel</label>
                            <select
                                id="hotel-estrellas"
                                className={INPUT_NORMAL}
                                value={form.starRating || ""}
                                onChange={(event) => setForm((prev) => ({ ...prev, starRating: event.target.value }))}
                                data-testid="hotel-estrellas"
                                aria-label="Estrellas del hotel"
                            >
                                <option value="">Sin especificar</option>
                                <option value="1">1 estrella</option>
                                <option value="2">2 estrellas</option>
                                <option value="3">3 estrellas</option>
                                <option value="4">4 estrellas</option>
                                <option value="5">5 estrellas</option>
                            </select>
                        </div>
                        <div>
                            <label className={LABEL_BASE} htmlFor="hotel-confirmacion">Confirmación del operador</label>
                            <input
                                id="hotel-confirmacion"
                                type="text"
                                className={INPUT_NORMAL}
                                value={form.confirmationNumber || ""}
                                onChange={(event) => setForm((prev) => ({ ...prev, confirmationNumber: event.target.value }))}
                                placeholder="Número o código de confirmación"
                                data-testid="hotel-confirmacion"
                                aria-label="Número de confirmación del operador"
                            />
                        </div>
                        {/* Campo "Fecha límite de seña/pago" eliminado en F2 (Próximos Inicios).
                            El aviso de la campanita se calcula desde firstStartDate (backend),
                            no desde un campo manual. Sin campo = sin dato viejo que desincronizar. */}
                        {/* Plan de cuotas (spec 2026-08-13, §8.3): dato informativo para el PDF de
                            presupuesto ("6 CUOTAS 280 USD") — NO participa del cálculo de Venta
                            total (esa cuenta sigue siendo noches × habitaciones × precio, arriba).
                            Va DESPUÉS de Estrellas/Confirmación (mismo criterio: descriptivo antes
                            que operativo) y ANTES de Dirección. Sin validación cruzada contra el
                            total: el dueño puede anotar un plan con recargo que suma distinto al
                            contado, y el sistema no lo corrige ni lo avisa. */}
                        <div>
                            <label className={LABEL_BASE} htmlFor="hotel-cuotas">Cuotas</label>
                            <input
                                id="hotel-cuotas"
                                type="text"
                                inputMode="numeric"
                                className={INPUT_NORMAL}
                                value={form.installmentsCount || ""}
                                onChange={(event) => setForm((prev) => ({ ...prev, installmentsCount: sanitizarCantidadPositiva(event.target.value) }))}
                                placeholder="Ej: 6"
                                data-testid="hotel-cuotas"
                                aria-label="Cantidad de cuotas"
                            />
                        </div>
                        <div>
                            {/* Sin selector de moneda propio (P-16): se entiende en la moneda que
                                ya eligió el servicio en el selector "Moneda" de la fila de Precios. */}
                            <label className={LABEL_BASE} htmlFor="hotel-valor-cuota">Valor por cuota</label>
                            <MoneyInput
                                id="hotel-valor-cuota"
                                className={INPUT_NORMAL}
                                value={form.installmentAmount || ""}
                                onChange={(nuevoValor) => setForm((prev) => ({ ...prev, installmentAmount: nuevoValor }))}
                                data-testid="hotel-valor-cuota"
                                aria-label="Valor de cada cuota"
                            />
                        </div>
                        <div className="sm:col-span-2">
                            <label className={LABEL_BASE} htmlFor="hotel-direccion">Dirección</label>
                            <input
                                id="hotel-direccion"
                                type="text"
                                className={INPUT_NORMAL}
                                value={form.address || ""}
                                onChange={(event) => setForm((prev) => ({ ...prev, address: event.target.value }))}
                                placeholder="Dirección del hotel"
                                data-testid="hotel-direccion"
                                aria-label="Dirección del hotel"
                            />
                        </div>
                    </div>
                )}
            </div>

            {/* Los totales (ventaTotal, costoTotal, ganancia) se calculan en este componente
                y se consumen en el footer de ServiceInlineCard vía los exports de abajo. */}
        </div>
    );
}

// Exportamos los calculadores para que ServiceInlineCard los use en el footer
export { calcularNoches, redondearDinero, formatearPrecio };
