# Línea de comandos: `armandodb.exe`

`armandodb.exe` ejecuta consultas contra las conexiones guardadas en ArmandoDB Studio, sin abrir ninguna ventana. Está pensado para scripts y para agentes de IA que necesitan leer de una base de datos.

- Usa las conexiones que ya guardaste en la aplicación, por su nombre.
- Si la conexión va por túnel SSH, lo abre y lo cierra por su cuenta.
- Nunca hace preguntas ni muestra ventanas: o responde, o falla con un mensaje y un código de salida.
- Por defecto solo acepta lecturas.

## Dónde está

Se instala en la misma carpeta que `ArmandoDBStudio.exe`. El instalador no la añade al `PATH`: se llama con su ruta completa o se añade esa carpeta al `PATH` a mano.

Para probarla desde el código fuente:

```
dotnet build armandodb-cli
.\armandodb-cli\bin\Debug\net10.0-windows\armandodb.exe ayuda
```

## Comandos

| Comando | Qué hace |
|---|---|
| `armandodb conexiones` | Lista las conexiones guardadas. |
| `armandodb consulta` | Abre la conexión, ejecuta una consulta, responde y cierra. |
| `armandodb sesion` | Deja la conexión abierta y responde a las consultas que van llegando. |
| `armandodb ayuda` | Muestra la referencia resumida. |
| `armandodb version` | Muestra la versión. |

Los comandos y las opciones también se aceptan en inglés: `connections`, `query`, `session`, `help`; `--connection`, `--database`, `--file`, `--format`, `--max-rows`, `--allow-write`, `--idle`.

## Opciones

Comunes a `consulta` y `sesion`:

| Opción | Significado |
|---|---|
| `--conexion NOMBRE` | Conexión guardada en la aplicación. Obligatoria. No distingue mayúsculas. |
| `--base NOMBRE` | Base de datos. Por defecto, la de la conexión. |
| `--formato json\|csv\|tabla` | Formato de la salida. Por defecto, `json`. |
| `--max-filas N` | Tope de filas por resultado. Por defecto, 1000. Con `0`, el máximo de la aplicación (500.000). |
| `--permitir-escritura` | Acepta sentencias que modifican datos. Sin efecto en conexiones de producción. |

Solo `consulta`:

| Opción | Significado |
|---|---|
| `--sql "TEXTO"` | La consulta. Puede ser un script con varias sentencias. |
| `--archivo RUTA` | Lee la consulta de un archivo `.sql`. |

Sin `--sql` ni `--archivo`, la consulta se lee de la entrada estándar.

Solo `sesion`:

| Opción | Significado |
|---|---|
| `--inactividad SEG` | Segundos sin recibir nada tras los que la sesión se cierra sola. Por defecto, 600. Con `0`, nunca. |

Las opciones admiten las dos formas: `--formato csv` y `--formato=csv`.

## `conexiones`

```
armandodb conexiones
armandodb conexiones --formato json
```

Muestra el nombre, el motor, el detalle del servidor, la base por defecto, si está marcada como producción y si usa túnel SSH. No muestra contraseñas.

## `consulta`: una llamada, una respuesta

```
armandodb consulta --conexion "Tienda" --sql "SELECT id, nombre FROM cliente LIMIT 10"
armandodb consulta --conexion "Tienda" --base ventas --archivo informe.sql --formato csv > informe.csv
echo SELECT COUNT(*) FROM cliente | armandodb consulta --conexion "Tienda"
```

En cada llamada:

1. Abre el túnel SSH, si la conexión lo tiene.
2. Abre la conexión a la base de datos.
3. Ejecuta todas las sentencias del script por esa misma conexión.
4. Cierra la conexión y el túnel, y termina.

No queda nada abierto entre una llamada y la siguiente. Dentro de una misma llamada sí se comparte la sesión: un `USE`, una variable o una tabla temporal sirven para las sentencias que siguen.

### Salida

El resultado va por la salida estándar. Los errores y los avisos van por la salida de error, así que no se mezclan con los datos.

**`json`** (por defecto):

```json
{
  "conexion": "Tienda",
  "base": "ventas",
  "segundos": 0.07,
  "filasAfectadas": null,
  "resultados": [
    {
      "columnas": ["id", "nombre"],
      "totalFilas": 2,
      "truncado": false,
      "filas": [
        { "id": 1, "nombre": "Ana Ñandú" },
        { "id": 2, "nombre": "José" }
      ]
    }
  ]
}
```

- Hay un elemento en `resultados` por cada conjunto de resultados: un script con tres `SELECT` da tres.
- `filasAfectadas` es `null` si el script no modificó filas.
- `truncado` es `true` si el resultado se cortó en `--max-filas`.
- Los números van como números, los textos como textos, `NULL` como `null`, las fechas en formato ISO (`2026-01-15T10:30:00`) y los binarios como `0x00FF...`.
- Si dos columnas se llaman igual, la segunda se renombra (`id`, `id_2`).

**`csv`**: una línea de encabezados y una por fila. Si hay varios resultados, van separados por una línea en blanco. Si alguno se truncó, el aviso sale por la salida de error.

**`tabla`**: texto alineado para leer en la terminal, con el número de filas al pie. Los valores largos se recortan a 60 caracteres.

### Códigos de salida

| Código | Significado |
|---|---|
| 0 | Correcto. |
| 1 | Uso incorrecto: falta una opción, opción desconocida, no hay nada que ejecutar. |
| 2 | No se pudo conectar: la conexión no existe, el servidor no responde, el túnel no abre, contraseña incorrecta. |
| 3 | Consulta rechazada por no ser de solo lectura. |
| 4 | Error devuelto por la base de datos al ejecutar. |

Con cualquier código distinto de 0, la salida estándar queda vacía y el motivo está en la salida de error.

## `sesion`: una conexión para muchas consultas

```
armandodb sesion --conexion "Tienda"
```

El programa abre la conexión (y el túnel) una sola vez y se queda esperando consultas por la entrada estándar. Responde a cada una por la salida estándar y sigue abierto hasta que se le pide salir. Sirve cuando se van a hacer muchas consultas seguidas y no se quiere pagar el inicio de sesión SSH en cada una.

A diferencia de `consulta`, en una sesión **todas las respuestas van por la salida estándar, también los errores**, para que quien la maneja no tenga que leer dos canales. La salida de error solo se usa si la sesión no llega a abrirse.

### Cómo se envían las consultas

De una de estas dos formas, que se pueden mezclar:

**Una línea JSON por consulta** (recomendado para programas y agentes):

```
{"sql": "SELECT COUNT(*) AS n FROM cliente", "id": 1}
```

- `sql` es obligatorio. Puede contener varias sentencias y saltos de línea (escritos como `\n`).
- `id` es opcional y se devuelve tal cual en la respuesta, sea número o texto. Sirve para emparejar cada respuesta con su consulta.
- La consulta se ejecuta en cuanto llega la línea.

**Texto terminado con `GO`** (cómodo para escribir a mano):

```
SELECT id, nombre
FROM cliente
WHERE id < 10
GO
```

La consulta se ejecuta al llegar una línea que solo diga `GO`, en mayúsculas o minúsculas.

### Cómo llegan las respuestas

Al abrirse, la sesión escribe una primera línea avisando de que está lista. **Hay que esperar esa línea antes de enviar nada.**

Con `--formato json`, cada respuesta es **una sola línea** JSON:

```
{"ok":true,"sesion":"lista","conexion":"Tienda","base":"ventas","soloLectura":true}
{"id":1,"ok":true,"segundos":0.012,"filasAfectadas":null,"resultados":[{"columnas":["n"],"totalFilas":1,"truncado":false,"filas":[{"n":42}]}]}
{"id":2,"ok":false,"error":"Error 1146, línea 1: Table 'ventas.nada' doesn't exist","codigo":4}
{"ok":true,"sesion":"cerrada","conexion":"Tienda","base":"ventas","soloLectura":true}
```

- `ok` dice si la consulta funcionó.
- Si falló, `error` trae el mensaje y `codigo` el mismo número que los códigos de salida de `consulta` (1, 2, 3 o 4).
- Si funcionó, la respuesta trae `segundos`, `filasAfectadas` y `resultados`, con la misma forma que en `consulta`.
- `reconectado: true` aparece si hubo que volver a abrir la conexión antes de esa consulta (ver más abajo).

Con `--formato csv` o `tabla`, cada respuesta es el resultado (o el mensaje de error) seguido de una línea que empieza por `#FIN`:

```
#FIN sesion lista: Tienda / ventas
n
42
#FIN ok (1 filas, 0.012 s)
Error 1146, línea 1: Table 'ventas.nada' doesn't exist
#FIN error 4
#FIN sesion cerrada: Tienda / ventas
```

### Lo que se conserva y lo que no

- **Se conserva** todo lo propio de la sesión de base de datos entre una consulta y la siguiente: `USE`, variables, tablas temporales, transacciones abiertas.
- **Un error no cierra la sesión.** Una consulta que falla o que se rechaza recibe su respuesta de error y la sesión sigue esperando la siguiente.
- **Si la conexión se cae** (red, túnel, reinicio del servidor), la consulta en curso responde con error de código 2. Antes de la siguiente consulta la sesión vuelve a abrir la conexión y el túnel, y lo indica con `reconectado`. Al reconectar se pierde lo que hubiera en la sesión anterior.

### Cómo termina

- Con una línea `salir` (también `exit` o `quit`).
- Al cerrarse la entrada estándar. Si quedaba una consulta escrita sin su `GO`, se ejecuta antes de terminar.
- Por inactividad: tras `--inactividad` segundos sin recibir nada (10 minutos por defecto).

En los tres casos escribe una última línea de cierre, cierra la conexión y el túnel, y sale con código 0. Si el proceso que la maneja muere sin avisar, su extremo de la entrada estándar se cierra y la sesión termina igual que en el segundo caso; no quedan conexiones ni túneles abiertos.

### Ejemplo: manejar una sesión desde Python

```python
import json, subprocess

sesion = subprocess.Popen(
    ["armandodb", "sesion", "--conexion", "Tienda"],
    stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, encoding="utf-8")

lista = json.loads(sesion.stdout.readline())      # esperar a que esté lista
assert lista["ok"]

def consultar(sql):
    sesion.stdin.write(json.dumps({"sql": sql}) + "\n")
    sesion.stdin.flush()
    return json.loads(sesion.stdout.readline())   # una línea por respuesta

respuesta = consultar("SELECT COUNT(*) AS n FROM cliente")
if respuesta["ok"]:
    print(respuesta["resultados"][0]["filas"][0]["n"])
else:
    print(respuesta["error"])

sesion.stdin.write("salir\n")
sesion.stdin.flush()
sesion.wait()
```

## Solo lectura

Por defecto se rechaza cualquier script que contenga una sentencia que no sea claramente de lectura. La regla es de lista cerrada: se acepta lo conocido y se rechaza todo lo demás.

- **Se acepta:** `SELECT`, `WITH ... SELECT`, `SHOW`, `DESCRIBE`, `EXPLAIN`, `USE` y `VALUES`. En SQL Server y Sybase, además, `DECLARE`, `SET`, `PRINT`, `IF`, `WHILE`, `BEGIN` y `RETURN`.
- **Se rechaza:** `INSERT`, `UPDATE`, `DELETE`, `MERGE`, `REPLACE INTO`, `DROP`, `TRUNCATE`, `ALTER`, `CREATE`, `GRANT`, `REVOKE`, `CALL`, `EXEC`, `KILL`, y también `SELECT ... INTO`, `SELECT ... FOR UPDATE` y cualquier cosa que no se reconozca.
- Si una sola sentencia del script se rechaza, **no se ejecuta ninguna**.

Con `--permitir-escritura` se aceptan también las escrituras, con una excepción: **en una conexión marcada como producción no se escribe desde la línea de comandos en ningún caso**.

### Esta comprobación no es una garantía

Mira el texto de la consulta, no lo que hace el servidor. Una función llamada desde un `SELECT` puede modificar datos, y siempre puede existir una forma de escribir que la comprobación no reconozca.

**La protección real es la cuenta de la base de datos.** Para un agente o un script contra un servidor que importa, guarda en la aplicación una conexión cuya cuenta solo tenga permiso `SELECT` y usa esa. Así, aunque algo falle en la comprobación, el servidor rechaza la escritura.

## Contraseñas

- Se usan las guardadas con la conexión ("recordar contraseña" al guardarla en la aplicación). Están cifradas para tu usuario de Windows, así que `armandodb` debe ejecutarse con ese mismo usuario.
- Si no están guardadas, se leen de las variables de entorno `ARMANDODB_PASSWORD` (base de datos) y `ARMANDODB_SSH_PASSWORD` (túnel SSH).
- Nunca se piden por pantalla ni se aceptan como opción de la línea de comandos, para que no queden en el historial de la terminal.

## Historial

Cada consulta ejecutada, con `consulta` o dentro de una `sesion`, queda en el historial de consultas de la aplicación (`Ctrl+H`), marcada como `CLI` junto al nombre de la conexión. Sirve para revisar después qué ejecutó un script o un agente. Las consultas rechazadas no llegan a ejecutarse y no se guardan.

Se aplican los mismos ajustes que al resto del historial: si está desactivado en la aplicación, tampoco se guarda lo de la línea de comandos.

## Usarla con un agente de IA

Recomendaciones para dar a un agente acceso de lectura a una base:

1. **Crea una cuenta de solo lectura** en el servidor (solo `SELECT`) y guarda en la aplicación una conexión con ella, con la contraseña recordada.
2. **Márcala como producción** si lo es: la línea de comandos no escribirá en ella ni con `--permitir-escritura`.
3. **Indica al agente** el nombre de la conexión y que use `armandodb consulta` para consultas sueltas o `armandodb sesion` si va a hacer muchas.
4. **Deja el tope de filas por defecto** (1000) y que el agente pagine o agregue si necesita más.
5. **Revisa el historial** para ver qué ejecutó.

Instrucciones mínimas que se le pueden dar al agente:

```
Para consultar la base de datos usa:
  armandodb consulta --conexion "NOMBRE" --sql "SELECT ..."
La respuesta es JSON por la salida estándar. Si el código de salida no es 0, el error está en la salida de error.
Solo puedes leer. Cada resultado trae como mucho 1000 filas; si "truncado" es true, acota la consulta.
```

## Limitaciones conocidas

- **Probada con SQLite, SQL Server y MySQL a través de túnel SSH** (solo lecturas en este último). Con Sybase ASE aún no se ha verificado.
- **Un fallo de red al conectar no se reintenta de forma automática.** Si el servidor o el túnel SSH no responden en 15 segundos, la llamada termina con código 2 y el motivo en la salida de error. Es a propósito: quien la llama (el agente o la persona) decide si insiste.
- **Sin comandos para explorar:** no hay "listar tablas" ni "describir tabla". Se hace con consultas al catálogo (`SHOW TABLES`, `information_schema`, `sys.objects`...).
- **Una consulta a la vez por sesión:** las consultas se responden en el orden en que llegan.
- **No se puede cancelar una consulta en curso** desde la sesión; solo terminando el proceso.
- **El tope de filas corta la consulta:** al alcanzarlo, los resultados posteriores de esa misma sentencia o lote no se leen.
- **En modo texto, `GO` siempre termina la consulta**, también en SQL Server y Sybase, donde además separa lotes: un script con varios `GO` se responde lote a lote. Para enviarlo entero, usa una línea JSON.
- **Solo Windows.**
