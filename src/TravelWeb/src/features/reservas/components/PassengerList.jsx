/**
 * Solapa "Pasajeros" de la reserva.
 *
 * Muestra los pasajeros ya cargados + los "slots vacíos" correspondientes a
 * la cantidad declarada (adultos/menores/infantes) que todavía no tienen nombre.
 * Para cada slot vacío muestra un botón [Cargar] que despliega el mini-formulario
 * en línea (PasajeroInlineForm) sin abrir ninguna ventana flotante.
 *
 * T5 (2026-08-18, spec sección 3 — "muere el modal"): "Editar" y "Agregar Pasajero"
 * ya NO abren PassengerFormModal (jubilado). Los dos flujos se resuelven acá mismo
 * con PasajeroInlineForm en modo `conFuncionesCompletas` (lupa AFIP, histórico de la
 * agencia, "+ Más detalles"):
 *   - Editar: la MISMA fila del pasajero se transforma en el formulario (no se abre
 *     una tarjeta nueva debajo — P-16, no duplicar el nombre dos veces en pantalla).
 *     Los chips de vencimiento se esconden mientras se edita (P5=A, vuelven al
 *     guardar o cancelar) porque al reemplazar la fila entera, ya no están en el DOM.
 *   - Agregar Pasajero (uno "de más", no un slot declarado): abre el formulario de
 *     alta AL FINAL de la lista (P4=A), con el histórico activo (solo alta).
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
 *   onPasajeroGuardado    — callback() que el padre llama para recargar la reserva. Se usa para
 *                           los tres flujos (cargar slot vacío, editar, agregar extra) — ya no
 *                           hay callbacks separados de "abrir formulario completo" (el modal murió).
 *   onDeletePassenger     — callback(passengerId) para eliminar un pasajero
 *   sugerenciaComposicion — objeto { adultos, menores, infantes, ambigua } del backend, o null
 *                           Si es null, la franja de sugerencia no aparece.
 *   onUsarSugerencia      — callback({ adultCount, childCount, infantCount }) cuando el vendedor
 *                           aprieta [Usar] en la franja. El padre actualiza los casilleros.
 *   onRequestEdit         — callback () => void: abre la ventana de destrabar (EditAuthorizationModal).
 *                           Candado C1 (spec 2026-07-22, REFINADA 2026-08-06 — ver abajo): "Editar" y
 *                           "Eliminar" de un pasajero YA CARGADO quedan gris + candadito cuando la
 *                           reserva está bloqueada sin autorización viva. "Cargar" (slot vacío) queda
 *                           SIEMPRE encendido — completar una identidad vacía no espera candado
 *                           (exención anti-callejón, spec §1.6).
 *   canAddPassenger       — capacidad del backend { allowed, reason } (reserva.capabilities.canAddPassenger),
 *                           o null/undefined si el DTO no la trae (se degrada a "permitido", igual que
 *                           canEditPassengers). Frente 0 (2026-08-06, refina la decisión 2026-06-17):
 *                           "Agregar Pasajero" tiene 3 estados. (1) Bajo candado con algún nombre
 *                           declarado TODAVÍA sin cargar: sigue encendido, dice "Completar pasajero" —
 *                           la decisión 17/06 sigue intacta, eso es completar, no altera nada. (2) Bajo
 *                           candado con los N declarados YA TODOS cargados: agregar uno de más deja de
 *                           ser completar — no queda ningún lugar vacío — y pasa a ser alterar la
 *                           reserva; el botón queda gris + candadito, MISMO patrón que Editar/Borrar, y
 *                           el motor lo rechaza si igual se insiste (RESERVA_PASAJEROS_COMPLETOS_BAJO_
 *                           CANDADO). (3) Sin candado: "Agregar Pasajero" de siempre, sin cartel especial.
 *                           El SI/NO de (2) lo decide el backend (T-13): la pantalla no vuelve a comparar
 *                           declarados-vs-cargados por su cuenta para eso, solo lee canAddPassenger.
 */

import React, { useState } from 'react';
import { Plus, User, Trash2, Edit2, Users, Lightbulb, Lock } from "lucide-react";
import { getPublicId } from "../../../lib/publicIds";
import { PasajeroInlineForm } from "./PasajeroInlineForm";
import { tieneCandadoDeEdicionActivo } from "./ReservaStatusBadge";
import { construirChipPasaporte } from "../lib/passportAlertChip";
import { construirChipDni } from "../lib/dniAlertChip";
import { construirChipMenor } from "../lib/minorAlertChip";

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
            className="mb-4 flex flex-col sm:flex-row items-start sm:items-center gap-3 rounded-[10px] border border-amber-200 bg-amber-50 px-4 py-3 dark:border-amber-800/40 dark:bg-amber-950/10"
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
                className="flex-shrink-0 rounded-[10px] border border-amber-400 bg-amber-100 px-4 py-1.5 text-xs font-bold text-amber-800 transition-colors hover:bg-amber-200 dark:border-amber-700 dark:bg-amber-900/40 dark:text-amber-200 dark:hover:bg-amber-900/60"
            >
                Usar
            </button>
        </div>
    );
}

/**
 * Un casillero de solo lectura con la cantidad de una categoría de pasajero
 * (Adultos/Menores/Infantes) — maqueta sección 7, líneas 976-981. A diferencia
 * del widget de Presupuesto (Tanda 3), este NO se edita acá: la cantidad ya
 * está fija una vez que la reserva avanzó a En gestión.
 */
function ResumenCantidad({ etiqueta, valor }) {
    return (
        <div>
            <div className="text-[11px] font-bold uppercase tracking-wider text-slate-400 dark:text-slate-500">
                {etiqueta}
            </div>
            <div className="mt-0.5 flex h-9 w-16 items-center justify-center rounded-[10px] border border-slate-200 text-sm font-bold text-slate-700 dark:border-slate-700 dark:text-slate-200">
                {valor}
            </div>
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
    // Frente 0 (2026-08-06): capacidad { allowed, reason } que decide el ESTADO 2 de "Agregar
    // Pasajero" (roster declarado completo bajo candado). Ver doc de props arriba.
    canAddPassenger = null,
}) {
    // Slot que tiene el mini-formulario inline abierto (para CARGAR un slot vacío).
    // null = ninguno; guardamos el índice del slot.
    const [slotAbierto, setSlotAbierto] = useState(null);

    // T5 (2026-08-18): índice del slot que se está EDITANDO (pasajero ya cargado).
    // La fila entera se reemplaza por PasajeroInlineForm mientras esto no sea null
    // (P-16: nunca dos tarjetas con el mismo nombre a la vez en pantalla).
    const [editandoIndex, setEditandoIndex] = useState(null);

    // T5: si está abierto el formulario de "Agregar Pasajero" (uno DE MÁS, no un
    // slot declarado) — aparece al final de la lista (P4=A), nunca arriba.
    const [agregandoPasajeroExtra, setAgregandoPasajeroExtra] = useState(false);

    // Candado C1 (spec 2026-07-22): con la reserva bloqueada y sin autorización viva, los
    // botones "Editar" y "Eliminar" de un pasajero YA CARGADO quedan gris + candadito.
    // "Cargar" (slot vacío) NO lleva candado — completar un dato que falta no espera destrabe
    // (exención anti-callejón, spec §1.6). "Agregar Pasajero" se calcula aparte, más abajo
    // (Frente 0, 2026-08-06): tiene un tercer estado que este booleano solo no alcanza a distinguir.
    const candadoDeEdicionActivo = tieneCandadoDeEdicionActivo(reserva);

    const passengers = reserva?.passengers || [];
    const adultCount = reserva?.adultCount || 0;
    const childCount = reserva?.childCount || 0;
    const infantCount = reserva?.infantCount || 0;
    const totalDeclarado = adultCount + childCount + infantCount;

    // Pasajeros que tienen nombre cargado (no vacío).
    const cargados = passengers.filter(p => p?.fullName?.trim()).length;

    // Frente 0 (2026-08-06, refina la decisión 2026-06-17): los 3 estados del botón "Agregar Pasajero".
    //   1) Candado activo + todavía falta algún nombre por cargar -> sigue encendido, dice
    //      "Completar pasajero" (decisión 17/06 intacta: eso es completar, no altera nada).
    //   2) Candado activo + los N declarados YA TODOS cargados -> travado con candadito. El SI/NO lo
    //      decide el MOTOR (canAddPassenger, T-13) — la pantalla no vuelve a comparar declarados-vs-
    //      cargados por su cuenta para esta decisión puntual, solo lee la capacidad que ya viaja en el
    //      DTO de la reserva. Si el DTO todavía no la trae (degradación elegante, mismo criterio que
    //      canEditPassengers), se asume permitido: nunca se traba de más por un dato faltante.
    //   3) Sin candado -> "Agregar Pasajero" de siempre, sin ningún cartel especial.
    const bloqueadoPorCandado = canAddPassenger != null && canAddPassenger.allowed === false;
    const tituloBotonAgregar = candadoDeEdicionActivo && cargados < totalDeclarado
        ? "Completar pasajero"
        : "Agregar Pasajero";

    const slots = buildSlots(adultCount, childCount, infantCount, passengers);

    // Tanda 4 (2026-08-04, maqueta sección 7): resumen de cantidades arriba de la lista.
    // En Presupuesto ese mismo dato ya se ve — y se EDITA — en el casillero con auto-save
    // que ReservaDetailPage dibuja arriba de este componente (Tanda 3, P8c). Mostrarlo
    // OTRA VEZ acá adentro para Presupuesto duplicaría el mismo número dos veces en la
    // misma pantalla; por eso este resumen (de solo lectura, sin auto-save) aparece recién
    // desde En gestión en adelante, cuando la cantidad ya no se toca desde la ficha.
    const mostrarResumenCantidades = reserva?.status !== "Budget" && totalDeclarado > 0;

    return (
        <div>
            {mostrarResumenCantidades && (
                <div className="mb-4 flex flex-wrap gap-6" data-testid="resumen-cantidades-pasajeros">
                    <ResumenCantidad etiqueta="Adultos" valor={adultCount} />
                    <ResumenCantidad etiqueta="Menores" valor={childCount} />
                    <ResumenCantidad etiqueta="Infantes" valor={infantCount} />
                </div>
            )}

            {/* Encabezado con título, contador y botón de agregar */}
            <div className="flex flex-col sm:flex-row justify-between items-start sm:items-center gap-4 mb-4">
                <div>
                    <h3 className="text-lg font-medium text-gray-900 dark:text-white">Pasajeros del viaje</h3>

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
                    Feedback 2026-06-19: en Perdida/Cancelada/Finalizada no se pueden agregar pasajeros.
                    Frente 0 (2026-08-06): con la reserva EDITABLE pero bajo candado y el roster ya
                    completo, en vez de ocultarse queda TRAVADO con candadito (mismo patrón que
                    Editar/Borrar de un pasajero ya cargado) — abre la misma ventana de destrabar. */}
                {canEditPassengers && (
                    bloqueadoPorCandado ? (
                        <button
                            type="button"
                            onClick={onRequestEdit}
                            aria-label="Agregar pasajero — bloqueado, pedí autorización"
                            title={canAddPassenger?.reason || undefined}
                            data-testid="btn-agregar-pasajero-bloqueado"
                            className="w-full sm:w-auto flex items-center justify-center gap-2 rounded-[10px] border border-slate-200 bg-slate-100 px-4 py-2 text-sm font-semibold text-slate-400 transition-colors hover:bg-slate-200 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-500"
                        >
                            <Lock className="w-4 h-4" aria-hidden="true" /> Agregar Pasajero
                        </button>
                    ) : (
                        <button
                            onClick={() => setAgregandoPasajeroExtra(true)}
                            className="w-full sm:w-auto flex items-center justify-center gap-2 bg-primary text-primary-foreground px-4 py-2 rounded-[10px] hover:bg-primary/90 transition-colors shadow-sm"
                            data-testid="btn-agregar-pasajero"
                        >
                            <Plus className="w-4 h-4" /> {tituloBotonAgregar}
                        </button>
                    )
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
                <div className="text-center py-12 bg-gray-50 dark:bg-slate-800 rounded-[14px] border border-dashed border-gray-300 dark:border-slate-700">
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
                        // T5: esta fila está en modo edición — se reemplaza ENTERA por
                        // PasajeroInlineForm más abajo (ver el ternario que envuelve el renglón).
                        const esteSlotEditando = editandoIndex === index;
                        // F11 (D2, 2026-07-31, mockup firmado): chip fijo de vencimiento de
                        // pasaporte por fila. null cuando el motor no mandó alerta para este
                        // pasajero (pasaporte al día, sin vencimiento cargado, o slot vacío).
                        const chipPasaporte = tieneNombre ? construirChipPasaporte(slot.pasajero, reserva) : null;
                        // Semáforo DNI vencido (2026-08-03, spec firmada): hermano gemelo del chip de
                        // pasaporte de arriba. Va DESPUÉS en la fila (P1 firmado: pasaporte primero,
                        // DNI después) porque cada chip avisa de un documento distinto.
                        const chipDni = tieneNombre ? construirChipDni(slot.pasajero, reserva) : null;
                        // Chip "menor en tramo internacional" (decisión UX 2026-08-05 derivada de
                        // patrones firmados: P11=A ámbar + spec DNI 2026-08-03; label a validar): tercer
                        // chip de la fila, va DESPUÉS de pasaporte y DNI porque avisa de otra cosa
                        // (autorización de salida del país, no un documento vencido).
                        const chipMenor = tieneNombre ? construirChipMenor(slot.pasajero) : null;

                        return (
                            <div key={index}>
                                {/* T5 (2026-08-18): editando un pasajero ya cargado — la fila ENTERA
                                    se transforma en el formulario (no se abre una tarjeta aparte
                                    dejando el nombre viejo arriba, P-16). Los chips de vencimiento
                                    quedan afuera del DOM mientras se edita (P5=A: se esconden, vuelven
                                    al guardar/cancelar porque ahí termina esteSlotEditando). */}
                                {esteSlotEditando ? (
                                    <PasajeroInlineForm
                                        reservaId={reservaId}
                                        passengerToEdit={slot.pasajero}
                                        slotLabel={slot.etiqueta}
                                        mode="full"
                                        conFuncionesCompletas
                                        existingPassengers={passengers}
                                        onGuardado={() => {
                                            setEditandoIndex(null);
                                            onPasajeroGuardado?.();
                                        }}
                                        onCancelar={() => setEditandoIndex(null)}
                                    />
                                ) : (
                                <div
                                    className={`flex items-center gap-3 rounded-[10px] border px-4 py-3 transition-colors ${
                                        tieneNombre
                                            ? "border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900"
                                            : "border-amber-200 bg-amber-50/60 dark:border-amber-800/40 dark:bg-amber-950/10"
                                    }`}
                                    data-testid={`slot-pasajero-${index}`}
                                >
                                    {/* Avatar o icono vacío */}
                                    <div className={`flex-shrink-0 w-9 h-9 rounded-full flex items-center justify-center text-xs font-bold border ${
                                        tieneNombre
                                            ? "bg-blue-50 text-blue-600 border-blue-100 dark:bg-blue-900/30 dark:text-blue-400 dark:border-blue-800/50"
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
                                            {/* F11: chip fijo de pasaporte, mismo tratamiento visual que el
                                                chip "Vencida con deuda" de ReservaStatusChips.jsx (patrón
                                                existente, auditado antes de sumar uno nuevo). El texto largo
                                                del motor va en el title (tooltip). */}
                                            {chipPasaporte && (
                                                <span
                                                    data-testid={`chip-pasaporte-${chipPasaporte.key}-${index}`}
                                                    className={`px-2 py-0.5 rounded-full text-[11px] font-bold uppercase tracking-wider border ${chipPasaporte.className}`}
                                                    title={chipPasaporte.title}
                                                >
                                                    {chipPasaporte.label}
                                                </span>
                                            )}
                                            {/* Semáforo DNI vencido (P1 firmado: va después del de pasaporte,
                                                los dos conviven si el pasajero tiene ambos vencidos). */}
                                            {chipDni && (
                                                <span
                                                    data-testid={`chip-dni-vencido-${index}`}
                                                    className={`px-2 py-0.5 rounded-full text-[11px] font-bold uppercase tracking-wider border ${chipDni.className}`}
                                                    title={chipDni.title}
                                                >
                                                    {chipDni.label}
                                                </span>
                                            )}
                                            {/* Chip "menor en tramo internacional" (tercer chip, decisión
                                                UX 2026-08-05 derivada de patrones firmados): va después de
                                                pasaporte y DNI, sin reordenar nada. */}
                                            {chipMenor && (
                                                <span
                                                    data-testid={`chip-menor-autorizacion-${index}`}
                                                    className={`px-2 py-0.5 rounded-full text-[11px] font-bold uppercase tracking-wider border ${chipMenor.className}`}
                                                    title={chipMenor.title}
                                                >
                                                    {chipMenor.label}
                                                </span>
                                            )}
                                        </div>
                                        {/* Documento del pasajero (si existe) */}
                                        {tieneNombre && slot.pasajero.documentNumber && (
                                            <div className="text-[11px] text-slate-500 mt-0.5 uppercase">
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
                                                    // Tanda 4 (2026-08-04, maqueta sección 7): "cada iconito con su
                                                    // palabra" — antes estos dos candaditos eran mudos (sin texto
                                                    // visible al lado), y con la reserva confirmada el vendedor no
                                                    // podía saber qué tapaban sin pasar el mouse. Ahora dicen
                                                    // "Editar"/"Borrar" igual que el resto de la app, solo que grises.
                                                    <>
                                                        <button
                                                            type="button"
                                                            onClick={onRequestEdit}
                                                            aria-label="Editar pasajero — bloqueado, pedí autorización"
                                                            className="flex items-center gap-1 rounded-[10px] p-2 text-xs font-semibold text-slate-400 transition-colors hover:bg-slate-100 dark:text-slate-500 dark:hover:bg-slate-800"
                                                        >
                                                            <Lock className="w-4 h-4" aria-hidden="true" />
                                                            Editar
                                                        </button>
                                                        <button
                                                            type="button"
                                                            onClick={onRequestEdit}
                                                            aria-label="Borrar pasajero — bloqueado, pedí autorización"
                                                            className="flex items-center gap-1 rounded-[10px] p-2 text-xs font-semibold text-slate-400 transition-colors hover:bg-slate-100 dark:text-slate-500 dark:hover:bg-slate-800"
                                                        >
                                                            <Lock className="w-4 h-4" aria-hidden="true" />
                                                            Borrar
                                                        </button>
                                                    </>
                                                ) : (
                                                <>
                                                    <button
                                                        type="button"
                                                        onClick={() => setEditandoIndex(index)}
                                                        aria-label="Editar pasajero"
                                                        className="flex items-center gap-1 rounded-[10px] p-2 text-xs font-semibold text-primary transition-colors hover:bg-primary/10"
                                                    >
                                                        <Edit2 className="w-4 h-4" />
                                                        Editar
                                                    </button>
                                                    <button
                                                        type="button"
                                                        onClick={() => onDeletePassenger(getPublicId(slot.pasajero))}
                                                        aria-label="Borrar pasajero"
                                                        className="flex items-center gap-1 rounded-[10px] p-2 text-xs font-semibold text-rose-600 transition-colors hover:bg-rose-50 dark:hover:bg-rose-900/40"
                                                    >
                                                        <Trash2 className="w-4 h-4" />
                                                        Borrar
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
                                                    className="inline-flex items-center gap-1.5 rounded-[10px] border border-amber-400 bg-amber-100 px-3 py-1.5 text-xs font-bold text-amber-700 transition-colors hover:bg-amber-200 dark:border-amber-700 dark:bg-amber-900/30 dark:text-amber-300 dark:hover:bg-amber-900/50"
                                                >
                                                    <Plus className="w-3.5 h-3.5" />
                                                    Cargar
                                                </button>
                                            )
                                        ) : null /* estado terminal: solo lectura, sin botones */}
                                    </div>
                                </div>
                                )}

                                {/* Mini-formulario inline: solo se despliega para el slot abierto
                                    Y cuando canEditPassengers=true (no en solo lectura terminal).
                                    conFuncionesCompletas (T5): "Cargar" de un slot vacío es un alta
                                    desde la ficha — mismas funciones que cualquier otro alta
                                    (lupa AFIP, histórico, "+ Más detalles"). */}
                                {esteSlotAbierto && !tieneNombre && canEditPassengers && (
                                    <div className="mt-1 ml-4">
                                        <PasajeroInlineForm
                                            reservaId={reservaId}
                                            passengerToEdit={slot.pasajero}
                                            slotLabel={slot.etiqueta}
                                            mode="full"
                                            conFuncionesCompletas
                                            existingPassengers={passengers}
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

            {/* T5 (2026-08-18, P4=A): "Agregar Pasajero" (uno DE MÁS, no un slot declarado)
                abre el alta AL FINAL de la lista — mismo lugar donde después va a quedar,
                no reordena a los pasajeros ya cargados arriba. Con histórico activo (alta). */}
            {agregandoPasajeroExtra && canEditPassengers && (
                <div className="mt-2">
                    <PasajeroInlineForm
                        reservaId={reservaId}
                        passengerToEdit={null}
                        slotLabel={`Pasajero ${slots.length + 1}`}
                        mode="full"
                        conFuncionesCompletas
                        existingPassengers={passengers}
                        onGuardado={() => {
                            setAgregandoPasajeroExtra(false);
                            onPasajeroGuardado?.();
                        }}
                        onCancelar={() => setAgregandoPasajeroExtra(false)}
                    />
                </div>
            )}
        </div>
    );
}
