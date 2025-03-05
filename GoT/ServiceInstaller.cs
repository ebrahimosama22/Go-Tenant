using System;
using System.ComponentModel;
using System.Configuration.Install;
using System.ServiceProcess;
using System.Runtime.InteropServices;
namespace GoT
{
    [RunInstaller(true)]
    public class ProjectInstaller : Installer
    {
        private ServiceProcessInstaller processInstaller;
        private ServiceInstaller serviceInstaller;
        public ProjectInstaller()
        {
            // Initialize installers
            processInstaller = new ServiceProcessInstaller();
            serviceInstaller = new ServiceInstaller();
            // Set the service to run under the Local System account (Admin privileges)
            processInstaller.Account = ServiceAccount.LocalSystem;

            // Configure service properties
            serviceInstaller.ServiceName = "Go_tenant"; // Must match ServiceBase.ServiceName
            serviceInstaller.DisplayName = "Go-Tenant"; // Name in services.msc
            serviceInstaller.Description = "Monitors printed documents and logs details.";
            serviceInstaller.StartType = ServiceStartMode.Automatic; // Start on boot

            // Add installers to collection
            Installers.Add(processInstaller);
            Installers.Add(serviceInstaller);
        }
    }
}
