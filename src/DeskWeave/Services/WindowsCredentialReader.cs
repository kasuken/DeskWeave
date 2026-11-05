using System.Runtime.InteropServices;
using System.Text;
using static DeskWeave.Interop.NativeMethods;

namespace DeskWeave.Services;

/// <summary>Reads a generic secret from Windows Credential Manager. The value is returned to the caller and never stored.</summary>
internal static class WindowsCredentialReader
{
    public const string AntigravityTarget = "gemini:antigravity";

    public static string? ReadGeneric(string target)
    {
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var pointer))
        {
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return null;
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            var secret = Encoding.UTF8.GetString(bytes);
            Array.Clear(bytes);
            return secret;
        }
        finally
        {
            CredFree(pointer);
        }
    }
}
