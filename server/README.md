# tiktok-server

Bản viết lại của [`TikTokBridge/`](../TikTokBridge) bằng Rust + Axum. Cùng giao thức
WebSocket, cùng file cấu hình, nhưng không cần Node.js và không cần API key ký request.

## Khác gì bản Node

| | Node (`TikTokBridge/`) | Rust (`server/`) |
|---|---|---|
| Runtime | Node.js ≥ 20 | binary đơn lẻ |
| Nối TikTok trực tiếp | `tiktok-live-connector` + Euler API key | `piratetok-live-rs`, chỉ cần cookie `ttwid` |
| Phụ thuộc | express, ws, three, tiktok-live-connector | không có `node_modules` |
| Nguồn TikFinity | có | có (giữ làm đường lui) |

Giao thức WebSocket và định dạng JSON **giữ nguyên hoàn toàn**, nên
`public/control.html` và overlay Unity chạy được mà không phải sửa gì.

## Chạy

```bash
cp .env.example .env     # rồi sửa cho khớp máy
cargo run                # hoặc: cargo build --release && ./target/release/tiktok-server
```

Mở http://127.0.0.1:8085/control.html

> [!IMPORTANT]
> Cổng mặc định là **8085** (tránh đụng 3000/5173/8080 hay dùng cho dev).
> Đổi cổng thì phải đổi cả `serverUrl` trong
> `UnityProject/Assets/Scripts/TikTokWebSocketClient.cs` — Unity hardcode giá trị này.

### Chọn nguồn sự kiện

Đặt trong `.env`:

- `LIVE_PROVIDER=tiktok` — nối thẳng TikTok. Không cần API key.
- `LIVE_PROVIDER=tikfinity` — qua TikFinity Desktop (`TIKFINITY_WS_URL`).

> [!IMPORTANT]
> Nên giữ TikFinity làm đường lui. Cách nối thẳng dựa vào việc TikTok chưa bắt buộc
> ký request ở endpoint `ws_reuse_supplement`; nếu TikTok siết lại thì `LIVE_PROVIDER=tikfinity`
> là cách duy nhất chạy tiếp ngay được.

## Thư mục dữ liệu

Mặc định `./config`, `./public`, `./assets` cạnh nơi chạy binary, ghi đè được bằng
`CONFIG_DIR` / `PUBLIC_DIR` / `ASSETS_DIR`.

Trong lúc còn chạy song song với bản Node, `.env.example` trỏ `ASSETS_DIR` sang
`../TikTokBridge/assets` để khỏi nhân đôi ~5MB ảnh GIF. Khi gỡ bỏ bản Node thì
chuyển thư mục `assets/` sang đây rồi bỏ dòng đó đi.

File `config/master.json` và `config/observed-gifts.json` **được server ghi lại**
lúc chạy. Hai bản Node và Rust không nên chạy cùng lúc trên cùng thư mục config.

## Cấu trúc

Xếp theo tầng phụ thuộc, chỉ đi một chiều xuống dưới:

```
transport/  ──┐   HTTP, WebSocket, lọc dữ liệu không tin cậy
live/       ──┤   nguồn sự kiện: TikTok, TikFinity, demo
              ↓
session/          trạng thái phiên + pipeline sự kiện
              ↓
domain/           luật, bảng quà, chuẩn hoá chuỗi (thuần, không I/O)
```

```
src/
├── main.rs             bootstrap, nạp cấu hình, tắt êm
├── config.rs           đọc .env, thiết lập server, đường dẫn dữ liệu
│
├── domain/             logic thuần — không mạng, không đĩa, không trạng thái
│   ├── event.rs        hợp đồng wire-format với Unity
│   ├── rules.rs        luật Master
│   ├── gifts.rs        bảng quà + thư viện gift học được
│   └── text.rs         chuẩn hoá tiếng Việt để so khớp alias
│
├── session/            một phiên live
│   ├── state.rs        người chơi, điểm VIP, khử trùng lặp, chỉ số
│   └── pipeline.rs     đường đi sự kiện: sanitize → luật → broadcast
│
├── transport/          ranh giới tin cậy với client
│   ├── security.rs     lọc dữ liệu, kiểm tra Host/Origin, rate limit
│   ├── http.rs         router Axum, security headers, /api/*
│   └── ws.rs           hub WebSocket, phân quyền client
│
└── live/               nguồn sự kiện bên ngoài
    ├── mod.rs          điều phối + kết nối lại
    ├── tiktok.rs       nối thẳng TikTok
    ├── tikfinity.rs    qua TikFinity Desktop
    └── demo.rs         người xem giả để thử sàn nhảy
```

Vì sao không xếp theo kiểu NestJS (`common/`, `modules/`, `shared/`): `modules` trong
Nest là container DI có ý nghĩa lúc chạy, còn `mod` trong Rust chỉ là không gian tên —
bê nguyên sang chỉ thêm tầng thư mục mà không được gì. Cách xếp trên nhóm theo
*thứ gì thay đổi cùng nhau*, và làm chiều phụ thuộc nhìn thấy được ngay từ cây thư mục.

## Kiểm thử

```bash
cargo test      # 78 test, gồm 6 test chạy trên chính file config/ thật
cargo clippy --all-targets
```

`tests/real_config.rs` là chốt chặn quan trọng nhất: nó kiểm tra `master.json` và
`observed-gifts.json` round-trip qua Rust mà không đổi tên khoá hay mất dữ liệu.
Nếu test này đỏ, bản Node sẽ đọc hỏng file do bản Rust ghi ra.

## Lưu ý khi build trên Windows

Toolchain `x86_64-pc-windows-gnu` đôi khi vướng lỗi `dlltool` khi build song song:

```
error: dlltool could not create import library ... Permission denied
```

Đây là tranh chấp file tạm (thường do phần mềm diệt virus), không phải lỗi code.
Chạy lại, hoặc `cargo build -j 1`.

## Thư viện vendor

`vendor/piratetok-live-rs/` là bản sao đã sửa của crate cùng tên trên crates.io.
Bản 0.2.1 gốc **không build được trên Windows** — chi tiết và cách gỡ bỏ vendor
xem [`vendor/piratetok-live-rs/VENDOR-PATCH.md`](vendor/piratetok-live-rs/VENDOR-PATCH.md).

---

© 2025 wangnguen — giấy phép MIT.
