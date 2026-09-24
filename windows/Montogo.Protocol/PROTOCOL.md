# Montogo Protocol Specification

**Version:** 7
**Transport:** UDP unicast
**Discovery:** manual — the user enters the Windows LAN IP in the Mac app. (mDNS via `_montogo._udp.local.` is planned but not implemented.)

All multi-byte integer fields are **little-endian** (both platforms are LE: Windows/x86-64, Mac/ARM).

---

## Version History

| Version | What changed |
|---------|-------------|
| 1 | Initial: handshake + plain H.264 chunks |
| 2 | Auth token in HandshakeRequest + AES-256-GCM stream encryption |
| 5 | **HEVC (H.265)** video; Reed–Solomon FEC parity per frame; Mac→Windows link-quality **feedback** packet |
| 6 | Chunk header authenticated as GCM **associated data (AAD)** |
| 7 | **Ephemeral P-256 ECDH handshake** (forward secrecy); **64-bit / 13-char** pairing code; fully authenticated handshake response |

Each side rejects packets whose `Version` byte does not equal its own, so a mismatched pair fails fast at the version check rather than mis-parsing.

---

## Security Model

### Pairing code

Windows generates **8 cryptographically random bytes** (64-bit secret) on first launch and shows them as a **13-character connection code** (e.g. `7X4K-9M2P-QRST`). The code **authenticates** the handshake; it does **not** encrypt the video (see ECDH below).

**Encoding — base-32 over alphabet `23456789ABCDEFGHJKLMNPQRSTUVWXYZ`** (32 symbols × 5 bits; `0`,`1`,`I`,`O` omitted to avoid transcription errors). 8 bytes = 64 bits → 13 characters (the top character carries 4 significant bits; 13×5 = 65-bit space with the high bit unused). Formatting groups them `4-4-5` with dashes; strip dashes before decoding.

```
encode: bits = big-endian u64 of the 8 bytes; emit 13 base-32 groups MSB→LSB
decode: strip '-', map each char to its alphabet index, rebuild the 64-bit value, take 8 bytes big-endian
```

### Key derivation (HKDF-SHA256)

The pairing code yields **only** the handshake authentication key:

```
AuthKey = HKDF-SHA256(ikm = 8-byte code, salt = empty, info = "montogo-auth-v1", length = 32)
```

The **video key is not derived from the code.** It is established per session by the ECDH exchange below:

```
SessionKey = HKDF-SHA256(ikm = ECDH shared secret, salt = empty, info = "montogo-session-v7", length = 32)
```

Info strings are UTF-8, no null terminator.

### Ephemeral key exchange (P-256 ECDH)

Each session performs an authenticated Elliptic-Curve Diffie–Hellman exchange on **NIST P-256**:

- The Mac and Windows each generate an ephemeral P-256 key pair and send the public key (X9.63 uncompressed form, `0x04 || X(32) || Y(32)` = 65 bytes) in the handshake.
- Each side computes the shared secret (the 32-byte X coordinate of the ECDH result) and derives `SessionKey` from it via HKDF as above.
- The ephemeral private keys are discarded after the handshake.

**Consequences:** forward secrecy (captured traffic can't be decrypted later even if the code leaks); a passive eavesdropper cannot recover `SessionKey` at all (it is a 256-bit ECDH secret, not derived from the 64-bit code); and a replayed handshake is useless for decryption (the attacker lacks the ephemeral private key).

### Handshake authentication

Both handshake messages end with a **32-byte `HMAC-SHA256(AuthKey, all-preceding-bytes)`** tag. The receiver verifies it in constant time before trusting any field. This proves each party knows the code, binds the ephemeral public keys to the exchange, and authenticates the response (so a spoofed response can't set a bogus key or nonce prefix).

### Stream encryption (AES-256-GCM)

Every `VideoChunk` payload is sealed with AES-256-GCM under `SessionKey`.

**Nonce (12 bytes):**
```
nonce[0..7]  = HandshakeResponse.NoncePrefix   — 8 random bytes, fixed for the session
nonce[8..11] = VideoChunkHeader.SequenceNum    — little-endian uint32, unique per chunk
```
A fresh session key each run makes nonce reuse across sessions impossible; within a session the 32-bit `SequenceNum` keeps each chunk's nonce unique.

**Associated data (AAD):** the full **28-byte chunk header** is authenticated (not encrypted) as GCM associated data, so an attacker cannot tamper with routing/reassembly fields (frame/chunk indices, flags, lengths) without failing the tag.

**Payload layout (immediately after the 28-byte header):**
```
[ciphertext: PayloadLength-16 bytes][GCM tag: 16 bytes]
```
`PayloadLength = ciphertext_length + 16`. If the tag does not verify, discard the chunk silently.

### At rest

The pairing code is never stored in plaintext: Windows encrypts it with DPAPI (current-user scope) in `%APPDATA%\Montogo\settings.json`; the Mac keeps it in the Keychain. It can be rotated from the Windows tray ("Reset connection code").

---

## Common Header Prefix (4 bytes)

| Offset | Size | Type   | Field      | Description             |
|--------|------|--------|------------|-------------------------|
| 0      | 2    | uint16 | Magic      | Always `0x474D` ("GM") |
| 2      | 1    | uint8  | Version    | Protocol version = `7` |
| 3      | 1    | uint8  | PacketType | See table below         |

---

## Packet Types

| Value  | Name              | Direction      |
|--------|-------------------|----------------|
| `0x01` | VideoChunk        | Windows → Mac  |
| `0x10` | Heartbeat         | Mac → Windows  |
| `0x11` | HandshakeRequest  | Mac → Windows  |
| `0x12` | HandshakeResponse | Windows → Mac  |
| `0x13` | Feedback          | Mac → Windows  |

Video is one-directional: Windows sends, the Mac is a passive display. The Mac sends only `HandshakeRequest`, `Heartbeat`, and `Feedback`. There are no input events.

**Liveness:** Windows does not send heartbeats. The video stream is the liveness signal — the capture loop re-emits the last frame at the target fps even on a static desktop, so a connected Mac always receives packets. The Mac declares the connection lost after **3 seconds** without a valid-header packet and resumes handshaking.

---

## Ports

| Port      | Listener | Purpose                  |
|-----------|----------|--------------------------|
| **47921** | Both     | Video stream + handshake + feedback |

Both sides bind 47921. **Windows must send video from source port 47921** (the socket that answered the handshake): macOS's stateful firewall only passes inbound UDP that looks like a reply to the Mac's outbound handshake. Video from an ephemeral source port is dropped silently.

---

## VideoChunk Packet (Windows → Mac)

Carries one AES-256-GCM-encrypted chunk of a single HEVC-encoded frame, **or** a Reed–Solomon parity packet for that frame.

**Header size: 28 bytes**

| Offset | Size | Type   | Field         | Description                                              |
|--------|------|--------|---------------|----------------------------------------------------------|
| 0      | 2    | uint16 | Magic         | `0x474D`                                                 |
| 2      | 1    | uint8  | Version       | `7`                                                      |
| 3      | 1    | uint8  | PacketType    | `0x01`                                                   |
| 4      | 4    | uint32 | SequenceNum   | Per-packet monotonic counter; also the GCM nonce base    |
| 8      | 4    | uint32 | FrameId       | Per-frame monotonic counter                              |
| 12     | 8    | uint64 | TimestampUs   | Capture timestamp, µs since Unix epoch                   |
| 20     | 2    | uint16 | ChunkIndex    | Data chunk 0…ChunkTotal-1; **parity** when ≥ ChunkTotal  |
| 22     | 2    | uint16 | ChunkTotal    | Number of **data** chunks (k) for this frame             |
| 24     | 2    | uint16 | PayloadLength | `ciphertext_len + 16`                                    |
| 26     | 1    | uint8  | Flags         | Bit 0: IDR frame; bits 1–7 reserved (0)                 |
| 27     | 1    | uint8  | FecTotal      | Number of FEC parity packets (m) for this frame (0 = none) |
| 28     | …    | bytes  | Payload       | AES-256-GCM ciphertext + 16-byte tag (header is the AAD) |

A packet is **parity** when `ChunkIndex ≥ ChunkTotal`; its stripe index is `ChunkIndex - ChunkTotal`. **Chunk 0 of every IDR frame** decrypts to VPS + SPS + PPS parameter sets followed by the IDR slice.

### Forward Error Correction (Reed–Solomon over GF(256))

Each frame ships with `FecTotal` (m) systematic parity packets computed over all `ChunkTotal` (k) data chunks with a Cauchy matrix over GF(256). Because the code is MDS, **any k of the (k + m)** received packets reconstruct the original k — so a frame survives up to m losses anywhere (keyframes included) with no retransmission. Overhead is adaptive: Windows raises the parity percentage as the Mac reports loss, with an extra margin on IDR frames. Parity is computed over a fixed-width unit `[2-byte LE length][plaintext][zero pad]` so a rebuilt chunk knows its own length. A frame that arrives complete needs no decoding.

---

## Feedback Packet (Mac → Windows) — 40 bytes

Sent a few times per second so Windows can adapt the bitrate and FEC, and re-sync fast after a loss. Authenticated by the same truncated token as legacy handshakes plus a source-IP check (only the paired Mac's address may steer).

| Offset | Size | Type   | Field        | Description                                          |
|--------|------|--------|--------------|------------------------------------------------------|
| 0      | 2    | uint16 | Magic        | `0x474D`                                             |
| 2      | 1    | uint8  | Version      | `7`                                                  |
| 3      | 1    | uint8  | PacketType   | `0x13`                                               |
| 4      | 16   | bytes  | ClientId     | This Mac session's UUID                              |
| 20     | 16   | bytes  | Token        | `HMAC-SHA256(AuthKey, ClientId.bytes)[0..15]`        |
| 36     | 2    | uint16 | LossPermille | Packet loss over the last window, 0…1000 (‰)         |
| 38     | 1    | uint8  | Fps          | Rendered fps (diagnostic)                            |
| 39     | 1    | uint8  | Flags        | Bit 0: request keyframe (Mac saw a frameId gap)      |

**Fast gap recovery:** the frames form an IPPP chain, so a missing `FrameId` (lost on the link or dropped in Windows's send queue) breaks the reference chain. On a gap the Mac stops feeding the decoder broken frames and sets the request-keyframe flag; Windows forces an IDR, re-syncing in ~1 round-trip instead of freezing until the next periodic keyframe.

---

## Heartbeat Packet (Mac → Windows) — 16 bytes

| Offset | Size | Type   | Field       | Description                           |
|--------|------|--------|-------------|---------------------------------------|
| 0      | 2    | uint16 | Magic       | `0x474D`                              |
| 2      | 1    | uint8  | Version     | `7`                                   |
| 3      | 1    | uint8  | PacketType  | `0x10`                                |
| 4      | 8    | uint64 | TimestampUs | Sender's current time, µs since epoch |
| 12     | 4    | uint32 | SequenceNum | Per-packet counter                    |

Windows currently ignores heartbeats (its send path needs no liveness signal from the Mac).

---

## Handshake

The Mac generates **one ephemeral P-256 key pair per handshake cycle** and sends a `HandshakeRequest` every 500 ms until it receives a valid `HandshakeResponse`.

Windows verifies the request HMAC on **every** request:

- **Bad HMAC** (wrong code) → dropped silently; while no session is streaming the tray shows `Wrong code from <ip>` so a typo is visible. Nothing is sent on the wire.
- **Valid HMAC** → Windows runs the ECDH, derives `SessionKey`, and (if a session is already running — a relaunched or reconnecting Mac) tears the pipeline down and starts a fresh one on the new key. It replies with a `HandshakeResponse` and forces the first frame to be a keyframe. Because each handshake re-keys, a re-handshake **restarts** the pipeline (unlike ≤ v6, which re-targeted in place).

### HandshakeRequest (Mac → Windows) — 117 bytes

| Offset | Size | Type  | Field     | Description                                       |
|--------|------|-------|-----------|---------------------------------------------------|
| 0      | 2    | uint16| Magic     | `0x474D`                                          |
| 2      | 1    | uint8 | Version   | `7`                                               |
| 3      | 1    | uint8 | PacketType| `0x11`                                            |
| 4      | 16   | bytes | ClientId  | Random UUID identifying this Mac session          |
| 20     | 65   | bytes | MacPubKey | Ephemeral P-256 public key, X9.63 (`04‖X‖Y`)      |
| 85     | 32   | bytes | AuthTag   | `HMAC-SHA256(AuthKey, bytes[0..85])`              |

### HandshakeResponse (Windows → Mac) — 131 bytes

| Offset | Size | Type  | Field        | Description                                     |
|--------|------|-------|--------------|-------------------------------------------------|
| 0      | 2    | uint16| Magic        | `0x474D`                                        |
| 2      | 1    | uint8 | Version      | `7`                                             |
| 3      | 1    | uint8 | PacketType   | `0x12`                                          |
| 4      | 2    | uint16| DisplayWidth | Virtual display width (px)                       |
| 6      | 2    | uint16| DisplayHeight| Virtual display height (px)                      |
| 8      | 1    | uint8 | TargetFps    | Target frame rate (60)                          |
| 9      | 1    | uint8 | Reserved     | Must be 0                                        |
| 10     | 16   | bytes | ClientId     | Echo of the request ClientId                     |
| 26     | 65   | bytes | WinPubKey    | Ephemeral P-256 public key, X9.63 (`04‖X‖Y`)     |
| 91     | 8    | uint64| NoncePrefix  | Per-session GCM nonce prefix (LE)               |
| 99     | 32   | bytes | AuthTag      | `HMAC-SHA256(AuthKey, bytes[0..99])`            |

---

## Video Codec (HEVC / H.265)

Frames are HEVC Annex B access units (start-code-delimited NALs). The 2-byte HEVC NAL header encodes the type as `(firstByte >> 1) & 0x3F`:

| Type | NAL |
|------|-----|
| 32 | VPS |
| 33 | SPS |
| 34 | PPS |
| 35 | AUD (dropped before decode) |
| 0–31 | VCL slice (IDR = 19/20) |

The encoder emits an IDR access unit as AUD + VPS + SPS + PPS + slice; a P-frame as AUD + slice. NVENC HEVC is **I/P only (no B-frames)**, forced CBR, ~1 keyframe/sec, plus a keyframe on connect. The decoder builds its format description from VPS+SPS+PPS (via `CMVideoFormatDescriptionCreateFromHEVCParameterSets`) and submits the VCL slices as length-prefixed AVCC.

---

## MTU and Frame Chunking

- **MaxChunkPayload:** 1400 bytes of plaintext per chunk.
- Wire size per data chunk: 28 (header) + plaintext_len + 16 (tag) ≤ **1444 bytes** (under the 1500-byte Ethernet MTU).
- A frame's data chunks (`ChunkIndex` 0…k-1) are sent first, then its `FecTotal` parity packets (`ChunkIndex` k…k+m-1).
- The receiver buffers a frame's chunks, FEC-recovers any missing data chunks, then decrypts and decodes.
- If a frame can't be completed within **100 ms** it is dropped; the next keyframe (or a requested one) re-syncs.

---

## Receiver Implementation Checklist

Implemented by the Mac app (`UDPReceiver`, `ConnectionCode`, `AuthToken`, `StreamDecryptor`, `FrameAssembler`, `H264Decoder`).

1. **Setup:** user enters the 13-char code; store it. Decode → 8-byte IKM → `AuthKey = HKDF(…, "montogo-auth-v1")`.
2. **Per handshake cycle:** generate a random `ClientId` (UUID) and an **ephemeral P-256 key pair**.
3. **HandshakeRequest (117 B):** magic/ver/type, ClientId, `MacPubKey = pubkey.x963`, `AuthTag = HMAC(AuthKey, bytes[0..85])`. Send every 500 ms until a valid response.
4. **HandshakeResponse (131 B):** verify `AuthTag = HMAC(AuthKey, bytes[0..99])` and that `ClientId` echoes ours; parse `WinPubKey` and `NoncePrefix`.
5. **Session key:** `shared = ECDH(ephemeralPriv, WinPubKey)`; `SessionKey = HKDF(shared, "montogo-session-v7")`. Create the decryptor with `SessionKey` + `NoncePrefix`.
6. **Video receive loop:** verify Magic/Version/type; parse the 28-byte header; build the nonce (`NoncePrefix ‖ SequenceNum LE`); decrypt with the **28-byte header as AAD**; drop on tag failure. Buffer by `FrameId`; FEC-recover missing data chunks from parity; reassemble in `ChunkIndex` order.
7. **Ordering gate:** deliver frames in `FrameId` order; on a gap, drop P-frames until the next IDR and set the request-keyframe feedback flag.
8. **Decode:** split the access unit on start codes; on IDR, (re)build the format description from VPS+SPS+PPS; submit each VCL slice as length-prefixed AVCC to VideoToolbox (drop AUD/parameter-set NALs from the sample); suppress a byte-identical duplicate frame.
9. **Feedback:** every ~400 ms send a `Feedback` packet with measured loss (and the keyframe-request flag while awaiting recovery).
10. **Heartbeat / watchdog:** send a `Heartbeat` every 1 s; if no valid packet for 3 s, declare the connection lost and restart the handshake.
