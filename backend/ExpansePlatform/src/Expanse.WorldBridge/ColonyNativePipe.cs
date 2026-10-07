using System;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Expanse.WorldBridge
{
    // Unity's bundled Mono maps managed PipeOptions.Asynchronous into the
    // PIPE_NOWAIT mode, and ConnectNamedPipe(null OVERLAPPED) then fails. Create
    // a synchronous Windows pipe directly with a protected current-token ACL.
    // This avoids Mono's incomplete managed principal/ACL implementation.
    internal static class ColonyNativePipe
    {
        [StructLayout(LayoutKind.Sequential)]struct SecurityAttributes {public int Length;public IntPtr Descriptor;public int Inherit;}
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr CreateNamedPipeW(string name,uint openMode,uint pipeMode,uint maxInstances,uint outBuffer,uint inBuffer,uint timeout,ref SecurityAttributes attributes);
        [DllImport("kernel32.dll",SetLastError=true)]static extern bool ConnectNamedPipe(SafePipeHandle pipe,IntPtr overlapped);
        [DllImport("kernel32.dll",SetLastError=true)]static extern bool CancelIoEx(SafePipeHandle pipe,IntPtr overlapped);
        [DllImport("kernel32.dll")]static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")]static extern IntPtr LocalFree(IntPtr memory);
        [DllImport("kernel32.dll",SetLastError=true)]static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll",SetLastError=true)]static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
        [DllImport("advapi32.dll",SetLastError=true)]static extern bool GetTokenInformation(IntPtr token,int informationClass,IntPtr buffer,int size,out int needed);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool ConvertSidToStringSidW(IntPtr sid,out IntPtr text);
        [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text,uint revision,out IntPtr descriptor,out uint size);
        internal static NamedPipeServerStream Create(string name,bool firstInstance)
        {
            if(Environment.OSVersion.Platform!=PlatformID.Win32NT)throw new PlatformNotSupportedException("Colony management requires the qualified Windows local pipe provider.");
            string sid=CurrentUserSid();IntPtr descriptor;uint size;
            if(!ConvertStringSecurityDescriptorToSecurityDescriptorW("D:P(D;;GA;;;NU)(A;;GA;;;"+sid+")",1,out descriptor,out size))throw Error("Creating protected current-user pipe security");
            try
            {
                var attributes=new SecurityAttributes {Length=Marshal.SizeOf(typeof(SecurityAttributes)),Descriptor=descriptor,Inherit=0};
                // Duplex, byte framing, blocking native I/O; reject remote clients
                // at the kernel in addition to denying NETWORK SID in the ACL.
                IntPtr handle=CreateNamedPipeW("\\\\.\\pipe\\"+name,3u|(firstInstance ? 0x00080000u : 0u),8,4,4096,4096,0,ref attributes);
                if(handle==new IntPtr(-1))throw Error("Creating local colony pipe");
                var safe=new SafePipeHandle(handle,true);
                try {return new NamedPipeServerStream(PipeDirection.InOut,false,true,safe);}catch{safe.Dispose();throw;}
            }
            finally {LocalFree(descriptor);}
        }
        internal static void WaitForConnection(NamedPipeServerStream pipe)
        {
            if(ConnectNamedPipe(pipe.SafePipeHandle,IntPtr.Zero))return;
            int error=Marshal.GetLastWin32Error();if(error==535)return; // client connected between Create and Connect
            throw new Win32Exception(error,"Connecting local colony pipe");
        }
        internal static void Cancel(NamedPipeServerStream pipe)
        {
            try {if(pipe!=null && !pipe.SafePipeHandle.IsClosed)CancelIoEx(pipe.SafePipeHandle,IntPtr.Zero);}catch(ObjectDisposedException){}
        }
        static string CurrentUserSid()
        {
            IntPtr token;if(!OpenProcessToken(GetCurrentProcess(),8,out token))throw Error("Opening current process token");
            IntPtr buffer=IntPtr.Zero,text=IntPtr.Zero;
            try
            {
                int needed;GetTokenInformation(token,1,IntPtr.Zero,0,out needed);
                if(needed<Marshal.SizeOf(typeof(IntPtr)) || needed>65536)throw Error("Reading bounded process user SID");
                buffer=Marshal.AllocHGlobal(needed);
                if(!GetTokenInformation(token,1,buffer,needed,out needed) || !ConvertSidToStringSidW(Marshal.ReadIntPtr(buffer),out text))throw Error("Reading current process user SID");
                string sid=Marshal.PtrToStringUni(text);if(string.IsNullOrEmpty(sid) || !sid.StartsWith("S-1-",StringComparison.Ordinal) || sid.Length>256)throw new InvalidOperationException("Current process SID is invalid.");return sid;
            }
            finally {if(text!=IntPtr.Zero)LocalFree(text);if(buffer!=IntPtr.Zero)Marshal.FreeHGlobal(buffer);CloseHandle(token);}
        }
        static Win32Exception Error(string action) => new Win32Exception(Marshal.GetLastWin32Error(),action);
    }
}
