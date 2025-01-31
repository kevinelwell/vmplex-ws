﻿/*
 * Copyright (c) 2022 Ira Strawser. All rights reserved.
 */

using System;
using System.Linq;
using System.ComponentModel;
using System.Management;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Diagnostics;
using VMPlex.WMI;
using ORMi;

namespace VMPlex
{

    public class VirtualMachine : INotifyPropertyChanged
    {
        public enum HeartbeatState
        {
            Ok = 2,
            Error = 6,
            NoContact = 12,
            LostCommunication = 13
        }
        public event PropertyChangedEventHandler PropertyChanged;
        public void NotifyChange(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public enum StateChange : ushort
        {
            Enabled = 2, // Power on
            Disabled = 3, // Power off
            Shutdown = 4, // Attempt orderly shutdown via integration services (this fails more often than it should)
            Offline = 6, // Enabled but offline (Saved state, resume with Enabled)
            Quiesce = 9, // Pause VM (resume with Enabled)
            Reboot = 10, // Request reboot
            Reset = 11, // Power reset
        }

        public VirtualMachine(Msvm_ComputerSystem vm)
        {
            Msvm = vm;
            Name = vm.Name;
            Guid = vm.Guid;
            State = vm.State;
            ProcessID = vm.ProcessID;
            EnhancedSessionModeState = vm.EnhancedSessionModeState;

            //
            // Generates user settings if they don't exist.
            //
            GetVmUserSettings();
        }

        public static void OpenNewVmWizard()
        {
            TryLaunchHVIntegrateInJob(new string[] { "av" });
        }

        public void DeleteFromServer()
        {
            TryLaunchHVIntegrateInJob(new string[] { "dv", Guid });
        }

        public VmConfig GetVmUserSettings()
        {
            var settings = UserSettings.Instance.Settings;

            var vmSetting = settings.VirtualMachines.FirstOrDefault(v => v.Guid == Guid);
            if (vmSetting != null)
            {
                return vmSetting;
            }

            //
            // Create a new entry for this VM.
            //
            settings = UserSettings.Instance.Mutate(s =>
            {
                s.VirtualMachines.Add(
                    new VmConfig
                    {
                        Guid = Guid,
                        Name = Name,
                        DebuggerArguments = ""
                    });

                return s;
            });

            return settings.VirtualMachines.FirstOrDefault(v => v.Guid == Guid);
        }

        public void OpenDebugger()
        {
            var settings = UserSettings.Instance.Settings;
            var vm = GetVmUserSettings();

            if (settings.Debugger.Length == 0 ||
                vm.DebuggerArguments.Length == 0)
            {
                UI.MessageBox.Show(
                   MessageBoxImage.Information,
                    "Debugger settings are incomplete.",
                    "Please specify the debugger and arguments for the " +
                    "virtual machine in the user settings. The settings " +
                    "file will be opened when you close this dialog.");
                UserSettings.Instance.OpenInEditor();
                return;
            }

            var process = new Process();
            process.StartInfo = new ProcessStartInfo()
            {
                FileName = settings.Debugger,
                Arguments = vm.DebuggerArguments,
                UseShellExecute = true
            };

            try
            {
                process.Start();
            }
            catch (Exception exc)
            {
                UI.MessageBox.Show(
                    MessageBoxImage.Error,
                    "Failed to start debugger.",
                    exc.Message);
            }
        }

        public void OpenSettingsDialog()
        {
            TryLaunchHVIntegrateInJob(new string[] { "vm", Guid });
        }

        public static void OpenSwitchManagerDialog()
        {
            TryLaunchHVIntegrateInJob(new string[] { "vs" });
        }

        public static void OpenEditDiskWizard()
        {
            TryLaunchHVIntegrateInJob(new string[] { "ed" });
        }

        public static void OpenHyperVSettingsDialog()
        {
            TryLaunchHVIntegrateInJob(new string[] { "hv" });
        }

        public uint RequestStateChange(StateChange state)
        {
            return Msvm.RequestStateChange((ushort)state);
        }

        public void TypeText(string text)
        {
            Msvm.TypeText(text);
        }

        public bool IsVideoAvailable()
        {
            switch (State)
            {
            case Msvm_ComputerSystem.SystemState.Running:
            case Msvm_ComputerSystem.SystemState.Paused:
            case Msvm_ComputerSystem.SystemState.Pausing:
            case Msvm_ComputerSystem.SystemState.Stopping:
            case Msvm_ComputerSystem.SystemState.Saving:
                return true;
            }
            return false;
        }

        public void UpdateMainInformation(Msvm_ComputerSystem vm)
        {
            Msvm = vm;
            EnhancedSessionModeState = vm.EnhancedSessionModeState;
            State = vm.State;
            Name = vm.Name;
            ProcessID = vm.ProcessID;
            NotifyChange(null);
        }

        public void UpdateSummaryInformation(ManagementBaseObject summary)
        {
            bool notify = false;

            bool loadreceived = false;
            byte[] thumbnail = null;
            foreach (PropertyData pdata in summary.Properties)
            {
                if (pdata.Value == null)
                {
                    continue;
                }

                switch (pdata.Name)
                {
                case "Version":
                    {
                        string version = (string)pdata.Value;
                        if (version != Version)
                        {
                            Version = version;
                            notify = true;
                        }
                    }
                    break;

                case "NumberOfProcessors":
                    {
                        ushort numprocs = (ushort)pdata.Value;
                        if (numprocs != NumberOfProcessors)
                        {
                            NumberOfProcessors = numprocs;
                            notify = true;
                        }
                    }
                    break;

                case "MemoryAvailable":
                    {
                        int memavail = (int)pdata.Value;
                        if (memavail != MemoryAvailable)
                        {
                            MemoryAvailable = memavail;
                            notify = true;
                        }
                    }
                    break;

                case "UpTime":
                    {
                        ulong uptime = (ulong)pdata.Value / 1000;
                        UpTime = new TimeSpan((long)uptime * 10000000);
                    }
                    break;

                case "ProcessorLoad":
                    {
                        loadreceived = true;
                        ushort procload = (ushort)pdata.Value;
                        if (procload != ProcessorLoad)
                        {
                            ProcessorLoad = procload;
                            notify = true;
                        }
                    }
                    break;

                case "MemoryUsage":
                    {
                        ulong memusage = (ulong)pdata.Value;
                        if (memusage != MemoryUsage)
                        {
                            MemoryUsage = memusage;
                            notify = true;
                        }
                    }
                    break;

                case "Heartbeat":
                    {
                        HeartbeatState hbstate = (HeartbeatState)(ushort)pdata.Value;
                        if (hbstate != Heartbeat)
                        {
                            Heartbeat = hbstate;
                            notify = true;
                        }
                    }
                    break;

                case "ThumbnailImageWidth":
                    {
                        ushort width = (ushort)pdata.Value;
                        if (width != ThumbnailWidth)
                        {
                            ThumbnailWidth = width;
                            notify = true;
                        }
                    }
                    break;

                case "ThumbnailImageHeight":
                    {
                        ushort height = (ushort)pdata.Value;
                        if (height != ThumbnailHeight)
                        {
                            ThumbnailHeight = height;
                            notify = true;
                        }
                    }
                    break;

                case "ThumbnailImage":
                    {
                        thumbnail = (byte[])pdata.Value;
                    }
                    break;
                }
            }

            if (!loadreceived)
            {
                ProcessorLoad = 0;
                MemoryUsage = 0;
                Heartbeat = 0;
            }

            if (thumbnail != null)
            {
                try
                {
                    BitmapSource thumbnailBitmap = BitmapSource.Create(
                        ThumbnailWidth,
                        ThumbnailHeight,
                        96d,
                        96d,
                        System.Windows.Media.PixelFormats.Bgr565,
                        null,
                        thumbnail,
                        ThumbnailWidth * 2);
                    thumbnailBitmap.Freeze();
                    Thumbnail = thumbnailBitmap;
                    if (Thumbnail == null)
                    {
                        notify = true;
                    }
                }
                catch (System.NullReferenceException)
                {
                    if (Thumbnail != null)
                    {
                        Thumbnail = null;
                        notify = true;
                    }
                }
            }
            else
            {
                if (Thumbnail != null)
                {
                    Thumbnail = null;
                    notify = true;
                }
            }

            if (notify)
            {
                NotifyChange(null);
            }
            else
            {
                NotifyChange("Self");
                NotifyChange("UpTime");
                NotifyChange("Thumbnail");
            }
        }

        private static void TryLaunchHVIntegrateInJob(string[] args)
        {
            try
            {
                Utility.LaunchHVIntegrateInJob(args);
            }
            catch (Exception exc)
            {
                UI.MessageBox.Show(
                    System.Windows.MessageBoxImage.Error,
                    "Failed to start Hyper-V Integration",
                    exc.Message);
            }
        }

        private Msvm_ComputerSystem Msvm { get; set; }
        public VirtualMachine Self { get { return this; } }
        public string Name { get; set; }
        public string Guid { get; set; }
        public string Version { get; set; }
        public uint ProcessID { get; set; }
        public Msvm_SecurityElement SecurityElement{ get { return Msvm.SecurityElement; } }
        public Msvm_ComputerSystem.SystemState State { get; set; }
        public bool IsRunning {
            get
            {
                switch(State)
                {
                    case Msvm_ComputerSystem.SystemState.Unknown:
                    case Msvm_ComputerSystem.SystemState.Off:
                    case Msvm_ComputerSystem.SystemState.Saved:
                    case Msvm_ComputerSystem.SystemState.Paused:
                    case Msvm_ComputerSystem.SystemState.FastSaved:
                    case Msvm_ComputerSystem.SystemState.Hibernated:
                        return false;
                }
                return true;
            }
        }
        public bool IsPaused { get {  return State == Msvm_ComputerSystem.SystemState.Paused; } }
        public bool IsSaved { get {  return State == Msvm_ComputerSystem.SystemState.Saved; } }
        public bool IsPoweredOn { get { return IsRunning || IsPaused; } }
        public Msvm_ComputerSystem.EnhancedSessionMode EnhancedSessionModeState { get; set; }
        public ushort NumberOfProcessors { get; set; }
        public BitmapSource Thumbnail { get; set; }
        public ushort ThumbnailWidth { get; set; }
        public ushort ThumbnailHeight { get; set; }
        public ushort ProcessorLoad { get; set; }
        public ulong MemoryUsage { get; set; }
        public int MemoryAvailable { get; set; }
        public HeartbeatState Heartbeat { get; set; }
        public TimeSpan UpTime { get; set; }
    }
}
