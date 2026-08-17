/**
 * Bandeja "Repetidos" (spec firmada 2026-08-07, §6 / V11=B): el bibliotecario ya unió
 * solo lo obvio; acá quedan los "casi seguros" para que una persona decida. Un producto
 * arriba (el que se queda) y, debajo, todos los que se le parecen — cada uno con
 * [ Es el mismo ] / [ Es otro ].
 *
 * Pantalla de REVISIÓN (no de trabajo diario): por eso es la única parte del Tarifario
 * donde se ve el rastro de lo que el sistema hizo solo ("Ver qué ordenó").
 *
 * Permiso `tarifario.edit` (fix ronda 2 de review, P-9): los endpoints de unir/descartar
 * piden ese permiso en el servidor, igual que renombrar un producto. Sin él, los botones
 * ni se muestran — mismo patrón que ya usa ProductInlineEditForm — así nadie con solo
 * `tarifario.view` llega a tocar un botón que el servidor le va a rechazar con un 403.
 */
import { useEffect, useState } from "react";
import { api } from "../../../api";
import { showError, showSuccess } from "../../../alerts";
import { hasPermission } from "../../../auth";
import { ListEmptyState } from "../../../components/ui/ListEmptyState";
import { ListLoadErrorState } from "../../../components/ui/ListLoadErrorState";
import { Button } from "../../../components/ui/button";
import { quitarCandidatoResuelto } from "../lib/duplicatesTrayLogic";
import { TidyUpLogPanel } from "./TidyUpLogPanel";

export function DuplicatesTray({ onRepetidosCambiaron }) {
    const puedeEditar = hasPermission("tarifario.edit");
    const [groups, setGroups] = useState([]);
    const [tidiedUpThisWeek, setTidiedUpThisWeek] = useState(0);
    const [loading, setLoading] = useState(true);
    const [loadError, setLoadError] = useState(false);
    // Candidato en pleno "Uniendo…" (spec §9): sus dos botones quedan apagados mientras
    // el pedido está en vuelo, para que un doble click no dispare dos fusiones.
    const [resolviendoRatePublicId, setResolviendoRatePublicId] = useState(null);
    const [mostrarRegistro, setMostrarRegistro] = useState(false);

    const cargar = async () => {
        setLoading(true);
        setLoadError(false);
        try {
            const data = await api.get("/rates/duplicates");
            const gruposDelServidor = data?.groups || [];
            setGroups(gruposDelServidor);
            setTidiedUpThisWeek(data?.tidiedUpThisWeek || 0);
            // Retoque ronda 3: esta función también se usa para el "Probar de nuevo" del
            // error y para refrescar tras un Deshacer (ver onUndone en TidyUpLogPanel más
            // abajo) — sin avisar acá, el badge "Repetidos (N)" de la cabecera quedaba con
            // el número de la primera carga aunque la bandeja ya hubiera cambiado.
            onRepetidosCambiaron?.(gruposDelServidor.length);
        } catch {
            setLoadError(true);
        } finally {
            setLoading(false);
        }
    };

    useEffect(() => {
        cargar();
    }, []);

    // Saca el candidato resuelto de `groups` en memoria (actualización optimista, spec
    // §9) Y avisa al padre cuántos grupos quedan — el badge "Repetidos (N)" de la cabecera
    // se pidió UNA vez al entrar al Tarifario y, sin este aviso, quedaba desactualizado
    // toda la visita aunque acá adentro se resolvieran todos los grupos (hallazgo review).
    //
    // Fix ronda 4: el largo se calcula ACÁ AFUERA, antes de `setGroups`, en vez de adentro
    // del updater — un updater de React puede correr más de una vez por la misma acción
    // (StrictMode en desarrollo lo duplica a propósito para detectar efectos impuros), y
    // `onRepetidosCambiaron` es un aviso al padre, no debe repetirse.
    const actualizarGruposTrasResolver = (survivorPublicId, candidateRatePublicId) => {
        const nuevosGrupos = quitarCandidatoResuelto(groups, survivorPublicId, candidateRatePublicId);
        setGroups(nuevosGrupos);
        onRepetidosCambiaron?.(nuevosGrupos.length);
    };

    const handleEsElMismo = async (group, candidate) => {
        setResolviendoRatePublicId(candidate.ratePublicId);
        try {
            await api.post("/rates/duplicates/merge", {
                survivorPublicId: group.survivorPublicId,
                absorbedPublicId: candidate.ratePublicId,
            });
            // Spec §9 "Unido OK: el renglón desaparece del grupo + 'Listo, quedó uno solo.'"
            actualizarGruposTrasResolver(group.survivorPublicId, candidate.ratePublicId);
            showSuccess("Listo, quedó uno solo.");
        } catch (err) {
            // Spec §9 "Error al unir: el grupo queda como estaba": no tocamos `groups`,
            // solo avisamos con un toast — la fila sigue ahí para reintentar.
            showError(err.payload?.message || "No se pudo unir. Probá de nuevo.");
        } finally {
            setResolviendoRatePublicId(null);
        }
    };

    const handleEsOtro = async (group, candidate) => {
        setResolviendoRatePublicId(candidate.ratePublicId);
        try {
            await api.post("/rates/duplicates/not-duplicates", {
                firstPublicId: group.survivorPublicId,
                secondPublicId: candidate.ratePublicId,
            });
            actualizarGruposTrasResolver(group.survivorPublicId, candidate.ratePublicId);
        } catch (err) {
            showError(err.payload?.message || "No se pudo guardar. Probá de nuevo.");
        } finally {
            setResolviendoRatePublicId(null);
        }
    };

    if (loading) {
        return (
            <div className="space-y-2 p-6" data-testid="duplicates-tray-loading">
                {Array.from({ length: 3 }).map((_, index) => (
                    <div key={index} className="h-12 animate-pulse rounded-[10px] bg-slate-100 dark:bg-slate-800" />
                ))}
            </div>
        );
    }

    if (loadError) {
        return (
            <div className="p-6">
                <ListLoadErrorState message="No se pudo cargar la bandeja de repetidos." onRetry={cargar} />
            </div>
        );
    }

    if (groups.length === 0) {
        return <ListEmptyState title="No hay productos para revisar." />;
    }

    return (
        <div className="divide-y divide-slate-100 dark:divide-slate-800" data-testid="duplicates-tray">
            {groups.map((group) => (
                <div key={group.survivorPublicId} className="px-6 py-4">
                    <p className="font-semibold text-slate-900 dark:text-white">
                        {group.survivorName}
                        {group.survivorSubtitle && <span className="font-normal text-slate-500 dark:text-slate-400"> · {group.survivorSubtitle}</span>}
                        {" · "}
                        {group.survivorPriceCount} {group.survivorPriceCount === 1 ? "precio" : "precios"}
                    </p>
                    <p className="mt-2 text-xs font-semibold uppercase tracking-wide text-slate-400">Se le parecen</p>
                    <div className="mt-1 space-y-2">
                        {group.candidates.map((candidate) => {
                            const resolviendo = resolviendoRatePublicId === candidate.ratePublicId;
                            return (
                                <div key={candidate.ratePublicId} className="flex flex-col gap-1 rounded-[10px] border border-slate-100 px-3 py-2 dark:border-slate-800 sm:flex-row sm:items-center sm:justify-between">
                                    <div className="min-w-0">
                                        <p className="text-sm text-slate-700 dark:text-slate-200">
                                            {candidate.name}
                                            {candidate.subtitle && <span className="text-slate-500 dark:text-slate-400"> · {candidate.subtitle}</span>}
                                            {" · "}
                                            {candidate.priceCount} {candidate.priceCount === 1 ? "precio" : "precios"}
                                        </p>
                                        {/* Única aclaración permitida en la bandeja (V14): explica qué
                                            va a pasar con la habitación que venía metida en el nombre viejo. */}
                                        {candidate.variantLabelToRescue && (
                                            <p className="text-xs text-slate-400">
                                                la habitación pasaría a ser "{candidate.variantLabelToRescue}"
                                            </p>
                                        )}
                                    </div>
                                    {/* Sin tarifario.edit los botones ni se muestran (fix ronda 2, P-9):
                                        los dos endpoints piden ese permiso en el servidor. */}
                                    {puedeEditar && (
                                        <div className="flex shrink-0 gap-2">
                                            {/* B.3: acción por fila repetida N veces — nunca relleno azul
                                                repetido; outline como toda acción de fila del sistema. */}
                                            <Button
                                                type="button"
                                                variant="outline"
                                                size="sm"
                                                onClick={() => handleEsElMismo(group, candidate)}
                                                disabled={resolviendo}
                                            >
                                                {resolviendo ? "Uniendo…" : "Es el mismo"}
                                            </Button>
                                            <Button
                                                type="button"
                                                variant="outline"
                                                size="sm"
                                                onClick={() => handleEsOtro(group, candidate)}
                                                disabled={resolviendo}
                                            >
                                                Es otro
                                            </Button>
                                        </div>
                                    )}
                                </div>
                            );
                        })}
                    </div>
                </div>
            ))}

            <div className="px-6 py-4">
                <button
                    type="button"
                    onClick={() => setMostrarRegistro((prev) => !prev)}
                    className="text-sm font-semibold text-slate-500 hover:text-primary dark:text-slate-400"
                >
                    Ordenados y unidos por el sistema esta semana: {tidiedUpThisWeek} — Ver qué ordenó
                </button>
                {mostrarRegistro && (
                    <TidyUpLogPanel
                        onClose={() => setMostrarRegistro(false)}
                        // Deshacer una unión puede resucitar un candidato que ya había
                        // desaparecido de la bandeja de arriba (fix ronda 2, hallazgo
                        // review): sin este refresco, quedaba escondido hasta el próximo
                        // F5.
                        onUndone={cargar}
                    />
                )}
            </div>
        </div>
    );
}
