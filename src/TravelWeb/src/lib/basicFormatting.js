/**
 * Parser minimo y seguro para el formato usado por el timeline.
 *
 * Devuelve tokens de texto; el componente React los renderiza como nodos normales,
 * por lo que caracteres como `<`, `>` y atributos HTML nunca se interpretan como DOM.
 * Solo reconoce **negrita** y *italica*, igual que el renderer historico.
 */
export function parseBasicFormatting(value) {
  const text = String(value ?? "");
  const tokens = [];
  const pattern = /\*\*(.+?)\*\*|\*(.+?)\*/g;
  let lastIndex = 0;
  let match;

  while ((match = pattern.exec(text)) !== null) {
    if (match.index > lastIndex) {
      tokens.push({ style: "text", text: text.slice(lastIndex, match.index) });
    }

    tokens.push({
      style: match[1] !== undefined ? "bold" : "italic",
      text: match[1] ?? match[2],
    });
    lastIndex = pattern.lastIndex;
  }

  if (lastIndex < text.length) {
    tokens.push({ style: "text", text: text.slice(lastIndex) });
  }

  return tokens;
}
