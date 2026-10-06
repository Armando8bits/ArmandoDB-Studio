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

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; Las conexiones guardadas, sesiones y opciones del usuario (%APPDATA%\ArmandoDB Studio) no se tocan al desinstalar.
