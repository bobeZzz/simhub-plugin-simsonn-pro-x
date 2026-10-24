using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace SimsonnPedals
{
    /// <summary>
    /// A SIMSONN firmware file (.sfu) from the Simsonn Manager install, decrypted and checked
    /// (research/firmware-update.md). Nothing from SIMSONN ships with the plugin: the files and the key are read from
    /// the user's own Manager install.
    /// </summary>
    public sealed class FirmwareFile
    {
        private const int HeaderField = 16;
        private const int KeyOffset = 0x80, KeyLength = 256, IvOffset = 0x180, IvLength = 16, ImagesOffset = 0x190;

        public string Path { get; private set; }
        public string Model { get; private set; }
        public string MainVersion { get; private set; }
        public string SubVersion { get; private set; }
        public byte[] MainImage { get; private set; }
        public byte[] SubImage { get; private set; }
        public ushort MainCrc { get; private set; }
        public ushort SubCrc { get; private set; }

        public string FileName => System.IO.Path.GetFileName(Path);

        public override string ToString() => $"{MainVersion} (second chip {SubVersion}) – {FileName}";

        /// <summary>Firmware model in the file header that fits the pedals with this USB PID, or null if the plugin doesn't know it.</summary>
        public static string ModelForProduct(int productId)
        {
            switch (productId)
            {
                case 0x6012: return "proxultra";
                case 0x6020: return "proxultrav1.5";
                default: return null;
            }
        }

        /// <summary>The file Simsonn Manager itself picks for this PID.</summary>
        public static string ManagerDefaultFile(int productId)
        {
            string model = ModelForProduct(productId);
            return model == null ? null : model + ".sfu";
        }

        /// <summary>
        /// Reads, decrypts and checks a firmware file. Throws <see cref="InvalidDataException"/> with a user-facing
        /// message if anything doesn't add up, so a bad file can never reach the pedals.
        /// </summary>
        public static FirmwareFile Load(string sfuPath, string managerFolder)
        {
            byte[] file = File.ReadAllBytes(sfuPath);
            if (file.Length < ImagesOffset || Field(file, 0x00) != "SIMSONN")
                throw new InvalidDataException($"{System.IO.Path.GetFileName(sfuPath)} isn't a SIMSONN firmware file.");

            var fw = new FirmwareFile
            {
                Path = sfuPath,
                Model = Field(file, 0x10),
                MainVersion = Field(file, 0x20),
                SubVersion = Field(file, 0x50),
                MainCrc = HexField<ushort>(file, 0x30),
                SubCrc = HexField<ushort>(file, 0x60),
            };
            int mainLength = HexField<int>(file, 0x40);
            int subLength = HexField<int>(file, 0x70);
            if (mainLength <= 0 || subLength <= 0 || ImagesOffset + mainLength + subLength != file.Length)
                throw new InvalidDataException($"{fw.FileName}: the image sizes in the header don't match the file.");

            byte[] aesKey;
            try
            {
                using (var rsa = LoadManagerKey(managerFolder))
                    aesKey = rsa.Decrypt(Slice(file, KeyOffset, KeyLength), false);
            }
            catch (CryptographicException)
            {
                throw new InvalidDataException($"{fw.FileName}: Simsonn Manager's key doesn't open this file.");
            }
            if (aesKey.Length != 32)
                throw new InvalidDataException($"{fw.FileName}: unexpected key size ({aesKey.Length} bytes).");
            byte[] iv = Slice(file, IvOffset, IvLength);

            fw.MainImage = Decrypt(Slice(file, ImagesOffset, mainLength), aesKey, iv, fw.FileName);
            fw.SubImage = Decrypt(Slice(file, ImagesOffset + mainLength, subLength), aesKey, iv, fw.FileName);
            // The header CRCs are over the decrypted images: they prove the key and the file are right.
            if (Crc16Arc(fw.MainImage) != fw.MainCrc || Crc16Arc(fw.SubImage) != fw.SubCrc)
                throw new InvalidDataException($"{fw.FileName}: checksum mismatch after decryption; the file is damaged or not for this Manager version.");
            if (fw.MainImage.Length > ushort.MaxValue || fw.SubImage.Length > ushort.MaxValue)
                throw new InvalidDataException($"{fw.FileName}: image too large for the bootloader.");
            return fw;
        }

        /// <summary>The firmware files in the Manager install that fit the pedals with this PID; unreadable files are skipped.</summary>
        public static List<FirmwareFile> FindFor(int productId, string managerFolder, StringBuilder log)
        {
            var found = new List<FirmwareFile>();
            string model = ModelForProduct(productId);
            string folder = System.IO.Path.Combine(managerFolder, "env", "firmware", "pedal");
            if (model == null || !Directory.Exists(folder)) return found;
            foreach (string path in Directory.GetFiles(folder, "*.sfu"))
            {
                try
                {
                    var fw = Load(path, managerFolder);
                    if (fw.Model == model) found.Add(fw);
                }
                catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
                {
                    // Older files for other pedals use another format; only mention files that look like ours.
                    if (System.IO.Path.GetFileName(path).StartsWith(model, StringComparison.OrdinalIgnoreCase))
                        log?.AppendLine($"Skipped {System.IO.Path.GetFileName(path)}: {ex.Message}");
                }
            }
            string defaultFile = ManagerDefaultFile(productId);
            return found.OrderByDescending(f => string.Equals(f.FileName, defaultFile, StringComparison.OrdinalIgnoreCase))
                        .ThenByDescending(f => f.MainVersion, StringComparer.Ordinal)
                        .ToList();
        }

        /// <summary>True if <paramref name="folder"/> looks like a Simsonn Manager install (has its key and firmware folder).</summary>
        public static bool IsManagerFolder(string folder) =>
            !string.IsNullOrEmpty(folder) &&
            File.Exists(System.IO.Path.Combine(folder, "bin", "SSNetwork.dll")) &&
            Directory.Exists(System.IO.Path.Combine(folder, "env", "firmware", "pedal"));

        /// <summary>The Manager install folder from the uninstall entries or the usual places, or null.</summary>
        public static string FindManagerFolder()
        {
            var candidates = new List<string>();
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var root = RegistryKey.OpenBaseKey(hive, view))
                    using (var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                    {
                        if (uninstall == null) continue;
                        foreach (string name in uninstall.GetSubKeyNames())
                        using (var app = uninstall.OpenSubKey(name))
                        {
                            if (!(app?.GetValue("DisplayName") is string display) ||
                                display.IndexOf("SIMSONN", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            if (app.GetValue("InstallLocation") is string location) candidates.Add(location);
                            if (app.GetValue("UninstallString") is string uninstaller)
                                candidates.Add(System.IO.Path.GetDirectoryName(uninstaller.Trim('"')));
                        }
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException || ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
                {
                }
            }
            foreach (var programs in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
            {
                string baseFolder = Environment.GetFolderPath(programs);
                if (string.IsNullOrEmpty(baseFolder)) continue;
                candidates.Add(System.IO.Path.Combine(baseFolder, "SIMSONN Manager"));
                candidates.Add(System.IO.Path.Combine(baseFolder, "SIMSONN"));
            }
            return candidates.Where(c => !string.IsNullOrEmpty(c)).Select(c => c.TrimEnd('\\', '/')).FirstOrDefault(IsManagerFolder);
        }

        /// <summary>CRC-16/ARC (poly 0xA001 reflected, init 0), as in the file header and the bootloader's start packet.</summary>
        public static ushort Crc16Arc(byte[] data)
        {
            int crc = 0;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1;
            }
            return (ushort)crc;
        }

        private static RSACryptoServiceProvider LoadManagerKey(string managerFolder)
        {
            string dll = System.IO.Path.Combine(managerFolder, "bin", "SSNetwork.dll");
            if (!File.Exists(dll)) throw new InvalidDataException($"Simsonn Manager's SSNetwork.dll wasn't found in {managerFolder}.");
            string text = Encoding.ASCII.GetString(File.ReadAllBytes(dll));
            const string begin = "-----BEGIN RSA PRIVATE KEY-----", end = "-----END RSA PRIVATE KEY-----";
            int start = text.IndexOf(begin, StringComparison.Ordinal);
            int stop = start < 0 ? -1 : text.IndexOf(end, start, StringComparison.Ordinal);
            if (stop < 0) throw new InvalidDataException("Simsonn Manager's firmware key wasn't found (different Manager version?).");
            string base64 = new string(text.Substring(start + begin.Length, stop - start - begin.Length).Where(c => !char.IsWhiteSpace(c)).ToArray());

            var rsa = new RSACryptoServiceProvider();
            rsa.ImportParameters(ParsePkcs1(Convert.FromBase64String(base64)));
            return rsa;
        }

        // PKCS#1 RSAPrivateKey: SEQUENCE { version, n, e, d, p, q, dp, dq, qinv }. .NET Framework has no PEM import.
        private static RSAParameters ParsePkcs1(byte[] der)
        {
            int pos = 0;
            if (der[pos++] != 0x30) throw new InvalidDataException("Unexpected key format.");
            ReadLength(der, ref pos);
            ReadInteger(der, ref pos); // version
            byte[] n = ReadInteger(der, ref pos), e = ReadInteger(der, ref pos), d = ReadInteger(der, ref pos);
            byte[] p = ReadInteger(der, ref pos), q = ReadInteger(der, ref pos);
            byte[] dp = ReadInteger(der, ref pos), dq = ReadInteger(der, ref pos), qi = ReadInteger(der, ref pos);
            int size = n.Length, half = (size + 1) / 2;
            return new RSAParameters
            {
                Modulus = n, Exponent = e, D = Pad(d, size),
                P = Pad(p, half), Q = Pad(q, half), DP = Pad(dp, half), DQ = Pad(dq, half), InverseQ = Pad(qi, half),
            };
        }

        private static byte[] ReadInteger(byte[] der, ref int pos)
        {
            if (der[pos++] != 0x02) throw new InvalidDataException("Unexpected key format.");
            int length = ReadLength(der, ref pos);
            int skip = 0;
            while (skip < length - 1 && der[pos + skip] == 0) skip++;
            byte[] value = Slice(der, pos + skip, length - skip);
            pos += length;
            return value;
        }

        private static int ReadLength(byte[] der, ref int pos)
        {
            int first = der[pos++];
            if (first < 0x80) return first;
            int length = 0;
            for (int i = 0; i < (first & 0x7F); i++) length = (length << 8) | der[pos++];
            return length;
        }

        private static byte[] Pad(byte[] value, int length)
        {
            if (value.Length >= length) return value;
            var padded = new byte[length];
            Buffer.BlockCopy(value, 0, padded, length - value.Length, value.Length);
            return padded;
        }

        private static byte[] Decrypt(byte[] data, byte[] key, byte[] iv, string fileName)
        {
            try
            {
                using (var aes = new AesCryptoServiceProvider { Mode = CipherMode.CBC, Padding = PaddingMode.PKCS7, Key = key, IV = iv })
                using (var decryptor = aes.CreateDecryptor())
                    return decryptor.TransformFinalBlock(data, 0, data.Length);
            }
            catch (CryptographicException)
            {
                throw new InvalidDataException($"{fileName}: the firmware didn't decrypt cleanly.");
            }
        }

        private static string Field(byte[] file, int offset) =>
            Encoding.ASCII.GetString(file, offset, HeaderField).Split('\0')[0];

        private static T HexField<T>(byte[] file, int offset)
        {
            string text = Field(file, offset);
            if (!int.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out int value))
                throw new InvalidDataException($"Unexpected firmware header field \"{text}\".");
            return (T)Convert.ChangeType(value, typeof(T));
        }

        private static byte[] Slice(byte[] data, int offset, int length)
        {
            var slice = new byte[length];
            Buffer.BlockCopy(data, offset, slice, 0, length);
            return slice;
        }
    }
}
