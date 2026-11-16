# Third-Party Notices and Licenses

This project, TESL-Accelerator-Android, incorporates or depends upon several third-party software components and libraries. Below is an audit of their licenses, origins, and redistribution considerations.

---

## 1. sing-box Core and libbox

- **Project**: sing-box (v1.11.10)
- **Upstream Repository**: https://github.com/SagerNet/sing-box
- **License**: GNU General Public License v3.0 or later (GPL-3.0-or-later)
- **Component in this Project**:
  - `libs/libbox.aar`: Internal build artifact constructed from SFA APK dex2jar and native Go-compiled `libbox.so`.
- **Licensing & Redistribution Compliance Notes**:
  - **GPL-3.0 Obligations**: Under GPLv3, distributing binaries derived from sing-box requires making the corresponding complete and matching source code available under GPL-3.0.
  - **Upstream Branding & Trademark**: Upstream sing-box documentation specifies trademark and naming conditions. Derivative distributions must not misrepresent themselves as official upstream releases.
  - **AAR Audit Status**: The current `libbox.aar` is an internal reconstruction artifact. Prior to making `libbox.aar` publicly distributable on a public GitHub repository, the exact matching Go source tree, build toolchain scripts, and compliance declarations must be documented.

---

## 2. cfnat (Cloudflare Anycast Probe and Forwarder)

- **Component**: `src/native/cfnat` / `libcfnat.so`
- **Origin**: Cloudflare Anycast IP scanning and TCP forwarder implementation
- **License**: MIT License
- **Terms**: Permission is hereby granted, free of charge, to any person obtaining a copy of this software to deal in the Software without restriction, subject to the MIT copyright notice.

---

## 3. Microsoft .NET and Android Workload

- **Components**: Microsoft.NETCore.App, Microsoft.Android.Sdk
- **License**: MIT License
- **Terms**: Governed by the .NET Foundation and Microsoft open source licenses.

---

## 4. IP Range and Location Assets

- **Files**: `Assets/ips-v4.txt`, `Assets/ips-v4-8443.txt`, `Assets/locations.json`, `Assets/cloudflare-ranges-v4.txt`
- **Usage**: Publicly available Cloudflare Anycast CIDRs and airport IATA location code mappings used strictly for latency probing.
