using System.Runtime.InteropServices;

namespace CloudOrc.Agent.Updater;

/// <summary>
/// Verifies the installer's Authenticode signature with WinVerifyTrust
/// (WINTRUST_ACTION_GENERIC_VERIFY_V2), no UI, whole-chain revocation checking (excluding the
/// root). Any non-zero trust result - unsigned, tampered, untrusted root, revoked, or a
/// revocation check that could not complete - is a failure: the updater fails closed.
/// </summary>
public sealed class AuthenticodeVerifier : IAuthenticodeVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeWholeChain = 1;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdRevocationCheckChainExcludeRoot = 0x00000080;

    public VerificationResult Verify(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return VerificationResult.Invalid($"Installer not found at '{filePath}'.");
        }

        var pathPtr = Marshal.StringToCoTaskMemUni(filePath);
        var fileInfoPtr = IntPtr.Zero;
        try
        {
            var fileInfo = new WintrustFileInfo
            {
                CbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(),
                PcwszFilePath = pathPtr,
                HFile = IntPtr.Zero,
                PgKnownSubject = IntPtr.Zero
            };
            fileInfoPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WintrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

            var data = new WintrustData
            {
                CbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                DwUiChoice = WtdUiNone,
                FdwRevocationChecks = WtdRevokeWholeChain,
                DwUnionChoice = WtdChoiceFile,
                PFile = fileInfoPtr,
                DwStateAction = WtdStateActionVerify,
                DwProvFlags = WtdRevocationCheckChainExcludeRoot
            };

            var action = GenericVerifyV2;
            int result;
            try
            {
                result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            }
            finally
            {
                data.DwStateAction = WtdStateActionClose;
                _ = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            }

            return result == 0
                ? VerificationResult.Valid()
                : VerificationResult.Invalid($"Authenticode verification failed: {Describe(result)}.");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            return VerificationResult.Invalid($"Authenticode verification could not run: {ex.Message}");
        }
        finally
        {
            if (fileInfoPtr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(fileInfoPtr);
            }

            Marshal.FreeCoTaskMem(pathPtr);
        }
    }

    internal static string Describe(int hresult) => unchecked((uint)hresult) switch
    {
        0x800B0100 => "file is not signed (TRUST_E_NOSIGNATURE)",
        0x80096010 => "signature does not match file contents (TRUST_E_BAD_DIGEST)",
        0x800B0109 => "certificate chain ends in an untrusted root (CERT_E_UNTRUSTEDROOT)",
        0x800B010C => "signing certificate was revoked (CERT_E_REVOKED)",
        0x80092012 => "revocation status could not be checked (CRYPT_E_NO_REVOCATION_CHECK)",
        0x80092013 => "revocation server offline (CRYPT_E_REVOCATION_OFFLINE)",
        0x800B0101 => "certificate expired (CERT_E_EXPIRED)",
        0x800B0111 => "publisher explicitly distrusted (TRUST_E_EXPLICIT_DISTRUST)",
        0x800B0004 => "subject not trusted (TRUST_E_SUBJECT_NOT_TRUSTED)",
        var code => $"0x{code:X8}"
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint CbStruct;
        public IntPtr PcwszFilePath;
        public IntPtr HFile;
        public IntPtr PgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint CbStruct;
        public IntPtr PPolicyCallbackData;
        public IntPtr PSipClientData;
        public uint DwUiChoice;
        public uint FdwRevocationChecks;
        public uint DwUnionChoice;
        public IntPtr PFile;
        public uint DwStateAction;
        public IntPtr HWvtStateData;
        public IntPtr PwszUrlReference;
        public uint DwProvFlags;
        public uint DwUiContext;
        public IntPtr PSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionId, ref WintrustData pWvtData);
}
