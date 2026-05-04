export function getReservaArchiveBlockReason(reserva) {
  if (!reserva) {
    return "No se pudo evaluar la reserva.";
  }

  if (reserva.status === "Archived") {
    return "La reserva ya esta archivada.";
  }

  if (reserva.status !== "Traveling" && reserva.status !== "Closed") {
    return "Solo se pueden archivar reservas en viaje o finalizadas.";
  }

  const balance = Number(reserva.balance || 0);
  if (Math.round(balance * 100) / 100 > 0) {
    return "No se puede archivar una reserva con saldo pendiente.";
  }

  return null;
}

export function canArchiveReserva(reserva) {
  return !getReservaArchiveBlockReason(reserva);
}
