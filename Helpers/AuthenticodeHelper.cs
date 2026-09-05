using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace KaliteKit.Helpers
{
    /// <summary>
    /// Authenticode digital signature verification helper.
    /// Validates file integrity, certificate validity, and signer identity for AMD binaries.
    /// </summary>
    public static class AuthenticodeHelper
    {
        #region WinVerifyTrust P/Invoke Definitions

        private const string WINTRUST_ACTION_GENERIC_VERIFY_V2 = "{00AAC56B-CD44-11d0-8CC2-00C04FC295EE}";
        private const uint WTD_CHOICE_FILE = 1;
        private const uint WTD_UI_NONE = 2;
        private const uint WTD_REVOKE_NONE = 0;
        private const uint WTD_STATEACTION_IGNORE = 0;
        private const uint WTD_SAFER_FLAG = 0x00000100;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
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
            [MarshalAs(UnmanagedType.LPWStr)]
            public string pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(
            IntPtr hwnd,
            [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID,
            IntPtr pWVTData);

        #endregion

        /// <summary>
        /// Validates that the file has a valid digital signature signed by AMD (Advanced Micro Devices).
        /// </summary>
        public static bool VerifyAmdSignature(string filePath, out string signerSubject, out string? errorMessage)
        {
            signerSubject = string.Empty;
            errorMessage = null;

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                errorMessage = "File does not exist.";
                return false;
            }

            // Step 1: Read X509 certificate embedded in Authenticode
            X509Certificate2? cert = null;
            try
            {
#pragma warning disable SYSLIB0057
                cert = new X509Certificate2(filePath);
#pragma warning restore SYSLIB0057
            }
            catch (Exception ex)
            {
                errorMessage = $"No valid Authenticode digital signature found: {ex.Message}";
                return false;
            }


            using (cert)
            {
                signerSubject = cert.Subject;
                string issuer = cert.Issuer;

                // Step 2: Validate Signer Identity
                bool isAmdSubject = signerSubject.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase)
                                 || signerSubject.Contains("AMD Inc", StringComparison.OrdinalIgnoreCase)
                                 || signerSubject.Contains("Advanced Micro Devices, Inc.", StringComparison.OrdinalIgnoreCase);

                if (!isAmdSubject)
                {
                    errorMessage = $"Signer verification failed. Expected 'Advanced Micro Devices', got '{signerSubject}'.";
                    return false;
                }

                // Step 3: Check validity window
                DateTime now = DateTime.Now;
                if (now < cert.NotBefore || now > cert.NotAfter)
                {
                    errorMessage = $"Certificate is not within its validity period (Valid: {cert.NotBefore:d} to {cert.NotAfter:d}).";
                    return false;
                }

                // Step 4: WinVerifyTrust for full catalog/hash verification
                try
                {
                    var fileInfo = new WINTRUST_FILE_INFO
                    {
                        cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                        pcwszFilePath = filePath,
                        hFile = IntPtr.Zero,
                        pgKnownSubject = IntPtr.Zero
                    };

                    IntPtr pFileInfo = Marshal.AllocHGlobal((int)fileInfo.cbStruct);
                    try
                    {
                        Marshal.StructureToPtr(fileInfo, pFileInfo, false);

                        var trustData = new WINTRUST_DATA
                        {
                            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                            pPolicyCallbackData = IntPtr.Zero,
                            pSIPClientData = IntPtr.Zero,
                            dwUIChoice = WTD_UI_NONE,
                            fdwRevocationChecks = WTD_REVOKE_NONE,
                            dwUnionChoice = WTD_CHOICE_FILE,
                            pFile = pFileInfo,
                            dwStateAction = WTD_STATEACTION_IGNORE,
                            hWVTStateData = IntPtr.Zero,
                            pwszURLReference = null!,
                            dwProvFlags = WTD_SAFER_FLAG,
                            dwUIContext = 0,
                            pSignatureSettings = IntPtr.Zero
                        };

                        IntPtr pTrustData = Marshal.AllocHGlobal((int)trustData.cbStruct);
                        try
                        {
                            Marshal.StructureToPtr(trustData, pTrustData, false);
                            Guid actionGuid = new(WINTRUST_ACTION_GENERIC_VERIFY_V2);

                            int result = WinVerifyTrust(IntPtr.Zero, actionGuid, pTrustData);
                            if (result != 0)
                            {
                                // 0 = TRUST_E_PROVIDER_UNKNOWN / SUCCESS (0x00000000)
                                // If WinVerifyTrust returns non-zero, let's format error code
                                errorMessage = $"WinVerifyTrust validation code: 0x{result:X8}";
                                return false;
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(pTrustData);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(pFileInfo);
                    }
                }
                catch (Exception ex)
                {
                    // Fallback to X509Chain if WinVerifyTrust fails to invoke
                    try
                    {
                        using var chain = new X509Chain();
                        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                        bool chainOk = chain.Build(cert);
                        if (!chainOk)
                        {
                            errorMessage = $"Certificate chain validation failed: {ex.Message}";
                            return false;
                        }
                    }
                    catch
                    {
                        errorMessage = $"Signature trust validation failed: {ex.Message}";
                        return false;
                    }
                }

                return true;
            }
        }
    }
}