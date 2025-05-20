using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Diagnostics; // İşlem için gerekli
using System.Globalization; // CultureInfo için gerekli

public class FirefoxCookieReader
{
    public static void Main(string[] args)
    {
        string targetWebsite = "trendyol.com"; // Çerezlerini almak istediğiniz web sitesi

        // 1. PowerShell komutunu çalıştır ve çıktıyı yakala
        List<string> sqliteFiles = GetSQLiteFilesFromPowerShell();
        if (sqliteFiles == null || sqliteFiles.Count == 0)
        {
            Console.WriteLine("PowerShell komutu tarafından hiçbir SQLite dosyası bulunamadı.");
            Console.ReadLine(); // Konsolu açık tut
            return;
        }

        // 2. PowerShell tarafından bulunan her SQLite dosyasını işle
        foreach (string sqliteFilePath in sqliteFiles)
        {
            Console.WriteLine($"\nSQLite dosyası işleniyor: {sqliteFilePath}");
            ProcessSQLiteFile(sqliteFilePath, targetWebsite);
        }

        Console.WriteLine("\nTüm SQLite dosyaları işlendi.");
        Console.ReadLine(); // Konsolu açık tut
    }

    // PowerShell komutunu çalıştıran ve çıktıyı döndüren fonksiyon
    private static List<string> GetSQLiteFilesFromPowerShell()
    {
        List<string> files = new List<string>();
        try
        {
            // Düzeltilmiş PowerShell komutu
            string command = @"
                Get-ChildItem -Path ""$env:USERPROFILE\AppData"" -Include 'cookies.sqlite','Cookies' -Recurse -File -ErrorAction SilentlyContinue |
                Where-Object {
                    $file = $_.FullName
                    try {
                        $fs = [System.IO.File]::OpenRead($file)
                        $bytes = New-Object byte[] 16
                        $fs.Read($bytes, 0, 16) | Out-Null
                        $fs.Close()
                        $header = [System.Text.Encoding]::ASCII.GetString($bytes)
                        $header.StartsWith('SQLite format 3')
                    } catch {
                        # Herhangi bir hata durumunda (dosya kilitli, erişilemez), dosyayı hariç tut
                        $false
                    }
                } |
                Select-Object -ExpandProperty FullName
                ";

            // İşlem kurulumu
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true, // Hataları da yakala
                CreateNoWindow = true, // PowerShell penceresini gösterme
            };

            // İşlemi yürüt
            using (Process process = new Process { StartInfo = psi })
            {
                process.Start();

                // Çıktıyı oku
                using (StreamReader reader = process.StandardOutput)
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (!string.IsNullOrWhiteSpace(line)) // Önemli: Boş satırları filtrele.
                        {
                            files.Add(line);
                        }
                    }
                }

                // Hataları oku
                string errorOutput = process.StandardError.ReadToEnd();
                if (!string.IsNullOrEmpty(errorOutput))
                {
                    Console.WriteLine($"PowerShell Hatası: {errorOutput}");
                    // Hata kritikse burada bir istisna atmayı düşünün.
                    return null; // Veya hatayı uygulamanız için uygun şekilde işleyin
                }
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    Console.WriteLine($"PowerShell işlemi {process.ExitCode} koduyla çıktı");
                    return null;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"PowerShell komutu yürütülürken hata oluştu: {ex.Message}");
            return null;
        }
        return files;
    }

    private static void ProcessSQLiteFile(string sqliteFilePath, string targetWebsite)
    {
        if (!File.Exists(sqliteFilePath))
        {
            Console.WriteLine($"Hata: cookies.sqlite bulunamadı: {sqliteFilePath}");
            return;
        }

        string tempCookiesDbPath = Path.GetTempFileName();
        try
        {
            File.Copy(sqliteFilePath, tempCookiesDbPath, true);
        }
        catch (IOException ex)
        {
            Console.WriteLine($"cookies.sqlite kopyalanırken hata oluştu: {ex.Message}");
            return;
        }

        string connectionString = $"Data Source={tempCookiesDbPath}";
        SqliteConnection connection = null;

        try
        {
            connection = new SqliteConnection(connectionString);
            connection.Open();

            // Tablo adlarını kontrol et
            List<string> availableTables = new();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
                using var tableReader = cmd.ExecuteReader();
                while (tableReader.Read())
                {
                    availableTables.Add(tableReader.GetString(0));
                }
            }

            string tableName = null;
            bool isFirefox = false;

            if (availableTables.Contains("moz_cookies"))
            {
                tableName = "moz_cookies";
                isFirefox = true;
            }
            else if (availableTables.Contains("cookies"))
            {
                tableName = "cookies"; // Chromium
            }
            else
            {
                Console.WriteLine($"'{sqliteFilePath}' içinde tanınan bir çerez tablosu bulunamadı.");
                return;
            }

            string query = isFirefox
                ? $"SELECT name, value, host, path, expiry, isSecure, isHttpOnly, creationTime, lastAccessed FROM {tableName} WHERE host LIKE '%{targetWebsite}%'"
                : $"SELECT name, encrypted_value, host_key, path, expires_utc, is_secure, is_httponly, creation_utc, last_access_utc FROM {tableName} WHERE host_key LIKE '%{targetWebsite}%'";

            using var command = new SqliteCommand(query, connection);
            using var cookieReader = command.ExecuteReader();

            if (!cookieReader.HasRows)
            {
                Console.WriteLine($"'{targetWebsite}' için '{tableName}' tablosunda çerez bulunamadı, dosya: '{sqliteFilePath}'.");
                return;
            }

            Console.WriteLine($"'{targetWebsite}' için '{tableName}' tablosundan çerezler bulundu, dosya: '{sqliteFilePath}':");

            while (cookieReader.Read())
            {
                string name = cookieReader.GetString(0);
                string value = isFirefox ? cookieReader.GetString(1) : "[ENCRYPTED]";

                string host = isFirefox ? cookieReader.GetString(2) : cookieReader.GetString(2); // host or host_key
                string path = cookieReader.GetString(3);

                DateTime expiry = isFirefox
                    ? DateTimeOffset.FromUnixTimeSeconds(cookieReader.GetInt64(4)).LocalDateTime
                    : ConvertChromeTimestamp(cookieReader.GetInt64(4));

                bool isSecure = (cookieReader.GetInt64(5) == 1);
                bool isHttpOnly = (cookieReader.GetInt64(6) == 1);

                DateTime creation = isFirefox
                    ? DateTimeOffset.FromUnixTimeMilliseconds(cookieReader.GetInt64(7) / 1000).LocalDateTime
                    : ConvertChromeTimestamp(cookieReader.GetInt64(7));

                DateTime lastAccess = isFirefox
                    ? DateTimeOffset.FromUnixTimeMilliseconds(cookieReader.GetInt64(8) / 1000).LocalDateTime
                    : ConvertChromeTimestamp(cookieReader.GetInt64(8));

                Console.WriteLine("----------------------------------");
                Console.WriteLine($"Ad: {name}");
                Console.WriteLine($"Değer: {value} {(value == "[ENCRYPTED]" ? "(şifreli, çözümleme gerekli)" : "")}");
                Console.WriteLine($"Host: {host}");
                Console.WriteLine($"Yol: {path}");
                Console.WriteLine($"Bitiş Zamanı: {expiry}");
                Console.WriteLine($"Güvenli mi: {isSecure}");
                Console.WriteLine($"HttpOnly mi: {isHttpOnly}");
                Console.WriteLine($"Oluşturulma Zamanı: {creation}");
                Console.WriteLine($"Son Erişim Zamanı: {lastAccess}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Hata: {ex.Message} (Dosya: {sqliteFilePath})");
        }
        finally
        {
            if (connection != null)
            {
                connection.Close();
                connection.Dispose();
            }

            try
            {
                if (File.Exists(tempCookiesDbPath))
                    File.Delete(tempCookiesDbPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Geçici dosya silinirken hata oluştu: {ex.Message}");
            }
        }
    }

    private static DateTime ConvertChromeTimestamp(long chromeTimestamp)
    {
        // Chromium timestamp starts from Jan 1, 1601 in microseconds
        long unixEpochTicks = new DateTime(1970, 1, 1).Ticks;
        long chromeEpochTicks = new DateTime(1601, 1, 1).Ticks;
        long timestampTicks = chromeTimestamp * 10; // microseconds to ticks
        DateTime chromeEpoch = new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return chromeEpoch.AddTicks(timestampTicks).ToLocalTime();
    }

}

