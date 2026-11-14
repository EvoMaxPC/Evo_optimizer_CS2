; ============================================================================
;  FragForge - instalador Inno Setup (Capa 11.5)
;
;  Compilar:
;    .\scripts\publish.ps1
;    iscc installer\FragForge.iss
;
;  Antes de compilar hay que haber publicado: este script NO compila nada.
;  La version sale de MyAppVersion, y se pasa desde la linea de comandos:
;    iscc /DMyAppVersion=0.1.0 installer\FragForge.iss
;  Si no se pasa, se usa la de respaldo de abajo.
; ============================================================================

#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif

#define MyAppName "FragForge"
#define MyAppPublisher "FragForge"
#define MyAppExeName "FragForge.exe"
#define MyCliName "fragforge.exe"
#define PublishRoot "..\artifacts\publish"

[Setup]
AppId={{8F3C2A91-4D7E-4B62-9A15-6E0B7C3D8F24}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
; Program Files: la app pide requireAdministrator (docs/elevacion.md), asi que va donde va lo que
; escribe en HKLM y en servicios.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\artifacts\installer
OutputBaseFilename=FragForge-{#MyAppVersion}-win-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Sin auto-update a proposito: nada de la app se actualiza sola ni llama a casa.
DisableAutoUpdating=yes
; Sin telemetria: este instalador no envia nada.
DisableReadyMemo=no
UninstallDisplayIcon={app}\{#MyAppExeName}
; El uninstaller se firma aparte si hay certificado (scripts/sign.ps1 no lo cubre: lo genera Inno).
; Ver docs/firma.md.

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "cliPath"; Description: "Agregar el CLI al PATH"; GroupDescription: "Linea de comandos"; Flags: checkedonce

[Files]
; El arbol publicado por scripts/publish.ps1. Se excluyen .pdb si los hubiera.
Source: "{#PublishRoot}\app\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishRoot}\cli\win-x64\*"; DestDir: "{app}\cli"; Flags: ignoreversion recursesubdirs createallsubdirs
; Documentos que el usuario puede querer leer sin abrir el repo.
Source: "..\docs\elevacion.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "..\docs\firma.md"; DestDir: "{app}\docs"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Desinstalar {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Registry]
; CLI en el PATH (opcional). HKLM porque la instalacion es para todos los usuarios.
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; ValueType: expandsz; ValueName: "Path"; \
    ValueData: "{olddata};{app}\cli"; Tasks: cliPath; Check: NeedsAddPath('{app}\cli')

[Code]
// ---------------------------------------------------------------------------
// DESINSTALADOR: los dos avisos que el enunciado exige. Nada se revierte ni se
// borra sin preguntar.
// ---------------------------------------------------------------------------

function NeedsAddPath(Param: string): Boolean;
var
  Paths: string;
begin
  if not RegQueryStringValue(HKEY_LOCAL_MACHINE,
    'SYSTEM\CurrentControlSet\Control\Session Manager\Environment', 'Path', Paths) then
  begin
    Result := True;
    exit;
  end;
  Result := Pos(';' + Uppercase(Param) + ';', ';' + Uppercase(Paths) + ';') = 0;
end;

// Quita la entrada del PATH al desinstalar, sin tocar el resto.
procedure RemoveFromPath();
var
  Paths, NewPaths, Entry: string;
  P: Integer;
begin
  if not RegQueryStringValue(HKEY_LOCAL_MACHINE,
    'SYSTEM\CurrentControlSet\Control\Session Manager\Environment', 'Path', Paths) then
    exit;
  Entry := ExpandConstant('{app}\cli');
  NewPaths := Paths;
  P := Pos(';' + Uppercase(Entry), Uppercase(NewPaths));
  if P > 0 then
    Delete(NewPaths, P, Length(Entry) + 1)
  else
  begin
    P := Pos(Uppercase(Entry) + ';', Uppercase(NewPaths));
    if P > 0 then
      Delete(NewPaths, P, Length(Entry) + 1)
    else
    begin
      P := Pos(Uppercase(Entry), Uppercase(NewPaths));
      if P > 0 then
        Delete(NewPaths, P, Length(Entry));
    end;
  end;
  if NewPaths <> Paths then
    RegWriteExpandStringValue(HKEY_LOCAL_MACHINE,
      'SYSTEM\CurrentControlSet\Control\Session Manager\Environment', 'Path', NewPaths);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
  Response: Integer;
begin
  // El aviso va en usUninstall, ANTES de que se borre nada: si se preguntara en usPostUninstall,
  // Abort() llegaria con los ficheros ya eliminados y la pregunta no serviria de nada.
  if CurUninstallStep = usUninstall then
  begin
    // 1) NO revertir tweaks sin preguntar. Desinstalar NO es lo mismo que revertir: si alguien ya
    //    esta contento con un cambio, desinstalar y perderlo seria una sorpresa desagradable.
    Response := MsgBox(
      'Los cambios de sistema que FragForge haya aplicado NO se han revertido.'#13#10#13#10 +
      'Desinstalar no revierte nada. Si quieres volver al estado anterior, abre FragForge antes de ' +
      'desinstalarlo y usa "Revertir", o ejecuta:'#13#10#13#10 +
      '    fragforge revert --last'#13#10#13#10 +
      '¿Quieres continuar con la desinstalacion?',
      mbConfirmation, MB_YESNO);
    if Response = IDNO then
      Abort;

    // 2) NO borrar %ProgramData%\FragForge sin confirmacion explicita. Ahi vive el journal con lo que
    //    hace falta para revertir. Si alguien responde que no, se conserva: es lo prudente, porque
    //    perder el journal convierte los cambios en irreversibles.
    DataDir := ExpandConstant('{commonappdata}\FragForge');
    if DirExists(DataDir) then
    begin
      Response := MsgBox(
        'FragForge guarda en:'#13#10#13#10 +
        '    ' + DataDir + #13#10#13#10 +
        'el journal de cambios y las instantaneas que permiten revertir. Si lo borras, esos cambios ' +
        'dejaran de poder revertirse desde la app.'#13#10#13#10 +
        '¿Borrar esa carpeta? (Recomendado: NO)',
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2);
      if Response = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;

  // El PATH se limpia al final, despues de que el usuario haya aceptado desinstalar.
  if CurUninstallStep = usPostUninstall then
    RemoveFromPath();
end;
