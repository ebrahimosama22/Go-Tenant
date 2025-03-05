using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.ComponentModel;
using System.Configuration.Install;
namespace GoT
{
    //[RunInstaller(true)]
    //public class MyServiceInstaller : Installer
    //{
    //    private ServiceProcessInstaller processInstaller;
    //    private ServiceInstaller serviceInstaller;

    //    public MyServiceInstaller()
    //    {
    //        processInstaller = new ServiceProcessInstaller();
    //        serviceInstaller = new ServiceInstaller();

    //        processInstaller.Account = ServiceAccount.LocalSystem;
    //        serviceInstaller.StartType = ServiceStartMode.Automatic;
    //        serviceInstaller.ServiceName = "Go_tenant";

    //        Installers.Add(processInstaller);
    //        Installers.Add(serviceInstaller);
    //    }
    //}
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        static void Main()
        {
            ServiceBase[] ServicesToRun;
            ServicesToRun = new ServiceBase[]
            {
                new Service2()
            };
            ServiceBase.Run(ServicesToRun);
        }
    }

    
}
