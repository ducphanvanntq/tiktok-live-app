//! Cổng vào/ra với client: HTTP, WebSocket, và lớp lọc dữ liệu không tin cậy.
//!
//! Đây là ranh giới tin cậy của server — mọi thứ từ ngoài vào đều đi qua
//! [`security`] trước khi chạm tới [`crate::session`].

pub mod http;
pub mod security;
pub mod ws;
