# La línea inteligente y la pantalla para configurar la IA (F2 del Tarifario Inteligente)

**Fecha:** 2026-08-09 · Continúa la obra de `2026-08-08-tarifario-inteligente-f1.md`
**Spec firmada:** `docs/ux/specs/2026-08-07-tarifario-inteligente-FIRMADA.md` (§3, §4 y §15)

---

## Qué se construyó hoy, en criollo

### 1. La pantalla para enchufar el cerebro (Configuración → Inteligencia artificial)

Hasta ayer, la clave de la IA solo la podía poner un técnico tocando el servidor.
Desde hoy hay una solapa nueva en Configuración (solo la ve el Admin) donde:

- Elegís con cuál IA trabajar de una lista con nombres de la calle: **Groq**
  (recomendada, gratis para arrancar), OpenAI, Claude, Gemini, Grok, OpenRouter,
  u "Otra" poniendo la dirección a mano.
- **Pegás la clave una sola vez y nunca más se muestra.** Queda guardada cifrada
  en la base con el mismo candado que protege los certificados de ARCA. La
  pantalla solo dice "Configurada ✓ · empieza con gsk_…". No hay ojito, no hay
  botón de copiar: la clave entra y no sale.
- Un botón **"Probar conexión"** que contesta en criollo: "Funciona ✓ (contestó
  en 0,8 s)", "La clave no sirve o venció", etc. Nunca un código de error.
- Arriba de todo, un semáforo de una línea: 🟢 funcionando / ⚪ sin configurar /
  🟠 la última prueba no anduvo.

**Regla nueva de una sola pieza:** si hay una IA configurada, las ayudas
inteligentes funcionan; si no hay, el sistema anda exactamente igual, sin esas
ayudas. Murió el interruptor viejo de "prender la IA" (basta de llaves).

Lo que carga el dueño en la pantalla **manda** sobre lo que puso el técnico en
el servidor; lo del servidor queda de respaldo. (Eso derogó una regla vieja del
ADR-016 y quedó escrito como adenda en el propio ADR.)

### 2. La línea inteligente (la ficha de carga de servicio)

El casillero de buscar producto ahora **entiende frases enteras**. Escribís:

> sheraton iguazu doble desayuno ola 48 usd del 12 al 15/9

y el sistema, sin trabar nada y con el mismo "Buscando…" sutil de siempre:

- Encuentra **Sheraton Iguazú** en tu tarifario y lo muestra como renglón
  "Producto *" en amarillo debajo de la caja (tu frase queda intacta arriba).
- Precarga **operador** (Ola Mayorista), **habitación y régimen** (Doble con
  desayuno), **fechas** (12/09 al 15/09) y **costo** (US$ 48) — todo en
  amarillo, todo editable.
- Si algo no lo entendió, ese casillero queda vacío y listo. Sin carteles.
- Si duda de algo que cambia la plata, pregunta **una sola línea con Sí/No**
  debajo del campo ("¿El operador es Ola Mayorista?"). Podés ignorarla y
  guardar igual.

**El amarillo es el que habla:** lo que está en amarillo lo puso el sistema y
espera tu mirada. Lo que escribiste vos, jamás se pisa — ni la interpretación,
ni la sugerencia de precio por variante, ni un "No" en una duda pueden borrar
un número que tipeaste a mano.

**Si la IA no está configurada, se cayó o tarda:** la ficha es exactamente la
de siempre. Ni una palabra distinta, ni un cartel. Esa es la regla más
importante de toda la obra.

---

## Cómo se protegió (lo invisible pero importante)

1. **La clave nunca sale.** No viaja al navegador, no aparece en logs, no se
   audita en crudo, y el probador de conexión no puede usarse para mandarla a
   una dirección ajena (solo la reusa si la dirección probada es la misma que
   la guardada). El cliente HTTP no sigue redirecciones (nadie puede hacer
   rebotar el pedido hacia adentro del servidor).
2. **El prompt que viaja a la IA está acotado:** solo nombres de productos del
   tarifario, operadores y variantes ya usadas. Jamás pasajeros, clientes,
   documentos, costos ni datos de otras reservas. Hay un test que siembra un
   dato sensible en una reserva y verifica que NO viaja.
3. **Lo que devuelve el modelo no se cree porque sí:** el producto tiene que
   existir en el catálogo, el precio tiene que estar escrito en la frase (con
   corridas de dígitos reales, no un pegote), la habitación tiene que ser de
   las conocidas, y las preguntas de duda las arma el motor con datos de la
   base — nunca con texto crudo del modelo. Si el modelo devuelve basura, el
   resultado es "no entendí" y la ficha sigue como siempre.
4. **Nada se guarda solo.** La interpretación solo precarga; el servicio se
   crea cuando el vendedor toca Guardar, como siempre.

## Proceso de la sesión

4 obras en serie (motor config → pantalla config → motor línea → ficha), cada
una con sus revisores (funcional + seguridad donde tocaba + el gate de
exposición de datos). 11 bloqueantes encontrados y cerrados en pasadas de
arreglo, todos re-verificados con PASS. Suites al cierre: backend unit
5152/5152, frontend 3483/3483.

## Qué queda para las próximas sesiones

- **F3:** el bibliotecario nocturno con IA (propone agrupaciones más finas
  sobre la misma bandeja de Repetidos; la pantalla no cambia).
- El flaky `Adr042 D_TwoConcurrentRetries` (3 reincidencias).
- Anotados para Gastón: la duda del año de fechas vacía las dos puntas al
  contestar "No" (desvío chico de la letra de §4, a validar); "borrar el grupo
  configuración" ahora también borra la clave de IA (decisión tomada por
  coherencia, reversible si la quiere distinta); la duda de plata "¿es por
  noche?" hoy solo aplica a hotel (en paquete/aéreo el monto se asume por
  pasajero sin preguntar).
