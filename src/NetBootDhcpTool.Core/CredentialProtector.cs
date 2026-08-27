using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace NetBootDhcpTool.Core;

/// <summary>
/// Protects local favorite credentials with Windows DPAPI. The data is bound to
/// the current Windows user and cannot be treated as a portable export format.
/// </summary>
public static class CredentialProtector
{
    private const int CryptprotectUiForbidden = 0x1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NetBootDhcpTool.FavoritePassword.v1");

    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        EnsureWindows();

        var inputBytes = Encoding.UTF8.GetBytes(plaintext);
        var input = CreateBlob(inputBytes);
        var entropy = CreateBlob(Entropy);
        try
        {
            if (!CryptProtectData(ref input, "NetBootDhcpTool favorite credential", ref entropy, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out var output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows DPAPI protection failed");
            }

            try
            {
                return Convert.ToBase64String(CopyBlob(output));
            }
            finally
            {
                FreeBlob(output);
            }
        }
        finally
        {
            FreeBlob(input);
            FreeBlob(entropy);
        }
    }

    public static bool TryUnprotect(string ciphertext, out string plaintext)
    {
        plaintext = "";
        if (string.IsNullOrWhiteSpace(ciphertext) || !OperatingSystem.IsWindows()) return false;

        byte[] protectedBytes;
        try
        {
            protectedBytes = Convert.FromBase64String(ciphertext);
        }
        catch (FormatException)
        {
            return false;
        }

        var input = CreateBlob(protectedBytes);
        var entropy = CreateBlob(Entropy);
        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CryptprotectUiForbidden, out var output)) return false;
            try
            {
                plaintext = Encoding.UTF8.GetString(CopyBlob(output));
                return true;
            }
            finally
            {
                FreeBlob(output);
            }
        }
        catch (Win32Exception)
        {
            return false;
        }
        finally
        {
            FreeBlob(input);
            FreeBlob(entropy);
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Favorite credential protection requires Windows DPAPI");
    }

    private static DataBlob CreateBlob(byte[] bytes)
    {
        var pointer = Marshal.AllocHGlobal(bytes.Length == 0 ? 1 : bytes.Length);
        if (bytes.Length > 0) Marshal.Copy(bytes, 0, pointer, bytes.Length);
        return new DataBlob { Size = bytes.Length, Data = pointer };
    }

    private static byte[] CopyBlob(DataBlob blob)
    {
        if (blob.Size <= 0 || blob.Data == IntPtr.Zero) return [];
        var bytes = new byte[blob.Size];
        Marshal.Copy(blob.Data, bytes, 0, blob.Size);
        return bytes;
    }

    private static void FreeBlob(DataBlob blob)
    {
        if (blob.Data != IntPtr.Zero) Marshal.FreeHGlobal(blob.Data);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? dataDescription,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr dataDescription,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);
}
