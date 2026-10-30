#ifndef Payload
  #error Payload is required
#endif
#ifndef OutDir
  #error OutDir is required
#endif
[Setup]
AppId={{4F4312DD-C6B4-49F3-B86C-197BF15203ED}
AppName=LinkGate Trial
AppVersion=0.3.0-preview.2
AppPublisher=LinkGate contributors
DefaultDirName={localappdata}\Programs\LinkGateTrial
DefaultGroupName=LinkGate Trial
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutDir}
OutputBaseFilename=LinkGate-0.3.0-preview.2-Windows-x64-Setup
Compression=lzma2
SolidCompression=yes
CloseApplications=no
RestartApplications=no
UninstallDisplayIcon={app}\agent\LinkGate.Agent.Console.exe
[Files]
Source: "{#Payload}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\LinkGate"; Filename: "{app}\Start-LinkGate.cmd"; WorkingDir: "{app}"
Name: "{group}\Quick start"; Filename: "{sys}\notepad.exe"; Parameters: """{app}\QUICKSTART.md"""
[Run]
Filename: "{sys}\notepad.exe"; Parameters: """{app}\QUICKSTART.md"""; Flags: postinstall skipifsilent unchecked; Description: "Open quick start"
