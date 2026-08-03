# HyperTizenProbe

Read-only Tizen 9 hardware probe used to validate capture APIs on a physical
Samsung TV. It is packaged under the separate application ID
`io.github.bigsant.HyperTizenProbe`, so it does not replace HyperTizen.

The HTTP report listens on port `45679` and exposes:

- `/` - text diagnostics;
- `/frame.ppm` - one-shot EFL screenshot;
- `/mirror.ppm` - first continuous screen-mirror callback frame.

The probe verifies EFL/TBM capture, screen-mirror callback rate, library and
device access, and optional SWU TrustZone behavior. `probe_native.c` is the
source for the optional ARM helper used by the SWU test. The compiled shared
object is intentionally not stored in Git; without it, only the SWU subsection
reports that the optional helper is unavailable.

Build and signing require a local Samsung TV certificate profile:

```powershell
C:\tizen-studio\tools\ide\bin\tizen.bat build-cs -C Debug -s <profile> -- .
```

Do not commit TPK files, certificates, extracted TV libraries, firmware, or
captured frames.
