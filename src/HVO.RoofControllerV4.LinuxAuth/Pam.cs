using System.Runtime.InteropServices;

namespace HVO.RoofControllerV4.LinuxAuth;

/// <summary>Linux-PAM authentication/account adapter. It opens no session and never changes a password.</summary>
internal static class Pam
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Conversation(int count, IntPtr messages, out IntPtr responses, IntPtr context);
    [StructLayout(LayoutKind.Sequential)] private struct ConversationData { public Conversation Callback; public IntPtr Context; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public int Style; public IntPtr Text; }
    [StructLayout(LayoutKind.Sequential)] private struct Response { public IntPtr Text; public int Code; }
    [DllImport("libpam.so.0", CallingConvention = CallingConvention.Cdecl)] private static extern int pam_start(string service, string user, ref ConversationData conversation, out IntPtr handle);
    [DllImport("libpam.so.0", CallingConvention = CallingConvention.Cdecl)] private static extern int pam_authenticate(IntPtr handle, int flags);
    [DllImport("libpam.so.0", CallingConvention = CallingConvention.Cdecl)] private static extern int pam_acct_mgmt(IntPtr handle, int flags);
    [DllImport("libpam.so.0", CallingConvention = CallingConvention.Cdecl)] private static extern int pam_end(IntPtr handle, int status);
    [DllImport("libc", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr calloc(nuint count, nuint size);
    [DllImport("libc", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr strdup([MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport("libc", CallingConvention = CallingConvention.Cdecl)] private static extern void free(IntPtr value);

    internal static bool Check(string service, string user, string? password, bool authenticate)
    {
        IntPtr ConversationResponses(int count, IntPtr messages)
        {
            if (count is < 1 or > 32) return IntPtr.Zero;
            var responseSize = Marshal.SizeOf<Response>();
            var result = calloc((nuint)count, (nuint)responseSize);
            if (result == IntPtr.Zero) return result;
            try
            {
                for (var index = 0; index < count; index++)
                {
                    var message = Marshal.PtrToStructure<Message>(Marshal.ReadIntPtr(messages, index * IntPtr.Size));
                    IntPtr text = IntPtr.Zero;
                    if (message.Style == 1 && authenticate && password is not null)
                    {
                        text = strdup(password);
                        if (text == IntPtr.Zero) throw new InvalidOperationException();
                    }
                    else if (message.Style is not (3 or 4)) throw new InvalidOperationException();
                    Marshal.StructureToPtr(new Response { Text = text, Code = 0 }, result + index * responseSize, false);
                }
                return result;
            }
            catch
            {
                for (var index = 0; index < count; index++) free(Marshal.ReadIntPtr(result, index * responseSize));
                free(result);
                return IntPtr.Zero;
            }
        }
        Conversation callback = (int count, IntPtr messages, out IntPtr responses, IntPtr _) =>
        {
            responses = ConversationResponses(count, messages);
            return responses == IntPtr.Zero ? 19 : 0; // PAM_CONV_ERR
        };
        var conversation = new ConversationData { Callback = callback };
        var status = pam_start(service, user, ref conversation, out var handle);
        if (status != 0) return false;
        try
        {
            if (authenticate) status = pam_authenticate(handle, 0x8000); // PAM_DISALLOW_NULL_AUTHTOK
            if (status == 0) status = pam_acct_mgmt(handle, 0);
            return status == 0;
        }
        finally
        {
            pam_end(handle, status);
            GC.KeepAlive(callback);
        }
    }
}
