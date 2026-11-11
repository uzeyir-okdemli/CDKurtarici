using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace CDKurtarici;

public enum RecoveryStrategy
{
    /// <summary>
    /// 1. Aşamada okunamayan dosyayı anında atlayıp tüm sağlam dosyaları tam hızda kurtarır
    /// (okunan sağlam kısmı korur!), 2. Aşamada kaldığı yerden 2048-byte sektör hassasiyetiyle devam eder.
    /// </summary>
    TwoPassSmart = 0,

    /// <summary>
    /// Sağlam yerleri 64 KB tam hızda, çizik bölgeleri 2048-byte (1 CD sektörü) hassasiyetle okur;
    /// okunamayan sektörleri sıfırla doldurup dosyayı kurtarır, limit aşılırsa sonraki dosyaya geçer.
    /// </summary>
    SectorBySectorSalvage = 1,

    /// <summary>
    /// Bir dosyada okuma hatası alındığında vakit kaybetmeden o dosyayı atlar ve hemen sıradaki dosyaya geçer.
    /// </summary>
    SkipFileImmediately = 2
}

public enum HardwareSpeedProfile
{
    MaxPerformance = 0,        // 0xFFFF KB/sn + CAV (Tam Performans)
    BalancedAdaptive = 1,      // Sağlam yerlerde Tam Hız, çiziklerde 8x (1411 KB/sn)
    PrecisionLowVibration = 2  // 4x (706 KB/sn) - Ağır çizik ve balanssız CD'ler için
}

public enum FileRecoveryStatus
{
    Pending,
    Reading,
    DeferredToPass2,
    RecoveredPerfect,
    RecoveredPartial,
    SkippedError
}

public class RecoverableFileItem
{
    public int Index { get; set; }
    public bool Selected { get; set; } = true;
    public string FullSourcePath { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public long SizeBytes { get; set; }
    public FileRecoveryStatus Status { get; set; } = FileRecoveryStatus.Pending;
    public long BytesRecovered { get; set; }
    public long ResumeOffset { get; set; }
    public int BadSectorsCount { get; set; }
    public string StatusText { get; set; } = "Bekliyor";
    public string DetailMessage { get; set; } = "";

    public double RecoveryPercent => SizeBytes <= 0
        ? (Status == FileRecoveryStatus.RecoveredPerfect ? 100.0 : 0.0)
        : Math.Min(100.0, (BytesRecovered * 100.0) / SizeBytes);
}

public class RecoveryOptions
{
    public string SourceRoot { get; set; } = "";
    public string DestinationRoot { get; set; } = "";
    public RecoveryStrategy Strategy { get; set; } = RecoveryStrategy.TwoPassSmart;
    public HardwareSpeedProfile SpeedProfile { get; set; } = HardwareSpeedProfile.MaxPerformance;
    public int MaxRetriesPerSector { get; set; } = 2;
    public int MaxConsecutiveBadSectors { get; set; } = 32; // 32 * 2KB = 64 KB üst üste bozuksa dosyayı atla
    public int ReadTimeoutMs { get; set; } = 3500;          // 3.5 sn'de cevap vermeyen sektörü CancelIoEx ile kes
    public bool KeepPartialFiles { get; set; } = true;
    public bool AutoEnhanceVcdVideo { get; set; } = true;   // AVSEQ*.DAT dosyalarını otomatik 720p 50FPS MP4'e onar/netleştir
}

public class RecoveryProgressSnapshot
{
    public int CurrentFileIndex { get; set; } = -1;
    public string CurrentFileName { get; set; } = "";
    public string CurrentPhaseText { get; set; } = "";
    public long CurrentFileOffset { get; set; }
    public long CurrentFileSize { get; set; }
    public long TotalBytesProcessed { get; set; }
    public long TotalBytesTarget { get; set; }
    public double CurrentSpeedBytesPerSec { get; set; }
    public int TotalFiles { get; set; }
    public int PerfectFiles { get; set; }
    public int PartialFiles { get; set; }
    public int SkippedFiles { get; set; }
    public int TotalBadSectors { get; set; }
    public byte[] SectorMap { get; set; } = Array.Empty<byte>(); // 0=Bekliyor, 1=Tam Hız Sağlam, 2=2KB Kurtarıldı, 3=Bozuk/Atlandı
}

public class CdRecoveryEngine
{
    public const int SECTOR_MAP_CELLS = 240;

    private volatile bool _skipCurrentFileRequested;
    private volatile bool _isPaused;
    private volatile bool _isReadInProgress;
    private long _currentReadStartTicks;
    private SafeFileHandle? _activeReadHandle;
    private IntPtr _workerThreadHandle = IntPtr.Zero;
    private readonly object _handleLock = new();

    public event Action<string>? OnLog;
    public event Action<RecoveryProgressSnapshot>? OnProgress;
    public event Action<RecoverableFileItem>? OnFileUpdated;

    public void RequestSkipCurrentFile()
    {
        _skipCurrentFileRequested = true;
        AbortActiveKernelIo();
    }

    public void SetPaused(bool paused)
    {
        _isPaused = paused;
    }

    public bool IsPaused => _isPaused;

    private void AbortActiveKernelIo()
    {
        lock (_handleLock)
        {
            try
            {
                if (_activeReadHandle != null && !_activeReadHandle.IsInvalid && !_activeReadHandle.IsClosed)
                {
                    NativeCdIo.CancelIoEx(_activeReadHandle, IntPtr.Zero);
                }
            }
            catch { }

            try
            {
                if (_workerThreadHandle != IntPtr.Zero)
                {
                    NativeCdIo.CancelSynchronousIo(_workerThreadHandle);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Çizik CD'lerde bozuk alt klasörlerde kilitlenmeden tüm erişilebilir dosya ağacını tarar.
    /// </summary>
    public Task<List<RecoverableFileItem>> ScanDirectoryTreeAsync(
        string rootPath,
        CancellationToken ct,
        Action<string>? statusCallback = null)
    {
        return Task.Run(() =>
        {
            var result = new List<RecoverableFileItem>();
            string canonicalRoot = Path.GetFullPath(rootPath);
            var dirs = new Queue<string>();
            dirs.Enqueue(canonicalRoot);

            int index = 0;
            while (dirs.Count > 0 && !ct.IsCancellationRequested)
            {
                string currentDir = dirs.Dequeue();
                statusCallback?.Invoke($"Taranıyor: {currentDir}");

                try
                {
                    var currentDirInfo = new DirectoryInfo(currentDir);
                    foreach (DirectoryInfo subDir in currentDirInfo.EnumerateDirectories())
                    {
                        if (ct.IsCancellationRequested) break;
                        // Güvenlik: Sembolik bağlantıları / Reparse Point'leri (CWE-59 / sonsuz döngü) atla
                        if ((subDir.Attributes & FileAttributes.ReparsePoint) != 0)
                            continue;
                        dirs.Enqueue(subDir.FullName);
                    }
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[UYARI] Klasör tablosu okunamadı (Atlandı): {currentDir} -> {ex.Message}");
                }

                try
                {
                    var dirInfo = new DirectoryInfo(currentDir);
                    foreach (FileInfo fi in dirInfo.EnumerateFiles())
                    {
                        if (ct.IsCancellationRequested) break;
                        try
                        {
                            if ((fi.Attributes & FileAttributes.ReparsePoint) != 0)
                                continue;

                            string full = fi.FullName;
                            string rel = Path.GetRelativePath(canonicalRoot, full);
                            long len = 0;
                            try { len = fi.Length; } catch { len = 0; }

                            result.Add(new RecoverableFileItem
                            {
                                Index = index++,
                                Selected = true,
                                FullSourcePath = full,
                                RelativePath = rel,
                                SizeBytes = len,
                                Status = FileRecoveryStatus.Pending,
                                StatusText = "Bekliyor"
                            });
                        }
                        catch (Exception fex)
                        {
                            OnLog?.Invoke($"[UYARI] Dosya bilgisi okunamadı: {fex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    OnLog?.Invoke($"[UYARI] Klasör içeriği okunamadı (Atlandı): {currentDir} -> {ex.Message}");
                }
            }

            return result;
        }, ct);
    }

    /// <summary>
    /// Seçili dosyaları donanım seviyesinde tam performans + çizik sektör atlama algoritmasıyla kurtarır.
    /// </summary>
    public Task RunRecoveryAsync(
        List<RecoverableFileItem> files,
        RecoveryOptions options,
        CancellationToken ct)
    {
        return Task.Run(() =>
        {
            NativeCdIo.SetThreadExecutionState(NativeCdIo.ES_CONTINUOUS | NativeCdIo.ES_SYSTEM_REQUIRED);

            uint tid = NativeCdIo.GetCurrentThreadId();
            lock (_handleLock)
            {
                _workerThreadHandle = NativeCdIo.OpenThread(NativeCdIo.THREAD_TERMINATE | NativeCdIo.SYNCHRONIZE, false, tid);
            }

            using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var watchdogTask = Task.Run(async () =>
            {
                while (!watchdogCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(200, watchdogCts.Token);
                        if (_isReadInProgress)
                        {
                            long elapsedMs = (Stopwatch.GetTimestamp() - Interlocked.Read(ref _currentReadStartTicks)) * 1000 / Stopwatch.Frequency;
                            if (elapsedMs >= options.ReadTimeoutMs)
                            {
                                AbortActiveKernelIo();
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch { }
                }
            }, watchdogCts.Token);

            try
            {
                ExecuteRecoveryCore(files, options, ct);
            }
            finally
            {
                watchdogCts.Cancel();
                lock (_handleLock)
                {
                    if (_workerThreadHandle != IntPtr.Zero)
                    {
                        NativeCdIo.CloseHandle(_workerThreadHandle);
                        _workerThreadHandle = IntPtr.Zero;
                    }
                }
                NativeCdIo.SetThreadExecutionState(NativeCdIo.ES_CONTINUOUS);
            }
        }, ct);
    }

    private unsafe void ExecuteRecoveryCore(
        List<RecoverableFileItem> files,
        RecoveryOptions options,
        CancellationToken ct)
    {
        var selectedFiles = files.FindAll(f => f.Selected);
        if (selectedFiles.Count == 0)
        {
            OnLog?.Invoke("Kurtarılacak seçili dosya yok.");
            return;
        }

        foreach (var f in selectedFiles)
        {
            f.ResumeOffset = 0;
            f.BytesRecovered = 0;
            f.BadSectorsCount = 0;
        }

        // CD-ROM Donanım Hızını Ayarla
        ApplyHardwareSpeed(options.SourceRoot, options.SpeedProfile, isRoughRegion: false);

        long totalTargetBytes = 0;
        foreach (var f in selectedFiles) totalTargetBytes += Math.Max(0, f.SizeBytes);

        int perfectCount = 0;
        int partialCount = 0;
        int skippedCount = 0;
        int totalBadSectors = 0;
        long totalProcessedBytes = 0;

        var speedSw = Stopwatch.StartNew();
        long bytesSinceLastSpeedCalc = 0;
        double currentSpeedBps = 0;

        // 4096-byte hizalı (sector-aligned) bellek tamponu ayır ve sıfırla (Bellek sızıntısı önlemi)
        void* alignedBuffer = NativeMemory.AlignedAlloc(NativeCdIo.FAST_BLOCK_SIZE, NativeCdIo.ALIGNMENT_SIZE);
        NativeMemory.Clear(alignedBuffer, NativeCdIo.FAST_BLOCK_SIZE);

        try
        {
            var deferredFiles = new List<RecoverableFileItem>();

            string pass1Title = options.Strategy == RecoveryStrategy.TwoPassSmart
                ? "1. AŞAMA: Tam Performans Sağlam Dosya Kurtarma"
                : "Dosya Kurtarma İşlemi";
            OnLog?.Invoke($"=== {pass1Title} Başladı ({selectedFiles.Count} dosya) ===");

            for (int i = 0; i < selectedFiles.Count && !ct.IsCancellationRequested; i++)
            {
                var item = selectedFiles[i];
                bool isPass1Quick = (options.Strategy == RecoveryStrategy.TwoPassSmart);

                ProcessSingleFile(
                    item,
                    options,
                    alignedBuffer,
                    isPass1QuickSweep: isPass1Quick,
                    phaseTitle: pass1Title,
                    ref totalProcessedBytes,
                    totalTargetBytes,
                    selectedFiles.Count,
                    ref perfectCount,
                    ref partialCount,
                    ref skippedCount,
                    ref totalBadSectors,
                    ref bytesSinceLastSpeedCalc,
                    ref currentSpeedBps,
                    speedSw,
                    deferredFiles,
                    ct);
            }

            // 2. AŞAMA (1. aşamada çizik nedeniyle bekletilen dosyaları kaldıkları byte'tan itibaren kurtar)
            if (options.Strategy == RecoveryStrategy.TwoPassSmart && deferredFiles.Count > 0 && !ct.IsCancellationRequested)
            {
                string pass2Title = "2. AŞAMA: Çizik Dosyalar İçin Sektör Bazlı (2 KB) Derin Kurtarma";
                OnLog?.Invoke($"=== {pass2Title} Başladı ({deferredFiles.Count} çizik dosya kaldığı yerden devam ediyor) ===");

                if (options.SpeedProfile == HardwareSpeedProfile.BalancedAdaptive)
                {
                    ApplyHardwareSpeed(options.SourceRoot, options.SpeedProfile, isRoughRegion: true);
                }

                for (int i = 0; i < deferredFiles.Count && !ct.IsCancellationRequested; i++)
                {
                    var item = deferredFiles[i];
                    ProcessSingleFile(
                        item,
                        options,
                        alignedBuffer,
                        isPass1QuickSweep: false,
                        phaseTitle: pass2Title,
                        ref totalProcessedBytes,
                        totalTargetBytes,
                        selectedFiles.Count,
                        ref perfectCount,
                        ref partialCount,
                        ref skippedCount,
                        ref totalBadSectors,
                        ref bytesSinceLastSpeedCalc,
                        ref currentSpeedBps,
                        speedSw,
                        null,
                        ct);
                }
            }

            OnLog?.Invoke($"=== Kurtarma Tamamlandı | Tam: {perfectCount}, Kısmi: {partialCount}, Atlanan: {skippedCount}, Bozuk Sektör: {totalBadSectors} ===");
        }
        finally
        {
            NativeMemory.Clear(alignedBuffer, NativeCdIo.FAST_BLOCK_SIZE);
            NativeMemory.AlignedFree(alignedBuffer);
        }
    }

    /// <summary>
    /// Hedef dosya yolunu Path Traversal (CWE-22 / Zip-Slip) ve ayrılmış Windows cihaz isimlerine karşı doğrular.
    /// </summary>
    private static bool TryResolveSafeDestinationPath(string destinationRoot, string relativePath, out string safeDestPath, out string errorReason)
    {
        safeDestPath = "";
        errorReason = "";

        if (string.IsNullOrWhiteSpace(destinationRoot) || string.IsNullOrWhiteSpace(relativePath))
        {
            errorReason = "Geçersiz hedef veya göreli dosya yolu.";
            return false;
        }

        try
        {
            string fullRoot = Path.GetFullPath(destinationRoot);
            if (!fullRoot.EndsWith(Path.DirectorySeparatorChar))
            {
                fullRoot += Path.DirectorySeparatorChar;
            }

            string candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
            if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                errorReason = "Güvenlik engeli: Hedef klasör dışına yazma girişimi (Path Traversal) engellendi.";
                return false;
            }

            string fileNameNoExt = Path.GetFileNameWithoutExtension(candidate).ToUpperInvariant();
            string[] reservedNames =
            {
                "CON", "PRN", "AUX", "NUL",
                "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
            };
            if (Array.IndexOf(reservedNames, fileNameNoExt) >= 0)
            {
                errorReason = "Güvenlik engeli: Ayrılmış Windows aygıt ismi kullanılamaz.";
                return false;
            }

            safeDestPath = candidate;
            return true;
        }
        catch (Exception ex)
        {
            errorReason = ex.Message;
            return false;
        }
    }

    private unsafe void ProcessSingleFile(
        RecoverableFileItem item,
        RecoveryOptions options,
        void* alignedBuffer,
        bool isPass1QuickSweep,
        string phaseTitle,
        ref long totalProcessedBytes,
        long totalTargetBytes,
        int totalFilesCount,
        ref int perfectCount,
        ref int partialCount,
        ref int skippedCount,
        ref int totalBadSectors,
        ref long bytesSinceLastSpeedCalc,
        ref double currentSpeedBps,
        Stopwatch speedSw,
        List<RecoverableFileItem>? deferredList,
        CancellationToken ct)
    {
        while (_isPaused && !ct.IsCancellationRequested)
        {
            Thread.Sleep(100);
        }
        if (ct.IsCancellationRequested) return;

        _skipCurrentFileRequested = false;
        item.Status = FileRecoveryStatus.Reading;
        item.StatusText = isPass1QuickSweep ? "Okunuyor (Tam Hız)..." : "Okunuyor (Sektör Modu)...";
        item.DetailMessage = "";
        OnFileUpdated?.Invoke(item);

        byte[] sectorMap = new byte[SECTOR_MAP_CELLS];
        long fileSize = item.SizeBytes;
        long offset = item.ResumeOffset;

        if (offset > 0 && fileSize > 0)
        {
            MarkSectorMapRange(sectorMap, 0, (int)Math.Min(offset, int.MaxValue), fileSize, 1);
        }

        if (!TryResolveSafeDestinationPath(options.DestinationRoot, item.RelativePath, out string destPath, out string pathError))
        {
            item.Status = FileRecoveryStatus.SkippedError;
            item.StatusText = "Güvenlik Engeli";
            item.DetailMessage = pathError;
            skippedCount++;
            OnFileUpdated?.Invoke(item);
            return;
        }

        try
        {
            string? destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir))
            {
                Directory.CreateDirectory(destDir);
            }
        }
        catch (Exception ex)
        {
            item.Status = FileRecoveryStatus.SkippedError;
            item.StatusText = "Hedef Klasör Hatası";
            item.DetailMessage = ex.Message;
            skippedCount++;
            OnFileUpdated?.Invoke(item);
            return;
        }

        // Kaynak dosyayı öncelikle FILE_FLAG_NO_BUFFERING | FILE_FLAG_SEQUENTIAL_SCAN ile aç
        bool unbuffered = true;
        SafeFileHandle hSource = OpenSourceFile(item.FullSourcePath, useNoBuffering: true);
        if (hSource.IsInvalid)
        {
            unbuffered = false;
            hSource = OpenSourceFile(item.FullSourcePath, useNoBuffering: false);
        }

        if (hSource.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            string errMsg = NativeCdIo.FormatWin32Error(err);

            if (isPass1QuickSweep && deferredList != null)
            {
                item.Status = FileRecoveryStatus.DeferredToPass2;
                item.StatusText = "2. Aşamaya Bırakıldı";
                item.DetailMessage = $"1. Aşamada açılamadı ({errMsg}), 2. aşamada tekrar denenecek.";
                deferredList.Add(item);
                OnLog?.Invoke($"[2. AŞAMAYA ERTELENDİ] {item.RelativePath} -> {errMsg}");
            }
            else
            {
                item.Status = FileRecoveryStatus.SkippedError;
                item.StatusText = "Atlandı (Açılamadı)";
                item.DetailMessage = errMsg;
                skippedCount++;
                OnLog?.Invoke($"[ATLANDI - AÇILAMADI] {item.RelativePath} -> {errMsg}");
            }

            OnFileUpdated?.Invoke(item);
            return;
        }

        bool fileDeferred = false;
        bool fileAborted = false;
        int consecutiveBadSectors = 0;

        try
        {
            FileMode fileMode = (offset > 0 && File.Exists(destPath)) ? FileMode.OpenOrCreate : FileMode.Create;
            using var destStream = new FileStream(destPath, fileMode, FileAccess.Write, FileShare.None, NativeCdIo.FAST_BLOCK_SIZE);
            if (offset > 0)
            {
                destStream.Seek(offset, SeekOrigin.Begin);
            }

            if (fileSize == 0)
            {
                item.Status = FileRecoveryStatus.RecoveredPerfect;
                item.StatusText = "Kurtarıldı (%100)";
                perfectCount++;
                OnFileUpdated?.Invoke(item);
                return;
            }

            while (offset < fileSize && !ct.IsCancellationRequested)
            {
                while (_isPaused && !ct.IsCancellationRequested)
                {
                    Thread.Sleep(100);
                }
                if (ct.IsCancellationRequested) break;

                if (_skipCurrentFileRequested)
                {
                    fileAborted = true;
                    item.DetailMessage = "Kullanıcı tarafından atlandı -> Sonraki dosyaya geçildi.";
                    OnLog?.Invoke($"[MANUEL ATLANDI] {item.RelativePath} (Sonraki dosyaya geçiliyor)");
                    break;
                }

                long remaining = fileSize - offset;
                int logicalBytesToRead = (int)Math.Min(NativeCdIo.FAST_BLOCK_SIZE, remaining);
                uint alignedBytesToRead = unbuffered
                    ? (uint)AlignUp(logicalBytesToRead, NativeCdIo.ALIGNMENT_SIZE)
                    : (uint)logicalBytesToRead;

                bool fastReadOk = TryReadAlignedBlock(
                    ref hSource,
                    item.FullSourcePath,
                    ref unbuffered,
                    offset,
                    alignedBuffer,
                    alignedBytesToRead,
                    logicalBytesToRead,
                    out uint actualRead,
                    out int lastWin32Err);

                if (fastReadOk && actualRead > 0)
                {
                    int writeLen = (int)Math.Min(logicalBytesToRead, (int)actualRead);
                    destStream.Write(new ReadOnlySpan<byte>(alignedBuffer, writeLen));

                    MarkSectorMapRange(sectorMap, offset, writeLen, fileSize, 1);

                    offset += writeLen;
                    item.BytesRecovered += writeLen;
                    item.ResumeOffset = offset;
                    totalProcessedBytes += writeLen;
                    bytesSinceLastSpeedCalc += writeLen;
                    consecutiveBadSectors = 0;
                }
                else
                {
                    if (_skipCurrentFileRequested)
                    {
                        fileAborted = true;
                        item.DetailMessage = "Kullanıcı tarafından atlandı -> Sonraki dosyaya geçildi.";
                        OnLog?.Invoke($"[MANUEL ATLANDI] {item.RelativePath}");
                        break;
                    }

                    // 1) Eğer 1. Aşama Hızlı Tarama ise -> Okunan sağlam kısmı koru, kalanını 2. aşamaya bırak ve hemen sonraki dosyaya geç!
                    if (isPass1QuickSweep && deferredList != null)
                    {
                        fileDeferred = true;
                        item.ResumeOffset = offset;
                        item.Status = FileRecoveryStatus.DeferredToPass2;
                        item.StatusText = $"2. Aşamada Devam Edecek (%{item.RecoveryPercent:F0})";
                        item.DetailMessage = $"Çizik algılandı ({FormatBytes(offset)} konumunda), sağlam dosyalar bittikten sonra kaldığı yerden kurtarılacak.";
                        deferredList.Add(item);
                        OnLog?.Invoke($"[HIZLI GEÇİŞ -> 2. AŞAMA] {item.RelativePath} (%{item.RecoveryPercent:F0} okundu, {FormatBytes(offset)} konumunda çizik var -> sıradaki dosyaya geçiliyor)");
                        break;
                    }

                    // 2) Eğer "Okunamayan Dosyayı Hemen Atla" stratejisi seçiliyse:
                    if (options.Strategy == RecoveryStrategy.SkipFileImmediately)
                    {
                        bool retryRecovered = false;
                        for (int r = 0; r < options.MaxRetriesPerSector && !_skipCurrentFileRequested && !ct.IsCancellationRequested; r++)
                        {
                            if (TryReadAlignedBlock(ref hSource, item.FullSourcePath, ref unbuffered, offset, alignedBuffer, alignedBytesToRead, logicalBytesToRead, out actualRead, out lastWin32Err) && actualRead > 0)
                            {
                                int writeLen = (int)Math.Min(logicalBytesToRead, (int)actualRead);
                                destStream.Write(new ReadOnlySpan<byte>(alignedBuffer, writeLen));
                                MarkSectorMapRange(sectorMap, offset, writeLen, fileSize, 2);
                                offset += writeLen;
                                item.BytesRecovered += writeLen;
                                item.ResumeOffset = offset;
                                totalProcessedBytes += writeLen;
                                bytesSinceLastSpeedCalc += writeLen;
                                retryRecovered = true;
                                break;
                            }
                        }

                        if (!retryRecovered)
                        {
                            fileAborted = true;
                            item.BadSectorsCount++;
                            totalBadSectors++;
                            string errDesc = NativeCdIo.FormatWin32Error(lastWin32Err);
                            item.DetailMessage = $"Okunamadı ({FormatBytes(offset)}): {errDesc} -> Sonraki dosyaya geçildi.";
                            OnLog?.Invoke($"[ATLANDI -> SONRAKİ DOSYA] {item.RelativePath} ({FormatBytes(offset)} konumunda: {errDesc})");
                            break;
                        }
                        continue;
                    }

                    // 3) SEKTÖR BAZLI HASSAS KURTARMA (64 KB bloğu 2048-byte tekil CD sektörlerine bölerek kurtar!)
                    int subOffset = 0;
                    while (subOffset < logicalBytesToRead && !ct.IsCancellationRequested && !_skipCurrentFileRequested)
                    {
                        long sectorFileOffset = offset + subOffset;
                        int logicalSectorBytes = Math.Min(NativeCdIo.CD_SECTOR_SIZE, (int)(fileSize - sectorFileOffset));
                        uint alignedSectorRead = unbuffered ? (uint)NativeCdIo.CD_SECTOR_SIZE : (uint)logicalSectorBytes;

                        bool sectorOk = false;
                        int attempts = Math.Max(1, options.MaxRetriesPerSector + 1);

                        for (int attempt = 0; attempt < attempts && !_skipCurrentFileRequested && !ct.IsCancellationRequested; attempt++)
                        {
                            if (TryReadAlignedBlock(ref hSource, item.FullSourcePath, ref unbuffered, sectorFileOffset, alignedBuffer, alignedSectorRead, logicalSectorBytes, out uint secActual, out lastWin32Err) && secActual > 0)
                            {
                                int writeLen = Math.Min(logicalSectorBytes, (int)secActual);
                                destStream.Write(new ReadOnlySpan<byte>(alignedBuffer, writeLen));
                                byte mapCode = (attempt == 0) ? (byte)1 : (byte)2;
                                MarkSectorMapRange(sectorMap, sectorFileOffset, writeLen, fileSize, mapCode);

                                item.BytesRecovered += writeLen;
                                item.ResumeOffset = sectorFileOffset + writeLen;
                                totalProcessedBytes += writeLen;
                                bytesSinceLastSpeedCalc += writeLen;
                                consecutiveBadSectors = 0;
                                sectorOk = true;
                                break;
                            }
                        }

                        if (!sectorOk)
                        {
                            // Bu 2048-byte sektör çizik! Sıfırla (0x00) doldur ve bir sonraki sektöre geç:
                            NativeMemory.Clear(alignedBuffer, (nuint)logicalSectorBytes);
                            destStream.Write(new ReadOnlySpan<byte>(alignedBuffer, logicalSectorBytes));
                            MarkSectorMapRange(sectorMap, sectorFileOffset, logicalSectorBytes, fileSize, 3);

                            item.BadSectorsCount++;
                            totalBadSectors++;
                            consecutiveBadSectors++;
                            item.ResumeOffset = sectorFileOffset + logicalSectorBytes;
                            totalProcessedBytes += logicalSectorBytes;

                            bool isMainVcdVideo = item.RelativePath.Contains("AVSEQ", StringComparison.OrdinalIgnoreCase) ||
                                                  item.RelativePath.Contains("MUSIC", StringComparison.OrdinalIgnoreCase);
                            int effectiveMaxConsecutive = isMainVcdVideo
                                ? Math.Max(options.MaxConsecutiveBadSectors * 40, 1500)
                                : options.MaxConsecutiveBadSectors;

                            if (effectiveMaxConsecutive > 0 && consecutiveBadSectors >= effectiveMaxConsecutive)
                            {
                                fileAborted = true;
                                item.DetailMessage = $"Ardışık {consecutiveBadSectors} bozuk sektör limiti aşıldı ({FormatBytes(sectorFileOffset)}). Kalan kısım atlanıp sonraki dosyaya geçildi.";
                                OnLog?.Invoke($"[ARDISIK ÇİZİK LİMİTİ -> SONRAKİ DOSYA] {item.RelativePath} ({consecutiveBadSectors} ardışık bozuk sektör, sıradaki dosyaya geçiliyor)");
                                break;
                            }
                        }

                        subOffset += logicalSectorBytes;
                        UpdateSpeedAndReportProgress(
                            item, phaseTitle, sectorFileOffset + logicalSectorBytes, fileSize,
                            totalProcessedBytes, totalTargetBytes, totalFilesCount,
                            perfectCount, partialCount, skippedCount, totalBadSectors,
                            ref bytesSinceLastSpeedCalc, ref currentSpeedBps, speedSw, sectorMap);
                    }

                    if (fileAborted) break;
                    offset += logicalBytesToRead;
                }

                UpdateSpeedAndReportProgress(
                    item, phaseTitle, offset, fileSize,
                    totalProcessedBytes, totalTargetBytes, totalFilesCount,
                    perfectCount, partialCount, skippedCount, totalBadSectors,
                    ref bytesSinceLastSpeedCalc, ref currentSpeedBps, speedSw, sectorMap);
            }

            destStream.Flush();
        }
        catch (Exception ex)
        {
            fileAborted = true;
            item.DetailMessage = $"Hata: {ex.Message} -> Sonraki dosyaya geçildi.";
            OnLog?.Invoke($"[HATA -> SONRAKİ DOSYA] {item.RelativePath}: {ex.Message}");
        }
        finally
        {
            lock (_handleLock)
            {
                _activeReadHandle = null;
            }
            hSource.Dispose();
        }

        if (fileDeferred)
        {
            OnFileUpdated?.Invoke(item);
            return;
        }

        if (fileAborted)
        {
            if (options.KeepPartialFiles && item.BytesRecovered > 0)
            {
                item.Status = FileRecoveryStatus.RecoveredPartial;
                item.StatusText = $"Kısmi (%{item.RecoveryPercent:F1})";
                partialCount++;
            }
            else
            {
                TryDeleteFileSafe(destPath);
                item.Status = FileRecoveryStatus.SkippedError;
                item.StatusText = "Atlandı (Okunamadı)";
                skippedCount++;
            }
        }
        else if (item.BadSectorsCount > 0)
        {
            item.Status = FileRecoveryStatus.RecoveredPartial;
            item.StatusText = $"Kurtarıldı (%{item.RecoveryPercent:F1} | {item.BadSectorsCount} çizik sektör)";
            if (string.IsNullOrEmpty(item.DetailMessage))
            {
                item.DetailMessage = $"{item.BadSectorsCount} bozuk sektör (2 KB) atlanarak dosyanın geri kalanı kurtarıldı.";
            }
            partialCount++;
            OnLog?.Invoke($"[KISMİ KURTARILDI] {item.RelativePath} -> %{item.RecoveryPercent:F1} ({item.BadSectorsCount} bozuk sektör sıfırlandı)");
        }
        else
        {
            item.Status = FileRecoveryStatus.RecoveredPerfect;
            item.StatusText = "Kurtarıldı (%100)";
            item.DetailMessage = "Eksiksiz tam hızda okundu.";
            perfectCount++;
            OnLog?.Invoke($"[TAMAMLANDI] {item.RelativePath} ({FormatBytes(item.SizeBytes)})");
        }

        // Eğer bu bir VCD ana video dosyasıysa (AVSEQxx.DAT / MUSICxx.DAT) ve otomatik iyileştirme açıksa:
        if (options.AutoEnhanceVcdVideo &&
            !ct.IsCancellationRequested &&
            item.BytesRecovered >= 1_000_000 &&
            (item.RelativePath.Contains("AVSEQ", StringComparison.OrdinalIgnoreCase) ||
             item.RelativePath.Contains("MUSIC", StringComparison.OrdinalIgnoreCase)) &&
            File.Exists(destPath))
        {
            try
            {
                item.StatusText = "🎬 Video Netleştiriliyor...";
                OnFileUpdated?.Invoke(item);
                string outDir = Path.GetDirectoryName(destPath) ?? options.DestinationRoot;

                var vcdRes = VcdVideoRecovery.RecoverAndEnhanceVcdFileAsync(
                    destPath,
                    outDir,
                    VideoEnhancementPreset.CleanSharpSmooth720p50Fps,
                    msg => OnLog?.Invoke(msg),
                    (perm, txt) =>
                    {
                        item.DetailMessage = txt;
                        OnFileUpdated?.Invoke(item);
                    },
                    ct).GetAwaiter().GetResult();

                if (vcdRes.Success)
                {
                    item.StatusText = "🎬 Net 720p50 MP4 Hazır";
                    item.DetailMessage = vcdRes.SummaryMessage;
                }
            }
            catch (Exception vex)
            {
                OnLog?.Invoke($"[VCD İYİLEŞTİRME UYARISI] {vex.Message}");
            }
        }

        OnFileUpdated?.Invoke(item);
        UpdateSpeedAndReportProgress(
            item, phaseTitle, offset, fileSize,
            totalProcessedBytes, totalTargetBytes, totalFilesCount,
            perfectCount, partialCount, skippedCount, totalBadSectors,
            ref bytesSinceLastSpeedCalc, ref currentSpeedBps, speedSw, sectorMap);
    }

    private SafeFileHandle OpenSourceFile(string path, bool useNoBuffering)
    {
        uint flags = NativeCdIo.FILE_FLAG_SEQUENTIAL_SCAN;
        if (useNoBuffering)
        {
            flags |= NativeCdIo.FILE_FLAG_NO_BUFFERING;
        }

        return NativeCdIo.CreateFileW(
            path,
            NativeCdIo.GENERIC_READ,
            NativeCdIo.FILE_SHARE_READ | NativeCdIo.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeCdIo.OPEN_EXISTING,
            flags,
            IntPtr.Zero);
    }

    private unsafe bool TryReadAlignedBlock(
        ref SafeFileHandle hFile,
        string sourcePath,
        ref bool unbuffered,
        long offset,
        void* alignedBuffer,
        uint alignedBytesToRead,
        int logicalBytesToRead,
        out uint bytesRead,
        out int win32Error)
    {
        bytesRead = 0;
        win32Error = 0;

        lock (_handleLock)
        {
            _activeReadHandle = hFile;
        }

        if (!NativeCdIo.SetFilePointerEx(hFile, offset, out _, 0))
        {
            win32Error = Marshal.GetLastWin32Error();
            if (win32Error == 87 && unbuffered)
            {
                // 4K diskte 2KB hizalama reddedilirse anında sıralı (sequential) handle'a geç
                if (FallbackToSequentialHandle(ref hFile, sourcePath, ref unbuffered, offset))
                {
                    return TryReadAlignedBlock(ref hFile, sourcePath, ref unbuffered, offset, alignedBuffer, (uint)logicalBytesToRead, logicalBytesToRead, out bytesRead, out win32Error);
                }
            }
            return false;
        }

        Interlocked.Exchange(ref _currentReadStartTicks, Stopwatch.GetTimestamp());
        _isReadInProgress = true;
        try
        {
            uint reqBytes = unbuffered ? alignedBytesToRead : (uint)logicalBytesToRead;
            ArgumentOutOfRangeException.ThrowIfGreaterThan(reqBytes, (uint)NativeCdIo.FAST_BLOCK_SIZE);
            bool ok = NativeCdIo.ReadFile(hFile, alignedBuffer, reqBytes, out bytesRead, IntPtr.Zero);
            if (!ok)
            {
                win32Error = Marshal.GetLastWin32Error();
                if (win32Error == 87 && unbuffered)
                {
                    _isReadInProgress = false;
                    if (FallbackToSequentialHandle(ref hFile, sourcePath, ref unbuffered, offset))
                    {
                        return TryReadAlignedBlock(ref hFile, sourcePath, ref unbuffered, offset, alignedBuffer, (uint)logicalBytesToRead, logicalBytesToRead, out bytesRead, out win32Error);
                    }
                }
                return false;
            }
            return true;
        }
        finally
        {
            _isReadInProgress = false;
        }
    }

    private bool FallbackToSequentialHandle(ref SafeFileHandle hFile, string sourcePath, ref bool unbuffered, long offset)
    {
        SafeFileHandle newHandle = OpenSourceFile(sourcePath, useNoBuffering: false);
        if (!newHandle.IsInvalid)
        {
            lock (_handleLock)
            {
                hFile.Dispose();
                hFile = newHandle;
                _activeReadHandle = hFile;
            }
            unbuffered = false;
            NativeCdIo.SetFilePointerEx(hFile, offset, out _, 0);
            return true;
        }
        return false;
    }

    private void ApplyHardwareSpeed(string rootPath, HardwareSpeedProfile profile, bool isRoughRegion)
    {
        ushort speed = profile switch
        {
            HardwareSpeedProfile.MaxPerformance => 0xFFFF,
            HardwareSpeedProfile.BalancedAdaptive => isRoughRegion ? (ushort)1411 : (ushort)0xFFFF,
            HardwareSpeedProfile.PrecisionLowVibration => 706,
            _ => 0xFFFF
        };
        bool useCav = (profile == HardwareSpeedProfile.MaxPerformance) || (profile == HardwareSpeedProfile.BalancedAdaptive && !isRoughRegion);
        NativeCdIo.SetCdRomHardwareSpeed(rootPath, speed, useCav, out string msg);
        OnLog?.Invoke($"[DONANIM] {msg}");
    }

    private static void MarkSectorMapRange(byte[] map, long offset, int length, long totalSize, byte state)
    {
        if (totalSize <= 0 || length <= 0) return;
        int startCell = (int)((offset * SECTOR_MAP_CELLS) / totalSize);
        int endCell = (int)(((offset + length) * SECTOR_MAP_CELLS) / totalSize);
        startCell = Math.Clamp(startCell, 0, SECTOR_MAP_CELLS - 1);
        endCell = Math.Clamp(endCell, startCell, SECTOR_MAP_CELLS - 1);

        for (int c = startCell; c <= endCell; c++)
        {
            if (state >= map[c])
            {
                map[c] = state;
            }
        }
    }

    private void UpdateSpeedAndReportProgress(
        RecoverableFileItem item,
        string phaseTitle,
        long fileOffset,
        long fileSize,
        long totalProcessedBytes,
        long totalTargetBytes,
        int totalFilesCount,
        int perfectCount,
        int partialCount,
        int skippedCount,
        int totalBadSectors,
        ref long bytesSinceLastSpeedCalc,
        ref double currentSpeedBps,
        Stopwatch speedSw,
        byte[] sectorMap)
    {
        double elapsedSec = speedSw.Elapsed.TotalSeconds;
        if (elapsedSec >= 0.35)
        {
            currentSpeedBps = bytesSinceLastSpeedCalc / elapsedSec;
            bytesSinceLastSpeedCalc = 0;
            speedSw.Restart();
        }

        var mapCopy = new byte[SECTOR_MAP_CELLS];
        Buffer.BlockCopy(sectorMap, 0, mapCopy, 0, SECTOR_MAP_CELLS);

        OnProgress?.Invoke(new RecoveryProgressSnapshot
        {
            CurrentFileIndex = item.Index,
            CurrentFileName = item.RelativePath,
            CurrentPhaseText = phaseTitle,
            CurrentFileOffset = Math.Min(fileOffset, fileSize),
            CurrentFileSize = fileSize,
            TotalBytesProcessed = totalProcessedBytes,
            TotalBytesTarget = totalTargetBytes,
            CurrentSpeedBytesPerSec = currentSpeedBps,
            TotalFiles = totalFilesCount,
            PerfectFiles = perfectCount,
            PartialFiles = partialCount,
            SkippedFiles = skippedCount,
            TotalBadSectors = totalBadSectors,
            SectorMap = mapCopy
        });
    }

    private static int AlignUp(int value, int alignment)
    {
        return ((value + alignment - 1) / alignment) * alignment;
    }

    private static void TryDeleteFileSafe(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:F1} KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return $"{mb:F2} MB";
        double gb = mb / 1024.0;
        return $"{gb:F2} GB";
    }
}
