//! Logic nghiệp vụ thuần: không chạm mạng, không chạm đĩa, không giữ trạng thái.
//!
//! Tầng này không phụ thuộc vào tầng nào khác nên test được bằng giá trị thường,
//! không cần dựng server hay mock. Mọi thứ ở đây nhận vào dữ liệu và trả ra dữ liệu.

pub mod event;
pub mod points;
pub mod display;
pub mod gifts;
pub mod rules;
pub mod text;
