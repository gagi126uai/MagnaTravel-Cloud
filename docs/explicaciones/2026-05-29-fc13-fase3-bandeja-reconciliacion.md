# FC1.3 Fase 3 — La "bandeja de avisos" de notas de crédito parciales (2026-05-29)

> Nivel: trainee. Si nunca tocaste el módulo, empezá por acá.

## El problema, con un ejemplo de la vida real

Imaginate una verdulería. Un cliente te compra $1000 de fruta, te paga, y vos le das
un **recibo** (un papelito que dice "me pagaste $1000"). Al rato el cliente se arrepiente
de la mitad: devuelve $500 de fruta. Vos tenés que hacerle una **nota de crédito** por
esos $500 (un papel fiscal que le dice a la AFIP/ARCA "che, de aquella venta, $500 ya no
valen").

Ahora el problema: ese **recibo viejo de $1000 sigue diciendo $1000**. No se rompe solo.
Quedó "desactualizado". Alguien tiene que acordarse de ir, mirar ese recibo, y decidir
qué hacer con él (anularlo, dejarlo, lo que corresponda).

En el sistema pasa exactamente eso. Cuando se emite una **nota de crédito parcial**
(NC parcial), los **recibos de pago de la factura original NO se anulan automáticamente**.
Esa fue una decisión a propósito de la Fase 2 (no queremos que el sistema toque la plata
solo). Esos recibos quedan "vivos".

## Qué construimos en la Fase 3

Una **pantalla tipo "bandeja de pendientes"**: una lista donde un encargado de la oficina
ve todos esos casos ("emitiste una NC parcial y hay recibos viejos sin acomodar") y los
resuelve a mano.

Reglas de negocio que fijó Gastón (no se discuten, el código las respeta):

1. **El cierre es manual.** Un encargado mira el caso y aprieta "resuelto". El sistema
   nunca lo cierra solo.
2. **La bandeja solo AVISA y organiza.** La devolución de plata de verdad se hace en
   Caja / Cuenta Corriente (que ya existe), NO desde esta pantalla.
3. **Solo casos nuevos.** No vamos a salir a buscar casos viejos del pasado.
4. **Cuatro ojos.** Para cerrar un caso uno mismo (sin que otro lo apruebe) hace falta
   una excepción especial, salvo que haya un solo administrador en la agencia (ahí se
   permite). Reusa el mismo mecanismo que ya usábamos en otras pantallas.

## Cómo está armado (las dos mitades)

### La mitad de atrás (backend)

- Una **tabla nueva** que guarda cada caso: qué NC parcial fue, de qué factura venía,
  y una **foto** ("snapshot") de los recibos que estaban vivos en ese momento.
- El caso se **da de alta en el mismo movimiento** en que se hace el ajuste contable de
  la NC. Esto es clave: o se guardan las dos cosas juntas, o no se guarda ninguna. Nunca
  queda "ajuste hecho pero caso sin registrar" (eso sería plata que nadie ve). En la jerga:
  un solo `SaveChanges`, todo o nada.
- Al **cerrar** un caso: si hay recibos todavía vivos, te **obliga a escribir una nota**
  explicando por qué cerrás igual, y queda marcado en la auditoría.
- Si dos personas cierran el mismo caso al mismo tiempo, el segundo recibe un aviso
  (error 409 "alguien lo tocó antes, refrescá") en vez de pisar el trabajo del otro.

#### El arreglo que salió de la revisión

Había un detalle: después de cerrar el caso, el sistema escribe un renglón de auditoría.
Ese paso decía en un comentario "esto es secundario, si falla no rompe nada"... pero el
código **no** estaba protegido para eso. Si justo fallaba escribir la auditoría, el
usuario veía un error feo (500) **aunque el cierre ya había quedado guardado y bien**.

Lo arreglamos: ahora la escritura de auditoría está dentro de un "paraguas" (try/catch)
que, si falla, lo **anota en el log** pero **no le tira el error al usuario**. El cierre
ya está hecho y es válido; no tiene sentido asustar al encargado por un renglón de log.

### La mitad de adelante (frontend)

La pantalla es un **clon** de la bandeja de aprobaciones que ya existía (para no inventar
un diseño nuevo y mantener todo parecido). Tiene:

- Filtro por **mes** (el mismo navegador de meses que usan Cobranza y Facturación) y por
  estado (pendientes / resueltos / todos).
- Cada fila muestra: número de NC, número de factura original, cliente, monto fiscal
  (aclarando que es **informativo**, NO la plata a devolver), y la lista de recibos con
  su estado actual ("2 de 3 ya anulados").
- Botón "anular recibo" solo en los que siguen vivos, y un cuadro de notas + botón
  "marcar resuelto".

#### El arreglo que salió de la revisión

Cuando un usuario intenta anular un recibo y el sistema le dice "esto necesita la
aprobación de otra persona" (el cuatro-ojos), el **resto del sistema abre una ventanita**
para mandar esa solicitud. Esta pantalla nueva, al principio, lo mostraba como un cartel
rojo de error y te dejaba sin salida. Lo igualamos: ahora **abre la misma ventanita** que
el resto, así el encargado puede pedir la aprobación ahí mismo.

## Estado y qué falta

- **Código:** backend y frontend hechos, **revisados** (backend-dotnet-reviewer +
  frontend-reviewer) y commiteados/pusheados. HEAD `1154034`.
- **La llave de seguridad sigue apagada** (`EnablePartialCreditNoteRealEmission` = OFF):
  el sistema en producción sigue emitiendo notas de crédito TOTALES como siempre. Cero
  riesgo fiscal por esto.
- **Falta (cosas de máquina/humano, no de código):**
  - Aplicar las migraciones de base de datos en el servidor (Fase2_M2 + Fase3_M1). Son
    aditivas: solo agregan, no rompen nada.
  - Correr los tests en el servidor (`scripts/ops/run-tests-fc13.sh`), porque la base de
    datos está en el servidor, no en la máquina local.
  - Pendiente de prueba real (no se puede en local): que el "409 por dos personas a la
    vez" y las reglas de la base se comporten igual contra Postgres de verdad.
  - Firma del contador y homologación con ARCA antes de prender la llave (esto es de la
    Fase 2, sigue pendiente).
