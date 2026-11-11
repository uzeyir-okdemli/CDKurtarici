# 💿 CDKurtarici — Sektör Tabanlı Çizik CD/DVD & VCD Veri Kurtarma Motoru

**CDKurtarici**, eski ve çizik CD/DVD/VCD disklerdeki dosyaları **Windows Gezgini'nin önbellek kilitlenmelerine (Cache Manager Read-Ahead) takılmadan**, optik sürücüyü donanım seviyesinde (`Win32 IOCTL` & `FILE_FLAG_NO_BUFFERING`) tam performansla kullanarak kurtaran açık kaynaklı bir Windows masaüstü uygulamasıdır.

Ayrıca eski **Video CD (`AVSEQ01.DAT` / `ITEMxxxx.DAT`)** dosyaları için resmi **32-bit Mode 2 Form 2 EDC CRC (`0xD8018001`)** doğrulaması, **Bitwise Majority Voting (Bit Çoğunluk Oylaması)** ve **FFmpeg 720p HD 50 FPS Video Restorasyon** motoru içerir.

---

## 🚀 Temel Özellikler

### 1. Windows Gezgini'nden Neden Daha İyi Okur?
* **Önbelleksiz Doğrudan Sektör Okuma (`FILE_FLAG_NO_BUFFERING`)**: Windows Gezgini bir dosyayı kopyalarken 64 KB – 256 KB ileri okuma (*Read-Ahead*) yapar; ilerideki tek bir 2 KB'lık sektörde çizik varsa tüm okuma çöker ve kopyalama iptal edilir. **CDKurtarici**, Windows önbellek yöneticisini devre dışı bırakarak doğrudan optik sürücüden 4096/2048-byte hizalı okuma yapar.
* **Akıllı 2 Aşamalı Kurtarma (`TwoPassSmart`)**:
  * **1. Aşama**: Tüm sağlam dosyaları 64 KB bloklarla maksimum hızda kurtarır. Bir dosyada çiziğe rastladığı anda **o ana kadar okunan sağlam kısmı korur (`ResumeOffset`)**, dosyayı 2. aşamaya erteleyip **hiç vakit kaybetmeden sıradaki dosyaya geçer**.
  * **2. Aşama**: Sağlam dosyalar bittikten sonra çizik dosyalara geri döner, **kaldığı byte'tan itibaren** **2048 byte (1 CD sektörü)** hassasiyetle okur; yalnızca çizik olan 2 KB'lık sektörü `0x00` ile doldurup dosyanın kalanını kurtarır.
* **Çekirdek Seviyesi Takılma Kesici (`CancelIoEx` & `CancelSynchronousIo`)**: Sürücü derin bir çizikte odaklanamayıp belirlenen zaman aşımını (varsayılan `3500 ms`) aşarsa veya kullanıcı **`⏭ DOSYAYI ATLA`** butonuna tıklarsa, çekirdek G/Ç isteği anında kesilir ve arayüz asla donmaz.
* **Donanım Hız Kontrolü (`IOCTL_CDROM_SET_SPEED`)**: Sürücüyü **Maksimum Donanım Hızı (`0xFFFF` CAV)**, **Adaptif Dengeli (`8x`)** veya ağır çizik diskler için **Sarsıntısız Hassas Mod (`4x CLV`)** hızına ayarlar.

### 2. 🎬 Özel VCD (`AVSEQ01.DAT`) Video Onarım, 720p Netleştirme & 50 FPS Akıcılaştırma
* **32-Bit Mode 2 Form 2 EDC CRC (`0xD8018001`) Denetimi**: VCD `.DAT` dosyaları `2352 byte` ham sektörlerden oluşur ve Windows bu sektörlerin son 4 byte'ındaki EDC CRC sağlama kodunu kontrol etmez. `VcdVideoRecovery` her sektörü matematiksel olarak doğrular.
* **Lazer Önbellek Boşaltmalı Çoklu Okuma & Bit Çoğunluk Oylaması (`Bitwise Majority Vote`)**: Bit hatası tespit edilen VCD sektörlerinde lazeri 2 MB uzağa taşıyarak sürücü önbelleğini sıfırlar, sektörü 5 defaya kadar tekrar okur ve bit bazlı çoğunluk oylaması ile hasarlı bitleri onarır.
* **CDXA Kabuk Soyma ve Paket Onarımı**: `44-byte` RIFF CDXA başlığını ve her sektördeki `28-byte` sektör başlıklarını soyar, cızırtı yapan bozuk MP2 ses çerçevelerini temizler ve saf `.mpg` üretir.
* **720p 50 FPS Görüntü Restorasyonu**: FFmpeg `-ec 3` (hareket vektörü hata gizleme), `hqdn3d` (MPEG-1 bloklaşma/gürültü temizleme), `lanczos` (`352x288 -> 960x720 HD` yükseltme), `unsharp` (keskinleştirme) ve `framerate=fps=50` (50 FPS akıcı hareket) filtreleriyle bozuk VCD videoları net ve akıcı `.mp4` formatına dönüştürür.

---

## 🛠️ Kurulum ve Çalıştırma

### Gereksinimler
* Windows 10 / 11 (x64)
* [.NET 8.0 SDK / Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
* *(İsteğe bağlı - VCD 720p 50FPS MP4 dönüştürme için)* `ffmpeg` (`PATH` üzerinde, uygulama klasöründe veya `pip install imageio-ffmpeg` ile)

### Derleme ve Başlatma
```powershell
dotnet build -c Release
.\bin\Release\net8.0-windows\CDKurtarici.exe
```
veya doğrudan **`Baslat.bat`** dosyasına çift tıklayarak çalıştırabilirsiniz.

---

## 🔒 Güvenlik Mimarisi
Bu proje açık kaynak güvenlik standartlarına göre sertleştirilmiştir:
* **DLL Hijacking Koruması (`CWE-427`)**: `[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]`
* **Path Traversal / Zip-Slip Koruması (`CWE-22`)**: Kanonik hedef dizin doğrulaması ve ayrılmış aygıt adı filtrelemesi
* **Argüman Enjeksiyonu Koruması (`CWE-88`)**: `ProcessStartInfo.ArgumentList` kullanımı
* **Symlink / Reparse Point Koruması (`CWE-59`)** ve **Bellek Sıfırlama (`NativeMemory.Clear`)**

Detaylar için [SECURITY.md](SECURITY.md) dosyasına göz atabilirsiniz.

---

## 📄 Lisans
Bu proje [MIT Lisansı](LICENSE) altında açık kaynaklı olarak sunulmaktadır.
