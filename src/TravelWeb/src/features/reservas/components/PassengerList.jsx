/**
 * Solapa "Pasajeros" de la reserva.
 *
 * Muestra los pasajeros ya cargados + los "slots vacíos" correspondientes a
 * la cantidad declarada (adultos/menores/infantes) que todavía no tienen nombre.
 * Para cada slot vacío muestra un botón [Cargar] que despliega el mini-formulario
 * en línea (PasajeroInlineForm) sin abrir ninguna ventana flotante.
 *
 * Guía UX 2026-06-15 (P9, P10):
 *   - Un renglón por pasajero declarado con "sin cargar" cuando no tiene nombre.
 *   - Contador "X de N nombres cargados" arriba de la lista.
 *   - Botón [Cargar] despliega inline en el mismo renglón (nunca modal).
 *
 * Guía UX 2026-06-15 tarde (Pieza C):
 *   - Franja "💡 Por los servicios, parece que viajan X adultos + Y menores".
 *   - Botón [Usar] llena los casilleros. NUNCA se autollena.
 *   - No aparece si la cantidad ya coincide.
 *
 * Props:
 *   reserva               — objeto reserva con adultCount, childCount, infantCount, passengers[]
 *   reservaId             — publicId de la reserva (necesario para POST/PUT pasajeros)
 *   onPasajeroGuardado    — callback() que el padre llama para recargar la reserva
 *   onAddPassenger        — callback() para abrir el formulario completo de agregar
 *   onEditPassenger       — callback(passenger) para abrir el formulario completo de editar
 *   onDeletePassenger     — callback(passengerId) para eliminar un pasajero
 *   sugerenciaComposicion — objeto { adultos, menores, infantes, ambigua } del backend, o null
 *                           Si es null, la franja de sugerencia no aparece.
 *   onUsarSugerencia      — callback({ adultCount, childCount, infantCount }) cuando el vendedor
 *                           aprieta [Usar] en la franja. El padre actualiza los casilleros.
 *   onRequestEdit         — callback () => void: abre la ventana de destrabar (EditAuthorizationModal).
 *                           Candado C1 (spec 2026-07-22): "Editar" y "Eliminar" de un pasajero YA
 *                           CARGADO quedan gris + candadito cuando la reserva está bloqueada sin
 *                           autorización viva. "Agregar Pasajero" y "Cargar" (slot vacío) quedan
 *                           SIEMPRE encendidos — completar una identidad vacía no espera candado
 *                           (exención anti-callejón, spec §1.6).
 */

import React, { useState } from 'react';
import { Plus, User, Trash2, Edit2, Users, Lightbulb, Lock } from "lucide-react";
import { getPublicId } from "../../../lib/publicIds";
import { PasajeroInlineForm } from "./PasajeroInlineForm";
import { tieneCandadoDeEdicionActivo } from "./ReservaStatusBadge";

/**
 * Franja de sugerencia de cantidad de pasajeros (Pieza C — ADR-031 v2.1).
 *
 * Aparece arriba de los slots de pasajeros cuando el backend detectó una
 * composición diferente a la que el vendedor tiene cargada.
 * NUNCA se autollena: el vendedor tiene que tocar [Usar] explícitamente.
 *
 * Props:
 *   sugerencia       — { adultos, menores, infantes, ambigua }
 *   onUsarSugerencia — callback({ adultCount, childCount, infantCount })
 */
function FranjaSugerenciaComposicion({ sugerencia, onUsarSugerencia }) {
    if (!sugerencia) return null;

    // Construimos el texto de composición sugerida.
    // Solo mencionamos las categorías que tienen al menos 1 pasajero.
    const partes = [];
    if (sugerencia.adultos > 0) partes.push(`${sugerencia.adultos} adulto${sugerencia.adultos > 1 ? "s" : ""}`);
    if (sugerencia.menores > 0) partes.push(`${sugerencia.menores} menor${sugerencia.menores > 1 ? "es" : ""}`);
    if (sugerencia.infantes > 0) partes.push(`${sugerencia.infantes} infante${sugerencia.infantes > 1 ? "s" : ""}`);

    // Si no hay partes, no mostramos la franja (sin datos útiles para mostrar).
    if (partes.length === 0) return null;

    const textoComposicion = partes.join(" + ");

    const handleUsar = () => {
        onUsarSugerencia?.({
            adultCount: sugerencia.adultos,
            childCount: sugerencia.menores,
            infantCount: sugerencia.infantes,
        });
    };

    return (
        <div
            className="mb-4 flex flex-col sm:flex-row items-start sm:items-center gap-3 rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 dark:border-amber-800/40 dark:bg-amber-950/10"
            data-testid="franja-sugerencia-composicion"
            role="status"
            aria-live="polite"
        >
            <div className="flex items-start gap-2 flex-1 min-w-0">
                <Lightbulb className="mt-0.5 h-4 w-4 flex-shrink-0 text-amber-600 dark:text-amber-400" aria-hidden="true" />
                <p className="text-sm text-amber-800 dark:text-amber-200">
                    {/* Usamos "parece que" para dejar claro que es una sugerencia, no un dato confirmado. */}
                    Por los servicios, parece que viajan{" "}
                    <span className="font-bold">{textoComposicion}</span>.
                    {sugerencia.ambigua && (
                        <span className="ml-1 text-amber-600 dark:text-amber-400">
                            (Hay servicios con cantidades distintas — tomamos la mayor.)
                        </span>
                    )}
                </p>
            </div>

            <button
                type="button"
                onClick={handleUsar}
                data-testid="btn-usar-sugerencia-composicion"
                className="flex-shrink-0 rounded-lg border border-amber-400 bg-amber-100 px-4 py-1.5 text-xs font-bold text-amber-800 transition-colors hover:bg-amber-200 dark:border-amber-700 dark:bg-amber-900/40 dark:text-amber-200 dark:hover:bg-amber-900/60"
            >
                Usar
            </button>
        </div>
    );
}

/**
 * Construye la lista de slots (uno por pasajero declarado) fusionando
 * cantidad declarada con los pasajeros ya cargados.
 *
 * Si hay más pasajeros cargados que los declarados, los extras se muestran igual
 * (no los descartamos: el backend es la autoridad en eso).
 */
function buildSlots(adultCount, childCount, infantCount, passengers) {
    const slots = [];

    // Un slot por pasajero declarado, en el orden: adultos → menores → infantes.
    for (let i = 0; i < adultCount; i++) {
        slots.push({ etiqueta: `Adulto ${i + 1}`, pasajero: passengers[slots.length] || null });
    }
    for (let i = 0; i < childCount; i++) {
        slots.push({ etiqueta: `Menor ${i + 1}`, pasajero: passengers[slots.length] || null });
    }
    for (let i = 0; i < infantCount; i++) {
        slots.push({ etiqueta: `Infante ${i + 1}`, pasajero: passengers[slots.length] || null });
    }

    // Pasajeros extras (más de los declarados): los mostramos sin etiqueta categórica.
    const extras = passengers.slice(slots.length);
    extras.forEach((pax, i) => {
        slots.push({ etiqueta: `Pasajero ${slots.length + 1}`, pasajero: pax });
    });

    return slots;
}

export function PassengerList({
    reserva,
    reservaId,
    onPasajeroGuardado,
    onAddPassenger,
    onEditPassenger,
    onDeletePassenger,
    // Pieza C (ADR-031 v2.1): sugerencia de composición desde los servicios.
    // Viene del padre (ReservaDetailPage) que ya tiene el TransitionReadinessDto procesado.
    sugerenciaComposicion = null,
    onUsarSugerencia = null,
    // ADR-035 feedback 2026-06-19: gate de solo-lectura.
    // Cuando es false (estado terminal: Lost, Cancelled, Closed), los botones de
    // agregar/editar/borrar pasajero se ocultan. El padre lo extrae de capabilities.canEditPassengers.
    // Degradación elegante: si no se pasa, se permite editar (mismo comportamiento previo).
    canEditPassengers = true,
    // Candado C1 (2026-07-22): abre la ventana de destrabar cuando se toca un botón
    // gris + candadito de "Editar"/"Eliminar" de un pasajero ya cargado.
    onRequestEdit,
}) {
    // Slot que tiene el mini-formulario inline abierto.
    // null = ninguno; guardamos el índice del slot.
    const [slotAbierto, setSlotAbierto] = useState(null);

    // Candado C1 (spec 2026-07-22): con la reserva bloqueada y sin autorización viva, los
    // botones "Editar" y "Eliminar" de un pasajero YA CARGADO quedan gris + candadito.
    // "Agregar Pasajero" y "Cargar" (slot vacío) NO llevan candado — completar un dato que
    // falta no espera destrabe (exención anti-callejón, spec §1.6).
    const candadoDeEdicionActivo = tieneCandadoDeEdicionActivo(reserva);

    const passengers = reserva?.passengers || [];
    const adultCount = reserva?.adultCount || 0;
    const childCount = reserva?.childCount || 0;
    const infantCount = reserva?.infantCount || 0;
    const totalDeclarado = adultCount + childCount + infantCount;

    // Pasajeros que tienen nombre cargado (no vacío).
    const cargados = passengers.filter(p => p?.fullName?.trim()).length;

    const slots = buildSlots(adultCount, childCount, infantCount, passengers);

    return (
        <div>
            {/* Encabezado con título, contador y botón de agregar */}
            <div className="flex flex-col sm:flex-row justify-between items-start sm:items-center gap-4 mb-4">
                <div>
                    <h3 className="text-lg font-medium text-gray-900 dark:text-white">Pasajeros del Viaje</h3>

                    {/* Contador "X de N nombres cargados" (P10, guía UX 2026-06-15).
                        Se oculta si no hay cantidad declarada (nada que contar).
                        Desaparece cuando todos tienen nombre (cargados === totalDeclarado). */}
                    {totalDeclarado > 0 && cargados < totalDeclarado && (
                        <p
                            className="mt-0.5 text-sm text-amber-700 dark:text-amber-400 font-semibold"
                            data-testid="contador-nombres-cargados"
                            aria-live="polite"
                        >
                            {cargados} de {totalDeclarado} nombres cargados
                        </p>
                    )}
                    {totalDeclarado > 0 && cargados === totalDeclarado && (
                        <p
                            className="mt-0.5 text-sm text-emerald-600 dark:text-emerald-400 font-semibold"
                            data-testid="contador-nombres-cargados"
                        >
                            {cargados} de {totalDeclarado} nombres cargados
                        </p>
                    )}
                </div>

                {/* "Agregar Pasajero" se oculta en estados terminales donde canEditPassengers=false.
                    Feedback 2026-06-19: en Perdida/Cancelada/Finalizada no se pueden agregar pasajeros. */}
                {canEditPassengers && (
                    <button
                        onClick={onAddPassenger}
                        className="w-full sm:w-auto flex items-center justify-center gap-2 bg-indigo-600 text-white px-4 py-2 rounded-lg hover:bg-indigo-700 transition-colors shadow-sm"
                        data-testid="btn-agregar-pasajero"
                    >
                        <Plus className="w-4 h-4" /> Agregar Pasajero
                    </button>
                )}
            </div>

            {/* Franja de sugerencia de composición (Pieza C — ADR-031 v2.1).
                Solo aparece cuando el backend sugiere una composición diferente a la actual.
                NUNCA se autollena: el vendedor aprieta [Usar] para aplicar la sugerencia.
                El padre (ReservaDetailPage) es quien calcula si hay sugerencia o no. */}
            <FranjaSugerenciaComposicion
                sugerencia={sugerenciaComposicion}
                onUsarSugerencia={onUsarSugerencia}
            />

            {/* Estado vacío: cantidad declarada = 0 y sin pasajeros cargados */}
            {slots.length === 0 && (
                <div className="text-center py-12 bg-gray-50 dark:bg-slate-800 rounded-lg border border-dashed border-gray-300 dark:border-slate-700">
                    <User className="w-12 h-12 text-gray-300 dark:text-slate-600 mx-auto mb-3" />
                    <p className="text-gray-500 dark:text-slate-400">No hay pasajeros registrados.</p>
                </div>
            )}

            {/* Lista de slots */}
            {slots.length > 0 && (
                <div className="space-y-2">
                    {slots.map((slot, index) => {
                        const tieneNombre = Boolean(slot.pasajero?.fullName?.trim());
                        const esteSlotAbierto = slotAbierto === index;

                        return (
                            <div key={index}>
                                {/* Renglón del pasajero */}
                                <div
                                    className={`flex items-center gap-3 rounded-xl border px-4 py-3 transition-colors ${
                                        tieneNombre
                                            ? "border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900"
                                            : "border-amber-200 bg-amber-50/60 dark:border-amber-800/40 dark:bg-amber-950/10"
                                    }`}
                                    data-testid={`slot-pasajero-${index}`}
                                >
                                    {/* Avatar o icono vacío */}
                                    <div className={`flex-shrink-0 w-9 h-9 rounded-full flex items-center justify-center text-xs font-bold border ${
                                        tieneNombre
                                            ? "bg-indigo-50 text-indigo-600 border-indigo-100 dark:bg-indigo-900/30 dark:text-indigo-400 dark:border-indigo-800/50"
                                            : "bg-amber-100 text-amber-500 border-amber-200 dark:bg-amber-900/30 dark:text-amber-400 dark:border-amber-800/40"
                                    }`}>
                                        {tieneNombre
                                            ? (slot.pasajero.fullName[0] || "P").toUpperCase()
                                            : <Users className="w-4 h-4" />
                                        }
                                    </div>

                                    {/* Datos del pasajero o etiqueta vacía */}
                                    <div className="flex-1 min-w-0">
                                        <div className="flex items-center gap-2 flex-wrap">
                                            <span className="text-xs font-bold uppercase tracking-wider text-slate-500 dark:text-slate-400">
                                                {slot.etiqueta}
                                            </span>
                                            {tieneNombre ? (
                                                <span className="text-sm font-semibold text-slate-900 dark:text-white uppercase truncate">
                                                    {slot.pasajero.fullName}
                                                </span>
                                            ) : (
                                                <span className="text-sm italic text-amber-600 dark:text-amber-400">
                                                    — sin cargar
                                                </span>
                                            )}
                                        </div>
                                        {/* Documento del pasajero (si existe) */}
                                        {tieneNombre && slot.pasajero.documentNumber && (
                                            <div className="text-[10px] text-slate-500 mt-0.5 uppercase">
                                                {slot.pasajero.documentType || "DNI"} {slot.pasajero.documentNumber}
                                            </div>
                                        )}
                                    </div>

                                    {/* Acciones de pasajero.
                                        canEditPassengers=false → se ocultan todos los botones (solo lectura).
                                        Feedback 2026-06-19: en estados terminales la lista es informativa. */}
                                    <div className="flex items-center gap-1 flex-shrink-0">
                                        {canEditPassengers ? (
                                            tieneNombre ? (
                                                // Pasajero con nombre: Editar + Eliminar.
                                                // Candado C1 (2026-07-22): con la reserva bloqueada sin
                                                // autorización viva, los dos quedan gris + candadito y
                                                // abren la ventana de destrabar en vez de editar/borrar directo.
                                                candadoDeEdicionActivo ? (
                                                    <>
                                                        <button
                                                            type="button"
                                                            onClick={onRequestEdit}
                                                            aria-label="Editar pasajero — bloqueado, pedí autorización"
                                                            className="p-2 text-slate-400 hover:bg-slate-100 dark:text-slate-500 dark:hover:bg-slate-800 rounded-lg transition-colors"
                                                        >
                                                            <Lock className="w-4 h-4" aria-hidden="true" />
                                                        </button>
                                                        <button
                                                            type="button"
                                                            onClick={onRequestEdit}
                                                            aria-label="Eliminar pasajero — bloqueado, pedí autorización"
                                                            className="p-2 text-slate-400 hover:bg-slate-100 dark:text-slate-500 dark:hover:bg-slate-800 rounded-lg transition-colors"
                                                        >
                                                            <Lock className="w-4 h-4" aria-hidden="true" />
                                                        </button>
                                                    </>
                                                ) : (
                                                <>
                                                    <button
                                                        type="button"
                                                        onClick={() => onEditPassenger(slot.pasajero)}
                                                        aria-label="Editar pasajero"
                                                        className="p-2 text-indigo-600 hover:bg-indigo-50 dark:hover:bg-indigo-900/40 rounded-lg transition-colors"
                                                        title="Editar"
                                                    >
                                                        <Edit2 className="w-4 h-4" />
                                                    </button>
                                                    <button
                                                        type="button"
                                                        onClick={() => onDeletePassenger(getPublicId(slot.pasajero))}
                                                        aria-label="Eliminar pasajero"
                                                        className="p-2 text-rose-600 hover:bg-rose-50 dark:hover:bg-rose-900/40 rounded-lg transition-colors"
                                                        title="Eliminar"
                                                    >
                                                        <Trash2 className="w-4 h-4" />
                                                    </button>
                                                </>
                                                )
                                            ) : (
                                                // Slot sin nombre: botón [Cargar] que abre el inline form
                                                <button
                                                    type="button"
                                                    onClick={() => setSlotAbierto(esteSlotAbierto ? null : index)}
                                                    aria-label={`Cargar datos de ${slot.etiqueta}`}
                                                    aria-expanded={esteSlotAbierto}
                                                    data-testid={`btn-cargar-pasajero-${index}`}
                                                    className="inline-flex items-center gap-1.5 rounded-lg border border-amber-400 bg-amber-100 px-3 py-1.5 text-xs font-bold text-amber-700 transition-colors hover:bg-amber-200 dark:border-amber-700 dark:bg-amber-900/30 dark:text-amber-300 dark:hover:bg-amber-900/50"
                                                >
                                                    <Plus className="w-3.5 h-3.5" />
                                                    Cargar
                                                </button>
                                            )
                                        ) : null /* estado terminal: solo lectura, sin botones */}
                                    </div>
                                </div>

                                {/* Mini-formulario inline: solo se despliega para el slot abierto
                                    Y cuando canEditPassengers=true (no en solo lectura terminal). */}
                                {esteSlotAbierto && !tieneNombre && canEditPassengers && (
                                    <div className="mt-1 ml-4">
                                        <PasajeroInlineForm
                                            reservaId={reservaId}
                                            passengerToEdit={slot.pasajero}
                                            slotLabel={slot.etiqueta}
                                            mode="full"
                                            onGuardado={(pasajeroGuardado) => {
                                                setSlotAbierto(null);
                                                onPasajeroGuardado?.();
                                            }}
                                            onCancelar={() => setSlotAbierto(null)}
                                        />
                                    </div>
                                )}
                            </div>
                        );
                    })}
                </div>
            )}
        </div>
    );
}
