/**
 * Ficha de carga en línea de servicios de una reserva.
 * Reemplaza al modal ServiceFormModal cuando el flag EnableCatalogFindOrCreate está ON.
 *
 * Se abre DEBAJO de la lista de servicios (inline, sin ventana emergente).
 * Al guardar, la ficha se cierra y el servicio aparece como una fila más.
 *
 * Pestañas: Hotel | Aéreo | Traslado | Paquete | Asistencia
 * F2 parte 1: solo Hotel funciona; los otros 4 están preparados (placeholder).
 *
 * Flujo de guardado:
 *   - Producto existente (rateId): POST /reservas/{id}/hotels con rateId
 *   - Producto nuevo (newCatalogProduct): POST /reservas/{id}/hotels con newCatalogProduct
 *   - Editar (serviceToEdit): PUT /reservas/{id}/hotels/{serviceId}
 *
 * Si el guardado falla, la ficha queda abierta con los datos intactos y muestra
 * un cartel rojo arriba de los botones (nunca se pierde lo cargado — guía UX ronda 2).
 */

import { useState, useCallback } from "react";
import { Hotel, Plane, Car, Package, ShieldCheck, AlertCircle } from "lucide-react";
import { hasPermission } from "../../../auth";
import { api } from "../../../api";
import { getApiErrorMessage } from "../../../lib/errors";
import { getReservationServicePublicId } from "../lib/reservationServiceModel";
import { HotelInlineForm, calcularNoches, redondearDinero, formatearPrecio } from "./HotelInlineForm";

// ─── Configuración de pestañas ────────────────────────────────────────────────

const TABS = [
    { id: "Hotel", label: "Hotel", icon: Hotel },
    { id: "Aereo", label: "Aéreo", icon: Plane },
    { id: "Traslado", label: "Traslado", icon: Car },
    { id: "Paquete", label: "Paquete", icon: Package },
    { id: "Asistencia", label: "Asistencia", icon: ShieldCheck },
];

// ─── Estado inicial del form de Hotel ────────────────────────────────────────

function buildHotelFormInitial(serviceToEdit) {
    if (!serviceToEdit) {
        return {
            hotelName: "",
            city: "",
            checkIn: "",
            checkOut: "",
            passengers: "",
            // rooms: cantidad de habitaciones. Default 1 (guía UX 2026-06-06).
            // Afecta el total: noches × habitaciones × precio/noche.
            rooms: 1,
            supplierId: "",
            unitNetCost: "",
            unitSalePrice: "",
            currency: "ARS",
            mealPlan: "",
            roomType: "",
            confirmationNumber: "",
            operatorPaymentDeadline: "",
            address: "",
            rateId: null,
            newCatalogProduct: null,
        };
    }

    // Al editar: populamos el form con los datos existentes del servicio.
    // Los precios "por noche" se calculan dividiendo el total / (noches × habitaciones).
    const noches = calcularNoches(serviceToEdit.checkIn, serviceToEdit.checkOut);
    const habitaciones = Math.max(serviceToEdit.rooms || 1, 1);
    const divisor = Math.max(noches, 1) * habitaciones;

    return {
        hotelName: serviceToEdit.hotelName || serviceToEdit.name || "",
        city: serviceToEdit.city || "",
        checkIn: (serviceToEdit.checkIn || "").split("T")[0] || "",
        checkOut: (serviceToEdit.checkOut || "").split("T")[0] || "",
        passengers: serviceToEdit.paxCount || serviceToEdit.adults || serviceToEdit.passengers || "",
        rooms: habitaciones,
        supplierId: serviceToEdit.supplierId || serviceToEdit.supplierPublicId || "",
        // Dividimos por (noches × habitaciones) para el precio por noche por habitación
        unitNetCost: noches > 0 ? String(redondearDinero((serviceToEdit.netCost || 0) / divisor)) : "",
        unitSalePrice: noches > 0 ? String(redondearDinero((serviceToEdit.salePrice || 0) / divisor)) : "",
        currency: serviceToEdit.currency || "ARS",
        mealPlan: serviceToEdit.mealPlan || "",
        roomType: serviceToEdit.roomType || "",
        confirmationNumber: serviceToEdit.confirmationNumber || "",
        operatorPaymentDeadline: (serviceToEdit.operatorPaymentDeadline || "").split("T")[0] || "",
        address: serviceToEdit.address || "",
        rateId: serviceToEdit.rateId || null,
        newCatalogProduct: null,
    };
}

// ─── Componente PlaceholderTab ────────────────────────────────────────────────

/**
 * Contenido placeholder para las pestañas que aún no se construyeron (parte 2).
 * Avisa al usuario sin romper el flujo.
 */
function PlaceholderTab({ tipo }) {
    return (
        <div className="py-10 text-center text-slate-400">
            <div className="text-sm font-medium">La carga de {tipo} estará disponible pronto.</div>
            <div className="text-xs mt-1">Por ahora usá el formulario anterior para este tipo de servicio.</div>
        </div>
    );
}

// ─── Componente principal ServiceInlineCard ───────────────────────────────────

/**
 * Props:
 *   reservaId     — publicId de la reserva (para los endpoints)
 *   serviceToEdit — si viene, la ficha se abre en modo edición con los datos precargados
 *   suppliers     — lista de proveedores del contexto de la reserva
 *   onGuardado    — callback que se llama con opciones después de guardar exitosamente
 *   onCancelar    — callback para cerrar la ficha sin guardar
 */
export function ServiceInlineCard({ reservaId, serviceToEdit, suppliers, onGuardado, onCancelar }) {
    const canSeeCost = hasPermission("cobranzas.see_cost");

    // La pestaña activa: si estamos editando, forzamos Hotel (único tipo implementado en parte 1)
    const [tabActiva, setTabActiva] = useState("Hotel");

    // Estado del formulario de Hotel
    const [form, setForm] = useState(() => buildHotelFormInitial(serviceToEdit));

    // Estado de guardado
    const [guardando, setGuardando] = useState(false);
    const [errorGuardado, setErrorGuardado] = useState(null);

    // ─── Construir payload para el backend ────────────────────────────────────

    const buildHotelPayload = useCallback(() => {
        const noches = calcularNoches(form.checkIn, form.checkOut);
        const habitaciones = Math.max(Number(form.rooms) || 1, 1);
        // El backend espera el total de la estadía completa (noches × habitaciones × precio/noche)
        const factorTotal = Math.max(noches, 1) * habitaciones;

        // Calculamos totales desde los precios por noche × habitaciones (la ficha trabaja por noche/hab)
        const netCostTotal = redondearDinero((Number(form.unitNetCost) || 0) * factorTotal);
        const salePriceTotal = redondearDinero((Number(form.unitSalePrice) || 0) * factorTotal);

        const payload = {
            hotelName: form.hotelName?.trim() || "",
            city: form.city?.trim() || "",
            checkIn: form.checkIn,
            checkOut: form.checkOut,
            nights: noches,
            rooms: habitaciones,
            // Pasajeros: enviar el campo que espera el backend para hotel
            adults: Number(form.passengers) || 1,
            children: 0,
            supplierId: form.supplierId || null,
            // Los totales van al backend (el precio por noche es solo para la UI)
            netCost: canSeeCost ? netCostTotal : 0,
            salePrice: salePriceTotal,
            tax: 0,
            currency: form.currency || "ARS",
            mealPlan: form.mealPlan || null,
            roomType: form.roomType || null,
            confirmationNumber: form.confirmationNumber || null,
            // Dirección: el backend acepta address en el payload de hotel (se persiste)
            address: form.address || null,
            // Deadline: se envía solo si el campo tiene valor; null = borrar si es edición
            operatorPaymentDeadline: form.operatorPaymentDeadline || null,
            deadlinesSpecified: true, // Nueva ficha siempre especifica deadlines
        };

        // Mutuamente excluyentes: rateId O newCatalogProduct
        if (form.rateId) {
            payload.rateId = form.rateId;
        } else if (form.newCatalogProduct) {
            payload.newCatalogProduct = {
                name: form.newCatalogProduct.name?.trim() || "",
                city: form.newCatalogProduct.city?.trim() || "",
                supplierPublicId: form.newCatalogProduct.supplierPublicId || "",
            };
            // Al crear con producto nuevo, el operador ya viene dentro de newCatalogProduct
            payload.supplierId = form.newCatalogProduct.supplierPublicId || null;
        }

        return payload;
    }, [form, canSeeCost]);

    // ─── Validar antes de guardar ─────────────────────────────────────────────

    const validarFormHotel = () => {
        if (!form.hotelName?.trim()) return "Escribí el nombre del hotel.";
        if (!form.checkIn) return "Elegí la fecha de entrada.";
        if (!form.checkOut) return "Elegí la fecha de salida.";
        const noches = calcularNoches(form.checkIn, form.checkOut);
        if (noches <= 0) return "La fecha de salida debe ser posterior a la de entrada.";
        if (!form.unitSalePrice || Number(form.unitSalePrice) <= 0) return "Ingresá el precio de venta por noche.";

        // C1: el operador es SIEMPRE obligatorio, tanto en el camino "producto existente"
        // como al crear uno nuevo. Un hotel sin operador no se puede gestionar ni cobrar.
        if (!form.newCatalogProduct && !form.supplierId) {
            return "Elegí el operador.";
        }

        if (form.newCatalogProduct) {
            if (!form.newCatalogProduct.name?.trim()) return "Ingresá el nombre del hotel nuevo.";
            // Ciudad OBLIGATORIA al crear un hotel nuevo (decisión D6 de Gastón)
            if (!form.newCatalogProduct.city?.trim()) return "La ciudad es obligatoria para crear un hotel nuevo.";
            if (!form.newCatalogProduct.supplierPublicId) return "Elegí el operador del hotel nuevo.";
        }
        return null;
    };

    // ─── Guardar ──────────────────────────────────────────────────────────────

    const handleGuardar = async () => {
        // Limpiar error anterior antes de intentar
        setErrorGuardado(null);

        const errorValidacion = validarFormHotel();
        if (errorValidacion) {
            setErrorGuardado(errorValidacion);
            return;
        }

        setGuardando(true);
        try {
            const payload = buildHotelPayload();

            if (serviceToEdit) {
                // Edición: PUT al endpoint del servicio existente
                const serviceId = getReservationServicePublicId(serviceToEdit);
                await api.put(`/reservas/${reservaId}/hotels/${serviceId}`, payload);
            } else {
                // Creación: POST al endpoint de hoteles de la reserva
                await api.post(`/reservas/${reservaId}/hotels`, payload);
            }

            // Éxito: notificamos al padre para que recargue los servicios
            // showLoading: false preserva el contenido mientras recarga (evita flash)
            onGuardado({ showLoading: false, preserveOnError: true });
        } catch (error) {
            // Si falla, mostramos el error ARRIBA de los botones y NO cerramos la ficha.
            // El usuario puede corregir y reintentar en el mismo botón (guía UX ronda 2).
            setErrorGuardado(getApiErrorMessage(error, "No se pudo guardar. Revisá la conexión y probá de nuevo."));
        } finally {
            setGuardando(false);
        }
    };

    // ─── Calcular totales para el footer ─────────────────────────────────────

    const noches = calcularNoches(form.checkIn, form.checkOut);
    // Mismo factor que el payload: noches × habitaciones
    const habitacionesFooter = Math.max(Number(form.rooms) || 1, 1);
    const factorFooter = Math.max(noches, 0) * habitacionesFooter;
    const ventaTotal = redondearDinero((Number(form.unitSalePrice) || 0) * factorFooter);
    const costoTotal = canSeeCost ? redondearDinero((Number(form.unitNetCost) || 0) * factorFooter) : null;
    const ganancia = canSeeCost && costoTotal !== null ? redondearDinero(ventaTotal - costoTotal) : null;

    const esEdicion = Boolean(serviceToEdit);
    const labelBotonGuardar = esEdicion ? "Guardar cambios" : (form.newCatalogProduct ? "Guardar servicio y hotel" : "Guardar servicio");

    // ─── Render ───────────────────────────────────────────────────────────────

    return (
        // Borde azul = zona activa (mockup estilo .inlinecard)
        <div
            className="border-2 border-blue-500 rounded-xl bg-white p-5 mt-4 shadow-sm"
            data-testid="service-inline-card"
        >
            {/* PESTAÑAS */}
            <div className="flex gap-2 mb-5 flex-wrap" role="tablist" aria-label="Tipo de servicio">
                {TABS.map(({ id, label, icon: Icon }) => {
                    const estaActiva = tabActiva === id;
                    const estaImplementada = id === "Hotel";
                    return (
                        <button
                            key={id}
                            type="button"
                            role="tab"
                            aria-selected={estaActiva}
                            // Deshabilitamos las tabs no implementadas (F2 parte 2)
                            disabled={!estaImplementada || esEdicion}
                            onClick={() => { if (estaImplementada && !esEdicion) setTabActiva(id); }}
                            className={`flex items-center gap-1.5 px-4 py-1.5 rounded-full text-sm font-semibold transition-colors ${
                                estaActiva
                                    ? "bg-blue-600 text-white"
                                    : estaImplementada
                                    ? "bg-slate-100 text-slate-600 hover:bg-slate-200"
                                    : "bg-slate-50 text-slate-300 cursor-not-allowed"
                            }`}
                            data-testid={`tab-${id.toLowerCase()}`}
                        >
                            <Icon className="w-3.5 h-3.5" />
                            {label}
                        </button>
                    );
                })}
            </div>

            {/* CONTENIDO DE LA PESTAÑA ACTIVA */}
            <div role="tabpanel">
                {tabActiva === "Hotel" ? (
                    <HotelInlineForm
                        form={form}
                        setForm={setForm}
                        suppliers={suppliers}
                        isEditing={esEdicion}
                    />
                ) : (
                    <PlaceholderTab tipo={tabActiva} />
                )}
            </div>

            {/* FOOTER FIJO: totales + botones */}
            <div className="mt-5 pt-4 border-t border-slate-100 flex flex-col sm:flex-row justify-between items-start sm:items-center gap-3">
                {/* Izquierda: totales + enlace "Más detalles" (si aplica) */}
                <div className="text-sm text-slate-700 flex flex-wrap items-center gap-3">
                    {tabActiva === "Hotel" && noches > 0 && (Number(form.unitSalePrice) > 0) && (
                        <>
                            <span>
                                Venta <strong>{formatearPrecio(ventaTotal)}</strong>
                            </span>
                            {/* Ganancia: solo para quien tiene permiso de ver costos */}
                            {canSeeCost && ganancia !== null && (
                                <span className={ganancia >= 0 ? "font-semibold text-emerald-600" : "font-semibold text-red-600"}>
                                    Ganás {formatearPrecio(ganancia)}
                                </span>
                            )}
                        </>
                    )}
                </div>

                {/* Derecha: cartel de error + botones */}
                <div className="flex flex-col items-end gap-2 w-full sm:w-auto">
                    {/* Error de guardado: arriba de los botones, visible y claro */}
                    {errorGuardado && (
                        <div
                            className="flex items-start gap-2 text-xs text-red-700 bg-red-50 border border-red-200 rounded-lg px-3 py-2 w-full sm:w-auto max-w-sm"
                            role="alert"
                            data-testid="inline-card-error"
                        >
                            <AlertCircle className="w-3.5 h-3.5 mt-0.5 shrink-0" />
                            <span>{errorGuardado}</span>
                        </div>
                    )}
                    <div className="flex gap-2">
                        <button
                            type="button"
                            onClick={onCancelar}
                            disabled={guardando}
                            className="px-4 py-2 text-sm font-medium text-slate-600 border border-slate-200 rounded-lg hover:bg-slate-50 disabled:opacity-50 transition-colors"
                            data-testid="inline-card-cancelar"
                        >
                            Cancelar
                        </button>
                        <button
                            type="button"
                            onClick={handleGuardar}
                            disabled={guardando || tabActiva !== "Hotel"}
                            className="px-5 py-2 text-sm font-semibold bg-blue-600 text-white rounded-lg hover:bg-blue-700 disabled:opacity-50 transition-colors"
                            data-testid="inline-card-guardar"
                        >
                            {guardando ? "Guardando…" : labelBotonGuardar}
                        </button>
                    </div>
                </div>
            </div>
        </div>
    );
}
