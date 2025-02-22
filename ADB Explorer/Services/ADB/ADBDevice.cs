﻿using ADB_Explorer.Helpers;
using ADB_Explorer.Models;
using ADB_Explorer.ViewModels;
using static ADB_Explorer.Models.AbstractFile;

namespace ADB_Explorer.Services;

public partial class ADBService
{
    private const string GET_PROP = "getprop";
    private const string ANDROID_VERSION = "ro.build.version.release";
    private const string BATTERY = "dumpsys battery";
    private const string MMC_PROP = "vold.microsd.uuid";
    private const string OTG_PROP = "vold.otgstorage.uuid";

    /// <summary>
    /// First partition of MMC block device 0 / 1
    /// </summary>
    private static readonly string[] MMC_BLOCK_DEVICES = { "/dev/block/mmcblk0p1", "/dev/block/mmcblk1p1" };

    private static readonly string[] EMULATED_DRIVES_GREP = { "|", "grep", "-E", "'/mnt/media_rw/|/storage/'" };

    public class AdbDevice : Device
    {
        public LogicalDeviceViewModel Device { get; private set; }

        public override string ID => Device.ID;

        public override DeviceType Type => Device.Type;

        public override DeviceStatus Status => Device.Status;

        public override string IpAddress => Device.IpAddress;

        public AdbDevice(LogicalDeviceViewModel other)
        {
            Device = other;
        }

        private const string CURRENT_DIR = ".";
        private const string PARENT_DIR = "..";
        private static readonly string[] SPECIAL_DIRS = { CURRENT_DIR, PARENT_DIR };
        private static readonly char[] LINE_SEPARATORS = { '\n', '\r' };

        private enum UnixFileMode : UInt32
        {
            S_IFMT = 0b1111 << 12,   // bit mask for the file type bit fields
            S_IFSOCK = 0b1100 << 12, // socket
            S_IFLNK = 0b1010 << 12,  // symbolic link
            S_IFREG = 0b1000 << 12,  // regular file
            S_IFBLK = 0b0110 << 12,  // block device
            S_IFDIR = 0b0100 << 12,  // directory
            S_IFCHR = 0b0010 << 12,  // character device
            S_IFIFO = 0b0001 << 12   // FIFO
        }

        private static FileStat CreateFile(string path, string stdoutLine)
        {
            var match = AdbRegEx.RE_LS_FILE_ENTRY.Match(stdoutLine);
            if (!match.Success)
            {
                throw new Exception($"Invalid output for adb ls command: {stdoutLine}");
            }

            var name = match.Groups["Name"].Value;
            var size = UInt64.Parse(match.Groups["Size"].Value, NumberStyles.HexNumber);
            var time = long.Parse(match.Groups["Time"].Value, NumberStyles.HexNumber);
            var mode = UInt32.Parse(match.Groups["Mode"].Value, NumberStyles.HexNumber);

            if (SPECIAL_DIRS.Contains(name))
                return null;

            return new(
                fileName: name,
                path: FileHelper.ConcatPaths(path, name),
                type: ParseFileMode(mode),
                size: (mode != 0) ? size : new UInt64?(),
                modifiedTime: (time > 0) ? DateTimeOffset.FromUnixTimeSeconds(time).DateTime.ToLocalTime() : new DateTime?(),
                isLink: (mode & (UInt32)UnixFileMode.S_IFMT) == (UInt32)UnixFileMode.S_IFLNK);
        }

        private static FileType ParseFileMode(uint mode) =>
            (UnixFileMode)(mode & (UInt32)UnixFileMode.S_IFMT) switch
            {
                UnixFileMode.S_IFSOCK => FileType.Socket,
                UnixFileMode.S_IFLNK => FileType.Unknown,
                UnixFileMode.S_IFREG => FileType.File,
                UnixFileMode.S_IFBLK => FileType.BlockDevice,
                UnixFileMode.S_IFDIR => FileType.Folder,
                UnixFileMode.S_IFCHR => FileType.CharDevice,
                UnixFileMode.S_IFIFO => FileType.FIFO,
                _ => FileType.Unknown,
            };

        public FileType GetFile(string path, CancellationToken cancellationToken)
        {
            if (ExecuteDeviceAdbShellCommand(ID, "stat", out string stdout, out _, cancellationToken, "-L", "-c", "%f", EscapeAdbString(path)) == 0)
                return ParseFileMode(UInt32.Parse(stdout, NumberStyles.HexNumber));

            return FileType.Unknown;
        }

        public void ListDirectory(string path, ref ConcurrentQueue<FileStat> output, CancellationToken cancellationToken)
        {
            // Execute adb ls to get file list
            var stdout = ExecuteDeviceAdbCommandAsync(ID, "ls", cancellationToken, EscapeAdbString(path));
            foreach (string stdoutLine in stdout)
            {
                var item = CreateFile(path, stdoutLine);

                if (item is null)
                    continue;

                output.Enqueue(item);
            }
        }

        public AdbSyncStatsInfo DoFileSync(
            string cmd,
            string arg,
            string target,
            string source,
            ref ObservableList<FileOpProgressInfo> updates,
            CancellationToken cancellationToken)
        {
            var stdout = ExecuteCommandAsync(
                Data.ProgressRedirectionPath,
                ADB_PATH,
                Encoding.Unicode,
                cancellationToken,
                "-s",
                ID,
                cmd,
                arg,
                EscapeAdbString(source),
                EscapeAdbString(target));

            // Each line should be a progress update (but sometimes the output can be weird)
            string lastStdoutLine = null;
            foreach (string stdoutLine in stdout)
            {
                lastStdoutLine = stdoutLine;
                if (string.IsNullOrWhiteSpace(lastStdoutLine))
                    continue;

                var progressMatch = AdbRegEx.RE_FILE_SYNC_PROGRESS.Match(stdoutLine);
                if (progressMatch.Success)
                    updates.Add(new AdbSyncProgressInfo(progressMatch));
                else
                {
                    var errorMatch = AdbRegEx.RE_FILE_SYNC_ERROR.Match(stdoutLine);
                    if (errorMatch.Success && SyncErrorInfo.New(errorMatch) is SyncErrorInfo error)
                    {
                        updates.Add(error);
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(lastStdoutLine))
                return null;

            var match = AdbRegEx.RE_FILE_SYNC_STATS.Match(lastStdoutLine);
            if (!match.Success)
                return null;

            return new AdbSyncStatsInfo(match);
        }

        public string TranslateDevicePath(string path)
        {
            if (path.StartsWith('~'))
                path = path.Length == 1 ? "/" : path[1..];

            if (path.StartsWith("//"))
                path = path[1..];

            int exitCode = ExecuteDeviceAdbShellCommand(ID, "cd", out string stdout, out string stderr, new(), EscapeAdbShellString(path), "&&", "pwd");
            if (exitCode != 0)
            {
                throw new Exception(stderr);
            }
            return stdout.TrimEnd(LINE_SEPARATORS);
        }

        public List<LogicalDrive> GetDrives()
        {
            List<LogicalDrive> drives = new();

            var root = ReadDrives(AdbRegEx.RE_EMULATED_STORAGE_SINGLE, "/");
            if (root is null)
                return null;
            else if (root.Any())
                drives.Add(root.First());

            var intStorage = ReadDrives(AdbRegEx.RE_EMULATED_STORAGE_SINGLE, "/sdcard");
            if (intStorage is null)
                return drives;
            else if (intStorage.Any())
                drives.Add(intStorage.First());

            var extStorage = ReadDrives(AdbRegEx.RE_EMULATED_ONLY, EMULATED_DRIVES_GREP);
            if (extStorage is null)
                return drives;
            else
            {
                Func<LogicalDrive, bool> predicate = drives.Any(drive => drive.Type is AbstractDrive.DriveType.Internal)
                    ? d => d.Type is not AbstractDrive.DriveType.Internal or AbstractDrive.DriveType.Root
                    : d => d.Type is not AbstractDrive.DriveType.Root;
                drives.AddRange(extStorage.Where(predicate));
            }

            if (!drives.Any(d => d.Type == AbstractDrive.DriveType.Internal))
            {
                drives.Insert(0, new(path: AdbExplorerConst.DEFAULT_PATH));
            }

            if (!drives.Any(d => d.Type == AbstractDrive.DriveType.Root))
            {
                drives.Insert(0, new(path: "/"));
            }

            return drives;
        }

        private IEnumerable<LogicalDrive> ReadDrives(Regex re, params string[] args)
        {
            int exitCode = ExecuteDeviceAdbShellCommand(ID, "df", out string stdout, out string stderr, new(), args);
            if (exitCode != 0)
                return null;

            return re.Matches(stdout).Select(m => new LogicalDrive(m.Groups, isEmulator: Type is DeviceType.Emulator, forcePath: args[0] == "/" ? "/" : ""));
        }

        private Dictionary<string, string> props;
        public Dictionary<string, string> Props
        {
            get
            {
                if (props is null)
                {
                    int exitCode = ExecuteDeviceAdbShellCommand(ID, GET_PROP, out string stdout, out string stderr, new());
                    if (exitCode == 0)
                    {
                        props = stdout.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Where(
                            l => l[0] == '[' && l[^1] == ']').TryToDictionary(
                                line => line.Split(':')[0].Trim('[', ']', ' '),
                                line => line.Split(':')[1].Trim('[', ']', ' '));
                    }
                    else
                        props = new Dictionary<string, string>();

                }

                return props;
            }
        }

        public string MmcProp => Props.ContainsKey(MMC_PROP) ? Props[MMC_PROP] : null;
        public string OtgProp => Props.ContainsKey(OTG_PROP) ? Props[OTG_PROP] : null;

        public Task<string> GetAndroidVersion() => Task.Run(() =>
        {
            if (Props.TryGetValue(ANDROID_VERSION, out string value))
                return value;
            else
                return "";
        });

        public static Dictionary<string, string> GetBatteryInfo(LogicalDevice device)
        {
            if (ExecuteDeviceAdbShellCommand(device.ID, BATTERY, out string stdout, out string stderr, new()) == 0)
            {
                return stdout.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Where(l => l.Contains(':')).ToDictionary(
                    line => line.Split(':')[0].Trim(),
                    line => line.Split(':')[1].Trim());
            }
            return null;
        }

        public static void Reboot(string deviceId, string arg)
        {
            if (ExecuteDeviceAdbCommand(deviceId, "reboot", out string stdout, out string stderr, new(), arg) != 0)
                throw new Exception(string.IsNullOrEmpty(stderr) ? stdout : stderr);
        }

        public static bool GetDeviceIp(DeviceViewModel device)
        {
            if (ExecuteDeviceAdbShellCommand(device.ID, "ip", out string stdout, out _, new(), new[] { "-f", "inet", "addr", "show", "wlan0" }) != 0)
                return false;

            var match = AdbRegEx.RE_DEVICE_WLAN_INET.Match(stdout);
            if (!match.Success)
                return false;

            device.SetIpAddress(match.Groups["IP"].Value);

            return true;
        }
    }
}
