# Vì sao crate này được vendor

**Upstream**: https://github.com/PirateTok/live-rs — crates.io `piratetok-live-rs` 0.2.1
**License**: 0BSD (cho phép sao chép/sửa đổi tự do, không cần ghi công)

## Vấn đề 1 — không build được trên Windows

Bản 0.2.1 trên crates.io **không build được trên Windows**:

```
error: failed to run custom build command for `piratetok-live-rs v0.2.1`
  process didn't exit successfully: ... build-script-build (exit code: 101)
  thread 'main' panicked at build.rs:33:
  ========== BUILD CONSTITUTION VIOLATED (23 issues) ==========
    [R2] src\bin\generate_manifest.rs:225 contains '.unwrap_or(' ...
    [R1] src\structs\proto\messages.rs has 811 lines (max 800)
```

Crate có `build.rs` chạy lint nội bộ ("BUILD CONSTITUTION"). Lint dựng đường dẫn bằng
`path.to_string_lossy()` rồi so khớp với danh sách miễn trừ viết theo dấu `/`:

```rust
// build.rs:69
let is_exempt = ERROR_SWALLOW_EXEMPT.iter().any(|e| rel.ends_with(e))
    || rel.contains("src/bin/");
```

Trên Windows `rel` là `src\bin\record_capture.rs`, nên `contains("src/bin/")` luôn `false`.
Kết quả: mọi file trong `src/bin/` bị lint sai, và `messages.rs` mất miễn trừ 900 dòng
(`LOC_EXEMPT` cũng so khớp bằng `/`) nên vượt hạn mức 800 → panic.

Không có env var hay feature flag nào tắt được lint này.

Đã báo upstream: https://github.com/PirateTok/live-rs/issues/1 (mở 2026-07-03, chưa có phản hồi).

## Vấn đề 2 — không lấy được ttwid, mọi phiên live rớt ngay

Kiểm thử thật ngày 2026-09-18 với một tài khoản đang live: server vào được phòng
(`đã vào phòng live, room 7686...`) rồi rớt sau khoảng một giây, lặp vô hạn:

```
ERROR ttwid fetch failed: invalid response: no ttwid cookie in tiktok.com response
WARN  mất kết nối live: stream đã đóng
```

`src/http/ttwid.rs` lấy cookie bằng một GET tới `https://www.tiktok.com/`. URL đó
nay trả về 200 **không kèm bất kỳ header `Set-Cookie` nào** — đã đối chiếu bằng
curl, cả với bộ header giống trình duyệt thật:

```
https://www.tiktok.com/            -> 0 header set-cookie
https://www.tiktok.com/@<user>     -> khong co ttwid
https://www.tiktok.com/live        -> co ttwid
```

ttwid là thứ duy nhất cần để mở WebSocket Webcast, nên mất nó là chế độ
`LIVE_PROVIDER=tiktok` chết hoàn toàn.

## Thay đổi so với upstream

### 1. `build.rs:54` — chuẩn hoá dấu phân cách đường dẫn

Đúng **một dòng**:

```rust
// trước
let rel = path.to_string_lossy().to_string();
// sau
let rel = path.to_string_lossy().replace('\\', "/");
```

Chuẩn hoá dấu phân cách để danh sách miễn trừ khớp trên cả Windows lẫn Unix.

### 2. `src/http/ttwid.rs` — đổi URL lấy ttwid

```rust
// trước
const TIKTOK_URL: &str = "https://www.tiktok.com/";
// sau
const TIKTOK_URL: &str = "https://www.tiktok.com/live";
```

Trang `/live` vẫn cấp `ttwid`. Đây là thay đổi **có chạm vào logic thư viện**, khác
với vá số 1. Nếu một ngày `/live` cũng ngừng cấp, chỗ cần sửa vẫn là hằng số này;
cách cuối cùng là tự lấy ttwid từ trình duyệt rồi truyền vào qua
`TikTokLive::builder(..).cookies(..)`.

Ngoài ra `Cargo.toml` được trim về lib-only (bỏ `[[bin]]`, `[[example]]`, `[[test]]` và
feature `cli`), và các thư mục `src/bin/`, `examples/`, `tests/` không được vendor —
dự án này chỉ dùng thư viện.

Vá số 1 không chạm logic thư viện; vá số 2 có (đổi một hằng số URL).

## Khi nào gỡ bỏ vendor

Khi upstream phát hành bản có fix (theo dõi issue #1), đổi `Cargo.toml` của server về:

```toml
piratetok-live-rs = "0.2"   # hoặc bản mới hơn
```

rồi xoá khối `[patch.crates-io]` và thư mục này.
