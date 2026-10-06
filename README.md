# ArmandoDB Studio 🚀

A lightning-fast, lightweight, and distraction-free database manager designed for developers who are tired of bloated GUIs, intrusive paywalls, and rigid keyboard shortcuts. 

Built out of pure frustration with not finding a tool that fit, this is a custom-tailored client for **MySQL**, **SQLite** and **Microsoft SQL Server**, with shortcuts and a workflow that will feel familiar to anyone coming from SQL Server Management Studio (SSMS).

---

## Why this exists?
For over a year and a half, I couldn't find a lightweight manager that matched the way I work: some interfaces felt dated to me, while other tools used more system resources than I wanted or kept core features behind paywalls and pop-ups.

If you love hitting **`Ctrl + E`** to instantly execute your selected query (just like in SSMS) and want something blazing fast that doesn't consume half your RAM, this tool is for you.

## Features
- **SSMS-Style Execution:** Press `Ctrl + E` to run the entire script or just the highlighted selection seamlessly.
- **Multi-Engine Support:** Works with **MySQL** (remote VMs, local instances), **SQLite** and **Microsoft SQL Server** (SQL login or Windows authentication, named instances, LocalDB, `GO` batches) databases.
- **Zero Bloat / Zero Ads:** Open-source, lightweight, no telemetry, no "upgrade to Pro" nags.
- **Customized UI:** Built by a developer, for developers, focusing purely on productivity and speed.

## Known limitations
- **Windows only.** The installer is not code-signed, so Windows SmartScreen shows a warning the first time you run it.
- **Process monitor:** MySQL only. To see connections from other accounts, the MySQL user needs the global `PROCESS` privilege.
- **SQL Server support is new.** It has been tested against LocalDB with Windows authentication. SQL logins, remote servers, named instances over TCP, Azure SQL, SSH tunnels and older server versions should work but have not been verified yet.
- **SQL Server objects are shown with their schema** (`dbo.Customers`), and scripts are sent in `GO`-separated batches, not statement by statement.
- **Backup and restore use `.sql` scripts**, not native formats (`.bak`, `mysqldump` options). Users and permissions are not included. On SQL Server, restoring only some tables with "drop first" removes the foreign keys that tables outside the backup had towards them.
- **Dangerous-statement confirmation is a safety net, not a guarantee.** On SQL Server, a `DELETE` or `UPDATE` without `WHERE` that follows another statement without a `;` in the same batch may go undetected.
- **SQL formatting** is skipped for scripts containing `DELIMITER` or `GO`; select just the query you want to format.
- **Execution plans on SQL Server** require the `SHOWPLAN` permission on the database.
- **Result grids are read-only:** there is no in-grid data editing, table designer or schema comparison.

---

# ArmandoDB Studio 🚀 (Español)

Un gestor de bases de datos ultrarrápido, ligero y libre de distracciones, diseñado para desarrolladores cansados de interfaces pesadas, muros de pago molestos y atajos de teclado rígidos.

Nacido de la pura frustración de no encontrar una herramienta a mi medida, este es un cliente para **MySQL**, **SQLite** y **Microsoft SQL Server** con atajos y una forma de trabajo que resultarán familiares a quien venga de SQL Server Management Studio (SSMS).

---

## ¿Por qué existe esto?
Durante más de año y medio no encontré un gestor ligero que encajara con mi forma de trabajar: algunas interfaces me resultaban anticuadas, y otras herramientas consumían más recursos de los que quería o dejaban funciones clave tras avisos de pago y publicidad.

Si te encanta presionar **`Ctrl + E`** para ejecutar al instante tu consulta seleccionada (exactamente igual que en SSMS) y buscas algo rapidísimo que no consuma media memoria RAM, esta herramienta es para ti.

## Características
- **Ejecución estilo SSMS:** Presiona `Ctrl + E` para ejecutar todo el script o únicamente el texto seleccionado sin fricciones.
- **Soporte Multi-motor:** Funciona con bases de datos **MySQL** (en VMs remotas, servidores locales), **SQLite** y **Microsoft SQL Server** (usuario de SQL o autenticación de Windows, instancias con nombre, LocalDB, lotes `GO`).
- **Cero peso innecesario / Cero publicidad:** Código abierto, ligero, sin telemetría ni anuncios de "actualizar a Pro".
- **Interfaz a medida:** Creado por un desarrollador para desarrolladores, enfocado 100% en la productividad y la velocidad.

## Limitaciones conocidas
- **Solo Windows.** El instalador no está firmado, así que Windows SmartScreen muestra un aviso la primera vez que se ejecuta.
- **Monitor de procesos:** solo para MySQL. Para ver las conexiones de otras cuentas, el usuario de MySQL necesita el privilegio global `PROCESS`.
- **El soporte de SQL Server es reciente.** Está probado contra LocalDB con autenticación de Windows. El acceso con usuario de SQL, los servidores remotos, las instancias con nombre por TCP, Azure SQL, el túnel SSH y las versiones antiguas del servidor deberían funcionar, pero aún no se han verificado.
- **Los objetos de SQL Server se muestran con su esquema** (`dbo.Clientes`), y los scripts se envían por lotes separados con `GO`, no sentencia a sentencia.
- **La copia de seguridad y la restauración usan scripts `.sql`**, no formatos nativos (`.bak`, opciones de `mysqldump`). No incluyen usuarios ni permisos. En SQL Server, restaurar solo algunas tablas con "borrar antes" elimina las claves foráneas que tuvieran hacia ellas las tablas que no están en la copia.
- **La confirmación de sentencias peligrosas es una red de seguridad, no una garantía.** En SQL Server, un `DELETE` o `UPDATE` sin `WHERE` que vaya tras otra sentencia sin `;` en el mismo lote puede no detectarse.
- **Formatear SQL** no se aplica a scripts que contengan `DELIMITER` o `GO`; selecciona solo la consulta que quieras formatear.
- **Los planes de ejecución en SQL Server** requieren el permiso `SHOWPLAN` sobre la base.
- **Las cuadrículas de resultados son de solo lectura:** no hay edición de datos en la cuadrícula, diseñador de tablas ni comparación de esquemas.

---

## Disclaimer / Aviso

ArmandoDB Studio is an independent project. It is not affiliated with, sponsored by, or endorsed by Microsoft, Oracle, or the SQLite project. SQL Server and SQL Server Management Studio are trademarks of Microsoft Corporation; MySQL is a trademark of Oracle Corporation and/or its affiliates. These names are used only to describe compatibility and familiar behavior.

ArmandoDB Studio es un proyecto independiente. No está afiliado a Microsoft, Oracle ni al proyecto SQLite, ni cuenta con su patrocinio o respaldo. SQL Server y SQL Server Management Studio son marcas de Microsoft Corporation; MySQL es una marca de Oracle Corporation y/o sus filiales. Estos nombres se usan únicamente para describir compatibilidad y un comportamiento familiar.

## License / Licencia

This project is open-source and available under the **MIT License**. Feel free to use, modify, audit, and contribute!
