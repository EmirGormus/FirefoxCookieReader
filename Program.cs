using Microsoft.Data.Sqlite;
using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Diagnostics; 
using System.Globalization; 

public class FirefoxCookieReader
{
    public static void Main(string[] args)
    {
        string targetWebsite = "trendyol.com"; 

        
        List<string> sqliteFiles = GetSQLiteFilesFromPowerShell();
        if (sqliteFiles == null || sqliteFiles.Count == 0)
        {
            Console.WriteLine("PowerShell komutu tarafından hiçbir SQLite dosyası bulunamadı.");
            Console.ReadLine(); 
            return;
        }

        
        foreach (string sqliteFilePath in sqliteFiles)
        {
            Console.WriteLine($"\nSQLite dosyası işleniyor: {sqliteFilePath}");
            ProcessSQLiteFile(sqliteFilePath, targetWebsite);
        }

        Console.WriteLine("\nTüm SQLite dosyaları işlendi.");
        Console.ReadLine(); 
    }

    
    private static List<string> GetSQLiteFilesFromPowerShell()
    {
        List<string> files = new List<string>();
        try
        {
            
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

            
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true, 
                CreateNoWindow = true, 
            };

            
            using (Process process = new Process { StartInfo = psi })
            {
                process.Start();

                
                using (StreamReader reader = process.StandardOutput)
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (!string.IsNullOrWhiteSpace(line)) 
                        {
                            files.Add(line);
                        }
                    }
                }

                
                string errorOutput = process.StandardError.ReadToEnd();
                if (!string.IsNullOrEmpty(errorOutput))
                {
                    Console.WriteLine($"PowerShell Hatası: {errorOutput}");
                    return null; 
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
                tableName = "cookies"; 
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

                string host = isFirefox ? cookieReader.GetString(2) : cookieReader.GetString(2);
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
        
        long unixEpochTicks = new DateTime(1970, 1, 1).Ticks;
        long chromeEpochTicks = new DateTime(1601, 1, 1).Ticks;
        long timestampTicks = chromeTimestamp * 10; 
        DateTime chromeEpoch = new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return chromeEpoch.AddTicks(timestampTicks).ToLocalTime();
    }

}

