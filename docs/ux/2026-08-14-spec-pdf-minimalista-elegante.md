# Spec: PDF de presupuesto "Minimalista elegante" (v3) — APROBADA

**Fecha**: 2026-08-14 · **Decisiones firmadas por Gaston (multiple choice + "me cierra" sobre maqueta)**:
estilo MINIMALISTA ELEGANTE · arranque con HERO · plata = precio por servicio + tarjeta de total ·
color = PALETA SEGÚN EL DESTINO elegida por IA · logo de Ajustes arriba a la izquierda (con respaldo
en texto si no hay logo).

**Maqueta aprobada** (fuente de verdad visual): proyecto Claude Design "MagnaTravel — PDF de presupuesto",
tarjetas `previews/pagina-1.html`, `previews/pagina-2.html`, `previews/paleta-destinos.html`.
La maqueta está en px sobre una página de 794px de ancho (A4 @96dpi): **pt = px × 0.75**.

**Reemplaza** el estilo "banda de color arriba" de la v2. La INFORMACIÓN no cambia: mismo contenido
que hoy (vuelos con horarios/duración/equipaje, hotel con estrellas/habitación/régimen/cuotas, opciones,
otros servicios, formas de pago, condiciones, pie legal, por persona/total opción A, WhatsApp).

**Reglas vinculantes**: el PDF lo lee un cliente final — cero jerga técnica, cero IDs internos (gate
data-exposure). La IA enriquece, NUNCA bloquea (contrato IAiChatProvider, ADR-016). Sin migraciones.

## 0. Tipografía

- **Marcellus** (Google Fonts, licencia OFL): cara display, SOLO para el wordmark de la agencia (cuando
  no hay logo), el DESTINO del hero y el MONTO de la tarjeta de total. El TTF se agrega al repo
  (Infrastructure, recurso embebido) y se registra en QuestPDF al inicializar el servicio, con
  try/catch: si el registro falla, el PDF sale igual con la fuente default (Lato) — jamás se rompe la emisión.
- Todo lo demás: fuente default (Lato). Jerarquía por tamaño/peso/espaciado.
- Colores base: tinta `#1a1a1a` · apagado `#6b7280` · filetes `#e5e7eb` (0.75pt) · fondo blanco, sin banda.
- El ACENTO (uno por documento, §5) aparece SOLO en: eyebrow del hero, subrayado del hero, etiquetas de
  sección, nodos del riel, avioncito, "+1", estrellas, filo y etiqueta de la tarjeta de total.

## 1. Página 1 (ver pagina-1.html; medidas ya convertidas a pt)

**Cabecera**: logo de `AgencySettings` a la izquierda (alto máx 26pt, ancho proporcional); si no hay
logo, wordmark en Marcellus 13pt letter-spacing amplio + debajo el resto del nombre en 6.5pt caps
espaciadas apagadas. A la derecha, alineado a la derecha: "PRESUPUESTO" (7pt, caps, tracking ancho,
apagado) / número visible ("2026-1067", 10.5pt bold) / fecha de emisión (7pt apagado).

**Hero** (margen sup. 33pt): eyebrow en ACENTO 7pt caps tracking ancho ("PROPUESTA DE VIAJE · 7 NOCHES" —
noches solo si se conocen); destino en Marcellus caps: 55pt si ≤14 caracteres, 40pt si ≤24, 30pt si más
largo; subrayado acento 48×2.25pt; línea meta apagada 9.5pt ("27 feb — 6 mar 2027 · 2 pasajeros · País"
— solo las partes con dato). Debajo, las 4 líneas de datos actuales (SALIDA/EQUIPAJE/TRASLADO/RÉGIMEN)
en grilla de 2 columnas: etiqueta 6pt caps tracking 1.5 apagada (ancho fijo 55pt) + valor 8pt tinta;
línea sin dato no se dibuja. Cierra filete completo.

**Riel de itinerario** (firma visual): línea vertical de 0.75pt `#e5e7eb` en el margen izquierdo del
bloque de contenido; el contenido se indenta 20pt. Cada sección lleva un nodo: círculo blanco de 7pt con
borde 1.5pt ACENTO, centrado sobre el riel a la altura del título. En QuestPDF: fila de 2 columnas
(riel 20pt + contenido), el riel dibuja la línea con un Canvas/SVG o borde, y cada título de sección
superpone su nodo (patrón concreto a criterio del implementador, el resultado visual manda).

**Título de sección**: caps 7pt tracking 2 en ACENTO bold + filete `#e5e7eb` que corre hasta el margen
derecho. Secciones en orden actual: AÉREOS · HOTEL · TRASLADOS · OPCIONES (grupos como hoy) · MÁS SERVICIOS
(renombrada desde "OTROS" en ronda 2, maqueta aprobada).

**Vuelos**: grilla estricta (los anchos fijos ya implementados sirven: avión 22/salida 96/chip 50/
llegada 96/duración 60pt + elástico + equipaje; ajustar a ojo con la maqueta). Avioncito: SOLO el path
del avión en ACENTO, 15pt, SIN círculo gris de fondo. Hora 13pt ExtraBold tinta; "+1" 6.5pt ACENTO en
superíndice; aeropuerto 7pt apagado tracking leve. Chip "Directo": borde 0.75pt `#e5e7eb` redondeado,
texto 7pt apagado, SIN fondo. Duración 8pt apagado. Equipaje: mochila/maletín/valija 15/14/16pt,
`#5b6067`, no-incluido al 20% (dibujos ya implementados). Filete entre filas.

**Hotel**: nombre 12pt bold tinta + estrellas ACENTO 8.5pt con tracking, en la misma línea; tarifa a la
DERECHA de esa línea, 10.5pt bold + "USD" 7pt apagado. Debajo: "Junior Suite · All inclusive · 7 noches"
8pt apagado; cuotas 7.5pt apagado tracking leve. Todo servicio con tarifa la muestra igual (derecha, bold).

**Traslados/Otros**: nombre 9.5pt, precio derecha 9pt bold, mismo patrón.

**Tarjeta de total** (tras el último servicio): filete superior 1.5pt ACENTO, padding sup. 10pt. Izquierda:
"TOTAL DEL VIAJE" (etiqueta de sección en ACENTO) + nota 7pt apagada máx 210pt de ancho (qué incluye —
texto derivado de las secciones presentes, sin inventar). Derecha: monto en Marcellus 30pt tinta
("2.680 USD") + debajo "1.340 USD por persona" 8pt apagado cuando corresponde (misma lógica actual
por persona/total).

**Pie**: filete + izquierda "Documento no válido como factura · Legajo EVT {nro}" itálica 6.5pt apagada
(mismos textos legales actuales, incluida la X si la regla vigente la pide) + derecha paginación
"01 / 02" 6.5pt tracking ancho.

## 2. Página 2 (ver pagina-2.html)

Cabecera compacta: wordmark/logo chico a la izquierda (Marcellus 10pt) + derecha "PRESUPUESTO {nro} ·
{DESTINO}" 7pt caps apagado; filete debajo. Secciones "FORMAS DE PAGO" (texto de la plantilla de
Ajustes, 8pt, interlineado 1.8, tinta suave `#3f434a`, negritas si la plantilla las trae hoy) y
"CONDICIONES" (los bloques actuales de Ajustes: título del bloque en 6.5pt caps apagada, cuerpo 7.5pt
interlineado 1.65 `#3f434a`, máx ~480pt de ancho). Mismo criterio de salto de página que hoy. Pie igual
al de página 1 con "02 / 02".

## 3. Qué NO cambia

Textos y su origen (Ajustes), orden de secciones, reglas de omisión (vuelo sin datos se omite entero,
"Otro" se dibuja, grupos ambiguos a OPCIONES), por persona/total (opción A firmada 13/08), WhatsApp,
nombre de archivo, endpoint de emisión.

## 4. Logo

El de `AgencySettings` (Configuración → Presupuestos y PDF). Sin logo cargado → wordmark en texto
(nombre de la agencia). Nunca un hueco vacío.

## 5. Paleta según el destino (IA)

- Set CURADO (la IA elige CATEGORÍA, jamás un hex libre): `caribe` #0e7c86 · `playa` #b3873e ·
  `nieve` #3d6b9e · `ciudad` #b05c3b · `naturaleza` #3e7d4f · `vino` #7d3c4e · `otro`/fallback →
  `AgencySettings.PdfBandColorHex` (default #0e3a4f). El color de Ajustes pasa a ser EL RESPALDO.
- Servicio nuevo `DestinationPaletteService` (Infrastructure, interfaz en Application): entrada =
  título de destino (`QuoteBudgetPdfRules.ResolveDestinationTitle`) + ciudades de los servicios; UN
  turno a `IAiChatProvider` (conexión barata vía resolver existente, espejo del consumidor IA ya
  existente) pidiendo UNA palabra del set; fuera del set o Degraded → fallback. Caché `IMemoryCache`
  por destino normalizado (trim/lower), TTL 30 días. Sin IA configurada → fallback directo sin llamar.
- El caller async resuelve la paleta ANTES y se la pasa a `GenerateQuotePdf` como parámetro opcional
  (null → fallback) — nada de I/O dentro del dibujo.
- El prompt lleva SOLO destino/ciudades — jamás datos del cliente (gate data-exposure).

## 6. Ronda 2 — APROBADA por Gaston (14/08, "me cierra" sobre maqueta actualizada)

Decisiones firmadas (multiple choice 14/08): escalas SIMPLES por tramo · etiqueta por ítem en
"MÁS SERVICIOS" · cliente en cabecera + sección PASAJEROS con nombres (menores con edad, SIN
documentos) · cuotas en cualquier servicio Y plan de pagos del total.

**Escalas (por tramo del vuelo, ida y vuelta por separado)**: cantidad de escalas + dónde (texto
libre corto, ej. "Lima (LIM)") + espera opcional (texto libre, ej. "2h 10m"). En el PDF: si el tramo
tiene escalas, el chip pasa de "Directo" a "1 escala"/"N escalas" A SECAS (ajuste 14/08 tras render:
el lugar dentro del chip se partía en dos renglones — el lugar vive SOLO en el renglón de detalle);
el chip de escala PISA al "Directo" — no conviven. Debajo de las filas de vuelo, un renglón apagado por tramo con
escala: "Escala en {lugar} · espera {espera}" (partes sin dato se omiten). Campos nuevos en
`FlightSegment` (migración ADITIVA).

**Cabecera**: cuarta línea del bloque derecho: "Preparado para {cliente}" (nombre del pagador de la
reserva; sin pagador, la línea no se dibuja).

**Sección PASAJEROS** (nodo propio del riel, después de los servicios, antes del total): grilla de
2 columnas con `Passenger.FullName`; menores de 18 (según BirthDate) agregan "· N años" apagado.
Sin datos de documento.

**"MÁS SERVICIOS" con etiqueta por ítem**: cada ítem lleva arriba su etiqueta de tipo en caps chicas
apagadas (tracking 2): ASISTENCIA AL VIAJERO / EXCURSIÓN / PAQUETE / la categoría visible del servicio
genérico. Nombres de tipo = los del negocio (los que ya usa `QuoteBudgetPdfRules` para OPCIONES),
jamás nombres de clases.

**Cuotas en cualquier servicio**: `InstallmentsCount`/`InstallmentAmount` (hoy solo en HotelBooking)
se agregan a FlightSegment, TransferBooking, PackageBooking, AssistanceBooking y ServicioReserva
(migración ADITIVA); el PDF los dibuja igual que en hotel ("N cuotas de X {moneda}"), informativos,
sin tocar la venta total.

**PLAN DE PAGOS del total** (bloque bajo la tarjeta de total): tabla hija nueva
(fila = texto de cuándo, ej. "Al confirmar la reserva" o "10 de enero de 2027" + monto + moneda,
ordenadas). Se dibujan tal cual se cargaron; sin filas, el bloque no aparece. Informativo — NO toca
cobranzas ni cuenta corriente.
