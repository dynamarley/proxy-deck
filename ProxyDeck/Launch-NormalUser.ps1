param([string]$Arguments = '',[string]$Executable = (Join-Path $env:LOCALAPPDATA 'ProxyDeck\ProxyDeck.exe'))
$ErrorActionPreference = 'Stop'
# Launch using the existing desktop user's medium-integrity token. This also lets
# setup verify that the application really works without administrator rights.
if (-not ('ProxyDeckDesktopLauncher' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class ProxyDeckDesktopLauncher {
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct STARTUPINFO {
  public int cb; public string reserved,desktop,title; public int x,y,xSize,ySize,xChars,yChars,fill,flags; public short show,reserved2; public IntPtr reservedPtr,input,output,error;
 }
 [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr process,thread; public uint pid,tid; }
 [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
 [DllImport("advapi32.dll",SetLastError=true)] static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
 [DllImport("advapi32.dll",SetLastError=true)] static extern bool DuplicateTokenEx(IntPtr token,uint access,IntPtr attributes,int impersonation,int type,out IntPtr duplicate);
 [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateProcessWithTokenW(IntPtr token,int logon,string app,StringBuilder command,uint flags,IntPtr environment,string cwd,ref STARTUPINFO info,out PROCESS_INFORMATION process);
 [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
 public static uint Launch(int desktopPid,string executable,string args,string directory) {
  IntPtr process=IntPtr.Zero, token=IntPtr.Zero, duplicate=IntPtr.Zero;
  try {
   process=OpenProcess(0x1000,false,desktopPid); if(process==IntPtr.Zero)throw new System.ComponentModel.Win32Exception();
   if(!OpenProcessToken(process,0xB,out token))throw new System.ComponentModel.Win32Exception();
   if(!DuplicateTokenEx(token,0x2000000,IntPtr.Zero,2,1,out duplicate))throw new System.ComponentModel.Win32Exception();
   STARTUPINFO info=new STARTUPINFO();info.cb=Marshal.SizeOf(typeof(STARTUPINFO));info.desktop="winsta0\\default";
   PROCESS_INFORMATION created; var command=new StringBuilder("\""+executable+"\" "+args);
   if(!CreateProcessWithTokenW(duplicate,0,executable,command,0x400,IntPtr.Zero,directory,ref info,out created))throw new System.ComponentModel.Win32Exception();
   CloseHandle(created.thread);CloseHandle(created.process);return created.pid;
  } finally { if(duplicate!=IntPtr.Zero)CloseHandle(duplicate);if(token!=IntPtr.Zero)CloseHandle(token);if(process!=IntPtr.Zero)CloseHandle(process); }
 }
}
'@
}
$desktopProcess = Get-Process -Name explorer | Where-Object { $_.SessionId -eq (Get-Process -Id $PID).SessionId } | Select-Object -First 1
if (-not $desktopProcess) { throw 'No normal Windows desktop session was found.' }
[ProxyDeckDesktopLauncher]::Launch($desktopProcess.Id,$Executable,$Arguments,(Split-Path -Parent $Executable))
