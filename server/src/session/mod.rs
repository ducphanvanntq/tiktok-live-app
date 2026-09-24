//! Trạng thái một phiên live và đường đi của sự kiện qua nó.
//!
//! Phụ thuộc vào [`crate::domain`]; không biết gì về HTTP hay WebSocket.

pub mod pipeline;
pub mod state;
