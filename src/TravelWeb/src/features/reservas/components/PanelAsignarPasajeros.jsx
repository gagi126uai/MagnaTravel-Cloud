/**
 * Panel inline "¿Quiénes van en este servicio?" (Pieza A — ADR-031 v2.1).
 *
 * Se despliega DENTRO de la fila del servicio (nunca como modal flotante).
 * Muestra la lista de pasajeros de la reserva con tildes para elegir quiénes
 * van en ese servicio particular.
 *
 * Reglas de negocio (guía UX 2026-06-15 tarde):
 *   - Por defecto todos los pasajeros están tildados (el default es "Para: Todos").
 *   - Destildar = crear asignaciones explícitas solo para los tildados.
 *   - Volver a todos tildados = borrar asignaciones (vuelve al default "todos").
 *   - No se puede acotar hasta que existan pasajeros con nombre cargado.
 *
 * Contrato de backend (B2 — guardado atómico):
 *   - PUT /api/reservas/{id}/services/{serviceType}/{servicePublicId}/assignments
 *     Body: { passengerPublicIds: ["<guid>", ...] }
 *     - Lista vacía o lista con TODOS los pasajeros → "Para: Todos" (backend no persiste asignaciones).
 *     - Subconjunto estricto → "Para: X de N" (hasExplicitAssignments = true).
 *     Respuesta 200: ServiceNominalCoverageDto (mismo shape que GET nominal-coverage).
 *   - La respuesta del PUT se usa directamente para actualizar el estado SIN re-pedir.
 *     El reintento es idempotente: el mismo set de tildes produce el mismo resultado.
 *
 * Inicialización de tildes (B2 — desde coverage, no desde GET /assignments):
 *   - coverage.hasExplicitAssignments = false → todos tildados
 *   - coverage.hasExplicitAssignments = true  → solo los de coverage.serviceSet tildados
 *   - Esto evita el bug de matching por servicePublicId null que tenía el GET anterior.
 *
 * Props:
 *   reservaId       — publicId de la reserva
 *   serviceType     — tipo en formato backend ("Hotel", "Flight", etc.)
 *   servicePublicId — publicId del servicio
 *   pasajeros       — array de pasajeros de la reserva (los que ya tienen nombre)
 *   coverage        — ServiceNominalCoverageDto del backend (puede ser null mientras carga)
 *   onListo         — callback(nuevaCoverage) cuando el usuario aprieta [Listo] con éxito
 *   onCancelar      — callback() cuando el usuario aprieta [Cancelar]
 */

import React, { useState, useEffect } from "react";
import { Loader2, Users } from "lucide-react";
import { api } from "../../../api";
import { getApiErrorMessage } from "../../../lib/errors";
// Helpers de lógica pura en archivo separado para que los tests de Node puedan importarlos
// sin transpiler de JSX (los tests usan node --test sobre .mjs, sin Vite/Babel).
import { inicializarTildados, armarPayloadPut } from "../lib/panelAsignarPasajerosHelpers";

export function PanelAsignarPasajeros({
    reservaId,
    serviceType,
    servicePublicId,
    pasajeros,
    coverage,
    onListo,
    onCancelar,
}) {
    // Estado de guardado: mientras se guarda, se deshabilitan los controles
    const [guardando, setGuardando] = useState(false);

    // Error de guardado: si falla el PUT, se muestra aquí.
    // El usuario NO pierde los tildes — puede reintentar con el mismo set.
    const [errorGuardando, setErrorGuardando] = useState(null);

    // Set de publicIds de pasajeros que el usuario tiene tildados localmente.
    // Se inicializa a partir del coverage que viene del padre (no re-pedimos al backend).
    const [tildados, setTildados] = useState(() => inicializarTildados(coverage, pasajeros));

    // Si el coverage cambia mientras el panel está abierto (por ejemplo si el padre
    // llama a refetch), re-sincronizamos los tildes para que estén al día.
    // Solo lo hacemos si el usuario NO está guardando (no pisar estado en vuelo).
    useEffect(() => {
        if (!guardando) {
            setTildados(inicializarTildados(coverage, pasajeros));
        }
    // Dependemos de coverage y pasajeros. guardando como guard para no pisar mid-flight.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [coverage]);

    const handleToggle = (pasajeroPublicId) => {
        const key = String(pasajeroPublicId || "").toLowerCase();
        setTildados(prev => {
            const next = new Set(prev);
            if (next.has(key)) {
                // Regla de negocio: no se puede dejar 0 tildados (al menos 1 va al servicio)
                if (next.size <= 1) return prev;
                next.delete(key);
            } else {
                next.add(key);
            }
            return next;
        });
    };

    const handleListo = async () => {
        if (!tildados) return;
        setGuardando(true);
        setErrorGuardando(null);

        try {
            // Armamos el payload con la función exportada (la misma que prueban los tests).
            // Lista vacía = "Para: Todos"; ids específicos = subconjunto explícito.
            const passengerPublicIds = armarPayloadPut(tildados, pasajeros);

            // PUT atómico: el backend reemplaza todas las asignaciones en una sola operación.
            // La respuesta es el nuevo ServiceNominalCoverageDto, listo para usar directamente.
            const respuesta = await api.put(
                `/reservas/${reservaId}/services/${serviceType}/${servicePublicId}/assignments`,
                { passengerPublicIds }
            );

            // Usamos la coverage que devuelve el PUT directamente: no re-pedimos.
            // El padre actualiza su estado con esta nueva coverage.
            const nuevaCoverage = respuesta.data;
            onListo?.(nuevaCoverage);
        } catch (err) {
            // Si falla, el estado de tildes se conserva intacto → el usuario puede reintentar
            // con el mismo set. El PUT es idempotente: mismos ids → mismo resultado.
            setErrorGuardando(getApiErrorMessage(err, "No se pudo guardar. Reintentá."));
        } finally {
            setGuardando(false);
        }
    };

    return (
        <div
            className="mt-2 rounded-xl border border-indigo-200 bg-indigo-50/60 px-4 py-3 dark:border-indigo-800/40 dark:bg-indigo-950/10"
            data-testid="panel-asignar-pasajeros"
        >
            <div className="mb-2 flex items-center gap-2 text-xs font-bold uppercase tracking-wider text-indigo-700 dark:text-indigo-400">
                <Users className="h-4 w-4" aria-hidden="true" />
                ¿Quiénes van en este servicio?
            </div>

            {/* Lista de pasajeros con tildes */}
            <div className="space-y-1 mb-3">
                {pasajeros.map((pax) => {
                    const publicId = String(pax.publicId || pax.PassengerPublicId || "").toLowerCase();
                    const esTildado = tildados?.has(publicId) ?? true;
                    const inputId = `asignar-pax-${servicePublicId}-${publicId}`;

                    return (
                        <label
                            key={publicId}
                            htmlFor={inputId}
                            className="flex items-center gap-3 rounded-lg px-2 py-1.5 cursor-pointer hover:bg-indigo-100/60 dark:hover:bg-indigo-900/20 transition-colors"
                        >
                            <input
                                type="checkbox"
                                id={inputId}
                                checked={esTildado}
                                onChange={() => handleToggle(publicId)}
                                disabled={guardando}
                                className="h-4 w-4 rounded border-indigo-300 text-indigo-600 focus:ring-indigo-500 dark:border-indigo-700"
                            />
                            <span className="text-sm text-slate-800 dark:text-slate-200 font-medium">
                                {pax.fullName || pax.FullName || "(sin nombre)"}
                            </span>
                            {/* Indicador "faltan datos" si el coverage nos lo dice.
                                Solo aparece si el backend reporta que este pasajero
                                no tiene los datos requeridos para el tipo de servicio. */}
                            {coverage?.serviceSet && (() => {
                                const pubIdStr = String(pax.publicId || pax.PassengerPublicId || "");
                                const paxEnSet = coverage.serviceSet.find(
                                    s => String(s.passengerPublicId).toLowerCase() === pubIdStr.toLowerCase()
                                );
                                if (paxEnSet && !paxEnSet.hasRequiredDataForServiceType) {
                                    return (
                                        <span className="ml-auto text-[10px] text-amber-600 dark:text-amber-400 font-semibold">
                                            faltan datos
                                        </span>
                                    );
                                }
                                return null;
                            })()}
                        </label>
                    );
                })}
            </div>

            {/* Cartel de error: si el PUT falló, mostramos el mensaje aquí.
                El usuario NO pierde lo que tildó — puede reintentar con el mismo botón.
                El texto del botón cambia de "Listo" a "Reintentar" como pista visual. */}
            {errorGuardando && (
                <div
                    role="alert"
                    className="mb-3 rounded-lg border border-rose-200 bg-rose-50 px-3 py-2 text-xs text-rose-700 dark:border-rose-800/40 dark:bg-rose-950/20 dark:text-rose-300"
                >
                    {errorGuardando}
                </div>
            )}

            <div className="flex gap-2">
                <button
                    type="button"
                    onClick={onCancelar}
                    disabled={guardando}
                    className="rounded-lg px-4 py-1.5 text-xs font-bold text-slate-600 hover:bg-slate-100 disabled:opacity-50 transition-colors dark:text-slate-300 dark:hover:bg-slate-800"
                >
                    Cancelar
                </button>
                <button
                    type="button"
                    onClick={handleListo}
                    disabled={guardando || !tildados}
                    data-testid="btn-listo-asignacion"
                    className="flex items-center gap-1.5 rounded-lg bg-indigo-600 px-4 py-1.5 text-xs font-bold text-white transition-colors hover:bg-indigo-700 disabled:opacity-50"
                >
                    {guardando && <Loader2 className="h-3 w-3 animate-spin" aria-hidden="true" />}
                    {errorGuardando ? "Reintentar" : "Listo"}
                </button>
            </div>
        </div>
    );
}
