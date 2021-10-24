﻿using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace ADB_Explorer.Services
{
    public abstract class FileOperation : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        public enum OperationStatus
        {
            Waiting,
            InProgress,
            Completed,
            Canceled,
            Failed
        }

        public string OperationName { get; protected set; }

        public Dispatcher Dispatcher { get; }

        public ADBService.Device Device { get; }
        public string FilePath { get; }

        public string FileName
        { 
            get
            {
                return System.IO.Path.GetFileName(FilePath);
            } 
        }

        private OperationStatus status;
        public OperationStatus Status
        {
            get
            {
                return status;
            }
            protected set
            {
                if (Dispatcher != null && !Dispatcher.CheckAccess())
                {
                    Dispatcher.Invoke(() => Status = value);
                    return;
                }

                status = value;
                NotifyPropertyChanged();
            }
        }

        private object statusInfo;
        public object StatusInfo
        {
            get
            {
                return statusInfo;
            }
            protected set
            {
                if (Dispatcher != null && !Dispatcher.CheckAccess())
                {
                    Dispatcher.Invoke(() => StatusInfo = value);
                    return;
                }

                statusInfo = value;
                NotifyPropertyChanged();
            }
        }

        public FileOperation(Dispatcher dispatcher, ADBService.Device adbDevice, string filePath)
        {
            Dispatcher = dispatcher;
            Device = adbDevice;
            FilePath = filePath;
            Status = OperationStatus.Waiting;
        }

        public abstract void Start();
        public abstract void Cancel();

        protected virtual void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
