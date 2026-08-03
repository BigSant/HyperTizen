# QE77S95F 1296.8 firmware decoding

## Scope and provenance

- TV: `QE77S95FATXXH`, model group `25TV_PREMIUM3`
- Tizen: 9.0
- update: `T-RSMFDEUC-0090_TB-RSWF4DEUC-0090`, version `1296.8`
- archive size: `2,479,379,839` bytes
- containers: `MSDU11`, AES-256-CBC, production family `RoseM 2025 rel key`

Firmware images, decrypted files, Samsung binaries, passphrases, and keys are
not stored in Git.  The JSON manifests in `tools/firmware/manifests` contain
only clear MSD section metadata needed to reproduce CRC verification.

## Result

`T-RSMFDEUC-0090` was completely decrypted and every section passed the CRC32
recorded in the container.  Its platform GPT was split and both VDFS4 file
systems were extracted without failed files.

| Section | Encrypted bytes | Plain bytes | CRC32 |
| --- | ---: | ---: | ---: |
| `platform.img` | 1,478,492,176 | 1,478,492,160 | `6077955e` |
| `uImage` | 11,892,544 | 11,892,528 | `ffb658c7` |
| `factory_peq.img` | 9,306,128 | 9,306,112 | `ceb9e554` |
| `secos.bin` | 8,380,432 | 8,380,416 | `ff3f80ac` |
| `seret.bin` | 3,661,840 | 3,661,824 | `a5925323` |
| `secos_drv.bin` | 3,137,552 | 3,137,536 | `2dd67ad8` |
| `dtb.bin` | 1,564,688 | 1,564,672 | `d733e955` |
| `ddr.init` | 166,416 | 166,400 | `9d6b9266` |
| `sign.bin` | 1,040 | 1,024 | `2b7c747e` |

The 16-byte difference in each section is valid PKCS#7 padding.  The kernel is
an ARM64 Linux 5.4.261 image built on 2026-01-21.

The decrypted `platform.img` contains this GPT:

| Partition | Sectors | Approx. size | Name | Contents |
| --- | --- | ---: | --- | --- |
| 1 | 2048-2764799 | 1.3 GiB | `pltf_t.img` | main VDFS4 platform |
| 2 | 2764800-2885631 | 59 MiB | `prd_t.img` | product-specific VDFS4 |

The extracted platform contains 59,565 regular files (~2.6 GB expanded); the
product partition contains 976 regular files (~121 MB expanded).

`TB-RSWF4DEUC-0090` uses the same construction and protocol.  It was also
completely decrypted and all nine CRCs match:

| Section | Encrypted bytes | Plain bytes | CRC32 |
| --- | ---: | ---: | ---: |
| `platform.img` | 929,038,352 | 929,038,336 | `9df86642` |
| `uImage` | 11,415,888 | 11,415,884 | `4f9ca674` |
| `secos.bin` | 8,380,432 | 8,380,416 | `095816fc` |
| `factory_peq.img` | 4,653,072 | 4,653,056 | `bfa7fbc4` |
| `seret.bin` | 3,661,840 | 3,661,824 | `3b43754f` |
| `secos_drv.bin` | 3,137,552 | 3,137,536 | `0f668ab2` |
| `dtb.bin` | 1,564,688 | 1,564,672 | `91c7467f` |
| `ddr.init` | 522,256 | 522,240 | `a85e647f` |
| `sign.bin` | 1,040 | 1,024 | `5d2b4156` |

Its GPT contains an 886,046,720-byte `pltf_t.img` and a 40,894,464-byte
`prd_t.img`.  Both extracted without failures.  The expanded platform has
33,004 regular files (~1.7 GB), and the product image has 621 (~83 MB).  Its
kernel is also ARM64 Linux 5.4.261, built on 2026-01-21.

Capture-related shared components such as `libgstdisplayencodesrc`,
`libcapi-encoder-tv`, `libgstwaylandsrc`, and the RM backend are byte-identical
between the two platform families.  `libtzcapturec` differs in its compiled
process allowlist but exposes the same symbols and function layout.  The
unstripped raw `libvideo-capture-impl-sec` scaler backend is present in the
larger RSM product image and absent from the RSW product image.

## TrustZone decryption protocol

The installed SWU client was analyzed to reproduce its normal request flow.
The firmware secret remains inside the TV: the host sends ciphertext and salt,
and receives plaintext.  It never reads or exports the passphrase or derived
AES key.

1. Open the installed SWU trusted application using its session UUID.
2. Invoke command 0 with two input buffers: the encrypted production
   passphrase and the section's 8-byte salt.
3. Set `derivation=1` (SHA-256), `keysize=2` (AES-256), and `mode=1`
   (decrypt) in the packed value parameters.
4. Feed the section through command 1 using registered partial input/output
   shared memory.
5. Invoke command 2 to finalize the CBC operation.
6. Remove PKCS#7 padding and require the resulting CRC32 to equal the MSD
   section table value.

Tizen's TEEC parameter encoding in these binaries stores each parameter type
in a byte.  The relevant command layouts are:

```text
command 0: PARTIAL_INPUT, PARTIAL_INPUT, VALUE_INPUT, VALUE_INPUT
command 1: PARTIAL_INPUT, PARTIAL_OUTPUT, VALUE_OUTPUT, NONE
command 2: PARTIAL_OUTPUT, VALUE_OUTPUT, NONE, NONE
```

`tools/firmware/decrypt_via_tv.py` implements streaming, padding removal, and
CRC enforcement against a checked-in manifest.  A diagnostic service is
required only during this step; static analysis after decryption is entirely
local.

## VDFS4 extraction correction

The public `vdfs-unpack` parser could enumerate the image, but initially
failed on many authenticated `hZip` files because it treated compressed data
as one zlib stream and placed the extent descriptor before the authentication
table.  Inspection of the on-disk flags showed that the descriptor follows:

```text
(extent_count + 1) * digest_size + signature_size
```

Digest sizes are 0 for `C`, 16 for `I` (MD5), 20 for `H` (SHA-1), and 32 for
`h` (SHA-256); signature size is 0, 128, or 256 bytes according to the signing
type.  After correcting that offset, each extent is inflated independently
and both main-container VDFS images extract with zero failures.

The extractor and VDFS4 kernel sources used for format validation are public:

- <https://github.com/gtors/vdfs-unpack>
- <https://github.com/vdfs-team/vdfs4>

The local extractor patch lives in the external research-tools directory, not
in this repository, because it is a general upstream-tool change rather than
HyperTizen runtime code.

## Reproducibility and safety

- `tools/firmware/inspect_msd.py` inspects clear MSD tables without a TV.
- `tools/firmware/decrypt_via_tv.py` decrypts only the named sections and
  rejects wrong plaintext by CRC.
- Decrypted artifacts remain outside Git under the local firmware workspace.
- No firmware was flashed, modified, or written back to the TV.
- The separately named temporary diagnostic TPK was uninstalled after both
  containers were decoded; the production HyperTizen package was untouched.

Public legacy work was useful for container orientation, but does not include
the RoseM 2025 production material or this installed SWU command sequence:

- <https://github.com/synacktiv/samsung-q60t-exploit>
- <https://wiki.samygo.tv/index.php?title=Samsung_OTN_protocol>
