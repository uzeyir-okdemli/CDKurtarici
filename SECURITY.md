# Güvenlik Politikası ve Mimari Önlemler (Security Policy)

Bu proje, bozuk/çizik optik medyalardan (CD/DVD/VCD) düşük seviyeli Win32 G/Ç (I/O) kullanarak veri kurtarma işlemi yapar. Düşük seviyeli sistem çağrıları ve harici süreç yönetimi içerdiğinden aşağıdaki güvenlik önlemleri doğrudan kod tabanına uygulanmıştır:

## Uygulanan Güvenlik Kontrolleri

1. **DLL Hijacking / Preloading Koruması (`CWE-427`)**:
   - `NativeCdIo.cs` içerisindeki tüm `kernel32.dll` P/Invoke bildirimleri `[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]` ve `ExactSpelling = true` öznitelikleri ile sınırlandırılmıştır. Uygulama dizininden sahte sistem DLL'lerinin yüklenmesi engellenir.

2. **Path Traversal / Zip-Slip Koruması (`CWE-22`)**:
   - `CdRecoveryEngine.TryResolveSafeDestinationPath` metodu, bozuk veya özel hazırlanmış ISO9660/UDF dosya tablolarındaki `..\` gibi üst dizine çıkış girişimlerini `Path.GetFullPath` kanonikleştirmesi ile denetler ve hedef klasör (`DestinationRoot`) dışına dosya yazılmasını kesin olarak reddeder.
   - Ayrılmış Windows aygıt isimleri (`CON`, `PRN`, `AUX`, `NUL`, `COM1..9`, `LPT1..9`) engellenir.

3. **Win32 Cihaz İsim Alanı (`\\.\`) Doğrulaması**:
   - `NativeCdIo.TryGetSafeDeviceVolumePath` metodu, `DeviceIoControl` çağrılarında yalnızca `^[A-Z]:$` formatındaki geçerli sürücü harflerine izin verir; rastgele adlandırılmış borulara (Named Pipes) veya çekirdek aygıtlarına erişimi engeller.

4. **Komut ve Argüman Enjeksiyonu Koruması (`CWE-78` / `CWE-88`)**:
   - `VcdVideoRecovery.cs` içerisinde FFmpeg çalıştırılırken dize birleştirme yerine `.NET` `ProcessStartInfo.ArgumentList` API'si kullanılır (`UseShellExecute = false`). Özel karakter içeren dosya adları kabuk (shell) veya argüman enjeksiyonuna yol açamaz.

5. **Sembolik Bağlantı / Reparse Point Döngü Koruması (`CWE-59`)**:
   - `CdRecoveryEngine.ScanDirectoryTreeAsync`, `FileAttributes.ReparsePoint` bayrağına sahip dizin ve dosyaları atlayarak sembolik bağlantı tuzaklarını ve sonsuz döngüleri önler.

6. **Bellek Güvenliği ve En Az Ayrıcalık (`Least Privilege`)**:
   - Sektör okuma tamponu (`alignedBuffer`) ayrıldığında ve serbest bırakılmadan hemen önce `NativeMemory.Clear` ile sıfırlanır (`CWE-212` / bellek sızıntısı önlemi).
   - `ReadFile` çağrılarından önce tampon boyutu `ArgumentOutOfRangeException.ThrowIfGreaterThan` ile denetlenir (`CWE-120` Buffer Overflow önlemi).
   - Watchdog iş parçacığı açılırken `THREAD_ALL_ACCESS` yerine yalnızca `THREAD_TERMINATE | SYNCHRONIZE` izinleri kullanılır.

## Güvenlik Açığı Bildirimi (Reporting a Vulnerability)

Bir güvenlik açığı tespit ederseniz lütfen GitHub **Security Advisories** veya **Issues** üzerinden bildirin.
