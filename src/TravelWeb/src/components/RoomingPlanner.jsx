import { useState, useEffect } from "react";
import { Users, User, UserPlus, Info } from "lucide-react";

export default function RoomingPlanner({ rooms, reservaPax, value, onChange }) {
    const [assignments, setAssignments] = useState([]);

    useEffect(() => {
        // Parse incoming value or initialize
        if (value) {
            try {
                const parsed = JSON.parse(value);
                // Si la longitud coincide o hay datos, usamos eso
                if (Array.isArray(parsed) && parsed.length > 0) {
                    // Ajustar si la cantidad de habitaciones cambia
                    let adjusted = [...parsed];
                    if (adjusted.length < rooms) {
                        for (let i = adjusted.length; i < rooms; i++) {
                            adjusted.push({ roomIndex: i + 1, paxIds: [] });
                        }
                    } else if (adjusted.length > rooms) {
                        adjusted = adjusted.slice(0, rooms);
                        // Los que quedaron fuera quedan "sin asignar"
                    }
                    setAssignments(adjusted);
                    return;
                }
            } catch (e) {
                // ignore
            }
        }
        
        // Initialize empty assignments for N rooms
        const initial = Array.from({ length: rooms || 1 }).map((_, i) => ({
            roomIndex: i + 1,
            paxIds: []
        }));
        setAssignments(initial);
    }, [rooms, value]);

    const handleAssign = (roomIndex, paxId) => {
        const newAssignments = assignments.map(room => {
            // Remove pax from any room first
            const newPaxIds = room.paxIds.filter(id => id !== paxId);
            
            // Add to selected room
            if (room.roomIndex === roomIndex) {
                newPaxIds.push(paxId);
            }
            return { ...room, paxIds: newPaxIds };
        });
        
        setAssignments(newAssignments);
        onChange(JSON.stringify(newAssignments));
    };

    const handleUnassign = (paxId) => {
        const newAssignments = assignments.map(room => ({
            ...room,
            paxIds: room.paxIds.filter(id => id !== paxId)
        }));
        setAssignments(newAssignments);
        onChange(JSON.stringify(newAssignments));
    };

    const assignedPaxIds = assignments.flatMap(r => r.paxIds);
    const unassignedPax = reservaPax?.filter(p => !assignedPaxIds.includes(p.publicId)) || [];

    if (!reservaPax || reservaPax.length === 0) {
        return (
            <div className="rounded-xl border border-amber-200 bg-amber-50 p-4 dark:border-amber-900/50 dark:bg-amber-900/20">
                <div className="flex items-center gap-2 text-amber-800 dark:text-amber-200">
                    <Info className="h-5 w-5" />
                    <span className="text-sm font-medium">No hay pasajeros cargados.</span>
                </div>
                <p className="mt-1 text-xs text-amber-700 dark:text-amber-300">
                    Agrega pasajeros a la reserva primero para poder asignarlos a las habitaciones.
                </p>
            </div>
        );
    }

    return (
        <div className="space-y-4">
            <h4 className="flex items-center gap-2 text-sm font-bold text-slate-800 dark:text-slate-200">
                <Users className="h-4 w-4 text-indigo-500" />
                Distribución de Habitaciones (Rooming)
            </h4>

            {/* Habitaciones */}
            <div className="grid gap-3 sm:grid-cols-2">
                {assignments.map((room) => (
                    <div key={room.roomIndex} className="rounded-xl border border-slate-200 bg-slate-50 p-3 dark:border-slate-700 dark:bg-slate-800/50">
                        <h5 className="mb-2 text-xs font-bold uppercase text-slate-500 dark:text-slate-400">
                            Habitación {room.roomIndex}
                        </h5>
                        <div className="space-y-2">
                            {room.paxIds.map(paxId => {
                                const pax = reservaPax.find(p => p.publicId === paxId);
                                if (!pax) return null;
                                return (
                                    <div key={paxId} className="flex items-center justify-between rounded-lg border border-slate-200 bg-white p-2 text-sm shadow-sm dark:border-slate-700 dark:bg-slate-900">
                                        <div className="flex items-center gap-2">
                                            <User className="h-4 w-4 text-slate-400" />
                                            <span className="font-medium text-slate-700 dark:text-slate-200">{pax.fullName}</span>
                                        </div>
                                        <button 
                                            type="button" 
                                            onClick={() => handleUnassign(paxId)}
                                            className="text-xs text-red-500 hover:text-red-700 dark:text-red-400 dark:hover:text-red-300"
                                        >
                                            Quitar
                                        </button>
                                    </div>
                                );
                            })}
                            
                            {/* Opciones para agregar */}
                            {unassignedPax.length > 0 && (
                                <select 
                                    className="w-full rounded-lg border border-slate-200 bg-white p-2 text-sm text-slate-600 focus:border-indigo-500 focus:outline-none dark:border-slate-700 dark:bg-slate-900 dark:text-slate-300"
                                    value=""
                                    onChange={(e) => {
                                        if (e.target.value) handleAssign(room.roomIndex, e.target.value);
                                    }}
                                >
                                    <option value="">+ Asignar pasajero...</option>
                                    {unassignedPax.map(p => (
                                        <option key={p.publicId} value={p.publicId}>{p.fullName}</option>
                                    ))}
                                </select>
                            )}
                        </div>
                    </div>
                ))}
            </div>

            {/* Pasajeros sin asignar */}
            {unassignedPax.length > 0 && (
                <div className="rounded-lg border border-slate-200 bg-white p-3 dark:border-slate-700 dark:bg-slate-900">
                    <h5 className="mb-2 text-xs font-bold uppercase text-slate-500 dark:text-slate-400">Pasajeros sin asignar</h5>
                    <div className="flex flex-wrap gap-2">
                        {unassignedPax.map(p => (
                            <div key={p.publicId} className="flex items-center gap-1.5 rounded-full bg-slate-100 px-3 py-1 text-xs font-medium text-slate-600 dark:bg-slate-800 dark:text-slate-300">
                                <UserPlus className="h-3.5 w-3.5" />
                                {p.fullName}
                            </div>
                        ))}
                    </div>
                </div>
            )}
        </div>
    );
}
