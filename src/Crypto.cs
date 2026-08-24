using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RedOSPackageUpdater
{
    // Шифрование секретов.
    //  - ProtectLocal/UnprotectLocal: DPAPI (CurrentUser) для хранения в конфиге на этой машине/пользователе.
    //    Это защищает config.json от чтения "с диска" другим пользователем Windows или при краже файла.
    //    НЕ защищает от вредоносного процесса, работающего под той же учёткой - тот расшифрует тем же
    //    вызовом ProtectedData.Unprotect. Для инструмента одного инженера это осознанный и достаточный
    //    компромисс (без внешнего HSM/менеджера паролей).
    //  - EncryptPortable/DecryptPortable: PBKDF2 + AES-256-CBC + HMAC-SHA256 под мастер-паролем,
    //    для ПЕРЕНОСИМОГО экспорта (DPAPI не расшифровывается на другой машине/у другого пользователя).
    internal static class Crypto
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RedOSPackageUpdater/v1/dpapi");

        // Версия 1 (старый формат, ещё может встречаться в уже сделанных экспортах): magic+salt+iv+cipher,
        // без проверки целостности, PBKDF2 100k итераций. Версия 2 (текущая): добавлен HMAC-SHA256
        // поверх шифротекста (encrypt-then-MAC - подмена/повреждение файла обнаруживается ДО расшифровки,
        // без этого шифрование без аутентификации - классический padding-oracle риск) и итерации подняты
        // до значения, актуального для PBKDF2-SHA1 на 2026 год. Старые файлы по-прежнему читаются.
        private static readonly byte[] MagicPrefix = new byte[] { (byte)'R', (byte)'P', (byte)'U' };
        private const byte VersionLegacyNoHmac = 1;
        private const byte VersionHmac = 2;
        private const int Pbkdf2ItersV1 = 100000;
        private const int Pbkdf2ItersV2 = 300000;

        // ---- DPAPI (локально) ----
        public static string ProtectLocal(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            byte[] data = Encoding.UTF8.GetBytes(plain);
            try
            {
                byte[] enc = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(enc);
            }
            finally { Array.Clear(data, 0, data.Length); }
        }

        // Возвращает: "" если исходных данных нет; null если данные есть, но расшифровать НЕ удалось
        // (чужой DPAPI - другой пользователь/машина). Разделение важно, чтобы не затереть пароль пустышкой.
        public static string UnprotectLocal(string enc)
        {
            if (string.IsNullOrEmpty(enc)) return "";
            byte[] dec = null;
            try
            {
                byte[] data = Convert.FromBase64String(enc);
                dec = ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(dec);
            }
            catch { return null; }
            finally { if (dec != null) Array.Clear(dec, 0, dec.Length); }
        }

        // ---- Переносимое шифрование под мастер-паролем ----
        public static string EncryptPortable(string plain, string master)
        {
            if (plain == null) plain = "";
            byte[] salt = new byte[16];
            byte[] iv = new byte[16];
            using (var rng = new RNGCryptoServiceProvider()) { rng.GetBytes(salt); rng.GetBytes(iv); }

            byte[] aesKey = null, hmacKey = null, pt = null, cipher;
            try
            {
                DeriveKeys(master, salt, Pbkdf2ItersV2, out aesKey, out hmacKey);
                using (var aes = NewAes(aesKey, iv))
                using (var enc = aes.CreateEncryptor())
                {
                    pt = Encoding.UTF8.GetBytes(plain);
                    cipher = enc.TransformFinalBlock(pt, 0, pt.Length);
                }

                byte[] header = new byte[4 + 16 + 16]; // magic(3)+version(1) + salt(16) + iv(16)
                MagicPrefix.CopyTo(header, 0);
                header[3] = VersionHmac;
                salt.CopyTo(header, 4);
                iv.CopyTo(header, 20);

                byte[] mac;
                using (var hmac = new HMACSHA256(hmacKey))
                {
                    hmac.TransformBlock(header, 0, header.Length, null, 0);
                    hmac.TransformFinalBlock(cipher, 0, cipher.Length);
                    mac = hmac.Hash;
                }

                using (var ms = new MemoryStream())
                {
                    ms.Write(header, 0, header.Length);
                    ms.Write(mac, 0, mac.Length);
                    ms.Write(cipher, 0, cipher.Length);
                    return Convert.ToBase64String(ms.ToArray());
                }
            }
            finally
            {
                if (aesKey != null) Array.Clear(aesKey, 0, aesKey.Length);
                if (hmacKey != null) Array.Clear(hmacKey, 0, hmacKey.Length);
                if (pt != null) Array.Clear(pt, 0, pt.Length);
            }
        }

        public static string DecryptPortable(string blob, string master)
        {
            byte[] all = Convert.FromBase64String(blob);
            if (all.Length < 4) throw new InvalidDataException("Повреждённые данные экспорта");
            for (int i = 0; i < 3; i++)
                if (all[i] != MagicPrefix[i]) throw new InvalidDataException("Неверный формат/магия экспорта");
            byte version = all[3];

            if (version == VersionLegacyNoHmac) return DecryptLegacyV1(all, master);
            if (version == VersionHmac) return DecryptV2(all, master);
            throw new InvalidDataException("Экспорт сделан более новой версией программы (версия формата " + version + ") - обновите RedOSPackageUpdater");
        }

        private static string DecryptV2(byte[] all, string master)
        {
            const int headerLen = 36; // magic+version(4) + salt(16) + iv(16)
            const int macLen = 32;
            if (all.Length < headerLen + macLen) throw new InvalidDataException("Повреждённые данные экспорта");

            byte[] header = new byte[headerLen];
            Buffer.BlockCopy(all, 0, header, 0, headerLen);
            byte[] salt = new byte[16], iv = new byte[16];
            Buffer.BlockCopy(all, 4, salt, 0, 16);
            Buffer.BlockCopy(all, 20, iv, 0, 16);
            byte[] macExpected = new byte[macLen];
            Buffer.BlockCopy(all, headerLen, macExpected, 0, macLen);
            int clen = all.Length - headerLen - macLen;
            byte[] cipher = new byte[clen];
            Buffer.BlockCopy(all, headerLen + macLen, cipher, 0, clen);

            byte[] aesKey = null, hmacKey = null, pt = null;
            try
            {
                DeriveKeys(master, salt, Pbkdf2ItersV2, out aesKey, out hmacKey);

                byte[] macActual;
                using (var hmac = new HMACSHA256(hmacKey))
                {
                    hmac.TransformBlock(header, 0, header.Length, null, 0);
                    hmac.TransformFinalBlock(cipher, 0, cipher.Length);
                    macActual = hmac.Hash;
                }
                // Неверный мастер-пароль и повреждённый/подменённый файл дают одну и ту же ошибку -
                // так и должно быть: не раскрываем атакующему, что именно не совпало.
                if (!FixedTimeEquals(macActual, macExpected))
                    throw new CryptographicException("Неверный мастер-пароль или файл экспорта повреждён/изменён");

                using (var aes = NewAes(aesKey, iv))
                using (var dec = aes.CreateDecryptor())
                {
                    pt = dec.TransformFinalBlock(cipher, 0, cipher.Length);
                    return Encoding.UTF8.GetString(pt);
                }
            }
            finally
            {
                if (aesKey != null) Array.Clear(aesKey, 0, aesKey.Length);
                if (hmacKey != null) Array.Clear(hmacKey, 0, hmacKey.Length);
                if (pt != null) Array.Clear(pt, 0, pt.Length);
            }
        }

        // Совместимость со старыми файлами экспорта (без HMAC, меньше итераций PBKDF2).
        // Новые экспорты всегда пишутся в формате v2 (DecryptV2) - этот путь только на чтение.
        private static string DecryptLegacyV1(byte[] all, string master)
        {
            if (all.Length < 36) throw new InvalidDataException("Повреждённые данные экспорта");
            byte[] salt = new byte[16], iv = new byte[16];
            Buffer.BlockCopy(all, 4, salt, 0, 16);
            Buffer.BlockCopy(all, 20, iv, 0, 16);
            int clen = all.Length - 36;
            byte[] cipher = new byte[clen];
            Buffer.BlockCopy(all, 36, cipher, 0, clen);

            byte[] key = null, pt = null;
            try
            {
                using (var kdf = new Rfc2898DeriveBytes(master, salt, Pbkdf2ItersV1)) key = kdf.GetBytes(32);
                using (var aes = NewAes(key, iv))
                using (var dec = aes.CreateDecryptor())
                {
                    pt = dec.TransformFinalBlock(cipher, 0, cipher.Length);
                    return Encoding.UTF8.GetString(pt);
                }
            }
            finally
            {
                if (key != null) Array.Clear(key, 0, key.Length);
                if (pt != null) Array.Clear(pt, 0, pt.Length);
            }
        }

        // 64 байта PBKDF2-вывода: первые 32 - ключ AES-256, вторые 32 - ключ HMAC-SHA256.
        // Один KDF-проход на оба ключа (а не два независимых PBKDF2) - меньше дорогих итераций,
        // безопасность не страдает, т.к. ключи берутся из непересекающихся кусков вывода.
        private static void DeriveKeys(string master, byte[] salt, int iters, out byte[] aesKey, out byte[] hmacKey)
        {
            using (var kdf = new Rfc2898DeriveBytes(master, salt, iters))
            {
                byte[] combined = kdf.GetBytes(64);
                aesKey = new byte[32];
                hmacKey = new byte[32];
                Buffer.BlockCopy(combined, 0, aesKey, 0, 32);
                Buffer.BlockCopy(combined, 32, hmacKey, 0, 32);
                Array.Clear(combined, 0, combined.Length);
            }
        }

        // Сравнение MAC за время, не зависящее от того, на каком байте нашлось расхождение -
        // обычный "return false на первом несовпадении" теоретически позволяет timing-атаку.
        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static SymmetricAlgorithm NewAes(byte[] key, byte[] iv)
        {
            // AesCryptoServiceProvider, а не AesManaged - последний бросает при включённой FIPS-политике Windows.
            var aes = new AesCryptoServiceProvider
            {
                KeySize = 256,
                BlockSize = 128,
                Mode = CipherMode.CBC,
                Padding = PaddingMode.PKCS7,
                Key = key,
                IV = iv
            };
            return aes;
        }
    }
}
