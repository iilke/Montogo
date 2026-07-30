# Montogo Protocol Specification

**Version:** 2  
**Transport:** UDP unicast  
**Discovery:** manual — the user enters the Windows LAN IP in the Mac app. (mDNS via `_montogo._udp.local.` is planned but not implemented.)

All multi-byte integer fields are **little-endian** (both platforms are LE: Windows/x86-64, Mac/ARM).

---

## Version History

| Version | What changed |
|---------|-------------|
| 1 | Initial: handshake + plain H.264 chunks |
| 2 | **Auth token in HandshakeRequest + AES-256-GCM stream encryption** |

A v2 HandshakeRequest is 16 bytes larger than v1 (40 vs 24 bytes); Windows rejects v1 packets as too short.

---

## Security Model

### Shared Secret and Connection Code

Windows generates **5 cryptographically random bytes** on first launch and stores them as an 8-character
**connection code** (e.g. `7X4K-9M2P`).  The code is the master secret: both sides derive all
cryptographic keys from it via HKDF.

**Connection code encoding — base-32 over alphabet `23456789ABCDEFGHJKLMNPQRSTUVWXYZ`:**

The alphabet has 32 symbols (5 bits each), mapping 5 bytes = 40 bits → 8 characters.  The characters
`0`, `1`, `I`, `O` are omitted to avoid transcription errors.  Formatting inserts a `-` between
characters 4 and 5 for readability; strip the dash before decoding.

Encoding algorithm (big-endian):
```
bits = byte[0]<<32 | byte[1]<<24 | byte[2]<<16 | byte[3]<<8 | byte[4]
chars[0..7]: extract 8 groups of 5 bits from MSB to LSB, map each to Alphabet[group]
display as chars[0..3] + "-" + chars[4..7]
```

Decoding: strip `-`, map each character to its index in the alphabet (case-insensitive),
reconstruct the 40-bit integer (big-endian), extract 5 bytes.

### Key Derivation (HKDF-SHA256)

From the 5-byte IKM (decoded connection code), derive two 32-byte keys:

```
EncKey  = HKDF-SHA256(ikm, salt=empty, info="montogo-enc-v1",  length=32)
AuthKey = HKDF-SHA256(ikm, salt=empty, info="montogo-auth-v1", length=32)
```

Info strings are UTF-8 with no null terminator.  HKDF expands entropy but does not add it —
effective security is ~40 bits (2⁴⁰ ≈ 1 trillion combinations).

### Auth Token

Included in every `HandshakeRequest`.  Windows silently drops requests with an invalid token.

```
Token = HMAC-SHA256(AuthKey, ClientId.bytes)[0..15]   // first 16 of 32 HMAC bytes
```

`ClientId.bytes` is the 16-byte little-endian UUID representation (matching `Guid.ToByteArray()` on
.NET and the standard UUID byte layout on little-endian Swift/macOS).

Windows compares the received token using a **constant-time equality check** to prevent timing attacks.

### Stream Encryption (AES-256-GCM)

Every `VideoChunk` payload is encrypted with AES-256-GCM using `EncKey`.

**Nonce construction (12 bytes):**
```
nonce[0..7]  = HandshakeResponsePacket.NoncePrefix  — 8 random bytes, fixed for the session
nonce[8..11] = VideoChunkHeader.SequenceNum         — little-endian uint32, unique per chunk
```

The prefix is generated fresh by `UdpSender` on each app launch and transmitted once to the Mac in
`HandshakeResponsePacket.NoncePrefix`.  This eliminates cross-session nonce reuse: even though the
same `EncKey` persists across restarts (derived from the stored connection code), each session gets
a distinct nonce space.  Within a session the 32-bit `SequenceNum` counter keeps every chunk unique;
at 60 fps with ~15 chunks/frame the counter would take ~55 days to wrap.

**Wire layout of the payload section (immediately after the 28-byte header):**
```
[ciphertext: PayloadLength-16 bytes][GCM tag: 16 bytes]
```

`VideoChunkHeader.PayloadLength` = ciphertext_length + 16.  
Plaintext (H.264 NAL unit bytes) length = `PayloadLength - 16`.

**Decryption on Mac:**
```
nonce          = noncePrefix(from HandshakeResponse) ++ SequenceNum(LE uint32)
ciphertext_len = PayloadLength - 16
plaintext = AES-256-GCM.Decrypt(
    key   = EncKey,
    nonce = nonce,                            // 12 bytes: 8-byte prefix + 4-byte counter
    data  = payload[0 .. ciphertext_len-1],
    tag   = payload[ciphertext_len .. ciphertext_len+15]
)
```
If the GCM tag does not verify, discard the chunk silently.

---

## Common Header Prefix (4 bytes)

Every Montogo packet begins with these 4 bytes:

| Offset | Size | Type   | Field      | Description              |
|--------|------|--------|------------|--------------------------|
| 0      | 2    | uint16 | Magic      | Always `0x474D` ("GM")  |
| 2      | 1    | uint8  | Version    | Protocol version = `2`  |
| 3      | 1    | uint8  | PacketType | See table below          |

---

## Packet Types

| Value  | Name              | Direction      |
|--------|-------------------|----------------|
| `0x01` | VideoChunk        | Windows → Mac  |
| `0x10` | Heartbeat         | Mac → Windows  |
| `0x11` | HandshakeRequest  | Mac → Windows  |
| `0x12` | HandshakeResponse | Windows → Mac  |

UDP is **one-directional for video**: Windows sends, Mac receives.  The Mac is a passive display;
it sends only `HandshakeRequest` and `Heartbeat` packets.  There are no input events.

**Liveness:** Windows does not send heartbeats.  The video stream itself is the liveness signal —
the capture loop re-emits the last frame at the target fps even when the desktop is static, so a
connected Mac always receives packets.  The Mac declares the connection lost after **3 seconds**
without a valid-header packet and resumes handshaking.

---

## Ports

| Port      | Listener | Purpose                  |
|-----------|----------|--------------------------|
| **47921** | Both     | Video stream + handshake |

Both sides bind 47921 and all traffic flows between the two bound sockets.  **Windows must send
video from source port 47921** (the same socket that answered the handshake): macOS's stateful
firewall only passes inbound UDP that looks like a reply to the Mac's outbound handshake, i.e.
traffic from `WindowsIP:47921`.  Video sent from an ephemeral source port is dropped silently.

---

## VideoChunk Packet (Windows → Mac)

Carries one AES-256-GCM encrypted chunk of a single H.264-encoded frame.

**Header size: 28 bytes**

| Offset | Size | Type   | Field         | Description                                              |
|--------|------|--------|---------------|----------------------------------------------------------|
| 0      | 2    | uint16 | Magic         | `0x474D`                                                 |
| 2      | 1    | uint8  | Version       | `2`                                                      |
| 3      | 1    | uint8  | PacketType    | `0x01`                                                   |
| 4      | 4    | uint32 | SequenceNum   | Per-packet monotonic counter; also the GCM nonce base    |
| 8      | 4    | uint32 | FrameId       | Per-frame monotonic counter                              |
| 12     | 8    | uint64 | TimestampUs   | Capture timestamp, µs since Unix epoch                   |
| 20     | 2    | uint16 | ChunkIndex    | 0-based index of this chunk within the frame             |
| 22     | 2    | uint16 | ChunkTotal    | Total chunk count for this frame                         |
| 24     | 2    | uint16 | PayloadLength | `ciphertext_len + 16` (GCM tag is always 16 bytes)       |
| 26     | 1    | uint8  | Flags         | Bit 0: IDR frame; bits 1–7 reserved (must be 0)         |
| 27     | 1    | uint8  | Reserved      | Must be 0                                                |
| 28     | …    | bytes  | Payload       | AES-256-GCM ciphertext followed by 16-byte GCM tag       |

**Chunk 0 of every IDR frame** contains SPS + PPS NAL units followed by the IDR slice (after decryption).

---

## Heartbeat Packet — 16 bytes

Sent by the Mac every 1 second while connected.  Windows currently ignores heartbeats (its send
path needs no liveness signal from the Mac); they exist so a future Windows version can detect a
departed Mac and return to the handshake loop.

| Offset | Size | Type   | Field       | Description                           |
|--------|------|--------|-------------|---------------------------------------|
| 0      | 2    | uint16 | Magic       | `0x474D`                              |
| 2      | 1    | uint8  | Version     | `2`                                   |
| 3      | 1    | uint8  | PacketType  | `0x10`                                |
| 4      | 8    | uint64 | TimestampUs | Sender's current time, µs since epoch |
| 12     | 4    | uint32 | SequenceNum | Per-packet counter                    |

---

## Handshake

The Mac sends a `HandshakeRequest` on startup and retries every 500 ms until it receives a
`HandshakeResponse`.

Windows validates the token on **every** request:

- **Invalid token** → dropped silently (an unauthenticated flood never touches the pipeline).
  While no session is streaming, Windows also updates its local tray to `Wrong code from <ip>` so a
  mistyped connection code is visible rather than a silent hang. Nothing is sent on the wire.
- **Valid token, no session running** → Windows starts the pipeline and replies with a
  `HandshakeResponse`. The first encoded frame is a keyframe, so the Mac can decode immediately.
- **Valid token, session already running** (the Mac was relaunched with a new `ClientId`, or dropped
  and is reconnecting) → Windows re-targets the running sender at the requester, replies with a
  `HandshakeResponse` carrying the **existing** session `NoncePrefix`, and forces the encoder to emit
  a keyframe on the next frame so the new client can start decoding at once. The pipeline is **not**
  restarted, so the `EncKey`/`NoncePrefix` and the running `SequenceNum` counter are unchanged.

### HandshakeRequest (Mac → Windows) — 40 bytes

| Offset | Size | Type   | Field        | Description                                         |
|--------|------|--------|--------------|-----------------------------------------------------|
| 0      | 2    | uint16 | Magic        | `0x474D`                                            |
| 2      | 1    | uint8  | Version      | `2`                                                 |
| 3      | 1    | uint8  | PacketType   | `0x11`                                              |
| 4      | 2    | uint16 | ProtoVersion | Highest protocol version the client supports        |
| 6      | 2    | uint16 | Reserved     | Must be 0                                           |
| 8      | 16   | bytes  | ClientId     | Random UUID identifying this Mac session            |
| 24     | 16   | bytes  | Token        | `HMAC-SHA256(AuthKey, ClientId.bytes)[0..15]`       |

**Token wire encoding:** the 16 HMAC bytes are laid out as two consecutive little-endian `uint64`
values (the `HandshakeToken` struct).  On both LE platforms this is equivalent to copying the 16
HMAC bytes verbatim into packet bytes 24–39.

### HandshakeResponse (Windows → Mac) — 36 bytes

| Offset | Size | Type   | Field             | Description                                        |
|--------|------|--------|-------------------|----------------------------------------------------|
| 0      | 2    | uint16 | Magic             | `0x474D`                                           |
| 2      | 1    | uint8  | Version           | `2`                                                |
| 3      | 1    | uint8  | PacketType        | `0x12`                                             |
| 4      | 2    | uint16 | NegotiatedVersion | `2`                                                |
| 6      | 2    | uint16 | DisplayWidth      | Virtual display width in pixels                    |
| 8      | 2    | uint16 | DisplayHeight     | Virtual display height in pixels                   |
| 10     | 1    | uint8  | TargetFps         | Target frame rate (30 SW or 60 HW)                 |
| 11     | 1    | uint8  | Reserved          | Must be 0                                          |
| 12     | 16   | bytes  | ClientId          | Echo of the ClientId from the request              |
| 28     | 8    | uint64 | NoncePrefix       | Per-session GCM nonce prefix (LE); use as nonce[0..7] |

---

## MTU and Frame Chunking

- **MaxChunkPayload:** 1400 bytes of **plaintext** H.264 per chunk
- Wire size per chunk: 28 (header) + plaintext_len + 16 (GCM tag) ≤ **1444 bytes** (well under 1500-byte Ethernet MTU)
- Frames larger than 1400 plaintext bytes are split into sequential chunks (`ChunkIndex` 0…`ChunkTotal`-1)
- The receiver buffers all chunks of a frame before decrypting and decoding
- If any chunk is missing after **100 ms**, the entire frame is silently dropped
- **No retransmission.** The next IDR frame re-syncs the decoder

---

## Receiver Implementation Checklist

Implemented by the Mac app in `mac/Montogo` (`UDPReceiver`, `ConnectionCode`, `AuthToken`,
`StreamDecryptor`, `FrameAssembler`, `H264Decoder`); kept here as the spec-conformance checklist.

1. **Setup (one-time):** user enters the 8-char code shown in the Windows tray; store it.
2. **Key derivation:** decode code → 5-byte IKM → HKDF-SHA256 → `EncKey` + `AuthKey`.
3. **Session UUID:** generate a new random UUID as `ClientId` on each app launch.
4. **Auth token:** `HMAC-SHA256(AuthKey, clientId.bytes)[0..15]`; copy all 16 bytes verbatim into packet bytes 24–39.
5. **Handshake loop:** send `HandshakeRequest` (40 bytes) every 500 ms until a valid `HandshakeResponse` is received; store `NoncePrefix` from the response.
6. **Video receive loop:**
   a. Read UDP datagram; verify `Magic == 0x474D` and `PacketType == 0x01`.
   b. Parse the 28-byte `VideoChunkHeader`.
   c. Build 12-byte nonce: `NoncePrefix` (8 bytes from HandshakeResponse) + `SequenceNum` (4 bytes LE).
   d. Extract `ciphertext_len = PayloadLength - 16`.
   e. Decrypt: `plaintext = AES-256-GCM.Decrypt(EncKey, nonce, ciphertext=payload[0..ciphertext_len-1], tag=payload[ciphertext_len..])`.
   f. Drop chunk silently if GCM verification fails.
   g. Buffer chunks by `FrameId`; once all `ChunkTotal` chunks are received, reassemble plaintext in `ChunkIndex` order and feed to the H.264 decoder.
   h. Drop incomplete frames after 100 ms timeout.
7. **Decode.** Each reassembled frame is an Annex B access unit that may contain **multiple NAL units**
   (an IDR frame carries SPS + PPS + the IDR slice; a P-frame may carry an access-unit delimiter plus
   the slice). Split on start codes and handle every NAL:
   - On an IDR frame (`Flags & 1 == 1`): extract SPS (type 7) + PPS (type 8) and (re)build the decoder
     format description from them when they change.
   - For **all** frames, build the decoder sample by converting each NAL to length-prefixed AVCC
     (`[4-byte big-endian length][NAL]`), dropping the AUD (type 9). Treating a multi-NAL P-frame as a
     single NAL produces a malformed sample the decoder silently rejects — every P-frame fails and only
     keyframes render.
8. **Heartbeat:** send a `Heartbeat` packet every 1 s while connected.
9. **Watchdog:** if no valid-header packet arrives for 3 s, declare the connection lost and return to the handshake loop (step 5).
