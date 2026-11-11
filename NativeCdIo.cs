using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace CDKurtarici;

/// <summary>
/// Windows Kernel32 &amp; CD-ROM Donanım Seviyesi (Win32 API / IOCTL) Doğrudan Erişim Katmanı.
/// Güvenlik Sertleştirmesi:
/// - Tüm P/Invoke çağrıları DllImportSearchPath.System32 ile sınırlandırılarak DLL Hijacking (CWE-427) engellenmiştir.
/// - Win32 cihaz isim alanı (\\.\) erişimleri yalnızca doğrulanmış sürücü harfleri ([A-Z]:) ile sınırlandırılmıştır.
/// </summary>
public static unsafe class NativeCdIo
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;

    public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
    public const uint FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    // CD-ROM standart veri sektörü boyutu (2048 byte = 2 KB)
    public const int CD_SECTOR_SIZE = 2048;
    // 4K hizalı sektör okuması (hem 2048B CD-ROM hem 4096B disklerle %100 uyumlu)
    public const int ALIGNMENT_SIZE = 4096;
    // Tam performans blok boyutu (64 KB = 32 CD sektörü)
    public const int FAST_BLOCK_SIZE = 65536;

    // Win32 Drive Types
    public const uint DRIVE_UNKNOWN = 0;
    public const uint DRIVE_NO_ROOT_DIR = 1;
    public const uint DRIVE_REMOVABLE = 2;
    public const uint DRIVE_FIXED = 3;
    public const uint DRIVE_REMOTE = 4;
    public const uint DRIVE_CDROM = 5;
    public const uint DRIVE_RAMDISK = 6;

    // IOCTL Kodları
    public const uint IOCTL_CDROM_SET_SPEED = 0x00024060;
    public const uint IOCTL_STORAGE_EJECT_MEDIA = 0x002D4808;
    public const uint IOCTL_STORAGE_LOAD_MEDIA = 0x002D480C;

    // Güç Yönetimi (Uyku modunu engelleme)
    public const uint ES_CONTINUOUS = 0x80000000;
    public const uint ES_SYSTEM_REQUIRED = 0x00000001;

    // Thread Erişim Hakları (CancelSynchronousIo için en az ayrıcalık prensibi - Least Privilege)
    public const uint THREAD_TERMINATE = 0x0001;
    public const uint SYNCHRONIZE = 0x00100000;

    [StructLayout(LayoutKind.Sequential)]
    public struct CDROM_SET_SPEED
    {
        public int RequestType;     // 0 = CdromSetSpeed, 1 = CdromSetStreaming
        public ushort ReadSpeed;    // KB/sn cinsinden hız (0xFFFF = Maksimum Donanım Hızı)
        public ushort WriteSpeed;   // KB/sn
        public int RotationControl; // 0 = Default, 1 = CAV (Tam Performans Açısal Hız)
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    public static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern bool ReadFile(
        SafeFileHandle hFile,
        void* lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern bool SetFilePointerEx(
        SafeFileHandle hFile,
        long liDistanceToMove,
        out long lpNewFilePointer,
        uint dwMoveMethod);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref CDROM_SET_SPEED lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern bool CancelIoEx(
        SafeFileHandle hFile,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern bool CancelSynchronousIo(IntPtr hThread);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern IntPtr OpenThread(
        uint dwDesiredAccess,
        bool bInheritHandle,
        uint dwThreadId);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern uint GetLogicalDrives();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern uint GetDriveTypeW(string lpRootPathName);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    public static extern uint SetThreadExecutionState(uint esFlags);

    /// <summary>
    /// Win32 cihaz isim alanı (\\.\X:) için sürücü harfini sıkı şekilde doğrular (Device Namespace Injection önlemi).
    /// </summary>
    public static bool TryGetSafeDeviceVolumePath(string inputPath, out string devicePath)
    {
        devicePath = "";
        if (string.IsNullOrWhiteSpace(inputPath) || inputPath.Length < 2)
            return false;

        char letter = char.ToUpperInvariant(inputPath[0]);
        if (letter < 'A' || letter > 'Z' || inputPath[1] != ':')
            return false;

        devicePath = $@"\\.\{letter}:";
        return true;
    }

    /// <summary>
    /// Sürücüye dokunmadan (lazer okuması tetiklemeden ve arayüzü dondurmadan)
    /// sistemdeki optik (CD/DVD) ve diğer sürücü harflerini anında listeler.
    /// </summary>
    public static List<DriveItem> GetDrivesNonBlocking(bool includeAllDrives = false)
    {
        var list = new List<DriveItem>();
        uint mask = GetLogicalDrives();
        for (int i = 0; i < 26; i++)
        {
            if ((mask & (1u << i)) != 0)
            {
                char letter = (char)('A' + i);
                string root = $"{letter}:\\";
                uint type = GetDriveTypeW(root);
                if (type == DRIVE_CDROM || includeAllDrives)
                {
                    string typeLabel = type switch
                    {
                        DRIVE_CDROM => "CD/DVD-ROM Optik Sürücü",
                        DRIVE_REMOVABLE => "Çıkarılabilir Sürücü (USB/SD)",
                        DRIVE_FIXED => "Sabit Disk",
                        DRIVE_REMOTE => "Ağ Sürücüsü",
                        _ => "Diğer Sürücü"
                    };
                    list.Add(new DriveItem
                    {
                        DriveLetter = $"{letter}:",
                        RootPath = root,
                        IsCdRom = (type == DRIVE_CDROM),
                        DisplayText = $"{root}  [{typeLabel}]"
                    });
                }
            }
        }
        return list;
    }

    /// <summary>
    /// CD/DVD sürücüsünü donanım seviyesinde (IOCTL_CDROM_SET_SPEED) istenen hıza ve
    /// CAV (Constant Angular Velocity - Tam Performans) moduna ayarlar.
    /// </summary>
    public static bool SetCdRomHardwareSpeed(string driveLetter, ushort speedKbPerSec, bool useCav, out string statusMessage)
    {
        if (!TryGetSafeDeviceVolumePath(driveLetter, out string devicePath))
        {
            statusMessage = "Geçersiz sürücü harfi formatı.";
            return false;
        }

        using SafeFileHandle hDevice = CreateFileW(
            devicePath,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            IntPtr.Zero);

        if (hDevice.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            statusMessage = $"Sürücü hız komutu açılamadı (Kod: {err})";
            return false;
        }

        var speedCmd = new CDROM_SET_SPEED
        {
            RequestType = 0,
            ReadSpeed = speedKbPerSec,
            WriteSpeed = 0xFFFF,
            RotationControl = useCav ? 1 : 0
        };

        bool ok = DeviceIoControl(
            hDevice,
            IOCTL_CDROM_SET_SPEED,
            ref speedCmd,
            (uint)sizeof(CDROM_SET_SPEED),
            IntPtr.Zero,
            0,
            out _,
            IntPtr.Zero);

        if (!ok && useCav)
        {
            speedCmd.RotationControl = 0;
            ok = DeviceIoControl(
                hDevice,
                IOCTL_CDROM_SET_SPEED,
                ref speedCmd,
                (uint)sizeof(CDROM_SET_SPEED),
                IntPtr.Zero,
                0,
                out _,
                IntPtr.Zero);
        }

        if (ok)
        {
            string speedLabel = speedKbPerSec == 0xFFFF
                ? "MAKSİMUM DONANIM PERFORMANSI (Tam Hız)"
                : $"{speedKbPerSec} KB/sn (~{Math.Max(1, speedKbPerSec / 176)}x CD Hızı)";
            statusMessage = $"CD-ROM hızı ayarlandı: {speedLabel}";
            return true;
        }
        else
        {
            int err = Marshal.GetLastWin32Error();
            statusMessage = $"Sürücü varsayılan donanım hız profilini kullanıyor (IOCTL Kod: {err})";
            return false;
        }
    }

    /// <summary>
    /// CD/DVD tepsisini açar veya kapatır.
    /// </summary>
    public static bool EjectOrLoadMedia(string driveLetter, bool eject, out string message)
    {
        if (!TryGetSafeDeviceVolumePath(driveLetter, out string devicePath))
        {
            message = "Geçersiz sürücü harfi formatı.";
            return false;
        }

        using SafeFileHandle hDevice = CreateFileW(
            devicePath,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            IntPtr.Zero);

        if (hDevice.IsInvalid)
        {
            message = "Sürücüye erişilemedi.";
            return false;
        }

        uint ioctl = eject ? IOCTL_STORAGE_EJECT_MEDIA : IOCTL_STORAGE_LOAD_MEDIA;
        bool ok = DeviceIoControl(
            hDevice,
            ioctl,
            IntPtr.Zero,
            0,
            IntPtr.Zero,
            0,
            out _,
            IntPtr.Zero);

        message = ok
            ? (eject ? "CD tepsisi açıldı." : "CD tepsisi kapatıldı.")
            : "Tepsi komutu sürücü tarafından reddedildi.";
        return ok;
    }

    public static string FormatWin32Error(int errorCode)
    {
        return errorCode switch
        {
            23 => "Veri hatası - Döngüsel Artıklık Denetimi (CRC / Çizik Sektör)",
            21 => "Cihaz hazır değil (CD takılı değil veya odaklanamadı)",
            27 => "İstenen sektör bulunamadı (Derin çizik / iz kaybı)",
            31 => "Sisteme bağlı bir aygıt çalışmıyor (Sürücü okuma hatası)",
            1117 => "G/Ç (I/O) aygıt hatası (Bozuk sektör)",
            995 => "Zaman aşımı veya kullanıcı atlaması nedeniyle G/Ç kesildi",
            _ => new Win32Exception(errorCode).Message + $" (Kod: {errorCode})"
        };
    }
}

public class DriveItem
{
    public string DriveLetter { get; set; } = "";
    public string RootPath { get; set; } = "";
    public bool IsCdRom { get; set; }
    public string DisplayText { get; set; } = "";

    public override string ToString() => DisplayText;
}
