﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using SharpCompress.Archive.SevenZip;
using SharpCompress.Common;
using SharpCompress.Reader;
using MediaBrowser.InstallUtil.Entities;
using MediaBrowser.InstallUtil.Shortcuts;
using MediaBrowser.InstallUtil.Extensions;
using Microsoft.Win32;
using ServiceStack.Text;

namespace MediaBrowser.InstallUtil
{
    public class Installer
    {
        protected PackageVersionClass PackageClass = PackageVersionClass.Release;
        protected Version RequestedVersion = new Version(4, 0, 0, 0);
        protected Version ActualVersion;
        protected string PackageName = "MBServer";
        protected string TargetExecutablePath;
        protected string TargetArgs = "";
        protected string FriendlyName = "Emby Server";
        protected string Archive = null;
        protected static string StartMenuFolder = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        protected string ProgramDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Emby-Server");
        protected string StartMenuPath = Path.Combine(StartMenuFolder, "Emby");
        protected IProgress<double> Progress;
        protected Action<string> ReportStatus;
        protected string ServiceName;
        protected InstallOperation Operation;
        protected string SystemPath;

        protected static string TempLocation = Path.Combine(Path.GetTempPath(), "Emby");

        protected WebClient MainClient;

        public Installer(InstallationRequest request)
        {
            Init(request);
        }

        /// <summary>
        /// Initialize our internal variables from an installation request
        /// </summary>
        /// <param name="request"></param>
        protected void Init(InstallationRequest request)
        {
            Operation = request.Operation;
            Archive = request.Archive;
            PackageClass = request.PackageClass;
            RequestedVersion = request.Version ?? new Version("4.0");
            Progress = request.Progress;
            ReportStatus = request.ReportStatus;
            MainClient = request.WebClient;
            ServiceName = request.ServiceName;

            switch (request.Product.ToLower())
            {
                case "mbt":
                    PackageName = "MBTheater";
                    FriendlyName = "Emby Theater";
                    ProgramDataPath = request.ProgramDataPath ?? GetTheaterProgramDataPath();
                    TargetExecutablePath = request.TargetExecutablePath ?? Path.Combine(ProgramDataPath, "system", "MediaBrowser.UI.exe");
                    SystemPath = request.SystemPath ?? Path.GetDirectoryName(TargetExecutablePath);
                    break;

                case "mbc":
                    PackageName = "MBClassic";
                    TargetArgs = @"/nostartupanimation /entrypoint:{CE32C570-4BEC-4aeb-AD1D-CF47B91DE0B2}\{FC9ABCCC-36CB-47ac-8BAB-03E8EF5F6F22}";
                    FriendlyName = "Emby for WMC";
                    ProgramDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MediaBrowser" + "-Classic");
                    TargetExecutablePath = request.TargetExecutablePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ehome", "ehshell.exe");
                    SystemPath = request.SystemPath ?? Path.Combine(ProgramDataPath, "system");
                    break;

                default:
                    PackageName = "MBServer";
                    FriendlyName = "Emby Server";
                    ProgramDataPath = request.ProgramDataPath ?? GetServerProgramDataPath();
                    TargetExecutablePath = request.TargetExecutablePath ?? Path.Combine(ProgramDataPath, "system", "MediaBrowser.ServerApplication.exe");
                    SystemPath = request.SystemPath ?? Path.GetDirectoryName(TargetExecutablePath);
                    break;
            }

        }

        public static string GetServerProgramDataPath()
        {
            var installPaths = new List<string>
            {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Emby-Server"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MediaBrowser-Server")
            };
            return installPaths.FirstOrDefault(Directory.Exists) ??
                installPaths.FirstOrDefault();
        }

        public static string GetTheaterProgramDataPath()
        {
            var installPaths = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Emby-Theater"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MediaBrowser-Theater")
            };
            return installPaths.FirstOrDefault(Directory.Exists) ??
                installPaths.FirstOrDefault();
        }

        public static bool IsAdmin 
        {
            get
            {
                return Environment.GetCommandLineArgs().Last() == "admin=true";
            }
        }

        /// <summary>
        /// Parse an argument string array into an installation request and wait on a calling process if there was one
        /// </summary>
        /// <param name="argString"></param>
        /// <returns></returns>
        public static InstallationRequest ParseArgsAndWait(string[] argString)
        {
            var request = new InstallationRequest();

            var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in argString)
            {
                var nameValue = pair.Split('=');
                if (nameValue.Length == 2)
                {
                    args[nameValue[0]] = nameValue[1];
                }
            }
            request.Archive = args.GetValueOrDefault("archive", null);

            request.Product = args.GetValueOrDefault("product", null) ?? ConfigurationManager.AppSettings["product"] ?? "server";
            request.PackageClass = (PackageVersionClass)Enum.Parse(typeof(PackageVersionClass), args.GetValueOrDefault("class", null) ?? ConfigurationManager.AppSettings["class"] ?? "Release");
            request.Version = new Version(args.GetValueOrDefault("version", "4.0"));
            request.ServiceName = args.GetValueOrDefault("service", string.Empty);
            request.ProgramDataPath = args.GetValueOrDefault("installpath", null);
            request.TargetExecutablePath = args.GetValueOrDefault("startpath", null);
            request.SystemPath = args.GetValueOrDefault("systempath", null);

            var callerId = args.GetValueOrDefault("caller", null);
            if (callerId != null)
            {
                // Wait for our caller to exit
                try
                {
                    var process = Process.GetProcessById(Convert.ToInt32(callerId));
                    process.WaitForExit();
                }
                catch (ArgumentException)
                {
                    // wasn't running
                }

                request.Operation = InstallOperation.Update;
            }
            else
            {
                request.Operation = InstallOperation.Install;
            }

            return request;
        }

        /// <summary>
        /// Execute the install process
        /// </summary>
        /// <returns></returns>
        public async Task<InstallationResult> DoInstall()
        {
            Trace.TraceInformation("Installing {0}", FriendlyName);
            ReportStatus(String.Format("Installing {0}...", FriendlyName));

            // Determine Package version
            var version = Archive == null ? await GetPackageVersion() : null;
            ActualVersion = version != null ? version.version : new Version(3, 0);

            Trace.TraceInformation("Version is {0}", ActualVersion);
            // Now try and shut down the server if that is what we are installing and it is running
            var procs = Process.GetProcessesByName("mediabrowser.serverapplication");
            var server = procs.Length > 0 ? procs[0] : null;
            if (PackageName == "MBServer" && server != null)
            {
                Trace.TraceInformation("Shutting down running server {0}", server.ProcessName);
                ReportStatus("Shutting Down Media Browser Server...");
                using (var client = new WebClient())
                {
                    try
                    {
                        client.UploadString("http://localhost:8096/mediabrowser/System/Shutdown", "");
                        try
                        {
                            Trace.TraceInformation("Waiting for server to exit...");
                            server.WaitForExit(30000); //don't hang indefinitely
                            Trace.TraceInformation("Server exited...");
                        }
                        catch (ArgumentException)
                        {
                            // already gone
                            Trace.TraceInformation("Server had already shutdown.");
                        }
                    }
                    catch (WebException e)
                    {
                        if (e.Status != WebExceptionStatus.Timeout && !e.Message.StartsWith("Unable to connect", StringComparison.OrdinalIgnoreCase))
                        {
                            Trace.TraceError("Error shutting down server.  Installation Aborting. {0}", e.Message);
                            return new InstallationResult(false, "Error shutting down server. Please be sure it is not running and try again.", e);
                        }
                        Trace.TraceError("Error attempting to shut downs server.  Installation will continue. {0}", e.Message);
                    }
                }
            }
            else
            {
                if (PackageName == "MBTheater")
                {
                    // Uninstalling MBT - shut it down if it is running
                    var processes = Process.GetProcessesByName("mediabrowser.ui");
                    if (processes.Length > 0)
                    {
                        Trace.TraceInformation("Shutting down MB Theater...");
                        ReportStatus("Shutting Down Media Browser Theater...");
                        try
                        {
                            processes[0].Kill();
                            Trace.TraceInformation("Successfully killed MBT process.");
                        }
                        catch (Exception ex)
                        {
                            Trace.TraceError("Error shutting down MBT. Installation aborting. {0}", ex.Message);
                            return new InstallationResult(false, "Unable to shutdown Media Browser Theater.  Please ensure it is not running and try again.", ex);
                        }
                    }
                }
            }

            // Download if we don't already have it
            if (Archive == null)
            {
                Trace.TraceInformation("Downloading {0} version {1}", FriendlyName, ActualVersion);
                ReportStatus(String.Format("Downloading {0} (version {1})...", FriendlyName, ActualVersion));
                try
                {
                    Archive = await DownloadPackage(version);
                    if (Archive != null) Trace.TraceInformation("Successfully downloaded version {0}", ActualVersion);
                }
                catch (Exception e)
                {
                    Trace.TraceError("Error downloading.  Installation aborting. {0}", e.Message);
                    return new InstallationResult(false, "Error Downloading Package. There may be a problem communicating with the update server.  Please try again.\n\n", e);
                }
            }
            else
            {
                Trace.TraceInformation("Archive to install was supplied {0}", Archive);
            }

            if (Archive == null) return new InstallationResult(false);  //we canceled or had an error that was already reported

            // Create our main directory and set permissions - this should only happen on install
            if (!Directory.Exists(ProgramDataPath))
            {
                Trace.TraceInformation("Creating directory {0}", ProgramDataPath);
                ReportStatus("Configuring directories.  This may take a minute...");
                var info = Directory.CreateDirectory(ProgramDataPath);
                //Trace.TraceInformation("Attempting to set access rights on {0}", ProgramDataPath);
                //await SetPermissions(info);
            }

            if (Path.GetExtension(Archive) == ".msi")
            {
                try
                {
                    var result = await RunMsi(Archive);
                    if (!result.Success) return result;
                }
                catch (Exception e)
                {
                    Trace.TraceError("Error installing msi. "+e.Message);
                    return new InstallationResult(false, e.Message, e);
                }
            }
            else
            {
                // Extract
                var result = await Extract(Archive);
                if (!result.Success) return result;

                // Create shortcut
                ReportStatus("Creating Shortcuts...");
                var fullPath = TargetExecutablePath;

                try
                {
                    Trace.TraceInformation("Creating shortcuts");
                    result = CreateShortcuts(fullPath);
                    if (!result.Success) return result;
                }
                catch (Exception e)
                {
                    Trace.TraceError("Error creating shortcuts. Installation should still be valid. {0}", e.Message);
                    return new InstallationResult(false, "Error Creating Shortcut", e);
                }

            }

            // Now delete the pismo install files
            Trace.TraceInformation("Deleting Pismo install files");
            RemovePath(Path.Combine(ProgramDataPath, "Pismo"));

            // Update stats
            UpdateStats();

            // And run
            return RunProgram();
        }

        protected void UpdateStats()
        {
            try
            {
                var result = MainClient.DownloadString(string.Format("http://www.mb3admin.com/admin/service/package/installed?mac={0}&product={1}&operation={2}&version={3}", GetMacAddress(), PackageName, Operation, ActualVersion));

                if (result != "success")
                {
                Trace.TraceError("Error updating install stats.  Installation is still complete. "+result);
                }
            }
            catch (Exception e)
            {
                Trace.TraceError("Error updating install stats.  Installation is still complete. "+e.Message);
            }
        }

        protected async Task<InstallationResult> Extract(string archive)
        {
            var backupDir = Path.Combine(ProgramDataPath, "System.old");

            Trace.TraceInformation("Starting extract package.");
            ReportStatus("Extracting Package...");
            var result = await ExtractPackage(archive, SystemPath, backupDir);

            if (!result.Success)
            {
                Trace.TraceError("Final extract failure.  Installation aborting.");
                // Delete archive even if failed so we don't try again with this one
                TryDelete(archive);
                return result;
            }
            else
            {
                Trace.TraceInformation("Extract successful.  Will now delete archive {0}", archive);
                // We're done with it so delete it (this is necessary for update operations)
                TryDelete(archive);
                // Also be sure there isn't an old update lying around
                Trace.TraceInformation("Deleting any old updates as well.");
                RemovePath(Path.Combine(ProgramDataPath, "Updates"));
            }

            return new InstallationResult();
        }

        protected async Task<InstallationResult> RunMsi(string archive)
        {
            try
            {
                Trace.TraceInformation("Archive is MSI installer {0}", archive);
                var logPath = Path.Combine(ProgramDataPath, "Logs");
                if (!Directory.Exists(logPath)) Directory.CreateDirectory(logPath);

                // Run in silent mode and wait for it to finish
                // First uninstall any previous version
                //ReportStatus("Uninstalling any previous version...");
                //var logfile = Path.Combine(ProgramDataPath, "logs", "MsiUnInstall.log");
                //Trace.TraceInformation("Calling msi uninstall");
                //var uninstaller = Process.Start("msiexec.exe", "/x \"" + archive + "\" /quiet /le \"" + logfile + "\"");
                //if (uninstaller != null) uninstaller.WaitForExit();
                //else Trace.TraceError("Uninstall start returned null...");
                // And now installer
                Trace.TraceInformation("Calling msi install");
                ReportStatus("Installing " + FriendlyName);
                var logfile = Path.Combine(ProgramDataPath, "logs", PackageName+"-Msi.log");
                var installer = Process.Start("msiexec.exe", "/i \"" + archive + "\" /quiet /l \"" + logfile + "\"");
                installer.WaitForExit(); // let this throw if there is a problem
            }
            catch (Exception e)
            {
                return new InstallationResult(false, "Error running MSI", e);
            }

            return new InstallationResult();
        }

        protected InstallationResult RunProgram()
        {
            if (!string.IsNullOrEmpty(ServiceName))
            {
                Trace.TraceInformation("Attempting to start service {0}", ServiceName);
                ReportStatus(String.Format("Starting {0}...", FriendlyName));

                try
                {
                    Process.Start("cmd", "/c net start " + ServiceName);
                }
                catch (Exception e)
                {
                    Trace.TraceError("Error starting program. Installation should still be valid. {0}", e.Message);
                    return new InstallationResult(false, "Error Starting service " + ServiceName, e);
                }
                return new InstallationResult();
            }

            var executablePath = TargetExecutablePath;

            Trace.TraceInformation("Attempting to start program {0} {1}", executablePath, TargetArgs);
            ReportStatus(String.Format("Starting {0}...", FriendlyName));
            try
            {
                Process.Start(executablePath, TargetArgs);
            }
            catch (Exception e)
            {
                Trace.TraceError("Error starting program. Installation should still be valid. {0}", e.Message);
                return new InstallationResult(false, "Error Executing - " + executablePath + " " + TargetArgs, e);
            }

            Trace.TraceInformation("Installation complete");
            return new InstallationResult();
            
        }

        /// <summary>
        /// Execute the update process
        /// </summary>
        /// <returns></returns>
        public async Task<InstallationResult> DoUpdate()
        {

            ActualVersion = RequestedVersion;
            Trace.TraceInformation("Updating {0} to version {1}...", FriendlyName, ActualVersion);
            ReportStatus(String.Format("Updating {0}...", FriendlyName));

            if (Path.GetExtension(Archive) == ".msi")
            {
                try
                {
                    var result = await RunMsi(Archive);
                    if (!result.Success) return result;
                }
                catch (Exception e)
                {
                    Trace.TraceError("Error installing msi. " + e.Message);
                    return new InstallationResult(false, e.Message, e);
                }
            }
            else
            {
                // Extract
                var result = await Extract(Archive);
                if (!result.Success) return result;

            }

            // Update stats
            UpdateStats();

            // And run
            return RunProgram();

        }
        /// <summary>
        /// Set permissions for all users
        /// </summary>
        /// <param name="directoryInfo"></param>
        private Task SetPermissions(DirectoryInfo directoryInfo)
        {
            return Task.Run(() =>
            {
                var securityIdentifier = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

                var directorySecurity = directoryInfo.GetAccessControl();
                var rule = new FileSystemAccessRule(
                        securityIdentifier,
                        FileSystemRights.Write |
                        FileSystemRights.ReadAndExecute |
                        FileSystemRights.Modify,
                        AccessControlType.Allow);
                bool modified;

                directorySecurity.ModifyAccessRule(AccessControlModification.Add, rule, out modified);
                directoryInfo.SetAccessControl(directorySecurity);
                
            });

        }

        protected async Task<PackageVersionInfo> GetPackageVersion()
        {
            // get the package information for the server
            Trace.TraceInformation("Attempting to retrieve latest version of {0}", PackageName);

            try
            {
                var json = await MainClient.DownloadStringTaskAsync("http://www.mb3admin.com/admin/service/package/retrieveAll?name=" + PackageName);
                var packages = JsonSerializer.DeserializeFromString<List<PackageInfo>>(json);
                Trace.TraceInformation("Found {0} versions.  Will choose latest one of {1} class", packages.Count, PackageClass);

                return packages[0].versions.Where(v => v.classification <= PackageClass).OrderByDescending(v => v.version).FirstOrDefault(v => v.version <= RequestedVersion);
            }
            catch (Exception e)
            {
                return null;
            }
        }

        /// <summary>
        /// Download our specified package to an archive in a temp location
        /// </summary>
        /// <returns>The fully qualified name of the downloaded package</returns>
        protected async Task<string> DownloadPackage(PackageVersionInfo version)
        {
            var success = false;
            var retryCount = 0;
            var archiveFile = Path.Combine(PrepareTempLocation(), version.targetFilename);

            while (!success && retryCount < 3)
            {

                // setup download progress and download the package
                MainClient.DownloadProgressChanged += DownloadProgressChanged;
                try
                {
                    await MainClient.DownloadFileTaskAsync(version.sourceUrl, archiveFile);
                    success = true;
                }
                catch (WebException e)
                {
                    if (e.Status == WebExceptionStatus.RequestCanceled)
                    {
                        Trace.TraceInformation("Download cancelled");
                        return null;
                    }
                    if (retryCount < 3 && (e.Status == WebExceptionStatus.Timeout || e.Status == WebExceptionStatus.ConnectFailure || e.Status == WebExceptionStatus.ProtocolError))
                    {
                        Thread.Sleep(500); //wait just a sec
                        PrepareTempLocation(); //clear this out
                        retryCount++;
                    }
                    else
                    {
                        throw;
                    }
                }
            }

            return archiveFile;
        }

        void DownloadProgressChanged(object sender, DownloadProgressChangedEventArgs e)
        {
            Progress.Report(e.ProgressPercentage);
        }

        /// <summary>
        /// Extract the provided archive to our program root
        /// It is assumed the archive is a zip file relative to that root (with all necessary sub-folders)
        /// </summary>
        /// <param name="archive"></param>
        protected Task<InstallationResult> ExtractPackage(string archive, string systemDir, string backupDir)
        {
            return Task.Run(() =>
                                {
                                    var retryCount = 0;
                                    var success = false;
                                    // Delete old content of system
                                    if (Directory.Exists(systemDir))
                                    {
                                        Trace.TraceInformation("Creating backup by moving {0} to {1}", systemDir, backupDir);

                                        try
                                        {
                                            if (Directory.Exists(backupDir)) Directory.Delete(backupDir, true);

                                        }
                                        catch (Exception e)
                                        {
                                            Trace.TraceError("Error deleting previous backup. {0}", e.Message);
                                            return new InstallationResult(false, "Could not delete previous backup directory.", e);
                                        }

                                        retryCount = 0;
                                        success = false;
                                        while (!success)
                                        {
                                            try
                                            {
                                                Directory.Move(systemDir, backupDir);
                                                success = true;
                                            }
                                            catch (Exception e)
                                            {
                                                // Sighting of system->ntkrnlpa.exe->fltmgr.sys(6.1.7600.16385) on Windows 7 with no updates
                                                // shows that the release the file handle to the .exe in systemDir folder can take up to 
                                                // a minute to clear. Appears to be a flushing issue and the shutdown and move(delete) happen too quickly.
                                                // fltmgr.sys performs a CreateFileMapping on MediaBrowser.ServerApplication.exe
                                                // then asynchronously with the application closure it finally does a Close on the handle about a minute later.

                                                // Be prepared to wait up to 5 mins for all file accesses to clear before Move(delete) will succeed.
                                                // Only required on some Windows systems.
                                                if (retryCount < 5 * 60) 
                                                {
                                                    Trace.TraceError("Move attempt failed (likely another process still has a file open). Will retry... Error: " + e.Message);
                                                    Thread.Sleep(1000);
                                                    retryCount++;
                                                }
                                                else
                                                {
                                                    Trace.TraceError("Error creating backup. {0}", e.Message);
                                                    return new InstallationResult(false, "Could not move system directory to backup.", e);
                                                }
                                            }
                                        }
                                    }

                                    // And extract
                                    retryCount = 0;
                                    success = false;
                                    while (!success)
                                    {
                                        try
                                        {
                                            using (var fs = File.OpenRead(archive))
                                            using (var reader = ReaderFactory.Open(fs))
                                            {
                                                reader.WriteAllToDirectory(ProgramDataPath, ExtractOptions.ExtractFullPath | ExtractOptions.Overwrite);
                                                success = true;
                                            }
                                        }
                                        catch (Exception e)
                                        {
                                            if (retryCount < 3)
                                            {
                                                Trace.TraceError("Extract attempt failed. Will retry... Error: "+e.Message);
                                                Thread.Sleep(250);
                                                retryCount++;
                                            }
                                            else
                                            {
                                                //Rollback
                                                Trace.TraceError("Final extract attempt failed. Rolling back. Error: "+e.Message);
                                                RollBack(systemDir, backupDir);
                                                return new InstallationResult(false, String.Format("Could not extract {0} to {1} after {2} attempts.", archive, ProgramDataPath, retryCount), e);
                                            }
                                        }
                                    }

                                    return new InstallationResult();

                                });
        }

        protected void RollBack(string systemDir, string backupDir)
        {
            if (Directory.Exists(backupDir))
            {
                if (Directory.Exists(systemDir)) Directory.Delete(systemDir);
                Directory.Move(backupDir, systemDir);
            }
        }

        /// <summary>
        /// Create a shortcut in the current user's start menu
        ///  Only do current user to avoid need for admin elevation
        /// </summary>
        /// <param name="targetExe"></param>
        protected InstallationResult CreateShortcuts(string targetExe)
        {
            // get path to users start menu
            var startMenu = StartMenuPath;
            if (!Directory.Exists(startMenu)) Directory.CreateDirectory(startMenu);

            Trace.TraceInformation("Creating start menu shortcut {0}", Path.Combine(startMenu, FriendlyName + ".lnk"));

            var product = new ShellShortcut(Path.Combine(startMenu, FriendlyName + ".lnk")) { Path = targetExe, Description = "Run " + FriendlyName };
            product.Save();

            if (PackageName == "MBServer")
            {
                var path = Path.Combine(startMenu, "Emby Server Dashboard.lnk");
                Trace.TraceInformation("Creating dashboard shortcut {0}", path);
                var dashboard = new ShellShortcut(path) { Path = @"http://localhost:8096/web/dashboard.html", Description = "Open the Emby Server Dashboard" };
                dashboard.Save();
            }

            return CreateUninstaller(Path.Combine(Path.GetDirectoryName(targetExe) ?? "", "MediaBrowser.Uninstaller.exe") + " " + (PackageName == "MBServer" ? "server" : "mbt"), targetExe);

        }

        /// <summary>
        /// Create uninstall entry in add/remove
        /// </summary>
        /// <param name="uninstallPath"></param>
        /// <param name="targetExe"></param>
        private InstallationResult CreateUninstaller(string uninstallPath, string targetExe)
        {
            Trace.TraceInformation("Creating uninstaller shortcut");
            var parent = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", true);
            {
                if (parent == null)
                {
                    var rootParent = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion", true);
                    {
                        if (rootParent != null)
                        {
                            Trace.TraceInformation("Root uninstall key did not exist.  Creating {0}", (rootParent.Name + @"\Uninstall"));

                            parent = rootParent.CreateSubKey("Uninstall");
                            if (parent == null)
                            {
                                Trace.TraceError("Unable to create uninstall key {0}", (rootParent.Name + @"\Uninstall"));
                                return new InstallationResult(false, "Unable to create Uninstall registry key.  Program is still installed sucessfully.");
                            }
                        }
                    }
                }
                try
                {
                    RegistryKey key = null;

                    try
                    {
                        key = parent.OpenSubKey(FriendlyName, true) ??
                              parent.CreateSubKey(FriendlyName);

                        if (key == null)
                        {
                            Trace.TraceError("Unable to create uninstall key {0}\\{1}", parent.Name, FriendlyName);
                            return new InstallationResult(false, String.Format("Unable to create uninstaller entry'{0}\\{1}'.  Program is still installed successfully.", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", FriendlyName));
                        }

                        key.SetValue("DisplayName", FriendlyName);
                        key.SetValue("ApplicationVersion", ActualVersion);
                        key.SetValue("Publisher", "Media Browser Team");
                        key.SetValue("DisplayIcon", targetExe);
                        key.SetValue("DisplayVersion", ActualVersion.ToString(2));
                        key.SetValue("URLInfoAbout", "http://www.mediabrowser3.com");
                        key.SetValue("Contact", "http://community.mediabrowser.tv");
                        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                        key.SetValue("UninstallString", uninstallPath);
                    }
                    finally
                    {
                        if (key != null)
                        {
                            key.Close();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Trace.TraceError("Error writing uninstall information to registry. {0}", ex.Message);
                    return new InstallationResult(false, "An error occurred writing uninstall information to the registry.", ex);
                }
            }
            
            return new InstallationResult();
        }

        /// <summary>
        /// Prepare a temporary location to download to
        /// </summary>
        /// <returns>The path to the temporary location</returns>
        protected string PrepareTempLocation()
        {
            ClearTempLocation(TempLocation);
            Directory.CreateDirectory(TempLocation);
            return TempLocation;
        }

        /// <summary>
        /// Publicly accessible version to clear our temp location
        /// </summary>
        public static void ClearTempLocation()
        {
            ClearTempLocation(TempLocation);
        }

        /// <summary>
        /// Clear out (delete recursively) the supplied temp location
        /// </summary>
        /// <param name="location"></param>
        protected static void ClearTempLocation(string location)
        {
            if (Directory.Exists(location))
            {
                Directory.Delete(location, true);
            }
        }

        private static void RemovePath(string path)
        {
            try
            {
                Directory.Delete(path, true);
            }
            catch (DirectoryNotFoundException)
            {
            }

        }

        private bool TryDelete(string file)
        {
            try
            {
                File.Delete(file);
            }
            catch (FileNotFoundException)
            {
            }
            catch (Exception)
            {
                return false;
            }

            return true;
        }
        /// <summary>
        /// Returns MAC Address from first Network Card in Computer
        /// </summary>
        /// <returns>[string] MAC Address</returns>
        public string GetMacAddress()
        {
            var mc = new ManagementClass("Win32_NetworkAdapterConfiguration");
            var moc = mc.GetInstances();
            var macAddress = String.Empty;
            foreach (ManagementObject mo in moc)
            {
                if (macAddress == String.Empty)  // only return MAC Address from first card
                {
                    try
                    {
                        if ((bool)mo["IPEnabled"]) macAddress = mo["MacAddress"].ToString();
                    }
                    catch
                    {
                        mo.Dispose();
                        return "";
                    }
                }
                mo.Dispose();
            }

            return macAddress.Replace(":", "");
        }
    }
}
