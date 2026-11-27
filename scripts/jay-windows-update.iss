; The running launcher supplies its existing portable installation directory via /DIR.
[Setup]
AppId=nz.work.minecraft.jay
AppName=Jay
AppVersion={#JayVersion}
AppendDefaultDirName=no
DefaultDirName={localappdata}\Programs\Jay
UsePreviousAppDir=no
OutputBaseFilename=Jay-win-x64-setup
OutputDir=..\packages
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
DisableProgramGroupPage=yes
Uninstallable=no
CloseApplications=yes
RestartApplications=no
Compression=lzma2/fast

[Files]
Source: "..\out\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Run]
Filename: "{app}\Jay.exe"; Description: "{cm:LaunchProgram,Jay}"; Flags: postinstall nowait skipifsilent
