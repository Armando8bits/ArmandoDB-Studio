# Publicar una versión

Pasos para generar el instalador y publicarlo en GitHub Releases. El instalador se genera en el equipo y se sube a la página de Releases; no se guarda en el repositorio (`installer/Output/` está en `.gitignore`).

## Requisitos (solo la primera vez)

- SDK de .NET 10.
- Inno Setup 6: `winget install JRSoftware.InnoSetup`

## 1. Preparar el código

1. Subir el número de versión en `MySmdb.csproj` (`<Version>1.0.0</Version>`). El nombre y la versión del instalador se toman de ahí.
2. Si cambió algo visible para el usuario, actualizar `README.md`.
3. Hacer commit y `git push` de la rama desde la que se publica.

## 2. Probar y generar el instalador

Cerrar ArmandoDB Studio si está abierto (si no, falla la copia del ejecutable). Desde la carpeta del proyecto, **en PowerShell**:

```powershell
dotnet test tests\MySmdb.Tests
.\installer\build-installer.ps1
```

Desde el símbolo del sistema (cmd) el `.ps1` se abre en el Bloc de notas en vez de ejecutarse. Ahí hay que usar:

```
powershell -ExecutionPolicy Bypass -File .\installer\build-installer.ps1
```

Esa misma forma sirve si PowerShell responde que la ejecución de scripts está deshabilitada.

El resultado queda en `installer\Output\ArmandoDBStudio-Setup-<versión>.exe`.

## 3. Comprobar el instalador

Instalarlo en el equipo, abrir la aplicación, conectar a una base y ejecutar una consulta. Si es posible, probarlo también en otro equipo que no tenga .NET instalado.

## 4. Crear el release en GitHub

1. Abrir <https://github.com/Armando8bits/ArmandoDB-Studio/releases> y pulsar **Draft a new release**.
2. **Choose a tag:** escribir `v<versión>` (por ejemplo `v1.0.0`) y elegir **Create new tag on publish**.
3. **Target:** la rama del paso 1.
4. **Título:** `ArmandoDB Studio <versión>`.
5. **Descripción:** qué hay de nuevo, qué es experimental y el aviso de SmartScreen (ver la plantilla de abajo).
6. Arrastrar el `.exe` a **Attach binaries**.
7. Marcar **Set as a pre-release** mientras sea una versión de prueba.
8. **Publish release**.

## Reglas

- Una etiqueta por versión (`v1.0.0`, `v1.0.1`, ...). No reemplazar el archivo de un release ya publicado: se publica una versión nueva.
- No subir el instalador ni la carpeta `publish\` al repositorio.
- El instalador no está firmado: Windows SmartScreen avisa al ejecutarlo. Hay que decirlo siempre en la descripción.

## Plantilla de la descripción

```markdown
## Novedades
- ...

## Correcciones
- ...

## Instalación
Descarga `ArmandoDBStudio-Setup-<versión>.exe` y ejecútalo. No hace falta instalar .NET.

El instalador no está firmado, así que Windows SmartScreen muestra un aviso:
pulsa **Más información** y luego **Ejecutar de todas formas**.

## Limitaciones conocidas
Ver el [README](https://github.com/Armando8bits/ArmandoDB-Studio#limitaciones-conocidas).
```
