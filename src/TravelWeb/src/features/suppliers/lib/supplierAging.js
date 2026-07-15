export function supplierDueState(suggestedDueDate, now = new Date()) {
  if (!suggestedDueDate) return null;
  const dateOnly = typeof suggestedDueDate === "string"
    ? suggestedDueDate.match(/^(\d{4})-(\d{2})-(\d{2})/)
    : null;
  const due = dateOnly
    ? new Date(Number(dateOnly[1]), Number(dateOnly[2]) - 1, Number(dateOnly[3]))
    : new Date(suggestedDueDate);
  if (Number.isNaN(due.getTime())) return null;
  const today = new Date(now.getFullYear(), now.getMonth(), now.getDate());
  const dueDay = new Date(due.getFullYear(), due.getMonth(), due.getDate());
  const days = Math.round((dueDay - today) / 86_400_000);
  if (days < 0) return { tone: "overdue", label: `Vencida hace ${Math.abs(days)} día${Math.abs(days) === 1 ? "" : "s"}` };
  if (days === 0) return { tone: "today", label: "Vence hoy" };
  if (days <= 7) return { tone: "soon", label: `Vence en ${days} día${days === 1 ? "" : "s"}` };
  return { tone: "future", label: `Vence ${due.toLocaleDateString("es-AR")}` };
}
