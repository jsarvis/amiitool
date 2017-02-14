/*
 * Copyright (C) 2015 Marcos Vives Del Sol
 * Copyright (C) 2016 Benjamin Krämer
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
 */

using System;
using System.IO;
using LibAmiibo.Data;
using LibAmiibo.Data.Figurine;
using LibAmiibo.Data.Settings;
using LibAmiibo.Encryption;
using LibAmiibo.Helper;
using NDesk.Options;

namespace amiitool
{
    class Program
    {
        private static bool showHelp;
        private static bool doEncrypt;
        private static bool doDecrypt;
        private static bool deactivateSignatureCheck;
        private static string keyFile;
        private static string mergeFile;
        private static string inputFile;
        private static string outputFile;

        private static OptionSet p = new OptionSet
        {
            {
                "e|encrypt", "encrypt and sign amiibo.",
                v => doEncrypt = v != null
            },
            {
                "d|decrypt", "decrypt and test amiibo.",
                v => doDecrypt = v != null
            },
            {
                "k|keyfile=", "key set file. For retail amiibo, use \"retail unfixed\" key set.",
                v => keyFile = v
            },
            {
                "m|mergefile=", "merge app data from this file into the input file (decrypting if needed). encrypt or decrypt flags determine the output file format.",
                v => mergeFile = v
            },
            {
                "i|infile=", "input file. If not specified, stdin will be used.",
                v => inputFile = v
            },
            {
                "o|outfile=", "output file. If not specified, stdout will be used.",
                v => outputFile = v
            },
            {
                "s|skip", "decrypt files with invalid signatures or force encrypting when already encrypted.",
                v => deactivateSignatureCheck = v != null
            },
            {
                "h|help", "shows the help.",
                v => showHelp = v != null
            }
        };

        static void ShowHelp()
        {
            Console.Error.WriteLine(
                "amiitool\n" +
                "\n" +
                "Usage: amiitool (-e|-d) -k keyfile [-m mergefile] [-i input] [-o output]");
            p.WriteOptionDescriptions(Console.Error);
        }

        static int Main(string[] args)
        {
            try
            {
                p.Parse(args);
            }
            catch (OptionException e)
            {
                Console.Write("amiitool: ");
                Console.WriteLine(e.Message);
                Console.WriteLine("Try `amiitool --help' for more information.");
                return 1;
            }

            if (showHelp || !(doEncrypt ^ doDecrypt) || keyFile == null)
            {
                ShowHelp();
                return 1;
            }

            AmiiboKeys amiiboKeys = AmiiboKeys.LoadKeys(keyFile);
            if (amiiboKeys == null)
            {
                Console.Error.WriteLine("Could not load keys from \"{0}\"", keyFile);
                return 5;
            }

            byte[] original = new byte[NtagHelpers.NFC3D_NTAG_SIZE];
            byte[] decrypted = new byte[NtagHelpers.NFC3D_AMIIBO_SIZE];
            byte[] encrypted = new byte[NtagHelpers.NFC3D_NTAG_SIZE];

            Stream file = Console.OpenStandardInput();
            if (inputFile != null)
            {
                try
                {
                    file = File.OpenRead(inputFile);
                }
                catch(Exception ex)
                {
                    Console.Error.WriteLine("Could not open input file: {0}", ex.Message);
                    return 3;
                }
            }

            int readBytes = 0;
            bool inputEncryptedTag;
            try
            {
                using (var reader = new BinaryReader(file))
                {
                    readBytes = reader.Read(original, 0, original.Length);
                    if (readBytes < NtagHelpers.NFC3D_AMIIBO_SIZE)
                        throw new Exception("Wrong length");
                }

                if (readBytes > NtagHelpers.NFC3D_AMIIBO_SIZE)
                {
                    inputEncryptedTag = true;
                    Console.Error.WriteLine("Input file appears to be an encrypted tag.");
                }
                else
                {
                    inputEncryptedTag = false;
                    Console.Error.WriteLine("Input file appears to be a decrypted amiibo.");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Could not read from input: {0}", ex.Message);
                return 3;
            }

            if (!string.IsNullOrEmpty(mergeFile))
            {
                Stream appdataFile;
                try
                {
                    appdataFile = File.OpenRead(mergeFile);
                }
                catch(Exception ex)
                {
                    Console.Error.WriteLine("Could not open merge file: {0}", ex.Message);
                    return 3;
                }

                byte[] rawMergable = new byte[NtagHelpers.NFC3D_NTAG_SIZE];
                bool mergeFileEncryptedTag;

                try
                {
                    using (var reader = new BinaryReader(appdataFile))
                    {
                        readBytes = reader.Read(rawMergable, 0, rawMergable.Length);
                        if (readBytes < NtagHelpers.NFC3D_AMIIBO_SIZE)
                            throw new Exception("Wrong length");

                        if (readBytes > NtagHelpers.NFC3D_AMIIBO_SIZE)
                        {
                            mergeFileEncryptedTag = true;
                            Console.Error.WriteLine("Merge file appears to be an encrypted tag.");
                        }
                        else
                        {
                            mergeFileEncryptedTag = false;
                            Console.Error.WriteLine("Merge file appears to be a decrypted amiibo.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Could not read from merge file: {0}", ex.Message);
                    return 3;
                }

                Amiibo originalAmiibo;
                if (inputEncryptedTag)
                {
                    originalAmiibo = Amiibo.FromNtagData(original);
                }
                else
                {
                    originalAmiibo = Amiibo.FromInternalTag(original);
                }

                Amiibo mergableAmiibo;
                if (mergeFileEncryptedTag)
                {
                    mergableAmiibo = Amiibo.FromNtagData(rawMergable);
                }
                else
                {
                    mergableAmiibo = Amiibo.FromInternalTag(rawMergable);
                }

                if (!originalAmiibo.StatueId.Equals(mergableAmiibo.StatueId))
                {
                    Console.Error.WriteLine("!!! WARNING !!!: Input and merge file are not the same amiibo. Input is {0} and merge is {1}.", originalAmiibo, mergableAmiibo);
                }

                byte[] decryptedMergable = new byte[NtagHelpers.NFC3D_AMIIBO_SIZE];

                // Attempt to decrypt
                if (!amiiboKeys.Unpack(rawMergable, decryptedMergable))
                {
                    // Already decrypted
                    Array.Copy(rawMergable, 0, decryptedMergable, 0, decryptedMergable.Length);

                    if (mergeFileEncryptedTag)
                    {
                        // Failed to decrypt, but appears encrypted due to size
                        Console.Error.WriteLine("!!! WARNING !!!: Decryption failed on merge file tag that appeared encrypted. Use skip option to proceed.");
                        if (!deactivateSignatureCheck)
                            return 6;
                    }
                    else
                    {
                        Console.Error.WriteLine("HMAC checks indicates the merge file tag appears to already be decrypted as expected.");
                    }
                }
                else
                {
                    Console.Error.WriteLine("HMAC checks indicates the merge file tag decrypted successfully.");
                }

                var mergableTag = AmiiboTag.FromInternalTag(decryptedMergable);

                byte[] interim = new byte[NtagHelpers.NFC3D_AMIIBO_SIZE];

                // Attempt to decrypt
                if (!amiiboKeys.Unpack(original, interim))
                {
                    // Already decrypted
                    Array.Copy(original, 0, interim, 0, interim.Length);

                    if (inputEncryptedTag)
                    {
                        // Failed to decrypt, but appears encrypted due to size
                        Console.Error.WriteLine("!!! WARNING !!!: Decryption failed on input file tag that appeared encrypted. Use skip option to proceed.");
                        if (!deactivateSignatureCheck)
                            return 6;
                    }
                    else
                    {
                        Console.Error.WriteLine("HMAC checks indicates the input file tag appears to already be decrypted as expected.");
                    }
                }
                else
                {
                    Console.Error.WriteLine("HMAC checks indicates the input file tag decrypted successfully.");
                }

                var targetTag = AmiiboTag.FromInternalTag(interim);

                if (!mergableTag.HasAppData)
                {
                    if (!mergableTag.AmiiboSettings.Status.HasFlag(Status.SettingsInitialized))
                    {
                        Console.Error.WriteLine("The merge file does not have settings data!");
                    }
                    else
                    {
                        Console.Error.WriteLine("The merge file does not have app data!");
                    }
                    return 3;
                }

                if (targetTag.HasAppData || targetTag.AmiiboSettings.Status.HasFlag(Status.SettingsInitialized))
                {
                    Console.Error.WriteLine("!!! WARNING !!!: The input file already has settings data. Overwriting the following:");
                    Console.Error.WriteLine("Amiibo Nickname {0}", targetTag.AmiiboSettings.AmiiboNickname);
                }

                if (targetTag.HasAppData)
                {
                    Console.Error.WriteLine("!!! WARNING !!!: The input file already has app data. Overwriting the following:");
                    Console.Error.WriteLine("App id {0}", targetTag.AmiiboSettings.AppID);
                    Console.Error.WriteLine("App data title id {0}", targetTag.AmiiboSettings.AppDataInitializationTitleID.TitleID);
                }

                //TODO exclude AmiiboNickname and OwnerMii, but might break signature
                // Copy Amiibo Settings
                Console.Error.WriteLine("Copying settings data with the following values:");
                Console.Error.WriteLine("Amiibo Nickname {0}", mergableTag.AmiiboSettings.AmiiboNickname);
                // mergableTag.AmiiboSettings
                var amiiboSettings = mergableTag.CryptoBuffer;
                Array.Copy(amiiboSettings, 0, interim, 0x02C, amiiboSettings.Length);

                // Copy App Data
                Console.Error.WriteLine("Copying app data with the following values:");
                Console.Error.WriteLine("App id {0}", mergableTag.AmiiboSettings.AppID);
                Console.Error.WriteLine("App data title id {0}", mergableTag.AmiiboSettings.AppDataInitializationTitleID.TitleID);
                var appData = mergableTag.AppData;
                Array.Copy(appData, 0, interim, 0x0DC, appData.Length);

                if (doEncrypt)
                {
                    amiiboKeys.Pack(interim, encrypted);
                }
                else
                {
                    decrypted = interim;
                }
            }
            else
            {
                if (doEncrypt)
                {
                    if (inputEncryptedTag)
                    {
                        Console.Error.WriteLine("!!! WARNING !!!: Based on input size the tag appears to already be encrypted.");
                    }
                    byte[] test = new byte[NtagHelpers.NFC3D_NTAG_SIZE];
                    if (amiiboKeys.Unpack(original, decrypted))
                    {
                        Console.Error.WriteLine("!!! WARNING !!!: Based on HMAC checks the tag appears to already be encrypted. Use skip option to proceed.");
                        if (!deactivateSignatureCheck)
                            return 6;
                    }
                    amiiboKeys.Pack(original, encrypted);
                }
                else
                {
                    if (!inputEncryptedTag)
                    {
                        Console.Error.WriteLine("!!! WARNING !!!: Based on input size the tag appears to already be decrypted.");
                    }
                    if (!amiiboKeys.Unpack(original, decrypted))
                    {
                        Console.Error.WriteLine("!!! WARNING !!!: Tag signature was NOT valid. Maybe this was already decrypted? Use skip option to proceed.");
                        if (!deactivateSignatureCheck)
                            return 6;
                    }
                    else
                    {
                        Console.Error.WriteLine("HMAC checks indicates the tag decrypted successfully.");
                    }

                    var amiiboTag1 = AmiiboTag.FromInternalTag(decrypted);
                    var amiiboTag2 = AmiiboTag.FromNtagData(original);
                }
            }

            file = Console.OpenStandardOutput();
            if (outputFile != null)
            {
                try
                {
                    file = File.OpenWrite(outputFile);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Could not open output file: {0}", ex.Message);
                    return 4;
                }
            }

            try
            {
                using (var writer = new BinaryWriter(file))
                {
                    if (doEncrypt)
                    {
                        writer.Write(encrypted, 0, encrypted.Length);
                    }
                    else
                    {
                        writer.Write(decrypted, 0, decrypted.Length);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Could not write to output: {0}", ex.Message);
                return 3;
            }

            return 0;
        }
    }
}
