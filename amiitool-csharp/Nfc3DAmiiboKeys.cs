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
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Parameters;

namespace amiitool
{
    class Nfc3DAmiiboKeys
    {
        public const int NFC3D_AMIIBO_SIZE = 520;
        public const int HMAC_POS_DATA = 0x008;
        public const int HMAC_POS_TAG = 0x1B4;

        private Nfc3DKeygenMasterkeys data;
        private Nfc3DKeygenMasterkeys tag;

        internal static Nfc3DAmiiboKeys Unserialize(BinaryReader reader)
        {
            return new Nfc3DAmiiboKeys
            {
                data = Nfc3DKeygenMasterkeys.Unserialize(reader),
                tag = Nfc3DKeygenMasterkeys.Unserialize(reader)
            };
        }

        internal void Serialize(BinaryWriter writer)
        {
            this.data.Serialize(writer);
            this.tag.Serialize(writer);
        }

        public bool Unpack(byte[] tag, byte[] plain)
        {
            byte[] internalBytes = new byte[NFC3D_AMIIBO_SIZE];

            // Convert format
            TagToInternal(tag, internalBytes);

            // Generate keys
            Nfc3DKeygenDerivedkeys dataKeys = GenerateKey(this.data, internalBytes);
            Nfc3DKeygenDerivedkeys tagKeys = GenerateKey(this.tag, internalBytes);

            // Decrypt
            dataKeys.Cipher(internalBytes, plain, false);

            // Init OpenSSL HMAC context
            HMac hmacCtx = new HMac(new Sha256Digest());

            // Regenerate tag HMAC. Note: order matters, data HMAC depends on tag HMAC!
            hmacCtx.Init(new KeyParameter(tagKeys.hmacKey));
            hmacCtx.BlockUpdate(plain, 0x1D4, 0x34);
            hmacCtx.DoFinal(plain, HMAC_POS_TAG);

            // Regenerate data HMAC
            hmacCtx.Init(new KeyParameter(dataKeys.hmacKey));
            hmacCtx.BlockUpdate(plain, 0x029, 0x1DF);
            hmacCtx.DoFinal(plain, HMAC_POS_DATA);

            return
                NativeHelpers.MemCmp(plain, internalBytes, HMAC_POS_DATA, 32) &&
                NativeHelpers.MemCmp(plain, internalBytes, HMAC_POS_TAG, 32);
        }

        public void Pack(byte[] plain, byte[] tag)
        {
            byte[] cipher = new byte[NFC3D_AMIIBO_SIZE];

            // Generate keys
            var tagKeys = GenerateKey(this.tag, plain);
            var dataKeys = GenerateKey(this.data, plain);

            // Init OpenSSL HMAC context
            HMac hmacCtx = new HMac(new Sha256Digest());

            // Generate tag HMAC
            hmacCtx.Init(new KeyParameter(tagKeys.hmacKey));
            hmacCtx.BlockUpdate(plain, 0x1D4, 0x34);
            hmacCtx.DoFinal(cipher, HMAC_POS_TAG);

            // Generate data HMAC
            hmacCtx.Init(new KeyParameter(dataKeys.hmacKey));
            hmacCtx.BlockUpdate(plain, 0x029, 0x18B);           // Data
            hmacCtx.BlockUpdate(cipher, HMAC_POS_TAG, 0x20);    // Tag HMAC
            hmacCtx.BlockUpdate(plain, 0x1D4, 0x34);            // Here be dragons
            hmacCtx.DoFinal(cipher, HMAC_POS_DATA);

            // Encrypt
            dataKeys.Cipher(plain, cipher, true);

            // Convert back to hardware
            InternalToTag(cipher, tag);
        }

        public static Nfc3DAmiiboKeys LoadKeys(string path)
        {
            if (!File.Exists(path))
                return null;

            try
            {
                using (var reader = new BinaryReader(File.OpenRead(path)))
                {
                    var result = Nfc3DAmiiboKeys.Unserialize(reader);

                    if ((result.data.magicBytesSize > 16) || (result.tag.magicBytesSize > 16))
                        return null;

                    return result;
                }
            }
            catch
            {
                return null;
            }
        }

        private byte[] CalcSeed(byte[] dump)
        {
            byte[] key = new byte[Nfc3DKeygenMasterkeys.NFC3D_KEYGEN_SEED_SIZE];
            Array.Copy(dump, 0x029, key, 0x00, 0x02);
            Array.Copy(dump, 0x1D4, key, 0x10, 0x08);
            Array.Copy(dump, 0x1D4, key, 0x18, 0x08);
            Array.Copy(dump, 0x1E8, key, 0x20, 0x20);
            return key;
        }

        private Nfc3DKeygenDerivedkeys GenerateKey(Nfc3DKeygenMasterkeys masterKeys, byte[] dump)
        {
            byte[] seed = CalcSeed(dump);
            return masterKeys.GenerateKey(seed);
        }

        private void TagToInternal(byte[] tag, byte[] intl)
        {
            Array.Copy(tag, 0x008, intl, 0x000, 0x008);
            Array.Copy(tag, 0x080, intl, 0x008, 0x020);
            Array.Copy(tag, 0x010, intl, 0x028, 0x024);
            Array.Copy(tag, 0x0A0, intl, 0x04C, 0x168);
            Array.Copy(tag, 0x034, intl, 0x1B4, 0x020);
            Array.Copy(tag, 0x000, intl, 0x1D4, 0x008);
            Array.Copy(tag, 0x054, intl, 0x1DC, 0x02C);
        }

        private void InternalToTag(byte[] intl, byte[] tag)
        {
            Array.Copy(intl, 0x000, tag, 0x008, 0x008);
            Array.Copy(intl, 0x008, tag, 0x080, 0x020);
            Array.Copy(intl, 0x028, tag, 0x010, 0x024);
            Array.Copy(intl, 0x04C, tag, 0x0A0, 0x168);
            Array.Copy(intl, 0x1B4, tag, 0x034, 0x020);
            Array.Copy(intl, 0x1D4, tag, 0x000, 0x008);
            Array.Copy(intl, 0x1DC, tag, 0x054, 0x02C);
        }
    }
}