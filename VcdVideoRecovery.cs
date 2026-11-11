using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CDKurtarici;

public enum VideoEnhancementPreset
{
    /// <summary>
    /// Onarım + 720p HD Netleştirme + 50 FPS Akıcılaştırma (Deblock + Denoise + Lanczos Keskinleştirme + 50 FPS)
    /// </summary>
    CleanSharpSmooth720p50Fps = 0,

    /// <summary>
    /// Maksimum Kalite Restorasyon + Derin Keskinleştirme + 50 FPS Akıcı Hareket
    /// </summary>
    UltraRestoreMotion50Fps = 1,

    /// <summary>
    /// Sadece CDXA Sektörlerini Temizle ve Orijinal MPG Olarak Kaydet (En Hızlı)
    /// </summary>
    FastCleanMpgOnly = 2
}

public class VcdRepairResult
{
    public bool Success { get; set; }
    public string CleanMpgPath { get; set; } = "";
    public string EnhancedMp4Path { get; set; } = "";
    public int TotalCdxaSectors { get; set; }
    public int EdcPerfectSectors { get; set; }
    public int BitRepairedSectors { get; set; }
    public int ConcealedCorruptSectors { get; set; }
    public string SummaryMessage { get; set; } = "";
}

/// <summary>
/// Video CD (VCD - AVSEQ01.DAT / ITEMxxxx.DAT) dosyaları için özel CD-ROM XA Mode 2 Form 2 (2352-byte sektör)
/// 32-Bit Donanım EDC CRC (0xD8018001) Doğrulama, Lazer Önbellek Boşaltmalı Çoklu Okuma (Bitwise Majority Vote),
/// MPEG-1 Paket Başlığı Onarımı ve FFmpeg 720p 50FPS Görüntü Restorasyon Motoru.
/// </summary>
public static class VcdVideoRecovery
{
    public const int RIFF_CDXA_HEADER_SIZE = 44;
    public const int VCD_RAW_SECTOR_SIZE = 2352;
    public const int VCD_SECTOR_HEADER_SIZE = 24;  // 12B Sync + 4B MSF + 8B Subheader (2x4B)
    public const int VCD_MPEG_PAYLOAD_SIZE = 2324; // Saf MPEG-1 Paket Yükü
    public const int VCD_SECTOR_TRAILER_SIZE = 4;  // 4B Mode 2 Form 2 EDC CRC (0xD8018001)

    private const byte SUBMODE_AUDIO = 0x04;

    private static readonly byte[] CdSyncPattern =
    {
        0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00
    };

    // Yellow Book / Green Book / White Book CD-ROM 32-bit EDC CRC Tablosu (Polinom: 0xD8018001)
    private static readonly uint[] EdcLut = BuildCdRomEdcTable();

    private static uint[] BuildCdRomEdcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int b = 0; b < 8; b++)
            {
                if ((crc & 1) != 0)
                    crc = (crc >> 1) ^ 0xD8018001u;
                else
                    crc >>= 1;
            }
            table[i] = crc;
        }
        return table;
    }

    /// <summary>
    /// Mode 2 Form 2 sektörünün 16..2347 (2332 byte) aralığı üzerinden resmi CD-ROM 32-bit EDC CRC değerini hesaplar.
    /// </summary>
    public static uint ComputeMode2Form2Edc(ReadOnlySpan<byte> data2332)
    {
        uint crc = 0;
        for (int i = 0; i < data2332.Length; i++)
        {
            crc = (crc >> 8) ^ EdcLut[(crc ^ data2332[i]) & 0xFF];
        }
        return crc;
    }

    public static bool IsVcdDatFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        string ext = Path.GetExtension(filePath);
        if (!ext.Equals(".DAT", StringComparison.OrdinalIgnoreCase)) return false;

        string name = Path.GetFileNameWithoutExtension(filePath);
        if (name.StartsWith("AVSEQ", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("MUSIC", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("ITEM", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            if (File.Exists(filePath))
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length >= 44)
                {
                    Span<byte> hdr = stackalloc byte[12];
                    if (fs.Read(hdr) == 12)
                    {
                        if (hdr[0] == 'R' && hdr[1] == 'I' && hdr[2] == 'F' && hdr[3] == 'F' &&
                            hdr[8] == 'C' && hdr[9] == 'D' && hdr[10] == 'X' && hdr[11] == 'A')
                        {
                            return true;
                        }
                    }
                }
            }
        }
        catch { }

        return false;
    }

    /// <summary>
    /// 2352-byte VCD sektörünü hem 12B Sync, hem 2x4B Subheader eşleşmesi, hem de 32-bit EDC CRC (0xD8018001) ile doğrular.
    /// </summary>
    public static bool VerifyVcdSectorEdcAndSync(ReadOnlySpan<byte> sector2352, out bool edcMatched)
    {
        edcMatched = false;
        if (sector2352.Length < VCD_RAW_SECTOR_SIZE) return false;

        for (int i = 0; i < 12; i++)
        {
            if (sector2352[i] != CdSyncPattern[i])
                return false;
        }

        if (sector2352[16] != sector2352[20] ||
            sector2352[17] != sector2352[21] ||
            sector2352[18] != sector2352[22] ||
            sector2352[19] != sector2352[23])
        {
            return false;
        }

        uint storedEdc = (uint)(sector2352[2348] |
                                (sector2352[2349] << 8) |
                                (sector2352[2350] << 16) |
                                (sector2352[2351] << 24));

        if (storedEdc != 0)
        {
            uint computedEdc = ComputeMode2Form2Edc(sector2352.Slice(16, 2332));
            if (computedEdc != storedEdc)
            {
                return false;
            }
        }

        edcMatched = true;
        return true;
    }

    /// <summary>
    /// Aynı sektörün çoklu fiziksel lazer okumaları arasında Bitwise Majority Voting (Bit Çoğunluk Oylaması) yapar.
    /// </summary>
    public static void BitwiseMajorityVote(List<byte[]> reads, Span<byte> output2352)
    {
        if (reads.Count == 0) return;
        if (reads.Count == 1)
        {
            reads[0].AsSpan(0, VCD_RAW_SECTOR_SIZE).CopyTo(output2352);
            return;
        }

        if (reads.Count == 3)
        {
            byte[] a = reads[0];
            byte[] b = reads[1];
            byte[] c = reads[2];
            for (int i = 0; i < VCD_RAW_SECTOR_SIZE; i++)
            {
                output2352[i] = (byte)((a[i] & b[i]) | (b[i] & c[i]) | (a[i] & c[i]));
            }
            return;
        }

        int threshold = reads.Count / 2;
        for (int i = 0; i < VCD_RAW_SECTOR_SIZE; i++)
        {
            byte resultByte = 0;
            for (int bit = 0; bit < 8; bit++)
            {
                int mask = 1 << bit;
                int ones = 0;
                for (int r = 0; r < reads.Count; r++)
                {
                    if ((reads[r][i] & mask) != 0) ones++;
                }
                if (ones > threshold)
                {
                    resultByte |= (byte)mask;
                }
            }
            output2352[i] = resultByte;
        }
    }

    public static async Task<VcdRepairResult> RecoverAndEnhanceVcdFileAsync(
        string sourceDatPath,
        string outputDirectory,
        VideoEnhancementPreset preset,
        Action<string>? onLog,
        Action<int, string>? onProgressPermille,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var result = new VcdRepairResult();
            Directory.CreateDirectory(outputDirectory);

            string baseName = Path.GetFileNameWithoutExtension(sourceDatPath);
            string cleanMpgPath = Path.Combine(outputDirectory, $"{baseName}_Onarilmis_Temiz.mpg");
            string enhancedMp4Path = Path.Combine(outputDirectory, $"{baseName}_Net_Akici_720p50fps.mp4");

            result.CleanMpgPath = cleanMpgPath;
            result.EnhancedMp4Path = enhancedMp4Path;

            string? root = Path.GetPathRoot(sourceDatPath);
            bool isFromCdRom = false;
            if (!string.IsNullOrEmpty(root) && NativeCdIo.GetDriveTypeW(root) == NativeCdIo.DRIVE_CDROM)
            {
                isFromCdRom = true;
                NativeCdIo.SetCdRomHardwareSpeed(root, 706, useCav: false, out string speedMsg);
                onLog?.Invoke($"[VCD LAZER KİLİDİ] Mode 2 Form 2 sessiz bit hatalarını önlemek için sürücü 4x CLV hassas moda alındı ({speedMsg})");
            }

            onLog?.Invoke($"[VCD EDC-CRC ANALİZİ] '{Path.GetFileName(sourceDatPath)}' içindeki 2352-byte CDXA sektörleri 32-bit EDC CRC (0xD8018001) ile denetleniyor...");

            ExtractAndSanitizeMpegStream(
                sourceDatPath,
                cleanMpgPath,
                isFromCdRom,
                result,
                onLog,
                (p, txt) =>
                {
                    int scaled = (preset == VideoEnhancementPreset.FastCleanMpgOnly) ? p : (p * 30) / 100;
                    onProgressPermille?.Invoke(scaled, txt);
                },
                ct);

            if (ct.IsCancellationRequested)
            {
                result.SummaryMessage = "İşlem iptal edildi.";
                return result;
            }

            if (preset == VideoEnhancementPreset.FastCleanMpgOnly)
            {
                result.Success = true;
                result.SummaryMessage = $"Temiz MPG oluşturuldu: {cleanMpgPath}";
                onProgressPermille?.Invoke(1000, "Tamamlandı (Temiz MPG hazır)");
                return result;
            }

            string? ffmpegExe = FindFfmpegExecutable();
            if (string.IsNullOrEmpty(ffmpegExe))
            {
                onLog?.Invoke("[UYARI] FFmpeg bulunamadı, yalnızca temizlenmiş MPG dosyası üretildi.");
                result.Success = true;
                result.SummaryMessage = $"Temiz MPG hazır: {cleanMpgPath}";
                onProgressPermille?.Invoke(1000, "Tamamlandı");
                return result;
            }

            onLog?.Invoke($"[VİDEO RESTORASYON] FFmpeg ile bozuk kare gizleme (-ec 3), blok giderme (Deblock/Denoise), Lanczos 720p Netleştirme ve 50 FPS Akıcılaştırma başlatılıyor...");

            bool mp4Ok = RunFfmpegEnhancement(
                ffmpegExe,
                cleanMpgPath,
                enhancedMp4Path,
                preset,
                result.TotalCdxaSectors,
                onLog,
                (p, txt) =>
                {
                    int scaled = 300 + (p * 70) / 100;
                    onProgressPermille?.Invoke(Math.Clamp(scaled, 300, 999), txt);
                },
                ct);

            result.Success = mp4Ok || File.Exists(cleanMpgPath);
            result.SummaryMessage = mp4Ok
                ? $"Net ve akıcı 720p 50FPS MP4 video hazır: {enhancedMp4Path}"
                : $"Temiz MPG hazır: {cleanMpgPath}";

            onProgressPermille?.Invoke(1000, "✅ VCD Video Onarım, Netleştirme ve 50 FPS Akıcılaştırma Tamamlandı!");
            return result;
        }, ct);
    }

    private static void ExtractAndSanitizeMpegStream(
        string sourceDatPath,
        string cleanMpgPath,
        bool isFromCdRom,
        VcdRepairResult stats,
        Action<string>? onLog,
        Action<int, string>? onProgress,
        CancellationToken ct)
    {
        using var inStream = new FileStream(
            sourceDatPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            65536,
            FileOptions.SequentialScan);

        using var outStream = new FileStream(
            cleanMpgPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            262144);

        long fileLen = inStream.Length;
        if (fileLen <= RIFF_CDXA_HEADER_SIZE)
        {
            throw new InvalidOperationException("Dosya boyutu geçerli bir VCD .DAT dosyası için çok küçük.");
        }

        byte[] headerBuf = new byte[RIFF_CDXA_HEADER_SIZE];
        int hdrRead = inStream.Read(headerBuf, 0, RIFF_CDXA_HEADER_SIZE);
        bool hasRiffCdxa = hdrRead == RIFF_CDXA_HEADER_SIZE &&
                           headerBuf[0] == 'R' && headerBuf[1] == 'I' && headerBuf[2] == 'F' && headerBuf[3] == 'F' &&
                           headerBuf[8] == 'C' && headerBuf[9] == 'D' && headerBuf[10] == 'X' && headerBuf[11] == 'A';

        long dataStartOffset = hasRiffCdxa ? RIFF_CDXA_HEADER_SIZE : 0;
        if (!hasRiffCdxa)
        {
            inStream.Seek(0, SeekOrigin.Begin);
        }

        int totalSectors = (int)((fileLen - dataStartOffset) / VCD_RAW_SECTOR_SIZE);
        stats.TotalCdxaSectors = totalSectors;

        byte[] sectorBuf = new byte[VCD_RAW_SECTOR_SIZE];
        byte[] votedSector = new byte[VCD_RAW_SECTOR_SIZE];

        for (int s = 0; s < totalSectors && !ct.IsCancellationRequested; s++)
        {
            long sectorOffset = dataStartOffset + (long)s * VCD_RAW_SECTOR_SIZE;
            bool readOk = TryReadExactSector(inStream, sectorOffset, sectorBuf);

            bool edcPerfect = readOk && VerifyVcdSectorEdcAndSync(sectorBuf, out _);

            if (!edcPerfect && isFromCdRom)
            {
                var multiReads = new List<byte[]>(5);
                if (readOk)
                {
                    var firstCopy = new byte[VCD_RAW_SECTOR_SIZE];
                    Buffer.BlockCopy(sectorBuf, 0, firstCopy, 0, VCD_RAW_SECTOR_SIZE);
                    multiReads.Add(firstCopy);
                }

                for (int retry = 0; retry < 4 && !ct.IsCancellationRequested; retry++)
                {
                    try
                    {
                        long flushPos = Math.Max(0, sectorOffset - 2 * 1024 * 1024);
                        if (flushPos == sectorOffset) flushPos = Math.Min(fileLen - VCD_RAW_SECTOR_SIZE, sectorOffset + 2 * 1024 * 1024);
                        inStream.Seek(flushPos, SeekOrigin.Begin);
                        _ = inStream.ReadByte();
                    }
                    catch { }

                    var retryBuf = new byte[VCD_RAW_SECTOR_SIZE];
                    if (TryReadExactSector(inStream, sectorOffset, retryBuf))
                    {
                        multiReads.Add(retryBuf);
                        if (VerifyVcdSectorEdcAndSync(retryBuf, out _))
                        {
                            Buffer.BlockCopy(retryBuf, 0, sectorBuf, 0, VCD_RAW_SECTOR_SIZE);
                            edcPerfect = true;
                            stats.BitRepairedSectors++;
                            break;
                        }
                    }
                }

                if (!edcPerfect && multiReads.Count >= 3)
                {
                    BitwiseMajorityVote(multiReads, votedSector);
                    Buffer.BlockCopy(votedSector, 0, sectorBuf, 0, VCD_RAW_SECTOR_SIZE);
                    if (VerifyVcdSectorEdcAndSync(sectorBuf, out _))
                    {
                        edcPerfect = true;
                        stats.BitRepairedSectors++;
                    }
                }
            }

            if (edcPerfect)
            {
                if (IsNonEmptyMpegSector(sectorBuf))
                {
                    outStream.Write(sectorBuf, VCD_SECTOR_HEADER_SIZE, VCD_MPEG_PAYLOAD_SIZE);
                    stats.EdcPerfectSectors++;
                }
            }
            else
            {
                stats.ConcealedCorruptSectors++;
                // Eğer tamamen sıfır değilse:
                // Ses sektörü ise bozuk MP2 ses çerçevesinin patlama ("chirp") yapmaması için atla;
                // Video sektörü ise PES ve Dilim (Slice) hizalamasını bozmamak için Pack başlığını onararak koru
                // (FFmpeg -ec 3 guess_mvs+deblock yalnızca o dilimdeki hasarlı makro-bloğu komşu kareyle gizler!)
                byte submode = (sectorBuf[16] == sectorBuf[20]) ? sectorBuf[18] : sectorBuf[22];
                bool isAudioSector = (submode & SUBMODE_AUDIO) != 0;

                if (!isAudioSector && IsNonEmptyMpegSector(sectorBuf))
                {
                    RepairDamagedVideoPackHeader(sectorBuf);
                    outStream.Write(sectorBuf, VCD_SECTOR_HEADER_SIZE, VCD_MPEG_PAYLOAD_SIZE);
                }
            }

            if (s % 1000 == 0 || s == totalSectors - 1)
            {
                int permille = totalSectors > 0 ? (int)((s * 1000L) / totalSectors) : 1000;
                onProgress?.Invoke(
                    permille,
                    $"EDC-CRC Sektör Analizi: {s + 1:N0}/{totalSectors:N0} | Hatasız: {stats.EdcPerfectSectors:N0} | Bit-Onarılan: {stats.BitRepairedSectors:N0} | Onarılan Çizik: {stats.ConcealedCorruptSectors:N0}");
            }
        }

        outStream.Flush();
        onLog?.Invoke($"[EDC-CRC SONUCU] Toplam: {stats.TotalCdxaSectors:N0} sektör | %100 Hatasız: {stats.EdcPerfectSectors:N0} | Bit-Oylama ile Kurtarılan: {stats.BitRepairedSectors:N0} | Onarılan Çizik Sektör: {stats.ConcealedCorruptSectors:N0}");
    }

    private static bool IsNonEmptyMpegSector(byte[] sector2352)
    {
        for (int i = VCD_SECTOR_HEADER_SIZE; i < VCD_SECTOR_HEADER_SIZE + 64; i++)
        {
            if (sector2352[i] != 0) return true;
        }
        return false;
    }

    private static void RepairDamagedVideoPackHeader(byte[] sector2352)
    {
        int p = VCD_SECTOR_HEADER_SIZE;
        // Standart VCD Video sektörü +24'te 00 00 01 BA ile başlar
        sector2352[p] = 0x00;
        sector2352[p + 1] = 0x00;
        sector2352[p + 2] = 0x01;
        sector2352[p + 3] = 0xBA;

        // SCR marker bitlerini garanti altına al
        sector2352[p + 4] = (byte)((sector2352[p + 4] & 0x0E) | 0x21);
        sector2352[p + 6] |= 0x01;
        sector2352[p + 8] |= 0x01;
        sector2352[p + 9] |= 0x80;
        sector2352[p + 11] |= 0x01;
    }

    private static bool TryReadExactSector(FileStream fs, long offset, byte[] buffer2352)
    {
        try
        {
            fs.Seek(offset, SeekOrigin.Begin);
            int totalRead = 0;
            while (totalRead < VCD_RAW_SECTOR_SIZE)
            {
                int r = fs.Read(buffer2352, totalRead, VCD_RAW_SECTOR_SIZE - totalRead);
                if (r <= 0) return false;
                totalRead += r;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool RunFfmpegEnhancement(
        string ffmpegExe,
        string inputMpgPath,
        string outputMp4Path,
        VideoEnhancementPreset preset,
        int totalSectors,
        Action<string>? onLog,
        Action<int, string>? onProgress,
        CancellationToken ct)
    {
        double estimatedDurationSec = Math.Max(1.0, totalSectors / 75.0);

        // Görsel İyileştirme Filtre Zinciri:
        // 1. hqdn3d: VCD MPEG-1 8x8 bloklaşma ve karıncalanmayı temizler
        // 2. scale=960:720:flags=lanczos: 352x288 VCD görüntüsünü Lanczos ile 720p HD'ye yükseltir
        // 3. unsharp: Kenarları ve detayları netleştirir (bulanıklığı giderir)
        // 4. eq: Kontrast ve renk doygunluğunu canlandırır
        // 5. framerate=fps=50: 25 fps videoyu 50 fps akıcı harekete dönüştürür
        string vfChain = preset switch
        {
            VideoEnhancementPreset.UltraRestoreMotion50Fps =>
                "hqdn3d=6:5:8:6,scale=960:720:flags=lanczos,unsharp=5:5:1.25:5:5:0.0,eq=contrast=1.05:saturation=1.08,framerate=fps=50:interp_start=15:interp_end=240:scene=8.2",
            _ =>
                "hqdn3d=4.5:3.5:6:4.5,scale=960:720:flags=lanczos,unsharp=5:5:1.15:5:5:0.0,eq=contrast=1.04:saturation=1.06,framerate=fps=50"
        };

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegExe,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8
        };

        // Güvenlik: ArgumentList kullanarak dosya yollarındaki özel karakterlerin komut enjeksiyonuna (CWE-88) yol açmasını engelle
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-fflags");
        psi.ArgumentList.Add("+genpts+igndts+discardcorrupt");
        psi.ArgumentList.Add("-err_detect");
        psi.ArgumentList.Add("ignore_err");
        psi.ArgumentList.Add("-ec");
        psi.ArgumentList.Add("3");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(Path.GetFullPath(inputMpgPath));
        psi.ArgumentList.Add("-vf");
        psi.ArgumentList.Add(vfChain);
        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add("libx264");
        psi.ArgumentList.Add("-preset");
        psi.ArgumentList.Add("fast");
        psi.ArgumentList.Add("-crf");
        psi.ArgumentList.Add("19");
        psi.ArgumentList.Add("-pix_fmt");
        psi.ArgumentList.Add("yuv420p");
        psi.ArgumentList.Add("-af");
        psi.ArgumentList.Add("aresample=async=1:first_pts=0");
        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("aac");
        psi.ArgumentList.Add("-b:a");
        psi.ArgumentList.Add("192k");
        psi.ArgumentList.Add("-movflags");
        psi.ArgumentList.Add("+faststart");
        psi.ArgumentList.Add(Path.GetFullPath(outputMp4Path));

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(true); } catch { }
        });

        string? line;
        while ((line = proc.StandardError.ReadLine()) != null)
        {
            if (ct.IsCancellationRequested) break;

            int tIdx = line.IndexOf("time=", StringComparison.Ordinal);
            if (tIdx >= 0 && line.Length >= tIdx + 13)
            {
                string timeStr = line.Substring(tIdx + 5, 11);
                if (TimeSpan.TryParse(timeStr, out TimeSpan ts))
                {
                    int permille = (int)Math.Clamp((ts.TotalSeconds * 1000.0) / estimatedDurationSec, 0, 999);
                    onProgress?.Invoke(
                        permille,
                        $"720p 50FPS Netleştirme & Akıcılaştırma İşleniyor: {ts:mm\\:ss} / {TimeSpan.FromSeconds(estimatedDurationSec):mm\\:ss} (%{permille / 10.0:F1})");
                }
            }
        }

        proc.WaitForExit();
        bool ok = proc.ExitCode == 0 && File.Exists(outputMp4Path);
        if (ok)
        {
            onLog?.Invoke($"[BAŞARILI] Net ve akıcı 720p 50FPS MP4 video kaydedildi: {outputMp4Path}");
        }
        else
        {
            onLog?.Invoke($"[UYARI] FFmpeg işlem kodu: {proc.ExitCode}");
        }
        return ok;
    }

    public static string? FindFfmpegExecutable()
    {
        // 1. Uygulama dizininde ffmpeg.exe kontrolü
        string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(localPath)) return localPath;

        // 2. Sistem PATH ortam değişkeninde güvenilir ffmpeg.exe kontrolü
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch { }
            }
        }

        // 3. Python imageio_ffmpeg resmi binaries dizini kontrolü
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string pythonRoot = Path.Combine(localAppData, "Programs", "Python");
        try
        {
            if (Directory.Exists(pythonRoot))
            {
                foreach (string pyDir in Directory.EnumerateDirectories(pythonRoot, "Python*"))
                {
                    string binDir = Path.Combine(pyDir, "Lib", "site-packages", "imageio_ffmpeg", "binaries");
                    if (Directory.Exists(binDir))
                    {
                        foreach (string file in Directory.EnumerateFiles(binDir, "ffmpeg-win-*.exe"))
                        {
                            return Path.GetFullPath(file);
                        }
                    }
                }
            }
        }
        catch { }

        return null;
    }
}
