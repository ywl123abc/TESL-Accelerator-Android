# Android Release Signing and Compatibility Audit

## 1. Current APK Identity Baseline

- **Application ID**: `com.tesl.accelerator`
- **Current Version Name**: `2.0.0`
- **Current Version Code**: `1`
- **Target SDK**: `35` (Android 15)
- **Minimum Supported OS**: `26.0` (Android 8.0 Oreo)

---

## 2. Current Signing Certificate Audit

Verified using Android SDK `apksigner verify --print-certs`:

- **Signer DN**: `CN=Android Debug, O=Android, C=US`
- **Keystore Type**: Debug Keystore (Default .NET Android development keystore)
- **Certificate SHA-256 Digest**: `dd085a35e1b66507e440fb689ee4bbd60f8e7c74c4b364dcc8671903604ee7b6`
- **Certificate SHA-1 Digest**: `3f86071e3e2f1d91a379f7f5f3e724ee834085ba`
- **Certificate MD5 Digest**: `5ddebe4366652b6af08a6ddf7ea381ff`

---

## 3. Upgrade and Overwrite Compatibility Analysis

1. **Android Package Signature Verification**:
   Android's `PackageManager` strictly requires that any update package (`.apk`) sharing the same `applicationId` must be signed with the exact same certificate (or a recognized certificate lineage in Android 9+).
2. **Debug-to-Release Incompatibility**:
   The current device build is signed with the local Debug key. When the formal Release build is generated with the user's private Release Keystore, the SHA-256 fingerprint will differ.
   Directly installing the Release APK over the Debug APK will result in `INSTALL_FAILED_UPDATE_INCOMPATIBLE`.
3. **Migration Policy**:
   - For the initial transition from Debug to the first official Release version, the Debug APK must be manually uninstalled by the user.
   - All subsequent official Release versions (e.g. v2.0.1, v2.1.0) must use the identical formal Release Keystore. In-place updates will succeed seamlessly.
4. **Security & Credential Boundaries**:
   - The formal Release Keystore and its passwords must remain strictly confidential and will never be committed to Git.
   - The application ID `com.tesl.accelerator` remains permanent and unchanged.
