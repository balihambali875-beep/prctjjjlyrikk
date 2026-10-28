# Lyric Pop untuk Windows — dibuat oleh arlnxs

Widget lirik tersinkron yang mengambang di atas layar PC dan otomatis mengikuti lagu dari
**Spotify desktop, YouTube Music, browser, dan pemutar lain** (semua yang tampil di panel
volume/media Windows). Lirik dari [LRCLIB](https://lrclib.net).

## Mendapatkan LyricPop.exe

### Opsi A — GitHub Actions (tanpa install apa pun)
1. Buat repo GitHub baru dan upload **seluruh isi folder ini** (termasuk folder `.github`).
2. Tab **Actions** -> **Build EXE** -> *Run workflow* (otomatis juga saat push).
3. Setelah selesai, unduh artifact **LyricPop-windows** -> ekstrak -> `LyricPop.exe`.
   File ini mandiri (tidak perlu memasang .NET).

### Opsi B — Visual Studio / .NET SDK 8
```
dotnet publish LyricPop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o out
```
Hasilnya `out/LyricPop.exe`. (Atau buka `LyricPop.csproj` di Visual Studio 2022 lalu tekan F5.)

## Pemakaian
- Jalankan `LyricPop.exe`, lalu putar lagu di Spotify. Widget muncul di tengah atas layar.
- **Klik** header = buka/tutup lirik lengkap. **Seret** header = pindahkan (posisi diingat).
- **Klik kanan** widget = tema, ukuran font, transparansi, sinkron lirik (offset), cari manual, dll.
- Tombol minus = mode mini. Tombol silang = sembunyikan sampai lagu berganti.
- Ikon di system tray: Tampilkan widget, Mulai bersama Windows, Tentang, Keluar.

Persyaratan: Windows 10 versi 1809 atau lebih baru. Di game layar penuh *eksklusif* widget tidak
terlihat; pakai mode *borderless/windowed*.
