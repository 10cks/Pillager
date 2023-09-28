﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Pillager.Helper
{
    internal class LockedFile
    {
        public static byte[] ReadLockedFile(string fileName)
        {
            try
            {
                int pid = GetProcessIDByFileName(fileName)[0];
                IntPtr hfile = DuplicateHandleByFileName(pid, fileName);
                uint read;
                int oldFilePointer = 0;
                oldFilePointer = Native.SetFilePointer(hfile, 0, 0, 1);
                int size = Native.SetFilePointer(hfile, 0, 0, 2);
                byte[] fileBuffer = new byte[size];
                IntPtr hProcess = Native.OpenProcess(Native.PROCESS_ACCESS_FLAGS.PROCESS_SUSPEND_RESUME, false, pid);
                Native.NtSuspendProcess(hProcess);
                Native.SetFilePointer(hfile, 0, 0, 0);
                Native.ReadFile(hfile, fileBuffer, (uint)size, out read, IntPtr.Zero);
                Native.SetFilePointer(hfile, oldFilePointer, 0, 0);
                Native.CloseHandle(hfile);
                Native.NtResumeProcess(hProcess);
                Native.CloseHandle(hProcess);
                return fileBuffer;
            }
            catch { return null; }
        }

        public static List<Native.SYSTEM_HANDLE_INFORMATION> GetHandles(int pid)
        {
            List<Native.SYSTEM_HANDLE_INFORMATION> aHandles = new List<Native.SYSTEM_HANDLE_INFORMATION>();
            int handle_info_size = Marshal.SizeOf(new Native.SYSTEM_HANDLE_INFORMATION()) * 20000;
            IntPtr ptrHandleData = IntPtr.Zero;
            try
            {
                ptrHandleData = Marshal.AllocHGlobal(handle_info_size);
                int nLength = 0;

                while (Native.NtQuerySystemInformation(Native.CNST_SYSTEM_HANDLE_INFORMATION, ptrHandleData, handle_info_size, ref nLength) == Native.STATUS_INFO_LENGTH_MISMATCH)
                {
                    handle_info_size = nLength;
                    Marshal.FreeHGlobal(ptrHandleData);
                    ptrHandleData = Marshal.AllocHGlobal(nLength);
                }

                long handle_count = Marshal.ReadIntPtr(ptrHandleData).ToInt64();
                IntPtr ptrHandleItem = new IntPtr(ptrHandleData.ToInt32() + Marshal.SizeOf(ptrHandleData));

                for (long lIndex = 0; lIndex < handle_count; lIndex++)
                {
                    Native.SYSTEM_HANDLE_INFORMATION oSystemHandleInfo = new Native.SYSTEM_HANDLE_INFORMATION();
                    oSystemHandleInfo = (Native.SYSTEM_HANDLE_INFORMATION)Marshal.PtrToStructure(ptrHandleItem, oSystemHandleInfo.GetType());
                    ptrHandleItem = new IntPtr(ptrHandleItem.ToInt32() + Marshal.SizeOf(new Native.SYSTEM_HANDLE_INFORMATION()));
                    if (oSystemHandleInfo.ProcessID != pid) { continue; }
                    aHandles.Add(oSystemHandleInfo);
                }
            }
            catch (Exception ex)
            {
                throw ex;
            }
            finally
            {
                Marshal.FreeHGlobal(ptrHandleData);
            }
            return aHandles;
        }

        private static string TryGetName(IntPtr Handle)
        {
            Native.IO_STATUS_BLOCK status = new Native.IO_STATUS_BLOCK();
            uint bufferSize = 32 * 1024;
            var bufferPtr = Marshal.AllocHGlobal((int)bufferSize);
            Native.NtQueryInformationFile(Handle, ref status, bufferPtr, bufferSize, Native.FILE_INFORMATION_CLASS.FileNameInformation);
            var nameInfo = (Native.FileNameInformation)Marshal.PtrToStructure(bufferPtr, typeof(Native.FileNameInformation));
            return Marshal.PtrToStringUni(new IntPtr(bufferPtr.ToInt32() + 4), nameInfo.NameLength / 2);
        }

        public static IntPtr FindHandleByFileName(Native.SYSTEM_HANDLE_INFORMATION systemHandleInformation, string filename, IntPtr processHandle)
        {
            IntPtr ipHandle = IntPtr.Zero;
            IntPtr openProcessHandle = processHandle;
            IntPtr hObjectBasicInfo = IntPtr.Zero;
            try
            {
                if (!Native.DuplicateHandle(openProcessHandle, new IntPtr(systemHandleInformation.Handle), Native.GetCurrentProcess(), out ipHandle, 0, false, Native.DUPLICATE_SAME_ACCESS))
                {
                    return IntPtr.Zero;
                }
                int objectTypeInfoSize = 0x1000;
                IntPtr objectTypeInfo = Marshal.AllocHGlobal(objectTypeInfoSize);
                try
                {
                    int returnLength = 0;
                    if (Native.NtQueryObject(ipHandle, (int)Native.OBJECT_INFORMATION_CLASS.ObjectTypeInformation, objectTypeInfo, objectTypeInfoSize, ref returnLength) != 0)
                    {
                        return IntPtr.Zero;
                    }
                    var objectTypeInfoStruct = (Native.OBJECT_TYPE_INFORMATION)Marshal.PtrToStructure(objectTypeInfo, typeof(Native.OBJECT_TYPE_INFORMATION));
                    string typeName = objectTypeInfoStruct.Name.ToString();
                    if (typeName == "File")
                    {
                        string name = TryGetName(ipHandle);
                        if (name == filename.Substring(2, filename.Length - 2))
                            return ipHandle;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(objectTypeInfo);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
            return IntPtr.Zero;
        }

        private static IntPtr DuplicateHandleByFileName(int pid, string fileName)
        {
            IntPtr handle = IntPtr.Zero;
            List<Native.SYSTEM_HANDLE_INFORMATION> syshInfos = GetHandles(pid);

            IntPtr processHandle = GetProcessHandle(pid);

            for (int i = 0; i < syshInfos.Count; i++)
            {
                handle = FindHandleByFileName(syshInfos[i], fileName, processHandle);
                if (handle != IntPtr.Zero)
                {
                    Native.CloseHandle(processHandle);
                    return handle;
                }
            }
            Native.CloseHandle(processHandle);
            return handle;
        }

        private static List<int> GetProcessIDByFileName(string path)
        {
            List<int> result = new List<int>();
            var bufferPtr = IntPtr.Zero;
            var statusBlock = new Native.IO_STATUS_BLOCK();

            try
            {
                var handle = GetFileHandle(path);
                uint bufferSize = 0x4000;
                bufferPtr = Marshal.AllocHGlobal((int)bufferSize);

                uint status;
                while ((status = Native.NtQueryInformationFile(handle,
                    ref statusBlock, bufferPtr, bufferSize,
                    Native.FILE_INFORMATION_CLASS.FileProcessIdsUsingFileInformation))
                    == Native.STATUS_INFO_LENGTH_MISMATCH)
                {
                    Marshal.FreeHGlobal(bufferPtr);
                    bufferPtr = IntPtr.Zero;
                    bufferSize *= 2;
                    bufferPtr = Marshal.AllocHGlobal((int)bufferSize);
                }

                Native.CloseHandle(handle);

                if (status != Native.STATUS_SUCCESS)
                {
                    return result;
                }

                IntPtr readBuffer = bufferPtr;
                int numEntries = Marshal.ReadInt32(readBuffer); // NumberOfProcessIdsInList
                readBuffer = new IntPtr(readBuffer.ToInt32() + IntPtr.Size);

                for (int i = 0; i < numEntries; i++)
                {
                    int processId = Marshal.ReadIntPtr(readBuffer).ToInt32(); // A single ProcessIdList[] element
                    result.Add(processId);
                    readBuffer = new IntPtr(readBuffer.ToInt32() + IntPtr.Size);
                }
            }
            catch { return result; }
            finally
            {
                if (bufferPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(bufferPtr);
                }
            }
            return result;
        }

        private static IntPtr GetFileHandle(string name)
        {
            return Native.CreateFile(name,
                0,
                FileShare.Read | FileShare.Write | FileShare.Delete,
                IntPtr.Zero,
                FileMode.Open,
                (int)FileAttributes.Normal,
                IntPtr.Zero);
        }

        private static IntPtr GetProcessHandle(int pid)
        {
            return Native.OpenProcess(Native.PROCESS_ACCESS_FLAGS.PROCESS_DUP_HANDLE | Native.PROCESS_ACCESS_FLAGS.PROCESS_VM_READ, false, pid);
        }
    }
}
