# El buscador de servicios se vuelve versátil (y la IA aprende a preguntar)

*Explicación nivel trainee — 2026-08-10*

## El problema que trajo Gastón

El buscador de la ficha era tosco: había que escribir el nombre casi completo
("sheraton" solo NO encontraba "Sheraton Buenos Aires Hotel & Convention
Center"), había que saber en qué solapa estaba el servicio, y no servía de
nada escribir el operador o las fechas.

## Por qué pasaba (la causa técnica, en fácil)

El buscador comparaba tu texto contra el nombre ENTERO del servicio usando una
medida de parecido global (`similarity()` de pg_trgm) con un piso de 0.4.
"sheraton" contra un nombre de 8 palabras da un parecido bajísimo aunque la
palabra esté ahí adentro. Encima, si no le decías el tipo de servicio,
directamente devolvía lista vacía.

## Qué se construyó

### 1. Matching tolerante (backend)

`CatalogSearchAsync` ahora combina tres formas de encontrar:

- **`word_similarity()`**: mide el parecido contra la MEJOR palabra del nombre,
  no contra el nombre entero → "sheraton" y hasta "sheratom" (con typo) matchean.
- **Substring por token** (`ILIKE %token%`): pedazos de palabra encuentran.
- **Token-AND con degradé** (en C#, `CatalogSearchTokens.cs`): tu texto se corta
  en palabras; si algún resultado cumple TODAS, la lista se achica a esos
  (así "sheraton + operador" filtra); si ninguno cumple todo, se muestran
  igual ordenados por cuántas palabras cumplen — nunca peor que antes.

Los tokens matchean contra nombre, ciudad, operador (de la memoria de ventas)
y hasta la palabra del tipo ("aereo"). Todo escribiendo en la misma barra de
siempre: cero filtros visibles.

### 2. Búsqueda entre tipos + salto de solapa (frontend)

El tipo de servicio pasó de filtro obligatorio a "preferido": parado en Hotel
podés encontrar un aéreo. Las filas de otro tipo llevan una chapita gris con
la palabra del negocio; primero van los del tipo de tu solapa. Al elegir uno
de otro tipo, la solapa salta sola y el producto queda precargado (mecánica:
"selección pendiente" que el formulario destino consume al montarse — cada
solapa tiene su formulario propio y la precarga tiene que correr adentro del
destino para usar su mecánica amarilla).

**Candado de seguridad**: el backend ahora rechaza guardar un servicio con un
producto de catálogo de OTRO tipo (antes ni lo miraba — con cross-tipo era
una bomba silenciosa).

### 3. El hack de la frase completa (firmado por Gastón hoy)

Podés tirar "llao llao del 10/02 al 15/02 con delfos" en el mismo buscador:
el motor de interpretación (que ya existía de F2 y estaba tirando esa info a
la basura desde V18) extrae fechas y operador, y al elegir el hotel quedan
precargados en amarillo editable. Es un hack escondido a propósito: cero
placeholder, cero texto que lo insinúe — el que no lo conoce busca el hotel
como siempre.

### 4. La pregunta con ✨ (matiz firmado hoy sobre la regla de IA invisible)

Cuando el sistema tiene una duda concreta ("¿El Panamericano de Buenos Aires o
el de Bariloche?") la muestra en un renglón gris de una línea arriba del
desplegable, con un ✨ adelante. No es clickeable, no entra en la navegación
con flechas, y desaparece sola. La duda de producto ambiguo se calcula con
LÓGICA PURA (mismo nombre, distinta ciudad/operador) — cero tokens de IA.
Jamás aparece la palabra "IA".

### 5. La IA sale mucho más barata

- **Se llama menos**: solo si la búsqueda local vino floja (sin resultados o
  parecido < 0.45) O si el texto parece una frase completa (tiene números,
  meses, "del ... al" o 4+ palabras). Como el buscador local ahora encuentra
  casi todo, la mayoría de los tecleos no llaman al modelo. Debounce 600→900ms.
- **Cada llamada cuesta menos**: cache de respuestas (10 min) para no preguntar
  dos veces lo mismo, prompt recortado (sin variantes ni precios, operadores
  60→30), y respuesta máxima 400→250 tokens.

## Decisiones firmadas hoy (no reabrir)

1. **Preguntas + simbolito ✨** — supera parcialmente la invisibilidad total
   del 09/08: la IA puede mostrar UNA pregunta corta discreta cuando tiene
   duda concreta. Todo lo demás de la regla sigue vigente.
2. **Chapita gris** para filas de otro tipo; **solapa actual primero**; al
   saltar de solapa **no se copia nada** de lo tipeado a mano.
3. **Fechas = hack de frase completa**, no búsqueda por fecha: la fecha es la
   del viaje que estás cargando y va a parar al formulario, no al filtro.

## Archivos protagonistas

- Backend: `RateService.cs` (CatalogSearchAsync reescrito),
  `CatalogSearchTokens.cs` (nuevo), `ServiceLineInterpreter.cs` +
  `ServiceLinePromptBuilder.cs` (IA barata + dudas),
  `ServiceLineInterpretationCache.cs` (nuevo), `BookingService*.cs` (candado).
- Frontend: `ProductSearchField.jsx`, `ServiceInlineCard.jsx`, los 5 forms,
  `crossTypeSearchLogic.js` (nuevo), `useSeleccionPendienteDelTipo.js` (nuevo),
  `productDedupMatchLogic.js`, `useProductDedupMatch.js`.
- Spec UX firmada: `docs/ux/2026-08-10-buscador-cross-tipo.md` (D1..D13).
