# 2026-08-08 — Tarifario Inteligente F1: variantes + bibliotecario de repetidos

## Qué se ve distinto desde hoy

- **El Tarifario tiene 6 solapas**: Hoteles / Aéreos / Paquetes / Traslados /
  Asistencias / **Excursiones** (nueva, decisión firmada hoy: V17). "Otro" ya no
  se puede cargar en el tarifario (se sigue vendiendo normal en las reservas).
- **Cada producto conoce sus habitaciones**: la doble y la triple del mismo hotel
  ya no se pisan el precio entre sí. Al vender, el sistema sugiere el precio de
  ESA habitación; si es otra, lo muestra en gris como referencia, sin precargar.
- **Bandeja "Repetidos"**: cuando el sistema sospecha que dos productos son el
  mismo escrito distinto, los muestra juntos y ofrece "Es el mismo" / "Es otro".
  Unir tiene **Deshacer** fiel: cada movimiento guarda una foto de lo que tocó y
  nada se borra jamás — lo que pierde se esconde con rastro.
- **Corregir una habitación mal cargada** desde la ficha del producto: el form se
  abre precargado con lo que está, y la corrección también queda en el registro
  con su Deshacer.
- **Campos con memoria**: habitación del hotel y tipo de vehículo del traslado
  ahora recuerdan lo que ya escribiste y lo ofrecen al tipear (con teclado).

## Qué NO se ve todavía

- La solapa Excursiones arranca vacía: hoy una venta de excursión no alimenta el
  tarifario (se carga como servicio genérico, excluido por ADR-017). Que aprenda
  es una obra aparte, anotada.
- La línea inteligente con IA (F2) y el bibliotecario nocturno (F3): siguen.

## Cómo se construyó (para el que quiera aprender)

- Spec firmada `docs/ux/specs/2026-08-07-tarifario-inteligente-FIRMADA.md` + addendum
  V17 firmado hoy. Commit `0d94e806` (deploy con CI verde completo, incluida la
  migración contra Postgres real).
- La clave técnica del "nada se pisa": índice único **parcial** por
  (producto, operador, variante) que ignora las filas escondidas, y el upsert de
  ventas apunta a ese índice — una fila escondida jamás ocupa el casillero ni
  aprende ventas en silencio.
- El guard del **Deshacer terminó universal**: en vez de adivinar qué eje tocó
  cada movimiento (producto, clave, importes — le encontramos 3 agujeros por
  ejes), pregunta directo al rastro: "¿esta fila tiene un movimiento más nuevo
  vigente?" → "Deshacé primero el movimiento más nuevo."
- Proceso: ronda 1 de reviews anoche (la sesión se cortó por tokens) + 4 rondas
  más hoy con 4 reviewers (backend, frontend, seguridad/datos, gate de
  exposición). 12 bloqueantes en la ronda completa + 4 residuales cazados en las
  verificaciones — entre ellos: el guard por ejes (3 iteraciones), una regresión
  que vaciaba el precio guardado al abrir "Editar", y Twin/Suite que se
  reescribían como "Doble" (con un test replicado que certificaba el error).
- Lección repetida que vale: **un fix puede abrir un bug nuevo** — las dos
  regresiones de hoy nacieron de fixes de review; la verificación puntual del
  reviewer sobre cada fix las cazó antes de PROD.
- Verificación final: keeper en PROD, 13/13 PASS, capturas f1-*.png.
