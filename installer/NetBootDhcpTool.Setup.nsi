Unicode true
!include "MUI2.nsh"

!ifndef PRODUCT_VERSION
  !error "PRODUCT_VERSION must be supplied by the release version source."
!endif
!ifndef PE_VERSION
  !error "PE_VERSION must be supplied by the release version source."
!endif
VIProductVersion "${PE_VERSION}"
VIFileVersion "${PE_VERSION}"
VIAddVersionKey /LANG=1033 "ProductName" "${PRODUCT_NAME}"
VIAddVersionKey /LANG=1033 "FileDescription" "${PRODUCT_NAME} Setup"
VIAddVersionKey /LANG=1033 "FileVersion" "${PE_VERSION}"
VIAddVersionKey /LANG=1033 "ProductVersion" "${PRODUCT_VERSION}"
VIAddVersionKey /LANG=1033 "CompanyName" "${COMPANY_NAME}"
VIAddVersionKey /LANG=1033 "LegalCopyright" "${PRODUCT_COPYRIGHT}"
VIAddVersionKey /LANG=1033 "OriginalFilename" "NetBootDhcpTool-Setup-v${PRODUCT_VERSION}.exe"
!ifndef SETUP_HELPER
  !define SETUP_HELPER "..\artifacts\setup\NetBootDhcpTool.SetupHelper.exe"
!endif
!ifndef MANIFEST
  !define MANIFEST "..\artifacts\setup\latest-v2.json"
!endif
!ifndef SIGNATURE
  !define SIGNATURE "..\artifacts\setup\latest-v2.json.sig"
!endif
!ifndef FULL_PACKAGE_NAME
  !define FULL_PACKAGE_NAME "NetBootDhcpTool-full-v${PRODUCT_VERSION}.7z"
!endif
!ifndef SETUP_COMPRESSOR
  !define SETUP_COMPRESSOR "zlib"
!endif
!ifndef OUTPUT_DIRECTORY
  !define OUTPUT_DIRECTORY "..\artifacts\setup"
!endif
!ifndef USER_CANCELLED_EXIT_CODE
  !error "USER_CANCELLED_EXIT_CODE must be supplied from the shared InstallExitCode protocol."
!endif

Name "NetBoot DHCP Tool ${PRODUCT_VERSION}"
OutFile "${OUTPUT_DIRECTORY}\NetBootDhcpTool-Setup-v${PRODUCT_VERSION}.exe"
InstallDir "$LOCALAPPDATA\Programs\NetBootDhcpTool"
InstallDirRegKey HKCU "Software\NetBootDhcpTool" "InstallPath"
RequestExecutionLevel admin
SetCompressor /SOLID ${SETUP_COMPRESSOR}
ShowInstDetails show
ShowUnInstDetails show

!define MUI_ABORTWARNING
!define MUI_CUSTOMFUNCTION_ABORT OnUserCancel
!define MUI_LANGDLL_ALLLANGUAGES
!define MUI_WELCOMEPAGE_TEXT "New install: choose a program folder. Older ZIP version: run Setup once to migrate, then use in-app updates. With the new updater: update in-app or use Setup to update in place.$\r$\n$\r$\nSetup verifies the matching Full 7z from release mirrors or beside Setup.exe (offline). User settings and data are preserved.$\r$\n$\r$\n新安装：选择程序目录。旧 ZIP 版：运行一次 Setup 迁移，后续使用应用内更新。已有新版更新器：可应用内更新，也可用 Setup 原位更新。$\r$\n$\r$\nSetup 会校验发布镜像或同目录的匹配 Full 7z（离线）。已有用户配置和数据会保留。"
!define MUI_DIRECTORYPAGE_TEXT_TOP "New installation: use the default or another program folder. Migration or update: select the existing NetBootDhcpTool program folder; browse to it if detection missed it. Do not select the LocalAppData user-data folder.$\r$\n新安装：使用默认目录或其他程序目录。迁移或更新：选择现有 NetBootDhcpTool 程序目录；未自动识别时请手动选择。不要选择 LocalAppData 用户数据目录。"
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "SimpChinese"

Function OnUserCancel
  SetErrorLevel ${USER_CANCELLED_EXIT_CODE}
FunctionEnd

Function .onInit
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File /oname=NetBootDhcpTool.SetupHelper.exe "${SETUP_HELPER}"
  nsExec::Exec /TIMEOUT=15000 '"$PLUGINSDIR\NetBootDhcpTool.SetupHelper.exe" --detect-install "$PLUGINSDIR\detected-install.txt"'
  Pop $0
  ${If} $0 == "0"
    StrCpy $1 ""
    IfFileExists "$PLUGINSDIR\detected-install.txt" 0 detect_done
    FileOpen $2 "$PLUGINSDIR\detected-install.txt" r
    FileRead $2 $1
    FileClose $2
    StrCpy $1 $1 -2
    ${If} $1 != ""
      StrCpy $INSTDIR $1
    ${EndIf}
  ${EndIf}
  detect_done:
  ; LangDLL aborts inside .onInit, before MUI's .onUserAbort callback.
  ; Preserve the same centralized cancellation code for that early dialog too.
  SetErrorLevel ${USER_CANCELLED_EXIT_CODE}
  !insertmacro MUI_LANGDLL_DISPLAY
  SetErrorLevel 0
FunctionEnd

Section "Install"
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File /oname=latest-v2.json "${MANIFEST}"
  File /oname=latest-v2.json.sig "${SIGNATURE}"

  StrCpy $1 "$EXEDIR\${FULL_PACKAGE_NAME}"
  IfFileExists "$1" local_package_ready 0
  StrCpy $1 ""
  DetailPrint "No adjacent Full 7z found. Setup will use the signed dual-source downloader."
  DetailPrint "未发现同目录 Full 7z，安装程序将使用签名双源下载。"
  Goto package_source_ready
  local_package_ready:
  DetailPrint "A same-directory Full 7z was found; the helper will verify it before use."
  DetailPrint "发现同目录 Full 7z，辅助程序将先校验再使用。"
  package_source_ready:
  DetailPrint "Validating the signed Full 7z payload and existing installation."
  DetailPrint "正在验证签名全量 7z 包和现有安装。"
  StrCpy $2 ""
  IfSilent 0 +2
  StrCpy $2 "--silent"
  ExecWait '"$PLUGINSDIR\NetBootDhcpTool.SetupHelper.exe" --full-install "$1" "$PLUGINSDIR\latest-v2.json" "$PLUGINSDIR\latest-v2.json.sig" "$INSTDIR" $2' $0
  ${If} $0 != 0
    SetErrorLevel $0
    Quit
  ${EndIf}
  SetErrorLevel 0
  DetailPrint "Installation completed and the new application passed its health check."
  DetailPrint "安装已完成，新程序已通过启动健康检查。"
SectionEnd
