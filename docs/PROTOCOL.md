# PocketDrop protocol (v1)

PocketDrop has three ways for a phone to reach the PC. All three end up in the same
receiver on the PC (confirmation dialog → progress overlay → folder → Explorer).

| Transport | Used by | PC side |
|-----------|---------|---------|
| TCP stream, port **45455** | Android app over Wi‑Fi | `TcpTransport` |
| Bluetooth RFCOMM, service UUID **`7a1c3e52-5d0b-4b8e-9f43-2c6a0d1e8b71`** | Android app over Bluetooth | `BluetoothTransport` |
| HTTP, port **45456** | Any browser (iPhone, Android, tablets) | `WebServer` |

Discovery over Wi‑Fi uses UDP port **45454**.

## Discovery (UDP 45454)

The phone sends the ASCII datagram `POCKETDROP_DISCOVER_V1` to the broadcast address
(`255.255.255.255` and the subnet broadcast). Every PC that runs PocketDrop answers the
sender with one JSON datagram:

```json
{ "app": "PocketDrop", "v": 1, "id": "b1f…", "name": "DESKTOP-ABC", "tcpPort": 45455, "httpPort": 45456 }
```

## Stream protocol (TCP and Bluetooth)

Control messages are **frames**: a 4‑byte big‑endian length followed by that many bytes of
UTF‑8 JSON (max 1 MiB). File contents are sent raw, outside frames.

```
phone                                   PC
  ── {"type":"hello","v":1,"device":"Pixel 8"} ──▶
  ◀── {"type":"hello","v":1,"app":"PocketDrop","name":"DESKTOP-ABC"} ──
  ── {"type":"list_folders"} ──▶                       (optional, any number of times)
  ◀── {"type":"folders","folders":[{"name":"Trip","files":12,"modified":"2026-11-25T10:00:00Z"}]} ──
  ── {"type":"offer",
      "folder":{"mode":"new"|"existing","name":"Trip"},
      "files":[{"name":"IMG_1.jpg","size":3145728}, …]} ──▶
                                         user sees the confirmation dialog
  ◀── {"type":"accept","folder":"Trip"} ──   or   ◀── {"type":"reject","reason":"declined|busy|timeout|invalid"} ──
  ── raw bytes of file 0 (exactly size bytes) ──▶
  ── raw bytes of file 1 … ──▶
  ◀── {"type":"done","saved":12,"folder":"Trip"} ──
```

* Either side cancels by closing the connection. The PC keeps the files that finished and
  deletes the one that was cut off.
* `mode:"new"` creates a new sub‑folder. If the name is taken, the PC appends ` (2)`, ` (3)` …
  An empty name becomes `PocketDrop yyyy-MM-dd HH-mm-ss`.
* `mode:"existing"` writes into an existing sub‑folder (it is created if it was deleted meanwhile).
  Files whose names already exist get ` (1)`, ` (2)` … appended.
* Folder and file names are sanitised on the PC: path separators, `..`, reserved Windows
  names and invalid characters are removed. Nothing can be written outside the receive folder.

## HTTP API (port 45456)

| Method | Path | Body / result |
|--------|------|---------------|
| GET | `/` | the web sender page |
| GET | `/api/info` | `{"app","name","v"}` |
| GET | `/api/folders` | `{"folders":[…]}` (same shape as above) |
| POST | `/api/offer` | body `{"device","folder":{…},"files":[…]}` → waits for the user → `{"accepted":true,"id":"…","folder":"…"}` or `{"accepted":false,"reason":"…"}` |
| POST | `/api/offer?wait=0` | same body → `{"pending":true,"ticket":"…"}` at once |
| GET | `/api/offer/{ticket}` | waits up to 20 s → `{"pending":true}` or the answer as above. Not asked for 50 s = withdrawn |
| POST | `/api/offer/{ticket}/cancel` | withdraws the question on the PC |
| POST | `/api/upload/{id}/{index}` | raw file bytes for file `index` of the offer |
| POST | `/api/upload/{id}/{index}?offset=N` | one piece of the file (needs `Content-Length`) → `{"ok":true,"complete":bool}`. Pieces in order; a piece may be resent (the file rewinds to `N`). `503 {"error":"retry"}` = send it again, `409` = the drop is over |
| POST | `/api/finish/{id}` | `{"saved":n,"folder":"…"}`, opens Explorer |
| POST | `/api/cancel/{id}` | aborts the transfer |

A drop that gets no data for 2 minutes fails by itself (the phone went away).

### From anywhere (port 45457, loopback only)

When *From anywhere* is on, the PC runs Cloudflare's `cloudflared` as a free quick tunnel
(`cloudflared tunnel --url http://127.0.0.1:45457`), which gives a public `https://….trycloudflare.com` address.
The same API is served on 127.0.0.1:45457, but every `/api/*` call there must carry the secret from the link
(`https://….trycloudflare.com/?k=<32 hex>`) in the `X-PocketDrop-Key` header or a `k` query parameter;
otherwise `401`. The key is new every time the tunnel starts, and so is the address. Offers that arrive this way show
"via Internet" on the PC. Cloudflare limits one request to 100 MB and 100 seconds, which is why the browser polls for
the answer and sends 8 MB pieces.
