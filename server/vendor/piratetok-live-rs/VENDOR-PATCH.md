# Vì sao crate này được vendor

**Upstream**: https://github.com/PirateTok/live-rs — crates.io `piratetok-live-rs` 0.2.1
**License**: 0BSD (cho phép sao chép/sửa đổi tự do, không cần ghi công)

## Vấn đề

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

## Thay đổi so với upstream

Đúng **một dòng**, tại `build.rs:54`:

```rust
// trước
let rel = path.to_string_lossy().to_string();
// sau
let rel = path.to_string_lossy().replace('\\', "/");
```

Chuẩn hoá dấu phân cách để danh sách miễn trừ khớp trên cả Windows lẫn Unix.

Ngoài ra `Cargo.toml` được trim về lib-only (bỏ `[[bin]]`, `[[example]]`, `[[test]]` và
feature `cli`), và các thư mục `src/bin/`, `examples/`, `tests/` không được vendor —
dự án này chỉ dùng thư viện.

**Không sửa gì trong logic thư viện.**

## Khi nào gỡ bỏ vendor

Khi upstream phát hành bản có fix (theo dõi issue #1), đổi `Cargo.toml` của server về:

```toml
piratetok-live-rs = "0.2"   # hoặc bản mới hơn
```

rồi xoá khối `[patch.crates-io]` và thư mục này.
