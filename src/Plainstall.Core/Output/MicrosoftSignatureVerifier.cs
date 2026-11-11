using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;

namespace Plainstall.Core.Output;

/// <summary>Outcome of a signature check on a file.</summary>
/// <param name="IsTrusted">True when the file carries a valid signature by Microsoft.</param>
/// <param name="Signer">Subject of the signing certificate when a valid signature was found, else null.</param>
/// <param name="ViaCatalog">True when the signature came from a Windows security catalog instead of the file itself.</param>
/// <param name="Detail">One sentence for the log: who signed it, or why it was not accepted.</param>
public sealed record SignatureCheckResult(bool IsTrusted, string? Signer, bool ViaCatalog, string Detail);

/// <summary>Decides whether a program file may be run because Microsoft signed it.</summary>
public interface IFileSignatureVerifier
{
    SignatureCheckResult Verify(string path);
}

/// <summary>
///     Checks a file's Authenticode signature with <c>WinVerifyTrust</c> and accepts it only when the
///     certificate chain ends in a Microsoft root and the signing certificate belongs to Microsoft Corporation.
/// </summary>
/// <remarks>
///     <para>
///         The signature inside the file is tried first, then the Windows security catalogs: many tools of
///         the Windows ADK and of Windows itself carry no signature of their own and are signed through a
///         catalog instead.
///     </para>
///     <para>
///         The check shows no UI and fetches nothing from the network (no revocation lookup, cached
///         certificates only), so it behaves the same offline and never stalls a build.
///     </para>
/// </remarks>
public sealed partial class MicrosoftSignatureVerifier : IFileSignatureVerifier
{
    private const string MicrosoftOrganization = "Microsoft Corporation";
    private const string OrganizationOid = "2.5.4.10";

    private const uint UiNone = 2;
    private const uint RevokeNone = 0;
    private const uint ChoiceFile = 1;
    private const uint ChoiceCatalog = 2;
    private const uint StateActionVerify = 1;
    private const uint StateActionClose = 2;
    private const uint ProvFlagRevocationCheckNone = 0x00000010;
    private const uint ProvFlagCacheOnlyUrlRetrieval = 0x00001000;
    private const int TrustENoSignature = unchecked((int)0x800B0100);

    private const uint MicrosoftRootCheckApplicationRootFlag = 0x00020000;

    private static readonly IntPtr InvalidHandleValue = new(-1);

    /// <summary>WINTRUST_ACTION_GENERIC_VERIFY_V2: the standard Authenticode policy.</summary>
    private static readonly Guid ActionGenericVerifyV2 = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    /// <summary>CERT_CHAIN_POLICY_MICROSOFT_ROOT.</summary>
    private static readonly IntPtr CertChainPolicyMicrosoftRoot = new(7);

    public SignatureCheckResult Verify(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path)) return new SignatureCheckResult(false, null, false, "The file does not exist.");

        try
        {
            var embedded = VerifyEmbedded(path);
            if (embedded.IsTrusted) return embedded;
            var catalog = VerifyThroughCatalog(path);
            if (catalog is { IsTrusted: true }) return catalog;
            // When the file has a signature of its own, that failure is the more useful one to report.
            return catalog is null || embedded.Signer is not null ? embedded : catalog;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException
                                       or DllNotFoundException or EntryPointNotFoundException)
        {
            return new SignatureCheckResult(false, null, false, $"The signature could not be checked: {ex.Message}");
        }
    }

    private static SignatureCheckResult VerifyEmbedded(string path)
    {
        var pathPointer = Marshal.StringToHGlobalUni(path);
        var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            var fileInfo = new WinTrustFileInfo
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                pcwszFilePath = pathPointer
            };
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            return RunWinVerifyTrust(ChoiceFile, fileInfoPointer, false);
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfoPointer);
            Marshal.FreeHGlobal(pathPointer);
        }
    }

    /// <summary>Looks the file's hash up in the system catalogs. Null when no catalog lists it.</summary>
    private static SignatureCheckResult? VerifyThroughCatalog(string path)
    {
        // The defaults open the file for reading and let others read it too.
        using var file = File.OpenHandle(path);

        // Current catalogs list SHA-256 hashes, older ones SHA-1 (a null algorithm name selects SHA-1).
        foreach (var algorithm in new[] { "SHA256", null })
        {
            if (!CryptCatAdminAcquireContext2(out var catAdmin, IntPtr.Zero, algorithm, IntPtr.Zero, 0)) continue;
            try
            {
                uint hashLength = 0;
                CryptCatAdminCalcHashFromFileHandle2(catAdmin, file, ref hashLength, null, 0);
                if (hashLength is 0 or > 64) continue;
                var hash = new byte[hashLength];
                if (!CryptCatAdminCalcHashFromFileHandle2(catAdmin, file, ref hashLength, hash, 0)) continue;

                var catalogContext = CryptCatAdminEnumCatalogFromHash(catAdmin, hash, hashLength, 0, IntPtr.Zero);
                if (catalogContext == IntPtr.Zero) continue;
                try
                {
                    return VerifyCatalogMember(path, file, catAdmin, catalogContext, hash.AsSpan(0, (int)hashLength));
                }
                finally
                {
                    CryptCatAdminReleaseCatalogContext(catAdmin, catalogContext, 0);
                }
            }
            finally
            {
                CryptCatAdminReleaseContext(catAdmin, 0);
            }
        }

        return null;
    }

    private static unsafe SignatureCheckResult VerifyCatalogMember(string path, SafeFileHandle file, IntPtr catAdmin,
        IntPtr catalogContext, ReadOnlySpan<byte> hash)
    {
        var catalogInfo = new CatalogInfo { cbStruct = (uint)sizeof(CatalogInfo) };
        if (!CryptCatCatalogInfoFromContext(catalogContext, ref catalogInfo, 0))
            return new SignatureCheckResult(false, null, true,
                "The security catalog that lists the file could not be opened.");
        var catalogPath = new string(catalogInfo.wszCatalogFile);

        var catalogPathPointer = Marshal.StringToHGlobalUni(catalogPath);
        var memberTagPointer = Marshal.StringToHGlobalUni(Convert.ToHexString(hash));
        var memberPathPointer = Marshal.StringToHGlobalUni(path);
        var hashPointer = Marshal.AllocHGlobal(hash.Length);
        var infoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustCatalogInfo>());
        try
        {
            hash.CopyTo(new Span<byte>((void*)hashPointer, hash.Length));
            var info = new WinTrustCatalogInfo
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustCatalogInfo>(),
                pcwszCatalogFilePath = catalogPathPointer,
                pcwszMemberTag = memberTagPointer,
                pcwszMemberFilePath = memberPathPointer,
                hMemberFile = file.DangerousGetHandle(),
                pbCalculatedFileHash = hashPointer,
                cbCalculatedFileHash = (uint)hash.Length,
                hCatAdmin = catAdmin
            };
            Marshal.StructureToPtr(info, infoPointer, false);
            return RunWinVerifyTrust(ChoiceCatalog, infoPointer, true);
        }
        finally
        {
            Marshal.FreeHGlobal(infoPointer);
            Marshal.FreeHGlobal(hashPointer);
            Marshal.FreeHGlobal(memberPathPointer);
            Marshal.FreeHGlobal(memberTagPointer);
            Marshal.FreeHGlobal(catalogPathPointer);
        }
    }

    private static SignatureCheckResult RunWinVerifyTrust(uint unionChoice, IntPtr subjectInfo, bool viaCatalog)
    {
        var data = new WinTrustData
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
            dwUIChoice = UiNone,
            fdwRevocationChecks = RevokeNone,
            dwUnionChoice = unionChoice,
            pInfo = subjectInfo,
            dwStateAction = StateActionVerify,
            dwProvFlags = ProvFlagRevocationCheckNone | ProvFlagCacheOnlyUrlRetrieval
        };

        var action = ActionGenericVerifyV2;
        var status = WinVerifyTrust(InvalidHandleValue, in action, ref data);
        try
        {
            if (status == 0) return CheckSignerIsMicrosoft(data.hWVTStateData, viaCatalog);
            var reason = status == TrustENoSignature
                ? "The file has no signature."
                : $"The signature is not valid (0x{status:X8}: {new Win32Exception(status).Message}).";
            return new SignatureCheckResult(false, null, viaCatalog, reason);
        }
        finally
        {
            // The state holds the certificate chain; it must be released with a second call.
            data.dwStateAction = StateActionClose;
            WinVerifyTrust(InvalidHandleValue, in action, ref data);
        }
    }

    /// <summary>
    ///     A valid signature only proves that someone with a trusted certificate signed the file. This
    ///     asks the two further questions: does the chain end in a Microsoft root, and is the signer Microsoft itself?
    /// </summary>
    private static SignatureCheckResult CheckSignerIsMicrosoft(IntPtr stateData, bool viaCatalog)
    {
        var providerData = stateData == IntPtr.Zero ? IntPtr.Zero : WtHelperProvDataFromStateData(stateData);
        var signerPointer = providerData == IntPtr.Zero
            ? IntPtr.Zero
            : WtHelperGetProvSignerFromChain(providerData, 0, false, 0);
        if (signerPointer == IntPtr.Zero)
            return new SignatureCheckResult(false, null, viaCatalog,
                "The signature is valid, but its signer could not be read.");

        var signer = Marshal.PtrToStructure<CryptProviderSigner>(signerPointer);
        if (signer.cbStruct < Marshal.SizeOf<CryptProviderSigner>() || signer.pChainContext == IntPtr.Zero ||
            signer.csCertChain == 0 || signer.pasCertChain == IntPtr.Zero)
            return new SignatureCheckResult(false, null, viaCatalog,
                "The signature is valid, but its certificate chain could not be read.");

        // CRYPT_PROVIDER_CERT starts with { DWORD cbStruct; PCCERT_CONTEXT pCert; }, so the pointer sits at offset 8.
        var leafContext = Marshal.ReadIntPtr(signer.pasCertChain, IntPtr.Size);
        if (leafContext == IntPtr.Zero)
            return new SignatureCheckResult(false, null, viaCatalog,
                "The signature is valid, but its signing certificate could not be read.");
        string subject;
        string? organization = null;
        using (var certificate = new X509Certificate2(leafContext))
        {
            subject = certificate.Subject;
            foreach (var part in certificate.SubjectName.EnumerateRelativeDistinguishedNames())
                if (part.GetSingleElementType().Value == OrganizationOid)
                    organization = part.GetSingleElementValue();
        }

        if (!ChainEndsInMicrosoftRoot(signer.pChainContext))
            return new SignatureCheckResult(false, subject, viaCatalog,
                $"The file is signed by '{subject}', and that certificate does not lead back to a Microsoft root.");
        if (!string.Equals(organization, MicrosoftOrganization, StringComparison.Ordinal))
            return new SignatureCheckResult(false, subject, viaCatalog,
                $"The file is signed by '{subject}', not by Microsoft Corporation.");
        return new SignatureCheckResult(true, subject, viaCatalog,
            viaCatalog ? $"Signed by '{subject}' through a Windows security catalog." : $"Signed by '{subject}'.");
    }

    private static bool ChainEndsInMicrosoftRoot(IntPtr chainContext)
    {
        // Windows components chain to the Microsoft product roots; tools such as the ADK chain to the
        // "application" root (Microsoft Root Certificate Authority 2011), which needs its own flag.
        foreach (var flags in new[] { 0u, MicrosoftRootCheckApplicationRootFlag })
        {
            var parameters = new CertChainPolicyPara
                { cbSize = (uint)Marshal.SizeOf<CertChainPolicyPara>(), dwFlags = flags };
            var policyStatus = new CertChainPolicyStatus { cbSize = (uint)Marshal.SizeOf<CertChainPolicyStatus>() };
            if (CertVerifyCertificateChainPolicy(CertChainPolicyMicrosoftRoot, chainContext, ref parameters,
                    ref policyStatus) && policyStatus.dwError == 0) return true;
        }

        return false;
    }

    // ---- Windows API ----

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(IntPtr hwnd, in Guid actionId, ref WinTrustData data);

    [LibraryImport("wintrust.dll", EntryPoint = "WTHelperProvDataFromStateData")]
    private static partial IntPtr WtHelperProvDataFromStateData(IntPtr stateData);

    [LibraryImport("wintrust.dll", EntryPoint = "WTHelperGetProvSignerFromChain")]
    private static partial IntPtr WtHelperGetProvSignerFromChain(IntPtr providerData, uint signerIndex,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

    [LibraryImport("wintrust.dll", EntryPoint = "CryptCATAdminAcquireContext2",
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCatAdminAcquireContext2(out IntPtr catAdmin, IntPtr subsystem,
        string? hashAlgorithm, IntPtr strongHashPolicy, uint flags);

    // The release functions return BOOL. A failed release leaves nothing to undo, so the result is not read.
    [LibraryImport("wintrust.dll", EntryPoint = "CryptCATAdminReleaseContext")]
    private static partial void CryptCatAdminReleaseContext(IntPtr catAdmin, uint flags);

    [LibraryImport("wintrust.dll", EntryPoint = "CryptCATAdminCalcHashFromFileHandle2")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCatAdminCalcHashFromFileHandle2(IntPtr catAdmin, SafeFileHandle file,
        ref uint hashLength, [Out] byte[]? hash, uint flags);

    [LibraryImport("wintrust.dll", EntryPoint = "CryptCATAdminEnumCatalogFromHash")]
    private static partial IntPtr CryptCatAdminEnumCatalogFromHash(IntPtr catAdmin, [In] byte[] hash, uint hashLength,
        uint flags, IntPtr previousCatalogInfo);

    [LibraryImport("wintrust.dll", EntryPoint = "CryptCATCatalogInfoFromContext")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCatCatalogInfoFromContext(IntPtr catalogContext, ref CatalogInfo catalogInfo,
        uint flags);

    [LibraryImport("wintrust.dll", EntryPoint = "CryptCATAdminReleaseCatalogContext")]
    private static partial void CryptCatAdminReleaseCatalogContext(IntPtr catAdmin, IntPtr catalogContext, uint flags);

    [LibraryImport("crypt32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CertVerifyCertificateChainPolicy(IntPtr policyOid, IntPtr chainContext,
        ref CertChainPolicyPara policyPara, ref CertChainPolicyStatus policyStatus);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustCatalogInfo
    {
        public uint cbStruct;
        public uint dwCatalogVersion;
        public IntPtr pcwszCatalogFilePath;
        public IntPtr pcwszMemberTag;
        public IntPtr pcwszMemberFilePath;
        public IntPtr hMemberFile;
        public IntPtr pbCalculatedFileHash;
        public uint cbCalculatedFileHash;
        public IntPtr pcCatalogContext;
        public IntPtr hCatAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pInfo;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    /// <summary>CRYPT_PROVIDER_SGNR. The FILETIME is two 32-bit fields so that it keeps its 4-byte alignment.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CryptProviderSigner
    {
        public uint cbStruct;
        public uint sftVerifyAsOfLow;
        public uint sftVerifyAsOfHigh;
        public uint csCertChain;
        public IntPtr pasCertChain;
        public uint dwSignerType;
        public IntPtr psSigner;
        public uint dwError;
        public uint csCounterSigners;
        public IntPtr pasCounterSigners;
        public IntPtr pChainContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct CatalogInfo
    {
        public uint cbStruct;
        public fixed char wszCatalogFile[260];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CertChainPolicyPara
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr pvExtraPolicyPara;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CertChainPolicyStatus
    {
        public uint cbSize;
        public uint dwError;
        public int lChainIndex;
        public int lElementIndex;
        public IntPtr pvExtraPolicyStatus;
    }
}