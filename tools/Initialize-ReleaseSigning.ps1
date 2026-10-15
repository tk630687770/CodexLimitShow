$ErrorActionPreference = 'Stop'
# The private key stays in the current Windows user's CNG key store, never in Git or the EXE.
$keyName = 'CodexLimitShow.ReleaseSigning.v1'
if ([Security.Cryptography.CngKey]::Exists($keyName)) {
    $key = [Security.Cryptography.CngKey]::Open($keyName)
} else {
    $key = [Security.Cryptography.CngKey]::Create([Security.Cryptography.CngAlgorithm]::ECDsaP256, $keyName)
}
try {
    $signer = [Security.Cryptography.ECDsaCng]::new($key)
    try { [Convert]::ToBase64String($signer.ExportSubjectPublicKeyInfo()) }
    finally { $signer.Dispose() }
} finally { $key.Dispose() }
