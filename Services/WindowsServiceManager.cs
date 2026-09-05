using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace KaliteKit.Services
{
    public class WindowsServiceManager
    {
        private readonly LoggingService _log;

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern IntPtr OpenSCManager(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ControlService(IntPtr hService, uint dwControl, ref SERVICE_STATUS lpServiceStatus);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseServiceHandle(IntPtr hSCObject);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool StartService(IntPtr hService, uint dwNumServiceArgs, string[]? lpServiceArgVectors);

        private const uint SC_MANAGER_ALL_ACCESS = 0xF003F;
        private const uint SERVICE_ALL_ACCESS = 0xF01FF;
        private const uint SERVICE_CONTROL_STOP = 0x00000001;

        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_STATUS
        {
            public int dwServiceType;
            public int dwCurrentState;
            public int dwControlsAccepted;
            public int dwWin32ExitCode;
            public int dwServiceSpecificExitCode;
            public int dwCheckPoint;
            public int dwWaitHint;
        }

        public WindowsServiceManager(LoggingService log)
        {
            _log = log;
        }

        public async Task StopServiceAsync(string serviceName)
        {
            await Task.Run(() =>
            {
                try
                {
                    var scManager = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
                    if (scManager == IntPtr.Zero) { _log.Warn("Could not open Service Control Manager"); return; }

                    var service = OpenService(scManager, serviceName, SERVICE_ALL_ACCESS);
                    if (service == IntPtr.Zero)
                    {
                        CloseServiceHandle(scManager);
                        return;
                    }

                    var status = new SERVICE_STATUS();
                    if (ControlService(service, SERVICE_CONTROL_STOP, ref status))
                        _log.Info($"Stopped service: {serviceName}");
                    else
                        _log.Warn($"Could not stop service: {serviceName} (state: {status.dwCurrentState})");

                    CloseServiceHandle(service);
                    CloseServiceHandle(scManager);
                }
                catch (Exception ex)
                {
                    _log.Warn($"Failed to stop service {serviceName}: {ex.Message}");
                }
            });

            await Task.Delay(1000);
        }

        public async Task StartServiceAsync(string serviceName)
        {
            await Task.Run(() =>
            {
                try
                {
                    var scManager = OpenSCManager(null, null, SC_MANAGER_ALL_ACCESS);
                    if (scManager == IntPtr.Zero) { _log.Warn("Could not open Service Control Manager"); return; }

                    var service = OpenService(scManager, serviceName, SERVICE_ALL_ACCESS);
                    if (service == IntPtr.Zero)
                    {
                        CloseServiceHandle(scManager);
                        return;
                    }

                    if (StartService(service, 0, null))
                        _log.Info($"Started service: {serviceName}");
                    else
                        _log.Warn($"Could not start service: {serviceName}");

                    CloseServiceHandle(service);
                    CloseServiceHandle(scManager);
                }
                catch (Exception ex)
                {
                    _log.Warn($"Failed to start service {serviceName}: {ex.Message}");
                }
            });

            await Task.Delay(1000);
        }
    }
}
