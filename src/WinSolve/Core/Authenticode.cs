using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace WinSolve.Core;

/// <summary>Authenticode verification (WinVerifyTrust) plus an exact signer name check.</summary>
public static class Authenticode
{
    /// <summary>
    /// True when the file has a valid, trusted Authenticode signature and the signing
    /// certificate's organization (O=) or common name (CN=) exactly matches one of the names.
    /// </summary>
    public static bool IsSignedBy(string file, IEnumerable<string> allowedSigners, out string? signer)
    {
        signer = null;
        try
        {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile is the supported way to read the Authenticode signer
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(file));
#pragma warning restore SYSLIB0057
            var name = cert.GetNameInfo(X509NameType.SimpleName, false);
            var org = SubjectField(cert.Subject, "O");
            signer = name;
            if (!Verify(file)) return false;
            return allowedSigners.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)
                                           || string.Equals(a, org, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static string? SubjectField(string subject, string key)
    {
        // Subject looks like: CN="NVIDIA Corporation", O="NVIDIA Corporation", L=Santa Clara, ...
        foreach (var part in SplitDn(subject))
        {
            var idx = part.IndexOf('=');
            if (idx > 0 && part[..idx].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return part[(idx + 1)..].Trim().Trim('"');
        }
        return null;
    }

    private static IEnumerable<string> SplitDn(string dn)
    {
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var ch in dn)
        {
            if (ch == '"') quoted = !quoted;
            if (ch == ',' && !quoted)
            {
                yield return current.ToString();
                current.Clear();
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0) yield return current.ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WINTRUST_DATA data);

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private static bool Verify(string file)
    {
        var info = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = file };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(info, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,              // WTD_UI_NONE
                fdwRevocationChecks = 1,     // WTD_REVOKE_WHOLECHAIN
                dwUnionChoice = 1,           // WTD_CHOICE_FILE
                pFile = pFile,
                dwProvFlags = 0x00000080,    // WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT
            };
            return WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }
}
