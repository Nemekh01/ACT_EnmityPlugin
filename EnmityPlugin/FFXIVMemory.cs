﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Advanced_Combat_Tracker;
using System.Runtime.InteropServices;

namespace Tamagawa.EnmityPlugin
{
    public class FFXIVMemory
    {
        private const string charmapSignature32 = "81feffff0000743581fe58010000732d8b3cb5";
        private const string charmapSignature64 = "48c1e8033dffff0000742b3da80100007324488d0d";
        private const string targetSignature32  = "750e85d2750ab9";
        private const string targetSignature64  = "4883C4205FC3483935285729017520483935";
        private const int charmapOffset32 = 0;
        private const int charmapOffset64 = 0;
        private const int targetOffset32  = 88;
        private const int targetOffset64  = 0;
        private const int hateOffset32    = 19188; // TODO: should be more stable
        private const int hateOffset64    = 25312; // TODO: should be more stable

        private EnmityOverlay _overlay;
        private Process _process;
        private FFXIVClientMode _mode;

        private IntPtr charmapAddress = IntPtr.Zero;
        private IntPtr targetAddress = IntPtr.Zero;
        private IntPtr hateAddress = IntPtr.Zero;

        public FFXIVMemory(EnmityOverlay overlay, Process process)
        {
            _overlay = overlay;
            _process = process;
            if (process.ProcessName == "ffxiv")
            {
                _mode = FFXIVClientMode.FFXIV_32;
            }
            else if (process.ProcessName == "ffxiv_dx11")
            {
                _mode = FFXIVClientMode.FFXIV_64;
            }
            else
            {
                _mode = FFXIVClientMode.Unknown;
            }
            overlay.LogInfo("Attached process: {0} ({1})",
                process.Id, _mode == FFXIVClientMode.FFXIV_32 ? "dx9" : "dx11");
            getPointerAddress();
        }

        public enum FFXIVClientMode
        {
            Unknown = 0,
            FFXIV_32 = 1,
            FFXIV_64 = 2,
        }

        public Process process
        {
            get
            {
                return _process;
            }
        }

        public bool validateProcess()
        {
            if (_process == null)
            {
                return false;
            }
            if (_process.HasExited)
            {
                return false;
            }
            if (charmapAddress == IntPtr.Zero ||
                hateAddress == IntPtr.Zero ||
                targetAddress == IntPtr.Zero)
            {
                return getPointerAddress();
            }
            return true;
        }

        /// <summary>
        /// 各ポインタのアドレスを取得
        /// </summary>
        private bool getPointerAddress()
        {
            bool success = true;
            string charmapSignature = charmapSignature32;
            string targetSignature = targetSignature32;
            int targetOffset = targetOffset32;
            int hateOffset = hateOffset32;
            int charmapOffset = charmapOffset32;
            bool bRIP = false;

            if (_mode == FFXIVClientMode.FFXIV_64)
            {
                bRIP = true;
                hateOffset = hateOffset64;
                targetOffset = targetOffset64;
                charmapOffset = charmapOffset64;
                targetSignature = targetSignature64;
                charmapSignature = charmapSignature64;
            }

            /// CHARMAP
            List<IntPtr> list = SigScan(charmapSignature, 0, bRIP);
            if (list == null || list.Count == 0)
            {
                charmapAddress = IntPtr.Zero;
            }
            if (list.Count == 1)
            {
                charmapAddress = list[0] + charmapOffset;
                hateAddress = charmapAddress + hateOffset;
            }
            if (charmapAddress == IntPtr.Zero)
            {
                _overlay.LogError(Messages.FailedToSigScan, "CombatantList");
                hateAddress = IntPtr.Zero;
                success = false;
            }

            /// TARGET
            list = SigScan(targetSignature, 0, bRIP);
            if (list == null || list.Count == 0)
            {
                targetAddress = IntPtr.Zero;
            }
            if (list.Count == 1)
            {
                targetAddress = list[0] + targetOffset;
            }
            if (targetAddress == IntPtr.Zero)
            {
                _overlay.LogError(Messages.FailedToSigScan, "Target");
                success = false;
            }

            _overlay.LogDebug("charmapAddress: 0x{0:X}, enmityAddress: 0x{1:X}",
                charmapAddress.ToInt64(), hateAddress.ToInt64());
            _overlay.LogDebug("targetAddress: 0x{0:X}", targetAddress.ToInt64());

            if (success)
            {
                Combatant c = GetSelfCombatant();
                if (c != null)
                {
                    _overlay.LogDebug("MyCharacter: '{0}' ({1})", c.Name, c.ID);
                }
            }
            return success;
        }

        /// <summary>
        /// カレントターゲットの情報を取得
        /// </summary>
        public Combatant GetTargetCombatant()
        {
            Combatant target = null;
            IntPtr address = IntPtr.Zero;

            byte[] source = GetByteArray(targetAddress, 128);
            unsafe
            {
                if (_mode == FFXIVClientMode.FFXIV_64)
                {
                    fixed (byte* p = source) address = new IntPtr(*(Int64*)p);
                }
                else
                {
                    fixed (byte* p = source) address = new IntPtr(*(Int32*)p);
                }
            }
            if (address.ToInt64() <= 0)
            {
                return null;
            }

            source = GetByteArray(address, 0x3F40);
            target = GetCombatantFromByteArray(source);
            return target;
        }

        /// <summary>
        /// 自キャラの情報を取得
        /// </summary>
        public Combatant GetSelfCombatant()
        {
            Combatant self = null;
            IntPtr address = (IntPtr)GetUInt32(charmapAddress);
            if (address.ToInt64() > 0) {
                byte[] source = GetByteArray(address, 0x3F40);
                self = GetCombatantFromByteArray(source);
            }
            return self;
        }

        /// <summary>
        /// 周辺のキャラ情報を取得
        /// </summary>
        public unsafe List<Combatant> GetCombatantList()
        {
            int num = 344;
            List<Combatant> result = new List<Combatant>();

            int sz = (_mode == FFXIVClientMode.FFXIV_64) ? 8 : 4;
            byte[] source = GetByteArray(charmapAddress, sz * num);
            if (source == null || source.Length == 0) { return result; }

                for (int i = 0; i < num; i++)
                {
                    IntPtr p;
                    if (_mode == FFXIVClientMode.FFXIV_64)
                    {
                        fixed (byte* bp = source) p = new IntPtr(*(Int64*)&bp[i * sz]);
                    }
                    else
                    {
                        fixed (byte* bp = source) p = new IntPtr(*(Int32*)&bp[i * sz]);
                    }

                    if (!(p == IntPtr.Zero))
                    {
                        byte[] c = GetByteArray(p, 0x3F40);
                        Combatant combatant = GetCombatantFromByteArray(c);
                        if (combatant.type != ObjectType.PC && combatant.type != ObjectType.Monster)
                        {
                            continue;
                        }
                        if (combatant.ID != 0 && combatant.ID != 3758096384u && !result.Exists((Combatant x) => x.ID == combatant.ID))
                        {
                            result.Add(combatant);
                        }
                    }
                }

            return result;
        }

        /// <summary>
        /// メモリのバイト配列からキャラ情報に変換
        /// </summary>
        public unsafe Combatant GetCombatantFromByteArray(byte[] source)
        {
            int offset = 0;
            Combatant combatant = new Combatant();
            fixed (byte* p = source)
            {
                combatant.Name    = GetStringFromBytes(source, 48);
                combatant.ID      = *(uint*)&p[0x74];
                combatant.OwnerID = *(uint*)&p[0x84];
                if (combatant.OwnerID == 3758096384u)
                {
                    combatant.OwnerID = 0u;
                }
                combatant.type = (ObjectType)p[0x8A];
                combatant.EffectiveDistance = p[0x91];

                offset = (_mode == FFXIVClientMode.FFXIV_64) ? 176 : 160;
                combatant.PosX = *(Single*)&p[offset];
                combatant.PosZ = *(Single*)&p[offset + 4];
                combatant.PosY = *(Single*)&p[offset + 8];

                if (combatant.type == ObjectType.PC || combatant.type == ObjectType.Monster)
                {
                    offset = (_mode == FFXIVClientMode.FFXIV_64) ? 5872 : 5312;
                    combatant.Job       = p[offset];
                    combatant.Level     = p[offset + 1];
                    combatant.CurrentHP = *(int*)&p[offset + 8];
                    combatant.MaxHP     = *(int*)&p[offset + 12];
                    combatant.CurrentMP = *(int*)&p[offset + 16];
                    combatant.MaxMP     = *(int*)&p[offset + 20];
                    combatant.CurrentTP = *(short*)&p[offset + 24];
                    combatant.MaxTP     = 1000;
                }
                else
                {
                    combatant.CurrentHP =
                    combatant.MaxHP     =
                    combatant.CurrentMP =
                    combatant.MaxMP     =
                    combatant.MaxTP     =
                    combatant.CurrentTP = 0;
                }
            }
            return combatant;
        }

        /// <summary>
        /// カレントターゲットの敵視情報を取得
        /// </summary>
        public List<EnmityEntry> GetEnmityEntryList()
        {
            List<EnmityEntry> result = new List<EnmityEntry>();
            List<Combatant> combatantList = GetCombatantList();
            Combatant mychar = GetSelfCombatant();

            /// 一度に全部読む
            byte[] buffer = GetByteArray(hateAddress, 16 * 72);
            uint TopEnmity = 0;
            ///
            for (int i = 0; i < 16; i++)
            {
                int p = i * 72;
                uint _id;
                uint _enmity;

                unsafe
                {
                    fixed (byte* bp = buffer)
                    {
                        _id = *(uint*)&bp[p];
                        _enmity = *(uint*)&bp[p + 4];
                    }
                }
                var entry = new EnmityEntry()
                {
                    ID = _id,
                    Enmity = _enmity,
                    isMe = false,
                    Name = "Unknown",
                    Job = 0x00
                };
                if (entry.ID > 0)
                {
                    Combatant c = combatantList.Find(x => x.ID == entry.ID);
                    if (c != null)
                    {
                        entry.Name = c.Name;
                        entry.Job = c.Job;
                        entry.OwnerID = c.OwnerID;
                    }
                    if (entry.ID == mychar.ID)
                    {
                        entry.isMe = true;
                    }
                    if (TopEnmity == 0)
                    {
                        TopEnmity = entry.Enmity;
                    }
                    entry.HateRate = (int)(((double)entry.Enmity / (double)TopEnmity) * 100);
                    result.Add(entry);
                }
                else
                {
                    break; // もう読まない
                }
            }
            return result;
        }

        /// <summary>
        /// バイト配列からUTF-8文字列に変換
        /// </summary>
        private static string GetStringFromBytes(byte[] source, int offset = 0, int size = 256)
        {
            var bytes = new byte[size];
            Array.Copy(source, offset, bytes, 0, size);
            var realSize = 0;
            for (var i = 0; i < size; i++)
            {
                if (bytes[i] != 0)
                {
                    continue;
                }
                realSize = i;
                break;
            }
            Array.Resize(ref bytes, realSize);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        /// <summary>
        /// バッファの長さだけメモリを読み取ってバッファに格納
        /// </summary>
        private bool Peek(IntPtr address, byte[] buffer)
        {
            IntPtr zero = IntPtr.Zero;
            IntPtr nSize = new IntPtr(buffer.Length);
            return NativeMethods.ReadProcessMemory(_process.Handle, address, buffer, nSize, ref zero);
        }

        /// <summary>
        /// メモリから指定された長さだけ読み取りバイト配列として返す
        /// </summary>
        /// <param name="address">読み取る開始アドレス</param>
        /// <param name="length">読み取る長さ</param>
        /// <returns></returns>
        private byte[] GetByteArray(IntPtr address, int length)
        {
            var data = new byte[length];
            Peek(address, data);
            return data;
        }

        /// <summary>
        /// メモリから4バイト読み取り32ビットIntegerとして返す
        /// </summary>
        /// <param name="address">読み取る位置</param>
        /// <param name="offset">オフセット</param>
        /// <returns></returns>
        private unsafe int GetInt32(IntPtr address, int offset = 0)
        {
            int ret;
            var value = new byte[4];
            Peek(IntPtr.Add(address,  offset), value);
            fixed (byte* p = &value[0]) ret = *(int*)p;
            return ret;
        }

        /// <summary>
        /// </summary>
        /// <param name="address"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        private unsafe uint GetUInt32(IntPtr address, int offset = 0)
        {
            uint ret;
            var value = new byte[4];
            Peek(IntPtr.Add(address, offset), value);
            fixed (byte* p = &value[0]) ret = *(uint*)p;
            return ret;
        }

        /// <summary>
        /// Signature scan.
        /// Read data at address which follow matched with the pattern and return it as a pointer.
        /// </summary>
        /// <param name="pattern">byte pattern signature</param>
        /// <param name="offset">offset to read</param>
        /// <param name="bRIP">x64 rip relative addressing mode if true</param>
        /// <returns>the pointer addresses</returns>
        private List<IntPtr> SigScan(string pattern, int offset = 0, bool bRIP = false)
        {
            IntPtr arg_05_0 = IntPtr.Zero;
            if (pattern == null || pattern.Length % 2 != 0)
            {
                return new List<IntPtr>();
            }

            byte?[] array = new byte?[pattern.Length / 2];
            for (int i = 0; i < pattern.Length / 2; i++)
            {
                string text = pattern.Substring(i * 2, 2);
                if (text == "??")
                {
                    array[i] = null;
                }
                else
                {
                    array[i] = new byte?(Convert.ToByte(text, 16));
                }
            }

            int num = 4096;

            int moduleMemorySize = _process.MainModule.ModuleMemorySize;
            IntPtr baseAddress = _process.MainModule.BaseAddress;
            IntPtr intPtr = IntPtr.Add(baseAddress, moduleMemorySize);
            IntPtr intPtr2 = baseAddress;
            byte[] array2 = new byte[num];
            List<IntPtr> list = new List<IntPtr>();
            while (intPtr2.ToInt64() < intPtr.ToInt64())
            {
                IntPtr zero = IntPtr.Zero;
                IntPtr nSize = new IntPtr(num);
                if (IntPtr.Add(intPtr2, num).ToInt64() > intPtr.ToInt64())
                {
                    nSize = (IntPtr)(intPtr.ToInt64() - intPtr2.ToInt64());
                }
                if (NativeMethods.ReadProcessMemory(_process.Handle, intPtr2, array2, nSize, ref zero))
                {
                    int num2 = 0;
                    while ((long)num2 < zero.ToInt64() - (long)array.Length - (long)offset - 4L + 1L)
                    {
                        int num3 = 0;
                        for (int j = 0; j < array.Length; j++)
                        {
                            if (!array[j].HasValue)
                            {
                                num3++;
                            }
                            else
                            {
                                if (array[j].Value != array2[num2 + j])
                                {
                                    break;
                                }
                                num3++;
                            }
                        }
                        if (num3 == array.Length)
                        {
                            IntPtr item;
                            if (bRIP)
                            {
                                item = new IntPtr(BitConverter.ToInt32(array2, num2 + array.Length + offset));
                                item = new IntPtr(intPtr2.ToInt64() + (long)num2 + (long)array.Length + 4L + item.ToInt64());
                            }
                            else if (_mode == FFXIVClientMode.FFXIV_64)
                            {
                                item = new IntPtr(BitConverter.ToInt64(array2, num2 + array.Length + offset));
                                item = new IntPtr(item.ToInt64());
                            }
                            else
                            {
                                item = new IntPtr(BitConverter.ToInt32(array2, num2 + array.Length + offset));
                                item = new IntPtr(item.ToInt64());
                            }
                            list.Add(item);
                        }
                        num2++;
                    }
                }
                intPtr2 = IntPtr.Add(intPtr2, num);
            }
            return list;
        }
    }
}