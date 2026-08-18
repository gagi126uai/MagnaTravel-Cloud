# Guía de rollout del estándar visual (tandas D1..D7)

> **Fecha:** 2026-08-16 · Complementa `docs/ux/2026-08-11-estandar-visual-y-lavado-de-cara.md`
> (el documento firmado, con TODA la letra chica) y su enmienda del 16/08 (se retiró el sello).
> Esta guía es solo el **resumen operativo**: qué molde usar para cada cosa y qué está prohibido.
> Ante cualquier duda, manda el documento firmado, no esta guía.

## Los moldes ya construidos (Tanda D0) — usar SIEMPRE estos, no dibujar a mano

| Necesito... | Uso este molde | Dónde vive |
|---|---|---|
| Botón principal (nivel 1, UNO por pantalla) | `<Button variant="default">` | `components/ui/button.jsx` |
| Botón secundario (nivel 2) | `<Button variant="outline">` | idem |
| Salida lateral / terciario (nivel 3, "Perdida", "Archivar", "⋯") | `<Button variant="ghost">` | idem |
| Acción destructiva discreta (nivel 4, "Anular", "Borrar") | `<Button variant="destructive">` | idem — nunca relleno rojo, siempre pide confirmación (P-14) |
| Botón chico dentro de una fila de tabla | agregar `size="sm"` (32px) a cualquiera de los de arriba | idem |
| Botón apagado (nivel 5, "el motor no lo permite") | prop nativa `disabled` + el motivo escrito al lado en 11px gris (P-9, nunca en `title`) | responsabilidad de cada pantalla |
| Chip de estado nuevo (Clientes, Cobranzas, Caja, Proveedores, Tarifario, CRM) | `<StatusChip tone="neutro\|azul\|ambar\|verde\|rojo">` | `components/ui/badge.jsx` |
| Chip de estado de Reserva (ya existe, no se toca) | `ReservaStatusBadge` | `features/reservas/components/ReservaStatusBadge.jsx` |
| Cartelito de moneda ($/US$) | `CurrencyBadge` (ya gris neutro, no compite con el azul de acción) | `components/ui/CurrencyBadge.jsx` |
| Color de acción / foco de teclado | token `--primary` (azul boleto `#1D4ED8`, mismo en claro y oscuro) | `src/TravelWeb/src/styles.css` |

## Tipografía (B.2) — tres roles, una sola familia (Inter)

- **Título**: 24px / 20px, peso 700. *(Enmienda firmada 2026-08-18: la guía decía 800,
  pero todas las páginas migradas se construyeron en 700 y Gastón eligió dejar 700 como
  estándar y corregir acá — la letra ahora refleja lo construido. Patrón de referencia:
  `text-2xl font-bold tracking-tight`.)*
- **Cuerpo**: 14px, peso 400/600. Etiquetas de columna: 11px mayúsculas, gris dato.
- **Datos (plata y fechas)**: 14px peso 600 (22px para el número grande de la ficha), cifras
  monoespaciadas para que las comas queden alineadas en columna.
- Tamaños prohibidos: cualquiera que no sea **11 · 12 · 14 · 16 · 20 · 24**. Nada de 10px ni 13px.

## Espaciado (B.4)

- Escala única: **4 · 8 · 12 · 16 · 24 · 32 · 48**. Nada de 5, 6, 10, 18, 20.
- Ancho máximo de contenido: 1440px, 24px de aire a los costados.
- Padding de tarjeta: 20px · separación entre tarjetas: 24px.
- Un solo canal de alineación a la izquierda: volver/título/cliente/chips/avisos/solapas arrancan
  todos en la misma línea vertical.

## Tabla (B.5)

- Encabezado 11px mayúsculas gris dato, sin fondo de color, línea abajo.
- Fila de 56px mínimo, separada por línea de 1px, se pinta gris clarito al pasar el mouse.
- Texto a la izquierda, **importes a la derecha**, una moneda por renglón (P-3 — pesos y dólares
  nunca se suman ni se mezclan en la misma celda).
- Una sola acción por fila, a la derecha, ícono + palabra (P-10). Si está apagada, el motivo abajo
  en 11px, máximo 2 renglones, sin ensanchar la columna.

## Orden de módulos firmado para las próximas tandas

1. **Clientes**
2. **Cobranzas / Facturación**
3. **Caja**
4. **Proveedores**
5. **Tarifario**
6. **CRM**
7. **Dashboard / Ajustes / topbar**

## Prohibido (sección E del estándar firmado — repaso corto)

1. **No tocar los huesos**: no se agrega, saca ni reordena ningún campo, botón, solapa o aviso.
   Esto es piel, no estructura. Si algo parece sobrar, se pregunta antes.
2. **No inventar colores.** Solo los de B.1 (tinta / gris dato / papel / mesa / línea / azul
   boleto + ámbar-verde-rojo de significado). Si hace falta un color que no está, el diseño está
   mal: se pregunta.
3. **No escribir botones a mano.** Todo botón nuevo o tocado sale de `button.jsx`. Cero
   `bg-cyan-600` / `bg-emerald-600` / clases de color sueltas.
4. **No esconder el motivo de un botón apagado** (P-9) ni meterlo en un `title`.
5. **No agregar leyendas ni cartelitos aclarativos nuevos** a los formularios (P-15).
6. **No mover ninguna ficha de trabajo a ventana flotante** (P-5) ni convertir errores en globitos
   que se van solos (P-6).
7. **No tocar la ✨ de la IA**: sigue discreta, una línea, sin caja nueva ni color fuerte.
8. **No usar emojis como íconos de acción.** Un solo juego de íconos dibujados (lucide-react).
9. **No cambiar palabras** que no estén ya firmadas. "Cancelar" ≠ "Anular" sigue firme.
10. **No recrear el sello.** Se retiró por completo el 16/08 (enmienda al estándar): todos los
    estados de Reserva, incluidos Anulada/Perdida/Finalizada, van con el chip normal
    (`ReservaStatusBadge`), sin excepción. Este mismo criterio aplica a cualquier chip de estado
    nuevo que se agregue en Clientes/Cobranzas/Caja/etc.: nunca un "sello" especial para estados
    terminales, siempre el chip del molde.
11. **No arrancar a programar una pantalla sin la maqueta pasada por `ux-ui-disenador`** (gate
    obligatorio, regla del dueño 2026-06-05) — esta guía no reemplaza esa revisión, solo evita que
    cada pantalla reinvente el molde de botón/chip/tabla.

## Qué queda pendiente de prolijidad (detectado en Tanda D0, no corregido — no era el alcance)

- El logotipo "MT" de la topbar (`components/Layout.jsx`, caja `bg-indigo-600`) sigue en índigo,
  no en azul boleto. No es un botón de acción (es la marca), así que D0 no lo tocó, pero cuando se
  llegue a la tanda de "Dashboard/Ajustes/topbar" (módulo 7) conviene preguntarle a Gastón si el
  isotipo también pasa a azul boleto o se mantiene como acento de marca aparte.
