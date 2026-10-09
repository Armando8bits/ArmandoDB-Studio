; Instalador de ArmandoDB Studio (Inno Setup 6).
; Empaqueta la publicación autónoma de ..\publish\win-x64, que ya incluye .NET:
; el equipo de destino no necesita tener .NET instalado.
; No se compila a mano: lo hace build-installer.ps1, que primero publica la aplicación.

#define AppName "ArmandoDB Studio"
#define AppExe "ArmandoDBStudio.exe"
#define SourceDir "..\publish\win-x64"
; La versión se lee del propio .exe ("1.0.0.0" -> "1.0.0"), así nunca se desincroniza del proyecto.
#define AppVersion RemoveFileExt(GetVersionNumbersString(SourceDir + "\" + AppExe))

[Setup]
; Identificador fijo de la aplicación: no cambiarlo, o las actualizaciones se instalarían como otro programa.
AppId={{B7C1F0C2-6C1E-4E57-9E0B-6B1D5B0E7A11}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Roque A Ramírez
AppPublisherURL=https://github.com/Armando8bits/ArmandoDB-Studio
AppSupportURL=https://github.com/Armando8bits/ArmandoDB-Studio/issues
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir=Output
OutputBaseFilename=ArmandoDBStudio-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Sin permisos de administrador por defecto (instala solo para el usuario); el asistente deja elegir "para todos".
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
; Si la aplicación está abierta al actualizar, ofrece cerrarla.
CloseApplications=yes
; El instalador puede cambiar el PATH: avisa a Windows para que las terminales nuevas lo vean sin reiniciar sesión.
ChangesEnvironment=yes

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
spanish.CommandLineGroup=Línea de comandos:
english.CommandLineGroup=Command line:
spanish.AddToPath=Añadir armandodb al PATH (para usarlo desde cualquier terminal, script o agente)
english.AddToPath=Add armandodb to PATH (to use it from any terminal, script or agent)

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
; Marcada por defecto. Lo que hace está en [Code]: añade la carpeta de instalación al PATH y la quita al desinstalar.
Name: "addtopath"; Description: "{cm:AddToPath}"; GroupDescription: "{cm:CommandLineGroup}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; Las conexiones guardadas, sesiones y opciones del usuario (%APPDATA%\ArmandoDB Studio) no se tocan al desinstalar.

[Code]
// La carpeta de instalación en el PATH, para llamar a armandodb.exe por su nombre.
// Instalación solo para el usuario: su PATH (HKCU). Instalación para todos: el PATH del equipo (HKLM).

function EnvRoot: Integer;
begin
  if IsAdminInstallMode then Result := HKEY_LOCAL_MACHINE else Result := HKEY_CURRENT_USER;
end;

function EnvKey: String;
begin
  if IsAdminInstallMode then Result := 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment'
  else Result := 'Environment';
end;

// Posición de la carpeta dentro de ";ruta1;ruta2;", sin distinguir mayúsculas; 0 si no está.
function PathPosition(Paths, Dir: String): Integer;
begin
  Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Paths) + ';');
end;

procedure AddToPath(Dir: String);
var
  Paths: String;
begin
  if not RegQueryStringValue(EnvRoot, EnvKey, 'Path', Paths) then Paths := '';
  if PathPosition(Paths, Dir) > 0 then exit;
  if (Paths <> '') and (Paths[Length(Paths)] <> ';') then Paths := Paths + ';';
  // Tipo "expandible", que es el del PATH: conserva las referencias a otras variables (%USERPROFILE%...).
  RegWriteExpandStringValue(EnvRoot, EnvKey, 'Path', Paths + Dir);
end;

procedure RemoveFromPath(Dir: String);
var
  Paths, Wrapped: String;
  Position: Integer;
begin
  if not RegQueryStringValue(EnvRoot, EnvKey, 'Path', Paths) then exit;
  Position := PathPosition(Paths, Dir);
  if Position = 0 then exit;
  Wrapped := ';' + Paths + ';';
  // Quita ";carpeta" y deja el resto como estaba.
  Delete(Wrapped, Position, Length(Dir) + 1);
  Paths := Copy(Wrapped, 2, Length(Wrapped) - 2);
  RegWriteExpandStringValue(EnvRoot, EnvKey, 'Path', Paths);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep <> ssPostInstall then exit;
  // Al reinstalar con la casilla desmarcada, se quita lo que hubiera puesto una instalación anterior.
  if WizardIsTaskSelected('addtopath') then AddToPath(ExpandConstant('{app}'))
  else RemoveFromPath(ExpandConstant('{app}'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then RemoveFromPath(ExpandConstant('{app}'));
end;
