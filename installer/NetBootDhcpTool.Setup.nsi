Unicode true
!include "MUI2.nsh"

!ifndef PRODUCT_VERSION
  !define PRODUCT_VERSION "1.1.0"
!endif
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
!define MUI_WELCOMEPAGE_TEXT "This release introduces the new automatic update system. Run this Setup.exe once; it will verify and download the matching Full 7z from the available release mirrors. For offline installation, place the matching Full 7z beside Setup.exe. User settings and data in LocalAppData are preserved. Future updates use the in-app 7z updater.$\r$\n$\r$\n本版本升级了更新机制。运行本 Setup.exe 一次即可；安装程序会校验并从可用发布镜像下载对应的 Full 7z。离线安装时，可将匹配的 Full 7z 放在 Setup.exe 同目录。用户配置和 LocalAppData 中的数据会保留；后续版本使用应用内 7z 自动更新。"
!define MUI_DIRECTORYPAGE_TEXT_TOP "Choose the existing NetBootDhcpTool folder to migrate it in place. If this is an older portable copy and setup cannot detect it, browse to that folder. Do not choose the LocalAppData user-data folder.$\r$\n请选择现有 NetBootDhcpTool 程序目录以原位迁移。若旧便携版未被自动识别，请浏览选择旧程序目录。不要选择 LocalAppData 用户数据目录。"
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
