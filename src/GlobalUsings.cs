global using System.IO;

// WinScope's own Windows API calls load their DLLs only from System32, never from the app's folder.
// (This covers WinScope's DllImports only, not the .NET runtime's own native files – see the README note on
// keeping WinScope.exe somewhere only admins can change if you use "Restart as admin".)
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
