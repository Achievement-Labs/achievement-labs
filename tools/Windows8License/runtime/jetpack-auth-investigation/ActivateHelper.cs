using System;
using System.Runtime.InteropServices;
[ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IProbeActivationManager {
 void ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string app, [MarshalAs(UnmanagedType.LPWStr)] string args, uint options, out uint pid);
 void ActivateForFile(IntPtr a, IntPtr b, IntPtr c, out uint pid);
 void ActivateForProtocol(IntPtr a, IntPtr b, out uint pid);
}
public static class ProbeActivation {
 public static uint Launch(string app, string args) {
  return LaunchOptions(app,args,2); // AO_NOERRORUI; errors are returned to the caller.
 }
 public static uint LaunchSilent(string app, string args) {
  return LaunchOptions(app,args,0x02000002); // AO_PRELAUNCH | AO_NOERRORUI.
 }
 private static uint LaunchOptions(string app, string args, uint options) {
  object manager=Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")));
  try { uint pid; ((IProbeActivationManager)manager).ActivateApplication(app,args,options,out pid); return pid; }
  finally { Marshal.ReleaseComObject(manager); }
 }
}
