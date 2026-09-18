//! TikTok Live Game Server.
//!
//! Cầu nối giữa TikTok Live và overlay game Unity: nhận sự kiện live, áp luật Master,
//! rồi phát cho client qua WebSocket.
//!
//! # Cách các tầng xếp chồng
//!
//! ```text
//!   transport/  ──┐   HTTP, WebSocket, lọc dữ liệu không tin cậy
//!   live/       ──┤   nguồn sự kiện: TikTok, TikFinity, demo
//!                 ↓
//!   session/        trạng thái phiên + pipeline sự kiện
//!                 ↓
//!   domain/         luật, bảng quà, chuẩn hoá chuỗi (thuần, không I/O)
//! ```
//!
//! Phụ thuộc chỉ đi một chiều xuống dưới. [`config`] là lá, tầng nào cũng dùng được.

pub mod config;
pub mod domain;
pub mod live;
pub mod session;
pub mod transport;
