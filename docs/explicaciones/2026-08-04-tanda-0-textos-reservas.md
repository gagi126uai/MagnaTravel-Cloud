# Explicación del día — 2026-08-04: Tanda 0 del rediseño de Reservas (los 230 textos)

**Para quién es esto:** para cualquiera que entre al proyecto sin contexto. Sin jerga; cuando
aparece un término técnico, se explica al lado.

## Qué se hizo, en una frase

Toda la sección Reservas — pantallas y mensajes del servidor — dejó de hablar "en programador"
y pasó a hablar el idioma del negocio, en el español de acá (voseo), con tildes y sin palabras
técnicas. Deployado en producción con el commit `fbe9aa36`.

## De dónde venía esto

El 2026-08-03 se hizo una auditoría completa de la sección Reservas (162 elementos revisados
uno por uno) y un inventario de textos rotos (230 hallazgos). Gastón firmó 16 decisiones esa
noche. La ejecución se dividió en tandas; esta fue la **Tanda 0**: todo lo que se podía arreglar
sin rediseñar pantallas. La sesión anterior se cortó a mitad de la tanda; esta sesión la retomó
del disco (el trabajo estaba sin commitear) y la terminó.

## Qué cambió concretamente

### 1. Los textos (la parte grande)

- **Tildes y voseo en todo**: "Cotización" (antes "Cotizacion"), "En gestión", "Probá de nuevo"
  (antes "Vuelve a intentar"), fuera "añadir/puedes/selecciona" (españolismos).
- **Fuera los Títulos Con Mayúscula En Cada Palabra** (costumbre del inglés, no del español).
- **Fuera la leyenda "(Opcional)"** en los formularios: lo obligatorio ya se marca con asterisco,
  el resto se sobreentiende.
- **Plurales de verdad**: "1 pasajero" (antes decía "1 pasajeros"), "1 día" en reprogramaciones.
- **Vocabulario firmado por el dueño**: "Forma de pago" (no "Método"), "Emitir igual" (no
  "Emitir por excepción"), "Perdida" (no "Perdido", porque concuerda con "reserva"),
  "Mayorista (BCRA)" (no la jerga "A3500"), "Rentabilidad estimada" (no "Rentabilidad Est.").

### 2. Las tres reglas funcionales firmadas

- **"Anular varios" lista solo servicios confirmados**: un servicio que todavía está "Solicitado"
  (pedido al operador pero sin respuesta) no se "anula", se borra de su propia fila — no tiene
  sentido avisarle al operador de algo que nunca confirmó.
- **El botón "Anular varios" aparece recién con 2 o más** servicios anulables: con uno solo,
  el botón de la fila alcanza.
- **Etiqueta única "Solicitado"**: antes el mismo servicio decía "En espera" en unas etapas y
  "Solicitado" en otras. Dos nombres para lo mismo confunden; quedó uno solo.

### 3. Cero información técnica al usuario (el gate de exposición)

Esto es lo más importante de la tanda. Un ERP jamás debe mostrarle al que lo usa (que no es
programador) las tripas del sistema. Se cerraron:

- Un aviso que mostraba el estado **en inglés crudo** ("Traveling" en vez de "En viaje").
- Los 5 formularios de servicio que mostraban un **código interno recortado** del operador
  (algo como "a3f9b21c…") al lado de "Operador sugerido".
- Dos pantallas que mostraban el **error técnico crudo** de la red ("Internal Server Error")
  en vez de "No se pudo guardar. Probá de nuevo."
- Mensajes del motor que listaban los **nombres internos de los estados** ("Budget,
  InManagement, Confirmed") — dos de estas fugas las cazaron los revisores porque ni siquiera
  estaban en el inventario. Ahora dicen, por ejemplo: "Elegí una de las opciones que ofrece
  la pantalla."
- El mensaje "ServiceType no soportado" (jerga pura) → "Ese tipo de servicio no está disponible."

## La trampa que casi pasa: tests que certificaban la regla vieja

Detalle instructivo. Los tests del front no importan la función real de la pantalla: la
**copian** a mano (limitación del corredor de pruebas). Cuando se cambió la regla de "En espera"
→ "Solicitado" en la pantalla, tres archivos de test quedaron con la copia vieja, y **pasaban
en verde certificando el comportamiento derogado**. El revisor de front lo cazó. La lección:
"todos los tests verdes" no significa nada si los tests miran una copia desactualizada. Se
actualizaron las tres copias, las aserciones se dieron vuelta al texto nuevo (no se borró
ninguna — regla T-6: jamás relajar un test) y se agregaron 2 tests nuevos que cubren la regla
nueva explícitamente.

## Cómo se verificó

- Suite de front: **3081 pruebas, 0 fallas** (2 más que antes, las nuevas de la regla P13).
- Suite del servidor (unitarias): **4653 pruebas, 0 fallas**. Compilación sin errores.
- Tres revisiones independientes: funcional de front (contra la guía UX de Gastón), funcional
  de backend (confirmó que ningún cambio tocó lógica, solo textos) y el gate obligatorio de
  exposición de datos. Las tres bloquearon algo en la primera pasada, todo se corrigió y las
  tres aprobaron en la segunda.
- CI completo verde (incluye las pruebas de integración contra Postgres real) y deploy
  automático al VPS. El backoffice de producción responde bien.

## Qué queda pendiente

- **Verificación visual en el navegador de Gastón** (regla de siempre: la verificación final es
  en PROD con sus ojos). En particular: que "Rentabilidad estimada" entre en el ancho de su
  caja en la ficha de la reserva.
- **Las tandas siguientes del rediseño** (ya firmadas): Tanda 1 listado (necesita backend de
  moneda por fila), Tanda 2 ficha, Tanda 3 formularios/flujo. Ahí también muere el botón
  "Eliminar" del presupuesto (regla absoluta: nada se borra).
- Limpieza de datos de prueba de la sesión anterior: reserva F-2026-1065 y "AEP-COR PRUEBA DNI"
  en el tarifario.
- Un hallazgo menor fuera de alcance anotado por un revisor: `CustomerPaymentModal.jsx` (cuenta
  corriente de clientes, no Reservas) tiene el mismo patrón de "(opcional)" y Títulos En
  Mayúscula — para la próxima pasada de textos de esa sección.
